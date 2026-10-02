using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using static MultiplayerARPG.MMO.Mesh.Tests.MeshFakeWorlds;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>The rig's own world asset, read-only: static cells behave exactly as before dynamic cells existed.</summary>
    public class MeshStaticInvarianceTests
    {
        private const string AssetPath = "Assets/ServerMeshing/TestMap/ServerMeshWorld.asset";
        private const string TestMap = "ServerMeshTestMap";

        private MeshWorldRegistry _copy;
        private BandEnvironment _band;

        [SetUp]
        public void SetUp()
        {
            _band = new BandEnvironment();
            MeshWorldRegistry asset = AssetDatabase.LoadAssetAtPath<MeshWorldRegistry>(AssetPath);
            Assert.That(asset, Is.Not.Null, AssetPath);
            _copy = Object.Instantiate(asset);
            _copy.Rebuild();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_copy);
            _band.Dispose();
        }

        private void AssertBandIsTouching()
        {
            foreach (MeshCell cell in _copy.Cells)
            {
                Assert.That(Ids(cell.bandNeighbours), Is.EqualTo(Ids(cell.neighbours)), cell.id);
                Assert.That(cell.interest, Is.EqualTo(cell.owned), cell.id);
                Assert.That(cell.isDynamic, Is.False, cell.id);
            }
            Assert.That(_copy.GetCellById("C").neighbours.Count, Is.EqualTo(8));
            Assert.That(Ids(_copy.GetCellById("C2").neighbours), Is.EquivalentTo(new[] { "E", "NE", "SE" }));
        }

        [Test]
        public void StaticWorld_BandNeighboursAreTheTouchingOnes()
        {
            AssertBandIsTouching();
            foreach (MeshCell cell in _copy.Cells)
                Assert.That(cell.state, Is.EqualTo(MeshCellState.Static), cell.id);
        }

        [Test]
        public void DynamicTestMapWithNoLayout_StillBandsExactlyItsTouchingCells()
        {
            _copy.GetLayout(TestMap).cellMode = MeshCellMode.Dynamic;
            _copy.Rebuild();

            AssertBandIsTouching();
        }

        [Test]
        public void SplittingE_ChangesC2sNeighbours_SplittingC_DoesNot()
        {
            _copy.GetLayout(TestMap).cellMode = MeshCellMode.Dynamic;
            _copy.Rebuild();
            MeshLayoutSnapshot splitE = Snapshot(1,
                Entry("E", "E", MeshCellState.Active, new MeshRect(100f, -100f, 200f, 100f), new MeshRect(100f, -100f, 200f, 100f), TestMap),
                Entry("E.1", "E", MeshCellState.Active, new MeshRect(200f, -100f, 300f, 100f), new MeshRect(200f, -100f, 300f, 100f), TestMap));

            Assert.That(_copy.ApplyLayout(splitE, out MeshLayoutChange change, out string reason), Is.True, reason);

            Assert.That(change.bandChangedIds, Does.Contain("C2"));
            Assert.That(Ids(_copy.GetCellById("C2").neighbours), Does.Contain("E.1").And.Not.Contain("E"));

            // A snapshot is the whole truth, so C's split carries E's forward.
            MeshLayoutSnapshot splitC = Snapshot(2,
                splitE.entries[0], splitE.entries[1],
                Entry("C", "C", MeshCellState.Active, new MeshRect(-100f, -100f, 0f, 100f), new MeshRect(-100f, -100f, 0f, 100f), TestMap),
                Entry("C.1", "C", MeshCellState.Active, new MeshRect(0f, -100f, 100f, 100f), new MeshRect(0f, -100f, 100f, 100f), TestMap));
            Assert.That(_copy.ApplyLayout(splitC, out change, out reason), Is.True, reason);

            Assert.That(change.bandChangedIds, Does.Not.Contain("C2"));
        }

        /// <summary>The band mask as it was measured before dynamic cells: touching neighbours, owned rectangles.</summary>
        private static int TouchingMask(MeshCell own, Vector3 pos, float band)
        {
            int mask = 0;
            for (int i = 0; i < own.neighbours.Count; ++i)
            {
                if (own.DistanceTo(own.neighbours[i], pos) <= band)
                    mask |= 1 << i;
            }
            return mask;
        }

        [TestCase(false)]
        [TestCase(true)]
        public void StaticWorld_BandMasksEqualTheTouchingMasks(bool testMapDynamicWithNoLayout)
        {
            if (testMapDynamicWithNoLayout)
            {
                _copy.GetLayout(TestMap).cellMode = MeshCellMode.Dynamic;
                _copy.Rebuild();
            }
            foreach (MeshCell cell in _copy.Cells)
            {
                float width = cell.owned.Width, depth = cell.owned.Depth;
                for (int k = 0; k < 20; ++k)
                {
                    // 5 x 4 positions from just inside the west/south edges to just inside the east/north ones
                    float fx = (k % 5) / 4f, fz = (k / 5) / 3f;
                    Vector3 world = new Vector3(cell.owned.minX + 1f + fx * (width - 2f), 0f, cell.owned.minZ + 1f + fz * (depth - 2f));
                    Vector3 scene = cell.WorldToScene(world);

                    Assert.That(MeshBandMask.Of(cell, cell.bandNeighbours, scene, 80f), Is.EqualTo(TouchingMask(cell, scene, 80f)), $"{cell.id} at {world}");
                }
            }
        }
    }
}
