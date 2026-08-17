"""Mock Unity Bridge 服务器 —— 在没有 Unity 环境时验证 Python 客户端/CLI。

行为完全复刻 Unity 侧 BridgeServer 的协议（单行 JSON）。
用法:
    python scripts/mock_unity_server.py [--port 21927]
"""

import argparse
import copy
import json
import os
import socket
import struct
import sys
import threading
import zlib

# 端口默认值与 CLI 保持一致：优先从 bridge.ini 的 [server] port 读取，缺失回退 21927
try:
    sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    from unity_bridge.config import load_server_port
except Exception:
    load_server_port = lambda: 21927

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
    {"name": "bridge.version", "description": "返回桥接层版本号与命令统计，用于确认 Unity 侧代码是否为最新"},
    {"name": "bridge.reload", "description": "触发 Unity 脚本重编译（domain reload），编译完成后服务器自动恢复"},
    {"name": "debug.log", "description": "在 Unity Console 打印一条 Info 日志。参数: message(string)"},
    {"name": "debug.log_warning", "description": "在 Unity Console 打印一条 Warning 日志。参数: message(string)"},
    {"name": "debug.log_error", "description": "在 Unity Console 打印一条 Error 日志。参数: message(string)"},
    {"name": "debug.get_logs", "description": "读取最近 N 条 Console 日志（环形缓冲）。参数: count(int,可选,默认50), type(string,可选 all/log/warning/error/exception)"},
    {"name": "debug.log_version", "description": "在 Unity Console 打印桥接层版本号（含命令总数）。参数: 无"},
    {"name": "scene.tree", "description": "以树状结构返回当前场景中的物体层级。参数: components(bool)"},
    {"name": "mesh.bounds", "description": "计算 Assets 中网格/模型/预制体的轴对齐包围盒。参数: path(string)"},
    {"name": "prefab.screenshot", "description": "将预制体复制到场景隔离位置并截图保存为 PNG（旋转保持资产原有，摄制后销毁临时对象）。参数: path(string), offset{x,y,z}, output(string,.png), orthographic(bool), fov(number), width(int), height(int), bg(string)"},
    {"name": "terrain.list", "description": "列出场景中所有 Terrain。参数: terrain(string,可选)"},
    {"name": "terrain.get_heights", "description": "读取高度图区域。参数: terrain, xBase, zBase, width, height"},
    {"name": "terrain.set_heights", "description": "写入高度图。参数: terrain, xBase, zBase, width, height, data(float[]) 或 noise/noiseScale/noiseSeed/baseHeight/heightScale"},
    {"name": "terrain.get_layers", "description": "列出 Terrain 纹理层。参数: terrain(string,可选)"},
    {"name": "terrain.get_diffuse_dirs", "description": "返回 Terrain 所有 TerrainLayer 的 Diffuse 贴图目录（去重）及完整路径。参数: terrain(string,可选)"},
    {"name": "terrain.get_tree_prefab_dirs", "description": "返回 Terrain 所有树原型的 Prefab 目录（去重）及完整路径。参数: terrain(string,可选)"},
    {"name": "terrain.get_detail_asset_dirs", "description": "返回 Terrain 所有草原型的预制体或贴图目录（去重）及完整路径。参数: terrain(string,可选)"},
    {"name": "terrain.get_alphamaps", "description": "读取纹理混合权重。参数: terrain, xBase, zBase, width, height"},
    {"name": "terrain.set_alphamaps", "description": "写入纹理混合权重。参数: terrain, xBase, zBase, width, height, data(float[])"},
    {"name": "terrain.list_details", "description": "列出 Terrain 草原型。参数: terrain(string,可选)"},
    {"name": "terrain.get_details", "description": "读取某层植被密度图。参数: terrain, layer, xBase, zBase, width, height"},
    {"name": "terrain.set_details", "description": "写入植被密度。参数: terrain, layer, xBase, zBase, width, height, data(int[]) 或 random/count/seed/density"},
    {"name": "terrain.list_trees", "description": "列出 Terrain 树原型与树实例。参数: terrain(string,可选)"},
    {"name": "terrain.add_trees", "description": "添加树木。参数: terrain, prototypeIndex, positions(float[]) 或 random/count/seed/minScale/maxScale"},
    {"name": "terrain.clear_trees", "description": "清空 Terrain 上所有树实例。参数: terrain(string,可选)"},
    {"name": "terrain.stash", "description": "把当前地形的树木/植被全量序列化为 JSON 存到工具 stash 子目录（同名报错）。参数: terrain(string,可选), type(string,可选 trees/details/all), name(string,必填)"},
    {"name": "terrain.apply_stash", "description": "读取 stash JSON 并整体写回地形。参数: terrain(string,可选), type(string,可选), name(string,必填)"},
    {"name": "terrain.stash_delete", "description": "删除指定 stash 文件。参数: type(string,必填 trees/details), name(string,必填)"},
    {"name": "terrain.stash_list", "description": "列出工具 stash 子目录下所有 stash 文件。参数: type(string,可选)"},
    {"name": "view.camera", "description": "渲染指定相机的实时画面保存为 PNG（默认 MainCamera）。参数: camera(string,可选), output(string,.png), width(int,可选), height(int,可选)"},
    {"name": "gameobject.get", "description": "读取 GameObject 的 active 状态与 Transform 的 position/rotation/scale。参数: target(string,必填,路径优先名称兼容), quaternion(bool,可选)"},
    {"name": "gameobject.set", "description": "写入 GameObject 的 active 状态与 Transform 的 position/rotation/scale（支持 Undo）。参数: target(string,必填), active(int,可选 -1/0/1), position(float[]3), rotation(float[]3/4), scale(float[]3), quaternion(bool)"},
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
    "details": [
        {"index": 0, "name": "TallGrass", "type": "prefab", "asset": "Assets/Vegetation/TallGrass.prefab"},
        {"index": 1, "name": "FlowerPatch", "type": "texture", "asset": "Assets/Textures/flower.png"},
    ],
    "trees": [
        {"index": 0, "name": "OakTree", "prefab": "Assets/Vegetation/Trees/OakTree.prefab"},
        {"index": 1, "name": "PineTree", "prefab": "Assets/Vegetation/Trees/PineTree.prefab"},
    ],
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
                elif cmd == "bridge.version":
                    data = {"version": "1.6.0", "commandCount": len(COMMANDS),
                            "terrainCommandCount": sum(1 for c in COMMANDS if c["name"].startswith("terrain."))}
                elif cmd == "bridge.reload":
                    data = {"requested": True,
                            "message": "[mock] 重编译已触发（模拟立即恢复）"}
                elif cmd in ("debug.log", "debug.log_warning", "debug.log_error"):
                    level = {"debug.log": "info",
                             "debug.log_warning": "warning",
                             "debug.log_error": "error"}[cmd]
                    mock_append_log({"debug.log": "log",
                                     "debug.log_warning": "warning",
                                     "debug.log_error": "error"}[cmd],
                                    args.get("message", ""))
                    data = {"level": level,
                            "message": args.get("message", ""),
                            "logged": True}
                elif cmd == "debug.get_logs":
                    data = mock_get_logs(args)
                elif cmd == "debug.log_version":
                    data = mock_log_version()
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
                elif cmd == "terrain.get_diffuse_dirs":
                    data = mock_get_diffuse_dirs()
                elif cmd == "terrain.get_tree_prefab_dirs":
                    data = mock_get_tree_prefab_dirs()
                elif cmd == "terrain.get_detail_asset_dirs":
                    data = mock_get_detail_asset_dirs()
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
                elif cmd == "terrain.stash":
                    data = mock_stash(args)
                elif cmd == "terrain.apply_stash":
                    data = mock_apply_stash(args)
                elif cmd == "terrain.stash_delete":
                    data = mock_stash_delete(args)
                elif cmd == "terrain.stash_list":
                    data = mock_stash_list(args)
                elif cmd == "view.camera":
                    data = mock_view_camera(args)
                elif cmd == "gameobject.get":
                    data = mock_go_get(args)
                elif cmd == "gameobject.set":
                    data = mock_go_set(args)
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


