using System.Collections.Generic;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>The split planner's cuts, model and gate, and the union prediction (Slice 3 decisions 1-4).</summary>
    public class MeshSplitPlannerTests
    {
        private static readonly MeshRect C = new MeshRect(-100f, -100f, 100f, 100f);
        private readonly MeshPlannerWeights _w = new MeshPlannerWeights();

        private static List<(float, float)> Both(float x, int n)
        {
            List<(float, float)> points = MeshFakeReports.Spread(-x, n);
            points.AddRange(MeshFakeReports.Spread(x, n));
            return points;
        }

        [Test]
        public void TwoGroupsBeyondTheBand_CutBetweenThem_PeakIsHalfTheLoadPlusTheFloor()
        {
            MeshLoadReportMessage report = MeshFakeReports.Report("C", C, 0.9f, Both(88f, 4));

            bool ok = MeshSplitPlanner.TryPlan(report, C, 100f, 80f, _w, 1f, out MeshSplitPlan plan, out string refusal);

            Assert.That(ok, Is.True, refusal);
            Assert.That((plan.axis, plan.at, plan.childOnPositiveSide), Is.EqualTo((MeshSplitAxis.X, 0f, true)), "x 0, ties go to the positive side");
            Assert.That(plan.candidates, Is.EqualTo(2), "x 0 and z 0 are the only cuts leaving 100 m sides");
            float expected = 0.9f * (9.1f + 4f) / (9.1f + 8f);
            Assert.That(plan.peak, Is.EqualTo(expected).Within(1e-4f));
            Assert.That((plan.predictedParent, plan.predictedChild), Is.EqualTo((expected, expected)));
            Assert.That((plan.ownedPlayersKept, plan.ownedPlayersGiven), Is.EqualTo((4, 4)));
        }

        [Test]
        public void TheGate_RefusesAPeakOverTheRatio_AndNamesIt()
        {
            MeshLoadReportMessage report = MeshFakeReports.Report("C", C, 0.9f, Both(88f, 4));

            bool ok = MeshSplitPlanner.TryPlan(report, C, 100f, 80f, _w, 0.75f, out MeshSplitPlan plan, out string refusal);

            Assert.That(ok, Is.False);
            Assert.That(refusal, Is.EqualTo("best peak 0.69 over 0.75 x util 0.9"));
            Assert.That(plan, Is.Not.Null, "the best cut is still described");
        }

        [Test]
        public void ACutThroughACrowd_CountsTheStripAsPuppets_SoTheOtherAxisWins()
        {
            // Four players at x = -30 (south) and four at x = 30 (north), all within the band of x = 0: the x cut puppets
            // every one of them on both sides, the z cut separates two groups of four with only three in each strip.
            List<(float, float)> players = new List<(float, float)>();
            for (int i = 0; i < 4; ++i)
            {
                players.Add((-30f, -90f + 20f * i));   // z -90..-30: south
                players.Add((30f, 30f + 20f * i));     // z 30..90: north
            }
            MeshLoadReportMessage report = MeshFakeReports.Report("C", C, 0.9f, players);

            Assert.That(MeshSplitPlanner.TryPlan(report, C, 100f, 80f, _w, 1f, out MeshSplitPlan plan, out string refusal), Is.True, refusal);
            Assert.That(plan.axis, Is.EqualTo(MeshSplitAxis.Z));
            float xPeak = 0.9f * (9.1f + 4f + 4f * 0.174f) / (9.1f + 8f);
            Assert.That(plan.peak, Is.LessThan(xPeak));
        }

        [Test]
        public void TheChild_TakesTheSideWithFewerPlayers()
        {
            // Six west, two east, all north of z = 0: the z cut would give one side every player, the x cut 6 against 2.
            List<(float, float)> players = new List<(float, float)>();
            for (int i = 0; i < 6; ++i)
                players.Add((-88f, 10f + 10f * i));
            players.Add((88f, 10f));
            players.Add((88f, 30f));
            MeshLoadReportMessage report = MeshFakeReports.Report("C", C, 0.9f, players);

            Assert.That(MeshSplitPlanner.TryPlan(report, C, 100f, 80f, _w, 1f, out MeshSplitPlan plan, out string refusal), Is.True, refusal);
            Assert.That((plan.axis, plan.childOnPositiveSide, plan.ownedPlayersKept, plan.ownedPlayersGiven), Is.EqualTo((MeshSplitAxis.X, true, 6, 2)));
            Assert.That(plan.predictedParent, Is.GreaterThan(plan.predictedChild));
        }

        [Test]
        public void ExternalPuppets_CountForTheSideTheyAreNear_WithTheBlendedWeight()
        {
            // Twelve puppets just east of C (a neighbour's crowd): the east side carries them, so the cut gives the west side more.
            List<(float, float)> puppets = MeshFakeReports.Spread(110f, 12);
            MeshLoadReportMessage report = MeshFakeReports.Report("C", C, 0.9f, Both(88f, 4), puppets);
            report.puppetMonsters = 12;   // half the puppets are monsters: the blend is (0.174 + 0.1) / 2
            float blend = (0.174f * 12 + 0.1f * 12) / 24f;
            Assert.That(_w.ExternalPuppetWeight(report), Is.EqualTo(blend).Within(1e-6f));

            Assert.That(MeshSplitPlanner.TryPlan(report, C, 100f, 80f, _w, 1f, out MeshSplitPlan plan, out string refusal), Is.True, refusal);
            // The x cut gives the east side all twelve and the west none; the z cut's rings reach 80 m over the line, so
            // each of its sides sees eleven of them plus a strip of four players: the x cut wins.
            Assert.That((plan.axis, plan.childOnPositiveSide), Is.EqualTo((MeshSplitAxis.X, true)), "ties on players: the positive side");
            float loadNow = 9.1f + 8f + 0.174f * 12 + 0.1f * 12;
            float east = 0.9f * (9.1f + 4f + blend * 12) / loadNow, west = 0.9f * (9.1f + 4f) / loadNow;
            Assert.That(plan.predictedChild, Is.EqualTo(east).Within(1e-4f));
            Assert.That(plan.predictedParent, Is.EqualTo(west).Within(1e-4f));
            float zSide = 0.9f * (9.1f + 4f + blend * 11 + 0.174f * 4) / loadNow;
            Assert.That(plan.peak, Is.LessThan(zSide));
        }

        [Test]
        public void MinCellSize_LeavesNoCut_WhenTheCellIsUnderTwiceIt()
        {
            MeshLoadReportMessage report = MeshFakeReports.Report("C", C, 0.9f, Both(88f, 4));

            Assert.That(MeshSplitPlanner.TryPlan(report, C, 150f, 80f, _w, 1f, out _, out string refusal), Is.False);
            Assert.That(refusal, Is.EqualTo("no cut leaves both sides at least 150 m"));
        }

        [Test]
        public void CandidatesLieOnBucketMultiples_AndTiesGoToTheMiddle()
        {
            MeshRect coarse = new MeshRect(-300f, -300f, 300f, 300f);
            MeshLoadReportMessage report = MeshFakeReports.Report("C", coarse, 0.6f, Both(250f, 10));

            Assert.That(MeshSplitPlanner.TryPlan(report, coarse, 100f, 80f, _w, 1f, out MeshSplitPlan plan, out string refusal), Is.True, refusal);
            Assert.That(plan.candidates, Is.EqualTo(42), "21 lines per axis at 20 m from -200 to 200");
            Assert.That((plan.axis, plan.at), Is.EqualTo((MeshSplitAxis.X, 0f)));
        }

        [TestCase(MeshProbeState.Invalid, "probe Invalid")]
        [TestCase(MeshProbeState.Unknown, "probe Unknown")]
        [TestCase(MeshProbeState.Uncapped, "probe Uncapped")]
        public void AnUnusableProbe_IsRefused(MeshProbeState state, string why)
        {
            MeshLoadReportMessage report = MeshFakeReports.Report("C", C, 0.9f, Both(88f, 4));
            report.work.state = state;
            Assert.That(MeshSplitPlanner.TryPlan(report, C, 100f, 80f, _w, 1f, out _, out string refusal), Is.False);
            Assert.That(refusal, Is.EqualTo(why));
        }

        [Test]
        public void UnionPrediction_DropsThePuppetsInsideTheUnion_AndUsesTheLargerScale()
        {
            MeshRect west = new MeshRect(-100f, -100f, 0f, 100f), east = new MeshRect(0f, -100f, 100f, 100f);
            // West: 4 players, 3 puppets that are east's players (inside the union), 2 puppets from a northern neighbour.
            MeshLoadReportMessage a = MeshFakeReports.Report("C", west, 0.4f, MeshFakeReports.Spread(-50f, 4), new List<(float, float)> { (20f, 0f), (20f, 10f), (20f, 20f), (-50f, 110f), (-40f, 110f) });
            MeshLoadReportMessage b = MeshFakeReports.Report("C.1", east, 0.2f, MeshFakeReports.Spread(20f, 3), new List<(float, float)> { (-50f, 0f) });

            Assert.That(MeshSplitPlanner.TryPredictUnion(a, west, b, east, new MeshRect(-100f, -100f, 100f, 100f), 80f, _w, out float predicted, out string why), Is.True, why);
            float loadA = 9.1f + 4f + 5 * 0.174f, loadB = 9.1f + 3f + 0.174f;
            float scale = System.Math.Max(0.4f / loadA, 0.2f / loadB);
            float union = 9.1f + 4f + 3f + 2 * 0.174f;
            Assert.That(predicted, Is.EqualTo(scale * union).Within(1e-4f));
        }

        [Test]
        public void Weights_FromSettings_AndTheirError()
        {
            MeshDynamicSettings settings = new MeshDynamicSettings { modelFloorPlayers = 5f, puppetPlayerWeight = 0.5f, ownedMonsterWeight = 0.2f, puppetMonsterWeight = 0.3f };
            MeshPlannerWeights w = MeshPlannerWeights.From(settings);
            Assert.That((w.floorPlayers, w.puppetPlayer, w.ownedMonster, w.puppetMonster), Is.EqualTo((5f, 0.5f, 0.2f, 0.3f)));
            Assert.That(w.Error(), Is.Null);
            settings.modelFloorPlayers = 0f;
            Assert.That(MeshPlannerWeights.From(settings).Error(), Is.EqualTo("modelFloorPlayers must be positive"));
            Assert.That(MeshLayoutValidator.PolicySettingsError(settings), Does.EndWith("modelFloorPlayers must be positive"));
            Assert.That(MeshLayoutValidator.PolicySettingsError(new MeshDynamicSettings()), Is.Null);
            Assert.That(MeshLayoutValidator.PolicySettingsError(new MeshDynamicSettings { splitUtil = 1.5f }), Is.EqualTo("dynamicSettings.splitUtil 1.5 is not in (0, 1]"));
            Assert.That(MeshLayoutValidator.PolicySettingsError(new MeshDynamicSettings { fastPathUtil = 0.5f }), Is.EqualTo("dynamicSettings.fastPathUtil 0.5 is under splitUtil 0.8"));
            Assert.That(new MeshDynamicSettings { splitUtil = 0.6f, mergeUtil = 0.5f }.MergeThreshold, Is.EqualTo(0.36f).Within(1e-6f));
            Assert.That(new MeshDynamicSettings { splitUtil = 0.25f, mergeUtil = 0.5f }.MergeThreshold, Is.EqualTo(0.15f).Within(1e-6f), "mergeUtil over splitUtil is bounded, not an error");
        }
    }
}
