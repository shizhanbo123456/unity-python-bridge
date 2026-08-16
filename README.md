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
| 线程模型 | 后台线程收发 + **主线程队列执行** | Unity API 只能主线程访问，命令队列由 **BridgeServer 自身驱动**（`EditorApplication.update`），Edit Mode 与 Play Mode 均安全可用，**不依赖场景组件** |
| 命令注册 | **反射扫描 `[BridgeCommand]` 特性** | 新增命令只需写一个静态方法类，零改动现有代码 |
| 数据格式 | 请求 `{id, cmd, args}` / 响应 `{id, ok, data\|error}` | 支持并发请求（按 id 匹配），错误与数据分离 |
| Python 依赖 | 纯标准库 | 零安装成本，Python 3.8+ |

---

## 二、快速开始

### 1. Unity 侧（一次配置）

1. 把本仓库整个文件夹（`unity-python-bridge/`）复制或 `git clone` 到 Unity 项目的 `Assets/` 下，即 `Assets/unity-python-bridge/`。
2. 等待编译完成，用菜单 **Tools → Unity Python Bridge → Start Server** 启动服务器，看到日志提示监听 `127.0.0.1:21927` 即成功。
   - Edit Mode 和 Play Mode 均可使用（命令在主线程执行）。
   - **BridgeManager 组件（可选）**：场景里新建空物体 → Add Component → 搜索 `Bridge Manager` 挂上，Inspector 会显示「启动/停止服务器」按钮，且**组件被销毁时自动停止服务器**。不挂组件也完全可用（菜单等效，服务器自驱命令队列）。

> **重编译自动恢复**：服务器状态会持久化到 `Library/BridgeServerState.txt`——**触发脚本重编译或重新打开项目后，自动按该状态恢复**（无需手动重启）。
> 菜单 Start/Stop 与组件按钮均会同步写入该状态。也可用 `python -m unity_bridge reload` 命令行触发重编译并自动等待恢复（见「命令总览」）。

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

## 三、命令总览（25 条：7 基础 + 3 调试 + 15 Terrain）

### A. 基础命令（7 条）

| 命令 (bus name) | 类别 | 功能 | Python CLI | 关键参数 |
|---|---|---|---|---|
| `scene.tree` | 场景读取 | 树状返回当前激活场景的物体层级 | `tree` | `components`(bool, 可选，显组件类型) |
| `mesh.bounds` | 资源查询 | 计算 Assets 中 mesh / 模型 / prefab 的轴对齐包围盒（AABB，多网格合并） | `mesh-bounds`（`bounds`） | `path`(string, Assets 相对路径) |
| `prefab.screenshot` | 资源查询 | 隔离复制 prefab 到 `(9999,9999,9999)` + 相机环绕 `LookAt` 渲染存 PNG（**旋转保持资产原有**；支持正交/透视、`fov`、`bg`、补光） | `screenshot`（`shot`） | `path`、`output`(.png)、`offset`("x,y,z")、`orthographic`、`fov`、`width`、`height`、`bg`、`light` |
| `bridge.ping` | 系统 | 连通性测试，返回 `pong` + 服务器时间 | 无专用子命令（`client.ping()` / `client.call("bridge.ping")` / 原始 TCP） | 无 |
| `bridge.list_commands` | 系统 | 列出所有已注册命令 | `list`（`ls`） | 无 |
| `bridge.version` | 系统 | 返回桥接层版本号与命令统计，确认 Unity 侧代码是否为最新 | `version`（`ver`/`v`） | 无 |
| `bridge.reload` | 系统 | 触发 Unity 脚本重编译（domain reload），编译完成后服务器自动恢复 | `reload`（`rl`） | `--expect-version`、`--timeout`、`--interval` |

### A2. 调试命令（3 条）

| 命令 (bus name) | 功能 | Python CLI | 关键参数 |
|---|---|---|---|
| `debug.log` | 在 Unity Console 打印一条 Info 日志 | `debug-log`（`dlog`） | `message` |
| `debug.log_warning` | 在 Unity Console 打印一条 Warning 日志 | `debug-log-warning`（`dlogw`） | `message` |
| `debug.log_error` | 在 Unity Console 打印一条 Error 日志 | `debug-log-error`（`dloge`） | `message` |

> **版本确认**：Unity 侧菜单 **Tools → Unity Python Bridge → 打印版本信息** 会在 Console 输出版本号与命令统计；也可用 `python -m unity_bridge version` 远程查询。当前版本 **v1.2.0**（v1.0.0=独立重构 / v1.1.0=新增 terrain 命令 / v1.2.0=修复 list_commands 序列化 + 版本工具；后续 debug 命令、reload、Flush 驱动下沉均保持 v1.2.0）。

