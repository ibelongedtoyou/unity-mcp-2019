#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityBuildResult = UnityEditor.Build.Reporting.BuildResult;

namespace UnityMcp2019
{
    [InitializeOnLoad]
    internal static class Mcp2019BuildTools
    {
        static Mcp2019BuildTools()
        {
            Directory.CreateDirectory(StorageDirectory);
            RecoverInterruptedJobs();
        }

        internal static string Execute(string argumentsJson)
        {
            BuildArguments arguments = string.IsNullOrEmpty(argumentsJson) || argumentsJson == "{}"
                ? new BuildArguments()
                : JsonUtility.FromJson<BuildArguments>(argumentsJson) ?? new BuildArguments();
            string action = (arguments.Action ?? string.Empty).Trim().ToLowerInvariant();
            try
            {
                BuildResult result;
                switch (action)
                {
                    case "build": result = StartBuild(arguments); break;
                    case "status": result = Status(arguments.JobId); break;
                    case "platform": result = Platform(arguments); break;
                    case "settings": result = Settings(arguments); break;
                    case "scenes": result = Scenes(arguments); break;
                    case "profiles": result = Fail("Build Profiles require Unity 6; current version is " + Application.unityVersion + "."); break;
                    case "batch": result = StartBatch(arguments); break;
                    case "cancel": result = Cancel(arguments.JobId); break;
                    default: result = Fail("Unknown build action: " + action); break;
                }
                return JsonUtility.ToJson(result);
            }
            catch (Exception exception)
            {
                return JsonUtility.ToJson(Fail(exception.GetType().Name + ": " + exception.Message));
            }
        }

        private static BuildResult StartBuild(BuildArguments arguments)
        {
            if (BuildPipeline.isBuildingPlayer) return Fail("A build is already in progress.");
            BuildTarget target;
            if (!TryResolveTarget(arguments.Target, out target)) return Fail(UnknownTarget(arguments.Target));
            BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(target);
            if (!BuildPipeline.IsBuildTargetSupported(group, target))
                return Fail("Platform '" + target + "' is not installed.");
            if (!string.IsNullOrEmpty(arguments.Profile))
                return Fail("Build Profiles require Unity 6 and cannot be used in Unity 2019.");
            string backendError = ApplyScriptingBackend(group, arguments.ScriptingBackend);
            if (backendError != null) return Fail(backendError);
            string output = string.IsNullOrEmpty(arguments.OutputPath)
                ? DefaultOutputPath(target, PlayerSettings.productName)
                : arguments.OutputPath;
            string[] scenes = arguments.HasScenes
                ? ValidateBuildScenes(arguments.SceneEntries)
                : EditorBuildSettings.scenes.Where(item => item.enabled).Select(item => item.path).ToArray();
            if (scenes.Length == 0) return Fail("No enabled build scenes were found.");
            BuildOptions options = ParseBuildOptions(arguments.Options, arguments.HasDevelopment && arguments.Development, arguments.Subtarget);
            BuildJob job = CreateJob("build", target.ToString(), output);
            job.SceneCount = scenes.Length;
            WriteJob(job);
            EditorApplication.delayCall += delegate { RunBuild(job.JobId, target, output, scenes, options); };
            return Pending("Build scheduled.", ToData(job));
        }

