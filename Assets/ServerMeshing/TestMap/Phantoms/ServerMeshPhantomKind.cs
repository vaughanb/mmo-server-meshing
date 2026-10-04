// Server Meshing add-on — rig phantom kind: what one phantom owns (MESH_PHANTOM's third field).
namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>ServerMeshPhantomKind is what a phantom owns: a walking character, a hidden eye or a mirror ship.</summary>
    public enum ServerMeshPhantomKind
    {
        /// <summary>Character phantoms own a player character fed by uploads.</summary>
        Character = 0,
        /// <summary>Eye phantoms own a hidden identity that follows a host and observes what is around it.</summary>
        Eye = 1,
        /// <summary>Ship phantoms own an OwnerMirror ship that flies a seeded path, uploading MESH_SHIP_WIRE records.</summary>
        Ship = 2,
    }
}
