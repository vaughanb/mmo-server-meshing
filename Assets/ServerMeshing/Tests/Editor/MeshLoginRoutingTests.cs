using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using static MultiplayerARPG.MMO.Mesh.Tests.MeshFakeWorlds;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class MeshLoginRoutingTests
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
            Object.DestroyImmediate(_world);
            _band.Dispose();
        }

        private void Apply(MeshLayoutSnapshot snapshot)
        {
            Assert.That(_world.ApplyLayout(snapshot, out string reason), Is.True, reason);
        }

        private static MeshLayoutSnapshot PendingE1() => Snapshot(1,
            Active("E", "E", new MeshRect(100f, -100f, 300f, 100f)),
            Entry("E.1", "E", MeshCellState.Pending, MeshRect.Empty, new MeshRect(200f, -100f, 300f, 100f)));

        private MeshCell Route(float x, float z, params string[] live) => MeshLoginRouting.Route(_world, Map1, new Vector3(x, 0f, z), live, out _);

        [Test]
        public void MapWithoutALayout_IsNull()
        {
            Assert.That(MeshLoginRouting.Route(_world, "Elsewhere", Vector3.zero, new[] { "C" }, out bool fellBack), Is.Null);
            Assert.That(fellBack, Is.False);
        }

        [Test]
        public void NoCellRegistered_IsNull()
        {
            Assert.That(Route(0f, 0f), Is.Null);
        }

        [Test]
        public void OnlyAPendingChildRegistered_IsNull()
        {
            Apply(PendingE1());

            Assert.That(Route(250f, 0f, "E.1"), Is.Null);
        }

        [Test]
        public void DrainingCellAndRetiredId_AreNeverChosen()
        {
            Apply(Snapshot(1,
                Active("C", "C", new MeshRect(-100f, -100f, 100f, 100f)),
                Entry("C.1", "C", MeshCellState.Draining, MeshRect.Empty, new MeshRect(0f, -100f, 100f, 100f))));

            Assert.That(Route(50f, 0f, "C.1", "C.9"), Is.Null);
            Assert.That(Route(50f, 0f, "C.1", "C.9", "E").id, Is.EqualTo("E"));
        }

        [Test]
        public void OwnerDown_NearestLiveCellByGeometry()
        {
            MeshCell routed = MeshLoginRouting.Route(_world, Map1, new Vector3(50f, 0f, 0f), new[] { "N", "E", "SW" }, out bool fellBack);

            Assert.That((routed.id, fellBack), Is.EqualTo(("E", true)));
        }

        [TestCase(new[] { "N", "E" }, "N")]
        [TestCase(new[] { "E", "N" }, "E")]
        public void Tie_KeepsPeerOrder(string[] live, string expected)
        {
            Assert.That(Route(0f, 0f, live).id, Is.EqualTo(expected));
        }

        [Test]
        public void RegisteredOwner_IsTheResult()
        {
            MeshCell routed = MeshLoginRouting.Route(_world, Map1, new Vector3(50f, 0f, 0f), new[] { "E", "c" }, out bool fellBack);

            Assert.That((routed.id, fellBack), Is.EqualTo(("C", false)));
        }

        [Test]
        public void ActiveChildOwningThePoint_IsTheResult()
        {
            Apply(SplitC());

            Assert.That(Route(50f, 0f, "C", "C.1").id, Is.EqualTo("C.1"));
            Assert.That(Route(-50f, 0f, "C", "C.1").id, Is.EqualTo("C"));
        }

        [Test]
        public void PendingChildOverR_LeavesTheParentOwningIt()
        {
            Apply(PendingE1());

            Assert.That(MeshLoginRouting.OwnerAt(_world, Map1, new Vector3(250f, 0f, 0f)).id, Is.EqualTo("E"));
            Assert.That(Route(250f, 0f, "E.1", "E").id, Is.EqualTo("E"));
        }

        [Test]
        public void StaticMap_RoutesExactlyWhereCellAtDoes()
        {
            MeshWorldRegistry staticWorld = RigLike(map1Dynamic: false);
            try
            {
                List<string> live = Ids(staticWorld.CellsOf(Map1));
                for (float x = -400f; x <= 400f; x += 50f)
                {
                    for (float z = -400f; z <= 400f; z += 50f)
                    {
                        Vector3 pos = new Vector3(x, 0f, z);
                        MeshCell routed = MeshLoginRouting.Route(staticWorld, Map1, pos, live, out bool fellBack);

                        Assert.That(routed, Is.SameAs(staticWorld.CellAt(Map1, pos)), $"at {pos}");
                        Assert.That(fellBack, Is.False, $"at {pos}");
                    }
                }
            }
            finally
            {
                Object.DestroyImmediate(staticWorld);
            }
        }
    }

    public class MeshSpawnerCellSelectionTests
    {
        private BandEnvironment _band;
        private readonly List<MeshWorldRegistry> _worlds = new List<MeshWorldRegistry>();

        [SetUp]
        public void SetUp() => _band = new BandEnvironment();

        [TearDown]
        public void TearDown()
        {
            foreach (MeshWorldRegistry world in _worlds)
                Object.DestroyImmediate(world);
            _band.Dispose();
        }

        private MeshWorldRegistry World(bool dynamicMap)
        {
            MeshWorldRegistry world = RigLike(dynamicMap);
            _worlds.Add(world);
            return world;
        }

        private static List<string> Started(MeshWorldRegistry world, System.Func<string, bool> serves = null)
        {
            List<KeyValuePair<string, string>> cells = new List<KeyValuePair<string, string>>();
            MeshMapSpawnNetworkManager.AppendCellsToStart(world, Map1, serves ?? (id => true), cells);
            return cells.ConvertAll(kv => kv.Key);
        }

        [Test]
        public void DynamicChild_IsNotStarted()
        {
            MeshWorldRegistry world = World(true);
            Assert.That(world.ApplyLayout(SplitC(), out string reason), Is.True, reason);

            Assert.That(Started(world), Has.No.Member("C.1").And.Member("C"));
        }

        [Test]
        public void RetiredChild_IsNotStarted()
        {
            MeshWorldRegistry world = World(true);
            Assert.That(world.ApplyLayout(SplitC(1), out string reason), Is.True, reason);
            Assert.That(world.ApplyLayout(Snapshot(2), out reason), Is.True, reason);

            Assert.That(world.GetCellById("C.1").state, Is.EqualTo(MeshCellState.Retired));
            Assert.That(Started(world), Has.No.Member("C.1"));
        }

        [Test]
        public void CellAnotherHostServes_IsNotStarted()
        {
            Assert.That(Started(World(false), id => id != "E"), Has.No.Member("E"));
        }

        [Test]
        public void DynamicMapBaseCells_AreStarted()
        {
            Assert.That(Started(World(true)), Is.EqualTo(new[] { "SW", "S", "SE", "W", "C", "E", "NW", "N", "NE" }));
        }

        [Test]
        public void StaticMap_StartsEveryCellInRegistryOrder_WithItsMapId()
        {
            List<KeyValuePair<string, string>> cells = new List<KeyValuePair<string, string>>();
            MeshMapSpawnNetworkManager.AppendCellsToStart(World(false), Map1, id => true, cells);

            Assert.That(cells.ConvertAll(kv => kv.Key), Is.EqualTo(new[] { "SW", "S", "SE", "W", "C", "E", "NW", "N", "NE" }));
            Assert.That(cells.TrueForAll(kv => kv.Value == Map1), Is.True);
        }
    }

    public class MeshSpawnOnceTests
    {
        [Test]
        public void SecondRegistration_StartsNothing()
        {
            MeshSpawnOnce once = new MeshSpawnOnce();

            Assert.That(once.TryStart(), Is.True);
            Assert.That(once.TryStart(), Is.False);
            Assert.That(once.Started, Is.True);
        }
    }

    public class MeshSpawnerForTests
    {
        private static MeshCell Cell(string id, string baseId, bool isDynamic) => new MeshCell { id = id, baseId = baseId, isDynamic = isDynamic };

        [Test]
        public void NoSpawner_IsMinusOne()
        {
            Assert.That(MeshCentralNetworkManager.SpawnerFor(Cell("E", "E", false), new Dictionary<string, long>(), new List<long>(), out bool advertised), Is.EqualTo(-1));
            Assert.That(advertised, Is.False);
        }

        [Test]
        public void StaleAdvertisement_FallsBackToTheFirstSpawner()
        {
            long spawner = MeshCentralNetworkManager.SpawnerFor(Cell("E", "E", false), new Dictionary<string, long> { { "E", 7 } }, new List<long> { 5, 6 }, out bool advertised);

            Assert.That((spawner, advertised), Is.EqualTo((5L, false)));
        }

        [Test]
        public void AssetCell_GetsTheSpawnerThatAdvertisedIt()
        {
            long spawner = MeshCentralNetworkManager.SpawnerFor(Cell("E", "E", false), new Dictionary<string, long> { { "E", 7 } }, new List<long> { 5, 7 }, out bool advertised);

            Assert.That((spawner, advertised), Is.EqualTo((7L, true)));
        }

        [Test]
        public void DynamicCell_GetsItsBaseCellsSpawner()
        {
            long spawner = MeshCentralNetworkManager.SpawnerFor(Cell("C.1", "C", true), new Dictionary<string, long> { { "C", 9 }, { "C.1", 5 } }, new List<long> { 5, 9 }, out bool advertised);

            Assert.That((spawner, advertised), Is.EqualTo((9L, true)));
        }
    }

    public class MeshSpawnerForFamilyTests
    {
        private static long For(string baseId, Dictionary<string, long> byCell, long[] servingAll, long[] registered, out string error)
        {
            return MeshCentralNetworkManager.SpawnerForFamily(baseId, byCell, new HashSet<long>(servingAll), new List<long>(registered), out error);
        }

        [Test]
        public void AdvertisedBase_GetsItsSpawner()
        {
            long spawner = For("C", new Dictionary<string, long> { { "C", 9 }, { "E", 5 } }, new long[0], new long[] { 5, 9 }, out string error);

            Assert.That((spawner, error), Is.EqualTo((9L, (string)null)));
        }

        [Test]
        public void AdvertisedBaseWhoseSpawnerIsAway_GoesToNoOtherHost()
        {
            long spawner = For("C", new Dictionary<string, long> { { "C", 9 }, { "E", 5 } }, new long[] { 6 }, new long[] { 5, 6 }, out string error);

            Assert.That(spawner, Is.EqualTo(-1));
            Assert.That(error, Does.Contain("serves C").And.Contain("no other host"));
        }

        [Test]
        public void UnadvertisedBase_GoesToASpawnerThatServesEveryCell()
        {
            long spawner = For("C", new Dictionary<string, long> { { "E", 5 } }, new long[] { 6 }, new long[] { 5, 6 }, out string error);

            Assert.That((spawner, error), Is.EqualTo((6L, (string)null)));
        }

        [Test]
        public void UnadvertisedBase_WithEveryHostListingOthers_IsRefused()
        {
            long spawner = For("C", new Dictionary<string, long> { { "E", 5 } }, new long[0], new long[] { 5 }, out string error);

            Assert.That(spawner, Is.EqualTo(-1));
            Assert.That(error, Does.Contain("no registered map-spawn server serves C"));
        }

        [Test]
        public void SpawnerWhoseListHasNotArrived_IsNotAsked()
        {
            long spawner = For("C", new Dictionary<string, long>(), new long[0], new long[] { 5 }, out string error);

            Assert.That(spawner, Is.EqualTo(-1));
            Assert.That(error, Does.Contain("none serves every cell"));
        }

        [Test]
        public void NoSpawner_SaysSo()
        {
            long spawner = For("C", new Dictionary<string, long>(), new long[] { 6 }, new long[0], out string error);

            Assert.That((spawner, error), Is.EqualTo((-1L, "no map-spawn server is registered")));
        }

        [Test]
        public void ServingAllSpawnerThatLeft_IsNotAsked()
        {
            long spawner = For("C", new Dictionary<string, long>(), new long[] { 6 }, new long[] { 7 }, out _);

            Assert.That(spawner, Is.EqualTo(-1));
        }
    }
}
