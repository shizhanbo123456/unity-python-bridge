"""Unity Bridge 命令行入口。

用法:
    python -m unity_bridge tree                 # 打印当前场景物体层级树
    python -m unity_bridge tree --components     # 同时显示组件类型
    python -m unity_bridge tree --json           # 输出原始 JSON
    python -m unity_bridge list                  # 列出 Unity 侧所有可用命令
    python -m unity_bridge mesh-bounds Assets/.../Rock.fbx   # 计算网格/模型/预制体包围盒
    python -m unity_bridge screenshot Assets/Prefabs/Tree.prefab out/tree.png \
        --offset "3,2,5" [--orthographic] [--fov 50] [--width 1920] [--height 1080] \
        [--bg "0.2,0.2,0.2,1"] [--light 2]
    python -m unity_bridge reload               # 触发 Unity 重编译并等待服务器恢复
"""

from __future__ import annotations

import argparse
import json
import sys
import time
from typing import List, Optional

from .client import UnityBridgeError, UnityClient
from .config import (DEFAULT_HOST, DEFAULT_PORT, load_reload_timeout,
                     load_server_port)


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


def _cmd_reload(args) -> int:
    """触发 Unity 重编译，然后每 1 秒轮询 bridge.version，直到服务器恢复或超时。"""
    with UnityClient(args.host, args.port, args.timeout) as client:
        result = client.reload_unity()
    print(f"已触发: {result.get('message', 'reload requested')}")

    deadline = time.time() + args.timeout
    attempt = 0
    while time.time() < deadline:
        attempt += 1
        time.sleep(args.interval)
        try:
            with UnityClient(args.host, args.port, timeout=min(5, args.interval + 2)) as client:
                v = client.call("bridge.version")
        except (UnityBridgeError, ConnectionError, OSError) as e:
            print(f"[{attempt}] 等待中（服务器不可用）: {e}")
            continue

        ver = v.get("version", "?")
        if args.expect_version and ver != args.expect_version:
            print(f"[{attempt}] 版本 v{ver} != 期望 v{args.expect_version}，继续等待...")
            continue

        elapsed = time.time() - (deadline - args.timeout)
        print(f"服务器已恢复: v{ver}，命令 {v.get('commandCount')} 条，耗时 {elapsed:.1f}s")
        return 0

    print(f"超时: {args.timeout:.0f}s 内服务器未恢复")
    return 1


def _cmd_debug_log(args) -> int:
    with UnityClient(args.host, args.port, args.timeout) as client:
        data = client.debug_log(args.message)
    if args.json:
        print(json.dumps(data, ensure_ascii=False, indent=2))
        return 0
    print(f"logged: {data.get('logged')}  level={data.get('level')}  message={data.get('message')}")
    return 0


def _cmd_debug_log_warning(args) -> int:
    with UnityClient(args.host, args.port, args.timeout) as client:
        data = client.debug_log_warning(args.message)
    if args.json:
        print(json.dumps(data, ensure_ascii=False, indent=2))
        return 0
    print(f"logged: {data.get('logged')}  level={data.get('level')}  message={data.get('message')}")
    return 0


def _cmd_debug_log_error(args) -> int:
    with UnityClient(args.host, args.port, args.timeout) as client:
        data = client.debug_log_error(args.message)
    if args.json:
        print(json.dumps(data, ensure_ascii=False, indent=2))
        return 0
    print(f"logged: {data.get('logged')}  level={data.get('level')}  message={data.get('message')}")
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


def _cmd_view_screenshot(args) -> int:
    with UnityClient(args.host, args.port, args.timeout) as client:
        data = client.view_screenshot(output=args.output, camera=args.camera,
                                      width=args.width, height=args.height)
    if args.json:
        print(json.dumps(data, ensure_ascii=False, indent=2))
        return 0
    print(f"camera : {data.get('camera')}" + (f"  (requested: {data.get('requestedCamera')})" if data.get("requestedCamera") else ""))
    print(f"output : {data.get('output')}")
    print(f"size   : {data.get('width')}x{data.get('height')}")
    print(f"bytes  : {data.get('bytes')}")
    return 0


