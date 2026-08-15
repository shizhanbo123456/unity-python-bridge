"""Unity Bridge 命令行入口。

用法:
    python -m unity_bridge tree                 # 打印当前场景物体层级树
    python -m unity_bridge tree --components     # 同时显示组件类型
    python -m unity_bridge tree --json           # 输出原始 JSON
    python -m unity_bridge list                  # 列出 Unity 侧所有可用命令
    python -m unity_bridge mesh-bounds Assets/.../Rock.fbx   # 计算网格/模型/预制体包围盒
    python -m unity_bridge screenshot Assets/Prefabs/Tree.prefab out/tree.png \
        --offset "3,2,5" [--orthographic] [--fov 50] [--width 1920] [--height 1080] \
        [--bg "0.2,0.2,0.2,1"] [--light 1.5]
"""

from __future__ import annotations

import argparse
import json
import sys
from typing import List, Optional

from .client import DEFAULT_HOST, DEFAULT_PORT, UnityBridgeError, UnityClient


# 树形绘制字符
_TEE = "├── "
_LAST = "└── "
_PIPE = "│   "
_SPACE = "    "


def render_tree(node: dict) -> List[str]:
    """把 C# 返回的场景树节点渲染成 ├── / └── 风格的文本行（根节点无前缀）。"""
    lines: List[str] = [_format_label(node)]
    children = node.get("children") or []
    for i, child in enumerate(children):
        is_last = i == len(children) - 1
        _render(child, "", _LAST if is_last else _TEE, lines)
    return lines


def _format_label(node: dict) -> str:
    label = node.get("name", "?")
    if not node.get("active", True):
        label += " (inactive)"
    if node.get("components"):
        label += "  [" + ", ".join(node["components"]) + "]"
    return label


def _render(node: dict, prefix: str, connector: str, out: List[str]) -> None:
    out.append(prefix + connector + _format_label(node))

    children = node.get("children") or []
    child_prefix = prefix + (_SPACE if connector == _LAST else _PIPE)
    for i, child in enumerate(children):
        is_last = i == len(children) - 1
        _render(child, child_prefix, _LAST if is_last else _TEE, out)


def _cmd_tree(args) -> int:
    with UnityClient(args.host, args.port, args.timeout) as client:
        data = client.scene_tree(components=args.components)

    if args.json:
        print(json.dumps(data, ensure_ascii=False, indent=2))
        return 0

    print(f"Scene: {data.get('name', '?')}  ({data.get('rootCount', '?')} 个根物体)")
    for root in data.get("roots", []):
        for line in render_tree(root):
            print(line)
    return 0


def _cmd_list(args) -> int:
    with UnityClient(args.host, args.port, args.timeout) as client:
        data = client.list_commands()
    commands = data.get("commands") if isinstance(data, dict) else data
    for c in commands or []:
        name = c.get("name", "?")
        desc = c.get("description", "")
        print(f"{name:<24} {desc}")
    return 0


def _cmd_version(args) -> int:
    with UnityClient(args.host, args.port, args.timeout) as client:
        data = client.call("bridge.version")
    if args.json:
        print(json.dumps(data, ensure_ascii=False, indent=2))
        return 0
    print(f"version            : v{data.get('version')}")
    print(f"commandCount       : {data.get('commandCount')}")
    print(f"terrainCommandCount: {data.get('terrainCommandCount')}")
    return 0


def _cmd_mesh_bounds(args) -> int:
    with UnityClient(args.host, args.port, args.timeout) as client:
        data = client.mesh_bounds(args.path)

    if args.json:
        print(json.dumps(data, ensure_ascii=False, indent=2))
        return 0

    print(f"path  : {data.get('resolvedPath')}")
    print(f"type  : {data.get('type')}")
    print(f"bounds: {data.get('format')}")
    mn, mx, sz = data.get("min", {}), data.get("max", {}), data.get("size", {})
    print(f"  min : ({mn.get('x')}, {mn.get('y')}, {mn.get('z')})")
    print(f"  max : ({mx.get('x')}, {mx.get('y')}, {mx.get('z')})")
    print(f"  size: ({sz.get('x')}, {sz.get('y')}, {sz.get('z')})")
    return 0


