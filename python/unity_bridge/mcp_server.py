"""UnityPythonBridge 的 MCP 适配入口。

对外仅暴露一组经过筛选、参数明确的工具；内部继续复用 UnityClient 和
现有 TCP/JSON 协议，不改变 Unity 侧 BridgeServer。
"""

from __future__ import annotations

import json
from typing import Any, Optional

from mcp.server.fastmcp import FastMCP

from .client import UnityBridgeError, UnityClient


mcp = FastMCP(
    "Unity Python Bridge",
    instructions=(
        "操控当前已打开的 Unity Editor。读取工具可直接调用；写入工具会修改当前场景，"
        "调用前应确认目标对象。Unity 编译期间服务可能短暂不可用。"
    ),
)


def _call(command: str, **arguments: Any) -> str:
    """把 Unity Bridge 响应规范化为适合模型读取的 JSON 文本。"""
    try:
        with UnityClient() as client:
            result = client.call(command, **arguments)
    except UnityBridgeError as exc:
        raise RuntimeError(str(exc)) from exc
    return json.dumps(result, ensure_ascii=False, indent=2)


@mcp.tool()
def unity_ping() -> str:
    """检查当前 Unity Editor 的 BridgeServer 是否在线。"""
    return _call("bridge.ping")


@mcp.tool()
def unity_version() -> str:
    """读取 Unity Bridge 版本和已注册命令数量。"""
    return _call("bridge.version")


@mcp.tool()
def get_scene_tree(
    components: bool = False,
    depth: int = 3,
    path: Optional[str] = None,
) -> str:
    """读取当前场景的 GameObject 层级；可限制起点和递归深度。"""
    arguments: dict[str, Any] = {"components": components, "depth": depth}
    if path:
        arguments["path"] = path
    return _call("scene.tree", **arguments)


@mcp.tool()
def get_gameobject(target: str, quaternion: bool = False) -> str:
    """读取指定 GameObject 的激活状态和 Transform；target 为层级路径或唯一名称。"""
    return _call("gameobject.get", target=target, quaternion=quaternion)


@mcp.tool()
def set_gameobject_transform(
    target: str,
    position: Optional[list[float]] = None,
    rotation: Optional[list[float]] = None,
    scale: Optional[list[float]] = None,
) -> str:
    """修改现有 GameObject 的世界位置、欧拉角或局部缩放；该操作支持 Unity Undo。"""
    arguments: dict[str, Any] = {"target": target}
    if position is not None:
        arguments["position"] = position
    if rotation is not None:
        arguments["rotation"] = rotation
    if scale is not None:
        arguments["scale"] = scale
    return _call("gameobject.set", **arguments)


@mcp.tool()
def get_console_logs(count: int = 50, log_type: str = "all") -> str:
    """读取 Unity Console 日志；log_type 为 all/log/warning/error/exception。"""
    return _call("debug.get_logs", count=count, type=log_type)


@mcp.tool()
def enter_play_mode() -> str:
    """让 Unity Editor 进入 Play Mode。"""
    return _call("editor.play")


@mcp.tool()
def exit_play_mode() -> str:
    """让 Unity Editor 退出 Play Mode。"""
    return _call("editor.stop")


@mcp.tool()
def capture_camera(
    output: str,
    camera: Optional[str] = None,
    width: int = 0,
    height: int = 0,
) -> str:
    """将指定相机画面保存为 PNG；相对输出路径以 Unity Assets 目录为基准。"""
    arguments: dict[str, Any] = {"output": output}
    if camera:
        arguments["camera"] = camera
    if width > 0:
        arguments["width"] = width
    if height > 0:
        arguments["height"] = height
    return _call("view.camera", **arguments)


def main() -> None:
    """通过 stdio 启动本地 MCP Server。"""
    mcp.run(transport="stdio")


if __name__ == "__main__":
    main()
