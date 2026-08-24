#if UNITY_EDITOR
using System;
using UnityEditor;

namespace UnityPythonBridge.Commands
{
    /// <summary>editor.play / editor.stop / editor.pause / editor.unpause 返回结构。</summary>
    [System.Serializable]
    public class PlayModeResult
    {
        public bool isPlaying;
        public bool isPaused;
        public string message;
    }

    /// <summary>
    /// 编辑器 Play Mode 控制命令（纯 Editor API，bridge 仓库的通用能力，不依赖任何业务项目）。
    /// 注意：退出 Play Mode 时，若 Unity 启用了 "Reload Domain"（默认开启），会触发 domain reload，
    /// 桥服务器会随旧域一起卸载；这由 BridgeAutoRestart 的 watchdog 在数秒内自动恢复，调用方无需处理。
    /// </summary>
    public static class EditorCommands
    {
        [BridgeCommand("editor.play", "进入 Play Mode（开始运行）。返回切换后的 isPlaying/isPaused。")]
        public static object Play(BridgeContext ctx, BridgeArgs args)
        {
            EnterPlayMode();
            return new PlayModeResult
            {
                isPlaying = EditorApplication.isPlaying,
                isPaused = EditorApplication.isPaused,
                message = "已请求进入 Play Mode"
            };
        }

        [BridgeCommand("editor.stop", "退出 Play Mode（停止运行）。返回切换后的 isPlaying/isPaused。")]
        public static object Stop(BridgeContext ctx, BridgeArgs args)
        {
            ExitPlayMode();
            return new PlayModeResult
            {
                isPlaying = EditorApplication.isPlaying,
                isPaused = EditorApplication.isPaused,
                message = "已请求退出 Play Mode（若启用 Reload Domain，退出后将由 watchdog 自动恢复桥）"
            };
        }

        [BridgeCommand("editor.pause", "暂停 Play Mode（保持运行中但暂停模拟）。返回切换后的 isPlaying/isPaused。")]
        public static object Pause(BridgeContext ctx, BridgeArgs args)
        {
            EditorApplication.isPaused = true;
            return new PlayModeResult
            {
                isPlaying = EditorApplication.isPlaying,
                isPaused = EditorApplication.isPaused,
                message = "已暂停 Play Mode"
            };
        }

        [BridgeCommand("editor.unpause", "恢复 Play Mode（取消暂停）。返回切换后的 isPlaying/isPaused。")]
        public static object Unpause(BridgeContext ctx, BridgeArgs args)
        {
            EditorApplication.isPaused = false;
            return new PlayModeResult
            {
                isPlaying = EditorApplication.isPlaying,
                isPaused = EditorApplication.isPaused,
                message = "已恢复 Play Mode"
            };
        }

        private static void EnterPlayMode()
        {
#if UNITY_2019_1_OR_NEWER
            if (!EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode)
                EditorApplication.EnterPlaymode();
#else
            EditorApplication.isPlaying = true;
#endif
        }

        private static void ExitPlayMode()
        {
#if UNITY_2019_1_OR_NEWER
            if (EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode)
                EditorApplication.ExitPlaymode();
#else
            EditorApplication.isPlaying = false;
#endif
        }
    }
}
#endif // UNITY_EDITOR