def _parse_vec3(s: str) -> dict:
    parts = [float(x) for x in s.split(",")]
    if len(parts) != 3:
        raise ValueError("需要 3 个分量，格式 'x,y,z'")
    return {"x": parts[0], "y": parts[1], "z": parts[2]}


def _cmd_screenshot(args) -> int:
    try:
        offset = _parse_vec3(args.offset)
    except ValueError as e:
        print(f"[错误] offset 解析失败: {e}", file=sys.stderr)
        return 1

    if not args.output.lower().endswith(".png"):
        print(f"[错误] output 必须是 .png 文件路径（当前: {args.output}）", file=sys.stderr)
        return 1

    with UnityClient(args.host, args.port, args.timeout) as client:
        data = client.prefab_screenshot(
            path=args.path,
            offset=offset,
            output=args.output,
            orthographic=args.orthographic,
            fov=args.fov,
            width=args.width,
            height=args.height,
            bg=args.bg,
            light=args.light,
        )

    if args.json:
        print(json.dumps(data, ensure_ascii=False, indent=2))
        return 0

    cp = data.get("cameraPosition", {})
    la = data.get("lookAt", {})
    print(f"prefab : {data.get('resolvedPath')}")
    print(f"output : {data.get('output')}")
    print(f"camera : {data.get('cameraType')}  {data.get('width')}x{data.get('height')}")
    print(f"camPos : ({cp.get('x')}, {cp.get('y')}, {cp.get('z')})")
    print(f"lookAt : ({la.get('x')}, {la.get('y')}, {la.get('z')})")
    light_val = data.get("fillLight", 0)
    print(f"light  : {light_val if light_val else '无补光'}")
    print(f"bytes  : {data.get('bytes')}")
    return 0


# ============ Terrain 程序化编辑命令 ============


def _parse_floats(s):
    """把 '1,2,3' / '1 2 3' 解析为 float 列表。"""
    parts = s.replace(",", " ").split()
    return [float(x) for x in parts]


def _parse_ints(s):
    parts = s.replace(",", " ").split()
    return [int(x) for x in parts]


def _cmd_terrain_list(args) -> int:
    with UnityClient(args.host, args.port, args.timeout) as client:
        data = client.terrain_list(args.terrain)
    if args.json:
        print(json.dumps(data, ensure_ascii=False, indent=2))
        return 0
    print(f"count: {data.get('count')}")
    for t in data.get("terrains", []):
        pos = t.get("position", {})
        size = t.get("size", {})
        print(f"  {t.get('name')}  pos=({pos.get('x')},{pos.get('y')},{pos.get('z')}) "
              f"size=({size.get('x')},{size.get('y')},{size.get('z')})")
        print(f"      heightmap={t.get('heightmapResolution')} "
              f"alphamap={t.get('alphamapResolution')} detail={t.get('detailResolution')} "
              f"layers={t.get('layers')} trees={t.get('treeInstanceCount')}")
    return 0


def _cmd_terrain_get_heights(args) -> int:
    with UnityClient(args.host, args.port, args.timeout) as client:
        data = client.get_heights(args.terrain, args.xBase, args.zBase, args.width, args.height)
    if args.json:
        print(json.dumps(data, ensure_ascii=False, indent=2))
        return 0
    print(f"terrain: {data.get('terrain')}  region: "
          f"xBase={data.get('xBase')} zBase={data.get('zBase')} {data.get('width')}x{data.get('height')}")
    d = data.get("data", [])
    print(f"count  : {data.get('count')}")
    if d:
        print(f"range  : min={min(d):.4f} max={max(d):.4f}  (前8个: {[round(v,4) for v in d[:8]]})")
    return 0


def _cmd_terrain_set_heights(args) -> int:
    data = _parse_floats(args.data) if args.data else None
    if data is None and not args.noise:
        print("[错误] 必须提供 --data 或 --noise", file=sys.stderr)
        return 1
    with UnityClient(args.host, args.port, args.timeout) as client:
        data = client.set_heights(
            args.terrain, args.xBase, args.zBase, args.width, args.height,
            data=data, noise=args.noise, noise_scale=args.noiseScale,
            noise_seed=args.noiseSeed, base_height=args.baseHeight,
            height_scale=args.heightScale)
    if args.json:
        print(json.dumps(data, ensure_ascii=False, indent=2))
        return 0
    print(f"terrain: {data.get('terrain')}  mode={data.get('mode')}  "
          f"region: {data.get('width')}x{data.get('height')}  cells={data.get('cells')}")
    return 0


