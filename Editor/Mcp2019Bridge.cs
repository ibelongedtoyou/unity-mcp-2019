#if UNITY_EDITOR
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace UnityMcp2019
{
    /// <summary>
    /// Minimal localhost bridge for Unity 2019.4. The external process owns the
    /// MCP protocol; this class only accepts authenticated, whitelisted commands.
    /// </summary>
    [InitializeOnLoad]
    public static class Mcp2019Bridge
    {
        private const int FirstPort = 6419;
        private const int LastPort = 6439;
        private const int MaxRequestsPerUpdate = 16;
        private const int MaxRequestCharacters = 1024 * 1024;
        internal const string BridgeVersion = "0.3.0";

        private static readonly ConcurrentQueue<PendingRequest> PendingRequests =
            new ConcurrentQueue<PendingRequest>();

        private static TcpListener listener;
        private static Thread acceptThread;
        private static volatile bool running;
        private static bool initialized;
        private static string token;
        private static int port;
        private static string connectionFilePath;

        internal static bool IsRunning
        {
            get { return running; }
        }

        internal static int Port
        {
            get { return port; }
        }

        internal static string ConnectionFilePath
        {
            get { return connectionFilePath; }
        }

        static Mcp2019Bridge()
        {
            Initialize();
        }

        [InitializeOnLoadMethod]
        private static void InitializeAfterLoad()
        {
            Initialize();
        }

        private static void Initialize()
        {
            if (initialized)
            {
                return;
            }

            initialized = true;
            Mcp2019ToolRouter.EnsureInitialized();
            EditorApplication.update += ProcessPendingRequests;
            EditorApplication.quitting += Stop;
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            Start();
            EditorApplication.delayCall += Start;
        }

        [MenuItem(Mcp2019ServerManager.ToolsMenuRoot + "Restart Bridge")]
        private static void RestartFromMenu()
        {
            RestartBridge();
        }

        internal static void StartBridge()
        {
            Start();
        }

        internal static void StopBridge()
        {
            Stop();
        }

        internal static void RestartBridge()
        {
            Stop();
            Start();
        }

        [MenuItem(Mcp2019ServerManager.ToolsMenuRoot + "Print Connection Info")]
        private static void PrintConnectionInfo()
        {
            if (!running)
            {
                UnityEngine.Debug.LogWarning("[MCP 2019] Bridge is not running.");
                return;
            }

            UnityEngine.Debug.Log(
                "[MCP 2019] Listening on 127.0.0.1:" + port +
                ". Connection metadata: " + connectionFilePath);
        }

        private static void Start()
        {
            if (running)
            {
                return;
            }

            token = CreateToken();
            listener = null;

            for (int candidatePort = FirstPort; candidatePort <= LastPort; candidatePort++)
            {
                TcpListener candidate = null;
                try
                {
                    candidate = new TcpListener(IPAddress.Loopback, candidatePort);
                    candidate.Start(8);
                    listener = candidate;
                    port = candidatePort;
                    break;
                }
                catch (SocketException)
                {
                    if (candidate != null)
                    {
                        candidate.Stop();
                    }
                }
            }

            if (listener == null)
            {
                UnityEngine.Debug.LogError(
                    "[MCP 2019] Could not bind a localhost port in range " +
                    FirstPort + "-" + LastPort + ".");
                return;
            }

            try
            {
                running = true;
                WriteConnectionInfo();
                acceptThread = new Thread(AcceptLoop);
                acceptThread.IsBackground = true;
                acceptThread.Name = "Unity2019McpBridge";
                acceptThread.Start();

                UnityEngine.Debug.Log(
                    "[MCP 2019] Bridge ready on 127.0.0.1:" + port +
                    ". Metadata: " + connectionFilePath);
            }
            catch (Exception exception)
            {
                Stop();
                UnityEngine.Debug.LogError("[MCP 2019] Failed to start bridge: " + exception.Message);
            }
        }

        private static void Stop()
        {
            running = false;

            if (listener != null)
            {
                try
                {
                    listener.Stop();
                }
                catch (SocketException)
                {
                }

                listener = null;
            }

            if (acceptThread != null && acceptThread.IsAlive &&
                Thread.CurrentThread != acceptThread)
            {
                acceptThread.Join(250);
            }

            acceptThread = null;

            PendingRequest pending;
            while (PendingRequests.TryDequeue(out pending))
            {
                CloseClient(pending.Client);
            }

            if (!string.IsNullOrEmpty(connectionFilePath) && File.Exists(connectionFilePath))
            {
                try
                {
                    File.Delete(connectionFilePath);
                }
                catch (IOException)
                {
                }
            }
        }

        private static void AcceptLoop()
        {
            while (running)
            {
                try
                {
                    TcpClient client = listener.AcceptTcpClient();
                    client.NoDelay = true;
                    client.ReceiveTimeout = 10000;
                    client.SendTimeout = 10000;
                    ThreadPool.QueueUserWorkItem(ReadRequest, client);
                }
                catch (SocketException)
                {
                    if (running)
                    {
                        Thread.Sleep(50);
                    }
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        }

        private static void ReadRequest(object state)
        {
            TcpClient client = state as TcpClient;
            if (client == null)
            {
                return;
            }

            try
            {
                StreamReader reader = new StreamReader(
                    client.GetStream(), new UTF8Encoding(false), false, 4096, true);
                string line = reader.ReadLine();

                if (line == null || line.Length > MaxRequestCharacters)
                {
                    CloseClient(client);
                    return;
                }

                PendingRequests.Enqueue(new PendingRequest
                {
                    Client = client,
                    Line = line
                });
            }
            catch (IOException)
            {
                CloseClient(client);
            }
            catch (SocketException)
            {
                CloseClient(client);
            }
        }

        private static void ProcessPendingRequests()
        {
            PendingRequest pending;
            int processed = 0;

            while (processed < MaxRequestsPerUpdate &&
                   PendingRequests.TryDequeue(out pending))
            {
                if (!IsClientConnected(pending.Client))
                {
                    CloseClient(pending.Client);
                    processed++;
                    continue;
                }

                BridgeResponse response = ExecuteRequest(pending.Line);
                WriteResponse(pending.Client, response);
                processed++;
            }
        }

        private static BridgeResponse ExecuteRequest(string line)
        {
            BridgeRequest request = null;

            try
            {
                request = JsonUtility.FromJson<BridgeRequest>(line);
                if (request == null || string.IsNullOrEmpty(request.Method))
                {
                    throw new ArgumentException("Malformed bridge request.");
                }

                if (request.Version != 1)
                {
                    throw new ArgumentException("Unsupported bridge protocol version.");
                }

                if (!string.Equals(request.Token, token, StringComparison.Ordinal))
                {
                    return BridgeResponse.Failure(request.Id, "Unauthorized bridge request.");
                }

                if (request.DeadlineUtcTicks > 0 &&
                    DateTime.UtcNow.Ticks > request.DeadlineUtcTicks)
                {
                    return BridgeResponse.Failure(
                        request.Id,
                        "Request expired before Unity main-thread execution.");
                }

                string resultJson = Mcp2019ToolRouter.Execute(
                    request.Method,
                    string.IsNullOrEmpty(request.ArgumentsJson) ? "{}" : request.ArgumentsJson);
                return BridgeResponse.Success(request.Id, resultJson);
            }
            catch (Exception exception)
            {
                return BridgeResponse.Failure(
                    request == null ? null : request.Id,
                    exception.GetType().Name + ": " + exception.Message);
            }
        }

        private static void WriteResponse(TcpClient client, BridgeResponse response)
        {
            if (client == null)
            {
                return;
            }

            try
            {
                string json = JsonUtility.ToJson(response);
                byte[] bytes = new UTF8Encoding(false).GetBytes(json + "\n");
                NetworkStream stream = client.GetStream();
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
            catch (IOException)
            {
            }
            catch (SocketException)
            {
            }
            finally
            {
                CloseClient(client);
            }
        }

        private static void WriteConnectionInfo()
        {
            string projectPath = Directory.GetParent(Application.dataPath).FullName;
            string directory = Path.Combine(
                projectPath,
                "Library",
                Mcp2019ServerManager.RuntimeDirectoryName);
            Directory.CreateDirectory(directory);
            connectionFilePath = Path.Combine(directory, "connection.json");

            ConnectionInfo connectionInfo = new ConnectionInfo
            {
                Host = "127.0.0.1",
                Port = port,
                Token = token,
                ProcessId = Process.GetCurrentProcess().Id,
                ProjectPath = projectPath,
                UnityVersion = Application.unityVersion,
                BridgeVersion = Mcp2019Bridge.BridgeVersion
            };

            string temporaryPath = connectionFilePath + ".tmp";
            File.WriteAllText(
                temporaryPath,
                JsonUtility.ToJson(connectionInfo, true),
                new UTF8Encoding(false));

            if (File.Exists(connectionFilePath))
            {
                File.Delete(connectionFilePath);
            }

            File.Move(temporaryPath, connectionFilePath);
        }

        private static string CreateToken()
        {
            byte[] bytes = new byte[32];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create())
            {
                random.GetBytes(bytes);
            }

            return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static void CloseClient(TcpClient client)
        {
            if (client == null)
            {
                return;
            }

            try
            {
                client.Close();
            }
            catch (SocketException)
            {
            }
        }

        private static bool IsClientConnected(TcpClient client)
        {
            if (client == null || client.Client == null)
            {
                return false;
            }

            try
            {
                return !(client.Client.Poll(0, SelectMode.SelectRead) &&
                         client.Client.Available == 0);
            }
            catch (SocketException)
            {
                return false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        private sealed class PendingRequest
        {
            public TcpClient Client;
            public string Line;
        }

        [Serializable]
        private sealed class BridgeRequest
        {
            public int Version;
            public string Id;
            public string Token;
            public string Method;
            public string ArgumentsJson;
            public long DeadlineUtcTicks;
        }

        [Serializable]
        private sealed class BridgeResponse
        {
            public string Id;
            public bool Ok;
            public string ResultJson;
            public string Error;

            public static BridgeResponse Success(string id, string resultJson)
            {
                return new BridgeResponse
                {
                    Id = id,
                    Ok = true,
                    ResultJson = resultJson,
                    Error = string.Empty
                };
            }

            public static BridgeResponse Failure(string id, string error)
            {
                return new BridgeResponse
                {
                    Id = id,
                    Ok = false,
                    ResultJson = string.Empty,
                    Error = error
                };
            }
        }

        [Serializable]
        private sealed class ConnectionInfo
        {
            public string Host;
            public int Port;
            public string Token;
            public int ProcessId;
            public string ProjectPath;
            public string UnityVersion;
            public string BridgeVersion;
        }
    }
}
#endif
