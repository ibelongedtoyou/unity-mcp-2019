#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace UnityMcp2019
{
    internal static class Mcp2019ResourceTools
    {
        internal static string Execute(string argumentsJson)
        {
            ResourceArguments arguments = string.IsNullOrEmpty(argumentsJson) || argumentsJson == "{}"
                ? new ResourceArguments()
                : JsonUtility.FromJson<ResourceArguments>(argumentsJson) ?? new ResourceArguments();
            switch ((arguments.Kind ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "project_info":
                    return JsonUtility.ToJson(GetProjectInfo());
                case "project_layers":
                    return JsonUtility.ToJson(new StringListResult
                    {
                        Success = true,
                        Kind = arguments.Kind,
                        Values = InternalEditorUtility.layers
                    });
                case "project_tags":
                    return JsonUtility.ToJson(new StringListResult
                    {
                        Success = true,
                        Kind = arguments.Kind,
                        Values = InternalEditorUtility.tags
                    });
                case "instances":
                    return JsonUtility.ToJson(GetInstances());
                case "menu_items":
                    return JsonUtility.ToJson(new StringListResult
                    {
                        Success = true,
                        Kind = arguments.Kind,
                        Values = GetMenuItems()
                    });
                case "editor_active_tool":
                    return JsonUtility.ToJson(new StringValueResult
                    {
                        Success = true,
                        Kind = arguments.Kind,
                        Value = Tools.current.ToString()
                    });
                case "editor_selection":
                    return JsonUtility.ToJson(GetSelection());
                case "editor_windows":
                    return JsonUtility.ToJson(GetWindows());
                case "editor_prefab_stage":
                    return JsonUtility.ToJson(GetPrefabStage());
                case "rendering_stats":
                    return JsonUtility.ToJson(GetRenderingStats());
                case "renderer_features":
                    return JsonUtility.ToJson(GetRendererFeatures());
                case "scene_cameras":
                    return JsonUtility.ToJson(GetCameras());
                case "scene_volumes":
                    return JsonUtility.ToJson(GetVolumes());
                case "custom_tools":
                    return Mcp2019ExecutionTools.ListCustomToolsJson();
                case "tool_groups":
                    return JsonUtility.ToJson(GetToolGroups());
                case "gameobject_api":
                    return JsonUtility.ToJson(GetApiDescription("gameobject_api"));
                case "prefab_api":
                    return JsonUtility.ToJson(GetApiDescription("prefab_api"));
                case "gameobject_components":
                    return JsonUtility.ToJson(GetGameObjectComponents(arguments.InstanceId));
                case "gameobject_component":
                    return JsonUtility.ToJson(GetGameObjectComponent(
                        arguments.InstanceId,
                        Require(arguments.ComponentName, "component_name")));
                case "prefab_info":
                    return JsonUtility.ToJson(GetPrefabInfo(arguments.AssetPath));
                case "prefab_hierarchy":
                    return JsonUtility.ToJson(GetPrefabHierarchy(arguments.AssetPath));
                case "tests":
                    return JsonUtility.ToJson(GetTests(arguments.Mode));
                default:
                    throw new ArgumentException("Unknown resource snapshot kind: " + arguments.Kind);
            }
        }

        private static ProjectInfoResult GetProjectInfo()
        {
            Scene scene = SceneManager.GetActiveScene();
            return new ProjectInfoResult
            {
                Success = true,
                ProjectPath = Directory.GetParent(Application.dataPath).FullName,
                AssetsPath = Application.dataPath,
                ProjectName = new DirectoryInfo(Directory.GetParent(Application.dataPath).FullName).Name,
                ProductName = Application.productName,
                CompanyName = Application.companyName,
                UnityVersion = Application.unityVersion,
                Platform = EditorUserBuildSettings.activeBuildTarget.ToString(),
                ActiveSceneName = scene.IsValid() ? scene.name : string.Empty,
                ActiveScenePath = scene.IsValid() ? scene.path : string.Empty,
                IsPlaying = EditorApplication.isPlaying,
                IsCompiling = EditorApplication.isCompiling,
                ColorSpace = PlayerSettings.colorSpace.ToString(),
                RenderPipeline = GraphicsSettings.renderPipelineAsset == null
                    ? "Built-in"
                    : GraphicsSettings.renderPipelineAsset.GetType().FullName
            };
        }

        private static string[] GetMenuItems()
        {
            HashSet<string> items = new HashSet<string>(StringComparer.Ordinal);
            string[] roots =
            {
                "File", "Edit", "Assets", "GameObject", "Component", "Window", "Help", "Tools"
            };
            for (int index = 0; index < roots.Length; index++)
            {
                string[] submenus = Unsupported.GetSubmenus(roots[index]);
                if (submenus == null)
                {
                    continue;
                }

                for (int submenuIndex = 0; submenuIndex < submenus.Length; submenuIndex++)
                {
                    items.Add(submenus[submenuIndex]);
                }
            }

            Type menuType = typeof(Editor).Assembly.GetType("UnityEditor.Menu");
            if (menuType != null)
            {
                MethodInfo[] methods = menuType.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                for (int methodIndex = 0; methodIndex < methods.Length; methodIndex++)
                {
                    MethodInfo method = methods[methodIndex];
                    if (method.Name != "GetMenuItems")
                    {
                        continue;
                    }

                    ParameterInfo[] parameters = method.GetParameters();
                    object[] values = new object[parameters.Length];
                    bool supported = true;
                    for (int parameterIndex = 0; parameterIndex < parameters.Length; parameterIndex++)
                    {
                        Type parameterType = parameters[parameterIndex].ParameterType;
                        if (parameterType == typeof(string))
                        {
                            values[parameterIndex] = string.Empty;
                        }
                        else if (parameterType == typeof(bool))
                        {
                            values[parameterIndex] = false;
                        }
                        else if (parameterType == typeof(int))
                        {
                            values[parameterIndex] = 0;
                        }
                        else
                        {
                            supported = false;
                            break;
                        }
                    }

                    if (!supported)
                    {
                        continue;
                    }

                    try
                    {
                        string[] reflected = method.Invoke(null, values) as string[];
                        if (reflected != null)
                        {
                            for (int valueIndex = 0; valueIndex < reflected.Length; valueIndex++)
                            {
                                items.Add(reflected[valueIndex]);
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            }

            return items.OrderBy(value => value).ToArray();
        }

        private static InstanceListResult GetInstances()
        {
            string projectPath = Directory.GetParent(Application.dataPath).FullName;
            return new InstanceListResult
            {
                Success = true,
                Instances = new[]
                {
                    new InstanceRecord
                    {
                        Name = new DirectoryInfo(projectPath).Name,
                        ProjectPath = projectPath,
                        ProcessId = System.Diagnostics.Process.GetCurrentProcess().Id,
                        UnityVersion = Application.unityVersion,
                        IsActive = true
                    }
                }
            };
        }

        private static ObjectListResult GetSelection()
        {
            UnityEngine.Object[] selected = Selection.objects ?? new UnityEngine.Object[0];
            ObjectRecord[] records = new ObjectRecord[selected.Length];
            for (int index = 0; index < selected.Length; index++)
            {
                records[index] = ObjectRecord.From(selected[index]);
            }

            return new ObjectListResult
            {
                Success = true,
                Kind = "editor_selection",
                Count = records.Length,
                Objects = records
            };
        }

        private static WindowListResult GetWindows()
        {
            EditorWindow[] windows = Resources.FindObjectsOfTypeAll<EditorWindow>();
            WindowRecord[] records = windows
                .Where(window => window != null)
                .Select(window => new WindowRecord
                {
                    InstanceId = window.GetInstanceID(),
                    Type = window.GetType().FullName,
                    Title = window.titleContent == null ? string.Empty : window.titleContent.text,
                    HasFocus = EditorWindow.focusedWindow == window,
                    Position = new FloatArray
                    {
                        Values = new[]
                        {
                            window.position.x,
                            window.position.y,
                            window.position.width,
                            window.position.height
                        }
                    }
                })
                .OrderBy(record => record.Type)
                .ToArray();
            return new WindowListResult
            {
                Success = true,
                Count = records.Length,
                Windows = records
            };
        }

        private static PrefabStageResult GetPrefabStage()
        {
            Type utilityType = Type.GetType(
                "UnityEditor.Experimental.SceneManagement.PrefabStageUtility, UnityEditor");
            if (utilityType == null)
            {
                utilityType = Type.GetType("UnityEditor.SceneManagement.PrefabStageUtility, UnityEditor");
            }

            object stage = null;
            if (utilityType != null)
            {
                MethodInfo method = utilityType.GetMethod(
                    "GetCurrentPrefabStage",
                    BindingFlags.Public | BindingFlags.Static);
                if (method != null)
                {
                    stage = method.Invoke(null, null);
                }
            }

            if (stage == null)
            {
                return new PrefabStageResult { Success = true, IsOpen = false };
            }

            Type stageType = stage.GetType();
            return new PrefabStageResult
            {
                Success = true,
                IsOpen = true,
                AssetPath = ReadStringProperty(stage, stageType, "assetPath"),
                PrefabContentsRootName = ReadObjectNameProperty(
                    stage,
                    stageType,
                    "prefabContentsRoot")
            };
        }

        private static RenderingStatsResult GetRenderingStats()
        {
            return new RenderingStatsResult
            {
                Success = true,
                DrawCalls = UnityStats.drawCalls,
                Batches = UnityStats.batches,
                SetPassCalls = UnityStats.setPassCalls,
                Triangles = UnityStats.triangles,
                Vertices = UnityStats.vertices,
                DynamicBatchedDrawCalls = UnityStats.dynamicBatchedDrawCalls,
                StaticBatchedDrawCalls = UnityStats.staticBatchedDrawCalls,
                RenderPipeline = GraphicsSettings.renderPipelineAsset == null
                    ? "Built-in"
                    : GraphicsSettings.renderPipelineAsset.GetType().FullName
            };
        }

        private static RendererFeaturesResult GetRendererFeatures()
        {
            RenderPipelineAsset asset = GraphicsSettings.renderPipelineAsset;
            return new RendererFeaturesResult
            {
                Success = true,
                Pipeline = asset == null ? "Built-in" : asset.GetType().FullName,
                PipelineAssetPath = asset == null ? string.Empty : AssetDatabase.GetAssetPath(asset),
                Features = new string[0],
                Message = asset == null
                    ? "The Built-in Render Pipeline does not expose renderer feature assets."
                    : "Renderer feature enumeration is unavailable through the Unity 2019 public API."
            };
        }

        private static CameraListResult GetCameras()
        {
            Camera[] cameras = Resources.FindObjectsOfTypeAll<Camera>();
            List<CameraRecord> records = new List<CameraRecord>();
            for (int index = 0; index < cameras.Length; index++)
            {
                Camera camera = cameras[index];
                if (camera == null || EditorUtility.IsPersistent(camera) ||
                    !camera.gameObject.scene.IsValid() || !camera.gameObject.scene.isLoaded)
                {
                    continue;
                }

                records.Add(new CameraRecord
                {
                    InstanceId = camera.gameObject.GetInstanceID(),
                    Name = camera.name,
                    Path = GetPath(camera.transform),
                    Scene = camera.gameObject.scene.name,
                    Enabled = camera.enabled,
                    Orthographic = camera.orthographic,
                    FieldOfView = camera.fieldOfView,
                    Depth = camera.depth,
                    CullingMask = camera.cullingMask,
                    IsMain = Camera.main == camera
                });
            }

            return new CameraListResult
            {
                Success = true,
                Count = records.Count,
                Cameras = records.OrderBy(record => record.Path).ToArray()
            };
        }

        private static VolumeListResult GetVolumes()
        {
            Component[] components = Resources.FindObjectsOfTypeAll<Component>();
            List<VolumeRecord> records = new List<VolumeRecord>();
            for (int index = 0; index < components.Length; index++)
            {
                Component component = components[index];
                if (component == null || EditorUtility.IsPersistent(component) ||
                    !component.gameObject.scene.IsValid() || !component.gameObject.scene.isLoaded)
                {
                    continue;
                }

                string typeName = component.GetType().Name;
                if (!typeName.EndsWith("Volume", StringComparison.OrdinalIgnoreCase) &&
                    typeName.IndexOf("PostProcessVolume", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                records.Add(new VolumeRecord
                {
                    InstanceId = component.gameObject.GetInstanceID(),
                    Name = component.gameObject.name,
                    Path = GetPath(component.transform),
                    ComponentType = component.GetType().FullName,
                    Enabled = !(component is Behaviour) || ((Behaviour)component).enabled
                });
            }

            return new VolumeListResult
            {
                Success = true,
                Count = records.Count,
                Volumes = records.OrderBy(record => record.Path).ToArray()
            };
        }

        private static ToolGroupsResult GetToolGroups()
        {
            string[] groups =
            {
                "animation", "asset_gen", "core", "docs", "probuilder",
                "profiling", "scripting_ext", "testing", "ui", "vfx"
            };
            ToolGroupRecord[] records = new ToolGroupRecord[groups.Length];
            for (int index = 0; index < groups.Length; index++)
            {
                records[index] = new ToolGroupRecord
                {
                    Name = groups[index],
                    Active = true,
                    Available = groups[index] != "asset_gen" && groups[index] != "probuilder"
                };
            }

            return new ToolGroupsResult { Success = true, Groups = records };
        }

        private static ApiDescriptionResult GetApiDescription(string kind)
        {
            if (kind == "gameobject_api")
            {
                return new ApiDescriptionResult
                {
                    Success = true,
                    Kind = kind,
                    Actions = new[] { "create", "modify", "delete" },
                    LookupMethods = new[]
                    {
                        "by_name", "by_tag", "by_layer", "by_component", "by_path", "by_id"
                    },
                    Notes = "Mutations are Undo-aware and limited to loaded scene objects."
                };
            }

            return new ApiDescriptionResult
            {
                Success = true,
                Kind = kind,
                Actions = new[]
                {
                    "create", "modify", "delete", "instantiate", "open", "save", "close"
                },
                LookupMethods = new[] { "asset_path" },
                Notes = "Prefab resources inspect assets without modifying them."
            };
        }

        private static ComponentListResult GetGameObjectComponents(int instanceId)
        {
            GameObject gameObject = ResolveSceneGameObject(instanceId);
            Component[] components = gameObject.GetComponents<Component>();
            ComponentRecord[] records = new ComponentRecord[components.Length];
            for (int index = 0; index < components.Length; index++)
            {
                records[index] = CreateComponentRecord(components[index], index, false);
            }

            return new ComponentListResult
            {
                Success = true,
                InstanceId = instanceId,
                GameObjectName = gameObject.name,
                Components = records
            };
        }

        private static ComponentDetailResult GetGameObjectComponent(
            int instanceId,
            string requestedType)
        {
            GameObject gameObject = ResolveSceneGameObject(instanceId);
            Component[] components = gameObject.GetComponents<Component>();
            for (int index = 0; index < components.Length; index++)
            {
                Component component = components[index];
                if (component == null)
                {
                    continue;
                }

                Type type = component.GetType();
                if (string.Equals(type.Name, requestedType, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(type.FullName, requestedType, StringComparison.OrdinalIgnoreCase))
                {
                    return new ComponentDetailResult
                    {
                        Success = true,
                        InstanceId = instanceId,
                        GameObjectName = gameObject.name,
                        Component = CreateComponentRecord(component, index, true)
                    };
                }
            }

            throw new ArgumentException(
                "Component was not found on GameObject " + instanceId + ": " + requestedType);
        }

        private static ComponentRecord CreateComponentRecord(
            Component component,
            int index,
            bool includeProperties)
        {
            if (component == null)
            {
                return new ComponentRecord
                {
                    Index = index,
                    Type = "<Missing Script>",
                    Properties = new SerializedPropertyRecord[0]
                };
            }

            List<SerializedPropertyRecord> properties = new List<SerializedPropertyRecord>();
            if (includeProperties)
            {
                SerializedObject serialized = new SerializedObject(component);
                SerializedProperty iterator = serialized.GetIterator();
                bool enterChildren = true;
                while (iterator.NextVisible(enterChildren) && properties.Count < 300)
                {
                    enterChildren = false;
                    properties.Add(new SerializedPropertyRecord
                    {
                        Path = iterator.propertyPath,
                        Type = iterator.propertyType.ToString(),
                        Value = SerializedPropertyValue(iterator)
                    });
                }
            }

            return new ComponentRecord
            {
                Index = index,
                InstanceId = component.GetInstanceID(),
                Type = component.GetType().FullName,
                Enabled = !(component is Behaviour) || ((Behaviour)component).enabled,
                Properties = properties.ToArray()
            };
        }

        private static string SerializedPropertyValue(SerializedProperty property)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.LayerMask:
                    return property.intValue.ToString();
                case SerializedPropertyType.Boolean:
                    return property.boolValue.ToString();
                case SerializedPropertyType.Float:
                    return property.floatValue.ToString("R");
                case SerializedPropertyType.String:
                    return property.stringValue ?? string.Empty;
                case SerializedPropertyType.Enum:
                    return property.enumDisplayNames != null &&
                           property.enumValueIndex >= 0 &&
                           property.enumValueIndex < property.enumDisplayNames.Length
                        ? property.enumDisplayNames[property.enumValueIndex]
                        : property.enumValueIndex.ToString();
                case SerializedPropertyType.ObjectReference:
                    return property.objectReferenceValue == null
                        ? "null"
                        : property.objectReferenceValue.name + " (" +
                          property.objectReferenceValue.GetInstanceID() + ")";
                case SerializedPropertyType.Vector2:
                    return property.vector2Value.ToString("R");
                case SerializedPropertyType.Vector3:
                    return property.vector3Value.ToString("R");
                case SerializedPropertyType.Vector4:
                    return property.vector4Value.ToString("R");
                case SerializedPropertyType.Color:
                    return property.colorValue.ToString();
                default:
                    return property.isArray ? "Array[" + property.arraySize + "]" : string.Empty;
            }
        }

        private static PrefabInfoResult GetPrefabInfo(string path)
        {
            string normalized = ValidatePrefabPath(path);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(normalized);
            if (prefab == null)
            {
                throw new FileNotFoundException("Prefab asset was not found: " + normalized);
            }

            return new PrefabInfoResult
            {
                Success = true,
                AssetPath = normalized,
                Name = prefab.name,
                Guid = AssetDatabase.AssetPathToGUID(normalized),
                RootComponentTypes = prefab.GetComponents<Component>()
                    .Select(component => component == null
                        ? "<Missing Script>"
                        : component.GetType().FullName)
                    .ToArray(),
                ChildCount = prefab.GetComponentsInChildren<Transform>(true).Length - 1
            };
        }

        private static PrefabHierarchyResult GetPrefabHierarchy(string path)
        {
            string normalized = ValidatePrefabPath(path);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(normalized);
            if (prefab == null)
            {
                throw new FileNotFoundException("Prefab asset was not found: " + normalized);
            }

            List<PrefabNodeRecord> nodes = new List<PrefabNodeRecord>();
            AddPrefabNode(prefab.transform, 0, nodes);
            return new PrefabHierarchyResult
            {
                Success = true,
                AssetPath = normalized,
                Nodes = nodes.ToArray()
            };
        }

        private static void AddPrefabNode(
            Transform transform,
            int depth,
            List<PrefabNodeRecord> nodes)
        {
            nodes.Add(new PrefabNodeRecord
            {
                Name = transform.name,
                Path = GetPath(transform),
                Depth = depth,
                ActiveSelf = transform.gameObject.activeSelf,
                ComponentTypes = transform.GetComponents<Component>()
                    .Select(component => component == null
                        ? "<Missing Script>"
                        : component.GetType().FullName)
                    .ToArray()
            });
            for (int index = 0; index < transform.childCount; index++)
            {
                AddPrefabNode(transform.GetChild(index), depth + 1, nodes);
            }
        }

        private static TestListResult GetTests(string mode)
        {
            string normalizedMode = string.IsNullOrEmpty(mode) ? "all" : mode.Trim().ToLowerInvariant();
            if (normalizedMode != "all" && normalizedMode != "editmode" &&
                normalizedMode != "playmode")
            {
                throw new ArgumentException("test mode must be editmode, playmode, or all.");
            }

            List<TestRecord> tests = new List<TestRecord>();
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int assemblyIndex = 0; assemblyIndex < assemblies.Length; assemblyIndex++)
            {
                Assembly assembly = assemblies[assemblyIndex];
                string assemblyName = assembly.GetName().Name ?? string.Empty;
                string inferredMode = assemblyName.IndexOf("Editor", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "editmode"
                    : "playmode";
                if (normalizedMode != "all" && normalizedMode != inferredMode)
                {
                    continue;
                }

                Type[] types = SafeGetTypes(assembly);
                for (int typeIndex = 0; typeIndex < types.Length; typeIndex++)
                {
                    MethodInfo[] methods;
                    try
                    {
                        methods = types[typeIndex].GetMethods(
                            BindingFlags.Public | BindingFlags.NonPublic |
                            BindingFlags.Instance | BindingFlags.Static);
                    }
                    catch
                    {
                        continue;
                    }

                    for (int methodIndex = 0; methodIndex < methods.Length; methodIndex++)
                    {
                        MethodInfo method = methods[methodIndex];
                        if (!HasTestAttribute(method))
                        {
                            continue;
                        }

                        tests.Add(new TestRecord
                        {
                            Name = method.Name,
                            FullName = types[typeIndex].FullName + "." + method.Name,
                            Assembly = assemblyName,
                            Mode = inferredMode
                        });
                        if (tests.Count >= 5000)
                        {
                            break;
                        }
                    }

                    if (tests.Count >= 5000)
                    {
                        break;
                    }
                }

                if (tests.Count >= 5000)
                {
                    break;
                }
            }

            return new TestListResult
            {
                Success = true,
                Mode = normalizedMode,
                Count = tests.Count,
                Truncated = tests.Count >= 5000,
                Tests = tests.OrderBy(test => test.FullName).ToArray()
            };
        }

        private static bool HasTestAttribute(MethodInfo method)
        {
            object[] attributes;
            try
            {
                attributes = method.GetCustomAttributes(false);
            }
            catch
            {
                return false;
            }

            for (int index = 0; index < attributes.Length; index++)
            {
                string fullName = attributes[index].GetType().FullName ?? string.Empty;
                if (fullName == "NUnit.Framework.TestAttribute" ||
                    fullName == "NUnit.Framework.TestCaseAttribute" ||
                    fullName == "UnityEngine.TestTools.UnityTestAttribute")
                {
                    return true;
                }
            }

            return false;
        }

        private static Type[] SafeGetTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                return exception.Types.Where(type => type != null).ToArray();
            }
            catch
            {
                return new Type[0];
            }
        }

        private static GameObject ResolveSceneGameObject(int instanceId)
        {
            if (instanceId == 0)
            {
                throw new ArgumentException("instance_id must be non-zero.");
            }

            GameObject gameObject = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
            if (gameObject == null || EditorUtility.IsPersistent(gameObject) ||
                !gameObject.scene.IsValid() || !gameObject.scene.isLoaded)
            {
                throw new ArgumentException(
                    "Loaded scene GameObject was not found for instance_id " + instanceId + ".");
            }

            return gameObject;
        }

        private static string ValidatePrefabPath(string path)
        {
            string normalized = Require(path, "encoded_path").Replace('\\', '/').TrimStart('/');
            if (!normalized.StartsWith("Assets/", StringComparison.Ordinal) ||
                !normalized.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("../"))
            {
                throw new ArgumentException(
                    "Prefab path must be an Assets-relative .prefab path.");
            }

            return normalized;
        }

        private static string GetPath(Transform transform)
        {
            List<string> names = new List<string>();
            Transform current = transform;
            while (current != null)
            {
                names.Add(current.name);
                current = current.parent;
            }

            names.Reverse();
            return string.Join("/", names.ToArray());
        }

        private static string ReadStringProperty(object instance, Type type, string name)
        {
            PropertyInfo property = type.GetProperty(
                name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            object value = property == null ? null : property.GetValue(instance, null);
            return value == null ? string.Empty : value.ToString();
        }

        private static string ReadObjectNameProperty(object instance, Type type, string name)
        {
            PropertyInfo property = type.GetProperty(
                name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            UnityEngine.Object value = property == null
                ? null
                : property.GetValue(instance, null) as UnityEngine.Object;
            return value == null ? string.Empty : value.name;
        }

        private static string Require(string value, string field)
        {
            if (string.IsNullOrEmpty(value) || value.Trim().Length == 0)
            {
                throw new ArgumentException(field + " must be a non-empty string.");
            }

            return value.Trim();
        }

        [Serializable] private sealed class ResourceArguments
        {
            public string Kind;
            public int InstanceId;
            public string ComponentName;
            public string AssetPath;
            public string Mode;
        }

        [Serializable] private sealed class StringListResult
        {
            public bool Success;
            public string Kind;
            public string Message;
            public string[] Values;
        }

        [Serializable] private sealed class StringValueResult
        {
            public bool Success;
            public string Kind;
            public string Value;
        }

        [Serializable] private sealed class ProjectInfoResult
        {
            public bool Success;
            public string ProjectPath;
            public string AssetsPath;
            public string ProjectName;
            public string ProductName;
            public string CompanyName;
            public string UnityVersion;
            public string Platform;
            public string ActiveSceneName;
            public string ActiveScenePath;
            public bool IsPlaying;
            public bool IsCompiling;
            public string ColorSpace;
            public string RenderPipeline;
        }

        [Serializable] private sealed class InstanceListResult
        {
            public bool Success;
            public InstanceRecord[] Instances;
        }

        [Serializable] private sealed class InstanceRecord
        {
            public string Name;
            public string ProjectPath;
            public int ProcessId;
            public string UnityVersion;
            public bool IsActive;
        }

        [Serializable] private sealed class ObjectListResult
        {
            public bool Success;
            public string Kind;
            public int Count;
            public ObjectRecord[] Objects;
        }

        [Serializable] private sealed class ObjectRecord
        {
            public int InstanceId;
            public string Name;
            public string Type;
            public string AssetPath;
            public string ScenePath;

            public static ObjectRecord From(UnityEngine.Object value)
            {
                GameObject gameObject = value as GameObject;
                Component component = value as Component;
                if (gameObject == null && component != null)
                {
                    gameObject = component.gameObject;
                }

                return new ObjectRecord
                {
                    InstanceId = value == null ? 0 : value.GetInstanceID(),
                    Name = value == null ? string.Empty : value.name,
                    Type = value == null ? string.Empty : value.GetType().FullName,
                    AssetPath = value == null ? string.Empty : AssetDatabase.GetAssetPath(value),
                    ScenePath = gameObject == null || !gameObject.scene.IsValid()
                        ? string.Empty
                        : gameObject.scene.path
                };
            }
        }

        [Serializable] private sealed class WindowListResult
        {
            public bool Success;
            public int Count;
            public WindowRecord[] Windows;
        }

        [Serializable] private sealed class WindowRecord
        {
            public int InstanceId;
            public string Type;
            public string Title;
            public bool HasFocus;
            public FloatArray Position;
        }

        [Serializable] private sealed class FloatArray { public float[] Values; }

        [Serializable] private sealed class PrefabStageResult
        {
            public bool Success;
            public bool IsOpen;
            public string AssetPath;
            public string PrefabContentsRootName;
        }

        [Serializable] private sealed class RenderingStatsResult
        {
            public bool Success;
            public int DrawCalls;
            public int Batches;
            public int SetPassCalls;
            public int Triangles;
            public int Vertices;
            public int DynamicBatchedDrawCalls;
            public int StaticBatchedDrawCalls;
            public string RenderPipeline;
        }

        [Serializable] private sealed class RendererFeaturesResult
        {
            public bool Success;
            public string Pipeline;
            public string PipelineAssetPath;
            public string[] Features;
            public string Message;
        }

        [Serializable] private sealed class CameraListResult
        {
            public bool Success;
            public int Count;
            public CameraRecord[] Cameras;
        }

        [Serializable] private sealed class CameraRecord
        {
            public int InstanceId;
            public string Name;
            public string Path;
            public string Scene;
            public bool Enabled;
            public bool Orthographic;
            public float FieldOfView;
            public float Depth;
            public int CullingMask;
            public bool IsMain;
        }

        [Serializable] private sealed class VolumeListResult
        {
            public bool Success;
            public int Count;
            public VolumeRecord[] Volumes;
        }

        [Serializable] private sealed class VolumeRecord
        {
            public int InstanceId;
            public string Name;
            public string Path;
            public string ComponentType;
            public bool Enabled;
        }

        [Serializable] private sealed class ToolGroupsResult
        {
            public bool Success;
            public ToolGroupRecord[] Groups;
        }

        [Serializable] private sealed class ToolGroupRecord
        {
            public string Name;
            public bool Active;
            public bool Available;
        }

        [Serializable] private sealed class ApiDescriptionResult
        {
            public bool Success;
            public string Kind;
            public string[] Actions;
            public string[] LookupMethods;
            public string Notes;
        }

        [Serializable] private sealed class ComponentListResult
        {
            public bool Success;
            public int InstanceId;
            public string GameObjectName;
            public ComponentRecord[] Components;
        }

        [Serializable] private sealed class ComponentDetailResult
        {
            public bool Success;
            public int InstanceId;
            public string GameObjectName;
            public ComponentRecord Component;
        }

        [Serializable] private sealed class ComponentRecord
        {
            public int Index;
            public int InstanceId;
            public string Type;
            public bool Enabled;
            public SerializedPropertyRecord[] Properties;
        }

        [Serializable] private sealed class SerializedPropertyRecord
        {
            public string Path;
            public string Type;
            public string Value;
        }

        [Serializable] private sealed class PrefabInfoResult
        {
            public bool Success;
            public string AssetPath;
            public string Name;
            public string Guid;
            public string[] RootComponentTypes;
            public int ChildCount;
        }

        [Serializable] private sealed class PrefabHierarchyResult
        {
            public bool Success;
            public string AssetPath;
            public PrefabNodeRecord[] Nodes;
        }

        [Serializable] private sealed class PrefabNodeRecord
        {
            public string Name;
            public string Path;
            public int Depth;
            public bool ActiveSelf;
            public string[] ComponentTypes;
        }

        [Serializable] private sealed class TestListResult
        {
            public bool Success;
            public string Mode;
            public int Count;
            public bool Truncated;
            public TestRecord[] Tests;
        }

        [Serializable] private sealed class TestRecord
        {
            public string Name;
            public string FullName;
            public string Assembly;
            public string Mode;
        }
    }
}
#endif
