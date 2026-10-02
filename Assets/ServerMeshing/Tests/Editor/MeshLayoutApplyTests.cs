using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static MultiplayerARPG.MMO.Mesh.Tests.MeshFakeWorlds;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class MeshLayoutApplyTests
    {
        private static readonly MeshRect CBase = new MeshRect(-100f, -100f, 100f, 100f);
        private static readonly MeshRect CEast = new MeshRect(0f, -100f, 100f, 100f);

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
            Object.DestroyImmediate(_world);
            _band.Dispose();
        }

        [Test]
        public void ApplyLayout_OlderPair_IsRefusedAndChangesNothing()
        {
            Assert.That(_world.ApplyLayout(SplitC(5), out _), Is.True);

            bool ok = _world.ApplyLayout(Snapshot(4), out string reason);

            Assert.That(ok, Is.False);
            Assert.That(reason, Does.Contain("stale"));
            Assert.That(_world.GetCellById("C.1").state, Is.EqualTo(MeshCellState.Active));
        }

        [Test]
        public void ApplyLayout_EqualPair_IsAlreadyAppliedWithNoEvent()
        {
            Assert.That(_world.ApplyLayout(SplitC(1), out _), Is.True);
            int events = 0;
            _world.LayoutChanged += _ => events++;

            bool ok = _world.ApplyLayout(SplitC(1), out MeshLayoutChange change, out string reason);

            Assert.That(ok, Is.True);
            Assert.That(reason, Is.EqualTo("already applied"));
            Assert.That(change, Is.Null);
            Assert.That(events, Is.EqualTo(0));
        }

        [Test]
        public void ApplyLayout_InvalidSnapshot_IsAtomic()
        {
            MeshCell c = _world.GetCellById("C");
            List<string> neighboursBefore = Ids(c.neighbours);

            bool ok = _world.ApplyLayout(Snapshot(1, Active("C", "C", new MeshRect(-100f, -100f, 10f, 100f)), Active("C.1", "C", CEast)), out string reason);

            Assert.That(ok, Is.False, reason);
            Assert.That(c.owned, Is.EqualTo(CBase));
            Assert.That(Ids(c.neighbours), Is.EqualTo(neighboursBefore));
            Assert.That(_world.GetCellById("C.1"), Is.Null);
            Assert.That(_world.AppliedPair.IsNone, Is.True);
        }

        [Test]
        public void ApplyLayout_EntryForAStaticMap_IsRefused()
        {
            bool ok = _world.ApplyLayout(Snapshot(1, Entry("C2", "C2", MeshCellState.Active, new MeshRect(300f, -100f, 500f, 100f), new MeshRect(300f, -100f, 500f, 100f), Map2)), out string reason);

            Assert.That(ok, Is.False);
            Assert.That(reason, Does.Contain("Static"));
        }

        [Test]
        public void ApplyLayout_SplitC_MakesAChildAndRecutsNeighbours()
        {
            MeshCell c = _world.GetCellById("C");
            List<MeshLayoutChange> changes = new List<MeshLayoutChange>();
            _world.LayoutChanged += changes.Add;

            Assert.That(_world.ApplyLayout(SplitC(1), out string reason), Is.True, reason);

            MeshCell child = _world.GetCellById("C.1");
            Assert.That(_world.GetCellById("C"), Is.SameAs(c));
            Assert.That(child.isDynamic, Is.True);
            Assert.That(child.baseId, Is.EqualTo("C"));
            Assert.That(child.frameOrigin, Is.EqualTo(c.frameOrigin));
            Assert.That((child.gridX, child.gridY), Is.EqualTo((-1, -1)));
            Assert.That(_world.Cells, Has.Member(child));
            Assert.That(_world.CellsOf(Map1), Has.Member(child));
            Assert.That(_world.CellAt(Map1, new Vector3(50f, 0f, 0f)).id, Is.EqualTo("C.1"));
            Assert.That(_world.CellAt(Map1, new Vector3(-50f, 0f, 0f)).id, Is.EqualTo("C"));
            Assert.That(_world.OwnerOf(c, new Vector3(50f, 0f, 0f)).id, Is.EqualTo("C.1"));
            Assert.That(Ids(child.neighbours), Is.EqualTo(new[] { "S", "SE", "C", "E", "N", "NE" }));
            Assert.That(Ids(c.neighbours), Is.EqualTo(new[] { "SW", "S", "W", "NW", "N", "C.1" }));
            Assert.That(changes.Count, Is.EqualTo(1));
            Assert.That(changes[0].changedIds, Is.SupersetOf(new[] { "C", "C.1" }));
            Assert.That(_world.AppliedPair, Is.EqualTo(new MeshLayoutPair(1, 1)));
        }

        [Test]
        public void ApplyLayout_PendingChild_IsABandNeighbourButOwnsNothing()
        {
            Assert.That(_world.ApplyLayout(Snapshot(1, Entry("C.1", "C", MeshCellState.Pending, MeshRect.Empty, CEast)), out string reason), Is.True, reason);

            MeshCell child = _world.GetCellById("C.1");
            MeshCell c = _world.GetCellById("C");
            Assert.That(c.owned, Is.EqualTo(CBase));
            foreach (MeshCell cell in _world.Cells)
                Assert.That(cell.neighbours, Has.No.Member(child), cell.id);
            Assert.That(c.bandNeighbours, Has.Member(child));
            Assert.That(_world.GetCellById("E").bandNeighbours, Has.Member(child));
            Assert.That(_world.CellAt(Map1, new Vector3(50f, 0f, 0f)).id, Is.EqualTo("C"));
            Assert.That(_world.CellAtWorld(new Vector3(50f, 0f, 0f)).id, Is.EqualTo("C"));
            Assert.That(_world.OwnerOf(c, new Vector3(50f, 0f, 0f)).id, Is.EqualTo("C"));
            Assert.That(child.DistanceToEdge(new Vector3(50f, 0f, 0f)), Is.EqualTo(float.NegativeInfinity));
            Assert.That(child.IsRoutable, Is.False);
        }

        [Test]
        public void ApplyLayout_TJunction_MakesBandNeighboursThatDoNotTouch()
        {
            Object.DestroyImmediate(_world);
            _world = RigLike(map1Dynamic: true, minCellSize: 80f);

            bool ok = _world.ApplyLayout(Snapshot(1,
                Active("E", "E", new MeshRect(100f, -100f, 300f, -20f)),
                Active("E.1", "E", new MeshRect(100f, -20f, 300f, 100f))), out string reason);

            Assert.That(ok, Is.True, reason);
            MeshCell child = _world.GetCellById("E.1");
            MeshCell se = _world.GetCellById("SE");
            Assert.That(child.neighbours, Has.No.Member(se));
            Assert.That(child.bandNeighbours, Has.Member(se));
            Assert.That(se.bandNeighbours, Has.Member(child));
        }

        [Test]
        public void ApplyLayout_ChildMissingFromTheNextSnapshot_IsRetired()
        {
            Assert.That(_world.ApplyLayout(SplitC(1), out _), Is.True);

            Assert.That(_world.ApplyLayout(Snapshot(2), out string reason), Is.True, reason);

            MeshCell child = _world.GetCellById("C.1");
            Assert.That(child, Is.Not.Null);
            Assert.That(child.state, Is.EqualTo(MeshCellState.Retired));
            Assert.That(_world.Cells, Has.No.Member(child));
            Assert.That(_world.CellsOf(Map1), Has.No.Member(child));
            foreach (MeshCell cell in _world.Cells)
                Assert.That(cell.neighbours, Has.No.Member(child), cell.id);
            Assert.That(_world.CellAt(Map1, new Vector3(50f, 0f, 0f)).id, Is.EqualTo("C"));
            Assert.That(_world.GetCellById("C").owned, Is.EqualTo(CBase));
        }

        [Test]
        public void ApplyLayout_ThrowingSubscriber_DoesNotStopTheApply()
        {
            _world.LayoutChanged += _ => throw new System.InvalidOperationException("boom");
            int later = 0;
            _world.LayoutChanged += _ => later++;
            LogAssert.Expect(LogType.Exception, new Regex("boom"));

            Assert.That(_world.ApplyLayout(SplitC(1), out _), Is.True);

            Assert.That(later, Is.EqualTo(1));
            Assert.That(_world.AppliedPair, Is.EqualTo(new MeshLayoutPair(1, 1)));
        }

        [Test]
        public void Rebuild_KeepsTheAppliedLayoutAndItsInstances()
        {
            Assert.That(_world.ApplyLayout(SplitC(1), out _), Is.True);
            MeshCell child = _world.GetCellById("C.1");
            MeshCell c = _world.GetCellById("C");

            _world.Rebuild();

            Assert.That(_world.GetCellById("C.1"), Is.SameAs(child));
            Assert.That(_world.GetCellById("C"), Is.SameAs(c));
            Assert.That(c.owned, Is.EqualTo(new MeshRect(-100f, -100f, 0f, 100f)));
            Assert.That(Ids(child.neighbours), Is.EqualTo(new[] { "S", "SE", "C", "E", "N", "NE" }));
        }

        [Test]
        public void ResetRuntimeLayout_ReturnsToTheAsset()
        {
            int events = 0;
            _world.LayoutChanged += _ => events++;
            Assert.That(_world.ApplyLayout(SplitC(1), out _), Is.True);

            _world.ResetRuntimeLayout();

            Assert.That(_world.GetCellById("C.1"), Is.Null);
            Assert.That(_world.GetCellById("C").owned, Is.EqualTo(CBase));
            Assert.That(_world.AppliedPair.IsNone, Is.True);
            Assert.That(_world.ApplyLayout(SplitC(2), out _), Is.True);
            Assert.That(events, Is.EqualTo(1), "a subscriber from before the reset is not called again");
        }
    }
}
