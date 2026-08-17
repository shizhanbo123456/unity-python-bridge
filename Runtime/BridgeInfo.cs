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
        /// </summary>
        public const string Version = "1.6.0";

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
