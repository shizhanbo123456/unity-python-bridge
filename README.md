# UnityPythonBridge — 通过 Python 命令行操控 Unity Editor

在 Unity Editor 运行时，通过 Python 命令行工具对编辑器进行操控。架构上采用 **TCP + 单行 JSON** 协议，C# 侧通过**反射**自动注册命令，新增命令零样板代码。

**纯 Unity 原生实现**：C# 侧仅使用 Unity 内置 `JsonUtility`（无 Newtonsoft.Json），Python 侧仅使用标准库——**克隆/复制整个仓库文件夹到任意项目的 `Assets/` 下即可使用**，无需安装任何包。

> 📖 **完整命令列表见 [`COMMANDS.md`](COMMANDS.md)**（36 条，按功能分类：系统 / 调试 / 相机截图 / 场景与 Prefab 层级 / 网格 / 物体 / 地形；含全部参数与示例）。本 README 只讲架构、连接、配置与用法。

## 📚 文档导航（按需阅读）

| 文档 | 适合谁 / 什么时候读 | 内容 |
|---|---|---|
| [`GETTING_STARTED.md`](GETTING_STARTED.md) | **第一次接触**（读一遍即可） | 环境前置、一分钟速览、安装三步验证、排障速查、术语表、命令影响边界、升级、安全边界 |
| [`README.md`](README.md)（本文） | 想深入理解 | 架构总览、快速开始、连接与配置（端口/超时/bridge.ini/服务器生命周期/reload）、扩展命令、协议参考 |
| [`COMMANDS.md`](COMMANDS.md) | **用命令时** | 36 条命令的完整参考：服务端命令名、CLI 别名、全部参数、常用工作流 |
| [`FAQ.md`](FAQ.md) | **遇到问题 / 已知坑** | 连接、编译 reload、命令使用、命令相关 bug、环境维护的常见问题 |

> 建议路径：新手先读 GETTING_STARTED 一次 → 用命令查 COMMANDS → 出问题查 FAQ → 需要原理再回来看本文。

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
│                             │          │  │ Commands/ (各命令实现)       │  │
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
2. 等待编译完成，用菜单 **Tools → Unity Python Bridge → Start Server** 启动服务器，看到日志提示监听 `127.0.0.1:21927`（或 bridge.ini 中 `[server] port` 配置的值）即成功。
   - Edit Mode 和 Play Mode 均可使用（命令在主线程执行）。
   - **BridgeManager 组件（可选）**：场景里新建空物体 → Add Component → 搜索 `Bridge Manager` 挂上，Inspector 会显示「启动/停止服务器」按钮，且**组件被销毁时自动停止服务器**。不挂组件也完全可用（菜单等效，服务器自驱命令队列）。
   - 重复点击「启动」/「停止」不会静默：已运行时再启动、未运行时再停止，会打印 Warning 提示。

> **重编译自动恢复**：服务器状态持久化到 `Library/BridgeServerState.txt`——**触发脚本重编译或重新打开项目后，自动按该状态恢复**（无需手动重启）。菜单 Start/Stop 与组件按钮均会同步写入该状态。

### 2. Python 侧

```bash
cd python

# 查看可用命令（完整参考见 COMMANDS.md）
python -m unity_bridge list

# 打印当前场景物体层级树
python -m unity_bridge tree
python -m unity_bridge tree --components      # 附带组件类型
python -m unity_bridge tree --json            # 原始 JSON
python -m unity_bridge tree --depth 3         # 遍历深度（根算第 1 层；默认 1 只显示起点本身）
python -m unity_bridge tree --path "MainCamera/Object1"  # 只从指定物体开始扫描（层级路径或唯一名称）
# 注：prefab 实例根不展开内部，直接标注资产路径（tree 输出中形如 "(prefab: Assets/...)"）；
# --path 起点为 prefab 实例内部时报错，返回 prefab 根场景路径与资产路径

# 计算包围盒 / 隔离渲染预制体截图 / 查看 prefab 内部层级（完整参数见 COMMANDS.md）
python -m unity_bridge bounds Assets/Prefabs/Tree.prefab --json
python -m unity_bridge shot Assets/Prefabs/Tree.prefab out/tree.png --offset "3,2,5"
python -m unity_bridge prefab-tree Assets/Prefabs/Tree.prefab --depth 3   # path 必填

# 物体操作：绝对设置 + 相对操作（move/rotate/zoom 基于当前值，支持 Undo）
python -m unity_bridge gameobject-set "Player/Body" --position "10,0,5" --scale "2,2,2"
python -m unity_bridge gameobject-set "Player/Body" --move "0,10,0"        # 位置 += (0,10,0)
python -m unity_bridge gameobject-set "Player/Body" --rotate "0,90,0"      # 欧拉角各分量相加
python -m unity_bridge gameobject-set "Player/Body" --zoom "2,1,1"         # x 轴放大 2 倍
```

