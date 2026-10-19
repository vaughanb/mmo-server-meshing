// Server Meshing add-on — one split of a Dynamic cell as the central's coordinator runs it (Slice 2b,
// slices/slice2b_design.md decisions 2-8, plan D4). The operation's state and its pure parts: the steps, the geometry
// of the cut, the layouts each step stages or broadcasts, and the two gate predicates over a cell's
// MeshLayoutEvidenceMessage. The coordinator (MeshLayoutCoordinator_Split.cs) does the sending, the store and the logs.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>MeshSplitAxis is the axis a cut is made across: x cuts at an x (a line along z), z at a z.</summary>
    public enum MeshSplitAxis : byte
    {
        X = 0,
        Z = 1,
    }

    /// <summary>MeshSplitStep is where a split is (plan D4): the order keeps exactly one owner of every position.</summary>
    public enum MeshSplitStep
    {
        /// <summary>The kit's channel for the child exists; the spawn is next.</summary>
        ChannelAdded,
        /// <summary>The child's process was asked for; waiting for its first LayoutHello.</summary>
        Spawning,
        /// <summary>e1 (child Pending, owns nothing, interest R) was sent to the child alone; waiting for its ack.</summary>
        E1Staged,
        /// <summary>e1 was broadcast; waiting for every participant's ack and the child's pre-warm evidence.</summary>
        E1Gate,
        /// <summary>e2 (child owns R; the parent owns P minus R, interest still P) was sent to the child alone.</summary>
        E2Staged,
        /// <summary>e2 was broadcast; waiting for every participant's ack and the parent's drain evidence.</summary>
        DrainGate,
        /// <summary>e3 (the parent's interest shrinks) was broadcast; waiting for every participant's ack.</summary>
        E3Broadcast,
        Done,
        Aborted,
        Frozen,
        /// <summary>The child was queued for collapse after its failed restarts (Slice 2d decision 6): ended, not frozen.</summary>
        Ended,
    }

    /// <summary>MeshSplitOperation is one split in progress, or the last one that ended.</summary>
    public sealed class MeshSplitOperation : MeshLayoutOperation
    {
        /// <summary>The cell being split (P): a base cell or an earlier child.</summary>
        public string parentId;
        public string childId;
        public MeshSplitAxis axis;
        public float at;
        /// <summary>True when the child takes the side at or above the line.</summary>
        public bool childOnPositiveSide;
        /// <summary>P's owned rectangle when the split was requested, and its interest.</summary>
        public MeshRect parentOwned;
        public MeshRect parentInterest;
        /// <summary>R: the rectangle the child takes; and what P keeps.</summary>
        public MeshRect given;
        public MeshRect kept;

        public MeshSplitStep step = MeshSplitStep.ChannelAdded;

        /// <summary>The child's process once its first hello bound it.</summary>
        public bool childBound;
        public long childConnection = -1;
        public string childIncarnation = "";
        /// <summary>P's binding when the split started: a new incarnation of P mid-split aborts or freezes it.</summary>
        public long parentConnection = -1;
        public string parentIncarnation = "";
        /// <summary>Whether the kit's spawn callback has been seen once (every later call is ignored).</summary>
        public bool spawnAnswered;

        /// <summary>The drain: N_R from P's first qualifying window, and the deadline it gives (-1 until known).</summary>
        public int drainInitial = -1;
        public float drainDeadline = -1f;
        public int stragglers;
        /// <summary>After a drain timeout: the parent's interest was shrunk once its stragglers were gone (plan D4).</summary>
        public bool settled;

        public MeshSplitOperation(string opId) : base(opId, MeshLayoutOpKind.Split)
        {
        }

        /// <summary>Whether the operation still runs (Frozen is ended but keeps every later operation out).</summary>
        public override bool InProgress => step != MeshSplitStep.Done && step != MeshSplitStep.Aborted && step != MeshSplitStep.Frozen && step != MeshSplitStep.Ended;

        /// <summary>Whether e2 has been sent to the child: from here a failure freezes the operation instead of aborting it.</summary>
        public override bool PastCommit => step >= MeshSplitStep.E2Staged;

        /// <summary>Whether e2 was broadcast: from the drain gate on (a split in E2Staged has not committed).</summary>
        public override bool CommitBroadcast => step >= MeshSplitStep.DrainGate;

        public override bool IsFrozen => step == MeshSplitStep.Frozen;
        public override bool IsAborted => step == MeshSplitStep.Aborted;
        public override bool CommitStaged => step == MeshSplitStep.E2Staged;
        public override string StepText => step.ToString();
        public override string StagedTargetId => childId;
        public override long StagedTargetConnection { get => childConnection; set => childConnection = value; }
        public override string StagedTargetIncarnation { get => childIncarnation; set => childIncarnation = value; }
        public override IEnumerable<string> SeedParticipants => new[] { childId, parentId };
        public override bool IsMember(string cellId) => SameId(cellId, childId) || SameId(cellId, parentId);
        internal override void MarkAborted() => step = MeshSplitStep.Aborted;
        internal override void MarkFrozen() => step = MeshSplitStep.Frozen;

        public override MeshLayoutPair CurrentStepPair
        {
            get
            {
                switch (step)
                {
                    case MeshSplitStep.E1Staged:
                    case MeshSplitStep.E1Gate:
                        return e1Pair;
                    case MeshSplitStep.E2Staged:
                    case MeshSplitStep.DrainGate:
                        return e2Pair;
                    case MeshSplitStep.E3Broadcast:
                        return e3Pair;
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
                    case MeshSplitStep.E1Staged:
                    case MeshSplitStep.E1Gate:
                        return "e1";
                    case MeshSplitStep.E2Staged:
                    case MeshSplitStep.DrainGate:
                        return "e2";
                    case MeshSplitStep.E3Broadcast:
                        return "e3";
                    default:
                        return step.ToString();
                }
            }
        }

        public string SideText => childOnPositiveSide ? "+" : "-";

        public string AxisText => axis == MeshSplitAxis.X ? "x" : "z";

        /// <summary>
        /// Cut divides <paramref name="owned"/> at the line: the child's side and what the parent keeps. False when the
        /// line does not run strictly inside the rectangle.
        /// </summary>
        public static bool Cut(MeshRect owned, MeshSplitAxis axis, float at, bool childOnPositiveSide, out MeshRect given, out MeshRect kept)
        {
            given = kept = MeshRect.Empty;
            if (owned.IsEmpty)
                return false;
            float min = axis == MeshSplitAxis.X ? owned.minX : owned.minZ;
            float max = axis == MeshSplitAxis.X ? owned.maxX : owned.maxZ;
            if (!(at > min && at < max))
                return false;
            MeshRect low = axis == MeshSplitAxis.X ? new MeshRect(owned.minX, owned.minZ, at, owned.maxZ) : new MeshRect(owned.minX, owned.minZ, owned.maxX, at);
            MeshRect high = axis == MeshSplitAxis.X ? new MeshRect(at, owned.minZ, owned.maxX, owned.maxZ) : new MeshRect(owned.minX, at, owned.maxX, owned.maxZ);
            given = childOnPositiveSide ? high : low;
            kept = childOnPositiveSide ? low : high;
            return true;
        }

        /// <summary>E1 is the broadcast plus the child, Pending, owning nothing, with interest R (P unchanged).</summary>
        public MeshLayoutSnapshot E1(MeshLayoutSnapshot broadcast, MeshLayoutPair pair)
        {
            MeshLayoutSnapshot next = WithoutChild(broadcast, pair);
            next.entries.Add(new MeshLayoutEntry { id = childId, mapId = mapId, baseId = baseId, state = MeshCellState.Pending, owned = MeshRect.Empty, interest = given });
            return next;
        }

        /// <summary>E2 commits: the child Active owning R; P owns what it keeps, its interest still its old rectangle.</summary>
        public MeshLayoutSnapshot E2(MeshLayoutSnapshot broadcast, MeshLayoutPair pair)
        {
            MeshLayoutSnapshot next = WithoutChild(broadcast, pair);
            SetEntry(next, new MeshLayoutEntry { id = parentId, mapId = mapId, baseId = baseId, state = MeshCellState.Active, owned = kept, interest = parentInterest });
            next.entries.Add(new MeshLayoutEntry { id = childId, mapId = mapId, baseId = baseId, state = MeshCellState.Active, owned = given, interest = given });
            return next;
        }

        /// <summary>
        /// E3 settles P's interest: what it owns once drained, or (after a drain timeout) its old rectangle, which holds
        /// every straggler (the evidence counts them, it does not place them).
        /// </summary>
        public MeshLayoutSnapshot E3(MeshLayoutSnapshot broadcast, MeshLayoutPair pair, bool drained)
        {
            MeshLayoutSnapshot next = broadcast.Clone();
            next.pair = pair;
            SetEntry(next, new MeshLayoutEntry { id = parentId, mapId = mapId, baseId = baseId, state = MeshCellState.Active, owned = kept, interest = drained ? kept : parentInterest });
            return next;
        }

        /// <summary>Retired is the broadcast with the child Retired (after an abort that followed the e1 broadcast).</summary>
        public MeshLayoutSnapshot Retired(MeshLayoutSnapshot broadcast, MeshLayoutPair pair)
        {
            MeshLayoutSnapshot next = WithoutChild(broadcast, pair);
            next.entries.Add(new MeshLayoutEntry { id = childId, mapId = mapId, baseId = baseId, state = MeshCellState.Retired, owned = MeshRect.Empty, interest = MeshRect.Empty });
            return next;
        }

        private MeshLayoutSnapshot WithoutChild(MeshLayoutSnapshot broadcast, MeshLayoutPair pair)
        {
            MeshLayoutSnapshot next = broadcast.Clone();
            next.pair = pair;
            next.entries.RemoveAll(e => string.Equals(e.id, childId, StringComparison.OrdinalIgnoreCase));
            return next;
        }
    }

    /// <summary>MeshSplitGates are the e1 (pre-warm) and drain gate predicates over a cell's evidence. Pure.</summary>
    public static class MeshSplitGates
    {
        /// <summary>The frame budget an uncapped cell's p95 is held to (60 fps).</summary>
        public const float UncappedNominalBudgetMs = 1000f / 60f;

        /// <summary>
        /// PrewarmReady: the child's window at or after e1 (its pair at least e1, started after that apply) shows every band neighbour's inbound hello at least
        /// <paramref name="prewarmSeconds"/> old with a datagram since, an empty spawn queue, and no saturated frame (a
        /// capped cell) or a p95 within the nominal budget (an uncapped one). <paramref name="why"/> names the first miss.
        /// </summary>
        public static bool PrewarmReady(MeshLayoutEvidenceMessage evidence, MeshLayoutPair e1, IReadOnlyList<string> neighbours, float prewarmSeconds, out string why)
        {
            return PrewarmCore(evidence, e1, neighbours, prewarmSeconds, true, out why);
        }

        /// <summary>
        /// PrewarmCore is PrewarmReady with the load clause (saturation, or p95 when uncapped) optional: a merge's survivor
        /// is a running, possibly loaded cell, and its e1 gate leaves it out (Slice 2c decision 5).
        /// </summary>
        internal static bool PrewarmCore(MeshLayoutEvidenceMessage evidence, MeshLayoutPair e1, IReadOnlyList<string> neighbours, float prewarmSeconds, bool checkLoad, out string why)
        {
            why = null;
            if (evidence.pair.CompareTo(e1) < 0)
            {
                why = $"evidence at {evidence.pair}, before e1 {e1}";
                return false;
            }
            if (evidence.windowStartedAfterApplySeconds < 0f)
            {
                why = "the window started before the apply";
                return false;
            }
            for (int i = 0; neighbours != null && i < neighbours.Count; ++i)
            {
                string zone = neighbours[i];
                if (!TryFind(evidence, zone, out MeshNeighbourEvidence heard) || heard.helloAgeSeconds < 0f)
                {
                    why = $"no hello from {zone}";
                    return false;
                }
                if (heard.helloAgeSeconds < prewarmSeconds)
                {
                    why = $"hello from {zone} {MeshSplitOperation.Number(heard.helloAgeSeconds)} s old";
                    return false;
                }
                if (heard.datagramsSinceHello < 1)
                {
                    why = $"no datagram from {zone} since its hello";
                    return false;
                }
            }
            if (evidence.prewarmQueue > 0)
            {
                why = $"prewarm queue {evidence.prewarmQueue}";
                return false;
            }
            if (!checkLoad)
                return true;
            if (evidence.budgetMs > 0f)
            {
                if (evidence.saturatedShare > 0f)
                {
                    why = $"saturated share {evidence.saturatedShare.ToString("0.###", CultureInfo.InvariantCulture)}";
                    return false;
                }
            }
            else if (evidence.p95Ms > UncappedNominalBudgetMs)
            {
                why = $"p95 {MeshSplitOperation.Number(evidence.p95Ms)} ms over the nominal {MeshSplitOperation.Number(UncappedNominalBudgetMs)} ms";
                return false;
            }
            return true;
        }

        /// <summary>DrainWindowQualifies: P's window counts when it is at or after e2, started after that apply, and P shrank.</summary>
        public static bool DrainWindowQualifies(MeshLayoutEvidenceMessage evidence, MeshLayoutPair e2)
        {
            return evidence.pair.CompareTo(e2) >= 0 && evidence.windowStartedAfterApplySeconds >= 0f && evidence.shrunk;
        }

        /// <summary>Drained: a qualifying window of P's with nothing left in R and no handoff toward the child unsettled.</summary>
        public static bool Drained(MeshLayoutEvidenceMessage evidence, MeshLayoutPair e2, out string why)
        {
            why = null;
            if (!DrainWindowQualifies(evidence, e2))
            {
                why = evidence.pair.CompareTo(e2) < 0 ? $"evidence at {evidence.pair}, before e2 {e2}"
                    : !evidence.shrunk ? "the parent reports no shrunk rectangle" : "the window started before the apply";
                return false;
            }
            MeshDrainCounts d = evidence.drain;
            if (d.Total > 0)
            {
                why = $"{d.Total} left: {d.characters} character(s), {d.petsAndMounts} pet(s) or mount(s), {d.monsters} monster(s), {d.staticsAndDrops} static(s) or drop(s), {d.synthetics} synthetic(s), {d.pendingHandoffs} handoff(s) unsettled, {d.buildings} building(s)";
                return false;
            }
            return true;
        }

        /// <summary>DrainTimeoutSeconds is N_R / drainRate + the handoff ack guard + 10 s (decision 8).</summary>
        public static float DrainTimeoutSeconds(int initial, float drainRatePerSecond, float handoffAckGuardSeconds)
        {
            return Math.Max(0, initial) / Math.Max(0.01f, drainRatePerSecond) + Math.Max(0f, handoffAckGuardSeconds) + 10f;
        }

        private static bool TryFind(MeshLayoutEvidenceMessage evidence, string zone, out MeshNeighbourEvidence found)
        {
            found = default;
            if (evidence.neighbours == null)
                return false;
            for (int i = 0; i < evidence.neighbours.Count; ++i)
            {
                if (string.Equals(evidence.neighbours[i].zoneId, zone, StringComparison.OrdinalIgnoreCase))
                {
                    found = evidence.neighbours[i];
                    return true;
                }
            }
            return false;
        }
    }
}
