// Server Meshing add-on — the coordinator's supervision (Slice 2d, slices/slice2d_design.md decisions 1-7): the watched
// cells of every Dynamic map's split family are restarted when down or hung (MeshCellSupervisor decides, this file acts),
// collapsed into their base after childMaxRestarts failures, and killed through their spawner when hung, on the operator's
// `kill`, or when a merge's retire step outlives retireKillAfterSeconds. Incarnations only move forward: a fenced or older
// one is answered with a `fenced:` Retire. An operation whose member crashes aborts before its e2 broadcast and pauses
// after it, resuming on the member's return (rebound before anything is re-answered). Every line an operator or the
// harness reads starts "Supervisor: ", "Split <op>: " or "Merge <op>: " and goes through Line (mirrored by the runner).
// The collapse, the spawner kills, the fenced set's store and the id-reuse rule are in MeshLayoutCoordinator_Collapse.cs.
using System;
using System.Collections.Generic;

namespace MultiplayerARPG.MMO.Mesh
{
    public sealed partial class MeshLayoutCoordinator
    {
        /// <summary>A member of an operation unregistered this long is lost: it aborts the operation before its e2 broadcast and pauses it after.</summary>
        public const float MemberLostSeconds = 5f;

        private readonly Dictionary<string, MeshSupervisedCell> _supervised = new Dictionary<string, MeshSupervisedCell>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _collapseWaitLogged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly MeshFencedSet _fenced = new MeshFencedSet();
        private readonly List<MeshCell> _watched = new List<MeshCell>();
        private readonly List<MeshSupervisedCell> _supervisedPass = new List<MeshSupervisedCell>();

        /// <summary>The supervisor's record of a cell (null when it watches none by that id).</summary>
        public MeshSupervisedCell SupervisedOf(string cellId) => _supervised.TryGetValue(cellId ?? string.Empty, out MeshSupervisedCell cell) ? cell : null;

        /// <summary>Whether a hello from this incarnation of the cell would be answered with a fenced Retire.</summary>
        public bool IsFencedIncarnation(string cellId, string incarnation) => _fenced.IsFenced(cellId, incarnation);

        private MeshSupervisorSettings SupervisorSettings => MeshSupervisorSettings.From(Settings);

        private static string Seconds(float value) => MeshCellSupervisor.Number(value);

        /// <summary>SupervisionStart: the store's fenced set and aborted ids are loaded (the first save kept them, decision 7), and the grace rule checked.</summary>
        private void SupervisionStart(MeshCoordinatorStoreData data)
        {
            _fenced.Load(data.fencedIncarnations);
            if (data.abortedIds != null)
                _abortedIds.UnionWith(data.abortedIds);
            string error = MeshLayoutValidator.SupervisionSettingsError(Settings);
            if (error != null)
                _log.Error("Coordinator: " + error);
        }

        private void SupervisionStop()
        {
            _supervised.Clear();
            _collapseWaitLogged.Clear();
            _fenced.Clear();
        }

        // ---- hellos --------------------------------------------------------------------------------------------------

        /// <summary>
        /// HelloIsFenced answers with a `fenced:` Retire a hello from a fenced incarnation, from one older than the cell's
        /// binding (incarnations only move forward, decision 5), or from any process of a cell being collapsed.
        /// </summary>
        private bool HelloIsFenced(long connectionId, MeshLayoutHelloMessage hello)
        {
            string id = hello.cellId, incarnation = hello.incarnation ?? string.Empty;
            MeshCellBinding bound = BindingOf(id);
            string reason = null;
            if (_fenced.IsFenced(id, incarnation) || (bound != null && !bound.staticNeighbour && MeshIncarnation.IsOlder(incarnation, bound.incarnation)))
                reason = "fenced: stale incarnation";
            else if (_supervised.TryGetValue(id, out MeshSupervisedCell cell) && (cell.phase == MeshSupervisorPhase.CollapseQueued || cell.phase == MeshSupervisorPhase.Collapsing))
                reason = "fenced: collapsing";
            if (reason == null)
                return false;
            if (_fenced.Add(id, incarnation))
                SaveFencedSet();
            _transport.Send(connectionId, new MeshRetireMessage { cellId = id, incarnation = incarnation, pair = BroadcastPair, reason = reason });
            Line(true, $"Layout hello from {id} incarnation {incarnation} ({reason}) answered with Retire");
            return true;
        }

