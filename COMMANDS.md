# UnityPythonBridge 命令列表（COMMANDS.md）

> 本文件是完整的命令参考；**使用方法、架构、配置见 `README.md`**。
> 命令清单已对照源码（C# `[BridgeCommand]` 反射注册 + Python CLI 封装）逐一核对。
> 版本：v1.14.2（47 条）｜整理日期：2026-08-27

## 全局约定

```bash
# 入口：在 python/ 目录下执行
python -m unity_bridge <子命令> [参数]

# 全局参数（必须放在子命令前）
python -m unity_bridge --host 127.0.0.1 --port 21927 --timeout 10 tree

# 所有命令都支持 --json：输出原始 JSON 而非格式化文本，便于程序化处理
python -m unity_bridge terrain-list --json
```

| 全局参数 | 默认值 | 说明 |
|---|---|---|
| `--host` | `127.0.0.1` | Unity 地址（仅本机） |
| `--port` | 读 `bridge.ini` 的 `[server] port`（默认 21927） | 连接/监听端口，**覆盖 ini** |
| `--timeout` | 读 `bridge.ini` 的 `[reload] timeout`（默认 30，仅 reload 用）；连接/响应超时默认 10 | 全局连接/响应超时 |

> ⚠️ **`--port` / `--timeout` 等全局参数必须放在子命令前面**，放后面会报 `unrecognized arguments`。

---

## 一、系统与连通（4 条）

| 服务端命令 | CLI（别名） | 关键参数 / 说明 |
|---|---|---|
| `bridge.ping` | 无专用子命令（`client.ping()` / `client.call("bridge.ping")` / 原始 TCP） | 连通性测试，返回 `pong` + 服务器时间 |
| `bridge.list_commands` | `list`（`ls`） | 列出所有已注册命令 |
| `bridge.version` | `version`（`ver`/`v`） | 版本号 + 命令统计（确认 Unity 侧代码是否最新） |
| `bridge.reload` | `reload`（`rl`） | 触发重编译并轮询等待服务器恢复；`--expect-version`（不匹配继续等）、`--timeout`（总超时，默认读 ini 30s）、`--interval`（轮询间隔，默认 1s） |

## 二、调试与日志（5 条）

| 服务端命令 | CLI（别名） | 关键参数 / 说明 |
|---|---|---|
| `debug.log` | `debug-log`（`dlog`） | Console 打 Info；`message` |
| `debug.log_warning` | `debug-log-warning`（`dlogw`） | Console 打 Warning；`message` |
| `debug.log_error` | `debug-log-error`（`dloge`） | Console 打 Error；`message` |
| `debug.get_logs` | `debug-logs`（`dlogs`） | **读回**最近 N 条 Console 日志（环形缓冲 500，自订阅时刻起）；`--count`（默认 50）、`--type`（all/log/warning/error/exception）；返回 `{index, time, type, message, stackTrace}` |
| `debug.log_version` | `debug-log-version`（`dlogv`） | Console 打印桥接层版本号（含命令总数） |

## 三、相机与截图（3 条）

| 服务端命令 | CLI（别名） | 关键参数 / 说明 |
|---|---|---|
| `prefab.screenshot` | `screenshot`（`shot`） | **隔离渲染**预制体/模型为 PNG（复制到 `(9999,9999,9999)`，摄后销毁）。相机定位二选一：`--offset "x,y,z"`（相对预制体，必填）或 `--camPos`/`--lookAt`（直接指定，`--relative` 切相对模式，`--lookAt` 缺省=预制体）。其它：`--orthographic`、`--fov`（透视=fov/正交=size）、`--width`（1920）、`--height`（1080）、`--bg "r,g,b[,a]"`（默认透明）、`--light`（补光强度，推荐 2） |
| `prefab.billboard` | `prefab-billboard`（`billboard`、`pboard`） | 按 `--camera-position "x,y,z"` 指定的相机相对单位方向正交截取透明 PNG；`output` 必须是输出目录，相对路径基于 `Assets`；先计算投影 bounds，再按 `--pixels-per-meter`（默认 100）自动确定宽高；`--light` 控制与相机同向的平行光强度（默认 2，负数关闭）；输出文件名为预制体名 |
| `view.camera` | `view-screenshot`（`vshot`） | 渲染**场景中指定相机的实时画面**为 PNG（不隔离、不创建临时物体）。`output`(.png)、`--camera`（省略时依次找 MainCamera → "Main Camera" → 第一个激活相机）、`--width`/`--height`（默认相机当前分辨率） |

> `prefab.screenshot` = 单资产隔离渲染（不依赖场景相机）；`view.camera` = 场景已有相机的实时画面。抓"Scene/Game 窗口最终呈现"（含 UI 叠加）为规划中的 `view.window`，尚未实现。

## 四、场景与 Prefab 层级（3 条）

