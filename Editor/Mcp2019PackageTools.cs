#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace UnityMcp2019
{
    [InitializeOnLoad]
    internal static class Mcp2019PackageTools
    {
        private static readonly Dictionary<string, Request> Requests = new Dictionary<string, Request>();
        private static readonly Dictionary<string, ListRequest> ListRequests = new Dictionary<string, ListRequest>();
        private static readonly Dictionary<string, SearchRequest> SearchRequests = new Dictionary<string, SearchRequest>();

        static Mcp2019PackageTools()
        {
            EnsureStorage();
            EditorApplication.update -= UpdateRequests;
            EditorApplication.update += UpdateRequests;
            RecoverJobsAfterReload();
        }

        internal static string Execute(string argumentsJson)
        {
            PackageArguments arguments = string.IsNullOrEmpty(argumentsJson) || argumentsJson == "{}"
                ? new PackageArguments()
                : JsonUtility.FromJson<PackageArguments>(argumentsJson) ?? new PackageArguments();
            string action = (arguments.Action ?? string.Empty).Trim().ToLowerInvariant();
            try
            {
                PackageResult result;
                switch (action)
                {
                    case "ping": result = Ping(); break;
                    case "list_packages": result = StartList(); break;
                    case "search_packages": result = StartSearch(arguments.Query); break;
                    case "get_package_info": result = GetPackageInfo(arguments.Package); break;
                    case "add_package": result = StartRequest("add", arguments.Package, Client.Add(arguments.Package)); break;
                    case "remove_package": result = StartRemove(arguments.Package, arguments.Force); break;
                    case "embed_package": result = StartRequest("embed", arguments.Package, Client.Embed(arguments.Package)); break;
                    case "resolve_packages": result = ResolvePackages(); break;
                    case "status": result = GetStatus(arguments.JobId); break;
                    default: result = Fail("Unknown package action: " + action); break;
                }
                return JsonUtility.ToJson(result);
            }
            catch (Exception exception)
            {
                return JsonUtility.ToJson(Fail(exception.GetType().Name + ": " + exception.Message));
            }
        }

        private static PackageResult Ping()
        {
            return Success("Unity Package Manager is available.", new PackageData
            {
                UnityVersion = Application.unityVersion
            });
        }

        private static PackageResult StartList()
        {
            string id = CreateJob("list_packages", string.Empty);
            ListRequests[id] = Client.List();
            return Pending("Listing installed packages.", id, "list_packages");
        }

        private static PackageResult StartSearch(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return Fail("query is required for search_packages.");
            string id = CreateJob("search_packages", query.Trim());
            SearchRequests[id] = Client.Search(query.Trim());
            return Pending("Searching packages.", id, "search_packages");
        }

        private static PackageResult StartRemove(string package, bool force)
        {
            if (string.IsNullOrWhiteSpace(package)) return Fail("package is required for remove_package.");
            if (!force)
            {
                return Fail("Dependency verification is required before removal; call through the MCP server or set force=true.");
            }
            return StartRequest("remove", package, Client.Remove(package));
        }

        private static PackageResult StartRequest(string operation, string package, Request request)
        {
            if (string.IsNullOrWhiteSpace(package)) return Fail("package is required for " + operation + ".");
            string id = CreateJob(operation, package.Trim());
            Requests[id] = request;
            return Pending("Package " + operation + " started.", id, operation);
        }

        private static PackageResult ResolvePackages()
        {
            AssetDatabase.Refresh();
            return Success("Package resolution requested.", new PackageData { Operation = "resolve_packages" });
        }

        private static PackageResult GetPackageInfo(string package)
        {
            if (string.IsNullOrWhiteSpace(package)) return Fail("package is required for get_package_info.");
            PackageInfo info = PackageInfo.FindForAssetPath("Packages/" + package.Trim());
            if (info == null) return Fail("Package '" + package + "' is not installed.");
            return Success("Package information read.", new PackageData
            {
                Packages = new[] { ToRecord(info, true) }, Count = 1
            });
        }

        private static PackageResult GetStatus(string requestedId)
        {
            string id = string.IsNullOrWhiteSpace(requestedId) ? ReadLatestJobId() : requestedId.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(id)) return Success("No package jobs found.", new PackageData());
            PackageJob job = ReadJob(id);
            if (job == null) return Fail("No package job found with ID '" + id + "'.");
            if (ListRequests.ContainsKey(id)) CompleteList(id, ListRequests[id]);
            else if (SearchRequests.ContainsKey(id)) CompleteSearch(id, SearchRequests[id]);
            else if (Requests.ContainsKey(id)) CompleteRequest(id, Requests[id]);
            job = ReadJob(id) ?? job;
            PackageData data = new PackageData
            {
                JobId = job.JobId, Operation = job.Operation, Package = job.Package,
                Status = job.Status, Error = job.Error, StartedUtc = job.StartedUtc,
                CompletedUtc = job.CompletedUtc, Packages = job.Packages ?? new PackageRecord[0],
                Count = job.Packages == null ? 0 : job.Packages.Length
            };
            return job.Status == "running"
                ? Pending("Package job is still running.", data)
                : job.Status == "succeeded"
                    ? Success("Package job succeeded.", data)
                    : Fail("Package job failed: " + job.Error, data);
        }

        private static void UpdateRequests()
        {
            foreach (KeyValuePair<string, ListRequest> pair in ListRequests.ToArray()) CompleteList(pair.Key, pair.Value);
            foreach (KeyValuePair<string, SearchRequest> pair in SearchRequests.ToArray()) CompleteSearch(pair.Key, pair.Value);
            foreach (KeyValuePair<string, Request> pair in Requests.ToArray()) CompleteRequest(pair.Key, pair.Value);
        }

        private static void CompleteList(string id, ListRequest request)
        {
            if (request == null || !request.IsCompleted) return;
            PackageJob job = ReadJob(id);
            if (job != null)
            {
                if (request.Status == StatusCode.Success)
                {
                    job.Status = "succeeded";
                    job.Packages = request.Result.Select(item => ToRecord(item, false)).ToArray();
                }
                else FailJob(job, request.Error == null ? "Unknown list error." : request.Error.message);
                FinishJob(job);
            }
            ListRequests.Remove(id);
        }

        private static void CompleteSearch(string id, SearchRequest request)
        {
            if (request == null || !request.IsCompleted) return;
            PackageJob job = ReadJob(id);
            if (job != null)
            {
                if (request.Status == StatusCode.Success)
                {
                    job.Status = "succeeded";
                    job.Packages = request.Result.Select(item => ToRecord(item, false)).ToArray();
                }
                else FailJob(job, request.Error == null ? "Unknown search error." : request.Error.message);
                FinishJob(job);
            }
            SearchRequests.Remove(id);
        }

        private static void CompleteRequest(string id, Request request)
        {
            if (request == null || !request.IsCompleted) return;
            PackageJob job = ReadJob(id);
            if (job != null)
            {
                if (request.Status == StatusCode.Success)
                {
                    job.Status = "succeeded";
                    PackageInfo installed = FindInstalledPackage(job.Package);
                    job.Packages = installed == null ? new PackageRecord[0] : new[] { ToRecord(installed, true) };
                }
                else FailJob(job, request.Error == null ? "Unknown package request error." : request.Error.message);
                FinishJob(job);
            }
            Requests.Remove(id);
        }

        private static void RecoverJobsAfterReload()
        {
            foreach (string file in Directory.GetFiles(StorageDirectory, "*.json"))
            {
                PackageJob job;
                try { job = JsonUtility.FromJson<PackageJob>(File.ReadAllText(file)); }
                catch { continue; }
                if (job == null || job.Status != "running") continue;
                if (job.Operation == "list_packages" || job.Operation == "search_packages")
                {
                    FailJob(job, "Package query was interrupted by a Unity domain reload; run it again.");
                    FinishJob(job);
                    continue;
                }
                PackageInfo installed = FindInstalledPackage(job.Package);
                bool success = (job.Operation == "remove" && installed == null) ||
                    ((job.Operation == "add" || job.Operation == "embed") && installed != null);
                if (success)
                {
                    job.Status = "succeeded";
                    job.Packages = installed == null ? new PackageRecord[0] : new[] { ToRecord(installed, true) };
                }
                else FailJob(job, "Package operation state could not be recovered after domain reload.");
                FinishJob(job);
            }
        }

        private static PackageInfo FindInstalledPackage(string identifier)
        {
            string name = identifier ?? string.Empty;
            if (name.StartsWith("com.", StringComparison.OrdinalIgnoreCase) && name.Contains("@"))
                name = name.Substring(0, name.IndexOf('@'));
            if (!name.StartsWith("com.", StringComparison.OrdinalIgnoreCase)) return null;
            return PackageInfo.FindForAssetPath("Packages/" + name);
        }

        private static PackageRecord ToRecord(PackageInfo info, bool dependencies)
        {
            return new PackageRecord
            {
                Name = info.name, Version = info.version, DisplayName = info.displayName,
                Description = info.description, Source = info.source.ToString(),
                ResolvedPath = info.resolvedPath,
                Dependencies = dependencies && info.dependencies != null
                    ? info.dependencies.Select(item => new DependencyRecord { Name = item.name, Version = item.version }).ToArray()
                    : new DependencyRecord[0]
            };
        }

        private static string CreateJob(string operation, string package)
        {
            PackageJob job = new PackageJob
            {
                JobId = Guid.NewGuid().ToString("N"), Operation = operation,
                Package = package, Status = "running", StartedUtc = DateTime.UtcNow.ToString("o"),
                Packages = new PackageRecord[0]
            };
            WriteJob(job);
            File.WriteAllText(LatestJobPath, job.JobId);
            return job.JobId;
        }

        private static void FinishJob(PackageJob job)
        {
            job.CompletedUtc = DateTime.UtcNow.ToString("o");
            WriteJob(job);
        }

        private static void FailJob(PackageJob job, string error)
        {
            job.Status = "failed";
            job.Error = error ?? "Unknown error.";
        }

        private static string StorageDirectory
        {
            get
            {
                return Path.Combine(
                    Mcp2019ServerManager.RuntimeDirectory,
                    "package-jobs");
            }
        }
        private static string LatestJobPath { get { return Path.Combine(StorageDirectory, "latest.txt"); } }
        private static void EnsureStorage() { Directory.CreateDirectory(StorageDirectory); }
        private static string JobPath(string id) { return Path.Combine(StorageDirectory, RequireJobId(id) + ".json"); }
        private static string RequireJobId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length != 32 || id.Any(character => !Uri.IsHexDigit(character)))
                throw new ArgumentException("job_id must be a 32-character hexadecimal identifier.");
            return id.ToLowerInvariant();
        }
        private static void WriteJob(PackageJob job) { EnsureStorage(); File.WriteAllText(JobPath(job.JobId), JsonUtility.ToJson(job, true)); }
        private static PackageJob ReadJob(string id)
        {
            string path;
            try { path = JobPath(id); } catch { return null; }
            return File.Exists(path) ? JsonUtility.FromJson<PackageJob>(File.ReadAllText(path)) : null;
        }
        private static string ReadLatestJobId() { return File.Exists(LatestJobPath) ? File.ReadAllText(LatestJobPath).Trim() : string.Empty; }

        private static PackageResult Success(string message, PackageData data)
        { return new PackageResult { Success = true, Message = message, Status = "success", Data = data }; }
        private static PackageResult Pending(string message, string id, string operation)
        { return Pending(message, new PackageData { JobId = id, Operation = operation, Status = "running" }); }
        private static PackageResult Pending(string message, PackageData data)
        { return new PackageResult { Success = true, Message = message, Status = "pending", PollIntervalSeconds = 1f, Data = data }; }
        private static PackageResult Fail(string message, PackageData data = null)
        { return new PackageResult { Success = false, Message = message, Status = "error", Data = data }; }

        [Serializable] private sealed class PackageArguments
        { public string Action, Package, Query, JobId; public bool Force; }
        [Serializable] private sealed class PackageResult
        { public bool Success; public string Message, Status; public float PollIntervalSeconds; public PackageData Data; }
        [Serializable] private sealed class PackageData
        {
            public string JobId, Operation, Package, Status, Error, StartedUtc, CompletedUtc, UnityVersion;
            public int Count; public PackageRecord[] Packages;
        }
        [Serializable] private sealed class PackageJob
        {
            public string JobId, Operation, Package, Status, Error, StartedUtc, CompletedUtc;
            public PackageRecord[] Packages;
        }
        [Serializable] private sealed class PackageRecord
        {
            public string Name, Version, DisplayName, Description, Source, ResolvedPath;
            public DependencyRecord[] Dependencies;
        }
        [Serializable] private sealed class DependencyRecord
        { public string Name, Version; }
    }
}
#endif
