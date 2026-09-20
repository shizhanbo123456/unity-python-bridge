#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityPythonBridge.Commands
{
    /// <summary>component.add 返回结构。</summary>
    [System.Serializable]
    public class ComponentAddResult
    {
        public string target;      // 解析后的物体层级路径
        public string component;   // 实际加上的组件全名
        public bool added;         // 本次是否真的新加了（已存在则为 false）
        public string message;     // 补充说明
    }

    /// <summary>property.set 返回结构。</summary>
    [System.Serializable]
    public class PropertySetResult
    {
        public string target;      // 用户传入的 target
        public string owner;       // 实际被写入的对象（组件全名 / 资产类型名）
        public string property;    // 成员名
        public string memberKind;  // property / field / serialized
        public string memberType;  // 成员类型名
        public string value;       // 写入后值的字符串表示
        public bool saved;         // 是否已落盘（资产目标才触发 SaveAssets）
    }

    /// <summary>asset.create 返回结构。</summary>
    [System.Serializable]
    public class AssetCreateResult
    {
        public string path;
        public string type;
        public bool created;
    }

    /// <summary>WriteMember 的结果（内部用，不直接序列化）。</summary>
    internal sealed class MemberWrite
    {
        public string kind;       // property / field / serialized
        public string typeName;   // 成员类型名
        public string display;    // 写入后值的字符串表示
    }

    /// <summary>
    /// 构建类命令：给场景物体加组件、往任意对象写属性、反射创建 ScriptableObject 资产。
    ///
    /// 存在意义：桥原本只能实例化已有 Prefab、只能改 Transform 的 position/rotation/scale，
    /// 既建不了空物体也加不了组件，因此无法从零搭出 UI 载体（PanelSettings + UIDocument）。
    /// 这三条补上之后，UI 层级可以完全由命令行搭出来。
    ///
    /// 定位规则（target）：以 Assets/ 或 Packages/ 开头视为资产路径，否则视为场景物体层级路径。
    /// </summary>
    public static class BuildCommands
    {
        // ---------- component.add ----------

        [BridgeCommand("component.add",
            "给场景物体添加组件（支持 Undo；已存在则跳过不报错）。参数: target(string,必填,层级路径/名称), " +
            "component(string,必填,类型名,简名如 UIDocument 或全名如 UnityEngine.UIElements.UIDocument)")]
        public static object AddComponent(BridgeContext ctx, BridgeArgs args)
        {
            var go = GameObjectCommands.ResolveTarget(args.target);
            var type = ResolveType(args.component);
            if (type == null)
                throw new ArgumentException("找不到组件类型: " + args.component);
            if (!typeof(Component).IsAssignableFrom(type))
                throw new ArgumentException(type.FullName + " 不是 Component，无法加到 GameObject 上");

            var existing = go.GetComponent(type);
            if (existing != null)
            {
                return new ComponentAddResult
                {
                    target = GameObjectCommands.BuildPath(go.transform),
                    component = type.FullName,
                    added = false,
                    message = "该物体上已存在此组件，未重复添加",
                };
            }

            var comp = Undo.AddComponent(go, type);
            if (comp == null)
                throw new InvalidOperationException("AddComponent 失败: " + type.FullName);

            return new ComponentAddResult
            {
                target = GameObjectCommands.BuildPath(go.transform),
                component = type.FullName,
                added = true,
                message = "",
            };
        }

        // ---------- property.set ----------

        [BridgeCommand("property.set",
            "写入某个对象的属性或字段（支持 Undo；资产目标会 SaveAssets 落盘）。参数: " +
            "target(string,必填,场景物体层级路径 或 Assets/Packages 资产路径), " +
            "component(string,可选,组件类型名;省略=直接对 target 本身操作;target 是资产时不可指定), " +
            "property(string,必填,属性名或字段名;也兼容 m_Xxx 形式的序列化字段), " +
            "value(string,必填,按成员类型自动转换: bool 收 true/false/1/0; 枚举收名字; " +
            "Vector/Color 收 \"x,y,z\"; 字面量 null 置空; 引用类型收 Assets 路径)")]
        public static object SetProperty(BridgeContext ctx, BridgeArgs args)
        {
            if (string.IsNullOrWhiteSpace(args.property))
                throw new ArgumentException("property.set 需要参数 property（属性名或字段名）");
            if (args.value == null)
                throw new ArgumentException("property.set 需要参数 value（字符串形式的值；置空请传字面量 null）");

            var targetObj = ResolvePropertyTarget(args.target);
            var holder = ResolveHolder(targetObj, args.component, args.target);
            var write = WriteMember(holder, args.property, args.value, true);
            return new PropertySetResult
            {
                target = args.target,
                owner = holder.GetType().FullName,
                property = args.property,
                memberKind = write.kind,
                memberType = write.typeName,
                value = write.display,
                saved = SaveIfAsset(holder),
            };
        }

        // ---------- asset.create ----------

        [BridgeCommand("asset.create",
            "反射创建一个 ScriptableObject 资产（如 PanelSettings）。参数: " +
            "type(string,必填,类型名,简名或全名), path(string,必填,Assets 下路径,如 Assets/UI/Panel.asset), " +
            "overwrite(bool,可选,默认 false,已存在则报错)")]
        public static object CreateAsset(BridgeContext ctx, BridgeArgs args)
        {
            if (string.IsNullOrWhiteSpace(args.type))
                throw new ArgumentException("asset.create 需要参数 type（类型名，如 PanelSettings）");

            var path = (args.path ?? "").Trim().Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("asset.create 需要参数 path（如 Assets/UI/MySettings.asset）");
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("path 必须以 Assets/ 开头（当前: " + path + "）");

            var type = ResolveType(args.type);
            if (type == null)
                throw new ArgumentException("找不到类型: " + args.type);
            if (!typeof(ScriptableObject).IsAssignableFrom(type))
                throw new ArgumentException(type.FullName + " 不是 ScriptableObject，asset.create 只支持 ScriptableObject");

            var full = Path.GetFullPath(Path.Combine(Application.dataPath, "..", path));
            if (File.Exists(full))
            {
                if (!args.overwrite)
                    throw new ArgumentException("资产已存在，拒绝覆盖（如需覆盖请传 overwrite=true）: " + path);
                AssetDatabase.DeleteAsset(path);
            }

            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                AssetDatabase.Refresh();
            }

            var obj = ScriptableObject.CreateInstance(type);
            AssetDatabase.CreateAsset(obj, path);
            AssetDatabase.SaveAssets();

            return new AssetCreateResult
            {
                path = path,
                type = type.FullName,
                created = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path) != null,
            };
        }

        // ---------- 类型解析 ----------

        private static readonly Dictionary<string, Type> TypeCache = new Dictionary<string, Type>();

        /// <summary>按类型名找类型：全名优先，其次简名；简名有多个候选时报错并列出。
        /// internal：同程序集的 PrefabObjectCommands 复用。</summary>
        internal static Type ResolveType(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            name = name.Trim();

            Type cached;
            if (TypeCache.TryGetValue(name, out cached)) return cached;

            Type byFullName = null;
            var bySimpleName = new List<Type>();

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; }
                catch (Exception) { continue; }
                if (types == null) continue;

                foreach (var t in types)
                {
                    if (t == null) continue;
                    if (byFullName == null && t.FullName == name) byFullName = t;
                    if (t.Name == name) bySimpleName.Add(t);
                }
            }

            var resolved = byFullName;
            if (resolved == null)
            {
                if (bySimpleName.Count == 1)
                {
                    resolved = bySimpleName[0];
                }
                else if (bySimpleName.Count > 1)
                {
                    var sample = new List<string>();
                    for (int i = 0; i < bySimpleName.Count && i < 3; i++) sample.Add(bySimpleName[i].FullName);
                    throw new ArgumentException(
                        "类型名 '" + name + "' 有 " + bySimpleName.Count + " 个候选，请改用全名，例如: " + string.Join(" / ", sample));
                }
            }

            TypeCache[name] = resolved;
            return resolved;
        }

        // ---------- 目标解析 ----------

        /// <summary>target 以 Assets/ 或 Packages/ 开头按资产解析，否则按场景物体解析。</summary>
        private static UnityEngine.Object ResolvePropertyTarget(string target)
        {
            if (string.IsNullOrWhiteSpace(target))
                throw new ArgumentException("property.set 需要参数 target（场景物体层级路径 或 Assets 资产路径）");

            var t = target.Trim().Replace('\\', '/');
            if (t.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(t);
                if (asset == null) throw new ArgumentException("找不到资产: " + t);
                return asset;
            }
            return GameObjectCommands.ResolveTarget(t);
        }

        /// <summary>确定实际被写入的对象：target 本身，或 target 上指定的组件。</summary>
        private static UnityEngine.Object ResolveHolder(UnityEngine.Object targetObj, string componentName, string target)
        {
            if (string.IsNullOrWhiteSpace(componentName)) return targetObj;

            var compType = ResolveType(componentName);
            if (compType == null)
                throw new ArgumentException("找不到组件类型: " + componentName);

            var go = targetObj as GameObject;
            if (go == null)
                throw new ArgumentException("target 是资产时不能指定 component 参数（资产本身就是操作对象）");

            var comp = go.GetComponent(compType);
            if (comp == null)
                throw new InvalidOperationException("物体 '" + target + "' 上没有 " + compType.FullName + " 组件");

            return comp;
        }

        /// <summary>沿继承链找字段（含私有）。</summary>
        private static FieldInfo FindField(Type type, string name)
        {
            while (type != null)
            {
                var f = type.GetField(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (f != null) return f;
                type = type.BaseType;
            }
            return null;
        }

        // ---------- 值转换 ----------

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>把字符串按目标类型转换；引用类型按资产路径加载。</summary>
        private static object ConvertValue(string raw, Type type, string memberName)
        {
            var s = raw.Trim();
            if (s.Equals("null", StringComparison.OrdinalIgnoreCase)) return null;
            if (type == typeof(string)) return raw;

            if (type == typeof(bool))
            {
                if (s == "1") return true;
                if (s == "0") return false;
                bool b;
                if (bool.TryParse(s, out b)) return b;
                throw new ArgumentException(memberName + ": 需要 bool（true/false/1/0），当前: " + raw);
            }
            if (type == typeof(int))
            {
                int i;
                if (int.TryParse(s, NumberStyles.Integer, Inv, out i)) return i;
                throw new ArgumentException(memberName + ": 需要 int，当前: " + raw);
            }
            if (type == typeof(long))
            {
                long l;
                if (long.TryParse(s, NumberStyles.Integer, Inv, out l)) return l;
                throw new ArgumentException(memberName + ": 需要 long，当前: " + raw);
            }
            if (type == typeof(float))
            {
                float f;
                if (float.TryParse(s, NumberStyles.Float, Inv, out f)) return f;
                throw new ArgumentException(memberName + ": 需要 float，当前: " + raw);
            }
            if (type == typeof(double))
            {
                double d;
                if (double.TryParse(s, NumberStyles.Float, Inv, out d)) return d;
                throw new ArgumentException(memberName + ": 需要 double，当前: " + raw);
            }

            if (type.IsEnum)
            {
                try { return Enum.Parse(type, s, true); }
                catch (Exception)
                {
                    throw new ArgumentException(
                        memberName + ": '" + raw + "' 不是合法的 " + type.Name + " 值。可选: " +
                        string.Join(", ", Enum.GetNames(type)));
                }
            }

            if (type == typeof(Vector2) || type == typeof(Vector3) || type == typeof(Vector4))
            {
                var v = ParseFloats(s, memberName);
                int need = type == typeof(Vector2) ? 2 : (type == typeof(Vector3) ? 3 : 4);
                if (v.Length != need)
                    throw new ArgumentException(memberName + ": " + type.Name + " 需要 " + need + " 个分量，当前: " + raw);
                if (type == typeof(Vector2)) return new Vector2(v[0], v[1]);
                if (type == typeof(Vector3)) return new Vector3(v[0], v[1], v[2]);
                return new Vector4(v[0], v[1], v[2], v[3]);
            }

            if (type == typeof(Vector2Int) || type == typeof(Vector3Int))
            {
                var v = ParseFloats(s, memberName);
                int need = type == typeof(Vector2Int) ? 2 : 3;
                if (v.Length != need)
                    throw new ArgumentException(memberName + ": " + type.Name + " 需要 " + need + " 个分量，当前: " + raw);
                if (type == typeof(Vector2Int)) return new Vector2Int((int)v[0], (int)v[1]);
                return new Vector3Int((int)v[0], (int)v[1], (int)v[2]);
            }

            if (type == typeof(Color) || type == typeof(Color32))
            {
                var v = ParseFloats(s, memberName);
                if (v.Length != 3 && v.Length != 4)
                    throw new ArgumentException(memberName + ": Color 需要 3 或 4 个分量，当前: " + raw);
                var a = v.Length == 4 ? v[3] : 1f;
                if (type == typeof(Color)) return new Color(v[0], v[1], v[2], a);
                return (Color32)new Color(v[0], v[1], v[2], a);
            }

            if (typeof(UnityEngine.Object).IsAssignableFrom(type))
                return LoadObjectAsset(s, type, memberName);

            throw new ArgumentException(
                memberName + ": 暂不支持类型 " + type.Name + "（支持 string / bool / 数值 / 枚举 / Vector / Color / 资产引用）");
        }

        private static float[] ParseFloats(string s, string memberName)
        {
            var parts = s.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
            var result = new float[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!float.TryParse(parts[i], NumberStyles.Float, Inv, out result[i]))
                    throw new ArgumentException(memberName + ": '" + parts[i] + "' 不是合法数字（当前: " + s + "）");
            }
            return result;
        }

        /// <summary>按 Assets 路径加载资产并校验类型。</summary>
        private static UnityEngine.Object LoadObjectAsset(string path, Type type, string memberName)
        {
            var p = path.Replace('\\', '/').Trim();
            if (!p.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    memberName + ": 引用类型的值必须是 Assets/ 或 Packages/ 开头的资产路径（当前: " + path + "）");

            var obj = AssetDatabase.LoadAssetAtPath(p, type);
            if (obj == null) obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(p);
            if (obj == null)
                throw new ArgumentException(memberName + ": 找不到资产 " + p);
            if (!type.IsInstanceOfType(obj))
                throw new ArgumentException(
                    memberName + ": 资产类型不匹配，" + p + " 是 " + obj.GetType().Name + "，需要 " + type.Name);
            return obj;
        }

        /// <summary>SerializedObject 兜底通道（覆盖 m_Xxx 这类序列化私有字段）。</summary>
        private static void SetSerializedProperty(SerializedProperty sp, string raw, string memberName, Type ownerType)
        {
            var s = raw.Trim();
            switch (sp.propertyType)
            {
                case SerializedPropertyType.Integer:
                    sp.intValue = (int)ConvertValue(s, typeof(int), memberName);
                    break;
                case SerializedPropertyType.Boolean:
                    sp.boolValue = (bool)ConvertValue(s, typeof(bool), memberName);
                    break;
                case SerializedPropertyType.Float:
                    sp.floatValue = (float)ConvertValue(s, typeof(float), memberName);
                    break;
                case SerializedPropertyType.String:
                    sp.stringValue = raw;
                    break;
                case SerializedPropertyType.Enum:
                {
                    var f = FindField(ownerType, sp.name);
                    if (f == null || !f.FieldType.IsEnum)
                        throw new ArgumentException(memberName + ": 无法确定枚举类型，请改用属性名或字段名");
                    var ev = Enum.Parse(f.FieldType, s, true);
                    sp.intValue = Convert.ToInt32(ev);
                    break;
                }
                case SerializedPropertyType.ObjectReference:
                    sp.objectReferenceValue = LoadObjectAsset(s, typeof(UnityEngine.Object), memberName);
                    break;
                default:
                    throw new ArgumentException(
                        "property.set: " + ownerType.Name + "." + memberName + " 的序列化类型 " +
                        sp.propertyType + " 暂不支持（请改用属性名或字段名）");
            }
        }

        // ---------- 成员写入（property.set 与 prefab.set 共用）----------

        /// <summary>
        /// 把字符串值写进 holder 的指定成员。查找顺序：属性 → 字段（沿继承链）→ SerializedObject。
        /// recordUndo=false 用于编辑 Prefab 内容：那批对象是 LoadPrefabContents 出来的临时对象，
        /// 不该进场景 Undo，也不该标 dirty（改动由 SaveAsPrefabAsset 统一写回资产）。
        /// </summary>
        internal static MemberWrite WriteMember(UnityEngine.Object holder, string memberName,
            string rawValue, bool recordUndo)
        {
            var holderType = holder.GetType();

            var propInfo = holderType.GetProperty(memberName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (propInfo != null)
            {
                if (!propInfo.CanWrite)
                    throw new InvalidOperationException("属性 '" + memberName + "' 只读（无 setter），无法写入");
                var converted = ConvertValue(rawValue, propInfo.PropertyType, memberName);
                if (recordUndo) Undo.RecordObject(holder, "UnityBridge property.set");
                propInfo.SetValue(holder, converted);
                if (recordUndo) EditorUtility.SetDirty(holder);
                return new MemberWrite
                {
                    kind = "property",
                    typeName = propInfo.PropertyType.Name,
                    display = Describe(converted),
                };
            }

            var fieldInfo = FindField(holderType, memberName);
            if (fieldInfo != null)
            {
                if (fieldInfo.IsInitOnly || fieldInfo.IsLiteral)
                    throw new InvalidOperationException("字段 '" + memberName + "' 只读（readonly/const），无法写入");
                var converted = ConvertValue(rawValue, fieldInfo.FieldType, memberName);
                if (recordUndo) Undo.RecordObject(holder, "UnityBridge property.set");
                fieldInfo.SetValue(holder, converted);
                if (recordUndo) EditorUtility.SetDirty(holder);
                return new MemberWrite
                {
                    kind = "field",
                    typeName = fieldInfo.FieldType.Name,
                    display = Describe(converted),
                };
            }

            var so = new SerializedObject(holder);
            var sp = so.FindProperty(memberName);
            if (sp == null)
                throw new ArgumentException(
                    "在 " + holderType.Name + " 上找不到成员 '" + memberName + "'（属性 / 字段 / 序列化字段都没有）");

            SetSerializedProperty(sp, rawValue, memberName, holderType);
            so.ApplyModifiedProperties();
            if (recordUndo) EditorUtility.SetDirty(holder);

            return new MemberWrite
            {
                kind = "serialized",
                typeName = sp.propertyType.ToString(),
                display = rawValue,
            };
        }

        // ---------- 原生几何体共用工具 ----------

        /// <summary>解析几何体类型名（Cube / Sphere / Plane / Capsule / Cylinder / Quad，忽略大小写）。</summary>
        internal static PrimitiveType ParsePrimitiveType(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("需要参数 type（几何体类型）: " +
                    string.Join(" / ", Enum.GetNames(typeof(PrimitiveType))));
            try
            {
                return (PrimitiveType)Enum.Parse(typeof(PrimitiveType), name.Trim(), true);
            }
            catch (Exception)
            {
                throw new ArgumentException("未知的几何体类型 '" + name + "'，可选: " +
                    string.Join(" / ", Enum.GetNames(typeof(PrimitiveType))));
            }
        }

        /// <summary>按 Assets 路径加载材质；路径为空返回 null。
        /// 应在创建物体【之前】调用——否则失败时会在场景里留下垃圾物体。</summary>
        internal static Material LoadMaterial(string materialPath)
        {
            if (string.IsNullOrWhiteSpace(materialPath)) return null;

            var p = materialPath.Replace('\\', '/').Trim();
            var mat = AssetDatabase.LoadAssetAtPath<Material>(p);
            if (mat == null) throw new ArgumentException("找不到材质: " + p);
            return mat;
        }

        /// <summary>把材质赋给物体的 Renderer（sharedMaterial）；mat 为 null 则不改。</summary>
        internal static void ApplyMaterial(GameObject go, Material mat)
        {
            if (mat == null) return;

            var renderer = go.GetComponent<Renderer>();
            if (renderer == null)
                throw new InvalidOperationException("物体 '" + go.name + "' 上没有 Renderer，无法赋材质");
            renderer.sharedMaterial = mat;
        }

        private static string Describe(object v)
        {
            if (v == null) return "null";
            var uo = v as UnityEngine.Object;
            if (uo != null) return uo.name + " (" + uo.GetType().Name + ")";
            return v.ToString();
        }

        /// <summary>资产目标才落盘（场景物体交给 Unity 的 dirty 机制）。</summary>
        private static bool SaveIfAsset(UnityEngine.Object holder)
        {
            if (!AssetDatabase.Contains(holder)) return false;
            AssetDatabase.SaveAssets();
            return true;
        }
    }
}
#endif // UNITY_EDITOR