| 服务端命令 | CLI（别名） | 关键参数 / 说明 |
|---|---|---|
| `scene.tree` | `tree` | 树状输出场景物体层级；**prefab 实例根不展开内部，备注资产路径**（`(prefab: Assets/...)`）；`--depth`（遍历深度，根算第 1 层，默认 1 只显示起点本身）、`--path`（扫描起点，层级路径如 `MainCamera/Object1` 或唯一名称，省略=整个场景；起点为 prefab 实例内部时报错并返回 prefab 根场景路径+资产路径）、`--components`（同时显示组件类型）、`--json`（原始数据，prefab 根含 `"prefab"` 字段，指定起点时含 `"startPath"` 字段） |
| `scene.important_scripts` | `important-scripts`（`impscripts`、`imps`） | 列出场景中挂有"重要脚本"的物体；匹配规则取 `bridge.ini` 的 `[scene] important_suffix`（逗号分隔的类名后缀，默认 `Manager, Tool`，忽略大小写）；扫描范围含未激活物体（`active` 标注实际状态）；返回 `suffix`（生效规则）、`scripts`（`path` 层级路径 / `name` 脚本名 / `active`） |
| `prefab.tree` | `prefab-tree`（`ptree`、`pt`） | 以树状结构返回 **prefab 资产内部**的物体层级（类似 scene.tree，但扫描对象是 Assets 下的 prefab）；`path`（**必填**，Assets 相对路径，可带或不带 `Assets/` 前缀，.prefab 或模型文件）、`--depth`（根算第 1 层，默认 `-1`=完整展开）、`--components`、`--json`；嵌套 prefab 实例根同样不展开并备注资产路径 |

## 五、网格与资源（2 条）

| 服务端命令 | CLI（别名） | 关键参数 / 说明 |
|---|---|---|
| `mesh.bounds` | `mesh-bounds`（`bounds`） | 计算 mesh/模型/预制体的轴对齐包围盒（AABB，多网格合并）；`path`（Assets 相对路径，可带或不带 `Assets/` 前缀，支持 .mesh/.fbx/.obj/.blend/.prefab）；返回 `min/max/center/size/format` |
| `prefab.bounds` | `prefab-bounds`（`pbounds`） | 计算 prefab 内全部 MeshFilter/SkinnedMeshRenderer 应用完整父子层级位移、旋转、缩放后的世界 AABB；`path` 可省略 `Assets/` 和 `.prefab`；返回 `min/max/center/size/format` |

## 六、物体操作（4 条）

> `gameobject.instantiate` / `gameobject.destroy` 与下文的 Prefab 资产内部编辑 3 条，原为 workflow 仓库的通用命令，现已提升为 bridge 原生命令（位于 bridge 仓库 `Runtime/Commands/`）。

| 服务端命令 | CLI（别名） | 关键参数 / 说明 |
|---|---|---|
| `gameobject.get` | `gameobject-get`（`gget`） | 读 active / position(世界) / rotation / scale(localScale)；`target`（层级路径优先，名称兼容，重名报错）、`--quaternion`（同时输出四元数） |
| `gameobject.set` | `gameobject-set`（`gset`） | 写上述属性（支持 Undo）；`target`、`--active`(-1 不改/0 隐藏/1 激活)、`--position "x,y,z"`、`--rotation "x,y,z"`（`--quaternion` 时 `"x,y,z,w"`）、`--scale "x,y,z"`；**相对操作**（基于当前值，在绝对设置后执行）：`--move "x,y,z"`（position+=）、`--rotate "x,y,z"`（欧拉各分量相加；`--quaternion` 时四元数 `"x,y,z,w"` 与当前相乘）、`--zoom "x,y,z"`（localScale 各分量相乘，如 `"2,1,1"`=x 放大 2 倍） |
| `gameobject.instantiate` | 无专用子命令（`client.call("gameobject.instantiate")` / 原始 TCP） | 在场景中实例化 Prefab（支持 Undo）；`path`(必填,Prefab 资产路径)、`target`(可选,父物体层级路径/名称,空=场景根)、`name`(可选)、`position`/`rotation`/`scale`/`quaternion`(可选) |
| `gameobject.destroy` | 无专用子命令（`client.call("gameobject.destroy")` / 原始 TCP） | 销毁场景中的 GameObject（支持 Undo）；`target`(必填,层级路径/名称) |

## 七、Prefab 资产内部编辑（3 条）

> 这 3 条命令**直接改并保存 Prefab 资产**（不经场景）。它们现位于 bridge 仓库 `Runtime/Commands/PrefabEditCommands.cs`，删除该文件即完整移除，不影响其它命令。

