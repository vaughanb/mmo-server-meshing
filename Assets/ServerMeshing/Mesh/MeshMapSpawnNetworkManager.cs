// Server Meshing add-on — MeshMapSpawnNetworkManager: the kit's map spawner starting one server per cell.
//
// The kit starts every map in `spawningMaps` on every channel in `spawningChannelIds` (a cartesian product in a
// private loop). A cell is a channel, and two meshed maps have different cells, so this subclass takes over the
// start-up spawn once the spawner is registered with the cluster: a meshed map (one with a layout in the world
// registry) is started on its own cells only, through the kit's own private SpawnMap (reached by reflection — the
// kit stays untouched); maps without a layout are left to the kit's loop over its channel list.
//
// Kills (Slice 2d decision 4): the central's supervisor sends KillChannel (30020) for a hung, stale or straggling cell. The
// kit keeps the pids it started in a private set with no channel, so the cell's own hello names its pid; the spawner
// kills that process only when it runs the build's executable and started within 60 s of the incarnation's ticks (the
// incarnation is made at server start, after the batchmode boot).
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using Cysharp.Threading.Tasks;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public class MeshMapSpawnNetworkManager : MapSpawnNetworkManager
    {
        [Header("Server Meshing")]
        [Tooltip("World registry: a map with a layout is started once per cell (channel id = cell id).")]
        public MeshWorldRegistry world;
        [Tooltip("Sleeping cells: do not start the cells at boot; the central starts them on demand. (Rig: MESH_SLEEP=1.)")]
        public bool sleepCells = false;
        [Tooltip("Cell ids this spawner is responsible for. Empty = every cell of every meshed map, which is right for a single host. " +
                 "With several machines sharing one world, give each host its own list so two of them never serve the same cell " +
                 "(the cluster keys a map server by cell id and rejects the second). List base cells only: a Dynamic map's family (children, restarts, " +
                 "kills) runs on the host whose list names its base, never elsewhere. Rig override: MESH_SPAWN_CELLS=C,N.")]
        public string[] servedCells = new string[0];

        public const string ENV_SERVED_CELLS = "MESH_SPAWN_CELLS";

#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        private static readonly MethodInfo s_spawnMap = FindSpawnMap();
        private ClusterClient.OnResponseAppServerRegister _kitOnRegister;
        // The spawner registers again after every central restart; its cells are still running then (the spawner
        // outlives the central), so they are started once per spawner lifetime, never again on a re-registration.
        private MeshSpawnOnce _startOnce = new MeshSpawnOnce();
        private bool _hostReported;
        private readonly MeshHostCpuSampler _hostCpu = new MeshHostCpuSampler();
        private float _nextHostReport;

        /// <summary>Seconds between host reports to the central (Slice 3 decision 8).</summary>
        public const float HostReportSeconds = 10f;

        protected override void Update()
        {
            base.Update();
            if (ClusterClient != null && ClusterClient.IsAppRegistered && UnityEngine.Time.unscaledTime >= _nextHostReport)
                SendHostReport();
        }

        /// <summary>
        /// SendHostReport tells the central this host's name, cores and cores in use (every process's processor time this
        /// spawner can read, summed, over the interval since the last report; 0 at the first).
        /// </summary>
        private void SendHostReport()
        {
            _nextHostReport = UnityEngine.Time.unscaledTime + HostReportSeconds;
            if (ClusterClient == null)
                return;
            double total = 0d;
            foreach (System.Diagnostics.Process process in System.Diagnostics.Process.GetProcesses())
            {
                try
                {
                    total += process.TotalProcessorTime.TotalSeconds;
                }
                catch (System.Exception)
                {
                    // another user's process, or one that exited meanwhile: not ours to read
                }
                finally
                {
                    process.Dispose();
                }
            }
            MeshHostReportMessage host = new MeshHostReportMessage
            {
                hostName = System.Environment.MachineName,
                cores = MeshHostReportMessage.Cores(System.Environment.GetEnvironmentVariable(MeshHostReportMessage.ENV_HOST_CORES), System.Environment.ProcessorCount),
                coresInUse = _hostCpu.Sample(total, UnityEngine.Time.realtimeSinceStartupAsDouble),
            };
            ClusterClient.SendPacket(0, LiteNetLib.DeliveryMethod.ReliableOrdered, MeshClusterMsg.HostReport, writer => host.Serialize(writer));
            if (!_hostReported)
            {
                _hostReported = true;
                Logging.Log("Mesh", $"Host report: {host.hostName} {host.cores} cores");
            }
        }

        /// <summary>The kit's SpawnMap(channelId, mapName, isAllocate, instanceId, warpPosition, overrideRotation, warpRotation, autoRestart, result) — it has a second, request-based overload.</summary>
        private static MethodInfo FindSpawnMap()
        {
            foreach (MethodInfo method in typeof(MapSpawnNetworkManager).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic))
            {
                if (method.Name != "SpawnMap") continue;
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length == 9 && parameters[0].ParameterType == typeof(string) && parameters[1].ParameterType == typeof(string))
                    return method;
            }
            return null;
        }

        protected override void Initialize()
        {
            base.Initialize();
            ResolveEditorExePath();
            ResolveEditorPublicAddress();
            // Registered whether or not a map is meshed here: the central only sends it for a cell this host started.
            ClusterClient?.RegisterMessageHandler(MeshClusterMsg.KillChannel, HandleKillChannel);
            world = MeshLayoutOverride.Resolve(world);
            if (world == null)
                return;
            if (s_spawnMap == null)
            {
                Logging.LogError("Mesh", "MapSpawnNetworkManager.SpawnMap not found (kit changed): meshed maps are started by the kit's loop, on every channel");
                return;
            }
            string env = System.Environment.GetEnvironmentVariable(ENV_SERVED_CELLS);
            if (!string.IsNullOrEmpty(env))
            {
                servedCells = env.Split(',');
                for (int i = 0; i < servedCells.Length; ++i)
                    servedCells[i] = servedCells[i].Trim();
                Logging.Log("Mesh", $"{ENV_SERVED_CELLS}={env}: this spawner serves {servedCells.Length} cell(s)");
            }
            _kitOnRegister = ClusterClient.onResponseAppServerRegister;
            ClusterClient.onResponseAppServerRegister = OnRegisteredWithMesh;
            _startOnce = new MeshSpawnOnce();
        }

        /// <summary>
        /// In the editor the kit starts cells from overrideExePath, an absolute path saved with the prefab, so it names
        /// the machine the prefab was last saved on. When that file does not exist here, cells start from this project's
        /// own player build instead (Build/Server/&lt;product name&gt;.exe), and the substitution is logged.
        /// </summary>
        private void ResolveEditorExePath()
        {
            if (!Application.isEditor || !isOverrideExePath || (!string.IsNullOrEmpty(overrideExePath) && File.Exists(overrideExePath)))
                return;
            string projectBuild = ProjectBuildExePath();
            if (!File.Exists(projectBuild))
            {
                Logging.LogError("Mesh", $"Map spawner: overrideExePath '{overrideExePath}' does not exist, and neither does this project's build at {projectBuild}; cells cannot start");
                return;
            }
            Logging.LogWarning("Mesh", $"Map spawner: overrideExePath '{overrideExePath}' does not exist on this machine; starting cells from this project's build at {projectBuild}");
            overrideExePath = projectBuild;
        }

        /// <summary>
        /// In the editor the spawner hands every cell it starts publicAddress, saved with the prefab: clients, other cells'
        /// mesh links and the login server all connect to it. A private-network address that no interface of this machine
        /// has names another computer on the LAN (the prefab was saved for a two-machine run there), so cells started here
        /// would send every client and link to that computer. It is replaced, with a warning, by this machine's address on
        /// the same subnet, or loopback when it has none there.
        /// </summary>
        private void ResolveEditorPublicAddress()
        {
            if (!Application.isEditor)
                return;
            string chosen = ChooseAdvertisedAddress(publicAddress, LocalIPv4Addresses());
            if (chosen == publicAddress)
                return;
            Logging.LogWarning("Mesh", $"Map spawner: publicAddress {publicAddress} is another machine on this network; cells started here advertise {chosen} instead");
            publicAddress = chosen;
        }
