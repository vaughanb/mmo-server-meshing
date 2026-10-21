// Server Meshing add-on — the coordinator's manual split (Slice 2b, slices/slice2b_design.md decisions 2-5c, plan D3/D4)
// and what every layout operation shares (Slice 2c decision 4): the request and its validation (the common guards in
// CheckOpCommon), the hooks the hello, ack, evidence and spawn handlers call, and the abort and freeze paths. The steps
// are in MeshLayoutCoordinator_SplitSteps.cs; merges in MeshLayoutCoordinator_Merge*.cs. One operation runs world-wide,
// so there is one staged step at most. Every line an operator or the harness reads starts "Split <op>: ",
// "Merge <op>: " or "Layout op refused: ".
using System;
using System.Collections.Generic;

namespace MultiplayerARPG.MMO.Mesh
{
    public sealed partial class MeshLayoutCoordinator
    {
        private MeshLayoutOperation _op;
        private int _opCounter;
        private long _lastVersion;
        private readonly HashSet<string> _abortedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, float> _cooldownUntil = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        /// <summary>What a split needs from the central around the coordinator (channels, spawns); none = splits refused.</summary>
        public IMeshSplitHost SplitHost { get; set; }

        /// <summary>OpLine is raised with every operation line ("Split ...", "Merge ..." and "Layout op refused: ..."), after it is logged.</summary>
        public event Action<string> OpLine;

        /// <summary>The operation in progress, or the last one (Done, Aborted or Frozen); null before the first.</summary>
        public MeshLayoutOperation CurrentOp => _op;

        /// <summary>CurrentOp when it is a split (else null).</summary>
        public MeshSplitOperation CurrentSplit => _op as MeshSplitOperation;

        /// <summary>CurrentOp when it is a merge (else null).</summary>
        public MeshMergeOperation CurrentMerge => _op as MeshMergeOperation;

        /// <summary>Whether a dynamic id was aborted by an operation of this coordinator (its hello is answered with Retire).</summary>
        public bool IsAborted(string cellId) => !string.IsNullOrEmpty(cellId) && _abortedIds.Contains(cellId);

        /// <summary>IsSplitFamily: the base cell's family is cut, or an operation on it runs or froze, so it is never slept or started by the awake set.</summary>
        public bool IsSplitFamily(string baseId)
        {
            if (string.IsNullOrEmpty(baseId))
                return false;
            if (_op != null && _op.HoldsFamily && string.Equals(_op.baseId, baseId, StringComparison.OrdinalIgnoreCase))
                return true;
            return _world.FamilyOf(baseId).Count > 1;
        }

        /// <summary>
        /// IsOperationChild: the id is the dynamic cell of the operation in progress — a split's child (known before any
        /// layout names it) or a merge's retiring cell until e3 retires it.
        /// </summary>
        public bool IsOperationChild(string cellId)
        {
            if (_op == null || !_op.InProgress)
                return false;
            if (_op is MeshSplitOperation split)
                return SameId(split.childId, cellId);
            return _op is MeshMergeOperation merge && merge.step < MeshMergeStep.E3Broadcast && SameId(merge.retiringId, cellId);
        }

        private MeshDynamicSettings Settings => _world.dynamicSettings ?? new MeshDynamicSettings();

