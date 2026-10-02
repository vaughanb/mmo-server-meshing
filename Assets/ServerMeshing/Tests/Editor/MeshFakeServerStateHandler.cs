using System;
using System.Collections.Generic;
using LiteNetLib.Utils;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>
    /// MeshFakeServerStateHandler is a kit movement data handler with one-shot flags (CE-12) that counts its consumes,
    /// and whose client-side read takes a set number of bytes and then optionally throws (CE-13).
    /// </summary>
    internal sealed class MeshFakeServerStateHandler : IEntityMovementDataHandler, IEntityMovementServerStateFlags
    {
        public MeshFakeServerStateHandler(uint objectId)
        {
            ObjectId = objectId;
        }

        public uint ObjectId { get; }
        public long ConnectionId => 6;
        public int consumes;
        public int bytesToRead;
        public bool throwAfterReading;

        public void ConsumeServerStateFlags()
        {
            consumes++;
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
            for (int i = 0; i < bytesToRead; ++i)
                reader.GetByte();
            if (throwAfterReading)
                throw new InvalidOperationException("the fake state reader failed on purpose");
        }

        public bool WriteClientState(long writeTimestamp, NetDataWriter writer, out bool shouldSendReliably)
        {
            shouldSendReliably = false;
            return false;
        }

        public bool WriteServerState(long writeTimestamp, NetDataWriter writer, out bool shouldSendReliably)
        {
            shouldSendReliably = false;
            return true;
        }
    }
}