### 3. 无 Unity 环境联调（可选）

```bash
# 终端 A：启动模拟服务器（复刻 Unity 侧协议）
python scripts/mock_unity_server.py

# 终端 B：正常使用 CLI（所有命令可用，截图会落占位 PNG）
python -m unity_bridge tree --components
```

---

## 三、连接与配置

### 3.1 连接参数（Python 侧）

```bash
python -m unity_bridge --host 127.0.0.1 --port 21927 --timeout 10 tree
```

| 参数 | 默认值 | 说明 |
|---|---|---|
| `--host` | `127.0.0.1` | Unity 地址（服务器只绑本机） |
| `--port` | 读 `bridge.ini` 的 `[server] port`（默认 21927） | **显式传入时覆盖 ini** |
| `--timeout` | 连接/响应超时 10 秒；`reload` 子命令另有总超时（读 ini，默认 30s，可 `--timeout` 覆盖） | 网络超时 |

> ⚠️ **全局参数必须放在子命令前面**（如 `python -m unity_bridge --port 21950 version`），放后面会报 `unrecognized arguments`。

底层 `UnityClient()` 无参构造也读取 ini 端口，因此直接当库用同样生效：

```python
from unity_bridge import UnityClient
with UnityClient() as c:            # 自动使用 bridge.ini 端口
    print(c.call("bridge.version"))
```

### 3.2 配置文件 `bridge.ini`（Python 与 C# 两侧都读取）

位于工具根目录，**修改后无需重启，下次运行即生效**：

```ini
[server]
port = 21927        ; TCP 端口：C# 服务器据此监听，Python CLI 据此连接；--port 可临时覆盖
[reload]
timeout = 30        ; 等待 Unity 重编译恢复的超时（秒）；--timeout 可临时覆盖
[scene]
important_suffix = Manager, Tool  ; scene.important_scripts 的重要脚本匹配规则（类名后缀，逗号分隔）
```

- **Python 侧**：CLI 的 `--port` / `reload --timeout` 默认值分别来自 `[server] port` / `[reload] timeout`；底层 `UnityClient` 同样读取 ini。
- **C# 侧**：`BridgeServer.Start()` 未按参数传端口时，从 `<项目>/Assets/unity-python-bridge/bridge.ini` 读取 `[server] port` 作为监听端口；`scene.important_scripts` 命令读取 `[scene] important_suffix` 作为重要脚本的类名后缀匹配规则（默认 `Manager, Tool`，忽略大小写，可自由增删如 `Controller, System`）。
- 支持行内注释（`;` 或 `#` 之后的内容被忽略）；文件缺失、解析失败或值无效时，端口回退 `21927`、超时回退 `30` 秒、重要脚本后缀回退默认 `Manager, Tool`。
- 若重命名工具文件夹（非 `unity-python-bridge`），C# 读不到 ini 端口时会自动回退默认端口（行为不退化）。

### 3.3 服务器生命周期

