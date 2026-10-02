// Server Meshing add-on — MeshMapSpawnNetworkManager: the kit's map spawner starting one server per cell.
//
// The kit starts every map in `spawningMaps` on every channel in `spawningChannelIds` (a cartesian product in a
// private loop). A cell is a channel, and two meshed maps have different cells, so this subclass takes over the
// start-up spawn once the spawner is registered with the cluster: a meshed map (one with a layout in the world
// registry) is started on its own cells only, through the kit's own private SpawnMap (reached by reflection — the
// kit stays untouched); maps without a layout are left to the kit's loop over its channel list.
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
                 "(the cluster keys a map server by cell id and rejects the second). Rig override: MESH_SPAWN_CELLS=C,N.")]
        public string[] servedCells = new string[0];

        public const string ENV_SERVED_CELLS = "MESH_SPAWN_CELLS";

#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        private static readonly MethodInfo s_spawnMap = FindSpawnMap();
        private ClusterClient.OnResponseAppServerRegister _kitOnRegister;
        // The spawner registers again after every central restart; its cells are still running then (the spawner
        // outlives the central), so they are started once per spawner lifetime, never again on a re-registration.
        private MeshSpawnOnce _startOnce = new MeshSpawnOnce();

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
            // Tell the central which cells this host may start, so a cell wakes on the machine that can serve it.
            // An empty list means "all of them", which is what a single-host deployment has always meant.
            if (servedCells != null && servedCells.Length > 0 && ClusterClient != null)
            {
                MeshServesMessage serves = new MeshServesMessage { cellIds = servedCells };
                ClusterClient.SendPacket(0, LiteNetLib.DeliveryMethod.ReliableOrdered, MeshClusterMsg.Serves, writer => serves.Serialize(writer));
                Logging.Log("Mesh", $"Told the central this spawner serves {string.Join(",", servedCells)}");
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
}
