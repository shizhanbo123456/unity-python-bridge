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
    /// 请求参数（强类型，JsonUtility 反序列化；未提供的字段保持默认值）。
    /// 新增命令若需要新参数，在此追加字段即可（字段名即 JSON 键名，建议小驼峰）。
    /// </summary>
    [Serializable]
    public class BridgeArgs
    {
        // ---- scene.tree ----
        public bool components;

        // ---- mesh.bounds / prefab.screenshot ----
        public string path;
        public string output;
        public Vector3 offset;
        public bool orthographic;
        public float fov;
        public int width;
        public int height;
        public string bg;
        public float light;

        // ---- terrain 通用 ----
        /// <summary>目标 Terrain 的 GameObject 名称；省略时取场景中第一个 Terrain。</summary>
        public string terrain;

        // ---- terrain 区域（高度图/纹理/植被均用 xBase/zBase 起始 + width/height 范围）----
        public int xBase;
        public int zBase;

        // ---- 数组数据（高度/纹理权重/植被密度，行优先展平）----
        public float[] data;
        public int[] dataInt;

        // ---- terrain.set_heights 噪声生成 ----
        public bool noise;
        public float noiseScale;
        public int noiseSeed;
        public float baseHeight;
        public float heightScale;

        // ---- terrain 植被 / 树木 ----
        public int layer;
        public int prototypeIndex;  // 树原型索引（terrain.add_trees 使用）
        public int count;
        public int seed;
        public int density;
        public float minScale;
        public float maxScale;
        public float[] positions;   // 树木位置列表，每 3 个一组 {x, y, z}（归一化 0~1）
        public bool random;
    }

    /// <summary>TCP 请求行：{id, cmd, args}。</summary>
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
    /// <param name="args">请求参数（强类型 BridgeArgs，字段缺省为默认值）</param>
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
