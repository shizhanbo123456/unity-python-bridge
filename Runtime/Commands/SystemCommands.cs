#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;

namespace UnityPythonBridge.Commands
{
    /// <summary>bridge.ping 返回结构。</summary>
    [System.Serializable]
    public class PingResult
    {
        public bool pong;
        public string time;
    }

    /// <summary>bridge.reload 返回结构。</summary>
    [System.Serializable]
    public class ReloadResult
    {
        public bool requested;
        public string message;
    }

    /// <summary>bridge.echo 返回结构。</summary>
    [System.Serializable]
    public class EchoResult
    {
        public string message;
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

        [BridgeCommand("bridge.echo", "回显传入的 message 参数与服务器时间（用于验证新命令是否已生效）。参数: message(string)")]
        public static object Echo(BridgeContext ctx, BridgeArgs args)
        {
            return new EchoResult
            {
                message = args.message ?? "",
                time = DateTime.UtcNow.ToString("o")
            };
        }

        [BridgeCommand("bridge.reload",
            "触发 Unity 脚本重编译（domain reload），编译完成后服务器自动恢复（依赖 BridgeManager 状态持久化）。" +
            "参数: 无。客户端应轮询 bridge.version 等待恢复")]
        public static object Reload(BridgeContext ctx, BridgeArgs args)
        {
            // 确保重编译后自动恢复：先写状态"运行中"
            BridgeStateStore.Save(true);
            // 延迟一帧触发重编译，保证本次响应先发回客户端（否则响应会随旧域一起丢失）
            EditorApplication.delayCall += CompilationPipeline.RequestScriptCompilation;

            return new ReloadResult
            {
                requested = true,
                message = "重编译已触发，服务器将在编译完成后自动恢复（客户端请轮询 bridge.version）"
            };
        }
    }
}
#endif // UNITY_EDITOR
