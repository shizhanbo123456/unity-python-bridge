#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace UnityPythonBridge.Commands
{
    /// <summary>view.camera 返回结构。</summary>
    [System.Serializable]
    public class ViewScreenshotResult
    {
        public string camera;            // 实际使用的相机名称
        public string requestedCamera;   // 用户传入的相机名称（可能为空 = 默认）
        public string output;            // 输出的 PNG 绝对路径
        public int width;
        public int height;
        public int bytes;
    }

    /// <summary>
    /// 相机视图截图命令：渲染指定相机的【实时渲染输出】保存为 PNG。
    ///
    /// 与 prefab.screenshot（把预制体复制到隔离位置单独渲染）不同，
    /// view.camera 直接抓取场景中已有相机的画面——用于看「整场景当前效果」，
    /// 配合 terrain.stash/clear/apply 即可实现「stash → clear → 截图看干净地形 → apply → 截图对比」的调整链路。
    ///
    /// 命名约定（view.* 系列）：
    ///   view.camera  - 渲染指定相机的画面（本命令）
    ///   view.window  - 【预留】截取 Unity 界面 Scene/Game 窗口的最终呈现内容（含 UI/叠加层），后续按需实现
    ///
    /// 参数（BridgeArgs）:
    ///   camera (string, 可选) - 相机 GameObject 名称；省略时依次找 tag=MainCamera、名为 "Main Camera" 的、
    ///                           第一个激活且启用的相机。
    ///   output (string, 必填) - PNG 输出路径（必须以 .png 结尾）
    ///   width / height (int, 可选) - 输出图片尺寸，默认取相机当前 pixelWidth/pixelHeight
    ///
    /// 说明：渲染期间临时接管相机的 targetTexture，结束后恢复，不修改相机其它属性、不污染场景。
    /// </summary>
    public static class ViewScreenshotCommand
    {
        [BridgeCommand("view.camera",
            "渲染指定相机的实时画面保存为 PNG（默认 MainCamera）。参数: camera(string,可选), output(string,.png), " +
            "width(int,可选,默认相机分辨率), height(int,可选)")]
        public static object Capture(BridgeContext ctx, BridgeArgs args)
        {
            var output = args.output;
            if (string.IsNullOrEmpty(output))
                throw new ArgumentException("view.camera 需要参数 output（PNG 输出路径）");
            if (!output.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("output 必须是 .png 文件路径（当前: " + output + "）");

            var cam = FindCamera(args.camera);
            if (!cam.gameObject.activeInHierarchy)
                throw new InvalidOperationException($"相机 '{cam.name}' 的 GameObject 未激活，无法渲染（请先激活物体）");

            int width = args.width > 0 ? args.width : cam.pixelWidth;
            int height = args.height > 0 ? args.height : cam.pixelHeight;
            if (width <= 0 || height <= 0)
                throw new InvalidOperationException($"相机 '{cam.name}' 当前分辨率为 {width}x{height}，无法渲染（请传 width/height 或确保 Game 视图已打开）");

            var outDir = Path.GetDirectoryName(Path.GetFullPath(output));
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

            RenderTexture rt = null;
            Texture2D tex = null;
            var prevTarget = cam.targetTexture;
            try
            {
                rt = RenderTexture.GetTemporary(width, height, 24,
                    RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
                cam.targetTexture = rt;
                cam.Render();

                RenderTexture.active = rt;
                tex = new Texture2D(width, height, TextureFormat.ARGB32, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();
                RenderTexture.active = null;

                byte[] png = tex.EncodeToPNG();
                File.WriteAllBytes(output, png);

                return new ViewScreenshotResult
                {
                    camera = cam.name,
                    requestedCamera = args.camera,
                    output = Path.GetFullPath(output),
                    width = width,
                    height = height,
                    bytes = png.Length,
                };
            }
            finally
            {
                cam.targetTexture = prevTarget;
                RenderTexture.active = null;
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
            }
        }

        /// <summary>按名称查找相机；未传名称时按 MainCamera → Main Camera → 第一个激活相机 的顺序。</summary>
        private static Camera FindCamera(string name)
        {
            Camera[] cams;
#if UNITY_2023_1_OR_NEWER
            cams = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
            cams = UnityEngine.Object.FindObjectsOfType<Camera>(true);
#endif
            if (cams.Length == 0)
                throw new InvalidOperationException("场景中没有任何相机");

            if (string.IsNullOrEmpty(name))
            {
                var main = Camera.main;   // tag = MainCamera
                if (main != null) return main;
                foreach (var c in cams)
                    if (c.name == "Main Camera") return c;
                foreach (var c in cams)
                    if (c.gameObject.activeInHierarchy && c.enabled) return c;
                throw new InvalidOperationException("未找到 tag=MainCamera 的相机，且没有激活的相机（请传 camera 名称）");
            }

            var matches = new List<Camera>();
            foreach (var c in cams)
                if (c.name == name) matches.Add(c);

            if (matches.Count == 1) return matches[0];
            if (matches.Count == 0)
                throw new InvalidOperationException($"未找到名为 '{name}' 的相机");
            throw new InvalidOperationException($"存在 {matches.Count} 个名为 '{name}' 的相机，请使用唯一名称");
        }
    }
}
#endif // UNITY_EDITOR
