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
    {"name": "terrain.list", "description": "列出场景中所有 Terrain。参数: terrain(string,可选)"},
    {"name": "terrain.get_heights", "description": "读取高度图区域。参数: terrain, xBase, zBase, width, height"},
    {"name": "terrain.set_heights", "description": "写入高度图。参数: terrain, xBase, zBase, width, height, data(float[]) 或 noise/noiseScale/noiseSeed/baseHeight/heightScale"},
    {"name": "terrain.get_layers", "description": "列出 Terrain 纹理层。参数: terrain(string,可选)"},
    {"name": "terrain.get_alphamaps", "description": "读取纹理混合权重。参数: terrain, xBase, zBase, width, height"},
    {"name": "terrain.set_alphamaps", "description": "写入纹理混合权重。参数: terrain, xBase, zBase, width, height, data(float[])"},
    {"name": "terrain.list_details", "description": "列出 Terrain 草原型。参数: terrain(string,可选)"},
    {"name": "terrain.get_details", "description": "读取某层植被密度图。参数: terrain, layer, xBase, zBase, width, height"},
    {"name": "terrain.set_details", "description": "写入植被密度。参数: terrain, layer, xBase, zBase, width, height, data(int[]) 或 random/count/seed/density"},
    {"name": "terrain.list_trees", "description": "列出 Terrain 树原型与树实例。参数: terrain(string,可选)"},
    {"name": "terrain.add_trees", "description": "添加树木。参数: terrain, prototypeIndex, positions(float[]) 或 random/count/seed/minScale/maxScale"},
    {"name": "terrain.clear_trees", "description": "清空 Terrain 上所有树实例。参数: terrain(string,可选)"},
]

# 离线模拟的 Terrain 状态（仅用于无 Unity 环境联调）
MOCK_TERRAIN = {
    "name": "MainTerrain",
    "position": {"x": 0, "y": 0, "z": 0},
    "size": {"x": 1000, "y": 600, "z": 1000},
    "heightmapResolution": 513,
    "alphamapResolution": 512,
    "detailResolution": 1024,
    "holesResolution": 512,
    "layers": [
        {"index": 0, "name": "Grass", "diffuseTexture": "Assets/Textures/grass.jpg"},
        {"index": 1, "name": "Rock", "diffuseTexture": "Assets/Textures/rock.jpg"},
    ],
    "details": [{"index": 0, "name": "TallGrass"}],
    "trees": [{"index": 0, "name": "OakTree"}],
    "heightmap": {},   # {(x,z): height} 模拟高度图局部修改
    "alphamap": {},    # {(x,z,layer): weight}
    "detail": {},      # {(x,z): density}
    "tree_instances": [],  # [{prototypeIndex, x, y, z, widthScale, heightScale}]
}


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
                    data = {"count": len(COMMANDS), "commands": COMMANDS}
                elif cmd == "scene.tree":
                    data = json.loads(json.dumps(MOCK_SCENE))  # 深拷贝，避免污染全局
                    if not args.get("components"):
                        for root in data["roots"]:
                            strip_components(root)
                elif cmd == "mesh.bounds":
                    data = mock_mesh_bounds(args.get("path", ""))
                elif cmd == "prefab.screenshot":
                    data = mock_screenshot(args)
                elif cmd == "terrain.list":
                    data = mock_terrain_list()
                elif cmd == "terrain.get_heights":
                    data = mock_get_heights(args)
                elif cmd == "terrain.set_heights":
                    data = mock_set_heights(args)
                elif cmd == "terrain.get_layers":
                    data = mock_get_layers()
                elif cmd == "terrain.get_alphamaps":
                    data = mock_get_alphamaps(args)
                elif cmd == "terrain.set_alphamaps":
                    data = mock_set_alphamaps(args)
                elif cmd == "terrain.list_details":
                    data = mock_list_details()
                elif cmd == "terrain.get_details":
                    data = mock_get_details(args)
                elif cmd == "terrain.set_details":
                    data = mock_set_details(args)
                elif cmd == "terrain.list_trees":
                    data = mock_list_trees()
                elif cmd == "terrain.add_trees":
                    data = mock_add_trees(args)
                elif cmd == "terrain.clear_trees":
                    data = mock_clear_trees()
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


