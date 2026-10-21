// Server Meshing add-on — the operator's escape from a freeze (DESIGN 0.85, slices/unfreeze_design.md): `<seq> unfreeze
// <base>`. A family frozen after a central restart (a dynamic id never said hello by the ceiling, Slice 5 decision 3) has
// its missing ids declared dead: each is fenced by a fence floor (every process of it started before the command), one
// owning a rectangle collapses into a live sibling or the base through 2d's collapse, one owning nothing is retired by a
// retire version, the live cells stay as they are, and a stored operation waiting on the family is dropped (a member
// dead) or resumed. A frozen operation is resolved by the rules a restarted central applies to a stored one (Slice 5
// decision 7), keyed on the step it froze at. A stored operation waiting on a frozen family holds the world-wide op slot
// (decision 9). Every line starts "Unfreeze <base>: ", "Split <op>: ", "Merge <op>: " or "Layout op refused: ".
using System;
using System.Collections.Generic;

namespace MultiplayerARPG.MMO.Mesh
{
    public sealed partial class MeshLayoutCoordinator
    {
        /// <summary>Dead ids owning nothing, by base, whose retire version waits for the op slot (decision 4).</summary>
        private readonly Dictionary<string, List<string>> _retireQueued = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// StoredOpWaits (decision 9): the store's operation is unresolved while every map recovered, so it waits for its
        /// frozen family and holds the world-wide op slot.
        /// </summary>
        private bool StoredOpWaits => Phase == MeshCoordinatorPhase.Running && !_storedOpResolved && _storeData != null && _storeData.hasOperation
            && FrozenFamilyReason(_storeData.operation.baseId) != null;

        /// <summary>StoredOpWaitRefusal is CheckOpCommon's refusal while a stored operation waits (null when none does).</summary>
        private string StoredOpWaitRefusal()
        {
            if (!StoredOpWaits)
                return null;
            MeshStoredOperation stored = _storeData.operation;
            return $"{stored.opId} waits for family {stored.baseId}, which is frozen: {FrozenFamilyReason(stored.baseId)}";
        }

        /// <summary>Whether the world-wide op slot is held: an operation runs or froze, or a stored one waits.</summary>
        private bool OpSlotHeld => (_op != null && (_op.InProgress || _op.IsFrozen)) || StoredOpWaits;

        /// <summary>
        /// RequestUnfreeze runs `unfreeze &lt;base&gt;`: the family's recovery freeze or its frozen operation is resolved. False
        /// with the reason (also logged "Layout op refused: reason") when there is nothing to resolve or it may not be.
        /// </summary>
        public bool RequestUnfreeze(string baseId, out string error)
        {
            error = CheckUnfreeze(baseId, out MeshCell baseCell, out MapRecovery map, out FrozenFamily family);
            if (error == null && family != null)
                error = CheckDead(family);
            if (error == null && family == null)
                error = CheckFrozenOp(_op);
            if (error != null)
            {
                Refuse(error);
                return false;
            }
            if (family != null)
                UnfreezeFamily(baseCell, map, family);
            else
                UnfreezeOperation(baseCell.id, _op);
            return true;
        }

