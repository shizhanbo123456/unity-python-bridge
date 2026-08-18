#if UNITY_EDITOR
using System.Collections.Generic;
using System.Text;
using UnityEditor;
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
    ///   depth (int, 可选) - 遍历深度，根算第 1 层，默认 1（只显示起点本身）
    ///   path (string, 可选) - 扫描起点：层级路径（如 "MainCamera/Object1"）或唯一名称；
    ///                          省略则扫描整个场景；起点为 prefab 实例内部时报错
    /// 返回（JSON 文本）:
    ///   { type, name, path, buildIndex, startPath?, rootCount, roots: [ { name, active, components?, prefab?, children: [...] } ] }
    ///
    /// prefab 实例根节点不展开内部结构，附加 "prefab" 字段为资产路径（Assets/...）。
    /// </summary>
    public static class SceneTreeCommand
    {
        [BridgeCommand("scene.tree",
            "以树状结构返回当前场景中的物体层级。参数: components(bool), depth(int,遍历深度,根算第1层,默认1), path(string,可选,扫描起点,层级路径或唯一名称)")]
        public static object Tree(BridgeContext ctx, BridgeArgs args)
        {
            bool withComponents = args.components;
            int maxDepth = args.depth <= 0 ? 1 : args.depth; // 根算第 1 层，默认 1 只显示起点本身
            string startPath = string.IsNullOrWhiteSpace(args.path) ? null : args.path.Trim();

            var scene = SceneManager.GetActiveScene();

            // 确定扫描起点：指定 path 时从该物体开始；否则从场景根物体开始
            Transform[] starts;
            if (startPath != null)
            {
                starts = new[] { ResolveStartTransform(startPath) };
            }
            else
            {
                var roots = scene.GetRootGameObjects();
                starts = new Transform[roots.Length];
                for (var i = 0; i < roots.Length; i++) starts[i] = roots[i].transform;
            }

            var sb = new StringBuilder(4096);
            sb.Append("{\"type\":\"scene\"");
            sb.Append(",\"name\":").Append(JsonString(scene.name));
            sb.Append(",\"path\":").Append(JsonString(scene.path));
            sb.Append(",\"buildIndex\":").Append(scene.buildIndex);
            if (startPath != null)
            {
                sb.Append(",\"startPath\":").Append(JsonString(startPath));
            }
            sb.Append(",\"rootCount\":").Append(starts.Length);
            sb.Append(",\"roots\":[");
            for (var i = 0; i < starts.Length; i++)
            {
                if (i > 0) sb.Append(',');
                AppendNode(sb, starts[i], withComponents, maxDepth, 1);
            }
            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>
        /// prefab 树命令：以树状结构返回指定 prefab 资产内部的物体层级（类似 scene.tree，
        /// 但扫描对象是 Assets 下的 prefab 资产而非场景）。path 必填。
        /// 嵌套 prefab 实例根同样不展开（附加 prefab 资产路径）。
        /// depth<=0 表示完整展开（默认），与 scene.tree 的默认 1（只显示起点）不同。
        /// </summary>
        [BridgeCommand("prefab.tree",
            "以树状结构返回 prefab 资产内部的物体层级。参数: path(string,必填,Assets 相对路径, .prefab 或模型文件), components(bool), depth(int,遍历深度,根算第1层,默认-1=完整展开)")]
        public static object PrefabTree(BridgeContext ctx, BridgeArgs args)
        {
            var path = args.path;
            if (string.IsNullOrWhiteSpace(path))
                throw new System.ArgumentException("prefab.tree 需要参数 path（prefab 在 Assets 中的相对路径，必填）");

            bool withComponents = args.components;
            int maxDepth = args.depth <= 0 ? int.MaxValue : args.depth; // 默认完整展开

            var resolved = NormalizeAssetPath(path);
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(resolved);
            if (asset == null)
                throw new System.InvalidOperationException(
                    $"找不到 prefab 资产: {resolved}（需为 Assets 下的 .prefab 或模型文件）");

            var sb = new StringBuilder(4096);
            sb.Append("{\"type\":\"prefab\"");
            sb.Append(",\"path\":").Append(JsonString(resolved));
            sb.Append(",\"rootCount\":1");
            sb.Append(",\"roots\":[");
            AppendNode(sb, asset.transform, withComponents, maxDepth, 1);
            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>规范化资产路径：统一 '/' 分隔，无 "Assets/" 前缀时自动补上。</summary>
        private static string NormalizeAssetPath(string path)
        {
            var p = path.Replace('\\', '/').Trim();
            if (!p.StartsWith("Assets/", System.StringComparison.OrdinalIgnoreCase))
                p = "Assets/" + p.TrimStart('/');
            return p;
        }

        /// <summary>
        /// 解析 scene.tree 的扫描起点：层级路径（从场景根开始 '/' 分隔，如 "MainCamera/Object1"）
        /// 或唯一名称。找不到/重名时抛异常。起点位于 prefab 实例内部（非实例根）时抛异常，
        /// 错误信息含 prefab 根在场景中的路径与 Assets 中 prefab 路径。
        /// </summary>
        private static Transform ResolveStartTransform(string target)
        {
            var scene = SceneManager.GetActiveScene();

            if (target.IndexOf('/') >= 0)
            {
                var segs = target.Split(new[] { '/' }, System.StringSplitOptions.RemoveEmptyEntries);
                if (segs.Length == 0)
                    throw new System.ArgumentException("路径无效: " + target);
                var matches = new List<Transform>();
                foreach (var root in scene.GetRootGameObjects())
                    MatchPathTransform(root.transform, segs, 0, matches);
                if (matches.Count == 0)
                    throw new System.InvalidOperationException($"场景中未找到路径 '{target}'");
                if (matches.Count > 1)
                    throw new System.InvalidOperationException($"路径 '{target}' 匹配到 {matches.Count} 个物体，请使用更完整的路径");
                CheckNotInsidePrefab(target, matches[0].gameObject);
                return matches[0];
            }

            // 按名称（唯一命中，重名报错）
            var byName = new List<Transform>();
            foreach (var root in scene.GetRootGameObjects())
                CollectByNameTransform(root.transform, target, byName);
            if (byName.Count == 0)
                throw new System.InvalidOperationException($"场景中未找到名为 '{target}' 的物体");
            if (byName.Count > 1)
                throw new System.InvalidOperationException($"场景中有 {byName.Count} 个名为 '{target}' 的物体，请使用层级路径");
            CheckNotInsidePrefab(target, byName[0].gameObject);
            return byName[0];
        }

        /// <summary>起点不允许位于 prefab 实例内部（允许起点就是 prefab 实例根）。
        /// 报错时附带 prefab 根在场景中的路径与 Assets 中 prefab 路径。</summary>
        private static void CheckNotInsidePrefab(string target, GameObject go)
        {
            if (!PrefabUtility.IsPartOfPrefabInstance(go) || PrefabUtility.IsAnyPrefabInstanceRoot(go))
                return; // 普通物体或 prefab 实例根：允许

            var root = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
            // 注意：该 API 在不同 Unity 版本返回类型不同（旧版 Transform / 新版 GameObject），
            // .transform 对两者均成立（Component.transform 返回自身，GameObject.transform 返回其 Transform）
            string rootPath = root != null ? TransformPath(root.transform) : "?";
            string assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go) ?? "";
            throw new System.InvalidOperationException(
                $"不能直接扫描 prefab 实例内部 '{target}': prefab 根在场景中的路径为 '{rootPath}', " +
                $"Assets 中 prefab 路径为 '{assetPath}'。请从 prefab 根或场景根开始扫描。");
        }

        private static void MatchPathTransform(Transform t, string[] segs, int depth, List<Transform> matches)
        {
            // 名称按 Trim 后比较：宽容物体名首尾空格（如 prefab 内部物体名 "Cylinder "），
            // 保证 "Tree_A_1/Cylinder" 能命中 prefab 内部并触发"不可扫描 prefab 内部"报错
            if (t.name.Trim() != segs[depth].Trim()) return;
            if (depth == segs.Length - 1)
            {
                matches.Add(t);
                return;
            }
            for (var i = 0; i < t.childCount; i++)
            {
                var child = t.GetChild(i);
                if (child != null) MatchPathTransform(child, segs, depth + 1, matches);
            }
        }

        private static void CollectByNameTransform(Transform t, string name, List<Transform> matches)
        {
            if (t.name.Trim() == name.Trim()) matches.Add(t);
            for (var i = 0; i < t.childCount; i++)
            {
                var child = t.GetChild(i);
                if (child != null) CollectByNameTransform(child, name, matches);
            }
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

        /// <summary>递归构建单个节点 JSON（无深度限制）。prefab 实例根不展开内部结构，
        /// 改为附加 prefab 资产路径（Assets/...，见 "prefab" 字段）。
        /// maxDepth=最大遍历深度（根算第 1 层）；currentDepth=当前节点深度。</summary>
        private static void AppendNode(StringBuilder sb, Transform t, bool withComponents,
            int maxDepth, int currentDepth)
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

            // prefab 实例根：不进入内部遍历，备注资产路径（Assets/...）
            // 注：不用 GetOutermostPrefabInstanceRoot == t 判断——实测该 API 返回的 Transform
            // 与场景实例根做 == 比较为 False（对象引用不一致），应使用 IsAnyPrefabInstanceRoot。
            if (PrefabUtility.IsAnyPrefabInstanceRoot(go))
            {
                string assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go) ?? "";
                sb.Append(",\"prefab\":").Append(JsonString(assetPath));
                sb.Append(",\"children\":[]}");
                return;
            }

            sb.Append(",\"children\":[");
            if (currentDepth < maxDepth)
            {
                bool firstChild = true;
                for (var i = 0; i < t.childCount; i++)
                {
                    var child = t.GetChild(i);
                    if (child == null) continue;
                    if (!firstChild) sb.Append(',');
                    firstChild = false;
                    AppendNode(sb, child, withComponents, maxDepth, currentDepth + 1);
                }
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
