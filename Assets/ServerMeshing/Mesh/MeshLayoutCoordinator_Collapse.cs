// Server Meshing add-on — the supervisor's collapse and kills (Slice 2d, slices/slice2d_design.md decisions 3, 4, 5 and 7):
// a child whose restarts all failed is merged into its base by 2c's merge with the collapse's relaxations (or left down
// when it shares no full edge with it); a process is killed through its base cell's spawner (KillChannel, 30020) when hung,
// on the operator's `kill`, or past retireKillAfterSeconds in a merge's retire step, and its incarnation is fenced; the
// fenced set is stored; a child id is named again only after the fence time. Split from
// MeshLayoutCoordinator_Supervision.cs to keep files short.
using System;
using System.Collections.Generic;

namespace MultiplayerARPG.MMO.Mesh
{
    public sealed partial class MeshLayoutCoordinator
    {
        // ---- collapse ------------------------------------------------------------------------------------------------

        /// <summary>QueueCollapse: the child's budget is spent; a split it is a member of ends; a child sharing no full edge with its base is left down.</summary>
        private void QueueCollapse(MeshSupervisedCell record)
        {
            string id = record.cellId;
            record.phase = MeshSupervisorPhase.CollapseQueued;
            Line(true, $"Supervisor: {id} collapse queued after {record.failures} failed restart(s)");
            if (_op is MeshSplitOperation split && split.InProgress && split.IsMember(id))
                EndSplitForCollapse(split, id);
            MeshCell cell = _world.GetCellById(id), baseCell = _world.GetCellById(record.baseId);
            if (cell == null || baseCell == null || !baseCell.owned.TryUnion(cell.owned, _world.touchTolerance, out _))
            {
                record.phase = MeshSupervisorPhase.LeftDown;
                Line(true, $"Supervisor: {id} cannot collapse (Slice 3); left down");
                NoteCannotCollapse(record);
            }
        }

        /// <summary>EndSplitForCollapse: a split whose member is queued for collapse ends (not frozen: the op slot is free).</summary>
        private void EndSplitForCollapse(MeshSplitOperation split, string id)
        {
            split.step = MeshSplitStep.Ended;
            split.endReason = id + " collapsing";
            split.staged = null;
            split.stagedStepName = "";
            split.pausedMembers.Clear();
            split.pausedAt = -1f;
            split.gateRestartPending = false;
            SaveStore(split, _broadcast, null, null);
            Line(true, $"Split {split.opId}: ended, {id} collapsing");
        }

        private void NoteCannotCollapse(MeshSupervisedCell record)
        {
            MeshCellBinding baseBinding = BindingOf(record.baseId);
            if (baseBinding != null && baseBinding.unregisteredAt < 0f)
                Note(baseBinding.connectionId, MeshNoteLevel.Error, record.baseId, "cannot-collapse",
                    $"{record.cellId} is down after {record.failures} failed restart(s) and shares no full edge with {record.baseId} (Slice 3): its rectangle is left unserved");
        }

        private void TryStartCollapse(MeshSupervisedCell record)
        {
            if (_op != null && (_op.InProgress || _op.IsFrozen))
                return;
            string error = CheckCollapse(record, out MeshMergeOperation op);
            if (error != null)
            {
                if (!_collapseWaitLogged.TryGetValue(record.cellId, out string logged) || logged != error)
                {
                    _collapseWaitLogged[record.cellId] = error;
                    _log.Info($"Supervisor: {record.cellId} collapse waits: {error}");
                }
                return;
            }
            _collapseWaitLogged.Remove(record.cellId);
            record.phase = MeshSupervisorPhase.Collapsing;
            StartMerge(op);
        }

        /// <summary>
        /// CheckCollapse: 2c's merge of the child into its base with the collapse's relaxations (decision 3) — the child
        /// may be unbound and unregistered, the family's cooldown and the pending-settle refusal do not apply.
        /// </summary>
        private string CheckCollapse(MeshSupervisedCell record, out MeshMergeOperation op)
        {
            op = null;
            MeshCell cell = _world.GetCellById(record.cellId);
            if (cell == null || !cell.isDynamic || cell.state != MeshCellState.Active || cell.owned.IsEmpty)
                return $"{record.cellId} is {(cell != null ? cell.state.ToString() : "not in the layout")}";
            string common = CheckOpCommon(cell.baseId, out MeshCell survivor, out MeshCellBinding survivorBinding, true);
            if (common != null)
                return common;
            if (!survivor.owned.TryUnion(cell.owned, _world.touchTolerance, out MeshRect union))
                return $"{survivor.id} and {cell.id} do not make a rectangle";
            MeshCellBinding retiring = BindingOf(cell.id);
            float now = _clock.Now;
            op = new MeshMergeOperation("merge-" + (_mergeCounter + 1))
            {
                collapse = true, mapId = survivor.mapId, baseId = survivor.baseId, survivorId = survivor.id, retiringId = cell.id,
                survivorOwned = survivor.owned, retiringOwned = cell.owned, union = union,
                survivorEntry = EntryOf(_broadcast, survivor.id), retiringEntry = EntryOf(_broadcast, cell.id),
                survivorConnection = survivorBinding.connectionId, survivorIncarnation = survivorBinding.incarnation,
                retiringConnection = retiring != null ? retiring.connectionId : -1, retiringIncarnation = retiring != null ? retiring.incarnation : string.Empty,
                retiringGoneSince = retiring != null && retiring.unregisteredAt >= 0f ? retiring.unregisteredAt : now,
                requestedAt = now, stepStartedAt = now,
            };
            MeshLayoutWorld validation = ValidationWorld();
            MeshLayoutSnapshot e1 = op.E1(_broadcast, new MeshLayoutPair(Incarnation, _broadcast.pair.version + 1));
            MeshLayoutSnapshot e2 = op.E2(e1, new MeshLayoutPair(Incarnation, _broadcast.pair.version + 2));
            MeshLayoutSnapshot e3 = op.E3(e2, new MeshLayoutPair(Incarnation, _broadcast.pair.version + 3));
            if (!MeshLayoutValidator.Validate(validation, e1, _broadcast, out string reason)
                || !MeshLayoutValidator.Validate(validation, e2, e1, out reason)
                || !MeshLayoutValidator.Validate(validation, e3, e2, out reason))
            {
                op = null;
                return reason;
            }
            _mergeCounter++;
            return null;
        }

