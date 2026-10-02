using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using MultiplayerARPG.MMO.Mesh.Sync;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    internal sealed class MeshFakeReplaceableLayer : IEntityMovementDataHandler, IMeshReplaceableHandlerLayer
    {
        public MeshFakeReplaceableLayer(uint objectId)
        {
            ObjectId = objectId;
        }

        public uint ObjectId { get; }
        public long ConnectionId => -1;
        public int replacedCount;
        public Action onReplaced;

        public void OnReplaced()
        {
            replacedCount++;
            onReplaced?.Invoke();
        }

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
            writer.Put((byte)0xAA);
            return true;
        }
    }
}
