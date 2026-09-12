using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace MultiplayerARPG.MMO.Mesh.Editor
{
    /// <summary>
    /// Editor window to launch the full 9-zone server mesh cluster for testing.
    /// Starts a central server, database server, and 9 map server instances.
    /// </summary>
    public class ServerMeshLauncher : EditorWindow
    {
        private const string WindowTitle = "Server Mesh Launcher";
        private const int DefaultCentralPort = 6000;
        private const int DefaultLoginPort = 7500;
        private const int DefaultClusterPort = 6010;
        private const int DefaultDatabasePort = 6100;
        private const int DefaultBaseMapPort = 7770;
        private const string DefaultMapName = "ServerMeshTestMap";

        [SerializeField] private ServerMeshTestConfig meshConfig;
        [SerializeField] private string executablePath = "";
        [SerializeField] private string centralAddress = "127.0.0.1";
        [SerializeField] private int centralPort = DefaultCentralPort;
        [SerializeField] private int loginPort = DefaultLoginPort;
        [SerializeField] private int clusterPort = DefaultClusterPort;
        [SerializeField] private int databasePort = DefaultDatabasePort;
        [SerializeField] private string mapName = DefaultMapName;
        [SerializeField] private string zonesToLaunchCsv = "";
        [SerializeField] private bool launchCentralServer = true;
        [SerializeField] private bool launchLoginServer = true;
        [SerializeField] private bool launchDatabaseServer = true;
        [SerializeField] private bool batchMode = true;

        private readonly List<Process> _runningProcesses = new List<Process>();

        [MenuItem("Server Meshing/Launch Test Cluster")]
        public static void ShowWindow()
        {
            var window = GetWindow<ServerMeshLauncher>(WindowTitle);
            window.minSize = new Vector2(450, 500);
        }

        private void OnEnable()
        {
            if (string.IsNullOrEmpty(executablePath))
            {
                // Default to this project's server build (Build/Server next to Assets/).
                string projectRoot = Path.GetDirectoryName(Application.dataPath) ?? "";
                string candidate = Path.Combine(projectRoot, "Build", "Server", Application.productName + ".exe");
                if (File.Exists(candidate))
                    executablePath = candidate;
            }
            if (meshConfig == null)
            {
                string[] guids = AssetDatabase.FindAssets("t:ServerMeshTestConfig");
                if (guids.Length > 0)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guids[0]);
                    meshConfig = AssetDatabase.LoadAssetAtPath<ServerMeshTestConfig>(path);
                }
            }
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Server Mesh Test Cluster", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);

            meshConfig = (ServerMeshTestConfig)EditorGUILayout.ObjectField(
                "Mesh Config", meshConfig, typeof(ServerMeshTestConfig), false);

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Executable", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            executablePath = EditorGUILayout.TextField("Server Build Path", executablePath);
            if (GUILayout.Button("Browse", GUILayout.Width(60)))
            {
                string selected = EditorUtility.OpenFilePanel("Select Server Executable", "", "exe");
                if (!string.IsNullOrEmpty(selected))
                    executablePath = selected;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Network Settings", EditorStyles.boldLabel);

            centralAddress = EditorGUILayout.TextField("Central Address", centralAddress);
            centralPort = EditorGUILayout.IntField("Central Port", centralPort);
            loginPort = EditorGUILayout.IntField(new GUIContent("Login Port", "The client connects here for register/login. Must differ from Central Port."), loginPort);
            clusterPort = EditorGUILayout.IntField("Cluster Port", clusterPort);
            databasePort = EditorGUILayout.IntField("Database Port", databasePort);
            mapName = EditorGUILayout.TextField("Map Name", mapName);

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Launch Options", EditorStyles.boldLabel);

            launchCentralServer = EditorGUILayout.Toggle("Launch Central Server", launchCentralServer);
            launchLoginServer = EditorGUILayout.Toggle("Launch Login Server", launchLoginServer);
            launchDatabaseServer = EditorGUILayout.Toggle("Launch Database Server", launchDatabaseServer);
            batchMode = EditorGUILayout.Toggle("Batch Mode (no window)", batchMode);
            zonesToLaunchCsv = EditorGUILayout.TextField(
                new GUIContent("Zones To Launch", "Comma-separated zone IDs (empty = all). E.g. 'C,E' for a 2-zone first test — 9 headless servers is heavy."),
                zonesToLaunchCsv);

            EditorGUILayout.Space(12);

            // Zone summary
            if (meshConfig != null && meshConfig.zones != null)
            {
                EditorGUILayout.LabelField($"Zones: {meshConfig.zones.Length}", EditorStyles.miniLabel);
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                for (int i = 0; i < meshConfig.zones.Length; i++)
                {
                    var zone = meshConfig.zones[i];
                    EditorGUILayout.LabelField(
                        $"  [{zone.gridX},{zone.gridY}] {zone.zoneId} → port {zone.serverPort}",
                        EditorStyles.miniLabel);
                }
                EditorGUILayout.EndVertical();
            }

            EditorGUILayout.Space(12);

            GUI.backgroundColor = new Color(0.4f, 0.8f, 0.4f);
            if (GUILayout.Button("Launch All Servers", GUILayout.Height(32)))
            {
                LaunchCluster();
            }
            GUI.backgroundColor = Color.white;

            EditorGUILayout.Space(4);

            GUI.backgroundColor = new Color(0.8f, 0.4f, 0.4f);
            if (GUILayout.Button("Stop All Servers", GUILayout.Height(24)))
            {
                StopAll();
            }
            GUI.backgroundColor = Color.white;

            EditorGUILayout.Space(8);

            if (_runningProcesses.Count > 0)
            {
                EditorGUILayout.LabelField($"Running Processes: {_runningProcesses.Count}", EditorStyles.miniLabel);
            }

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Utilities", EditorStyles.boldLabel);

            if (GUILayout.Button("Generate serverConfig_meshtest.json"))
            {
                GenerateConfigJson();
            }

            if (GUILayout.Button("Copy Launch Commands to Clipboard"))
            {
                CopyLaunchCommands();
            }
        }

        /// <summary>
        /// Launches the full cluster: central, database, and 9 zone map servers.
        /// </summary>
        private void LaunchCluster()
        {
            if (string.IsNullOrEmpty(executablePath) || !File.Exists(executablePath))
            {
                EditorUtility.DisplayDialog("Error", "Server executable not found. Please set a valid build path.", "OK");
                return;
            }

            if (meshConfig == null || meshConfig.zones == null || meshConfig.zones.Length == 0)
            {
                EditorUtility.DisplayDialog("Error", "ServerMeshTestConfig is missing or has no zones defined.", "OK");
                return;
            }

            StopAll();

            string batchArg = batchMode ? "-batchmode -nographics" : "";

            // Launch infrastructure (central + login + database) in one process.
            // The client connects to the LOGIN server (loginPort) for register/login; those handlers
            // live only on LoginNetworkManager, not Central — so a login server is required.
            if (launchCentralServer || launchLoginServer || launchDatabaseServer)
            {
                string infraArgs = batchArg;
                if (launchCentralServer)
                    infraArgs += " -startCentralServer";
                if (launchLoginServer)
                    infraArgs += " -startLoginServer";
                if (launchDatabaseServer)
                    infraArgs += " -startDatabaseServer";

                infraArgs += $" -centralPort {centralPort}";
                infraArgs += $" -clusterPort {clusterPort}";
                infraArgs += $" -databaseManagerPort {databasePort}";
                if (launchLoginServer)
                    infraArgs += $" -loginPort {loginPort}";

                LaunchProcess("Central+Login+DB", infraArgs.Trim());
            }

            // Launch map servers (filtered by zonesToLaunchCsv; empty = all)
            HashSet<string> zoneFilter = ParseZonesToLaunch();
            int launchedZones = 0;
            for (int i = 0; i < meshConfig.zones.Length; i++)
            {
                var zone = meshConfig.zones[i];
                if (zoneFilter.Count > 0 && !zoneFilter.Contains(zone.zoneId))
                    continue;

                string zoneArgs = $"{batchArg} -startMapServer";
                zoneArgs += $" -centralAddress {centralAddress}";
                zoneArgs += $" -clusterPort {clusterPort}";
                zoneArgs += $" -mapName {mapName}";
                // Each zone needs a UNIQUE cluster key. The cluster registers map servers by
                // {channelId}_{mapName} and rejects duplicates (UI_ERROR_MAP_EXISTED), so we give
                // each zone a distinct channelId (the zoneId). Without this, only one zone registers.
                zoneArgs += $" -channelId {zone.zoneId}";
                zoneArgs += $" -mapPort {zone.serverPort}";
                zoneArgs += $" -publicAddress {zone.serverAddress}";
                // Map servers talk to the database server directly; without these they fall back to
                // serverConfig.json's databaseManagerPort and fail to connect when it differs.
                zoneArgs += $" -databaseManagerAddress {centralAddress}";
                zoneArgs += $" -databaseManagerPort {databasePort}";
                zoneArgs += $" -zoneX {zone.gridX}";
                zoneArgs += $" -zoneY {zone.gridY}";
                zoneArgs += $" -zoneId {zone.zoneId}";

                LaunchProcess($"Zone_{zone.zoneId}", zoneArgs.Trim());
                launchedZones++;
            }

            Debug.Log($"[ServerMesh] Launched cluster: {_runningProcesses.Count} processes ({launchedZones} zones)");
        }

        /// <summary>
        /// Parses the comma-separated zone-id filter. Empty/whitespace = launch all zones.
        /// </summary>
        private HashSet<string> ParseZonesToLaunch()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(zonesToLaunchCsv))
                return set;
            foreach (string part in zonesToLaunchCsv.Split(','))
            {
                string id = part.Trim();
                if (id.Length > 0)
                    set.Add(id);
            }
            return set;
        }

        private void LaunchProcess(string label, string arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = batchMode,
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? ""
            };

            try
            {
                Process proc = Process.Start(startInfo);
                if (proc != null)
                {
                    _runningProcesses.Add(proc);
                    Debug.Log($"[ServerMesh] Started {label}: {arguments}");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[ServerMesh] Failed to start {label}: {ex.Message}");
            }
        }

        /// <summary>
        /// Stops all running server processes launched by this window.
        /// </summary>
        private void StopAll()
        {
            int killed = 0;
            for (int i = _runningProcesses.Count - 1; i >= 0; i--)
            {
                try
                {
                    if (_runningProcesses[i] != null && !_runningProcesses[i].HasExited)
                    {
                        _runningProcesses[i].Kill();
                        killed++;
                    }
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[ServerMesh] Could not kill process: {ex.Message}");
                }
            }

            _runningProcesses.Clear();

            if (killed > 0)
                Debug.Log($"[ServerMesh] Stopped {killed} server processes.");
        }

        /// <summary>
        /// Generates a serverConfig JSON template for standalone deployment.
        /// </summary>
        private void GenerateConfigJson()
        {
            if (meshConfig == null || meshConfig.zones == null)
            {
                EditorUtility.DisplayDialog("Error", "ServerMeshTestConfig is not assigned.", "OK");
                return;
            }

            var spawnMaps = new List<string>();
            for (int i = 0; i < meshConfig.zones.Length; i++)
            {
                spawnMaps.Add(mapName);
            }

            var configObj = new
            {
                centralAddress = this.centralAddress,
                centralPort = this.centralPort,
                loginPort = this.loginPort,
                clusterPort = this.clusterPort,
                databaseManagerPort = this.databasePort,
                publicAddress = "127.0.0.1",
                mapSpawnPort = 6100,
                spawnExePath = "./Server.exe",
                spawnStartPort = DefaultBaseMapPort,
                spawnMaps = spawnMaps
            };

            string json = Newtonsoft.Json.JsonConvert.SerializeObject(configObj, Newtonsoft.Json.Formatting.Indented);

            // Also generate the per-zone launch commands as comments
            string fullContent = json + "\n\n// Per-zone launch commands:\n";
            for (int i = 0; i < meshConfig.zones.Length; i++)
            {
                var zone = meshConfig.zones[i];
                fullContent += $"// Zone {zone.zoneId}: -startMapServer -mapName {mapName} " +
                               $"-mapPort {zone.serverPort} -zoneX {zone.gridX} -zoneY {zone.gridY} -zoneId {zone.zoneId}\n";
            }

            string outputPath = Path.Combine(Application.dataPath, "ServerMeshing", "TestMap", "Config", "serverConfig_meshtest.json");
            string dir = Path.GetDirectoryName(outputPath);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(outputPath, json);
            AssetDatabase.Refresh();

            Debug.Log($"[ServerMesh] Config written to: {outputPath}");
            EditorUtility.DisplayDialog("Success", $"Config written to:\n{outputPath}", "OK");
        }

        /// <summary>
        /// Copies all launch commands to the clipboard for use in scripts or terminals.
        /// </summary>
        private void CopyLaunchCommands()
        {
            if (meshConfig == null || meshConfig.zones == null)
            {
                EditorUtility.DisplayDialog("Error", "ServerMeshTestConfig is not assigned.", "OK");
                return;
            }

            string exe = string.IsNullOrEmpty(executablePath) ? "./Server.exe" : executablePath;
            string batchArg = batchMode ? "-batchmode -nographics " : "";
            var lines = new List<string>();

            lines.Add("@echo off");
            lines.Add("REM === Server Mesh Test Cluster Launch Script ===");
            lines.Add("");

            // Central + Login + DB
            lines.Add("REM Central + Login + Database Server");
            lines.Add($"start \"Central\" \"{exe}\" {batchArg}-startCentralServer -startLoginServer -startDatabaseServer " +
                      $"-centralPort {centralPort} -loginPort {loginPort} -clusterPort {clusterPort} -databaseManagerPort {databasePort}");
            lines.Add("");
            lines.Add("timeout /t 3 >nul");
            lines.Add("");

            // Zone servers (filtered by zonesToLaunchCsv; empty = all)
            HashSet<string> zoneFilter = ParseZonesToLaunch();
            int launchedZones = 0;
            lines.Add("REM Map Servers");
            for (int i = 0; i < meshConfig.zones.Length; i++)
            {
                var zone = meshConfig.zones[i];
                if (zoneFilter.Count > 0 && !zoneFilter.Contains(zone.zoneId))
                    continue;

                // -channelId gives each zone a unique cluster key ({channelId}_{mapName}).
                lines.Add($"start \"Zone_{zone.zoneId}\" \"{exe}\" {batchArg}-startMapServer " +
                          $"-centralAddress {centralAddress} -clusterPort {clusterPort} " +
                          $"-mapName {mapName} -channelId {zone.zoneId} -mapPort {zone.serverPort} " +
                          $"-publicAddress {zone.serverAddress} " +
                          $"-databaseManagerAddress {centralAddress} -databaseManagerPort {databasePort} " +
                          $"-zoneX {zone.gridX} -zoneY {zone.gridY} -zoneId {zone.zoneId}");
                launchedZones++;
            }

            lines.Add("");
            lines.Add($"echo {launchedZones} zone servers launched.");
            lines.Add("pause");

            string script = string.Join("\n", lines);
            EditorGUIUtility.systemCopyBuffer = script;

            Debug.Log("[ServerMesh] Launch script copied to clipboard.");
            EditorUtility.DisplayDialog("Copied", "Launch script (.bat) copied to clipboard.", "OK");
        }

        private void OnDestroy()
        {
            // Don't kill processes on window close - they persist intentionally.
            // User must explicitly click "Stop All Servers".
        }
    }
}
