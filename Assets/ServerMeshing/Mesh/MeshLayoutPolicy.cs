// Server Meshing add-on — the automatic policy (Slice 3, slices/slice3_design.md decisions 5-10, 12; plan D3): which
// cell is hot long enough to split, which pair of a family is cool long enough to merge, what a split's host can take,
// what an automatic operation delivered against its prediction, and which family flaps. Pure: the coordinator
// (MeshLayoutCoordinator_Policy.cs) feeds it the participating cells once a second and acts on the decisions it returns;
// every line here is final text. Clocks are the coordinator's seconds; records live in memory only.
using System;
using System.Collections.Generic;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>MeshPolicyCell is one cell as the policy sees it this tick: its rectangle, its map's flags and its latest report.</summary>
    public sealed class MeshPolicyCell
    {
        public string id, baseId, mapId;
        public MeshRect owned, interest;
        public bool autoSplit, autoMerge;
        public float minCellSize;
        /// <summary>How many live cells the family has, and the most it may have (a family at its cap is not planned).</summary>
        public int familyCells = 1, maxCellsPerBase = 4;
        /// <summary>Whether any cell of the family, participating or not, has interest beyond owned (a split's settle pending: no merge).</summary>
        public bool familySettlePending;
        public MeshLoadReportMessage report;
        public float reportAt = -1f;
        /// <summary>The cell's process CPU share of one core (its report's cpuPercent / 100), for the host rule.</summary>
        public float Cores => report != null ? Math.Max(0f, report.cpuPercent) / 100f : 0f;
    }

    /// <summary>MeshPolicyHost is a base family's host this tick: its name, cores, and the cores its cells use now.</summary>
    public struct MeshPolicyHost
    {
        public string name;
        public int cores;
        public float inUse;
    }

    /// <summary>MeshPolicyDecisionKind is what the coordinator does with a decision.</summary>
    public enum MeshPolicyDecisionKind
    {
        /// <summary>Log the line (warning when flagged).</summary>
        Line,
        /// <summary>Log the line and send it to the cell as a CoordinatorNote (code noteCode).</summary>
        Note,
        /// <summary>Log the line, then request the split in plan of cellId.</summary>
        RequestSplit,
        /// <summary>Log the line, then request the merge of a and b.</summary>
        RequestMerge,
    }

    /// <summary>MeshPolicyDecision is one thing the policy decided this tick.</summary>
    public sealed class MeshPolicyDecision
    {
        public MeshPolicyDecisionKind kind;
        public bool warning;
        public string line;
        public string cellId;
        public string noteCode;
        public MeshSplitPlan plan;
        public string a, b;
        public float predicted;
    }

    /// <summary>MeshPolicyRecord is an automatic operation the policy predicted, measured once it is done.</summary>
    public sealed class MeshPolicyRecord
    {
        public string opId;
        public bool merge;
        public string baseId;
        /// <summary>A split's parent and child; a merge's survivor.</summary>
        public readonly List<string> cells = new List<string>();
        public float utilBefore;
        public float predicted;
        /// <summary>When the operation was done (-1 while it runs).</summary>
        public float doneAt = -1f;
    }

    /// <summary>MeshLayoutPolicy decides automatic splits and merges from the cells' load reports. Pure.</summary>
    public sealed class MeshLayoutPolicy
    {
        /// <summary>Seconds between two refusal or deferral lines about one cell, and the back-off after a refused request.</summary>
        public const float RepeatSeconds = 60f;
        /// <summary>Seconds past measureAfterSeconds a done operation waits for its cells' fresh reports before it is given up as unmeasured.</summary>
        public const float MeasureTimeoutSeconds = 60f;

        private readonly MeshDynamicSettings _settings;
        private readonly float _band;
        private readonly Dictionary<string, CellClocks> _cells = new Dictionary<string, CellClocks>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, FamilyState> _families = new Dictionary<string, FamilyState>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, float> _pairCoolSince = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private readonly List<MeshPolicyRecord> _records = new List<MeshPolicyRecord>();
        private readonly HashSet<string> _hostWarned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private sealed class CellClocks
        {
            public float hotSince = -1f, satSince = -1f, plannedReportAt = -1f, refusedUntil = -1f, lastRefusalAt = -1f, lastDeferralAt = -1f;
        }

        private sealed class FamilyState
        {
            public float cooldownUntil = -1f, multiplier = 1f, lastMergeDoneAt = -1f;
        }

        public MeshLayoutPolicy(MeshDynamicSettings settings, float band)
        {
            _settings = settings ?? new MeshDynamicSettings();
            _band = band;
        }

        public MeshPlannerWeights Weights => MeshPlannerWeights.From(_settings);

        /// <summary>The records of the automatic operations still to be measured (tests).</summary>
        public IReadOnlyList<MeshPolicyRecord> Records => _records;

        /// <summary>Multiplier of a family's dwells and cooldown (1 until it flaps).</summary>
        public float MultiplierOf(string baseId) => _families.TryGetValue(baseId ?? string.Empty, out FamilyState f) ? f.multiplier : 1f;

        /// <summary>CooldownLeft is how long a family's policy cooldown still runs at <paramref name="now"/> (0 when none).</summary>
        public float CooldownLeft(string baseId, float now) => _families.TryGetValue(baseId ?? string.Empty, out FamilyState f) && f.cooldownUntil > now ? f.cooldownUntil - now : 0f;

        private FamilyState Family(string baseId)
        {
            if (!_families.TryGetValue(baseId, out FamilyState family))
                _families[baseId] = family = new FamilyState();
            return family;
        }

        private CellClocks Clocks(string id)
        {
            if (!_cells.TryGetValue(id, out CellClocks clocks))
                _cells[id] = clocks = new CellClocks();
            return clocks;
        }

        private bool Fresh(MeshPolicyCell cell, float now) => cell.report != null && cell.reportAt >= 0f && now - cell.reportAt <= Math.Max(0f, _settings.reportFreshSeconds);

        /// <summary>
        /// Evaluate runs one tick over the participating cells: measurements due, then at most one request (the hottest
        /// split candidate, else the coolest merge pair) and the rate-limited refusal and deferral lines.
        /// <paramref name="hostOf"/> gives a base family's host (null when its spawner reported none); <paramref name="opBusy"/>
        /// is true while an operation runs or froze world-wide.
        /// </summary>
        public void Evaluate(float now, IReadOnlyList<MeshPolicyCell> cells, bool opBusy, Func<string, MeshPolicyHost?> hostOf, List<MeshPolicyDecision> decisions)
        {
            Measure(now, cells, decisions);
            Dictionary<string, MeshPolicyCell> byId = new Dictionary<string, MeshPolicyCell>(StringComparer.OrdinalIgnoreCase);
            foreach (MeshPolicyCell cell in cells)
                byId[cell.id] = cell;
            bool requested = false;
            List<MeshPolicyCell> hot = new List<MeshPolicyCell>();
            foreach (MeshPolicyCell cell in cells)
            {
                CellClocks clocks = Clocks(cell.id);
                bool usable = Fresh(cell, now) && MeshSplitPlanner.Usable(cell.report, out _);
                bool held = opBusy || CooldownLeft(cell.baseId, now) > 0f;
                float util = usable ? cell.report.work.Utilisation : 0f;
                bool isHot = usable && !held && util >= _settings.splitUtil;
                bool isSat = usable && !held && (cell.report.work.saturatedShare > _settings.fastPathSaturatedShare || util >= _settings.fastPathUtil);
                clocks.hotSince = isHot ? (clocks.hotSince < 0f ? now : clocks.hotSince) : -1f;
                clocks.satSince = isSat ? (clocks.satSince < 0f ? now : clocks.satSince) : -1f;
                if (!cell.autoSplit || !isHot)
                    continue;
                float m = MultiplierOf(cell.baseId);
                bool due = now - clocks.hotSince >= _settings.splitDwellSeconds * m || (isSat && now - clocks.satSince >= _settings.fastPathSeconds * m);
                if (due)
                    hot.Add(cell);
            }
            hot.Sort((x, y) => y.report.work.Utilisation.CompareTo(x.report.work.Utilisation));
            foreach (MeshPolicyCell cell in hot)
            {
                CellClocks clocks = Clocks(cell.id);
                if (clocks.plannedReportAt == cell.reportAt || clocks.refusedUntil > now)
                    continue;
                clocks.plannedReportAt = cell.reportAt;
                MeshSplitPlan plan = null;
                string refusal = cell.familyCells >= cell.maxCellsPerBase ? $"family {cell.baseId} has {cell.familyCells} cell(s), the map's most" : null;
                if (refusal != null || !MeshSplitPlanner.TryPlan(cell.report, cell.owned, cell.minCellSize, _band, Weights, _settings.splitBenefitRatio, out plan, out refusal))
                {
                    if (now - clocks.lastRefusalAt >= RepeatSeconds || clocks.lastRefusalAt < 0f)
                    {
                        clocks.lastRefusalAt = now;
                        decisions.Add(new MeshPolicyDecision { kind = MeshPolicyDecisionKind.Note, warning = true, cellId = cell.id, noteCode = "split-refused", line = $"Policy: split of {cell.id} refused: {refusal}" });
                    }
                    continue;
                }
                if (requested)
                    continue;
                MeshPolicyHost? host = hostOf(cell.baseId);
                if (host == null)
                {
                    if (_hostWarned.Add(cell.baseId))
                        decisions.Add(new MeshPolicyDecision { kind = MeshPolicyDecisionKind.Line, warning = true, cellId = cell.id, line = $"Policy: no host report for {cell.baseId}'s spawner; headroom not checked" });
                }
                else
                {
                    float projected = host.Value.inUse + cell.Cores * (plan.utilNow > 0f ? plan.predictedChild / plan.utilNow : 0f);
                    if (projected > _settings.hostHeadroomShare * host.Value.cores)
                    {
                        clocks.plannedReportAt = -1f;   // the dwell is kept: planned again on the next report
                        if (now - clocks.lastDeferralAt >= RepeatSeconds || clocks.lastDeferralAt < 0f)
                        {
                            clocks.lastDeferralAt = now;
                            decisions.Add(new MeshPolicyDecision { kind = MeshPolicyDecisionKind.Line, warning = true, cellId = cell.id, line = $"Policy: split of {cell.id} deferred: host {host.Value.name} at {MeshSplitPlanner.Ratio(projected)}/{host.Value.cores} cores" });
                        }
                        continue;
                    }
                }
                FamilyState family = Family(cell.baseId);
                if (family.lastMergeDoneAt >= 0f && now - family.lastMergeDoneAt < _settings.flapWindowSeconds)
                    Flap(family, cell.baseId, $"split of {cell.id} planned {MeshSplitOperation.Number(now - family.lastMergeDoneAt)} s after merge", decisions);
                requested = true;
                // The line is the coordinator's: it names the child id it picks (PlannedLine).
                decisions.Add(new MeshPolicyDecision { kind = MeshPolicyDecisionKind.RequestSplit, cellId = cell.id, plan = plan });
            }
            EvaluateMerges(now, cells, byId, opBusy, requested, decisions);
        }

        private void EvaluateMerges(float now, IReadOnlyList<MeshPolicyCell> cells, Dictionary<string, MeshPolicyCell> byId, bool opBusy, bool requested, List<MeshPolicyDecision> decisions)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<(float predicted, MeshPolicyCell a, MeshPolicyCell b)> due = new List<(float, MeshPolicyCell, MeshPolicyCell)>();
            for (int i = 0; i < cells.Count; ++i)
            {
                for (int j = i + 1; j < cells.Count; ++j)
                {
                    MeshPolicyCell a = cells[i], b = cells[j];
                    if (!string.Equals(a.baseId, b.baseId, StringComparison.OrdinalIgnoreCase) || a.mapId != b.mapId)
                        continue;
                    if (!a.owned.TryUnion(b.owned, 0.5f, out MeshRect union))
                        continue;
                    string key = PairKey(a.id, b.id);
                    seen.Add(key);
                    bool held = opBusy || CooldownLeft(a.baseId, now) > 0f || a.familySettlePending || b.familySettlePending || !a.autoMerge || !b.autoMerge;
                    float predicted = 0f;
                    bool cool = !held && Fresh(a, now) && Fresh(b, now)
                        && MeshSplitPlanner.TryPredictUnion(a.report, a.owned, b.report, b.owned, union, _band, Weights, out predicted, out _)
                        && predicted <= _settings.MergeThreshold;
                    if (!cool)
                    {
                        _pairCoolSince.Remove(key);
                        continue;
                    }
                    if (!_pairCoolSince.TryGetValue(key, out float since))
                        _pairCoolSince[key] = since = now;
                    if (now - since >= _settings.mergeDwellSeconds * MultiplierOf(a.baseId))
                        due.Add((predicted, a, b));
                }
            }
            List<string> stale = new List<string>();
            foreach (string key in _pairCoolSince.Keys)
            {
                if (!seen.Contains(key))
                    stale.Add(key);
            }
            foreach (string key in stale)
                _pairCoolSince.Remove(key);
            if (requested || due.Count == 0)
                return;
            due.Sort((x, y) => x.predicted.CompareTo(y.predicted));
            (float p, MeshPolicyCell first, MeshPolicyCell second) = due[0];
            decisions.Add(new MeshPolicyDecision
            {
                kind = MeshPolicyDecisionKind.RequestMerge, a = first.id, b = second.id, predicted = p,
                line = $"Policy: merge of {first.id} {second.id} planned: predicted {MeshSplitPlanner.Ratio(p)} ({first.id} {MeshSplitPlanner.Ratio(first.report.work.Utilisation)}, {second.id} {MeshSplitPlanner.Ratio(second.report.work.Utilisation)})",
            });
        }

        /// <summary>PlannedLine is the line of an accepted split once the coordinator has named the child.</summary>
        public static string PlannedLine(string cellId, MeshSplitPlan plan, string childId)
        {
            return $"Policy: split of {cellId} planned: {plan.AxisText} {MeshSplitOperation.Number(plan.at)} {plan.SideText} child {childId}, util {MeshSplitPlanner.Ratio(plan.utilNow)} -> peak {MeshSplitPlanner.Ratio(plan.peak)} ({cellId} {MeshSplitPlanner.Ratio(plan.predictedParent)}, {childId} {MeshSplitPlanner.Ratio(plan.predictedChild)}), {plan.candidates} cut(s) tried";
        }

        private static string PairKey(string a, string b)
        {
            return string.CompareOrdinal(a.ToUpperInvariant(), b.ToUpperInvariant()) <= 0 ? a + "|" + b : b + "|" + a;
        }

        /// <summary>RequestRefused: the coordinator refused the policy's request (its line said why); the cell is left alone for RepeatSeconds.</summary>
        public void RequestRefused(string cellId, float now) => Clocks(cellId).refusedUntil = now + RepeatSeconds;

        /// <summary>OpStarted records an automatic operation to measure once it is done; an older record of the family still waiting is given up.</summary>
        public void OpStarted(string opId, bool merge, string baseId, IEnumerable<string> cells, float utilBefore, float predicted, List<MeshPolicyDecision> decisions)
        {
            for (int i = _records.Count - 1; i >= 0; --i)
            {
                MeshPolicyRecord old = _records[i];
                if (!string.Equals(old.baseId, baseId, StringComparison.OrdinalIgnoreCase))
                    continue;
                _records.RemoveAt(i);
                decisions.Add(NotMeasured(old, "superseded by " + opId));
            }
            MeshPolicyRecord record = new MeshPolicyRecord { opId = opId, merge = merge, baseId = baseId, utilBefore = utilBefore, predicted = predicted };
            record.cells.AddRange(cells);
            _records.Add(record);
        }

        private static MeshPolicyDecision NotMeasured(MeshPolicyRecord record, string why)
        {
            return new MeshPolicyDecision { kind = MeshPolicyDecisionKind.Line, warning = true, line = $"Policy: {(record.merge ? "merge" : "split")} {record.opId} not measured ({why})" };
        }

        /// <summary>
        /// OpEnded: any operation on a family (manual or automatic) starts its cooldown (opCooldownSeconds times the
        /// family's multiplier, never under the manual guard splitCooldownSeconds); a done automatic one waits for its
        /// measurement, one that ended otherwise is reported unmeasured. A merge's done is remembered for the flap window.
        /// </summary>
        public void OpEnded(string opId, string baseId, bool merge, bool done, string endReason, float now, List<MeshPolicyDecision> decisions)
        {
            FamilyState family = Family(baseId ?? string.Empty);
            family.cooldownUntil = now + Math.Max(Math.Max(0f, _settings.opCooldownSeconds) * family.multiplier, Math.Max(0f, _settings.splitCooldownSeconds));
            if (done && merge)
                family.lastMergeDoneAt = now;
            MeshPolicyRecord record = _records.Find(r => r.opId == opId);
            if (record == null)
                return;
            if (done)
            {
                record.doneAt = now;
                return;
            }
            _records.Remove(record);
            decisions.Add(NotMeasured(record, endReason));
        }

        /// <summary>OpSettled: a split done with stragglers settled later; its measurement counts from the settle (the parent's interest shrank only then).</summary>
        public void OpSettled(string opId, float now)
        {
            MeshPolicyRecord record = _records.Find(r => r.opId == opId);
            if (record != null && record.doneAt >= 0f)
                record.doneAt = now;
        }

        private void Measure(float now, IReadOnlyList<MeshPolicyCell> cells, List<MeshPolicyDecision> decisions)
        {
            for (int i = _records.Count - 1; i >= 0; --i)
            {
                MeshPolicyRecord record = _records[i];
                if (record.doneAt < 0f || now - record.doneAt < _settings.measureAfterSeconds)
                    continue;
                float readyAt = record.doneAt + _settings.measureAfterSeconds;
                List<MeshPolicyCell> measured = new List<MeshPolicyCell>();
                string waitingFor = null;
                foreach (string id in record.cells)
                {
                    MeshPolicyCell cell = null;
                    foreach (MeshPolicyCell candidate in cells)
                    {
                        if (string.Equals(candidate.id, id, StringComparison.OrdinalIgnoreCase))
                            cell = candidate;
                    }
                    if (cell == null || !Fresh(cell, now) || cell.reportAt < readyAt || !MeshSplitPlanner.Usable(cell.report, out _))
                    {
                        waitingFor = id;
                        break;
                    }
                    measured.Add(cell);
                }
                if (waitingFor != null)
                {
                    if (now - readyAt >= MeasureTimeoutSeconds)
                    {
                        _records.RemoveAt(i);
                        decisions.Add(NotMeasured(record, $"no fresh report from {waitingFor} in {MeshSplitOperation.Number(now - readyAt)} s"));
                    }
                    continue;
                }
                _records.RemoveAt(i);
                float peak = 0f;
                foreach (MeshPolicyCell cell in measured)
                    peak = Math.Max(peak, cell.report.work.Utilisation);
                if (record.merge)
                {
                    decisions.Add(new MeshPolicyDecision { kind = MeshPolicyDecisionKind.Line, line = $"Policy: merge {record.opId} measured: predicted {MeshSplitPlanner.Ratio(record.predicted)}, measured {MeshSplitPlanner.Ratio(peak)} ({measured[0].id})" });
                    continue;
                }
                float relief = record.utilBefore > 0f ? 1f - peak / record.utilBefore : 0f;
                decisions.Add(new MeshPolicyDecision
                {
                    kind = MeshPolicyDecisionKind.Line,
                    line = $"Policy: split {record.opId} measured: predicted peak {MeshSplitPlanner.Ratio(record.predicted)}, measured {MeshSplitPlanner.Ratio(peak)} ({measured[0].id} {MeshSplitPlanner.Ratio(measured[0].report.work.Utilisation)}, {measured[1].id} {MeshSplitPlanner.Ratio(measured[1].report.work.Utilisation)}), relief {Percent(relief)} %",
                });
                FamilyState family = Family(record.baseId);
                if (relief < _settings.flapReliefShare)
                    Flap(family, record.baseId, $"split {record.opId} relief {Percent(relief)} % under {Percent(_settings.flapReliefShare)} %", decisions);
                else if (relief >= 2f * _settings.flapReliefShare && family.multiplier > 1f)
                {
                    family.multiplier = 1f;
                    decisions.Add(new MeshPolicyDecision { kind = MeshPolicyDecisionKind.Line, line = $"Policy: {record.baseId} steady again: split {record.opId} relief {Percent(relief)} %; dwell and cooldown x1" });
                }
            }
        }

        private void Flap(FamilyState family, string baseId, string why, List<MeshPolicyDecision> decisions)
        {
            family.multiplier = Math.Min(Math.Max(1f, _settings.flapMaxMultiplier), family.multiplier * 2f);
            decisions.Add(new MeshPolicyDecision { kind = MeshPolicyDecisionKind.Line, warning = true, line = $"Policy: flap on {baseId}: {why}; dwell and cooldown x{MeshSplitPlanner.Ratio(family.multiplier)}" });
        }

        private static string Percent(float share) => (share * 100f).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
    }
}
