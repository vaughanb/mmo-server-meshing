// Server Meshing add-on — the stored operation's fate after a central restart (Slice 5, slices/slice5_design.md
// decision 7): MeshRecoveryResolver.ResolveOperation, split from MeshRecoveryResolver.cs for size (Slice 6). Pure.
using System.Collections.Generic;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>MeshOperationVerdictKind is what becomes of the stored operation after a restart (decision 7).</summary>
    public enum MeshOperationVerdictKind
    {
        /// <summary>Forgotten with a warning: stale, or frozen with nothing to resume.</summary>
        Drop,
        /// <summary>Undone through the operation's abort path (a Retired or Reverted version when e1 went out).</summary>
        Abort,
        /// <summary>Resumed at the step given, its gate restarted.</summary>
        Resume,
        /// <summary>The staged step its target applied is broadcast, then the operation goes on from there.</summary>
        ResendStaged,
        /// <summary>A done split whose settle is pending: the settle check resumes.</summary>
        ResumeSettle,
    }

    /// <summary>MeshOperationVerdict is the resolver's answer for the stored operation.</summary>
    public sealed class MeshOperationVerdict
    {
        public MeshOperationVerdictKind kind;
        public MeshSplitStep splitStep;
        public MeshMergeStep mergeStep;
        public string reason = "";
    }

    /// <summary>MeshRecoveryResolver decides one map's recovery and the stored operation's fate.</summary>
    public static partial class MeshRecoveryResolver
    {
        /// <summary>
        /// ResolveOperation (decision 7) decides the stored operation's fate from its step, the pair the central adopted and
        /// the pairs the cells hold (<paramref name="heldByCell"/>: cell id to the pair its hello held).
        /// </summary>
        public static MeshOperationVerdict ResolveOperation(MeshStoredOperation stored, MeshLayoutPair chosen, IReadOnlyDictionary<string, MeshLayoutPair> heldByCell)
        {
            MeshOperationVerdict verdict = new MeshOperationVerdict();
            if (stored == null || string.IsNullOrEmpty(stored.opId))
            {
                verdict.kind = MeshOperationVerdictKind.Drop;
                verdict.reason = "no operation stored";
                return verdict;
            }
            MeshLayoutPair newest = stored.NewestPair;
            if (!newest.IsNone && chosen.IsNewerThan(newest))
            {
                verdict.kind = MeshOperationVerdictKind.Drop;
                verdict.reason = $"the adopted layout {chosen} is newer than anything {stored.opId} issued ({newest}): the stored operation is stale";
                return verdict;
            }
            if (stored.IsSplit)
                return ResolveSplit(stored, chosen, heldByCell, verdict);
            if (stored.IsMerge)
                return ResolveMerge(stored, chosen, heldByCell, verdict);
            verdict.kind = MeshOperationVerdictKind.Drop;
            verdict.reason = $"{stored.opId} is of an unknown kind {stored.kind}";
            return verdict;
        }

        private static bool Holds(IReadOnlyDictionary<string, MeshLayoutPair> heldByCell, string cellId, MeshLayoutPair pair)
        {
            return !pair.IsNone && heldByCell != null && cellId != null && heldByCell.TryGetValue(cellId, out MeshLayoutPair held) && held.CompareTo(pair) >= 0;
        }

        private static MeshOperationVerdict ResolveSplit(MeshStoredOperation stored, MeshLayoutPair chosen, IReadOnlyDictionary<string, MeshLayoutPair> held, MeshOperationVerdict verdict)
        {
            if (!stored.TrySplitStep(out MeshSplitStep step))
            {
                verdict.kind = MeshOperationVerdictKind.Drop;
                verdict.reason = $"{stored.opId} has an unknown step {stored.step}";
                return verdict;
            }
            verdict.splitStep = step;
            switch (step)
            {
                case MeshSplitStep.ChannelAdded:
                case MeshSplitStep.Spawning:
                    verdict.kind = MeshOperationVerdictKind.Abort;
                    verdict.reason = "the central restarted before e1";
                    return verdict;
                case MeshSplitStep.E1Staged:
                    if (Holds(held, stored.childId, stored.e1Pair))
                        return Resend(verdict, "e1", stored.childId);
                    verdict.kind = MeshOperationVerdictKind.Abort;
                    verdict.reason = "the central restarted before e1";
                    return verdict;
                case MeshSplitStep.E1Gate:
                    verdict.kind = MeshOperationVerdictKind.Abort;
                    verdict.reason = "the central restarted before e2";
                    return verdict;
                case MeshSplitStep.E2Staged:
                    if (Holds(held, stored.childId, stored.e2Pair))
                        return Resend(verdict, "e2", stored.childId);
                    verdict.kind = MeshOperationVerdictKind.Abort;
                    verdict.reason = "the central restarted before e2";
                    return verdict;
                case MeshSplitStep.DrainGate:
                case MeshSplitStep.E3Broadcast:
                    if (!stored.e3Pair.IsNone && chosen.CompareTo(stored.e3Pair) >= 0)
                    {
                        verdict.kind = MeshOperationVerdictKind.Resume;
                        verdict.splitStep = MeshSplitStep.E3Broadcast;
                        verdict.reason = "e3 was broadcast before the restart";
                        return verdict;
                    }
                    verdict.kind = MeshOperationVerdictKind.Resume;
                    verdict.splitStep = MeshSplitStep.DrainGate;
                    verdict.reason = "the drain gate re-opens on fresh evidence";
                    return verdict;
                case MeshSplitStep.Done:
                    if (stored.stragglers > 0 && !stored.settled)
                    {
                        verdict.kind = MeshOperationVerdictKind.ResumeSettle;
                        verdict.reason = $"{stored.stragglers} straggler(s) unsettled";
                        return verdict;
                    }
                    verdict.kind = MeshOperationVerdictKind.Drop;
                    verdict.reason = "done and settled";
                    return verdict;
                case MeshSplitStep.Frozen:
                    if (!stored.e2Pair.IsNone && Holds(held, stored.childId, stored.e2Pair) && chosen.CompareTo(stored.e2Pair) < 0)
                    {
                        verdict.splitStep = MeshSplitStep.E2Staged;
                        return Resend(verdict, "e2", stored.childId);
                    }
                    verdict.kind = MeshOperationVerdictKind.Drop;
                    verdict.reason = "frozen before the restart";
                    return verdict;
                default:
                    verdict.kind = MeshOperationVerdictKind.Drop;
                    verdict.reason = $"ended ({step})";
                    return verdict;
            }
        }

        private static MeshOperationVerdict ResolveMerge(MeshStoredOperation stored, MeshLayoutPair chosen, IReadOnlyDictionary<string, MeshLayoutPair> held, MeshOperationVerdict verdict)
        {
            if (!stored.TryMergeStep(out MeshMergeStep step))
            {
                verdict.kind = MeshOperationVerdictKind.Drop;
                verdict.reason = $"{stored.opId} has an unknown step {stored.step}";
                return verdict;
            }
            verdict.mergeStep = step;
            switch (step)
            {
                case MeshMergeStep.E1Gate:
                    verdict.kind = MeshOperationVerdictKind.Abort;
                    verdict.reason = "the central restarted before e2";
                    return verdict;
                case MeshMergeStep.E2Staged:
                    if (Holds(held, stored.survivorId, stored.e2Pair))
                        return Resend(verdict, "e2", stored.survivorId);
                    verdict.kind = MeshOperationVerdictKind.Abort;
                    verdict.reason = "the central restarted before e2";
                    return verdict;
                case MeshMergeStep.DrainGate:
                case MeshMergeStep.Retiring:
                case MeshMergeStep.E3Broadcast:
                case MeshMergeStep.TidyBroadcast:
                    verdict.kind = MeshOperationVerdictKind.Resume;
                    verdict.reason = $"resumed at {step}";
                    return verdict;
                case MeshMergeStep.Frozen:
                    if (!stored.e2Pair.IsNone && Holds(held, stored.survivorId, stored.e2Pair) && chosen.CompareTo(stored.e2Pair) < 0)
                    {
                        verdict.mergeStep = MeshMergeStep.E2Staged;
                        return Resend(verdict, "e2", stored.survivorId);
                    }
                    verdict.kind = MeshOperationVerdictKind.Drop;
                    verdict.reason = "frozen before the restart";
                    return verdict;
                default:
                    verdict.kind = MeshOperationVerdictKind.Drop;
                    verdict.reason = $"ended ({step})";
                    return verdict;
            }
        }

        private static MeshOperationVerdict Resend(MeshOperationVerdict verdict, string stepName, string target)
        {
            verdict.kind = MeshOperationVerdictKind.ResendStaged;
            verdict.reason = $"{target} holds the staged {stepName}: broadcast";
            return verdict;
        }
    }
}
