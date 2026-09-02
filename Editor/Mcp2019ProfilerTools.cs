#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
using UProfiler = UnityEngine.Profiling.Profiler;

namespace UnityMcp2019
{
    internal static class Mcp2019ProfilerTools
    {
        private const BindingFlags StaticAny = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        private const BindingFlags InstanceAny = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly string[] AreaNames = Enum.GetNames(typeof(ProfilerArea));

        internal static string Execute(string argumentsJson)
        {
            ProfilerArguments a = string.IsNullOrEmpty(argumentsJson) || argumentsJson == "{}"
                ? new ProfilerArguments()
                : JsonUtility.FromJson<ProfilerArguments>(argumentsJson) ?? new ProfilerArguments();
            string action = (a.Action ?? string.Empty).Trim().ToLowerInvariant();
            try
            {
                ProfilerResult result;
                switch (action)
                {
                    case "ping": result = Ping(); break;
                    case "profiler_start": result = ProfilerStart(a); break;
                    case "profiler_stop": result = ProfilerStop(); break;
                    case "profiler_status": result = ProfilerStatus(); break;
                    case "profiler_set_areas": result = ProfilerSetAreas(a); break;
                    case "get_frame_timing": result = GetFrameTiming(); break;
                    case "get_counters": result = GetCounters(a); break;
                    case "get_object_memory": result = GetObjectMemory(a); break;
                    case "memory_take_snapshot": result = MemoryTakeSnapshot(a); break;
                    case "memory_list_snapshots": result = MemoryListSnapshots(a); break;
                    case "memory_compare_snapshots": result = MemoryCompareSnapshots(a); break;
                    case "frame_debugger_enable": result = FrameDebuggerEnable(); break;
                    case "frame_debugger_disable": result = FrameDebuggerDisable(); break;
                    case "frame_debugger_get_events": result = FrameDebuggerEvents(a); break;
                    default: return JsonUtility.ToJson(Fail("Unknown manage_profiler action: " + action));
                }
                return JsonUtility.ToJson(result);
            }
            catch (Exception ex)
            {
                return JsonUtility.ToJson(Fail(ex.GetBaseException().Message));
            }
        }

        private static ProfilerResult Ping()
        {
            return Ok("manage_profiler is available.", new ProfilerData
            {
                UnityVersion = Application.unityVersion,
                Tool = "manage_profiler",
                Group = "profiling",
                MemorySnapshotAvailable = MemoryProfilerType() != null,
                FrameDebuggerAvailable = FrameDebuggerType() != null,
            });
        }

        private static ProfilerResult ProfilerStart(ProfilerArguments a)
        {
            if (!string.IsNullOrEmpty(a.LogFile))
            {
                string directory = Path.GetDirectoryName(Path.GetFullPath(a.LogFile));
                if (!Directory.Exists(directory)) throw new InvalidOperationException("Log file directory does not exist: " + directory);
                UProfiler.logFile = Path.GetFullPath(a.LogFile);
                UProfiler.enableBinaryLog = true;
            }
            if (a.HasEnableCallstacks) UProfiler.enableAllocationCallstacks = a.EnableCallstacks;
            UProfiler.enabled = true;
            return Ok("Profiler started.", StatusData());
        }

        private static ProfilerResult ProfilerStop()
        {
            string previous = UProfiler.enableBinaryLog ? UProfiler.logFile : string.Empty;
            UProfiler.enableBinaryLog = false;
            UProfiler.enableAllocationCallstacks = false;
            UProfiler.enabled = false;
            ProfilerData data = StatusData();
            data.PreviousLogFile = previous;
            return Ok("Profiler stopped.", data);
        }

        private static ProfilerResult ProfilerStatus()
        {
            return Ok("Profiler status read.", StatusData());
        }

        private static ProfilerData StatusData()
        {
            List<AreaRecord> areas = new List<AreaRecord>();
            MethodInfo getArea = typeof(UProfiler).GetMethod("GetAreaEnabled", StaticAny);
            foreach (string name in AreaNames)
            {
                ProfilerArea area = (ProfilerArea)Enum.Parse(typeof(ProfilerArea), name);
                bool enabled = getArea != null && Convert.ToBoolean(getArea.Invoke(null, new object[] { area }));
                areas.Add(new AreaRecord { Name = name, Enabled = enabled });
            }
            return new ProfilerData
            {
                Enabled = UProfiler.enabled,
                Recording = UProfiler.enableBinaryLog,
                LogFile = UProfiler.enableBinaryLog ? UProfiler.logFile : string.Empty,
                AllocationCallstacks = UProfiler.enableAllocationCallstacks,
                Areas = areas.ToArray(),
            };
        }