# ============ debug.get_logs 模拟（内存环形缓冲，复刻 C# 侧行为）============

MOCK_LOGS = []  # [{index, time, type, message, stackTrace}]
MOCK_LOG_MAX = 500


def mock_append_log(type_: str, message: str) -> None:
    """把一条日志加入缓冲（模拟 Unity 的 Application.logMessageReceived 回调）。"""
    entry = {"index": len(MOCK_LOGS), "time": 0.0,
             "type": type_, "message": message, "stackTrace": ""}
    MOCK_LOGS.append(entry)
    if len(MOCK_LOGS) > MOCK_LOG_MAX:
        del MOCK_LOGS[:len(MOCK_LOGS) - MOCK_LOG_MAX]


def mock_get_logs(args: dict) -> dict:
    """离线模拟 debug.get_logs：按 count 取最近、按 type 过滤。"""
    count = int(args.get("count", 50)) or 50
    filter_ = (args.get("type") or "all").strip().lower()
    if filter_ not in ("all", "log", "warning", "error", "exception"):
        raise ValueError(f"type 必须是 all/log/warning/error/exception，当前: {filter_}")
    recent = MOCK_LOGS[-count:]
    entries = [e for e in recent if filter_ == "all" or e["type"] == filter_]
    return {"count": len(entries), "entries": entries}


