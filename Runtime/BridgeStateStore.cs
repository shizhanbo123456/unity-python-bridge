#if UNITY_EDITOR
using System;
using System.IO;
using UnityEngine;

namespace UnityPythonBridge
{
    /// <summary>
    /// Bridge 服务器状态持久化存储（Runtime 程序集，Editor/Runtime 均可调用）。
    /// 状态文件位于 Library/BridgeServerState.txt（"1"=运行中，"0"=已停止）：
    ///   - Library 目录不参与编译，不会触发重编译循环
    ///   - 跨重编译（domain reload）保留、跨编辑器会话保留
    /// 供 SystemCommands（bridge.reload）、BridgeAutoRestart、BridgeManagerInspector 共用。
    /// </summary>
    public static class BridgeStateStore
    {
        private const string StateFileName = "BridgeServerState.txt";
        private static string StateFilePath =>
            Path.Combine(Application.dataPath, "..", "Library", StateFileName);

        /// <summary>把服务器状态写入文件（"1"/"0"）。</summary>
        public static void Save(bool running)
        {
            try
            {
                File.WriteAllText(StateFilePath, running ? "1" : "0");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[UnityPythonBridge] 写入服务器状态失败: {e.Message}");
            }
        }

        /// <summary>读取状态文件，返回是否处于"运行中"。文件不存在时视为未运行。</summary>
        public static bool Load()
        {
            try
            {
                if (!File.Exists(StateFilePath)) return false;
                return File.ReadAllText(StateFilePath).Trim() == "1";
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
#endif // UNITY_EDITOR
