// Server Meshing test rig — one group of MESH_LOAD_SHIPS (Phase 0 step 7). Rig-only: the space game at commit 1a3a58d9
// spawns its ships in waves instead.
using System;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>ServerMeshShipLoadGroup is one part of a ship load plan: Count ships of one authority on one cell.</summary>
    public readonly struct ServerMeshShipLoadGroup
    {
        /// <summary>ServerMeshShipLoadGroup creates a group; a null cell throws naming it.</summary>
        public ServerMeshShipLoadGroup(string cell, int count, ServerMeshShipAuthority authority)
        {
            Cell = cell ?? throw new ArgumentNullException(nameof(cell));
            Count = count;
            Authority = authority;
        }

        /// <summary>Cell is the id of the cell that spawns the group.</summary>
        public string Cell { get; }

        /// <summary>Count is how many ships the group spawns.</summary>
        public int Count { get; }

        /// <summary>Authority is who simulates the group's ships.</summary>
        public ServerMeshShipAuthority Authority { get; }
    }
}