        private static bool SameId(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// RequestSplit cuts <paramref name="cellId"/> (P) at <paramref name="at"/> across <paramref name="axis"/> (world
        /// frame); the child <paramref name="childId"/> (X) takes the side above the line when
        /// <paramref name="childOnPositiveSide"/>. False with the broken rule (also logged "Layout op refused: rule").
        /// </summary>
        public bool RequestSplit(string cellId, MeshSplitAxis axis, float at, bool childOnPositiveSide, string childId, out string error)
        {
            error = CheckSplit(cellId, axis, at, childOnPositiveSide, childId, out MeshSplitOperation op);
            if (error != null)
            {
                Refuse(error);
                return false;
            }
            _op = op;
            _abortedIds.Remove(op.childId);
            OpLog(op, $"requested {op.parentId} {op.AxisText} {MeshSplitOperation.Number(at)} {op.SideText} child {op.childId}");
            if (!SaveStore(op, _broadcast, null, null))
            {
                Abort(op, "the coordinator store could not be written");
                return true;
            }
            if (!SplitHost.AddChannel(op.childId, out string channelError))
            {
                Abort(op, "channel add failed: " + channelError);
                return true;
            }
            EnterStep(op, MeshSplitStep.ChannelAdded);
            OpLog(op, "channel added");
            if (!SplitHost.RequestSpawn(op.opId, op.baseId, op.childId, op.mapId, out string spawnError))
            {
                Abort(op, "spawn request failed: " + spawnError);
                return true;
            }
            EnterStep(op, MeshSplitStep.Spawning);
            OpLog(op, "spawning");
            return true;
        }

        /// <summary>
        /// CheckOpCommon: the guards every operation on <paramref name="cellId"/> must pass (Slice 2c decision 3) — the
        /// coordinator running and its store writable, no operation in progress or frozen world-wide, a host, the cell known,
        /// not Retired, on a recovered Dynamic map, Active and owning something, bound and answered, its family out of cooldown
        /// (a collapse ignores the cooldown, Slice 2d decision 3).
        /// </summary>
        private string CheckOpCommon(string cellId, out MeshCell cell, out MeshCellBinding binding, bool ignoreCooldown = false)
        {
            cell = null;
            binding = null;
            if (Phase == MeshCoordinatorPhase.Stopped)
                return "the layout coordinator is not running";
            if (Phase != MeshCoordinatorPhase.Running)
                return "the layout coordinator is still recovering";
            if (!_store.Writable)
                return "the coordinator store is not writable";
            if (_op != null && _op.InProgress)
                return $"{_op.opId} is in progress";
            if (_op != null && _op.IsFrozen)
                return $"{_op.opId} is frozen: {_op.endReason}";
            if (SplitHost == null)
                return "this central has no split host";
            cell = string.IsNullOrEmpty(cellId) ? null : _world.GetCellById(cellId);
            if (cell == null || cell.state == MeshCellState.Retired)
                return $"cell {cellId} is not in the layout";
            if (!cell.OnDynamicMap)
                return $"{cell.id} is on map {cell.mapId}, which is Static";
            if (!OutcomeOf(cell.mapId).Answers())
                return $"map {cell.mapId} is {OutcomeOf(cell.mapId)}";
            if (FrozenFamilyReason(cell.baseId) is string frozen)
                return $"family {cell.baseId} is frozen: {frozen}";   // Slice 5 decision 3
            if (StoredOpWaitRefusal() is string waits)
                return waits;   // unfreeze_design.md decision 9: a stored op waiting on a frozen family holds the slot
            if (cell.state != MeshCellState.Active || cell.owned.IsEmpty)
                return $"{cell.id} is {cell.state}";
            binding = BindingOf(cell.id);
            if (binding == null || !binding.answered || binding.unregisteredAt >= 0f || binding.staticNeighbour)
                return $"{cell.id} is not running with a layout answer";
            if (!ignoreCooldown && _cooldownUntil.TryGetValue(cell.baseId, out float until) && until > _clock.Now)
                return $"cooldown {until - _clock.Now:0} s left on {cell.baseId}";
            return null;
        }

        private string CheckSplit(string cellId, MeshSplitAxis axis, float at, bool positive, string childId, out MeshSplitOperation op)
        {
            op = null;
            string common = CheckOpCommon(cellId, out MeshCell cell, out MeshCellBinding parent);
            if (common != null)
                return common;
            if (!MeshLayoutValidator.IsSafeCellId(childId, out string unsafeId))
                return unsafeId;
            MeshCell existing = _world.GetCellById(childId);
            if (existing != null && existing.state != MeshCellState.Retired)
                return $"cell {childId} already exists";
            if (IsRegistered(childId))
                return $"a map server of {childId} is still registered";
            string reuse = IdReuseRefusal(childId);
            if (reuse != null)
                return reuse;
            if (!MeshSplitOperation.Cut(cell.owned, axis, at, positive, out MeshRect given, out MeshRect kept))
                return $"the {(axis == MeshSplitAxis.X ? "x" : "z")} {MeshSplitOperation.Number(at)} line does not cross {cell.id}, which owns {cell.owned}";
            op = new MeshSplitOperation("split-" + (_opCounter + 1))
            {
                parentId = cell.id, mapId = cell.mapId, baseId = cell.baseId, childId = childId,
                axis = axis, at = at, childOnPositiveSide = positive,
                parentOwned = cell.owned, parentInterest = cell.interest, given = given, kept = kept,
                parentConnection = parent.connectionId, parentIncarnation = parent.incarnation,
                requestedAt = _clock.Now, stepStartedAt = _clock.Now,
            };
            // Judged as the steps will be: e1 against the broadcast, e2 against e1 (rules 7 and maxCellsPerBase bind here).
            MeshLayoutWorld validation = ValidationWorld();
            MeshLayoutSnapshot e1 = op.E1(_broadcast, new MeshLayoutPair(Incarnation, _broadcast.pair.version + 1));
            MeshLayoutSnapshot e2 = op.E2(e1, new MeshLayoutPair(Incarnation, _broadcast.pair.version + 2));
            if (!MeshLayoutValidator.Validate(validation, e1, _broadcast, out string reason) || !MeshLayoutValidator.Validate(validation, e2, e1, out reason))
            {
                op = null;
                return reason;
            }
            if (!SplitHost.CanSpawn(cell.baseId, out string spawnError))
            {
                op = null;
                return spawnError;
            }
            _opCounter++;
            return null;
        }

        /// <summary>ValidationWorld is the registry's validation world plus each Dynamic map's maxCellsPerBase.</summary>
        private MeshLayoutWorld ValidationWorld()
        {
            MeshLayoutWorld validation = _world.ValidationWorld();
            foreach (MeshMapLayout layout in _world.maps)
            {
                if (layout != null && layout.IsDynamic && !string.IsNullOrEmpty(layout.mapId))
                    validation.maxCellsPerBaseByMap[layout.mapId] = layout.maxCellsPerBase > 0 ? layout.maxCellsPerBase : Settings.defaultMaxCellsPerBase;
            }
            return validation;
        }

        private bool IsRegistered(string cellId)
        {
            _peers.Clear();
            _transport.GetMapServers(_peers);
            foreach (MeshMapServerPeer peer in _peers)
            {
                if (SameId(peer.cellId, cellId))
                    return true;
            }
            return false;
        }

        /// <summary>OnSpawnResult: the kit's spawn callback for an operation's child; only its first call counts, and none after the operation ended.</summary>
        public void OnSpawnResult(string opId, bool success, string detail)
        {
            MeshSplitOperation op = _op as MeshSplitOperation;
            if (op == null || op.opId != opId || !op.InProgress)
            {
                Line(false, $"Split {opId}: spawn callback {detail} after the operation ended; ignored");
                return;
            }
            if (op.spawnAnswered)
                return;
            op.spawnAnswered = true;
            if (!success)
                FailOp(op, "spawn failed: " + detail);
        }

        /// <summary>OnLayoutEvidence: a bound cell's split-gate evidence (only its bound connection and incarnation count).</summary>
        public void OnLayoutEvidence(long connectionId, MeshLayoutEvidenceMessage evidence)
        {
            MeshCellBinding binding = BindingOf(evidence.cellId);
            if (Phase == MeshCoordinatorPhase.Stopped || binding == null || binding.connectionId != connectionId || binding.incarnation != (evidence.incarnation ?? string.Empty))
                return;
            binding.evidence = evidence;
            binding.hasEvidence = true;
            binding.lastEvidenceAt = _clock.Now;
            binding.layoutEvidenceAt = _clock.Now;
            Advance();
        }

        /// <summary>
        /// HelloGetsRetire answers a dynamic id this coordinator does not run with Retire: one an operation aborted, or one
        /// in neither the broadcast nor the staged layout (decision 5). A hello holding another coordinator's layout goes
        /// to recovery instead.
        /// </summary>
        private bool HelloGetsRetire(long connectionId, MeshLayoutHelloMessage hello)
        {
            string id = hello.cellId;
            bool aborted = _abortedIds.Contains(id);
            if (!aborted)
            {
                MeshCell cell = _world.GetCellById(id);
                if (cell != null && !cell.isDynamic)
                    return false;   // an asset cell
                if (cell != null && cell.state != MeshCellState.Retired)
                    return false;   // a live child in the broadcast
                if (IsOperationChild(id))
                    return false;
                if (RecoveryNames(id))
                    return false;   // Slice 5 decision 5: named for a recovering map, or the stored operation's cell
                if (MeshRecoveryResolver.HeldReopensRecovery(HeldEntriesOf(hello), HeldPair(hello), Incarnation, _adoptedPair, _ownBroadcastSent))
                    return false;
            }
            // Only a split aborts an id (a merge's abort retires nothing): the split in hand, or an earlier one.
            MeshSplitOperation aborter = _op is MeshSplitOperation split && SameId(split.childId, id) ? split : null;
            // Slice 5 decision 5: a cell whose own layout says it owns something is fenced (it saves, kicks and exits), not asked to retire.
            string reason = (HeldOwnsSomething(hello) ? "fenced: " : string.Empty) + (aborted ? "aborted by " + (aborter != null ? aborter.opId : "an earlier split") : "not in the layout");
            _transport.Send(connectionId, new MeshRetireMessage { cellId = id, incarnation = hello.incarnation ?? string.Empty, pair = BroadcastPair, reason = reason });
            Line(true, aborted && aborter != null
                ? $"{aborter.LinePrefix} {aborter.opId}: hello from aborted child {id} answered with Retire"
                : $"Layout hello from dynamic cell {id} ({reason}) answered with Retire");
            return true;
        }

        /// <summary>OpAnswersHello: the operation in progress answers a hello from one of its cells (true when it did).</summary>
        private bool OpAnswersHello(long connectionId, MeshLayoutHelloMessage hello, MeshCellBinding binding)
        {
            if (_op is MeshSplitOperation split)
                return SplitAnswersHello(connectionId, hello, binding, split);
            if (_op is MeshMergeOperation merge)
                return MergeAnswersHello(connectionId, hello, binding, merge);
            return false;
        }

        /// <summary>
        /// AnswerStaged: a hello from the staged target while its step is staged gets that staged step again, as the answer
        /// to its hello (so its gate opens on it).
        /// </summary>
        private bool AnswerStaged(long connectionId, MeshLayoutOperation op, MeshCellBinding binding)
        {
            if (op.staged == null)
                return false;
            _transport.Send(connectionId, new MeshLayoutMessage
            {
                kind = MeshLayoutKind.HelloAnswer, targetCellId = op.StagedTargetId, targetIncarnation = op.StagedTargetIncarnation,
                stepId = op.StepId(op.stagedStepName), snapshot = op.staged.Clone(),
            });
            binding.answered = true;
            return true;
        }

        /// <summary>SplitAnswersHello: binds the child on its first hello and answers it (and any later one) with the staged step.</summary>
        private bool SplitAnswersHello(long connectionId, MeshLayoutHelloMessage hello, MeshCellBinding binding, MeshSplitOperation op)
        {
            if (!op.InProgress)
                return false;
            if (SameId(hello.cellId, op.parentId) && binding.incarnation != op.parentIncarnation)
            {
                FailOp(op, $"parent {op.parentId} restarted: incarnation {op.parentIncarnation} -> {binding.incarnation}");
                return false;
            }
            if (!SameId(hello.cellId, op.childId))
                return false;
            if (!op.childBound)
            {
                op.childBound = true;
                op.childConnection = connectionId;
                op.childIncarnation = binding.incarnation;
                OpLog(op, "bound " + binding.incarnation);
                StageE1(op);
            }
            else if (binding.incarnation != op.childIncarnation)
            {
                FailOp(op, $"child {op.childId} restarted: incarnation {op.childIncarnation} -> {binding.incarnation}");
                if (op.step != MeshSplitStep.Aborted)
                    return false;
                _transport.Send(connectionId, new MeshRetireMessage { cellId = op.childId, incarnation = binding.incarnation, pair = BroadcastPair, reason = "aborted by " + op.opId });
                return true;
            }
            op.childConnection = connectionId;
            if (!op.InProgress || op.staged == null)
                return op.step == MeshSplitStep.Aborted;   // an aborted child got its Retire; otherwise the broadcast answers
            return AnswerStaged(connectionId, op, binding);
        }

        /// <summary>OpOnAck: a Rejected ack of the step's pair fails the operation; any other ack may open a gate.</summary>
        private void OpOnAck(MeshLayoutAckMessage ack)
        {
            MeshLayoutOperation op = _op;
            if (op == null || !op.InProgress)
                return;
            if (ack.result == MeshLayoutAckResult.Rejected)
            {
                MeshLayoutPair stepPair = op.CurrentStepPair;
                bool byTarget = SameId(ack.cellId, op.StagedTargetId);
                if (ack.pair == stepPair && (byTarget || op.participants.Exists(p => SameId(p, ack.cellId))))
                {
                    // The staged target refusing its staged commit is the only cell that saw it: it can still be undone.
                    bool undo = !op.PastCommit || (op.CommitStaged && byTarget);
                    End(op, undo, $"{ack.cellId} rejected {op.CurrentStepName} {ack.pair}: {ack.reason}");
                }
                return;
            }
            Advance();
        }

        private void FailOp(MeshLayoutOperation op, string reason) => End(op, !op.PastCommit, reason);

        private void End(MeshLayoutOperation op, bool abort, string reason)
        {
            if (!abort)
                Freeze(op, reason);
            else if (op is MeshSplitOperation split)
                Abort(split, reason);
            else if (op is MeshMergeOperation merge)
                AbortMerge(merge, reason);
        }

        /// <summary>
        /// Abort (before e2): retire the child, remove its channel, and broadcast it Retired if e1 went out (decision 5a). The
        /// Retire is `fenced:` when the child may own something (its e2 was staged) or the abort is a member's loss (Slice 2d).
        /// </summary>
        private void Abort(MeshSplitOperation op, string reason, bool fence = false)
        {
            string retireReason = (fence || op.step == MeshSplitStep.E2Staged ? "fenced: aborted by " : "aborted by ") + op.opId;
            op.step = MeshSplitStep.Aborted;
            op.endReason = reason;
            op.staged = null;
            op.stagedStepName = "";
            _abortedIds.Add(op.childId);
            Line(true, $"Split {op.opId}: aborted {reason}");
            PolicyOpEnded(op, false, "aborted " + reason);
            if (op.childBound)
                _transport.Send(op.childConnection, new MeshRetireMessage { cellId = op.childId, incarnation = op.childIncarnation, pair = BroadcastPair, reason = retireReason });
            SplitHost?.RemoveChannel(op.childId);
            if (op.e1Broadcast)
            {
                MeshLayoutSnapshot retired = op.Retired(_broadcast, NextPair());
                string applyError = null;
                if (SaveStore(op, retired, null, null) && ApplyOnCentral(retired, out applyError))
                {
                    SendBroadcast(retired, op.StepId("abort"));
                    Line(false, $"Split {op.opId}: retired broadcast {retired.pair}");
                }
                else
                    Line(true, $"Split {op.opId}: the Retired version could not be broadcast: {applyError ?? _store.LastError}");
            }
            else
                SaveStore(op, _broadcast, null, null);
        }

        /// <summary>Freeze (after e2): the operation stops where it is; nothing else runs until supervision (Slice 2d) or a restart.</summary>
        private void Freeze(MeshLayoutOperation op, string reason)
        {
            op.frozenStep = StepToResolve(op);   // memory only: the unfreeze resolves it by this step (the store keeps "Frozen")
            op.MarkFrozen();
            op.endReason = reason;
            Line(true, $"{op.LinePrefix} {op.opId}: frozen {reason}");
            PolicyOpEnded(op, false, "frozen " + reason);
            SaveStore(op, _broadcast, op.staged, op.staged != null ? op.StepId(string.IsNullOrEmpty(op.stagedStepName) ? "e2" : op.stagedStepName) : null);
        }

        private void Refuse(string rule) => Line(true, "Layout op refused: " + rule);

        private void OpLog(MeshLayoutOperation op, string text) => Line(false, $"{op.LinePrefix} {op.opId}: {text}");

        private void Line(bool warning, string line)
        {
            if (warning)
                _log.Warning(line);
            else
                _log.Info(line);
            Action<string> handlers = OpLine;
            if (handlers == null)
                return;
            foreach (Delegate handler in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<string>)handler)(line);
                }
                catch (Exception ex)
                {
                    _log.Error("Layout op line subscriber failed: " + ex.Message);
                }
            }
        }

        private void SplitStop()
        {
            _op = null;
            _opCounter = 0;
            _mergeCounter = 0;
            _lastVersion = 0;
            _abortedIds.Clear();
            _cooldownUntil.Clear();
        }

        /// <summary>BandsWithDynamicMap: the cell (of any map) has a band neighbour on a Dynamic map.</summary>
        private bool BandsWithDynamicMap(string cellId)
        {
            MeshCell cell = _world.GetCellById(cellId);
            if (cell == null)
                return false;
            for (int i = 0; i < cell.bandNeighbours.Count; ++i)
            {
                if (cell.bandNeighbours[i].OnDynamicMap)
                    return true;
            }
            return false;
        }

        private MeshCellBinding BindStaticNeighbour(long connectionId, string cellId, string mapId, string incarnation)
        {
            if (!_bindings.TryGetValue(cellId, out MeshCellBinding binding))
            {
                binding = new MeshCellBinding { cellId = cellId };
                _bindings[cellId] = binding;
                _log.Info($"Static neighbour {cellId} of a Dynamic map bound (connection {connectionId}, incarnation {incarnation})");
            }
            binding.mapId = mapId;
            binding.connectionId = connectionId;
            binding.incarnation = incarnation ?? string.Empty;
            binding.helloAt = _clock.Now;
            binding.answered = true;
            binding.unregisteredAt = -1f;
            binding.staticNeighbour = true;
            return binding;
        }

        /// <summary>A Static neighbour that never said hello is bound by its first ack, from the map server registered as it.</summary>
        private MeshCellBinding BindStaticFromAck(long connectionId, MeshLayoutAckMessage ack)
        {
            if (string.IsNullOrEmpty(ack.cellId) || !_transport.TryGetMapServer(connectionId, out MeshMapServerPeer peer)
                || !SameId(peer.cellId, ack.cellId) || _world.IsDynamicMap(peer.mapId) || !BandsWithDynamicMap(ack.cellId))
                return null;
            return BindStaticNeighbour(connectionId, ack.cellId, peer.mapId, ack.incarnation);
        }
    }
}
