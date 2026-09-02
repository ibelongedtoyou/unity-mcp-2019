#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityMcp2019
{
    internal static class Mcp2019CoreTools
    {
        internal static string Execute(string method, string argumentsJson)
        {
            switch (method)
            {
                case "execute_menu_item":
                    return JsonUtility.ToJson(
                        ExecuteMenuItem(Parse<MenuItemArguments>(argumentsJson)));
                case "manage_editor":
                    return JsonUtility.ToJson(
                        ManageEditor(Parse<ManageEditorArguments>(argumentsJson)));
                case "manage_scene":
                    return ManageScene(Parse<ManageSceneArguments>(argumentsJson));
                default:
                    throw new ArgumentException("Unknown core tool method: " + method);
            }
        }

        private static T Parse<T>(string json) where T : class, new()
        {
            if (string.IsNullOrEmpty(json) || json == "{}")
            {
                return new T();
            }

            T value = JsonUtility.FromJson<T>(json);
            return value ?? new T();
        }

        private static ActionResult ExecuteMenuItem(MenuItemArguments arguments)
        {
            string menuPath = RequireText(arguments.MenuPath, "menu_path");
            if (string.Equals(menuPath, "File/Quit", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("File/Quit is blocked for MCP safety.");
            }

            bool executed = EditorApplication.ExecuteMenuItem(menuPath);
            if (!executed)
            {
                throw new InvalidOperationException(
                    "Unity could not find or execute menu item: " + menuPath);
            }

            return new ActionResult
            {
                Success = true,
                Action = "execute_menu_item",
                Message = "Executed Unity menu item: " + menuPath
            };
        }

        private static ActionResult ManageEditor(ManageEditorArguments arguments)
        {
            string action = Normalize(arguments.Action);
            switch (action)
            {
                case "telemetry_status":
                    return new ActionResult
                    {
                        Success = true,
                        Action = action,
                        Message = "Telemetry is not emitted by the Unity 2019 compatibility bridge."
                    };
                case "telemetry_ping":
                    return new ActionResult
                    {
                        Success = true,
                        Action = action,
                        Message = "Unity 2019 compatibility bridge is reachable."
                    };
                case "set_active_tool":
                    Tool tool;
                    if (!Enum.TryParse(RequireText(arguments.ToolName, "tool_name"), true, out tool) ||
                        !Enum.IsDefined(typeof(Tool), tool))
                    {
                        throw new ArgumentException("Unknown Unity editor tool: " + arguments.ToolName);
                    }

                    Tools.current = tool;
                    return Success(action, "Active Unity editor tool set to " + tool + ".");
                case "add_tag":
                    AddTag(RequireText(arguments.TagName, "tag_name"));
                    return Success(action, "Tag is available: " + arguments.TagName);
                case "remove_tag":
                    RemoveTag(RequireText(arguments.TagName, "tag_name"));
                    return Success(action, "Tag removed: " + arguments.TagName);
                case "add_layer":
                    AddLayer(RequireText(arguments.LayerName, "layer_name"));
                    return Success(action, "Layer is available: " + arguments.LayerName);
                case "remove_layer":
                    RemoveLayer(RequireText(arguments.LayerName, "layer_name"));
                    return Success(action, "Layer removed: " + arguments.LayerName);
                case "deploy_package":
                case "restore_package":
                    throw new InvalidOperationException(
                        action + " is not applicable because the Unity 2019 bridge is project-local, " +
                        "not an installed MCPForUnity package.");
                default:
                    throw new ArgumentException(
                        "manage_editor action is unsupported by the Unity-side compatibility router: " +
                        action);
            }
        }

        private static string ManageScene(ManageSceneArguments arguments)
        {
            EnsureSceneEditingAvailable(arguments.Action);
            string action = Normalize(arguments.Action);
            switch (action)
            {
                case "get_active":
                    return JsonUtility.ToJson(SceneSummaryResult.From(SceneManager.GetActiveScene(), action));
                case "get_loaded_scenes":
                    return JsonUtility.ToJson(GetLoadedScenes(action));
                case "get_build_settings":
                    return JsonUtility.ToJson(GetBuildSettings(action));
                case "get_hierarchy":
                    return JsonUtility.ToJson(GetHierarchy(arguments));
                case "scene_view_frame":
                    return JsonUtility.ToJson(FrameSceneView(arguments));
                case "create":
                    return JsonUtility.ToJson(CreateScene(arguments));
                case "load":
                    return JsonUtility.ToJson(LoadScene(arguments));
                case "save":
                    return JsonUtility.ToJson(SaveScene(arguments));
                case "close_scene":
                    return JsonUtility.ToJson(CloseScene(arguments));
                case "set_active_scene":
                    return JsonUtility.ToJson(SetActiveScene(arguments));
                case "move_to_scene":
                    return JsonUtility.ToJson(MoveToScene(arguments));
                case "validate":
                    return JsonUtility.ToJson(ValidateScene(arguments));
                default:
                    throw new ArgumentException("Unsupported manage_scene action: " + action);
            }
        }

        private static SceneSummaryResult CreateScene(ManageSceneArguments arguments)
        {
            string template = string.IsNullOrEmpty(arguments.Template)
                ? "empty"
                : arguments.Template.Trim().ToLowerInvariant();
            if (template != "empty" && template != "default" &&
                template != "3d_basic" && template != "2d_basic")
            {
                throw new ArgumentException("template must be empty, default, 3d_basic, or 2d_basic.");
            }

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (template != "empty")
            {
                GameObject cameraObject = new GameObject("Main Camera");
                Undo.RegisterCreatedObjectUndo(cameraObject, "MCP Create Scene Camera");
                Camera camera = Undo.AddComponent<Camera>(cameraObject);
                cameraObject.tag = "MainCamera";
                cameraObject.transform.position = template == "2d_basic"
                    ? new Vector3(0f, 0f, -10f)
                    : new Vector3(0f, 1f, -10f);
                camera.orthographic = template == "2d_basic";

                if (template != "2d_basic")
                {
                    GameObject lightObject = new GameObject("Directional Light");
                    Undo.RegisterCreatedObjectUndo(lightObject, "MCP Create Scene Light");
                    Undo.AddComponent<Light>(lightObject).type = LightType.Directional;
                    lightObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
                }
            }

            string path = ResolveSceneAssetPath(arguments.Path, arguments.Name, false);
            if (!string.IsNullOrEmpty(path))
            {
                EnsureAssetFolderExists(Path.GetDirectoryName(path));
                if (!EditorSceneManager.SaveScene(scene, path))
                {
                    throw new InvalidOperationException("Unity failed to save the new scene: " + path);
                }
            }

            return SceneSummaryResult.From(scene, "create");
        }

        private static SceneSummaryResult LoadScene(ManageSceneArguments arguments)
        {
            string path = ResolveSceneAssetPath(arguments.Path, arguments.Name, true);
            OpenSceneMode mode = arguments.Additive ? OpenSceneMode.Additive : OpenSceneMode.Single;
            Scene scene = EditorSceneManager.OpenScene(path, mode);
            return SceneSummaryResult.From(scene, "load");
        }

        private static SceneSummaryResult SaveScene(ManageSceneArguments arguments)
        {
            Scene scene = ResolveScene(arguments);
            string path = string.IsNullOrEmpty(arguments.Path)
                ? scene.path
                : ResolveSceneAssetPath(arguments.Path, arguments.Name, false);
            if (string.IsNullOrEmpty(path))
            {
                throw new InvalidOperationException(
                    "The scene has no asset path; provide path when saving a new scene.");
            }

            EnsureAssetFolderExists(Path.GetDirectoryName(path));
            if (!EditorSceneManager.SaveScene(scene, path))
            {
                throw new InvalidOperationException("Unity failed to save scene: " + path);
            }

            return SceneSummaryResult.From(scene, "save");
        }

        private static SceneSummaryResult CloseScene(ManageSceneArguments arguments)
        {
            Scene scene = ResolveScene(arguments);
            if (SceneManager.sceneCount <= 1)
            {
                throw new InvalidOperationException("Cannot close the only loaded scene.");
            }

            SceneSummaryResult result = SceneSummaryResult.From(scene, "close_scene");
            if (!EditorSceneManager.CloseScene(scene, arguments.RemoveScene))
            {
                throw new InvalidOperationException("Unity failed to close scene: " + scene.name);
            }

            result.Message = arguments.RemoveScene ? "Scene removed." : "Scene unloaded.";
            return result;
        }

        private static SceneSummaryResult SetActiveScene(ManageSceneArguments arguments)
        {
            Scene scene = ResolveScene(arguments);
            if (!SceneManager.SetActiveScene(scene))
            {
                throw new InvalidOperationException("Unity failed to activate scene: " + scene.name);
            }

            return SceneSummaryResult.From(scene, "set_active_scene");
        }

        private static ActionResult MoveToScene(ManageSceneArguments arguments)
        {
            Scene scene = ResolveScene(arguments);
            GameObject target = ResolveGameObject(arguments.TargetInstanceId, arguments.Target);
            Undo.SetTransformParent(target.transform, null, "MCP Move GameObject To Scene");
            SceneManager.MoveGameObjectToScene(target, scene);
            EditorSceneManager.MarkSceneDirty(scene);
            return Success("move_to_scene", "Moved " + target.name + " to scene " + scene.name + ".");
        }

        private static ActionResult FrameSceneView(ManageSceneArguments arguments)
        {
            GameObject target = ResolveGameObject(
                arguments.SceneViewTargetInstanceId,
                arguments.SceneViewTarget);
            Selection.activeGameObject = target;
            SceneView sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null)
            {
                throw new InvalidOperationException("No Scene View window is currently available.");
            }

            sceneView.FrameSelected();
            return Success("scene_view_frame", "Framed " + target.name + " in Scene View.");
        }

        private static SceneListResult GetLoadedScenes(string action)
        {
            SceneInfo[] scenes = new SceneInfo[SceneManager.sceneCount];
            for (int index = 0; index < SceneManager.sceneCount; index++)
            {
                scenes[index] = SceneInfo.From(SceneManager.GetSceneAt(index));
            }

            return new SceneListResult { Success = true, Action = action, Scenes = scenes };
        }

        private static BuildSettingsResult GetBuildSettings(string action)
        {
            EditorBuildSettingsScene[] configured = EditorBuildSettings.scenes;
            BuildSceneInfo[] scenes = new BuildSceneInfo[configured.Length];
            for (int index = 0; index < configured.Length; index++)
            {
                scenes[index] = new BuildSceneInfo
                {
                    Path = configured[index].path,
                    Enabled = configured[index].enabled,
                    BuildIndex = index
                };
            }

            return new BuildSettingsResult { Success = true, Action = action, Scenes = scenes };
        }

        private static HierarchyResult GetHierarchy(ManageSceneArguments arguments)
        {
            Scene scene = ResolveScene(arguments);
            int offset = Math.Max(0, arguments.Cursor);
            int pageSize = Math.Max(1, Math.Min(arguments.PageSize <= 0 ? 100 : arguments.PageSize, 500));
            int maxDepth = Math.Max(0, Math.Min(arguments.MaxDepth <= 0 ? 10 : arguments.MaxDepth, 20));
            List<HierarchyNode> all = new List<HierarchyNode>();
            GameObject[] roots = scene.GetRootGameObjects();
            for (int index = 0; index < roots.Length; index++)
            {
                AddHierarchyNode(roots[index].transform, 0, maxDepth, all);
            }

            int count = offset >= all.Count ? 0 : Math.Min(pageSize, all.Count - offset);
            HierarchyNode[] page = count == 0 ? new HierarchyNode[0] : all.GetRange(offset, count).ToArray();
            bool hasMore = offset + count < all.Count;
            return new HierarchyResult
            {
                Success = true,
                Action = "get_hierarchy",
                Scene = SceneInfo.From(scene),
                Nodes = page,
                Total = all.Count,
                Cursor = offset.ToString(),
                NextCursor = hasMore ? (offset + count).ToString() : string.Empty,
                HasMore = hasMore
            };
        }

        private static void AddHierarchyNode(
            Transform transform,
            int depth,
            int maxDepth,
            List<HierarchyNode> nodes)
        {
            nodes.Add(new HierarchyNode
            {
                InstanceId = transform.gameObject.GetInstanceID(),
                Name = transform.name,
                Path = GetPath(transform),
                Depth = depth,
                Active = transform.gameObject.activeInHierarchy
            });
            if (depth >= maxDepth)
            {
                return;
            }

            for (int index = 0; index < transform.childCount; index++)
            {
                AddHierarchyNode(transform.GetChild(index), depth + 1, maxDepth, nodes);
            }
        }

        private static ValidationResult ValidateScene(ManageSceneArguments arguments)
        {
            Scene scene = ResolveScene(arguments);
            int missingScripts = 0;
            int repaired = 0;
            GameObject[] roots = scene.GetRootGameObjects();
            for (int index = 0; index < roots.Length; index++)
            {
                Transform[] transforms = roots[index].GetComponentsInChildren<Transform>(true);
                for (int childIndex = 0; childIndex < transforms.Length; childIndex++)
                {
                    GameObject gameObject = transforms[childIndex].gameObject;
                    int missing = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(gameObject);
                    missingScripts += missing;
                    if (arguments.AutoRepair && missing > 0)
                    {
                        Undo.RegisterCompleteObjectUndo(gameObject, "MCP Remove Missing Scripts");
                        GameObjectUtility.RemoveMonoBehavioursWithMissingScript(gameObject);
                        repaired += missing;
                    }
                }
            }

            if (repaired > 0)
            {
                EditorSceneManager.MarkSceneDirty(scene);
            }

            return new ValidationResult
            {
                Success = missingScripts == 0 || repaired == missingScripts,
                Action = "validate",
                MissingScripts = missingScripts,
                Repaired = repaired,
                Message = missingScripts == 0
                    ? "No missing MonoBehaviour scripts were found."
                    : "Scene validation found missing MonoBehaviour scripts."
            };
        }

        private static Scene ResolveScene(ManageSceneArguments arguments)
        {
            if (arguments.BuildIndex >= 0)
            {
                Scene indexed = SceneManager.GetSceneByBuildIndex(arguments.BuildIndex);
                if (indexed.IsValid() && indexed.isLoaded)
                {
                    return indexed;
                }
            }

            string path = FirstText(arguments.ScenePath, arguments.Path);
            if (!string.IsNullOrEmpty(path))
            {
                string normalized = NormalizeScenePath(path);
                Scene byPath = SceneManager.GetSceneByPath(normalized);
                if (byPath.IsValid() && byPath.isLoaded)
                {
                    return byPath;
                }
            }

            string name = FirstText(arguments.SceneName, arguments.Name);
            if (!string.IsNullOrEmpty(name))
            {
                Scene byName = SceneManager.GetSceneByName(name);
                if (byName.IsValid() && byName.isLoaded)
                {
                    return byName;
                }
            }

            if (string.IsNullOrEmpty(path) && string.IsNullOrEmpty(name) && arguments.BuildIndex < 0)
            {
                Scene active = SceneManager.GetActiveScene();
                if (active.IsValid() && active.isLoaded)
                {
                    return active;
                }
            }

            throw new ArgumentException("A matching loaded scene was not found.");
        }

        private static GameObject ResolveGameObject(int instanceId, string target)
        {
            if (instanceId != 0)
            {
                GameObject byId = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
                if (IsLoadedSceneObject(byId))
                {
                    return byId;
                }
            }

            string query = RequireText(target, "target").Trim('/');
            GameObject[] candidates = Resources.FindObjectsOfTypeAll<GameObject>();
            for (int index = 0; index < candidates.Length; index++)
            {
                GameObject candidate = candidates[index];
                if (!IsLoadedSceneObject(candidate))
                {
                    continue;
                }

                if (string.Equals(candidate.name, query, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(GetPath(candidate.transform), query, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }

            throw new ArgumentException("A matching loaded scene GameObject was not found: " + query);
        }

        private static bool IsLoadedSceneObject(GameObject gameObject)
        {
            return gameObject != null && !EditorUtility.IsPersistent(gameObject) &&
                   gameObject.scene.IsValid() && gameObject.scene.isLoaded;
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

        private static string ResolveSceneAssetPath(string path, string name, bool mustExist)
        {
            string candidate = FirstText(path, name);
            if (string.IsNullOrEmpty(candidate))
            {
                if (mustExist)
                {
                    throw new ArgumentException("path or name is required.");
                }

                return string.Empty;
            }

            candidate = NormalizeScenePath(candidate);
            if (!candidate.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                candidate += ".unity";
            }

            if (!candidate.StartsWith("Assets/", StringComparison.Ordinal))
            {
                candidate = "Assets/" + candidate.TrimStart('/');
            }

            if (candidate.Contains("../") || candidate.Contains("\\"))
            {
                throw new ArgumentException("Scene path must stay under Assets and use forward slashes.");
            }

            if (mustExist && AssetDatabase.LoadAssetAtPath<SceneAsset>(candidate) == null)
            {
                throw new FileNotFoundException("Scene asset was not found: " + candidate);
            }

            return candidate;
        }

        private static string NormalizeScenePath(string value)
        {
            return value.Trim().Replace('\\', '/');
        }

        private static void EnsureAssetFolderExists(string folder)
        {
            if (string.IsNullOrEmpty(folder) || folder == "Assets" || AssetDatabase.IsValidFolder(folder))
            {
                return;
            }

            string parent = Path.GetDirectoryName(folder);
            if (string.IsNullOrEmpty(parent))
            {
                parent = "Assets";
            }

            parent = parent.Replace('\\', '/');
            EnsureAssetFolderExists(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }

        private static void AddTag(string tag)
        {
            if (Array.IndexOf(InternalEditorUtility.tags, tag) >= 0)
            {
                return;
            }

            SerializedObject manager = LoadTagManager();
            SerializedProperty tags = manager.FindProperty("tags");
            tags.InsertArrayElementAtIndex(tags.arraySize);
            tags.GetArrayElementAtIndex(tags.arraySize - 1).stringValue = tag;
            manager.ApplyModifiedProperties();
        }

        private static void RemoveTag(string tag)
        {
            SerializedObject manager = LoadTagManager();
            SerializedProperty tags = manager.FindProperty("tags");
            for (int index = 0; index < tags.arraySize; index++)
            {
                if (tags.GetArrayElementAtIndex(index).stringValue == tag)
                {
                    tags.DeleteArrayElementAtIndex(index);
                    manager.ApplyModifiedProperties();
                    return;
                }
            }

            throw new ArgumentException("Tag was not found: " + tag);
        }

        private static void AddLayer(string layer)
        {
            if (LayerMask.NameToLayer(layer) >= 0)
            {
                return;
            }

            SerializedObject manager = LoadTagManager();
            SerializedProperty layers = manager.FindProperty("layers");
            for (int index = 8; index < layers.arraySize; index++)
            {
                SerializedProperty slot = layers.GetArrayElementAtIndex(index);
                if (string.IsNullOrEmpty(slot.stringValue))
                {
                    slot.stringValue = layer;
                    manager.ApplyModifiedProperties();
                    return;
                }
            }

            throw new InvalidOperationException("No empty user layer slots are available.");
        }

        private static void RemoveLayer(string layer)
        {
            int layerIndex = LayerMask.NameToLayer(layer);
            if (layerIndex < 8)
            {
                throw new ArgumentException("Built-in layers cannot be removed: " + layer);
            }

            SerializedObject manager = LoadTagManager();
            SerializedProperty layers = manager.FindProperty("layers");
            if (layerIndex < 0 || layerIndex >= layers.arraySize)
            {
                throw new ArgumentException("Layer was not found: " + layer);
            }

            layers.GetArrayElementAtIndex(layerIndex).stringValue = string.Empty;
            manager.ApplyModifiedProperties();
        }

        private static SerializedObject LoadTagManager()
        {
            UnityEngine.Object[] assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
            if (assets == null || assets.Length == 0)
            {
                throw new InvalidOperationException("Unity TagManager.asset could not be loaded.");
            }

            return new SerializedObject(assets[0]);
        }

        private static void EnsureSceneEditingAvailable(string action)
        {
            string normalized = Normalize(action);
            bool readOnly = normalized == "get_active" || normalized == "get_loaded_scenes" ||
                            normalized == "get_build_settings" || normalized == "get_hierarchy";
            if (!readOnly && (EditorApplication.isPlayingOrWillChangePlaymode ||
                              EditorApplication.isCompiling || EditorApplication.isUpdating))
            {
                throw new InvalidOperationException(
                    "Scene mutation is unavailable while playing, compiling, or updating assets.");
            }
        }

        private static ActionResult Success(string action, string message)
        {
            return new ActionResult { Success = true, Action = action, Message = message };
        }

        private static string Normalize(string value)
        {
            return RequireText(value, "action").Trim().ToLowerInvariant();
        }

        private static string RequireText(string value, string field)
        {
            if (string.IsNullOrEmpty(value) || value.Trim().Length == 0)
            {
                throw new ArgumentException(field + " must be a non-empty string.");
            }

            return value.Trim();
        }

        private static string FirstText(string first, string second)
        {
            if (!string.IsNullOrEmpty(first) && first.Trim().Length > 0)
            {
                return first.Trim();
            }

            return string.IsNullOrEmpty(second) ? string.Empty : second.Trim();
        }

        [Serializable]
        private sealed class MenuItemArguments
        {
            public string MenuPath;
        }

        [Serializable]
        private sealed class ManageEditorArguments
        {
            public string Action;
            public string ToolName;
            public string TagName;
            public string LayerName;
        }

        [Serializable]
        private sealed class ManageSceneArguments
        {
            public string Action;
            public string Name;
            public string Path;
            public int BuildIndex = -1;
            public string SceneViewTarget;
            public int SceneViewTargetInstanceId;
            public int PageSize;
            public int Cursor;
            public int MaxDepth;
            public string SceneName;
            public string ScenePath;
            public string Target;
            public int TargetInstanceId;
            public bool RemoveScene;
            public bool Additive;
            public string Template;
            public bool AutoRepair;
        }

        [Serializable]
        private sealed class ActionResult
        {
            public bool Success;
            public string Action;
            public string Message;
        }

        [Serializable]
        private sealed class SceneSummaryResult
        {
            public bool Success;
            public string Action;
            public string Message;
            public SceneInfo Scene;

            public static SceneSummaryResult From(Scene scene, string action)
            {
                return new SceneSummaryResult
                {
                    Success = scene.IsValid(),
                    Action = action,
                    Scene = SceneInfo.From(scene)
                };
            }
        }

        [Serializable]
        private sealed class SceneInfo
        {
            public string Name;
            public string Path;
            public int BuildIndex;
            public int RootCount;
            public bool IsLoaded;
            public bool IsDirty;
            public bool IsActive;

            public static SceneInfo From(Scene scene)
            {
                return new SceneInfo
                {
                    Name = scene.IsValid() ? scene.name : string.Empty,
                    Path = scene.IsValid() ? scene.path : string.Empty,
                    BuildIndex = scene.IsValid() ? scene.buildIndex : -1,
                    RootCount = scene.IsValid() && scene.isLoaded ? scene.rootCount : 0,
                    IsLoaded = scene.IsValid() && scene.isLoaded,
                    IsDirty = scene.IsValid() && scene.isDirty,
                    IsActive = scene.IsValid() && scene == SceneManager.GetActiveScene()
                };
            }
        }

        [Serializable]
        private sealed class SceneListResult
        {
            public bool Success;
            public string Action;
            public SceneInfo[] Scenes;
        }

        [Serializable]
        private sealed class BuildSettingsResult
        {
            public bool Success;
            public string Action;
            public BuildSceneInfo[] Scenes;
        }

        [Serializable]
        private sealed class BuildSceneInfo
        {
            public string Path;
            public bool Enabled;
            public int BuildIndex;
        }

        [Serializable]
        private sealed class HierarchyResult
        {
            public bool Success;
            public string Action;
            public SceneInfo Scene;
            public HierarchyNode[] Nodes;
            public int Total;
            public string Cursor;
            public string NextCursor;
            public bool HasMore;
        }

        [Serializable]
        private sealed class HierarchyNode
        {
            public int InstanceId;
            public string Name;
            public string Path;
            public int Depth;
            public bool Active;
        }

        [Serializable]
        private sealed class ValidationResult
        {
            public bool Success;
            public string Action;
            public int MissingScripts;
            public int Repaired;
            public string Message;
        }
    }
}
#endif
