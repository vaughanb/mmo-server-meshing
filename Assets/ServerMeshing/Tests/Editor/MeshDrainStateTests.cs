using System.Collections.Generic;
using LiteNetLib.Utils;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>Slice 2c decisions 9 and 10: what a Draining cell still holds, its predicate and text, and the evidence wire version 2.</summary>
    public class MeshDrainStateTests
    {
        private static MeshDrainState Every(int value)
        {
            return new MeshDrainState
            {
                characters = value, summons = value, monsters = value, itemDrops = value, rewardDrops = value, harvestables = value,
                buildings = value, connections = value, inProgress = value, pendingAcks = value, rolledBack = value, deferredAcks = value,
                reserved = value, incoming = value, staticLoans = value, serverCharacters = value,
            };
        }

        [Test]
        public void IsDrained_EveryCountButBuildingsIsZero()
        {
            Assert.That(default(MeshDrainState).IsDrained, Is.True);
            Assert.That(new MeshDrainState { buildings = 3 }.IsDrained, Is.True, "buildings are Slice 4: reported, never waited for");
            string[] names = { "characters", "summons", "monsters", "itemDrops", "rewardDrops", "harvestables", "connections", "inProgress",
                "pendingAcks", "rolledBack", "deferredAcks", "reserved", "incoming", "staticLoans", "serverCharacters" };
            foreach (string name in names)
            {
                object boxed = default(MeshDrainState);
                typeof(MeshDrainState).GetField(name).SetValue(boxed, 1);
                Assert.That(((MeshDrainState)boxed).IsDrained, Is.False, name);
            }
        }

        [Test]
        public void Describe_ListsTheNonZeroCountsInDecisionNineOrder()
        {
            Assert.That(default(MeshDrainState).Describe(), Is.EqualTo("nothing"));
            Assert.That(new MeshDrainState { pendingAcks = 1, characters = 1 }.Describe(), Is.EqualTo("characters=1 pendingAcks=1"));
            Assert.That(new MeshDrainState { buildings = 2, serverCharacters = 4 }.Describe(), Is.EqualTo("buildings=2 serverCharacters=4"));
            Assert.That(Every(1).Describe(), Is.EqualTo("characters=1 summons=1 monsters=1 itemDrops=1 rewardDrops=1 harvestables=1 buildings=1 "
                + "connections=1 inProgress=1 pendingAcks=1 rolledBack=1 deferredAcks=1 reserved=1 incoming=1 staticLoans=1 serverCharacters=1"));
            Assert.That(Every(7).Describe(), Does.Not.Contain("{").And.Not.Contain("\n"));
        }

        [Test]
        public void Wire_RoundTripsEveryCountInOrder()
        {
            MeshDrainState state = new MeshDrainState
            {
                characters = 1, summons = 2, monsters = 3, itemDrops = 4, rewardDrops = 5, harvestables = 6, buildings = 7, connections = 8,
                inProgress = 9, pendingAcks = 10, rolledBack = 11, deferredAcks = 12, reserved = 13, incoming = 14, staticLoans = 15, serverCharacters = 16,
            };
            NetDataWriter writer = new NetDataWriter();
            state.Put(writer);
            Assert.That(writer.Length, Is.EqualTo(16 * 4), "sixteen ints");
            NetDataReader reader = new NetDataReader(writer.CopyData());
            Assert.That(reader.GetInt(), Is.EqualTo(1), "characters first");
            reader = new NetDataReader(writer.CopyData());
            Assert.That(MeshDrainState.Get(reader), Is.EqualTo(state));
            Assert.That(reader.AvailableBytes, Is.EqualTo(0));
        }

        [Test]
        public void Max_IsTheMostOfEachCount()
        {
            MeshDrainState a = new MeshDrainState { characters = 2, connections = 0, buildings = 1 };
            MeshDrainState b = new MeshDrainState { characters = 1, connections = 3 };
            MeshDrainState max = MeshDrainState.Max(a, b);
            Assert.That((max.characters, max.connections, max.buildings, max.monsters), Is.EqualTo((2, 3, 1, 0)));
            Assert.That(MeshDrainState.Max(Every(1), default(MeshDrainState)), Is.EqualTo(Every(1)));
        }

        [Test]
        public void Evidence_V2_CarriesTheDrainStateAndDrained()
        {
            Assert.That(MeshLayoutEvidenceMessage.WireVersion, Is.EqualTo((byte)2));
            MeshRect given = new MeshRect(0f, -100f, 100f, 100f);
            MeshLayoutEvidenceMessage built = MeshSplitEvidence.Assemble("C.1", "inc", new MeshLayoutPair(3, 9), 20f, 10f,
                new List<MeshNeighbourEvidence>(), 0, 0f, 4f, 16.67f, true, given, new MeshDrainCounts { characters = 1 });
            MeshDrainState state = new MeshDrainState { characters = 1, pendingAcks = 2, buildings = 1 };
            built = MeshSplitEvidence.WithDrainState(built, true, state);
            Assert.That(built.drained, Is.False);

            NetDataWriter writer = new NetDataWriter();
            built.Serialize(writer);
            Assert.That(MeshWholeMessage.TryRead(new NetDataReader(writer.CopyData()), out MeshLayoutEvidenceMessage read, out string error), Is.True, error);
            Assert.That((read.cellId, read.pair, read.shrunk, read.givenAway, read.drain.characters), Is.EqualTo(("C.1", new MeshLayoutPair(3, 9), true, given, 1)), "2b's fields as before");
            Assert.That(read.drainState, Is.EqualTo(state));
            Assert.That(read.drained, Is.False);

            MeshLayoutEvidenceMessage drained = MeshSplitEvidence.WithDrainState(built, true, new MeshDrainState { buildings = 2 });
            writer.Reset();
            drained.Serialize(writer);
            Assert.That(MeshWholeMessage.TryRead(new NetDataReader(writer.CopyData()), out read, out error), Is.True, error);
            Assert.That((read.drained, read.drainState.buildings), Is.EqualTo((true, 2)), "buildings never hold a drain");
        }

        [Test]
        public void Evidence_NotDraining_SendsZerosAndNotDrained()
        {
            MeshLayoutEvidenceMessage built = MeshSplitEvidence.Assemble("C", "inc", new MeshLayoutPair(1, 1), 0f, 0f, null, 0, 0f, 0f, 0f, false, MeshRect.Empty, default);
            MeshLayoutEvidenceMessage active = MeshSplitEvidence.WithDrainState(built, false, Every(3));
            Assert.That((active.drainState, active.drained), Is.EqualTo((default(MeshDrainState), false)));
            Assert.That(MeshSplitEvidence.WithDrainState(built, false, default).drained, Is.False, "an Active cell is never drained, even holding nothing");
        }

        [Test]
        public void Evidence_AnotherWireVersionIsRefused()
        {
            MeshLayoutEvidenceMessage built = MeshSplitEvidence.Assemble("C", "inc", new MeshLayoutPair(1, 1), 0f, 0f, null, 0, 0f, 0f, 0f, false, MeshRect.Empty, default);
            NetDataWriter writer = new NetDataWriter();
            built.Serialize(writer);
            byte[] data = writer.CopyData();
            data[0] = 1; // a version-1 sender
            Assert.That(MeshWholeMessage.TryRead(new NetDataReader(data), out MeshLayoutEvidenceMessage _, out string error), Is.False);
            Assert.That(error, Does.Contain("version"));
        }
    }
}
