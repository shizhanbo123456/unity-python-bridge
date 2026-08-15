#if UNITY_EDITOR
using UnityEngine;

namespace UnityPythonBridge
{
    /// <summary>
    /// Bridge 管理组件：挂到场景任意物体上，Inspector 会显示"启动/停止服务器"按钮
    /// （按钮 UI 由 Editor/BridgeManagerInspector.cs 提供）。
    /// 按钮切换状态时会写入持久化状态文件（Library/BridgeServerState.txt），
    /// 触发重编译（domain reload）后由 BridgeAutoRestart 按文件内容自动恢复。
    /// </summary>
    [DisallowMultipleComponent]
    public class BridgeManager : MonoBehaviour
    {
        // 标记组件：无需任何字段/方法，仅作为 Inspector 自定义绘制的挂载点。
    }
}
#endif // UNITY_EDITOR
