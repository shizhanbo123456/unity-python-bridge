#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityPythonBridge.Commands
{
    [Serializable]
    public class PrefabBoundsResult
    {
        public string path;
        public string resolvedPath;
        public Vector3 min;
        public Vector3 max;
        public Vector3 center;
        public Vector3 size;
        public string format;
    }

    [Serializable]
    public class PrefabBillboardResult
    {
        public string path;
        public string resolvedPath;
        public string output;
        public string cameraType;
        public Vector3 cameraDirection;
        public Vector3 boundsCenter;
        public Vector3 boundsSize;
        public float projectedWidth;
        public float projectedHeight;
        public float pixelsPerMeter;
        public int width;
        public int height;
        public int bytes;
    }

    /// <summary>Prefab 完整变换包围盒与自动定尺寸的正交 billboard 截图。</summary>
    public static class PrefabBillboardCommand
    {
        private const int CaptureLayer = 31;

        [BridgeCommand("prefab.bounds",
            "计算预制体内所有网格应用完整层级位移/旋转/缩放后的世界 AABB。参数: path(string, Assets 相对路径，可省略 .prefab)")]
        public static object Bounds(BridgeContext ctx, BridgeArgs args)
        {
            GameObject asset;
            string resolved = ResolvePrefab(args.path, out asset);
            GameObject instance = null;
            try
            {
                instance = (GameObject)PrefabUtility.InstantiatePrefab(asset);
                var points = CollectMeshBoundsCorners(instance);
                var bounds = BoundsFromPoints(points);
                return new PrefabBoundsResult
                {
                    path = args.path,
                    resolvedPath = resolved,
                    min = bounds.min,
                    max = bounds.max,
                    center = bounds.center,
                    size = bounds.size,
                    format = FormatBounds(bounds.min, bounds.max),
                };
            }
            finally
            {
                if (instance != null) UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        [BridgeCommand("prefab.billboard",
            "按指定相机相对单位方向正交截取预制体，尺寸由投影 bounds 自动计算。参数: path(string), output(string,输出目录;相对路径基于 Assets), cameraPosition(float[]3,必填单位向量), pixelsPerMeter(float,默认100)")]
        public static object Billboard(BridgeContext ctx, BridgeArgs args)
        {
            if (string.IsNullOrWhiteSpace(args.output))
                throw new ArgumentException("prefab.billboard 需要参数 output（输出目录；相对路径基于 Assets）");
            if (args.cameraPosition == null || args.cameraPosition.Length != 3)
                throw new ArgumentException("prefab.billboard 需要 cameraPosition(float[]3) 单位向量");

            var direction = new Vector3(args.cameraPosition[0], args.cameraPosition[1], args.cameraPosition[2]);
            float magnitude = direction.magnitude;
            if (magnitude < 0.0001f || Mathf.Abs(magnitude - 1f) > 0.001f)
                throw new ArgumentException($"cameraPosition 必须是单位向量（当前长度 {magnitude:g}）");
            direction.Normalize();

            float ppm = args.pixelsPerMeter > 0f ? args.pixelsPerMeter : 100f;
            if (ppm > 8192f)
                throw new ArgumentException("pixelsPerMeter 过大（最大 8192）");

            GameObject asset;
            string resolved = ResolvePrefab(args.path, out asset);
            string outputDir = ResolveOutputDirectory(args.output);
            Directory.CreateDirectory(outputDir);
            string outputPath = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(resolved) + ".png");

            GameObject instance = null;
            GameObject cameraObject = null;
            RenderTexture rt = null;
            Texture2D texture = null;
            try
            {
                instance = (GameObject)PrefabUtility.InstantiatePrefab(asset);
                var originalPoints = CollectMeshBoundsCorners(instance);
                var originalBounds = BoundsFromPoints(originalPoints);

                // 保持资产的完整世界变换；临时切到独占渲染层，避免场景内容进入截图。
                SetLayerRecursively(instance, CaptureLayer);
                var points = originalPoints;
                Vector3 captureCenter = originalBounds.center;

                Vector3 forward = -direction; // cameraPosition 是“相机位于物体哪一侧”
                Vector3 upHint = Mathf.Abs(Vector3.Dot(forward, Vector3.up)) > 0.999f ? Vector3.forward : Vector3.up;
                Vector3 right = Vector3.Normalize(Vector3.Cross(upHint, forward));
                Vector3 up = Vector3.Normalize(Vector3.Cross(forward, right));

                float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
                float minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
                float minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity;
                foreach (var point in points)
                {
                    Vector3 delta = point - captureCenter;
                    float x = Vector3.Dot(delta, right);
                    float y = Vector3.Dot(delta, up);
                    float z = Vector3.Dot(delta, direction);
                    minX = Mathf.Min(minX, x); maxX = Mathf.Max(maxX, x);
                    minY = Mathf.Min(minY, y); maxY = Mathf.Max(maxY, y);
                    minZ = Mathf.Min(minZ, z); maxZ = Mathf.Max(maxZ, z);
                }

                float projectedWidth = Mathf.Max(maxX - minX, 0.0001f);
                float projectedHeight = Mathf.Max(maxY - minY, 0.0001f);
                int width = Mathf.Max(1, Mathf.CeilToInt(projectedWidth * ppm));
                int height = Mathf.Max(1, Mathf.CeilToInt(projectedHeight * ppm));
                if (width > 16384 || height > 16384)
                    throw new InvalidOperationException($"输出尺寸 {width}x{height} 超过 16384；请降低 pixelsPerMeter");

                Vector3 projectedCenter = captureCenter + right * ((minX + maxX) * 0.5f) + up * ((minY + maxY) * 0.5f);
                float distance = Mathf.Max(1f, maxZ - minZ + 1f);
                Vector3 cameraPosition = projectedCenter + direction * (maxZ + distance);

                cameraObject = new GameObject("BridgeBillboardCamera");
                var camera = cameraObject.AddComponent<Camera>();
                cameraObject.transform.position = cameraPosition;
                cameraObject.transform.rotation = Quaternion.LookRotation(forward, up);
                camera.orthographic = true;
                camera.orthographicSize = projectedHeight * 0.5f;
                camera.aspect = projectedWidth / projectedHeight;
                camera.clearFlags = CameraClearFlags.Color;
                camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
                camera.cullingMask = 1 << CaptureLayer;
                camera.nearClipPlane = 0.01f;
                camera.farClipPlane = distance * 2f + (maxZ - minZ) + 1f;

                rt = RenderTexture.GetTemporary(width, height, 24,
                    RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
                camera.targetTexture = rt;
                camera.Render();
                RenderTexture.active = rt;
                texture = new Texture2D(width, height, TextureFormat.ARGB32, false);
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();
                RenderTexture.active = null;
                camera.targetTexture = null;

                byte[] png = texture.EncodeToPNG();
                File.WriteAllBytes(outputPath, png);
                return new PrefabBillboardResult
                {
                    path = args.path,
                    resolvedPath = resolved,
                    output = Path.GetFullPath(outputPath),
                    cameraType = "orthographic",
                    cameraDirection = direction,
                    boundsCenter = originalBounds.center,
                    boundsSize = originalBounds.size,
                    projectedWidth = projectedWidth,
                    projectedHeight = projectedHeight,
                    pixelsPerMeter = ppm,
                    width = width,
                    height = height,
                    bytes = png.Length,
                };
            }
            finally
            {
                RenderTexture.active = null;
                if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                if (cameraObject != null) UnityEngine.Object.DestroyImmediate(cameraObject);
                if (instance != null) UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        private static string ResolvePrefab(string input, out GameObject asset)
        {
            if (string.IsNullOrWhiteSpace(input))
                throw new ArgumentException("需要参数 path（Assets 中的预制体路径）");
            string path = input.Replace('\\', '/').Trim();
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                path = "Assets/" + path.TrimStart('/');
            if (string.IsNullOrEmpty(Path.GetExtension(path))) path += ".prefab";
            if (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("path 必须指向 .prefab 资源");
            asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (asset == null) throw new InvalidOperationException("找不到预制体: " + path);
            return path;
        }

        private static string ResolveOutputDirectory(string output)
        {
            string normalized = output.Replace('\\', '/').Trim().TrimEnd('/');
            if (string.IsNullOrEmpty(normalized)) throw new ArgumentException("output 目录不能为空");
            if (Path.IsPathRooted(normalized)) return Path.GetFullPath(normalized);
            if (normalized.Equals("Assets", StringComparison.OrdinalIgnoreCase)) return Application.dataPath;
            if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring("Assets/".Length);
            return Path.GetFullPath(Path.Combine(Application.dataPath, normalized));
        }

        private static List<Vector3> CollectMeshBoundsCorners(GameObject root)
        {
            var points = new List<Vector3>();
            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
                if (filter.sharedMesh != null) AddCorners(points, filter.sharedMesh.bounds, filter.transform.localToWorldMatrix);
            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                AddCorners(points, renderer.localBounds, renderer.transform.localToWorldMatrix);
            if (points.Count == 0)
                throw new InvalidOperationException("预制体未包含 MeshFilter 或 SkinnedMeshRenderer 网格");
            return points;
        }

        private static void AddCorners(List<Vector3> points, Bounds bounds, Matrix4x4 matrix)
        {
            Vector3 min = bounds.min, max = bounds.max;
            for (int x = 0; x < 2; x++)
                for (int y = 0; y < 2; y++)
                    for (int z = 0; z < 2; z++)
                        points.Add(matrix.MultiplyPoint3x4(new Vector3(x == 0 ? min.x : max.x,
                            y == 0 ? min.y : max.y, z == 0 ? min.z : max.z)));
        }

        private static void SetLayerRecursively(GameObject root, int layer)
        {
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                transform.gameObject.layer = layer;
        }

        private static Bounds BoundsFromPoints(List<Vector3> points)
        {
            var result = new Bounds(points[0], Vector3.zero);
            for (int i = 1; i < points.Count; i++) result.Encapsulate(points[i]);
            return result;
        }

        private static string FormatBounds(Vector3 min, Vector3 max)
        {
            return $"x:{min.x:g}~{max.x:g}, y:{min.y:g}~{max.y:g}, z:{min.z:g}~{max.z:g}";
        }
    }
}
#endif // UNITY_EDITOR
