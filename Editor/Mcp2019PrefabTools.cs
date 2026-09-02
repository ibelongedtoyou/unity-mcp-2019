#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityMcp2019
{
    internal static class Mcp2019PrefabTools
    {
        internal static string Execute(string argumentsJson)
        {
            PrefabArguments arguments = string.IsNullOrEmpty(argumentsJson) || argumentsJson == "{}"
                ? new PrefabArguments()
                : JsonUtility.FromJson<PrefabArguments>(argumentsJson) ?? new PrefabArguments();
            string action = Require(arguments.Action, "action").ToLowerInvariant();
            if (action == "save_prefab_stage") return SavePrefabStage();
            if (action == "close_prefab_stage") return ClosePrefabStage();

            string path = NormalizePrefabPath(arguments.PrefabPath);
            if (action == "get_info")
            {
                return Mcp2019ResourceTools.Execute(JsonUtility.ToJson(new ResourceRequest
                {
                    Kind = "prefab_info",
                    AssetPath = path
                }));
            }

            if (action == "get_hierarchy")
            {
                return Mcp2019ResourceTools.Execute(JsonUtility.ToJson(new ResourceRequest
                {
                    Kind = "prefab_hierarchy",
                    AssetPath = path
                }));
            }

            if (action == "open_prefab_stage")
            {
                GameObject prefab = LoadPrefab(path);
                if (!AssetDatabase.OpenAsset(prefab))
                {
                    throw new InvalidOperationException("Unity failed to open Prefab Stage: " + path);
                }

                return JsonUtility.ToJson(ActionResult.Ok(action, "Opened Prefab Stage: " + path));
            }

            if (action == "create_from_gameobject")
            {
                return JsonUtility.ToJson(CreateFromGameObject(path, arguments));
            }

            if (action == "modify_contents")
            {
                return JsonUtility.ToJson(ModifyContents(path, arguments));
            }

            throw new ArgumentException("Unsupported manage_prefabs action: " + action);
        }

        private static ActionResult CreateFromGameObject(string path, PrefabArguments arguments)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Prefab creation is unavailable in Play Mode.");
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null && !arguments.AllowOverwrite)
                throw new InvalidOperationException("Prefab already exists; set allow_overwrite=true: " + path);
            GameObject target = ResolveSceneGameObject(arguments.TargetInstanceId, arguments.Target);
            if (PrefabUtility.IsPartOfPrefabInstance(target) && arguments.UnlinkIfInstance)
            {
                GameObject root = PrefabUtility.GetOutermostPrefabInstanceRoot(target);
                PrefabUtility.UnpackPrefabInstance(
                    root,
                    PrefabUnpackMode.Completely,
                    InteractionMode.AutomatedAction);
            }

            EnsureFolder(Path.GetDirectoryName(path).Replace('\\', '/'));
            GameObject saved = PrefabUtility.SaveAsPrefabAssetAndConnect(
                target,
                path,
                InteractionMode.AutomatedAction);
            if (saved == null)
                throw new InvalidOperationException("Unity failed to create prefab: " + path);
            return ActionResult.Ok("create_from_gameobject", "Created prefab: " + path);
        }

        private static ActionResult ModifyContents(string path, PrefabArguments arguments)
        {
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            if (root == null) throw new InvalidOperationException("Unity failed to load prefab contents: " + path);
            try
            {
                Transform target = ResolvePrefabTransform(root.transform, arguments.Target);
                if (arguments.HasPosition) target.localPosition = Vector3From(arguments.Position, Vector3.zero);
                if (arguments.HasRotation) target.localEulerAngles = Vector3From(arguments.Rotation, Vector3.zero);
                if (arguments.HasScale) target.localScale = Vector3From(arguments.Scale, Vector3.one);
                if (!string.IsNullOrEmpty(arguments.Name)) target.name = arguments.Name;
                if (!string.IsNullOrEmpty(arguments.Tag)) target.gameObject.tag = arguments.Tag;
                if (!string.IsNullOrEmpty(arguments.Layer))
                {
                    int layer = LayerMask.NameToLayer(arguments.Layer);
                    if (layer < 0) throw new ArgumentException("Layer was not found: " + arguments.Layer);
                    target.gameObject.layer = layer;
                }

                if (arguments.HasSetActive) target.gameObject.SetActive(arguments.SetActive);
                if (!string.IsNullOrEmpty(arguments.Parent))
                {
                    Transform parent = ResolvePrefabTransform(root.transform, arguments.Parent);
                    if (parent == target || parent.IsChildOf(target))
                        throw new ArgumentException("Prefab object cannot be parented to itself or its child.");
                    target.SetParent(parent, false);
                }

                AddComponents(target.gameObject, arguments.ComponentsToAdd);
                RemoveComponents(target.gameObject, arguments.ComponentsToRemove);
                ApplyComponentPatches(target.gameObject, arguments.ComponentPatches);
                CreateChildren(root.transform, target, arguments.CreateChildren);
                DeleteChildren(root.transform, arguments.DeleteChildren);
                PrefabUtility.SaveAsPrefabAsset(root, path);
                return ActionResult.Ok("modify_contents", "Modified prefab contents: " + path);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static void CreateChildren(
            Transform root,
            Transform defaultParent,
            ChildDefinition[] definitions)
        {
            if (definitions == null) return;
            for (int index = 0; index < definitions.Length; index++)
            {
                ChildDefinition definition = definitions[index];
                Transform parent = string.IsNullOrEmpty(definition.Parent)
                    ? defaultParent
                    : ResolvePrefabTransform(root, definition.Parent);
                GameObject child;
                if (!string.IsNullOrEmpty(definition.SourcePrefabPath))
                {
                    GameObject source = LoadPrefab(NormalizePrefabPath(definition.SourcePrefabPath));
                    child = PrefabUtility.InstantiatePrefab(source) as GameObject;
                    if (child == null) throw new InvalidOperationException("Failed to instantiate nested prefab.");
                }
                else if (!string.IsNullOrEmpty(definition.PrimitiveType))
                {
                    PrimitiveType primitive;
                    if (!Enum.TryParse(definition.PrimitiveType, true, out primitive))
                        throw new ArgumentException("Unknown primitive_type: " + definition.PrimitiveType);
                    child = GameObject.CreatePrimitive(primitive);
                }
                else child = new GameObject();
                child.name = definition.Name;
                child.transform.SetParent(parent, false);
                child.transform.localPosition = Vector3From(definition.Position, Vector3.zero);
                child.transform.localEulerAngles = Vector3From(definition.Rotation, Vector3.zero);
                child.transform.localScale = Vector3From(definition.Scale, Vector3.one);
                if (!string.IsNullOrEmpty(definition.Tag)) child.tag = definition.Tag;
                if (!string.IsNullOrEmpty(definition.Layer))
                {
                    int layer = LayerMask.NameToLayer(definition.Layer);
                    if (layer < 0) throw new ArgumentException("Layer was not found: " + definition.Layer);
                    child.layer = layer;
                }
                child.SetActive(definition.SetActive);
                AddComponents(child, definition.ComponentsToAdd);
            }
        }

        private static void DeleteChildren(Transform root, string[] paths)
        {
            if (paths == null) return;
            for (int index = 0; index < paths.Length; index++)
            {
                Transform child = ResolvePrefabTransform(root, paths[index]);
                if (child == root) throw new ArgumentException("Cannot delete the prefab root.");
                UnityEngine.Object.DestroyImmediate(child.gameObject);
            }
        }

        private static void AddComponents(GameObject target, string[] names)
        {
            if (names == null) return;
            for (int index = 0; index < names.Length; index++)
            {
                Type type = ResolveComponentType(names[index]);
                if (typeof(Transform).IsAssignableFrom(type)) continue;
                if (target.GetComponent(type) == null) target.AddComponent(type);
            }
        }

        private static void RemoveComponents(GameObject target, string[] names)
        {
            if (names == null) return;
            for (int index = 0; index < names.Length; index++)
            {
                Type type = ResolveComponentType(names[index]);
                if (typeof(Transform).IsAssignableFrom(type))
                    throw new ArgumentException("Transform components cannot be removed.");
                Component component = target.GetComponent(type);
                if (component == null) throw new ArgumentException("Component was not found: " + names[index]);
                UnityEngine.Object.DestroyImmediate(component, true);
            }
        }

        private static void ApplyComponentPatches(GameObject target, ComponentPatch[] patches)
        {
            if (patches == null) return;
            for (int index = 0; index < patches.Length; index++)
            {
                ComponentPatch patch = patches[index];
                Component component = target.GetComponent(ResolveComponentType(patch.ComponentType));
                if (component == null) throw new ArgumentException("Component was not found: " + patch.ComponentType);
                SerializedObject serialized = new SerializedObject(component);
                SerializedProperty property = serialized.FindProperty(patch.Property.Path);
                if (property == null) throw new ArgumentException("Serialized property was not found: " + patch.Property.Path);
                SetProperty(property, patch.Property);
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        private static void SetProperty(SerializedProperty property, SerializedPatch patch)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Boolean: property.boolValue = patch.BoolValue; break;
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.LayerMask: property.intValue = patch.IntValue; break;
                case SerializedPropertyType.Float: property.floatValue = patch.Kind == "int" ? patch.IntValue : patch.FloatValue; break;
                case SerializedPropertyType.String: property.stringValue = patch.StringValue ?? string.Empty; break;
                case SerializedPropertyType.Enum:
                    if (patch.Kind == "int") property.enumValueIndex = patch.IntValue;
                    else
                    {
                        int enumIndex = Array.FindIndex(property.enumNames, value =>
                            string.Equals(value, patch.StringValue, StringComparison.OrdinalIgnoreCase));
                        if (enumIndex < 0) throw new ArgumentException("Enum value was not found: " + patch.StringValue);
                        property.enumValueIndex = enumIndex;
                    }
                    break;
                case SerializedPropertyType.Vector2:
                    Vector3 v2 = Vector3From(patch.FloatValues, Vector3.zero);
                    property.vector2Value = new Vector2(v2.x, v2.y);
                    break;
                case SerializedPropertyType.Vector3: property.vector3Value = Vector3From(patch.FloatValues, Vector3.zero); break;
                case SerializedPropertyType.Vector4:
                    Vector3 v3 = Vector3From(patch.FloatValues, Vector3.zero);
                    property.vector4Value = new Vector4(v3.x, v3.y, v3.z,
                        patch.FloatValues != null && patch.FloatValues.Length > 3 ? patch.FloatValues[3] : 0f);
                    break;
                case SerializedPropertyType.Color:
                    Vector3 color = Vector3From(patch.FloatValues, Vector3.zero);
                    property.colorValue = new Color(color.x, color.y, color.z,
                        patch.FloatValues != null && patch.FloatValues.Length > 3 ? patch.FloatValues[3] : 1f);
                    break;
                case SerializedPropertyType.ObjectReference:
                    string referencePath = patch.ReferencePath;
                    if (string.IsNullOrEmpty(referencePath) && !string.IsNullOrEmpty(patch.ReferenceGuid))
                        referencePath = AssetDatabase.GUIDToAssetPath(patch.ReferenceGuid);
                    property.objectReferenceValue = patch.Kind == "null"
                        ? null
                        : AssetDatabase.LoadMainAssetAtPath(referencePath);
                    break;
                default: throw new ArgumentException("Unsupported serialized property type: " + property.propertyType);
            }
        }

        private static string SavePrefabStage()
        {
            object stage = CurrentPrefabStage();
            if (stage == null) throw new InvalidOperationException("No Prefab Stage is open.");
            MethodInfo save = stage.GetType().GetMethod(
                "SavePrefab",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (save == null) throw new InvalidOperationException("Prefab Stage save API was not found.");
            save.Invoke(stage, null);
            return JsonUtility.ToJson(ActionResult.Ok("save_prefab_stage", "Prefab Stage saved."));
        }

        private static string ClosePrefabStage()
        {
            if (CurrentPrefabStage() == null) throw new InvalidOperationException("No Prefab Stage is open.");
            Type stageUtility = typeof(Editor).Assembly.GetType("UnityEditor.Experimental.SceneManagement.StageUtility") ??
                                typeof(Editor).Assembly.GetType("UnityEditor.SceneManagement.StageUtility");
            MethodInfo back = stageUtility == null ? null : stageUtility.GetMethod(
                "GoBackToPreviousStage",
                BindingFlags.Public | BindingFlags.Static);
            if (back == null) throw new InvalidOperationException("Prefab Stage close API was not found.");
            back.Invoke(null, null);
            return JsonUtility.ToJson(ActionResult.Ok("close_prefab_stage", "Prefab Stage closed."));
        }

        private static object CurrentPrefabStage()
        {
            Type utility = typeof(Editor).Assembly.GetType("UnityEditor.Experimental.SceneManagement.PrefabStageUtility") ??
                           typeof(Editor).Assembly.GetType("UnityEditor.SceneManagement.PrefabStageUtility");
            MethodInfo current = utility == null ? null : utility.GetMethod(
                "GetCurrentPrefabStage",
                BindingFlags.Public | BindingFlags.Static);
            return current == null ? null : current.Invoke(null, null);
        }

        private static Transform ResolvePrefabTransform(Transform root, string target)
        {
            if (string.IsNullOrEmpty(target) || target == root.name) return root;
            string path = target.Trim('/');
            if (path.StartsWith(root.name + "/", StringComparison.OrdinalIgnoreCase))
                path = path.Substring(root.name.Length + 1);
            Transform direct = root.Find(path);
            if (direct != null) return direct;
            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            List<Transform> matches = all.Where(item =>
                string.Equals(item.name, target, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 1) return matches[0];
            throw new ArgumentException("Prefab target must resolve exactly once: " + target);
        }

        private static GameObject ResolveSceneGameObject(int instanceId, string target)
        {
            if (instanceId != 0)
            {
                GameObject byId = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
                if (byId != null && !EditorUtility.IsPersistent(byId) && byId.scene.IsValid()) return byId;
            }
            string query = Require(target, "target").Trim('/');
            GameObject[] all = Resources.FindObjectsOfTypeAll<GameObject>();
            List<GameObject> matches = all.Where(item => item != null && !EditorUtility.IsPersistent(item) &&
                item.scene.IsValid() && item.scene.isLoaded &&
                (string.Equals(item.name, query, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(GetPath(item.transform), query, StringComparison.OrdinalIgnoreCase))).ToList();
            if (matches.Count == 1) return matches[0];
            throw new ArgumentException("Scene target must resolve exactly once: " + query);
        }

        private static string GetPath(Transform transform)
        {
            List<string> names = new List<string>();
            while (transform != null) { names.Add(transform.name); transform = transform.parent; }
            names.Reverse();
            return string.Join("/", names.ToArray());
        }

        private static Type ResolveComponentType(string name)
        {
            List<Type> matches = new List<Type>();
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type exact = assembly.GetType(name, false, true);
                if (exact != null && typeof(Component).IsAssignableFrom(exact)) return exact;
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException error) { types = error.Types; }
                catch { continue; }
                foreach (Type type in types)
                    if (type != null && typeof(Component).IsAssignableFrom(type) &&
                        string.Equals(type.Name, name, StringComparison.OrdinalIgnoreCase)) matches.Add(type);
            }
            if (matches.Count == 1) return matches[0];
            throw new ArgumentException(matches.Count == 0
                ? "Component type was not found: " + name
                : "Component type is ambiguous: " + name);
        }

        private static GameObject LoadPrefab(string path)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) throw new FileNotFoundException("Prefab was not found: " + path);
            return prefab;
        }

        private static string NormalizePrefabPath(string value)
        {
            string path = Require(value, "prefab_path").Replace('\\', '/');
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) path = "Assets/" + path.TrimStart('/');
            if (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) path += ".prefab";
            if (path.Contains("../") || path.IndexOf(':') >= 0) throw new ArgumentException("Prefab path must stay under Assets/.");
            return path;
        }

        private static void EnsureFolder(string path)
        {
            if (string.IsNullOrEmpty(path) || path == "Assets" || AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        private static Vector3 Vector3From(float[] values, Vector3 fallback)
        {
            return values == null || values.Length < 3
                ? fallback
                : new Vector3(values[0], values[1], values[2]);
        }

        private static string Require(string value, string field)
        {
            if (string.IsNullOrEmpty(value) || value.Trim().Length == 0)
                throw new ArgumentException(field + " must be a non-empty string.");
            return value.Trim();
        }

        [Serializable] private sealed class ResourceRequest { public string Kind; public string AssetPath; }
        [Serializable] private sealed class PrefabArguments
        {
            public string Action; public string PrefabPath; public string Target; public int TargetInstanceId;
            public bool AllowOverwrite; public bool SearchInactive; public bool UnlinkIfInstance;
            public bool HasPosition; public float[] Position; public bool HasRotation; public float[] Rotation;
            public bool HasScale; public float[] Scale; public string Name; public string Tag; public string Layer;
            public bool HasSetActive; public bool SetActive; public string Parent;
            public string[] ComponentsToAdd; public string[] ComponentsToRemove;
            public ChildDefinition[] CreateChildren; public string[] DeleteChildren;
            public ComponentPatch[] ComponentPatches;
        }
        [Serializable] private sealed class ChildDefinition
        {
            public string Name; public string Parent; public string SourcePrefabPath; public string PrimitiveType;
            public float[] Position; public float[] Rotation; public float[] Scale;
            public string[] ComponentsToAdd; public string Tag; public string Layer; public bool SetActive = true;
        }
        [Serializable] private sealed class ComponentPatch { public string ComponentType; public SerializedPatch Property; }
        [Serializable] private sealed class SerializedPatch
        {
            public string Path; public string Kind; public bool BoolValue; public int IntValue; public float FloatValue;
            public string StringValue; public float[] FloatValues; public string ReferencePath; public string ReferenceGuid;
        }
        [Serializable] private sealed class ActionResult
        {
            public bool Success; public string Action; public string Message;
            public static ActionResult Ok(string action, string message)
            { return new ActionResult { Success = true, Action = action, Message = message }; }
        }
    }
}
#endif
