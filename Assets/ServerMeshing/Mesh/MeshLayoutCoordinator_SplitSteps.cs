// Server Meshing add-on — the steps of the coordinator's split (Slice 2b decisions 3, 4, 6, 8; plan D4): e1 staged to
// the child, then broadcast; the e1 (pre-warm) gate; e2 staged, then broadcast; the drain gate; e3; done. The store is
// written before every step, the central applies each broadcast to its own registry, and a step whose participants stay
// silent past splitStepTimeoutSeconds aborts the operation before e2 and freezes it after. The step plumbing here
// (Advance, BroadcastStep, SaveStore, participants and acks, the tick) serves merges too (Slice 2c decision 4).
using System;
using System.Collections.Generic;

namespace MultiplayerARPG.MMO.Mesh
{
    public sealed partial class MeshLayoutCoordinator
    {
        private readonly List<string> _silent = new List<string>();

        /// <summary>Advance takes every step whose gate is open now (several in one call when acks are already in); none while paused (Slice 2d).</summary>
        private void Advance()
        {
            MeshLayoutOperation op = _op;
            if (op != null && op.Paused)
                return;
            for (int guard = 0; guard < 8 && op != null && op.InProgress; ++guard)
            {
                bool stepped = op is MeshSplitOperation s ? TryStep(s) : op is MeshMergeOperation m && TryMergeStep(m);
                if (!stepped)
                    break;
            }
            if (op is MeshSplitOperation split && split.step == MeshSplitStep.Done)
                TrySettleStragglers(split);
        }

        /// <summary>
        /// After a drain timeout e3 left the parent's interest over R: once the parent reports nothing left there, a new
        /// version shrinks it to what it owns (only while no other operation runs, and only if the parent's cut is unchanged).
        /// </summary>
        private void TrySettleStragglers(MeshSplitOperation op)
        {
            if (op.stragglers <= 0 || op.settled)
                return;
            MeshCellBinding parent = BindingOf(op.parentId);
            if (parent == null || !parent.hasEvidence || !MeshSplitGates.Drained(parent.evidence, op.e3Pair, out _))
                return;
            MeshLayoutEntry entry = _broadcast.entries.Find(e => SameId(e.id, op.parentId));
            if (entry == null || !entry.owned.Equals(op.kept) || !entry.interest.Equals(op.parentInterest))
            {
                op.settled = true;   // a later layout already changed the parent: nothing of this split is left to settle
                return;
            }
            MeshLayoutSnapshot settle = op.E3(_broadcast, NextPair(), drained: true);
            string reason = null;
            if (!SaveStore(op, settle, null, null) || !ApplyOnCentral(settle, out reason))
            {
                Line(true, $"Split {op.opId}: the settle version could not be broadcast: {(reason ?? _store.LastError)}");
                op.settled = true;
                return;
            }
            op.settled = true;
            op.stragglers = 0;
            SendBroadcast(settle, op.StepId("settle"));
            OpLog(op, $"stragglers gone, settle broadcast {settle.pair}");
        }

        private bool TryStep(MeshSplitOperation op)
        {
            switch (op.step)
            {
                case MeshSplitStep.E1Staged:
                    if (!AckedAtLeast(op.childId, op.e1Pair))
                        return false;
                    OpLog(op, "e1 acked");
                    if (!BroadcastStep(op, op.staged, "e1", MeshSplitStep.E1Gate))
                        return false;
                    op.e1Broadcast = true;
                    op.prewarmNeighbours.Clear();
                    op.prewarmNeighbours.AddRange(BandNeighboursOf(_broadcast, op.childId));
                    return true;
                case MeshSplitStep.E1Gate:
                    if (!ParticipantsAcked(op, op.e1Pair) || !PrewarmReady(op, out op.lastGateWait))
                        return false;
                    OpLog(op, "e1 gate open");
                    StageE2(op);
                    return op.InProgress;
                case MeshSplitStep.E2Staged:
                    if (!AckedAtLeast(op.childId, op.e2Pair))
                        return false;
                    OpLog(op, "e2 acked");
                    return BroadcastStep(op, op.staged, "e2", MeshSplitStep.DrainGate);
                case MeshSplitStep.DrainGate:
                    return TryDrain(op);
                case MeshSplitStep.E3Broadcast:
                    if (!ParticipantsAcked(op, op.e3Pair))
                        return false;
                    op.step = MeshSplitStep.Done;
                    _cooldownUntil[op.baseId] = _clock.Now + Math.Max(0f, Settings.splitCooldownSeconds);
                    SaveStore(op, _broadcast, null, null);
                    OpLog(op, "done");
                    return false;
                default:
                    return false;
            }
        }

