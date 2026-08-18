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
    /// 请求参数（强类型，JsonUtility 反序列化；未提供的字段保持默认值）。
    /// 新增命令若需要新参数，在此追加字段即可（字段名即 JSON 键名，建议小驼峰）。
    /// </summary>
    [Serializable]
    public class BridgeArgs
    {
        // ---- scene.tree ----
        public bool components;
        /// <summary>scene.tree：遍历深度（根算第 1 层，默认 1 只显示起点本身）。</summary>
        public int depth;

        // ---- debug.log / debug.log_warning / debug.log_error / debug.get_logs ----
        // 注意：count 复用下方 terrain 段的同名字段（均表示"数量"），勿重复声明
        public string message;

        // ---- mesh.bounds / prefab.screenshot / scene.tree(起点) ----
        // 注意：path 被多命令复用——mesh.bounds/prefab.screenshot 解释为 Assets 资产路径，
        // scene.tree 解释为扫描起点（层级路径或唯一名称），各自命令自行解释，勿重复声明
        public string path;
        public string output;
        public Vector3 offset;
        /// <summary>prefab.screenshot：相机世界坐标（float[] 3）；relative=true 时解释为相对预制体位置。缺省回退 offset。</summary>
        public float[] cameraPosition;
        /// <summary>prefab.screenshot：观察目标世界坐标（float[] 3）；relative=true 时解释为相对预制体位置。缺省为预制体位置。</summary>
        public float[] lookAt;
        /// <summary>prefab.screenshot：cameraPosition/lookAt 是否按相对预制体位置解释（默认 false=世界坐标）。</summary>
        public bool relative;
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
        public int count;           // 数量：terrain.set_details/add_trees 的随机数量；debug.get_logs 的返回条数
        public int seed;
        public int density;
        public float minScale;
        public float maxScale;
        public float[] positions;   // 树木位置列表，每 3 个一组 {x, y, z}（归一化 0~1）
        public bool random;

        // ---- terrain stash（stash / apply_stash / stash_delete / stash_list）----
        /// <summary>stash 类型："trees" / "details" / "all"（省略时默认 "all"）。</summary>
        public string type;
        /// <summary>stash 名称（不含扩展名，如 "forest_v1"）。同名保存会报错，不允许覆盖。</summary>
        public string name;

        // ---- view.camera（抓取指定相机）----
        /// <summary>相机 GameObject 名称；省略时依次找 MainCamera / 名为 Main Camera 的 / 第一个相机。</summary>
        public string camera;

        // ---- gameobject.get / gameobject.set ----
        /// <summary>目标物体：层级路径（如 "Player/Body"）优先，单个名称兼容（重名时报错）。</summary>
        public string target;
        /// <summary>rotation 是否用四元数表示（默认 false=欧拉角）。get 输出与 set 输入一致。</summary>
        public bool quaternion;
        /// <summary>gameobject.set：active 目标值，-1=不修改，0=SetActive(false)，1=SetActive(true)。</summary>
        public int active;
        /// <summary>gameobject.set：position 世界坐标（float[] 3），null=不修改。</summary>
        public float[] position;
        /// <summary>gameobject.set：rotation，默认欧拉角（float[] 3）；quaternion=true 时为四元数（float[] 4），null=不修改。</summary>
        public float[] rotation;
        /// <summary>gameobject.set：localScale（float[] 3），null=不修改。</summary>
        public float[] scale;

        // ---- gameobject.set 相对操作（基于当前值）----
        /// <summary>gameobject.set：相对位移（float[] 3），position += move，null=不修改。</summary>
        public float[] move;
        /// <summary>gameobject.set：相对旋转（默认欧拉角 float[] 3，各分量相加；quaternion=true 时四元数 float[] 4，与当前四元数相乘），null=不修改。</summary>
        public float[] rotate;
        /// <summary>gameobject.set：相对缩放（float[] 3），localScale 各分量相乘（如 "2,1,1" = x 轴放大 2 倍），null=不修改。</summary>
        public float[] zoom;
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
#endif // UNITY_EDITOR
