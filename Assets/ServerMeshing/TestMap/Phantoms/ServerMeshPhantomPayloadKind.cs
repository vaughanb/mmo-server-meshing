// Server Meshing add-on — rig phantom payload kind: what a character phantom uploads (MESH_PHANTOM_PAYLOAD).
namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>ServerMeshPhantomPayloadKind is what a character phantom uploads (MESH_PHANTOM_PAYLOAD).</summary>
    public enum ServerMeshPhantomPayloadKind
    {
        /// <summary>Transform uploads are the kit's NotSecure sync transform: state, extra state, position, yaw.</summary>
        Transform = 0,
        /// <summary>Input uploads are the kit's ServerAuthoritative key-movement input, sent only when it changes.</summary>
        Input = 1,
    }
}