def _cmd_terrain_get_layers(args) -> int:
    with UnityClient(args.host, args.port, args.timeout) as client:
        data = client.get_layers(args.terrain)
    if args.json:
        print(json.dumps(data, ensure_ascii=False, indent=2))
        return 0
    print(f"terrain: {data.get('terrain')}  count: {data.get('count')}")
    for l in data.get("layers", []):
        tex = l.get("diffuseTexture") or "(无贴图)"
        print(f"  [{l.get('index')}] {l.get('name')}  {tex}")
    return 0


def _cmd_terrain_get_alphamaps(args) -> int:
    with UnityClient(args.host, args.port, args.timeout) as client:
        data = client.get_alphamaps(args.terrain, args.xBase, args.zBase, args.width, args.height)
    if args.json:
        print(json.dumps(data, ensure_ascii=False, indent=2))
        return 0
    print(f"terrain: {data.get('terrain')}  region: "
          f"xBase={data.get('xBase')} zBase={data.get('zBase')} "
          f"{data.get('width')}x{data.get('height')}  layers={data.get('layers')}")
    d = data.get("data", [])
    print(f"count  : {data.get('count')}")
    if d:
        print(f"range  : min={min(d):.4f} max={max(d):.4f}  (前8个: {[round(v,4) for v in d[:8]]})")
    return 0


def _cmd_terrain_set_alphamaps(args) -> int:
    data = _parse_floats(args.data)
    with UnityClient(args.host, args.port, args.timeout) as client:
        data = client.set_alphamaps(args.terrain, args.xBase, args.zBase,
                                    args.width, args.height, data=data)
    if args.json:
        print(json.dumps(data, ensure_ascii=False, indent=2))
        return 0
    print(f"terrain: {data.get('terrain')}  region: "
          f"{data.get('width')}x{data.get('height')}  layers={data.get('layers')}  "
          f"cells={data.get('cells')}  normalized={data.get('normalized')}")
    return 0


def _cmd_terrain_list_details(args) -> int:
    with UnityClient(args.host, args.port, args.timeout) as client:
        data = client.list_details(args.terrain)
    if args.json:
        print(json.dumps(data, ensure_ascii=False, indent=2))
        return 0
    print(f"terrain: {data.get('terrain')}  count: {data.get('count')}")
    for d in data.get("details", []):
        print(f"  [{d.get('index')}] {d.get('name')}")
    return 0


def _cmd_terrain_get_details(args) -> int:
    with UnityClient(args.host, args.port, args.timeout) as client:
        data = client.get_details(args.terrain, args.layer, args.xBase, args.zBase,
                                  args.width, args.height)
    if args.json:
        print(json.dumps(data, ensure_ascii=False, indent=2))
        return 0
    print(f"terrain: {data.get('terrain')}  layer={data.get('layer')}  region: "
          f"xBase={data.get('xBase')} zBase={data.get('zBase')} "
          f"{data.get('width')}x{data.get('height')}")
    d = data.get("data", [])
    print(f"count  : {data.get('count')}  range: {min(d) if d else 0}~{max(d) if d else 0}")
    return 0


def _cmd_terrain_set_details(args) -> int:
    data = _parse_ints(args.data) if args.data else None
    if data is None and not args.random:
        print("[错误] 必须提供 --data 或 --random", file=sys.stderr)
        return 1
    with UnityClient(args.host, args.port, args.timeout) as client:
        data = client.set_details(args.terrain, args.layer, args.xBase, args.zBase,
                                  args.width, args.height, data=data, random=args.random,
                                  count=args.count, seed=args.seed, density=args.density)
    if args.json:
        print(json.dumps(data, ensure_ascii=False, indent=2))
        return 0
    print(f"terrain: {data.get('terrain')}  layer={data.get('layer')}  mode={data.get('mode')}  "
          f"region: {data.get('width')}x{data.get('height')}  cells={data.get('cells')}")
    return 0


