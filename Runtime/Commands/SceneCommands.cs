#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityPythonBridge.Commands
{
    /// <summary>scene.open / scene.new 的返回结构。</summary>
    [System.Serializable]
    public class SceneResult
    {
        public string name;     // 场景名（去扩展名）
        public string path;     // 完整项目相对路径（含 Assets/ 前缀与 .unity 后缀）
        public bool isLoaded;   // 是否加载成功
    }

    /// <summary>scene.save 的返回结构。</summary>
    [System.Serializable]
    public class SaveResult
    {
        public bool saved;      // 是否保存成功
        public string path;     // 实际保存到的完整路径
        public string message;  // 附加说明（如就地保存 / 另存为）
    }

    /// <summary>
    /// 场景生命周期命令（Editor 专用的场景文件操作，不进 Player）：
    ///   - scene.open  加载（打开）一个已存在的 .unity 场景（关闭其他场景）
    ///   - scene.new   新建空白场景并立即持久化到指定路径（创建即落盘）
    ///   - scene.save  保存当前活动场景（省略 path=就地保存；传入 path=另存为，强制完整路径）
    ///
    /// 路径约定（强制）：
    ///   - 创建/加载必须传「完整项目相对路径」，以 "Assets/" 开头、以 ".unity" 结尾，
    ///     不能只传文件名（如 "Foo" / "Foo.unity" 会直接报错）。
    ///   - scene.save 传入 path 时同样强制完整路径；省略则就地保存当前场景。
    ///
    /// 这些命令只用到 bridge 既有的 BridgeArgs.path 字段，不新增字段、不改其它文件；
    /// 删除本文件即完整移除这三条场景命令。
    /// </summary>
    public static class SceneCommands
    {
        // ---------- scene.open ----------

        /// <summary>
        /// 加载（打开）一个已存在的 .unity 场景。
        /// 参数: path(string,必填, 完整路径如 Assets/Scenes/Foo.unity；不能只传文件名)
        /// 行为: 关闭其它场景，打开该场景；文件不存在报错。
        /// </summary>
        [BridgeCommand("scene.open",
            "加载(打开)一个已存在的场景。参数: path=完整场景路径(必填,如 Assets/Scenes/Foo.unity,以 Assets/ 开头,不能只传文件名); 文件不存在报错")]
        public static object Open(BridgeContext ctx, BridgeArgs args)
        {
            string scenePath = RequireFullScenePath(args.path);
            string full = ToProjectFullPath(scenePath);
            if (!File.Exists(full))
                throw new ArgumentException("场景文件不存在: " + scenePath + "（请先用 scene.new 创建，或确认路径正确）");

            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            return new SceneResult
            {
                name = scene.name,
                path = scene.path,
                isLoaded = scene.isLoaded,
            };
        }

        // ---------- scene.new ----------

        /// <summary>
        /// 新建空白场景并立即持久化到指定路径。
        /// 参数: path(string,必填, 完整路径如 Assets/Scenes/Foo.unity；不能只传文件名)
        /// 行为: 关闭其它场景，创建 EmptyScene 并 SaveScene 到该路径（创建即落盘）；
        ///       目标文件已存在则拒绝（避免误覆盖）。
        /// </summary>
        [BridgeCommand("scene.new",
            "新建空白场景并立即保存到指定路径(创建即落盘)。参数: path=完整场景路径(必填,如 Assets/Scenes/Foo.unity,以 Assets/ 开头,不能只传文件名); 已存在则报错")]
        public static object New(BridgeContext ctx, BridgeArgs args)
        {
            string scenePath = RequireFullScenePath(args.path);
            string full = ToProjectFullPath(scenePath);
            if (File.Exists(full))
                throw new ArgumentException("场景已存在，拒绝覆盖: " + scenePath + "（如需重建请先手动删除该文件）");

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSceneManager.SaveScene(scene, scenePath);
            return new SceneResult
            {
                name = scene.name,
                path = scene.path,
                isLoaded = scene.isLoaded,
            };
        }

        // ---------- scene.save ----------

        /// <summary>
        /// 保存当前活动场景。
        /// 参数: path(string,可选) —— 省略=就地保存当前场景；传入=另存为指定完整路径（强制完整路径）。
        /// 行为: 若当前场景从未保存过且未传 path，则报错提示必须传 path。
        /// </summary>
        [BridgeCommand("scene.save",
            "保存当前活动场景。参数: path(可选,完整场景路径,如 Assets/Scenes/Foo.unity; 省略=就地保存,传入=另存为; 均以 Assets/ 开头,不能只传文件名)")]
        public static object Save(BridgeContext ctx, BridgeArgs args)
        {
            var scene = SceneManager.GetActiveScene();

            if (string.IsNullOrWhiteSpace(args.path))
            {
                // 就地保存；未保存过的场景（无 path）SaveScene 会失败，这里提前给清晰错误
                if (string.IsNullOrEmpty(scene.path))
                    throw new InvalidOperationException(
                        "当前场景尚未保存过（无路径），无法就地保存；请传 path 参数另存为，如 scene.save path=Assets/Scenes/Foo.unity");
                EditorSceneManager.SaveScene(scene);
                return new SaveResult
                {
                    saved = true,
                    path = scene.path,
                    message = "已就地保存当前场景",
                };
            }

            string scenePath = RequireFullScenePath(args.path);
            EditorSceneManager.SaveScene(scene, scenePath);
            return new SaveResult
            {
                saved = true,
                path = scenePath,
                message = "已另存为",
            };
        }

        // ---------- 内部工具 ----------

        /// <summary>
        /// 强制完整场景路径：必须以 "Assets/" 开头、以 ".unity" 结尾，禁止裸文件名。
        /// 缺省后缀自动补 ".unity"。
        /// </summary>
        private static string RequireFullScenePath(string raw)
        {
            var p = (raw ?? "").Trim().Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(p))
                throw new ArgumentException(
                    "scene 命令必须传完整场景路径（以 Assets/ 开头，如 Assets/Scenes/Foo.unity），不能只传文件名");

            if (!p.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                p += ".unity";

            if (!p.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    "必须传完整场景路径（以 Assets/ 开头，如 Assets/Scenes/Foo.unity），不能只传文件名: " + p);

            return p;
        }

        /// <summary>把项目相对路径转换为绝对路径用于 File.Exists 判断。</summary>
        private static string ToProjectFullPath(string projectRelativePath)
        {
            // Application.dataPath = "<项目>/Assets"；项目根为 dataPath/..
            var combined = Path.Combine(Application.dataPath, "..", projectRelativePath);
            return Path.GetFullPath(combined);
        }
    }
}
#endif // UNITY_EDITOR
