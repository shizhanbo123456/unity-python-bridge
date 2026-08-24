#if UNITY_EDITOR
using System.Linq;
using UnityEngine;

namespace UnityPythonBridge
{
    /// <summary>
    /// 桥接层版本信息。用于确认 Unity 侧代码是否为最新：
    /// 菜单 Tools > Unity Python Bridge > 打印版本信息 会输出版本号与命令统计，
    /// 与 GitHub 仓库最新提交对比即可判断是否已更新。
    /// </summary>
    public static class BridgeInfo
    {
        /// <summary>
        /// 桥接层版本号。每次发布新功能时递增：
        ///   v1.0.0 独立重构（JsonUtility，5 条原生命令）
        ///   v1.1.0 新增 12 条 terrain.* 命令
        ///   v1.2.0 修复 list_commands 顶层 List 序列化 + 新增版本工具
        ///   v1.3.0 新增 terrain.stash 四命令（快照 JSON）+ view.camera（抓指定相机）+ gameobject.get/set
        ///   v1.4.0 新增 debug.get_logs（读取最近 Console 日志，环形缓冲）
        ///   v1.5.0 新增 debug.log_version（在 Console 打印版本号）
        ///   v1.6.0 版本号维护
        ///   v1.7.0 服务器重复启动/停止时打印 Warning 提示
        ///   v1.8.0 prefab.screenshot 支持直接指定相机位置与观察目标（世界/相对）
        ///   v1.9.0 新增 scene.important_scripts（列出挂有 Manager/Tool 等后缀脚本的物体，
        ///          匹配规则来自 bridge.ini [scene] important_suffix）
        ///   v1.10.0 scene.tree 遇到 prefab 实例根不再展开内部，改为备注 prefab 资产路径（Assets/...）
        ///   v1.11.0 scene.tree 新增 depth（遍历深度，根算第 1 层，默认 1）与 path（扫描起点，
        ///          层级路径/唯一名称；prefab 实例内部报错并返回 prefab 根与资产路径）
        ///   v1.12.0 新增 prefab.tree（prefab 资产内部层级树，path 必填；depth 默认完整展开）
        ///   v1.13.0 gameobject.set 新增相对操作 move（position+=）/ rotate（欧拉各分量加、四元数乘）/
        ///          zoom（localScale 各分量乘）
        ///   v1.14.2 新增 editor.play / editor.stop / editor.pause / editor.unpause
        ///          （Play Mode 控制，纯 Editor API，bridge 仓库通用能力）
        /// </summary>
        public const string Version = "1.14.2";

        /// <summary>在 Unity Console 打印版本与命令统计。</summary>
        public static void PrintVersion()
        {
            var names = BridgeDispatcher.CommandMap.Keys.OrderBy(k => k, System.StringComparer.Ordinal).ToArray();
            int terrainCount = names.Count(k => k.StartsWith("terrain."));

            Debug.Log($"[UnityPythonBridge] 版本 v{Version}");
            Debug.Log($"[UnityPythonBridge] 命令总数: {names.Length}（含 terrain.* {terrainCount} 条）");
            Debug.Log($"[UnityPythonBridge] 命令列表: {string.Join(", ", names)}");
        }

        /// <summary>返回版本与命令统计（供 bridge.version 命令使用）。</summary>
        public static VersionInfo GetVersionInfo()
        {
            var names = BridgeDispatcher.CommandMap.Keys;
            return new VersionInfo
            {
                version = Version,
                commandCount = names.Count(),
                terrainCommandCount = names.Count(k => k.StartsWith("terrain.")),
            };
        }
    }

    /// <summary>bridge.version 返回结构。</summary>
    [System.Serializable]
    public class VersionInfo
    {
        public string version;
        public int commandCount;
        public int terrainCommandCount;
    }
}
#endif // UNITY_EDITOR
