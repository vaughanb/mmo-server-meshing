// Server Meshing test rig — who simulates a stand-in ship (Phase 0 step 7). Modelled by reading the space game at
// commit 1a3a58d9, where authority is implicit (owner client, or the server with or without an AI pilot).
namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>ServerMeshShipAuthority is who simulates a stand-in ship; the values are the header56 authority bits (3 is reserved).</summary>
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
