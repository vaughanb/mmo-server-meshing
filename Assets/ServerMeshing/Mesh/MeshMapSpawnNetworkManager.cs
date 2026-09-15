// Server Meshing add-on — MeshMapSpawnNetworkManager: the kit's map spawner with the world registry as its channel source.
//
// The kit starts every map in `spawningMaps` on every channel in `spawningChannelIds`. A cell is a channel, so the
// channels a meshed map needs are its cells: this subclass adds every cell id of every meshed map it spawns to the
// channel list before the kit reads it (explicit entries are kept). Known limit: the kit's start-up loop is a
// cartesian product and private, so with two meshed maps of different cell sets each map is also started on the
// other's cells — harmless (they register and serve nothing) but wasteful; to be replaced when the second map lands.
using System.Collections.Generic;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public class MeshMapSpawnNetworkManager : MapSpawnNetworkManager
    {
        [Header("Server Meshing")]
        [Tooltip("World registry: the cells (= channels) of every meshed map this spawner starts.")]
        public MeshWorldRegistry world;

        protected override void Initialize()
        {
            if (world != null)
            {
                if (spawningChannelIds == null)
                    spawningChannelIds = new List<string>();
                List<BaseMapInfo> maps = spawningMaps != null && spawningMaps.Count > 0 ? spawningMaps : new List<BaseMapInfo>(GameInstance.MapInfos.Values);
                int added = 0;
                foreach (BaseMapInfo map in maps)
                {
                    if (map == null || !world.HasLayout(map.Id))
                        continue;
                    IReadOnlyList<MeshCell> cells = world.CellsOf(map.Id);
                    for (int i = 0; i < cells.Count; ++i)
                    {
                        if (spawningChannelIds.Contains(cells[i].id))
                            continue;
                        spawningChannelIds.Add(cells[i].id);
                        added++;
                    }
                }
                Logging.Log("Mesh", $"Map spawner channels from the world registry: {string.Join(",", spawningChannelIds)} ({added} added)");
            }
            base.Initialize();
        }
    }
}