        private static ProfilerResult ProfilerSetAreas(ProfilerArguments a)
        {
            if (a.Areas == null || a.Areas.Length == 0) throw new InvalidOperationException("areas is required. Valid areas: " + string.Join(", ", AreaNames));
            MethodInfo setArea = typeof(UProfiler).GetMethod("SetAreaEnabled", StaticAny);
            if (setArea == null) throw new InvalidOperationException("Profiler area control is unavailable in this Unity editor.");
            foreach (AreaInput entry in a.Areas)
            {
                if (!Enum.TryParse(entry.Name, true, out ProfilerArea area)) throw new InvalidOperationException("Unknown profiler area: " + entry.Name);
                setArea.Invoke(null, new object[] { area, entry.Enabled });
            }
            ProfilerData data = StatusData();
            return Ok("Updated " + a.Areas.Length + " profiler area(s).", data);
        }

        private static ProfilerResult GetFrameTiming()
        {
            FrameTimingManager.CaptureFrameTimings();
            FrameTiming[] timings = new FrameTiming[1];
            uint count = FrameTimingManager.GetLatestTimings(1, timings);
            if (count == 0) return Ok("No frame timing data available yet (need a few frames).", new ProfilerData { Available = false });
            FrameTiming t = timings[0];
            return Ok("Frame timing captured.", new ProfilerData
            {
                Available = true,
                FrameTiming = new FrameTimingRecord
                {
                    CpuFrameTimeMs = t.cpuFrameTime,
                    GpuFrameTimeMs = t.gpuFrameTime,
                    CpuTimePresentCalled = t.cpuTimePresentCalled,
                    CpuTimeFrameComplete = t.cpuTimeFrameComplete,
                    HeightScale = t.heightScale,
                    WidthScale = t.widthScale,
                    SyncInterval = t.syncInterval,
                },
            });
        }

        private static ProfilerResult GetCounters(ProfilerArguments a)
        {
            if (string.IsNullOrEmpty(a.Category)) throw new InvalidOperationException("category is required.");
            string category = a.Category.Trim();
            List<CounterRecord> counters = new List<CounterRecord>();
            if (string.Equals(category, "Memory", StringComparison.OrdinalIgnoreCase))
            {
                AddCounter(counters, "Total Allocated Memory", UProfiler.GetTotalAllocatedMemoryLong(), "Bytes", a.Counters);
                AddCounter(counters, "Total Reserved Memory", UProfiler.GetTotalReservedMemoryLong(), "Bytes", a.Counters);
                AddCounter(counters, "Total Unused Reserved Memory", UProfiler.GetTotalUnusedReservedMemoryLong(), "Bytes", a.Counters);
                AddCounter(counters, "Mono Heap Size", UProfiler.GetMonoHeapSizeLong(), "Bytes", a.Counters);
                AddCounter(counters, "Mono Used Size", UProfiler.GetMonoUsedSizeLong(), "Bytes", a.Counters);
            }
            else if (string.Equals(category, "Render", StringComparison.OrdinalIgnoreCase))
            {
                Type stats = Type.GetType("UnityEditor.UnityStats, UnityEditor");
                string[] names = a.Counters != null && a.Counters.Length > 0 ? a.Counters : new[] { "batches", "drawCalls", "setPassCalls", "triangles", "vertices", "shadowCasters" };
                foreach (string name in names)
                {
                    PropertyInfo property = stats == null ? null : stats.GetProperty(name, StaticAny);
                    if (property == null) counters.Add(new CounterRecord { Name = name, Valid = false, Unit = "Unknown" });
                    else counters.Add(new CounterRecord { Name = name, Value = Convert.ToInt64(property.GetValue(null, null)), Valid = true, Unit = "Count" });
                }
            }
            else
            {
                string[] names = a.Counters ?? new string[0];
                foreach (string name in names) counters.Add(new CounterRecord { Name = name, Valid = false, Unit = "UnavailableInUnity2019" });
            }
            return Ok("Captured " + counters.Count + " counter(s) from '" + category + "'.", new ProfilerData { Category = category, Counters = counters.ToArray() });
        }