        /// <summary>
        /// SupervisionAnswersHello runs on a bound hello before the operation's own hook: a restart's new incarnation is
        /// back; an operation past its e2 broadcast rebinds a member's new incarnation (before anything is re-answered) or
        /// resumes on a blip; a split's staged e2 is aborted by a member's new incarnation. True when it answered the hello.
        /// </summary>
        private bool SupervisionAnswersHello(long connectionId, MeshCellBinding binding, string previousIncarnation)
        {
            float now = _clock.Now;
            string id = binding.cellId;
            bool fresh = previousIncarnation != null && !string.Equals(previousIncarnation, binding.incarnation, StringComparison.Ordinal);
            if (fresh && _supervised.TryGetValue(id, out MeshSupervisedCell cell))
            {
                if (cell.announced || cell.phase != MeshSupervisorPhase.Up)
                    Line(false, $"Supervisor: {id} back, incarnation {binding.incarnation}");
                MeshCellSupervisor.Back(cell, now);
            }
            MeshLayoutOperation op = _op;
            if (op == null || !op.InProgress || !op.IsMember(id))
                return false;
            string was = MemberIncarnation(op, id);
            if (was == null)
                return false;   // a split's child before its first hello binds it
            if (!string.Equals(binding.incarnation, was, StringComparison.Ordinal))
            {
                if (op.CommitBroadcast)
                {
                    if (op is MeshMergeOperation committed && SameId(id, committed.retiringId))
                        return false;   // the retiring cell is not supervised after e2: 2c's answer (the merge goes on)
                    Rebind(op, id, connectionId, binding, now);
                    return false;       // the broadcast answers it, to the rebound incarnation
                }
                if (op is MeshSplitOperation split && split.step == MeshSplitStep.E2Staged)
                {
                    // e2 was seen by the child alone: still undone (2b froze here).
                    bool child = SameId(id, split.childId);
                    End(split, true, $"{(child ? "child" : "parent")} {id} restarted: incarnation {was} -> {binding.incarnation}");
                    if (!child)
                        return false;
                    _transport.Send(connectionId, new MeshRetireMessage { cellId = id, incarnation = binding.incarnation, pair = BroadcastPair, reason = "aborted by " + split.opId });
                    return true;
                }
                if (op is MeshMergeOperation merge && merge.step == MeshMergeStep.E2Staged && SameId(id, merge.retiringId) && !merge.collapse)
                {
                    End(merge, true, $"{id} restarted: incarnation {was} -> {binding.incarnation}");
                    return false;
                }
                return false;           // earlier steps, and a merge's survivor while e2 is staged to it: the operation's own hook
            }
            if (op.pausedMembers.Remove(id))
            {
                SetMemberConnection(op, id, connectionId);
                OpLog(op, $"{id} back, op resumed");
                if (!op.Paused)
                    Resume(op, now);
            }
            return false;
        }

        private static string MemberIncarnation(MeshLayoutOperation op, string id)
        {
            if (op is MeshSplitOperation split)
                return SameId(id, split.parentId) ? split.parentIncarnation : split.childBound ? split.childIncarnation : null;
            if (op is MeshMergeOperation merge)
                return SameId(id, merge.survivorId) ? merge.survivorIncarnation : merge.retiringIncarnation;
            return null;
        }

