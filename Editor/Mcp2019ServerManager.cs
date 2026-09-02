#if UNITY_EDITOR
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;

namespace UnityMcp2019
{
    [InitializeOnLoad]
    internal static class Mcp2019ServerManager
    {
        internal const string PackageName = "com.ibelongedtoyou.unity-mcp-2019";
        internal const string CodexServerName = "unity-mcp-2019-http";
        internal const string ClaudeServerName = "UnityMCP";
        internal const string RuntimeDirectoryName = "UnityMcp2019";
        internal const string ToolsMenuRoot = "Tools/MCP for Unity 2019/";

        private const string PythonPreference = "UnityMcp2019.PythonExecutable";
        private const string CodexPreference = "UnityMcp2019.CodexExecutable";
        private const string ClaudePreference = "UnityMcp2019.ClaudeExecutable";
        private const string HttpPortPreference = "UnityMcp2019.HttpPort";
        private const int DefaultHttpPort = 6500;

        private static readonly ConcurrentQueue<Action> MainThreadActions =
            new ConcurrentQueue<Action>();

        static Mcp2019ServerManager()
        {
            EditorApplication.update += DispatchMainThreadActions;
            EditorApplication.quitting += StopServerOnEditorQuit;
        }

        internal static string ProjectRoot
        {
            get { return Directory.GetParent(Application.dataPath).FullName; }
        }

        internal static string WorkspaceRoot
        {
            get { return ProjectRoot; }
        }

        internal static string PackageRoot
        {
            get
            {
                UnityEditor.PackageManager.PackageInfo package =
                    UnityEditor.PackageManager.PackageInfo.FindForAssetPath(
                        "Packages/" + PackageName);
                if (package != null &&
                    string.Equals(package.name, PackageName, StringComparison.Ordinal))
                {
                    return package.resolvedPath;
                }

                return Path.Combine(ProjectRoot, "Packages", PackageName);
            }
        }

        internal static string ServerScriptPath
        {
            get
            {
                return Path.Combine(PackageRoot, "Tools~", "server.py");
            }
        }

        internal static string RuntimeDirectory
        {
            get { return Path.Combine(ProjectRoot, "Library", RuntimeDirectoryName); }
        }

        internal static string MetadataPath
        {
            get { return Path.Combine(RuntimeDirectory, "http-server.json"); }
        }

        internal static string LogPath
        {
            get { return Path.Combine(RuntimeDirectory, "http-server.log"); }
        }

        internal static string PythonExecutable
        {
            get { return EditorPrefs.GetString(PythonPreference, "python"); }
            set { EditorPrefs.SetString(PythonPreference, value); }
        }

        internal static string CodexExecutable
        {
            get { return EditorPrefs.GetString(CodexPreference, "codex"); }
            set { EditorPrefs.SetString(CodexPreference, value); }
        }

        internal static string ClaudeExecutable
        {
            get
            {
                string defaultExecutable =
                    Application.platform == RuntimePlatform.WindowsEditor
                        ? "claude.cmd"
                        : "claude";
                return EditorPrefs.GetString(ClaudePreference, defaultExecutable);
            }
            set { EditorPrefs.SetString(ClaudePreference, value); }
        }

        internal static string ClaudeDesktopConfigPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Claude",
                    "claude_desktop_config.json");
            }
        }

        internal static int HttpPort
        {
            get { return EditorPrefs.GetInt(HttpPortPreference, DefaultHttpPort); }
            set { EditorPrefs.SetInt(HttpPortPreference, Mathf.Clamp(value, 1024, 65535)); }
        }

        internal static string ConfiguredMcpUrl
        {
            get { return "http://127.0.0.1:" + HttpPort + "/mcp"; }
        }

        internal static HttpServerStatus GetStatus()
        {
            HttpServerMetadata metadata;
            string metadataError;
            if (!TryReadMetadata(out metadata, out metadataError))
            {
                return new HttpServerStatus(false, metadataError, null);
            }

            if (!PathsEqual(metadata.ProjectPath, ProjectRoot))
            {
                return new HttpServerStatus(
                    false,
                    "Runtime metadata belongs to another Unity project.",
                    metadata);
            }

            try
            {
                using (Process process = Process.GetProcessById(metadata.Pid))
                {
                    if (!process.HasExited)
                    {
                        return new HttpServerStatus(
                            true,
                            "Running (PID " + metadata.Pid + ")",
                            metadata);
                    }
                }
            }
            catch (ArgumentException)
            {
            }
            catch (InvalidOperationException)
            {
            }

            return new HttpServerStatus(false, "Stopped (stale runtime metadata).", metadata);
        }

        internal static CommandResult StartHttpServer()
        {
            HttpServerStatus status = GetStatus();
            if (status.Running)
            {
                return CommandResult.Success("HTTP MCP server is already running at " + status.Metadata.Url);
            }
            if (!File.Exists(ServerScriptPath))
            {
                return CommandResult.Failure("MCP server script was not found: " + ServerScriptPath);
            }
            if (string.IsNullOrWhiteSpace(PythonExecutable))
            {
                return CommandResult.Failure("Python executable is empty.");
            }

            try
            {
                Directory.CreateDirectory(RuntimeDirectory);
                if (status.Metadata != null && PathsEqual(status.Metadata.ProjectPath, ProjectRoot))
                {
                    File.Delete(MetadataPath);
                }

                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = PythonExecutable.Trim();
                startInfo.Arguments =
                    Quote(ServerScriptPath) +
                    " --project " + Quote(ProjectRoot) +
                    " --transport http" +
                    " --http-host 127.0.0.1" +
                    " --http-port " + HttpPort +
                    " --http-metadata " + Quote(MetadataPath) +
                    " --http-log " + Quote(LogPath);
                startInfo.WorkingDirectory = WorkspaceRoot;
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;
                startInfo.WindowStyle = ProcessWindowStyle.Hidden;

                using (Process process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        return CommandResult.Failure("Python process did not start.");
                    }
                    return CommandResult.Success(
                        "Started HTTP MCP process " + process.Id +
                        ". Waiting for " + ConfiguredMcpUrl + ".");
                }
            }
            catch (Exception exception)
            {
                return CommandResult.Failure("Could not start HTTP MCP server: " + exception.Message);
            }
        }

        internal static void BeginStopHttpServer(Action<CommandResult> completed)
        {
            HttpServerStatus status = GetStatus();
            if (!status.Running || status.Metadata == null)
            {
                if (status.Metadata != null && PathsEqual(status.Metadata.ProjectPath, ProjectRoot))
                {
                    TryDeleteMetadata();
                }
                completed(CommandResult.Success("HTTP MCP server is already stopped."));
                return;
            }

            HttpServerMetadata metadata = status.Metadata;
            BeginBackgroundOperation(
                delegate { return RequestServerShutdown(metadata); },
                completed);
        }

        internal static void BeginRestartHttpServer(Action<CommandResult> completed)
        {
            BeginStopHttpServer(
                delegate(CommandResult stopResult)
                {
                    if (!stopResult.Ok)
                    {
                        completed(stopResult);
                        return;
                    }

                    double deadline = EditorApplication.timeSinceStartup + 3.0;
                    EditorApplication.CallbackFunction waitForStop = null;
                    waitForStop = delegate
                    {
                        if (GetStatus().Running && EditorApplication.timeSinceStartup < deadline)
                        {
                            return;
                        }
                        EditorApplication.update -= waitForStop;
                        CommandResult startResult = StartHttpServer();
                        completed(CommandResult.Combine(stopResult, startResult));
                    };
                    EditorApplication.update += waitForStop;
                });
        }

        internal static void BeginProtocolSelfTest(Action<CommandResult> completed)
        {
            string executable = PythonExecutable.Trim();
            string arguments =
                Quote(ServerScriptPath) + " --project " + Quote(ProjectRoot) + " --self-test";
            BeginBackgroundOperation(
                delegate { return RunProcess(executable, arguments, WorkspaceRoot, 15000); },
                completed);
        }

        internal static void BeginBridgeSmokeTest(Action<CommandResult> completed)
        {
            string executable = PythonExecutable.Trim();
            string arguments =
                Quote(ServerScriptPath) + " --project " + Quote(ProjectRoot) +
                " --timeout 5 --bridge-smoke";
            BeginBackgroundOperation(
                delegate { return RunProcess(executable, arguments, WorkspaceRoot, 10000); },
                completed);
        }

        internal static void BeginHttpHealthCheck(Action<CommandResult> completed)
        {
            HttpServerStatus status = GetStatus();
            string healthUrl =
                status.Metadata != null && !string.IsNullOrEmpty(status.Metadata.HealthUrl)
                    ? status.Metadata.HealthUrl
                    : "http://127.0.0.1:" + HttpPort + "/health";
            BeginBackgroundOperation(
                delegate { return RequestHealth(healthUrl); },
                completed);
        }

        internal static void BeginConfigureCodex(Action<CommandResult> completed)
        {
            string executable = CodexExecutable.Trim();
            string url = ConfiguredMcpUrl;
            string workingDirectory = WorkspaceRoot;
            BeginBackgroundOperation(
                delegate { return ConfigureCodex(executable, url, workingDirectory); },
                completed);
        }

        internal static string GetCodexAddCommand()
        {
            return "codex mcp add " + CodexServerName + " --url " + ConfiguredMcpUrl;
        }

        internal static void BeginCheckCodex(Action<CommandResult> completed)
        {
            BeginClientCommand(
                CodexExecutable,
                "mcp get " + CodexServerName,
                WorkspaceRoot,
                completed);
        }

        internal static void BeginRemoveCodex(Action<CommandResult> completed)
        {
            BeginClientCommand(
                CodexExecutable,
                "mcp remove " + CodexServerName,
                WorkspaceRoot,
                completed);
        }

        internal static void BeginConfigureClaudeCode(Action<CommandResult> completed)
        {
            string executable = ClaudeExecutable.Trim();
            string url = ConfiguredMcpUrl;
            BeginBackgroundOperation(
                delegate { return ConfigureClaudeCode(executable, url); },
                completed);
        }

        internal static void BeginCheckClaudeCode(Action<CommandResult> completed)
        {
            BeginClientCommand(
                ClaudeExecutable,
                "mcp get " + ClaudeServerName,
                ProjectRoot,
                completed);
        }

        internal static void BeginRemoveClaudeCode(Action<CommandResult> completed)
        {
            BeginClientCommand(
                ClaudeExecutable,
                "mcp remove --scope local " + ClaudeServerName,
                ProjectRoot,
                completed);
        }

        internal static string GetClaudeCodeAddCommand()
        {
            return "claude mcp add --scope local --transport http " +
                ClaudeServerName + " " + ConfiguredMcpUrl;
        }

        internal static void BeginConfigureClaudeDesktop(Action<CommandResult> completed)
        {
            BeginClaudeDesktopAction("configure", completed);
        }

        internal static void BeginCheckClaudeDesktop(Action<CommandResult> completed)
        {
            BeginClaudeDesktopAction("status", completed);
        }

        internal static void BeginRemoveClaudeDesktop(Action<CommandResult> completed)
        {
            BeginClaudeDesktopAction("remove", completed);
        }

        internal static string GetClaudeDesktopSnippet()
        {
            string python = EscapeJson(PythonExecutable.Trim());
            string script = EscapeJson(ServerScriptPath);
            string project = EscapeJson(ProjectRoot);
            return "{\n" +
                "  \"mcpServers\": {\n" +
                "    \"" + ClaudeServerName + "\": {\n" +
                "      \"command\": \"" + python + "\",\n" +
                "      \"args\": [\"" + script + "\", \"--project\", \"" +
                project + "\"]\n" +
                "    }\n" +
                "  }\n" +
                "}";
        }

        internal static string GetClientEntryName(string client)
        {
            return client == "Codex" ? CodexServerName : ClaudeServerName;
        }

        internal static string GetClientTransport(string client)
        {
            return client == "Claude Desktop" ? "stdio" : "Streamable HTTP";
        }

        internal static string GetClientTarget(string client)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            switch (client)
            {
                case "Claude Desktop":
                    return ClaudeDesktopConfigPath;
                case "Claude Code":
                    return "Project-local Claude Code configuration";
                case "Codex":
                    return Path.Combine(home, ".codex", "config.toml");
                case "Antigravity":
                    return Path.Combine(home, ".gemini", "config", "mcp_config.json");
                case "Cline":
                    return Path.Combine(appData, "Code", "User", "globalStorage",
                        "saoudrizwan.claude-dev", "settings", "cline_mcp_settings.json");
                case "Cursor":
                    return Path.Combine(home, ".cursor", "mcp.json");
                case "Gemini CLI":
                    return Path.Combine(home, ".gemini", "settings.json");
                case "GitHub Copilot CLI":
                    return Path.Combine(home, ".copilot", "mcp-config.json");
                case "OpenClaw":
                    return Path.Combine(home, ".openclaw", "openclaw.json");
                case "Qwen Code":
                    return Path.Combine(home, ".qwen", "settings.json");
                case "VS Code Copilot":
                    return Path.Combine(appData, "Code", "User", "mcp.json");
                case "Windsurf":
                    return Path.Combine(home, ".codeium", "windsurf", "mcp_config.json");
                default:
                    return ConfiguredMcpUrl;
            }
        }

        internal static string GetClientRegistration(string client)
        {
            if (client == "Codex") return GetCodexAddCommand();
            if (client == "Claude Code") return GetClaudeCodeAddCommand();
            if (client == "Claude Desktop") return GetClaudeDesktopSnippet();

            string urlProperty = client == "Antigravity" || client == "Windsurf"
                ? "serverUrl"
                : client == "Gemini CLI" ? "httpUrl" : "url";
            string type = client == "Cline" ? "streamableHttp" : "http";
            string container = client == "VS Code Copilot" ? "servers" : "mcpServers";
            string extra = client == "Cline"
                ? ",\n      \"disabled\": false,\n      \"autoApprove\": []"
                : client == "Antigravity" || client == "Windsurf"
                    ? ",\n      \"disabled\": false"
                    : string.Empty;
            if (client == "OpenClaw")
            {
                return "{\n  \"plugins\": {\n    \"entries\": {\n" +
                    "      \"openclaw-mcp-bridge\": {\n        \"enabled\": true,\n" +
                    "        \"config\": {\n          \"servers\": {\n" +
                    "            \"unityMCP\": {\n              \"enabled\": true,\n" +
                    "              \"url\": \"" + EscapeJson(ConfiguredMcpUrl) + "\",\n" +
                    "              \"transport\": \"http\",\n" +
                    "              \"toolPrefix\": \"unityMCP\",\n" +
                    "              \"requestTimeoutMs\": 30000\n" +
                    "            }\n          }\n        }\n      }\n    }\n  }\n}";
            }
            return "{\n  \"" + container + "\": {\n    \"unityMCP\": {\n" +
                "      \"" + urlProperty + "\": \"" + EscapeJson(ConfiguredMcpUrl) + "\",\n" +
                "      \"type\": \"" + type + "\"" + extra + "\n" +
                "    }\n  }\n}";
        }

        internal static void BeginConfigureClient(
            string client,
            Action<CommandResult> completed)
        {
            if (client == "Codex") BeginConfigureCodex(completed);
            else if (client == "Claude Code") BeginConfigureClaudeCode(completed);
            else if (client == "Claude Desktop") BeginConfigureClaudeDesktop(completed);
            else BeginJsonClientAction(client, "configure", completed);
        }

        internal static void BeginCheckClient(
            string client,
            Action<CommandResult> completed)
        {
            if (client == "Codex") BeginCheckCodex(completed);
            else if (client == "Claude Code") BeginCheckClaudeCode(completed);
            else if (client == "Claude Desktop") BeginCheckClaudeDesktop(completed);
            else BeginJsonClientAction(client, "status", completed);
        }

        internal static void BeginRemoveClient(
            string client,
            Action<CommandResult> completed)
        {
            if (client == "Codex") BeginRemoveCodex(completed);
            else if (client == "Claude Code") BeginRemoveClaudeCode(completed);
            else if (client == "Claude Desktop") BeginRemoveClaudeDesktop(completed);
            else BeginJsonClientAction(client, "remove", completed);
        }

        private static void BeginJsonClientAction(
            string client,
            string action,
            Action<CommandResult> completed)
        {
            string executable = PythonExecutable.Trim();
            string arguments =
                Quote(ServerScriptPath) +
                " --project " + Quote(ProjectRoot) +
                " --client-action " + action +
                " --client-name " + Quote(client) +
                " --client-url " + Quote(ConfiguredMcpUrl);
            BeginBackgroundOperation(
                delegate { return RunProcess(executable, arguments, WorkspaceRoot, 10000); },
                completed);
        }

        private static CommandResult ConfigureCodex(
            string executable,
            string url,
            string workingDirectory)
        {
            if (string.IsNullOrWhiteSpace(executable))
            {
                return CommandResult.Failure("Codex executable is empty.");
            }

            CommandResult existing = RunProcess(
                executable,
                "mcp get " + CodexServerName,
                workingDirectory,
                10000);
            if (existing.Ok && existing.Output.IndexOf(url, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return CommandResult.Success(
                    "Codex MCP entry is already configured.\n" + existing.Output);
            }

            StringBuilder transcript = new StringBuilder();
            if (existing.Ok)
            {
                CommandResult removed = RunProcess(
                    executable,
                    "mcp remove " + CodexServerName,
                    workingDirectory,
                    10000);
                transcript.AppendLine(removed.Output);
                if (!removed.Ok)
                {
                    return CommandResult.Failure(
                        "Existing Codex MCP entry could not be replaced.\n" + transcript);
                }
            }

            CommandResult added = RunProcess(
                executable,
                "mcp add " + CodexServerName + " --url " + Quote(url),
                workingDirectory,
                10000);
            transcript.AppendLine(added.Output);
            return added.Ok
                ? CommandResult.Success("Codex MCP entry configured.\n" + transcript)
                : CommandResult.Failure("Codex MCP entry could not be configured.\n" + transcript);
        }

        private static CommandResult ConfigureClaudeCode(string executable, string url)
        {
            if (string.IsNullOrWhiteSpace(executable))
            {
                return CommandResult.Failure("Claude executable is empty.");
            }

            CommandResult existing = RunProcess(
                executable,
                "mcp get " + ClaudeServerName,
                ProjectRoot,
                10000);
            if (existing.Ok && existing.Output.IndexOf(url, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return CommandResult.Success(
                    "Claude Code project-local MCP entry is already configured.\n" +
                    existing.Output);
            }

            StringBuilder transcript = new StringBuilder();
            if (existing.Ok)
            {
                CommandResult removed = RunProcess(
                    executable,
                    "mcp remove --scope local " + ClaudeServerName,
                    ProjectRoot,
                    10000);
                transcript.AppendLine(removed.Output);
                if (!removed.Ok)
                {
                    return CommandResult.Failure(
                        "Existing Claude Code local MCP entry could not be replaced.\n" + transcript);
                }
            }

            CommandResult added = RunProcess(
                executable,
                "mcp add --scope local --transport http " + ClaudeServerName + " " + Quote(url),
                ProjectRoot,
                15000);
            transcript.AppendLine(added.Output);
            return added.Ok
                ? CommandResult.Success(
                    "Claude Code project-local MCP entry configured.\n" + transcript)
                : CommandResult.Failure(
                    "Claude Code MCP entry could not be configured.\n" + transcript);
        }

        private static void BeginClaudeDesktopAction(
            string action,
            Action<CommandResult> completed)
        {
            string executable = PythonExecutable.Trim();
            string arguments =
                Quote(ServerScriptPath) +
                " --project " + Quote(ProjectRoot) +
                " --claude-desktop-action " + action +
                " --claude-desktop-config " + Quote(ClaudeDesktopConfigPath);
            BeginBackgroundOperation(
                delegate { return RunProcess(executable, arguments, WorkspaceRoot, 10000); },
                completed);
        }

        private static void BeginClientCommand(
            string executable,
            string arguments,
            string workingDirectory,
            Action<CommandResult> completed)
        {
            string executableValue = executable.Trim();
            BeginBackgroundOperation(
                delegate
                {
                    return RunProcess(
                        executableValue,
                        arguments,
                        workingDirectory,
                        10000);
                },
                completed);
        }

        private static CommandResult RequestServerShutdown(HttpServerMetadata metadata)
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(metadata.ControlUrl);
                request.Method = "POST";
                request.ContentLength = 0;
                request.Timeout = 2500;
                request.ReadWriteTimeout = 2500;
                request.Proxy = null;
                request.Headers["X-Unity-MCP-Control-Token"] = metadata.ControlToken;
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                {
                    string body = reader.ReadToEnd();
                    return CommandResult.Success(
                        "Shutdown accepted by HTTP MCP server.\n" + body);
                }
            }
            catch (Exception exception)
            {
                return CommandResult.Failure("Could not stop HTTP MCP server: " + exception.Message);
            }
        }

        private static CommandResult RequestHealth(string healthUrl)
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(healthUrl);
                request.Method = "GET";
                request.Timeout = 2500;
                request.ReadWriteTimeout = 2500;
                request.Proxy = null;
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                {
                    return CommandResult.Success(reader.ReadToEnd());
                }
            }
            catch (Exception exception)
            {
                return CommandResult.Failure("HTTP health check failed: " + exception.Message);
            }
        }

        private static CommandResult RunProcess(
            string executable,
            string arguments,
            string workingDirectory,
            int timeoutMilliseconds)
        {
            if (string.IsNullOrWhiteSpace(executable))
            {
                return CommandResult.Failure("Executable is empty.");
            }

            try
            {
                string processExecutable = executable;
                string processArguments = arguments;
                if (Path.DirectorySeparatorChar == '\\' &&
                    (executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
                     executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
                {
                    if (executable.IndexOf('"') >= 0)
                    {
                        return CommandResult.Failure(
                            "Batch executable path contains an unsupported quote.");
                    }
                    processExecutable = Environment.GetEnvironmentVariable("ComSpec");
                    if (string.IsNullOrEmpty(processExecutable))
                    {
                        processExecutable = "cmd.exe";
                    }
                    processArguments =
                        "/d /s /c \"\"" + executable + "\" " + arguments + "\"";
                }

                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = processExecutable;
                startInfo.Arguments = processArguments;
                startInfo.WorkingDirectory = workingDirectory;
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;
                startInfo.WindowStyle = ProcessWindowStyle.Hidden;
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;

                using (Process process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        return CommandResult.Failure("Process did not start: " + executable);
                    }

                    StringBuilder standardOutput = new StringBuilder();
                    StringBuilder standardError = new StringBuilder();
                    process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs eventArgs)
                    {
                        if (eventArgs.Data != null)
                        {
                            lock (standardOutput)
                            {
                                standardOutput.AppendLine(eventArgs.Data);
                            }
                        }
                    };
                    process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs eventArgs)
                    {
                        if (eventArgs.Data != null)
                        {
                            lock (standardError)
                            {
                                standardError.AppendLine(eventArgs.Data);
                            }
                        }
                    };
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    if (!process.WaitForExit(timeoutMilliseconds))
                    {
                        process.Kill();
                        process.WaitForExit();
                        return CommandResult.Failure(
                            "Process timed out after " + timeoutMilliseconds + " ms.");
                    }
                    process.WaitForExit();

                    string output;
                    lock (standardOutput)
                    lock (standardError)
                    {
                        output = (standardOutput.ToString() + standardError).Trim();
                    }
                    return process.ExitCode == 0
                        ? CommandResult.Success(output)
                        : CommandResult.Failure(
                            "Exit code " + process.ExitCode + ".\n" + output);
                }
            }
            catch (Exception exception)
            {
                return CommandResult.Failure(
                    "Could not run " + executable + ": " + exception.Message);
            }
        }

        private static void BeginBackgroundOperation(
            Func<CommandResult> operation,
            Action<CommandResult> completed)
        {
            ThreadPool.QueueUserWorkItem(
                delegate
                {
                    CommandResult result;
                    try
                    {
                        result = operation();
                    }
                    catch (Exception exception)
                    {
                        result = CommandResult.Failure(exception.ToString());
                    }

                    MainThreadActions.Enqueue(delegate { completed(result); });
                });
        }

        private static void DispatchMainThreadActions()
        {
            Action action;
            while (MainThreadActions.TryDequeue(out action))
            {
                action();
            }
        }

        private static bool TryReadMetadata(
            out HttpServerMetadata metadata,
            out string error)
        {
            metadata = null;
            if (!File.Exists(MetadataPath))
            {
                error = "Stopped";
                return false;
            }

            try
            {
                metadata = JsonUtility.FromJson<HttpServerMetadata>(File.ReadAllText(MetadataPath));
                if (metadata == null || metadata.Pid <= 0 || string.IsNullOrEmpty(metadata.Url))
                {
                    error = "Runtime metadata is invalid.";
                    return false;
                }
                error = null;
                return true;
            }
            catch (Exception exception)
            {
                error = "Could not read runtime metadata: " + exception.Message;
                return false;
            }
        }

        private static bool PathsEqual(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
            {
                return false;
            }

            try
            {
                string normalizedLeft = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar);
                string normalizedRight = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar);
                return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string Quote(string value)
        {
            if (value == null || value.IndexOf('"') >= 0)
            {
                throw new ArgumentException("Command argument contains an unsupported quote.");
            }
            return "\"" + value + "\"";
        }

        private static string EscapeJson(string value)
        {
            return (value ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
        }

        private static void TryDeleteMetadata()
        {
            try
            {
                File.Delete(MetadataPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static void StopServerOnEditorQuit()
        {
            HttpServerStatus status = GetStatus();
            if (!status.Running || status.Metadata == null)
            {
                return;
            }
            RequestServerShutdown(status.Metadata);
        }
    }

    [Serializable]
    internal sealed class HttpServerMetadata
    {
        public int Version;
        public int Pid;
        public string Host;
        public int Port;
        public string Url;
        public string HealthUrl;
        public string ControlUrl;
        public string ControlToken;
        public string ProjectPath;
        public string StartedUtc;
        public string ServerVersion;
        public string ProtocolVersion;
    }

    internal sealed class HttpServerStatus
    {
        internal readonly bool Running;
        internal readonly string Message;
        internal readonly HttpServerMetadata Metadata;

        internal HttpServerStatus(bool running, string message, HttpServerMetadata metadata)
        {
            Running = running;
            Message = message;
            Metadata = metadata;
        }
    }

    internal sealed class CommandResult
    {
        internal readonly bool Ok;
        internal readonly string Output;

        private CommandResult(bool ok, string output)
        {
            Ok = ok;
            Output = string.IsNullOrEmpty(output) ? (ok ? "OK" : "Failed") : output;
        }

        internal static CommandResult Success(string output)
        {
            return new CommandResult(true, output);
        }

        internal static CommandResult Failure(string output)
        {
            return new CommandResult(false, output);
        }

        internal static CommandResult Combine(CommandResult first, CommandResult second)
        {
            return new CommandResult(
                first.Ok && second.Ok,
                first.Output + "\n" + second.Output);
        }
    }
}
#endif
