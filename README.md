# UnityPythonBridge — 通过 Python 命令行操控 Unity Editor

在 Unity Editor 运行时，通过 Python 命令行工具对编辑器进行操控。架构上采用 **TCP + 单行 JSON** 协议，C# 侧通过**反射**自动注册命令，新增命令零样板代码。

**纯 Unity 原生实现**：C# 侧仅使用 Unity 内置 `JsonUtility`（无 Newtonsoft.Json），Python 侧仅使用标准库——**克隆/复制整个仓库文件夹到任意项目的 `Assets/` 下即可使用**，无需安装任何包。

---

## 一、架构总览

```
┌─────────────────────────────┐          ┌───────────────────────────────────┐
│         Python 侧            │          │            Unity Editor            │
│                             │          │                                   │
│  ┌───────────────────────┐  │  TCP     │  ┌─────────────────────────────┐  │
│  │ cli.py (命令行入口)    │  │ JSON行    │  │ BridgeServer (TCP 监听)      │  │
│  │   tree / list / ...   │──┼─────────▶│   · 仅监听 127.0.0.1         │  │
│  └──────────┬────────────┘  │          │   · 后台线程收/发              │  │
│             │               │          │  └──────────┬──────────────────┘  │
│  ┌──────────▼────────────┐  │          │             │ 投递 (线程安全队列)   │
│  │ client.py              │  │          │  ┌──────────▼──────────────────┐  │
│  │  · socket 收发          │  │          │  │ MainThreadRunner            │  │
│  │  · JSON 编解码          │  │          │  │  · 主线程队列                │  │
│  │  · call(cmd, **args)   │  │          │  └──────────┬──────────────────┘  │
│  └───────────────────────┘  │          │             │ 主线程 Flush          │
│                             │          │  ┌──────────▼──────────────────┐  │
│                             │          │  │ BridgeDispatcher (反射分发)  │  │
│                             │          │  │  · 扫描 [BridgeCommand] 特性  │  │
│                             │          │  └──────────┬──────────────────┘  │
│                             │          │             │                     │
│                             │          │  ┌──────────▼──────────────────┐  │
│                             │          │  │ Commands/                   │  │
│                             │          │  │  SceneTreeCommand           │  │
│                             │          │  │  SystemCommands             │  │
│                             │          │  └─────────────────────────────┘  │
└─────────────────────────────┘          └───────────────────────────────────┘
```

**核心设计决策：**

| 决策点 | 方案 | 理由 |
|---|---|---|
| 通信协议 | TCP + 单行 JSON（UTF-8） | 简单可靠、调试直观（可用 netcat 直接发命令） |
| JSON 实现 | Unity 内置 `JsonUtility` | 零第三方依赖，拖入 Assets 即用，跨项目零配置 |
| 监听地址 | `127.0.0.1` 仅本机 | 避免局域网暴露风险 |
| 线程模型 | 后台线程收发 + **主线程队列执行** | Unity API 只能主线程访问，`EditorApplication.update` 驱动队列，Edit Mode 与 Play Mode 均安全可用 |
| 命令注册 | **反射扫描 `[BridgeCommand]` 特性** | 新增命令只需写一个静态方法类，零改动现有代码 |
| 数据格式 | 请求 `{id, cmd, args}` / 响应 `{id, ok, data\|error}` | 支持并发请求（按 id 匹配），错误与数据分离 |
| Python 依赖 | 纯标准库 | 零安装成本，Python 3.8+ |

---

## 二、快速开始

### 1. Unity 侧（一次配置）

1. 把本仓库整个文件夹（`unity-python-bridge/`）复制或 `git clone` 到 Unity 项目的 `Assets/` 下，即 `Assets/unity-python-bridge/`。
2. 等待编译完成，打开菜单 **Tools → Unity Python Bridge**（或菜单 **Tools → Unity Python Bridge → Start Server**），点击「启动服务器」，看到日志提示监听 `127.0.0.1:21927` 即成功。
   - Edit Mode 和 Play Mode 均可使用（命令在主线程执行）。

### 2. Python 侧

