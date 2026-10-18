// Server Meshing add-on — what every layout operation of the central's coordinator shares (Slice 2c decision 4,
// slices/slice2c_design.md): a split (MeshSplitOperation, Slice 2b) and a merge (MeshMergeOperation, Slice 2c) both run
// as numbered steps, stage at most one layout to one cell before broadcasting it, wait for their participants' acks, and
// end Done, Aborted (undone) or Frozen. The coordinator (MeshLayoutCoordinator_Split*.cs, _Merge*.cs) drives them
// through this base: the store, the staged re-answer, the undo rule, the freeze and the op lines. Pure.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>MeshLayoutOpKind is what an operation does to the layout.</summary>
    public enum MeshLayoutOpKind : byte
    {
        Split = 0,
        Merge = 1,
    }

    /// <summary>MeshLayoutOperation is one layout operation in progress, or the last one that ended.</summary>
    public abstract class MeshLayoutOperation
    {
        public string opId;
        public readonly MeshLayoutOpKind kind;
        public string mapId;
        /// <summary>The asset cell whose family the operation changes (its cooldown and its "never slept" rule).</summary>
        public string baseId;

        public float requestedAt;
        /// <summary>When the current step started (its silence timer).</summary>
        public float stepStartedAt;

        public MeshLayoutPair e1Pair, e2Pair, e3Pair;
        /// <summary>The layout staged for the staged target (null when none is), and the name of its step ("e1", "e2").</summary>
        public MeshLayoutSnapshot staged;
        public string stagedStepName = "";
        /// <summary>Whether e1 reached every cell (an abort then broadcasts a version that undoes it).</summary>
        public bool e1Broadcast;
        /// <summary>The cells whose ack the current broadcast step waits for.</summary>
        public readonly List<string> participants = new List<string>();
        /// <summary>The band neighbours whose hellos the e1 gate's evidence must name (the new child's, or the survivor's).</summary>
        public readonly List<string> prewarmNeighbours = new List<string>();
        public string lastGateWait = "";
        public string endReason = "";

        protected MeshLayoutOperation(string opId, MeshLayoutOpKind kind)
        {
            this.opId = opId;
            this.kind = kind;
        }

        /// <summary>LinePrefix starts every line of the operation: "Split" or "Merge".</summary>
        public string LinePrefix => kind == MeshLayoutOpKind.Merge ? "Merge" : "Split";

        /// <summary>Whether the operation still runs (Frozen is ended but keeps every later operation out).</summary>
        public abstract bool InProgress { get; }

        /// <summary>Whether the commit step has been sent: from here a failure freezes the operation instead of undoing it.</summary>
        public abstract bool PastCommit { get; }

        public abstract bool IsFrozen { get; }

        /// <summary>Whether it ended undone (nothing of it is left in the layout).</summary>
        public abstract bool IsAborted { get; }

        /// <summary>Whether the step in progress is the commit step staged to its target alone (its rejection can still be undone).</summary>
        public abstract bool CommitStaged { get; }

        /// <summary>StepText is the step's name for the store and an operator.</summary>
        public abstract string StepText { get; }

        /// <summary>The cell a step is staged to first: the split's child, the merge's survivor.</summary>
        public abstract string StagedTargetId { get; }
        public abstract long StagedTargetConnection { get; set; }
        public abstract string StagedTargetIncarnation { get; set; }

        /// <summary>The pair the current step waits on (None while nothing was sent yet), and its name in lines.</summary>
        public abstract MeshLayoutPair CurrentStepPair { get; }
        public abstract string CurrentStepName { get; }

        /// <summary>The cells every broadcast step of the operation has as participants, before the changed ones are added.</summary>
        public abstract IEnumerable<string> SeedParticipants { get; }

        /// <summary>IsMember: the cell is one the operation runs on (the split's child or parent, the merge's two cells).</summary>
        public abstract bool IsMember(string cellId);

        internal abstract void MarkAborted();
        internal abstract void MarkFrozen();

        /// <summary>Whether the operation keeps its family out of the awake set: it runs, or it froze.</summary>
        public bool HoldsFamily => InProgress || IsFrozen;

        public string StepId(string stepName) => opId + "/" + stepName;

        public static string Number(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        protected static bool SameId(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        /// <summary>SetEntry puts <paramref name="entry"/> in the snapshot in place of the entry with its id (or adds it).</summary>
        protected static void SetEntry(MeshLayoutSnapshot snapshot, MeshLayoutEntry entry)
        {
            for (int i = 0; i < snapshot.entries.Count; ++i)
            {
                if (SameId(snapshot.entries[i].id, entry.id))
                {
                    snapshot.entries[i] = entry;
                    return;
                }
            }
            snapshot.entries.Add(entry);
        }
    }
}
