using System;
using UnityEditor;
using UnityEngine;

namespace UnityPythonBridge
{
    /// <summary>
    /// Bridge 服务器自动恢复（Editor 程序集，仅负责"时机"；状态读写委托给 Runtime 的 BridgeStateStore）。
    ///
    /// 原理：
    ///   - 状态文件存于 Library/BridgeServerState.txt（由 BridgeStateStore 读写），跨重编译/跨会话保留；
    ///     内容只由显式 Start/Stop 写入（= 用户意图）。**不在域卸载时用瞬时运行状态覆盖**——
    ///     否则重编译中止监听线程后会把 "1" 误写成 "0"，导致自动恢复失效。
    ///   - 常驻 watchdog：每 <see cref="ProbeInterval"/> 秒探测本机端口是否真的在监听；
    ///     状态文件为 "1"（用户意图=运行）但端口无响应时自动 Stop()+Start() 自愈
    ///     （覆盖绑定失败、线程崩溃、端口冲突、域重载后未恢复等场景）。
    ///   - [InitializeOnLoadMethod]：每次 domain reload 后重新注册 watchdog；域加载后
    ///     若状态为 "1" 且服务器未运行，延迟一帧立即 Start() 一次（快速恢复，无需等下一轮探测）。
    /// </summary>
    public static class BridgeAutoRestart
    {
        private const float ProbeInterval = 5f;
        private static float _nextProbeTime;
        private static bool _restarting;

        [InitializeOnLoadMethod]
        private static void OnDomainLoaded()
        {
            _nextProbeTime = 0f;
            EditorApplication.update += WatchdogTick;

            // 域加载完成后延迟一帧再恢复（确保编辑器环境就绪）
            EditorApplication.delayCall += () =>
            {
                if (BridgeStateStore.Load() && !BridgeServer.IsRunning)
                {
                    BridgeServer.Start();
                }
            };
        }

        /// <summary>常驻自愈：状态文件=用户意图，端口实际未监听时自动重启。</summary>
        private static void WatchdogTick()
        {
            if (Time.realtimeSinceStartup < _nextProbeTime || _restarting) return;
            _nextProbeTime = Time.realtimeSinceStartup + ProbeInterval;

            if (!BridgeStateStore.Load()) return;                       // 用户意图=未运行，不干预
            if (BridgeServer.IsRunning && BridgeServer.ProbeListening()) return; // 正常在监听

            _restarting = true;
            try
            {
                if (BridgeServer.IsRunning) BridgeServer.Stop();
                BridgeServer.Start();
                Debug.LogWarning("[UnityPythonBridge] 自检发现端口未监听，已自动重启服务器");
            }
            finally
            {
                _restarting = false;
            }
        }

        /// <summary>写服务器状态（桥接到 BridgeStateStore）。供 Inspector 按钮调用。</summary>
        public static void SaveState(bool running) => BridgeStateStore.Save(running);
    }
}