        private string CheckUnfreeze(string baseId, out MeshCell baseCell, out MapRecovery map, out FrozenFamily family)
        {
            baseCell = null;
            map = null;
            family = null;
            if (Phase == MeshCoordinatorPhase.Stopped)
                return "the layout coordinator is not running";
            if (Phase != MeshCoordinatorPhase.Running)
                return "the layout coordinator is still recovering";
            if (!_store.Writable && (_storeData == null || !_store.TrySave(_storeData, out _)))
                return "the coordinator store is not writable";   // one save tried first: a store blocked once stays unwritable until a save succeeds
            if (SplitHost == null)
                return "this central has no split host";
            MeshCell cell = string.IsNullOrEmpty(baseId) ? null : _world.GetCellById(baseId);
            if (cell == null)
                return $"cell {baseId} is not in the layout";
            if (!cell.OnDynamicMap)
                return $"{cell.id} is on map {cell.mapId}, which is Static";
            if (cell.isDynamic)
                return $"{cell.id} is a child of {cell.baseId}: unfreeze takes the family's base";
            baseCell = cell;
            if (_maps.TryGetValue(cell.mapId, out map) && map.frozenFamilies.TryGetValue(cell.id, out family))
                return null;
            if (_op != null && _op.IsFrozen && SameId(_op.baseId, cell.id))
                return null;
            string holder = null;
            if (_op != null && _op.InProgress)
                holder = SameId(_op.baseId, cell.id) ? $"{_op.opId} is in progress" : $"{_op.opId} of family {_op.baseId} is in progress";
            else if (_op != null && _op.IsFrozen)
                holder = $"{_op.opId} of family {_op.baseId} is frozen: unfreeze {_op.baseId}";
            else if (StoredOpWaits)
                holder = $"{_storeData.operation.opId} waits for family {_storeData.operation.baseId}, which is frozen: unfreeze {_storeData.operation.baseId}";
            return $"family {cell.id} is not frozen" + (holder != null ? "; " + holder : string.Empty);
        }

        /// <summary>CheckDead (decision 3): a missing id the cluster sees is not dead, and a cut-off cell must have had the time to fence.</summary>
        private string CheckDead(FrozenFamily family)
        {
            foreach (string id in family.missing)
            {
                if (IsRegistered(id))
                    return $"a map server of {id} is registered but has not said hello: stop it or let it say hello";
            }
            float up = _clock.Now - _startedAt, need = SupervisorSettings.IdReuseSeconds;
            return up < need ? $"the central started {Seconds(up)} s ago, under the {Seconds(need)} s a cut-off cell may take to fence" : null;
        }

        // ---- a frozen family -------------------------------------------------------------------------------------------

        private void UnfreezeFamily(MeshCell baseCell, MapRecovery map, FrozenFamily family)
        {
            string baseId = baseCell.id, prefix = "Unfreeze " + baseId + ": ";
            List<string> dead = new List<string>(family.missing);
            dead.Sort(StringComparer.OrdinalIgnoreCase);
            // The floor is this central's start: nothing could start a process of a missing id since (the old central is
            // dead, the family frozen and unsupervised), and the margin covers a spawning host's clock lagging this one's.
            string floor = MeshUnfreeze.FenceFloor(Incarnation);
            float up = _clock.Now - _startedAt;
            foreach (string id in dead)
            {
                _fenced.Add(id, floor);
                Line(true, $"{prefix}{id} declared dead (no hello {Seconds(up)} s after the central started); incarnations started before {floor} fenced");
            }
            map.frozenFamilies.Remove(baseId);
            Line(false, $"{prefix}family {baseId} of map {baseCell.mapId} unfrozen by the operator: {string.Join(", ", dead)} declared dead");
            ResolveWaitingOperation(baseId, dead);
            SaveFencedSet();
            List<string> collapse = new List<string>(), retire = new List<string>();
            MeshUnfreeze.Classify(_broadcast, baseId, dead, collapse, retire);
            foreach (string id in collapse)
                QueueUnfreezeCollapse(prefix, baseId, baseCell.mapId, id, dead, collapse.Count > 1);
            if (retire.Count > 0)
            {
                if (!_retireQueued.TryGetValue(baseId, out List<string> queued))
                    _retireQueued[baseId] = queued = new List<string>();
                queued.AddRange(retire);
                UnfreezeTick();
            }
        }

