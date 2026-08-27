#if UNITY_EDITOR
using System;
using System.IO;
using UnityEngine;

namespace UnityPythonBridge.Commands
{
    /// <summary>view.camera_create 返回结构。</summary>
    [System.Serializable]
    public class ViewCameraCreateResult
    {
        public string output;            // 输出 PNG 绝对路径
        public int width;
        public int height;
        public string cameraType;        // "orthographic" / "perspective"
        public Vector3 position;         // 实际使用的相机世界坐标
        public Vector3 rotationEuler;    // 实际应用的欧拉角
        public bool quaternion;          // 输入是否以四元数解释
        public float fillLight;          // 实际补光强度（0=无）
        public int bytes;
    }

    /// <summary>
    /// 自由相机截图命令：临时创建一个【全新的相机】放在任意位置/朝向，渲染【真实的当前场景】
    /// （不是隔离预制体，也不是复用场景里已有的相机），保存为 PNG 后立刻销毁该相机（及可选补光），不污染场景。
    ///
    /// 与 view.camera（复用场景里已有的相机）的区别：本命令自己 new 一个相机，位置/朝向完全由调用方指定，
    /// 适合「从任意视点看场景」——例如搭完场景后从不同机位出图对比。
    /// 与 prefab.screenshot（把预制体复制到隔离点单独渲染）的区别：本命令渲染的是真实场景中的全部物体。
    ///
    /// 参数（BridgeArgs，全部复用既有字段）:
    ///   output   (string, 必填)        - PNG 输出路径（必须以 .png 结尾）
    ///   position (float[]3, 可选)       - 相机世界坐标 {x,y,z}，缺省 (0,0,0)
    ///   rotation (float[]3 或 float[]4, 可选) - 相机朝向；默认欧拉角 {x,y,z}，quaternion=true 时为四元数 {x,y,z,w}；缺省 identity
    ///   width    (int, 可选)            - 输出图片宽，默认 1920
    ///   height   (int, 可选)            - 输出图片高，默认 1080
    ///   orthographic (bool, 可选)       - 是否正交相机，默认 false（透视）
    ///   fov      (float, 可选)          - 视野：透视=fieldOfView，正交=orthographicSize；<=0 使用 Unity 默认
    ///   bg       (string, 可选)         - 背景色 "r,g,b[,a]"（0~1）；提供则用该纯色作背景，缺省渲染场景 Skybox
    ///   light    (float, 可选)          - 补光强度，默认 0（用场景自身光照）；>0 追加一盏与相机同向平行光
    ///
    /// 返回: { output, width, height, cameraType, position{x,y,z}, rotationEuler{x,y,z}, quaternion, fillLight, bytes }
    /// </summary>
    public static class ViewCameraCreateCommand
    {
        [BridgeCommand("view.camera_create",
            "临时创建一个新相机（任意 position/rotation）渲染真实场景并截图保存 PNG，截完立即销毁。参数: " +
            "output(string,.png), position(float[]3,可选,默认0,0,0), rotation(float[]3欧拉或float[]4四元数,可选,默认identity), " +
            "width(int,默认1920), height(int,默认1080), orthographic(bool,默认false), fov(number,默认Unity默认), " +
            "bg(string r,g,b,a,可选,缺省用场景Skybox), light(number,默认0不补光;>0追加与相机同向平行光)")]
        public static object Capture(BridgeContext ctx, BridgeArgs args)
        {
            var output = args.output;
            if (string.IsNullOrEmpty(output))
                throw new ArgumentException("view.camera_create 需要参数 output（PNG 输出路径）");
            if (!output.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("output 必须是 .png 文件路径（当前: " + output + "）");

            // 相机位置（缺省原点）
            Vector3 camPos = Vector3.zero;
            if (args.position != null)
            {
                if (args.position.Length != 3)
                    throw new ArgumentException("position 必须是 3 个分量 {x,y,z}");
                camPos = new Vector3(args.position[0], args.position[1], args.position[2]);
            }

            // 相机朝向（欧拉或四元数，缺省 identity）—— 与 gameobject.set 的解析规则一致
            Quaternion camRot = Quaternion.identity;
            if (args.rotation != null)
            {
                if (args.quaternion)
                {
                    if (args.rotation.Length != 4)
                        throw new ArgumentException("quaternion=true 时 rotation 必须是 4 个分量 {x,y,z,w}");
                    camRot = new Quaternion(args.rotation[0], args.rotation[1], args.rotation[2], args.rotation[3]);
                }
                else
                {
                    if (args.rotation.Length != 3)
                        throw new ArgumentException("rotation 必须是 3 个分量 {x,y,z}（欧拉角）");
                    camRot = Quaternion.Euler(args.rotation[0], args.rotation[1], args.rotation[2]);
                }
            }

            bool orthographic = args.orthographic;
            float fov = args.fov;                  // <=0 表示未提供，使用 Unity 默认
            int width = args.width > 0 ? args.width : 1920;
            int height = args.height > 0 ? args.height : 1080;
            float lightIntensity = args.light;

            var outDir = Path.GetDirectoryName(Path.GetFullPath(output));
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

            GameObject camGo = null;
            GameObject lightGo = null;
            RenderTexture rt = null;
            Texture2D tex = null;
            try
            {
                // 1) 创建新相机并摆放（不在场景中保留，finally 立即销毁）
                camGo = new GameObject("BridgeFreeCamera");
                var cam = camGo.AddComponent<Camera>();
                camGo.transform.SetPositionAndRotation(camPos, camRot);

                // 2) 投影 / 视野 / 背景
                cam.orthographic = orthographic;
                if (fov > 0)
                {
                    if (orthographic) cam.orthographicSize = fov;
                    else cam.fieldOfView = fov;
                }
                if (!string.IsNullOrEmpty(args.bg))
                {
                    cam.clearFlags = CameraClearFlags.Color;
                    cam.backgroundColor = ParseColor(args.bg);
                }
                else
                {
                    cam.clearFlags = CameraClearFlags.Skybox;   // 缺省渲染场景真实背景
                }
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = 5000f;
                cam.aspect = (float)width / height;
                cam.targetTexture = null;

                // 2.5) 可选补光：与相机朝向一致的平行光（light>0 时）
                if (lightIntensity > 0)
                {
                    lightGo = new GameObject("BridgeFreeFillLight");
                    var fillLight = lightGo.AddComponent<Light>();
                    fillLight.type = LightType.Directional;
                    fillLight.intensity = lightIntensity;
                    fillLight.color = Color.white;
                    fillLight.shadows = LightShadows.None;
                    lightGo.transform.rotation = camRot;
                    lightGo.transform.position = camPos;
                }

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

                return new ViewCameraCreateResult
                {
                    output = Path.GetFullPath(output),
                    width = width,
                    height = height,
                    cameraType = orthographic ? "orthographic" : "perspective",
                    position = camPos,
                    rotationEuler = camGo.transform.eulerAngles,
                    quaternion = args.quaternion,
                    fillLight = lightIntensity > 0 ? lightIntensity : 0f,
                    bytes = png.Length,
                };
            }
            finally
            {
                // 4) 无论成功与否，立刻销毁临时相机与补光，不污染场景
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                if (lightGo != null) UnityEngine.Object.DestroyImmediate(lightGo);
                if (camGo != null) UnityEngine.Object.DestroyImmediate(camGo);
            }
        }

        private static Color ParseColor(string s)
        {
            if (string.IsNullOrEmpty(s)) return new Color(0f, 0f, 0f, 0f); // 默认透明背景

            var parts = s.Split(',');
            if (parts.Length < 3 || parts.Length > 4)
                throw new ArgumentException("bg 格式应为 'r,g,b[,a]'（0~1）");

            var ci = System.Globalization.CultureInfo.InvariantCulture;
            float r = float.Parse(parts[0].Trim(), ci);
            float g = float.Parse(parts[1].Trim(), ci);
            float b = float.Parse(parts[2].Trim(), ci);
            float a = parts.Length == 4 ? float.Parse(parts[3].Trim(), ci) : 1f;
            return new Color(r, g, b, a);
        }
    }
}
#endif // UNITY_EDITOR
