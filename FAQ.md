# UnityPythonBridge 常见问题（FAQ.md）

> 遇到问题先看这里。新手入门（一次性阅读）见 [`GETTING_STARTED.md`](GETTING_STARTED.md)，命令列表见 [`COMMANDS.md`](COMMANDS.md)，架构/配置见 [`README.md`](README.md)。

---

## A. 连接与配置

**A1. 报「无法连接 Unity（127.0.0.1:21927）」？**
服务器没启动、端口不一致或 Unity 未运行。处理：Unity 菜单 **Tools → Unity Python Bridge → Start Server** 后重试；检查 `bridge.ini` 的 `[server] port` 与命令 `--port` 是否一致。

**A2. 改了 bridge.ini 端口不生效？**
检查：段名是否写对（`[server]` 不是 `[Server]`）、`port = 21928` 写法、行内注释 `; 注释` 是否把数字截断、Python 侧是否有 `--port` 显式覆盖了 ini。ini 修改后无需重启，下次运行命令即生效。

**A3. 端口被占用怎么办？**
改 `bridge.ini` 的 `[server] port`（Python 与 C# 两侧都自动读取），或临时用 `--port <新端口>`（注意：`--port` 是全局参数，必须放在子命令**前面**）。

**A4. 服务器没有自动恢复？**
自动恢复依赖 `Library/BridgeServerState.txt` 为 `1`。若上次是通过「Stop Server」正常停止（写 0），重开项目不会自动启动；重新 Start Server 一次即可。

## B. 编译与 reload

**B1. reload 一直「等待中」或超时？**
**Unity 窗口必须在前台**——失焦/后台时 `EditorApplication.update` 不运行，重编译不会触发。把 Unity 切到前台再执行 reload。

**B2. 能用模拟鼠标点击触发编译吗？**
**不能。** Unity 编辑器会忽略一切软件注入的鼠标输入（`SetForegroundWindow` 置前、`mouse_event` 点击、`PostMessage` 消息序列都无效，已实测）。`bridge.reload` 是进程内 API（`CompilationPipeline.RequestScriptCompilation`），不依赖输入事件，是自动化编译的正路。

**B3. 怎么确认编译后代码生效？**
`python -m unity_bridge reload --expect-version <版本号>`——版本不匹配会继续等待，匹配才返回成功；或编译完成后 `version` 查看命令数（如 v1.14.1 = 38 条）。

**B4. reload 与「改 Assets 文件自动编译」的关系？**
Unity 检测到 Assets 下 C# 变化会自动编译，但**失焦时会暂停**，需要窗口获得真实点击焦点才恢复。reload 则是在进程内显式请求编译，配合窗口置前即可全自动。

## C. 命令使用

**C1. `screenshot` 和 `view-screenshot` 有什么区别？**
`screenshot`（`prefab.screenshot`）= 把单个预制体/模型临时复制到 `(9999,9999,9999)` 隔离渲染，适合"看单个资产"，用完销毁不污染场景；`view-screenshot`（`view.camera`）= 抓场景中**已有相机**的实时画面，适合"看整场景当前效果"。

**C2. 截图太暗或全黑？**
场景里没有平行光。加 `--light 2`（推荐值）临时补一盏与相机同向的平行光，渲染完自动销毁。

**C3. 改了地形，场景里没变化 / 想保存？**
写入立即生效并标记 dirty；保存需在 Unity 里 **Ctrl+S**。高度/纹理/植被写入会自动重建地形碰撞体（大区域可能卡一下，属正常）。

**C4. `gameobject.set` 会弄坏场景吗？**
支持 Undo（编辑器内 Ctrl+Z 可撤销）。`target` 有重名时会报错并提示用层级路径（如 `Player/Body`）。

**C5. 种树/撒草的坐标是什么单位？**
`terrain.add_trees --positions` 用**归一化坐标（0~1）**：0=地形该轴起点，1=终点；不是世界坐标。随机模式（`--random`）由工具自动生成合法位置。

**C6. stash 同名保存报错？**
设计如此（防止误覆盖）。需要覆盖时先 `terrain-stash-delete --type trees --name <名>` 再保存。

**C7. apply_stash 被拒绝？**
保存时的树原型数 / 草原型数 / detail 分辨率与当前地形不一致时会拒绝应用（防止错位），属保护行为。需先调整地形或删除旧 stash。

**C8. 怎么读 Unity 的报错日志？**
`python -m unity_bridge debug-logs --type error --count 20`（v1.4.0+），返回最近 20 条错误及完整 stackTrace。