def mock_log_version() -> dict:
    """离线模拟 debug.log_version：记录一条版本日志并返回结果。"""
    mock_append_log("log", "版本号: v1.6.0（mock）")
    return {"level": "info", "message": "v1.6.0", "logged": True}


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


def mock_get_diffuse_dirs() -> dict:
    t = MOCK_TERRAIN
    dirs = []
    layers_out = []
    for l in t["layers"]:
        tex = l.get("diffuseTexture", "")
        d = os.path.dirname(tex).replace("\\", "/") if tex else ""
        if d and d not in dirs:
            dirs.append(d)
        layers_out.append({
            "index": l["index"],
            "name": l["name"],
            "diffuseTexture": tex,
            "diffuseDir": d,
        })
    return {
        "terrain": t["name"],
        "count": len(layers_out),
        "directoryCount": len(dirs),
        "directories": dirs,
        "layers": layers_out,
    }


def mock_get_tree_prefab_dirs() -> dict:
    t = MOCK_TERRAIN
    dirs = []
    trees_out = []
    for tr in t["trees"]:
        prefab = tr.get("prefab", "")
        d = os.path.dirname(prefab).replace("\\", "/") if prefab else ""
        if d and d not in dirs:
            dirs.append(d)
        trees_out.append({
            "index": tr["index"],
            "name": tr["name"],
            "prefab": prefab,
            "prefabDir": d,
        })
    return {
        "terrain": t["name"],
        "count": len(trees_out),
        "directoryCount": len(dirs),
        "directories": dirs,
        "trees": trees_out,
    }


def mock_get_detail_asset_dirs() -> dict:
    t = MOCK_TERRAIN
    dirs = []
    details_out = []
    for d in t["details"]:
        asset = d.get("asset", "")
        typ = d.get("type", "none")
        adir = os.path.dirname(asset).replace("\\", "/") if asset else ""
        if adir and adir not in dirs:
            dirs.append(adir)
        details_out.append({
            "index": d["index"],
            "name": d["name"],
            "type": typ,
            "asset": asset,
            "assetDir": adir,
        })
    return {
        "terrain": t["name"],
        "count": len(details_out),
        "directoryCount": len(dirs),
        "directories": dirs,
        "details": details_out,
    }


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


