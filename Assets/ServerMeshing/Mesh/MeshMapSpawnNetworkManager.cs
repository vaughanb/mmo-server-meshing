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
                    cells.Add(new KeyValuePair<string, string>(mapCells[i].id, map.Id));
            }
            Logging.Log("Mesh", $"Map spawner: {cells.Count} cell server(s) for {maps.Count - plain.Count} meshed map(s), {plain.Count} map(s) on the kit's channels");
            if (plain.Count > 0 || (spawningAllocateMaps != null && spawningAllocateMaps.Count > 0))
            {
                // The kit's handler spawns `spawningMaps` × channels; an empty list would mean every map, so it gets
                // the non-meshed ones only (a placeholder keeps it from falling back to all maps).
                spawningMaps = plain.Count > 0 ? plain : new List<BaseMapInfo> { null };
                _kitOnRegister?.Invoke(responseCode);
            }
            SpawnCells(cells).Forget();
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
