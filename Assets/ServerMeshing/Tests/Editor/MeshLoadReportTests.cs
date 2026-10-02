using System;
using System.Collections.Generic;
using System.IO;
using LiteNetLib.Utils;
using NUnit.Framework;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class MeshLoadReportRoundTripTests
    {
        private static MeshLoadReportMessage Sample(bool withGrid)
        {
            MeshLoadReportMessage report = new MeshLoadReportMessage
            {
                cellId = "C.1", mapId = "Map", incarnation = "abc", layoutIncarnation = 7, layoutVersion = 42, windowSeconds = 5.1f,
                work = new MeshWorkWindow
                {
                    frames = 300, workMeanMs = 2.5f, workP95Ms = 3.75f, workMaxMs = 9f, waitMeanMs = 14.1f,
                    periodMeanMs = 16.6f, saturatedShare = 0.02f, budgetMs = 16.67f, state = MeshProbeState.Valid,
                },
                registeredPlayers = 3, ownedPlayers = 12, ownedMonsters = 30, puppetPlayers = 4, puppetMonsters = 5,
                puppetDrops = 1, puppetStatics = 2, clients = 3, clientOutKBps = 4.5f, linkOutKBps = 12.25f,
                cpuPercent = 88f, ownedOutside = 6, ownedDrops = 7, ownedStatics = 8, pendingAcks = 1, reserved = 2,
            };
            if (withGrid)
            {
                report.grid = MeshOccupancyGrid.Covering(-40f, -40f, 40f, 40f, 20f);
                report.grid.Add(MeshOccupancyLayer.OwnedPlayers, -30f, -30f);
                report.grid.Add(MeshOccupancyLayer.Puppets, 30f, 30f);
            }
            return report;
        }

        private static MeshLoadReportMessage RoundTrip(MeshLoadReportMessage report, out int leftover)
        {
            NetDataWriter writer = new NetDataWriter();
            report.Serialize(writer);
            NetDataReader reader = new NetDataReader(writer.CopyData());
            MeshLoadReportMessage copy = new MeshLoadReportMessage();
            copy.Deserialize(reader);
            leftover = reader.AvailableBytes;
            return copy;
        }

        [TestCase(false, TestName = "LoadReport_RoundTrips_WithoutAGrid")]
        [TestCase(true, TestName = "LoadReport_RoundTrips_WithAGrid")]
        public void RoundTrips(bool withGrid)
        {
            MeshLoadReportMessage report = Sample(withGrid);
            MeshLoadReportMessage copy = RoundTrip(report, out int leftover);

            Assert.That(leftover, Is.EqualTo(0));
            Assert.That(copy.cellId, Is.EqualTo(report.cellId));
            Assert.That(copy.mapId, Is.EqualTo(report.mapId));
            Assert.That(copy.incarnation, Is.EqualTo(report.incarnation));
            Assert.That(copy.layoutIncarnation, Is.EqualTo(report.layoutIncarnation));
            Assert.That(copy.layoutVersion, Is.EqualTo(report.layoutVersion));
            Assert.That(copy.windowSeconds, Is.EqualTo(report.windowSeconds));
            Assert.That(copy.work, Is.EqualTo(report.work));
            Assert.That(new[] { copy.registeredPlayers, copy.ownedPlayers, copy.ownedMonsters, copy.puppetPlayers, copy.puppetMonsters, copy.puppetDrops, copy.puppetStatics, copy.clients },
                Is.EqualTo(new[] { 3, 12, 30, 4, 5, 1, 2, 3 }));
            Assert.That(new[] { copy.clientOutKBps, copy.linkOutKBps, copy.cpuPercent }, Is.EqualTo(new[] { 4.5f, 12.25f, 88f }));
            Assert.That(new[] { copy.ownedOutside, copy.ownedDrops, copy.ownedStatics, copy.pendingAcks, copy.reserved }, Is.EqualTo(new[] { 6, 7, 8, 1, 2 }));
            if (!withGrid)
            {
                Assert.That(copy.grid, Is.Null);
                return;
            }
            Assert.That(copy.grid.Count(MeshOccupancyLayer.OwnedPlayers, 0, 0), Is.EqualTo(1));
            Assert.That(copy.grid.Count(MeshOccupancyLayer.Puppets, 3, 3), Is.EqualTo(1));
        }

        [Test]
        public void NullStrings_RoundTripAsEmpty()
        {
            MeshLoadReportMessage copy = RoundTrip(new MeshLoadReportMessage(), out int leftover);
            Assert.That(leftover, Is.EqualTo(0));
            Assert.That(copy.cellId, Is.Empty);
            Assert.That(copy.grid, Is.Null);
        }
    }

    public class MeshOccupancyGridLimitTests
    {
        [Test]
        public void Serialize_RoundTripsEveryField()
        {
            MeshOccupancyGrid grid = MeshOccupancyGrid.Covering(-45f, -25f, 45f, 25f, 10f);
            int n = 0;
            foreach (MeshOccupancyLayer layer in Enum.GetValues(typeof(MeshOccupancyLayer)))
            {
                for (int k = 0; k <= n; ++k)
                    grid.Add(layer, -40f + 10f * n, -20f);
                n++;
            }
            NetDataWriter writer = new NetDataWriter();
            grid.Serialize(writer);
            MeshOccupancyGrid copy = new MeshOccupancyGrid();
            copy.Deserialize(new NetDataReader(writer.CopyData()));

            Assert.That(copy.bucketSize, Is.EqualTo(grid.bucketSize));
            Assert.That(copy.originX, Is.EqualTo(grid.originX));
            Assert.That(copy.originZ, Is.EqualTo(grid.originZ));
            Assert.That(copy.columns, Is.EqualTo(grid.columns));
            Assert.That(copy.rows, Is.EqualTo(grid.rows));
            for (int layer = 0; layer < MeshOccupancyGrid.LayerCount; ++layer)
                Assert.That(copy.layers[layer], Is.EqualTo(grid.layers[layer]), $"layer {layer}");
        }

        [Test]
        public void Covering_GrowsTheBucketPastMaxSide()
        {
            MeshOccupancyGrid grid = MeshOccupancyGrid.Covering(0f, 0f, 100_000f, 50_000f, 20f);
            Assert.That(grid.columns, Is.LessThanOrEqualTo(MeshOccupancyGrid.MaxSide));
            Assert.That(grid.rows, Is.LessThanOrEqualTo(MeshOccupancyGrid.MaxSide));
            Assert.That(grid.bucketSize, Is.GreaterThan(20f));
            Assert.That(grid.Add(MeshOccupancyLayer.OwnedPlayers, 99_999f, 49_999f), Is.True);
        }

        private static IEnumerable<TestCaseData> BadHeaders()
        {
            yield return new TestCaseData(0f, (ushort)4, (ushort)4, 128).SetName("Deserialize_ZeroBucket_Throws");
            yield return new TestCaseData(20f, (ushort)0, (ushort)4, 128).SetName("Deserialize_ZeroColumns_Throws");
            yield return new TestCaseData(20f, (ushort)(MeshOccupancyGrid.MaxSide + 1), (ushort)1, 4096).SetName("Deserialize_TooWide_Throws");
            yield return new TestCaseData(20f, (ushort)100, (ushort)100, 16).SetName("Deserialize_LongerThanPacket_Throws");
        }

        [TestCaseSource(nameof(BadHeaders))]
        public void Deserialize_RejectsBadHeaders(float bucket, ushort columns, ushort rows, int bodyBytes)
        {
            NetDataWriter writer = new NetDataWriter();
            writer.Put(bucket);
            writer.Put(0f);
            writer.Put(0f);
            writer.Put(columns);
            writer.Put(rows);
            for (int i = 0; i < bodyBytes; ++i)
                writer.Put((byte)0);
            Assert.Throws<InvalidDataException>(() => new MeshOccupancyGrid().Deserialize(new NetDataReader(writer.CopyData())));
        }
    }

    public class MeshLoadBoardTests
    {
        private static MeshLoadReportMessage Report(string cell, float util)
        {
            return new MeshLoadReportMessage
            {
                cellId = cell,
                work = new MeshWorkWindow { workMeanMs = util * 16.67f, budgetMs = 16.67f, state = MeshProbeState.Valid },
                ownedPlayers = 5,
            };
        }

        [Test]
        public void Record_RefusesAReportWithoutACell()
        {
            MeshLoadBoard board = new MeshLoadBoard();
            Assert.That(board.Record(null, 0f), Is.False);
            Assert.That(board.Record(new MeshLoadReportMessage(), 0f), Is.False);
            Assert.That(board.Count, Is.EqualTo(0));
        }

        [Test]
        public void Record_KeepsTheLatestPerCell_CaseInsensitive()
        {
            MeshLoadBoard board = new MeshLoadBoard();
            board.Record(Report("C", 0.1f), 1f);
            board.Record(Report("c", 0.5f), 2f);
            Assert.That(board.Count, Is.EqualTo(1));
            Assert.That(board.TryGet("C", out MeshLoadBoard.MeshLoadEntry entry), Is.True);
            Assert.That(entry.receivedAt, Is.EqualTo(2f));
            Assert.That(entry.report.work.Utilisation, Is.EqualTo(0.5f).Within(1e-4f));
        }

        [Test]
        public void Forget_DropsTheCell()
        {
            MeshLoadBoard board = new MeshLoadBoard();
            board.Record(Report("C", 0.1f), 1f);
            board.Forget("C");
            board.Forget(null);
            Assert.That(board.TryGet("C", out _), Is.False);
        }

        [Test]
        public void Summary_ListsRecentCellsInOrder()
        {
            MeshLoadBoard board = new MeshLoadBoard();
            Assert.That(board.Summary(0f, 30f), Is.EqualTo("no recent reports"));
            board.Record(Report("E", 0.25f), 100f);
            board.Record(Report("C", 0.5f), 100f);
            board.Record(Report("OLD", 0.9f), 10f);
            string summary = board.Summary(105f, 30f);
            Assert.That(summary, Does.StartWith("C util 50%"));
            Assert.That(summary, Does.Contain("| E util 25%"));
            Assert.That(summary, Does.Not.Contain("OLD"));
        }
    }

    public class MeshLayoutOverrideResolveTests
    {
        private string _saved, _savedMode;
        private readonly List<UnityEngine.Object> _made = new List<UnityEngine.Object>();

        [SetUp]
        public void SetUp()
        {
            _saved = Environment.GetEnvironmentVariable(MeshLayoutOverride.ENV);
            _savedMode = Environment.GetEnvironmentVariable(MeshLayoutOverride.ENV_CELL_MODE);
            Environment.SetEnvironmentVariable(MeshLayoutOverride.ENV, null);
            Environment.SetEnvironmentVariable(MeshLayoutOverride.ENV_CELL_MODE, null);
        }

        [TearDown]
        public void TearDown()
        {
            Environment.SetEnvironmentVariable(MeshLayoutOverride.ENV, _saved);
            Environment.SetEnvironmentVariable(MeshLayoutOverride.ENV_CELL_MODE, _savedMode);
            foreach (UnityEngine.Object o in _made)
                if (o != null) UnityEngine.Object.DestroyImmediate(o);
            _made.Clear();
        }

        private MeshWorldRegistry Registry()
        {
            MeshWorldRegistry world = ScriptableObject.CreateInstance<MeshWorldRegistry>();
            world.maps.Add(new MeshMapLayout { mapId = "M", gridOrigin = new Vector3(-100f, 0f, -100f), cellSize = 100f, columns = 2, rows = 2 });
            _made.Add(world);
            return world;
        }

        [Test]
        public void Resolve_Null_IsNull()
        {
            Assert.That(MeshLayoutOverride.Resolve(null), Is.Null);
        }

        [Test]
        public void Resolve_Unset_ReturnsTheAsset()
        {
            Environment.SetEnvironmentVariable(MeshLayoutOverride.ENV, null);
            MeshWorldRegistry world = Registry();
            Assert.That(MeshLayoutOverride.Resolve(world), Is.SameAs(world));
        }

        [Test]
        public void Resolve_Malformed_FallsBackToTheAsset()
        {
            Environment.SetEnvironmentVariable(MeshLayoutOverride.ENV, "M=C@1,2");
            MeshWorldRegistry world = Registry();
            UnityEngine.TestTools.LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("needs 4 numbers"));
            Assert.That(MeshLayoutOverride.Resolve(world), Is.SameAs(world));
        }

        [Test]
        public void Resolve_Set_ReCutsACopy_TheSameCopyEveryTime_AndLeavesTheAssetAlone()
        {
            Environment.SetEnvironmentVariable(MeshLayoutOverride.ENV, "M=C@-100,-100,200,200");
            MeshWorldRegistry world = Registry();

            MeshWorldRegistry copy = MeshLayoutOverride.Resolve(world);
            _made.Add(copy);

            Assert.That(copy, Is.Not.SameAs(world));
            Assert.That(MeshLayoutOverride.Resolve(world), Is.SameAs(copy));
            Assert.That(MeshLayoutOverride.Resolve(copy), Is.SameAs(copy));
            Assert.That(copy.CellsOf("M").Count, Is.EqualTo(1));
            Assert.That(copy.GetCellById("C").worldMaxX, Is.EqualTo(100f));
            Assert.That(world.maps[0].IsExplicit, Is.False);
            Assert.That(world.CellsOf("M").Count, Is.EqualTo(4));
        }

        [Test]
        public void Resolve_UnknownMap_IsLoggedAndIgnored()
        {
            Environment.SetEnvironmentVariable(MeshLayoutOverride.ENV, "Other=C@0,0,10,10");
            MeshWorldRegistry world = Registry();
            UnityEngine.TestTools.LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("has no layout"));
            MeshWorldRegistry copy = MeshLayoutOverride.Resolve(world);
            _made.Add(copy);
            Assert.That(copy.CellsOf("M").Count, Is.EqualTo(4));
        }

        [Test]
        public void Resolve_BothMalformed_IsTheAsset()
        {
            Environment.SetEnvironmentVariable(MeshLayoutOverride.ENV, "M=C@1,2");
            Environment.SetEnvironmentVariable(MeshLayoutOverride.ENV_CELL_MODE, "M:Sideways");
            MeshWorldRegistry world = Registry();
            UnityEngine.TestTools.LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("needs 4 numbers"));
            UnityEngine.TestTools.LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("neither Static nor Dynamic"));

            Assert.That(MeshLayoutOverride.Resolve(world), Is.SameAs(world));
        }

        [Test]
        public void Resolve_MalformedModeWithAValidLayout_ReCutsAndIgnoresTheMode()
        {
            Environment.SetEnvironmentVariable(MeshLayoutOverride.ENV, "M=C@-100,-100,200,200");
            Environment.SetEnvironmentVariable(MeshLayoutOverride.ENV_CELL_MODE, "M");
            MeshWorldRegistry world = Registry();
            UnityEngine.TestTools.LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("MESH_CELL_MODE: 'M' is not"));

            MeshWorldRegistry copy = MeshLayoutOverride.Resolve(world);
            _made.Add(copy);

            Assert.That(copy.CellsOf("M").Count, Is.EqualTo(1));
            Assert.That(copy.IsDynamicMap("M"), Is.False);
        }

        [Test]
        public void Resolve_UnknownMapInTheMode_IsLoggedAndIgnored()
        {
            Environment.SetEnvironmentVariable(MeshLayoutOverride.ENV_CELL_MODE, "Other:Dynamic");
            MeshWorldRegistry world = Registry();
            UnityEngine.TestTools.LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("MESH_CELL_MODE: map 'Other' has no layout"));

            MeshWorldRegistry copy = MeshLayoutOverride.Resolve(world);
            _made.Add(copy);

            Assert.That(copy.HasDynamicMap, Is.False);
        }

        [Test]
        public void Resolve_OnlyTheMode_MakesADynamicCopy_AndLeavesTheAssetAlone()
        {
            Environment.SetEnvironmentVariable(MeshLayoutOverride.ENV_CELL_MODE, "all:Dynamic");
            MeshWorldRegistry world = Registry();
            string before = UnityEditor.EditorJsonUtility.ToJson(world);

            MeshWorldRegistry copy = MeshLayoutOverride.Resolve(world);
            _made.Add(copy);

            Assert.That(copy, Is.Not.SameAs(world));
            Assert.That(copy.IsDynamicMap("M"), Is.True);
            Assert.That(copy.CellsOf("M").Count, Is.EqualTo(4));
            Assert.That(UnityEditor.EditorJsonUtility.ToJson(world), Is.EqualTo(before));
        }

        [Test]
        public void Resolve_BothSet_OneCopyCarriesBoth_EveryTime()
        {
            Environment.SetEnvironmentVariable(MeshLayoutOverride.ENV, "M=C@-100,-100,200,200");
            Environment.SetEnvironmentVariable(MeshLayoutOverride.ENV_CELL_MODE, "all:Static,M:Dynamic");
            MeshWorldRegistry world = Registry();

            MeshWorldRegistry copy = MeshLayoutOverride.Resolve(world);
            _made.Add(copy);

            Assert.That(copy.CellsOf("M").Count, Is.EqualTo(1));
            Assert.That(copy.IsDynamicMap("M"), Is.True, "a map's own entry wins over all");
            Assert.That(MeshLayoutOverride.Resolve(world), Is.SameAs(copy));
            Assert.That(MeshLayoutOverride.Resolve(copy), Is.SameAs(copy));
        }
    }

    public class MeshBirthIncarnationTests
    {
        [TestCase(null, null, TestName = "BirthIncarnation_Null_IsNull")]
        [TestCase("", null, TestName = "BirthIncarnation_Empty_IsNull")]
        [TestCase("493cd2454f4949ebbf2f09748fe78af8", null, TestName = "BirthIncarnation_CharacterId_IsNull")]
        [TestCase("C:only", null, TestName = "BirthIncarnation_OneColon_IsNull")]
        [TestCase("C:8dd1234:17", "8dd1234", TestName = "BirthIncarnation_BirthId_IsTheMiddle")]
        [TestCase("C.1:ab:9", "ab", TestName = "BirthIncarnation_DynamicCellId")]
        public void BirthIncarnation(string meshId, string expected)
        {
            Assert.That(MeshIdentity.BirthIncarnation(meshId), Is.EqualTo(expected));
        }
    }
}