**触发重编译并等待恢复**：

```bash
# 触发 Unity 脚本重编译，每 1 秒轮询 bridge.version，直到服务器恢复或超时
# 等待超时默认读取 bridge.ini 的 [reload] timeout（默认 30 秒），可用 --timeout 覆盖
python -m unity_bridge reload

# 指定期望版本（不匹配则继续等待）
python -m unity_bridge reload --expect-version 1.2.0

# 自定义超时与轮询间隔
python -m unity_bridge reload --timeout 180 --interval 2
```

> **配置文件 `bridge.ini`**：工具根目录下的 `bridge.ini` 存放运行时默认参数。目前支持：
> ```ini
> [reload]
> timeout = 30   ; 等待 Unity 重编译恢复的超时（秒），命令行 --timeout 可临时覆盖
> ```
> 修改后无需重启，下次运行 `reload` 即生效；文件不存在或解析失败时回退到 30 秒。

> 原理：`bridge.reload` 先持久化"运行中"状态，再延迟一帧调用 `CompilationPipeline.RequestScriptCompilation()` 触发重编译；
> 重编译（domain reload）完成后由 BridgeAutoRestart 自动恢复服务器，客户端轮询版本号直到恢复。
> 注意：**Unity 失焦/后台时 `EditorApplication.update` 不运行，重编译不会自动触发**——执行 `reload` 前请让 Unity 窗口保持在前台。

### B. Terrain 程序化编辑命令（15 条，Unity 原生 TerrainData API）

> **公共参数**：`terrain`(string, 可选) —— 目标 Terrain 的 GameObject 名称，省略时取场景中**第一个** Terrain；区域参数 `xBase`/`zBase`/`width`/`height`(int, 可选) —— 操作区域，省略时默认整图。

| 命令 (bus name) | 类别 | 功能 | Python CLI | 关键参数 |
|---|---|---|---|---|
| `terrain.list` | 地形查询 | 列出场景中所有 Terrain（名称/位置/尺寸/各分辨率/层数/树数） | `terrain-list`（`tlist`） | `terrain` |
| `terrain.get_heights` | 高度图 | 读取高度图区域，data 行优先 `index=y*width+x`，值 0~1 | `terrain-get-heights`（`tget`） | `terrain`、`xBase`、`zBase`、`width`、`height` |
| `terrain.set_heights` | 高度图 | 写入高度图：`data`(float[] 行优先 0~1) **或** `noise=true` 用 Perlin 噪声生成（可复现） | `terrain-set-heights`（`tset`） | `terrain`、区域、`data` / `noise`、`noiseScale`、`noiseSeed`、`baseHeight`、`heightScale` |
| `terrain.get_layers` | 纹理 | 列出 TerrainLayer（名称 + 漫反射贴图路径） | `terrain-get-layers`（`tlayer`） | `terrain` |
| `terrain.get_diffuse_dirs` | 纹理 | 返回所有 TerrainLayer 的 Diffuse 贴图**目录（去重）**及每层完整路径（layers 数组 1:1 对应原始索引，不去重） | `terrain-get-diffuse-dirs`（`tdiff`） | `terrain` |
| `terrain.get_tree_prefab_dirs` | 树木 | 返回所有树原型（TreePrototype）的 Prefab **目录（去重）**及完整路径（trees 数组 1:1 对应原始索引） | `terrain-get-tree-prefab-dirs`（`ttpd`） | `terrain` |
| `terrain.get_detail_asset_dirs` | 植被 | 返回所有草原型（DetailPrototype）的**预制体或贴图**目录（去重）及完整路径，自动区分类型 | `terrain-get-detail-asset-dirs`（`tdad`） | `terrain` |
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
复制出的预制体**旋转保持资产原有的**（不强制 identity），缩放统一为 1。

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
| `light` | number | ❌ | 补光强度，默认 `0`（不补光）；`>0` 时在相机就位后追加一盏**rotation 与相机一致的平行光**，**推荐 `2`**（水平视角下也能保证物体清晰可见） |

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
> 请确保截图时场景具备合适照明。也可直接用 `--light <强度>` 让命令临时追加一盏**rotation 与相机
> 一致的平行光**补光（Unity 平行光光线方向即 `transform.forward`，与相机一致时从相机方向照向物体，
> 相机指向任何方向物体正面都亮），**推荐 `--light 2`**；`light=0`（默认）则不补光；该补光在相机
> 渲染完成后立即销毁，不会留在场景里。

---

## 七、如何扩展新命令（核心能力）

新增命令 = 新建一个静态方法 + 打上特性，**不需要改任何其他代码**：

