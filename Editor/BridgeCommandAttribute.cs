using System;

namespace UnityPythonBridge
{
    /// <summary>
    /// 标记一个静态方法为可被 Python 调用的 Bridge 命令。
    /// 方法签名必须是:
    ///   public static object MethodName(BridgeContext ctx, BridgeArgs args)
    /// 命令在启动时通过反射自动扫描注册，无需手动登记。
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class BridgeCommandAttribute : Attribute
    {
        public string Name { get; }
        public string Description { get; }

        public BridgeCommandAttribute(string name, string description = "")
        {
            Name = name;
            Description = description;
        }
    }
}