| 服务端命令 | CLI（别名） | 关键参数 / 说明 |
|---|---|---|
| `prefab.edit` | 无专用子命令（`client.call("prefab.edit")` / 原始 TCP） | 编辑 Prefab 资产内部物体的 Transform（直接保存资产）；`path`(必填,Prefab 资产路径)、`target`(可选,内部层级路径,空=根)、`position`/`rotation`/`scale`/`move`/`rotate`/`zoom`/`quaternion`（语义同 `gameobject.set`） |
| `prefab.remove` | 无专用子命令（`client.call("prefab.remove")` / 原始 TCP） | 从 Prefab 资产内部删除物体（直接保存资产）；`path`(必填)、`target`(必填,内部层级路径) |
| `prefab.instantiate` | 无专用子命令（`client.call("prefab.instantiate")` / 原始 TCP） | 在 Prefab 资产内部实例化另一个 Prefab 为子物体（直接保存资产）；`path`(必填,目标 Prefab)、`output`(必填,子 Prefab 资产路径)、`target`(可选,内部父路径)、`position`/`rotation`/`scale`(可选) |

## 八、地形编辑（19 条）

> **公共参数**：`terrain`(可选) —— 目标 Terrain 名称，省略取场景第一个；区域参数 `xBase`/`zBase`/`width`/`height`(可选) —— 操作区域，省略默认整图。

### 7a. 信息查询（4）
| 服务端命令 | CLI（别名） | 关键参数 / 说明 |
|---|---|---|
| `terrain.list` | `terrain-list`（`tlist`） | 列出所有 Terrain（位置/尺寸/各分辨率/层数/树数） |
| `terrain.get_layers` | `terrain-get-layers`（`tlayer`） | 列出纹理层（名称 + Diffuse 贴图路径） |
| `terrain.list_details` | `terrain-list-details`（`tdlist`） | 列出草原型（DetailPrototype） |
| `terrain.list_trees` | `terrain-list-trees`（`ttlist`） | 列出树原型 + 全部树实例（位置/缩放） |

### 7b. 高度图（2）
| `terrain.get_heights` | `terrain-get-heights`（`tget`） | 读区域高度（0~1，行优先 `index=y*width+x`） |
| `terrain.set_heights` | `terrain-set-heights`（`tset`） | 写高度：`--data`（float[] 行优先 0~1）**或** `--noise` Perlin 噪声（`--noiseScale/--noiseSeed/--baseHeight/--heightScale`，可复现） |

### 7c. 纹理混合（2）
| `terrain.get_alphamaps` | `terrain-get-alphamaps`（`tamap`） | 读混合权重（`index=(y*width+x)*layers+layer`） |
| `terrain.set_alphamaps` | `terrain-set-alphamaps`（`tsamap`） | 写混合权重（**每像素自动归一化**）；`--data`(float[]) |

### 7d. 植被 Detail（2）
| `terrain.get_details` | `terrain-get-details`（`tdget`） | 读某层植被密度图；`--layer` |
| `terrain.set_details` | `terrain-set-details`（`tdset`） | 写密度：`--data`（int[] 0~16）**或** `--random --count --seed --density` 撒点；`--layer` |

### 7e. 树木（2）
| `terrain.add_trees` | `terrain-add-trees`（`ttadd`） | 加树：`--positions`（每 3 个一组 {x,y,z} 归一化 0~1，自动贴地）**或** `--random --count --seed --minScale --maxScale`；`--prototypeIndex` |
| `terrain.clear_trees` | `terrain-clear-trees`（`ttclear`） | 清空所有树实例 |

### 7f. 快照 stash（4）
| `terrain.stash` | `terrain-stash`（`tstash`） | trees/details/all 全量存 JSON 到 `Assets/unity-python-bridge/stash/{trees\|details}/<name>.json`；`--type`（默认 all）、`--name`（必填，**同名报错不允许覆盖**） |
| `terrain.apply_stash` | `terrain-apply-stash`（`tapply`） | 读 JSON **整体写回**地形（替换当前内容；原型数/分辨率不匹配拒绝）；`--type`、`--name` |
| `terrain.stash_delete` | `terrain-stash-delete`（`tstashdel`） | 删除快照；`--type`（trees/details）、`--name` |
| `terrain.stash_list` | `terrain-stash-list`（`tstashlist`） | 列出快照；`--type`（默认 all） |

### 7g. 资源目录审计（3）
| `terrain.get_diffuse_dirs` | `terrain-get-diffuse-dirs`（`tdiff`） | TerrainLayer 的 Diffuse 贴图**目录（去重）** + 各层完整路径 |
| `terrain.get_tree_prefab_dirs` | `terrain-get-tree-prefab-dirs`（`ttpd`） | 树原型 Prefab 目录（去重）+ 各原型路径 |
| `terrain.get_detail_asset_dirs` | `terrain-get-detail-asset-dirs`（`tdad`） | 草原型的 prefab/贴图目录（去重）+ 各原型路径 |

---

## 九、编辑器 Play Mode 控制（4 条）

