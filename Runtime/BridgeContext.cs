#if UNITY_EDITOR
using System;
using UnityEngine;

namespace UnityPythonBridge
{
    /// <summary>
    /// 命令执行上下文。每个请求执行时创建一个。
    /// 预留字段：后续可注入日志、连接信息、全局配置等，避免修改命令签名。
    /// </summary>
    public class BridgeContext
    {
        // 预留：public TextWriter Log { get; set; }
        // 预留：public string ClientId { get; set; }
    }

    /// <summary>
    /// 命令参数（强类型）。由 JsonUtility 从请求 args 反序列化，
    /// 未提供的字段保持默认值（bool=false, 数值=0, string=null, Vector3=zero）。
    /// 新增命令需要新参数时，在此追加字段即可（注意保持 JSON 键名与字段名一致）。
    /// </summary>
    [Serializable]
    public class BridgeArgs
    {
        // scene.tree
        public bool components;

        // mesh.bounds / prefab.screenshot
        public string path;

        // prefab.screenshot
        public string output;
        public Vector3 offset;
        public bool orthographic;
        public float fov;
        public int width;
        public int height;
        public string bg;
        public float light;
    }

    /// <summary>
    /// TCP 请求行 { id, cmd, args } 的强类型定义。
    /// </summary>
    [Serializable]
    public class BridgeRequest
    {
        public int id;
        public string cmd;
        public BridgeArgs args;
    }

    /// <summary>
    /// 命令处理器委托。与 [BridgeCommand] 标记的方法签名一致。
    /// </summary>
    /// <param name="ctx">执行上下文</param>
    /// <param name="args">请求参数（强类型，可空）</param>
    /// <returns>任意可被 JsonUtility 序列化的结果（[Serializable] 类 / List / 基本类型）</returns>
    public delegate object BridgeCommandHandler(BridgeContext ctx, BridgeArgs args);

    /// <summary>已注册命令的元信息。</summary>
    public sealed class BridgeCommandInfo
    {
        public string Name { get; }
        public string Description { get; }
        public BridgeCommandHandler Handler { get; }

        public BridgeCommandInfo(string name, string description, BridgeCommandHandler handler)
        {
            Name = name;
            Description = description;
            Handler = handler;
        }
    }
}
#endif // UNITY_EDITOR
