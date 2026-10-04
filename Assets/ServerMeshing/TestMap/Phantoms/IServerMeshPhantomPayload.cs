// Server Meshing add-on — rig phantom upload payload: the bytes after a phantom upload's header, written as the kit's own client would write them.
using LiteNetLib.Utils;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// IServerMeshPhantomPayload writes a phantom upload's payload: what follows [packed ushort 119][packed long
    /// timestamp][packed uint object id]. The virtual feeder and the sink use the same writers. Implementations hold no
    /// per-phantom state and allocate nothing; the one piece of state they need is passed by reference.
    /// </summary>
    public interface IServerMeshPhantomPayload
    {
        /// <summary>
        /// TryWrite appends the payload for a phantom at pose (character payloads read only its position and yaw in
        /// degrees). moving is false while it stands; teleport asks for the one frame that confirms a server teleport (the
        /// pose's position is then the server's destination). stopSent is the phantom's "a stop was sent since it last moved"
        /// flag. It returns false, writing nothing, when this payload sends nothing now; reliable says whether the kit would
        /// send this frame ReliableOrdered.
        /// </summary>
        bool TryWrite(NetDataWriter writer, bool moving, bool teleport, in ServerMeshPhantomPose pose, ref bool stopSent, out bool reliable);
    }
}