> **纯 Editor API，bridge 仓库通用能力，不依赖任何业务项目。** 用于控制 Unity Editor 的 Play Mode（开始 / 停止 / 暂停 / 恢复模拟）。
> ⚠️ **退出 Play Mode 时**，若 Unity 启用了「Reload Domain」（默认开启），会触发 domain reload，桥服务器会随旧域一起卸载——**由 BridgeAutoRestart 的 watchdog 在数秒内自动恢复**，调用方无需处理（可轮询 `bridge.version` 等待恢复，同 `bridge.reload`）。

| 服务端命令 | CLI（别名） | 关键参数 / 说明 |
|---|---|---|
| `editor.play` | `play`（`pl`） | 进入 Play Mode；返回 `{isPlaying, isPaused, message}` |
| `editor.stop` | `stop`（`st`） | 退出 Play Mode；返回切换后的状态（若触发 domain reload，桥会自动恢复） |
| `editor.pause` | `pause`（`pa`） | 暂停 Play Mode 模拟（保持运行中）；返回状态 |
| `editor.unpause` | `unpause`（`unp`） | 恢复 Play Mode 模拟（取消暂停）；返回状态 |

---

## 常用工作流

### 造地形（一条龙）
```bash
python -m unity_bridge terrain-set-heights --noise --noiseScale 0.02 --noiseSeed 42 --baseHeight 0.2 --heightScale 0.6
python -m unity_bridge terrain-set-details --layer 0 --random --count 200 --seed 7 --density 4
python -m unity_bridge terrain-add-trees --prototypeIndex 0 --random --count 50 --seed 7
```

### 截图评审（配合 AI 识图）
```bash
# 隔离渲染单资产（相机位置/观察点直接指定）
python -m unity_bridge screenshot Assets/Prefabs/Tree.prefab out/tree.png --offset "0,0,0" --camPos "5,3,8" --lookAt "0,0,0" --light 2
# 抓场景相机实时画面
python -m unity_bridge view-screenshot out/game.png
```

### stash 快照调试（stash → clear → 截图 → apply）
```bash
python -m unity_bridge terrain-stash --name forest_v1 --type all    # 保存当前状态（同名会报错）
python -m unity_bridge terrain-clear-trees                           # 清空看干净地形
python -m unity_bridge view-screenshot out/clean.png
python -m unity_bridge terrain-apply-stash --name forest_v1 --type all  # 恢复
```

### 查看层级结构（场景 / Prefab 资产）
```bash
python -m unity_bridge tree                                   # 场景树（默认 depth=1 只显示根；prefab 实例折叠标注资产路径）
python -m unity_bridge tree --depth 3 --components            # 展开到第 3 层并带组件
python -m unity_bridge tree --path "MainCamera/Object1"       # 只从指定物体开始扫描（prefab 实例内部会报错并给出 prefab 位置）
python -m unity_bridge important-scripts                      # 列出场景中挂有 Manager/Tool 等后缀脚本的物体
python -m unity_bridge prefab-tree Assets/Prefabs/Tree.prefab # 直接查看 prefab 资产内部层级（path 必填，默认完整展开）
```

### 物体相对变换（基于当前值，支持 Undo）
```bash
python -m unity_bridge gameobject-get "Player/Body"           # 先读当前状态
python -m unity_bridge gameobject-set "Player/Body" --move "0,10,0"    # 相对位移：position += (0,10,0)
python -m unity_bridge gameobject-set "Player/Body" --rotate "0,90,0"  # 相对旋转：欧拉角各分量相加
python -m unity_bridge gameobject-set "Player/Body" --rotate "0,0,0.7071,0.7071" --quaternion  # 四元数：与当前旋转相乘
python -m unity_bridge gameobject-set "Player/Body" --zoom "2,1,1"     # 相对缩放：x 轴放大 2 倍（相乘，非相加）
# 绝对设置与相对操作可混用（相对操作在绝对设置之后执行）：
python -m unity_bridge gameobject-set "Player/Body" --position "0,0,0" --zoom "0.5,0.5,0.5"
```

### 自诊断（日志读回 + 重编译）
```bash
python -m unity_bridge debug-logs --type error --count 20   # 读最近 20 条错误（含 stackTrace）
python -m unity_bridge reload --expect-version 1.13.0        # 改完 C# 后触发重编译并等待恢复
```

### Play Mode 控制（开始 / 暂停 / 恢复 / 停止）
```bash
python -m unity_bridge play            # 进入 Play Mode（开始运行）
python -m unity_bridge pause           # 暂停模拟（保持运行中）
python -m unity_bridge unpause         # 恢复模拟
python -m unity_bridge stop            # 退出 Play Mode（若启用 Reload Domain，桥会自动恢复）
# 也可用 Python API：client.play() / stop() / pause() / unpause()

```
