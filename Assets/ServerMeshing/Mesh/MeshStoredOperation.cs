// Server Meshing add-on — the operation the central's coordinator was running, as its store keeps it (Slice 5,
// slices/slice5_design.md decision 6): enough of a split or a merge to rebuild it after a central restart, so the
// resolver (MeshRecoveryResolver.ResolveOperation) can retire, resume or drop it, and the coordinator
// (MeshLayoutCoordinator_Resume) can carry it on. The step stored is the step being entered (the store is written after
// the step is set and before anything is sent). Pure.
using System;
using System.Globalization;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>MeshStoredOperation is a layout operation as the coordinator store holds it between two central lifetimes.</summary>
    [Serializable]
    public sealed class MeshStoredOperation
    {
        public const string SplitKind = "split";
        public const string MergeKind = "merge";

        public string kind = "";
        public string opId = "";
        /// <summary>The step's enum text (MeshSplitStep or MeshMergeStep): the step the operation was entering when saved.</summary>
        public string step = "";
        public string baseId = "";
        public string mapId = "";

        // A split (Slice 2b).
        public string parentId = "";
        public string childId = "";
        public int axis;
        public float at;
        public bool childOnPositiveSide;
        public MeshRect parentOwned, parentInterest, given, kept;
        public int stragglers;
        public bool settled;

        // A merge (Slice 2c; collapse: Slice 2d).
        public string survivorId = "";
        public string retiringId = "";
        public MeshRect survivorOwned, retiringOwned, union;
        public bool collapse;
        public bool hasSurvivorEntry;
        public MeshLayoutEntry survivorEntry = EmptyEntry();
        public bool hasRetiringEntry;
        public MeshLayoutEntry retiringEntry = EmptyEntry();

        public MeshLayoutPair e1Pair, e2Pair, e3Pair, tidyPair;

        public bool IsSplit => kind == SplitKind;
        public bool IsMerge => kind == MergeKind;

        /// <summary>The operation's dynamic cell: a split's child, a merge's retiring cell.</summary>
        public string DynamicId => IsSplit ? childId : retiringId;

        /// <summary>The newest pair the operation issued (None before its first step).</summary>
        public MeshLayoutPair NewestPair
        {
            get
            {
                MeshLayoutPair newest = e1Pair;
                foreach (MeshLayoutPair pair in new[] { e2Pair, e3Pair, tidyPair })
                {
                    if (pair.IsNewerThan(newest))
                        newest = pair;
                }
                return newest;
            }
        }

        private static MeshLayoutEntry EmptyEntry() => new MeshLayoutEntry { id = "", mapId = "", baseId = "" };

        /// <summary>
        /// ShouldStore: an operation is kept in the store while it runs or froze, and a split done with stragglers whose
        /// settle is still pending (decision 6); Done, Aborted and Ended operations are not.
        /// </summary>
        public static bool ShouldStore(MeshLayoutOperation op)
        {
            if (op == null)
                return false;
            if (op.InProgress || op.IsFrozen)
                return true;
            return op is MeshSplitOperation split && split.step == MeshSplitStep.Done && split.stragglers > 0 && !split.settled;
        }

        /// <summary>From copies what a rebuild needs out of a running operation (null for null).</summary>
        public static MeshStoredOperation From(MeshLayoutOperation op)
        {
            if (op == null)
                return null;
            MeshStoredOperation stored = new MeshStoredOperation
            {
                opId = op.opId ?? "", step = op.StepText ?? "", baseId = op.baseId ?? "", mapId = op.mapId ?? "",
                e1Pair = op.e1Pair, e2Pair = op.e2Pair, e3Pair = op.e3Pair,
            };
            if (op is MeshSplitOperation split)
            {
                stored.kind = SplitKind;
                stored.parentId = split.parentId ?? "";
                stored.childId = split.childId ?? "";
                stored.axis = (int)split.axis;
                stored.at = split.at;
                stored.childOnPositiveSide = split.childOnPositiveSide;
                stored.parentOwned = split.parentOwned;
                stored.parentInterest = split.parentInterest;
                stored.given = split.given;
                stored.kept = split.kept;
                stored.stragglers = split.stragglers;
                stored.settled = split.settled;
            }
            else if (op is MeshMergeOperation merge)
            {
                stored.kind = MergeKind;
                stored.survivorId = merge.survivorId ?? "";
                stored.retiringId = merge.retiringId ?? "";
                stored.survivorOwned = merge.survivorOwned;
                stored.retiringOwned = merge.retiringOwned;
                stored.union = merge.union;
                stored.collapse = merge.collapse;
                stored.tidyPair = merge.tidyPair;
                stored.hasSurvivorEntry = merge.survivorEntry != null;
                stored.survivorEntry = merge.survivorEntry != null ? Filled(merge.survivorEntry.Clone()) : EmptyEntry();
                stored.hasRetiringEntry = merge.retiringEntry != null;
                stored.retiringEntry = merge.retiringEntry != null ? Filled(merge.retiringEntry.Clone()) : EmptyEntry();
            }
            return stored;
        }

        private static MeshLayoutEntry Filled(MeshLayoutEntry entry)
        {
            entry.id = entry.id ?? "";
            entry.mapId = entry.mapId ?? "";
            entry.baseId = entry.baseId ?? "";
            return entry;
        }

        /// <summary>TrySplitStep reads the stored step as a split's (false for a merge or an unknown text).</summary>
        public bool TrySplitStep(out MeshSplitStep value)
        {
            value = default;
            return IsSplit && Enum.TryParse(step, out value);
        }

        /// <summary>TryMergeStep reads the stored step as a merge's (false for a split or an unknown text).</summary>
        public bool TryMergeStep(out MeshMergeStep value)
        {
            value = default;
            return IsMerge && Enum.TryParse(step, out value);
        }

        /// <summary>
        /// ToSplit rebuilds the split at <paramref name="step"/>: its geometry and pairs from here, e1Broadcast derived from the
        /// stored broadcast (the flag was set after its save), the bindings and timers left for the coordinator.
        /// </summary>
        public MeshSplitOperation ToSplit(MeshSplitStep step, MeshLayoutPair storedBroadcast)
        {
            MeshSplitOperation op = new MeshSplitOperation(opId)
            {
                parentId = parentId, childId = childId, mapId = mapId, baseId = baseId, axis = (MeshSplitAxis)axis, at = at,
                childOnPositiveSide = childOnPositiveSide, parentOwned = parentOwned, parentInterest = parentInterest,
                given = given, kept = kept, stragglers = stragglers, settled = settled, step = step,
                e1Pair = e1Pair, e2Pair = e2Pair, e3Pair = e3Pair, spawnAnswered = true,
            };
            op.e1Broadcast = E1Broadcast(storedBroadcast, e1Pair);
            return op;
        }

        /// <summary>ToMerge rebuilds the merge at <paramref name="step"/> (the original entries for a Reverted abort included).</summary>
        public MeshMergeOperation ToMerge(MeshMergeStep step, MeshLayoutPair storedBroadcast)
        {
            MeshMergeOperation op = new MeshMergeOperation(opId)
            {
                survivorId = survivorId, retiringId = retiringId, mapId = mapId, baseId = baseId, survivorOwned = survivorOwned,
                retiringOwned = retiringOwned, union = union, collapse = collapse, step = step,
                e1Pair = e1Pair, e2Pair = e2Pair, e3Pair = e3Pair, tidyPair = tidyPair,
                survivorEntry = hasSurvivorEntry ? survivorEntry.Clone() : null,
                retiringEntry = hasRetiringEntry ? retiringEntry.Clone() : null,
            };
            op.e1Broadcast = E1Broadcast(storedBroadcast, e1Pair);
            return op;
        }

        /// <summary>E1Broadcast: the stored broadcast is at or past e1, so e1 reached every cell (an abort must undo it).</summary>
        public static bool E1Broadcast(MeshLayoutPair storedBroadcast, MeshLayoutPair e1Pair)
        {
            return !e1Pair.IsNone && storedBroadcast.CompareTo(e1Pair) >= 0;
        }

        /// <summary>Number is the counter in an op id such as split-3 (0 when there is none).</summary>
        public static int Number(string opId)
        {
            int dash = opId != null ? opId.LastIndexOf('-') : -1;
            return dash >= 0 && int.TryParse(opId.Substring(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : 0;
        }

        /// <summary>
        /// ExcludedId (decision 1): the operation's dynamic cell when its hello cannot change what becomes of the operation,
        /// so recovery must not wait for it: a split's child before any step was staged to it or once e1 went out to
        /// everyone (its absence aborts either way), a merge's retiring cell from the commit broadcast on (its absence is the
        /// exit the step waits for). Null otherwise: a staged step's target is waited for, since its hello decides whether
        /// the step is re-sent or dropped.
        /// </summary>
        public string ExcludedId
        {
            get
            {
                if (IsSplit && TrySplitStep(out MeshSplitStep split))
                    return split <= MeshSplitStep.Spawning || split == MeshSplitStep.E1Gate ? childId : null;
                if (IsMerge && TryMergeStep(out MeshMergeStep merge))
                    return merge >= MeshMergeStep.DrainGate && merge <= MeshMergeStep.TidyBroadcast ? retiringId : null;
                return null;
            }
        }

        /// <summary>
        /// AbortableId (decision 3): the operation's dynamic cell while the operation is before its commit broadcast (or
        /// frozen): its absence at the ceiling never freezes its family, because the verdict retires or reverts it anyway.
        /// Not a collapse's retiring cell (DESIGN 0.85): a reverted collapse gives the dead cell its rectangle back, so its
        /// absence must freeze the family again for the operator's unfreeze.
        /// </summary>
        public string AbortableId
        {
            get
            {
                if (IsSplit && TrySplitStep(out MeshSplitStep split))
                    return split <= MeshSplitStep.E2Staged || split == MeshSplitStep.Frozen ? childId : null;
                if (IsMerge && TryMergeStep(out MeshMergeStep merge))
                    return !collapse && (merge <= MeshMergeStep.E2Staged || merge == MeshMergeStep.Frozen) ? retiringId : null;
                return null;
            }
        }
    }
}