        /// <summary>
        /// ResolveWaitingOperation (decision 5): the store's operation waiting on this family is dropped when a member is
        /// dead (its unregistered retiring cell joins the dead), else resolved by the restart's verdict now the family is unfrozen.
        /// </summary>
        private void ResolveWaitingOperation(string baseId, List<string> dead)
        {
            // Not StoredOpWaits: the family has just left frozenFamilies; the stored op is still the one that waited on it.
            if (_storedOpResolved || _storeData == null || !_storeData.hasOperation || !SameId(_storeData.operation.baseId, baseId))
                return;
            MeshStoredOperation stored = _storeData.operation;
            string member = MeshUnfreeze.DeadMember(stored, dead);
            if (member == null)
            {
                ResumeStoredOperation();
                return;
            }
            _storedOpResolved = true;
            if (stored.IsSplit)
                _opCounter = Math.Max(_opCounter, MeshStoredOperation.Number(stored.opId));
            else
                _mergeCounter = Math.Max(_mergeCounter, MeshStoredOperation.Number(stored.opId));
            Line(true, $"{(stored.IsMerge ? "Merge" : "Split")} {stored.opId}: dropped at the unfreeze: {member} declared dead");
            ClearStoredOperation();
            string other = stored.DynamicId;
            if (stored.IsMerge && !SameId(other, member) && !IsRegistered(other) && !dead.Exists(d => SameId(d, other)))
            {
                dead.Add(other);
                _fenced.Add(other, MeshUnfreeze.FenceFloor(Incarnation));
                Line(true, $"Unfreeze {baseId}: {other} declared dead (its merge was dropped and no process of it is registered)");
            }
        }

        /// <summary>
        /// QueueUnfreezeCollapse gives a dead id owning a rectangle 2d's CollapseQueued record. The survivor named is the base
        /// or a live sibling sharing a full edge (never another dead id); with none, a dead id waits for the family's other
        /// collapses when there are any (one may grow a cell to its edge), else it is left down.
        /// </summary>
        private void QueueUnfreezeCollapse(string prefix, string baseId, string mapId, string id, List<string> dead, bool others)
        {
            MeshSupervisedCell record = new MeshSupervisedCell { cellId = id, baseId = baseId, mapId = mapId, isBase = false, phase = MeshSupervisorPhase.CollapseQueued, announced = true };
            _supervised[id] = record;
            MeshCell survivor = LiveSurvivorOf(id, baseId, dead);
            if (survivor == null && others)
            {
                Line(true, $"{prefix}{id} waits for the family's other collapses (no live cell of its family shares a full edge with it yet)");
                return;
            }
            if (survivor == null)
            {
                record.phase = MeshSupervisorPhase.LeftDown;
                Line(true, $"{prefix}{id} cannot collapse (no live cell of its family shares a full edge); left down");
                NoteCannotCollapse(record);
                return;
            }
            Line(false, $"{prefix}{id} collapses into {survivor.id}");
        }

        /// <summary>LiveSurvivorOf: the base when its rectangle and the dead cell's make one, else the first live Active sibling that does.</summary>
        private MeshCell LiveSurvivorOf(string id, string baseId, List<string> dead)
        {
            MeshCell cell = _world.GetCellById(id), baseCell = _world.GetCellById(baseId);
            if (cell == null || baseCell == null)
                return null;
            if (!baseCell.owned.IsEmpty && baseCell.owned.TryUnion(cell.owned, _world.touchTolerance, out _))
                return baseCell;
            foreach (MeshCell sibling in _world.FamilyOf(baseId))
            {
                if (sibling.isDynamic && !SameId(sibling.id, id) && !dead.Exists(d => SameId(d, sibling.id)) && sibling.state == MeshCellState.Active
                    && !sibling.owned.IsEmpty && sibling.owned.TryUnion(cell.owned, _world.touchTolerance, out _))
                    return sibling;
            }
            return null;
        }