        // ---- kills, fencing and id reuse -----------------------------------------------------------------------------

        /// <summary>RequestKill: the operator's `kill &lt;cell&gt;` has the cell's process killed by its spawner (decision 4).</summary>
        public bool RequestKill(string cellId, out string error)
        {
            error = null;
            MeshCellBinding binding = BindingOf(cellId);
            if (Phase == MeshCoordinatorPhase.Stopped)
                error = "the layout coordinator is not running";
            else if (SplitHost == null)
                error = "this central has no split host";
            else if (binding == null || binding.staticNeighbour)
                error = $"{cellId} is not a cell of a Dynamic map that said hello";
            else if (binding.unregisteredAt >= 0f)
                error = $"{cellId} is not registered";
            else if (binding.processId <= 0)
                error = $"{cellId} has no known process id";
            else
                TryKill(cellId, binding, "operator", true, out error);
            if (error == null)
                return true;
            Refuse(error);
            return false;
        }

        /// <summary>RetireKillTick: a merge's retire step older than retireKillAfterSeconds has the retiring cell killed (once).</summary>
        private void RetireKillTick(MeshMergeOperation op, float now)
        {
            float elapsed = now - op.stepStartedAt;
            if (op.collapse || op.retireKillAt >= 0f || elapsed < Math.Max(1f, Settings.retireKillAfterSeconds))
                return;
            op.retireKillAt = now;
            MeshCellBinding binding = BindingOf(op.retiringId);
            if (binding == null || !IsRegistered(op.retiringId))
                return;
            KillOrSay(op.retiringId, binding, $"retire straggling {Seconds(elapsed)} s");
        }

        private void KillOrSay(string cellId, MeshCellBinding binding, string why)
        {
            if (!TryKill(cellId, binding, why, true, out string error))
                Line(true, $"Supervisor: {cellId} cannot be killed by its spawner ({error})");
        }

        /// <summary>TryKill sends KillChannel for the binding's process to its base cell's spawner; the killed incarnation is fenced. Only success is logged (when asked).</summary>
        private bool TryKill(string cellId, MeshCellBinding binding, string why, bool logLine, out string error)
        {
            error = null;
            if (SplitHost == null || binding == null || binding.processId <= 0)
            {
                error = $"{cellId} has no known process id";
                return false;
            }
            MeshCell cell = _world.GetCellById(cellId);
            MeshKillChannelMessage message = new MeshKillChannelMessage
            {
                channelId = cellId, mapId = binding.mapId ?? string.Empty, processId = binding.processId,
                incarnation = binding.incarnation ?? string.Empty, reason = why,
            };
            if (!SplitHost.KillChannel(message, cell != null ? cell.baseId : cellId, out error))
                return false;
            binding.killedAt = _clock.Now;
            FenceBinding(cellId, binding);
            if (logLine)
                Line(true, $"Supervisor: {cellId} killed by its spawner ({why})");
            return true;
        }

        private void FenceBinding(string cellId, MeshCellBinding binding)
        {
            if (binding != null && _fenced.Add(cellId, binding.incarnation))
                SaveFencedSet();
        }

        /// <summary>SaveFencedSet writes the fenced set (and the aborted ids) without touching the layout the store holds.</summary>
        private void SaveFencedSet()
        {
            MeshCoordinatorStoreData data = _storeData ?? new MeshCoordinatorStoreData { lastIncarnation = Incarnation };
            data.fencedIncarnations = _fenced.ToStore();
            data.abortedIds = new List<string>(_abortedIds);
            _storeData = data;
            if (!_store.TrySave(data, out string error))
                _log.Error(error);
        }

        /// <summary>
        /// IdReuseRefusal (decision 7): a child id may be named again only once its last process has been gone at least
        /// childFenceSeconds + fenceSaveSeconds + 1 (a cut-off cell has fenced by then), or was killed by its spawner.
        /// </summary>
        private string IdReuseRefusal(string cellId)
        {
            MeshCellBinding binding = BindingOf(cellId);
            if (binding == null || binding.staticNeighbour || binding.killedAt >= 0f)
                return null;
            float need = SupervisorSettings.IdReuseSeconds;
            float gone = binding.unregisteredAt >= 0f ? _clock.Now - binding.unregisteredAt : 0f;
            return gone >= need ? null : $"{cellId} left the cluster {Seconds(gone)} s ago, under the {Seconds(need)} s a fencing cell may take";
        }
    }
}
