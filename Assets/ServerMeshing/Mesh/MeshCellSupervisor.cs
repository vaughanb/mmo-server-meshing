// Server Meshing add-on — the central's cell supervisor (Slice 2d, slices/slice2d_design.md decisions 1-5 and 7): which
// cell is down or hung, when a restart may be asked for, whether it succeeded, when the budget is spent; and the fenced
// set (incarnations the supervisor gave up on, which only ever move forward). Pure: the coordinator
// (MeshLayoutCoordinator_Supervision.cs) observes the cluster, asks Evaluate once a second per watched cell, and does
// the sending, the store and the lines. Incarnations are hex UTC ticks; two that are not cannot be ordered.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>MeshIncarnation orders map-server incarnations (hex UTC ticks, MeshMapNetworkManager.CellIncarnation).</summary>
    public static class MeshIncarnation
    {
        /// <summary>TryTicks reads an incarnation as UTC ticks (false when it is not a positive hex number).</summary>
        public static bool TryTicks(string incarnation, out long ticks)
        {
            ticks = 0;
            return !string.IsNullOrEmpty(incarnation) && incarnation.Length <= 16
                && long.TryParse(incarnation, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ticks) && ticks > 0;
        }

        /// <summary>IsOlder: both are UTC ticks and <paramref name="a"/> started before <paramref name="b"/>.</summary>
        public static bool IsOlder(string a, string b) => TryTicks(a, out long ta) && TryTicks(b, out long tb) && ta < tb;
    }

    /// <summary>MeshFencedSet holds, per cell, the incarnations that may never be bound again (decision 5).</summary>
    public sealed class MeshFencedSet
    {
        /// <summary>The newest entries kept per cell (older ones are covered by the "not newer than a fenced one" rule).</summary>
        public const int KeptPerCell = 16;

        private readonly Dictionary<string, List<string>> _byCell = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        public int Count
        {
            get
            {
                int n = 0;
                foreach (List<string> list in _byCell.Values)
                    n += list.Count;
                return n;
            }
        }

        /// <summary>Add fences one incarnation of a cell; false when it was already fenced (or empty).</summary>
        public bool Add(string cellId, string incarnation)
        {
            if (string.IsNullOrEmpty(cellId) || string.IsNullOrEmpty(incarnation))
                return false;
            if (!_byCell.TryGetValue(cellId, out List<string> list))
                _byCell[cellId] = list = new List<string>();
            if (list.Contains(incarnation))
                return false;
            list.Add(incarnation);
            if (list.Count > KeptPerCell)
                list.RemoveAt(0);
            return true;
        }

        /// <summary>
        /// IsFenced: the incarnation is in the set, or it is not newer than a fenced one of the same cell (a process that
        /// started before one the supervisor already gave up on is stale too).
        /// </summary>
        public bool IsFenced(string cellId, string incarnation)
        {
            if (string.IsNullOrEmpty(cellId) || string.IsNullOrEmpty(incarnation) || !_byCell.TryGetValue(cellId, out List<string> list))
                return false;
            bool ordered = MeshIncarnation.TryTicks(incarnation, out long ticks);
            foreach (string fenced in list)
            {
                if (string.Equals(fenced, incarnation, StringComparison.Ordinal))
                    return true;
                if (ordered && MeshIncarnation.TryTicks(fenced, out long fencedTicks) && fencedTicks >= ticks)
                    return true;
            }
            return false;
        }

        public void Clear() => _byCell.Clear();

        /// <summary>Load replaces the set with the store's entries.</summary>
        public void Load(IEnumerable<MeshFencedIncarnation> entries)
        {
            _byCell.Clear();
            if (entries == null)
                return;
            foreach (MeshFencedIncarnation entry in entries)
            {
                if (entry != null)
                    Add(entry.cellId, entry.incarnation);
            }
        }

        /// <summary>ToStore lists every entry for the store file, cells in id order.</summary>
        public List<MeshFencedIncarnation> ToStore()
        {
            List<string> cells = new List<string>(_byCell.Keys);
            cells.Sort(StringComparer.OrdinalIgnoreCase);
            List<MeshFencedIncarnation> entries = new List<MeshFencedIncarnation>();
            foreach (string cell in cells)
            {
                foreach (string incarnation in _byCell[cell])
                    entries.Add(new MeshFencedIncarnation { cellId = cell, incarnation = incarnation });
            }
            return entries;
        }
    }

    /// <summary>MeshSupervisorPhase is where the supervisor is with one cell.</summary>
    public enum MeshSupervisorPhase
    {
        /// <summary>Running (or not yet seen down); after a restart, stabilising until childRestartStableSeconds have passed.</summary>
        Up,
        /// <summary>A hung process was killed through its spawner; the restart waits until its key is gone.</summary>
        Killing,
        /// <summary>A restart was asked for; waiting for a hello from a new incarnation.</summary>
        Restarting,
        /// <summary>A base cell the kit restarts (autoRestart): the supervisor only waits for it.</summary>
        LeftToKit,
        /// <summary>A child whose restarts all failed: merged into its base once the op slot is free.</summary>
        CollapseQueued,
        /// <summary>Its collapse merge runs.</summary>
        Collapsing,
        /// <summary>It cannot collapse (no full edge shared with its base, Slice 3): left down, no more restarts.</summary>
        LeftDown,
    }

    /// <summary>MeshSupervisorVerdict is what Evaluate found.</summary>
    public enum MeshSupervisorVerdict
    {
        None,
        /// <summary>Unregistered with its key absent past the grace (or killed): restart (or leave it to the kit).</summary>
        Down,
        /// <summary>Registered but silent past childHungSeconds: kill it, then restart.</summary>
        Hung,
        /// <summary>The killed hung process left the cluster: ask for the restart now.</summary>
        RestartNow,
        /// <summary>The attempt in flight (or the last one, still stabilising) failed; why says how.</summary>
        AttemptFailed,
        /// <summary>The restarted cell stayed up childRestartStableSeconds: its failure count is reset.</summary>
        Stable,
    }

    /// <summary>MeshSupervisorInput is one second's observation of a watched cell.</summary>
    public struct MeshSupervisorInput
    {
        public float now;
        /// <summary>The bound process (the incarnation that last said hello) is registered with the cluster.</summary>
        public bool registered;
        /// <summary>A map server of the cell's id is registered (any process: a zombie holds the key too).</summary>
        public bool keyPresent;
        /// <summary>The bound process's last load report (-1 before its first).</summary>
        public float lastReportAt;
        /// <summary>A spawner kill of the bound process was sent (it needs no fence time).</summary>
        public bool killed;
    }

    /// <summary>MeshSupervisorDecision is Evaluate's answer.</summary>
    public struct MeshSupervisorDecision
    {
        public MeshSupervisorVerdict verdict;
        /// <summary>Down: seconds unregistered; Hung: seconds silent.</summary>
        public float seconds;
        public string why;

        public static MeshSupervisorDecision Of(MeshSupervisorVerdict verdict, float seconds = 0f, string why = null) => new MeshSupervisorDecision { verdict = verdict, seconds = seconds, why = why };
    }

    /// <summary>MeshSupervisedCell is the supervisor's record of one cell.</summary>
    public sealed class MeshSupervisedCell
    {
        public string cellId;
        public string baseId;
        public string mapId;
        /// <summary>The family's base cell (never collapsed; the kit restarts it outside sleep mode).</summary>
        public bool isBase;
        public MeshSupervisorPhase phase = MeshSupervisorPhase.Up;
        /// <summary>Since when its bound process is unregistered with the key absent, without a break (-1 while present).</summary>
        public float downSince = -1f;
        /// <summary>Failed restarts in a row (reset once a restart stays up childRestartStableSeconds).</summary>
        public int failures;
        /// <summary>When the last restart was asked for (or the hung kill sent): attempts are childRestartIntervalSeconds apart.</summary>
        public float attemptAt = -1f;
        /// <summary>When the last restart's new incarnation said hello (-1 when none is stabilising).</summary>
        public float backAt = -1f;
        /// <summary>The trouble that started the current attempt was a hang (for the lines).</summary>
        public bool hung;
        /// <summary>A supervisor line about this cell was logged since it was last up (so its return is logged too).</summary>
        public bool announced;

        /// <summary>The number of the next (or current) attempt, 1-based.</summary>
        public int Attempt => failures + 1;
    }

    /// <summary>MeshSupervisorSettings are the dynamicSettings the supervisor reads.</summary>
    public struct MeshSupervisorSettings
    {
        public float downGraceSeconds;
        public int maxRestarts;
        public float restartIntervalSeconds;
        public float restartStableSeconds;
        public float hungSeconds;
        public float fenceSeconds;
        public float fenceSaveSeconds;
        public float stepTimeoutSeconds;

        public static MeshSupervisorSettings From(MeshDynamicSettings s)
        {
            s = s ?? new MeshDynamicSettings();
            return new MeshSupervisorSettings
            {
                downGraceSeconds = s.childDownGraceSeconds, maxRestarts = Math.Max(0, s.childMaxRestarts),
                restartIntervalSeconds = Math.Max(0f, s.childRestartIntervalSeconds), restartStableSeconds = Math.Max(0f, s.childRestartStableSeconds),
                hungSeconds = Math.Max(1f, s.childHungSeconds), fenceSeconds = Math.Max(0f, s.childFenceSeconds),
                fenceSaveSeconds = Math.Max(0f, s.fenceSaveSeconds), stepTimeoutSeconds = Math.Max(1f, s.splitStepTimeoutSeconds),
            };
        }

        /// <summary>IdReuseSeconds: a fence finishes within childFenceSeconds + fenceSaveSeconds + 1 of losing the cluster (decision 7).</summary>
        public float IdReuseSeconds => fenceSeconds + fenceSaveSeconds + 1f;

        /// <summary>DownSeconds: how long an unkilled cell must be gone before it is down (the grace, never under the id-reuse time).</summary>
        public float DownSeconds => Math.Max(downGraceSeconds, IdReuseSeconds);
    }

    /// <summary>MeshCellSupervisor decides, per watched cell, what the coordinator does next. Pure.</summary>
    public static class MeshCellSupervisor
    {
        /// <summary>A killed process counts as down once it has been gone this long (the cluster's own disconnect detection is about as long).</summary>
        public const float KilledDownSeconds = 5f;

        /// <summary>
        /// Evaluate observes one cell for one second: it tracks how long the cell has been gone, and says whether it is down,
        /// hung, ready for the restart after a kill, failed its attempt, or stable again. It changes only the record's
        /// presence tracking and, on Stable, its failure count; the coordinator acts on the verdict.
        /// </summary>
        public static MeshSupervisorDecision Evaluate(MeshSupervisedCell cell, MeshSupervisorInput input, MeshSupervisorSettings settings)
        {
            float now = input.now;
            bool gone = !input.registered && !input.keyPresent;
            if (!gone)
                cell.downSince = -1f;
            else if (cell.downSince < 0f)
                cell.downSince = now;
            float goneFor = gone ? now - cell.downSince : 0f;
            switch (cell.phase)
            {
                case MeshSupervisorPhase.Up:
                    return EvaluateUp(cell, input, settings, gone, goneFor);
                case MeshSupervisorPhase.Killing:
                    if (gone)
                        return MeshSupervisorDecision.Of(MeshSupervisorVerdict.RestartNow);
                    if (now - cell.attemptAt >= settings.stepTimeoutSeconds)
                        return MeshSupervisorDecision.Of(MeshSupervisorVerdict.AttemptFailed, 0f, $"still registered {Number(now - cell.attemptAt)} s after the kill");
                    return default;
                case MeshSupervisorPhase.Restarting:
                    if (now - cell.attemptAt >= settings.stepTimeoutSeconds)
                        return MeshSupervisorDecision.Of(MeshSupervisorVerdict.AttemptFailed, 0f, $"no hello from a new incarnation in {Number(settings.stepTimeoutSeconds)} s");
                    return default;
                default:
                    return default;
            }
        }

        private static MeshSupervisorDecision EvaluateUp(MeshSupervisedCell cell, MeshSupervisorInput input, MeshSupervisorSettings settings, bool gone, float goneFor)
        {
            float now = input.now;
            bool stabilising = cell.backAt >= 0f;
            if (stabilising && input.registered && now - cell.backAt >= settings.restartStableSeconds)
            {
                cell.backAt = -1f;
                cell.failures = 0;
                return MeshSupervisorDecision.Of(MeshSupervisorVerdict.Stable);
            }
            float needed = input.killed ? KilledDownSeconds : settings.DownSeconds;
            if (gone && goneFor >= needed)
            {
                // Gone again before the restart proved itself: that restart failed (the next attempt follows at once).
                if (stabilising && cell.downSince - cell.backAt < settings.restartStableSeconds)
                    return MeshSupervisorDecision.Of(MeshSupervisorVerdict.AttemptFailed, 0f, $"down again {Number(cell.downSince - cell.backAt)} s after it came back");
                if (cell.attemptAt >= 0f && now - cell.attemptAt < settings.restartIntervalSeconds)
                    return default;
                return MeshSupervisorDecision.Of(MeshSupervisorVerdict.Down, goneFor);
            }
            if (input.registered && input.lastReportAt >= 0f && now - input.lastReportAt >= settings.hungSeconds)
            {
                if (stabilising && now - cell.backAt < settings.restartStableSeconds)
                    return MeshSupervisorDecision.Of(MeshSupervisorVerdict.AttemptFailed, 0f, $"hung {Number(now - input.lastReportAt)} s after it came back");
                if (cell.attemptAt >= 0f && now - cell.attemptAt < settings.restartIntervalSeconds)
                    return default;
                return MeshSupervisorDecision.Of(MeshSupervisorVerdict.Hung, now - input.lastReportAt);
            }
            return default;
        }

        /// <summary>BeginAttempt records a restart asked for now (or, for a hang, the kill that comes first).</summary>
        public static void BeginAttempt(MeshSupervisedCell cell, float now, bool hung)
        {
            cell.phase = hung ? MeshSupervisorPhase.Killing : MeshSupervisorPhase.Restarting;
            cell.hung = hung;
            cell.attemptAt = now;
            cell.backAt = -1f;
            cell.announced = true;
        }

        /// <summary>Failed counts a failed attempt; true when the budget is spent (the cell is then for collapse, or the kit).</summary>
        public static bool Failed(MeshSupervisedCell cell, MeshSupervisorSettings settings)
        {
            cell.failures++;
            cell.phase = MeshSupervisorPhase.Up;
            cell.backAt = -1f;
            return cell.failures >= Math.Max(1, settings.maxRestarts);
        }

        /// <summary>Back records a hello from a new incarnation: a restart in flight succeeded and now has to stay up.</summary>
        public static void Back(MeshSupervisedCell cell, float now)
        {
            bool restarted = cell.phase == MeshSupervisorPhase.Restarting || cell.phase == MeshSupervisorPhase.Killing || cell.failures > 0;
            cell.phase = MeshSupervisorPhase.Up;
            cell.downSince = -1f;
            cell.backAt = restarted ? now : -1f;
            cell.announced = false;
        }

        public static string Number(float value) => Math.Max(0f, value).ToString("0", CultureInfo.InvariantCulture);
    }
}