#endif

        /// <summary>
        /// ChooseAdvertisedAddress returns the address cells started on this machine should advertise: the configured one,
        /// unless it is a private IPv4 address (10/8, 172.16/12, 192.168/16) that none of <paramref name="local"/> is. Then
        /// it is the local address on the same /24, or loopback. Hostnames, public and loopback addresses are kept: a public
        /// address may reach this machine through NAT.
        /// </summary>
        public static string ChooseAdvertisedAddress(string configured, IList<IPAddress> local)
        {
            if (!IPAddress.TryParse(configured, out IPAddress address) || address.AddressFamily != AddressFamily.InterNetwork || !IsPrivate(address))
                return configured;
            foreach (IPAddress mine in local)
            {
                if (mine.Equals(address))
                    return configured;
            }
            byte[] want = address.GetAddressBytes();
            foreach (IPAddress mine in local)
            {
                byte[] have = mine.GetAddressBytes();
                if (mine.AddressFamily == AddressFamily.InterNetwork && have[0] == want[0] && have[1] == want[1] && have[2] == want[2])
                    return mine.ToString();
            }
            return IPAddress.Loopback.ToString();
        }

        private static bool IsPrivate(IPAddress address)
        {
            byte[] b = address.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
        }

        /// <summary>The IPv4 unicast addresses of this machine's network interfaces that are up.</summary>
        private static List<IPAddress> LocalIPv4Addresses()
        {
            List<IPAddress> addresses = new List<IPAddress>();
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;
                foreach (UnicastIPAddressInformation info in nic.GetIPProperties().UnicastAddresses)
                {
                    if (info.Address.AddressFamily == AddressFamily.InterNetwork)
                        addresses.Add(info.Address);
                }
            }
            return addresses;
        }

        /// <summary>ProjectBuildExePath is where this project's player build lives: Build/Server/&lt;product name&gt;.exe beside Assets.</summary>
        public static string ProjectBuildExePath()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Build", "Server", Application.productName + ".exe"));
        }

        /// <summary>Whether this spawner is responsible for a cell (an empty list means all of them).</summary>
        public bool Serves(string cellId)
        {
            if (servedCells == null || servedCells.Length == 0)
                return true;
            for (int i = 0; i < servedCells.Length; ++i)
            {
                if (string.Equals(servedCells[i], cellId, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        private void OnRegisteredWithMesh(AckResponseCode responseCode)
        {
            if (responseCode != AckResponseCode.Success)
            {
                _kitOnRegister?.Invoke(responseCode);
                return;
            }
            SendHostReport();   // Slice 3: at every registration (the central may be new), then every HostReportSeconds
            // Tell the central which cells this host may start, so a cell wakes on the machine that can serve it and a
            // Dynamic family stays on its base's host. An empty list means "all of them", which is what a single-host
            // deployment has always meant; it is sent too (DESIGN 0.90), so the central can tell a spawner that serves
            // every cell from one whose list has not arrived yet.
            if (ClusterClient != null)
            {
                bool all = servedCells == null || servedCells.Length == 0;
                MeshServesMessage serves = new MeshServesMessage { cellIds = all ? new string[0] : servedCells };
                ClusterClient.SendPacket(0, LiteNetLib.DeliveryMethod.ReliableOrdered, MeshClusterMsg.Serves, writer => serves.Serialize(writer));
                Logging.Log("Mesh", all ? "Told the central this spawner serves every cell no other spawner advertises" : $"Told the central this spawner serves {string.Join(",", servedCells)}");
            }
            if (!_startOnce.TryStart())
            {
                Logging.Log("Mesh", "Map spawner registered again (the central restarted): its cells are already running, none started");
                return;
            }
            List<BaseMapInfo> maps = spawningMaps != null && spawningMaps.Count > 0 ? new List<BaseMapInfo>(spawningMaps) : new List<BaseMapInfo>(GameInstance.MapInfos.Values);
            List<BaseMapInfo> plain = new List<BaseMapInfo>();
            List<KeyValuePair<string, string>> cells = new List<KeyValuePair<string, string>>(); // (cell id, map id)
            foreach (BaseMapInfo map in maps)
            {
                if (map == null || (!map.IsAddressableSceneValid() && !map.IsSceneValid()))
                    continue;
                if (!world.HasLayout(map.Id))
                {
                    plain.Add(map);
                    continue;
                }
                AppendCellsToStart(world, map.Id, Serves, cells);
            }
            Logging.Log("Mesh", $"Map spawner: {cells.Count} cell server(s) for {maps.Count - plain.Count} meshed map(s), {plain.Count} map(s) on the kit's channels" + (servedCells != null && servedCells.Length > 0 ? $" (this host serves {string.Join(",", servedCells)})" : string.Empty));
            if (plain.Count > 0 || (spawningAllocateMaps != null && spawningAllocateMaps.Count > 0))
            {
                // The kit's handler spawns `spawningMaps` × channels; an empty list would mean every map, so it gets
                // the non-meshed ones only (a placeholder keeps it from falling back to all maps).
                spawningMaps = plain.Count > 0 ? plain : new List<BaseMapInfo> { null };
                _kitOnRegister?.Invoke(responseCode);
            }
            if (MeshSleepConfig.EnvEnabled || sleepCells)
            {
                Logging.Log("Mesh", $"Sleeping cells: {cells.Count} cell(s) start on demand (the central wakes them)");
                return;
            }
            SpawnCells(cells).Forget();
        }

        /// <summary>
        /// AppendCellsToStart adds (cell id, map id) for every cell of a map that a spawner starts at boot: the ones it serves
        /// (another host runs the rest), never a dynamic cell, which only the central's layout coordinator starts. The
        /// editor's spawner shares the central's registry, so it would otherwise see the coordinator's children.
        /// </summary>
        public static void AppendCellsToStart(MeshWorldRegistry world, string mapId, System.Func<string, bool> serves, List<KeyValuePair<string, string>> into)
        {
            IReadOnlyList<MeshCell> mapCells = world.CellsOf(mapId);
            for (int i = 0; i < mapCells.Count; ++i)
            {
                MeshCell cell = mapCells[i];
                if (cell.isDynamic || !serves(cell.id))
                    continue;
                into.Add(new KeyValuePair<string, string>(cell.id, mapId));
            }
        }

        /// <summary>HandleKillChannel kills one map-server process this host started, after MeshSpawnerKillRule's checks.</summary>
        private void HandleKillChannel(MessageHandlerData messageHandler)
        {
            if (!MeshWholeMessage.TryRead(messageHandler.Reader, out MeshKillChannelMessage message, out string readError))
            {
                Logging.LogWarning("Mesh", $"Spawner: kill request dropped: {readError}");
                return;
            }
            string refused = TryKill(message);
            if (refused == null)
                Logging.Log("Mesh", $"Spawner: killed {message.channelId} pid {message.processId} ({message.reason})");
            else
                Logging.LogWarning("Mesh", $"Spawner: kill of {message.channelId} pid {message.processId} refused ({refused})");
        }

        /// <summary>TryKill: null once the process is killed, else why it was not.</summary>
        private string TryKill(MeshKillChannelMessage message)
        {
            if (message.processId <= 0)
                return "no process id";
            System.Diagnostics.Process process;
            try
            {
                if (message.processId == System.Diagnostics.Process.GetCurrentProcess().Id)
                    return "that is this spawner";
                process = System.Diagnostics.Process.GetProcessById(message.processId);
            }
            catch (ArgumentException)
            {
                return "no such process";
            }
            catch (InvalidOperationException)
            {
                return "no such process";
            }
            using (process)
            {
                string executable;
                DateTime startedUtc;
                try
                {
                    executable = process.MainModule != null ? process.MainModule.FileName : string.Empty;
                    startedUtc = process.StartTime.ToUniversalTime();
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException || ex is NotSupportedException)
                {
                    return "the process could not be read (it exited, or access was denied)";
                }
                string why = MeshSpawnerKillRule.Check(executable, BuildExecutablePath(), startedUtc, message.incarnation);
                if (why != null)
                    return why;
                try
                {
                    process.Kill();
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException || ex is NotSupportedException)
                {
                    return "the kill failed (it exited, or access was denied)";
                }
            }
            return null;
        }

        /// <summary>The executable this spawner starts map servers from, as the kit resolves it (its own when ExePath is empty).</summary>
        private string BuildExecutablePath()
        {
            string path = ExePath;
            if (string.IsNullOrEmpty(path))
            {
                string[] args = Environment.GetCommandLineArgs();
                path = args.Length > 0 && File.Exists(args[0]) ? args[0] : System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
            }
            return MeshSpawnerKillRule.FullPath(path);
        }

        private async UniTaskVoid SpawnCells(List<KeyValuePair<string, string>> cells)
        {
            ParameterInfo[] parameters = s_spawnMap.GetParameters();
            object vecDefault = System.Activator.CreateInstance(parameters[4].ParameterType);
            for (int i = 0; i < cells.Count; ++i)
            {
                // SpawnMap(channelId, mapName, isAllocate, instanceId, warpPosition, overrideRotation, warpRotation, autoRestart, result)
                s_spawnMap.Invoke(this, new object[] { cells[i].Key, cells[i].Value, false, string.Empty, vecDefault, false, vecDefault, true, null });
                await UniTask.Delay(100); // the kit's own pacing between spawns
            }
        }
#endif
    }

    /// <summary>
    /// MeshSpawnerKillRule decides whether a pid named by a kill request is still the map server it was (Slice 2d decision
    /// 4): the build's executable, started within KillStartWindowSeconds of the incarnation's UTC ticks (a pid can be reused).
    /// </summary>
    public static class MeshSpawnerKillRule
    {
        public const double KillStartWindowSeconds = 60.0;

        /// <summary>Check: null when the process may be killed, else why not.</summary>
        public static string Check(string executable, string buildExecutable, DateTime processStartedUtc, string incarnation)
        {
            if (string.IsNullOrEmpty(executable) || string.IsNullOrEmpty(buildExecutable)
                || !string.Equals(FullPath(executable), FullPath(buildExecutable), StringComparison.OrdinalIgnoreCase))
                return $"it runs {FileName(executable)}, not the build's {FileName(buildExecutable)}";
            if (!MeshIncarnation.TryTicks(incarnation, out long ticks) || ticks > DateTime.MaxValue.Ticks)
                return $"incarnation {incarnation} is no start time";
            double apart = Math.Abs((processStartedUtc.ToUniversalTime() - new DateTime(ticks, DateTimeKind.Utc)).TotalSeconds);
            if (apart > KillStartWindowSeconds)
                return $"it started {apart.ToString("0", System.Globalization.CultureInfo.InvariantCulture)} s from incarnation {incarnation}, over {KillStartWindowSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture)} s: the pid was reused";
            return null;
        }

        /// <summary>FullPath makes a path absolute (as given when it cannot be).</summary>
        public static string FullPath(string path)
        {
            try
            {
                return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is System.Security.SecurityException)
            {
                return path;
            }
        }

        private static string FileName(string path)
        {
            if (string.IsNullOrEmpty(path))
                return "nothing known";
            try
            {
                return Path.GetFileName(path);
            }
            catch (ArgumentException)
            {
                return path;
            }
        }
    }
}
