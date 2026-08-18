#if UNITY_EDITOR
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityPythonBridge.Commands
{
    /// <summary>
    /// 场景树命令：以树状结构返回当前激活场景中的物体层级。
    ///
    /// 注意：本命令【手动构建 JSON 字符串】返回（而非返回强类型 DTO 交给 JsonUtility 序列化），
    /// 因为 JsonUtility 存在 10 层序列化深度限制——场景层级超过约 4~5 层就会抛
    /// "Serialization depth limit 10 exceeded"，而真实项目场景（UI 结构、prefab 嵌套）极易触发。
    /// 手动拼接 JSON 无深度限制，输出结构不变（仍是 roots 递归树）。
    ///
    /// 参数:
    ///   components (bool, 可选) - 为 true 时每个节点附带组件类型列表
    /// 返回（JSON 文本）:
    ///   { type, name, path, buildIndex, rootCount, roots: [ { name, active, components?, children: [...] } ] }
    /// </summary>
    public static class SceneTreeCommand
    {
        [BridgeCommand("scene.tree",
            "以树状结构返回当前场景中的物体层级。参数: components(bool) 是否附带组件类型")]
        public static object Tree(BridgeContext ctx, BridgeArgs args)
        {
            bool withComponents = args.components;

            var scene = SceneManager.GetActiveScene();
            var roots = scene.GetRootGameObjects();

            var sb = new StringBuilder(4096);
            sb.Append("{\"type\":\"scene\"");
            sb.Append(",\"name\":").Append(JsonString(scene.name));
            sb.Append(",\"path\":").Append(JsonString(scene.path));
            sb.Append(",\"buildIndex\":").Append(scene.buildIndex);
            sb.Append(",\"rootCount\":").Append(roots.Length);
            sb.Append(",\"roots\":[");
            for (var i = 0; i < roots.Length; i++)
            {
                if (i > 0) sb.Append(',');
                AppendNode(sb, roots[i].transform, withComponents);
            }
            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>
        /// 重要脚本命令：列出当前激活场景中所有挂载了"重要脚本"的物体。
        /// 匹配规则取 bridge.ini 的 [scene] important_suffix（逗号分隔的类名后缀列表，
        /// 默认 Manager / Tool），脚本类名以任一后缀结尾（忽略大小写）即视为重要脚本。
        /// 扫描范围含未激活物体（结果用 active 标注实际可见状态）。
        ///
        /// 返回（JSON 文本，手动构建）:
        ///   { type, scene, suffix: [...], count, scripts: [ { path, name, active }, ... ] }
        /// </summary>
        [BridgeCommand("scene.important_scripts",
            "列出场景中挂有重要脚本的物体（类名以 Manager/Tool 等后缀结尾，规则见 bridge.ini [scene] important_suffix）")]
        public static object ImportantScripts(BridgeContext ctx, BridgeArgs args)
        {
            var suffix = ReadImportantSuffixes();
            var scene = SceneManager.GetActiveScene();

            // 先收集条目再拼接，保证 count 准确
            var entries = new List<string>();
            var roots = scene.GetRootGameObjects();
            for (var i = 0; i < roots.Length; i++)
            {
                CollectImportant(entries, roots[i].transform, suffix);
            }

            var sb = new StringBuilder(4096);
            sb.Append("{\"type\":\"important_scripts\"");
            sb.Append(",\"scene\":").Append(JsonString(scene.name));
            sb.Append(",\"suffix\":[");
            for (var i = 0; i < suffix.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(JsonString(suffix[i]));
            }
            sb.Append("],\"count\":").Append(entries.Count);
            sb.Append(",\"scripts\":[");
            for (var i = 0; i < entries.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(entries[i]);
            }
            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>递归遍历物体，收集挂有重要脚本的条目 JSON（含未激活子物体）。</summary>
        private static void CollectImportant(List<string> entries, Transform t, List<string> suffix)
        {
            var go = t.gameObject;
            foreach (var c in go.GetComponents<MonoBehaviour>())
            {
                if (c == null) continue; // missing script 引用
                var typeName = c.GetType().Name;
                if (MatchesAnySuffix(typeName, suffix))
                {
                    entries.Add("{\"path\":" + JsonString(TransformPath(t)) +
                                ",\"name\":" + JsonString(typeName) +
                                ",\"active\":" + (go.activeInHierarchy ? "true" : "false") + "}");
                }
            }
            for (var i = 0; i < t.childCount; i++)
            {
                var child = t.GetChild(i);
                if (child != null) CollectImportant(entries, child, suffix);
            }
        }

        /// <summary>判断类型名是否以任一后缀结尾（忽略大小写）。</summary>
        private static bool MatchesAnySuffix(string typeName, List<string> suffix)
        {
            for (var i = 0; i < suffix.Count; i++)
            {
                if (typeName.EndsWith(suffix[i], System.StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>构建物体层级路径（根物体只含名称，子物体用 / 连接）。</summary>
        private static string TransformPath(Transform t)
        {
            if (t.parent == null) return t.name;
            return TransformPath(t.parent) + "/" + t.name;
        }

        /// <summary>从 bridge.ini 读取 [scene] important_suffix（逗号分隔、支持行内注释）。
        /// 文件缺失、解析失败或无有效后缀时回退默认 ["Manager", "Tool"]。</summary>
        private static List<string> ReadImportantSuffixes()
        {
            var fallback = new List<string> { "Manager", "Tool" };
            string iniPath = System.IO.Path.Combine(Application.dataPath, "unity-python-bridge", "bridge.ini");
            if (!System.IO.File.Exists(iniPath)) return fallback;
            try
            {
                string currentSection = null;
                foreach (var raw in System.IO.File.ReadAllLines(iniPath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                    if (line[0] == '[' && line[line.Length - 1] == ']')
                    {
                        currentSection = line.Substring(1, line.Length - 2).Trim();
                        continue;
                    }
                    if (currentSection != "scene") continue;
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    if (line.Substring(0, eq).Trim() != "important_suffix") continue;
                    string value = line.Substring(eq + 1).Trim();
                    // 去掉行内注释（; 或 # 之后）
                    int semicolon = value.IndexOf(';');
                    int hashSign = value.IndexOf('#');
                    int cut = semicolon >= 0 && (hashSign < 0 || semicolon < hashSign)
                        ? semicolon
                        : (hashSign >= 0 ? hashSign : -1);
                    if (cut >= 0) value = value.Substring(0, cut).Trim();
                    if (value.Length == 0) return fallback;

                    var result = new List<string>();
                    foreach (var part in value.Split(','))
                    {
                        var s = part.Trim();
                        if (s.Length == 0) continue;
                        if (!result.Contains(s)) result.Add(s);
                    }
                    return result.Count > 0 ? result : fallback;
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[UnityPythonBridge] 读取 bridge.ini [scene] important_suffix 失败: {e.Message}，使用默认后缀");
            }
            return fallback;
        }

        /// <summary>递归构建单个节点 JSON（无深度限制）。</summary>
        private static void AppendNode(StringBuilder sb, Transform t, bool withComponents)
        {
            var go = t.gameObject;
            sb.Append("{\"name\":").Append(JsonString(go.name));
            sb.Append(",\"active\":").Append(go.activeSelf ? "true" : "false");

            if (withComponents)
            {
                sb.Append(",\"components\":[");
                bool first = true;
                foreach (var c in go.GetComponents<Component>())
                {
                    if (c == null) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonString(c.GetType().Name));
                }
                sb.Append(']');
            }

            sb.Append(",\"children\":[");
            bool firstChild = true;
            for (var i = 0; i < t.childCount; i++)
            {
                var child = t.GetChild(i);
                if (child == null) continue;
                if (!firstChild) sb.Append(',');
                firstChild = false;
                AppendNode(sb, child, withComponents);
            }
            sb.Append("]}");
        }

        /// <summary>把字符串转义为 JSON 字符串字面量（含引号）。</summary>
        private static string JsonString(string s)
        {
            if (s == null) return "\"\"";
            var sb = new StringBuilder(s.Length + 8);
            sb.Append('"');
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