```csharp
// Runtime/Commands/MyCommands.cs
using UnityEngine;

namespace UnityPythonBridge.Commands
{
    // 返回值必须是 [Serializable] 类（JsonUtility 序列化要求，不能返回匿名类型）
    [System.Serializable]
    public class LogResult
    {
        public bool logged;
        public string message;
    }

    public static class MyCommands
    {
        [BridgeCommand("debug.log", "在 Unity Console 打印一行日志。参数: message(string)")]
        public static object Log(BridgeContext ctx, BridgeArgs args)
        {
            var message = args.message ?? "";  // 参数从 BridgeArgs 强类型字段读取
            Debug.Log("[Bridge] " + message);
            return new LogResult { logged = true, message = message };
        }
    }
}
```

> ⚠️ **重要**：返回对象会被 `JsonUtility.ToJson` 序列化，因此**必须是 `[Serializable]` 类
> 或 Unity 支持的容器**（List / 数组 / Vector3 等），不能返回匿名类型。若需要新参数，在
> `BridgeContext.cs` 的 `BridgeArgs` 类中追加字段即可。

保存后重新编译，Python 侧即可使用（已有子命令直接调用，或通用方式）：

```bash
# 已有专用子命令（debug 系列）
python -m unity_bridge debug-log "hello"
python -m unity_bridge dlogw "warning" 
python -m unity_bridge dloge "error"

# 或直接用 Python API 调用任意命令
python -c "
from unity_bridge import UnityClient
with UnityClient() as c:
    c.call('debug.log', message='hello')
"
```

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
├── Editor/                         ← 纯编辑器工具（Editor 程序集，不进 Player）
│   ├── BridgeManagerInspector.cs   # BridgeManager 的 Inspector 按钮 + Tools 菜单快捷入口
│   └── BridgeAutoRestart.cs        # 服务器状态持久化 + 重编译后自动恢复（时机管理）
├── Runtime/                        ← 桥接层 + 可挂场景组件（Assembly-CSharp，全部 #if UNITY_EDITOR 包裹）
│   ├── BridgeManager.cs            # 可选场景组件：Inspector 按钮宿主、销毁时自动停服
│   ├── BridgeCommandAttribute.cs   # [BridgeCommand] 命令特性
│   ├── BridgeContext.cs            # 执行上下文 + BridgeArgs 强类型参数 + 委托定义
│   ├── BridgeDispatcher.cs         # 反射扫描 + 命令分发
│   ├── BridgeInfo.cs               # 版本号与命令统计（菜单"打印版本信息" / bridge.version）
│   ├── BridgeServer.cs             # TCP 服务器（单行 JSON，JsonUtility；自带 Flush 驱动，不依赖组件）
│   ├── BridgeStateStore.cs         # 状态文件读写（Library/BridgeServerState.txt）
│   ├── MainThreadRunner.cs         # 主线程执行队列
│   └── Commands/
│       ├── SceneTreeCommand.cs     # 命令 scene.tree
│       ├── MeshBoundsCommand.cs    # 命令 mesh.bounds（包围盒计算）
│       ├── PrefabScreenshotCommand.cs  # 命令 prefab.screenshot（隔离复制+相机截图）
│       ├── TerrainCommands.cs      # 命令 terrain.*（高度图/纹理/植被/树木，Unity 原生 TerrainData）
│       ├── SystemCommands.cs       # bridge.ping / bridge.list_commands / bridge.version / bridge.reload
│       └── DebugCommands.cs        # debug.log / debug.log_warning / debug.log_error
└── python/                         # Python 侧（无需安装依赖）
    ├── unity_bridge/
    │   ├── __init__.py
    │   ├── client.py               # TCP/JSON 客户端 UnityClient
    │   ├── cli.py                  # 命令行入口（tree / list / mesh-bounds / screenshot / terrain / reload / debug）
    │   └── __main__.py             # 支持 python -m unity_bridge
    ├── scripts/
    │   └── mock_unity_server.py    # 模拟 Unity 侧协议，无 Unity 也能联调
    └── requirements.txt
```

---

## 十、安全与注意事项

- 服务器**只绑定 127.0.0.1**，仅本机进程可访问，不会暴露到局域网。
- 命令在主线程执行，避免 Unity API 跨线程调用崩溃。
- 销毁场景中的 BridgeManager 组件/物体（若挂了）会自动停止服务器，不留后台线程。
- 若要在打包后的 Player 中使用，请自行评估：本项目针对 **Editor 开发期工具** 场景。
- **首次导入后**：Unity 会为脚本生成 `.meta` 文件（GUID）。若希望跨项目复制时保持 GUID
  稳定（推荐），请把生成的 `.meta` 一并提交到 git。
