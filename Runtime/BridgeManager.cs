#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace UnityPythonBridge
{
    /// <summary>
    /// Bridge 管理组件：挂到场景任意物体上，Inspector 会显示"启动/停止服务器"按钮
    /// （按钮 UI 由 Editor/BridgeManagerInspector.cs 提供）。
    ///
    /// 职责：
    ///   - 组件/物体被 Destroy 时自动停止服务器（若正在运行）
    ///   - 按钮切换状态时写入持久化状态文件（Library/BridgeServerState.txt），
    ///     触发重编译（domain reload）后由 BridgeAutoRestart 按文件内容自动恢复。
    ///
    /// 注意：命令执行队列（MainThreadRunner.Flush）由 BridgeServer 自身驱动，
    /// 不依赖本组件——即使场景中没有此组件，服务器也能正常执行命令。
    /// </summary>
    [DisallowMultipleComponent]
    public class BridgeManager : MonoBehaviour
    {
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
