using LiteNetLib.Utils;
using NUnit.Framework;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    // DESIGN 1.28: a monster handoff carries the walk the monster was on and its AI's bookkeeping.
    public class MeshMonsterMotionCarryTests
    {
        private static MonsterHandoffMessage RoundTrip(MonsterHandoffMessage msg)
        {
            NetDataWriter writer = new NetDataWriter();
            msg.Serialize(writer);
            NetDataReader reader = new NetDataReader(writer.CopyData());
            MonsterHandoffMessage back = default;
            back.Deserialize(reader);
            Assert.That(reader.EndOfData, Is.True, "the reader ends at the end of the message");
            return back;
        }

        private static MonsterHandoffMessage Sample(bool walking)
        {
            return new MonsterHandoffMessage
            {
                meshId = "cell-4-4:abc:1", entityId = 17, dataId = 3, level = 2, currentHp = 40,
                position = new Vector3(1f, 20f, 506f), yaw = 90f, spawnPosition = new Vector3(0.5f, 20f, 509f),
                homeZoneId = "cell-4-4", targetMeshId = string.Empty,
                walking = walking, destination = walking ? new Vector3(-2.5f, 20f, 507f) : Vector3.zero,
                extraMovementState = (byte)ExtraMovementState.IsWalking, ai = new byte[] { 1, 2, 3 },
            };
        }

        [TestCase(true)]
        [TestCase(false)]
        public void MonsterHandoff_RoundTrip_KeepsTheWalk(bool walking)
        {
            MonsterHandoffMessage back = RoundTrip(Sample(walking));

            Assert.That(back.walking, Is.EqualTo(walking));
            Assert.That(back.destination, Is.EqualTo(walking ? new Vector3(-2.5f, 20f, 507f) : Vector3.zero));
            Assert.That(back.extraMovementState, Is.EqualTo((byte)ExtraMovementState.IsWalking));
            Assert.That(back.ai, Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(back.targetMeshId, Is.EqualTo(string.Empty));
            Assert.That(back.spawnPosition, Is.EqualTo(new Vector3(0.5f, 20f, 509f)));
        }

        [Test]
        public void MonsterHandoff_NullAi_ArrivesEmpty()
        {
            MonsterHandoffMessage msg = Sample(false);
            msg.ai = null;

            Assert.That(RoundTrip(msg).ai, Is.Empty);
        }

        [Test]
        public void AiState_RoundTrip_KeepsEveryField()
        {
            MeshMonsterAiState state = new MeshMonsterAiState { reachedSpawnPoint = true, wanderCountDown = 1.25f, wanderDelay = 3.5f, followEnemyElapsed = 7f };
            NetDataWriter writer = new NetDataWriter();
            state.Write(writer);
            NetDataReader reader = new NetDataReader(writer.CopyData());

            MeshMonsterAiState back = MeshMonsterAiState.Read(reader);

            Assert.That(back.reachedSpawnPoint, Is.True);
            Assert.That(back.wanderCountDown, Is.EqualTo(1.25f));
            Assert.That(back.wanderDelay, Is.EqualTo(3.5f));
            Assert.That(back.followEnemyElapsed, Is.EqualTo(7f));
            Assert.That(reader.EndOfData, Is.True);
        }

        [Test]
        public void ReadAi_WithoutACarrierOrBytes_IsFalse()
        {
            Assert.That(MeshMonsterMotion.ReadAi(null, new byte[] { 1 }), Is.False);
            Assert.That(MeshMonsterMotion.WriteAi(null), Is.Empty);
            Assert.That(MeshMonsterMotion.TryGetDestination(null, out _), Is.False);
        }
    }
}
