"""Mock Unity Bridge 服务器 —— 在没有 Unity 环境时验证 Python 客户端/CLI。

行为完全复刻 Unity 侧 BridgeServer 的协议（单行 JSON）。
用法:
    python scripts/mock_unity_server.py [--port 21927]
"""

import argparse
import json
import os
import socket
import struct
import threading
import zlib

# 模拟 Unity 场景树（与 C# SceneTreeCommand 返回结构一致）
MOCK_SCENE = {
    "type": "scene",
    "name": "DemoScene",
    "path": "Assets/Scenes/Demo.unity",
    "buildIndex": 0,
    "rootCount": 3,
    "roots": [
        {"name": "Main Camera", "active": True, "components": ["Transform", "Camera", "AudioListener"], "children": []},
        {"name": "Directional Light", "active": True, "components": ["Transform", "Light"], "children": []},
        {
            "name": "Player", "active": True,
            "components": ["Transform", "CharacterController", "PlayerController"],
            "children": [
                {"name": "Body", "active": True, "components": ["Transform", "Animator"],
                 "children": [
                     {"name": "LeftArm", "active": True, "components": ["Transform"], "children": []},
                     {"name": "RightArm", "active": True, "components": ["Transform"], "children": []},
                 ]},
                {"name": "Head", "active": True, "components": ["Transform", "SkinnedMeshRenderer"],
                 "children": [
                     {"name": "Hat", "active": False, "components": ["Transform"], "children": []},
                 ]},
            ],
        },
    ],
}

COMMANDS = [
    {"name": "bridge.ping", "description": "连通性测试，成功返回 pong 与服务器时间"},
    {"name": "bridge.list_commands", "description": "列出所有已通过反射注册的命令"},
    {"name": "scene.tree", "description": "以树状结构返回当前场景中的物体层级。参数: components(bool)"},
    {"name": "mesh.bounds", "description": "计算 Assets 中网格/模型/预制体的轴对齐包围盒。参数: path(string)"},
    {"name": "prefab.screenshot", "description": "将预制体复制到场景隔离位置并截图保存为 PNG。参数: path(string), offset{x,y,z}, output(string,.png), orthographic(bool), fov(number), width(int), height(int), bg(string)"},
]


def handle_client(client: socket.socket) -> None:
    with client:
        stream = client.makefile("rwb", buffering=0)
        while True:
            line = stream.readline()
            if not line:
                break
            try:
                req = json.loads(line.decode("utf-8"))
            except json.JSONDecodeError:
                stream.write((json.dumps({"ok": False, "error": "JSON 解析失败"}) + "\n").encode("utf-8"))
                continue

            cmd = req.get("cmd")
            args = req.get("args") or {}
            try:
                if cmd == "bridge.ping":
                    data = {"pong": True, "time": "2026-08-15T10:00:00Z"}
                elif cmd == "bridge.list_commands":
                    data = COMMANDS
                elif cmd == "scene.tree":
                    data = json.loads(json.dumps(MOCK_SCENE))  # 深拷贝，避免污染全局
                    if not args.get("components"):
                        for root in data["roots"]:
                            strip_components(root)
                elif cmd == "mesh.bounds":
                    data = mock_mesh_bounds(args.get("path", ""))
                elif cmd == "prefab.screenshot":
                    data = mock_screenshot(args)
                else:
                    raise KeyError(f"未知命令: {cmd}")
                resp = {"id": req.get("id"), "ok": True, "data": data}
            except Exception as e:
                resp = {"id": req.get("id"), "ok": False, "error": str(e)}

            stream.write((json.dumps(resp, ensure_ascii=False) + "\n").encode("utf-8"))


def strip_components(node) -> None:
    node.pop("components", None)
    for child in node.get("children", []):
        strip_components(child)


def _png_chunk(tag: bytes, data: bytes) -> bytes:
    chunk = tag + data
    return struct.pack(">I", len(data)) + chunk + struct.pack(">I", zlib.crc32(chunk) & 0xFFFFFFFF)


