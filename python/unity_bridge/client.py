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

DEFAULT_HOST = "127.0.0.1"
DEFAULT_PORT = 21927


class UnityBridgeError(Exception):
    """协议错误、执行错误或连接中断时抛出。"""


class UnityClient:
    """与 Unity Editor 内 BridgeServer 通信的客户端。

    用法::

        with UnityClient() as client:
            data = client.call("scene.tree", components=True)
    """

    def __init__(self, host: str = DEFAULT_HOST, port: int = DEFAULT_PORT,
                 timeout: float = 10.0) -> None:
        self.host = host
        self.port = port
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
                f"请确认已在 Unity Editor 中通过 Tools > Unity Python Bridge 启动服务器，"
                f"且端口一致。"
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

    def scene_tree(self, components: bool = False) -> dict:
        return self.call("scene.tree", components=components)

    def mesh_bounds(self, path: str) -> dict:
        return self.call("mesh.bounds", path=path)

    def prefab_screenshot(self, path: str, offset: dict, output: str,
                          orthographic: bool = False, fov=None,
                          width: int = 1920, height: int = 1080, bg=None,
                          light: float = 0.0) -> dict:
        """将目标预制体复制到场景隔离位置并截图保存为 PNG。

        offset 为相机相对预制体的位置，形如 {"x":, "y":, "z":}。
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
