#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityPythonBridge
{
    /// <summary>
    /// Bridge 服务器状态持久化与自动恢复。
    ///
    /// 原理：
    ///   - 状态文件存于 Library/BridgeServerState.txt（"1"=运行中，"0"=已停止；Library 目录不参与
    ///     编译、也不会被重编译循环触发，且跨重编译/跨编辑器会话保留）。
    ///   - [InitializeOnLoadMethod]：每次 domain reload（重编译 / 打开项目）后执行——
    ///     若状态文件为 "1" 且服务器未运行，则自动 Start()，实现"重编译后维持之前状态"。
    ///   - AppDomain.DomainUnload：旧域卸载前触发，把"当时是否在运行"写回状态文件，
    ///     保证重编译瞬间的状态被准确记录（用户手动 Stop 过则会记录 0）。
    /// </summary>
    public static class BridgeAutoRestart
    {
        private const string StateFileName = "BridgeServerState.txt";
        private static string StateFilePath =>
            Path.Combine(Application.dataPath, "..", "Library", StateFileName);

        [InitializeOnLoadMethod]
        private static void OnDomainLoaded()
        {
            // 订阅域卸载：重编译 / 退出前，把当前运行状态落盘
            AppDomain.CurrentDomain.DomainUnload += (s, e) => SaveState(BridgeServer.IsRunning);

            // 域加载完成后延迟一帧再恢复（确保编辑器环境就绪）
            EditorApplication.delayCall += () =>
            {
                if (LoadState() && !BridgeServer.IsRunning)
                {
                    BridgeServer.Start();
                }
            };
        }

        /// <summary>把服务器状态写入文件（"1"/"0"）。供 Inspector 按钮与域卸载回调调用。</summary>
        public static void SaveState(bool running)
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
        public static bool LoadState()
        {
            try
            {
                if (!File.Exists(StateFilePath)) return false;
                var text = File.ReadAllText(StateFilePath).Trim();
                return text == "1";
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
#endif // UNITY_EDITOR
