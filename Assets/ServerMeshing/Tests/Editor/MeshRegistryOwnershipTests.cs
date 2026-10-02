using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class MeshRegistryOwnershipTests
    {
        private MeshWorldRegistry _world;

        [SetUp]
        public void SetUp()
        {
            _world = ScriptableObject.CreateInstance<MeshWorldRegistry>();
            _world.maps.Add(new MeshMapLayout
            {
                mapId = "TestMap",
                gridOrigin = new Vector3(-100f, 0f, -100f),
                cellSize = 100f,
                columns = 2,
                rows = 2,
                cellIds = new[] { "SW", "SE", "NW", "NE" },
            });
            _world.Rebuild();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_world);
        }

        private static IEnumerable<TestCaseData> OwnerCases()
        {
            yield return new TestCaseData("SW", new Vector3(-500f, 0f, -500f), "SW").SetName("OwnerOf_OutsideGrid_ClampsToNearestCell");
            yield return new TestCaseData("SW", new Vector3(500f, 0f, 500f), "NE").SetName("OwnerOf_FarOutsideGrid_ClampsToOppositeCorner");
            yield return new TestCaseData("SW", new Vector3(-50f, 0f, -50f), "SW").SetName("OwnerOf_InsideOwnCell_IsSelf");
            yield return new TestCaseData("SW", new Vector3(50f, 0f, -50f), "SE").SetName("OwnerOf_InsideEdgeNeighbour_IsNeighbour");
            yield return new TestCaseData("SW", new Vector3(50f, 0f, 50f), "NE").SetName("OwnerOf_InsideDiagonalNeighbour_IsNeighbour");
            yield return new TestCaseData("SW", new Vector3(0f, 0f, 0f), "NE").SetName("OwnerOf_OnSharedCorner_IsMaxSideCell");
        }

        [TestCaseSource(nameof(OwnerCases))]
        public void OwnerOf(string fromId, Vector3 position, string expectedId)
        {
            MeshCell from = _world.GetCellById(fromId);
            Assert.That(from, Is.Not.Null);

            MeshCell owner = _world.OwnerOf(from, position);

            Assert.That(owner, Is.Not.Null);
            Assert.That(owner.id, Is.EqualTo(expectedId));
        }

        [Test]
        public void Rebuild_TouchingCells_AreMutualNeighbours()
        {
            MeshCell sw = _world.GetCellById("SW");

            List<string> ids = new List<string>();
            foreach (MeshCell n in sw.neighbours)
                ids.Add(n.id);

            Assert.That(ids, Is.EquivalentTo(new[] { "SE", "NW", "NE" }));
        }

        private static IEnumerable<TestCaseData> SameCases()
        {
            yield return new TestCaseData(null, null, true).SetName("Same_NullAndNull_IsTrue");
            yield return new TestCaseData(new MeshCell { id = "C", mapId = "M" }, null, false).SetName("Same_CellAndNull_IsFalse");
            yield return new TestCaseData(new MeshCell { id = "C", mapId = "M" }, new MeshCell { id = "D", mapId = "M" }, false).SetName("Same_DifferentId_IsFalse");
            yield return new TestCaseData(new MeshCell { id = "C", mapId = "M" }, new MeshCell { id = "C", mapId = "M2" }, false).SetName("Same_SameIdOnAnotherMap_IsFalse");
            yield return new TestCaseData(new MeshCell { id = "C", mapId = "M" }, new MeshCell { id = "c", mapId = "M" }, true).SetName("Same_IdDifferingByCase_IsTrue");
            yield return new TestCaseData(new MeshCell { id = "C", mapId = "M" }, new MeshCell { id = "C", mapId = "M" }, true).SetName("Same_AnotherInstanceOfTheCell_IsTrue");
        }

        [TestCaseSource(nameof(SameCases))]
        public void Same(MeshCell a, MeshCell b, bool expected)
        {
            Assert.That(MeshCell.Same(a, b), Is.EqualTo(expected));
            Assert.That(MeshCell.Same(b, a), Is.EqualTo(expected));
        }

        [Test]
        public void Same_TheSameObject_IsTrue()
        {
            MeshCell sw = _world.GetCellById("SW");
            Assert.That(MeshCell.Same(sw, sw), Is.True);
        }

        [Test]
        public void CellAt_ExplicitLayout_ContainingElseNearest()
        {
            MeshWorldRegistry world = ScriptableObject.CreateInstance<MeshWorldRegistry>();
            world.maps.Add(new MeshMapLayout
            {
                mapId = "Towns",
                cellRects = new[]
                {
                    new MeshCellRect { id = "Town", min = new Vector2(0f, 0f), size = new Vector2(50f, 50f) },
                    new MeshCellRect { id = "Wilds", min = new Vector2(50f, 0f), size = new Vector2(300f, 50f) },
                },
            });
            world.Rebuild();

            Assert.That(world.CellAt("Towns", new Vector3(10f, 0f, 10f)).id, Is.EqualTo("Town"));
            Assert.That(world.CellAt("Towns", new Vector3(100f, 0f, 10f)).id, Is.EqualTo("Wilds"));
            Assert.That(world.CellAt("Towns", new Vector3(-30f, 0f, 10f)).id, Is.EqualTo("Town"));
            Object.DestroyImmediate(world);
        }

        [Test]
        public void Rebuild_KeepsTheSameInstances()
        {
            MeshCell sw = _world.GetCellById("SW");

            _world.Rebuild();

            Assert.That(_world.GetCellById("SW"), Is.SameAs(sw));
            Assert.That(sw.neighbours.Count, Is.EqualTo(3));
        }

        [Test]
        public void Rebuild_CellsSharingAKey_StayTwoInstances()
        {
            _world.maps[0].cellIds = new[] { "SW", "SW", "NW", "NE" };
            LogAssert.Expect(LogType.Error, new Regex("is not unique across the world"));
            _world.Rebuild();
            LogAssert.Expect(LogType.Error, new Regex("is not unique across the world"));

            _world.Rebuild();

            MeshCell first = _world.CellsOf("TestMap")[0];
            MeshCell second = _world.CellsOf("TestMap")[1];
            Assert.That(first, Is.Not.SameAs(second));
            Assert.That(second.owned.minX, Is.EqualTo(0f));
        }

        [Test]
        public void Rebuild_MoreThan32TouchingCells_TruncatesWithTheSameError()
        {
            MeshWorldRegistry world = ScriptableObject.CreateInstance<MeshWorldRegistry>();
            MeshCellRect[] rects = new MeshCellRect[35];
            rects[0] = new MeshCellRect { id = "Hub", min = new Vector2(0f, 0f), size = new Vector2(3400f, 100f) };
            for (int i = 1; i < rects.Length; ++i)
                rects[i] = new MeshCellRect { id = "R" + i, min = new Vector2((i - 1) * 100f, 100f), size = new Vector2(100f, 100f) };
            world.maps.Add(new MeshMapLayout { mapId = "Row", cellRects = rects });
            LogAssert.Expect(LogType.Error, new Regex("cell Hub touches 34 cells; only the first 32 are neighbours"));

            world.Rebuild();

            Assert.That(world.GetCellById("Hub").neighbours.Count, Is.EqualTo(MeshWorldRegistry.MaxNeighbours));
            Assert.That(world.GetCellById("Hub").bandNeighbours.Count, Is.EqualTo(MeshWorldRegistry.MaxNeighbours));
            Object.DestroyImmediate(world);
        }
    }
}
