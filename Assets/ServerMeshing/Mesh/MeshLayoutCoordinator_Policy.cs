// Server Meshing add-on — the coordinator's automatic policy (Slice 3, slices/slice3_design.md decisions 5-10, 12): once
// a second, after the operation's tick, the participating cells of every Dynamic map that sets autoSplit or autoMerge
// are handed to MeshLayoutPolicy with their latest load reports; its decisions become lines, notes to cells, and
// requests through the manual split and merge paths (so their guards and lines are the same). Every operation's end,
// manual or automatic, is reported to the policy for the family's cooldown and the automatic ones' measurement.
using System;
using System.Collections.Generic;

namespace MultiplayerARPG.MMO.Mesh
{
    public sealed partial class MeshLayoutCoordinator
    {
        private MeshLayoutPolicy _policy;
        private readonly List<MeshPolicyCell> _policyCells = new List<MeshPolicyCell>();
        private readonly List<MeshPolicyDecision> _policyDecisions = new List<MeshPolicyDecision>();

        /// <summary>The policy (null before Start); tests read its records and multipliers.</summary>
        public MeshLayoutPolicy Policy => _policy;

        /// <summary>PolicyEnabled: some Dynamic map lets the central split or merge by itself.</summary>
        public bool PolicyEnabled
        {
            get
            {
                foreach (MeshMapLayout layout in _world.maps)
                {
                    if (layout != null && layout.IsDynamic && (layout.autoSplit || layout.autoMerge))
                        return true;
                }
                return false;
            }
        }

        private void PolicyStart()
        {
            _policy = new MeshLayoutPolicy(Settings, Settings.bandWidth);
            string error = MeshLayoutValidator.PolicySettingsError(Settings);
            if (error != null)
                _log.Error("Coordinator: " + error);
            if (!PolicyEnabled)
                return;
            List<string> splits = new List<string>(), merges = new List<string>();
            foreach (MeshMapLayout layout in _world.maps)
            {
                if (layout == null || !layout.IsDynamic)
                    continue;
                if (layout.autoSplit)
                    splits.Add(layout.mapId);
                if (layout.autoMerge)
                    merges.Add(layout.mapId);
            }
            _log.Info($"Coordinator policy: automatic split on {(splits.Count > 0 ? string.Join(",", splits) : "no map")}, automatic merge on {(merges.Count > 0 ? string.Join(",", merges) : "no map")}; splitUtil {MeshSplitPlanner.Ratio(Settings.splitUtil)} dwell {Seconds(Settings.splitDwellSeconds)} s, merge at {MeshSplitPlanner.Ratio(Settings.MergeThreshold)} for {Seconds(Settings.mergeDwellSeconds)} s, cooldown {Seconds(Settings.opCooldownSeconds)} s, benefit {MeshSplitPlanner.Ratio(Settings.splitBenefitRatio)}");
        }

        private void PolicyStop()
        {
            _policy = null;
            _policyCells.Clear();
            _policyDecisions.Clear();
        }

        /// <summary>PolicyTick runs the policy over the participating cells and acts on what it decided.</summary>
        private void PolicyTick()
        {
            if (_policy == null || Phase != MeshCoordinatorPhase.Running || SplitHost == null || !_store.Writable || !PolicyEnabled)
                return;
            float now = _clock.Now;
            CollectPolicyCells(_policyCells);
            bool busy = _op != null && (_op.InProgress || _op.IsFrozen);
            _policyDecisions.Clear();
            _policy.Evaluate(now, _policyCells, busy, HostOf, _policyDecisions);
            ActOnDecisions(_policyDecisions);
        }

        /// <summary>The participating cells: Active, owning something, bound and answered, registered, on a recovered Dynamic map (decision 5).</summary>
        private void CollectPolicyCells(List<MeshPolicyCell> into)
        {
            into.Clear();
            IReadOnlyList<MeshCell> cells = _world.Cells;
            for (int i = 0; i < cells.Count; ++i)
            {
                MeshCell cell = cells[i];
                if (!cell.OnDynamicMap || cell.state != MeshCellState.Active || cell.owned.IsEmpty || OutcomeOf(cell.mapId) != MeshRecoveryOutcome.AdoptStatic)
                    continue;
                MeshCellBinding binding = BindingOf(cell.id);
                if (binding == null || !binding.answered || binding.unregisteredAt >= 0f || binding.staticNeighbour)
                    continue;
                MeshMapLayout layout = _world.GetLayout(cell.mapId);
                if (layout == null)
                    continue;
                IReadOnlyList<MeshCell> family = _world.FamilyOf(cell.baseId);
                int live = 0;
                bool settlePending = false;
                for (int f = 0; f < family.Count; ++f)
                {
                    if (family[f].state == MeshCellState.Retired)
                        continue;
                    live++;
                    if (!family[f].interest.Equals(family[f].owned))
                        settlePending = true;
                }
                into.Add(new MeshPolicyCell
                {
                    id = cell.id, baseId = cell.baseId, mapId = cell.mapId, owned = cell.owned, interest = cell.interest,
                    autoSplit = layout.autoSplit, autoMerge = layout.autoMerge,
                    minCellSize = Math.Max(Settings.bandWidth, layout.minCellSize > 0f ? layout.minCellSize : Settings.defaultMinCellSize),
                    familyCells = Math.Max(1, live), maxCellsPerBase = layout.maxCellsPerBase > 0 ? layout.maxCellsPerBase : Settings.defaultMaxCellsPerBase,
                    familySettlePending = settlePending, report = binding.report, reportAt = binding.lastReportAt,
                });
            }
        }