        /// <summary>
        /// StepToResolve (decision 7) is the step a freezing operation is resolved by: its own, unless the step's broadcast
        /// never went out (BroadcastStep enters the next step before its save and apply, so a store or apply failure freezes
        /// it one step on, at a pair nobody holds): then the step before it, whose layout every cell has.
        /// </summary>
        private string StepToResolve(MeshLayoutOperation op)
        {
            MeshLayoutPair pair = op.CurrentStepPair;
            bool unsent = !pair.IsNone && _broadcast != null && pair.IsNewerThan(_broadcast.pair);
            if (op is MeshSplitOperation split && unsent)
            {
                if (split.step == MeshSplitStep.DrainGate)
                    return nameof(MeshSplitStep.E2Staged);
                if (split.step == MeshSplitStep.E3Broadcast)
                    return nameof(MeshSplitStep.DrainGate);
            }
            if (op is MeshMergeOperation merge && unsent)
            {
                if (merge.step == MeshMergeStep.DrainGate)
                    return nameof(MeshMergeStep.E2Staged);
                if (merge.step == MeshMergeStep.E3Broadcast)
                    return merge.retireSentAt >= 0f ? nameof(MeshMergeStep.Retiring) : nameof(MeshMergeStep.DrainGate);
                if (merge.step == MeshMergeStep.TidyBroadcast)
                    return nameof(MeshMergeStep.E3Broadcast);
            }
            return op.StepText;
        }

        /// <summary>UnfreezeTick (decision 6): a queued retire version goes out once the op slot is free.</summary>
        private void UnfreezeTick()
        {
            if (Phase != MeshCoordinatorPhase.Running || _retireQueued.Count == 0 || OpSlotHeld)
                return;
            foreach (string baseId in new List<string>(_retireQueued.Keys))
            {
                List<string> ids = _retireQueued[baseId];
                ids.RemoveAll(id => { MeshCell cell = _world.GetCellById(id); return cell == null || cell.state == MeshCellState.Retired; });
                if (ids.Count == 0)
                {
                    _retireQueued.Remove(baseId);
                    continue;
                }
                if (BroadcastRetireVersion(baseId, ids))
                    _retireQueued.Remove(baseId);
            }
        }

        private bool BroadcastRetireVersion(string baseId, List<string> ids)
        {
            string prefix = "Unfreeze " + baseId + ": ";
            MeshLayoutSnapshot retired = MeshUnfreeze.Retired(_broadcast, ids, NextPair());
            if (!MeshLayoutValidator.Validate(ValidationWorld(), retired, _broadcast, out string reason) || !SaveLayout(retired) || !ApplyOnCentral(retired, out reason))
            {
                Line(true, $"{prefix}the retire version could not be broadcast: {reason ?? _store.LastError}");
                return false;
            }
            SendBroadcast(retired, "unfreeze-" + baseId + "/retire");
            foreach (string id in ids)
                SplitHost?.RemoveChannel(id);
            Line(false, $"{prefix}retire version {retired.pair} broadcast ({string.Join(", ", ids)} Retired)");
            return true;
        }

        /// <summary>SaveLayout writes a layout of no operation's (the retire version): the stored operation, if any, is kept.</summary>
        private bool SaveLayout(MeshLayoutSnapshot broadcast)
        {
            MeshCoordinatorStoreData data = _storeData ?? new MeshCoordinatorStoreData { lastIncarnation = Incarnation };
            data.broadcast = broadcast.Clone();
            data.hasStaged = false;
            data.staged = new MeshLayoutSnapshot();
            data.stagedStepId = string.Empty;
            data.stagedFirstApplier = string.Empty;
            data.abortedIds = new List<string>(_abortedIds);
            data.fencedIncarnations = _fenced.ToStore();
            _storeData = data;
            if (_store.TrySave(data, out string error))
                return true;
            _log.Error(error);
            return false;
        }

        // ---- a frozen operation ----------------------------------------------------------------------------------------

