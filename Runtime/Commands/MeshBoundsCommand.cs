#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

namespace UnityPythonBridge.Commands
{
    /// <summary>mesh.bounds 返回结构（min/max/center/size 为 Vector3，序列化为 {x,y,z}）。</summary>
    [System.Serializable]
    public class MeshBoundsResult
    {
        public string path;
        public string resolvedPath;
        public string type;
        public Vector3 min;
        public Vector3 max;
        public Vector3 center;
        public Vector3 size;
        public string format;
    }

    /// <summary>
    /// 包围盒命令：计算 Assets 中网格 / 模型 / 预制体的轴对齐包围盒（AABB）。
    /// 参数:
    ///   path (string) - 目标在 Assets 中的相对路径（可带或不带 "Assets/" 前缀），
    ///                    支持 .mesh（网格）、.fbx/.obj/.blend 等（模型）、.prefab（预制体）。
    ///   placed (bool) - 仅对 prefab 有效：true=保持 prefab 资产原有旋转计算 AABB
    ///                    （与 prefab.screenshot 的视觉姿态一致），false（默认）=根旋转
    ///                    重置为 identity，得到建模原始姿态的包围盒。
    /// 返回结构:
    ///   { path, resolvedPath, type, min{x,y,z}, max{x,y,z}, center{x,y,z}, size{x,y,z}, format }
    /// 说明:
    ///   - 预制体/模型：实例化到原点（位置归零；placed=false 时根旋转/缩放重置为
    ///     identity/one，placed=true 时保持根旋转），合并其下所有 MeshRenderer 与
    ///     SkinnedMeshRenderer 的包围盒。
    ///   - 网格（.mesh）：取 mesh.bounds（物体局部空间）。
    ///   - 含多个网格时，返回能包围所有网格的合并包围盒。
    ///   - 坐标格式形如 "x:-2~6, y:-0.5~2, z:1~6"（见 format 字段）。
    /// </summary>
    public static class MeshBoundsCommand
    {
        [BridgeCommand("mesh.bounds",
            "计算 Assets 中网格/模型/预制体的轴对齐包围盒。参数: path(string, Assets 相对路径), placed(bool, 默认false; true=保持prefab原有旋转)")]
        public static object Bounds(BridgeContext ctx, BridgeArgs args)
        {
            var path = args.path;
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("mesh.bounds 需要参数 path（Assets 中的相对路径）");

            var assetPath = Normalize(path);
            var ext = System.IO.Path.GetExtension(assetPath).ToLowerInvariant();

            // 1) 先尝试当作 GameObject（预制体 / 模型）
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (go != null)
            {
                var type = ext == ".prefab" ? "prefab" : "model";
                var bounds = ComputeGameObjectBounds(go, args.placed);
                return BuildResult(path, assetPath, type, bounds);
            }

            // 2) 再尝试当作 Mesh
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
            if (mesh != null)
            {
                return BuildResult(path, assetPath, "mesh", mesh.bounds);
            }

            throw new InvalidOperationException(
                $"找不到资源或类型不支持: {assetPath}（需为 .mesh / 模型文件 / .prefab，且位于 Assets 目录下）");
        }

        private static string Normalize(string path)
        {
            var p = path.Replace('\\', '/').Trim();
            if (!p.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                p = "Assets/" + p.TrimStart('/');
            return p;
        }

        /// <summary>
        /// 实例化预制体/模型到原点，合并其下所有网格渲染器的世界包围盒。
        /// 位置归零；keepRotation=false 时根旋转重置为 identity（建模原始姿态），
        /// true 时保持 prefab 资产原有旋转（与截图/实际摆放的视觉姿态一致）；
        /// 缩放统一为 1（只看几何本身）。
        /// </summary>
        private static Bounds ComputeGameObjectBounds(GameObject prefab, bool keepRotation)
        {
            var instance = GameObject.Instantiate(prefab);
            instance.transform.position = Vector3.zero;
            if (!keepRotation)
                instance.transform.rotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one;

            Bounds? combined = null;
            foreach (var mr in instance.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (combined == null) combined = mr.bounds;
                else combined.Value.Encapsulate(mr.bounds);
            }
            foreach (var smr in instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (combined == null) combined = smr.bounds;
                else combined.Value.Encapsulate(smr.bounds);
            }

            UnityEngine.Object.DestroyImmediate(instance);

            if (combined == null)
                throw new InvalidOperationException(
                    "目标未包含任何 MeshRenderer / SkinnedMeshRenderer，无法计算包围盒");

            return combined.Value;
        }

        private static object BuildResult(string inputPath, string resolvedPath, string type, Bounds b)
        {
            var min = b.min;
            var max = b.max;
            return new MeshBoundsResult
            {
                path = inputPath,
                resolvedPath = resolvedPath,
                type = type,
                min = min,
                max = max,
                center = b.center,
                size = b.size,
                format = FormatBounds(min, max),
            };
        }

        private static string FormatBounds(Vector3 min, Vector3 max)
        {
            string F(float v) => v.ToString("g");
            return $"x:{F(min.x)}~{F(max.x)}, y:{F(min.y)}~{F(max.y)}, z:{F(min.z)}~{F(max.z)}";
        }
    }
}
#endif // UNITY_EDITOR