# ============ Terrain 模拟命令 ============


def _region(args, res_w, res_h):
    """解析区域参数，返回 (xb, zb, w, h)。"""
    xb = int(args.get("xBase", 0))
    zb = int(args.get("zBase", 0))
    w = int(args.get("width", 0)) or (res_w - xb)
    h = int(args.get("height", 0)) or (res_h - zb)
    return xb, zb, w, h


def mock_terrain_list() -> dict:
    t = MOCK_TERRAIN
    return {
        "count": 1,
        "terrains": [{
            "name": t["name"],
            "position": t["position"],
            "size": t["size"],
            "heightmapResolution": t["heightmapResolution"],
            "alphamapResolution": t["alphamapResolution"],
            "detailResolution": t["detailResolution"],
            "holesResolution": t["holesResolution"],
            "layers": len(t["layers"]),
            "detailPrototypeCount": len(t["details"]),
            "treePrototypeCount": len(t["trees"]),
            "treeInstanceCount": len(t["tree_instances"]),
        }],
    }


def mock_get_heights(args: dict) -> dict:
    t = MOCK_TERRAIN
    xb, zb, w, h = _region(args, t["heightmapResolution"], t["heightmapResolution"])
    data = []
    for y in range(h):
        for x in range(w):
            data.append(t["heightmap"].get((xb + x, zb + y), 0.5))
    return {"terrain": t["name"], "xBase": xb, "zBase": zb, "width": w, "height": h,
            "data": data, "count": len(data)}


def mock_set_heights(args: dict) -> dict:
    t = MOCK_TERRAIN
    xb, zb, w, h = _region(args, t["heightmapResolution"], t["heightmapResolution"])
    data = args.get("data") or []
    if not data:
        # noise 模式：生成简单正弦/伪噪声，仅用于联调
        import random as _r
        rng = _r.Random(int(args.get("noiseSeed", 0)))
        ox, oz = rng.random() * 100, rng.random() * 100
        scale = float(args.get("noiseScale", 1.0))
        base = float(args.get("baseHeight", 0.0))
        amp = float(args.get("heightScale", 1.0))
        for y in range(h):
            for x in range(w):
                v = base + amp * ((1 + __import__("math").sin((xb + x + ox) * scale * 0.1 + (zb + y + oz) * scale * 0.1)) / 2)
                t["heightmap"][(xb + x, zb + y)] = max(0.0, min(1.0, v))
    else:
        for y in range(h):
            for x in range(w):
                idx = y * w + x
                if idx < len(data):
                    t["heightmap"][(xb + x, zb + y)] = max(0.0, min(1.0, float(data[idx])))
    return {"terrain": t["name"], "xBase": xb, "zBase": zb, "width": w, "height": h,
            "cells": w * h, "mode": "noise" if not data else "data"}


def mock_get_layers() -> dict:
    t = MOCK_TERRAIN
    return {"terrain": t["name"], "count": len(t["layers"]), "layers": t["layers"]}


def mock_get_alphamaps(args: dict) -> dict:
    t = MOCK_TERRAIN
    xb, zb, w, h = _region(args, t["alphamapResolution"], t["alphamapResolution"])
    layers = len(t["layers"])
    data = []
    for y in range(h):
        for x in range(w):
            for l in range(layers):
                data.append(t["alphamap"].get((xb + x, zb + y, l), 1.0 if l == 0 else 0.0))
    return {"terrain": t["name"], "xBase": xb, "zBase": zb, "width": w, "height": h,
            "layers": layers, "data": data, "count": len(data)}


