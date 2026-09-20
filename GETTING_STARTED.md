# UnityPythonBridge 新手入门（GETTING_STARTED.md）

> 本文件面向**第一次接触该工具的人**：读一遍即可，不需要长期查阅。
> 使用过程中需要查命令 → 看 [`COMMANDS.md`](COMMANDS.md)；遇到问题 → 看 [`FAQ.md`](FAQ.md)；架构与配置细节 → 看 [`README.md`](README.md)。

---

## 1. 环境前置

| 项 | 要求 | 说明 |
|---|---|---|
| Unity | 2020.3+（建议 2022 LTS） | 实测基于 2022.3.62f3c1，更老版本未验证 |
| Python | 3.8+ | 仅用标准库，**零第三方依赖** |
| 操作系统 | Windows 主验证 | C# 侧走 UnityEditor API 跨平台；Python 侧标准库跨平台；路径/编码差异主要在 Windows 验证 |
| 网络 | 仅本机 | 服务器只监听 `127.0.0.1`，无需外网 |

## 2. 这个工具能做什么（一分钟速览）

- **命令行操控 Unity 编辑器**：读场景结构、查资产、算包围盒、截图
- **物体操作**：`gameobject.get/set` 读写 active/position/rotation/scale（支持 Undo），并有相对操作 `--move`（位移+=）/ `--rotate`（欧拉各分量加、四元数乘）/ `--zoom`（缩放各分量乘）；`gameobject.instantiate` / `gameobject.destroy` 在场景中实例化/销毁 Prefab（支持 Undo）；`prefab.edit` / `prefab.remove` / `prefab.instantiate` 直接改并保存 Prefab 资产（不经场景）
- **场景与 Prefab 层级查看**：`scene.tree` 树状看场景（prefab 实例折叠并标注资产路径，支持深度/起点）；`prefab.tree` 直接看 prefab 资产内部层级；`scene.important_scripts` 一键列出挂有 Manager/Tool 等重要脚本的物体
- **地形程序化生成**：造山（噪声）、铺纹理、撒植被、种树，一条命令完成
- **地形快照**：把树木/植被存成 JSON，随时恢复（stash → clear → 截图 → apply）
- **日志与调试**：往 Console 打日志、读回最近日志（含报错堆栈）
- **自动化重编译**：改完 C# 一条命令触发 Unity 重编译并等待恢复
- **离线联调**：没开 Unity 也能用 mock 服务器验证命令行

## 3. 安装与验证（三步确认装好）

```bash
# ① 把 unity-python-bridge/ 整个文件夹放进 Unity 项目的 Assets/ 下
# ② Unity 里菜单 Tools → Unity Python Bridge → Start Server（看到"监听 127.0.0.1:21927"即成功）
# ③ 验证（在 python/ 目录下）：
python -m unity_bridge version      # 能打印 v1.14.2 / 47 命令 = 连通 ✓
python -m unity_bridge tree         # 能看到场景物体树 = 命令可用 ✓
python -m unity_bridge list         # 命令数=47 = 注册完整 ✓
```

> Windows 下如果 `python` 不在 PATH，用 `py -3 -m unity_bridge ...`。

## 4. 排障速查（新手最常遇到）

| 症状 | 原因 | 处理 |
|---|---|---|
| 「无法连接 Unity（127.0.0.1:21927）」 | 服务器没启动 / 端口不一致 / Unity 关了 | 菜单 Start Server；检查 `bridge.ini` 端口与 `--port` 是否一致 |
| 连上了但命令报「未知命令」 | Unity 侧代码比 Python 侧旧 | `reload --expect-version <当前版本号>` 让两侧同步 |
| 端口被占用 | 多项目共用默认 21927 | 改 `bridge.ini` 的 `[server] port`（两侧自动读取） |
| reload 一直等待/超时 | **Unity 窗口不在前台**（失焦时 update 不运行） | 把 Unity 窗口切到前台再 reload |

## 5. 术语表

| 术语 | 含义 |
|---|---|
| `domain reload` | Unity 脚本重新编译后整个脚本域重启的过程（服务器靠它自动恢复） |
| `JsonUtility` | Unity 内置 JSON 序列化器（工具用它，不装任何第三方库） |
| 隔离位置 `(9999,9999,9999)` | 截图时临时摆放预制体的坐标，远离场景原点避免干扰；用完即销毁 |
| 行优先 `index=y*width+x` | 一维数组按"先横后竖"展开的索引规则（地形高度/密度图） |
| 归一化坐标（0~1） | 地形上相对位置：0=地形起点，1=地形终点（种树 `positions` 用） |
| `dirty` | Unity 里"有未保存修改"的标记；地形写入后需 Ctrl+S 保存 |
| Edit / Play Mode | 编辑器模式 / 运行模式；工具两种模式都可用 |

## 6. 命令的影响边界（哪些会动你的场景）

| 类型 | 命令 | 影响 |
|---|---|---|
| **只读** | `tree` / `prefab-tree` / `important-scripts` / `bounds` / `terrain-list` / `get_*` / `dlogs` / `version` 等 | 不改变任何东西 |
| **改场景（可保存）** | `terrain.set_*` / `add_trees` / `clear_trees` / `apply_stash` | 立即生效并标记 dirty，需 Ctrl+S；写入会自动重建地形碰撞体 |
| **改物体（支持 Undo）** | `gameobject.set` / `gameobject.instantiate` / `gameobject.destroy` | 编辑器内可 Ctrl+Z 撤销 |
| **改 Prefab 资产（直接保存）** | `prefab.edit` / `prefab.remove` / `prefab.instantiate` | 直接改并保存 .prefab 资产，不经场景；保存后无法用场景 Undo 回退 |
| **临时隔离（用完销毁）** | `prefab.screenshot` | 复制到 9999 坐标渲染，结束后销毁临时对象，不污染场景 |
| **仅日志** | `debug.log*` / `debug.log_version` | 只在 Console 打日志 |

## 7. 升级与版本

- 升级：`git pull`（若 clone 部署）或重新复制文件夹覆盖
- 确认版本：`python -m unity_bridge version`，与 GitHub 最新提交对比
- **首次导入后把生成的 `.meta` 一并提交 git**，跨项目复制时 GUID 才能保持稳定

## 8. 安全边界（务必知道）

- 服务器只监听 `127.0.0.1`，不暴露局域网
- **但任何能访问该端口的本机进程都可以执行任意命令**（包括读取/修改你的场景与资产）——不要让不明来源的脚本在服务器运行时运行
- 工具定位是 **Editor 开发期工具**，不要在打包后的 Player 中依赖它

## 9. MCP 客户端接入

MCP 是原有 CLI 之外的可选入口。先在 `python/` 目录执行 `python -m pip install -r requirements.txt`，然后把 MCP 客户端配置为以该目录为工作目录、启动 `python -m unity_bridge.mcp_server`。stdio 服务通常由客户端自动启动和关闭，无需手动常驻。

适配器提供高频强类型工具，并通过 `list_unity_commands` 和 `call_unity_command` 覆盖当前全部 Bridge 命令。通用网关的只读命令可直接执行；任何可能修改场景、资产或编辑器状态的命令都要求 `confirm_changes=true`。编译应使用 `reload_unity`，它会等待 Unity Domain Reload 后自动恢复。

保持 Unity Editor 打开且 BridgeServer 在线，然后运行 `python scripts/test_mcp_smoke.py` 可验证 MCP 握手、工具发现和真实 Unity 调用。
