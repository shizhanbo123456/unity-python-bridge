#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityPythonBridge;

namespace UnityPythonBridge.Commands
{
    /// <summary>prefab.edit / prefab.remove / prefab.instantiate 的返回结构。</summary>
    [System.Serializable]
    public class ObjectEditResult
    {
        public string operation;
        public string path;
        public string message;
        public bool valid;
        public string[] errors;
        public string json;
    }

    /// <summary>
    /// Prefab 资产内部编辑命令（不进入场景，直接改资产并保存）：
    ///   - prefab.edit         编辑 Prefab 资产内部某个物体的 Transform 常用字段
    ///   - prefab.remove       从 Prefab 资产内部删除某个物体
    ///   - prefab.instantiate  在 Prefab 资产内部实例化另一个 Prefab 为子物体
    ///
    /// 复用 bridge 现有 BridgeArgs 字段（path/output/target/position/rotation/scale/move/rotate/zoom/quaternion/name），
    /// 不修改 unity-python-bridge 仓库的其他文件；删除本文件即完整移除这三条命令。
    /// target 指 Prefab 内部子物体路径（'/' 分隔，空=根节点），与 gameobject.* 的场景物体解析相互独立。
    /// </summary>
    public static class PrefabEditCommands
    {
        // ---------- prefab.edit ----------

        /// <summary>
        /// 编辑 Prefab 资产内部物体（不进入场景，直接改资产并保存）。
        /// 参数:
        ///   path   = Prefab 资产路径（必填）
        ///   target = 内部物体层级路径（可选，空 = 根节点）
        ///   position/rotation/scale/move/rotate/zoom/quaternion 同 gameobject.set 语义
        /// 返回编辑后该物体的层级路径与状态。
        /// </summary>
        [BridgeCommand("prefab.edit",
            "编辑 Prefab 资产内部物体的 Transform（直接保存资产）。参数: path=Prefab资产路径(必填), target=内部路径(可选,空=根), position/rotation/scale/move/rotate/zoom/quaternion")]
        public static object Edit(BridgeContext ctx, BridgeArgs args)
        {
            string prefabPath = NormalizeAssetPath(args.path);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null) throw new ArgumentException("找不到 Prefab: " + prefabPath);

            GameObject contentsRoot = null;
            try
            {
                contentsRoot = PrefabUtility.LoadPrefabContents(prefabPath);
                Transform target = ResolvePrefabChild(contentsRoot.transform, args.target);
                ApplyTransform(target, args);
                PrefabUtility.SaveAsPrefabAsset(contentsRoot, prefabPath);
                return Result("prefab.edit", BuildChildPath(contentsRoot.transform, target),
                    "edited", null);
            }
            finally
            {
                if (contentsRoot != null)
                    PrefabUtility.UnloadPrefabContents(contentsRoot);
            }
        }

        // ---------- prefab.remove ----------

        /// <summary>从 Prefab 资产内部删除物体（直接保存资产）。参数: path=Prefab资产路径(必填), target=内部路径(必填)。</summary>
        [BridgeCommand("prefab.remove",
            "从 Prefab 资产内部删除物体（直接保存资产）。参数: path=Prefab资产路径(必填), target=内部路径(必填)")]
        public static object Remove(BridgeContext ctx, BridgeArgs args)
        {
            string prefabPath = NormalizeAssetPath(args.path);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null) throw new ArgumentException("找不到 Prefab: " + prefabPath);
            if (string.IsNullOrWhiteSpace(args.target))
                throw new ArgumentException("prefab.remove 需要参数 target（内部物体路径）");

