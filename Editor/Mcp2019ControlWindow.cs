#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityMcp2019
{
    public sealed class Mcp2019ControlWindow : EditorWindow
    {
        private const string WindowMenu = "Window/MCP for Unity 2019 %#m";
        private const string ToolsMenu = Mcp2019ServerManager.ToolsMenuRoot;

        private static readonly string[] Tabs = { "Connect", "Clients", "Diagnostics" };
        private static readonly string[] Clients =
        {
            "Claude Code", "Claude Desktop", "Codex", "Cursor", "Cline",
            "VS Code Copilot", "Windsurf", "Gemini CLI", "Qwen Code",
            "GitHub Copilot CLI", "Antigravity", "OpenClaw"
        };
        private static readonly string[] ProviderLabels =
        {
            "fal.ai", "OpenRouter", "Tripo", "Meshy", "Sketchfab"
        };
        private static readonly string[] ProviderIds =
        {
            "fal", "openrouter", "tripo", "meshy", "sketchfab"
        };

        private Vector2 scrollPosition;
        private Vector2 outputScrollPosition;
        private int activeTab;
        private int selectedClient;
        private bool advancedExpanded;
        private int selectedProvider;
        private string providerSecret = string.Empty;
        private string operationOutput = string.Empty;
        private bool operationRunning;
        private double nextRepaintTime;

        [MenuItem(WindowMenu, false, 2000)]
        public static void ShowWindow()
        {
            Mcp2019ControlWindow window = GetWindow<Mcp2019ControlWindow>();
            Mcp2019ControlWindow[] windows =
                Resources.FindObjectsOfTypeAll<Mcp2019ControlWindow>();
            foreach (Mcp2019ControlWindow candidate in windows)
            {
                if (candidate != window)
                {
                    candidate.Close();
                }
            }

            window.titleContent = new GUIContent("MCP for Unity 2019");
            window.minSize = new Vector2(500.0f, 340.0f);
            window.Show();
            window.Focus();
        }

        [MenuItem(ToolsMenu + "Open Control Panel", false, 0)]
        private static void OpenControlPanelFromToolsMenu()
        {
            ShowWindow();
        }

        [MenuItem(ToolsMenu + "Start MCP Stack", false, 20)]
        private static void StartStackFromMenu()
        {
            Mcp2019Bridge.StartBridge();
            CommandResult result = Mcp2019ServerManager.StartHttpServer();
            LogResult("Start MCP Stack", result);
            ShowWindow();
        }

        [MenuItem(ToolsMenu + "Stop MCP Stack", false, 21)]
        private static void StopStackFromMenu()
        {
            Mcp2019Bridge.StopBridge();
            Mcp2019ServerManager.BeginStopHttpServer(
                delegate(CommandResult result) { LogResult("Stop MCP Stack", result); });
        }

        [MenuItem(ToolsMenu + "Run Protocol Self-Test", false, 40)]
        private static void RunSelfTestFromMenu()
        {
            Mcp2019ServerManager.BeginProtocolSelfTest(
                delegate(CommandResult result) { LogResult("Protocol Self-Test", result); });
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("MCP for Unity 2019");
            minSize = new Vector2(500.0f, 340.0f);
        }

        private void Update()
        {
            if (EditorApplication.timeSinceStartup < nextRepaintTime)
            {
                return;
            }
            nextRepaintTime = EditorApplication.timeSinceStartup + 0.5;
            Repaint();
        }

        private void OnGUI()
        {
            HttpServerStatus status = Mcp2019ServerManager.GetStatus();
            DrawHeader(status);
            activeTab = GUILayout.Toolbar(activeTab, Tabs);

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
            EditorGUILayout.Space();
            if (activeTab == 0)
            {
                DrawConnectTab(status);
            }
            else if (activeTab == 1)
            {
                DrawClientsTab(status);
            }
            else
            {
                DrawDiagnosticsTab();
            }

            if (operationRunning || !string.IsNullOrEmpty(operationOutput))
            {
                EditorGUILayout.Space();
                DrawOutput();
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawHeader(HttpServerStatus status)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("MCP for Unity 2019", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            DrawStatusBadge("HTTP", status.Running);
            GUILayout.Space(4.0f);
            DrawStatusBadge("Bridge", Mcp2019Bridge.IsRunning);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField(
                "Unity 2019 compatibility layer aligned with CoplayDev main.",
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(4.0f);
        }

        private void DrawConnectTab(HttpServerStatus status)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Connection", EditorStyles.boldLabel);
            DrawValue("State", status.Message);
            DrawValue(
                "MCP URL",
                status.Metadata != null
                    ? status.Metadata.Url
                    : Mcp2019ServerManager.ConfiguredMcpUrl);
            DrawValue(
                "Unity bridge",
                Mcp2019Bridge.IsRunning
                    ? "Listening on 127.0.0.1:" + Mcp2019Bridge.Port
                    : "Stopped");

            EditorGUILayout.Space(3.0f);
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(operationRunning || status.Running))
            {
                if (GUILayout.Button("Start", GUILayout.Height(28.0f)))
                {
                    Mcp2019Bridge.StartBridge();
                    SetResult("Start MCP", Mcp2019ServerManager.StartHttpServer());
                }
            }
            using (new EditorGUI.DisabledScope(operationRunning || !status.Running))
            {
                if (GUILayout.Button("Restart", GUILayout.Height(28.0f)))
                {
                    Mcp2019Bridge.StartBridge();
                    BeginOperation("Restart MCP", Mcp2019ServerManager.BeginRestartHttpServer);
                }
                if (GUILayout.Button("Stop", GUILayout.Height(28.0f)))
                {
                    Mcp2019Bridge.StopBridge();
                    BeginOperation("Stop MCP", Mcp2019ServerManager.BeginStopHttpServer);
                }
            }
            if (GUILayout.Button("Copy URL", GUILayout.Height(28.0f)))
            {
                EditorGUIUtility.systemCopyBuffer = Mcp2019ServerManager.ConfiguredMcpUrl;
                operationOutput = "MCP URL copied to the clipboard.";
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();

            advancedExpanded = EditorGUILayout.Foldout(
                advancedExpanded,
                "Advanced settings",
                true);
            if (!advancedExpanded)
            {
                return;
            }

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUI.BeginChangeCheck();
            string python = EditorGUILayout.TextField(
                new GUIContent("Python", "Executable name or absolute path, without arguments."),
                Mcp2019ServerManager.PythonExecutable);
            int port = EditorGUILayout.IntField("HTTP port", Mcp2019ServerManager.HttpPort);
            if (EditorGUI.EndChangeCheck())
            {
                Mcp2019ServerManager.PythonExecutable = python.Trim();
                Mcp2019ServerManager.HttpPort = port;
            }

            if (status.Running && status.Metadata != null &&
                status.Metadata.Port != Mcp2019ServerManager.HttpPort)
            {
                EditorGUILayout.HelpBox("Restart MCP to apply the new port.", MessageType.Warning);
            }

            DrawProviderKeySettings();

            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(Mcp2019Bridge.IsRunning))
            {
                if (GUILayout.Button("Start Bridge"))
                {
                    Mcp2019Bridge.StartBridge();
                    operationOutput = "Unity bridge start requested.";
                }
            }
            using (new EditorGUI.DisabledScope(!Mcp2019Bridge.IsRunning))
            {
                if (GUILayout.Button("Restart Bridge"))
                {
                    Mcp2019Bridge.RestartBridge();
                    operationOutput = "Unity bridge restarted.";
                }
                if (GUILayout.Button("Stop Bridge"))
                {
                    Mcp2019Bridge.StopBridge();
                    operationOutput = "Unity bridge stopped.";
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Reveal Server Script"))
            {
                EditorUtility.RevealInFinder(Mcp2019ServerManager.ServerScriptPath);
            }
            using (new EditorGUI.DisabledScope(!File.Exists(Mcp2019ServerManager.LogPath)))
            {
                if (GUILayout.Button("Reveal Log"))
                {
                    EditorUtility.RevealInFinder(Mcp2019ServerManager.LogPath);
                }
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        private void DrawProviderKeySettings()
        {
            EditorGUILayout.Space(5.0f);
            EditorGUILayout.LabelField("Generative providers", EditorStyles.boldLabel);
            if (!Mcp2019CredentialStore.IsAvailable)
            {
                EditorGUILayout.HelpBox(
                    "Secure provider-key storage is currently available on Windows Editor.",
                    MessageType.Info);
                return;
            }

            selectedProvider = EditorGUILayout.Popup(
                "Provider", selectedProvider, ProviderLabels);
            string provider = ProviderIds[selectedProvider];
            DrawValue(
                "Key status",
                Mcp2019CredentialStore.Has(provider) ? "Configured" : "Not configured");
            providerSecret = EditorGUILayout.PasswordField("API key", providerSecret);
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(providerSecret)))
            {
                if (GUILayout.Button("Save Key"))
                {
                    try
                    {
                        Mcp2019CredentialStore.Save(provider, providerSecret);
                        providerSecret = string.Empty;
                        operationOutput = ProviderLabels[selectedProvider] +
                            " key saved in Windows Credential Manager.";
                    }
                    catch (Exception exception)
                    {
                        operationOutput = "Could not save provider key: " + exception.Message;
                    }
                }
            }
            using (new EditorGUI.DisabledScope(!Mcp2019CredentialStore.Has(provider)))
            {
                if (GUILayout.Button("Remove Key"))
                {
                    try
                    {
                        Mcp2019CredentialStore.Delete(provider);
                        providerSecret = string.Empty;
                        operationOutput = ProviderLabels[selectedProvider] + " key removed.";
                    }
                    catch (Exception exception)
                    {
                        operationOutput = "Could not remove provider key: " + exception.Message;
                    }
                }
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField(
                "Keys stay outside the project and never appear in MCP responses.",
                EditorStyles.wordWrappedMiniLabel);
        }

        private void DrawClientsTab(HttpServerStatus status)
        {
            selectedClient = EditorGUILayout.Popup("Client", selectedClient, Clients);
            string clientName = Clients[selectedClient];

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            DrawValue("Entry", Mcp2019ServerManager.GetClientEntryName(clientName));
            DrawValue("Transport", Mcp2019ServerManager.GetClientTransport(clientName));
            DrawValue("Config", Mcp2019ServerManager.GetClientTarget(clientName));

            if (clientName == "Codex")
            {
                EditorGUI.BeginChangeCheck();
                string codex = EditorGUILayout.TextField("Codex executable", Mcp2019ServerManager.CodexExecutable);
                if (EditorGUI.EndChangeCheck())
                {
                    Mcp2019ServerManager.CodexExecutable = codex.Trim();
                }
            }
            else if (clientName == "Claude Code")
            {
                EditorGUI.BeginChangeCheck();
                string claude = EditorGUILayout.TextField("Claude executable", Mcp2019ServerManager.ClaudeExecutable);
                if (EditorGUI.EndChangeCheck())
                {
                    Mcp2019ServerManager.ClaudeExecutable = claude.Trim();
                }
            }
            else if (clientName == "Claude Desktop")
            {
                EditorGUI.BeginChangeCheck();
                string python = EditorGUILayout.TextField("Python executable", Mcp2019ServerManager.PythonExecutable);
                if (EditorGUI.EndChangeCheck())
                {
                    Mcp2019ServerManager.PythonExecutable = python.Trim();
                }
            }

            string command = GetSelectedClientCommand();
            EditorGUILayout.LabelField("Registration", EditorStyles.miniLabel);
            EditorGUILayout.SelectableLabel(
                command,
                EditorStyles.textArea,
                GUILayout.Height(command.IndexOf('\n') >= 0 ? 96.0f : 38.0f));

            using (new EditorGUI.DisabledScope(operationRunning))
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Status"))
                {
                    BeginSelectedClientStatus();
                }
                if (GUILayout.Button("Configure"))
                {
                    ConfirmAndConfigureSelectedClient();
                }
                if (GUILayout.Button("Remove"))
                {
                    ConfirmAndRemoveSelectedClient();
                }
                if (GUILayout.Button("Copy"))
                {
                    EditorGUIUtility.systemCopyBuffer = command;
                    operationOutput = Clients[selectedClient] + " configuration copied.";
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndVertical();

            if (clientName == "Claude Code")
            {
                EditorGUILayout.HelpBox(
                    "Claude Code uses project-local scope, matching CoplayDev main.",
                    MessageType.None);
            }
            else if (clientName == "Claude Desktop")
            {
                EditorGUILayout.HelpBox(
                    "Claude Desktop supports stdio here. Restart Claude Desktop after changing its config.",
                    MessageType.None);
            }
            else if (clientName == "OpenClaw")
            {
                EditorGUILayout.HelpBox(
                    "OpenClaw requires the openclaw-mcp-bridge plugin; this panel configures its unityMCP server entry.",
                    MessageType.None);
            }
            if (!status.Running && clientName != "Claude Desktop")
            {
                EditorGUILayout.HelpBox(
                    "Configuration can be saved now; start MCP before connecting.",
                    MessageType.Warning);
            }
        }

        private void DrawDiagnosticsTab()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Validation", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Run the protocol check first, then validate the live Unity bridge and HTTP endpoint.",
                EditorStyles.wordWrappedMiniLabel);
            using (new EditorGUI.DisabledScope(operationRunning))
            {
                if (GUILayout.Button("Protocol Self-Test", GUILayout.Height(26.0f)))
                {
                    BeginOperation("Protocol Self-Test", Mcp2019ServerManager.BeginProtocolSelfTest);
                }
                if (GUILayout.Button("Bridge Smoke Test", GUILayout.Height(26.0f)))
                {
                    BeginOperation("Bridge Smoke Test", Mcp2019ServerManager.BeginBridgeSmokeTest);
                }
                if (GUILayout.Button("HTTP Health Check", GUILayout.Height(26.0f)))
                {
                    BeginOperation("HTTP Health Check", Mcp2019ServerManager.BeginHttpHealthCheck);
                }
            }
            EditorGUILayout.EndVertical();
        }

        private string GetSelectedClientCommand()
        {
            return Mcp2019ServerManager.GetClientRegistration(Clients[selectedClient]);
        }

        private void BeginSelectedClientStatus()
        {
            string client = Clients[selectedClient];
            BeginOperation(
                client + " Status",
                delegate(Action<CommandResult> completed)
                {
                    Mcp2019ServerManager.BeginCheckClient(client, completed);
                });
        }

        private void ConfirmAndConfigureSelectedClient()
        {
            string clientName = Clients[selectedClient];
            bool confirmed = EditorUtility.DisplayDialog(
                "Configure " + clientName,
                "Create or update the " + clientName + " MCP entry for this Unity project?",
                "Configure",
                "Cancel");
            if (!confirmed)
            {
                return;
            }

            BeginOperation(
                "Configure " + clientName,
                delegate(Action<CommandResult> completed)
                {
                    Mcp2019ServerManager.BeginConfigureClient(clientName, completed);
                });
        }

        private void ConfirmAndRemoveSelectedClient()
        {
            string clientName = Clients[selectedClient];
            bool confirmed = EditorUtility.DisplayDialog(
                "Remove " + clientName,
                "Remove this project's " + clientName + " MCP entry? Other entries are preserved.",
                "Remove",
                "Cancel");
            if (!confirmed)
            {
                return;
            }

            BeginOperation(
                "Remove " + clientName,
                delegate(Action<CommandResult> completed)
                {
                    Mcp2019ServerManager.BeginRemoveClient(clientName, completed);
                });
        }

        private void DrawOutput()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                operationRunning ? "Output (running...)" : "Output",
                EditorStyles.boldLabel);
            if (GUILayout.Button("Clear", GUILayout.Width(58.0f)))
            {
                operationOutput = string.Empty;
            }
            EditorGUILayout.EndHorizontal();
            outputScrollPosition = EditorGUILayout.BeginScrollView(
                outputScrollPosition,
                GUILayout.MinHeight(70.0f),
                GUILayout.MaxHeight(150.0f));
            EditorGUILayout.SelectableLabel(
                operationOutput,
                EditorStyles.textArea,
                GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        private void BeginOperation(string label, Action<Action<CommandResult>> begin)
        {
            operationRunning = true;
            operationOutput = label + " started...";
            begin(
                delegate(CommandResult result)
                {
                    if (this == null)
                    {
                        return;
                    }
                    operationRunning = false;
                    SetResult(label, result);
                    Repaint();
                });
        }

        private void SetResult(string label, CommandResult result)
        {
            operationOutput =
                "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + label + " - " +
                (result.Ok ? "OK" : "FAILED") + "\n" + result.Output;
        }

        private static void DrawStatusBadge(string label, bool active)
        {
            Color previous = GUI.color;
            GUI.color = active ? new Color(0.65f, 1.0f, 0.65f) : new Color(1.0f, 0.72f, 0.65f);
            GUILayout.Label(label + ": " + (active ? "On" : "Off"), EditorStyles.miniButton);
            GUI.color = previous;
        }

        private static void DrawValue(string label, string value)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, GUILayout.Width(95.0f));
            EditorGUILayout.SelectableLabel(
                string.IsNullOrEmpty(value) ? "-" : value,
                EditorStyles.textField,
                GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUILayout.EndHorizontal();
        }

        private static void LogResult(string label, CommandResult result)
        {
            string message = "[MCP 2019] " + label + ": " + result.Output;
            if (result.Ok)
            {
                UnityEngine.Debug.Log(message);
            }
            else
            {
                UnityEngine.Debug.LogError(message);
            }
        }
    }
}
#endif
