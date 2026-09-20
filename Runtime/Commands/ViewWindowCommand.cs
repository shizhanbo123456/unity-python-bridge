#if UNITY_EDITOR
using System;
using System.IO;
using UnityEngine;

namespace UnityPythonBridge.Commands
{
    /// <summary>view.window 返回结构。</summary>
    [System.Serializable]
    public class ViewWindowResult
    {
        public string output;      // PNG 绝对路径
        public bool requested;     // 是否已发出截图请求
        public int superSize;      // 实际使用的分辨率倍数
        public string note;        // 补充说明（异步落盘 / 分辨率规则）
    }

    /// <summary>
    /// 抓 Game 视图的最终呈现（含 Overlay UI）为 PNG。
    /// 与 view.camera 的区别：view.camera 抓某个相机的渲染输出，看不到 Overlay 的
    /// uGUI Canvas 与 UI Toolkit 面板；本命令抓合成后的画面，用于评审界面。
    /// </summary>
    public static class ViewWindowCommand
    {
        // 用 ScreenCapture（公开 API），截图在帧末由 Unity 异步写入文件，
        // 所以本命令只发请求、立刻返回，调用方需等待文件落盘。
        // 坑：Editor 下 Game 视图必须是当前选中的标签页，否则 Unity 静默不写文件（Scene 视图被选中时）。
        [BridgeCommand("view.window",
            "抓取 Game 视图最终呈现（含 uGUI / UI Toolkit 的 Overlay UI）保存为 PNG。参数: " +
            "output(string,.png,必填), superSize(int,可选,默认1;2=两倍分辨率,范围1~4)。" +
            "注意: 截图由 ScreenCapture 在帧末异步写入, 命令返回时文件可能尚未生成; " +
            "Edit Mode 下必须让 Game 视图成为【当前选中的标签页】, 否则静默不写文件(Scene 视图被选中时); " +
            "分辨率=Game 视图分辨率*superSize; Scene 视图本身不支持截取")]
        public static object Capture(BridgeContext ctx, BridgeArgs args)
        {
            var output = args.output;
            if (string.IsNullOrEmpty(output))
                throw new ArgumentException("view.window 需要参数 output（PNG 输出路径）");
            if (!output.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("output 必须是 .png 文件路径（当前: " + output + "）");

            int superSize = args.superSize < 1 ? 1 : Math.Min(args.superSize, 4);

            var outDir = Path.GetDirectoryName(Path.GetFullPath(output));
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

            // 先删旧文件，调用方才能靠"文件出现"判断这一轮结果已落盘
            if (File.Exists(output)) File.Delete(output);

            ScreenCapture.CaptureScreenshot(output, superSize);
            Debug.Log($"[UnityPythonBridge] 已请求 Game 视图截图: {output}（superSize={superSize}，帧末写入）");

            return new ViewWindowResult
            {
                output = Path.GetFullPath(output),
                requested = true,
                superSize = superSize,
                note = "截图在帧末异步落盘，请稍后确认文件存在；分辨率 = Game 视图分辨率 * superSize",
            };
        }
    }
}
#endif // UNITY_EDITOR
