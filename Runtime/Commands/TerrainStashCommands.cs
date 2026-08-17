#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityPythonBridge.Commands
{
    // ============ DTO ============

    /// <summary>terrain.stash 单棵树实例（序列化到 JSON 的持久化格式）。</summary>
    [Serializable]
    public class StashedTreeInstance
    {
        public int prototypeIndex;
        public Vector3 position;    // 归一化 0~1
        public float widthScale;
        public float heightScale;
    }

    /// <summary>trees stash 文件内容。</summary>
    [Serializable]
    public class TerrainTreesStash
    {
        public string type = "trees";
        public string terrain;
        public int prototypeCount;
        public List<StashedTreeInstance> instances = new List<StashedTreeInstance>();
    }

    /// <summary>单层 detail 密度图（全图，行优先 index=y*width+x）。</summary>
    [Serializable]
    public class TerrainDetailLayerStash
    {
        public int index;
        public string name;
        public int[] data;
    }

    /// <summary>details stash 文件内容。</summary>
    [Serializable]
    public class TerrainDetailsStash
    {
        public string type = "details";
        public string terrain;
        public int detailWidth;
        public int detailHeight;
        public int layerCount;
        public List<TerrainDetailLayerStash> layers = new List<TerrainDetailLayerStash>();
    }

    /// <summary>terrain.stash 返回结构。</summary>
    [Serializable]
    public class TerrainStashResult
    {
        public string terrain;
        public string type;     // "trees" / "details" / "all"
        public string name;
        public string path;     // 实际写入的文件路径（type=all 时以 "/" 分隔列出两个文件）
        public int treeInstances;
        public int detailLayers;
    }

    /// <summary>terrain.apply_stash 返回结构。</summary>
    [Serializable]
    public class TerrainApplyStashResult
    {
        public string terrain;
        public string type;     // 实际应用的类型（all 时仍返回 all）
        public string name;
        public string path;
        public int treeInstances;
        public int detailLayers;
    }

    /// <summary>terrain.stash_delete 返回结构。</summary>
    [Serializable]
    public class TerrainStashDeleteResult
    {
        public string type;
        public string name;
        public string path;
        public bool deleted;
    }

    /// <summary>stash 文件条目。</summary>
    [Serializable]
    public class TerrainStashEntry
    {
        public string type;     // "trees" / "details"
        public string name;     // 不含扩展名
        public string path;     // Assets 相对路径
        public long bytes;
    }

    /// <summary>terrain.stash_list 返回结构。</summary>
    [Serializable]
    public class TerrainStashListResult
    {
        public string stashDir;         // Assets 相对目录（工具内 stash 根目录）
        public int count;
        public List<TerrainStashEntry> entries = new List<TerrainStashEntry>();
    }

    // ============ 命令 ============

    /// <summary>
    /// Terrain 植被/树木 Stash 命令集。
    ///
    /// 目标：实现「stash → clear → 观察干净地形 → apply → 截图调整」的链路。
    ///   1. terrain.stash       把当前地形的树木实例 / 植被密度图全量序列化为 JSON 文件
    ///   2. terrain.apply_stash 读取 JSON 并整体写回地形（替换当前内容）
    ///   3. terrain.stash_delete 删除指定 stash 文件
    ///   4. terrain.stash_list   列出已有 stash 文件
    ///
    /// 存储位置：工具根目录下 <Assets>/unity-python-bridge/stash/{trees|details}/&lt;name&gt;.json
    ///   —— 按类分子目录；保存前检查同名，已存在则报错（不允许覆盖，避免误伤）。
    ///   文件为 UTF-8 JSON 文本，可进 git、可被 Python 侧直接读取。
    ///
    /// 参数（BridgeArgs）:
    ///   terrain (string, 可选) - 目标 Terrain 名称；省略取第一个。
    ///   type (string, 可选)    - "trees" / "details" / "all"（stash/apply 默认 "all"；delete/list 必填）。
    ///   name (string, 必填)    - stash 名称（不含扩展名，如 "forest_v1"）。
    /// </summary>
    public static class TerrainStashCommands
    {
        /// <summary>stash 根目录名（位于工具根目录下）。</summary>
        private const string StashRootDir = "stash";

        /// <summary>
        /// 工具根目录的 Assets 相对路径。
        /// 与 BridgeServer 读 bridge.ini 的约定一致：假定工具文件夹名为 unity-python-bridge 且直接位于 Assets/ 下；
        /// 若重命名工具文件夹，需同步修改此处。
        /// </summary>
        private static readonly string ToolRoot = "Assets/unity-python-bridge";

        private static string StashDirFor(string type)
        {
            return Path.Combine(ToolRoot, StashRootDir, type).Replace('\\', '/');
        }

        private static string StashPathFor(string type, string name)
        {
            return Path.Combine(StashDirFor(type), name + ".json").Replace('\\', '/');
        }

        private static string ValidateType(string type)
        {
            if (string.IsNullOrEmpty(type)) return "all";
            var t = type.Trim().ToLowerInvariant();
            if (t != "trees" && t != "details" && t != "all")
                throw new ArgumentException($"type 必须是 trees / details / all（当前: {type}）");
            return t;
        }

        private static string RequireName(BridgeArgs args)
        {
            if (string.IsNullOrWhiteSpace(args.name))
                throw new ArgumentException("需要参数 name（stash 名称，不含扩展名）");
            var n = args.name.Trim();
            if (n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || n.Contains('/') || n.Contains('\\'))
                throw new ArgumentException($"name 含非法字符: {n}");
            return n;
        }

        // ---------- terrain.stash ----------

        [BridgeCommand("terrain.stash",
            "把当前地形的树木实例/植被密度图全量序列化为 JSON 存到工具 stash 子目录（同名报错，不允许覆盖）。参数: terrain(string,可选), " +
            "type(string,可选 trees/details/all,默认all), name(string,必填,不含扩展名)")]
        public static object Stash(BridgeContext ctx, BridgeArgs args)
        {
            var terrain = SelectTerrain(args);
            var td = terrain.terrainData;
            string type = ValidateType(args.type);
            string name = RequireName(args);

            var result = new TerrainStashResult
            {
                terrain = terrain.gameObject.name,
                type = type,
                name = name,
            };

            var written = new List<string>();

            if (type == "trees" || type == "all")
            {
                var data = new TerrainTreesStash { terrain = terrain.gameObject.name };
                var prototypes = td.treePrototypes ?? new TreePrototype[0];
                data.prototypeCount = prototypes.Length;
                var instances = td.treeInstances ?? new TreeInstance[0];
                for (int i = 0; i < instances.Length; i++)
                {
                    var t = instances[i];
                    data.instances.Add(new StashedTreeInstance
                    {
                        prototypeIndex = t.prototypeIndex,
                        position = t.position,
                        widthScale = t.widthScale,
                        heightScale = t.heightScale,
                    });
                }
                result.treeInstances = data.instances.Count;
                var path = StashPathFor("trees", name);
                EnsureNotExists(path);
                WriteJson(path, data);
                written.Add(path);
            }

            if (type == "details" || type == "all")
            {
                var data = new TerrainDetailsStash
                {
                    terrain = terrain.gameObject.name,
                    detailWidth = td.detailWidth,
                    detailHeight = td.detailHeight,
                };
                var prototypes = td.detailPrototypes ?? new DetailPrototype[0];
                data.layerCount = prototypes.Length;
                for (int l = 0; l < prototypes.Length; l++)
                {
                    var raw = td.GetDetailLayer(0, 0, td.detailWidth, td.detailHeight, l);
                    var flat = new int[td.detailWidth * td.detailHeight];
                    for (int y = 0; y < td.detailHeight; y++)
                        for (int x = 0; x < td.detailWidth; x++)
                            flat[y * td.detailWidth + x] = raw[y, x];

                    string pname = "layer-" + l;
                    var p = prototypes[l];
                    if (p != null && p.prototype != null) pname = p.prototype.name;
                    else if (p != null && p.prototypeTexture != null) pname = p.prototypeTexture.name;
                    data.layers.Add(new TerrainDetailLayerStash { index = l, name = pname, data = flat });
                }
                result.detailLayers = data.layers.Count;
                var path = StashPathFor("details", name);
                EnsureNotExists(path);
                WriteJson(path, data);
                written.Add(path);
            }

            result.path = string.Join(", ", written);
            return result;
        }

        // ---------- terrain.apply_stash ----------

        [BridgeCommand("terrain.apply_stash",
            "读取 stash JSON 并整体写回地形（替换当前 trees/detail，配合 clear 后可恢复）。参数: terrain(string,可选), " +
            "type(string,可选 trees/details/all,默认all), name(string,必填)")]
        public static object ApplyStash(BridgeContext ctx, BridgeArgs args)
        {
            var terrain = SelectTerrain(args);
            var td = terrain.terrainData;
            string type = ValidateType(args.type);
            string name = RequireName(args);

            var result = new TerrainApplyStashResult
            {
                terrain = terrain.gameObject.name,
                type = type,
                name = name,
            };

            var applied = new List<string>();

            if (type == "trees" || type == "all")
            {
                var path = StashPathFor("trees", name);
                var data = ReadJson<TerrainTreesStash>(path, "trees");
                int curCount = td.treePrototypes?.Length ?? 0;
                if (data.prototypeCount != curCount)
                    throw new InvalidOperationException(
                        $"stash '{name}' 的树原型数({data.prototypeCount})与当前地形({curCount})不一致，拒绝应用");
                var list = new List<TreeInstance>(data.instances.Count);
                for (int i = 0; i < data.instances.Count; i++)
                {
                    var s = data.instances[i];
                    if (s.prototypeIndex < 0 || s.prototypeIndex >= curCount)
                        throw new InvalidOperationException(
                            $"stash '{name}' 第 {i} 棵树的 prototypeIndex={s.prototypeIndex} 越界（共 {curCount} 个原型）");
                    list.Add(new TreeInstance
                    {
                        prototypeIndex = s.prototypeIndex,
                        position = s.position,
                        widthScale = s.widthScale,
                        heightScale = s.heightScale,
                        color = Color.white,
                        lightmapColor = Color.white,
                    });
                }
                td.SetTreeInstances(list.ToArray(), true);   // snapToHeightmap=true
                EditorUtility.SetDirty(td);
                result.treeInstances = list.Count;
                applied.Add(path);
            }

            if (type == "details" || type == "all")
            {
                var path = StashPathFor("details", name);
                var data = ReadJson<TerrainDetailsStash>(path, "details");
                if (data.layerCount != (td.detailPrototypes?.Length ?? 0))
                    throw new InvalidOperationException(
                        $"stash '{name}' 的草原型数({data.layerCount})与当前地形({td.detailPrototypes?.Length ?? 0})不一致，拒绝应用");
                if (data.detailWidth != td.detailWidth || data.detailHeight != td.detailHeight)
                    throw new InvalidOperationException(
                        $"stash '{name}' 的分辨率({data.detailWidth}x{data.detailHeight})与当前地形({td.detailWidth}x{td.detailHeight})不一致，拒绝应用");
                foreach (var layer in data.layers)
                {
                    if (layer.index < 0 || layer.index >= data.layerCount)
                        throw new InvalidOperationException($"stash '{name}' 含非法 layer={layer.index}");
                    var raw = new int[td.detailHeight, td.detailWidth];
                    var src = layer.data ?? new int[0];
                    for (int y = 0; y < td.detailHeight; y++)
                        for (int x = 0; x < td.detailWidth; x++)
                        {
                            int idx = y * td.detailWidth + x;
                            raw[y, x] = idx < src.Length ? Mathf.Clamp(src[idx], 0, 16) : 0;
                        }
                    td.SetDetailLayer(0, 0, layer.index, raw);
                }
                EditorUtility.SetDirty(td);
                result.detailLayers = data.layers.Count;
                applied.Add(path);
            }

            result.path = string.Join(", ", applied);
            return result;
        }

        // ---------- terrain.stash_delete ----------

        [BridgeCommand("terrain.stash_delete",
            "删除指定 stash 文件。参数: type(string,必填 trees/details/all), name(string,必填)")]
        public static object StashDelete(BridgeContext ctx, BridgeArgs args)
        {
            string type = ValidateType(args.type);
            if (type == "all")
                throw new ArgumentException("stash_delete 的 type 必须是 trees 或 details（不能是 all）");
            string name = RequireName(args);
            var path = StashPathFor(type, name);
            var abs = Path.Combine(Directory.GetCurrentDirectory(), path);
            if (!File.Exists(abs))
                throw new InvalidOperationException($"stash 文件不存在: {path}");
            File.Delete(abs);
            AssetDatabase.Refresh();
            return new TerrainStashDeleteResult { type = type, name = name, path = path, deleted = true };
        }

        // ---------- terrain.stash_list ----------

        [BridgeCommand("terrain.stash_list",
            "列出工具 stash 子目录下所有 stash 文件。参数: type(string,可选 trees/details/all,默认all)")]
        public static object StashList(BridgeContext ctx, BridgeArgs args)
        {
            string type = ValidateType(args.type);
            var result = new TerrainStashListResult
            {
                stashDir = StashDirFor(type),
            };

            var dirs = new List<string>();
            if (type == "all") { dirs.Add("trees"); dirs.Add("details"); }
            else dirs.Add(type);

            foreach (var d in dirs)
            {
                var absDir = Path.Combine(Directory.GetCurrentDirectory(), StashDirFor(d));
                if (!Directory.Exists(absDir)) continue;
                foreach (var f in Directory.GetFiles(absDir, "*.json"))
                {
                    var fi = new FileInfo(f);
                    result.entries.Add(new TerrainStashEntry
                    {
                        type = d,
                        name = Path.GetFileNameWithoutExtension(f),
                        path = Path.Combine(StashDirFor(d), fi.Name).Replace('\\', '/'),
                        bytes = fi.Length,
                    });
                }
            }
            result.entries.Sort((a, b) => string.Compare(a.type + "/" + a.name, b.type + "/" + b.name, StringComparison.OrdinalIgnoreCase));
            result.count = result.entries.Count;
            return result;
        }

        // ---------- 内部工具 ----------

        private static void EnsureNotExists(string assetPath)
        {
            var abs = Path.Combine(Directory.GetCurrentDirectory(), assetPath);
            if (File.Exists(abs))
                throw new InvalidOperationException($"同名 stash 已存在，拒绝覆盖: {assetPath}（如需替换请先删除）");
        }

        private static void WriteJson(string assetPath, object data)
        {
            var abs = Path.Combine(Directory.GetCurrentDirectory(), assetPath);
            var dir = Path.GetDirectoryName(abs);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(abs, JsonUtility.ToJson(data, true));
            AssetDatabase.Refresh();
        }

        private static T ReadJson<T>(string assetPath, string type) where T : class
        {
            var abs = Path.Combine(Directory.GetCurrentDirectory(), assetPath);
            if (!File.Exists(abs))
                throw new InvalidOperationException($"stash 文件不存在（{type}）: {assetPath}（请先用 terrain.stash 保存）");
            try
            {
                var obj = JsonUtility.FromJson<T>(File.ReadAllText(abs));
                if (obj == null) throw new InvalidOperationException($"stash 文件解析失败: {assetPath}");
                return obj;
            }
            catch (Exception e) when (!(e is InvalidOperationException))
            {
                throw new InvalidOperationException($"stash 文件解析失败: {assetPath} ({e.Message})");
            }
        }

        private static List<Terrain> FindTerrains()
        {
#if UNITY_2023_1_OR_NEWER
            return new List<Terrain>(
                UnityEngine.Object.FindObjectsByType<Terrain>(FindObjectsInactive.Include, FindObjectsSortMode.None));
#else
            return new List<Terrain>(UnityEngine.Object.FindObjectsOfType<Terrain>(true));
#endif
        }

        private static Terrain SelectTerrain(BridgeArgs args)
        {
            var terrains = FindTerrains();
            if (terrains.Count == 0)
                throw new InvalidOperationException("场景中没有任何 Terrain 组件");

            var name = args.terrain;
            if (string.IsNullOrEmpty(name))
                return terrains[0];

            foreach (var t in terrains)
            {
                if (t.gameObject.name == name)
                    return t;
            }
            throw new InvalidOperationException($"场景中未找到名为 '{name}' 的 Terrain（共有 {terrains.Count} 个）");
        }
    }
}
#endif // UNITY_EDITOR
