// Server Meshing add-on — the coordinator's merge (Slice 2c, slices/slice2c_design.md decisions 1-7, plan D4): a child
// B of a Dynamic cell's family drains into the family's base cell A, exits, and is retired. The request and its
// refusals, the hello hook and the abort (a reverted broadcast; nothing retired or removed). The steps are in
// MeshLayoutCoordinator_MergeSteps.cs. Every line an operator or the harness reads starts "Merge <op>: " or
// "Layout op refused: ".
namespace MultiplayerARPG.MMO.Mesh
{
    public sealed partial class MeshLayoutCoordinator
    {
        /// <summary>Merges are numbered on their own (merge-1 is the first merge, whatever splits ran before it).</summary>
        private int _mergeCounter;

        /// <summary>
        /// RequestMerge joins <paramref name="a"/> and <paramref name="b"/> (either order): the family's base cell survives
        /// with the union, the child drains into it, exits and is retired. False with the broken rule (also logged
        /// "Layout op refused: rule").
        /// </summary>
        public bool RequestMerge(string a, string b, out string error)
        {
            error = CheckMerge(a, b, out MeshMergeOperation op);
            if (error != null)
            {
                Refuse(error);
                return false;
            }
            StartMerge(op);
            return true;
        }

        /// <summary>StartMerge runs a checked merge (an operator's, or the supervisor's collapse, Slice 2d decision 3) to its e1 broadcast.</summary>
        private void StartMerge(MeshMergeOperation op)
        {
            _op = op;
            OpLog(op, $"requested {op.survivorId} {op.retiringId}{(op.collapse ? " collapse" : string.Empty)}");
            MeshLayoutSnapshot e1 = op.E1(_broadcast, NextPair());
            op.e1Pair = e1.pair;
            if (!BroadcastStep(op, e1, "e1", () => EnterStep(op, MeshMergeStep.E1Gate), null))
                return;   // aborted, and logged
            op.e1Broadcast = true;
            op.prewarmNeighbours.Clear();
            op.prewarmNeighbours.AddRange(BandNeighboursOf(_broadcast, op.survivorId));
        }

        private string CheckMerge(string a, string b, out MeshMergeOperation op)
        {
            op = null;
            string common = CheckOpCommon(a, out MeshCell cellA, out MeshCellBinding bindingA);
            if (common != null)
                return common;
            common = CheckOpCommon(b, out MeshCell cellB, out MeshCellBinding bindingB);
            if (common != null)
                return common;
            if (SameId(cellA.id, cellB.id))
                return $"a merge needs two different cells, not {cellA.id} twice";
            if (!SameId(cellA.baseId, cellB.baseId) || cellA.mapId != cellB.mapId)
                return $"{cellA.id} and {cellB.id} are not of one family (bases {cellA.baseId} and {cellB.baseId})";
            if (cellA.isDynamic && cellB.isDynamic)
                return "merge of two children (Slice 3)";
            bool aSurvives = !cellA.isDynamic;
            MeshCell survivor = aSurvives ? cellA : cellB, retiring = aSurvives ? cellB : cellA;
            MeshCellBinding survivorBinding = aSurvives ? bindingA : bindingB, retiringBinding = aSurvives ? bindingB : bindingA;
            if (!survivor.owned.TryUnion(retiring.owned, _world.touchTolerance, out MeshRect union))
                return $"{survivor.id} and {retiring.id} do not make a rectangle: {survivor.id} owns {survivor.owned}, {retiring.id} owns {retiring.owned}";
            foreach (MeshCell member in _world.FamilyOf(survivor.baseId))
            {
                if (!member.interest.Equals(member.owned))
                    return $"{member.id} is interested in {member.interest} beyond its owned {member.owned}: a split's settle is pending";
            }
            op = new MeshMergeOperation("merge-" + (_mergeCounter + 1))
            {
                mapId = survivor.mapId, baseId = survivor.baseId, survivorId = survivor.id, retiringId = retiring.id,
                survivorOwned = survivor.owned, retiringOwned = retiring.owned, union = union,
                survivorEntry = EntryOf(_broadcast, survivor.id), retiringEntry = EntryOf(_broadcast, retiring.id),
                survivorConnection = survivorBinding.connectionId, survivorIncarnation = survivorBinding.incarnation,
                retiringConnection = retiringBinding.connectionId, retiringIncarnation = retiringBinding.incarnation,
                requestedAt = _clock.Now, stepStartedAt = _clock.Now,
            };
            // Judged as the steps will be: e1 against the broadcast, e2 against e1, e3 against e2 (rule 8 can refuse e1).
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

        private static MeshLayoutEntry EntryOf(MeshLayoutSnapshot snapshot, string id)
        {
            MeshLayoutEntry entry = snapshot.entries.Find(e => e != null && SameId(e.id, id));
            return entry?.Clone();
        }

        /// <summary>
        /// MergeAnswersHello: a new incarnation of either cell before e2 aborts the merge (the reverted broadcast answers
        /// it); after e2 it is answered with the current layout and the merge goes on. The survivor's hello while e2 is
        /// staged to it gets the staged e2 again.
        /// </summary>
        private bool MergeAnswersHello(long connectionId, MeshLayoutHelloMessage hello, MeshCellBinding binding, MeshMergeOperation op)
        {
            if (!op.InProgress)
                return false;
            bool survivor = SameId(hello.cellId, op.survivorId);
            if (!survivor && !SameId(hello.cellId, op.retiringId))
                return false;
            string was = survivor ? op.survivorIncarnation : op.retiringIncarnation;
            if (binding.incarnation != was)
            {
                if (!op.PastCommit)
                {
                    FailOp(op, $"{hello.cellId} restarted: incarnation {was} -> {binding.incarnation}");
                    return false;
                }
                _log.Info($"Merge {op.opId}: {hello.cellId} restarted after e2, incarnation {was} -> {binding.incarnation}; answered with the current layout, the merge goes on");
                if (survivor)
                    op.survivorIncarnation = binding.incarnation;
                else
                    op.retiringIncarnation = binding.incarnation;
            }
            if (survivor)
                op.survivorConnection = connectionId;
            else
                op.retiringConnection = connectionId;
            return survivor && op.step == MeshMergeStep.E2Staged && AnswerStaged(connectionId, op, binding);
        }

        /// <summary>AbortMerge (before e2): broadcast the cells' entries as they were (the survivor's interest back to its own rectangle).</summary>
        private void AbortMerge(MeshMergeOperation op, string reason)
        {
            op.step = MeshMergeStep.Aborted;
            op.endReason = reason;
            op.staged = null;
            op.stagedStepName = "";
            Line(true, $"Merge {op.opId}: aborted {reason}");
            if (!op.e1Broadcast)
            {
                SaveStore(op, _broadcast, null, null);
                return;
            }
            MeshLayoutSnapshot reverted = op.Reverted(_broadcast, NextPair());
            string applyError = null;
            if (SaveStore(op, reverted, null, null) && ApplyOnCentral(reverted, out applyError))
            {
                SendBroadcast(reverted, op.StepId("revert"));
                Line(false, $"Merge {op.opId}: reverted broadcast {reverted.pair}");
            }
            else
                Line(true, $"Merge {op.opId}: the reverted version could not be broadcast: {applyError ?? _store.LastError}");
        }

        private void EnterStep(MeshMergeOperation op, MeshMergeStep step)
        {
            op.step = step;
            StepStarted(op);
        }
    }
}
