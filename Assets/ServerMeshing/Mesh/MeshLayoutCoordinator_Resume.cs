// Server Meshing add-on — what a restarted central does with the operation its store says was running (Slice 5,
// slices/slice5_design.md decisions 6 and 7). Once every map recovered, the resolver's verdict is carried out: the
// operation is aborted through its own path (a Retired or Reverted version when e1 went out), rebuilt and resumed at its
// gate with the members' current bindings, its staged step broadcast when its target applied it, its settle resumed, or
// dropped with a warning. A Resume on a family frozen at the ceiling waits for the family's unfreeze.
using System;
using System.Collections.Generic;

namespace MultiplayerARPG.MMO.Mesh
{
    public sealed partial class MeshLayoutCoordinator
    {
        private bool _storedOpResolved;
        private string _resumeWaitLogged;

        /// <summary>ResumeStoredOperation runs the stored operation's verdict (once; a Resume on a frozen family waits).</summary>
        private void ResumeStoredOperation()
        {
            if (_storedOpResolved || Phase != MeshCoordinatorPhase.Running || _storeData == null || !_storeData.hasOperation)
                return;
            MeshStoredOperation stored = _storeData.operation;
            string prefix = stored.IsMerge ? "Merge" : "Split";
            if (!AdoptedRuntimeLayout)
            {
                _storedOpResolved = true;
                Line(true, $"{prefix} {stored.opId}: dropped after the restart: the asset cut was adopted");
                ClearStoredOperation();
                return;
            }
            Dictionary<string, MeshLayoutPair> held = new Dictionary<string, MeshLayoutPair>(StringComparer.OrdinalIgnoreCase);
            foreach (MeshCellBinding binding in _bindings.Values)
            {
                if (binding.unregisteredAt < 0f && !binding.staticNeighbour)
                    held[binding.cellId] = binding.held != null ? binding.held.pair : binding.applied;
            }
            MeshOperationVerdict verdict = MeshRecoveryResolver.ResolveOperation(stored, _broadcast.pair, held);
            bool resumes = verdict.kind == MeshOperationVerdictKind.Resume || verdict.kind == MeshOperationVerdictKind.ResendStaged || verdict.kind == MeshOperationVerdictKind.ResumeSettle;
            string frozen = FrozenFamilyReason(stored.baseId);
            if (resumes && frozen != null)
            {
                if (_resumeWaitLogged != frozen)
                {
                    _resumeWaitLogged = frozen;
                    Line(true, $"{prefix} {stored.opId}: waits after the restart: family {stored.baseId} is frozen: {frozen}");
                }
                return;
            }
            _storedOpResolved = true;
            int number = MeshStoredOperation.Number(stored.opId);
            if (stored.IsSplit)
                _opCounter = Math.Max(_opCounter, number);
            else
                _mergeCounter = Math.Max(_mergeCounter, number);
            MeshLayoutPair storedBroadcast = _storeData.broadcast != null ? _storeData.broadcast.pair : MeshLayoutPair.None;
            switch (verdict.kind)
            {
                case MeshOperationVerdictKind.Drop:
                    Line(true, $"{prefix} {stored.opId}: dropped after the restart: {verdict.reason}");
                    ClearStoredOperation();
                    return;
                case MeshOperationVerdictKind.Abort:
                    if (stored.IsSplit)
                    {
                        MeshSplitOperation split = stored.ToSplit(verdict.splitStep, storedBroadcast);
                        BindMembers(split);
                        _op = split;
                        Abort(split, verdict.reason);
                    }
                    else
                    {
                        MeshMergeOperation merge = stored.ToMerge(verdict.mergeStep, storedBroadcast);
                        BindMembers(merge);
                        _op = merge;
                        AbortMerge(merge, verdict.reason);
                    }
                    return;
                case MeshOperationVerdictKind.ResendStaged:
                    ResumeAt(stored, verdict, storedBroadcast, true);
                    return;
                case MeshOperationVerdictKind.Resume:
                    ResumeAt(stored, verdict, storedBroadcast, false);
                    return;
                case MeshOperationVerdictKind.ResumeSettle:
                    MeshSplitOperation done = stored.ToSplit(MeshSplitStep.Done, storedBroadcast);
                    BindMembers(done);
                    _op = done;
                    OpLog(done, "settle resumed after a central restart");
                    Advance();
                    return;
            }
        }

