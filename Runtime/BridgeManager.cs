#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace UnityPythonBridge
{
    /// <summary>
    /// Bridge 管理组件：挂到场景任意物体上，Inspector 会显示"启动/停止服务器"按钮
    /// （按钮 UI 由 Editor/BridgeManagerInspector.cs 提供）。
    ///
    /// 职责（替代原 BridgeWindow 窗口）：
    ///   - Edit Mode 下每帧驱动 MainThreadRunner.Flush()（命令队列在主线程执行的引擎）
    ///   - 组件/物体被 Destroy 时自动停止服务器（若正在运行）
    ///   - 按钮切换状态时写入持久化状态文件（Library/BridgeServerState.txt），
    ///     触发重编译（domain reload）后由 BridgeAutoRestart 按文件内容自动恢复。
    /// </summary>
    [DisallowMultipleComponent]
    public class BridgeManager : MonoBehaviour
    {
        private void OnEnable()
        {
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
        }

        /// <summary>主线程驱动：刷新命令执行队列（Edit Mode 与 Play Mode 均有效）。</summary>
        private void OnEditorUpdate()
        {
            MainThreadRunner.Flush();
        }

        private void OnDestroy()
        {
            // 组件/物体被销毁时自动停止服务器（若正在运行）
            if (BridgeServer.IsRunning)
            {
                BridgeServer.Stop();
                Debug.Log("[UnityPythonBridge] BridgeManager 已销毁，服务器已自动停止");
            }
        }
    }
}
#endif // UNITY_EDITOR
