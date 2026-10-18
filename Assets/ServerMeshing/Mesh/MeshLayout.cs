// Server Meshing add-on — the runtime layout of dynamic cells (DESIGN.md 0.72, plan D0/D1): the types a snapshot of
// who owns what is made of. A map is Static (its asset cells, today's behaviour) or Dynamic (its asset cells are base
// cells that the central may cut into children at runtime and merge back). Rectangles are in the world frame.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using LiteNetLib.Utils;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>MeshCellMode is how a map's cells behave: the asset's cut for good, or base cells the central may split.</summary>
    public enum MeshCellMode
    {
        Static = 0,
        Dynamic = 1,
    }

    /// <summary>MeshCellState is where a cell is in its life: a static cell, or a dynamic one being made, serving, emptying or gone.</summary>
    public enum MeshCellState : byte
    {
        Static = 0,
        Pending = 1,
        Active = 2,
        Draining = 3,
        Retired = 4,
    }

    /// <summary>
    /// MeshRect is a world-frame rectangle [min, max) on x and z. The empty rectangle is all zeros: it contains nothing,
    /// is infinitely far from everything, and a position is infinitely far outside it.
    /// </summary>
    [Serializable]
    public struct MeshRect : IEquatable<MeshRect>
    {
        public float minX, minZ, maxX, maxZ;

        /// <summary>Empty is the rectangle a cell that owns nothing has.</summary>
        public static readonly MeshRect Empty = default;

        public MeshRect(float minX, float minZ, float maxX, float maxZ)
        {
            this.minX = minX;
            this.minZ = minZ;
            this.maxX = maxX;
            this.maxZ = maxZ;
        }

        /// <summary>FromMinSize builds a rectangle from its minimum corner and its edge lengths.</summary>
        public static MeshRect FromMinSize(float minX, float minZ, float sizeX, float sizeZ) => new MeshRect(minX, minZ, minX + sizeX, minZ + sizeZ);

        public bool IsEmpty => maxX <= minX || maxZ <= minZ;
        public float Width => IsEmpty ? 0f : maxX - minX;
        public float Depth => IsEmpty ? 0f : maxZ - minZ;
        public float Area => Width * Depth;

        /// <summary>Contains is half-open: the min edges belong to the rectangle, the max edges to the next one.</summary>
        public bool Contains(float x, float z) => !IsEmpty && x >= minX && x < maxX && z >= minZ && z < maxZ;

        /// <summary>Chebyshev distance from a point to the rectangle (0 inside it; +infinity when it is empty).</summary>
        public float DistanceTo(float x, float z)
        {
            if (IsEmpty)
                return float.PositiveInfinity;
            float dx = Mathf.Max(minX - x, 0f, x - maxX);
            float dz = Mathf.Max(minZ - z, 0f, z - maxZ);
            return Mathf.Max(dx, dz);
        }

        /// <summary>Signed distance from a point to the rectangle's boundary: positive inside, negative outside (-infinity when empty).</summary>
        public float DistanceToEdge(float x, float z)
        {
            if (IsEmpty)
                return float.NegativeInfinity;
            return Mathf.Min(Mathf.Min(x - minX, maxX - x), Mathf.Min(z - minZ, maxZ - z));
        }

        /// <summary>Gap between two rectangles (0 when they touch or overlap; +infinity when either is empty).</summary>
        public float GapTo(MeshRect other)
        {
            if (IsEmpty || other.IsEmpty)
                return float.PositiveInfinity;
            float dx = Mathf.Max(other.minX - maxX, minX - other.maxX, 0f);
            float dz = Mathf.Max(other.minZ - maxZ, minZ - other.maxZ, 0f);
            return Mathf.Max(dx, dz);
        }

        /// <summary>Encloses says whether <paramref name="inner"/> lies within this rectangle, give or take <paramref name="eps"/> (an empty one always does).</summary>
        public bool Encloses(MeshRect inner, float eps = 1e-3f)
        {
            if (inner.IsEmpty)
                return true;
            return !IsEmpty && inner.minX >= minX - eps && inner.minZ >= minZ - eps && inner.maxX <= maxX + eps && inner.maxZ <= maxZ + eps;
        }

        /// <summary>Overlaps says whether the two share more than <paramref name="eps"/> of area on both axes (touching is not overlapping).</summary>
        public bool Overlaps(MeshRect other, float eps = 1e-3f)
        {
            if (IsEmpty || other.IsEmpty)
                return false;
            return Mathf.Min(maxX, other.maxX) - Mathf.Max(minX, other.minX) > eps && Mathf.Min(maxZ, other.maxZ) - Mathf.Max(minZ, other.minZ) > eps;
        }

        /// <summary>
        /// TryUnion gives the rectangle the two make together when they share a full edge (give or take
        /// <paramref name="tolerance"/>: same extent on one axis, touching on the other), so the union is a rectangle;
        /// false otherwise (overlapping, apart, offset edges, or either empty). Slice 2c merge geometry.
        /// </summary>
        public bool TryUnion(MeshRect other, float tolerance, out MeshRect union)
        {
            union = Empty;
            if (IsEmpty || other.IsEmpty)
                return false;
            bool sameX = Mathf.Abs(minX - other.minX) <= tolerance && Mathf.Abs(maxX - other.maxX) <= tolerance;
            bool sameZ = Mathf.Abs(minZ - other.minZ) <= tolerance && Mathf.Abs(maxZ - other.maxZ) <= tolerance;
            if (sameX && (Mathf.Abs(maxZ - other.minZ) <= tolerance || Mathf.Abs(other.maxZ - minZ) <= tolerance))
            {
                union = new MeshRect(Mathf.Min(minX, other.minX), Mathf.Min(minZ, other.minZ), Mathf.Max(maxX, other.maxX), Mathf.Max(maxZ, other.maxZ));
                return true;
            }
            if (sameZ && (Mathf.Abs(maxX - other.minX) <= tolerance || Mathf.Abs(other.maxX - minX) <= tolerance))
            {
                union = new MeshRect(Mathf.Min(minX, other.minX), Mathf.Min(minZ, other.minZ), Mathf.Max(maxX, other.maxX), Mathf.Max(maxZ, other.maxZ));
                return true;
            }
            return false;
        }

        public bool Equals(MeshRect other) => minX == other.minX && minZ == other.minZ && maxX == other.maxX && maxZ == other.maxZ;
        public override bool Equals(object obj) => obj is MeshRect other && Equals(other);
        public override int GetHashCode() => (minX, minZ, maxX, maxZ).GetHashCode();

        public override string ToString() => IsEmpty ? "empty"
            : string.Format(CultureInfo.InvariantCulture, "x [{0:0.##}, {1:0.##}) z [{2:0.##}, {3:0.##})", minX, maxX, minZ, maxZ);
    }

    /// <summary>
    /// MeshLayoutPair orders layouts: a coordinator incarnation (a new central starts a new one) and a version within it,
    /// compared as a pair. A cell applies only a strictly newer pair.
    /// </summary>
    [Serializable]
    public struct MeshLayoutPair : IEquatable<MeshLayoutPair>, IComparable<MeshLayoutPair>
    {
        public long coordinatorIncarnation;
        public long version;

        /// <summary>None is the pair of a layout nobody applied: older than every other.</summary>
        public static readonly MeshLayoutPair None = default;

        public MeshLayoutPair(long coordinatorIncarnation, long version)
        {
            this.coordinatorIncarnation = coordinatorIncarnation;
            this.version = version;
        }

        public bool IsNone => coordinatorIncarnation == 0 && version == 0;
        public bool IsNewerThan(MeshLayoutPair other) => CompareTo(other) > 0;

        public int CompareTo(MeshLayoutPair other)
        {
            int byIncarnation = coordinatorIncarnation.CompareTo(other.coordinatorIncarnation);
            return byIncarnation != 0 ? byIncarnation : version.CompareTo(other.version);
        }

        public bool Equals(MeshLayoutPair other) => coordinatorIncarnation == other.coordinatorIncarnation && version == other.version;
        public override bool Equals(object obj) => obj is MeshLayoutPair other && Equals(other);
        public override int GetHashCode() => (coordinatorIncarnation, version).GetHashCode();
        public static bool operator ==(MeshLayoutPair a, MeshLayoutPair b) => a.Equals(b);
        public static bool operator !=(MeshLayoutPair a, MeshLayoutPair b) => !a.Equals(b);

        public override string ToString() => string.Format(CultureInfo.InvariantCulture, "{0:x}/{1}", coordinatorIncarnation, version);
    }

    /// <summary>MeshLayoutEntry is one cell of a Dynamic map as a snapshot describes it: a base cell with a new cut, or a child.</summary>
    [Serializable]
    public sealed class MeshLayoutEntry
    {
        public string id;
        public string mapId;
        /// <summary>The asset cell this one was cut from (a base cell names itself).</summary>
        public string baseId;
        public MeshCellState state;
        /// <summary>What the cell is responsible for: players, monsters and statics inside it belong to it.</summary>
        public MeshRect owned;
        /// <summary>What the cell simulates and streams around (owned, or more while a split or merge moves things).</summary>
        public MeshRect interest;

        public MeshLayoutEntry Clone() => (MeshLayoutEntry)MemberwiseClone();

        /// <summary>SameAs is true when both entries describe the same cell the same way (ids compared as cell ids are).</summary>
        public bool SameAs(MeshLayoutEntry other)
        {
            return other != null
                && string.Equals(id, other.id, StringComparison.OrdinalIgnoreCase)
                && string.Equals(mapId, other.mapId, StringComparison.Ordinal)
                && string.Equals(baseId, other.baseId, StringComparison.OrdinalIgnoreCase)
                && state == other.state && owned.Equals(other.owned) && interest.Equals(other.interest);
        }

        public override string ToString() => $"{id} ({state}, base {baseId}) owns {owned}, interest {interest}";
    }

    /// <summary>MeshLayoutSnapshot is the whole runtime layout at one pair: every cell of every Dynamic map that differs from its asset cut.</summary>
    [Serializable]
    public sealed class MeshLayoutSnapshot : INetSerializable
    {
        /// <summary>The most entries a snapshot may carry on the wire: a bound, so a corrupt count cannot allocate the heap away.</summary>
        public const int MaxEntries = 1024;
        /// <summary>The fewest bytes an entry takes (three empty strings, the state, two rectangles).</summary>
        public const int MinEntryBytes = 3 * 2 + 1 + 2 * 16;

        public MeshLayoutPair pair;
        public List<MeshLayoutEntry> entries = new List<MeshLayoutEntry>();

        public void Serialize(NetDataWriter writer)
        {
            MeshLayoutWire.Put(writer, pair);
            writer.Put(entries.Count);
            foreach (MeshLayoutEntry entry in entries)
                MeshLayoutWire.Put(writer, entry ?? new MeshLayoutEntry());
        }

        public void Deserialize(NetDataReader reader)
        {
            pair = MeshLayoutWire.GetPair(reader);
            int count = reader.GetInt();
            if (count < 0 || count > MaxEntries)
                throw new InvalidDataException($"layout snapshot with {count} entries (0..{MaxEntries} allowed)");
            if ((long)count * MinEntryBytes > reader.AvailableBytes)
                throw new InvalidDataException($"layout snapshot of {count} entries is longer than the {reader.AvailableBytes} bytes left");
            entries = new List<MeshLayoutEntry>(count);
            for (int i = 0; i < count; ++i)
                entries.Add(MeshLayoutWire.GetEntry(reader));
        }

        public MeshLayoutSnapshot Clone()
        {
            MeshLayoutSnapshot copy = new MeshLayoutSnapshot { pair = pair };
            foreach (MeshLayoutEntry entry in entries)
            {
                if (entry != null)
                    copy.entries.Add(entry.Clone());
            }
            return copy;
        }

        /// <summary>
        /// SameEntries is true when both snapshots hold the same entries in the same order, a null snapshot counting as an
        /// empty one: two snapshots at one pair must agree, or the coordinator has sent two truths under one name.
        /// </summary>
        public static bool SameEntries(MeshLayoutSnapshot a, MeshLayoutSnapshot b)
        {
            int countA = a?.entries.Count ?? 0, countB = b?.entries.Count ?? 0;
            if (countA != countB)
                return false;
            for (int i = 0; i < countA; ++i)
            {
                MeshLayoutEntry entry = a.entries[i];
                if (entry == null ? b.entries[i] != null : !entry.SameAs(b.entries[i]))
                    return false;
            }
            return true;
        }
    }

    /// <summary>MeshLayoutWire writes and reads the layout types on the cluster link.</summary>
    public static class MeshLayoutWire
    {
        public static void Put(NetDataWriter writer, MeshRect rect)
        {
            writer.Put(rect.minX);
            writer.Put(rect.minZ);
            writer.Put(rect.maxX);
            writer.Put(rect.maxZ);
        }

        public static MeshRect GetRect(NetDataReader reader) => new MeshRect(reader.GetFloat(), reader.GetFloat(), reader.GetFloat(), reader.GetFloat());

        public static void Put(NetDataWriter writer, MeshLayoutPair pair)
        {
            writer.Put(pair.coordinatorIncarnation);
            writer.Put(pair.version);
        }

        public static MeshLayoutPair GetPair(NetDataReader reader) => new MeshLayoutPair(reader.GetLong(), reader.GetLong());

        public static void Put(NetDataWriter writer, MeshLayoutEntry entry)
        {
            writer.Put(entry.id ?? string.Empty);
            writer.Put(entry.mapId ?? string.Empty);
            writer.Put(entry.baseId ?? string.Empty);
            writer.Put((byte)entry.state);
            Put(writer, entry.owned);
            Put(writer, entry.interest);
        }

        public static MeshLayoutEntry GetEntry(NetDataReader reader)
        {
            MeshLayoutEntry entry = new MeshLayoutEntry { id = reader.GetString(), mapId = reader.GetString(), baseId = reader.GetString() };
            byte state = reader.GetByte();
            if (state > (byte)MeshCellState.Retired)
                throw new InvalidDataException($"layout entry {entry.id} with cell state {state}");
            entry.state = (MeshCellState)state;
            entry.owned = GetRect(reader);
            entry.interest = GetRect(reader);
            return entry;
        }

        /// <summary>GetEnum reads a byte that must name a value of an enum with values 0..<paramref name="max"/>.</summary>
        public static byte GetEnum(NetDataReader reader, byte max, string what)
        {
            byte value = reader.GetByte();
            if (value > max)
                throw new InvalidDataException($"{what} {value} is not known");
            return value;
        }
    }

    /// <summary>MeshLayoutChange says what an applied layout changed, so a cell reacts only to what concerns it.</summary>
    public sealed class MeshLayoutChange
    {
        public MeshLayoutPair pair;
        /// <summary>Cells whose state or rectangles changed.</summary>
        public readonly List<string> changedIds = new List<string>();
        /// <summary>Cells whose touching neighbours changed.</summary>
        public readonly List<string> neighbourChangedIds = new List<string>();
        /// <summary>Cells whose band neighbours changed.</summary>
        public readonly List<string> bandChangedIds = new List<string>();

        public bool Concerns(string cellId)
        {
            return Contains(changedIds, cellId) || Contains(neighbourChangedIds, cellId) || Contains(bandChangedIds, cellId);
        }

        /// <summary>BandChanged is true when the cell's band neighbours changed, so what it streams where must be re-measured.</summary>
        public bool BandChanged(string cellId) => Contains(bandChangedIds, cellId);

        private static bool Contains(List<string> ids, string id)
        {
            for (int i = 0; i < ids.Count; ++i)
            {
                if (string.Equals(ids[i], id, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }

    /// <summary>MeshDynamicSettings holds the registry-wide settings of dynamic cells that every process reads from the one asset.</summary>
    [Serializable]
    public class MeshDynamicSettings
    {
        [Tooltip("Ghost band width in metres (and the visible range it stands for): the band neighbours of a Dynamic map's cells, and the narrowest a cut may leave a cell. Every process must agree on it, so it lives here; each map server logs an error when its own ghostBandWidth is wider. Rig override: MESH_BAND.")]
        public float bandWidth = 80f;
        [Tooltip("Narrowest a new cut may make a cell, on either axis, for maps that set no minCellSize.")]
        public float defaultMinCellSize = 100f;
        [Tooltip("Most cells one base cell may be cut into, for maps that set no maxCellsPerBase.")]
        public int defaultMaxCellsPerBase = 4;
        [Tooltip("Seconds between warnings while a Dynamic map's cell still waits for the central's layout answer.")]
        public float layoutGateWarnSeconds = 10f;
        [Tooltip("Seconds the central waits for every registered cell's layout hello after it starts before it recovers without the missing ones.")]
        public float recoveryCeilingSeconds = 60f;
        [Tooltip("Where the central keeps its layout between restarts (a JSON file); empty = Temp/ServerMesh.Coordinator.json beside the process. Rig override: MESH_COORDINATOR_STORE.")]
        public string coordinatorStorePath = "";

        // Slice 2b (split). New fields keep their initializers when an older asset lacks them.
        [Tooltip("Seconds a handoff waits for the destination's ack before it is rolled back (the map servers' own field has the same default); the central adds it to a split's drain timeout.")]
        public float handoffAckGuardSeconds = 20f;
        [Tooltip("Character handoffs a draining cell starts per second (the database's sustained capacity; one operation runs world-wide, so this is the cluster's rate).")]
        public float drainRatePerSecond = 5f;
        [Tooltip("Monster, static and item drop handoffs a draining cell starts per second (no database work).")]
        public float drainEntityRatePerSecond = 20f;
        [Tooltip("Most handoffs in flight toward one destination while a cell drains (a safety cap).")]
        public int maxConcurrentHandoffsPerDestination = 20;
        [Tooltip("Seconds a new cell's band neighbours must have been linked to it (inbound hello, then traffic) before the split commits.")]
        public float prewarmSeconds = 2f;
        [Tooltip("Share of the frame budget a Dynamic map's cell spends spawning queued puppets per frame (0.25 = 4 ms at 60 fps).")]
        public float prewarmSpawnBudgetShare = 0.25f;
        [Tooltip("Seconds a split step waits for a silent participant (no ack, no hello, no evidence) before it aborts (before commit) or freezes (after).")]
        public float splitStepTimeoutSeconds = 60f;
        [Tooltip("Seconds after a split is done before the same base cell's family may be split again.")]
        public float splitCooldownSeconds = 30f;
        [Tooltip("A monster younger than this that a shrunk cell's spawn area made in the given-away rectangle is handed off, not counted by the drain gate.")]
        public float drainMonsterAgeSeconds = 5f;

        // Slice 2c (merge).
        [Tooltip("Seconds a merged-away cell may take to drain and exit before the central and the cell log it as straggling (every 30 s; players are never kicked).")]
        public float retireMaxSeconds = 90f;

        // Slice 2d (supervision and fencing).
        [Tooltip("Seconds a live dynamic cell (or a split family's base) must stay unregistered, its cluster key gone, before the central restarts it. Must exceed childFenceSeconds + fenceSaveSeconds + 3.")]
        public float childDownGraceSeconds = 20f;
        [Tooltip("Failed restarts of a child before the central collapses it into its base.")]
        public int childMaxRestarts = 3;
        [Tooltip("Least seconds between two restart attempts of one cell.")]
        public float childRestartIntervalSeconds = 10f;
        [Tooltip("Seconds a restarted cell must stay up for its restart to count as a success.")]
        public float childRestartStableSeconds = 60f;
        [Tooltip("Seconds a registered cell may send no load report before the central treats it as hung and has its spawner kill it.")]
        public float childHungSeconds = 30f;
        [Tooltip("Seconds a cell of a Dynamic map may be unregistered from the cluster, while a neighbour reports itself registered, before it fences itself (saves, kicks, exits).")]
        public float childFenceSeconds = 10f;
        [Tooltip("Upper bound, in seconds, on a fencing cell's player saves.")]
        public float fenceSaveSeconds = 5f;
        [Tooltip("Seconds a merge's retire step may last before the central has the retiring cell's spawner kill it.")]
        public float retireKillAfterSeconds = 270f;
    }
}
