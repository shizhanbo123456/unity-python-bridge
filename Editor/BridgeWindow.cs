#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace UnityPythonBridge.EditorTools
{
    /// <summary>
    /// Bridge 控制窗口：启动/停止 TCP 服务器，并在主线程驱动命令队列。
    /// 菜单：Tools > Unity Python Bridge
    /// </summary>
    public class BridgeWindow : EditorWindow
    {
        private int _port = BridgeServer.DefaultPort;
        private Vector2 _scroll;
        private string _log;

        [MenuItem("Tools/Unity Python Bridge")]
        public static void Open()
        {
            GetWindow<BridgeWindow>("Unity Python Bridge");
        }

        [MenuItem("Tools/Unity Python Bridge/Start Server", false, 20)]
        public static void StartServerFromMenu()
        {
            BridgeServer.Start();
            var w = GetWindow<BridgeWindow>("Unity Python Bridge");
            w.Repaint();
        }

        [MenuItem("Tools/Unity Python Bridge/Stop Server", false, 21)]
        public static void StopServerFromMenu()
        {
            BridgeServer.Stop();
            var w = GetWindow<BridgeWindow>("Unity Python Bridge");
            w.Repaint();
        }

        private void OnEnable()
        {
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
        }

        private void OnDestroy()
        {
            // 关闭窗口时也停掉服务器，避免残留后台线程
            BridgeServer.Stop();
        }

        /// <summary>主线程驱动：刷新命令执行队列（Edit Mode 与 Play Mode 均有效）。</summary>
        private void OnEditorUpdate()
        {
            MainThreadRunner.Flush();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space();

            EditorGUILayout.LabelField("状态", BridgeServer.IsRunning ? "● 运行中" : "○ 已停止",
                BridgeServer.IsRunning ? EditorStyles.boldLabel : EditorStyles.label);

            EditorGUI.BeginDisabledGroup(BridgeServer.IsRunning);
            _port = EditorGUILayout.IntField("监听端口", _port);
            if (_port <= 0 || _port > 65535) _port = BridgeServer.DefaultPort;
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.Space(4);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (!BridgeServer.IsRunning)
                {
                    if (GUILayout.Button("启动服务器"))
                    {
                        BridgeServer.Start(_port);
                        Log("启动服务器 @ 127.0.0.1:" + _port);
                    }
                }
                else
                {
                    if (GUILayout.Button("停止服务器"))
                    {
                        BridgeServer.Stop();
                        Log("停止服务器");
                    }
                }

                if (GUILayout.Button("清空日志", GUILayout.Width(80)))
                {
                    _log = "";
                }
            }

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("日志", EditorStyles.boldLabel);

            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
            EditorGUILayout.LabelField(string.IsNullOrEmpty(_log) ? "(空)" : _log,
                EditorStyles.wordWrappedLabel);
            EditorGUILayout.EndScrollView();
        }

        private void Log(string message)
        {
            _log = "[" + System.DateTime.Now.ToString("HH:mm:ss") + "] " + message + "\n" + _log;
        }
    }
}
#endif // UNITY_EDITOR
