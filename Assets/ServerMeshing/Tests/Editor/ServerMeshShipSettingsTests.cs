using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class ServerMeshShipSettingsTests
    {
        private static Func<string, string> Env(params string[] pairs)
        {
            Dictionary<string, string> values = new Dictionary<string, string>();
            for (int i = 0; i + 1 < pairs.Length; i += 2)
                values[pairs[i]] = pairs[i + 1];
            return key => values.TryGetValue(key, out string value) ? value : null;
        }

        private static void AssertClean(string error)
        {
            Assert.That(error, Does.Not.Contain("{").And.Not.Contain("}").And.Not.Contain("Exception").And.Not.Contain("\n"));
        }

        [Test]
        public void TryResolve_NullReader_ThrowsNamingIt()
        {
            ArgumentNullException thrown = Assert.Throws<ArgumentNullException>(() => ServerMeshShipSettings.TryResolve(null, out _));

            Assert.That(thrown.ParamName, Is.EqualTo("read"));
        }

        private static IEnumerable<TestCaseData> JunkCases()
        {
            yield return new TestCaseData(ServerMeshShipSettings.LoadKey, "C:0:ai").SetName("TryResolve_JunkLoad_FailsNamingTheKey");
            yield return new TestCaseData(ServerMeshShipSettings.PilotsKey, "2").SetName("TryResolve_JunkPilots_FailsNamingTheKey");
            yield return new TestCaseData(ServerMeshShipSettings.WireKey, "ship99").SetName("TryResolve_JunkWire_FailsNamingTheKey");
            yield return new TestCaseData(ServerMeshShipSettings.ScaleKey, "0").SetName("TryResolve_JunkScale_FailsNamingTheKey");
            yield return new TestCaseData(ServerMeshShipSettings.ProjectilesKey, "laser").SetName("TryResolve_JunkProjectiles_FailsNamingTheKey");
            yield return new TestCaseData(ServerMeshShipSettings.MirrorVelocityKey, "fast").SetName("TryResolve_JunkMirrorVelocity_FailsNamingTheKey");
            yield return new TestCaseData(ServerMeshShipSettings.RigShipsKey, "yes").SetName("TryResolve_JunkRigShips_FailsNamingTheKey");
            yield return new TestCaseData(ServerMeshShipSettings.SelfAckKey, "2").SetName("TryResolve_JunkSelfAck_FailsNamingTheKey");
            yield return new TestCaseData(ServerMeshShipSettings.SelfAckKey, "on").SetName("TryResolve_WordSelfAck_FailsNamingTheKey");
            yield return new TestCaseData(ServerMeshShipSettings.AiPrefabKey, "base").SetName("TryResolve_JunkAiPrefab_FailsNamingTheKey");
            yield return new TestCaseData(ServerMeshShipSettings.AiPrefabKey, "Ship_Enemy").SetName("TryResolve_PrefabNameAiPrefab_FailsNamingTheKey");
            yield return new TestCaseData(ServerMeshShipSettings.SeedKey, "abc").SetName("TryResolve_JunkSeedWithAShipKey_FailsNamingTheKey");
            yield return new TestCaseData(ServerMeshShipSettings.AreaKey, "1,2").SetName("TryResolve_JunkAreaWithAShipKey_FailsNamingTheKey");
            yield return new TestCaseData(ServerMeshShipSettings.AreaKey, "0,0,-5").SetName("TryResolve_NegativeAreaWithAShipKey_FailsNamingTheKey");
        }

        [TestCaseSource(nameof(JunkCases))]
        public void TryResolve_JunkValue_GivesNullAndAnErrorNamingTheKey(string key, string value)
        {
            string trigger = key == ServerMeshShipSettings.WireKey ? ServerMeshShipSettings.ScaleKey : ServerMeshShipSettings.WireKey;
            string triggerValue = trigger == ServerMeshShipSettings.WireKey ? "header56" : "0.5";

            ServerMeshShipSettings settings = ServerMeshShipSettings.TryResolve(Env(trigger, triggerValue, key, value), out string error);

            Assert.That(settings, Is.Null);
            Assert.That(error, Does.Contain(key));
            AssertClean(error);
        }

        [TestCase(ServerMeshShipSettings.SeedKey, "abc")]
        [TestCase(ServerMeshShipSettings.AreaKey, "nonsense")]
        public void TryResolve_JunkSeedOrAreaWithoutAShipKey_ResolvesWithNoError(string key, string value)
        {
            ServerMeshShipSettings settings = ServerMeshShipSettings.TryResolve(Env(key, value, "MESH_PHANTOM", "virtual:4:character:C"), out string error);

            Assert.That(error, Is.Null);
            Assert.That(settings.AnyShipKey, Is.False);
            Assert.That(settings.Seed, Is.EqualTo(ServerMeshShipSettings.DefaultSeed));
            Assert.That(settings.HasArea, Is.False);
        }

        [Test]
        public void TryResolve_NothingSet_IsTheDefault()
        {
            ServerMeshShipSettings settings = ServerMeshShipSettings.TryResolve(Env(), out string error);

            Assert.That(error, Is.Null);
            Assert.That(settings.AnyShipKey, Is.False);
            Assert.That(settings.Load, Is.Null);
            Assert.That(settings.Pilots, Is.False);
            Assert.That(settings.Wire, Is.EqualTo(ServerMeshShipWireKind.Legacy54));
            Assert.That(settings.Scale.Factor, Is.EqualTo(1f));
            Assert.That(settings.PooledProjectiles, Is.False);
            Assert.That(settings.RealMirrorVelocity, Is.True);
            Assert.That(settings.RigShips, Is.False);
            Assert.That(settings.SelfAck, Is.False);
            Assert.That(settings.WaveAi, Is.False);
            Assert.That(settings.AiClass.Name, Is.EqualTo(ServerMeshShipClass.Enemy.Name));
            Assert.That(settings.Seed, Is.EqualTo(12345));
            Assert.That(settings.HasArea, Is.False);
        }

        [Test]
        public void TryResolve_AShipKeyAlone_KeepsTheStep7bDefaults()
        {
            ServerMeshShipSettings settings = ServerMeshShipSettings.TryResolve(Env(ServerMeshShipSettings.WireKey, "header56"), out string error);

            Assert.That(error, Is.Null);
            Assert.That(settings.AnyShipKey, Is.True);
            Assert.That(settings.RealMirrorVelocity, Is.True);
            Assert.That(settings.SelfAck, Is.False);
            Assert.That(settings.WaveAi, Is.False);
            Assert.That(settings.AiClass.Name, Is.EqualTo(ServerMeshShipClass.Enemy.Name));
        }

        private static IEnumerable<TestCaseData> SwitchCases()
        {
            yield return new TestCaseData(ServerMeshShipSettings.MirrorVelocityKey, "ZERO", false, false, false).SetName("TryResolve_MirrorVelocityZero_IsNotReal");
            yield return new TestCaseData(ServerMeshShipSettings.MirrorVelocityKey, "real", true, false, false).SetName("TryResolve_MirrorVelocityReal_IsReal");
            yield return new TestCaseData(ServerMeshShipSettings.SelfAckKey, "1", true, true, false).SetName("TryResolve_SelfAck1_SelfAcks");
            yield return new TestCaseData(ServerMeshShipSettings.SelfAckKey, " 0 ", true, false, false).SetName("TryResolve_SelfAck0_DoesNot");
            yield return new TestCaseData(ServerMeshShipSettings.AiPrefabKey, "Wave", true, false, true).SetName("TryResolve_AiPrefabWaveAnyCase_IsWave");
            yield return new TestCaseData(ServerMeshShipSettings.AiPrefabKey, "ENEMY", true, false, false).SetName("TryResolve_AiPrefabEnemyAnyCase_IsEnemy");
        }

        [TestCaseSource(nameof(SwitchCases))]
        public void TryResolve_Switches(string key, string value, bool real, bool selfAck, bool wave)
        {
            ServerMeshShipSettings settings = ServerMeshShipSettings.TryResolve(Env(key, value), out string error);

            Assert.That(error, Is.Null);
            Assert.That(settings.RealMirrorVelocity, Is.EqualTo(real));
            Assert.That(settings.SelfAck, Is.EqualTo(selfAck));
            Assert.That(settings.WaveAi, Is.EqualTo(wave));
            Assert.That(settings.AiClass.Name, Is.EqualTo(wave ? ServerMeshShipClass.Base.Name : ServerMeshShipClass.Enemy.Name));
        }

        [Test]
        [SetCulture("de-DE")]
        public void TryResolve_EveryKeySet_ReadsEach()
        {
            ServerMeshShipSettings settings = ServerMeshShipSettings.TryResolve(Env(
                ServerMeshShipSettings.LoadKey, "C:40:mirror,C:10:parked",
                ServerMeshShipSettings.PilotsKey, "1",
                ServerMeshShipSettings.WireKey, "Compact28",
                ServerMeshShipSettings.ScaleKey, "0.03",
                ServerMeshShipSettings.ProjectilesKey, "pooled",
                ServerMeshShipSettings.MirrorVelocityKey, "REAL",
                ServerMeshShipSettings.RigShipsKey, "1",
                ServerMeshShipSettings.SeedKey, "-7",
                ServerMeshShipSettings.AreaKey, "10.5, -4, 30"), out string error);

            Assert.That(error, Is.Null);
            Assert.That(settings.AnyShipKey, Is.True);
            Assert.That(settings.Load.CountFor("C"), Is.EqualTo(50));
            Assert.That(settings.Pilots, Is.True);
            Assert.That(settings.Wire, Is.EqualTo(ServerMeshShipWireKind.Compact28));
            Assert.That(settings.Scale.Factor, Is.EqualTo(0.03f));
            Assert.That(settings.PooledProjectiles, Is.True);
            Assert.That(settings.RealMirrorVelocity, Is.True);
            Assert.That(settings.RigShips, Is.True);
            Assert.That(settings.Seed, Is.EqualTo(-7));
            Assert.That(settings.HasArea, Is.True);
            Assert.That(settings.Area.xMin, Is.EqualTo(-19.5f));
            Assert.That(settings.Area.yMin, Is.EqualTo(-34f));
            Assert.That(settings.Area.width, Is.EqualTo(60f));
            Assert.That(settings.Area.height, Is.EqualTo(60f));
        }

        private static IEnumerable<TestCaseData> AnyShipKeyCases()
        {
            yield return new TestCaseData("MESH_PHANTOM", "virtual:4:ship:C", true).SetName("AnyShipKey_PhantomKindShip_IsTrue");
            yield return new TestCaseData("MESH_PHANTOM", "sink:20:SHIP:C", true).SetName("AnyShipKey_PhantomKindShipInCapitals_IsTrue");
            yield return new TestCaseData("MESH_PUPPET_FEEDER", "ship:10@20", true).SetName("AnyShipKey_FeederKindShip_IsTrue");
            yield return new TestCaseData("MESH_PHANTOM", "virtual:4:character:C", false).SetName("AnyShipKey_PhantomKindCharacter_IsFalse");
            yield return new TestCaseData("MESH_PUPPET_FEEDER", "character:10@20", false).SetName("AnyShipKey_FeederKindCharacter_IsFalse");
            yield return new TestCaseData(ServerMeshShipSettings.ScaleKey, "1", true).SetName("AnyShipKey_ScaleAlone_IsTrue");
            yield return new TestCaseData(ServerMeshShipSettings.RigShipsKey, "1", true).SetName("AnyShipKey_RigShipsAlone_IsTrue");
            yield return new TestCaseData(ServerMeshShipSettings.SelfAckKey, "0", true).SetName("AnyShipKey_SelfAckAlone_IsTrue");
            yield return new TestCaseData(ServerMeshShipSettings.AiPrefabKey, "enemy", true).SetName("AnyShipKey_AiPrefabAlone_IsTrue");
            yield return new TestCaseData(ServerMeshShipSettings.LoadKey, "   ", false).SetName("AnyShipKey_BlankLoad_IsFalse");
        }

        [TestCaseSource(nameof(AnyShipKeyCases))]
        public void AnyShipKey_FromOneKey(string key, string value, bool expected)
        {
            ServerMeshShipSettings settings = ServerMeshShipSettings.TryResolve(Env(key, value), out string error);

            Assert.That(error, Is.Null);
            Assert.That(settings.AnyShipKey, Is.EqualTo(expected));
        }

        private static IEnumerable<TestCaseData> WireCases()
        {
            yield return new TestCaseData(null, true, ServerMeshShipWireKind.Legacy54).SetName("TryParseWire_Null_IsLegacy54");
            yield return new TestCaseData("", true, ServerMeshShipWireKind.Legacy54).SetName("TryParseWire_Empty_IsLegacy54");
            yield return new TestCaseData(" LEGACY54 ", true, ServerMeshShipWireKind.Legacy54).SetName("TryParseWire_Legacy54AnyCase_Parses");
            yield return new TestCaseData("header56", true, ServerMeshShipWireKind.Header56).SetName("TryParseWire_Header56_Parses");
            yield return new TestCaseData("compact28", true, ServerMeshShipWireKind.Compact28).SetName("TryParseWire_Compact28_Parses");
            yield return new TestCaseData("ship56", false, ServerMeshShipWireKind.Legacy54).SetName("TryParseWire_Ship56_Fails");
            yield return new TestCaseData("56", false, ServerMeshShipWireKind.Legacy54).SetName("TryParseWire_Number_Fails");
        }

        [TestCaseSource(nameof(WireCases))]
        public void TryParseWire_Rows(string text, bool valid, ServerMeshShipWireKind expected)
        {
            bool parsed = ServerMeshShipSettings.TryParseWire(text, out ServerMeshShipWireKind wire, out string error);

            Assert.That(parsed, Is.EqualTo(valid));
            Assert.That(wire, Is.EqualTo(expected));
            if (valid)
                Assert.That(error, Is.Null);
            else
            {
                Assert.That(error, Does.StartWith(ServerMeshShipSettings.WireKey + "="));
                AssertClean(error);
            }
        }

        [TestCase(ServerMeshShipWireKind.Legacy54, 12345, "1", "legacy54:12345:1")]
        [TestCase(ServerMeshShipWireKind.Header56, 12345, "0.03", "header56:12345:0.03")]
        [TestCase(ServerMeshShipWireKind.Compact28, -9, "0.0315", "compact28:-9:0.0315")]
        [TestCase(ServerMeshShipWireKind.Header56, 7, " ", "header56:7:1")]
        public void BotArgument_RoundTripsKeepingTheScaleText(ServerMeshShipWireKind wire, int seed, string scaleText, string expected)
        {
            string value = ServerMeshShipSettings.BotArgumentValue(wire, seed, scaleText);

            bool parsed = ServerMeshShipSettings.TryParseBotArgument(value, out ServerMeshShipWireKind readWire, out int readSeed, out ServerMeshShipScale scale, out string error);

            Assert.That(value, Is.EqualTo(expected));
            Assert.That(parsed, Is.True, error);
            Assert.That(readWire, Is.EqualTo(wire));
            Assert.That(readSeed, Is.EqualTo(seed));
            Assert.That(scale.Factor, Is.EqualTo(float.Parse(value.Substring(value.LastIndexOf(':') + 1), System.Globalization.CultureInfo.InvariantCulture)));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("header56:12345")]
        [TestCase("header56:12345:0.03:9")]
        [TestCase("ship56:12345:0.03")]
        [TestCase("header56:abc:0.03")]
        [TestCase("header56:12345:0")]
        [TestCase("header56::0.03")]
        [TestCase("header56:{1}:0.03")]
        public void TryParseBotArgument_Junk_FailsNamingTheArgument(string text)
        {
            bool parsed = ServerMeshShipSettings.TryParseBotArgument(text, out ServerMeshShipWireKind wire, out int seed, out ServerMeshShipScale scale, out string error);

            Assert.That(parsed, Is.False);
            Assert.That(wire, Is.EqualTo(ServerMeshShipWireKind.Legacy54));
            Assert.That(seed, Is.EqualTo(ServerMeshShipSettings.DefaultSeed));
            Assert.That(scale.Factor, Is.EqualTo(1f));
            Assert.That(error, Does.StartWith(ServerMeshShipSettings.BotArgument));
            AssertClean(error);
        }

        [TestCase(ServerMeshShipWireKind.Legacy54, "legacy54")]
        [TestCase(ServerMeshShipWireKind.Header56, "header56")]
        [TestCase(ServerMeshShipWireKind.Compact28, "compact28")]
        public void WireName_IsTheSettingWord(ServerMeshShipWireKind wire, string expected)
        {
            Assert.That(ServerMeshShipSettings.WireName(wire), Is.EqualTo(expected));
        }
    }
}
