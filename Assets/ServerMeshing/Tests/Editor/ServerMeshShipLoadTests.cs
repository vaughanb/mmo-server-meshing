using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class ServerMeshShipLoadTests
    {
        private const string FullLine = "Ships window 5.00 s: cell C ships 50 ai 46 mirror 2 parked 1 phantom 1 pilots 3 seated 2 outside 1 notInSlot 0 pending 3"
            + " | speed p50 1.50 max 3.76 patrol n 2 p50 1.50 m/s | scans 500 casts 7500 shots 300 bolts 600 hits 40 respawns 2 | shield writes 1500"
            + " | rpcSends 2400 | uploads 10 stale 1 bad 0 | ms/s pilot 0.40 engine 0.30 combat 0.20 bolts 0.90 mirror 0.00"
            + " | mirror age n 3 p50 26.0 ms frames 2 | gc0 1";

        private const string ZeroLine = "Ships window 0.00 s: cell - ships 0 ai 0 mirror 0 parked 0 phantom 0 pilots 0 seated 0 outside 0 notInSlot 0 pending 0"
            + " | speed p50 0.00 max 0.00 patrol n 0 p50 none m/s | scans 0 casts 0 shots 0 bolts 0 hits 0 respawns 0 | shield writes 0"
            + " | rpcSends 0 | uploads 0 stale 0 bad 0 | ms/s pilot 0.00 engine 0.00 combat 0.00 bolts 0.00 mirror 0.00"
            + " | mirror age n 0 p50 none ms frames none | gc0 0";

        private static readonly Rect Cell = Rect.MinMaxRect(-300f, -300f, 300f, 300f);

        private static void AssertPrintable(string line)
        {
            Assert.That(line, Does.Not.Contain("{").And.Not.Contain("}").And.Not.Contain("Exception").And.Not.Contain("NaN"));
        }

        [Test]
        public void TryLoadArea_DisjointArea_FailsNamingTheKey()
        {
            bool ok = ServerMeshShipLoad.TryLoadArea(Cell, true, Rect.MinMaxRect(400f, 400f, 450f, 450f), out _, out string error);

            Assert.That(ok, Is.False);
            StringAssert.Contains(ServerMeshShipSettings.AreaKey, error);
        }

        [Test]
        public void TryLoadArea_NoArea_IsTheCell()
        {
            Assert.That(ServerMeshShipLoad.TryLoadArea(Cell, false, default, out Rect area, out string error), Is.True);
            Assert.That(area, Is.EqualTo(Cell));
            Assert.That(error, Is.Null);
        }

        [Test]
        public void TryLoadArea_AreaSet_IsTheIntersectionWithTheCell()
        {
            Assert.That(ServerMeshShipLoad.TryLoadArea(Cell, true, Rect.MinMaxRect(250f, -30f, 400f, 30f), out Rect area, out _), Is.True);

            Assert.That(area, Is.EqualTo(Rect.MinMaxRect(250f, -30f, 300f, 30f)));
        }

        [Test]
        public void TryAiSpawnRect_NullScale_ThrowsNamingIt()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() => ServerMeshShipLoad.TryAiSpawnRect(Cell, null, out _));
            Assert.That(ex.ParamName, Is.EqualTo("scale"));
        }

        [Test]
        public void TryAiSpawnRect_OrbitWiderThanTheCell_FallsBackToTheShareInset()
        {
            bool fitted = ServerMeshShipLoad.TryAiSpawnRect(Cell, new ServerMeshShipScale(1f), out Rect spawn);

            Assert.That(fitted, Is.False);
            Assert.That(spawn.xMin, Is.EqualTo(-240f).Within(1e-3f));
            Assert.That(spawn.yMax, Is.EqualTo(240f).Within(1e-3f));
        }

        [Test]
        public void AiSpawnPoints_KeepTheirPatrolOrbitInsideTheInsetRectangle()
        {
            ServerMeshShipScale scale = new ServerMeshShipScale(0.03f);
            Rect inset = ServerMeshShipPilot.InsetRectangle(Cell, scale.RectangleInset);

            Assert.That(ServerMeshShipLoad.TryAiSpawnRect(Cell, scale, out Rect spawn), Is.True);
            for (int k = 0; k < 200; ++k)
            {
                Vector3 point = ServerMeshShipLoad.DrawPoint(new System.Random(ServerMeshShipFlightPath.MixSeed(12345 ^ ServerMeshShipLoad.AiSalt, k)), spawn, ServerMeshShipLoad.FlightHeight());
                Assert.That(spawn.Contains(new Vector2(point.x, point.z)), Is.True);
                float margin = scale.PatrolRadius + ServerMeshShipLoad.AiOrbitMarginMetres * scale.Factor;
                Assert.That(point.x - margin, Is.GreaterThanOrEqualTo(inset.xMin - 1e-3f));
                Assert.That(point.x + margin, Is.LessThanOrEqualTo(inset.xMax + 1e-3f));
                Assert.That(point.z - margin, Is.GreaterThanOrEqualTo(inset.yMin - 1e-3f));
                Assert.That(point.z + margin, Is.LessThanOrEqualTo(inset.yMax + 1e-3f));
                Assert.That(point.y, Is.EqualTo(60f));
            }
        }

        [Test]
        public void DrawPoint_NullRandom_ThrowsNamingIt()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() => ServerMeshShipLoad.DrawPoint(null, Cell, 0f));
            Assert.That(ex.ParamName, Is.EqualTo("random"));
        }

        [Test]
        public void DrawPoint_SameSeed_SamePoint()
        {
            Vector3 a = ServerMeshShipLoad.DrawPoint(new System.Random(7), Cell, 1f);
            Vector3 b = ServerMeshShipLoad.DrawPoint(new System.Random(7), Cell, 1f);

            Assert.That(a, Is.EqualTo(b));
        }

        [Test]
        public void ParkedHeight_IsLiftedAboveThePathPlane()
        {
            Assert.That(ServerMeshShipLoad.FlightHeight(), Is.EqualTo(60f));
            Assert.That(ServerMeshShipLoad.ParkedHeight(new ServerMeshShipScale(0.03f)), Is.EqualTo(80.09f).Within(1e-4f));
            Assert.That(ServerMeshShipLoad.ParkedHeight(new ServerMeshShipScale(1f)), Is.EqualTo(83f).Within(1e-4f));
        }

        private static IEnumerable<TestCaseData> BadBotArguments()
        {
            yield return new TestCaseData((string)null).SetName("ResolveBotArgument_Missing_Refused");
            yield return new TestCaseData("").SetName("ResolveBotArgument_Empty_Refused");
            yield return new TestCaseData("-meshUser").SetName("ResolveBotArgument_NextFlag_Refused");
            yield return new TestCaseData("header56:12345").SetName("ResolveBotArgument_TwoFields_Refused");
            yield return new TestCaseData("ship56:12345:1").SetName("ResolveBotArgument_UnknownWire_Refused");
            yield return new TestCaseData("header56:x:1").SetName("ResolveBotArgument_BadSeed_Refused");
            yield return new TestCaseData("header56:12345:0").SetName("ResolveBotArgument_ZeroScale_Refused");
        }

        [TestCaseSource(nameof(BadBotArguments))]
        public void ResolveBotArgument_Bad(string value)
        {
            ServerMeshShipSettings settings = ServerMeshShipLoad.ResolveBotArgument(value, out string error);

            Assert.That(settings, Is.Null);
            Assert.That(error, Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public void ResolveBotArgument_Valid_CarriesWireSeedAndScale()
        {
            ServerMeshShipSettings settings = ServerMeshShipLoad.ResolveBotArgument("header56:-77:0.03", out string error);

            Assert.That(settings, Is.Not.Null, error);
            Assert.That(settings.AnyShipKey, Is.True);
            Assert.That(settings.Wire, Is.EqualTo(ServerMeshShipWireKind.Header56));
            Assert.That(settings.Seed, Is.EqualTo(-77));
            Assert.That(settings.Scale.Factor, Is.EqualTo(0.03f));
            Assert.That(settings.Load, Is.Null);
        }

        private static IEnumerable<TestCaseData> ArgumentCases()
        {
            yield return new TestCaseData(null, null, false).SetName("ReadArgument_NoArgs_Absent");
            yield return new TestCaseData(new[] { "game.exe", "-meshAutoClient" }, null, false).SetName("ReadArgument_NotGiven_Absent");
            yield return new TestCaseData(new[] { "game.exe", "-meshRigShips" }, null, true).SetName("ReadArgument_GivenLast_NoValue");
            yield return new TestCaseData(new[] { "game.exe", "-meshRigShips", "legacy54:1:1", "-x" }, "legacy54:1:1", true).SetName("ReadArgument_Given_TheNextText");
            yield return new TestCaseData(new[] { "-MESHRIGSHIPS", "legacy54:1:1" }, null, false).SetName("ReadArgument_OtherCase_Absent");
        }

        [TestCaseSource(nameof(ArgumentCases))]
        public void ReadArgument(string[] args, string expected, bool present)
        {
            Assert.That(ServerMeshShipLoad.ReadArgument(args, ServerMeshShipSettings.BotArgument), Is.EqualTo(expected));
            Assert.That(ServerMeshShipLoad.HasArgument(args, ServerMeshShipSettings.BotArgument), Is.EqualTo(present));
        }

        [TestCase(ServerMeshShipWireKind.Legacy54, "legacy54")]
        [TestCase(ServerMeshShipWireKind.Header56, "header56")]
        [TestCase(ServerMeshShipWireKind.Compact28, "compact28")]
        public void WireName_IsTheSettingSpelling(ServerMeshShipWireKind wire, string expected)
        {
            Assert.That(ServerMeshShipLoad.WireName(wire), Is.EqualTo(expected));
        }

        [Test]
        [SetCulture("de-DE")]
        public void SettingLine_ExactLine()
        {
            string line = ServerMeshShipLoad.SettingLine("C", 50, 0, 0, 0, ServerMeshShipWireKind.Header56, new ServerMeshShipScale(0.03f), false, false, false, false,
                false, default, -12345678);

            Assert.That(line, Is.EqualTo("Ships: cell C ai 50 mirror 0 parked 0 pilots 0 | wire header56 scale 0.03 projectiles instantiate mirrorVelocity zero"
                + " selfAck 0 aiPrefab enemy | area none | prefab hash -12345678"));
            AssertPrintable(line);
        }

        [Test]
        [SetCulture("de-DE")]
        public void SettingLine_AreaPooledRealSelfAckWave_ExactLine()
        {
            string line = ServerMeshShipLoad.SettingLine("E", 0, 40, 10, 50, ServerMeshShipWireKind.Legacy54, new ServerMeshShipScale(0.5f), true, true, true, true, true,
                Rect.MinMaxRect(-30f, -30f, 30.5f, 30f), 99);

            Assert.That(line, Is.EqualTo("Ships: cell E ai 0 mirror 40 parked 10 pilots 50 | wire legacy54 scale 0.5 projectiles pooled mirrorVelocity real"
                + " selfAck 1 aiPrefab wave | area -30,-30,30.5,30 | prefab hash 99"));
        }

        private static IEnumerable<TestCaseData> ClassCases()
        {
            yield return new TestCaseData(ServerMeshShipAuthority.OwnerMirror, false).SetName("ClassFor_Mirror_BaseWhateverTheAiClass");
            yield return new TestCaseData(ServerMeshShipAuthority.Parked, false).SetName("ClassFor_Parked_BaseWhateverTheAiClass");
            yield return new TestCaseData(ServerMeshShipAuthority.ServerAI, true).SetName("ClassFor_ServerAI_TheAiClass");
        }

        [TestCaseSource(nameof(ClassCases))]
        public void ClassFor(ServerMeshShipAuthority authority, bool takesAiClass)
        {
            ServerMeshShipClass shipClass = ServerMeshShipLoad.ClassFor(authority, ServerMeshShipClass.Enemy);

            ServerMeshShipClass expected = takesAiClass ? ServerMeshShipClass.Enemy : ServerMeshShipClass.Base;
            Assert.That(shipClass.Hull, Is.EqualTo(expected.Hull));
            Assert.That(shipClass.Force, Is.EqualTo(expected.Force));
            Assert.That(shipClass.SteeringTorques, Is.EqualTo(expected.SteeringTorques));
        }

        [Test]
        public void WindowLine_NullCounters_ThrowsNamingThem()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() => ServerMeshShipLoad.WindowLine(5.0, "C", null, 0));
            Assert.That(ex.ParamName, Is.EqualTo("c"));
        }

        [Test]
        [SetCulture("de-DE")]
        public void WindowLine_Empty_IsTheZerosLine()
        {
            string line = ServerMeshShipLoad.WindowLine(double.NaN, null, new ServerMeshShipCounters(), -3);

            Assert.That(line, Is.EqualTo(ZeroLine));
            AssertPrintable(line);
        }

        [Test]
        [SetCulture("de-DE")]
        public void WindowLine_ExactLine()
        {
            ServerMeshShipCounters c = new ServerMeshShipCounters
            {
                Ships = 50, Ai = 46, Mirror = 2, Parked = 1, Phantom = 1, Pilots = 3, Seated = 2, Outside = 1, NotInSlot = 0, Pending = 3,
                Scans = 500, Casts = 7500, Shots = 300, Bolts = 600, Hits = 40, Respawns = 2, ShieldWrites = 1500, RpcSends = 2400,
                Uploads = 10, Stale = 1, Bad = 0, PilotMs = 2.0, EngineMs = 1.5, CombatMs = 1.0, BoltMs = 4.5, MirrorMs = 0,
            };
            c.Speeds.AddRange(new[] { 3.76f, 1.5f, 1.49f });
            c.PatrolSpeeds.AddRange(new[] { 1.6f, 1.504f });
            c.MirrorAges.AddRange(new[] { 40L, 16L, 26L });
            c.MirrorFrames.AddRange(new[] { 3, 1, 2 });

            string line = ServerMeshShipLoad.WindowLine(5.0, "C", c, 1);

            Assert.That(line, Is.EqualTo(FullLine));
            AssertPrintable(line);
        }

        [Test]
        [SetCulture("de-DE")]
        public void WindowLine_NoPatrolSampleButMirrorAges_PrintsNoneForPatrolOnly()
        {
            ServerMeshShipCounters c = new ServerMeshShipCounters { Ships = 2, Mirror = 2 };
            c.Speeds.Add(2.5f);
            c.MirrorAges.Add(14L);
            c.MirrorFrames.Add(1);

            string line = ServerMeshShipLoad.WindowLine(1.0, "C", c, 0);

            StringAssert.Contains(" notInSlot 0 pending 0 | speed p50 2.50 max 2.50 patrol n 0 p50 none m/s | ", line);
            StringAssert.EndsWith(" mirror 0.00 | mirror age n 1 p50 14.0 ms frames 1 | gc0 0", line);
            AssertPrintable(line);
        }

        [Test]
        public void CountersReset_EmptiesEverything()
        {
            ServerMeshShipCounters c = new ServerMeshShipCounters { Ships = 3, Hits = 4, BoltMs = 2.0, Pending = 2 };
            c.Speeds.Add(1f);
            c.PatrolSpeeds.Add(1f);
            c.MirrorAges.Add(30L);
            c.MirrorFrames.Add(2);


            c.Reset();

            Assert.That(ServerMeshShipLoad.WindowLine(0, null, c, 0), Is.EqualTo(ZeroLine));
        }
    }
}