        private static void SetMemberConnection(MeshLayoutOperation op, string id, long connectionId)
        {
            if (op is MeshSplitOperation split)
            {
                if (SameId(id, split.parentId))
                    split.parentConnection = connectionId;
                else
                    split.childConnection = connectionId;
            }
            else if (op is MeshMergeOperation merge)
            {
                if (SameId(id, merge.survivorId))
                    merge.survivorConnection = connectionId;
                else
                    merge.retiringConnection = connectionId;
            }
        }

        /// <summary>Rebind: a member's new incarnation after the e2 broadcast takes the operation's binding; the step's gate restarts.</summary>
        private void Rebind(MeshLayoutOperation op, string id, long connectionId, MeshCellBinding binding, float now)
        {
            if (op is MeshSplitOperation split)
            {
                if (SameId(id, split.parentId))
                    split.parentIncarnation = binding.incarnation;
                else
                    split.childIncarnation = binding.incarnation;
            }
            else if (op is MeshMergeOperation merge)
            {
                merge.survivorIncarnation = binding.incarnation;
                merge.e2AckedAt = -1f;
            }
            SetMemberConnection(op, id, connectionId);
            op.pausedMembers.Remove(id);
            op.gateRestartPending = true;
            OpLog(op, $"{id} rebound {binding.incarnation}, op resumed");
            if (!op.Paused)
                Resume(op, now);
        }

        /// <summary>Resume: every step timer moves by the paused time; a rebound member restarts the step's gate.</summary>
        private void Resume(MeshLayoutOperation op, float now)
        {
            if (op.pausedAt >= 0f)
            {
                float paused = Math.Max(0f, now - op.pausedAt);
                op.stepStartedAt += paused;
                if (op is MeshSplitOperation shifted && shifted.drainDeadline >= 0f)
                    shifted.drainDeadline += paused;
                if (op is MeshMergeOperation merge)
                {
                    if (merge.retireSentAt >= 0f)
                        merge.retireSentAt += paused;
                    if (merge.nextStraggleAt >= 0f)
                        merge.nextStraggleAt += paused;
                }
                op.pausedAt = -1f;
            }
            if (!op.gateRestartPending)
                return;
            op.gateRestartPending = false;
            StepStarted(op);
            if (op is MeshSplitOperation split)
            {
                split.drainInitial = -1;
                split.drainDeadline = -1f;
            }
        }

        // ---- the tick ------------------------------------------------------------------------------------------------

        /// <summary>SupervisionTick runs once a second before the operation's tick: its members' losses, then the supervisor.</summary>
        private void SupervisionTick()
        {
            if (Phase != MeshCoordinatorPhase.Running)
                return;
            float now = _clock.Now;
            OpMembersTick(now);
            if (SplitHost != null)
                SupervisorTick(now);
        }

        /// <summary>OpMembersTick: a member lost before the e2 broadcast aborts; after it, pauses; unregistered non-members drop out of the step.</summary>
        private void OpMembersTick(float now)
        {
            MeshLayoutOperation op = _op;
            if (op == null || !op.InProgress)
                return;
            foreach (string member in LossMembers(op))
            {
                MeshCellBinding binding = BindingOf(member);
                if (binding == null || binding.unregisteredAt < 0f || now - binding.unregisteredAt < MemberLostSeconds)
                    continue;
                if (!op.CommitBroadcast)
                {
                    AbortOnLoss(op, member);
                    return;
                }
                if (op.pausedMembers.Exists(m => SameId(m, member)))
                    continue;
                if (!op.Paused)
                    op.pausedAt = now;
                op.pausedMembers.Add(member);
                Line(true, $"{op.LinePrefix} {op.opId}: {member} lost, op paused");
            }
            op.participants.RemoveAll(p => !op.IsMember(p) && !IsRegistered(p));
        }

