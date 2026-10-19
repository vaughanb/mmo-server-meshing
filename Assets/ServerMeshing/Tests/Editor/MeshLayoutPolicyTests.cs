using System.Collections.Generic;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>The pure policy: dwell, fast path, cooldown, once per report, refusals, headroom, merge pairs, measurement and flaps (Slice 3 decisions 5-10, 12).</summary>
    public class MeshLayoutPolicyTests
    {
        private static readonly MeshRect C = new MeshRect(-100f, -100f, 100f, 100f);
        private static readonly MeshRect West = new MeshRect(-100f, -100f, 0f, 100f);
        private static readonly MeshRect East = new MeshRect(0f, -100f, 100f, 100f);
        private MeshDynamicSettings _s;
        private MeshLayoutPolicy _p;
        private readonly List<MeshPolicyDecision> _d = new List<MeshPolicyDecision>();

        [SetUp]
        public void SetUp()
        {
            _s = new MeshDynamicSettings { splitUtil = 0.8f, splitDwellSeconds = 10f, fastPathSeconds = 3f, mergeUtil = 0.5f, mergeDwellSeconds = 20f, opCooldownSeconds = 30f, measureAfterSeconds = 5f, reportFreshSeconds = 15f, splitBenefitRatio = 1f, flapWindowSeconds = 100f };
            _p = new MeshLayoutPolicy(_s, 80f);
        }

        private static List<(float, float)> Both(float x, int n)
        {
            List<(float, float)> points = MeshFakeReports.Spread(-x, n);
            points.AddRange(MeshFakeReports.Spread(x, n));
            return points;
        }

        private static MeshPolicyCell Cell(string id, MeshRect owned, float util, float reportAt, IEnumerable<(float, float)> players = null, bool autoSplit = true, bool autoMerge = true, float sat = 0f, string baseId = "C")
        {
            return new MeshPolicyCell
            {
                id = id, baseId = baseId, mapId = MeshFakeWorlds.Map1, owned = owned, interest = owned, autoSplit = autoSplit, autoMerge = autoMerge, minCellSize = 100f,
                report = MeshFakeReports.Report(id, owned, util, players ?? Both(88f, 4), saturatedShare: sat), reportAt = reportAt,
            };
        }

        private static readonly MeshPolicyHost Roomy = new MeshPolicyHost { name = "H", cores = 64, inUse = 0f };

        private List<MeshPolicyDecision> Tick(float now, params MeshPolicyCell[] cells)
        {
            _d.Clear();
            _p.Evaluate(now, cells, false, _ => Roomy, _d);
            return new List<MeshPolicyDecision>(_d);
        }

        private static List<string> Lines(List<MeshPolicyDecision> decisions) => decisions.ConvertAll(d => d.kind + (d.line != null ? " " + d.line : ""));

        [Test]
        public void AHotCell_IsPlannedAfterTheDwell_OncePerReport()
        {
            MeshPolicyCell hot = Cell("C", C, 0.9f, 100f);
            Assert.That(Tick(100f, hot), Is.Empty);
            Assert.That(Tick(109f, hot), Is.Empty, "under the dwell");
            List<MeshPolicyDecision> at = Tick(110f, hot);
            Assert.That(at.Count, Is.EqualTo(1));
            Assert.That((at[0].kind, at[0].cellId, at[0].plan.axis, at[0].plan.at, at[0].plan.childOnPositiveSide), Is.EqualTo((MeshPolicyDecisionKind.RequestSplit, "C", MeshSplitAxis.X, 0f, true)));
            Assert.That(at[0].line, Is.Null, "the coordinator names the child");
            Assert.That(MeshLayoutPolicy.PlannedLine("C", at[0].plan, "C.1"), Is.EqualTo("Policy: split of C planned: x 0 + child C.1, util 0.9 -> peak 0.69 (C 0.69, C.1 0.69), 2 cut(s) tried"));
            Assert.That(Tick(111f, hot), Is.Empty, "the same report is not planned twice");
            hot.reportAt = 112f;
            Assert.That(Tick(112f, hot).Count, Is.EqualTo(1), "a new report is");
        }

        [Test]
        public void TheDwell_ResetsWhenTheCellCools_AndAStaleReportIsNoEvidence()
        {
            MeshPolicyCell hot = Cell("C", C, 0.9f, 100f);
            Tick(100f, hot);
            MeshPolicyCell cool = Cell("C", C, 0.5f, 105f);
            Tick(105f, cool);
            hot.reportAt = 106f;
            Tick(106f, hot);
            Assert.That(Tick(115f, hot), Is.Empty, "hot again since 106, not since 100");
            Assert.That(Tick(116f, hot).Count, Is.EqualTo(1));
            MeshPolicyCell stale = Cell("C", C, 0.9f, 100f);
            Tick(140f, stale);
            Assert.That(Tick(160f, stale), Is.Empty, "a report 60 s old is no evidence");
        }

        [Test]
        public void TheFastPath_PlansASaturatedCell_Sooner()
        {
            MeshPolicyCell sat = Cell("C", C, 0.96f, 100f, sat: 0.5f);
            Tick(100f, sat);
            Assert.That(Tick(102f, sat), Is.Empty);
            Assert.That(Tick(103f, sat).Count, Is.EqualTo(1));
        }

        [Test]
        public void AnInvalidProbe_AutoSplitOff_OrACooldown_PlansNothing()
        {
            MeshPolicyCell invalid = Cell("C", C, 0.9f, 100f);
            invalid.report.work.state = MeshProbeState.Invalid;
            Tick(100f, invalid);
            Assert.That(Tick(120f, invalid), Is.Empty);
            MeshPolicyCell off = Cell("C", C, 0.9f, 100f, autoSplit: false);
            Tick(100f, off);
            Assert.That(Tick(120f, off), Is.Empty);
            MeshPolicyCell hot = Cell("C", C, 0.9f, 100f);
            _p.OpEnded("split-9", "C", false, true, "done", 100f, _d);
            Assert.That(_p.CooldownLeft("C", 100f), Is.EqualTo(30f));
            Tick(100f, hot);
            Assert.That(Tick(125f, hot), Is.Empty, "the dwell did not run through the cooldown");
            hot.reportAt = 131f;
            Tick(131f, hot);
            Assert.That(Tick(140f, hot), Is.Empty);
            hot.reportAt = 141f;
            Assert.That(Tick(141f, hot).Count, Is.EqualTo(1));
        }

        [Test]
        public void ARefusedCut_IsNotedOnceAMinute_AndARefusedRequestBacksOff()
        {
            _s.splitBenefitRatio = 0.75f;
            MeshPolicyCell hot = Cell("C", C, 0.9f, 100f);
            Tick(100f, hot);
            List<MeshPolicyDecision> at = Tick(110f, hot);
            Assert.That(Lines(at), Is.EqualTo(new[] { "Note Policy: split of C refused: best peak 0.69 over 0.75 x util 0.9" }));
            Assert.That((at[0].cellId, at[0].noteCode, at[0].warning), Is.EqualTo(("C", "split-refused", true)));
            hot.reportAt = 115f;
            Assert.That(Tick(115f, hot), Is.Empty, "not again within a minute");
            hot.reportAt = 170f;
            Assert.That(Tick(170f, hot).Count, Is.EqualTo(1));

            _s.splitBenefitRatio = 1f;
            hot.reportAt = 171f;
            Assert.That(Tick(171f, hot).Count, Is.EqualTo(1));
            _p.RequestRefused("C", 171f);
            hot.reportAt = 180f;
            Assert.That(Tick(180f, hot), Is.Empty);
            hot.reportAt = 232f;
            Assert.That(Tick(232f, hot).Count, Is.EqualTo(1));
        }

        [Test]
        public void HostHeadroom_DefersASplit_AndKeepsTheDwell()
        {
            MeshPolicyCell hot = Cell("C", C, 0.9f, 100f);
            hot.report.cpuPercent = 100f;
            MeshPolicyHost full = new MeshPolicyHost { name = "H", cores = 4, inUse = 3.5f };
            _d.Clear();
            _p.Evaluate(100f, new[] { hot }, false, _ => full, _d);
            _d.Clear();
            _p.Evaluate(110f, new[] { hot }, false, _ => full, _d);
            Assert.That(Lines(_d), Is.EqualTo(new[] { "Line Policy: split of C deferred: host H at 4.27/4 cores" }));
            _d.Clear();
            _p.Evaluate(111f, new[] { hot }, false, _ => full, _d);
            Assert.That(_d, Is.Empty, "the deferral line once a minute");
            MeshPolicyHost room = new MeshPolicyHost { name = "H", cores = 4, inUse = 1f };
            _d.Clear();
            _p.Evaluate(112f, new[] { hot }, false, _ => room, _d);
            Assert.That(_d.Count, Is.EqualTo(1), "planned as soon as the host has room, the dwell kept");
            Assert.That(_d[0].kind, Is.EqualTo(MeshPolicyDecisionKind.RequestSplit));
        }

        [Test]
        public void NoHostReport_IsSaidOnce_AndNothingIsDeferred()
        {
            MeshPolicyCell hot = Cell("C", C, 0.9f, 100f);
            _d.Clear();
            _p.Evaluate(100f, new[] { hot }, false, _ => null, _d);
            _d.Clear();
            _p.Evaluate(110f, new[] { hot }, false, _ => null, _d);
            Assert.That(Lines(_d), Is.EqualTo(new[] { "Line Policy: no host report for C's spawner; headroom not checked", "RequestSplit" }));
            hot.reportAt = 111f;
            _d.Clear();
            _p.Evaluate(111f, new[] { hot }, false, _ => null, _d);
            Assert.That(Lines(_d), Is.EqualTo(new[] { "RequestSplit" }));
        }

        [Test]
        public void HottestFirst_OneRequestPerTick()
        {
            MeshPolicyCell c = Cell("C", C, 0.85f, 100f), e = Cell("E", new MeshRect(100f, -100f, 300f, 100f), 0.95f, 100f, players: Both(88f, 4).ConvertAll(p => (p.Item1 + 200f, p.Item2)), baseId: "E");
            Tick(100f, c, e);
            List<MeshPolicyDecision> at = Tick(110f, c, e);
            Assert.That(at.Count, Is.EqualTo(1));
            Assert.That(at[0].cellId, Is.EqualTo("E"));
        }

        [Test]
        public void ACoolPair_IsMergedAfterItsDwell_LowestPredictionFirst()
        {
            MeshPolicyCell west = Cell("C", West, 0.1f, 100f, MeshFakeReports.Spread(-50f, 2)), east = Cell("C.1", East, 0.1f, 100f, MeshFakeReports.Spread(50f, 2));
            for (float now = 100f; now < 120f; now += 1f)
            {
                west.reportAt = east.reportAt = now;   // fresh reports every tick
                Assert.That(Tick(now, west, east), Is.Empty, "under the merge dwell at " + now);
            }
            west.reportAt = east.reportAt = 120f;
            List<MeshPolicyDecision> at = Tick(120f, west, east);
            Assert.That(at.Count, Is.EqualTo(1));
            Assert.That((at[0].kind, at[0].a, at[0].b), Is.EqualTo((MeshPolicyDecisionKind.RequestMerge, "C", "C.1")));
            Assert.That(at[0].line, Does.StartWith("Policy: merge of C C.1 planned: predicted 0.").And.EndWith("(C 0.1, C.1 0.1)"));
            Assert.That(at[0].predicted, Is.LessThanOrEqualTo(_s.MergeThreshold));
        }

        private List<MeshPolicyDecision> RunFresh(float from, float to, params MeshPolicyCell[] cells)
        {
            List<MeshPolicyDecision> last = null;
            for (float now = from; now <= to; now += 1f)
            {
                foreach (MeshPolicyCell cell in cells)
                    cell.reportAt = now;
                last = Tick(now, cells);
                if (last.Count > 0)
                    return last;
            }
            return last;
        }

        [Test]
        public void AWarmPair_APendingSettle_AutoMergeOff_OrNoFullEdge_DoesNotMerge()
        {
            MeshPolicyCell west = Cell("C", West, 0.45f, 100f, MeshFakeReports.Spread(-50f, 2)), east = Cell("C.1", East, 0.45f, 100f, MeshFakeReports.Spread(50f, 2));
            Assert.That(RunFresh(100f, 130f, west, east), Is.Empty, "predicted 0.53 over the 0.48 threshold");
            west = Cell("C", West, 0.1f, 100f, MeshFakeReports.Spread(-50f, 2));
            east = Cell("C.1", East, 0.1f, 100f, MeshFakeReports.Spread(50f, 2));
            west.familySettlePending = east.familySettlePending = true;
            Assert.That(RunFresh(100f, 130f, west, east), Is.Empty, "a settle is pending");
            west.familySettlePending = east.familySettlePending = false;
            east.autoMerge = false;
            Assert.That(RunFresh(100f, 130f, west, east), Is.Empty, "autoMerge off");
            east.autoMerge = true;
            MeshPolicyCell north = Cell("C.2", new MeshRect(0f, 0f, 100f, 100f), 0.1f, 100f, MeshFakeReports.Spread(50f, 1));
            Assert.That(RunFresh(100f, 130f, west, north), Is.Empty, "no full edge");
            Assert.That(RunFresh(131f, 160f, west, east).Count, Is.EqualTo(1), "the pair merges once nothing holds it");
        }

        [Test]
        public void AnAutomaticSplit_IsMeasured_AndAPoorReliefFlaps()
        {
            _p.OpStarted("split-1", false, "C", new[] { "C", "C.1" }, 0.9f, 0.69f, _d);
            _p.OpEnded("split-1", "C", false, true, "done", 100f, _d);
            MeshPolicyCell west = Cell("C", West, 0.85f, 104f), east = Cell("C.1", East, 0.5f, 104f);
            Assert.That(Tick(104f, west, east), Is.Empty, "under measureAfterSeconds");
            Assert.That(Tick(106f, west, east), Is.Empty, "the reports are from before done + measureAfterSeconds");
            west.reportAt = east.reportAt = 106f;
            List<MeshPolicyDecision> at = Tick(106f, west, east);
            Assert.That(Lines(at), Is.EqualTo(new[]
            {
                "Line Policy: split split-1 measured: predicted peak 0.69, measured 0.85 (C 0.85, C.1 0.5), relief 6 %",
                "Line Policy: flap on C: split split-1 relief 6 % under 10 %; dwell and cooldown x2",
            }));
            Assert.That(_p.MultiplierOf("C"), Is.EqualTo(2f));
            Assert.That(_p.Records, Is.Empty);
            _p.OpEnded("split-2", "C", false, true, "done", 200f, _d);
            Assert.That(_p.CooldownLeft("C", 200f), Is.EqualTo(60f), "the cooldown doubled too");
        }

        [Test]
        public void AGoodRelief_ResetsTheMultiplier_AndAnEndedOpIsNotMeasured()
        {
            _p.OpStarted("split-1", false, "C", new[] { "C", "C.1" }, 0.9f, 0.69f, _d);
            _p.OpEnded("split-1", "C", false, true, "done", 100f, _d);
            MeshPolicyCell west = Cell("C", West, 0.85f, 106f), east = Cell("C.1", East, 0.5f, 106f);
            Tick(106f, west, east);
            Assert.That(_p.MultiplierOf("C"), Is.EqualTo(2f));
            _p.OpStarted("split-2", false, "C", new[] { "C", "C.2" }, 0.9f, 0.5f, _d);
            _p.OpEnded("split-2", "C", false, true, "done", 200f, _d);
            MeshPolicyCell c = Cell("C", West, 0.5f, 206f), c2 = Cell("C.2", East, 0.4f, 206f);
            List<MeshPolicyDecision> at = Tick(206f, c, c2);
            Assert.That(Lines(at), Is.EqualTo(new[]
            {
                "Line Policy: split split-2 measured: predicted peak 0.5, measured 0.5 (C 0.5, C.2 0.4), relief 44 %",
                "Line Policy: C steady again: split split-2 relief 44 %; dwell and cooldown x1",
            }));
            Assert.That(_p.MultiplierOf("C"), Is.EqualTo(1f));

            _p.OpStarted("merge-1", true, "C", new[] { "C" }, 0.5f, 0.3f, _d);
            _d.Clear();
            _p.OpEnded("merge-1", "C", true, false, "aborted C.2 lost", 300f, _d);
            Assert.That(Lines(_d), Is.EqualTo(new[] { "Line Policy: merge merge-1 not measured (aborted C.2 lost)" }));
            Assert.That(_p.Records, Is.Empty);
        }

        [Test]
        public void AMeasurementThatNeverGetsFreshReports_TimesOut_AndANewOpSupersedesAnOldRecord()
        {
            _p.OpStarted("split-1", false, "C", new[] { "C", "C.1" }, 0.9f, 0.69f, _d);
            _p.OpEnded("split-1", "C", false, true, "done", 100f, _d);
            MeshPolicyCell c = Cell("C", West, 0.5f, 110f);   // C.1 never reports
            c.reportAt = 164f;
            Assert.That(Tick(164f, c), Is.Empty);
            c.reportAt = 165f;
            Assert.That(Lines(Tick(165f, c)), Is.EqualTo(new[] { "Line Policy: split split-1 not measured (no fresh report from C.1 in 60 s)" }));
            _p.OpStarted("split-2", false, "C", new[] { "C", "C.2" }, 0.9f, 0.5f, _d);
            _d.Clear();
            _p.OpStarted("merge-1", true, "C", new[] { "C" }, 0.5f, 0.3f, _d);
            Assert.That(Lines(_d), Is.EqualTo(new[] { "Line Policy: split split-2 not measured (superseded by merge-1)" }));
            Assert.That(_p.Records.Count, Is.EqualTo(1));
        }

        [Test]
        public void ASettleAfterDone_ReDatesTheMeasurement()
        {
            _p.OpStarted("split-1", false, "C", new[] { "C", "C.1" }, 0.9f, 0.69f, _d);
            _p.OpEnded("split-1", "C", false, true, "done", 100f, _d);
            Assert.That(_p.CooldownLeft("C", 100f), Is.EqualTo(30f), "the cooldown runs from done");
            _p.OpSettled("split-1", 150f);
            MeshPolicyCell west = Cell("C", West, 0.5f, 152f), east = Cell("C.1", East, 0.4f, 152f);
            Assert.That(Tick(152f, west, east), Is.Empty, "the measurement waits for reports after the settle + measureAfterSeconds");
            west.reportAt = east.reportAt = 156f;
            Assert.That(Lines(Tick(156f, west, east)), Has.Exactly(1).StartsWith("Line Policy: split split-1 measured"));
            _p.OpSettled("split-9", 160f);   // an unknown op is ignored
        }

        [Test]
        public void AFamilyAtItsCap_IsRefusedBeforePlanning()
        {
            MeshPolicyCell hot = Cell("C", C, 0.9f, 100f);
            hot.familyCells = 4;
            Tick(100f, hot);
            Assert.That(Lines(Tick(110f, hot)), Is.EqualTo(new[] { "Note Policy: split of C refused: family C has 4 cell(s), the map's most" }));
        }

        [Test]
        public void AMergeIsMeasured_AndASplitSoonAfterAMerge_Flaps()
        {
            _p.OpStarted("merge-1", true, "C", new[] { "C" }, 0.3f, 0.25f, _d);
            _p.OpEnded("merge-1", "C", true, true, "done", 100f, _d);
            MeshPolicyCell c = Cell("C", C, 0.3f, 106f);
            Assert.That(Lines(Tick(106f, c)), Is.EqualTo(new[] { "Line Policy: merge merge-1 measured: predicted 0.25, measured 0.3 (C)" }));
            MeshPolicyCell hot = Cell("C", C, 0.9f, 131f);
            Tick(131f, hot);
            hot.reportAt = 141f;
            List<MeshPolicyDecision> at = Tick(141f, hot);
            Assert.That(Lines(at), Is.EqualTo(new[] { "Line Policy: flap on C: split of C planned 41 s after merge; dwell and cooldown x2", "RequestSplit" }));
        }
    }
}