def _cmd_terrain_list_trees(args) -> int:
    with UnityClient(args.host, args.port, args.timeout) as client:
        data = client.list_trees(args.terrain)
    if args.json:
        print(json.dumps(data, ensure_ascii=False, indent=2))
        return 0
    print(f"terrain: {data.get('terrain')}")
    print(f"prototypes: {data.get('prototypeCount')}")
    for p in data.get("prototypes", []):
        print(f"  [{p.get('index')}] {p.get('name')}")
    insts = data.get("instances", [])
    print(f"instances: {data.get('instanceCount')}")
    for t in insts[:10]:
        pos = t.get("position", {})
        print(f"  [{t.get('index')}] proto={t.get('prototypeIndex')} "
              f"pos=({pos.get('x'):.3f},{pos.get('y'):.3f},{pos.get('z'):.3f}) "
              f"scale={t.get('widthScale'):.2f}")
    if len(insts) > 10:
        print(f"  ... 共 {len(insts)} 棵")
    return 0


def _cmd_terrain_add_trees(args) -> int:
    positions = _parse_floats(args.positions) if args.positions else None
    if positions is None and not args.random:
        print("[错误] 必须提供 --positions 或 --random", file=sys.stderr)
        return 1
    with UnityClient(args.host, args.port, args.timeout) as client:
        data = client.add_trees(args.terrain, args.prototypeIndex, positions=positions,
                                random=args.random, count=args.count, seed=args.seed,
                                min_scale=args.minScale, max_scale=args.maxScale)
    if args.json:
        print(json.dumps(data, ensure_ascii=False, indent=2))
        return 0
    print(f"terrain: {data.get('terrain')}  prototype={data.get('prototypeIndex')}  "
          f"mode={data.get('mode')}  added={data.get('added')}  total={data.get('total')}")
    return 0


