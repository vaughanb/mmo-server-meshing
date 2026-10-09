using System.Collections.Generic;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>
    /// Step 8b decision 17: an unowned ship acknowledges its teleport at once (TeleportShip) and when its owner leaves with
    /// one pending (OnSetOwnerClient); MESH_SHIP_SELF_ACK=1 keeps step 7's two-tick self-acknowledgement.
    /// </summary>
    public class ServerMeshShipTeleportAckTests
    {
        private const long Unowned = -1;
        private const long Owner = 5;

        private static IEnumerable<TestCaseData> AcknowledgesCases()
        {
            yield return new TestCaseData(false, false, Unowned, (byte)1, (byte)0, false).SetName("AcknowledgesUnowned_OnAClient_No");
            yield return new TestCaseData(true, true, Unowned, (byte)1, (byte)0, false).SetName("AcknowledgesUnowned_SelfAckOne_No");
            yield return new TestCaseData(true, false, Owner, (byte)1, (byte)0, false).SetName("AcknowledgesUnowned_Owned_No");
            yield return new TestCaseData(true, false, 0L, (byte)1, (byte)0, false).SetName("AcknowledgesUnowned_OwnedByConnectionZero_No");
            yield return new TestCaseData(true, false, Unowned, (byte)3, (byte)3, false).SetName("AcknowledgesUnowned_NothingPending_No");
            yield return new TestCaseData(true, false, Unowned, (byte)1, (byte)0, true).SetName("AcknowledgesUnowned_UnownedPending_Yes");
            yield return new TestCaseData(true, false, Unowned, (byte)0, (byte)255, true).SetName("AcknowledgesUnowned_SequenceWrapped_Yes");
        }

        [TestCaseSource(nameof(AcknowledgesCases))]
        public void AcknowledgesUnowned(bool isServer, bool selfAck, long connectionId, byte sequence, byte acknowledged, bool expected)
        {
            Assert.That(ServerMeshShipMovement.AcknowledgesUnowned(isServer, selfAck, connectionId, sequence, acknowledged), Is.EqualTo(expected));
        }

        [TestCase((byte)1, (byte)0, true)]
        [TestCase((byte)0, (byte)255, true)]
        [TestCase((byte)4, (byte)4, false)]
        public void SendsReliably_WhileTheSequenceIsUnacknowledged(byte sequence, byte acknowledged, bool expected)
        {
            Assert.That(ServerMeshShipMovement.SendsReliably(sequence, acknowledged), Is.EqualTo(expected));
        }

        [Test]
        public void UnownedTeleport_AcknowledgedAtOnce_NextRecordUnreliableAndCompactAllowed()
        {
            byte acknowledged = 0;
            byte sequence = Teleport(0, ref acknowledged, false, Unowned);

            Assert.That(acknowledged, Is.EqualTo(sequence));
            Assert.That(ServerMeshShipMovement.SendsReliably(sequence, acknowledged), Is.False);
            Assert.That(ServerMeshShipMovement.CompactAllowed(ServerMeshShipWireKind.Compact28, sequence, acknowledged), Is.True);
            Assert.That(ServerMeshShipMovement.PackDiscreteVersion(sequence, 0, 0), Is.Not.EqualTo(ServerMeshShipMovement.PackDiscreteVersion(0, 0, 0)),
                "the discrete version still bumps, so observers snap");
        }

        [Test]
        public void OwnedTeleport_StaysPendingAndReliableUntilTheOwnerEchoes()
        {
            byte acknowledged = 0;
            byte sequence = Teleport(0, ref acknowledged, false, Owner);

            Assert.That(acknowledged, Is.EqualTo(0));
            Assert.That(ServerMeshShipMovement.SendsReliably(sequence, acknowledged), Is.True);
            Assert.That(ServerMeshShipMovement.CompactAllowed(ServerMeshShipWireKind.Compact28, sequence, acknowledged), Is.False);
            Assert.That(ServerMeshShipMovement.SelfAckDue(false, false, sequence, acknowledged, 100u, 0u), Is.False, "nothing but the owner's upload acknowledges");
        }

        [Test]
        public void SelfAckOne_UnownedTeleport_PendingForTwoTicksThenAcknowledged()
        {
            byte acknowledged = 0;
            byte sequence = Teleport(0, ref acknowledged, true, Unowned);
            const uint teleportTick = 40;

            Assert.That(acknowledged, Is.EqualTo(0), "no immediate acknowledgement under MESH_SHIP_SELF_ACK=1");
            Assert.That(ServerMeshShipMovement.SendsReliably(sequence, acknowledged), Is.True);
            Assert.That(ServerMeshShipMovement.SelfAckDue(true, true, sequence, acknowledged, teleportTick, teleportTick), Is.False);
            Assert.That(ServerMeshShipMovement.SelfAckDue(true, true, sequence, acknowledged, teleportTick + 1, teleportTick), Is.False);
            Assert.That(ServerMeshShipMovement.SelfAckDue(true, true, sequence, acknowledged, teleportTick + 2, teleportTick), Is.True);
        }

        [Test]
        public void OwnerLeavesWithATeleportPending_Acknowledged()
        {
            byte acknowledged = 0;
            byte sequence = Teleport(0, ref acknowledged, false, Owner);
            Assert.That(ServerMeshShipMovement.SendsReliably(sequence, acknowledged), Is.True);

            OwnerChanged(sequence, ref acknowledged, false, Unowned);

            Assert.That(acknowledged, Is.EqualTo(sequence));
            Assert.That(ServerMeshShipMovement.SendsReliably(sequence, acknowledged), Is.False);
        }

        [Test]
        public void OwnerLeavesUnderSelfAckOne_StillPending()
        {
            byte acknowledged = 0;
            byte sequence = Teleport(0, ref acknowledged, true, Owner);

            OwnerChanged(sequence, ref acknowledged, true, Unowned);

            Assert.That(acknowledged, Is.EqualTo(0));
            Assert.That(ServerMeshShipMovement.SendsReliably(sequence, acknowledged), Is.True);
        }

        [Test]
        public void OwnerChangesToAnotherOwner_StillPending()
        {
            byte acknowledged = 0;
            byte sequence = Teleport(0, ref acknowledged, false, Owner);

            OwnerChanged(sequence, ref acknowledged, false, Owner + 1);

            Assert.That(acknowledged, Is.EqualTo(0));
        }

        [Test]
        public void UnownedThenOwned_AnOlderEchoMakesTheTeleportPendingAgain()
        {
            byte acknowledged = 0;
            byte sequence = Teleport(0, ref acknowledged, false, Unowned);
            Assert.That(ServerMeshShipMovement.SendsReliably(sequence, acknowledged), Is.False);

            acknowledged = 0; // the new owner's first upload echoes the sequence it applied (ReadClientStateAtServer)

            Assert.That(ServerMeshShipMovement.IsStaleUpload(acknowledged, sequence), Is.True);
            Assert.That(ServerMeshShipMovement.SendsReliably(sequence, acknowledged), Is.True);
        }

        // TeleportShip's sequence step: the bump, then the immediate acknowledgement of an unowned ship.
        private static byte Teleport(byte sequence, ref byte acknowledged, bool selfAck, long connectionId)
        {
            unchecked { sequence++; }
            if (ServerMeshShipMovement.AcknowledgesUnowned(true, selfAck, connectionId, sequence, acknowledged))
                acknowledged = sequence;
            return sequence;
        }

        // OnSetOwnerClient on the server after the kit set the new connection id.
        private static void OwnerChanged(byte sequence, ref byte acknowledged, bool selfAck, long connectionId)
        {
            if (ServerMeshShipMovement.AcknowledgesUnowned(true, selfAck, connectionId, sequence, acknowledged))
                acknowledged = sequence;
        }
    }
}
