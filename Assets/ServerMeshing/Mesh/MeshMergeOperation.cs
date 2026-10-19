// Server Meshing add-on — one merge of a child back into its family's base cell as the central's coordinator runs it
// (Slice 2c, slices/slice2c_design.md decisions 1-7, plan D4): the operation's state and its pure parts — the steps,
// the union geometry, the layouts each step stages or broadcasts (E1, E2, E3, Reverted, Tidy), the tidy condition, and
// the gate predicates over the cells' MeshLayoutEvidenceMessage. The coordinator (MeshLayoutCoordinator_Merge*.cs)
// does the sending, the store and the logs.
using System;
using System.Collections.Generic;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>MeshMergeStep is where a merge is: the survivor owns the union before the retiring cell lets go of anything.</summary>
    public enum MeshMergeStep
    {
        /// <summary>e1 (the survivor's interest becomes the union) was broadcast; waiting for every ack and the survivor's evidence.</summary>
        E1Gate,
        /// <summary>e2 (the survivor owns the union; the retiring cell Draining) was sent to the survivor alone.</summary>
        E2Staged,
        /// <summary>e2 was broadcast; waiting for every ack and the retiring cell's drained evidence.</summary>
        DrainGate,
        /// <summary>Retire was sent to the retiring cell; waiting for it to leave the cluster.</summary>
        Retiring,
        /// <summary>e3 (the retiring cell Retired) was broadcast; waiting for every participant's ack.</summary>
        E3Broadcast,
        /// <summary>The tidy version (the family's entries dropped) was broadcast; waiting for every participant's ack.</summary>
        TidyBroadcast,
        Done,
        Aborted,
        Frozen,
    }

    /// <summary>MeshMergeOperation is one merge in progress, or the last one that ended.</summary>
    public sealed class MeshMergeOperation : MeshLayoutOperation
    {
        /// <summary>The survivor (A), which takes the union: the family's base cell when it is one of the two (it is never retired), else the child holding more players (Slice 3).</summary>
        public string survivorId;
        /// <summary>The child (B) that drains to the survivor and exits.</summary>
        public string retiringId;
        public MeshRect survivorOwned;
        public MeshRect retiringOwned;
        /// <summary>A ∪ B, a rectangle (MeshRect.TryUnion).</summary>
        public MeshRect union;
        /// <summary>The two cells' entries as the broadcast held them when the merge was requested (null when it had none).</summary>
        public MeshLayoutEntry survivorEntry;
        public MeshLayoutEntry retiringEntry;

        public MeshMergeStep step = MeshMergeStep.E1Gate;

        /// <summary>The bindings when the merge started: a new incarnation before e2 aborts it, after e2 it is answered and the merge goes on.</summary>
        public long survivorConnection = -1;
        public string survivorIncarnation = "";
        public long retiringConnection = -1;
        public string retiringIncarnation = "";

        public MeshLayoutPair tidyPair;
        /// <summary>When every participant had acknowledged e2 (-1 before): the drained window must arrive a load window later.</summary>
        public float e2AckedAt = -1f;
        /// <summary>Since when the retiring cell has been seen gone from the cluster without a break (-1 while registered).</summary>
        public float retiringGoneSince = -1f;
        /// <summary>When Retire was last sent (resent every RetireResendSeconds).</summary>
        public float retireSentAt = -1f;
        /// <summary>When the next straggling line is due in the drain or the retire step (-1 until the first).</summary>
        public float nextStraggleAt = -1f;
        /// <summary>Whether the retiring cell left the cluster before it reported drained.</summary>
        public bool exitedBeforeDrained;
        /// <summary>
        /// A collapse (Slice 2d decision 3): the supervisor merges a child that failed its restarts into its base. The child
        /// is down and unbound, so there is no drain evidence, no Retire and no exit wait; its loss never aborts.
        /// </summary>
        public bool collapse;
        /// <summary>When the retire step's spawner kill was sent (-1 before; Slice 2d decision 4, past retireKillAfterSeconds).</summary>
        public float retireKillAt = -1f;

        public MeshMergeOperation(string opId) : base(opId, MeshLayoutOpKind.Merge)
        {
        }

        public override bool InProgress => step != MeshMergeStep.Done && step != MeshMergeStep.Aborted && step != MeshMergeStep.Frozen;

        /// <summary>From e2 staged to the survivor on, the survivor may own the union: a failure freezes instead of reverting.</summary>
        public override bool PastCommit => step >= MeshMergeStep.E2Staged;

        /// <summary>Whether e2 was broadcast: from the drain gate on.</summary>
        public override bool CommitBroadcast => step >= MeshMergeStep.DrainGate;

        public override bool IsFrozen => step == MeshMergeStep.Frozen;
        public override bool IsAborted => step == MeshMergeStep.Aborted;
        public override bool CommitStaged => step == MeshMergeStep.E2Staged;
        public override string StepText => step.ToString();
        public override string StagedTargetId => survivorId;
        public override long StagedTargetConnection { get => survivorConnection; set => survivorConnection = value; }
        public override string StagedTargetIncarnation { get => survivorIncarnation; set => survivorIncarnation = value; }
        public override IEnumerable<string> SeedParticipants => new[] { survivorId, retiringId };
        public override bool IsMember(string cellId) => SameId(cellId, survivorId) || SameId(cellId, retiringId);
        internal override void MarkAborted() => step = MeshMergeStep.Aborted;
        internal override void MarkFrozen() => step = MeshMergeStep.Frozen;

        public override MeshLayoutPair CurrentStepPair
        {
            get
            {
                switch (step)
                {
                    case MeshMergeStep.E1Gate:
                        return e1Pair;
                    case MeshMergeStep.E2Staged:
                    case MeshMergeStep.DrainGate:
                    case MeshMergeStep.Retiring:
                        return e2Pair;
                    case MeshMergeStep.E3Broadcast:
                        return e3Pair;
                    case MeshMergeStep.TidyBroadcast:
                        return tidyPair;
                    default:
                        return MeshLayoutPair.None;
                }
            }
        }

        public override string CurrentStepName
        {
            get
            {
                switch (step)
                {
                    case MeshMergeStep.E1Gate:
                        return "e1";
                    case MeshMergeStep.E2Staged:
                    case MeshMergeStep.DrainGate:
                    case MeshMergeStep.Retiring:
                        return "e2";
                    case MeshMergeStep.E3Broadcast:
                        return "e3";
                    case MeshMergeStep.TidyBroadcast:
                        return "tidy";
                    default:
                        return step.ToString();
                }
            }
        }

        /// <summary>E1 prepares: the survivor's interest becomes the union, it still owns its own rectangle.</summary>
        public MeshLayoutSnapshot E1(MeshLayoutSnapshot broadcast, MeshLayoutPair pair)
        {
            MeshLayoutSnapshot next = Next(broadcast, pair);
            SetEntry(next, Entry(survivorId, MeshCellState.Active, survivorOwned, union));
            return next;
        }

        /// <summary>E2 commits: the survivor Active owning the union; the retiring cell Draining, owning nothing, its interest its old rectangle.</summary>
        public MeshLayoutSnapshot E2(MeshLayoutSnapshot broadcast, MeshLayoutPair pair)
        {
            MeshLayoutSnapshot next = Next(broadcast, pair);
            SetEntry(next, Entry(survivorId, MeshCellState.Active, union, union));
            SetEntry(next, Entry(retiringId, MeshCellState.Draining, MeshRect.Empty, retiringOwned));
            return next;
        }

        /// <summary>E3 retires: the retiring cell Retired, owning nothing and interested in nothing.</summary>
        public MeshLayoutSnapshot E3(MeshLayoutSnapshot broadcast, MeshLayoutPair pair)
        {
            MeshLayoutSnapshot next = Next(broadcast, pair);
            SetEntry(next, Entry(retiringId, MeshCellState.Retired, MeshRect.Empty, MeshRect.Empty));
            return next;
        }

        /// <summary>Reverted undoes e1 (an abort before e2): both cells' entries as they were when the merge was requested.</summary>
        public MeshLayoutSnapshot Reverted(MeshLayoutSnapshot broadcast, MeshLayoutPair pair)
        {
            MeshLayoutSnapshot next = Next(broadcast, pair);
            Restore(next, survivorId, survivorEntry);
            Restore(next, retiringId, retiringEntry);
            return next;
        }

        /// <summary>Tidy drops every entry of the family: the base resets to its asset rectangle, the retired cells stay Retired by absence.</summary>
        public MeshLayoutSnapshot Tidy(MeshLayoutSnapshot broadcast, MeshLayoutPair pair)
        {
            MeshLayoutSnapshot next = Next(broadcast, pair);
            next.entries.RemoveAll(e => e != null && SameId(e.baseId, baseId));
            return next;
        }

        /// <summary>
        /// TidyApplies: the tidy may run when the survivor is Active with owned = interest = its base rectangle and is its
        /// family's only live cell (every other entry of the family Retired). <paramref name="why"/> names the first miss.
        /// </summary>
        public bool TidyApplies(MeshLayoutSnapshot broadcast, MeshRect baseRect, out string why)
        {
            why = null;
            MeshLayoutEntry survivor = null;
            foreach (MeshLayoutEntry entry in broadcast.entries)
            {
                if (entry == null || !SameId(entry.baseId, baseId))
                    continue;
                if (SameId(entry.id, survivorId))
                    survivor = entry;
                else if (entry.state != MeshCellState.Retired)
                {
                    why = $"{entry.id} is still {entry.state}";
                    return false;
                }
            }
            if (survivor == null)
                return true;   // nothing of the family is left to drop
            if (survivor.state != MeshCellState.Active || !Near(survivor.owned, baseRect) || !Near(survivor.interest, baseRect))
            {
                why = $"{survivorId} is {survivor.state} owning {survivor.owned}, interest {survivor.interest}, not its base {baseRect}";
                return false;
            }
            return true;
        }

        private static bool Near(MeshRect a, MeshRect b)
        {
            const float eps = 1e-3f;
            return Math.Abs(a.minX - b.minX) <= eps && Math.Abs(a.minZ - b.minZ) <= eps && Math.Abs(a.maxX - b.maxX) <= eps && Math.Abs(a.maxZ - b.maxZ) <= eps;
        }

        private MeshLayoutEntry Entry(string id, MeshCellState state, MeshRect owned, MeshRect interest)
        {
            return new MeshLayoutEntry { id = id, mapId = mapId, baseId = baseId, state = state, owned = owned, interest = interest };
        }

        private static MeshLayoutSnapshot Next(MeshLayoutSnapshot broadcast, MeshLayoutPair pair)
        {
            MeshLayoutSnapshot next = broadcast.Clone();
            next.pair = pair;
            return next;
        }

        private static void Restore(MeshLayoutSnapshot snapshot, string id, MeshLayoutEntry original)
        {
            if (original != null)
                SetEntry(snapshot, original.Clone());
            else
                snapshot.entries.RemoveAll(e => e != null && SameId(e.id, id));
        }
    }

    /// <summary>MeshMergeGates are the merge's gate predicates and timings. Pure.</summary>
    public static class MeshMergeGates
    {
        /// <summary>One load window: the drained window must be received at least this long after the last e2 ack.</summary>
        public const float LoadWindowSeconds = 5f;
        /// <summary>The retiring cell counts as exited once unregistered this long without a break (a cluster blip does not count).</summary>
        public const float ExitedAfterSeconds = 5f;
        /// <summary>Retire is sent again this often until the retiring cell has exited.</summary>
        public const float RetireResendSeconds = 10f;
        /// <summary>Past retireMaxSeconds, a straggling line is logged this often.</summary>
        public const float StraggleLineSeconds = 30f;

        /// <summary>
        /// SurvivorReady is the merge's e1 gate on the survivor's evidence: a window at or after e1, started after that apply,
        /// naming every band neighbour's hello at least <paramref name="prewarmSeconds"/> old with a datagram since, and an
        /// empty spawn queue. Unlike a new child's pre-warm, no saturation or p95 clause (Slice 2c decision 5).
        /// </summary>
        public static bool SurvivorReady(MeshLayoutEvidenceMessage evidence, MeshLayoutPair e1, IReadOnlyList<string> neighbours, float prewarmSeconds, out string why)
        {
            return MeshSplitGates.PrewarmCore(evidence, e1, neighbours, prewarmSeconds, false, out why);
        }

        /// <summary>
        /// DrainWindowQualifies: the retiring cell's window counts when it is at or after e2, started after that apply, and
        /// was received at least a load window after the last e2 ack (so every cell that routed to it has applied e2).
        /// </summary>
        public static bool DrainWindowQualifies(MeshLayoutEvidenceMessage evidence, MeshLayoutPair e2, float receivedAt, float e2AckedAt, out string why)
        {
            why = null;
            if (evidence.pair.CompareTo(e2) < 0)
                why = $"evidence at {evidence.pair}, before e2 {e2}";
            else if (evidence.windowStartedAfterApplySeconds < 0f)
                why = "the window started before the apply";
            else if (e2AckedAt < 0f)
                why = "e2 is not acknowledged by every participant";
            else if (receivedAt < e2AckedAt + LoadWindowSeconds)
                why = $"the window arrived {MeshLayoutOperation.Number(Math.Max(0f, receivedAt - e2AckedAt))} s after the last e2 ack, under {MeshLayoutOperation.Number(LoadWindowSeconds)} s";
            return why == null;
        }

        /// <summary>Drained: a qualifying window of the retiring cell that reports drained. <paramref name="why"/> names what is left.</summary>
        public static bool Drained(MeshLayoutEvidenceMessage evidence, MeshLayoutPair e2, float receivedAt, float e2AckedAt, out string why)
        {
            if (!DrainWindowQualifies(evidence, e2, receivedAt, e2AckedAt, out why))
                return false;
            if (!evidence.drained)
            {
                why = "still holds " + evidence.drainState.Describe();
                return false;
            }
            return true;
        }

        /// <summary>Exited: unregistered since <paramref name="goneSince"/> (-1 while registered) for at least ExitedAfterSeconds.</summary>
        public static bool Exited(float goneSince, float now) => goneSince >= 0f && now - goneSince >= ExitedAfterSeconds;
    }
}
