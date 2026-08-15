using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityPythonBridge.Commands
{
    // ============ DTO ============

    [Serializable]
    public class TerrainInfo
    {
        public string name;
        public Vector3 position;
        public Vector3 size;
        public int heightmapResolution;
        public int alphamapResolution;
        public int detailResolution;
        public int holesResolution;
        public int layers;
        public int detailPrototypeCount;
        public int treePrototypeCount;
        public int treeInstanceCount;
    }

    [Serializable]
    public class TerrainListResult
    {
        public int count;
        public List<TerrainInfo> terrains = new List<TerrainInfo>();
    }

    [Serializable]
    public class TerrainRegionResult
    {
        public string terrain;
        public int xBase;
        public int zBase;
        public int width;
        public int height;
        public float[] data;    // 行优先：index = y * width + x
        public int count;
    }

    [Serializable]
    public class TerrainSetRegionResult
    {
        public string terrain;
        public int xBase;
        public int zBase;
        public int width;
        public int height;
        public int cells;
        public string mode;     // "data" / "noise"
    }

    [Serializable]
    public class TerrainLayerInfo
    {
        public int index;
        public string name;
        public string diffuseTexture;
    }

    [Serializable]
    public class TerrainLayersResult
    {
        public string terrain;
        public int count;
        public List<TerrainLayerInfo> layers = new List<TerrainLayerInfo>();
    }

    [Serializable]
    public class TerrainDiffuseDirInfo
    {
        public int index;
        public string name;
        public string diffuseTexture;   // 完整贴图路径（AssetDatabase 路径）
        public string diffuseDir;       // 贴图所在目录
    }

    [Serializable]
    public class TerrainDiffuseDirsResult
    {
        public string terrain;
        public int count;               // TerrainLayer 数量
        public int directoryCount;      // 去重后的目录数量
        public List<string> directories = new List<string>();          // 去重目录列表
        public List<TerrainDiffuseDirInfo> layers = new List<TerrainDiffuseDirInfo>();
    }

    [Serializable]
    public class TerrainAlphamapResult
    {
        public string terrain;
        public int xBase;
        public int zBase;
        public int width;
        public int height;
        public int layers;
        public float[] data;    // index = (y * width + x) * layers + layer
        public int count;
    }

    [Serializable]
    public class TerrainSetAlphamapResult
    {
        public string terrain;
        public int xBase;
        public int zBase;
        public int width;
        public int height;
        public int layers;
        public int cells;
        public bool normalized;
    }

    [Serializable]
    public class TerrainDetailInfo
    {
        public int index;
        public string name;
    }

    [Serializable]
    public class TerrainDetailsResult
    {
        public string terrain;
        public int count;
        public List<TerrainDetailInfo> details = new List<TerrainDetailInfo>();
    }

    [Serializable]
    public class TerrainDetailDataResult
    {
        public string terrain;
        public int layer;
        public int xBase;
        public int zBase;
        public int width;
        public int height;
        public int[] data;      // 行优先：index = y * width + x
        public int count;
    }

    [Serializable]
    public class TerrainSetDetailResult
    {
        public string terrain;
        public int layer;
        public int xBase;
        public int zBase;
        public int width;
        public int height;
        public int cells;
        public string mode;     // "data" / "random"
    }

    [Serializable]
    public class TerrainTreePrototypeInfo
    {
        public int index;
        public string name;
    }

    [Serializable]
    public class TerrainTreePrefabInfo
    {
        public int index;
        public string name;
        public string prefab;   // 预制体完整路径（AssetDatabase 路径）
        public string prefabDir; // 预制体所在目录
    }

    [Serializable]
    public class TerrainTreePrefabDirsResult
    {
        public string terrain;
        public int count;               // TreePrototype 数量
        public int directoryCount;      // 去重后的目录数量
        public List<string> directories = new List<string>();
        public List<TerrainTreePrefabInfo> trees = new List<TerrainTreePrefabInfo>();
    }

    [Serializable]
    public class TerrainDetailAssetInfo
    {
        public int index;
        public string name;
        public string type;     // "prefab" / "texture" / "none"
        public string asset;    // 预制体或贴图的完整路径
        public string assetDir; // 所在目录
    }

    [Serializable]
    public class TerrainDetailAssetDirsResult
    {
        public string terrain;
        public int count;               // DetailPrototype 数量
        public int directoryCount;      // 去重后的目录数量
        public List<string> directories = new List<string>();
        public List<TerrainDetailAssetInfo> details = new List<TerrainDetailAssetInfo>();
    }

    [Serializable]
    public class TerrainTreeInstanceInfo
    {
        public int index;
        public int prototypeIndex;
        public Vector3 position;    // 归一化 0~1
        public float widthScale;
        public float heightScale;
    }

    [Serializable]
    public class TerrainTreesResult
    {
        public string terrain;
        public int prototypeCount;
        public int instanceCount;
        public List<TerrainTreePrototypeInfo> prototypes = new List<TerrainTreePrototypeInfo>();
        public List<TerrainTreeInstanceInfo> instances = new List<TerrainTreeInstanceInfo>();
    }

    [Serializable]
    public class TerrainAddTreesResult
    {
        public string terrain;
        public int prototypeIndex;
        public int added;
        public int total;
        public string mode;     // "positions" / "random"
    }

    [Serializable]
    public class TerrainClearTreesResult
    {
        public string terrain;
        public int removed;
    }

    // ============ 命令 ============

    /// <summary>
    /// Terrain 程序化编辑命令集。全部基于 Unity 原生 TerrainData API：
    ///   - 高度图：terrain.get_heights / terrain.set_heights（支持数组直写或 Perlin 噪声生成）
    ///   - 纹理：terrain.get_layers / terrain.get_alphamaps / terrain.set_alphamaps
    ///   - 植被：terrain.list_details / terrain.get_details / terrain.set_details
    ///   - 树木：terrain.list_trees / terrain.add_trees / terrain.clear_trees
    /// 公共参数：
    ///   terrain (string, 可选) —— 目标 Terrain 的 GameObject 名称；省略时取场景中第一个 Terrain。
    ///   区域参数 xBase/zBase/width/height (int, 可选) —— 操作区域；省略时默认整图。
    /// </summary>
    public static class TerrainCommands
    {
        // ---------- 工具 ----------

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

        private static void ValidateRegion(BridgeArgs args, int maxW, int maxH)
        {
            int xb = args.xBase, zb = args.zBase;
            int w = args.width, h = args.height;
            if (w <= 0) w = maxW - xb;
            if (h <= 0) h = maxH - zb;
            if (xb < 0 || zb < 0 || xb + w > maxW || zb + h > maxH)
                throw new ArgumentException($"区域越界: xBase={xb} zBase={zb} w={w} h={h}（最大 {maxW}x{maxH}）");
        }

        // ---------- terrain.list ----------

        [BridgeCommand("terrain.list", "列出场景中所有 Terrain 的名称、位置、尺寸与分辨率。参数: terrain(string,可选)")]
        public static object List(BridgeContext ctx, BridgeArgs args)
        {
            var result = new TerrainListResult();
            foreach (var t in FindTerrains())
            {
                var td = t.terrainData;
                result.terrains.Add(new TerrainInfo
                {
                    name = t.gameObject.name,
                    position = t.transform.position,
                    size = td.size,
                    heightmapResolution = td.heightmapResolution,
                    alphamapResolution = td.alphamapResolution,
                    detailResolution = td.detailResolution,
                    holesResolution = td.holesResolution,
                    layers = td.terrainLayers != null ? td.terrainLayers.Length : 0,
                    detailPrototypeCount = td.detailPrototypes != null ? td.detailPrototypes.Length : 0,
                    treePrototypeCount = td.treePrototypes != null ? td.treePrototypes.Length : 0,
                    treeInstanceCount = td.treeInstances != null ? td.treeInstances.Length : 0,
                });
            }
            result.count = result.terrains.Count;
            return result;
        }

        // ---------- 高度图 ----------

        [BridgeCommand("terrain.get_heights", "读取高度图区域。参数: terrain(string,可选), xBase,zBase,width,height(int,可选,默认全图)。data 行优先 index=y*width+x，值 0~1")]
        public static object GetHeights(BridgeContext ctx, BridgeArgs args)
        {
            var terrain = SelectTerrain(args);
            var td = terrain.terrainData;
            int res = td.heightmapResolution;
            int xb = args.xBase, zb = args.zBase;
            int w = args.width > 0 ? args.width : res - xb;
            int h = args.height > 0 ? args.height : res - zb;
            ValidateRegion(args, res, res);

            var raw = td.GetHeights(xb, zb, w, h);   // raw[y, x]
            var data = new float[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    data[y * w + x] = raw[y, x];

            return new TerrainRegionResult
            {
                terrain = terrain.gameObject.name,
                xBase = xb, zBase = zb, width = w, height = h,
                data = data, count = data.Length,
            };
        }

        [BridgeCommand("terrain.set_heights", "写入高度图。参数: terrain(string,可选), xBase,zBase,width,height, data(float[] 行优先 0~1)；或 noise=true + noiseScale/noiseSeed/baseHeight/heightScale 用 Perlin 噪声生成")]
        public static object SetHeights(BridgeContext ctx, BridgeArgs args)
        {
            var terrain = SelectTerrain(args);
            var td = terrain.terrainData;
            int res = td.heightmapResolution;
            int xb = args.xBase, zb = args.zBase;
            int w = args.width > 0 ? args.width : res - xb;
            int h = args.height > 0 ? args.height : res - zb;
            ValidateRegion(args, res, res);

            var heights = new float[h, w];
            string mode;

            if (args.noise)
            {
                // Perlin 噪声生成（可复现：seed 固定）
                mode = "noise";
                var rng = new System.Random(args.noiseSeed);
                float ox = (float)(rng.NextDouble() * 1000.0);
                float oz = (float)(rng.NextDouble() * 1000.0);
                float scale = args.noiseScale > 0 ? args.noiseScale : 1f;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        float nx = (xb + x) * scale + ox;
                        float nz = (zb + y) * scale + oz;
                        heights[y, x] = Mathf.Clamp01(args.baseHeight + args.heightScale * Mathf.PerlinNoise(nx, nz));
                    }
                }
            }
            else
            {
                mode = "data";
                var data = args.data;
                if (data == null || data.Length != w * h)
                    throw new ArgumentException($"data 长度必须为 width*height={w * h}（当前 {data?.Length ?? 0}）");
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        heights[y, x] = Mathf.Clamp01(data[y * w + x]);
            }

            td.SetHeights(xb, zb, heights);
            terrain.ApplyDelayedHeightmapModification();  // 立即应用（含碰撞体）
            EditorUtility.SetDirty(td);

            return new TerrainSetRegionResult
            {
                terrain = terrain.gameObject.name,
                xBase = xb, zBase = zb, width = w, height = h,
                cells = w * h, mode = mode,
            };
        }

        // ---------- 纹理 ----------

        [BridgeCommand("terrain.get_layers", "列出 Terrain 的纹理层（TerrainLayer）。参数: terrain(string,可选)")]
        public static object GetLayers(BridgeContext ctx, BridgeArgs args)
        {
            var terrain = SelectTerrain(args);
            var td = terrain.terrainData;
            var result = new TerrainLayersResult { terrain = terrain.gameObject.name };
            var layers = td.terrainLayers ?? new TerrainLayer[0];
            for (int i = 0; i < layers.Length; i++)
            {
                var layer = layers[i];
                result.layers.Add(new TerrainLayerInfo
                {
                    index = i,
                    name = layer != null ? layer.name : "(null)",
                    diffuseTexture = layer != null && layer.diffuseTexture != null
                        ? AssetDatabase.GetAssetPath(layer.diffuseTexture) : "",
                });
            }
            result.count = result.layers.Count;
            return result;
        }

        [BridgeCommand("terrain.get_diffuse_dirs",
            "返回 Terrain 所有 TerrainLayer 的 Diffuse 贴图目录（去重）及各层贴图完整路径。参数: terrain(string,可选)")]
        public static object GetDiffuseDirs(BridgeContext ctx, BridgeArgs args)
        {
            var terrain = SelectTerrain(args);
            var td = terrain.terrainData;
            var result = new TerrainDiffuseDirsResult { terrain = terrain.gameObject.name };
            var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var layers = td.terrainLayers ?? new TerrainLayer[0];
            for (int i = 0; i < layers.Length; i++)
            {
                var layer = layers[i];
                string texPath = "";
                string dir = "";
                if (layer != null && layer.diffuseTexture != null)
                {
                    texPath = AssetDatabase.GetAssetPath(layer.diffuseTexture);
                    if (!string.IsNullOrEmpty(texPath))
                    {
                        dir = System.IO.Path.GetDirectoryName(texPath) ?? "";
                        if (!string.IsNullOrEmpty(dir))
                            dirs.Add(dir.Replace('\\', '/'));
                    }
                }
                result.layers.Add(new TerrainDiffuseDirInfo
                {
                    index = i,
                    name = layer != null ? layer.name : "(null)",
                    diffuseTexture = texPath,
                    diffuseDir = dir.Replace('\\', '/'),
                });
            }
            result.directories = new List<string>(dirs);
            result.directories.Sort(StringComparer.OrdinalIgnoreCase);
            result.count = result.layers.Count;
            result.directoryCount = result.directories.Count;
            return result;
        }

        [BridgeCommand("terrain.get_tree_prefab_dirs",
            "返回 Terrain 所有树原型（TreePrototype）的 Prefab 目录（去重）及各预制体完整路径。参数: terrain(string,可选)")]
        public static object GetTreePrefabDirs(BridgeContext ctx, BridgeArgs args)
        {
            var terrain = SelectTerrain(args);
            var td = terrain.terrainData;
            var result = new TerrainTreePrefabDirsResult { terrain = terrain.gameObject.name };
            var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var prototypes = td.treePrototypes ?? new TreePrototype[0];
            for (int i = 0; i < prototypes.Length; i++)
            {
                var p = prototypes[i];
                string prefabPath = "";
                string dir = "";
                if (p != null && p.prefab != null)
                {
                    prefabPath = AssetDatabase.GetAssetPath(p.prefab);
                    if (!string.IsNullOrEmpty(prefabPath))
                    {
                        dir = System.IO.Path.GetDirectoryName(prefabPath) ?? "";
                        if (!string.IsNullOrEmpty(dir))
                            dirs.Add(dir.Replace('\\', '/'));
                    }
                }
                result.trees.Add(new TerrainTreePrefabInfo
                {
                    index = i,
                    name = p != null && p.prefab != null ? p.prefab.name : "(null)",
                    prefab = prefabPath,
                    prefabDir = dir.Replace('\\', '/'),
                });
            }
            result.directories = new List<string>(dirs);
            result.directories.Sort(StringComparer.OrdinalIgnoreCase);
            result.count = result.trees.Count;
            result.directoryCount = result.directories.Count;
            return result;
        }

        [BridgeCommand("terrain.get_detail_asset_dirs",
            "返回 Terrain 所有草原型（DetailPrototype）的预制体或贴图目录（去重）及各自完整路径。参数: terrain(string,可选)")]
        public static object GetDetailAssetDirs(BridgeContext ctx, BridgeArgs args)
        {
            var terrain = SelectTerrain(args);
            var td = terrain.terrainData;
            var result = new TerrainDetailAssetDirsResult { terrain = terrain.gameObject.name };
            var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var prototypes = td.detailPrototypes ?? new DetailPrototype[0];
            for (int i = 0; i < prototypes.Length; i++)
            {
                var p = prototypes[i];
                string type = "none";
                string assetPath = "";
                string name = "(null)";
                string dir = "";

                if (p != null)
                {
                    if (p.prototype != null)
                    {
                        type = "prefab";
                        name = p.prototype.name;
                        assetPath = AssetDatabase.GetAssetPath(p.prototype);
                    }
                    else if (p.prototypeTexture != null)
                    {
                        type = "texture";
                        name = p.prototypeTexture.name;
                        assetPath = AssetDatabase.GetAssetPath(p.prototypeTexture);
                    }

                    if (!string.IsNullOrEmpty(assetPath))
                    {
                        dir = System.IO.Path.GetDirectoryName(assetPath) ?? "";
                        if (!string.IsNullOrEmpty(dir))
                            dirs.Add(dir.Replace('\\', '/'));
                    }
                }

                result.details.Add(new TerrainDetailAssetInfo
                {
                    index = i,
                    name = name,
                    type = type,
                    asset = assetPath,
                    assetDir = dir.Replace('\\', '/'),
                });
            }
            result.directories = new List<string>(dirs);
            result.directories.Sort(StringComparer.OrdinalIgnoreCase);
            result.count = result.details.Count;
            result.directoryCount = result.directories.Count;
            return result;
        }

        [BridgeCommand("terrain.get_alphamaps", "读取纹理混合权重。参数: terrain(string,可选), xBase,zBase,width,height(可选,默认全图)。data index=(y*width+x)*layers+layer")]
        public static object GetAlphamaps(BridgeContext ctx, BridgeArgs args)
        {
            var terrain = SelectTerrain(args);
            var td = terrain.terrainData;
            int res = td.alphamapResolution;
            int xb = args.xBase, zb = args.zBase;
            int w = args.width > 0 ? args.width : res - xb;
            int h = args.height > 0 ? args.height : res - zb;
            ValidateRegion(args, res, res);
            int layers = td.alphamapLayers;
            if (layers == 0)
                throw new InvalidOperationException("该 Terrain 没有任何纹理层（请先添加 TerrainLayer）");

            var raw = td.GetAlphamaps(xb, zb, w, h);   // raw[y, x, layer]
            var data = new float[w * h * layers];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    for (int l = 0; l < layers; l++)
                        data[(y * w + x) * layers + l] = raw[y, x, l];

            return new TerrainAlphamapResult
            {
                terrain = terrain.gameObject.name,
                xBase = xb, zBase = zb, width = w, height = h, layers = layers,
                data = data, count = data.Length,
            };
        }

        [BridgeCommand("terrain.set_alphamaps", "写入纹理混合权重（每像素自动归一化到和为 1）。参数: terrain(string,可选), xBase,zBase,width,height, data(float[], index=(y*width+x)*layers+layer)")]
        public static object SetAlphamaps(BridgeContext ctx, BridgeArgs args)
        {
            var terrain = SelectTerrain(args);
            var td = terrain.terrainData;
            int res = td.alphamapResolution;
            int xb = args.xBase, zb = args.zBase;
            int w = args.width > 0 ? args.width : res - xb;
            int h = args.height > 0 ? args.height : res - zb;
            ValidateRegion(args, res, res);
            int layers = td.alphamapLayers;
            if (layers == 0)
                throw new InvalidOperationException("该 Terrain 没有任何纹理层（请先添加 TerrainLayer）");

            var data = args.data;
            if (data == null || data.Length != w * h * layers)
                throw new ArgumentException($"data 长度必须为 width*height*layers={w * h * layers}（当前 {data?.Length ?? 0}）");

            var map = new float[h, w, layers];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float sum = 0f;
                    for (int l = 0; l < layers; l++)
                        sum += data[(y * w + x) * layers + l];
                    bool normalize = sum > 1.0001f;
                    for (int l = 0; l < layers; l++)
                    {
                        float v = data[(y * w + x) * layers + l];
                        map[y, x, l] = normalize ? v / sum : v;
                    }
                }
            }

            td.SetAlphamaps(xb, zb, map);
            EditorUtility.SetDirty(td);

            return new TerrainSetAlphamapResult
            {
                terrain = terrain.gameObject.name,
                xBase = xb, zBase = zb, width = w, height = h, layers = layers,
                cells = w * h, normalized = true,
            };
        }

        // ---------- 植被 ----------

        [BridgeCommand("terrain.list_details", "列出 Terrain 的草原型（DetailPrototype）。参数: terrain(string,可选)")]
        public static object ListDetails(BridgeContext ctx, BridgeArgs args)
        {
            var terrain = SelectTerrain(args);
            var td = terrain.terrainData;
            var result = new TerrainDetailsResult { terrain = terrain.gameObject.name };
            var prototypes = td.detailPrototypes ?? new DetailPrototype[0];
            for (int i = 0; i < prototypes.Length; i++)
            {
                var p = prototypes[i];
                string name = "prototype-" + i;
                if (p != null && p.prototype != null) name = p.prototype.name;
                else if (p != null && p.prototypeTexture != null) name = p.prototypeTexture.name;
                result.details.Add(new TerrainDetailInfo { index = i, name = name });
            }
            result.count = result.details.Count;
            return result;
        }

        [BridgeCommand("terrain.get_details", "读取某层植被密度图。参数: terrain(string,可选), layer(int), xBase,zBase,width,height(可选,默认全图)。data 行优先 index=y*width+x")]
        public static object GetDetails(BridgeContext ctx, BridgeArgs args)
        {
            var terrain = SelectTerrain(args);
            var td = terrain.terrainData;
            if (args.layer < 0 || args.layer >= td.detailPrototypes.Length)
                throw new ArgumentException($"layer={args.layer} 越界（共 {td.detailPrototypes.Length} 个草原型）");

            int resW = td.detailWidth, resH = td.detailHeight;
            int xb = args.xBase, zb = args.zBase;
            int w = args.width > 0 ? args.width : resW - xb;
            int h = args.height > 0 ? args.height : resH - zb;
            ValidateRegion(args, resW, resH);

            var raw = td.GetDetailLayer(xb, zb, w, h, args.layer);   // raw[y, x]
            var data = new int[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    data[y * w + x] = raw[y, x];

            return new TerrainDetailDataResult
            {
                terrain = terrain.gameObject.name,
                layer = args.layer,
                xBase = xb, zBase = zb, width = w, height = h,
                data = data, count = data.Length,
            };
        }

        [BridgeCommand("terrain.set_details", "写入植被密度。参数: terrain(string,可选), layer(int), xBase,zBase,width,height, data(int[] 行优先 0~16)；或 random=true + count/seed/density 随机撒点")]
        public static object SetDetails(BridgeContext ctx, BridgeArgs args)
        {
            var terrain = SelectTerrain(args);
            var td = terrain.terrainData;
            if (args.layer < 0 || args.layer >= td.detailPrototypes.Length)
                throw new ArgumentException($"layer={args.layer} 越界（共 {td.detailPrototypes.Length} 个草原型）");

            int resW = td.detailWidth, resH = td.detailHeight;
            int xb = args.xBase, zb = args.zBase;
            int w = args.width > 0 ? args.width : resW - xb;
            int h = args.height > 0 ? args.height : resH - zb;
            ValidateRegion(args, resW, resH);

            var data = new int[h, w];
            string mode;
            if (args.random)
            {
                mode = "random";
                var rng = new System.Random(args.seed);
                int n = args.count;
                for (int i = 0; i < n; i++)
                {
                    int x = xb + rng.Next(w);
                    int y = zb + rng.Next(h);
                    data[y - zb, x - xb] = args.density;
                }
            }
            else
            {
                mode = "data";
                var src = args.dataInt;
                if (src == null || src.Length != w * h)
                    throw new ArgumentException($"data 长度必须为 width*height={w * h}（当前 {src?.Length ?? 0}）");
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        data[y, x] = Mathf.Clamp(src[y * w + x], 0, 16);
            }

            td.SetDetailLayer(xb, zb, args.layer, data);
            EditorUtility.SetDirty(td);

            return new TerrainSetDetailResult
            {
                terrain = terrain.gameObject.name,
                layer = args.layer,
                xBase = xb, zBase = zb, width = w, height = h,
                cells = w * h, mode = mode,
            };
        }

        // ---------- 树木 ----------

        [BridgeCommand("terrain.list_trees", "列出 Terrain 的树原型与树实例。参数: terrain(string,可选)")]
        public static object ListTrees(BridgeContext ctx, BridgeArgs args)
        {
            var terrain = SelectTerrain(args);
            var td = terrain.terrainData;
            var result = new TerrainTreesResult { terrain = terrain.gameObject.name };

            var prototypes = td.treePrototypes ?? new TreePrototype[0];
            for (int i = 0; i < prototypes.Length; i++)
            {
                var p = prototypes[i];
                result.prototypes.Add(new TerrainTreePrototypeInfo
                {
                    index = i,
                    name = p != null && p.prefab != null ? p.prefab.name : "(null)",
                });
            }
            result.prototypeCount = result.prototypes.Count;

            var instances = td.treeInstances ?? new TreeInstance[0];
            for (int i = 0; i < instances.Length; i++)
            {
                var t = instances[i];
                result.instances.Add(new TerrainTreeInstanceInfo
                {
                    index = i,
                    prototypeIndex = t.prototypeIndex,
                    position = t.position,
                    widthScale = t.widthScale,
                    heightScale = t.heightScale,
                });
            }
            result.instanceCount = result.instances.Count;
            return result;
        }

        [BridgeCommand("terrain.add_trees", "添加树木。参数: terrain(string,可选), prototypeIndex(int), positions(float[] 每3个一组 {x,y,z} 归一化 0~1)；或 random=true + count/seed/minScale/maxScale 随机种植")]
        public static object AddTrees(BridgeContext ctx, BridgeArgs args)
        {
            var terrain = SelectTerrain(args);
            var td = terrain.terrainData;
            int pi = args.prototypeIndex;
            if (pi < 0 || pi >= (td.treePrototypes?.Length ?? 0))
                throw new ArgumentException($"prototypeIndex={pi} 越界（共 {td.treePrototypes?.Length ?? 0} 个树原型）");

            var rng = new System.Random(args.seed);
            var list = new List<TreeInstance>(td.treeInstances ?? new TreeInstance[0]);
            string mode;
            int added = 0;

            if (args.positions != null && args.positions.Length > 0)
            {
                mode = "positions";
                if (args.positions.Length % 3 != 0)
                    throw new ArgumentException("positions 长度必须是 3 的倍数（每 3 个一组 {x,y,z}）");
                for (int i = 0; i < args.positions.Length; i += 3)
                {
                    var pos = new Vector3(
                        Mathf.Clamp01(args.positions[i]),
                        Mathf.Clamp01(args.positions[i + 1]),
                        Mathf.Clamp01(args.positions[i + 2]));
                    list.Add(MakeTree(pi, pos, args.minScale, args.maxScale, rng));
                    added++;
                }
            }
            else
            {
                mode = "random";
                int n = args.count;
                for (int i = 0; i < n; i++)
                {
                    var pos = new Vector3((float)rng.NextDouble(), 0.5f, (float)rng.NextDouble());
                    list.Add(MakeTree(pi, pos, args.minScale, args.maxScale, rng));
                    added++;
                }
            }

            td.SetTreeInstances(list.ToArray(), true);   // snapToHeightmap=true，自动贴地
            EditorUtility.SetDirty(td);

            return new TerrainAddTreesResult
            {
                terrain = terrain.gameObject.name,
                prototypeIndex = pi,
                added = added,
                total = list.Count,
                mode = mode,
            };
        }

        private static TreeInstance MakeTree(int prototypeIndex, Vector3 pos, float minScale, float maxScale, System.Random rng)
        {
            float min = minScale > 0 ? minScale : 0.8f;
            float max = maxScale >= min ? maxScale : 1.2f;
            float scale = (float)(min + rng.NextDouble() * (max - min));
            return new TreeInstance
            {
                prototypeIndex = prototypeIndex,
                position = pos,
                widthScale = scale,
                heightScale = scale,
                color = Color.white,
                lightmapColor = Color.white,
            };
        }

        [BridgeCommand("terrain.clear_trees", "清空 Terrain 上所有树实例。参数: terrain(string,可选)")]
        public static object ClearTrees(BridgeContext ctx, BridgeArgs args)
        {
            var terrain = SelectTerrain(args);
            var td = terrain.terrainData;
            int removed = td.treeInstances?.Length ?? 0;
            td.SetTreeInstances(new TreeInstance[0], false);
            EditorUtility.SetDirty(td);

            return new TerrainClearTreesResult
            {
                terrain = terrain.gameObject.name,
                removed = removed,
            };
        }
    }
}
