#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityPythonBridge.Commands
{
    /// <summary>prefab.create_object / prefab.create_primitive 返回结构。</summary>
    [System.Serializable]
    public class PrefabObjectResult
    {
        public string prefab;   // 被编辑的 Prefab 资产路径
        public string target;   // 新建物体在 Prefab 内部的路径（根为 ""）
        public string type;     // 几何体类型；空物体为空串
        public bool saved;      // 是否已写回资产
    }

    /// <summary>prefab.add_component 返回结构。</summary>
    [System.Serializable]
    public class PrefabComponentResult
    {
        public string prefab;
        public string target;     // 目标物体在 Prefab 内部的路径
        public string component;  // 组件全名
        public bool added;        // 本次是否真的新加了（已存在则为 false）
        public bool saved;
    }

    /// <summary>
    /// Prefab 资产内部的对象级编辑：新建空物体 / 创建原生几何体 / 加组件 / 写属性。
    ///
    /// 与 prefab.edit 同源：都是 LoadPrefabContents → 改 → SaveAsPrefabAsset → UnloadPrefabContents，
    /// **直接改并保存资产**，不经场景，不能用场景 Ctrl+Z 回退。
    /// target 统一为「Prefab 内部相对路径」，空 = 根节点。
    ///
    /// ⚠️ 已知副作用：新建物体时 Unity 没有"直接往指定场景里造物体"的公开 API，
    /// 只能先 new 出来（落在**活动场景**）再搬进 Prefab 的预览场景，因此活动场景可能被标脏
    /// （实际内容没有变化）。见 AttachInsidePrefab 的说明。
    /// </summary>
    public static class PrefabObjectCommands
    {
        // ---------- prefab.create_object ----------

        [BridgeCommand("prefab.create_object",
            "在 Prefab 资产内部新建空物体（常作组件载体；直接保存资产）。参数: " +
            "path(string,必填,Prefab 资产路径), target(string,可选,内部父物体路径,空=根节点), " +
            "name(string,必填,新物体名称), position/rotation/scale(float[]3,可选), quaternion(bool,可选)")]
        public static object CreateObject(BridgeContext ctx, BridgeArgs args)
        {
            if (string.IsNullOrWhiteSpace(args.name))
                throw new ArgumentException("prefab.create_object 需要参数 name（新物体名称）");

            return InPrefab(args.path, root =>
            {
                var parent = PrefabEditCommands.ResolvePrefabChild(root.transform, args.target);
                var go = new GameObject(args.name);
                AttachInsidePrefab(go, root, parent);
                PrefabEditCommands.ApplyTransform(go.transform, args);

                return new PrefabObjectResult
                {
                    prefab = PrefabEditCommands.NormalizeAssetPath(args.path),
                    target = PrefabEditCommands.BuildChildPath(root.transform, go.transform),
                    type = "",
                    saved = true,
                };
            });
        }

        // ---------- prefab.create_primitive ----------

        [BridgeCommand("prefab.create_primitive",
            "在 Prefab 资产内部创建原生几何体（自带 MeshFilter/MeshRenderer 与碰撞体；直接保存资产）。参数: " +
            "path(string,必填,Prefab 资产路径), target(string,可选,内部父物体路径,空=根节点), " +
            "type(string,必填,Cube/Sphere/Plane/Capsule/Cylinder/Quad), name(string,可选,默认用类型名), " +
            "position/rotation/scale(float[]3,可选), quaternion(bool,可选), material(string,可选,Assets 材质路径)")]
        public static object CreatePrimitive(BridgeContext ctx, BridgeArgs args)
        {
            var primitiveType = BuildCommands.ParsePrimitiveType(args.type);
            var material = BuildCommands.LoadMaterial(args.material);   // 先校验，避免打开/保存 Prefab 后才失败

            return InPrefab(args.path, root =>
            {
                var parent = PrefabEditCommands.ResolvePrefabChild(root.transform, args.target);
                var go = GameObject.CreatePrimitive(primitiveType);
                AttachInsidePrefab(go, root, parent);
                if (!string.IsNullOrWhiteSpace(args.name)) go.name = args.name;
                PrefabEditCommands.ApplyTransform(go.transform, args);
                BuildCommands.ApplyMaterial(go, material);

                return new PrefabObjectResult
                {
                    prefab = PrefabEditCommands.NormalizeAssetPath(args.path),
                    target = PrefabEditCommands.BuildChildPath(root.transform, go.transform),
                    type = primitiveType.ToString(),
                    saved = true,
                };
            });
        }

        // ---------- prefab.add_component ----------

        [BridgeCommand("prefab.add_component",
            "给 Prefab 资产内部物体添加组件（直接保存资产；已存在则跳过不报错）。参数: " +
            "path(string,必填,Prefab 资产路径), target(string,可选,内部物体路径,空=根节点), " +
            "component(string,必填,类型名,简名或全名)")]
        public static object AddComponent(BridgeContext ctx, BridgeArgs args)
        {
            var type = BuildCommands.ResolveType(args.component);
            if (type == null)
                throw new ArgumentException("找不到组件类型: " + args.component);
            if (!typeof(Component).IsAssignableFrom(type))
                throw new ArgumentException(type.FullName + " 不是 Component，无法加到 GameObject 上");

            return InPrefab(args.path, root =>
            {
                var target = PrefabEditCommands.ResolvePrefabChild(root.transform, args.target);
                var go = target.gameObject;

                var existing = go.GetComponent(type);
                if (existing == null) go.AddComponent(type);

                return new PrefabComponentResult
                {
                    prefab = PrefabEditCommands.NormalizeAssetPath(args.path),
                    target = PrefabEditCommands.BuildChildPath(root.transform, target),
                    component = type.FullName,
                    added = existing == null,
                    saved = true,
                };
            });
        }

        // ---------- prefab.set ----------

        [BridgeCommand("prefab.set",
            "写入 Prefab 资产内部物体的属性/字段（直接保存资产）。参数: " +
            "path(string,必填,Prefab 资产路径), target(string,可选,内部物体路径,空=根节点), " +
            "component(string,可选,组件类型名;省略=对目标物体本身操作), " +
            "property(string,必填,属性名或字段名), value(string,必填,转换规则同 property.set)")]
        public static object SetProperty(BridgeContext ctx, BridgeArgs args)
        {
            if (string.IsNullOrWhiteSpace(args.property))
                throw new ArgumentException("prefab.set 需要参数 property（属性名或字段名）");
            if (args.value == null)
                throw new ArgumentException("prefab.set 需要参数 value（字符串形式的值；置空请传字面量 null）");

            return InPrefab(args.path, root =>
            {
                var target = PrefabEditCommands.ResolvePrefabChild(root.transform, args.target);
                var holder = ResolveHolderOn(target.gameObject, args.component);

                // Prefab 内容是临时对象：不走 Undo、不标 dirty，改动由 SaveAsPrefabAsset 统一写回
                var write = BuildCommands.WriteMember(holder, args.property, args.value, false);

                return new PropertySetResult
                {
                    target = "prefab:" + PrefabEditCommands.NormalizeAssetPath(args.path)
                             + "::" + PrefabEditCommands.BuildChildPath(root.transform, target),
                    owner = holder.GetType().FullName,
                    property = args.property,
                    memberKind = write.kind,
                    memberType = write.typeName,
                    value = write.display,
                    saved = true,
                };
            });
        }

        // ---------- 内部工具 ----------

        /// <summary>在 Prefab 内容作用域内执行 body，正常结束后统一写回资产（抛异常则不保存）。</summary>
        private static T InPrefab<T>(string prefabPathArg, Func<GameObject, T> body)
        {
            var prefabPath = PrefabEditCommands.NormalizeAssetPath(prefabPathArg);
            if (string.IsNullOrWhiteSpace(prefabPath))
                throw new ArgumentException("需要参数 path（Prefab 资产路径，如 Assets/Prefabs/Card.prefab）");
            if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null)
                throw new ArgumentException("找不到 Prefab: " + prefabPath);

            GameObject contentsRoot = null;
            try
            {
                contentsRoot = PrefabUtility.LoadPrefabContents(prefabPath);
                var result = body(contentsRoot);
                PrefabUtility.SaveAsPrefabAsset(contentsRoot, prefabPath);
                return result;
            }
            finally
            {
                if (contentsRoot != null) PrefabUtility.UnloadPrefabContents(contentsRoot);
            }
        }

        /// <summary>
        /// 把新建的物体搬进 Prefab 的预览场景并挂到 parent 下。
        /// 坑：new GameObject() / CreatePrimitive() 会落在【活动场景】，而 Prefab 内容由
        /// LoadPrefabContents 加载在独立预览场景里。必须先以「根物体」身份 MoveGameObjectToScene
        /// 再 SetParent——反过来先 SetParent 会变成非根物体，MoveGameObjectToScene 会报错。
        /// </summary>
        private static void AttachInsidePrefab(GameObject go, GameObject contentsRoot, Transform parent)
        {
            if (go.scene != contentsRoot.scene)
                SceneManager.MoveGameObjectToScene(go, contentsRoot.scene);
            go.transform.SetParent(parent, false);
        }

        /// <summary>确定实际被写入的对象：目标物体本身，或它上面指定的组件。</summary>
        private static UnityEngine.Object ResolveHolderOn(GameObject go, string componentName)
        {
            if (string.IsNullOrWhiteSpace(componentName)) return go;

            var type = BuildCommands.ResolveType(componentName);
            if (type == null)
                throw new ArgumentException("找不到组件类型: " + componentName);

            var comp = go.GetComponent(type);
            if (comp == null)
                throw new InvalidOperationException("物体 '" + go.name + "' 上没有 " + type.FullName + " 组件");
            return comp;
        }
    }
}
#endif // UNITY_EDITOR