        private bool TryDrain(MeshSplitOperation op)
        {
            if (!ParticipantsAcked(op, op.e2Pair))
                return false;
            MeshCellBinding parent = BindingOf(op.parentId);
            bool drained = false;
            if (parent != null && parent.hasEvidence && MeshSplitGates.DrainWindowQualifies(parent.evidence, op.e2Pair))
            {
                op.stragglers = parent.evidence.drain.Total;
                if (op.drainInitial < 0)
                {
                    op.drainInitial = op.stragglers;
                    op.drainDeadline = op.stepStartedAt + MeshSplitGates.DrainTimeoutSeconds(op.drainInitial, Settings.drainRatePerSecond, Settings.handoffAckGuardSeconds);
                }
                drained = MeshSplitGates.Drained(parent.evidence, op.e2Pair, out op.lastGateWait);
            }
            else
                op.lastGateWait = $"no drain evidence from {op.parentId} at or after {op.e2Pair}";
            if (drained)
                OpLog(op, "drain gate open");
            else if (op.drainDeadline >= 0f && _clock.Now >= op.drainDeadline)
                Line(true, $"Split {op.opId}: drain timeout, {op.stragglers} straggler(s)");
            else
                return false;
            MeshLayoutSnapshot e3 = op.E3(_broadcast, NextPair(), drained);
            op.e3Pair = e3.pair;
            return BroadcastStep(op, e3, "e3", MeshSplitStep.E3Broadcast);
        }

        /// <summary>StageE1 sends e1 to the child alone, as the answer to its hello (so its gate opens on it).</summary>
        private void StageE1(MeshSplitOperation op)
        {
            MeshLayoutSnapshot e1 = op.E1(_broadcast, NextPair());
            op.e1Pair = e1.pair;
            if (!SaveStore(op, _broadcast, e1, op.StepId("e1")))
            {
                Abort(op, "the coordinator store could not be written before e1");
                return;
            }
            op.staged = e1;
            op.stagedStepName = "e1";
            EnterStep(op, MeshSplitStep.E1Staged);
            OpLog(op, "e1 staged");
        }

        /// <summary>StageE2 sends e2 to the child alone: it owns R before anyone hands it anything.</summary>
        private void StageE2(MeshSplitOperation op)
        {
            MeshLayoutSnapshot e2 = op.E2(_broadcast, NextPair());
            op.e2Pair = e2.pair;
            if (!SaveStore(op, _broadcast, e2, op.StepId("e2")))
            {
                Abort(op, "the coordinator store could not be written before e2");
                return;
            }
            op.staged = e2;
            op.stagedStepName = "e2";
            EnterStep(op, MeshSplitStep.E2Staged);
            _transport.Send(op.childConnection, new MeshLayoutMessage
            {
                kind = MeshLayoutKind.Staged, targetCellId = op.childId, targetIncarnation = op.childIncarnation,
                stepId = op.StepId("e2"), snapshot = e2.Clone(),
            });
            OpLog(op, "e2 staged");
        }

        /// <summary>BroadcastStep writes the store, applies the step on the central, sends it to every map server and waits for its participants.</summary>
        private bool BroadcastStep(MeshSplitOperation op, MeshLayoutSnapshot snapshot, string name, MeshSplitStep next)
        {
            return BroadcastStep(op, snapshot, name, () => EnterStep(op, next), null);
        }

        /// <summary>
        /// BroadcastStep for any operation: <paramref name="enter"/> moves it to its next step; <paramref name="extra"/> are
        /// cells that take part although the step does not change them (a merge's e2: every cell banding with the retiring one).
        /// </summary>
        private bool BroadcastStep(MeshLayoutOperation op, MeshLayoutSnapshot snapshot, string name, Action enter, List<string> extra)
        {
            List<string> participants = Participants(op, _broadcast, snapshot, extra);
            if (!SaveStore(op, snapshot, null, null))
            {
                FailOp(op, $"the coordinator store could not be written before {name}");
                return false;
            }
            if (!ApplyOnCentral(snapshot, out string reason))
            {
                FailOp(op, $"the central could not apply {name} {snapshot.pair}: {reason}");
                return false;
            }
            op.staged = null;
            op.stagedStepName = "";
            op.participants.Clear();
            op.participants.AddRange(participants);
            enter();
            SendBroadcast(snapshot, op.StepId(name));
            OpLog(op, $"{name} broadcast {snapshot.pair}");
            return true;
        }

