"""Unity Bridge TCP/JSON 客户端。

协议（单行 JSON，UTF-8）:
    请求:  {"id": 1, "cmd": "scene.tree", "args": {...}}
    响应:  {"id": 1, "ok": true,  "data": {...}}
           {"id": 1, "ok": false, "error": "..."}
"""

from __future__ import annotations

import json
import socket
from typing import Any, Optional

from .config import DEFAULT_HOST, DEFAULT_PORT, load_server_port


class UnityBridgeError(Exception):
    """协议错误、执行错误或连接中断时抛出。"""


class UnityClient:
    """与 Unity Editor 内 BridgeServer 通信的客户端。

    用法::

        with UnityClient() as client:
            data = client.call("scene.tree", components=True)
    """

    def __init__(self, host: str = None, port: int = None,
                 timeout: float = 10.0) -> None:
        self.host = host or DEFAULT_HOST
        self.port = port if port is not None else load_server_port()
        self.timeout = timeout
        self._sock: Optional[socket.socket] = None
        self._id = 0

    # ---- 生命周期 ----

    def __enter__(self) -> "UnityClient":
        self.connect()
        return self

    def __exit__(self, *exc) -> None:
        self.close()

    def connect(self) -> None:
        try:
            self._sock = socket.create_connection((self.host, self.port), timeout=self.timeout)
        except ConnectionRefusedError:
            raise UnityBridgeError(
                f"无法连接 Unity（{self.host}:{self.port}）。"
                f"请确认已在 Unity Editor 中通过 BridgeManager 组件或菜单 "
                f"Tools > Unity Python Bridge > Start Server 启动服务器，且端口一致。"
            ) from None
        except socket.timeout:
            raise UnityBridgeError(f"连接超时（{self.host}:{self.port}）") from None

    def close(self) -> None:
        if self._sock is not None:
            self._sock.close()
            self._sock = None

    @property
    def connected(self) -> bool:
        return self._sock is not None

    # ---- 协议 ----

    def call(self, command: str, **args: Any) -> Any:
        """发送一条命令并等待响应，返回 data 字段（任意 JSON 值）。"""
        self._ensure_connected()
        self._id += 1
        request = {"id": self._id, "cmd": command, "args": args}
        self._send_line(json.dumps(request, ensure_ascii=False))

        line = self._read_line()
        try:
            resp = json.loads(line)
        except json.JSONDecodeError:
            raise UnityBridgeError(f"响应不是合法 JSON: {line[:200]!r}") from None

        if not resp.get("ok"):
            raise UnityBridgeError(f"命令 {command} 执行失败: {resp.get('error', '未知错误')}")
        return resp.get("data")

    # ---- 预置命令 ----

    def ping(self) -> dict:
        return self.call("bridge.ping")

    def list_commands(self):
        return self.call("bridge.list_commands")

    def reload_unity(self) -> dict:
        """触发 Unity 脚本重编译（domain reload）。触发后旧域卸载、服务器会中断，
        重编译完成后由 BridgeAutoRestart 自动恢复；调用方应轮询等待恢复。"""
        return self.call("bridge.reload")

    def debug_log(self, message: str) -> dict:
        """在 Unity Console 打印一条 Info 日志。"""
        return self.call("debug.log", message=message)

    def debug_log_warning(self, message: str) -> dict:
        """在 Unity Console 打印一条 Warning 日志。"""
        return self.call("debug.log_warning", message=message)

    def debug_log_error(self, message: str) -> dict:
        """在 Unity Console 打印一条 Error 日志。"""
        return self.call("debug.log_error", message=message)

    def get_logs(self, count: int = 50, type_: str = "all") -> dict:
        """读取最近 N 条 Console 日志（自订阅时刻起缓存的环形缓冲，上限 500）。

        type_ 为 "all" / "log" / "warning" / "error" / "exception"。
        返回 {"count": n, "entries": [{index, time, type, message, stackTrace}, ...]}。
        """
        return self.call("debug.get_logs", count=count, type=type_)

    def debug_log_version(self) -> dict:
        """在 Unity Console 打印桥接层版本号（含命令总数）。"""
        return self.call("debug.log_version")

    def scene_tree(self, components: bool = False) -> dict:
        return self.call("scene.tree", components=components)

    def mesh_bounds(self, path: str) -> dict:
        return self.call("mesh.bounds", path=path)

    def prefab_screenshot(self, path: str, offset: dict, output: str,
                          orthographic: bool = False, fov=None,
                          width: int = 1920, height: int = 1080, bg=None,
                          light: float = 0.0, camera_position=None,
                          look_at=None, relative: bool = False) -> dict:
        """将目标预制体复制到场景隔离位置并截图保存为 PNG。

        offset 为相机相对预制体的位置，形如 {"x":, "y":, "z":}（camera_position 缺省时使用）。
        camera_position / look_at 为 [x,y,z]：relative=False 时是世界坐标，
        relative=True 时是相对预制体位置（预制体在隔离点 (9999,9999,9999)）。
        fov / bg 为 None 时不发送，由 Unity 侧使用默认值。
        light 为补光强度，0（默认）不补光，>0 时追加一盏与相机同向的平行光。
        """
        args = {
            "path": path,
            "offset": offset,
            "output": output,
            "orthographic": orthographic,
            "width": width,
            "height": height,
            "light": light,
        }
        if fov is not None:
            args["fov"] = fov
        if bg is not None:
            args["bg"] = bg
        if camera_position is not None:
            args["cameraPosition"] = list(camera_position)
        if look_at is not None:
            args["lookAt"] = list(look_at)
        if relative:
            args["relative"] = True
        return self.call("prefab.screenshot", **args)

    # ---- Terrain 程序化编辑（Unity 原生 TerrainData API）----

    def terrain_list(self, terrain: str = None) -> dict:
        """列出场景中所有 Terrain（名称/位置/尺寸/分辨率等）。"""
        return self.call("terrain.list", terrain=terrain)

    def get_heights(self, terrain: str = None, x_base: int = 0, z_base: int = 0,
                    width: int = 0, height: int = 0) -> dict:
        """读取高度图区域，data 行优先 index=y*width+x，值 0~1。"""
        return self.call("terrain.get_heights", terrain=terrain,
                         xBase=x_base, zBase=z_base, width=width, height=height)

    def set_heights(self, terrain: str = None, x_base: int = 0, z_base: int = 0,
                    width: int = 0, height: int = 0, data=None,
                    noise: bool = False, noise_scale: float = 1.0, noise_seed: int = 0,
                    base_height: float = 0.0, height_scale: float = 1.0) -> dict:
        """写入高度图：data（float[] 行优先 0~1）或 noise（Perlin 噪声生成）。"""
        args = dict(terrain=terrain, xBase=x_base, zBase=z_base,
                    width=width, height=height, noise=noise)
        if data is not None:
            args["data"] = list(data)
        if noise:
            args.update(noiseScale=noise_scale, noiseSeed=noise_seed,
                        baseHeight=base_height, heightScale=height_scale)
        return self.call("terrain.set_heights", **args)

    def get_layers(self, terrain: str = None) -> dict:
        """列出 Terrain 的纹理层（TerrainLayer）。"""
        return self.call("terrain.get_layers", terrain=terrain)

    def get_diffuse_dirs(self, terrain: str = None) -> dict:
        """返回 Terrain 所有 TerrainLayer 的 Diffuse 贴图目录（去重）及各层贴图完整路径。"""
        return self.call("terrain.get_diffuse_dirs", terrain=terrain)

    def get_alphamaps(self, terrain: str = None, x_base: int = 0, z_base: int = 0,
                      width: int = 0, height: int = 0) -> dict:
        """读取纹理混合权重，data index=(y*width+x)*layers+layer。"""
        return self.call("terrain.get_alphamaps", terrain=terrain,
                         xBase=x_base, zBase=z_base, width=width, height=height)

    def set_alphamaps(self, terrain: str = None, x_base: int = 0, z_base: int = 0,
                      width: int = 0, height: int = 0, data=None) -> dict:
        """写入纹理混合权重（每像素自动归一化）。data 长度=width*height*layers。"""
        args = dict(terrain=terrain, xBase=x_base, zBase=z_base,
                    width=width, height=height)
        if data is not None:
            args["data"] = list(data)
        return self.call("terrain.set_alphamaps", **args)

    def list_details(self, terrain: str = None) -> dict:
        """列出 Terrain 的草原型（DetailPrototype）。"""
        return self.call("terrain.list_details", terrain=terrain)

    def get_details(self, terrain: str = None, layer: int = 0,
                    x_base: int = 0, z_base: int = 0,
                    width: int = 0, height: int = 0) -> dict:
        """读取某层植被密度图，data 行优先 index=y*width+x。"""
        return self.call("terrain.get_details", terrain=terrain, layer=layer,
                         xBase=x_base, zBase=z_base, width=width, height=height)

    def set_details(self, terrain: str = None, layer: int = 0,
                    x_base: int = 0, z_base: int = 0,
                    width: int = 0, height: int = 0, data=None,
                    random: bool = False, count: int = 0, seed: int = 0,
                    density: int = 3) -> dict:
        """写入植被密度：data（int[] 行优先 0~16）或 random 随机撒点。"""
        args = dict(terrain=terrain, layer=layer, xBase=x_base, zBase=z_base,
                    width=width, height=height, random=random)
        if data is not None:
            args["dataInt"] = list(data)
        if random:
            args.update(count=count, seed=seed, density=density)
        return self.call("terrain.set_details", **args)

    def list_trees(self, terrain: str = None) -> dict:
        """列出 Terrain 的树原型与树实例。"""
        return self.call("terrain.list_trees", terrain=terrain)

    def add_trees(self, terrain: str = None, prototype_index: int = 0,
                  positions=None, random: bool = False, count: int = 0,
                  seed: int = 0, min_scale: float = 0.8, max_scale: float = 1.2) -> dict:
        """添加树木：positions（float[] 每3个一组 {x,y,z} 归一化）或 random 随机种植。"""
        args = dict(terrain=terrain, prototypeIndex=prototype_index)
        if positions is not None:
            args["positions"] = list(positions)
        if random:
            args.update(random=True, count=count, seed=seed,
                        minScale=min_scale, maxScale=max_scale)
        return self.call("terrain.add_trees", **args)

    def clear_trees(self, terrain: str = None) -> dict:
        """清空 Terrain 上所有树实例。"""
        return self.call("terrain.clear_trees", terrain=terrain)

    def get_tree_prefab_dirs(self, terrain: str = None) -> dict:
        """返回 Terrain 所有树原型的 Prefab 目录（去重）及各预制体完整路径。"""
        return self.call("terrain.get_tree_prefab_dirs", terrain=terrain)

    def get_detail_asset_dirs(self, terrain: str = None) -> dict:
        """返回 Terrain 所有草原型的预制体或贴图目录（去重）及各自完整路径。"""
        return self.call("terrain.get_detail_asset_dirs", terrain=terrain)

    # ---- Terrain stash（stash → clear → apply 截图调整链路）----

    def stash(self, terrain: str = None, type_: str = "all", name: str = "") -> dict:
        """把当前地形的树木实例/植被密度图全量序列化为 JSON 存到工具 stash 子目录。

        同名 stash 已存在时会报错（不允许覆盖，需先 stash_delete）。
        type_ 为 "trees" / "details" / "all"。
        """
        return self.call("terrain.stash", terrain=terrain, type=type_, name=name)

    def apply_stash(self, terrain: str = None, type_: str = "all", name: str = "") -> dict:
        """读取 stash JSON 并整体写回地形（替换当前 trees/detail）。"""
        return self.call("terrain.apply_stash", terrain=terrain, type=type_, name=name)

    def stash_delete(self, type_: str, name: str) -> dict:
        """删除指定 stash 文件（type 必须是 trees 或 details）。"""
        return self.call("terrain.stash_delete", type=type_, name=name)

    def stash_list(self, type_: str = "all") -> dict:
        """列出工具 stash 子目录下所有 stash 文件。"""
        return self.call("terrain.stash_list", type=type_)

    # ---- view.camera（抓取指定相机实时画面）----

    def view_screenshot(self, output: str, camera: str = None,
                        width: int = 0, height: int = 0) -> dict:
        """渲染指定相机的实时画面保存为 PNG（默认 MainCamera）。

        camera 省略时 Unity 侧依次找 tag=MainCamera、名为 Main Camera 的、第一个激活相机。
        width/height 为 0 时使用相机当前分辨率。
        """
        args = {"output": output}
        if camera:
            args["camera"] = camera
        if width > 0:
            args["width"] = width
        if height > 0:
            args["height"] = height
        return self.call("view.camera", **args)

    # ---- gameobject.get / gameobject.set（常规物体操作）----

    def gameobject_get(self, target: str, quaternion: bool = False) -> dict:
        """读取 GameObject 的 active 状态与 Transform 的 position/rotation/scale。

        target 为层级路径（如 "Player/Body"）或唯一名称；rotation 默认欧拉角，
        quaternion=True 时额外返回四元数。
        """
        return self.call("gameobject.get", target=target, quaternion=quaternion)

    def gameobject_set(self, target: str, active: int = -1, position=None,
                       rotation=None, scale=None, quaternion: bool = False) -> dict:
        """写入 GameObject 的 active 状态与 Transform 的 position/rotation/scale。

        active: -1 不改 / 0 隐藏 / 1 激活。
        position: [x, y, z] 世界坐标，None 不改。
        rotation: 默认 [x, y, z] 欧拉角；quaternion=True 时 [x, y, z, w] 四元数，None 不改。
        scale: [x, y, z] localScale，None 不改。
        返回设置后的完整状态。
        """
        args = {"target": target}
        if active != -1:
            args["active"] = active
        if position is not None:
            args["position"] = list(position)
        if rotation is not None:
            args["rotation"] = list(rotation)
        if scale is not None:
            args["scale"] = list(scale)
        if quaternion:
            args["quaternion"] = True
        return self.call("gameobject.set", **args)

    # ---- 内部 ----

    def _ensure_connected(self) -> None:
        if self._sock is None:
            self.connect()

    def _send_line(self, payload: str) -> None:
        assert self._sock is not None
        try:
            self._sock.sendall((payload + "\n").encode("utf-8"))
        except OSError as e:
            self._sock = None
            raise UnityBridgeError(f"发送失败，连接可能已断开: {e}") from None

    def _read_line(self) -> str:
        assert self._sock is not None
        buf = bytearray()
        while True:
            try:
                chunk = self._sock.recv(4096)
            except socket.timeout:
                raise UnityBridgeError("等待 Unity 响应超时") from None
            except OSError as e:
                raise UnityBridgeError(f"接收失败，连接可能已断开: {e}") from None

            if not chunk:
                raise UnityBridgeError("连接已被对端关闭")
            buf += chunk
            nl = buf.find(b"\n")
            if nl != -1:
                return bytes(buf[:nl]).decode("utf-8")
