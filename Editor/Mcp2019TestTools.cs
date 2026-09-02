#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace UnityMcp2019
{
    [InitializeOnLoad]
    internal static class Mcp2019TestTools
    {
        private static readonly TestRunnerApi Api;
        private static readonly TestCallbacks Callbacks;

        static Mcp2019TestTools()
        {
            EnsureStorage();
            Api = ScriptableObject.CreateInstance<TestRunnerApi>();
            Api.hideFlags = HideFlags.HideAndDontSave;
            Callbacks = new TestCallbacks();
            Api.RegisterCallbacks(Callbacks, 1000);
        }

        internal static string Execute(string method, string argumentsJson)
        {
            if (method == "run_tests")
                return JsonUtility.ToJson(RunTests(Parse<RunArguments>(argumentsJson)));
            if (method == "get_test_job")
                return JsonUtility.ToJson(GetJob(Parse<GetArguments>(argumentsJson)));
            throw new ArgumentException("Unknown test tool method: " + method);
        }

        private static T Parse<T>(string json) where T : class, new()
        {
            if (string.IsNullOrEmpty(json) || json == "{}") return new T();
            return JsonUtility.FromJson<T>(json) ?? new T();
        }

        private static TestJobResult RunTests(RunArguments arguments)
        {
            string activeId = ReadActiveJobId();
            if (!string.IsNullOrEmpty(activeId))
            {
                TestJobData active = ReadJob(activeId);
                if (active != null && (active.Status == "queued" || active.Status == "running"))
                {
                    if (!arguments.ClearStuck)
                        throw new InvalidOperationException(
                            "A Unity test job is already active: " + activeId +
                            ". Poll it or call run_tests(clear_stuck=true)." );
                    active.Status = "failed";
                    active.Error = "Job was cleared as stuck by a new MCP request.";
                    active.CompletedUtc = DateTime.UtcNow.ToString("o");
                    WriteJob(active);
                    ClearActiveJob();
                }
            }

            TestMode mode;
            if (!Enum.TryParse(string.IsNullOrEmpty(arguments.Mode) ? "EditMode" : arguments.Mode, true, out mode) ||
                (mode != TestMode.EditMode && mode != TestMode.PlayMode))
                throw new ArgumentException("mode must be EditMode or PlayMode.");

            string jobId = Guid.NewGuid().ToString("N");
            TestJobData job = new TestJobData
            {
                JobId = jobId,
                Status = "queued",
                Mode = mode.ToString(),
                CreatedUtc = DateTime.UtcNow.ToString("o"),
                IncludeFailedTests = arguments.IncludeFailedTests,
                IncludeDetails = arguments.IncludeDetails,
                Results = new List<TestCaseResult>()
            };
            WriteJob(job);
            WriteActiveJobId(jobId);
            Filter filter = new Filter
            {
                testMode = mode,
                testNames = EmptyToNull(arguments.TestNames),
                groupNames = EmptyToNull(arguments.GroupNames),
                categoryNames = EmptyToNull(arguments.CategoryNames),
                assemblyNames = EmptyToNull(arguments.AssemblyNames)
            };
            try
            {
                job.UnityJobId = Api.Execute(new ExecutionSettings(filter));
                WriteJob(job);
            }
            catch (Exception exception)
            {
                job.Status = "failed";
                job.Error = exception.Message;
                job.CompletedUtc = DateTime.UtcNow.ToString("o");
                WriteJob(job);
                ClearActiveJob();
                throw;
            }

            return BuildResult(job, false, false);
        }

        private static TestJobResult GetJob(GetArguments arguments)
        {
            string jobId = RequireJobId(arguments.JobId);
            TestJobData job = ReadJob(jobId);
            if (job == null) throw new FileNotFoundException("Unity test job was not found: " + jobId);
            return BuildResult(job, arguments.IncludeFailedTests, arguments.IncludeDetails);
        }

        private static TestJobResult BuildResult(
            TestJobData job,
            bool includeFailed,
            bool includeDetails)
        {
            IEnumerable<TestCaseResult> results = job.Results ?? new List<TestCaseResult>();
            bool details = includeDetails || job.IncludeDetails;
            bool failedOnly = includeFailed || job.IncludeFailedTests;
            if (details)
            {
                // include_details has precedence and returns every leaf result,
                // matching CoplayDev's public tool contract.
            }
            else if (failedOnly)
            {
                results = results.Where(item => item.Status != "Passed");
            }
            else
            {
                results = Enumerable.Empty<TestCaseResult>();
            }

            return new TestJobResult
            {
                Success = job.Status != "failed",
                JobId = job.JobId,
                UnityJobId = job.UnityJobId,
                Status = job.Status,
                Mode = job.Mode,
                CreatedUtc = job.CreatedUtc,
                StartedUtc = job.StartedUtc,
                CompletedUtc = job.CompletedUtc,
                Total = job.Total,
                Passed = job.Passed,
                Failed = job.Failed,
                Skipped = job.Skipped,
                Inconclusive = job.Inconclusive,
                Duration = job.Duration,
                Error = job.Error,
                Results = results.ToArray()
            };
        }

        private sealed class TestCallbacks : ICallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun)
            {
                UpdateActive(delegate(TestJobData job)
                {
                    job.Status = "running";
                    job.StartedUtc = DateTime.UtcNow.ToString("o");
                    job.Total = testsToRun == null ? 0 : testsToRun.TestCaseCount;
                });
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                UpdateActive(delegate(TestJobData job)
                {
                    job.Status = result != null && result.FailCount > 0 ? "failed" : "complete";
                    job.CompletedUtc = DateTime.UtcNow.ToString("o");
                    if (result != null)
                    {
                        job.Total = result.PassCount + result.FailCount + result.SkipCount + result.InconclusiveCount;
                        job.Passed = result.PassCount;
                        job.Failed = result.FailCount;
                        job.Skipped = result.SkipCount;
                        job.Inconclusive = result.InconclusiveCount;
                        job.Duration = result.Duration;
                        job.Error = result.Message ?? string.Empty;
                        if (job.Results == null || job.Results.Count == 0)
                        {
                            job.Results = new List<TestCaseResult>();
                            CollectLeafResults(result, job.Results);
                        }
                    }
                });
                ClearActiveJob();
            }

            public void TestStarted(ITestAdaptor test)
            {
            }

            public void TestFinished(ITestResultAdaptor result)
            {
                if (result == null || result.Test == null || result.Test.IsSuite) return;
                UpdateActive(delegate(TestJobData job)
                {
                    if (job.Results == null) job.Results = new List<TestCaseResult>();
                    job.Results.Add(new TestCaseResult
                    { Name = result.Name, FullName = result.FullName,
                      Status = result.TestStatus.ToString(), ResultState = result.ResultState,
                      Duration = result.Duration, Message = result.Message ?? string.Empty,
                      StackTrace = result.StackTrace ?? string.Empty, Output = result.Output ?? string.Empty });
                });
            }

            private static void CollectLeafResults(
                ITestResultAdaptor result,
                List<TestCaseResult> output)
            {
                if (result == null) return;
                ITestResultAdaptor[] children = result.Children == null
                    ? new ITestResultAdaptor[0]
                    : result.Children.Where(child => child != null).ToArray();
                if (!result.HasChildren || children.Length == 0)
                {
                    output.Add(new TestCaseResult
                    {
                        Name = result.Name,
                        FullName = result.FullName,
                        Status = result.TestStatus.ToString(),
                        ResultState = result.ResultState,
                        Duration = result.Duration,
                        Message = result.Message ?? string.Empty,
                        StackTrace = result.StackTrace ?? string.Empty,
                        Output = result.Output ?? string.Empty
                    });
                    return;
                }

                foreach (ITestResultAdaptor child in children)
                    CollectLeafResults(child, output);
            }
        }

        private static void UpdateActive(Action<TestJobData> update)
        {
            string jobId = ReadActiveJobId();
            if (string.IsNullOrEmpty(jobId)) return;
            TestJobData job = ReadJob(jobId);
            if (job == null) return;
            update(job);
            WriteJob(job);
        }

        private static string[] EmptyToNull(string[] values)
        {
            return values == null || values.Length == 0 ? null : values;
        }

        private static string RequireJobId(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 32 ||
                value.Any(character => !Uri.IsHexDigit(character)))
                throw new ArgumentException("job_id must be a 32-character hexadecimal identifier.");
            return value.ToLowerInvariant();
        }

        private static string StorageDirectory
        {
            get
            {
                return Path.Combine(
                    Mcp2019ServerManager.RuntimeDirectory,
                    "test-jobs");
            }
        }

        private static string ActiveJobPath { get { return Path.Combine(StorageDirectory, "active.txt"); } }

        private static void EnsureStorage()
        {
            Directory.CreateDirectory(StorageDirectory);
        }

        private static string JobPath(string jobId)
        {
            return Path.Combine(StorageDirectory, RequireJobId(jobId) + ".json");
        }

        private static void WriteJob(TestJobData job)
        {
            EnsureStorage();
            File.WriteAllText(JobPath(job.JobId), JsonUtility.ToJson(job, true));
        }

        private static TestJobData ReadJob(string jobId)
        {
            string path = JobPath(jobId);
            return File.Exists(path)
                ? JsonUtility.FromJson<TestJobData>(File.ReadAllText(path))
                : null;
        }

        private static void WriteActiveJobId(string jobId)
        {
            EnsureStorage();
            File.WriteAllText(ActiveJobPath, RequireJobId(jobId));
        }

        private static string ReadActiveJobId()
        {
            if (!File.Exists(ActiveJobPath)) return string.Empty;
            string value = File.ReadAllText(ActiveJobPath).Trim();
            try { return RequireJobId(value); }
            catch { return string.Empty; }
        }

        private static void ClearActiveJob()
        {
            if (File.Exists(ActiveJobPath)) File.Delete(ActiveJobPath);
        }

        [Serializable] private sealed class RunArguments
        {
            public string Mode = "EditMode";
            public string[] TestNames; public string[] GroupNames;
            public string[] CategoryNames; public string[] AssemblyNames;
            public bool IncludeFailedTests; public bool IncludeDetails; public bool ClearStuck;
        }

        [Serializable] private sealed class GetArguments
        {
            public string JobId; public bool IncludeFailedTests; public bool IncludeDetails;
        }

        [Serializable] private sealed class TestJobData
        {
            public string JobId; public string UnityJobId; public string Status; public string Mode;
            public string CreatedUtc; public string StartedUtc; public string CompletedUtc;
            public int Total; public int Passed; public int Failed; public int Skipped; public int Inconclusive;
            public double Duration; public string Error; public bool IncludeFailedTests; public bool IncludeDetails;
            public List<TestCaseResult> Results;
        }

        [Serializable] private sealed class TestJobResult
        {
            public bool Success; public string JobId; public string UnityJobId; public string Status; public string Mode;
            public string CreatedUtc; public string StartedUtc; public string CompletedUtc;
            public int Total; public int Passed; public int Failed; public int Skipped; public int Inconclusive;
            public double Duration; public string Error; public TestCaseResult[] Results;
        }

        [Serializable] private sealed class TestCaseResult
        {
            public string Name; public string FullName; public string Status; public string ResultState;
            public double Duration; public string Message; public string StackTrace; public string Output;
        }
    }
}
#endif
