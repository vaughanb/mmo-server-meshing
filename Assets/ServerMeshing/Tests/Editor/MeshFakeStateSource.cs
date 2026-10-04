using System;
using LiteNetLib.Utils;
using LiteNetLibManager;
using MultiplayerARPG.MMO.Mesh.Sync;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>
    /// MeshFakeStateSource is a programmable state source: its payload, reliable flag, return value, a throw, a writer
    /// reset or rewind, and its DiscreteVersion are set by the test; it counts its writes.
    /// </summary>
    internal class MeshFakeStateSource : IMeshStateSource
    {
        public MeshFakeStateSource(uint objectId, LiteNetLibIdentity identity)
        {
            ObjectId = objectId;
            Identity = identity;
        }

        public uint ObjectId { get; }
        public LiteNetLibIdentity Identity { get; set; }
        public uint DiscreteVersion { get; set; }
        public bool IsAtRest { get; set; }

        public byte[] payload = { 0x10, 0x20, 0x30 };
        public bool reliable;
        public bool returns = true;
        public bool throws;
        public bool resetWriter;
        public bool rewindWriter;
        public int writes;
        public long lastTimestamp;

        public bool WriteState(long sampleTimestamp, NetDataWriter writer, out bool sendReliably)
        {
            writes++;
            lastTimestamp = sampleTimestamp;
            if (throws)
                throw new InvalidOperationException("the fake source failed {on purpose} with an Exception");
            sendReliably = reliable;
            if (resetWriter)
                writer.Reset();
            if (rewindWriter)
                writer.SetPosition(writer.Length - 1);
            if (!returns)
                return false;
            writer.Put(payload);
            return true;
        }
    }
}