            GameObject contentsRoot = null;
            try
            {
                contentsRoot = PrefabUtility.LoadPrefabContents(prefabPath);
                Transform target = ResolvePrefabChild(contentsRoot.transform, args.target);
                if (target == contentsRoot.transform)
                    throw new InvalidOperationException("不能删除 Prefab 根节点本身");
                string removedPath = BuildChildPath(contentsRoot.transform, target);
                UnityEngine.Object.DestroyImmediate(target.gameObject);
                PrefabUtility.SaveAsPrefabAsset(contentsRoot, prefabPath);
                return Result("prefab.remove", removedPath, "removed", null);
            }
            finally
            {
                if (contentsRoot != null)
                    PrefabUtility.UnloadPrefabContents(contentsRoot);
            }
        }

        // ---------- prefab.instantiate ----------

        /// <summary>
        /// 在 Prefab 资产内部实例化另一个 Prefab 为子物体（直接保存资产）。
        /// 参数:
        ///   path   = 目标 Prefab 资产路径（必填，被编辑的）
        ///   output = 要实例化进来的子 Prefab 资产路径（必填）
        ///   target = 目标 Prefab 内部的父路径（可选，空 = 根）
        ///   position/rotation/scale = 子物体初始变换（可选）
        /// </summary>
        [BridgeCommand("prefab.instantiate",
            "在 Prefab 资产内部实例化另一个 Prefab 为子物体（直接保存资产）。参数: path=目标Prefab资产路径(必填), output=子Prefab资产路径(必填), target=内部父路径(可选), position/rotation/scale(可选)")]
        public static object Instantiate(BridgeContext ctx, BridgeArgs args)
        {
            string prefabPath = NormalizeAssetPath(args.path);
            string childPath = NormalizeAssetPath(args.output);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null) throw new ArgumentException("找不到目标 Prefab: " + prefabPath);
            var childPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(childPath);
            if (childPrefab == null) throw new ArgumentException("找不到子 Prefab: " + childPath);

            GameObject contentsRoot = null;
            try
            {
                contentsRoot = PrefabUtility.LoadPrefabContents(prefabPath);
                Transform parent = ResolvePrefabChild(contentsRoot.transform, args.target);
                var instance = PrefabUtility.InstantiatePrefab(childPrefab, parent) as GameObject;
                if (instance == null) throw new InvalidOperationException("Prefab 内部实例化失败: " + childPath);
                instance.name = childPrefab.name;
                ApplyTransform(instance.transform, args);
                PrefabUtility.SaveAsPrefabAsset(contentsRoot, prefabPath);
                return Result("prefab.instantiate", BuildChildPath(contentsRoot.transform, instance.transform),
                    "instantiated " + childPath, null);
            }
            finally
            {
                if (contentsRoot != null)
                    PrefabUtility.UnloadPrefabContents(contentsRoot);
            }
        }

        // ---------- 内部工具 ----------

        private static void ApplyTransform(Transform tf, BridgeArgs args)
        {
            if (args.position != null)
            {
                if (args.position.Length != 3) throw new ArgumentException("position 必须是 3 个分量 {x,y,z}");
                tf.position = new Vector3(args.position[0], args.position[1], args.position[2]);
            }
            if (args.rotation != null)
            {
                if (args.quaternion)
                {
                    if (args.rotation.Length != 4) throw new ArgumentException("quaternion=true 时 rotation 必须是 4 个分量");
                    tf.rotation = new Quaternion(args.rotation[0], args.rotation[1], args.rotation[2], args.rotation[3]);
                }
                else
                {
                    if (args.rotation.Length != 3) throw new ArgumentException("rotation 必须是 3 个分量 {x,y,z}（欧拉角）");
                    tf.rotation = Quaternion.Euler(args.rotation[0], args.rotation[1], args.rotation[2]);
                }
            }
            if (args.scale != null)
            {
                if (args.scale.Length != 3) throw new ArgumentException("scale 必须是 3 个分量 {x,y,z}");
                tf.localScale = new Vector3(args.scale[0], args.scale[1], args.scale[2]);
            }
            if (args.move != null)
            {
                if (args.move.Length != 3) throw new ArgumentException("move 必须是 3 个分量 {x,y,z}");
                tf.position += new Vector3(args.move[0], args.move[1], args.move[2]);
            }
            if (args.rotate != null)
            {
                if (args.quaternion)
                {
                    if (args.rotate.Length != 4) throw new ArgumentException("quaternion=true 时 rotate 必须是 4 个分量");
                    tf.rotation = tf.rotation * new Quaternion(args.rotate[0], args.rotate[1], args.rotate[2], args.rotate[3]);
                }
                else
                {
                    if (args.rotate.Length != 3) throw new ArgumentException("rotate 必须是 3 个分量 {x,y,z}");
                    tf.rotation = Quaternion.Euler(tf.eulerAngles +
                        new Vector3(args.rotate[0], args.rotate[1], args.rotate[2]));
                }
            }
            if (args.zoom != null)
            {
                if (args.zoom.Length != 3) throw new ArgumentException("zoom 必须是 3 个分量 {x,y,z}");
                var s = tf.localScale;
                tf.localScale = new Vector3(s.x * args.zoom[0], s.y * args.zoom[1], s.z * args.zoom[2]);
            }
        }

        /// <summary>解析 Prefab 资产内部子物体：空/根 = 根节点；否则按 '/' 分隔的子路径查找，支持使用子路径。</summary>
        private static Transform ResolvePrefabChild(Transform root, string target)
        {
            if (string.IsNullOrWhiteSpace(target)) return root;
            target = target.Trim().Trim('/');
            if (target.Length == 0) return root;
            var segs = target.Split('/');
            Transform current = root;
            foreach (string seg in segs)
            {
                if (seg.Length == 0) continue;
                Transform next = FindDirectChild(current, seg);
                if (next == null) throw new InvalidOperationException(
                    $"Prefab 内部未找到路径 '{target}'（在 '{current.name}' 下找不到子物体 '{seg}'）");
                current = next;
            }
            return current;
        }

        private static Transform FindDirectChild(Transform parent, string name)
        {
            for (int i = 0; i < parent.childCount; i++)
                if (parent.GetChild(i).name == name) return parent.GetChild(i);
            return null;
        }

        /// <summary>构建从 Prefab 根到目标的相对路径 "Child/SubChild"（根节点返回 ""）。</summary>
        private static string BuildChildPath(Transform root, Transform target)
        {
            if (target == root) return "";
            var names = new List<string>();
            var cur = target;
            while (cur != null && cur != root)
            {
                names.Add(cur.name);
                cur = cur.parent;
            }
            names.Reverse();
            return string.Join("/", names);
        }

        private static string NormalizeAssetPath(string path)
        {
            return (path ?? "").Replace('\\', '/');
        }

        private static ObjectEditResult Result(string operation, string path, string message, string[] errors)
        {
            return new ObjectEditResult
            {
                operation = operation,
                path = path,
                message = message,
                valid = errors == null || errors.Length == 0,
                errors = errors ?? Array.Empty<string>(),
            };
        }
    }
}
#endif