```bash
cd python

# 打印当前场景物体层级树（第一个命令功能）
python -m unity_bridge tree

# 附带显示每个物体的组件类型
python -m unity_bridge tree --components

# 输出原始 JSON（供程序化处理）
python -m unity_bridge tree --json

# 查看 Unity 侧所有可用命令
python -m unity_bridge list

# 计算 Assets 中网格/模型/预制体的轴对齐包围盒
python -m unity_bridge mesh-bounds Assets/Models/Rock.fbx

# 预制体同样支持；bounds 为 mesh-bounds 的别名，--json 输出原始数据
python -m unity_bridge bounds Assets/Prefabs/Tree.prefab --json

# 将预制体复制到场景隔离位置并截图保存为 PNG
#   path/output 为位置参数；--offset 为相机相对预制体的位置（必填，格式 "x,y,z"）
python -m unity_bridge screenshot Assets/Prefabs/Tree.prefab out/tree.png --offset "3,2,5"

# 正交相机 + 指定视野/分辨率/背景色；shot 为 screenshot 的别名
python -m unity_bridge shot Assets/Prefabs/Rock.fbx out/rock.png --offset "0,0,-8" \
    --orthographic --fov 3 --width 1280 --height 720 --bg "0.2,0.2,0.2,1"
```

### 3. 无 Unity 环境联调（可选）

```bash
# 终端 A：启动模拟服务器（复刻 Unity 侧协议）
python scripts/mock_unity_server.py

# 终端 B：正常使用 CLI
python -m unity_bridge tree --components
```

---

## 三、命令总览（5 条原生命令 + 12 条 Terrain 命令）

### A. 原生命令（5 条）

| 命令 (bus name) | 类别 | 功能 | Python CLI | 关键参数 |
|---|---|---|---|---|
| `scene.tree` | 场景读取 | 树状返回当前激活场景的物体层级 | `tree` | `components`(bool, 可选，显组件类型) |
| `mesh.bounds` | 资源查询 | 计算 Assets 中 mesh / 模型 / prefab 的轴对齐包围盒（AABB，多网格合并） | `mesh-bounds`（`bounds`） | `path`(string, Assets 相对路径) |
| `prefab.screenshot` | 资源查询 | 隔离复制 prefab 到 `(9999,9999,9999)` + 相机环绕 `LookAt` 渲染存 PNG（支持正交/透视、`fov`、`bg`、补光） | `screenshot`（`shot`） | `path`、`output`(.png)、`offset`("x,y,z")、`orthographic`、`fov`、`width`、`height`、`bg`、`light` |
| `bridge.ping` | 系统 | 连通性测试，返回 `pong` + 服务器时间 | 无专用子命令（`client.ping()` / `client.call("bridge.ping")` / 原始 TCP） | 无 |
| `bridge.list_commands` | 系统 | 列出所有已注册命令 | `list`（`ls`） | 无 |

### B. Terrain 程序化编辑命令（12 条，Unity 原生 TerrainData API）

> **公共参数**：`terrain`(string, 可选) —— 目标 Terrain 的 GameObject 名称，省略时取场景中**第一个** Terrain；区域参数 `xBase`/`zBase`/`width`/`height`(int, 可选) —— 操作区域，省略时默认整图。

