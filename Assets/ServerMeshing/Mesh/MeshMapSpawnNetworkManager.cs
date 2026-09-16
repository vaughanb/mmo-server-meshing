// Server Meshing add-on — MeshMapSpawnNetworkManager: the kit's map spawner starting one server per cell.
//
// The kit starts every map in `spawningMaps` on every channel in `spawningChannelIds` (a cartesian product in a
// private loop). A cell is a channel, and two meshed maps have different cells, so this subclass takes over the
// start-up spawn once the spawner is registered with the cluster: a meshed map (one with a layout in the world
// registry) is started on its own cells only, through the kit's own private SpawnMap (reached by reflection — the
// kit stays untouched); maps without a layout are left to the kit's loop over its channel list.
using System.Collections.Generic;
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

        private static readonly MethodInfo s_spawnMap = FindSpawnMap();
        private ClusterClient.OnResponseAppServerRegister _kitOnRegister;

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
        }

        private void OnRegisteredWithMesh(AckResponseCode responseCode)
        {
            if (responseCode != AckResponseCode.Success)
            {
                _kitOnRegister?.Invoke(responseCode);
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
                IReadOnlyList<MeshCell> mapCells = world.CellsOf(map.Id);
                for (int i = 0; i < mapCells.Count; ++i)
                {
                    if (!Serves(mapCells[i].id))
                        continue;   // another host runs this cell
                    cells.Add(new KeyValuePair<string, string>(mapCells[i].id, map.Id));
                }
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
    }
}
