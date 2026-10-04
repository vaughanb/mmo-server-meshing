using System.Collections.Generic;
using LiteNetLib.Utils;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>
    /// MeshFakePassKitHandler is a kit movement handler in the CE-12 pass shape: pending teleport, jump and dash flags that
    /// its write only reads (writing the kit's 22-byte layout, reliable while any is pending) and ConsumeServerStateFlags
    /// clears. It counts writes, consumes and uploads; its owner is settable, and a test can make its write return false,
    /// throw, reset the writer (breaking append-only), or carry padding bytes after the layout (as force appliers would).
    /// </summary>
    internal sealed class MeshFakePassKitHandler : IEntityMovementDataHandler, IEntityMovementServerStateFlags
    {
        public const uint IsJump = 64;
        public const uint IsTeleport = 128;
        public const uint IsDash = 2048;

        public MeshFakePassKitHandler(uint objectId)
        {
            ObjectId = objectId;
        }

        public uint ObjectId { get; }
        public long ConnectionId { get; set; } = -1;
        public bool pendingTeleport;
        public bool pendingJump;
        public bool pendingDash;
        public Vector3 position = new Vector3(1.5f, 0f, -2.5f);
        public bool returns = true;
        public bool throws;
        public bool resetWriter;
        public int padding;
        public int writes;
        public int consumes;
        public int uploads;
        public int clientReads;

        public void ConsumeServerStateFlags()
        {
            consumes++;
            pendingTeleport = false;
            pendingJump = false;
            pendingDash = false;
        }

        public MovementData CreateMovementData(out List<EntityMovementForceApplier> forceAppliers)
        {
            forceAppliers = null;
            return default;
        }

        public void ReadClientStateAtServer(long peerTimestamp, NetDataReader reader)
        {
            uploads++;
        }

        public void ReadServerStateAtClient(long peerTimestamp, NetDataReader reader)
        {
            clientReads++;
        }

        public bool WriteClientState(long writeTimestamp, NetDataWriter writer, out bool shouldSendReliably)
        {
            shouldSendReliably = false;
            return false;
        }

        public bool WriteServerState(long writeTimestamp, NetDataWriter writer, out bool shouldSendReliably)
        {
            writes++;
            if (throws)
                throw new System.InvalidOperationException("the fake kit handler failed on purpose");
            if (!returns)
            {
                shouldSendReliably = false;
                return false;
            }
            if (resetWriter)
                writer.Reset();
            uint flags = 0;
            if (pendingJump)
                flags |= IsJump;
            if (pendingDash)
                flags |= IsDash;
            if (pendingTeleport)
                flags |= IsTeleport;
            shouldSendReliably = flags != 0;
            MeshKinematicCharacterBody.WriteKitTransform(writer, flags, 0, position, 90f);
            for (int i = 0; i < padding; ++i)
                writer.Put((byte)i);
            return true;
        }
    }
}
