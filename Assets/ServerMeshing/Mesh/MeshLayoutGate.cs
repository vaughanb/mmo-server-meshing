// Server Meshing add-on — the pure parts of a map server's side of dynamic cells (DESIGN.md 0.72, plan D5): the layout
// gate, the queue of buildings held while it is closed, and what a layout change means for the cell's stream targets,
// band masks and frame offsets. MeshMapNetworkManager_Layout wires them to the server.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// MeshLayoutGate is a cell's layout gate. On a Dynamic map the cell hands nothing out and routes no building until the
    /// central has answered its LayoutHello, because until then it cannot know which cell owns what; on a Static map it is
    /// never closed. Once open it stays open for the life of the server: a re-registration or a lost cluster link does not
    /// close it again, since the layout the cell holds is still the newest it knows.
    /// </summary>
    public sealed class MeshLayoutGate
    {
        private float _firstHelloAt = -1f;
        private float _nextWarningAt;

        /// <summary>IsDynamic is whether the cell's map runs dynamic cells (fixed when the server starts).</summary>
        public bool IsDynamic { get; private set; }
        public bool IsAnswered { get; private set; }
        public bool HelloSent => _firstHelloAt >= 0f;
        /// <summary>When the first hello went out (negative before it).</summary>
        public float FirstHelloAt => _firstHelloAt;
        public bool IsGated => IsDynamic && !IsAnswered;

        /// <summary>Reset closes the gate of a Dynamic map's cell for a server start (a Static map's is never closed).</summary>
        public void Reset(bool dynamicMap)
        {
            IsDynamic = dynamicMap;
            IsAnswered = false;
            _firstHelloAt = -1f;
            _nextWarningAt = 0f;
        }

        /// <summary>OnHelloSent notes a hello; only the first one starts the warning clock.</summary>
        public void OnHelloSent(float now, float warnSeconds)
        {
            if (HelloSent)
                return;
            _firstHelloAt = now;
            _nextWarningAt = now + Mathf.Max(1f, warnSeconds);
        }

        /// <summary>OnAnswered opens the gate for an answer to a hello this server sent; false when it did not open it.</summary>
        public bool OnAnswered()
        {
            if (!IsGated || !HelloSent)
                return false;
            IsAnswered = true;
            return true;
        }

        /// <summary>WarningDue is true once every <paramref name="warnSeconds"/> while the gate waits on a hello that went out.</summary>
        public bool WarningDue(float now, float warnSeconds)
        {
            if (!IsGated || !HelloSent || now < _nextWarningAt)
                return false;
            _nextWarningAt = now + Mathf.Max(1f, warnSeconds);
            return true;
        }
    }

    /// <summary>
    /// MeshHeldPlacementQueue keeps what a gated cell was asked to build (placed here, or handed over by a neighbour) until
    /// the gate opens, in arrival order. Nothing is ever dropped: the player's item is already spent, so an entry whose
    /// deadline passes is handed back to be created here anyway.
    /// </summary>
    public sealed class MeshHeldPlacementQueue<T>
    {
        /// <summary>HeldItem is one request: the item, whether a neighbour handed it over, and when it is created at the latest.</summary>
        public struct HeldItem
        {
            public T item;
            public bool received;
            public float deadline;
        }

        private readonly List<HeldItem> _held = new List<HeldItem>();

        public int Count => _held.Count;

        /// <summary>TryHold keeps the item while <paramref name="gated"/>; false, holding nothing, when the gate is open.</summary>
        public bool TryHold(bool gated, T item, bool received, float deadline)
        {
            if (!gated)
                return false;
            _held.Add(new HeldItem { item = item, received = received, deadline = deadline });
            return true;
        }

        /// <summary>TryTakeNext hands back the oldest item (the gate is open, so everything goes, in order).</summary>
        public bool TryTakeNext(out HeldItem held)
        {
            if (_held.Count == 0)
            {
                held = default;
                return false;
            }
            held = _held[0];
            _held.RemoveAt(0);
            return true;
        }

        /// <summary>TryTakeExpired hands back the oldest item whose deadline has passed.</summary>
        public bool TryTakeExpired(float now, out HeldItem held)
        {
            for (int i = 0; i < _held.Count; ++i)
            {
                if (now >= _held[i].deadline)
                {
                    held = _held[i];
                    _held.RemoveAt(i);
                    return true;
                }
            }
            held = default;
            return false;
        }

        /// <summary>TakeAll empties the queue and returns what it held, oldest first.</summary>
        public List<HeldItem> TakeAll()
        {
            List<HeldItem> all = new List<HeldItem>(_held);
            _held.Clear();
            return all;
        }
    }

    /// <summary>MeshLayoutTarget is the map side's target check of a layout message (Slice 2b decision 4).</summary>
    public static class MeshLayoutTarget
    {
        /// <summary>
        /// Accepts: a broadcast is for every cell; a staged step and a hello answer only for the cell and incarnation they
        /// name (cell ids compare ignoring case, incarnations exactly), so a restarted cell ignores its predecessor's.
        /// </summary>
        public static bool Accepts(MeshLayoutKind kind, string targetCellId, string targetIncarnation, string ownCellId, string ownIncarnation)
        {
            if (kind == MeshLayoutKind.Broadcast)
                return true;
            return string.Equals(targetCellId, ownCellId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(targetIncarnation, ownIncarnation, StringComparison.Ordinal);
        }
    }

    /// <summary>MeshRetireRule is when a cell of a Dynamic map ends itself (Slice 2b decision 5a, 2b's share of 2c's retire).</summary>
    public static class MeshRetireRule
    {
        /// <summary>
        /// ShouldRetire: the retire names this cell and incarnation, and the cell owns nothing yet: it holds no layout, or
        /// its own cell is Pending in the one it holds. A cell that owns something stays up (2c drains it first).
        /// </summary>
        public static bool ShouldRetire(string retireCellId, string retireIncarnation, string ownCellId, string ownIncarnation, bool holdsLayout, MeshCellState ownState)
        {
            if (!string.Equals(retireCellId, ownCellId, StringComparison.OrdinalIgnoreCase) || !string.Equals(retireIncarnation, ownIncarnation, StringComparison.Ordinal))
                return false;
            return !holdsLayout || ownState == MeshCellState.Pending;
        }
    }

    /// <summary>MeshStreamTargetDiff compares two lists of cell ids the way cell ids compare (ignoring case).</summary>
    public static class MeshStreamTargetDiff
    {
        /// <summary>
        /// Compute fills <paramref name="left"/> with the ids of <paramref name="previous"/> that <paramref name="current"/>
        /// no longer has and <paramref name="added"/> with the new ones, each in its own list's order. A null list is empty.
        /// </summary>
        public static void Compute(IEnumerable<string> previous, IEnumerable<string> current, List<string> left, List<string> added)
        {
            left.Clear();
            added.Clear();
            HashSet<string> before = new HashSet<string>(previous ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            HashSet<string> after = new HashSet<string>(current ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (string id in previous ?? Array.Empty<string>())
            {
                if (!after.Contains(id))
                    left.Add(id);
            }
            foreach (string id in current ?? Array.Empty<string>())
            {
                if (!before.Contains(id))
                    added.Add(id);
            }
        }
    }

    /// <summary>MeshBandMask says which stream targets' bands a position lies in.</summary>
    public static class MeshBandMask
    {
        /// <summary>
        /// Of sets bit i when <paramref name="scenePos"/> (in <paramref name="own"/>'s frame) is within <paramref name="band"/>
        /// of targets[i]'s interest rectangle, which is its owned one except while a split or merge moves things.
        /// </summary>
        public static int Of(MeshCell own, IReadOnlyList<MeshCell> targets, Vector3 scenePos, float band)
        {
            if (own == null || targets == null)
                return 0;
            if (targets.Count > MeshWorldRegistry.MaxNeighbours)
                throw new ArgumentException($"{targets.Count} stream targets, but a band mask holds {MeshWorldRegistry.MaxNeighbours}", nameof(targets));
            int mask = 0;
            for (int i = 0; i < targets.Count; ++i)
            {
                if (own.InterestDistanceTo(targets[i], scenePos) <= band)
                    mask |= 1 << i;
            }
            return mask;
        }
    }

    /// <summary>MeshFrameOffsets brings the frame offsets of a cell's inbound links up to date after a layout change.</summary>
    public static class MeshFrameOffsets
    {
        /// <summary>
        /// Recompute sets the offset (added to a position from that neighbour to express it in <paramref name="own"/>'s frame)
        /// of every connection whose zone the registry knows; a connection to a zone it does not know keeps its offset.
        /// Returns how many offsets changed; <paramref name="changedZones"/>, when given, receives each changed connection's zone.
        /// </summary>
        public static int Recompute(MeshWorldRegistry world, MeshCell own, IReadOnlyDictionary<long, string> zoneByConnection, IDictionary<long, Vector3> offsets, List<string> changedZones = null)
        {
            if (world == null || own == null || zoneByConnection == null || offsets == null)
                return 0;
            int changed = 0;
            foreach (KeyValuePair<long, string> kv in zoneByConnection)
            {
                MeshCell sender = world.GetCellById(kv.Value);
                if (sender == null)
                    continue;
                Vector3 offset = sender.OffsetTo(own);
                if (!offsets.TryGetValue(kv.Key, out Vector3 was) || was != offset)
                {
                    offsets[kv.Key] = offset;
                    changed++;
                    changedZones?.Add(kv.Value);
                }
            }
            return changed;
        }
    }
}
