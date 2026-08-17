#if UNITY_EDITOR
using System;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityPythonBridge.Commands
{
    /// <summary>prefab.screenshot 返回结构。</summary>
    [System.Serializable]
    public class ScreenshotResult
    {
        public string path;
        public string resolvedPath;
        public string output;
        public string cameraType;
        public int width;
        public int height;
        public Vector3 cameraPosition;
        public Vector3 lookAt;
        public float fillLight;
        public int bytes;
    }

    /// <summary>
    /// 预制体截图命令：把目标预制体复制到场景中的隔离位置，创建相机进行截图并保存为 PNG，
    /// 完成后销毁临时复制的预制体与创建的相机（不污染场景）。
    /// 复制出的预制体【旋转保持资产原有的】（不再强制 identity），缩放统一为 1。
    ///
    /// 参数（BridgeArgs）:
    ///   path (string)        - 目标预制体在 Assets 中的相对路径（.prefab 或模型文件）
    ///   offset (Vector3)     - 相机相对于预制体位置 (9999,9999,9999) 的偏移，{x,y,z}（cameraPosition 缺省时使用）
    ///   cameraPosition (float[]) - 相机位置 {x,y,z}；relative=false 为世界坐标，relative=true 为相对预制体位置
    ///   lookAt (float[])     - 观察目标 {x,y,z}；relative=false 为世界坐标，relative=true 为相对预制体位置；缺省为预制体位置
    ///   relative (bool)      - cameraPosition/lookAt 是否按相对预制体位置解释（默认 false=世界坐标）
    ///   output (string)      - PNG 输出路径（必须以 .png 结尾）
    ///   orthographic (bool)  - 是否使用正交相机，默认 false（透视）
    ///   fov (float)          - 视野：透视时=fieldOfView，正交时=orthographicSize；<=0 使用 Unity 默认
    ///   width (int)          - 输出图片宽，默认 1920
    ///   height (int)         - 输出图片高，默认 1080
    ///   bg (string)          - 背景色 "r,g,b[,a]"（0~1），默认透明
    ///   light (float)        - 补光强度，默认 0（不补光）；>0 时追加一盏与相机朝向一致的平行光
    ///
    /// 返回:
    ///   { path, resolvedPath, output, cameraType, width, height, cameraPosition{x,y,z}, lookAt{x,y,z}, fillLight, bytes }
    /// </summary>
    public static class PrefabScreenshotCommand
    {
        // 远离原点的隔离位置，避免与场景中已有物体重叠 / 碰撞
        private static readonly Vector3 Isolation = new Vector3(9999f, 9999f, 9999f);

        [BridgeCommand("prefab.screenshot",
            "将目标预制体复制到场景隔离位置并截图保存为 PNG（旋转保持资产原有，摄制后销毁临时对象）。参数: path(string), " +
            "offset{x,y,z}（缺省相机相对预制体偏移）, cameraPosition(float[]3,相机位置,relative时相对预制体), " +
            "lookAt(float[]3,观察目标,缺省预制体位置), relative(bool,默认false=世界坐标), " +
            "output(string,.png), orthographic(bool,默认false), fov(number,默认Unity默认), " +
            "width(int,默认1920), height(int,默认1080), bg(string r,g,b,a,默认透明), " +
            "light(number,默认0不补光;>0追加与相机同向平行光)")]
        public static object Capture(BridgeContext ctx, BridgeArgs args)
        {
            var path = args.path;
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("prefab.screenshot 需要参数 path（预制体在 Assets 中的相对路径）");

            var output = args.output;
            if (string.IsNullOrEmpty(output))
                throw new ArgumentException("prefab.screenshot 需要参数 output（PNG 输出路径）");
            if (!output.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("output 必须是 .png 文件路径（当前: " + output + "）");

            var offset = args.offset;
            bool orthographic = args.orthographic;
            float fov = args.fov;                        // <=0 表示未提供，使用 Unity 默认
            int width = args.width > 0 ? args.width : 1920;
            int height = args.height > 0 ? args.height : 1080;
            Color bg = ParseColor(args.bg);              // null/空 -> 透明
            float lightIntensity = args.light;

            var resolved = Normalize(path);
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(resolved);
            if (go == null)
                throw new InvalidOperationException(
                    $"找不到预制体/模型: {resolved}（需为 Assets 下的 .prefab 或模型文件）");

            var outDir = Path.GetDirectoryName(Path.GetFullPath(output));
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

            GameObject instance = null;
            GameObject camGo = null;
            GameObject lightGo = null;
            RenderTexture rt = null;
            Texture2D tex = null;
            try
            {
                // 1) 复制到场景隔离位置；旋转保持 prefab 资产原有的（不强制 identity），
                //    缩放统一为 1（只看几何本身，不受资产缩放影响）
                instance = (GameObject)PrefabUtility.InstantiatePrefab(go);
                instance.transform.position = Isolation;
                instance.transform.localScale = Vector3.one;

                // 2) 创建相机：相机位置与观察目标，优先 cameraPosition/lookAt（relative 时相对隔离点），
                //    否则回退到 offset（相机相对预制体偏移、观察预制体）
                Vector3 camPos;
                Vector3 lookTarget;
                if (args.cameraPosition != null && args.cameraPosition.Length >= 3)
                {
                    var cp = new Vector3(args.cameraPosition[0], args.cameraPosition[1], args.cameraPosition[2]);
                    camPos = args.relative ? Isolation + cp : cp;
                }
                else
                {
                    camPos = Isolation + offset;
                }
                if (args.lookAt != null && args.lookAt.Length >= 3)
                {
                    var la = new Vector3(args.lookAt[0], args.lookAt[1], args.lookAt[2]);
                    lookTarget = args.relative ? Isolation + la : la;
                }
                else
                {
                    lookTarget = Isolation;
                }

                camGo = new GameObject("BridgeScreenshotCamera");
                var cam = camGo.AddComponent<Camera>();
                camGo.transform.position = camPos;
                camGo.transform.LookAt(lookTarget);

                // 2.5) 补光：追加一盏与相机朝向一致的平行光（light>0 时）
                if (lightIntensity > 0)
                {
                    lightGo = new GameObject("BridgeFillLight");
                    var fillLight = lightGo.AddComponent<Light>();
                    fillLight.type = LightType.Directional;
                    fillLight.intensity = lightIntensity;
                    fillLight.color = Color.white;
                    fillLight.shadows = LightShadows.None;
                    // 光线方向：light 的 rotation 与相机完全一致
                    lightGo.transform.rotation = camGo.transform.rotation;
                    lightGo.transform.position = camPos;
                }

                cam.orthographic = orthographic;
                if (fov > 0)
                {
                    if (orthographic) cam.orthographicSize = fov;
                    else cam.fieldOfView = fov;
                }
                cam.clearFlags = CameraClearFlags.Color;
                cam.backgroundColor = bg;
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = 5000f;
                cam.aspect = (float)width / height;
                cam.targetTexture = null;

                // 3) 渲染到 RenderTexture 并回读为 PNG
                rt = RenderTexture.GetTemporary(width, height, 24,
                    RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
                cam.targetTexture = rt;
                cam.Render();

                RenderTexture.active = rt;
                tex = new Texture2D(width, height, TextureFormat.ARGB32, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();
                RenderTexture.active = null;
                cam.targetTexture = null;

                byte[] png = tex.EncodeToPNG();
                File.WriteAllBytes(output, png);

                return new ScreenshotResult
                {
                    path = path,
                    resolvedPath = resolved,
                    output = Path.GetFullPath(output),
                    cameraType = orthographic ? "orthographic" : "perspective",
                    width = width,
                    height = height,
                    cameraPosition = camPos,
                    lookAt = lookTarget,
                    fillLight = lightIntensity > 0 ? lightIntensity : 0f,
                    bytes = png.Length,
                };
            }
            finally
            {
                // 4) 无论成功与否，销毁临时对象（预制体副本/相机/补光），不污染场景
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                if (lightGo != null) UnityEngine.Object.DestroyImmediate(lightGo);
                if (camGo != null) UnityEngine.Object.DestroyImmediate(camGo);
                if (instance != null) UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        private static Color ParseColor(string s)
        {
            if (string.IsNullOrEmpty(s)) return new Color(0f, 0f, 0f, 0f); // 默认透明背景

            var parts = s.Split(',');
            if (parts.Length < 3 || parts.Length > 4)
                throw new ArgumentException("bg 格式应为 'r,g,b[,a]'（0~1）");

            var ci = CultureInfo.InvariantCulture;
            float r = float.Parse(parts[0].Trim(), ci);
            float g = float.Parse(parts[1].Trim(), ci);
            float b = float.Parse(parts[2].Trim(), ci);
            float a = parts.Length == 4 ? float.Parse(parts[3].Trim(), ci) : 1f;
            return new Color(r, g, b, a);
        }

        private static string Normalize(string path)
        {
            var p = path.Replace('\\', '/').Trim();
            if (!p.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                p = "Assets/" + p.TrimStart('/');
            return p;
        }
    }
}
#endif // UNITY_EDITOR
