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