def _cmd_terrain_clear_trees(args) -> int:
    with UnityClient(args.host, args.port, args.timeout) as client:
        data = client.clear_trees(args.terrain)
    if args.json:
        print(json.dumps(data, ensure_ascii=False, indent=2))
        return 0
    print(f"terrain: {data.get('terrain')}  removed={data.get('removed')}")
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="unity-bridge",
        description="通过 Python 命令行操控 Unity Editor（TCP/JSON 协议，Unity 原生 JsonUtility）",
    )
    parser.add_argument("--host", default=DEFAULT_HOST, help=f"Unity 地址（默认 {DEFAULT_HOST}）")
    parser.add_argument("--port", type=int, default=DEFAULT_PORT, help=f"Unity 端口（默认 {DEFAULT_PORT}）")
    parser.add_argument("--timeout", type=float, default=10.0, help="连接/响应超时秒数（默认 10）")

    sub = parser.add_subparsers(dest="command", required=True)

    p_tree = sub.add_parser("tree", help="以树状结构打印当前场景中的物体名称")
    p_tree.add_argument("--components", action="store_true", help="同时显示每个物体的组件类型")
    p_tree.add_argument("--json", action="store_true", help="输出原始 JSON 而非树形文本")
    p_tree.set_defaults(func=_cmd_tree)

    p_list = sub.add_parser("list", aliases=["ls"], help="列出 Unity 侧所有已注册的命令")
    p_list.set_defaults(func=_cmd_list)

    p_ver = sub.add_parser("version", aliases=["ver", "v"],
                           help="显示 Unity 侧桥接层版本号与命令统计（确认是否最新）")
    p_ver.add_argument("--json", action="store_true", help="输出原始 JSON")
    p_ver.set_defaults(func=_cmd_version)

    p_bounds = sub.add_parser(
        "mesh-bounds", aliases=["bounds"],
        help="计算 Assets 中网格/模型/预制体的轴对齐包围盒")
    p_bounds.add_argument("path", help="目标在 Assets 中的相对路径（.mesh / 模型文件 / .prefab）")
    p_bounds.add_argument("--json", action="store_true", help="输出原始 JSON 而非文本")
    p_bounds.set_defaults(func=_cmd_mesh_bounds)

    p_shot = sub.add_parser(
        "screenshot", aliases=["shot"],
        help="将预制体复制到场景隔离位置并截图保存为 PNG")
    p_shot.add_argument("path", help="目标预制体在 Assets 中的相对路径（.prefab / 模型文件）")
    p_shot.add_argument("output", help="PNG 输出路径（必须以 .png 结尾）")
    p_shot.add_argument("--offset", required=True,
                        help="相机相对预制体的位置，格式 'x,y,z'（如 '3,2,5'）")
    p_shot.add_argument("--orthographic", action="store_true", help="使用正交相机（默认透视）")
    p_shot.add_argument("--fov", type=float, default=None,
                        help="视野：透视=fieldOfView，正交=orthographicSize（默认 Unity 默认）")
    p_shot.add_argument("--width", type=int, default=1920, help="输出图片宽（默认 1920）")
    p_shot.add_argument("--height", type=int, default=1080, help="输出图片高（默认 1080）")
    p_shot.add_argument("--bg", default=None,
                        help="背景色 'r,g,b[,a]'（0~1，默认透明）")
    p_shot.add_argument("--light", type=float, default=0.0,
                        help="补光强度（默认 0 不补光；>0 时追加与相机同向平行光）")
    p_shot.add_argument("--json", action="store_true", help="输出原始 JSON 而非文本")
    p_shot.set_defaults(func=_cmd_screenshot)

    # ============ Terrain 程序化编辑 ============

    p_tlist = sub.add_parser("terrain-list", aliases=["tlist"],
                             help="列出场景中所有 Terrain（名称/位置/尺寸/分辨率）")
    p_tlist.add_argument("--terrain", default=None, help="目标 Terrain 名称（省略=第一个）")
    p_tlist.add_argument("--json", action="store_true", help="输出原始 JSON")
    p_tlist.set_defaults(func=_cmd_terrain_list)

    p_tget = sub.add_parser("terrain-get-heights", aliases=["tget"],
                            help="读取高度图区域")
    p_tget.add_argument("--terrain", default=None, help="目标 Terrain 名称（省略=第一个）")
    p_tget.add_argument("--xBase", type=int, default=0)
    p_tget.add_argument("--zBase", type=int, default=0)
    p_tget.add_argument("--width", type=int, default=0, help="区域宽（省略=到边界）")
    p_tget.add_argument("--height", type=int, default=0, help="区域高（省略=到边界）")
    p_tget.add_argument("--json", action="store_true", help="输出原始 JSON")
    p_tget.set_defaults(func=_cmd_terrain_get_heights)

    p_tset = sub.add_parser("terrain-set-heights", aliases=["tset"],
                            help="写入高度图（data 数组或 noise 噪声生成）")
    p_tset.add_argument("--terrain", default=None)
    p_tset.add_argument("--xBase", type=int, default=0)
    p_tset.add_argument("--zBase", type=int, default=0)
    p_tset.add_argument("--width", type=int, default=0)
    p_tset.add_argument("--height", type=int, default=0)
    p_tset.add_argument("--data", default=None, help="高度数组（逗号分隔，行优先，0~1）")
    p_tset.add_argument("--noise", action="store_true", help="用 Perlin 噪声生成")
    p_tset.add_argument("--noiseScale", type=float, default=1.0)
    p_tset.add_argument("--noiseSeed", type=int, default=0)
    p_tset.add_argument("--baseHeight", type=float, default=0.0)
    p_tset.add_argument("--heightScale", type=float, default=1.0)
    p_tset.add_argument("--json", action="store_true")
    p_tset.set_defaults(func=_cmd_terrain_set_heights)

    p_tlayers = sub.add_parser("terrain-get-layers", aliases=["tlayer"],
                               help="列出 Terrain 的纹理层（TerrainLayer）")
    p_tlayers.add_argument("--terrain", default=None)
    p_tlayers.add_argument("--json", action="store_true")
    p_tlayers.set_defaults(func=_cmd_terrain_get_layers)

    p_tamap = sub.add_parser("terrain-get-alphamaps", aliases=["tamap"],
                             help="读取纹理混合权重")
    p_tamap.add_argument("--terrain", default=None)
    p_tamap.add_argument("--xBase", type=int, default=0)
    p_tamap.add_argument("--zBase", type=int, default=0)
    p_tamap.add_argument("--width", type=int, default=0)
    p_tamap.add_argument("--height", type=int, default=0)
    p_tamap.add_argument("--json", action="store_true")
    p_tamap.set_defaults(func=_cmd_terrain_get_alphamaps)

    p_tsamap = sub.add_parser("terrain-set-alphamaps", aliases=["tsamap"],
                              help="写入纹理混合权重（每像素自动归一化）")
    p_tsamap.add_argument("--terrain", default=None)
    p_tsamap.add_argument("--xBase", type=int, default=0)
    p_tsamap.add_argument("--zBase", type=int, default=0)
    p_tsamap.add_argument("--width", type=int, default=0)
    p_tsamap.add_argument("--height", type=int, default=0)
    p_tsamap.add_argument("--data", required=True,
                          help="权重数组（逗号分隔，index=(y*width+x)*layers+layer）")
    p_tsamap.add_argument("--json", action="store_true")
    p_tsamap.set_defaults(func=_cmd_terrain_set_alphamaps)

    p_tdlist = sub.add_parser("terrain-list-details", aliases=["tdlist"],
                              help="列出 Terrain 的草原型（DetailPrototype）")
    p_tdlist.add_argument("--terrain", default=None)
    p_tdlist.add_argument("--json", action="store_true")
    p_tdlist.set_defaults(func=_cmd_terrain_list_details)

    p_tdget = sub.add_parser("terrain-get-details", aliases=["tdget"],
                             help="读取某层植被密度图")
    p_tdget.add_argument("--terrain", default=None)
    p_tdget.add_argument("--layer", type=int, required=True)
    p_tdget.add_argument("--xBase", type=int, default=0)
    p_tdget.add_argument("--zBase", type=int, default=0)
    p_tdget.add_argument("--width", type=int, default=0)
    p_tdget.add_argument("--height", type=int, default=0)
    p_tdget.add_argument("--json", action="store_true")
    p_tdget.set_defaults(func=_cmd_terrain_get_details)

    p_tdset = sub.add_parser("terrain-set-details", aliases=["tdset"],
                             help="写入植被密度（data 数组或 random 随机撒点）")
    p_tdset.add_argument("--terrain", default=None)
    p_tdset.add_argument("--layer", type=int, required=True)
    p_tdset.add_argument("--xBase", type=int, default=0)
    p_tdset.add_argument("--zBase", type=int, default=0)
    p_tdset.add_argument("--width", type=int, default=0)
    p_tdset.add_argument("--height", type=int, default=0)
    p_tdset.add_argument("--data", default=None, help="密度数组（逗号分隔，0~16）")
    p_tdset.add_argument("--random", action="store_true", help="随机撒点")
    p_tdset.add_argument("--count", type=int, default=0)
    p_tdset.add_argument("--seed", type=int, default=0)
    p_tdset.add_argument("--density", type=int, default=3)
    p_tdset.add_argument("--json", action="store_true")
    p_tdset.set_defaults(func=_cmd_terrain_set_details)

    p_ttlist = sub.add_parser("terrain-list-trees", aliases=["ttlist"],
                              help="列出 Terrain 的树原型与树实例")
    p_ttlist.add_argument("--terrain", default=None)
    p_ttlist.add_argument("--json", action="store_true")
    p_ttlist.set_defaults(func=_cmd_terrain_list_trees)

    p_ttadd = sub.add_parser("terrain-add-trees", aliases=["ttadd"],
                             help="添加树木（positions 数组或 random 随机种植）")
    p_ttadd.add_argument("--terrain", default=None)
    p_ttadd.add_argument("--prototypeIndex", type=int, required=True)
    p_ttadd.add_argument("--positions", default=None,
                         help="位置数组（逗号分隔，每 3 个一组 {x,y,z} 归一化 0~1）")
    p_ttadd.add_argument("--random", action="store_true", help="随机种植")
    p_ttadd.add_argument("--count", type=int, default=0)
    p_ttadd.add_argument("--seed", type=int, default=0)
    p_ttadd.add_argument("--minScale", type=float, default=0.8)
    p_ttadd.add_argument("--maxScale", type=float, default=1.2)
    p_ttadd.add_argument("--json", action="store_true")
    p_ttadd.set_defaults(func=_cmd_terrain_add_trees)

    p_ttclear = sub.add_parser("terrain-clear-trees", aliases=["ttclear"],
                               help="清空 Terrain 上所有树实例")
    p_ttclear.add_argument("--terrain", default=None)
    p_ttclear.add_argument("--json", action="store_true")
    p_ttclear.set_defaults(func=_cmd_terrain_clear_trees)

    return parser


def main(argv: Optional[List[str]] = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        return args.func(args)
    except UnityBridgeError as e:
        print(f"[错误] {e}", file=sys.stderr)
        return 1
    except ConnectionError as e:
        print(f"[错误] 连接失败: {e}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
