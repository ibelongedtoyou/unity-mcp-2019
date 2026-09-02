#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityMcp2019
{
    internal static class Mcp2019ToolRouter
    {
        private const int ConsoleCapacity = 500;
        private const string ImplementationVersion = "0.3.0";
        private static readonly object ConsoleLock = new object();
        private static readonly List<ConsoleRecord> ConsoleRecords = new List<ConsoleRecord>();

        static Mcp2019ToolRouter()
        {
            Application.logMessageReceivedThreaded += CaptureLog;
        }

        internal static void EnsureInitialized()
        {
        }

        internal static string Execute(string method, string argumentsJson)
        {
            switch (method)
            {
                case "ping":
                    return JsonUtility.ToJson(Ping());
                case "editor_state":
                    return JsonUtility.ToJson(GetEditorState());
                case "read_console":
                    return JsonUtility.ToJson(ReadConsole(Parse<ReadConsoleArguments>(argumentsJson)));
                case "clear_console":
                    return JsonUtility.ToJson(ClearConsole());
                case "get_hierarchy":
                    return JsonUtility.ToJson(GetHierarchy(Parse<HierarchyArguments>(argumentsJson)));
                case "find_gameobjects":
                    return JsonUtility.ToJson(FindGameObjects(Parse<FindArguments>(argumentsJson)));
                case "get_gameobject":
                case "manage_gameobject":
                case "manage_component":
                    return Mcp2019SceneTools.Execute(method, argumentsJson);
                case "execute_menu_item":
                case "manage_editor":
                case "manage_scene":
                    return Mcp2019CoreTools.Execute(method, argumentsJson);
                case "unity_reflect":
                    return Mcp2019ReflectionTools.Execute(argumentsJson);
                case "resource_snapshot":
                    return Mcp2019ResourceTools.Execute(argumentsJson);
                case "execute_code":
                case "execute_custom_tool":
                    return Mcp2019ExecutionTools.Execute(method, argumentsJson);
                case "validate_script":
                    return Mcp2019ExecutionTools.ValidateScriptJson(argumentsJson);
                case "manage_asset":
                case "manage_material":
                    return Mcp2019AssetTools.Execute(method, argumentsJson);
                case "manage_texture":
                    return Mcp2019TextureTools.Execute(argumentsJson);
                case "manage_prefabs":
                    return Mcp2019PrefabTools.Execute(argumentsJson);
                case "manage_animation":
                    return Mcp2019AnimationTools.Execute(argumentsJson);
                case "manage_packages":
                    return Mcp2019PackageTools.Execute(argumentsJson);
                case "manage_build":
                    return Mcp2019BuildTools.Execute(argumentsJson);
                case "manage_camera":
                    return Mcp2019CameraTools.Execute(argumentsJson);
                case "manage_physics":
                    return Mcp2019PhysicsTools.Execute(argumentsJson);
                case "manage_graphics":
                    return Mcp2019GraphicsTools.Execute(argumentsJson);
                case "manage_profiler":
                    return Mcp2019ProfilerTools.Execute(argumentsJson);
                case "manage_ui":
                    return Mcp2019UiTools.Execute(argumentsJson);
                case "manage_vfx":
                    return Mcp2019VfxTools.Execute(argumentsJson);
                case "manage_probuilder":
                    return Mcp2019ProBuilderTools.Execute(argumentsJson);
                case "import_generated_asset":
                    return Mcp2019AssetGenerationTools.Execute(argumentsJson);
                case "run_tests":
                case "get_test_job":
                    return Mcp2019TestTools.Execute(method, argumentsJson);
                case "refresh_assets":
                    return JsonUtility.ToJson(
                        ScheduleAssetRefresh(Parse<RefreshArguments>(argumentsJson)));
                case "play_mode":
                    return JsonUtility.ToJson(SchedulePlayMode(Parse<PlayModeArguments>(argumentsJson)));
                case "undo_redo":
                    return JsonUtility.ToJson(PerformUndoRedo(Parse<UndoRedoArguments>(argumentsJson)));
                case "reload_active_scene":
                    return JsonUtility.ToJson(
                        ScheduleReloadActiveScene(Parse<ReloadSceneArguments>(argumentsJson)));
                default:
                    throw new ArgumentException("Unknown or disallowed method: " + method);
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

        private static PingResult Ping()
        {
            return new PingResult
            {
                Ok = true,
                BridgeVersion = ImplementationVersion,
                UnityVersion = Application.unityVersion,
                ProjectPath = Directory.GetParent(Application.dataPath).FullName,
                Utc = DateTime.UtcNow.ToString("o")
            };
        }

        private static EditorStateResult GetEditorState()
        {
            Scene scene = SceneManager.GetActiveScene();
            return new EditorStateResult
            {
                UnityVersion = Application.unityVersion,
                IsPlaying = EditorApplication.isPlaying,
                IsPaused = EditorApplication.isPaused,
                IsCompiling = EditorApplication.isCompiling,
                IsUpdating = EditorApplication.isUpdating,
                ReadyForTools = !EditorApplication.isCompiling && !EditorApplication.isUpdating,
                ActiveSceneName = scene.IsValid() ? scene.name : string.Empty,
                ActiveScenePath = scene.IsValid() ? scene.path : string.Empty,
                ActiveSceneIsDirty = scene.IsValid() && scene.isDirty,
                ActiveSceneRootCount = scene.IsValid() ? scene.rootCount : 0
            };
        }

        private static ReadConsoleResult ReadConsole(ReadConsoleArguments arguments)
        {
            int limit = Clamp(arguments.Limit <= 0 ? 50 : arguments.Limit, 1, 200);
            int offset = Math.Max(0, arguments.Offset);
            HashSet<string> acceptedTypes = null;

            if (arguments.Types != null && arguments.Types.Length > 0)
            {
                acceptedTypes = new HashSet<string>(arguments.Types, StringComparer.OrdinalIgnoreCase);
            }

            List<ConsoleRecord> matches = new List<ConsoleRecord>();
            lock (ConsoleLock)
            {
                for (int index = ConsoleRecords.Count - 1; index >= 0; index--)
                {
                    ConsoleRecord record = ConsoleRecords[index];
                    if (acceptedTypes != null && !acceptedTypes.Contains(record.Type))
                    {
                        continue;
                    }

                    if (!string.IsNullOrEmpty(arguments.FilterText) &&
                        record.Message.IndexOf(
                            arguments.FilterText,
                            StringComparison.OrdinalIgnoreCase) < 0 &&
                        record.StackTrace.IndexOf(
                            arguments.FilterText,
                            StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    matches.Add(new ConsoleRecord
                    {
                        Utc = record.Utc,
                        Type = record.Type,
                        Message = record.Message,
                        StackTrace = arguments.IncludeStackTrace
                            ? record.StackTrace
                            : string.Empty
                    });
                }
            }

            int totalMatches = matches.Count;
            List<ConsoleRecord> selected = new List<ConsoleRecord>();
            if (offset < matches.Count)
            {
                int count = Math.Min(limit, matches.Count - offset);
                for (int index = offset; index < offset + count; index++)
                {
                    selected.Add(matches[index]);
                }
            }

            bool hasMore = offset + selected.Count < totalMatches;
            return new ReadConsoleResult
            {
                Records = selected.ToArray(),
                TotalMatches = totalMatches,
                Cursor = offset.ToString(),
                NextCursor = hasMore ? (offset + selected.Count).ToString() : string.Empty,
                HasMore = hasMore,
                Format = string.IsNullOrEmpty(arguments.Format) ? "plain" : arguments.Format,
                Note = "Captures messages emitted after the MCP 2019 bridge was loaded."
            };
        }

        private static ClearConsoleResult ClearConsole()
        {
            int count;
            lock (ConsoleLock)
            {
                count = ConsoleRecords.Count;
                ConsoleRecords.Clear();
            }

            return new ClearConsoleResult
            {
                Success = true,
                ClearedCount = count,
                Message = "Captured MCP 2019 console records were cleared."
            };
        }

        private static bool MatchesSearch(
            GameObject candidate,
            string query,
            string searchMethod)
        {
            switch (searchMethod)
            {
                case "by_name":
                    return candidate.name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                case "by_tag":
                    return string.Equals(SafeGetTag(candidate), query, StringComparison.OrdinalIgnoreCase);
                case "by_layer":
                    int requestedLayer;
                    if (!int.TryParse(query, out requestedLayer))
                    {
                        requestedLayer = LayerMask.NameToLayer(query);
                    }

                    return requestedLayer >= 0 && candidate.layer == requestedLayer;
                case "by_component":
                    Component[] components = candidate.GetComponents<Component>();
                    for (int index = 0; index < components.Length; index++)
                    {
                        Component component = components[index];
                        if (component == null)
                        {
                            continue;
                        }

                        Type type = component.GetType();
                        if (string.Equals(type.Name, query, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(type.FullName, query, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }

                    return false;
                case "by_path":
                    return string.Equals(
                        GetPath(candidate.transform),
                        query.Trim('/'),
                        StringComparison.OrdinalIgnoreCase);
                case "by_id":
                    int instanceId;
                    return int.TryParse(query, out instanceId) &&
                           candidate.GetInstanceID() == instanceId;
                default:
                    throw new ArgumentException("Unsupported search_method: " + searchMethod);
            }
        }

        private static HierarchyResult GetHierarchy(HierarchyArguments arguments)
        {
            int maxDepth = Clamp(arguments.MaxDepth, 0, 10);
            int pageSize = Clamp(arguments.PageSize <= 0 ? 200 : arguments.PageSize, 1, 500);
            int offset = Math.Max(0, arguments.Offset);
            Scene scene = SceneManager.GetActiveScene();
            HierarchyResult result = new HierarchyResult
            {
                SceneName = scene.IsValid() ? scene.name : string.Empty,
                ScenePath = scene.IsValid() ? scene.path : string.Empty,
                MaxDepth = maxDepth,
                PageSize = pageSize,
                Cursor = offset.ToString(),
                Objects = new List<GameObjectRecord>()
            };

            if (!scene.IsValid() || !scene.isLoaded)
            {
                return result;
            }

            GameObject[] roots = scene.GetRootGameObjects();
            result.RootCount = roots.Length;

            for (int index = 0; index < roots.Length; index++)
            {
                AddHierarchy(roots[index].transform, 0, maxDepth, offset, pageSize, result);
                if (result.HasMore)
                {
                    break;
                }
            }

            result.NextCursor = result.HasMore
                ? (offset + result.Objects.Count).ToString()
                : string.Empty;

            return result;
        }

        private static void AddHierarchy(
            Transform transform,
            int depth,
            int maxDepth,
            int offset,
            int pageSize,
            HierarchyResult result)
        {
            if (result.VisitedCount >= offset)
            {
                if (result.Objects.Count >= pageSize)
                {
                    result.HasMore = true;
                    return;
                }

                result.Objects.Add(CreateRecord(transform.gameObject, depth));
            }

            result.VisitedCount++;

            if (depth >= maxDepth)
            {
                return;
            }

            for (int index = 0; index < transform.childCount; index++)
            {
                AddHierarchy(
                    transform.GetChild(index),
                    depth + 1,
                    maxDepth,
                    offset,
                    pageSize,
                    result);
                if (result.HasMore)
                {
                    return;
                }
            }
        }

        private static FindResult FindGameObjects(FindArguments arguments)
        {
            string query = arguments.Query == null ? string.Empty : arguments.Query.Trim();
            if (query.Length == 0)
            {
                throw new ArgumentException("query must not be empty.");
            }

            int pageSize = Clamp(arguments.PageSize <= 0 ? 100 : arguments.PageSize, 1, 500);
            int offset = Math.Max(0, arguments.Offset);
            string searchMethod = string.IsNullOrEmpty(arguments.SearchMethod)
                ? "by_name"
                : arguments.SearchMethod.Trim().ToLowerInvariant();
            GameObject[] candidates = Resources.FindObjectsOfTypeAll<GameObject>();
            List<GameObjectRecord> matches = new List<GameObjectRecord>();

            for (int index = 0; index < candidates.Length; index++)
            {
                GameObject candidate = candidates[index];
                if (candidate == null || EditorUtility.IsPersistent(candidate) ||
                    !candidate.scene.IsValid() || !candidate.scene.isLoaded)
                {
                    continue;
                }

                if (!arguments.IncludeInactive && !candidate.activeInHierarchy)
                {
                    continue;
                }

                if (!MatchesSearch(candidate, query, searchMethod))
                {
                    continue;
                }

                matches.Add(CreateRecord(candidate, GetDepth(candidate.transform)));
            }

            matches.Sort(delegate(GameObjectRecord left, GameObjectRecord right)
            {
                int pathComparison = string.Compare(left.Path, right.Path, StringComparison.Ordinal);
                return pathComparison != 0
                    ? pathComparison
                    : left.InstanceId.CompareTo(right.InstanceId);
            });

            int totalMatches = matches.Count;
            if (offset >= matches.Count)
            {
                matches.Clear();
            }
            else
            {
                int count = Math.Min(pageSize, matches.Count - offset);
                matches = matches.GetRange(offset, count);
            }

            bool hasMore = offset + matches.Count < totalMatches;

            return new FindResult
            {
                Query = query,
                SearchMethod = searchMethod,
                IncludeInactive = arguments.IncludeInactive,
                Cursor = offset.ToString(),
                NextCursor = hasMore ? (offset + matches.Count).ToString() : string.Empty,
                PageSize = pageSize,
                TotalMatches = totalMatches,
                HasMore = hasMore,
                Objects = matches.ToArray()
            };
        }

        private static ScheduledResult ScheduleAssetRefresh(RefreshArguments arguments)
        {
            EditorApplication.delayCall += delegate
            {
                AssetDatabase.Refresh();
                if (string.Equals(arguments.Compile, "request", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arguments.Compile, "force", StringComparison.OrdinalIgnoreCase))
                {
                    CompilationPipeline.RequestScriptCompilation();
                }
            };

            return new ScheduledResult
            {
                Scheduled = true,
                Action = "refresh_assets",
                Message = "AssetDatabase.Refresh was scheduled for the next editor update."
            };
        }

        private static ScheduledResult SchedulePlayMode(PlayModeArguments arguments)
        {
            string action = arguments.Action == null
                ? string.Empty
                : arguments.Action.Trim().ToLowerInvariant();

            switch (action)
            {
                case "play":
                    EditorApplication.delayCall += delegate { EditorApplication.isPlaying = true; };
                    break;
                case "stop":
                    EditorApplication.delayCall += delegate { EditorApplication.isPlaying = false; };
                    break;
                case "pause":
                    if (!EditorApplication.isPlaying)
                    {
                        throw new InvalidOperationException("Cannot pause while the Editor is not playing.");
                    }

                    EditorApplication.delayCall += delegate { EditorApplication.isPaused = true; };
                    break;
                case "resume":
                    if (!EditorApplication.isPlaying)
                    {
                        throw new InvalidOperationException("Cannot resume while the Editor is not playing.");
                    }

                    EditorApplication.delayCall += delegate { EditorApplication.isPaused = false; };
                    break;
                default:
                    throw new ArgumentException("action must be play, stop, pause, or resume.");
            }

            return new ScheduledResult
            {
                Scheduled = true,
                Action = action,
                Message = "Play mode action was scheduled for the next editor update."
            };
        }

        private static UndoRedoResult PerformUndoRedo(UndoRedoArguments arguments)
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating ||
                EditorApplication.isPlayingOrWillChangePlaymode)
            {
                throw new InvalidOperationException(
                    "Undo/redo is unavailable while compiling, updating assets, or in Play Mode.");
            }

            string action = arguments.Action == null
                ? string.Empty
                : arguments.Action.Trim().ToLowerInvariant();
            if (action == "undo")
            {
                Undo.PerformUndo();
            }
            else if (action == "redo")
            {
                Undo.PerformRedo();
            }
            else
            {
                throw new ArgumentException("action must be undo or redo.");
            }

            Scene scene = SceneManager.GetActiveScene();
            return new UndoRedoResult
            {
                Ok = true,
                Action = action,
                ActiveSceneIsDirty = scene.IsValid() && scene.isDirty
            };
        }

        private static ScheduledResult ScheduleReloadActiveScene(ReloadSceneArguments arguments)
        {
            if (!arguments.ConfirmDiscardChanges)
            {
                throw new ArgumentException(
                    "reload_active_scene requires confirm_discard_changes=true.");
            }

            if (EditorApplication.isCompiling || EditorApplication.isUpdating ||
                EditorApplication.isPlayingOrWillChangePlaymode)
            {
                throw new InvalidOperationException(
                    "Scene reload is unavailable while compiling, updating assets, or in Play Mode.");
            }

            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || string.IsNullOrEmpty(scene.path))
            {
                throw new InvalidOperationException("The active scene has no saved asset path.");
            }

            string path = scene.path;
            EditorApplication.delayCall += delegate
            {
                Scene current = SceneManager.GetActiveScene();
                if (!current.IsValid() || current.path != path)
                {
                    UnityEngine.Debug.LogWarning(
                        "[MCP 2019] Skipped scene reload because the active scene changed.");
                    return;
                }

                EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            };

            return new ScheduledResult
            {
                Scheduled = true,
                Action = "reload_active_scene",
                Message = "Active scene reload was scheduled; unsaved changes will be discarded."
            };
        }

        private static GameObjectRecord CreateRecord(GameObject gameObject, int depth)
        {
            Component[] components = gameObject.GetComponents<Component>();
            string[] componentNames = new string[components.Length];

            for (int index = 0; index < components.Length; index++)
            {
                componentNames[index] = components[index] == null
                    ? "<Missing Script>"
                    : components[index].GetType().FullName;
            }

            return new GameObjectRecord
            {
                InstanceId = gameObject.GetInstanceID(),
                Name = gameObject.name,
                Path = GetPath(gameObject.transform),
                Scene = gameObject.scene.name,
                Depth = depth,
                ParentInstanceId = gameObject.transform.parent == null
                    ? 0
                    : gameObject.transform.parent.gameObject.GetInstanceID(),
                ActiveSelf = gameObject.activeSelf,
                ActiveInHierarchy = gameObject.activeInHierarchy,
                Layer = gameObject.layer,
                Tag = SafeGetTag(gameObject),
                Components = componentNames
            };
        }

        private static string SafeGetTag(GameObject gameObject)
        {
            try
            {
                return gameObject.tag;
            }
            catch (UnityException)
            {
                return string.Empty;
            }
        }

        private static int GetDepth(Transform transform)
        {
            int depth = 0;
            Transform current = transform.parent;
            while (current != null)
            {
                depth++;
                current = current.parent;
            }

            return depth;
        }

        private static string GetPath(Transform transform)
        {
            List<string> parts = new List<string>();
            Transform current = transform;
            while (current != null)
            {
                parts.Add(current.name);
                current = current.parent;
            }

            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            return Math.Min(maximum, Math.Max(minimum, value));
        }

        private static void CaptureLog(string condition, string stackTrace, LogType type)
        {
            ConsoleRecord record = new ConsoleRecord
            {
                Utc = DateTime.UtcNow.ToString("o"),
                Type = type.ToString(),
                Message = condition ?? string.Empty,
                StackTrace = stackTrace ?? string.Empty
            };

            lock (ConsoleLock)
            {
                ConsoleRecords.Add(record);
                if (ConsoleRecords.Count > ConsoleCapacity)
                {
                    ConsoleRecords.RemoveRange(0, ConsoleRecords.Count - ConsoleCapacity);
                }
            }
        }

        [Serializable]
        private sealed class ReadConsoleArguments
        {
            public int Limit;
            public string[] Types;
            public string FilterText;
            public int Offset;
            public bool IncludeStackTrace;
            public string Format;
        }

        [Serializable]
        private sealed class HierarchyArguments
        {
            public int MaxDepth;
            public int Offset;
            public int PageSize;
        }

        [Serializable]
        private sealed class FindArguments
        {
            public string Query;
            public string SearchMethod;
            public bool IncludeInactive;
            public int Offset;
            public int PageSize;
        }

        [Serializable]
        private sealed class PlayModeArguments
        {
            public string Action;
        }

        [Serializable]
        private sealed class UndoRedoArguments
        {
            public string Action;
        }

        [Serializable]
        private sealed class ReloadSceneArguments
        {
            public bool ConfirmDiscardChanges;
        }

        [Serializable]
        private sealed class RefreshArguments
        {
            public string Mode;
            public string Scope;
            public string Compile;
        }

        [Serializable]
        private sealed class PingResult
        {
            public bool Ok;
            public string BridgeVersion;
            public string UnityVersion;
            public string ProjectPath;
            public string Utc;
        }

        [Serializable]
        private sealed class EditorStateResult
        {
            public string UnityVersion;
            public bool IsPlaying;
            public bool IsPaused;
            public bool IsCompiling;
            public bool IsUpdating;
            public bool ReadyForTools;
            public string ActiveSceneName;
            public string ActiveScenePath;
            public bool ActiveSceneIsDirty;
            public int ActiveSceneRootCount;
        }

        [Serializable]
        private sealed class ReadConsoleResult
        {
            public ConsoleRecord[] Records;
            public int TotalMatches;
            public string Cursor;
            public string NextCursor;
            public bool HasMore;
            public string Format;
            public string Note;
        }

        [Serializable]
        private sealed class ClearConsoleResult
        {
            public bool Success;
            public int ClearedCount;
            public string Message;
        }

        [Serializable]
        private sealed class ConsoleRecord
        {
            public string Utc;
            public string Type;
            public string Message;
            public string StackTrace;
        }

        [Serializable]
        private sealed class HierarchyResult
        {
            public string SceneName;
            public string ScenePath;
            public int RootCount;
            public int MaxDepth;
            public int PageSize;
            public string Cursor;
            public string NextCursor;
            public bool HasMore;
            [NonSerialized]
            public int VisitedCount;
            public List<GameObjectRecord> Objects;
        }

        [Serializable]
        private sealed class FindResult
        {
            public string Query;
            public string SearchMethod;
            public bool IncludeInactive;
            public string Cursor;
            public string NextCursor;
            public int PageSize;
            public int TotalMatches;
            public bool HasMore;
            public GameObjectRecord[] Objects;
        }

        [Serializable]
        private sealed class GameObjectRecord
        {
            public int InstanceId;
            public string Name;
            public string Path;
            public string Scene;
            public int Depth;
            public int ParentInstanceId;
            public bool ActiveSelf;
            public bool ActiveInHierarchy;
            public int Layer;
            public string Tag;
            public string[] Components;
        }

        [Serializable]
        private sealed class ScheduledResult
        {
            public bool Scheduled;
            public string Action;
            public string Message;
        }

        [Serializable]
        private sealed class UndoRedoResult
        {
            public bool Ok;
            public string Action;
            public bool ActiveSceneIsDirty;
        }
    }
}
#endif
