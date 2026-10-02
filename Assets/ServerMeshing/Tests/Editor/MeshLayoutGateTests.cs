using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using static MultiplayerARPG.MMO.Mesh.Tests.MeshFakeWorlds;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class MeshLayoutGateTests
    {
        [Test]
        public void StaticMap_IsNeverGated_AndAnAnswerChangesNothing()
        {
            MeshLayoutGate gate = new MeshLayoutGate();
            gate.Reset(false);
            gate.OnHelloSent(0f, 10f);

            Assert.That(gate.OnAnswered(), Is.False);
            Assert.That(gate.IsGated, Is.False);
        }

        [Test]
        public void AnswerBeforeTheHello_DoesNotOpen()
        {
            MeshLayoutGate gate = new MeshLayoutGate();
            gate.Reset(true);

            Assert.That(gate.OnAnswered(), Is.False);
            Assert.That(gate.IsGated, Is.True);
        }

        [Test]
        public void DynamicMap_IsGatedBeforeAndAfterTheHelloUntilAnswered()
        {
            MeshLayoutGate gate = new MeshLayoutGate();
            gate.Reset(true);
            Assert.That(gate.IsGated, Is.True);

            gate.OnHelloSent(5f, 10f);
            Assert.That((gate.IsGated, gate.HelloSent, gate.FirstHelloAt), Is.EqualTo((true, true, 5f)));

            Assert.That(gate.OnAnswered(), Is.True);
            Assert.That(gate.IsGated, Is.False);
        }

        [Test]
        public void OpenGate_StaysOpenThroughAnotherHello_AndClosesOnlyForANewStart()
        {
            MeshLayoutGate gate = new MeshLayoutGate();
            gate.Reset(true);
            gate.OnHelloSent(0f, 10f);
            gate.OnAnswered();

            gate.OnHelloSent(30f, 10f);
            Assert.That(gate.IsGated, Is.False, "a re-registration's hello");
            Assert.That(gate.OnAnswered(), Is.False, "already open");
            Assert.That(gate.FirstHelloAt, Is.EqualTo(0f));

            gate.Reset(true);
            Assert.That((gate.IsGated, gate.HelloSent), Is.EqualTo((true, false)));
        }
    }

    public class MeshLayoutGateWarningTests
    {
        private static IEnumerable<TestCaseData> Cases()
        {
            yield return new TestCaseData(false, false, new[] { 1000f }, new[] { false }).SetName("Warning_NoHello_NeverDue");
            yield return new TestCaseData(true, true, new[] { 110f, 200f }, new[] { false, false }).SetName("Warning_GateOpen_NeverDue");
            yield return new TestCaseData(true, false, new[] { 100f, 109.9f }, new[] { false, false }).SetName("Warning_WithinTheFirstPeriod_NotDue");
            yield return new TestCaseData(true, false, new[] { 110f, 110.5f, 119.9f, 120f, 131f }, new[] { true, false, false, true, true }).SetName("Warning_EveryPeriodAfterTheHello");
        }

        [TestCaseSource(nameof(Cases))]
        public void WarningDue(bool helloSent, bool answered, float[] times, bool[] expected)
        {
            MeshLayoutGate gate = new MeshLayoutGate();
            gate.Reset(true);
            if (helloSent)
                gate.OnHelloSent(100f, 10f);
            if (answered)
                gate.OnAnswered();

            bool[] due = new bool[times.Length];
            for (int i = 0; i < times.Length; ++i)
                due[i] = gate.WarningDue(times[i], 10f);

            Assert.That(due, Is.EqualTo(expected));
        }
    }

    public class MeshHeldPlacementQueueTests
    {
        [Test]
        public void TryHold_GateOpen_HoldsNothing()
        {
            MeshHeldPlacementQueue<string> queue = new MeshHeldPlacementQueue<string>();

            Assert.That(queue.TryHold(false, "b1", false, 30f), Is.False);
            Assert.That(queue.Count, Is.EqualTo(0));
        }

        [Test]
        public void TryTakeExpired_BeforeAnyDeadline_TakesNothing()
        {
            MeshHeldPlacementQueue<string> queue = new MeshHeldPlacementQueue<string>();
            queue.TryHold(true, "b1", false, 30f);

            Assert.That(queue.TryTakeExpired(29.9f, out _), Is.False);
            Assert.That(queue.Count, Is.EqualTo(1));
        }

        [Test]
        public void TryTakeExpired_HandsBackThePassedEntryAndKeepsTheRest()
        {
            MeshHeldPlacementQueue<string> queue = new MeshHeldPlacementQueue<string>();
            queue.TryHold(true, "late", true, 50f);
            queue.TryHold(true, "early", false, 30f);

            Assert.That(queue.TryTakeExpired(30f, out MeshHeldPlacementQueue<string>.HeldItem held), Is.True);

            Assert.That((held.item, held.received), Is.EqualTo(("early", false)));
            Assert.That(queue.Count, Is.EqualTo(1));
        }

        [Test]
        public void TryTakeNext_ReleasesInArrivalOrderWithTheReceivedFlag()
        {
            MeshHeldPlacementQueue<string> queue = new MeshHeldPlacementQueue<string>();
            queue.TryHold(true, "b1", false, 30f);
            queue.TryHold(true, "b2", true, 31f);
            queue.TryHold(true, "b3", false, 32f);

            List<(string, bool)> released = new List<(string, bool)>();
            while (queue.TryTakeNext(out MeshHeldPlacementQueue<string>.HeldItem held))
                released.Add((held.item, held.received));

            Assert.That(released, Is.EqualTo(new[] { ("b1", false), ("b2", true), ("b3", false) }));
            Assert.That(queue.Count, Is.EqualTo(0));
        }

        [Test]
        public void TakeAll_EmptiesTheQueueOldestFirst()
        {
            MeshHeldPlacementQueue<string> queue = new MeshHeldPlacementQueue<string>();
            queue.TryHold(true, "b1", false, 30f);
            queue.TryHold(true, "b2", true, 31f);

            List<MeshHeldPlacementQueue<string>.HeldItem> all = queue.TakeAll();

            Assert.That(all.ConvertAll(h => h.item), Is.EqualTo(new[] { "b1", "b2" }));
            Assert.That(queue.Count, Is.EqualTo(0));
        }
    }

    public class MeshStreamTargetDiffTests
    {
        private static IEnumerable<TestCaseData> Cases()
        {
            yield return new TestCaseData(null, new[] { "A", "B" }, new string[0], new[] { "A", "B" }).SetName("Diff_NullPrevious_AllAdded");
            yield return new TestCaseData(new[] { "A" }, null, new[] { "A" }, new string[0]).SetName("Diff_NullCurrent_AllLeft");
            yield return new TestCaseData(new string[0], new[] { "A" }, new string[0], new[] { "A" }).SetName("Diff_SeededFromEmpty");
            yield return new TestCaseData(new[] { "A", "B" }, new[] { "B", "C" }, new[] { "A" }, new[] { "C" }).SetName("Diff_OneLeftOneAdded");
            yield return new TestCaseData(new[] { "c.1", "E" }, new[] { "C.1", "e" }, new string[0], new string[0]).SetName("Diff_CaseOnly_Unchanged");
            yield return new TestCaseData(new[] { "A", "B" }, new[] { "B", "A" }, new string[0], new string[0]).SetName("Diff_SameIdsReordered_Unchanged");
        }

        [TestCaseSource(nameof(Cases))]
        public void Compute(string[] previous, string[] current, string[] expectedLeft, string[] expectedAdded)
        {
            List<string> left = new List<string> { "stale" }, added = new List<string> { "stale" };

            MeshStreamTargetDiff.Compute(previous, current, left, added);

            Assert.That(left, Is.EqualTo(expectedLeft));
            Assert.That(added, Is.EqualTo(expectedAdded));
        }
    }

    public class MeshBandMaskTests
    {
        private MeshWorldRegistry _world;
        private BandEnvironment _band;
        private MeshCell _c;

        [SetUp]
        public void SetUp()
        {
            _band = new BandEnvironment();
            _world = RigLike(map1Dynamic: false);
            _c = _world.GetCellById("C");
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_world);
            _band.Dispose();
        }

        private int Bit(string id) => 1 << _c.bandNeighbours.FindIndex(n => n.id == id);

        [Test]
        public void MoreThanThirtyTwoTargets_Throws()
        {
            List<MeshCell> targets = new List<MeshCell>();
            for (int i = 0; i <= MeshWorldRegistry.MaxNeighbours; ++i)
                targets.Add(_world.GetCellById("E"));

            Assert.Throws<ArgumentException>(() => MeshBandMask.Of(_c, targets, Vector3.zero, 80f));
        }

        [Test]
        public void NoOwnCell_IsZero()
        {
            Assert.That(MeshBandMask.Of(null, _c.bandNeighbours, Vector3.zero, 80f), Is.EqualTo(0));
        }

        [Test]
        public void Interior_IsZero()
        {
            Assert.That(MeshBandMask.Of(_c, _c.bandNeighbours, new Vector3(0f, 0f, 0f), 80f), Is.EqualTo(0));
        }

        [Test]
        public void NearTheEastEdge_SetsOnlyEast()
        {
            Assert.That(MeshBandMask.Of(_c, _c.bandNeighbours, new Vector3(90f, 0f, 0f), 80f), Is.EqualTo(Bit("E")));
        }

        [Test]
        public void NearTheNorthEastCorner_SetsBothEdgesAndTheDiagonal()
        {
            Assert.That(MeshBandMask.Of(_c, _c.bandNeighbours, new Vector3(90f, 0f, 90f), 80f), Is.EqualTo(Bit("E") | Bit("N") | Bit("NE")));
        }

        [Test]
        public void Mask_FollowsTargetOrder()
        {
            List<MeshCell> targets = new List<MeshCell> { _world.GetCellById("W"), _world.GetCellById("E") };

            Assert.That(MeshBandMask.Of(_c, targets, new Vector3(90f, 0f, 0f), 80f), Is.EqualTo(0b10));
            targets.Reverse();
            Assert.That(MeshBandMask.Of(_c, targets, new Vector3(90f, 0f, 0f), 80f), Is.EqualTo(0b01));
        }

        [Test]
        public void ChildBeingMade_CountsItsInterestNotItsEmptyOwnership()
        {
            MeshLayoutSnapshot pending = Snapshot(1,
                Active("E", "E", new MeshRect(100f, -100f, 300f, 100f)),
                Entry("E.1", "E", MeshCellState.Pending, MeshRect.Empty, new MeshRect(200f, -100f, 300f, 100f)));
            MeshWorldRegistry dynamicWorld = RigLike(map1Dynamic: true);
            try
            {
                Assert.That(dynamicWorld.ApplyLayout(pending, out string reason), Is.True, reason);
                MeshCell e = dynamicWorld.GetCellById("E");
                MeshCell child = dynamicWorld.GetCellById("E.1");
                List<MeshCell> targets = new List<MeshCell> { child };

                Assert.That(MeshBandMask.Of(e, targets, new Vector3(150f, 0f, 0f), 80f), Is.EqualTo(1), "50 m from its interest");
                Assert.That(MeshBandMask.Of(e, targets, new Vector3(110f, 0f, 0f), 80f), Is.EqualTo(0), "90 m from its interest");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(dynamicWorld);
            }
        }
    }

    public class MeshFrameOffsetRecomputeTests
    {
        private MeshWorldRegistry _world;
        private BandEnvironment _band;

        [SetUp]
        public void SetUp()
        {
            _band = new BandEnvironment();
            _world = RigLike(map1Dynamic: true);
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_world);
            _band.Dispose();
        }

        [Test]
        public void UnknownZone_KeepsNoOffsetUntilALayoutAddsIt()
        {
            MeshCell c = _world.GetCellById("C");
            Dictionary<long, string> zones = new Dictionary<long, string> { { 5, "C.1" } };
            Dictionary<long, Vector3> offsets = new Dictionary<long, Vector3>();

            Assert.That(MeshFrameOffsets.Recompute(_world, c, zones, offsets), Is.EqualTo(0));
            Assert.That(offsets.ContainsKey(5), Is.False);

            Assert.That(_world.ApplyLayout(SplitC(), out string reason), Is.True, reason);
            Assert.That(MeshFrameOffsets.Recompute(_world, c, zones, offsets), Is.EqualTo(1));
            Assert.That(offsets[5], Is.EqualTo(Vector3.zero), "same map, same frame");
        }

        [Test]
        public void NoOwnCellAtHello_IsFilledOnceItIsKnown()
        {
            Dictionary<long, string> zones = new Dictionary<long, string> { { 7, "C2" } };
            Dictionary<long, Vector3> offsets = new Dictionary<long, Vector3>();

            Assert.That(MeshFrameOffsets.Recompute(_world, null, zones, offsets), Is.EqualTo(0));
            Assert.That(MeshFrameOffsets.Recompute(_world, _world.GetCellById("E"), zones, offsets), Is.EqualTo(1));

            Assert.That(offsets[7], Is.EqualTo(new Vector3(400f, 0f, 0f)), "C2's map sits 400 m east of E's");
        }

        [Test]
        public void OnlyConnectionsStillLinked_AreRecomputed_AndAnUnchangedOneIsNotCounted()
        {
            MeshCell e = _world.GetCellById("E");
            Dictionary<long, string> zones = new Dictionary<long, string> { { 1, "C" } };
            Dictionary<long, Vector3> offsets = new Dictionary<long, Vector3> { { 1, Vector3.zero }, { 9, new Vector3(1f, 2f, 3f) } };

            Assert.That(MeshFrameOffsets.Recompute(_world, e, zones, offsets), Is.EqualTo(0));

            Assert.That(offsets[9], Is.EqualTo(new Vector3(1f, 2f, 3f)), "a disconnected connection is left alone");
        }
    }
}
