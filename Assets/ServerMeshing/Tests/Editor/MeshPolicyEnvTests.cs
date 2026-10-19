using System.Collections.Generic;
using LiteNetLib.Utils;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>The policy's rig toggles, the host report and the switchable burn (Slice 3 decisions 8 and 15).</summary>
    public class MeshPolicyEnvTests
    {
        private static System.Func<string, string> Env(Dictionary<string, string> values) => key => values.TryGetValue(key, out string v) ? v : null;

        [Test]
        public void Apply_SetsTheSettingsAndTheDynamicMapsFlags_AndReportsWhatItTook()
        {
            MeshWorldRegistry world = MeshFakeWorlds.RigLike(map1Dynamic: true);
            try
            {
                Dictionary<string, string> env = new Dictionary<string, string>
                {
                    { "MESH_AUTO_OPS", "split,merge" }, { "MESH_SPLIT_UTIL", "0.6" }, { "MESH_SPLIT_DWELL", "20" }, { "MESH_MERGE_UTIL", "0.5" },
                    { "MESH_MERGE_AFTER", "30" }, { "MESH_OP_COOLDOWN", "10" }, { "MESH_SPLIT_BENEFIT", "1" }, { "MESH_MONSTER_WEIGHT", "0" },
                };
                List<string> applied = new List<string>(), warnings = new List<string>();
                Assert.That(MeshPolicyEnv.AnySet(Env(env)), Is.True);
                MeshPolicyEnv.Apply(world.dynamicSettings, world.maps, Env(env), applied, warnings);
                Assert.That(warnings, Is.Empty);
                Assert.That(applied, Is.EqualTo(new[]
                {
                    "MESH_AUTO_OPS=split,merge (1 Dynamic map(s))", "MESH_SPLIT_UTIL=0.6", "MESH_SPLIT_DWELL=20", "MESH_MERGE_UTIL=0.5", "MESH_MERGE_AFTER=30",
                    "MESH_OP_COOLDOWN=10", "MESH_SPLIT_BENEFIT=1", "MESH_MONSTER_WEIGHT=0",
                }));
                MeshDynamicSettings s = world.dynamicSettings;
                Assert.That((s.splitUtil, s.splitDwellSeconds, s.mergeUtil, s.mergeDwellSeconds, s.opCooldownSeconds, s.splitBenefitRatio), Is.EqualTo((0.6f, 20f, 0.5f, 30f, 10f, 1f)));
                Assert.That((s.ownedMonsterWeight, s.puppetMonsterWeight), Is.EqualTo((0f, 0f)));
                Assert.That((world.maps[0].autoSplit, world.maps[0].autoMerge, world.maps[1].autoSplit), Is.EqualTo((true, true, false)), "only the Dynamic map");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(world);
            }
        }

        [Test]
        public void BadValues_AreWarnedAndIgnored_AndTheWeightsOverrideReadsFour()
        {
            MeshDynamicSettings s = new MeshDynamicSettings();
            List<string> applied = new List<string>(), warnings = new List<string>();
            MeshPolicyEnv.Apply(s, new List<MeshMapLayout>(), Env(new Dictionary<string, string> { { "MESH_AUTO_OPS", "both" }, { "MESH_SPLIT_UTIL", "hot" }, { "MESH_PLANNER_WEIGHTS", "1,2" } }), applied, warnings);
            Assert.That(applied, Is.Empty);
            Assert.That(warnings, Is.EqualTo(new[]
            {
                "MESH_AUTO_OPS=both is not split, merge or split,merge; ignored", "MESH_SPLIT_UTIL=hot is not a non-negative number; ignored",
                "MESH_PLANNER_WEIGHTS=1,2 is not <puppetPlayer>,<ownedMonster>,<puppetMonster>,<floor>; ignored",
            }));
            Assert.That(s.splitUtil, Is.EqualTo(0.8f));
            MeshPolicyEnv.Apply(s, new List<MeshMapLayout>(), Env(new Dictionary<string, string> { { "MESH_PLANNER_WEIGHTS", "0.5,0.2,0.3,4" } }), applied, warnings);
            Assert.That((s.puppetPlayerWeight, s.ownedMonsterWeight, s.puppetMonsterWeight, s.modelFloorPlayers), Is.EqualTo((0.5f, 0.2f, 0.3f, 4f)));
            Assert.That(MeshPolicyEnv.TryParseAutoOps(" Merge ", out bool split, out bool merge) && !split && merge, Is.True);
            Assert.That(MeshPolicyEnv.TryParseAutoOps("", out _, out _), Is.False);
            Assert.That(MeshPolicyEnv.AnySet(Env(new Dictionary<string, string>())), Is.False);
        }

        [Test]
        public void HostReport_RoundTrips_AndCoresReadTheOverride()
        {
            MeshHostReportMessage report = new MeshHostReportMessage { hostName = "BOX", cores = 16, coresInUse = 2.5f };
            NetDataWriter writer = new NetDataWriter();
            report.Serialize(writer);
            Assert.That(MeshWholeMessage.TryRead(new NetDataReader(writer.CopyData()), out MeshHostReportMessage copy, out string error), Is.True, error);
            Assert.That((copy.hostName, copy.cores, copy.coresInUse), Is.EqualTo(("BOX", 16, 2.5f)));
            MeshHostCpuSampler sampler = new MeshHostCpuSampler();
            Assert.That(sampler.Sample(100d, 1000d), Is.EqualTo(0f), "no interval yet");
            Assert.That(sampler.Sample(125d, 1010d), Is.EqualTo(2.5f).Within(1e-6f), "25 processor seconds over 10 s");
            Assert.That(sampler.Sample(120d, 1020d), Is.EqualTo(0f), "a sum that fell (processes exited) reads as idle, never negative");
            Assert.That(MeshHostReportMessage.Cores("4", 16), Is.EqualTo(4));
            Assert.That(MeshHostReportMessage.Cores("many", 16), Is.EqualTo(16));
            Assert.That(MeshHostReportMessage.Cores(null, 0), Is.EqualTo(1));
        }

        [TestCase("14", 14f, null, true)]
        [TestCase("C:14", 14f, "C", true)]
        [TestCase(" c.1 : 2.5 ", 2.5f, "c.1", true)]
        [TestCase(":8", 8f, null, true)]
        [TestCase("C:", 0f, null, false)]
        [TestCase("0", 0f, null, false)]
        [TestCase("", 0f, null, false)]
        public void TryParseBurn_ReadsCellAndMilliseconds(string text, float ms, string cell, bool ok)
        {
            Assert.That(MeshMapNetworkManager.TryParseBurn(text, out float parsedMs, out string parsedCell), Is.EqualTo(ok));
            if (ok)
                Assert.That((parsedMs, parsedCell), Is.EqualTo((ms, cell)));
        }
    }
}
