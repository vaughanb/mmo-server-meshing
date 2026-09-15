// Server Meshing add-on — MeshCentralNetworkManager: the kit's central server with the world registry's cells as channels.
//
// A cell is served by the map server whose channel id is the cell id, and the kit's login and cluster code accept a
// channel only if the central server lists it (character selection, the per-channel connection limit, the channel
// list the client sees). This subclass appends every cell of every layout in the registry to the central's channel
// list before it is read (the configured channels are kept), so no cell has to be typed into the server config.
using System.Collections.Generic;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public class MeshCentralNetworkManager : CentralNetworkManager
    {
        [Header("Server Meshing")]
        [Tooltip("World registry: every cell of every layout becomes a channel of this central server.")]
        public MeshWorldRegistry world;

        protected override void Initialize()
        {
            if (world != null)
            {
                if (channels == null)
                    channels = new List<ChannelData>();
                HashSet<string> known = new HashSet<string>();
                foreach (ChannelData channel in channels)
                    known.Add(channel.id);
                int added = 0;
                IReadOnlyList<MeshCell> cells = world.Cells;
                for (int i = 0; i < cells.Count; ++i)
                {
                    if (!known.Add(cells[i].id))
                        continue;
                    channels.Add(new ChannelData { id = cells[i].id, title = cells[i].id, maxConnections = defaultChannelMaxConnections > 0 ? defaultChannelMaxConnections : 500 });
                    added++;
                }
                Logging.Log("Mesh", $"Central channels from the world registry: {channels.Count} ({added} added)");
            }
            base.Initialize();
        }
    }
}