- **启动**：菜单 **Tools → Unity Python Bridge → Start Server**，或场景 BridgeManager 组件按钮。
- **停止**：菜单 **Tools → Unity Python Bridge → Stop Server**，或组件按钮；销毁挂有 BridgeManager 的物体也会自动停服。
- **重复操作提示**：已在运行时再「启动」→ Warning「服务器已在运行中…」；未运行时再「停止」→ Warning「服务器未在运行…」。
- **自动恢复**：状态写入 `Library/BridgeServerState.txt`，脚本重编译 / 重开项目后自动按状态恢复。

### 3.4 触发重编译（`bridge.reload`）

```bash
# 触发 Unity 脚本重编译，轮询 bridge.version 直到服务器恢复或超时
python -m unity_bridge reload

# 指定期望版本（不匹配则继续等待）、自定义超时与轮询间隔
python -m unity_bridge reload --expect-version 1.13.0 --timeout 180 --interval 2
```

- **原理**：`bridge.reload` 先持久化"运行中"状态，再延迟一帧调用 `CompilationPipeline.RequestScriptCompilation()` 触发重编译；重编译（domain reload）完成后由 BridgeAutoRestart 自动恢复服务器，客户端轮询版本号直到恢复。
- ⚠️ **Unity 失焦/后台时 `EditorApplication.update` 不运行，重编译不会自动触发**——执行 `reload` 前请让 Unity 窗口保持在前台。
- ⚠️ **Unity 编辑器忽略一切软件注入的鼠标输入**（模拟点击无法替代前台激活）；`reload` 是进程内 API，不受此限制，是自动化编译的正路。

---

## 四、命令列表

**完整命令列表（36 条，按功能分类：系统 4 / 调试 5 / 相机截图 2 / 场景与 Prefab 层级 3 / 网格 1 / 物体 2 / 地形 19）见 [`COMMANDS.md`](COMMANDS.md)**，含：服务端命令名、Python CLI 与别名、全部参数、常用工作流示例。

快速导航：

| 类别 | 说明 | 代表命令 |
|---|---|---|
| 系统与连通 | 连通测试 / 列命令 / 版本 / 触发重编译 | `bridge.ping` `bridge.reload` |
| 调试与日志 | 打日志、读回日志、打印版本 | `debug.get_logs` `debug.log_version` |
| 相机与截图 | 隔离渲染资产、抓相机实时画面 | `prefab.screenshot` `view.camera` |
| 场景与 Prefab 层级 | 场景物体树（prefab 备注资产路径）/ 重要脚本 / prefab 资产内部层级 | `scene.tree` `scene.important_scripts` `prefab.tree` |
| 网格与资源 | 包围盒 | `mesh.bounds` |
| 物体操作 | 读写 active/transform；相对操作 move/rotate/zoom | `gameobject.get/set` |
| 地形编辑 | 高度/纹理/植被/树木/快照/资源目录 | `terrain.*`（19 条） |

---

## 五、如何扩展新命令（核心能力）

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
> `BridgeContext.cs` 的 `BridgeArgs` 类中追加字段即可（字段名即 JSON 键名）。注意：**新增字段前先全文件搜索同名**，避免与既有字段重名导致 CS0102 编译错误（如 `count` 已被 terrain 与 debug.get_logs 共用）。

保存后触发重编译（Unity 前台），Python 侧即可使用：