        private static BuildResult StartBatch(BuildArguments arguments)
        {
            if (BuildPipeline.isBuildingPlayer) return Fail("A build is already in progress.");
            if (arguments.Targets == null || arguments.Targets.Length == 0)
            {
                if (arguments.Profiles != null && arguments.Profiles.Length > 0)
                    return Fail("Profile-based batch builds require Unity 6.");
                return Fail("targets is required for batch builds.");
            }
            List<BuildTarget> targets = new List<BuildTarget>();
            foreach (string name in arguments.Targets)
            {
                BuildTarget target;
                if (!TryResolveTarget(name, out target)) return Fail(UnknownTarget(name));
                BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(target);
                if (!BuildPipeline.IsBuildTargetSupported(group, target))
                    return Fail("Platform '" + target + "' is not installed.");
                targets.Add(target);
            }
            string[] scenes = arguments.HasScenes
                ? ValidateBuildScenes(arguments.SceneEntries)
                : EditorBuildSettings.scenes.Where(item => item.enabled).Select(item => item.path).ToArray();
            if (scenes.Length == 0) return Fail("No enabled build scenes were found.");
            BuildJob job = CreateJob("batch", string.Join(",", targets.Select(item => item.ToString()).ToArray()), arguments.OutputDir);
            job.Total = targets.Count;
            job.SceneCount = scenes.Length;
            WriteJob(job);
            string outputDirectory = string.IsNullOrEmpty(arguments.OutputDir) ? "Builds" : arguments.OutputDir.TrimEnd('/', '\\');
            BuildOptions options = ParseBuildOptions(arguments.Options, arguments.HasDevelopment && arguments.Development, arguments.Subtarget);
            EditorApplication.delayCall += delegate { RunBatch(job.JobId, targets.ToArray(), outputDirectory, scenes, options); };
            return Pending("Batch build scheduled.", ToData(job));
        }

        private static void RunBuild(string jobId, BuildTarget target, string output, string[] scenes, BuildOptions options)
        {
            BuildJob job = ReadJob(jobId);
            if (job == null || job.Status == "cancelled") return;
            job.Status = "building";
            job.StartedUtc = DateTime.UtcNow.ToString("o");
            WriteJob(job);
            try
            {
                BuildPlayerOptions playerOptions = new BuildPlayerOptions
                {
                    scenes = scenes, locationPathName = output,
                    target = target, options = options
                };
                BuildReport report = BuildPipeline.BuildPlayer(playerOptions);
                ApplyReport(job, report);
            }
            catch (Exception exception)
            {
                job.Status = "failed";
                job.Error = exception.Message;
            }
            job.CompletedUtc = DateTime.UtcNow.ToString("o");
            WriteJob(job);
        }

        private static void RunBatch(string jobId, BuildTarget[] targets, string outputDirectory, string[] scenes, BuildOptions options)
        {
            BuildJob job = ReadJob(jobId);
            if (job == null || job.Status == "cancelled") return;
            job.Status = "building";
            job.StartedUtc = DateTime.UtcNow.ToString("o");
            WriteJob(job);
            for (int index = 0; index < targets.Length; index++)
            {
                job = ReadJob(jobId) ?? job;
                if (job.CancelRequested) { job.Status = "cancelled"; break; }
                BuildTarget target = targets[index];
                BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(target);
                try
                {
                    if (EditorUserBuildSettings.activeBuildTarget != target)
                        EditorUserBuildSettings.SwitchActiveBuildTarget(group, target);
                    string output = outputDirectory + "/" + DefaultOutputPath(target, PlayerSettings.productName).Substring("Builds/".Length);
                    BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                    {
                        scenes = scenes, locationPathName = output, target = target, options = options
                    });
                    if (report.summary.result == UnityBuildResult.Succeeded) job.Passed++;
                    else
                    {
                        job.Failed++;
                        job.Error += target + ": " + report.summary.result + "; ";
                    }
                }
                catch (Exception exception)
                {
                    job.Failed++;
                    job.Error += target + ": " + exception.Message + "; ";
                }
                job.Completed = index + 1;
                WriteJob(job);
            }
            if (job.Status != "cancelled") job.Status = job.Failed == 0 ? "succeeded" : "failed";
            job.CompletedUtc = DateTime.UtcNow.ToString("o");
            WriteJob(job);
        }

        private static void ApplyReport(BuildJob job, BuildReport report)
        {
            if (report == null)
            {
                job.Status = "failed";
                job.Error = "BuildPipeline returned no report.";
                return;
            }
            BuildSummary summary = report.summary;
            job.Status = summary.result == UnityBuildResult.Succeeded ? "succeeded" : "failed";
            job.Result = summary.result.ToString().ToLowerInvariant();
            job.Platform = summary.platform.ToString();
            job.OutputPath = summary.outputPath;
            job.TotalSize = summary.totalSize;
            job.DurationSeconds = summary.totalTime.TotalSeconds;
            job.Errors = (int)summary.totalErrors;
            job.Warnings = (int)summary.totalWarnings;
            if (job.Status == "failed") job.Error = "Build result: " + summary.result;
        }

