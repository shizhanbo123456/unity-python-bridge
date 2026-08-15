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