        /// <summary>ResumeAt rebuilds the operation at the verdict's step (with its staged snapshot when re-sent) and lets the gates run.</summary>
        private void ResumeAt(MeshStoredOperation stored, MeshOperationVerdict verdict, MeshLayoutPair storedBroadcast, bool staged)
        {
            MeshLayoutOperation op;
            if (stored.IsSplit)
            {
                MeshSplitOperation split = stored.ToSplit(verdict.splitStep, storedBroadcast);
                BindMembers(split);
                split.drainInitial = -1;
                split.drainDeadline = -1f;
                op = split;
            }
            else
            {
                MeshMergeOperation merge = stored.ToMerge(verdict.mergeStep, storedBroadcast);
                BindMembers(merge);
                merge.e2AckedAt = -1f;
                merge.retiringGoneSince = -1f;
                merge.nextStraggleAt = -1f;
                op = merge;
            }
            if (staged && _storeData.hasStaged)
            {
                op.staged = _storeData.staged.Clone();
                op.stagedStepName = stored.IsSplit && verdict.splitStep == MeshSplitStep.E1Staged ? "e1" : "e2";
            }
            op.participants.Clear();
            // The resumed step's participants: every bound cell of a Dynamic map (a superset: each acks the hello answer with a pair at or above the step's).
            foreach (MeshCellBinding binding in _bindings.Values)
            {
                if (binding.unregisteredAt < 0f && !binding.staticNeighbour && _world.IsDynamicMap(binding.mapId))
                    op.participants.Add(binding.cellId);
            }
            if (!staged)
                op.prewarmNeighbours.AddRange(BandNeighboursOf(_broadcast, stored.IsSplit ? stored.childId : stored.survivorId));
            _op = op;
            StepStarted(op);
            OpLog(op, $"resumed at {op.StepText} after a central restart");
            if (op is MeshMergeOperation retiring && retiring.step == MeshMergeStep.Retiring)
                SendRetire(retiring);
            Advance();
        }

        private void BindMembers(MeshSplitOperation op)
        {
            MeshCellBinding parent = BindingOf(op.parentId);
            if (parent != null && !parent.staticNeighbour)
            {
                op.parentConnection = parent.connectionId;
                op.parentIncarnation = parent.incarnation;
            }
            MeshCellBinding child = BindingOf(op.childId);
            if (child != null && !child.staticNeighbour && child.unregisteredAt < 0f)
            {
                op.childBound = true;
                op.childConnection = child.connectionId;
                op.childIncarnation = child.incarnation;
            }
        }

        private void BindMembers(MeshMergeOperation op)
        {
            MeshCellBinding survivor = BindingOf(op.survivorId);
            if (survivor != null && !survivor.staticNeighbour)
            {
                op.survivorConnection = survivor.connectionId;
                op.survivorIncarnation = survivor.incarnation;
            }
            MeshCellBinding retiring = BindingOf(op.retiringId);
            if (retiring != null && !retiring.staticNeighbour)
            {
                op.retiringConnection = retiring.connectionId;
                op.retiringIncarnation = retiring.incarnation;
            }
        }

        /// <summary>ClearStoredOperation forgets the stored operation (dropped) without touching the layout the store holds.</summary>
        private void ClearStoredOperation()
        {
            MeshCoordinatorStoreData data = _storeData ?? new MeshCoordinatorStoreData { lastIncarnation = Incarnation };
            data.hasOperation = false;
            data.operation = new MeshStoredOperation();
            data.opId = string.Empty;
            _storeData = data;
            if (!_store.TrySave(data, out string error))
                _log.Error(error);
        }
    }
}