        private static BuildResult Status(string requestedId)
        {
            string id = string.IsNullOrWhiteSpace(requestedId) ? ReadLatestJobId() : requestedId.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(id)) return Fail("No build jobs found.");
            BuildJob job = ReadJob(id);
            if (job == null) return Fail("No build job found with ID '" + id + "'.");
            return job.Status == "pending" || job.Status == "building"
                ? Pending("Build " + job.Status + ".", ToData(job))
                : job.Status == "succeeded"
                    ? Success("Build succeeded.", ToData(job))
                    : job.Status == "cancelled"
                        ? Success("Build cancelled.", ToData(job))
                        : Fail("Build failed: " + job.Error, ToData(job));
        }

        private static BuildResult Cancel(string requestedId)
        {
            if (string.IsNullOrWhiteSpace(requestedId)) return Fail("job_id is required for cancel.");
            BuildJob job = ReadJob(requestedId.Trim());
            if (job == null) return Fail("Build job not found.");
            if (job.Status == "pending")
            {
                job.Status = "cancelled";
                job.CompletedUtc = DateTime.UtcNow.ToString("o");
                WriteJob(job);
                return Success("Pending build cancelled.", ToData(job));
            }
            if (job.Operation == "batch" && job.Status == "building")
            {
                job.CancelRequested = true;
                WriteJob(job);
                return Success("Batch cancellation requested; the current build will finish.", ToData(job));
            }
            if (job.Status == "building") return Fail("A single BuildPipeline.BuildPlayer call cannot be cancelled in Unity 2019.");
            return Fail("Build is already " + job.Status + ".");
        }

        private static BuildResult Platform(BuildArguments arguments)
        {
            if (string.IsNullOrEmpty(arguments.Target))
            {
                BuildTarget active = EditorUserBuildSettings.activeBuildTarget;
                return Success("Current build platform.", new BuildData
                {
                    Target = active.ToString(), TargetGroup = BuildPipeline.GetBuildTargetGroup(active).ToString()
                });
            }
            BuildTarget target;
            if (!TryResolveTarget(arguments.Target, out target)) return Fail(UnknownTarget(arguments.Target));
            BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(target);
            if (!BuildPipeline.IsBuildTargetSupported(group, target)) return Fail("Platform is not installed: " + target);
            BuildTarget previous = EditorUserBuildSettings.activeBuildTarget;
            if (previous != target && !EditorUserBuildSettings.SwitchActiveBuildTarget(group, target))
                return Fail("Unity failed to switch build target to " + target + ".");
            return Success(previous == target ? "Already on this platform." : "Build platform switched.", new BuildData
            {
                Target = target.ToString(), TargetGroup = group.ToString(), PreviousTarget = previous.ToString()
            });
        }

        private static BuildResult Settings(BuildArguments arguments)
        {
            if (string.IsNullOrEmpty(arguments.Property)) return Fail("property is required for settings.");
            BuildTarget target;
            if (!TryResolveTarget(arguments.Target, out target)) return Fail(UnknownTarget(arguments.Target));
            BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(target);
            string property = arguments.Property.ToLowerInvariant();
            if (arguments.HasValue)
            {
                string error = WriteSetting(property, arguments.Value ?? string.Empty, group);
                if (error != null) return Fail(error);
            }
            string value;
            string readError = ReadSetting(property, group, out value);
            return readError == null
                ? Success((arguments.HasValue ? "Set " : "Read ") + property + ".", new BuildData
                {
                    Property = property, Value = value, Target = target.ToString(), TargetGroup = group.ToString()
                })
                : Fail(readError);
        }

        private static BuildResult Scenes(BuildArguments arguments)
        {
            if (!arguments.HasScenes)
            {
                BuildSceneRecord[] records = EditorBuildSettings.scenes.Select(item => new BuildSceneRecord
                {
                    Path = item.path, Enabled = item.enabled, Guid = item.guid.ToString()
                }).ToArray();
                return Success("Build scenes read.", new BuildData { Scenes = records, SceneCount = records.Length });
            }
            BuildSceneArgument[] entries = arguments.SceneEntries ?? new BuildSceneArgument[0];
            List<EditorBuildSettingsScene> scenes = new List<EditorBuildSettingsScene>();
            foreach (BuildSceneArgument entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Path) || !entry.Path.StartsWith("Assets/", StringComparison.Ordinal) ||
                    AssetDatabase.LoadAssetAtPath<SceneAsset>(entry.Path) == null)
                    return Fail("Scene asset not found: " + entry.Path);
                scenes.Add(new EditorBuildSettingsScene(entry.Path, entry.Enabled));
            }
            EditorBuildSettings.scenes = scenes.ToArray();
            return Scenes(new BuildArguments());
        }

        private static string[] ValidateBuildScenes(BuildSceneArgument[] entries)
        {
            List<string> scenes = new List<string>();
            foreach (BuildSceneArgument entry in entries ?? new BuildSceneArgument[0])
            {
                if (!entry.Enabled) continue;
                if (string.IsNullOrEmpty(entry.Path) || AssetDatabase.LoadAssetAtPath<SceneAsset>(entry.Path) == null)
                    throw new ArgumentException("Scene asset not found: " + entry.Path);
                scenes.Add(entry.Path);
            }
            return scenes.ToArray();
        }

        private static string ReadSetting(string property, BuildTargetGroup group, out string value)
        {
            value = string.Empty;
            switch (property)
            {
                case "product_name": value = PlayerSettings.productName; return null;
                case "company_name": value = PlayerSettings.companyName; return null;
                case "version": value = PlayerSettings.bundleVersion; return null;
                case "bundle_id": value = PlayerSettings.GetApplicationIdentifier(group); return null;
                case "scripting_backend": value = PlayerSettings.GetScriptingBackend(group) == ScriptingImplementation.IL2CPP ? "il2cpp" : "mono"; return null;
                case "defines": value = PlayerSettings.GetScriptingDefineSymbolsForGroup(group); return null;
                case "architecture": value = PlayerSettings.GetArchitecture(group).ToString(); return null;
                default: return "Unknown property. Valid: product_name, company_name, version, bundle_id, scripting_backend, defines, architecture.";
            }
        }

        private static string WriteSetting(string property, string value, BuildTargetGroup group)
        {
            switch (property)
            {
                case "product_name": PlayerSettings.productName = value; return null;
                case "company_name": PlayerSettings.companyName = value; return null;
                case "version": PlayerSettings.bundleVersion = value; return null;
                case "bundle_id": PlayerSettings.SetApplicationIdentifier(group, value); return null;
                case "scripting_backend": return ApplyScriptingBackend(group, value);
                case "defines": PlayerSettings.SetScriptingDefineSymbolsForGroup(group, value); return null;
                case "architecture":
                    int architecture;
                    if (value == "x86_64" || value == "none" || value == "default") architecture = 0;
                    else if (value == "arm64") architecture = 1;
                    else if (value == "universal") architecture = 2;
                    else return "Unknown architecture: " + value;
                    PlayerSettings.SetArchitecture(group, architecture); return null;
                default: return "Unknown property. Valid: product_name, company_name, version, bundle_id, scripting_backend, defines, architecture.";
            }
        }

        private static string ApplyScriptingBackend(BuildTargetGroup group, string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            string normalized = value.ToLowerInvariant();
            if (normalized != "mono" && normalized != "il2cpp") return "scripting_backend must be mono or il2cpp.";
            PlayerSettings.SetScriptingBackend(group, normalized == "il2cpp" ? ScriptingImplementation.IL2CPP : ScriptingImplementation.Mono2x);
            return null;
        }

        private static BuildOptions ParseBuildOptions(string[] names, bool development, string subtarget)
        {
            BuildOptions result = development ? BuildOptions.Development : BuildOptions.None;
            foreach (string raw in names ?? new string[0])
            {
                switch ((raw ?? string.Empty).ToLowerInvariant())
                {
                    case "auto_run": result |= BuildOptions.AutoRunPlayer; break;
                    case "connect_profiler": result |= BuildOptions.ConnectWithProfiler; break;
                    case "allow_debugging": result |= BuildOptions.AllowDebugging; break;
                    case "build_scripts_only": result |= BuildOptions.BuildScriptsOnly; break;
                    case "strict_mode": result |= BuildOptions.StrictMode; break;
                    case "compress_lz4": result |= BuildOptions.CompressWithLz4; break;
                    case "compress_lz4hc": result |= BuildOptions.CompressWithLz4HC; break;
                    case "detailed_report":
                        BuildOptions detailed;
                        if (!Enum.TryParse("DetailedBuildReport", out detailed))
                            throw new ArgumentException("detailed_report is not supported by Unity " + Application.unityVersion + ".");
                        result |= detailed;
                        break;
                    case "deep_profiling": result |= BuildOptions.EnableDeepProfilingSupport; break;
                    case "clean_build": break;
                    default: throw new ArgumentException("Unknown BuildOption: " + raw);
                }
            }
            if (string.Equals(subtarget, "server", StringComparison.OrdinalIgnoreCase)) result |= BuildOptions.EnableHeadlessMode;
            else if (!string.IsNullOrEmpty(subtarget) && !string.Equals(subtarget, "player", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("subtarget must be player or server.");
            return result;
        }

        private static bool TryResolveTarget(string name, out BuildTarget target)
        {
            if (string.IsNullOrEmpty(name)) { target = EditorUserBuildSettings.activeBuildTarget; return true; }
            switch (name.ToLowerInvariant())
            {
                case "windows64": target = BuildTarget.StandaloneWindows64; return true;
                case "windows": case "windows32": target = BuildTarget.StandaloneWindows; return true;
                case "osx": case "macos": target = BuildTarget.StandaloneOSX; return true;
                case "linux": case "linux64": target = BuildTarget.StandaloneLinux64; return true;
                case "android": target = BuildTarget.Android; return true;
                case "ios": target = BuildTarget.iOS; return true;
                case "webgl": target = BuildTarget.WebGL; return true;
                case "uwp": target = BuildTarget.WSAPlayer; return true;
                case "tvos": target = BuildTarget.tvOS; return true;
                default: return Enum.TryParse(name, true, out target) && Enum.IsDefined(typeof(BuildTarget), target);
            }
        }

        private static string UnknownTarget(string name)
        { return "Unknown build target '" + name + "'. Valid: windows64, osx, linux64, android, ios, webgl, uwp, tvos."; }

        private static string DefaultOutputPath(BuildTarget target, string product)
        {
            string root = "Builds/" + target + "/" + product;
            if (target == BuildTarget.StandaloneWindows || target == BuildTarget.StandaloneWindows64) return root + ".exe";
            if (target == BuildTarget.StandaloneOSX) return root + ".app";
            if (target == BuildTarget.StandaloneLinux64) return root + ".x86_64";
            if (target == BuildTarget.Android) return root + (EditorUserBuildSettings.buildAppBundle ? ".aab" : ".apk");
            return root;
        }

        private static BuildJob CreateJob(string operation, string target, string output)
        {
            BuildJob job = new BuildJob
            {
                JobId = Guid.NewGuid().ToString("N"), Operation = operation,
                Target = target, OutputPath = output, Status = "pending",
                CreatedUtc = DateTime.UtcNow.ToString("o")
            };
            WriteJob(job);
            File.WriteAllText(LatestJobPath, job.JobId);
            return job;
        }

        private static void RecoverInterruptedJobs()
        {
            foreach (string path in Directory.GetFiles(StorageDirectory, "*.json"))
            {
                BuildJob job;
                try { job = JsonUtility.FromJson<BuildJob>(File.ReadAllText(path)); }
                catch { continue; }
                if (job == null || (job.Status != "pending" && job.Status != "building")) continue;
                job.Status = "failed";
                job.Error = "Build job was interrupted by a Unity domain reload or editor restart.";
                job.CompletedUtc = DateTime.UtcNow.ToString("o");
                WriteJob(job);
            }
        }

        private static BuildData ToData(BuildJob job)
        {
            return new BuildData
            {
                JobId = job.JobId, Operation = job.Operation, Status = job.Status,
                Target = job.Target, Platform = job.Platform, OutputPath = job.OutputPath,
                Result = job.Result, Error = job.Error, CreatedUtc = job.CreatedUtc,
                StartedUtc = job.StartedUtc, CompletedUtc = job.CompletedUtc,
                DurationSeconds = job.DurationSeconds, TotalSizeBytes = job.TotalSize,
                Errors = job.Errors, Warnings = job.Warnings, SceneCount = job.SceneCount,
                Total = job.Total, Completed = job.Completed, Passed = job.Passed, Failed = job.Failed,
                CancelRequested = job.CancelRequested
            };
        }

        private static string StorageDirectory
        {
            get
            {
                return Path.Combine(
                    Mcp2019ServerManager.RuntimeDirectory,
                    "build-jobs");
            }
        }
        private static string LatestJobPath { get { return Path.Combine(StorageDirectory, "latest.txt"); } }
        private static string JobPath(string id) { return Path.Combine(StorageDirectory, RequireJobId(id) + ".json"); }
        private static string RequireJobId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length != 32 || id.Any(character => !Uri.IsHexDigit(character)))
                throw new ArgumentException("job_id must be a 32-character hexadecimal identifier.");
            return id.ToLowerInvariant();
        }
        private static void WriteJob(BuildJob job) { Directory.CreateDirectory(StorageDirectory); File.WriteAllText(JobPath(job.JobId), JsonUtility.ToJson(job, true)); }
        private static BuildJob ReadJob(string id)
        {
            string path;
            try { path = JobPath(id); } catch { return null; }
            return File.Exists(path) ? JsonUtility.FromJson<BuildJob>(File.ReadAllText(path)) : null;
        }
        private static string ReadLatestJobId() { return File.Exists(LatestJobPath) ? File.ReadAllText(LatestJobPath).Trim() : string.Empty; }

        private static BuildResult Success(string message, BuildData data)
        { return new BuildResult { Success = true, Message = message, Status = "success", Data = data }; }
        private static BuildResult Pending(string message, BuildData data)
        { return new BuildResult { Success = true, Message = message, Status = "pending", PollIntervalSeconds = 5f, Data = data }; }
        private static BuildResult Fail(string message, BuildData data = null)
        { return new BuildResult { Success = false, Message = message, Status = "error", Data = data }; }

        [Serializable] private sealed class BuildArguments
        {
            public string Action, Target, OutputPath, Subtarget, ScriptingBackend, Profile, Property, Value, OutputDir, JobId;
            public bool Development, HasDevelopment, Activate, HasActivate, HasValue, HasScenes;
            public string[] Options, Targets, Profiles;
            public BuildSceneArgument[] SceneEntries;
        }
        [Serializable] private sealed class BuildSceneArgument { public string Path; public bool Enabled = true; }
        [Serializable] private sealed class BuildResult
        { public bool Success; public string Message, Status; public float PollIntervalSeconds; public BuildData Data; }
        [Serializable] private sealed class BuildData
        {
            public string JobId, Operation, Status, Target, TargetGroup, PreviousTarget, Platform, OutputPath;
            public string Result, Error, Property, Value, CreatedUtc, StartedUtc, CompletedUtc;
            public double DurationSeconds; public ulong TotalSizeBytes;
            public int Errors, Warnings, SceneCount, Total, Completed, Passed, Failed;
            public bool CancelRequested;
            public BuildSceneRecord[] Scenes;
        }
        [Serializable] private sealed class BuildSceneRecord { public string Path, Guid; public bool Enabled; }
        [Serializable] private sealed class BuildJob
        {
            public string JobId, Operation, Status, Target, Platform, OutputPath, Result, Error;
            public string CreatedUtc, StartedUtc, CompletedUtc;
            public double DurationSeconds; public ulong TotalSize;
            public int Errors, Warnings, SceneCount, Total, Completed, Passed, Failed;
            public bool CancelRequested;
        }
    }
}
#endif