```bash
python -m unity_bridge reload --expect-version <新版本号>

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

## 六、协议参考

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

## 七、目录结构

```
unity-python-bridge/                ← 复制/克隆到 Assets/ 下即用
├── README.md                       # 本文档：架构/连接/配置/扩展/协议 + 文档导航
├── GETTING_STARTED.md              # 新手入门（读一遍即可）：环境/安装验证/术语/影响边界/安全
├── COMMANDS.md                     # 完整命令列表（36 条，含参数与示例）
├── FAQ.md                          # 常见问题与已知坑（连接/编译/命令/bug/环境）
├── bridge.ini                      # 运行时配置：端口 [server] port / 重编译超时 [reload] timeout
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
│       ├── SceneTreeCommand.cs     # 命令 scene.tree（场景层级树，depth/path/prefab 折叠）+ scene.important_scripts（重要脚本检索，规则读 ini）+ prefab.tree（prefab 资产内部层级树，path 必填）
│       ├── MeshBoundsCommand.cs    # 命令 mesh.bounds（包围盒计算）
│       ├── PrefabScreenshotCommand.cs  # 命令 prefab.screenshot（隔离复制+相机截图，支持 camPos/lookAt）
│       ├── TerrainCommands.cs      # 命令 terrain.*（高度图/纹理/植被/树木，Unity 原生 TerrainData）
│       ├── TerrainStashCommands.cs # 命令 terrain.stash / apply_stash / stash_delete / stash_list（快照 JSON）
│       ├── ViewScreenshotCommand.cs # 命令 view.camera（抓取指定相机实时画面；预留 view.window）
│       ├── GameObjectCommands.cs   # 命令 gameobject.get / gameobject.set（active/position/rotation/scale + 相对操作 move/rotate/zoom）
│       ├── SystemCommands.cs       # bridge.ping / bridge.list_commands / bridge.version / bridge.reload
│       └── DebugCommands.cs        # debug.log / log_warning / log_error / get_logs / log_version
└── python/                         # Python 侧（无需安装依赖）
    ├── unity_bridge/
    │   ├── __init__.py
    │   ├── config.py               # 读取 bridge.ini（端口/超时默认值，CLI 与 client 共用）
    │   ├── client.py               # TCP/JSON 客户端 UnityClient
    │   ├── cli.py                  # 命令行入口（tree / list / mesh-bounds / screenshot / terrain / reload / debug）
    │   └── __main__.py             # 支持 python -m unity_bridge
    ├── scripts/
    │   └── mock_unity_server.py    # 模拟 Unity 侧协议，无 Unity 也能联调
    └── requirements.txt
```

---

## 八、安全与注意事项

- 服务器**只绑定 127.0.0.1**，仅本机进程可访问，不会暴露到局域网。
- 命令在主线程执行，避免 Unity API 跨线程调用崩溃。
- 销毁场景中的 BridgeManager 组件/物体（若挂了）会自动停止服务器，不留后台线程。
- 若要在打包后的 Player 中使用，请自行评估：本项目针对 **Editor 开发期工具** 场景。
- **首次导入后**：Unity 会为脚本生成 `.meta` 文件（GUID）。若希望跨项目复制时保持 GUID 稳定（推荐），请把生成的 `.meta` 一并提交到 git。
- **自动化编译**：`bridge.reload` 是进程内 API，可稳定触发重编译（需 Unity 前台）；**模拟鼠标点击对 Unity 编辑器无效**（编辑器忽略注入输入），不要走那条路。
- **版本演进**（当前 v1.13.0）：v1.0.0=独立重构（JsonUtility，5 条命令）→ v1.1.0=新增 12 条 terrain 命令 → v1.2.0=修复 list_commands 序列化 + 版本工具 → v1.3.0=新增 terrain.stash 四命令、view.camera、gameobject.get/set → v1.4.0=新增 debug.get_logs → v1.5.0=新增 debug.log_version → v1.6.0=版本号维护 → v1.7.0=服务器重复启动/停止打印 Warning → v1.8.0=prefab.screenshot 支持直接指定相机位置与观察目标 → v1.9.0=新增 scene.important_scripts（重要脚本检索，匹配规则来自 bridge.ini [scene] important_suffix，默认 Manager/Tool 结尾）→ v1.10.0=scene.tree 遇到 prefab 实例根不展开内部，改为备注 prefab 资产路径 → v1.11.0=scene.tree 新增 depth（遍历深度，根算第 1 层，默认 1）与 path（扫描起点；prefab 内部报错）→ v1.12.0=新增 prefab.tree（prefab 资产内部层级树，path 必填，depth 默认完整展开）→ v1.13.0=gameobject.set 新增相对操作 move（position+=）/ rotate（欧拉各分量加、四元数乘）/ zoom（localScale 各分量乘）。可用 `python -m unity_bridge version` 或 `debug-log-version` 确认当前版本。
