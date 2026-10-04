// Server Meshing test rig — the stand-in ship's record layouts (Phase 0 step 7, MESH_SHIP_WIRE). Legacy54 is the
// space game's record at commit 1a3a58d9; Header56 and Compact28 are the rig's.
namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>ServerMeshShipWireKind is a stand-in ship's record layout.</summary>
    public enum ServerMeshShipWireKind : byte
    {
        /// <summary>Legacy54 is the space game's 54-byte record with no header.</summary>
        Legacy54 = 0,

        /// <summary>Header56 is a 2-byte header (authority, flight state, epoch) followed by the 54 bytes.</summary>
        Header56 = 1,

        /// <summary>Compact28 is the rig's 28-byte far-observer record; its Full record is header56.</summary>
        Compact28 = 2,
    }
}
