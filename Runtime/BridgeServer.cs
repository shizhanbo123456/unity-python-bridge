#if UNITY_EDITOR
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace UnityPythonBridge
{
    /// <summary>
    /// TCP 桥接服务器。只监听 127.0.0.1（本机），协议为单行 JSON（Unity 原生 JsonUtility）：
    ///   请求:  {"id": 1, "cmd": "scene.tree", "args": {"components": true}}
    ///   响应:  {"id": 1, "ok": true,  "data": {...}}
    ///         {"id": 1, "ok": false, "error": "..."}
    /// 后台线程负责监听与收发，命令执行投递到主线程队列（见 MainThreadRunner）。
    /// 仅依赖 Unity 内置 JsonUtility，无任何第三方包。
    /// </summary>
    public static class BridgeServer
    {
        public const int DefaultPort = 21927;

        private static TcpListener _listener;
        private static Thread _listenThread;
        private static volatile bool _running;

        public static int Port { get; private set; } = DefaultPort;
        public static bool IsRunning => _running;

        /// <summary>从 bridge.ini 读取 [server] port。
        /// ini 路径 = &lt;项目&gt;/Assets/unity-python-bridge/bridge.ini。
        /// 文件缺失、解析失败或值无效（非 1~65535）时回退到 DefaultPort。</summary>
        private static int ReadServerPortFromIni()
        {
            string iniPath = Path.Combine(Application.dataPath, "unity-python-bridge", "bridge.ini");
            if (!File.Exists(iniPath))
            {
                return DefaultPort;
            }
            try
            {
                string currentSection = null;
                foreach (var raw in File.ReadAllLines(iniPath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                    if (line[0] == '[' && line[line.Length - 1] == ']')
                    {
                        currentSection = line.Substring(1, line.Length - 2).Trim();
                        continue;
                    }
                    if (currentSection != "server") continue;
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    if (line.Substring(0, eq).Trim() != "port") continue;
                    string value = line.Substring(eq + 1).Trim();
                    // 去掉行内注释（; 或 # 之后），支持 `port = 21927  ; 注释` 写法
                    int semicolon = value.IndexOf(';');
                    int hashSign = value.IndexOf('#');
                    int cut = semicolon >= 0 && (hashSign < 0 || semicolon < hashSign)
                        ? semicolon
                        : (hashSign >= 0 ? hashSign : -1);
                    if (cut >= 0) value = value.Substring(0, cut).Trim();
                    if (int.TryParse(value, out int p) && p > 0 && p <= 65535)
                    {
                        return p;
                    }
                    Debug.LogWarning($"[UnityPythonBridge] bridge.ini [server] port 无效: '{value}'，回退到默认端口 {DefaultPort}");
                    return DefaultPort;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[UnityPythonBridge] 读取 bridge.ini 端口失败: {e.Message}，回退到默认端口 {DefaultPort}");
            }
            return DefaultPort;
        }

        /// <summary>启动服务器。port 为 null 时从 bridge.ini 的 [server] port 读取；
        /// 读不到或无效则回退到 DefaultPort（21927）。</summary>
        public static void Start(int? port = null)
        {
            if (_running)
            {
                Debug.LogWarning($"[UnityPythonBridge] 服务器已在运行中（监听 127.0.0.1:{Port}），忽略重复启动");
                return;
            }

            Port = port ?? ReadServerPortFromIni();
            _running = true;
            _listenThread = new Thread(ListenLoop)
            {
                IsBackground = true,
                Name = "UnityPythonBridge-TCP"
            };
            _listenThread.Start();

            // 服务器自身驱动命令执行队列（不再依赖场景中的 BridgeManager 组件，
            // 避免组件 Missing 时出现"端口监听但命令不执行"）
            EditorApplication.update += OnEditorUpdate;

            Debug.Log($"[UnityPythonBridge] 服务器已启动，监听 127.0.0.1:{Port}（仅本机可访问）");
        }

        public static void Stop()
        {
            if (!_running)
            {
                Debug.LogWarning("[UnityPythonBridge] 服务器未在运行，忽略重复停止");
                return;
            }
            _running = false;
            EditorApplication.update -= OnEditorUpdate;
            try { _listener?.Stop(); } catch (Exception) { /* 忽略 */ }
            _listener = null;
            Debug.Log("[UnityPythonBridge] 服务器已停止");
        }

        /// <summary>主线程驱动：每帧刷新命令执行队列（命令由后台线程投递，此处消费）。</summary>
        private static void OnEditorUpdate()
        {
            MainThreadRunner.Flush();
        }

        private static void ListenLoop()
        {
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, Port);
                _listener.Start();

                while (_running)
                {
                    TcpClient client;
                    try
                    {
                        client = _listener.AcceptTcpClient();
                    }
                    catch (SocketException)
                    {
                        break; // 监听被主动停止
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }

                    var thread = new Thread(() => HandleClient(client))
                    {
                        IsBackground = true,
                        Name = "UnityPythonBridge-Session"
                    };
                    thread.Start();
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[UnityPythonBridge] 监听异常: {e}");
            }
            finally
            {
                _running = false;
            }
        }

        private static void HandleClient(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, new UTF8Encoding(false)))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" })
            {
                try
                {
                    string line;
                    while (_running && (line = reader.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;

                        var req = JsonUtility.FromJson<BridgeRequest>(line);
                        if (req == null || string.IsNullOrEmpty(req.cmd))
                        {
                            WriteError(writer, 0, "JSON 解析失败或缺少 cmd 字段");
                            continue;
                        }

                        var id = req.id;
                        var args = req.args ?? new BridgeArgs();

                        // 关键：切到主线程执行，避免跨线程访问 Unity API
                        MainThreadRunner.Enqueue(() =>
                        {
                            try
                            {
                                var data = BridgeDispatcher.Execute(req.cmd, args);
                                // 命令返回 string 时视为【已是 JSON 文本】（如 scene.tree 手动构建，
                                // 绕开 JsonUtility 10 层序列化深度限制），原样嵌入 data 字段；
                                // 其余类型走 JsonUtility 序列化。
                                string dataJson;
                                if (data is string rawJson)
                                {
                                    dataJson = rawJson;
                                }
                                else
                                {
                                    dataJson = data != null ? JsonUtility.ToJson(data) : "null";
                                }
                                writer.WriteLine($"{{\"id\":{id},\"ok\":true,\"data\":{dataJson}}}");
                            }
                            catch (Exception e)
                            {
                                WriteError(writer, id, e.Message);
                            }
                        });
                    }
                }
                catch (System.IO.IOException)
                {
                    // 客户端断开（如 Python 脚本被终止/超时）——正常现象，静默结束会话
                }
                catch (System.Net.Sockets.SocketException)
                {
                    // 远程主机强制关闭连接——正常现象，静默结束会话
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[UnityPythonBridge] 会话异常: {e.Message}");
                }
            }
        }

        private static void WriteError(TextWriter writer, int id, string message)
        {
            try
            {
                writer.WriteLine(
                    $"{{\"id\":{id},\"ok\":false,\"error\":{JsonEscape(message ?? "")}}}");
            }
            catch (Exception)
            {
                // 连接已断开，忽略
            }
        }

        /// <summary>把字符串转义为 JSON 字符串字面量（含引号）。</summary>
        private static string JsonEscape(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
#endif // UNITY_EDITOR