| 命令 (bus name) | 类别 | 功能 | Python CLI | 关键参数 |
|---|---|---|---|---|
| `terrain.list` | 地形查询 | 列出场景中所有 Terrain（名称/位置/尺寸/各分辨率/层数/树数） | `terrain-list`（`tlist`） | `terrain` |
| `terrain.get_heights` | 高度图 | 读取高度图区域，data 行优先 `index=y*width+x`，值 0~1 | `terrain-get-heights`（`tget`） | `terrain`、`xBase`、`zBase`、`width`、`height` |
| `terrain.set_heights` | 高度图 | 写入高度图：`data`(float[] 行优先 0~1) **或** `noise=true` 用 Perlin 噪声生成（可复现） | `terrain-set-heights`（`tset`） | `terrain`、区域、`data` / `noise`、`noiseScale`、`noiseSeed`、`baseHeight`、`heightScale` |
| `terrain.get_layers` | 纹理 | 列出 TerrainLayer（名称 + 漫反射贴图路径） | `terrain-get-layers`（`tlayer`） | `terrain` |
| `terrain.get_alphamaps` | 纹理 | 读取纹理混合权重，data `index=(y*width+x)*layers+layer` | `terrain-get-alphamaps`（`tamap`） | `terrain`、区域 |
| `terrain.set_alphamaps` | 纹理 | 写入纹理混合权重（**每像素自动归一化**到和为 1） | `terrain-set-alphamaps`（`tsamap`） | `terrain`、区域、`data`(float[]) |
| `terrain.list_details` | 植被 | 列出草原型（DetailPrototype） | `terrain-list-details`（`tdlist`） | `terrain` |
| `terrain.get_details` | 植被 | 读取某层植被密度图，data 行优先 `index=y*width+x` | `terrain-get-details`（`tdget`） | `terrain`、`layer`、区域 |
| `terrain.set_details` | 植被 | 写入植被密度：`data`(int[] 行优先 0~16) **或** `random=true` + `count`/`seed`/`density` 随机撒点 | `terrain-set-details`（`tdset`） | `terrain`、`layer`、区域、`data` / `random`、`count`、`seed`、`density` |
| `terrain.list_trees` | 树木 | 列出树原型与全部树实例（位置/缩放） | `terrain-list-trees`（`ttlist`） | `terrain` |
| `terrain.add_trees` | 树木 | 添加树木：`positions`(float[] 每 3 个一组 {x,y,z} 归一化 0~1) **或** `random=true` + `count`/`seed`/`minScale`/`maxScale` 随机种植（自动贴地） | `terrain-add-trees`（`ttadd`） | `terrain`、`prototypeIndex`、`positions` / `random`、`count`、`seed`、`minScale`、`maxScale` |
| `terrain.clear_trees` | 树木 | 清空 Terrain 上所有树实例 | `terrain-clear-trees`（`ttclear`） | `terrain` |

**典型用法**：

```bash
# 查看场景中有哪些地形
python -m unity_bridge terrain-list

# 用噪声生成 64x64 区域的山丘（可复现）
python -m unity_bridge terrain-set-heights --xBase 100 --zBase 100 \
    --width 64 --height 64 --noise --noiseScale 0.02 --noiseSeed 42 \
    --baseHeight 0.2 --heightScale 0.6

# 读取指定区域高度并查看范围
python -m unity_bridge terrain-get-heights --xBase 100 --zBase 100 --width 64 --height 64

# 列出纹理层，然后写入混合权重（2 层：草地/岩石，按 x 渐变）
python -m unity_bridge terrain-get-layers
python -m unity_bridge terrain-set-alphamaps --width 16 --height 16 \
    --data "1,0, 0.75,0.25, 0.5,0.5, 0.25,0.75, 0,1, ..."

# 随机撒 200 棵草（第 0 个草原型）
python -m unity_bridge terrain-set-details --layer 0 --random --count 200 --seed 7 --density 4

# 随机种 50 棵树（第 0 个树原型）
python -m unity_bridge terrain-add-trees --prototypeIndex 0 --random --count 50 --seed 7

# 指定位置种一棵树（归一化坐标）
python -m unity_bridge terrain-add-trees --prototypeIndex 0 --positions "0.25,0.5,0.25"

# 清空树木
python -m unity_bridge terrain-clear-trees
```

> **注意**：高度图 / 纹理 / 植被 / 树木的写入会立即应用到场景并标记 dirty（可保存）；修改后 Terrain 碰撞体会自动重建。所有命令均可用 `--json` 输出原始数据供程序化处理。

---

## 四、树状输出示例

```
Scene: DemoScene  (3 个根物体)
Main Camera  [Transform, Camera, AudioListener]
Directional Light  [Transform, Light]
Player  [Transform, CharacterController, PlayerController]
├── Body  [Transform, Animator]
│   ├── LeftArm  [Transform]
│   └── RightArm  [Transform]
└── Head  [Transform, SkinnedMeshRenderer]
    └── Hat (inactive)  [Transform]
```

---

## 五、mesh.bounds 命令（计算包围盒）

计算 Assets 中**网格 / 模型 / 预制体**的轴对齐包围盒（AABB）。C# 侧使用
`AssetDatabase` 加载资源、`mesh.bounds` / `renderer.bounds` 计算，结果返回三个轴
的坐标范围，形如 `x:-2~6, y:-0.5~2, z:1~6`。

**参数**：`path`（string）—— 目标在 Assets 中的相对路径。可带或不带 `Assets/` 前缀；
支持 `.mesh`（网格）、`.fbx`/`.obj`/`.blend` 等（模型）、`.prefab`（预制体）。

**多网格处理**：若 fbx 模型或 prefab 内含多个网格，命令会实例化到原点（根变换重置为
identity，取几何固有范围），合并其下所有 `MeshRenderer` 与 `SkinnedMeshRenderer` 的包围盒，
返回能包围所有网格的合并包围盒。