def _cmd_gameobject_get(args) -> int:
    with UnityClient(args.host, args.port, args.timeout) as client:
        data = client.gameobject_get(args.target, quaternion=args.quaternion)
    if args.json:
        print(json.dumps(data, ensure_ascii=False, indent=2))
        return 0
    _print_go_state(data)
    return 0


def _cmd_gameobject_set(args) -> int:
    def _vec3(s: str):
        parts = [float(x) for x in s.replace(",", " ").split()]
        if len(parts) != 3:
            raise ValueError("需要 3 个分量，格式 'x,y,z'")
        return parts

    position = _vec3(args.position) if args.position else None
    scale = _vec3(args.scale) if args.scale else None
    rotation = None
    if args.rotation:
        parts = [float(x) for x in args.rotation.replace(",", " ").split()]
        if args.quaternion and len(parts) != 4:
            print("[错误] --quaternion 时 --rotation 需要 4 个分量 {x,y,z,w}", file=sys.stderr)
            return 1
        if not args.quaternion and len(parts) != 3:
            print("[错误] --rotation 需要 3 个分量 {x,y,z}（欧拉角）", file=sys.stderr)
            return 1
        rotation = parts

    with UnityClient(args.host, args.port, args.timeout) as client:
        data = client.gameobject_set(
            args.target, active=args.active, position=position,
            rotation=rotation, scale=scale, quaternion=args.quaternion)
    if args.json:
        print(json.dumps(data, ensure_ascii=False, indent=2))
        return 0
    _print_go_state(data)
    return 0


def _print_go_state(data: dict) -> None:
    p = data.get("position", {})
    r = data.get("rotationEuler", {})
    s = data.get("scale", {})
    print(f"target : {data.get('target')}  ->  path: {data.get('resolvedPath')}")
    print(f"active : {data.get('active')}  (activeInHierarchy: {data.get('activeInHierarchy')})")
    print(f"pos    : ({p.get('x')}, {p.get('y')}, {p.get('z')})")
    if data.get("quaternion"):
        q = data.get("rotationQuat", {})
        print(f"rot    : quat({q.get('x')}, {q.get('y')}, {q.get('z')}, {q.get('w')})  euler({r.get('x')}, {r.get('y')}, {r.get('z')})")
    else:
        print(f"rot    : euler({r.get('x')}, {r.get('y')}, {r.get('z')})")
    print(f"scale  : ({s.get('x')}, {s.get('y')}, {s.get('z')})")


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


def _cmd_terrain_get_diffuse_dirs(args) -> int:
    with UnityClient(args.host, args.port, args.timeout) as client:
        data = client.get_diffuse_dirs(args.terrain)
    if args.json:
        print(json.dumps(data, ensure_ascii=False, indent=2))
        return 0
    print(f"terrain: {data.get('terrain')}  layers={data.get('count')}  "
          f"directories={data.get('directoryCount')}")
    print("去重贴图目录:")
    for d in data.get("directories", []):
        print(f"  {d}")
    print("各层 Diffuse 贴图:")
    for l in data.get("layers", []):
        tex = l.get("diffuseTexture") or "(无贴图)"
        print(f"  [{l.get('index')}] {l.get('name')}  {tex}")
        if l.get("diffuseDir"):
            print(f"      dir: {l.get('diffuseDir')}")
    return 0


def _cmd_terrain_get_tree_prefab_dirs(args) -> int:
    with UnityClient(args.host, args.port, args.timeout) as client:
        data = client.get_tree_prefab_dirs(args.terrain)
    if args.json:
        print(json.dumps(data, ensure_ascii=False, indent=2))
        return 0
    print(f"terrain: {data.get('terrain')}  trees={data.get('count')}  "
          f"directories={data.get('directoryCount')}")
    print("去重预制体目录:")
    for d in data.get("directories", []):
        print(f"  {d}")
    print("各树原型 Prefab:")
    for t in data.get("trees", []):
        prefab = t.get("prefab") or "(无预制体)"
        print(f"  [{t.get('index')}] {t.get('name')}  {prefab}")
        if t.get("prefabDir"):
            print(f"      dir: {t.get('prefabDir')}")
    return 0


