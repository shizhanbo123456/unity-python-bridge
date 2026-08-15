using System;
using UnityEditor;
using UnityEngine;

namespace UnityPythonBridge
{
    /// <summary>
    /// Bridge 服务器自动恢复（Editor 程序集，仅负责"时机"；状态读写委托给 Runtime 的 BridgeStateStore）。
    ///
    /// 原理：
    ///   - 状态文件存于 Library/BridgeServerState.txt（由 BridgeStateStore 读写），跨重编译/跨会话保留。
    ///   - [InitializeOnLoadMethod]：每次 domain reload（重编译 / 打开项目）后执行——
    ///     若状态为 "1" 且服务器未运行，则自动 Start()，实现"重编译后维持之前状态"。
    ///   - AppDomain.DomainUnload：旧域卸载前触发，把"当时是否在运行"写回状态文件，
    ///     保证重编译瞬间的状态被准确记录（用户手动 Stop 过则会记录 0）。
    /// </summary>
    public static class BridgeAutoRestart
    {
        [InitializeOnLoadMethod]
        private static void OnDomainLoaded()
        {
            // 订阅域卸载：重编译 / 退出前，把当前运行状态落盘
            AppDomain.CurrentDomain.DomainUnload += (s, e) => BridgeStateStore.Save(BridgeServer.IsRunning);

            // 域加载完成后延迟一帧再恢复（确保编辑器环境就绪）
            EditorApplication.delayCall += () =>
            {
                if (BridgeStateStore.Load() && !BridgeServer.IsRunning)
                {
                    BridgeServer.Start();
                }
            };
        }

        /// <summary>写服务器状态（桥接到 BridgeStateStore）。供 Inspector 按钮调用。</summary>
        public static void SaveState(bool running) => BridgeStateStore.Save(running);
    }
}
