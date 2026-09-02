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
    internal static class Mcp2019AssetTools
    {
        internal static string Execute(string method, string argumentsJson)
        {
            if (method == "manage_asset")
            {
                return ManageAsset(Parse<AssetArguments>(argumentsJson));
            }

            if (method == "manage_material")
            {
                return ManageMaterial(Parse<MaterialArguments>(argumentsJson));
            }

            throw new ArgumentException("Unknown asset tool method: " + method);
        }

        private static T Parse<T>(string json) where T : class, new()
        {
            if (string.IsNullOrEmpty(json) || json == "{}")
            {
                return new T();
            }

            return JsonUtility.FromJson<T>(json) ?? new T();
        }

        private static string ManageAsset(AssetArguments arguments)
        {
            string action = Require(arguments.Action, "action").ToLowerInvariant();
            string path = action == "resolve_guid"
                ? Require(arguments.Path, "path")
                : NormalizeAssetPath(arguments.Path, action == "search");
            switch (action)
            {
                case "import":
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                    return JsonUtility.ToJson(ActionResult.Ok(action, "Imported asset: " + path));
                case "create_folder":
                    EnsureFolder(path);
                    return JsonUtility.ToJson(ActionResult.Ok(action, "Folder is available: " + path));
                case "create":
                    return JsonUtility.ToJson(CreateAsset(path, arguments));
                case "modify":
                    return JsonUtility.ToJson(ModifyAsset(path, arguments.Properties));
                case "delete":
                    if (!AssetDatabase.DeleteAsset(path))
                    {
                        throw new InvalidOperationException("Unity failed to delete asset: " + path);
                    }

                    return JsonUtility.ToJson(ActionResult.Ok(action, "Deleted asset: " + path));
                case "duplicate":
                    return JsonUtility.ToJson(CopyAsset(path, arguments.Destination));
                case "move":
                    return JsonUtility.ToJson(MoveAsset(path, arguments.Destination, action));
                case "rename":
                    return JsonUtility.ToJson(RenameAsset(path, arguments.Destination));
                case "search":
                    return JsonUtility.ToJson(SearchAssets(path, arguments));
                case "get_info":
                    return JsonUtility.ToJson(GetAssetInfo(path));
                case "get_components":
                    return JsonUtility.ToJson(GetAssetComponents(path));
                case "resolve_guid":
                    string resolvedPath = AssetDatabase.GUIDToAssetPath(arguments.Path);
                    if (string.IsNullOrEmpty(resolvedPath))
                    {
                        throw new ArgumentException("Asset GUID was not found: " + arguments.Path);
                    }

                    return JsonUtility.ToJson(GetAssetInfo(resolvedPath));
                default:
                    throw new ArgumentException("Unsupported manage_asset action: " + action);
            }
        }

        private static AssetInfoResult CreateAsset(string path, AssetArguments arguments)
        {
            string assetType = Require(arguments.AssetType, "asset_type");
            if (assetType.Equals("Folder", StringComparison.OrdinalIgnoreCase))
            {
                EnsureFolder(path);
                return new AssetInfoResult
                {
                    Success = true,
                    Action = "create",
                    Path = path,
                    Name = Path.GetFileName(path),
                    Type = "Folder",
                    Guid = AssetDatabase.AssetPathToGUID(path)
                };
            }

            EnsureFolder(Path.GetDirectoryName(path).Replace('\\', '/'));
            UnityEngine.Object asset;
            if (assetType.Equals("Material", StringComparison.OrdinalIgnoreCase))
            {
                Shader shader = Shader.Find("Standard");
                if (shader == null)
                {
                    throw new InvalidOperationException("Unity Standard shader was not found.");
                }

                asset = new Material(shader);
            }
            else if (assetType.Equals("AnimationClip", StringComparison.OrdinalIgnoreCase) ||
                     assetType.Equals("Animation", StringComparison.OrdinalIgnoreCase))
            {
                asset = new AnimationClip();
            }
            else if (assetType.Equals("PhysicMaterial", StringComparison.OrdinalIgnoreCase) ||
                     assetType.Equals("PhysicsMaterial", StringComparison.OrdinalIgnoreCase))
            {
                asset = new PhysicMaterial();
            }
            else if (assetType.Equals("PhysicsMaterial2D", StringComparison.OrdinalIgnoreCase))
            {
                asset = new PhysicsMaterial2D();
            }
            else
            {
                Type type = ResolveType(assetType);
                if (!typeof(ScriptableObject).IsAssignableFrom(type) || type.IsAbstract)
                {
                    throw new ArgumentException(
                        "asset_type must be Material, AnimationClip, PhysicMaterial, " +
                        "PhysicsMaterial2D, Folder, or a concrete ScriptableObject type.");
                }

                asset = ScriptableObject.CreateInstance(type);
            }

            AssetDatabase.CreateAsset(asset, path);
            if (arguments.Properties != null && arguments.Properties.Length > 0)
            {
                ApplySerializedPatches(asset, arguments.Properties);
            }

            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            return GetAssetInfo(path);
        }

        private static ActionResult ModifyAsset(string path, SerializedPatch[] patches)
        {
            UnityEngine.Object asset = LoadMainAsset(path);
            ApplySerializedPatches(asset, patches);
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            return ActionResult.Ok("modify", "Modified asset: " + path);
        }

        private static ActionResult CopyAsset(string source, string destination)
        {
            string target = NormalizeAssetPath(Require(destination, "destination"), false);
            EnsureFolder(Path.GetDirectoryName(target).Replace('\\', '/'));
            if (!AssetDatabase.CopyAsset(source, target))
            {
                throw new InvalidOperationException(
                    "Unity failed to duplicate asset from " + source + " to " + target + ".");
            }

            return ActionResult.Ok("duplicate", "Duplicated asset to: " + target);
        }

        private static ActionResult MoveAsset(string source, string destination, string action)
        {
            string target = NormalizeAssetPath(Require(destination, "destination"), false);
            EnsureFolder(Path.GetDirectoryName(target).Replace('\\', '/'));
            string error = AssetDatabase.MoveAsset(source, target);
            if (!string.IsNullOrEmpty(error))
            {
                throw new InvalidOperationException("Unity failed to move asset: " + error);
            }

            return ActionResult.Ok(action, "Moved asset to: " + target);
        }

        private static ActionResult RenameAsset(string source, string destination)
        {
            string name = Require(destination, "destination");
            if (name.Contains("/") || name.Contains("\\"))
            {
                return MoveAsset(source, destination, "rename");
            }

            string error = AssetDatabase.RenameAsset(source, Path.GetFileNameWithoutExtension(name));
            if (!string.IsNullOrEmpty(error))
            {
                throw new InvalidOperationException("Unity failed to rename asset: " + error);
            }

            return ActionResult.Ok("rename", "Renamed asset: " + source);
        }

        private static AssetSearchResult SearchAssets(string path, AssetArguments arguments)
        {
            string filter = arguments.SearchPattern ?? string.Empty;
            if (filter.IndexOf('*') >= 0)
            {
                string extension = Path.GetExtension(filter);
                filter = string.IsNullOrEmpty(extension)
                    ? string.Empty
                    : extension.TrimStart('.') + " ";
            }

            if (!string.IsNullOrEmpty(arguments.FilterType) && filter.IndexOf("t:") < 0)
            {
                filter = (filter + " t:" + arguments.FilterType).Trim();
            }

            string[] folders = AssetDatabase.IsValidFolder(path) ? new[] { path } : new[] { "Assets" };
            string[] guids = AssetDatabase.FindAssets(filter, folders);
            int pageSize = Math.Max(1, Math.Min(arguments.PageSize <= 0 ? 25 : arguments.PageSize, 500));
            int pageNumber = Math.Max(1, arguments.PageNumber);
            int offset = (pageNumber - 1) * pageSize;
            List<AssetRecord> records = new List<AssetRecord>();
            for (int index = offset; index < guids.Length && records.Count < pageSize; index++)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guids[index]);
                UnityEngine.Object asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
                records.Add(AssetRecord.From(assetPath, guids[index], asset));
            }

            return new AssetSearchResult
            {
                Success = true,
                SearchPath = path,
                Filter = filter,
                Total = guids.Length,
                PageNumber = pageNumber,
                PageSize = pageSize,
                HasMore = offset + records.Count < guids.Length,
                Assets = records.ToArray()
            };
        }

        private static AssetInfoResult GetAssetInfo(string path)
        {
            UnityEngine.Object asset = LoadMainAsset(path);
            return new AssetInfoResult
            {
                Success = true,
                Action = "get_info",
                Path = path,
                Name = asset.name,
                Type = asset.GetType().FullName,
                Guid = AssetDatabase.AssetPathToGUID(path),
                FileSize = GetAssetFileSize(path),
                Labels = AssetDatabase.GetLabels(asset),
                Dependencies = AssetDatabase.GetDependencies(path, false)
            };
        }

        private static AssetComponentsResult GetAssetComponents(string path)
        {
            GameObject gameObject = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (gameObject == null)
            {
                throw new ArgumentException("Asset is not a GameObject or prefab: " + path);
            }

            Component[] components = gameObject.GetComponents<Component>();
            return new AssetComponentsResult
            {
                Success = true,
                Path = path,
                Components = components.Select(component => component == null
                    ? "<Missing Script>"
                    : component.GetType().FullName).ToArray()
            };
        }

        private static string ManageMaterial(MaterialArguments arguments)
        {
            string action = Require(arguments.Action, "action").ToLowerInvariant();
            if (action == "ping")
            {
                return JsonUtility.ToJson(ActionResult.Ok(action, "Unity material tools are available."));
            }

            if (action == "create")
            {
                string path = NormalizeAssetPath(arguments.MaterialPath, false);
                EnsureFolder(Path.GetDirectoryName(path).Replace('\\', '/'));
                if (AssetDatabase.LoadMainAssetAtPath(path) != null)
                {
                    throw new InvalidOperationException("Material asset already exists: " + path);
                }

                Shader shader = Shader.Find(string.IsNullOrEmpty(arguments.Shader)
                    ? "Standard"
                    : arguments.Shader);
                if (shader == null)
                {
                    throw new ArgumentException("Shader was not found: " + arguments.Shader);
                }

                Material material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
                ApplyMaterialPatches(material, arguments.Properties);
                EditorUtility.SetDirty(material);
                AssetDatabase.SaveAssets();
                return JsonUtility.ToJson(GetMaterialInfo(material, path, action));
            }

            if (action == "get_material_info")
            {
                string path = NormalizeAssetPath(arguments.MaterialPath, false);
                return JsonUtility.ToJson(GetMaterialInfo(LoadMaterial(path), path, action));
            }

            if (action == "set_material_shader_property" || action == "set_material_color")
            {
                string path = NormalizeAssetPath(arguments.MaterialPath, false);
                Material material = LoadMaterial(path);
                Undo.RecordObject(material, "MCP Modify Material");
                SerializedPatch value = action == "set_material_color" ? arguments.Color : arguments.Value;
                if (value == null)
                {
                    throw new ArgumentException(action + " requires value or color.");
                }

                string property = string.IsNullOrEmpty(arguments.Property) ? "_Color" : arguments.Property;
                value.Path = property;
                ApplyMaterialPatch(material, value);
                EditorUtility.SetDirty(material);
                AssetDatabase.SaveAssets();
                return JsonUtility.ToJson(GetMaterialInfo(material, path, action));
            }

            if (action == "assign_material_to_renderer")
            {
                Material material = LoadMaterial(NormalizeAssetPath(arguments.MaterialPath, false));
                Renderer renderer = ResolveRenderer(arguments);
                Undo.RecordObject(renderer, "MCP Assign Material");
                Material[] materials = arguments.Mode == "instance"
                    ? renderer.materials
                    : renderer.sharedMaterials;
                if (arguments.Slot < 0 || arguments.Slot >= materials.Length)
                {
                    throw new ArgumentException("slot is outside the renderer material array.");
                }

                materials[arguments.Slot] = material;
                if (arguments.Mode == "instance")
                {
                    renderer.materials = materials;
                }
                else
                {
                    renderer.sharedMaterials = materials;
                }

                return JsonUtility.ToJson(ActionResult.Ok(action, "Material assigned to " + renderer.name + "."));
            }

            if (action == "set_renderer_color")
            {
                Renderer renderer = ResolveRenderer(arguments);
                Color color = PatchColor(arguments.Color);
                string property = string.IsNullOrEmpty(arguments.Property) ? "_Color" : arguments.Property;
                Undo.RecordObject(renderer, "MCP Set Renderer Color");
                if (arguments.Mode == "property_block")
                {
                    MaterialPropertyBlock block = new MaterialPropertyBlock();
                    renderer.GetPropertyBlock(block);
                    block.SetColor(property, color);
                    renderer.SetPropertyBlock(block);
                }
                else
                {
                    Material material = arguments.Mode == "shared"
                        ? renderer.sharedMaterial
                        : renderer.material;
                    if (material == null)
                    {
                        throw new InvalidOperationException("Renderer has no material.");
                    }

                    material.SetColor(property, color);
                    EditorUtility.SetDirty(material);
                }

                return JsonUtility.ToJson(ActionResult.Ok(action, "Renderer color updated."));
            }

            throw new ArgumentException("Unsupported manage_material action: " + action);
        }

        private static MaterialInfoResult GetMaterialInfo(Material material, string path, string action)
        {
            int propertyCount = ShaderUtil.GetPropertyCount(material.shader);
            MaterialPropertyRecord[] properties = new MaterialPropertyRecord[propertyCount];
            for (int index = 0; index < propertyCount; index++)
            {
                string name = ShaderUtil.GetPropertyName(material.shader, index);
                ShaderUtil.ShaderPropertyType type = ShaderUtil.GetPropertyType(material.shader, index);
                string value;
                switch (type)
                {
                    case ShaderUtil.ShaderPropertyType.Color:
                        value = material.GetColor(name).ToString();
                        break;
                    case ShaderUtil.ShaderPropertyType.Vector:
                        value = material.GetVector(name).ToString("R");
                        break;
                    case ShaderUtil.ShaderPropertyType.TexEnv:
                        Texture texture = material.GetTexture(name);
                        value = texture == null ? "null" : AssetDatabase.GetAssetPath(texture);
                        break;
                    default:
                        value = material.GetFloat(name).ToString("R");
                        break;
                }

                properties[index] = new MaterialPropertyRecord
                {
                    Name = name,
                    Description = ShaderUtil.GetPropertyDescription(material.shader, index),
                    Type = type.ToString(),
                    Value = value
                };
            }

            return new MaterialInfoResult
            {
                Success = true,
                Action = action,
                Path = path,
                Name = material.name,
                Shader = material.shader == null ? string.Empty : material.shader.name,
                RenderQueue = material.renderQueue,
                Properties = properties
            };
        }

        private static void ApplyMaterialPatches(Material material, SerializedPatch[] patches)
        {
            if (patches == null)
            {
                return;
            }

            for (int index = 0; index < patches.Length; index++)
            {
                ApplyMaterialPatch(material, patches[index]);
            }
        }

        private static void ApplyMaterialPatch(Material material, SerializedPatch patch)
        {
            string property = Require(patch.Path, "material property");
            if (!material.HasProperty(property))
            {
                throw new ArgumentException(
                    "Material shader does not expose property: " + property);
            }

            switch (patch.Kind)
            {
                case "bool":
                    material.SetFloat(property, patch.BoolValue ? 1f : 0f);
                    break;
                case "int":
                    material.SetInt(property, patch.IntValue);
                    break;
                case "float":
                    material.SetFloat(property, patch.FloatValue);
                    break;
                case "vector":
                    if (patch.VectorLength == 4 &&
                        (property.IndexOf("color", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         property.IndexOf("tint", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         property == "_Color" || property == "_BaseColor"))
                    {
                        material.SetColor(property, PatchColor(patch));
                    }
                    else
                    {
                        material.SetVector(property, PatchVector(patch));
                    }
                    break;
                case "string":
                case "reference":
                    string path = patch.Kind == "reference"
                        ? ResolveReferencePath(patch)
                        : patch.StringValue;
                    Texture texture = AssetDatabase.LoadAssetAtPath<Texture>(path);
                    if (texture == null)
                    {
                        throw new ArgumentException("Texture asset was not found: " + path);
                    }

                    material.SetTexture(property, texture);
                    break;
                case "null":
                    material.SetTexture(property, null);
                    break;
                default:
                    throw new ArgumentException("Unsupported material value kind: " + patch.Kind);
            }
        }

        private static void ApplySerializedPatches(
            UnityEngine.Object target,
            SerializedPatch[] patches)
        {
            if (patches == null || patches.Length == 0)
            {
                return;
            }

            SerializedObject serialized = new SerializedObject(target);
            for (int index = 0; index < patches.Length; index++)
            {
                SerializedPatch patch = patches[index];
                SerializedProperty property = serialized.FindProperty(patch.Path);
                if (property == null)
                {
                    throw new ArgumentException(
                        "Serialized property was not found: " + patch.Path);
                }

                SetSerializedProperty(property, patch);
            }

            serialized.ApplyModifiedProperties();
        }

        private static void SetSerializedProperty(SerializedProperty property, SerializedPatch patch)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Boolean:
                    property.boolValue = patch.Kind == "bool" ? patch.BoolValue : patch.IntValue != 0;
                    break;
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.LayerMask:
                    property.intValue = patch.Kind == "int"
                        ? patch.IntValue
                        : Mathf.RoundToInt(patch.FloatValue);
                    break;
                case SerializedPropertyType.Float:
                    property.floatValue = patch.Kind == "int" ? patch.IntValue : patch.FloatValue;
                    break;
                case SerializedPropertyType.String:
                    property.stringValue = patch.StringValue ?? string.Empty;
                    break;
                case SerializedPropertyType.Enum:
                    if (patch.Kind == "int")
                    {
                        property.enumValueIndex = patch.IntValue;
                    }
                    else
                    {
                        int index = Array.FindIndex(property.enumNames, value =>
                            string.Equals(value, patch.StringValue, StringComparison.OrdinalIgnoreCase));
                        if (index < 0)
                        {
                            throw new ArgumentException("Enum value was not found: " + patch.StringValue);
                        }

                        property.enumValueIndex = index;
                    }

                    break;
                case SerializedPropertyType.Color:
                    property.colorValue = PatchColor(patch);
                    break;
                case SerializedPropertyType.Vector2:
                    Vector4 vector2 = PatchVector(patch);
                    property.vector2Value = new Vector2(vector2.x, vector2.y);
                    break;
                case SerializedPropertyType.Vector3:
                    Vector4 vector3 = PatchVector(patch);
                    property.vector3Value = new Vector3(vector3.x, vector3.y, vector3.z);
                    break;
                case SerializedPropertyType.Vector4:
                    property.vector4Value = PatchVector(patch);
                    break;
                case SerializedPropertyType.ObjectReference:
                    property.objectReferenceValue = patch.Kind == "null"
                        ? null
                        : AssetDatabase.LoadMainAssetAtPath(ResolveReferencePath(patch));
                    break;
                default:
                    throw new ArgumentException(
                        "Unsupported serialized property type: " + property.propertyType);
            }
        }

        private static Renderer ResolveRenderer(MaterialArguments arguments)
        {
            GameObject gameObject = ResolveSceneGameObject(
                arguments.TargetInstanceId,
                arguments.Target,
                arguments.SearchMethod);
            Renderer renderer = gameObject.GetComponent<Renderer>();
            if (renderer == null)
            {
                throw new ArgumentException("Target GameObject has no Renderer: " + gameObject.name);
            }

            return renderer;
        }

        private static GameObject ResolveSceneGameObject(int instanceId, string target, string method)
        {
            if (instanceId != 0)
            {
                GameObject byId = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
                if (IsSceneObject(byId))
                {
                    return byId;
                }
            }

            string query = Require(target, "target").Trim('/');
            GameObject[] candidates = Resources.FindObjectsOfTypeAll<GameObject>();
            for (int index = 0; index < candidates.Length; index++)
            {
                GameObject candidate = candidates[index];
                if (!IsSceneObject(candidate))
                {
                    continue;
                }

                bool match = string.Equals(method, "by_path", StringComparison.OrdinalIgnoreCase)
                    ? string.Equals(GetPath(candidate.transform), query, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(candidate.name, query, StringComparison.OrdinalIgnoreCase);
                if (match)
                {
                    return candidate;
                }
            }

            throw new ArgumentException("Target GameObject was not found: " + query);
        }

        private static bool IsSceneObject(GameObject gameObject)
        {
            return gameObject != null && !EditorUtility.IsPersistent(gameObject) &&
                   gameObject.scene.IsValid() && gameObject.scene.isLoaded;
        }

        private static string GetPath(Transform transform)
        {
            List<string> names = new List<string>();
            while (transform != null)
            {
                names.Add(transform.name);
                transform = transform.parent;
            }

            names.Reverse();
            return string.Join("/", names.ToArray());
        }

        private static Material LoadMaterial(string path)
        {
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                throw new FileNotFoundException("Material asset was not found: " + path);
            }

            return material;
        }

        private static UnityEngine.Object LoadMainAsset(string path)
        {
            UnityEngine.Object asset = AssetDatabase.LoadMainAssetAtPath(path);
            if (asset == null)
            {
                throw new FileNotFoundException("Asset was not found: " + path);
            }

            return asset;
        }

        private static long GetAssetFileSize(string path)
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string fullPath = Path.Combine(projectRoot, path.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(fullPath) ? new FileInfo(fullPath).Length : 0L;
        }

        private static void EnsureFolder(string path)
        {
            if (string.IsNullOrEmpty(path) || path == "Assets" || AssetDatabase.IsValidFolder(path))
            {
                return;
            }

            string normalized = NormalizeAssetPath(path, true).TrimEnd('/');
            string parent = Path.GetDirectoryName(normalized).Replace('\\', '/');
            EnsureFolder(parent);
            string guid = AssetDatabase.CreateFolder(parent, Path.GetFileName(normalized));
            if (string.IsNullOrEmpty(guid))
            {
                throw new InvalidOperationException("Unity failed to create folder: " + normalized);
            }
        }

        private static string NormalizeAssetPath(string value, bool allowFolder)
        {
            string path = Require(value, "path").Replace('\\', '/').Trim();
            if (!path.StartsWith("Assets", StringComparison.OrdinalIgnoreCase))
            {
                path = "Assets/" + path.TrimStart('/');
            }

            if (path.Contains("../") || path.Contains("/..") || path.IndexOf(':') >= 0)
            {
                throw new ArgumentException("Asset path must stay under Assets/.");
            }

            if (!allowFolder && path == "Assets")
            {
                throw new ArgumentException("An asset file path is required.");
            }

            return path;
        }

        private static string ResolveReferencePath(SerializedPatch patch)
        {
            string path = patch.ReferencePath;
            if (string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(patch.ReferenceGuid))
            {
                path = AssetDatabase.GUIDToAssetPath(patch.ReferenceGuid);
            }

            if (string.IsNullOrEmpty(path) && patch.Kind == "string")
            {
                path = patch.StringValue;
            }

            return NormalizeAssetPath(path, false);
        }

        private static Color PatchColor(SerializedPatch patch)
        {
            Vector4 value = PatchVector(patch);
            return new Color(value.x, value.y, value.z, patch.VectorLength >= 4 ? value.w : 1f);
        }

        private static Vector4 PatchVector(SerializedPatch patch)
        {
            if (patch == null || patch.FloatValues == null || patch.FloatValues.Length < 2)
            {
                throw new ArgumentException("A numeric vector value is required.");
            }

            return new Vector4(
                patch.FloatValues[0],
                patch.FloatValues[1],
                patch.FloatValues.Length > 2 ? patch.FloatValues[2] : 0f,
                patch.FloatValues.Length > 3 ? patch.FloatValues[3] : 0f);
        }

        private static Type ResolveType(string requestedName)
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            List<Type> matches = new List<Type>();
            for (int assemblyIndex = 0; assemblyIndex < assemblies.Length; assemblyIndex++)
            {
                Type exact = assemblies[assemblyIndex].GetType(requestedName, false, true);
                if (exact != null)
                {
                    return exact;
                }

                Type[] types;
                try { types = assemblies[assemblyIndex].GetTypes(); }
                catch (ReflectionTypeLoadException error) { types = error.Types; }
                catch { continue; }
                for (int typeIndex = 0; typeIndex < types.Length; typeIndex++)
                {
                    Type type = types[typeIndex];
                    if (type != null && string.Equals(
                        type.Name, requestedName, StringComparison.OrdinalIgnoreCase))
                    {
                        matches.Add(type);
                    }
                }
            }

            if (matches.Count == 1) return matches[0];
            throw new ArgumentException(
                matches.Count == 0
                    ? "Type was not found: " + requestedName
                    : "Type name is ambiguous; use a fully qualified name: " + requestedName);
        }

        private static string Require(string value, string field)
        {
            if (string.IsNullOrEmpty(value) || value.Trim().Length == 0)
            {
                throw new ArgumentException(field + " must be a non-empty string.");
            }

            return value.Trim();
        }

        [Serializable] private sealed class AssetArguments
        {
            public string Action;
            public string Path;
            public string AssetType;
            public string Destination;
            public string SearchPattern;
            public string FilterType;
            public int PageSize = 25;
            public int PageNumber = 1;
            public bool GeneratePreview;
            public SerializedPatch[] Properties;
        }

        [Serializable] private sealed class MaterialArguments
        {
            public string Action;
            public string MaterialPath;
            public string Property;
            public string Shader;
            public SerializedPatch[] Properties;
            public SerializedPatch Value;
            public SerializedPatch Color;
            public string Target;
            public int TargetInstanceId;
            public string SearchMethod;
            public int Slot;
            public string Mode;
        }

        [Serializable] private sealed class SerializedPatch
        {
            public string Path;
            public string Kind;
            public bool BoolValue;
            public int IntValue;
            public float FloatValue;
            public string StringValue;
            public int VectorLength;
            public float[] FloatValues;
            public string ReferencePath;
            public string ReferenceGuid;
        }

        [Serializable] private sealed class ActionResult
        {
            public bool Success;
            public string Action;
            public string Message;

            public static ActionResult Ok(string action, string message)
            {
                return new ActionResult { Success = true, Action = action, Message = message };
            }
        }

        [Serializable] private sealed class AssetInfoResult
        {
            public bool Success;
            public string Action;
            public string Path;
            public string Name;
            public string Type;
            public string Guid;
            public long FileSize;
            public string[] Labels;
            public string[] Dependencies;
        }

        [Serializable] private sealed class AssetSearchResult
        {
            public bool Success;
            public string SearchPath;
            public string Filter;
            public int Total;
            public int PageNumber;
            public int PageSize;
            public bool HasMore;
            public AssetRecord[] Assets;
        }

        [Serializable] private sealed class AssetRecord
        {
            public string Path;
            public string Guid;
            public string Name;
            public string Type;

            public static AssetRecord From(string path, string guid, UnityEngine.Object asset)
            {
                return new AssetRecord
                {
                    Path = path,
                    Guid = guid,
                    Name = asset == null ? System.IO.Path.GetFileNameWithoutExtension(path) : asset.name,
                    Type = asset == null ? string.Empty : asset.GetType().FullName
                };
            }
        }

        [Serializable] private sealed class AssetComponentsResult
        {
            public bool Success;
            public string Path;
            public string[] Components;
        }

        [Serializable] private sealed class MaterialInfoResult
        {
            public bool Success;
            public string Action;
            public string Path;
            public string Name;
            public string Shader;
            public int RenderQueue;
            public MaterialPropertyRecord[] Properties;
        }

        [Serializable] private sealed class MaterialPropertyRecord
        {
            public string Name;
            public string Description;
            public string Type;
            public string Value;
        }
    }
}
#endif