## D. 命令相关的已知坑（bug 与约定）

**D1. `--port` / `--timeout` 是全局参数，必须放在子命令前**
`python -m unity_bridge --port 21950 version` ✅；`python -m unity_bridge version --port 21950` ❌（报 `unrecognized arguments`）。

**D2. 新增 C# 命令参数时报 CS0102（重复字段）**
`BridgeArgs` 里 `count` 等字段已被多个命令共用（terrain 撒点数量、`debug.get_logs` 条数）。**加新字段前先全文件搜索同名**，尽量复用已有字段。

**D3. CLI 里 float 数组参数不要走 `_parse_vec3`**
它返回 dict `{x,y,z}`，`list(dict)` 会得到键名 `["x","y","z"]`（历史 bug，已修复为直接解析 float 列表）。写新 CLI 参数时注意区分"传 dict 给 Vector3"与"传 list 给 float[]"。

**D4. mock 服务器与真实 Unity 的差异**
`mock_unity_server.py` 的截图生成**占位 PNG**（不是真实渲染），stash 存在内存中（不落盘）。它只用于验证 Python 调用逻辑，不代表真实渲染结果。

**D5. 版本号不一致（mock 与 Unity 侧）**
`bridge.version` 读的是**运行中的服务器**：Python 侧 mock 返回 mock 里的版本号，真实 Unity 返回 C# `BridgeInfo.Version`。改版本号需同步 `BridgeInfo.cs` 与 mock，并 `reload` 让 Unity 重新编译。

**D6. 服务器重复启动/停止没有反应？**
v1.7.0+ 会打印 Warning：已运行再「启动」→「服务器已在运行中…」；未运行再「停止」→「服务器未在运行…」。看到 Warning 说明操作被正确拦截，不是 bug。

**D7. `tree --path` 报「不能直接扫描 prefab 实例内部」？**
设计如此（v1.11.0+）：`--path` 不允许指向 prefab 实例内部的物体，报错信息会给出 **prefab 根在场景中的路径**和 **Assets 中 prefab 的路径**，从 prefab 根或场景根开始扫描即可。起点就是 prefab 实例根（如 `Tree_A_1`）则允许，节点会显示 prefab 资产路径标注。

**D8. `tree --path` / `prefab-tree` 里物体名带空格匹配不到？**
prefab 资产内部物体名可能带首尾空格（如 `Tree_A_1.prefab` 里的子物体实际叫 `'Cylinder '`）。路径匹配已按 **Trim 后比较**处理（v1.11.0+），输入 `Tree_A_1/Cylinder` 即可命中；`--json` 输出的 `name` 保留原始名称（含空格），属正常。

**D9. `prefab.tree` 与 `scene.tree` 的 `--depth` 默认值不同？**
是的：`scene.tree` 默认 1（只显示起点本身，场景根通常够用）；`prefab.tree` 默认 `-1`（完整展开，因为该工具定位就是看 prefab 内部结构）。需要限制层级时显式传 `--depth N`。

**D10. `gameobject-set` 的 `--move/--rotate/--zoom` 与绝对参数混用时的顺序？**
相对操作在**绝对设置（`--position/--rotation/--scale`）之后**执行，所以 `--position "0,0,0" --zoom "2,1,1"` 是先归零再放大。`--rotate` 四元数模式是**右乘**（`当前旋转 * 输入`，即先自身旋转再按输入旋转）；欧拉角模式是各分量直接相加。`--zoom` 是**相乘**（`"2,1,1"` = x 放大 2 倍），不是相加。

## E. 环境与维护

**E1. Python 需要装包吗？**
不需要。Python 侧纯标准库（3.8+），零 pip 依赖。

**E2. mock_unity_server.py 什么时候用？**
没有 Unity 环境时验证命令行/客户端逻辑：终端 A `python scripts/mock_unity_server.py`，终端 B 正常用 CLI。

**E3. 新增命令后 Python 侧怎么用起来？**
① 写 C# 命令 → ② Unity 前台执行 `reload --expect-version <版本>` → ③ `list` 确认命令出现 → ④ 按需在 `cli.py` 加子命令封装。

**E4. 跨项目复制工具要注意什么？**
把生成的 `.meta` 一并提交 git（GUID 稳定，避免资源引用错乱）；多项目同时运行注意端口冲突（改各项目 `bridge.ini`）。

**E5. 能在打包后的 Player 里用吗？**
不推荐。工具全部代码 `#if UNITY_EDITOR` 包裹，定位是 Editor 开发期工具。
