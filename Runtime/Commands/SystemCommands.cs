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
            var list = new List<CommandInfo>();
            foreach (var kv in BridgeDispatcher.CommandMap.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                list.Add(new CommandInfo { name = kv.Key, description = kv.Value.Description });
            }
            return list;
        }
    }
}
#endif // UNITY_EDITOR