**返回结构**：

```json
{
  "path": "Assets/Models/Rock.fbx",
  "resolvedPath": "Assets/Models/Rock.fbx",
  "type": "model",
  "min":  { "x": -2, "y": -0.5, "z": 1 },
  "max":  { "x": 6,  "y": 2,   "z": 6 },
  "center": { "x": 2, "y": 0.75, "z": 3.5 },
  "size": { "x": 8, "y": 2.5, "z": 5 },
  "format": "x:-2~6, y:-0.5~2, z:1~6"
}
```

**命令行**：

```bash
python -m unity_bridge mesh-bounds Assets/Models/Rock.fbx
# 文本输出：
#   path  : Assets/Models/Rock.fbx
#   type  : model
#   bounds: x:-2~6, y:-0.5~2, z:1~6
#     min : (-2, -0.5, 1)
#     max : (6, 2, 6)
#     size: (8, 2.5, 5)

python -m unity_bridge bounds Assets/Prefabs/Tree.prefab --json   # bounds 为 mesh-bounds 的别名
```

> 提示：`format` 字段即 `x:min~max, y:..., z:...` 可读格式；`min/max/center/size`
> 为机器可解析的数值，方便后续地形拼接计算。

---

## 六、prefab.screenshot 命令（预制体截图）

将目标预制体**复制到当前场景的隔离位置 `(9999,9999,9999)`**（远离原点，避免与场景中已有
物体重叠/碰撞），创建一台相机移动到相对预制体的位置并 `LookAt` 看向它，渲染后保存为 PNG，
**最后销毁临时复制的预制体与创建的相机**，不污染场景。

**参数**：

| 参数 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `path` | string | ✅ | 预制体（或模型文件）在 Assets 中的相对路径 |
| `offset` | Vector3 | ✅ | 相机**相对预制体位置**的偏移，格式 `{x,y,z}` / `[x,y,z]` / `"x,y,z"` |
| `output` | string | ✅ | PNG 输出路径，**必须以 `.png` 结尾**（父目录会自动创建） |
| `orthographic` | bool | ❌ | 是否正交相机，默认 `false`（透视） |
| `fov` | number | ❌ | 视野：透视时=`fieldOfView`，正交时=`orthographicSize`；**默认使用 Unity 默认大小** |
| `width` | int | ❌ | 输出图片宽，默认 `1920` |
| `height` | int | ❌ | 输出图片高，默认 `1080` |
| `bg` | string | ❌ | 背景色 `"r,g,b[,a]"`（分量 0~1），默认**透明** |
| `light` | number | ❌ | 补光强度，默认 `0`（不补光）；`>0` 时在相机就位后追加一盏**与相机朝向一致的平行光**，相机完成即销毁 |

**坐标约定**：相机世界位置 = 隔离位置 `(9999,9999,9999)` + `offset`；`LookAt` 朝向隔离位置。
因此其它场景物体位于相机背后（约 9999 单位外），不会进入画面。

**返回结构**：

```json
{
  "path": "Assets/Prefabs/Tree.prefab",
  "resolvedPath": "Assets/Prefabs/Tree.prefab",
  "output": "C:\\...\\out\\tree.png",
  "cameraType": "perspective",
  "width": 1920,
  "height": 1080,
  "cameraPosition": { "x": 10002, "y": 10001, "z": 10004 },
  "lookAt": { "x": 9999, "y": 9999, "z": 9999 },
  "fillLight": 0,
  "bytes": 10570
}
```

> 注意：截图使用**当前激活场景的灯光**渲染。若场景没有平行光，预制体可能偏暗——
> 请确保截图时场景具备合适照明。也可直接用 `--light <强度>` 让命令临时追加一盏与相机同向的
> 平行光补光，`light=0`（默认）则不补光；该补光在相机渲染完成后立即销毁，不会留在场景里。

---

## 七、如何扩展新命令（核心能力）

新增命令 = 新建一个静态方法 + 打上特性，**不需要改任何其他代码**：