        private static void AddCounter(List<CounterRecord> output, string name, long value, string unit, string[] requested)
        {
            if (requested != null && requested.Length > 0 && !requested.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase))) return;
            output.Add(new CounterRecord { Name = name, Value = value, Valid = true, Unit = unit });
        }

        private static ProfilerResult GetObjectMemory(ProfilerArguments a)
        {
            if (string.IsNullOrEmpty(a.ObjectPath)) throw new InvalidOperationException("object_path is required.");
            UnityEngine.Object target = FindSceneObject(a.ObjectPath) as UnityEngine.Object;
            string source = "scene_hierarchy";
            if (target == null)
            {
                target = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(a.ObjectPath);
                source = "asset_database";
            }
            if (target == null) throw new InvalidOperationException("Object not found at path: " + a.ObjectPath);
            long bytes = UProfiler.GetRuntimeMemorySizeLong(target);
            return Ok("Object memory read.", new ProfilerData
            {
                Object = new ObjectMemoryRecord { Name = target.name, Type = target.GetType().Name, SizeBytes = bytes, SizeMb = Math.Round(bytes / 1048576d, 3), Source = source },
            });
        }

        private static ProfilerResult MemoryTakeSnapshot(ProfilerArguments a)
        {
            Type memoryProfiler = MemoryProfilerType();
            if (memoryProfiler == null) throw new InvalidOperationException("Package com.unity.memoryprofiler or Unity's experimental MemoryProfiler API is required.");
            string path = string.IsNullOrEmpty(a.SnapshotPath)
                ? Path.Combine(ProjectRoot(), "MemoryCaptures", "snapshot_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".snap")
                : Path.GetFullPath(a.SnapshotPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            MethodInfo method = memoryProfiler.GetMethods(StaticAny).Where(m => m.Name == "TakeSnapshot").OrderBy(m => m.GetParameters().Length).FirstOrDefault();
            if (method == null) throw new InvalidOperationException("MemoryProfiler.TakeSnapshot API was not found.");
            Action<string, bool> callback = (writtenPath, success) =>
            {
                string marker = path + ".mcp-status";
                try { File.WriteAllText(marker, success ? "succeeded" : "failed"); } catch { }
            };
            ParameterInfo[] parameters = method.GetParameters();
            object[] invoke = new object[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                Type type = parameters[i].ParameterType;
                if (i == 0 && type == typeof(string)) invoke[i] = path;
                else if (type == typeof(Action<string, bool>)) invoke[i] = callback;
                else if (type.IsEnum) invoke[i] = Enum.ToObject(type, 0);
                else if (type == typeof(uint)) invoke[i] = 0u;
                else invoke[i] = null;
            }
            method.Invoke(null, invoke);
            return Ok("Memory snapshot capture started.", new ProfilerData { SnapshotPath = path, SnapshotStatus = "running", MemorySnapshotAvailable = true });
        }

        private static ProfilerResult MemoryListSnapshots(ProfilerArguments a)
        {
            List<string> dirs = new List<string>();
            if (!string.IsNullOrEmpty(a.SearchPath)) dirs.Add(Path.GetFullPath(a.SearchPath));
            else
            {
                dirs.Add(Path.Combine(ProjectRoot(), "MemoryCaptures"));
                dirs.Add(Path.Combine(Application.temporaryCachePath, "MemoryCaptures"));
            }
            List<SnapshotRecord> snapshots = new List<SnapshotRecord>();
            foreach (string directory in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(directory)) continue;
                foreach (string file in Directory.GetFiles(directory, "*.snap", SearchOption.TopDirectoryOnly)) snapshots.Add(Snapshot(file));
            }
            return Ok("Found " + snapshots.Count + " snapshot(s).", new ProfilerData { Snapshots = snapshots.OrderByDescending(s => s.Created).ToArray(), SearchedDirectories = dirs.ToArray(), Count = snapshots.Count });
        }

        private static ProfilerResult MemoryCompareSnapshots(ProfilerArguments a)
        {
            if (string.IsNullOrEmpty(a.SnapshotA) || string.IsNullOrEmpty(a.SnapshotB)) throw new InvalidOperationException("snapshot_a and snapshot_b are required.");
            string pathA = Path.GetFullPath(a.SnapshotA);
            string pathB = Path.GetFullPath(a.SnapshotB);
            if (!File.Exists(pathA)) throw new FileNotFoundException("Snapshot file not found", pathA);
            if (!File.Exists(pathB)) throw new FileNotFoundException("Snapshot file not found", pathB);
            SnapshotRecord first = Snapshot(pathA);
            SnapshotRecord second = Snapshot(pathB);
            return Ok("Snapshot comparison (file-level metadata).", new ProfilerData
            {
                SnapshotA = first, SnapshotB = second,
                SnapshotDelta = new SnapshotDeltaRecord { SizeDeltaBytes = second.SizeBytes - first.SizeBytes, SizeDeltaMb = second.SizeMb - first.SizeMb, TimeDeltaSeconds = (DateTime.Parse(second.Created) - DateTime.Parse(first.Created)).TotalSeconds },
                Note = "For detailed object-level comparison, open both snapshots in the Memory Profiler window.",
            });
        }

        private static SnapshotRecord Snapshot(string path)
        {
            FileInfo info = new FileInfo(path);
            string marker = path + ".mcp-status";
            return new SnapshotRecord { Path = info.FullName, SizeBytes = info.Length, SizeMb = Math.Round(info.Length / 1048576d, 2), Created = info.CreationTimeUtc.ToString("o"), Status = File.Exists(marker) ? File.ReadAllText(marker) : "available" };
        }

        private static ProfilerResult FrameDebuggerEnable()
        {
            Type type = FrameDebuggerType();
            if (type == null) throw new InvalidOperationException("FrameDebuggerUtility was not found via reflection.");
            EditorApplication.ExecuteMenuItem("Window/Analysis/Frame Debugger");
            if (EditorApplication.isPlaying && !EditorApplication.isPaused) throw new InvalidOperationException("Game must be paused before enabling Frame Debugger.");
            SetFrameDebuggerEnabled(type, true);
            return Ok("Frame Debugger enabled.", new ProfilerData { Enabled = true, EventCount = FrameDebuggerCount(type), FrameDebuggerAvailable = true });
        }

        private static ProfilerResult FrameDebuggerDisable()
        {
            Type type = FrameDebuggerType();
            if (type == null) throw new InvalidOperationException("FrameDebuggerUtility was not found via reflection.");
            SetFrameDebuggerEnabled(type, false);
            return Ok("Frame Debugger disabled.", new ProfilerData { Enabled = false, FrameDebuggerAvailable = true });
        }

        private static ProfilerResult FrameDebuggerEvents(ProfilerArguments a)
        {
            Type type = FrameDebuggerType();
            if (type == null) throw new InvalidOperationException("FrameDebuggerUtility was not found via reflection.");
            int total = FrameDebuggerCount(type);
            int cursor = a.HasCursor ? Math.Max(0, a.Cursor) : 0;
            int pageSize = a.HasPageSize ? Math.Max(1, Math.Min(500, a.PageSize)) : 50;
            int end = Math.Min(total, cursor + pageSize);
            MethodInfo names = type.GetMethod("GetFrameEventInfoName", StaticAny);
            MethodInfo all = type.GetMethod("GetFrameEvents", StaticAny);
            Array descriptors = null;
            try { descriptors = all == null ? null : all.Invoke(null, null) as Array; } catch { }
            List<FrameEventRecord> events = new List<FrameEventRecord>();
            for (int i = cursor; i < end; i++)
            {
                FrameEventRecord record = new FrameEventRecord { Index = i };
                try { if (names != null) record.Name = Convert.ToString(names.Invoke(null, new object[] { i })); } catch { }
                if (descriptors != null && i < descriptors.Length)
                {
                    object descriptor = descriptors.GetValue(i);
                    record.EventType = ReadText(descriptor, "type");
                    record.GameObjectInstanceId = ReadInt(descriptor, "gameObjectInstanceID");
                }
                events.Add(record);
            }
            return Ok(total == 0 ? "Frame Debugger has no events. Is it enabled?" : "Frame Debugger events read.", new ProfilerData
            {
                Events = events.ToArray(), EventCount = total, Count = events.Count,
                Cursor = cursor, PageSize = pageSize, HasMore = end < total, NextCursor = end < total ? end.ToString() : string.Empty,
            });
        }

        private static Type MemoryProfilerType()
        {
            return Type.GetType("Unity.Profiling.Memory.MemoryProfiler, UnityEngine.CoreModule")
                   ?? Type.GetType("UnityEngine.Profiling.Memory.Experimental.MemoryProfiler, UnityEngine.CoreModule")
                   ?? Type.GetType("Unity.MemoryProfiler.MemoryProfiler, Unity.MemoryProfiler.Editor");
        }

        private static Type FrameDebuggerType()
        {
            return Type.GetType("UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility, UnityEditor")
                   ?? Type.GetType("UnityEditorInternal.FrameDebuggerUtility, UnityEditor");
        }

        private static int FrameDebuggerCount(Type type)
        {
            PropertyInfo property = type.GetProperty("count", StaticAny) ?? type.GetProperty("eventsCount", StaticAny);
            try { return property == null ? 0 : Convert.ToInt32(property.GetValue(null, null)); } catch { return 0; }
        }

        private static void SetFrameDebuggerEnabled(Type type, bool enabled)
        {
            MethodInfo method = type.GetMethods(StaticAny).FirstOrDefault(m => m.Name == "SetEnabled");
            if (method == null) throw new InvalidOperationException("Frame debugger SetEnabled API was not found.");
            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length == 2) method.Invoke(null, new object[] { enabled, 0 });
            else if (parameters.Length == 1) method.Invoke(null, new object[] { enabled });
            else throw new InvalidOperationException("Unsupported Frame Debugger SetEnabled signature.");
        }

        private static UnityEngine.Object FindSceneObject(string path)
        {
            GameObject direct = GameObject.Find(path);
            if (direct != null) return direct;
            string normalized = path.Trim('/');
            foreach (GameObject go in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (!go.scene.IsValid()) continue;
                if (string.Equals(go.name, normalized, StringComparison.Ordinal) || string.Equals(FullPath(go), normalized, StringComparison.Ordinal)) return go;
            }
            return null;
        }

        private static string FullPath(GameObject go)
        {
            string path = go.name;
            for (Transform parent = go.transform.parent; parent != null; parent = parent.parent) path = parent.name + "/" + path;
            return path;
        }

        private static string ProjectRoot() { return Path.GetFullPath(Path.Combine(Application.dataPath, "..")); }

        private static object ReadValue(object instance, string name)
        {
            if (instance == null) return null;
            Type type = instance.GetType();
            FieldInfo field = type.GetField(name, InstanceAny);
            if (field != null) return field.GetValue(instance);
            PropertyInfo property = type.GetProperty(name, InstanceAny);
            return property == null ? null : property.GetValue(instance, null);
        }

        private static string ReadText(object value, string member) { object result = ReadValue(value, member); return result == null ? string.Empty : result.ToString(); }
        private static int ReadInt(object value, string member) { object result = ReadValue(value, member); try { return result == null ? 0 : Convert.ToInt32(result); } catch { return 0; } }
        private static ProfilerResult Ok(string message, ProfilerData data) { return new ProfilerResult { Success = true, Message = message, Data = data ?? new ProfilerData() }; }
        private static ProfilerResult Fail(string message) { return new ProfilerResult { Success = false, Message = message, Data = new ProfilerData() }; }

        [Serializable] private sealed class ProfilerArguments
        {
            public string Action, Category, ObjectPath, LogFile, SnapshotPath, SearchPath, SnapshotA, SnapshotB;
            public string[] Counters;
            public bool EnableCallstacks, HasEnableCallstacks;
            public int PageSize, Cursor;
            public bool HasPageSize, HasCursor;
            public AreaInput[] Areas;
        }
        [Serializable] private sealed class AreaInput { public string Name; public bool Enabled; }
        [Serializable] private sealed class ProfilerResult { public bool Success; public string Message; public ProfilerData Data; }
        [Serializable] private sealed class ProfilerData
        {
            public string UnityVersion, Tool, Group, LogFile, PreviousLogFile, Category, SnapshotPath, SnapshotStatus, Note, NextCursor;
            public bool Enabled, Recording, AllocationCallstacks, Available, MemorySnapshotAvailable, FrameDebuggerAvailable, HasMore;
            public int Count, EventCount, Cursor, PageSize;
            public AreaRecord[] Areas;
            public CounterRecord[] Counters;
            public FrameTimingRecord FrameTiming;
            public ObjectMemoryRecord Object;
            public SnapshotRecord[] Snapshots;
            public string[] SearchedDirectories;
            public SnapshotRecord SnapshotA, SnapshotB;
            public SnapshotDeltaRecord SnapshotDelta;
            public FrameEventRecord[] Events;
        }
        [Serializable] private sealed class AreaRecord { public string Name; public bool Enabled; }
        [Serializable] private sealed class CounterRecord { public string Name, Unit; public long Value; public bool Valid; }
        [Serializable] private sealed class FrameTimingRecord { public double CpuFrameTimeMs, GpuFrameTimeMs, CpuTimePresentCalled, CpuTimeFrameComplete; public float HeightScale, WidthScale; public uint SyncInterval; }
        [Serializable] private sealed class ObjectMemoryRecord { public string Name, Type, Source; public long SizeBytes; public double SizeMb; }
        [Serializable] private sealed class SnapshotRecord { public string Path, Created, Status; public long SizeBytes; public double SizeMb; }
        [Serializable] private sealed class SnapshotDeltaRecord { public long SizeDeltaBytes; public double SizeDeltaMb, TimeDeltaSeconds; }
        [Serializable] private sealed class FrameEventRecord { public int Index, GameObjectInstanceId; public string Name, EventType; }
    }
}
#endif
