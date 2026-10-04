// Server Meshing test rig — who simulates a stand-in ship (Phase 0 step 7). Modelled by reading the space game at
// commits 1a3a58d9 and c2eb4154, where authority is implicit (owner client, or the server with or without an AI pilot).
namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshShipAuthority is who simulates a stand-in ship: a rig property of the ship (its load group, the window
    /// counters), never on the wire since step 7b, where header byte 0 carries the space game's wire mode instead.
    /// </summary>
    public enum ServerMeshShipAuthority : byte
    {
        /// <summary>OwnerMirror is a ship its owner simulates; the server holds a kinematic mirror fed by uploads.</summary>
        OwnerMirror = 0,

        /// <summary>ServerAI is a ship the server simulates under an AI pilot.</summary>
        ServerAI = 1,

        /// <summary>Parked is a ship the server simulates with no pilot.</summary>
        Parked = 2,
    }
}