        /// <summary>CheckFrozenOp (decision 7): the frozen operation must name the step it froze at and resolve by the table.</summary>
        private string CheckFrozenOp(MeshLayoutOperation op)
        {
            if (op == null || !op.IsFrozen)
                return null;
            MeshOperationVerdict verdict = FrozenVerdict(op, out _);
            return verdict == null ? $"{op.opId} froze at an unknown step{(string.IsNullOrEmpty(op.frozenStep) ? string.Empty : " " + op.frozenStep)}" : null;
        }

        /// <summary>FrozenVerdict: the restart resolver's verdict on the operation as stored at its frozen step (null when it is not one the table resumes or aborts).</summary>
        private MeshOperationVerdict FrozenVerdict(MeshLayoutOperation op, out MeshStoredOperation stored)
        {
            stored = MeshStoredOperation.From(op);
            stored.step = op.frozenStep ?? string.Empty;
            bool known = stored.IsSplit ? stored.TrySplitStep(out MeshSplitStep s) && s >= MeshSplitStep.E2Staged && s <= MeshSplitStep.E3Broadcast
                : stored.TryMergeStep(out MeshMergeStep m) && m >= MeshMergeStep.E2Staged && m <= MeshMergeStep.TidyBroadcast;
            if (!known)
                return null;
            Dictionary<string, MeshLayoutPair> applied = new Dictionary<string, MeshLayoutPair>(StringComparer.OrdinalIgnoreCase);
            foreach (MeshCellBinding binding in _bindings.Values)
            {
                if (binding.unregisteredAt < 0f && !binding.staticNeighbour && _world.IsDynamicMap(binding.mapId))
                    applied[binding.cellId] = binding.applied;
            }
            MeshOperationVerdict verdict = MeshRecoveryResolver.ResolveOperation(stored, _broadcast.pair, applied);
            return verdict.kind == MeshOperationVerdictKind.Drop || verdict.kind == MeshOperationVerdictKind.ResumeSettle ? null : verdict;
        }

        private void UnfreezeOperation(string baseId, MeshLayoutOperation frozen)
        {
            MeshOperationVerdict verdict = FrozenVerdict(frozen, out MeshStoredOperation stored);
            Line(false, $"Unfreeze {baseId}: {frozen.opId} frozen at {frozen.frozenStep} ({frozen.endReason})");
            MeshLayoutPair broadcastPair = _broadcast.pair;
            if (verdict.kind == MeshOperationVerdictKind.Abort)
            {
                string reason = $"by the unfreeze: {(stored.IsSplit ? stored.childId : stored.survivorId)} did not acknowledge the staged e2";
                if (stored.IsSplit)
                {
                    MeshSplitOperation split = stored.ToSplit(verdict.splitStep, broadcastPair);
                    BindMembers(split);
                    _op = split;
                    Abort(split, reason);
                }
                else
                {
                    MeshMergeOperation merge = stored.ToMerge(verdict.mergeStep, broadcastPair);
                    BindMembers(merge);
                    _op = merge;
                    AbortMerge(merge, reason);
                }
                return;
            }
            bool resend = verdict.kind == MeshOperationVerdictKind.ResendStaged;
            if (verdict.kind == MeshOperationVerdictKind.Resume && stored.IsSplit && verdict.splitStep == MeshSplitStep.DrainGate && BindingOf(stored.parentId) is MeshCellBinding parent)
                parent.hasEvidence = false;   // the drain gate re-opens on a window after the unfreeze, as after a restart's hello
            if (!resend)
            {
                SendBroadcast(_broadcast, frozen.StepId("unfreeze"));
                Line(false, $"Unfreeze {baseId}: layout {_broadcast.pair} re-sent");
            }
            MeshLayoutOperation op = ResumeAt(stored, verdict, broadcastPair, resend ? frozen.staged : null, "the unfreeze");
            if (op.InProgress && ReferenceEquals(_op, op))
                SaveStore(op, _broadcast, op.staged, op.staged != null ? op.StepId(op.stagedStepName) : null);
        }
    }
}