def _cmd_terrain_get_detail_asset_dirs(args) -> int:
    with UnityClient(args.host, args.port, args.timeout) as client:
        data = client.get_detail_asset_dirs(args.terrain)
    if args.json:
        print(json.dumps(data, ensure_ascii=False, indent=2))
        return 0
    print(f"terrain: {data.get('terrain')}  details={data.get('count')}  "
          f"directories={data.get('directoryCount')}")
    print("去重资源目录:")
    for d in data.get("directories", []):
        print(f"  {d}")
    print("各草原型资源:")
    for d in data.get("details", []):
        kind = {"prefab": "预制体", "texture": "贴图", "none": "无"}.get(d.get("type"), d.get("type"))
        asset = d.get("asset") or "(无)"
        print(f"  [{d.get('index')}] {d.get('name')}  [{kind}]  {asset}")
        if d.get("assetDir"):
            print(f"      dir: {d.get('assetDir')}")
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


def _cmd_terrain_stash(args) -> int:
    with UnityClient(args.host, args.port, args.timeout) as client:
        data = client.stash(args.terrain, type_=args.type, name=args.name)
    if args.json:
        print(json.dumps(data, ensure_ascii=False, indent=2))
        return 0
    print(f"terrain: {data.get('terrain')}  type={data.get('type')}  name={data.get('name')}")
    print(f"path   : {data.get('path')}")
    print(f"trees  : {data.get('treeInstances')}  detailLayers={data.get('detailLayers')}")
    return 0


def _cmd_terrain_apply_stash(args) -> int:
    with UnityClient(args.host, args.port, args.timeout) as client:
        data = client.apply_stash(args.terrain, type_=args.type, name=args.name)
    if args.json:
        print(json.dumps(data, ensure_ascii=False, indent=2))
        return 0
    print(f"terrain: {data.get('terrain')}  type={data.get('type')}  name={data.get('name')}")
    print(f"path   : {data.get('path')}")
    print(f"trees  : {data.get('treeInstances')}  detailLayers={data.get('detailLayers')}")
    return 0


def _cmd_terrain_stash_delete(args) -> int:
    with UnityClient(args.host, args.port, args.timeout) as client:
        data = client.stash_delete(args.type, args.name)
    if args.json:
        print(json.dumps(data, ensure_ascii=False, indent=2))
        return 0
    print(f"type   : {data.get('type')}  name={data.get('name')}  deleted={data.get('deleted')}")
    print(f"path   : {data.get('path')}")
    return 0