def mock_set_alphamaps(args: dict) -> dict:
    t = MOCK_TERRAIN
    xb, zb, w, h = _region(args, t["alphamapResolution"], t["alphamapResolution"])
    layers = len(t["layers"])
    data = args.get("data") or []
    for y in range(h):
        for x in range(w):
            s = 0.0
            for l in range(layers):
                idx = (y * w + x) * layers + l
                v = float(data[idx]) if idx < len(data) else 0.0
                t["alphamap"][(xb + x, zb + y, l)] = v
                s += v
            if s > 1.0001:
                for l in range(layers):
                    t["alphamap"][(xb + x, zb + y, l)] /= s
    return {"terrain": t["name"], "xBase": xb, "zBase": zb, "width": w, "height": h,
            "layers": layers, "cells": w * h, "normalized": True}


def mock_list_details() -> dict:
    t = MOCK_TERRAIN
    return {"terrain": t["name"], "count": len(t["details"]), "details": t["details"]}


def mock_get_details(args: dict) -> dict:
    t = MOCK_TERRAIN
    layer = int(args.get("layer", 0))
    xb, zb, w, h = _region(args, t["detailResolution"], t["detailResolution"])
    data = []
    for y in range(h):
        for x in range(w):
            data.append(t["detail"].get((layer, xb + x, zb + y), 0))
    return {"terrain": t["name"], "layer": layer, "xBase": xb, "zBase": zb,
            "width": w, "height": h, "data": data, "count": len(data)}


def mock_set_details(args: dict) -> dict:
    t = MOCK_TERRAIN
    layer = int(args.get("layer", 0))
    xb, zb, w, h = _region(args, t["detailResolution"], t["detailResolution"])
    if args.get("random"):
        import random as _r
        rng = _r.Random(int(args.get("seed", 0)))
        density = int(args.get("density", 3))
        for _ in range(int(args.get("count", 0))):
            x = xb + rng.randrange(w)
            z = zb + rng.randrange(h)
            t["detail"][(layer, x, z)] = density
        mode = "random"
    else:
        data = args.get("data") or []
        for y in range(h):
            for x in range(w):
                idx = y * w + x
                if idx < len(data):
                    t["detail"][(layer, xb + x, zb + y)] = max(0, min(16, int(data[idx])))
        mode = "data"
    return {"terrain": t["name"], "layer": layer, "xBase": xb, "zBase": zb,
            "width": w, "height": h, "cells": w * h, "mode": mode}


def mock_list_trees() -> dict:
    t = MOCK_TERRAIN
    return {
        "terrain": t["name"],
        "prototypeCount": len(t["trees"]),
        "instanceCount": len(t["tree_instances"]),
        "prototypes": t["trees"],
        "instances": [
            {"index": i, "prototypeIndex": ti["prototypeIndex"],
             "position": {"x": ti["x"], "y": ti["y"], "z": ti["z"]},
             "widthScale": ti["widthScale"], "heightScale": ti["heightScale"]}
            for i, ti in enumerate(t["tree_instances"])
        ],
    }


def mock_add_trees(args: dict) -> dict:
    t = MOCK_TERRAIN
    pi = int(args.get("prototypeIndex", 0))
    positions = args.get("positions") or []
    if positions:
        mode = "positions"
        added = len(positions) // 3
        for i in range(0, len(positions) - 2, 3):
            t["tree_instances"].append({
                "prototypeIndex": pi,
                "x": positions[i], "y": positions[i + 1], "z": positions[i + 2],
                "widthScale": 1.0, "heightScale": 1.0,
            })
    else:
        import random as _r
        mode = "random"
        rng = _r.Random(int(args.get("seed", 0)))
        count = int(args.get("count", 0))
        min_s = float(args.get("minScale", 0.8))
        max_s = float(args.get("maxScale", 1.2))
        added = count
        for _ in range(count):
            t["tree_instances"].append({
                "prototypeIndex": pi,
                "x": rng.random(), "y": 0.5, "z": rng.random(),
                "widthScale": min_s + rng.random() * (max_s - min_s),
                "heightScale": min_s + rng.random() * (max_s - min_s),
            })
    return {"terrain": t["name"], "prototypeIndex": pi, "added": added,
            "total": len(t["tree_instances"]), "mode": mode}


def mock_clear_trees() -> dict:
    t = MOCK_TERRAIN
    removed = len(t["tree_instances"])
    t["tree_instances"].clear()
    return {"terrain": t["name"], "removed": removed}


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