        private bool ApplyOnCentral(MeshLayoutSnapshot snapshot, out string reason)
        {
            if (!_world.ApplyLayout(snapshot, out reason))
                return false;
            _broadcast = snapshot.Clone();
            return true;
        }

        private void SendBroadcast(MeshLayoutSnapshot snapshot, string stepId)
        {
            _peers.Clear();
            _transport.GetMapServers(_peers);
            foreach (MeshMapServerPeer peer in _peers)
                _transport.Send(peer.connectionId, new MeshLayoutMessage { kind = MeshLayoutKind.Broadcast, targetCellId = string.Empty, targetIncarnation = string.Empty, stepId = stepId, snapshot = snapshot.Clone() });
        }

        private void EnterStep(MeshSplitOperation op, MeshSplitStep step)
        {
            op.step = step;
            StepStarted(op);
        }

        private void StepStarted(MeshLayoutOperation op)
        {
            op.stepStartedAt = _clock.Now;
            op.lastGateWait = "";
        }

        private MeshLayoutPair NextPair()
        {
            _lastVersion = Math.Max(_lastVersion, _broadcast.pair.version) + 1;
            return new MeshLayoutPair(Incarnation, _lastVersion);
        }

        private bool SaveStore(MeshLayoutOperation op, MeshLayoutSnapshot broadcast, MeshLayoutSnapshot staged, string stagedStepId)
        {
            MeshCoordinatorStoreData data = _storeData ?? new MeshCoordinatorStoreData { lastIncarnation = Incarnation };
            data.broadcast = broadcast.Clone();
            data.hasStaged = staged != null;
            data.staged = staged != null ? staged.Clone() : new MeshLayoutSnapshot();
            data.stagedStepId = stagedStepId ?? string.Empty;
            data.stagedFirstApplier = staged != null ? op.StagedTargetId : string.Empty;
            data.opId = op.InProgress ? op.opId : string.Empty;
            data.opStep = op.StepText;
            data.abortedIds = new List<string>(_abortedIds);
            data.fencedIncarnations = _fenced.ToStore();
            _storeData = data;
            if (_store.TrySave(data, out string error))
                return true;
            _log.Error(error);
            return false;
        }

        /// <summary>AckedAtLeast: the cell's bound, registered map server acknowledged this pair or a newer one.</summary>
        private bool AckedAtLeast(string cellId, MeshLayoutPair pair)
        {
            MeshCellBinding binding = BindingOf(cellId);
            return binding != null && binding.unregisteredAt < 0f && binding.applied.CompareTo(pair) >= 0;
        }

        private bool ParticipantsAcked(MeshLayoutOperation op, MeshLayoutPair pair)
        {
            _silent.Clear();
            foreach (string cell in op.participants)
            {
                if (!AckedAtLeast(cell, pair))
                    _silent.Add(cell);
            }
            if (_silent.Count == 0)
                return true;
            op.lastGateWait = $"no {op.CurrentStepName} ack from {string.Join(", ", _silent)}";
            return false;
        }

        private bool PrewarmReady(MeshSplitOperation op, out string why)
        {
            MeshCellBinding child = BindingOf(op.childId);
            if (child == null || !child.hasEvidence || child.connectionId != op.childConnection || child.incarnation != op.childIncarnation)
            {
                why = $"no evidence from {op.childId}";
                return false;
            }
            List<string> neighbours = new List<string>();
            foreach (string id in op.prewarmNeighbours)
            {
                if (IsRegistered(id))
                    neighbours.Add(id);
            }
            return MeshSplitGates.PrewarmReady(child.evidence, op.e1Pair, neighbours, Settings.prewarmSeconds, out why);
        }

        /// <summary>
        /// Participants of a broadcast step: the operation's own cells (the split's child and parent, the merge's two), the
        /// <paramref name="extra"/> ones, and every registered cell of any map whose rectangles, state or band neighbours the
        /// step changes (decision 4). Unregistered cells are left out (a merged-away cell that exited).
        /// </summary>
        private List<string> Participants(MeshLayoutOperation op, MeshLayoutSnapshot before, MeshLayoutSnapshot after, List<string> extra = null)
        {
            MeshLayoutWorld validation = ValidationWorld();
            Dictionary<string, (MeshLayoutValidator.LiveCell cell, string band)> was = Shapes(validation, before);
            Dictionary<string, (MeshLayoutValidator.LiveCell cell, string band)> now = Shapes(validation, after);
            List<string> participants = new List<string>(op.SeedParticipants);
            if (extra != null)
            {
                foreach (string id in extra)
                {
                    if (!participants.Exists(p => SameId(p, id)))
                        participants.Add(id);
                }
            }
            foreach (KeyValuePair<string, (MeshLayoutValidator.LiveCell cell, string band)> shape in now)
            {
                bool changed = !was.TryGetValue(shape.Key, out (MeshLayoutValidator.LiveCell cell, string band) old)
                    || old.band != shape.Value.band || old.cell.state != shape.Value.cell.state
                    || !old.cell.owned.Equals(shape.Value.cell.owned) || !old.cell.interest.Equals(shape.Value.cell.interest);
                if (changed && !participants.Exists(p => SameId(p, shape.Key)))
                    participants.Add(shape.Key);
            }
            participants.RemoveAll(id => !IsRegistered(id));
            return participants;
        }

