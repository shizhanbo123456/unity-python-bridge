#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityPythonBridge.Commands
{
    /// <summary>场景树节点（强类型，JsonUtility 序列化）。</summary>
    [System.Serializable]
    public class SceneTreeNode
    {
        public string name;
        public bool active;
        public List<string> components;
        public List<SceneTreeNode> children = new List<SceneTreeNode>();
    }

    /// <summary>scene.tree 返回结构。</summary>
    [System.Serializable]
    public class SceneTreeResult
    {
        public string type;
        public string name;
        public string path;
        public int buildIndex;
        public int rootCount;
        public List<SceneTreeNode> roots = new List<SceneTreeNode>();
    }

    /// <summary>
    /// 场景树命令：以树状结构返回当前激活场景中的物体层级。
    /// 参数:
    ///   components (bool, 可选) - 为 true 时每个节点附带组件类型列表
    /// 返回结构:
    ///   { type, name, path, buildIndex, rootCount, roots: [ { name, active, children: [...] } ] }
    /// </summary>
    public static class SceneTreeCommand
    {
        [BridgeCommand("scene.tree",
            "以树状结构返回当前场景中的物体层级。参数: components(bool) 是否附带组件类型")]
        public static object Tree(BridgeContext ctx, BridgeArgs args)
        {
            bool withComponents = args.components;

            var scene = SceneManager.GetActiveScene();
            var result = new SceneTreeResult
            {
                type = "scene",
                name = scene.name,
                path = scene.path,
                buildIndex = scene.buildIndex,
                rootCount = scene.rootCount
            };

            foreach (var go in scene.GetRootGameObjects())
            {
                result.roots.Add(Describe(go.transform, withComponents));
            }

            return result;
        }

        private static SceneTreeNode Describe(Transform t, bool withComponents)
        {
            var node = new SceneTreeNode
            {
                name = t.gameObject.name,
                active = t.gameObject.activeSelf
            };

            if (withComponents)
            {
                node.components = new List<string>();
                foreach (var c in t.gameObject.GetComponents<Component>())
                {
                    if (c == null) continue;
                    node.components.Add(c.GetType().Name);
                }
            }

            for (var i = 0; i < t.childCount; i++)
            {
                var child = t.GetChild(i);
                if (child == null) continue;
                node.children.Add(Describe(child, withComponents));
            }

            return node;
        }
    }
}
#endif // UNITY_EDITOR
