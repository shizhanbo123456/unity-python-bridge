#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityPythonBridge.Commands
{
    /// <summary>
    /// 调试日志命令：在 Unity Console 打印日志，便于从 Python 侧确认链路 / 调试。
    /// 参数: message (string)。
    /// </summary>
    public static class DebugCommands
    {
        // ---- debug.get_logs：Console 日志环形缓冲 ----
        // 通过 Application.logMessageReceived 在每次日志产生时收进缓冲，
        // 供 debug.get_logs 从 Python 侧读取最近 N 条。上限 500 条，超出丢最旧。
        private const int MaxBufferedLogs = 500;
        private static readonly List<LogEntry> _logBuffer = new List<LogEntry>(MaxBufferedLogs);
        private static readonly object _logLock = new object();
        /// <summary>
        /// 当前日志过滤子串；null=不过滤。
        /// 非 null 时：新日志 message 必须包含该子串才进入缓冲（debug.set_log_filter 设置）。
        /// </summary>
        private static string _logFilter;

        static DebugCommands()
        {
            // 静态构造函数在类型首次被访问时执行（BridgeDispatcher 反射调用命令时触发），
            // 确保任意 debug.* 命令可用前订阅已建立。
            Application.logMessageReceived += OnLogMessageReceived;
        }

        private static void OnLogMessageReceived(string condition, string stackTrace, LogType type)
        {
            string t;
            switch (type)
            {
                case LogType.Warning: t = "warning"; break;
                case LogType.Error: t = "error"; break;
                case LogType.Exception: t = "exception"; break;
                case LogType.Assert: t = "error"; break;
                default: t = "log"; break;
            }
            lock (_logLock)
            {
                // 过滤子串生效时，message 不含子串的日志直接丢弃（不入缓冲）
                if (_logFilter != null && (condition == null || !condition.Contains(_logFilter)))
                    return;
                _logBuffer.Add(new LogEntry
                {
                    time = Time.realtimeSinceStartup,
                    type = t,
                    message = condition,
                    stackTrace = stackTrace ?? "",
                });
                if (_logBuffer.Count > MaxBufferedLogs)
                    _logBuffer.RemoveRange(0, _logBuffer.Count - MaxBufferedLogs);
            }
        }

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

        [BridgeCommand("debug.get_logs", "读取最近 N 条 Console 日志（自订阅时刻起缓存的环形缓冲）。参数: count(可选,默认50), type(可选:all/log/warning/error/exception)")]
        public static object GetLogs(BridgeContext ctx, BridgeArgs args)
        {
            int count = args.count > 0 ? args.count : 50;
            string filter = string.IsNullOrEmpty(args.type) ? "all" : args.type.Trim().ToLowerInvariant();

            var entries = new List<LogEntryDto>();
            lock (_logLock)
            {
                int start = Mathf.Max(0, _logBuffer.Count - count);
                for (int i = start; i < _logBuffer.Count; i++)
                {
                    var e = _logBuffer[i];
                    if (filter != "all" && e.type != filter) continue;
                    entries.Add(new LogEntryDto
                    {
                        index = i,
                        time = (float)Math.Round(e.time, 3),
                        type = e.type,
                        message = e.message,
                        stackTrace = e.stackTrace,
                    });
                }
            }
            return new GetLogsResult { count = entries.Count, entries = entries.ToArray() };
        }

        [BridgeCommand("debug.set_log_filter", "设置日志过滤子串：立即丢弃当前缓冲中 message 不含该子串的日志，且后续日志必须包含该子串才入缓冲。参数: substring(string)；传空字符串清除过滤（不清空缓冲）。")]
        public static object SetLogFilter(BridgeContext ctx, BridgeArgs args)
        {
            string substring = args.substring;

            int kept;
            int removed;
            bool active;
            lock (_logLock)
            {
                if (string.IsNullOrEmpty(substring))
                {
                    // 空子串 = 清除过滤（保留现有缓冲内容不动）
                    _logFilter = null;
                    active = false;
                    kept = _logBuffer.Count;
                    removed = 0;
                }
                else
                {
                    _logFilter = substring;
                    int before = _logBuffer.Count;
                    _logBuffer.RemoveAll(e => e.message == null || !e.message.Contains(substring));
                    active = true;
                    kept = _logBuffer.Count;
                    removed = before - kept;
                }
            }
            return new SetLogFilterResult
            {
                filter = active ? substring : "",
                active = active,
                kept = kept,
                removed = removed,
            };
        }

        [BridgeCommand("debug.log_version", "在 Unity Console 打印桥接层版本号（含命令总数）。参数: 无")]
        public static object LogVersion(BridgeContext ctx, BridgeArgs args)
        {
            var info = BridgeInfo.GetVersionInfo();
            Debug.Log($"[Bridge] 版本号: v{info.version}（共 {info.commandCount} 条命令，含 terrain.* {info.terrainCommandCount} 条）");
            return new LogResult
            {
                level = "info",
                message = $"v{info.version}",
                logged = true,
            };
        }

        /// <summary>日志缓冲内部条目（不参与序列化）。</summary>
        private struct LogEntry
        {
            public float time;
            public string type;
            public string message;
            public string stackTrace;
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

    /// <summary>debug.get_logs 单条日志。</summary>
    [System.Serializable]
    public class LogEntryDto
    {
        public int index;       // 缓冲内序号（便于追踪）
        public float time;      // Time.realtimeSinceStartup（秒）
        public string type;     // log / warning / error / exception
        public string message;
        public string stackTrace;
    }

    /// <summary>debug.get_logs 返回结构。</summary>
    [System.Serializable]
    public class GetLogsResult
    {
        public int count;
        public LogEntryDto[] entries;
    }

    /// <summary>debug.set_log_filter 返回结构。</summary>
    [System.Serializable]
    public class SetLogFilterResult
    {
        public string filter;   // 生效的过滤子串（清除时为 ""）
        public bool active;     // 过滤是否生效
        public int kept;        // 操作后缓冲内剩余条数
        public int removed;     // 本次被丢弃的条数（清除过滤时为 0）
    }
}
#endif // UNITY_EDITOR
