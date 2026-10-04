using LiteNetLibManager;
using MultiplayerARPG.MMO.Mesh.Sync;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>MeshFakeSenderOwnedSource is a MeshFakeStateSource the sender owns; it counts its OnSenderReleased calls.</summary>
    internal sealed class MeshFakeSenderOwnedSource : MeshFakeStateSource, IMeshSenderOwnedSource
    {
        public MeshFakeSenderOwnedSource(uint objectId, LiteNetLibIdentity identity) : base(objectId, identity)
        {
        }

        public int released;

        public void OnSenderReleased()
        {
            released++;
        }
    }
}
