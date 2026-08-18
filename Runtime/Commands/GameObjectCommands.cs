#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityPythonBridge.Commands
{
    /// <summary>gameobject.get / gameobject.set 返回结构。</summary>
    [System.Serializable]
    public class GameObjectStateResult
    {
        public string target;          // 用户传入的目标（名称或路径）
        public string resolvedPath;    // 解析后的唯一层级路径 "Root/Child/..."
        public bool active;            // activeSelf（自身开关）
        public bool activeInHierarchy; // 是否在层级中实际激活（含父级影响）
        public Vector3 position;       // 世界坐标
        public Vector3 rotationEuler;  // 世界欧拉角
        public bool quaternion;        // 是否包含 rotationQuat（quaternion=true 时）
        public Quaternion rotationQuat; // 世界四元数（仅 quaternion=true 时有效）
        public Vector3 scale;          // localScale（世界缩放只读，故用本地缩放）
    }

    /// <summary>
    /// 常规 GameObject 操作命令：读写 active 状态与 Transform 的 position/rotation/scale。
    ///
    /// 定位规则（target）：
    ///   1. 优先按层级路径（如 "Player/Body/LeftArm"，'/' 分隔，从场景根开始）——同父下名称重复也会被严格区分；
    ///   2. 单个名称时兼容：场景中唯一则直接命中；多个同名报错并提示用路径。
    ///
    /// 坐标约定：
    ///   position    - 世界坐标
    ///   rotation    - 默认世界欧拉角；quaternion=true 时输出/输入世界四元数（x,y,z,w）
    ///   scale       - localScale（世界缩放只读，写入只能走本地缩放）
    ///
    /// gameobject.get:
    ///   参数: target(string,必填), quaternion(bool,可选)
    ///   返回: 上述全部状态。
    /// gameobject.set:
    ///   参数: target(string,必填), active(int,可选 -1=不改 0/1), position(float[]3,可选),
    ///         rotation(float[]3欧拉 或 4四元数,可选), scale(float[]3,可选), quaternion(bool,可选)
    ///   返回: 设置后的完整状态（同 get）。改动可通过 Unity Undo 撤销。
    /// </summary>
    public static class GameObjectCommands
    {
        // ---------- gameobject.get ----------

        [BridgeCommand("gameobject.get",
            "读取 GameObject 的 active 状态与 Transform 的 position/rotation/scale。参数: target(string,必填,路径优先名称兼容), " +
            "quaternion(bool,可选,默认false输出欧拉角,true输出四元数)")]
        public static object Get(BridgeContext ctx, BridgeArgs args)
        {
            var go = ResolveTarget(args.target);
            return BuildState(args.target, go, args.quaternion);
        }

        // ---------- gameobject.set ----------

        [BridgeCommand("gameobject.set",
            "写入 GameObject 的 active 状态与 Transform 的 position/rotation/scale（支持 Undo）。参数: target(string,必填), " +
            "active(int,可选 -1=不改 0=隐藏 1=激活), position(float[]3,可选,世界), " +
            "rotation(float[]3欧拉或4四元数,可选,quaternion=true时按四元数), scale(float[]3,可选,localScale), " +
            "move(float[]3,可选,相对位移 position+=), rotate(float[]3欧拉各分量加或4四元数相乘,可选,quaternion=true时按四元数), " +
            "zoom(float[]3,可选,相对缩放 localScale 各分量相乘)")]
        public static object Set(BridgeContext ctx, BridgeArgs args)
        {
            var go = ResolveTarget(args.target);
            var tf = go.transform;

            Undo.RecordObject(go, "UnityBridge gameobject.set active");
            Undo.RecordObject(tf, "UnityBridge gameobject.set transform");

            if (args.active != -1)
            {
                if (args.active != 0 && args.active != 1)
                    throw new ArgumentException($"active 必须是 -1(不改)/0(隐藏)/1(激活)，当前: {args.active}");
                go.SetActive(args.active == 1);
            }

            if (args.position != null)
            {
                if (args.position.Length != 3)
                    throw new ArgumentException("position 必须是 3 个分量 {x,y,z}");
                tf.position = new Vector3(args.position[0], args.position[1], args.position[2]);
            }

            if (args.rotation != null)
            {
                if (args.quaternion)
                {
                    if (args.rotation.Length != 4)
                        throw new ArgumentException("quaternion=true 时 rotation 必须是 4 个分量 {x,y,z,w}");
                    tf.rotation = new Quaternion(args.rotation[0], args.rotation[1], args.rotation[2], args.rotation[3]);
                }
                else
                {
                    if (args.rotation.Length != 3)
                        throw new ArgumentException("quaternion=false 时 rotation 必须是 3 个分量 {x,y,z}（欧拉角）");
                    tf.rotation = Quaternion.Euler(args.rotation[0], args.rotation[1], args.rotation[2]);
                }
            }

            if (args.scale != null)
            {
                if (args.scale.Length != 3)
                    throw new ArgumentException("scale 必须是 3 个分量 {x,y,z}");
                tf.localScale = new Vector3(args.scale[0], args.scale[1], args.scale[2]);
            }

            // ---- 相对操作（基于当前值）----
            if (args.move != null)
            {
                if (args.move.Length != 3)
                    throw new ArgumentException("move 必须是 3 个分量 {x,y,z}");
                tf.position += new Vector3(args.move[0], args.move[1], args.move[2]);
            }

            if (args.rotate != null)
            {
                if (args.quaternion)
                {
                    if (args.rotate.Length != 4)
                        throw new ArgumentException("quaternion=true 时 rotate 必须是 4 个分量 {x,y,z,w}");
                    // 四元数相乘：在当前旋转基础上按输入四元数旋转
                    tf.rotation = tf.rotation * new Quaternion(args.rotate[0], args.rotate[1], args.rotate[2], args.rotate[3]);
                }
                else
                {
                    if (args.rotate.Length != 3)
                        throw new ArgumentException("rotate 必须是 3 个分量 {x,y,z}（欧拉角，各分量相加）");
                    // 欧拉角各分量直接相加
                    tf.rotation = Quaternion.Euler(tf.eulerAngles +
                        new Vector3(args.rotate[0], args.rotate[1], args.rotate[2]));
                }
            }

            if (args.zoom != null)
            {
                if (args.zoom.Length != 3)
                    throw new ArgumentException("zoom 必须是 3 个分量 {x,y,z}");
                var s = tf.localScale;
                tf.localScale = new Vector3(s.x * args.zoom[0], s.y * args.zoom[1], s.z * args.zoom[2]);
            }

            return BuildState(args.target, go, args.quaternion);
        }

        // ---------- 内部工具 ----------

        private static GameObjectStateResult BuildState(string target, GameObject go, bool withQuaternion)
        {
            return new GameObjectStateResult
            {
                target = target,
                resolvedPath = BuildPath(go.transform),
                active = go.activeSelf,
                activeInHierarchy = go.activeInHierarchy,
                position = go.transform.position,
                rotationEuler = go.transform.eulerAngles,
                quaternion = withQuaternion,
                rotationQuat = withQuaternion ? go.transform.rotation : Quaternion.identity,
                scale = go.transform.localScale,
            };
        }

        /// <summary>解析目标：路径优先，名称兼容（重名报错）。</summary>
        private static GameObject ResolveTarget(string target)
        {
            if (string.IsNullOrWhiteSpace(target))
                throw new ArgumentException("gameobject 命令需要参数 target（名称或层级路径，如 \"Player/Body\"）");
            target = target.Trim();

            if (target.IndexOf('/') >= 0)
                return ResolveByPath(target);
            return ResolveByName(target);
        }

        /// <summary>按层级路径解析（从场景根开始，'/' 分隔）。同父下重名会报错。</summary>
        private static GameObject ResolveByPath(string target)
        {
            var segs = target.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segs.Length == 0)
                throw new ArgumentException("路径无效: " + target);

            var matches = new List<GameObject>();
            var scene = SceneManager.GetActiveScene();
            foreach (var root in scene.GetRootGameObjects())
                MatchPath(root.transform, segs, 0, "", matches);

            if (matches.Count == 0)
                throw new InvalidOperationException($"场景中未找到路径 '{target}'");
            if (matches.Count > 1)
            {
                var sample = new List<string>();
                foreach (var m in matches) { var p = BuildPath(m.transform); if (sample.Count < 2) sample.Add(p); }
                throw new InvalidOperationException(
                    $"路径 '{target}' 匹配到 {matches.Count} 个物体（场景中存在重名父级），请使用更完整的路径（如 {string.Join(" / ", sample)}）");
            }
            return matches[0];
        }

        private static void MatchPath(Transform t, string[] segs, int depth, string prefix, List<GameObject> matches)
        {
            var path = prefix.Length == 0 ? t.name : prefix + "/" + t.name;
            if (t.name != segs[depth]) return;
            if (depth == segs.Length - 1)
            {
                matches.Add(t.gameObject);
                return;
            }
            for (int i = 0; i < t.childCount; i++)
                MatchPath(t.GetChild(i), segs, depth + 1, path, matches);
        }

        /// <summary>按名称解析：唯一命中；重名/不存在报错。</summary>
        private static GameObject ResolveByName(string target)
        {
            var matches = new List<GameObject>();
            var scene = SceneManager.GetActiveScene();
            foreach (var root in scene.GetRootGameObjects())
                CollectByName(root.transform, target, matches);

            if (matches.Count == 0)
                throw new InvalidOperationException($"场景中未找到名为 '{target}' 的物体");
            if (matches.Count > 1)
            {
                var sample = new List<string>();
                foreach (var m in matches) { var p = BuildPath(m.transform); if (sample.Count < 2) sample.Add(p); }
                throw new InvalidOperationException(
                    $"场景中有 {matches.Count} 个名为 '{target}' 的物体，请使用层级路径（如 {string.Join(" / ", sample)}）");
            }
            return matches[0];
        }

        private static void CollectByName(Transform t, string name, List<GameObject> matches)
        {
            if (t.name == name) matches.Add(t.gameObject);
            for (int i = 0; i < t.childCount; i++)
                CollectByName(t.GetChild(i), name, matches);
        }

        /// <summary>构建从场景根到目标的唯一路径 "Root/Child/..."。</summary>
        private static string BuildPath(Transform t)
        {
            var names = new List<string>();
            var cur = t;
            while (cur != null)
            {
                names.Add(cur.name);
                cur = cur.parent;
            }
            names.Reverse();
            return string.Join("/", names);
        }
    }
}
#endif // UNITY_EDITOR