# ============ Terrain stash 模拟（内存态，复刻同名检查/删除/列表）============

# MOCK_STASH[type][name] = { ...数据... }，type ∈ {"trees", "details"}
MOCK_STASH = {"trees": {}, "details": {}}


def mock_stash(args: dict) -> dict:
    """离线模拟 terrain.stash：把当前树实例/植被密度存到内存 stash（同名报错）。"""
    t = MOCK_TERRAIN
    type_ = (args.get("type") or "all").strip().lower()
    if type_ not in ("trees", "details", "all"):
        raise ValueError(f"type 必须是 trees/details/all，当前: {args.get('type')}")
    name = (args.get("name") or "").strip()
    if not name:
        raise ValueError("需要参数 name（stash 名称）")

    result = {"terrain": t["name"], "type": type_, "name": name,
              "treeInstances": 0, "detailLayers": 0}
    written = []

    if type_ in ("trees", "all"):
        if name in MOCK_STASH["trees"]:
            raise ValueError(f"同名 stash 已存在，拒绝覆盖: Assets/unity-python-bridge/stash/trees/{name}.json")
        MOCK_STASH["trees"][name] = {
            "type": "trees",
            "prototypeCount": len(t["trees"]),
            "instances": copy.deepcopy(t["tree_instances"]),
        }
        result["treeInstances"] = len(t["tree_instances"])
        written.append(f"Assets/unity-python-bridge/stash/trees/{name}.json")

    if type_ in ("details", "all"):
        if name in MOCK_STASH["details"]:
            raise ValueError(f"同名 stash 已存在，拒绝覆盖: Assets/unity-python-bridge/stash/details/{name}.json")
        MOCK_STASH["details"][name] = {
            "type": "details",
            "layerCount": len(t["details"]),
            "detailWidth": t["detailResolution"],
            "detailHeight": t["detailResolution"],
            "detail": copy.deepcopy(t["detail"]),
        }
        result["detailLayers"] = len(t["details"])
        written.append(f"Assets/unity-python-bridge/stash/details/{name}.json")

    result["path"] = ", ".join(written)
    return result


def mock_apply_stash(args: dict) -> dict:
    """离线模拟 terrain.apply_stash：把内存 stash 整体写回地形。"""
    t = MOCK_TERRAIN
    type_ = (args.get("type") or "all").strip().lower()
    if type_ not in ("trees", "details", "all"):
        raise ValueError(f"type 必须是 trees/details/all，当前: {args.get('type')}")
    name = (args.get("name") or "").strip()
    if not name:
        raise ValueError("需要参数 name（stash 名称）")

    result = {"terrain": t["name"], "type": type_, "name": name,
              "treeInstances": 0, "detailLayers": 0}
    applied = []

    if type_ in ("trees", "all"):
        entry = MOCK_STASH["trees"].get(name)
        if entry is None:
            raise ValueError(f"stash 文件不存在（trees）: {name}.json（请先用 terrain.stash 保存）")
        if entry["prototypeCount"] != len(t["trees"]):
            raise ValueError(f"stash '{name}' 的树原型数({entry['prototypeCount']})与当前地形({len(t['trees'])})不一致，拒绝应用")
        t["tree_instances"] = copy.deepcopy(entry["instances"])
        result["treeInstances"] = len(t["tree_instances"])
        applied.append(f"Assets/unity-python-bridge/stash/trees/{name}.json")

    if type_ in ("details", "all"):
        entry = MOCK_STASH["details"].get(name)
        if entry is None:
            raise ValueError(f"stash 文件不存在（details）: {name}.json（请先用 terrain.stash 保存）")
        if entry["layerCount"] != len(t["details"]):
            raise ValueError(f"stash '{name}' 的草原型数({entry['layerCount']})与当前地形({len(t['details'])})不一致，拒绝应用")
        t["detail"] = copy.deepcopy(entry["detail"])
        result["detailLayers"] = entry["layerCount"]
        applied.append(f"Assets/unity-python-bridge/stash/details/{name}.json")

    result["path"] = ", ".join(applied)
    return result