```csharp
// Runtime/Commands/MyCommands.cs
using UnityEngine;

namespace UnityPythonBridge.Commands
{
    public static class MyCommands
    {
        [BridgeCommand("debug.log", "在 Unity Console 打印一行日志。参数: message(string)")]
        public static object Log(BridgeContext ctx, BridgeArgs args)
        {
            var message = args.path ?? "";  // 参数从 BridgeArgs 强类型字段读取
            Debug.Log("[Bridge] " + message);
            return new { logged = true, length = message.Length };
        }
    }
}
```

> ⚠️ **重要**：返回对象会被 `JsonUtility.ToJson` 序列化，因此**必须是 `[Serializable]` 类
> 或 Unity 支持的容器**（List / 数组 / Vector3 等），不能返回匿名类型。若需要新参数，在
> `BridgeContext.cs` 的 `BridgeArgs` 类中追加字段即可。

保存后重新编译，Python 侧即可使用：

```bash
python -m unity_bridge call debug.log --message "hello"
```

> `call` 通用子命令暂未实现（当前已有 tree / list / mesh-bounds / screenshot 等子命令），
> 如需可加，或直接用 Python API：
> ```python
> from unity_bridge import UnityClient
> with UnityClient() as c:
>     c.call("debug.log", path="hello")
> ```

**命令签名约定：**

```csharp
public static object MethodName(BridgeContext ctx, BridgeArgs args)
```

- `ctx`：执行上下文（预留扩展位，如注入日志/连接信息）
- `args`：请求参数，强类型 `BridgeArgs`，从字段读取（缺省为默认值）
- 返回值：任意可被 `JsonUtility` 序列化的对象（`[Serializable]` 类 / List / 基本类型）

---

## 八、协议参考

```jsonc
// 请求（一行）
{"id": 1, "cmd": "scene.tree", "args": {"components": true}}

// 成功响应（一行）
{"id": 1, "ok": true, "data": { "...": "..." }}

// 失败响应（一行）
{"id": 1, "ok": false, "error": "未知命令: xxx（可用 bridge.list_commands 查看全部命令）"}
```

> 也可用 netcat / 任意 TCP 工具直接调试：`echo '{"id":1,"cmd":"bridge.ping","args":{}}' | nc 127.0.0.1 21927`

---

## 九、目录结构

```
unity-python-bridge/                ← 复制/克隆到 Assets/ 下即用
├── Editor/
│   └── BridgeWindow.cs             # 控制窗口（启动/停止服务器、日志）
├── Runtime/
│   ├── BridgeCommandAttribute.cs   # [BridgeCommand] 命令特性
│   ├── BridgeContext.cs            # 执行上下文 + BridgeArgs 强类型参数 + 委托定义
│   ├── BridgeDispatcher.cs         # 反射扫描 + 命令分发
│   ├── BridgeServer.cs             # TCP 服务器（单行 JSON 协议，JsonUtility）
│   ├── MainThreadRunner.cs         # 主线程执行队列
│   └── Commands/
│       ├── SceneTreeCommand.cs     # 命令 scene.tree
│       ├── MeshBoundsCommand.cs    # 命令 mesh.bounds（包围盒计算）
│       ├── PrefabScreenshotCommand.cs  # 命令 prefab.screenshot（隔离复制+相机截图）
│       ├── TerrainCommands.cs      # 命令 terrain.*（高度图/纹理/植被/树木，Unity 原生 TerrainData）
│       └── SystemCommands.cs       # bridge.ping / bridge.list_commands
└── python/                         # Python 侧（无需安装依赖）
    ├── unity_bridge/
    │   ├── __init__.py
    │   ├── client.py               # TCP/JSON 客户端 UnityClient
    │   ├── cli.py                  # 命令行入口（tree / list / mesh-bounds / screenshot）
    │   └── __main__.py             # 支持 python -m unity_bridge
    ├── scripts/
    │   └── mock_unity_server.py    # 模拟 Unity 侧协议，无 Unity 也能联调
    └── requirements.txt
```

---

## 十、安全与注意事项

- 服务器**只绑定 127.0.0.1**，仅本机进程可访问，不会暴露到局域网。
- 命令在主线程执行，避免 Unity API 跨线程调用崩溃。
- 关闭 Bridge 窗口会自动停止服务器，不留后台线程。
- 若要在打包后的 Player 中使用，请自行评估：本项目针对 **Editor 开发期工具** 场景。
- **首次导入后**：Unity 会为脚本生成 `.meta` 文件（GUID）。若希望跨项目复制时保持 GUID
  稳定（推荐），请把生成的 `.meta` 一并提交到 git。
