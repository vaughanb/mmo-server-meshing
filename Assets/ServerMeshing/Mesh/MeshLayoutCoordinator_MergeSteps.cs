// Server Meshing add-on — the steps of the coordinator's merge (Slice 2c decisions 5 and 6): e1 broadcast; the e1 gate on
// the survivor's evidence; e2 staged to the survivor, then broadcast; the drain gate on the retiring cell's evidence;
// Retire sent (and resent) to the retiring cell; its exit (5 s unregistered) and its channel removed; e3 (it is
// Retired); the tidy when the survivor is whole again; done. Before e2 a silent participant aborts; from e2 on only acks
// can freeze: the drain and the retire straggle (logged every 30 s past retireMaxSeconds), they never time out.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace MultiplayerARPG.MMO.Mesh
{
    public sealed partial class MeshLayoutCoordinator
    {
        private bool TryMergeStep(MeshMergeOperation op)
        {
            switch (op.step)
            {
                case MeshMergeStep.E1Gate:
                    if (!ParticipantsAcked(op, op.e1Pair) || !SurvivorReady(op, out op.lastGateWait))
                        return false;
                    OpLog(op, "e1 gate open");
                    StageMergeE2(op);
                    return op.InProgress;
                case MeshMergeStep.E2Staged:
                    if (!AckedAtLeast(op.survivorId, op.e2Pair))
                        return false;
                    OpLog(op, "e2 acked");
                    // A cell touching the retiring one routes handoffs to it until it applies e2, band change or not.
                    List<string> banding = BandNeighboursOf(_broadcast, op.retiringId);
                    return BroadcastStep(op, op.staged, "e2", () => EnterStep(op, MeshMergeStep.DrainGate), banding);
                case MeshMergeStep.DrainGate:
                    return TryMergeDrain(op);
                case MeshMergeStep.Retiring:
                    if (!MeshMergeGates.Exited(op.retiringGoneSince, _clock.Now))
                        return false;
                    OpLog(op, $"{op.retiringId} exited");
                    RemoveRetiringChannel(op);
                    return BroadcastMergeE3(op);
                case MeshMergeStep.E3Broadcast:
                    if (!ParticipantsAcked(op, op.e3Pair))
                        return false;
                    MeshCell baseCell = _world.GetCellById(op.baseId);
                    if (baseCell != null && op.TidyApplies(_broadcast, baseCell.baseRect, out _))
                    {
                        MeshLayoutSnapshot tidy = op.Tidy(_broadcast, NextPair());
                        op.tidyPair = tidy.pair;
                        return BroadcastStep(op, tidy, "tidy", () => EnterStep(op, MeshMergeStep.TidyBroadcast), null);
                    }
                    FinishMerge(op);
                    return false;
                case MeshMergeStep.TidyBroadcast:
                    if (!ParticipantsAcked(op, op.tidyPair))
                        return false;
                    FinishMerge(op);
                    return false;
                default:
                    return false;
            }
        }

        /// <summary>
        /// TryMergeDrain: the retiring cell exiting first goes straight to e3 (the survivor owns everything already); else,
        /// once every participant acknowledged e2, a drained window of the retiring cell received a load window later opens
        /// the gate and Retire is sent.
        /// </summary>
        private bool TryMergeDrain(MeshMergeOperation op)
        {
            if (MeshMergeGates.Exited(op.retiringGoneSince, _clock.Now))
            {
                op.exitedBeforeDrained = true;
                Line(true, $"Merge {op.opId}: {op.retiringId} exited before drained");
                RemoveRetiringChannel(op);
                return BroadcastMergeE3(op);
            }
            if (op.e2AckedAt < 0f)
            {
                if (!ParticipantsAcked(op, op.e2Pair))
                    return false;
                op.e2AckedAt = _clock.Now;
            }
            MeshCellBinding retiring = BindingOf(op.retiringId);
            if (retiring == null || !retiring.hasEvidence)
            {
                op.lastGateWait = $"no drain evidence from {op.retiringId} at or after {op.e2Pair}";
                return false;
            }
            if (!MeshMergeGates.Drained(retiring.evidence, op.e2Pair, retiring.layoutEvidenceAt, op.e2AckedAt, out op.lastGateWait))
                return false;
            OpLog(op, "drain gate open");
            EnterStep(op, MeshMergeStep.Retiring);
            op.nextStraggleAt = -1f;
            SaveStore(op, _broadcast, null, null);
            SendRetire(op);
            OpLog(op, "retire sent");
            return true;
        }

        /// <summary>StageMergeE2 sends e2 to the survivor alone: it owns the union before the retiring cell lets go of anything.</summary>
        private void StageMergeE2(MeshMergeOperation op)
        {
            MeshLayoutSnapshot e2 = op.E2(_broadcast, NextPair());
            op.e2Pair = e2.pair;
            if (!SaveStore(op, _broadcast, e2, op.StepId("e2")))
            {
                AbortMerge(op, "the coordinator store could not be written before e2");
                return;
            }
            op.staged = e2;
            op.stagedStepName = "e2";
            EnterStep(op, MeshMergeStep.E2Staged);
            _transport.Send(op.survivorConnection, new MeshLayoutMessage
            {
                kind = MeshLayoutKind.Staged, targetCellId = op.survivorId, targetIncarnation = op.survivorIncarnation,
                stepId = op.StepId("e2"), snapshot = e2.Clone(),
            });
            OpLog(op, "e2 staged");
        }

        private bool BroadcastMergeE3(MeshMergeOperation op)
        {
            MeshLayoutSnapshot e3 = op.E3(_broadcast, NextPair());
            op.e3Pair = e3.pair;
            return BroadcastStep(op, e3, "e3", () => EnterStep(op, MeshMergeStep.E3Broadcast), null);
        }

        private void RemoveRetiringChannel(MeshMergeOperation op)
        {
            SplitHost?.RemoveChannel(op.retiringId);
            OpLog(op, $"channel {op.retiringId} removed");
        }

        private void FinishMerge(MeshMergeOperation op)
        {
            op.step = MeshMergeStep.Done;
            _cooldownUntil[op.baseId] = _clock.Now + Math.Max(0f, Settings.splitCooldownSeconds);
            SaveStore(op, _broadcast, null, null);
            OpLog(op, "done");
        }

        /// <summary>SendRetire sends Retire to the retiring cell's bound connection while it is registered (decision 5).</summary>
        private void SendRetire(MeshMergeOperation op)
        {
            op.retireSentAt = _clock.Now;
            MeshCellBinding retiring = BindingOf(op.retiringId);
            if (retiring == null || !IsRegistered(op.retiringId))
                return;
            _transport.Send(retiring.connectionId, new MeshRetireMessage
            {
                cellId = op.retiringId, incarnation = retiring.incarnation ?? string.Empty, pair = BroadcastPair, reason = "merged into " + op.survivorId,
            });
        }

        /// <summary>SurvivorReady: the e1 gate on the survivor's evidence (no saturation or p95 clause).</summary>
        private bool SurvivorReady(MeshMergeOperation op, out string why)
        {
            MeshCellBinding survivor = BindingOf(op.survivorId);
            if (survivor == null || !survivor.hasEvidence || survivor.incarnation != op.survivorIncarnation)
            {
                why = $"no evidence from {op.survivorId}";
                return false;
            }
            List<string> neighbours = new List<string>();
            foreach (string id in op.prewarmNeighbours)
            {
                if (IsRegistered(id))
                    neighbours.Add(id);
            }
            return MeshMergeGates.SurvivorReady(survivor.evidence, op.e1Pair, neighbours, Settings.prewarmSeconds, out why);
        }

        /// <summary>
        /// MergeTick: whether the retiring cell is registered (its exit is 5 s gone without a break; gone before e2 aborts),
        /// the gates, then the timers: silence aborts before e2 and freezes on the e2, e3 and tidy acks; the drain and the
        /// retire only straggle; Retire is resent every 10 s.
        /// </summary>
        private void MergeTick(MeshMergeOperation op)
        {
            if (!op.InProgress)
                return;
            float now = _clock.Now;
            bool present = IsRegistered(op.retiringId);
            if (present)
                op.retiringGoneSince = -1f;
            else if (op.retiringGoneSince < 0f)
                op.retiringGoneSince = now;
            if (!present && !op.PastCommit)
            {
                FailOp(op, $"{op.retiringId} unregistered");
                return;
            }
            Advance();
            if (!op.InProgress)
                return;
            float timeout = Math.Max(1f, Settings.splitStepTimeoutSeconds);
            string seconds = MeshLayoutOperation.Number(timeout);
            bool silent = now - op.stepStartedAt >= timeout;
            switch (op.step)
            {
                case MeshMergeStep.E1Gate:
                    if (silent)
                        FailOp(op, $"e1 gate not open in {seconds} s: {op.lastGateWait}");
                    break;
                case MeshMergeStep.E2Staged:
                    if (silent)
                        FailOp(op, $"no e2 ack from {op.survivorId} in {seconds} s");
                    break;
                case MeshMergeStep.DrainGate:
                    if (op.e2AckedAt < 0f)
                    {
                        if (silent)
                            FailOp(op, $"e2 not acknowledged in {seconds} s: {op.lastGateWait}");
                        break;
                    }
                    Straggle(op, now, true);
                    break;
                case MeshMergeStep.Retiring:
                    if (present && now - op.retireSentAt >= MeshMergeGates.RetireResendSeconds)
                        SendRetire(op);
                    Straggle(op, now, false);
                    break;
                case MeshMergeStep.E3Broadcast:
                case MeshMergeStep.TidyBroadcast:
                    if (silent)
                        FailOp(op, $"{op.CurrentStepName} not acknowledged in {seconds} s: {op.lastGateWait}");
                    break;
            }
        }

        /// <summary>Straggle: past retireMaxSeconds in the drain or the retire step, a line every 30 s and an error note to the retiring cell.</summary>
        private void Straggle(MeshMergeOperation op, float now, bool drain)
        {
            float elapsed = now - op.stepStartedAt;
            if (elapsed < Math.Max(1f, Settings.retireMaxSeconds) || (op.nextStraggleAt >= 0f && now < op.nextStraggleAt))
                return;
            op.nextStraggleAt = now + MeshMergeGates.StraggleLineSeconds;
            string s = elapsed.ToString("0", CultureInfo.InvariantCulture);
            MeshCellBinding retiring = BindingOf(op.retiringId);
            string text;
            if (drain)
            {
                string counts = retiring != null && retiring.hasEvidence && retiring.evidence.pair.CompareTo(op.e2Pair) >= 0
                    ? retiring.evidence.drainState.Describe() : "no drain evidence";
                Line(true, $"Merge {op.opId}: drain straggling {s} s: {counts}");
                text = $"{op.opId}: {op.retiringId} still holds {counts} {s} s after e2; nobody is kicked";
            }
            else
            {
                Line(true, $"Merge {op.opId}: retire straggling {s} s");
                text = $"{op.opId}: {op.retiringId} has not exited {s} s after its Retire";
            }
            if (retiring != null && IsRegistered(op.retiringId))
                Note(retiring.connectionId, MeshNoteLevel.Error, op.retiringId, "retire-straggler", text);
        }
    }
}
