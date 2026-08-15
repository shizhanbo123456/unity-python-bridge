using UnityEngine;

namespace UnityPythonBridge.Commands
{
    /// <summary>
    /// 调试日志命令：在 Unity Console 打印日志，便于从 Python 侧确认链路 / 调试。
    /// 参数: message (string)。
    /// </summary>
    public static class DebugCommands
    {
        [BridgeCommand("debug.log", "在 Unity Console 打印一条 Info 日志。参数: message(string)")]
        public static object Log(BridgeContext ctx, BridgeArgs args)
        {
            var message = args.message ?? "";
            Debug.Log($"[Bridge] {message}");
            return new LogResult
            {
                level = "info",
                message = message,
                logged = true,
            };
        }

        [BridgeCommand("debug.log_warning", "在 Unity Console 打印一条 Warning 日志。参数: message(string)")]
        public static object LogWarning(BridgeContext ctx, BridgeArgs args)
        {
            var message = args.message ?? "";
            Debug.LogWarning($"[Bridge] {message}");
            return new LogResult
            {
                level = "warning",
                message = message,
                logged = true,
            };
        }

        [BridgeCommand("debug.log_error", "在 Unity Console 打印一条 Error 日志。参数: message(string)")]
        public static object LogError(BridgeContext ctx, BridgeArgs args)
        {
            var message = args.message ?? "";
            Debug.LogError($"[Bridge] {message}");
            return new LogResult
            {
                level = "error",
                message = message,
                logged = true,
            };
        }
    }

    /// <summary>debug.log* 返回结构。</summary>
    [System.Serializable]
    public class LogResult
    {
        public string level;    // info / warning / error
        public string message;
        public bool logged;
    }
}