def mock_stash_delete(args: dict) -> dict:
    """离线模拟 terrain.stash_delete。"""
    type_ = (args.get("type") or "").strip().lower()
    if type_ not in ("trees", "details"):
        raise ValueError("stash_delete 的 type 必须是 trees 或 details（不能是 all）")
    name = (args.get("name") or "").strip()
    if not name:
        raise ValueError("需要参数 name（stash 名称）")
    if name not in MOCK_STASH[type_]:
        raise ValueError(f"stash 文件不存在: {type_}/{name}.json")
    del MOCK_STASH[type_][name]
    return {"type": type_, "name": name,
            "path": f"Assets/unity-python-bridge/stash/{type_}/{name}.json",
            "deleted": True}


def mock_stash_list(args: dict) -> dict:
    """离线模拟 terrain.stash_list。"""
    type_ = (args.get("type") or "all").strip().lower()
    if type_ not in ("trees", "details", "all"):
        raise ValueError(f"type 必须是 trees/details/all，当前: {args.get('type')}")
    dirs = ["trees", "details"] if type_ == "all" else [type_]
    entries = []
    for d in dirs:
        for name in sorted(MOCK_STASH[d].keys()):
            entries.append({
                "type": d,
                "name": name,
                "path": f"Assets/unity-python-bridge/stash/{d}/{name}.json",
                "bytes": 128,  # mock 不模拟真实文件大小
            })
    return {"stashDir": "Assets/unity-python-bridge/stash",
            "count": len(entries), "entries": entries}


# ============ view.camera 模拟 ============


def mock_view_camera(args: dict) -> dict:
    """离线模拟 view.camera：生成占位 PNG 并回显相机名/分辨率。

    真实渲染由 Unity 侧完成（把相机 targetTexture 临时指向 RT 后 cam.Render）；
    此处仅用于在无 Unity 环境时验证 Python 客户端、参数校验与文件写出逻辑。
    """
    cameras = [r["name"] for r in MOCK_SCENE["roots"]
               if any(c == "Camera" for c in r.get("components", []))]
    if not cameras:
        raise ValueError("场景中没有任何相机")

    requested = args.get("camera")
    if requested:
        if requested not in cameras:
            raise ValueError(f"未找到名为 '{requested}' 的相机（可用: {cameras}）")
        camera = requested
    else:
        camera = "Main Camera" if "Main Camera" in cameras else cameras[0]

    output = args.get("output", "")
    if not output.lower().endswith(".png"):
        raise ValueError("output 必须是 .png 文件路径")
    width = int(args.get("width", 0)) or 1920
    height = int(args.get("height", 0)) or 1080

    out_dir = os.path.dirname(os.path.abspath(output))
    if out_dir:
        os.makedirs(out_dir, exist_ok=True)
    png = make_png(width, height, bytes([20, 80, 200, 255]) * (width * height))
    with open(output, "wb") as f:
        f.write(png)

    return {
        "camera": camera,
        "requestedCamera": requested,
        "output": os.path.abspath(output),
        "width": width,
        "height": height,
        "bytes": len(png),
    }


# ============ gameobject.get / gameobject.set 模拟 ============

# path -> {"active": bool, "position": {x,y,z}, "rotationEuler": {x,y,z}, "scale": {x,y,z}}
MOCK_GO_OVERRIDES = {}