        /// <summary>The members whose loss counts: a split's parent and bound child; a merge's survivor, and its retiring cell while e2 is staged (not in a collapse).</summary>
        private static List<string> LossMembers(MeshLayoutOperation op)
        {
            List<string> members = new List<string>();
            if (op is MeshSplitOperation split)
            {
                members.Add(split.parentId);
                if (split.childBound)
                    members.Add(split.childId);
            }
            else if (op is MeshMergeOperation merge)
            {
                members.Add(merge.survivorId);
                if (merge.step == MeshMergeStep.E2Staged && !merge.collapse)
                    members.Add(merge.retiringId);
            }
            return members;
        }

        private void AbortOnLoss(MeshLayoutOperation op, string member)
        {
            string reason = member + " lost";
            if (op is MeshSplitOperation split)
            {
                Abort(split, reason, true);
                OpLog(split, $"channel {split.childId} removed");
            }
            else if (op is MeshMergeOperation merge)
                AbortMerge(merge, reason);
        }

        /// <summary>SupervisorTick: every watched cell is evaluated; queued collapses start when the op slot is free.</summary>
        private void SupervisorTick(float now)
        {
            MeshSupervisorSettings settings = SupervisorSettings;
            CollectWatched(_watched);
            foreach (MeshCell cell in _watched)
            {
                if (!_supervised.ContainsKey(cell.id))
                    _supervised[cell.id] = new MeshSupervisedCell { cellId = cell.id, baseId = cell.baseId, mapId = cell.mapId, isBase = !cell.isDynamic };
            }
            _supervisedPass.Clear();
            _supervisedPass.AddRange(_supervised.Values);
            foreach (MeshSupervisedCell record in _supervisedPass)
            {
                MeshCell cell = _world.GetCellById(record.cellId);
                bool watched = _watched.Exists(c => SameId(c.id, record.cellId));
                bool held = record.phase == MeshSupervisorPhase.CollapseQueued || record.phase == MeshSupervisorPhase.Collapsing || record.phase == MeshSupervisorPhase.LeftDown;
                if (cell == null || cell.state == MeshCellState.Retired || (!watched && !held))
                {
                    _supervised.Remove(record.cellId);
                    continue;
                }
                switch (record.phase)
                {
                    case MeshSupervisorPhase.CollapseQueued:
                        TryStartCollapse(record);
                        break;
                    case MeshSupervisorPhase.Collapsing:
                        if (!(_op is MeshMergeOperation merge && merge.collapse && SameId(merge.retiringId, record.cellId) && (merge.InProgress || merge.IsFrozen)))
                            record.phase = MeshSupervisorPhase.CollapseQueued;   // its merge aborted: queued again
                        break;
                    case MeshSupervisorPhase.LeftDown:
                        NoteCannotCollapse(record);
                        break;
                    default:
                        if (watched)
                            Evaluate(record, now, settings);
                        break;
                }
            }
        }

        /// <summary>The watched cells: Active and owning something, of a recovered Dynamic map's split family (decision 1).</summary>
        private void CollectWatched(List<MeshCell> into)
        {
            into.Clear();
            IReadOnlyList<MeshCell> cells = _world.Cells;
            for (int i = 0; i < cells.Count; ++i)
            {
                MeshCell cell = cells[i];
                if (cell.OnDynamicMap && cell.state == MeshCellState.Active && !cell.owned.IsEmpty
                    && OutcomeOf(cell.mapId).Answers() && !IsFamilyFrozen(cell.baseId) && _world.FamilyOf(cell.baseId).Count > 1)
                    into.Add(cell);
            }
        }

