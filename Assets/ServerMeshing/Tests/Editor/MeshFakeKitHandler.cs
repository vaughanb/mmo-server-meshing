using System.Collections.Generic;
using LiteNetLib.Utils;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    internal sealed class MeshFakeKitHandler : IEntityMovementDataHandler
    {
        public MeshFakeKitHandler(uint objectId)
        {
            ObjectId = objectId;
        }

        public uint ObjectId { get; }
        public long ConnectionId => 5;
        public int serverWrites;

        public MovementData CreateMovementData(out List<EntityMovementForceApplier> forceAppliers)
        {
            forceAppliers = null;
            return default;
        }

        public void ReadClientStateAtServer(long peerTimestamp, NetDataReader reader)
        {
        }

        public void ReadServerStateAtClient(long peerTimestamp, NetDataReader reader)
        {
        }

        public bool WriteClientState(long writeTimestamp, NetDataWriter writer, out bool shouldSendReliably)
        {
            shouldSendReliably = false;
            return false;
        }

        public bool WriteServerState(long writeTimestamp, NetDataWriter writer, out bool shouldSendReliably)
        {
            shouldSendReliably = false;
            serverWrites++;
            writer.Put((byte)0xEE);
            return true;
        }
    }
}
