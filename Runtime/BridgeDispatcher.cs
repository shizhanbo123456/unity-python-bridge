#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace UnityPythonBridge
{
    /// <summary>
    /// 命令分发器。启动时用反射扫描所有程序集中带 [BridgeCommand] 特性的
    /// 静态方法并自动注册 —— 新增命令只需写一个类，无需改动任何现有代码。
    /// </summary>
    public static class BridgeDispatcher
    {
        private static readonly Dictionary<string, BridgeCommandInfo> Commands =
            new Dictionary<string, BridgeCommandInfo>(StringComparer.Ordinal);

        static BridgeDispatcher()
        {
            RegisterByReflection();
        }

        private static void RegisterByReflection()
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var assembly in assemblies)
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    // 部分程序集（如插件）可能无法完整加载，跳过不可解析的类型
                    types = e.Types.Where(t => t != null).ToArray();
                }

                foreach (var type in types)
                {
                    foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        var attr = method.GetCustomAttribute<BridgeCommandAttribute>();
                        if (attr == null) continue;

                        // 校验签名：object Name(BridgeContext, BridgeArgs)
                        var pars = method.GetParameters();
                        if (pars.Length != 2 ||
                            pars[0].ParameterType != typeof(BridgeContext) ||
                            pars[1].ParameterType != typeof(BridgeArgs))
                        {
                            UnityEngine.Debug.LogWarning(
                                $"[UnityPythonBridge] 忽略命令方法 {type.Name}.{method.Name}：签名必须为 (BridgeContext, BridgeArgs)");
                            continue;
                        }

                        var handler = (BridgeCommandHandler)method.CreateDelegate(typeof(BridgeCommandHandler));
                        Commands[attr.Name] = new BridgeCommandInfo(attr.Name, attr.Description, handler);
                    }
                }
            }
        }

        /// <summary>执行命令（必须在主线程调用）。</summary>
        public static object Execute(string command, BridgeArgs args)
        {
            if (string.IsNullOrEmpty(command) || !Commands.TryGetValue(command, out var info))
                throw new KeyNotFoundException($"未知命令: {command}（可用 bridge.list_commands 查看全部命令）");

            return info.Handler(new BridgeContext(), args ?? new BridgeArgs());
        }

        public static IReadOnlyCollection<BridgeCommandInfo> AllCommands => Commands.Values;

        public static IReadOnlyDictionary<string, BridgeCommandInfo> CommandMap => Commands;
    }
}
#endif // UNITY_EDITOR