        /// <summary>HostOf: a base family's host as its spawner last reported it (the host-wide cores in use), or null without a report.</summary>
        private MeshPolicyHost? HostOf(string baseId)
        {
            if (!SplitHost.TryGetHost(baseId, out MeshHostInfo host))
                return null;
            return new MeshPolicyHost { name = host.hostName, cores = host.cores, inUse = host.coresInUse };
        }

        /// <summary>ActOnDecisions works from a copy: a request may end its operation at once, which reports back into the policy.</summary>
        private void ActOnDecisions(List<MeshPolicyDecision> decisions)
        {
            foreach (MeshPolicyDecision decision in decisions.ToArray())
            {
                switch (decision.kind)
                {
                    case MeshPolicyDecisionKind.Line:
                        Line(decision.warning, decision.line);
                        break;
                    case MeshPolicyDecisionKind.Note:
                        Line(decision.warning, decision.line);
                        MeshCellBinding binding = BindingOf(decision.cellId);
                        if (binding != null && binding.unregisteredAt < 0f)
                            Note(binding.connectionId, MeshNoteLevel.Warning, decision.cellId, decision.noteCode, decision.line.Substring("Policy: ".Length));
                        break;
                    case MeshPolicyDecisionKind.RequestSplit:
                        RequestPlannedSplit(decision);
                        break;
                    case MeshPolicyDecisionKind.RequestMerge:
                        RequestPlannedMerge(decision);
                        break;
                }
            }
        }

        private void RequestPlannedSplit(MeshPolicyDecision decision)
        {
            float now = _clock.Now;
            MeshCell cell = _world.GetCellById(decision.cellId);
            string childId = cell != null ? FreeChildId(cell) : null;
            if (childId == null)
            {
                Line(true, $"Policy: split of {decision.cellId} refused: no free child id for {(cell != null ? cell.baseId : decision.cellId)}");
                _policy.RequestRefused(decision.cellId, now);
                return;
            }
            MeshSplitPlan plan = decision.plan;
            Line(false, MeshLayoutPolicy.PlannedLine(decision.cellId, plan, childId));
            if (!RequestSplit(decision.cellId, plan.axis, plan.at, plan.childOnPositiveSide, childId, out _))
            {
                _policy.RequestRefused(decision.cellId, now);
                return;
            }
            Started(new[] { decision.cellId, childId }, false, plan.utilNow, plan.peak);
        }

        private void RequestPlannedMerge(MeshPolicyDecision decision)
        {
            Line(false, decision.line);
            if (!RequestMerge(decision.a, decision.b, out _))
            {
                _policy.RequestRefused(decision.a, _clock.Now);
                _policy.RequestRefused(decision.b, _clock.Now);
                return;
            }
            MeshMergeOperation merge = _op as MeshMergeOperation;
            Started(new[] { merge != null ? merge.survivorId : decision.a }, true, Math.Max(UtilOf(decision.a), UtilOf(decision.b)), decision.predicted);
        }

        /// <summary>Started records the operation just requested; one that ended inside its own request (a store or spawn failure) is reported unmeasured at once.</summary>
        private void Started(string[] cells, bool merge, float utilBefore, float predicted)
        {
            MeshLayoutOperation op = _op;
            List<MeshPolicyDecision> decisions = new List<MeshPolicyDecision>();
            _policy.OpStarted(op.opId, merge, op.baseId, cells, utilBefore, predicted, decisions);
            if (!op.InProgress)
                _policy.OpEnded(op.opId, op.baseId, merge, false, op.endReason, _clock.Now, decisions);
            ActOnDecisions(decisions);
        }

        private float UtilOf(string cellId)
        {
            MeshCellBinding binding = BindingOf(cellId);
            return binding != null && binding.report != null ? binding.report.work.Utilisation : 0f;
        }

        /// <summary>FreeChildId: `&lt;base&gt;.&lt;n&gt;` with the smallest n that names no live or registered cell and passes the id-reuse rule (decision 7).</summary>
        private string FreeChildId(MeshCell parent)
        {
            MeshMapLayout layout = _world.GetLayout(parent.mapId);
            int max = Math.Max(1, (layout != null && layout.maxCellsPerBase > 0 ? layout.maxCellsPerBase : Settings.defaultMaxCellsPerBase) * 4);
            for (int n = 1; n <= max; ++n)
            {
                string id = parent.baseId + "." + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
                MeshCell existing = _world.GetCellById(id);
                if (existing != null && existing.state != MeshCellState.Retired)
                    continue;
                if (IsRegistered(id) || IdReuseRefusal(id) != null || !MeshLayoutValidator.IsSafeCellId(id, out _))
                    continue;
                return id;
            }
            return null;
        }

        /// <summary>PolicyOpEnded: every operation's end (done, settled, aborted, frozen, ended) reaches the policy (decisions 10 and 12).</summary>
        private void PolicyOpEnded(MeshLayoutOperation op, bool done, string reason)
        {
            if (_policy == null || op == null)
                return;
            List<MeshPolicyDecision> decisions = new List<MeshPolicyDecision>();
            _policy.OpEnded(op.opId, op.baseId, op is MeshMergeOperation, done, reason, _clock.Now, decisions);
            ActOnDecisions(decisions);
        }
    }
}