def _cmd_terrain_stash_list(args) -> int:
    with UnityClient(args.host, args.port, args.timeout) as client:
        data = client.stash_list(args.type)
    if args.json:
        print(json.dumps(data, ensure_ascii=False, indent=2))
        return 0
    print(f"stashDir: {data.get('stashDir')}  count={data.get('count')}")
    for e in data.get("entries", []):
        print(f"  [{e.get('type'):<7}] {e.get('name'):<24} {e.get('bytes')}B  {e.get('path')}")
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="unity-bridge",
        description="通过 Python 命令行操控 Unity Editor（TCP/JSON 协议，Unity 原生 JsonUtility）",
    )
    parser.add_argument("--host", default=DEFAULT_HOST, help=f"Unity 地址（默认 {DEFAULT_HOST}）")
    parser.add_argument("--port", type=int, default=load_server_port(),
                        help=f"Unity 端口（默认读取 bridge.ini 的 [server] port，默认 {DEFAULT_PORT}）")
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

    p_reload = sub.add_parser("reload", aliases=["rl"],
                              help="触发 Unity 重编译（domain reload），并轮询等待服务器自动恢复")
    p_reload.add_argument("--expect-version", default=None,
                          help="期望恢复后的版本号（可选，不匹配则继续等待）")
    p_reload.add_argument("--timeout", type=float, default=load_reload_timeout(),
                          help="总超时秒数（默认读取 bridge.ini 的 [reload] timeout，默认 30）")
    p_reload.add_argument("--interval", type=float, default=1.0,
                          help="轮询间隔秒数（默认 1）")
    p_reload.set_defaults(func=_cmd_reload)

    p_dbg = sub.add_parser("debug-log", aliases=["dlog"],
                           help="在 Unity Console 打印一条 Info 日志")
    p_dbg.add_argument("message", help="日志内容")
    p_dbg.add_argument("--json", action="store_true", help="输出原始 JSON")
    p_dbg.set_defaults(func=_cmd_debug_log)

    p_dbgw = sub.add_parser("debug-log-warning", aliases=["dlogw"],
                            help="在 Unity Console 打印一条 Warning 日志")
    p_dbgw.add_argument("message", help="日志内容")
    p_dbgw.add_argument("--json", action="store_true", help="输出原始 JSON")
    p_dbgw.set_defaults(func=_cmd_debug_log_warning)

    p_dbge = sub.add_parser("debug-log-error", aliases=["dloge"],
                            help="在 Unity Console 打印一条 Error 日志")
    p_dbge.add_argument("message", help="日志内容")
    p_dbge.add_argument("--json", action="store_true", help="输出原始 JSON")
    p_dbge.set_defaults(func=_cmd_debug_log_error)

    p_bounds = sub.add_parser(
        "mesh-bounds", aliases=["bounds"],
        help="计算 Assets 中网格/模型/预制体的轴对齐包围盒")
    p_bounds.add_argument("path", help="目标在 Assets 中的相对路径（.mesh / 模型文件 / .prefab）")
    p_bounds.add_argument("--json", action="store_true", help="输出原始 JSON 而非文本")
    p_bounds.set_defaults(func=_cmd_mesh_bounds)

    p_shot = sub.add_parser(
        "screenshot", aliases=["shot"],
        help="将预制体复制到场景隔离位置并截图保存为 PNG（旋转保持资产原有，摄制后销毁临时对象）")
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
                        help="补光强度（默认 0 不补光；>0 时追加与相机同向平行光，推荐 2）")
    p_shot.add_argument("--json", action="store_true", help="输出原始 JSON 而非文本")
    p_shot.set_defaults(func=_cmd_screenshot)

    p_vshot = sub.add_parser(
        "view-screenshot", aliases=["vshot"],
        help="渲染指定相机的实时画面保存为 PNG（默认 MainCamera；区别于 screenshot 的隔离渲染）")
    p_vshot.add_argument("output", help="PNG 输出路径（必须以 .png 结尾）")
    p_vshot.add_argument("--camera", default=None,
                        help="相机 GameObject 名称（省略时依次找 MainCamera / Main Camera / 第一个激活相机）")
    p_vshot.add_argument("--width", type=int, default=0, help="输出图片宽（默认相机当前分辨率）")
    p_vshot.add_argument("--height", type=int, default=0, help="输出图片高（默认相机当前分辨率）")
    p_vshot.add_argument("--json", action="store_true", help="输出原始 JSON")
    p_vshot.set_defaults(func=_cmd_view_screenshot)

    p_gget = sub.add_parser(
        "gameobject-get", aliases=["gget"],
        help="读取 GameObject 的 active 状态与 Transform 的 position/rotation/scale")
    p_gget.add_argument("target", help="层级路径（如 Player/Body）优先，名称兼容（重名报错）")
    p_gget.add_argument("--quaternion", action="store_true", help="同时输出四元数（默认只输出欧拉角）")
    p_gget.add_argument("--json", action="store_true", help="输出原始 JSON")
    p_gget.set_defaults(func=_cmd_gameobject_get)

    p_gset = sub.add_parser(
        "gameobject-set", aliases=["gset"],
        help="写入 GameObject 的 active 状态与 Transform 的 position/rotation/scale（支持 Undo）")
    p_gset.add_argument("target", help="层级路径（如 Player/Body）优先，名称兼容（重名报错）")
    p_gset.add_argument("--active", type=int, default=-1,
                        help="0=隐藏 1=激活（默认 -1 不改）")
    p_gset.add_argument("--position", default=None, help="世界坐标 'x,y,z'")
    p_gset.add_argument("--rotation", default=None,
                        help="欧拉角 'x,y,z'；--quaternion 时四元数 'x,y,z,w'")
    p_gset.add_argument("--scale", default=None, help="localScale 'x,y,z'")
    p_gset.add_argument("--quaternion", action="store_true", help="rotation 按四元数输入/输出")
    p_gset.add_argument("--json", action="store_true", help="输出原始 JSON")
    p_gset.set_defaults(func=_cmd_gameobject_set)

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

    p_tdiff = sub.add_parser("terrain-get-diffuse-dirs", aliases=["tdiff"],
                             help="返回 Terrain 所有 TerrainLayer 的 Diffuse 贴图目录（去重）及完整路径")
    p_tdiff.add_argument("--terrain", default=None)
    p_tdiff.add_argument("--json", action="store_true")
    p_tdiff.set_defaults(func=_cmd_terrain_get_diffuse_dirs)

    p_ttpd = sub.add_parser("terrain-get-tree-prefab-dirs", aliases=["ttpd"],
                            help="返回 Terrain 所有树原型的 Prefab 目录（去重）及完整路径")
    p_ttpd.add_argument("--terrain", default=None)
    p_ttpd.add_argument("--json", action="store_true")
    p_ttpd.set_defaults(func=_cmd_terrain_get_tree_prefab_dirs)

    p_tdad = sub.add_parser("terrain-get-detail-asset-dirs", aliases=["tdad"],
                            help="返回 Terrain 所有草原型的预制体或贴图目录（去重）及完整路径")
    p_tdad.add_argument("--terrain", default=None)
    p_tdad.add_argument("--json", action="store_true")
    p_tdad.set_defaults(func=_cmd_terrain_get_detail_asset_dirs)

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

    # ============ Terrain stash（stash → clear → apply 截图调整链路）============

    p_tstash = sub.add_parser("terrain-stash", aliases=["tstash"],
                              help="把当前地形的树木/植被全量序列化为 JSON 存到工具 stash 子目录（同名报错）")
    p_tstash.add_argument("--terrain", default=None)
    p_tstash.add_argument("--type", default="all", choices=["trees", "details", "all"],
                          help="存储类型（默认 all）")
    p_tstash.add_argument("--name", required=True, help="stash 名称（不含扩展名，如 forest_v1）")
    p_tstash.add_argument("--json", action="store_true")
    p_tstash.set_defaults(func=_cmd_terrain_stash)

    p_tapply = sub.add_parser("terrain-apply-stash", aliases=["tapply"],
                              help="读取 stash JSON 并整体写回地形（替换当前 trees/detail）")
    p_tapply.add_argument("--terrain", default=None)
    p_tapply.add_argument("--type", default="all", choices=["trees", "details", "all"],
                          help="应用类型（默认 all）")
    p_tapply.add_argument("--name", required=True, help="stash 名称（不含扩展名）")
    p_tapply.add_argument("--json", action="store_true")
    p_tapply.set_defaults(func=_cmd_terrain_apply_stash)

    p_tstashdel = sub.add_parser("terrain-stash-delete", aliases=["tstashdel"],
                                 help="删除指定 stash 文件")
    p_tstashdel.add_argument("--type", required=True, choices=["trees", "details"],
                             help="stash 类型（trees 或 details）")
    p_tstashdel.add_argument("--name", required=True, help="stash 名称（不含扩展名）")
    p_tstashdel.add_argument("--json", action="store_true")
    p_tstashdel.set_defaults(func=_cmd_terrain_stash_delete)

    p_tstashlist = sub.add_parser("terrain-stash-list", aliases=["tstashlist"],
                                  help="列出工具 stash 子目录下所有 stash 文件")
    p_tstashlist.add_argument("--type", default="all", choices=["trees", "details", "all"],
                              help="筛选类型（默认 all）")
    p_tstashlist.add_argument("--json", action="store_true")
    p_tstashlist.set_defaults(func=_cmd_terrain_stash_list)

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