        private static Dictionary<string, (MeshLayoutValidator.LiveCell, string)> Shapes(MeshLayoutWorld validation, MeshLayoutSnapshot snapshot)
        {
            List<MeshLayoutValidator.LiveCell> live = MeshLayoutValidator.LiveWorld(validation, snapshot);
            Dictionary<string, (MeshLayoutValidator.LiveCell, string)> shapes = new Dictionary<string, (MeshLayoutValidator.LiveCell, string)>(StringComparer.OrdinalIgnoreCase);
            foreach (MeshLayoutValidator.LiveCell cell in live)
                shapes[cell.id] = (cell, string.Join(",", BandOf(validation, live, cell)));
            return shapes;
        }

        private static List<string> BandOf(MeshLayoutWorld validation, List<MeshLayoutValidator.LiveCell> live, MeshLayoutValidator.LiveCell cell)
        {
            List<string> band = new List<string>();
            foreach (MeshLayoutValidator.LiveCell other in live)
            {
                if (!ReferenceEquals(other, cell) && MeshLayoutValidator.BandNear(cell, other, validation.bandWidth, validation.touchTolerance))
                    band.Add(other.id);
            }
            band.Sort(StringComparer.OrdinalIgnoreCase);
            return band;
        }

        /// <summary>BandNeighboursOf: the cells that stream to a cell under a snapshot (the child's pre-warm list).</summary>
        private List<string> BandNeighboursOf(MeshLayoutSnapshot snapshot, string cellId)
        {
            MeshLayoutWorld validation = ValidationWorld();
            List<MeshLayoutValidator.LiveCell> live = MeshLayoutValidator.LiveWorld(validation, snapshot);
            MeshLayoutValidator.LiveCell cell = live.Find(c => SameId(c.id, cellId));
            return cell != null ? BandOf(validation, live, cell) : new List<string>();
        }

        /// <summary>OpTick: the silence timers of the current step, and gates that opened without a new message (none while paused, Slice 2d).</summary>
        private void OpTick()
        {
            MeshLayoutOperation op = _op;
            if (op == null || Phase != MeshCoordinatorPhase.Running || op.Paused)
                return;
            if (op is MeshMergeOperation merge)
            {
                MergeTick(merge);
                return;
            }
            Advance();
            if (op is MeshSplitOperation split)
                SplitTimers(split);
        }

        /// <summary>SplitTimers: a split step silent past splitStepTimeoutSeconds aborts before e2 and freezes after.</summary>
        private void SplitTimers(MeshSplitOperation op)
        {
            if (!op.InProgress || _clock.Now - op.stepStartedAt < Math.Max(1f, Settings.splitStepTimeoutSeconds))
                return;
            float timeout = Math.Max(1f, Settings.splitStepTimeoutSeconds);
            string seconds = MeshSplitOperation.Number(timeout);
            switch (op.step)
            {
                case MeshSplitStep.ChannelAdded:
                case MeshSplitStep.Spawning:
                    FailOp(op, $"no hello from {op.childId} in {seconds} s");
                    break;
                case MeshSplitStep.E1Staged:
                case MeshSplitStep.E2Staged:
                    FailOp(op, $"no {op.CurrentStepName} ack from {op.childId} in {seconds} s");
                    break;
                case MeshSplitStep.E1Gate:
                    FailOp(op, $"e1 gate not open in {seconds} s: {op.lastGateWait}");
                    break;
                case MeshSplitStep.DrainGate:
                    // The drain has its own deadline once the parent reported; silence before that, or missing acks, freeze.
                    if (op.drainDeadline < 0f || !ParticipantsAcked(op, op.e2Pair))
                        FailOp(op, $"drain gate not open in {seconds} s: {op.lastGateWait}");
                    break;
                case MeshSplitStep.E3Broadcast:
                    FailOp(op, $"e3 not acknowledged in {seconds} s: {op.lastGateWait}");
                    break;
            }
        }
    }
}
