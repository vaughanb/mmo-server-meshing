using System;
using System.Collections.Generic;
using System.IO;
using LiteNetLib.Utils;
using NUnit.Framework;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class MeshLayoutMessageTests
    {
        private static MeshLayoutSnapshot Snapshot(int entries)
        {
            MeshLayoutSnapshot snapshot = new MeshLayoutSnapshot { pair = new MeshLayoutPair(0x1234, 9) };
            for (int i = 0; i < entries; ++i)
            {
                MeshCellState state = i == 2 ? MeshCellState.Retired : MeshCellState.Active;
                MeshRect rect = i == 2 ? MeshRect.Empty : MeshRect.FromMinSize(i * 100f, -50f, 100f, 200f);
                snapshot.entries.Add(new MeshLayoutEntry { id = "C." + i, mapId = "Map", baseId = "C", state = state, owned = rect, interest = rect });
            }
            return snapshot;
        }

        private static T RoundTrip<T>(T message, Func<T> make) where T : INetSerializable
        {
            NetDataWriter writer = new NetDataWriter();
            message.Serialize(writer);
            NetDataReader reader = new NetDataReader(writer.CopyData());
            T copy = make();
            copy.Deserialize(reader);
            Assert.That(reader.AvailableBytes, Is.EqualTo(0), "every byte read");
            return copy;
        }

        private static void AssertSame(MeshLayoutSnapshot a, MeshLayoutSnapshot b)
        {
            Assert.That(b.pair, Is.EqualTo(a.pair));
            Assert.That(b.entries.Count, Is.EqualTo(a.entries.Count));
            for (int i = 0; i < a.entries.Count; ++i)
            {
                Assert.That(b.entries[i].id, Is.EqualTo(a.entries[i].id));
                Assert.That(b.entries[i].mapId, Is.EqualTo(a.entries[i].mapId));
                Assert.That(b.entries[i].baseId, Is.EqualTo(a.entries[i].baseId));
                Assert.That(b.entries[i].state, Is.EqualTo(a.entries[i].state));
                Assert.That(b.entries[i].owned, Is.EqualTo(a.entries[i].owned));
                Assert.That(b.entries[i].interest, Is.EqualTo(a.entries[i].interest));
            }
        }

        private static IEnumerable<TestCaseData> Kinds()
        {
            foreach (MeshLayoutKind kind in Enum.GetValues(typeof(MeshLayoutKind)))
            {
                yield return new TestCaseData(kind, 0).SetName($"LayoutMessage_{kind}_NoEntries_RoundTrips");
                yield return new TestCaseData(kind, 3).SetName($"LayoutMessage_{kind}_ThreeEntries_RoundTrips");
            }
        }

        [TestCaseSource(nameof(Kinds))]
        public void LayoutMessage_RoundTrips(MeshLayoutKind kind, int entries)
        {
            MeshLayoutMessage message = new MeshLayoutMessage { kind = kind, targetCellId = "C.1", targetIncarnation = "abc", stepId = null, snapshot = Snapshot(entries) };

            MeshLayoutMessage copy = RoundTrip(message, () => new MeshLayoutMessage());

            Assert.That(copy.kind, Is.EqualTo(kind));
            Assert.That(copy.targetCellId, Is.EqualTo("C.1"));
            Assert.That(copy.targetIncarnation, Is.EqualTo("abc"));
            Assert.That(copy.stepId, Is.EqualTo(string.Empty));
            AssertSame(message.snapshot, copy.snapshot);
        }

        [Test]
        public void Hello_RoundTrips()
        {
            MeshLayoutHelloMessage hello = new MeshLayoutHelloMessage { cellId = "C", mapId = "Map", incarnation = "ff", dynamicMap = true, held = Snapshot(3) };

            MeshLayoutHelloMessage copy = RoundTrip(hello, () => new MeshLayoutHelloMessage());

            Assert.That((copy.cellId, copy.mapId, copy.incarnation, copy.dynamicMap), Is.EqualTo(("C", "Map", "ff", true)));
            AssertSame(hello.held, copy.held);
        }

        [Test]
        public void AckRetireAndNote_RoundTrip()
        {
            MeshLayoutAckMessage ack = RoundTrip(new MeshLayoutAckMessage { cellId = "C", incarnation = "1", pair = new MeshLayoutPair(2, 3), result = MeshLayoutAckResult.Rejected, reason = "stale" }, () => new MeshLayoutAckMessage());
            Assert.That((ack.cellId, ack.pair, ack.result, ack.reason), Is.EqualTo(("C", new MeshLayoutPair(2, 3), MeshLayoutAckResult.Rejected, "stale")));

            MeshRetireMessage retire = RoundTrip(new MeshRetireMessage { cellId = "C.1", incarnation = null, pair = new MeshLayoutPair(2, 4), reason = "merged" }, () => new MeshRetireMessage());
            Assert.That((retire.cellId, retire.incarnation, retire.pair, retire.reason), Is.EqualTo(("C.1", string.Empty, new MeshLayoutPair(2, 4), "merged")));

            MeshCoordinatorNoteMessage note = RoundTrip(new MeshCoordinatorNoteMessage { level = MeshNoteLevel.Error, subjectCellId = "E", code = "recovery-frozen", text = "t", pair = new MeshLayoutPair(1, 1) }, () => new MeshCoordinatorNoteMessage());
            Assert.That((note.level, note.subjectCellId, note.code, note.text), Is.EqualTo((MeshNoteLevel.Error, "E", "recovery-frozen", "t")));
        }

        private static byte[] SnapshotBytes(int count, int realEntries)
        {
            NetDataWriter writer = new NetDataWriter();
            MeshLayoutWire.Put(writer, new MeshLayoutPair(1, 1));
            writer.Put(count);
            MeshLayoutSnapshot filler = Snapshot(realEntries);
            foreach (MeshLayoutEntry entry in filler.entries)
                MeshLayoutWire.Put(writer, entry);
            return writer.CopyData();
        }

        private static IEnumerable<TestCaseData> Corrupt()
        {
            yield return new TestCaseData(SnapshotBytes(-1, 0)).SetName("Snapshot_NegativeCount_Throws");
            yield return new TestCaseData(SnapshotBytes(MeshLayoutSnapshot.MaxEntries + 1, 0)).SetName("Snapshot_CountOverTheBound_Throws");
            yield return new TestCaseData(SnapshotBytes(5, 1)).SetName("Snapshot_CountLongerThanTheBytes_Throws");
        }

        [TestCaseSource(nameof(Corrupt))]
        public void Snapshot_Corrupt_Throws(byte[] bytes)
        {
            Assert.Throws<InvalidDataException>(() => new MeshLayoutSnapshot().Deserialize(new NetDataReader(bytes)));
        }

        [Test]
        public void Entry_UnknownState_Throws()
        {
            NetDataWriter writer = new NetDataWriter();
            MeshLayoutWire.Put(writer, new MeshLayoutEntry { id = "C", mapId = "M", baseId = "C", state = (MeshCellState)9 });

            Assert.Throws<InvalidDataException>(() => MeshLayoutWire.GetEntry(new NetDataReader(writer.CopyData())));
        }

        [Test]
        public void LayoutMessage_UnknownKind_Throws()
        {
            NetDataWriter writer = new NetDataWriter();
            new MeshLayoutMessage { kind = (MeshLayoutKind)7 }.Serialize(writer);

            Assert.Throws<InvalidDataException>(() => new MeshLayoutMessage().Deserialize(new NetDataReader(writer.CopyData())));
        }

        [Test]
        public void LayoutMessage_Truncated_Throws()
        {
            NetDataWriter writer = new NetDataWriter();
            new MeshLayoutMessage { kind = MeshLayoutKind.Broadcast, snapshot = Snapshot(3) }.Serialize(writer);
            byte[] bytes = writer.CopyData();
            Array.Resize(ref bytes, bytes.Length - 10);

            Assert.That(() => new MeshLayoutMessage().Deserialize(new NetDataReader(bytes)), Throws.Exception);
        }

        private static NetDataReader AckBytes(int extraBytes, int cutBytes)
        {
            NetDataWriter writer = new NetDataWriter();
            new MeshLayoutAckMessage { cellId = "C", incarnation = "i1", pair = new MeshLayoutPair(5, 6), result = MeshLayoutAckResult.Applied, reason = "ok" }.Serialize(writer);
            for (int i = 0; i < extraBytes; ++i)
                writer.Put((byte)0);
            byte[] bytes = writer.CopyData();
            Array.Resize(ref bytes, bytes.Length - cutBytes);
            return new NetDataReader(bytes);
        }

        [Test]
        public void WholeMessage_TrailingBytes_FailsNamingThem()
        {
            Assert.That(MeshWholeMessage.TryRead(AckBytes(3, 0), out MeshLayoutAckMessage _, out string error), Is.False);
            Assert.That(error, Is.EqualTo("3 unread bytes"));
        }

        [Test]
        public void WholeMessage_Truncated_FailsAsUnreadable()
        {
            Assert.That(MeshWholeMessage.TryRead(AckBytes(0, 4), out MeshLayoutAckMessage _, out string error), Is.False);
            Assert.That(error, Does.StartWith("unreadable"));
        }

        [Test]
        public void WholeMessage_StructMessage_KeepsWhatWasRead()
        {
            Assert.That(MeshWholeMessage.TryRead(AckBytes(0, 0), out MeshLayoutAckMessage ack, out string error), Is.True, error);

            Assert.That((ack.cellId, ack.incarnation, ack.pair, ack.result, ack.reason), Is.EqualTo(("C", "i1", new MeshLayoutPair(5, 6), MeshLayoutAckResult.Applied, "ok")));
        }

        [Test]
        public void ClusterMessageIds_AreDistinct()
        {
            ushort[] ids = { MeshClusterMsg.Sleep, MeshClusterMsg.Wake, MeshClusterMsg.Serves, MeshClusterMsg.LoadReport, MeshClusterMsg.Layout, MeshClusterMsg.LayoutHello, MeshClusterMsg.LayoutAck, MeshClusterMsg.Retire, MeshClusterMsg.CoordinatorNote };
            Assert.That(ids, Is.Unique);
        }

        [Test]
        public void Snapshot_JsonRoundTrip_KeepsThePairAndEntries()
        {
            MeshLayoutSnapshot snapshot = Snapshot(3);

            MeshLayoutSnapshot copy = JsonUtility.FromJson<MeshLayoutSnapshot>(JsonUtility.ToJson(snapshot));

            AssertSame(snapshot, copy);
        }
    }
}