        private void Evaluate(MeshSupervisedCell record, float now, MeshSupervisorSettings settings)
        {
            MeshCellBinding binding = BindingOf(record.cellId);
            if (binding == null || binding.staticNeighbour)
                return;   // it never said hello to this coordinator
            for (int pass = 0; pass < 2; ++pass)
            {
                MeshSupervisorInput input = new MeshSupervisorInput
                {
                    now = now, registered = binding.unregisteredAt < 0f, keyPresent = IsRegistered(record.cellId),
                    lastReportAt = binding.lastReportAt, killed = binding.killedAt >= 0f,
                };
                MeshSupervisorDecision decision = MeshCellSupervisor.Evaluate(record, input, settings);
                switch (decision.verdict)
                {
                    case MeshSupervisorVerdict.Down:
                        OnTrouble(record, binding, false, decision.seconds, settings);
                        return;
                    case MeshSupervisorVerdict.Hung:
                        OnTrouble(record, binding, true, decision.seconds, settings);
                        return;
                    case MeshSupervisorVerdict.RestartNow:
                        DoRestart(record, settings);
                        return;
                    case MeshSupervisorVerdict.AttemptFailed:
                        OnFailed(record, decision.why, settings);
                        if (record.phase != MeshSupervisorPhase.Up)
                            return;
                        continue;   // the next attempt may follow at once
                    default:
                        return;
                }
            }
        }

        /// <summary>
        /// OnTrouble: the line, then the down incarnation joins the fenced set and any process of the channel is killed
        /// through its spawner, then the restart (a hung one waits for its key to go). A base the kit restarts is left to it.
        /// </summary>
        private void OnTrouble(MeshSupervisedCell record, MeshCellBinding binding, bool hung, float seconds, MeshSupervisorSettings settings)
        {
            string id = record.cellId, what = $"{(hung ? "hung" : "down")} {Seconds(seconds)} s";
            if (record.isBase && SplitHost.KitRestartsBaseCells)
            {
                record.phase = MeshSupervisorPhase.LeftToKit;
                record.announced = true;
                Line(true, $"Supervisor: {id} {what}, left to the kit's restart");
                FenceBinding(id, binding);
                if (hung)
                    KillOrSay(id, binding, what);
                return;
            }
            Line(true, $"Supervisor: {id} {what}, restart {record.Attempt} of {Math.Max(1, settings.maxRestarts)} requested");
            FenceBinding(id, binding);
            MeshCellSupervisor.BeginAttempt(record, _clock.Now, hung);
            if (hung)
            {
                KillOrSay(id, binding, what);
                return;   // RestartNow once its key is gone
            }
            if (binding.processId > 0 && binding.killedAt < 0f)
                TryKill(id, binding, $"restart {record.Attempt}", false, out _);   // a late process never lives beside the new one
            DoRestart(record, settings);
        }

        private void DoRestart(MeshSupervisedCell record, MeshSupervisorSettings settings)
        {
            record.phase = MeshSupervisorPhase.Restarting;
            record.attemptAt = _clock.Now;
            if (!SplitHost.RequestRestart(record.cellId, record.baseId, record.mapId, out string error))
                OnFailed(record, "spawn error: " + error, settings);
        }

        /// <summary>OnRestartSpawnResult: the kit's spawn callback for a restart; only an error counts (success is the new incarnation's hello).</summary>
        public void OnRestartSpawnResult(string cellId, bool success, string detail)
        {
            if (success || Phase == MeshCoordinatorPhase.Stopped || !_supervised.TryGetValue(cellId ?? string.Empty, out MeshSupervisedCell record))
                return;
            if (record.phase == MeshSupervisorPhase.Restarting)
                OnFailed(record, "spawn failed: " + detail, SupervisorSettings);
        }

        private void OnFailed(MeshSupervisedCell record, string why, MeshSupervisorSettings settings)
        {
            bool waitedForHello = record.phase == MeshSupervisorPhase.Restarting && why.StartsWith("no hello", StringComparison.Ordinal);
            bool spent = MeshCellSupervisor.Failed(record, settings);
            Line(true, $"Supervisor: {record.cellId} restart {record.failures} failed ({why}{(waitedForHello ? "; no known process id, left to the kit" : string.Empty)})");
            if (!spent)
                return;
            if (record.isBase)
            {
                record.phase = MeshSupervisorPhase.LeftToKit;
                Line(true, $"Supervisor: {record.cellId} failed {record.failures} restart(s); a base cell is never collapsed, left to the kit");
                return;
            }
            QueueCollapse(record);
        }
    }
}