def _find_scene_node(target: str):
    """在 MOCK_SCENE 中按路径/名称定位节点，返回 (node, path)。"""
    if not target:
        raise ValueError("gameobject 命令需要参数 target")

    def find_path(roots, segs, depth):
        for r in roots:
            if r["name"] == segs[depth]:
                if depth == len(segs) - 1:
                    return r, [r["name"]]
                child, path = find_path(r.get("children", []), segs, depth + 1)
                if child is not None:
                    return child, [r["name"]] + path
        return None, None

    if "/" in target:
        segs = [s for s in target.split("/") if s]
        node, path = find_path(MOCK_SCENE["roots"], segs, 0)
        if node is None:
            raise ValueError(f"场景中未找到路径 '{target}'")
        return node, "/".join(path)

    matches = []

    def collect(node, prefix):
        p = (prefix + "/" + node["name"]) if prefix else node["name"]
        if node["name"] == target:
            matches.append((node, p))
        for c in node.get("children", []):
            collect(c, p)

    for r in MOCK_SCENE["roots"]:
        collect(r, "")
    if len(matches) == 0:
        raise ValueError(f"场景中未找到名为 '{target}' 的物体")
    if len(matches) > 1:
        sample = ", ".join(m[1] for m in matches[:2])
        raise ValueError(f"场景中有 {len(matches)} 个名为 '{target}' 的物体，请使用层级路径（如 {sample}）")
    return matches[0]


def _go_state(target: str) -> dict:
    node, path = _find_scene_node(target)
    ov = MOCK_GO_OVERRIDES.get(path, {})
    return {
        "target": target,
        "resolvedPath": path,
        "active": ov.get("active", node.get("active", True)),
        "activeInHierarchy": ov.get("active", node.get("active", True)),
        "position": ov.get("position", {"x": 0, "y": 0, "z": 0}),
        "rotationEuler": ov.get("rotationEuler", {"x": 0, "y": 0, "z": 0}),
        "quaternion": False,
        "rotationQuat": {"x": 0, "y": 0, "z": 0, "w": 1},
        "scale": ov.get("scale", {"x": 1, "y": 1, "z": 1}),
    }


def mock_go_get(args: dict) -> dict:
    target = args.get("target", "")
    state = _go_state(target)
    state["quaternion"] = bool(args.get("quaternion", False))
    return state


def mock_go_set(args: dict) -> dict:
    target = args.get("target", "")
    _, path = _find_scene_node(target)
    ov = MOCK_GO_OVERRIDES.setdefault(path, {})

    if "active" in args:
        a = int(args["active"])
        if a not in (-1, 0, 1):
            raise ValueError(f"active 必须是 -1(不改)/0(隐藏)/1(激活)，当前: {a}")
        if a != -1:
            ov["active"] = (a == 1)

    if args.get("position") is not None:
        p = args["position"]
        if len(p) != 3:
            raise ValueError("position 必须是 3 个分量 {x,y,z}")
        ov["position"] = {"x": p[0], "y": p[1], "z": p[2]}

    if args.get("rotation") is not None:
        r = args["rotation"]
        if args.get("quaternion"):
            if len(r) != 4:
                raise ValueError("quaternion=true 时 rotation 必须是 4 个分量 {x,y,z,w}")
            # mock 简化：不真正做四元数→欧拉换算，位置校验与读写逻辑已验证即可
            ov["rotationEuler"] = {"x": r[0], "y": r[1], "z": r[2]}
        else:
            if len(r) != 3:
                raise ValueError("rotation 必须是 3 个分量 {x,y,z}（欧拉角）")
            ov["rotationEuler"] = {"x": r[0], "y": r[1], "z": r[2]}

    if args.get("scale") is not None:
        s = args["scale"]
        if len(s) != 3:
            raise ValueError("scale 必须是 3 个分量 {x,y,z}")
        ov["scale"] = {"x": s[0], "y": s[1], "z": s[2]}

    return _go_state(target)


def main() -> None:
    parser = argparse.ArgumentParser(description="Mock Unity Bridge 服务器")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=load_server_port())
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
