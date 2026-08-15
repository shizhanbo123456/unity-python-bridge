#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace UnityPythonBridge
{
    /// <summary>
    /// BridgeManager 的 Inspector 自定义绘制：提供"启动/停止服务器"按钮。
    /// 切换状态时调用 BridgeServer.Start/Stop，并同步把状态写入持久化文件
    /// （由 BridgeAutoRestart 负责），确保重编译后维持相同状态。
    /// </summary>
    [CustomEditor(typeof(BridgeManager))]
    public class BridgeManagerInspector : Editor
    {
        public override void OnInspectorGUI()
        {
            // 标题与状态
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Unity Python Bridge", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("服务器状态",
                    BridgeServer.IsRunning ? "● 运行中" : "○ 已停止",
                    BridgeServer.IsRunning ? EditorStyles.boldLabel : EditorStyles.label);
            }

            EditorGUILayout.Space(4);

            // 启动 / 停止按钮（切换后写状态文件）
            using (new EditorGUILayout.HorizontalScope())
            {
                if (!BridgeServer.IsRunning)
                {
                    if (GUILayout.Button("启动服务器"))
                    {
                        BridgeServer.Start();
                        BridgeAutoRestart.SaveState(true);
                        Debug.Log($"[UnityPythonBridge] 服务器已启动，监听 127.0.0.1:{BridgeServer.Port}（状态已持久化）");
                    }
                }
                else
                {
                    if (GUILayout.Button("停止服务器"))
                    {
                        BridgeServer.Stop();
                        BridgeAutoRestart.SaveState(false);
                        Debug.Log("[UnityPythonBridge] 服务器已停止（状态已持久化）");
                    }
                }
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                "服务器状态会写入 Library/BridgeServerState.txt。\n" +
                "触发脚本重编译或重新打开项目后，将按该状态自动恢复。",
                MessageType.Info);
        }
    }
}
#endif // UNITY_EDITOR
