using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using static MultiplayerARPG.MMO.Mesh.Tests.MeshFakeWorlds;
using Object = UnityEngine.Object;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>Slice 7 (slices/slice7_design.md decisions 1-4, 7, 9 and 12): the on-demand link book, its toggles and the relay's targets.</summary>
    public class MeshLinkNeedsTests
    {
        private const float Idle = 30f;
        private const float Grace = 10f;

        private readonly List<KeyValuePair<string, MeshLinkNeedReason>> _wanted = new List<KeyValuePair<string, MeshLinkNeedReason>>();
        private readonly List<string> _close = new List<string>();
        private readonly List<MeshWorldRegistry> _worlds = new List<MeshWorldRegistry>();

        [TearDown]
        public void TearDown()
        {
            foreach (MeshWorldRegistry world in _worlds)
                Object.DestroyImmediate(world);
            _worlds.Clear();
        }

        private static MeshLinkBook Book() => new MeshLinkBook { IdleSeconds = Idle, GraceSeconds = Grace };

        /// <summary>One tick: the given wants, then Settle over the given links.</summary>
        private void Tick(MeshLinkBook book, float now, ICollection<string> linked, params (string zone, MeshLinkNeedReason reason)[] wants)
        {
            book.BeginTick();
            foreach ((string zone, MeshLinkNeedReason reason) in wants)
                book.Want(zone, reason, now);
            book.Settle(now, linked, _wanted, _close);
        }

        private List<string> WantedZones()
        {
            List<string> zones = new List<string>();
            foreach (KeyValuePair<string, MeshLinkNeedReason> kv in _wanted)
                zones.Add(kv.Key);
            return zones;
        }

        // ---------------------------------------------------------------- what must never close

        [Test]
        public void BandNeighbour_NeverCloses_HoweverLongItIsSilent()
        {
            MeshLinkBook book = Book();
            HashSet<string> linked = new HashSet<string> { "E" };
            for (float t = 0f; t <= 600f; t += 1f)
            {
                Tick(book, t, linked, ("E", MeshLinkNeedReason.Band));
                Assert.That(_close, Is.Empty, $"closed at {t}");
            }
            Assert.That(book.ReasonOf("E"), Is.EqualTo(MeshLinkNeedReason.Band));
        }

        [Test]
        public void Record_KeepsItsZone_AndTheLinkClosesIdleAfterTheRecordGoes()
        {
            MeshLinkBook book = Book();
            HashSet<string> linked = new HashSet<string> { "NE" };
            for (float t = 0f; t <= 100f; t += 1f)
            {
                Tick(book, t, linked, ("NE", MeshLinkNeedReason.Record));
                Assert.That(_close, Is.Empty, $"closed at {t} while a record names it");
            }
            // The record is gone at 101: nothing closes before 100 + idle.
            for (float t = 101f; t < 100f + Idle; t += 1f)
            {
                Tick(book, t, linked);
                Assert.That(_close, Is.Empty, $"closed at {t}, before the idle time");
            }
            Tick(book, 100f + Idle, linked);
            Assert.That(_close, Is.EqualTo(new[] { "NE" }));
        }

        [Test]
        public void Traffic_EitherWay_PostponesTheClose()
        {
            MeshLinkBook book = Book();
            HashSet<string> linked = new HashSet<string> { "N" };
            Tick(book, 0f, linked, ("N", MeshLinkNeedReason.Record));
            book.NoteSent("N", 20f);
            Tick(book, 45f, linked);
            Assert.That(_close, Is.Empty, "a message sent at 20 keeps the link to 50");
            Tick(book, 50f, linked);
            Assert.That(_close, Is.EqualTo(new[] { "N" }));
        }

        [Test]
        public void Heard_WantsTheZoneAsRecent_ForTheIdleTime_AndOpensItsLink()
        {
            MeshLinkBook book = Book();
            book.NoteHeard("SE", 5f);
            Tick(book, 6f, new HashSet<string>());
            Assert.That(_wanted, Has.Count.EqualTo(1));
            Assert.That(_wanted[0].Key, Is.EqualTo("SE"));
            Assert.That(_wanted[0].Value, Is.EqualTo(MeshLinkNeedReason.Recent));
            HashSet<string> linked = new HashSet<string> { "SE" };
            Tick(book, 5f + Idle - 1f, linked);
            Assert.That(WantedZones(), Does.Contain("SE"));
            Tick(book, 5f + Idle, linked);
            Assert.That(WantedZones(), Is.Empty);
            Assert.That(_close, Is.Empty, "the want at 34 keeps it another idle time");
            Tick(book, 5f + Idle - 1f + Idle, linked);
            Assert.That(_close, Is.EqualTo(new[] { "SE" }));
        }

        [Test]
        public void SilentLink_TheBookNeverHeardOf_ClosesIdleAfterItIsFirstSeen()
        {
            MeshLinkBook book = Book();
            HashSet<string> linked = new HashSet<string> { "W" };
            Tick(book, 100f, linked);
            Assert.That(_close, Is.Empty);
            Tick(book, 100f + Idle - 1f, linked);
            Assert.That(_close, Is.Empty);
            Tick(book, 100f + Idle, linked);
            Assert.That(_close, Is.EqualTo(new[] { "W" }));
        }

        [Test]
        public void Closing_IsNotPlannedTwice_AndAWantAfterTheStopReopens()
        {
            MeshLinkBook book = Book();
            HashSet<string> linked = new HashSet<string> { "S" };
            Tick(book, 0f, linked, ("S", MeshLinkNeedReason.Demand));
            Tick(book, Idle, linked);
            Assert.That(_close, Is.EqualTo(new[] { "S" }));
            book.MarkClosing("S", Idle);
            Assert.That(book.IsClosing("S"), Is.True);
            Tick(book, Idle + 1f, linked);
            Assert.That(_close, Is.Empty, "a closing link is not closed again");
            book.Stopped("S");
            linked.Clear();
            Assert.That(book.IsClosing("S"), Is.False);
            Assert.That(book.Closed, Is.EqualTo(1));
            Tick(book, Idle + 2f, linked, ("S", MeshLinkNeedReason.Record));
            Assert.That(WantedZones(), Is.EqualTo(new[] { "S" }), "wanted again after the stop: the caller reopens it");
        }

        [Test]
        public void Reasons_TheStrongestOfTheTickWins()
        {
            MeshLinkBook book = Book();
            book.NoteHeard("E", 0f);
            Tick(book, 1f, null, ("E", MeshLinkNeedReason.Demand), ("E", MeshLinkNeedReason.Record));
            Assert.That(book.ReasonOf("E"), Is.EqualTo(MeshLinkNeedReason.Record));
            Tick(book, 2f, null, ("E", MeshLinkNeedReason.Record), ("E", MeshLinkNeedReason.Band));
            Assert.That(book.ReasonOf("E"), Is.EqualTo(MeshLinkNeedReason.Band));
            Tick(book, 3f, null);
            Assert.That(book.ReasonOf("E"), Is.EqualTo(MeshLinkNeedReason.Recent), "heard at 0: still Recent");
            Assert.That(book.WasWanted("E"), Is.True);
        }

        [Test]
        public void Want_IgnoresEmptyZonesAndNone()
        {
            MeshLinkBook book = Book();
            Tick(book, 0f, null, (null, MeshLinkNeedReason.Band), ("", MeshLinkNeedReason.Band), ("E", MeshLinkNeedReason.None));
            Assert.That(_wanted, Is.Empty);
            Assert.That(book.Count, Is.EqualTo(0));
        }

        [Test]
        public void Settle_WithNoLinks_WantsEveryWantedZone()
        {
            MeshLinkBook book = Book();
            Tick(book, 0f, new HashSet<string>(), ("E", MeshLinkNeedReason.Band), ("N", MeshLinkNeedReason.Band), ("C2", MeshLinkNeedReason.Record));
            Assert.That(WantedZones(), Is.EquivalentTo(new[] { "E", "N", "C2" }));
            Assert.That(_close, Is.Empty);
        }

        [Test]
        public void UnwantedUnlinkedZones_AreForgottenOnceIdle()
        {
            MeshLinkBook book = Book();
            Tick(book, 0f, null, ("X", MeshLinkNeedReason.Demand));
            Tick(book, 1f, null);
            Assert.That(book.Count, Is.EqualTo(1));
            Tick(book, Idle, null);
            Assert.That(book.Count, Is.EqualTo(0));
        }

        // ---------------------------------------------------------------- the sweep's grace (decision 7)

        [Test]
        public void IsSettling_WithinTheGrace_ThenNot()
        {
            MeshLinkBook book = Book();
            Tick(book, 100f, null, ("W", MeshLinkNeedReason.Record));
            Assert.That(book.IsSettling("W", 100f, connected: false), Is.True, "just wanted");
            Assert.That(book.IsSettling("W", 105f, connected: false), Is.True, "tried for 5 s");
            Tick(book, 110f, null, ("W", MeshLinkNeedReason.Record));
            Assert.That(book.IsSettling("W", 110f, connected: false), Is.False, "tried for the grace: unreachable");
            Assert.That(book.IsSettling("W", 105f, connected: true), Is.False, "connected");
        }

        [Test]
        public void IsSettling_ABandNeighbourThatDrops_IsNotSettling()
        {
            MeshLinkBook book = Book();
            for (float t = 0f; t <= 60f; t += 1f)
                Tick(book, t, new HashSet<string> { "E" }, ("E", MeshLinkNeedReason.Band));
            Assert.That(book.IsSettling("E", 60f, connected: false), Is.False, "wanted since 0: its crash is a crash");
        }

        [Test]
        public void IsSettling_AZoneNotWanted_IsNot_AndAWantAfterABreakRestartsTheGrace()
        {
            MeshLinkBook book = Book();
            Assert.That(book.IsSettling("W", 0f, false), Is.False, "unknown");
            Tick(book, 0f, null, ("W", MeshLinkNeedReason.Record));
            Tick(book, 50f, null);
            Assert.That(book.IsSettling("W", 50f, false), Is.False, "not wanted");
            Tick(book, 60f, null, ("W", MeshLinkNeedReason.Record));
            Assert.That(book.IsSettling("W", 65f, false), Is.True, "wanted again at 60");
        }

        [Test]
        public void MinIdleSeconds_OutlastsTheAckGuardAndTheReservation()
        {
            Assert.That(MeshLinkEnv.MinIdleSeconds(20f, 15f), Is.EqualTo(25f));
            Assert.That(MeshLinkEnv.MinIdleSeconds(10f, 15f), Is.EqualTo(20f));
        }

        // ---------------------------------------------------------------- the bound (decision 3)

        [Test]
        public void Bound_OnA10x10Grid_EachCellIsWantedByAtMostItsEightNeighbours()
        {
            MeshWorldRegistry world = ScriptableObject.CreateInstance<MeshWorldRegistry>();
            _worlds.Add(world);
            world.maps.Add(new MeshMapLayout { mapId = Map1, gridOrigin = new Vector3(-500f, 0f, -500f), cellSize = 100f, columns = 10, rows = 10 });
            world.Rebuild();
            IReadOnlyList<MeshCell> cells = world.CellsOf(Map1);
            Assert.That(cells, Has.Count.EqualTo(100));
            Dictionary<string, int> inbound = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (MeshCell cell in cells)
            {
                MeshLinkBook book = Book();
                book.BeginTick();
                foreach (MeshCell n in cell.bandNeighbours)
                    book.Want(n.id, MeshLinkNeedReason.Band, 0f);
                book.Settle(0f, null, _wanted, _close);
                Assert.That(_wanted.Count, Is.LessThanOrEqualTo(8), cell.id);
                foreach (KeyValuePair<string, MeshLinkNeedReason> kv in _wanted)
                    inbound[kv.Key] = (inbound.TryGetValue(kv.Key, out int n) ? n : 0) + 1;
            }
            int most = 0;
            foreach (int n in inbound.Values)
                most = Math.Max(most, n);
            Assert.That(most, Is.EqualTo(8), "an inner cell is wanted by its 8 neighbours; with linkAllZones it takes 99");
            Assert.That(most, Is.LessThan(MeshLinkEnv.DefaultCapacity));
        }

        // ---------------------------------------------------------------- MESH_LINKS and the other toggles

        [Test]
        public void ParseLinkModes_Malformed_Throws()
        {
            Assert.Throws<FormatException>(() => MeshLinkEnv.ParseLinkModes("all"));
            Assert.Throws<FormatException>(() => MeshLinkEnv.ParseLinkModes(":OnDemand"));
            Assert.Throws<FormatException>(() => MeshLinkEnv.ParseLinkModes("all:Sometimes"));
            Assert.Throws<FormatException>(() => MeshLinkEnv.ParseLinkModes("Map1:OnDemand,Map1:Off"));
        }

        [Test]
        public void ParseLinkModes_AllAndAMap()
        {
            Assert.That(MeshLinkEnv.ParseLinkModes(""), Is.Empty);
            Dictionary<string, bool> modes = MeshLinkEnv.ParseLinkModes(" ALL:ondemand , Map2:Off ");
            Assert.That(modes[MeshLayoutOverride.AllMaps], Is.True);
            Assert.That(modes["Map2"], Is.False);
            Assert.That(modes, Has.Count.EqualTo(2));
        }

        [Test]
        public void ApplyLinkModes_AllFirst_ThenEachMap_UnknownReported()
        {
            List<MeshMapLayout> maps = new List<MeshMapLayout> { new MeshMapLayout { mapId = Map1 }, new MeshMapLayout { mapId = Map2 } };
            List<string> unknown = new List<string>();
            MeshLayoutOverride.ApplyLinkModes(maps, MeshLinkEnv.ParseLinkModes("Map2:Off,all:OnDemand,Map9:OnDemand"), unknown);
            Assert.That(maps[0].onDemandLinks, Is.True);
            Assert.That(maps[1].onDemandLinks, Is.False);
            Assert.That(unknown, Is.EqualTo(new[] { "Map9" }));
        }

        [Test]
        public void Resolve_WithOnlyMeshLinks_MakesACopyThatLinksOnDemand_AndLeavesTheAssetAlone()
        {
            string[] keys = { MeshLinkEnv.ENV_LINKS, MeshLayoutOverride.ENV, MeshLayoutOverride.ENV_CELL_MODE };
            string[] saved = Array.ConvertAll(keys, Environment.GetEnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable(MeshLayoutOverride.ENV, null);
                Environment.SetEnvironmentVariable(MeshLayoutOverride.ENV_CELL_MODE, null);
                Environment.SetEnvironmentVariable(MeshLinkEnv.ENV_LINKS, Map1 + ":OnDemand");
                MeshWorldRegistry asset = RigLike(false);
                _worlds.Add(asset);
                MeshWorldRegistry copy = MeshLayoutOverride.Resolve(asset);
                _worlds.Add(copy);
                Assert.That(copy, Is.Not.SameAs(asset));
                Assert.That(copy.GetLayout(Map1).onDemandLinks, Is.True);
                Assert.That(copy.GetLayout(Map2).onDemandLinks, Is.False);
                Assert.That(asset.GetLayout(Map1).onDemandLinks, Is.False, "the asset is never written");
            }
            finally
            {
                for (int i = 0; i < keys.Length; ++i)
                    Environment.SetEnvironmentVariable(keys[i], saved[i]);
            }
        }

        [Test]
        public void CopyModeFrom_CopiesOnDemandLinks()
        {
            MeshMapLayout a = new MeshMapLayout { mapId = Map1, onDemandLinks = true };
            MeshMapLayout b = new MeshMapLayout { mapId = Map1 };
            b.CopyModeFrom(a);
            Assert.That(b.onDemandLinks, Is.True);
        }

        [Test]
        public void Capacity_And_Idle_FallBackOnAnythingUnusable()
        {
            Assert.That(MeshLinkEnv.Capacity(null), Is.EqualTo(64));
            Assert.That(MeshLinkEnv.Capacity("0"), Is.EqualTo(64));
            Assert.That(MeshLinkEnv.Capacity("ten"), Is.EqualTo(64));
            Assert.That(MeshLinkEnv.Capacity("10"), Is.EqualTo(10));
            Assert.That(MeshLinkEnv.IdleSeconds(null, 30f), Is.EqualTo(30f));
            Assert.That(MeshLinkEnv.IdleSeconds("-1", 30f), Is.EqualTo(30f));
            Assert.That(MeshLinkEnv.IdleSeconds("7.5", 30f), Is.EqualTo(7.5f));
        }

        // ---------------------------------------------------------------- the relay (decision 9)

        private static MeshMapServerPeer Peer(long connectionId, string cellId, string mapId) => new MeshMapServerPeer { connectionId = connectionId, cellId = cellId, mapId = mapId };

        [Test]
        public void RelayTargets_SameMap_SenderAndOtherMapsLeftOut()
        {
            List<MeshMapServerPeer> peers = new List<MeshMapServerPeer>
            {
                Peer(1, "C", Map1), Peer(2, "E", Map1), Peer(3, "C.1", Map1), Peer(4, "C2", Map2), Peer(5, "W", Map1),
            };
            Assert.That(MeshPartyRelay.Targets(peers, Map1, 1), Is.EqualTo(new long[] { 2, 3, 5 }));
            Assert.That(MeshPartyRelay.Targets(peers, Map2, 1), Is.EqualTo(new long[] { 4 }));
            Assert.That(MeshPartyRelay.Targets(peers, "", 1), Is.Empty);
            Assert.That(MeshPartyRelay.Targets(null, Map1, 1), Is.Empty);
        }

        [Test]
        public void RelayMessage_RoundTrips()
        {
            MeshPartyShareRelayMessage sent = new MeshPartyShareRelayMessage
            {
                mapId = Map1, fromCellId = "C.1",
                share = new GhostPartyShareMessage { memberId = "m", exp = 10, expMultiplier = 0.5f, gold = 3, goldMultiplier = 0.5f, currencyDataIds = new[] { 7 }, currencyAmounts = new[] { 2 }, giverLevel = 4, sourceLevel = 5, monsterDataId = 9, countKill = true, broadcast = true },
            };
            LiteNetLib.Utils.NetDataWriter writer = new LiteNetLib.Utils.NetDataWriter();
            sent.Serialize(writer);
            MeshPartyShareRelayMessage got = new MeshPartyShareRelayMessage();
            got.Deserialize(new LiteNetLib.Utils.NetDataReader(writer.CopyData()));
            Assert.That(got.mapId, Is.EqualTo(Map1));
            Assert.That(got.fromCellId, Is.EqualTo("C.1"));
            Assert.That(got.share.memberId, Is.EqualTo("m"));
            Assert.That(got.share.exp, Is.EqualTo(10));
            Assert.That(got.share.currencyAmounts, Is.EqualTo(new[] { 2 }));
            Assert.That(got.share.countKill && got.share.broadcast, Is.True);
        }
    }
}