def make_png(width: int, height: int, rgba: bytes) -> bytes:
    """生成一个最小合法的 RGBA PNG（仅用于离线联调，不代表真实渲染结果）。"""
    sig = b"\x89PNG\r\n\x1a\n"
    ihdr = struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)  # 8-bit, RGBA
    raw = bytearray()
    for y in range(height):
        raw.append(0)  # 每行滤波字节
        raw += rgba[y * width * 4:(y + 1) * width * 4]
    idat = zlib.compress(bytes(raw))
    return sig + _png_chunk(b"IHDR", ihdr) + _png_chunk(b"IDAT", idat) + _png_chunk(b"IEND", b"")


def mock_screenshot(args: dict) -> dict:
    """离线模拟 prefab.screenshot：生成占位 PNG 并回显相机位置/朝向。

    真实渲染由 Unity 侧完成（RenderTexture + cam.Render + EncodeToPNG）；
    此处仅用于在无 Unity 环境时验证 Python 客户端、参数校验与文件写出逻辑。
    """
    path = args.get("path", "")
    offset = args.get("offset") or {"x": 0, "y": 0, "z": 0}
    output = args.get("output", "")
    if not output.lower().endswith(".png"):
        raise ValueError("output 必须是 .png 文件路径")

    orthographic = bool(args.get("orthographic", False))
    width = int(args.get("width", 1920))
    height = int(args.get("height", 1080))
    light = float(args.get("light", 0.0) or 0.0)

    iso = {"x": 9999, "y": 9999, "z": 9999}
    cam_pos = {k: iso[k] + float(offset.get(k, 0)) for k in ("x", "y", "z")}

    out_dir = os.path.dirname(os.path.abspath(output))
    if out_dir:
        os.makedirs(out_dir, exist_ok=True)
    png = make_png(width, height, bytes([40, 120, 130, 255]) * (width * height))
    with open(output, "wb") as f:
        f.write(png)

    return {
        "path": path,
        "resolvedPath": path if path.startswith("Assets/") else "Assets/" + path.lstrip("/"),
        "output": os.path.abspath(output),
        "cameraType": "orthographic" if orthographic else "perspective",
        "width": width,
        "height": height,
        "cameraPosition": cam_pos,
        "lookAt": iso,
        "fillLight": light if light > 0 else 0,
        "bytes": len(png),
    }


def mock_mesh_bounds(path: str) -> dict:
    """离线模拟 mesh.bounds：返回与示例一致的包围盒，并回显传入路径。

    真实计算由 Unity 侧 AssetDatabase + mesh.bounds / renderer.bounds 完成；
    此处仅用于在无 Unity 环境时验证 Python 客户端与协议。
    """
    resolved = path if path.startswith("Assets/") else "Assets/" + path.lstrip("/")
    ext = resolved.lower().rsplit(".", 1)[-1] if "." in resolved else ""
    type_name = "prefab" if ext == "prefab" else ("mesh" if ext == "mesh" else "model")
    return {
        "path": path,
        "resolvedPath": resolved,
        "type": type_name,
        "min": {"x": -2, "y": -0.5, "z": 1},
        "max": {"x": 6, "y": 2, "z": 6},
        "center": {"x": 2, "y": 0.75, "z": 3.5},
        "size": {"x": 8, "y": 2.5, "z": 5},
        "format": "x:-2~6, y:-0.5~2, z:1~6",
    }


def main() -> None:
    parser = argparse.ArgumentParser(description="Mock Unity Bridge 服务器")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=21927)
    args = parser.parse_args()

    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind((args.host, args.port))
    server.listen(5)
    print(f"[mock] Unity Bridge 模拟服务器已启动: {args.host}:{args.port} (Ctrl+C 退出)")

    try:
        while True:
            client, addr = server.accept()
            print(f"[mock] 新连接: {addr}")
            threading.Thread(target=handle_client, args=(client,), daemon=True).start()
    except KeyboardInterrupt:
        print("\n[mock] 已退出")
    finally:
        server.close()


if __name__ == "__main__":
    main()
