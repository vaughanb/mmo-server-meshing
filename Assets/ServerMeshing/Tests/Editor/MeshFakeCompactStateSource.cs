using LiteNetLib.Utils;
using LiteNetLibManager;
using MultiplayerARPG.MMO.Mesh.Sync;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>MeshFakeCompactStateSource is a MeshFakeStateSource with a programmable compact payload; it counts its compact writes.</summary>
    internal sealed class MeshFakeCompactStateSource : MeshFakeStateSource, IMeshCompactStateSource
    {
        public MeshFakeCompactStateSource(uint objectId, LiteNetLibIdentity identity) : base(objectId, identity)
        {
        }

        public byte[] compactPayload = { 0xC0 };
        public bool compactReturns = true;
        public int compactWrites;

        public bool WriteCompactState(long sampleTimestamp, NetDataWriter writer)
        {
            compactWrites++;
            if (!compactReturns)
                return false;
            writer.Put(compactPayload);
            return true;
        }
    }
}
