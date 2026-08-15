#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;

namespace UnityPythonBridge.Commands
{
    /// <summary>bridge.ping 返回结构。</summary>
    [System.Serializable]
    public class PingResult
    {
        public bool pong;
        public string time;
    }

    /// <summary>bridge.list_commands 返回的单个命令信息。</summary>
    [System.Serializable]
    public class CommandInfo
    {
        public string name;
        public string description;
    }

    /// <summary>
    /// bridge.list_commands 返回结构。
    /// 注意：不能直接返回顶层 List&lt;CommandInfo&gt;——JsonUtility.ToJson 对顶层 List
    /// 会序列化失败（退化为 {}），必须包一层 [Serializable] 类。
    /// </summary>
    [System.Serializable]
    public class CommandListResult
    {
        public int count;
        public List<CommandInfo> commands = new List<CommandInfo>();
    }

    /// <summary>系统级命令：连通性测试、命令列表等。</summary>
    public static class SystemCommands
    {
        [BridgeCommand("bridge.ping", "连通性测试，成功返回 pong 与服务器时间")]
        public static object Ping(BridgeContext ctx, BridgeArgs args)
        {
            return new PingResult
            {
                pong = true,
                time = DateTime.UtcNow.ToString("o")
            };
        }

        [BridgeCommand("bridge.list_commands", "列出所有已通过反射注册的命令")]
        public static object ListCommands(BridgeContext ctx, BridgeArgs args)
        {
            var result = new CommandListResult();
            foreach (var kv in BridgeDispatcher.CommandMap.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                result.commands.Add(new CommandInfo { name = kv.Key, description = kv.Value.Description });
            }
            result.count = result.commands.Count;
            return result;
        }

        [BridgeCommand("bridge.version", "返回桥接层版本号与命令统计，用于确认 Unity 侧代码是否为最新")]
        public static object Version(BridgeContext ctx, BridgeArgs args)
        {
            return BridgeInfo.GetVersionInfo();
        }
    }
}
#endif // UNITY_EDITOR
