// Server Meshing add-on — the rules a runtime layout must keep (DESIGN.md 0.72, plan D1). Pure: it sees the asset's cells
// and two snapshots as plain values, so the registry, the central's coordinator and the tests all judge a layout the same
// way. The first broken rule is the reason, naming the cell.
using System;
using System.Collections.Generic;
using System.IO;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>MeshLayoutBaseCell is one asset cell as the validator sees it.</summary>
    public sealed class MeshLayoutBaseCell
    {
        public string id;
        public string mapId;
        public MeshRect rect;
    }

    /// <summary>MeshLayoutWorld is what a layout is judged against: the asset's cells and the settings every process shares.</summary>
    public sealed class MeshLayoutWorld
    {
        /// <summary>Every asset cell, in the asset's order.</summary>
        public readonly List<MeshLayoutBaseCell> baseCells = new List<MeshLayoutBaseCell>();
        /// <summary>Every known map: true for Dynamic.</summary>
        public readonly Dictionary<string, bool> dynamicByMap = new Dictionary<string, bool>(StringComparer.Ordinal);
        /// <summary>The narrowest new cut per Dynamic map.</summary>
        public readonly Dictionary<string, float> minCellSizeByMap = new Dictionary<string, float>(StringComparer.Ordinal);
        public float bandWidth = 80f;
        public float touchTolerance = 0.5f;
        public int maxNeighbours = MeshWorldRegistry.MaxNeighbours;
    }

    /// <summary>MeshLayoutValidator checks a layout snapshot against its world.</summary>
    public static class MeshLayoutValidator
    {
        /// <summary>The longest cell id: the kit stores it in varchar(50) channel columns (buildings, characters).</summary>
        public const int MaxIdLength = 50;
        private const float Eps = 1e-3f;
        private static readonly char[] s_reserved = { '_', ':', '|', ';', '@', ',', '=' };
        private static readonly char[] s_invalidFileName = Path.GetInvalidFileNameChars();

        /// <summary>A cell as the prospective world holds it after a snapshot is applied.</summary>
        public sealed class LiveCell
        {
            public string id, mapId, baseId;
            public MeshCellState state;
            public MeshRect owned, interest;
            public bool onDynamicMap;
        }

        /// <summary>Touching: two cells that both own something and whose owned rectangles come within the tolerance.</summary>
        public static bool Touching(MeshRect a, MeshRect b, float touchTolerance) => !a.IsEmpty && !b.IsEmpty && a.GapTo(b) <= touchTolerance;

        /// <summary>
        /// BandNear: cells that must stream to each other. Touching cells always do; a cell of a Dynamic map (or next to
        /// one) also does with any cell whose interest comes within the band, so two cells that do not touch but stand
        /// within the band of each other (a T-junction after a split) still see across.
        /// </summary>
        public static bool BandNear(LiveCell a, LiveCell b, float bandWidth, float touchTolerance)
        {
            return Touching(a.owned, b.owned, touchTolerance) || ((a.onDynamicMap || b.onDynamicMap) && a.interest.GapTo(b.interest) <= bandWidth);
        }

        /// <summary>IsSafeCellId: a cell id must fit the cluster key, the mesh ids, the override syntax, file names and the DB column.</summary>
        public static bool IsSafeCellId(string id, out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(id))
                reason = "an empty cell id";
            else if (id.Length > MaxIdLength)
                reason = $"cell id '{id}' is longer than {MaxIdLength} characters";
            else if (id.IndexOfAny(s_reserved) >= 0)
                reason = $"cell id '{id}' contains one of {new string(s_reserved)}";
            else if (id.IndexOfAny(s_invalidFileName) >= 0)
                reason = $"cell id '{id}' contains a character a file name cannot hold";
            else
            {
                foreach (char c in id)
                {
                    if (char.IsWhiteSpace(c))
                    {
                        reason = $"cell id '{id}' contains whitespace";
                        break;
                    }
                }
            }
            return reason == null;
        }

        /// <summary>
        /// Validate judges <paramref name="next"/> against the world, knowing the layout it replaces (<paramref name="previous"/>,
        /// null when none was applied). An empty snapshot is always valid: it touches no family.
        /// </summary>
        public static bool Validate(MeshLayoutWorld world, MeshLayoutSnapshot next, MeshLayoutSnapshot previous, out string reason)
        {
            reason = null;
            if (world == null || next == null)
            {
                reason = "no world or no snapshot";
                return false;
            }
            Dictionary<string, MeshLayoutBaseCell> baseById = new Dictionary<string, MeshLayoutBaseCell>(StringComparer.OrdinalIgnoreCase);
            foreach (MeshLayoutBaseCell cell in world.baseCells)
            {
                if (cell != null && !string.IsNullOrEmpty(cell.id) && !baseById.ContainsKey(cell.id))
                    baseById[cell.id] = cell;
            }

            // 1-3: each entry on its own.
            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (MeshLayoutEntry entry in next.entries)
            {
                if (entry == null)
                {
                    reason = "a null entry";
                    return false;
                }
                if (!IsSafeCellId(entry.id, out reason))
                    return false;
                if (!ids.Add(entry.id))
                {
                    reason = $"cell id '{entry.id}' appears twice";
                    return false;
                }
                bool isBase = string.Equals(entry.id, entry.baseId, StringComparison.OrdinalIgnoreCase);
                if (!isBase && baseById.ContainsKey(entry.id))
                {
                    reason = $"child '{entry.id}' takes the id of an asset cell";
                    return false;
                }
                if (string.IsNullOrEmpty(entry.mapId) || !world.dynamicByMap.TryGetValue(entry.mapId, out bool dynamicMap))
                {
                    reason = $"{entry.id}: map '{entry.mapId}' is not in the world";
                    return false;
                }
                if (!dynamicMap)
                {
                    reason = $"{entry.id}: map '{entry.mapId}' is Static";
                    return false;
                }
                if (string.IsNullOrEmpty(entry.baseId) || !baseById.TryGetValue(entry.baseId, out MeshLayoutBaseCell baseCell) || baseCell.mapId != entry.mapId)
                {
                    reason = $"{entry.id}: base '{entry.baseId}' is not a cell of map {entry.mapId}";
                    return false;
                }
                if (!StateFits(entry, out reason))
                    return false;
                // 4: inside its base.
                if (!baseCell.rect.Encloses(entry.owned, Eps))
                {
                    reason = $"{entry.id} owns {entry.owned}, outside its base {entry.baseId} {baseCell.rect}";
                    return false;
                }
            }

            List<LiveCell> live = LiveWorld(world, next);
            Dictionary<string, MeshRect> previousOwned = OwnedById(world, previous);

            // 5-7: every family that has an entry.
            HashSet<string> families = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (MeshLayoutEntry entry in next.entries)
                families.Add(entry.baseId);
            foreach (string family in families)
            {
                MeshLayoutBaseCell baseCell = baseById[family];
                List<LiveCell> members = live.FindAll(c => string.Equals(c.baseId, family, StringComparison.OrdinalIgnoreCase) && !c.owned.IsEmpty);
                // 5: the family tiles its base, no overlap and no gap.
                float area = 0f;
                for (int i = 0; i < members.Count; ++i)
                {
                    area += members[i].owned.Area;
                    for (int j = i + 1; j < members.Count; ++j)
                    {
                        if (members[i].owned.Overlaps(members[j].owned, Eps))
                        {
                            reason = $"{members[i].id} and {members[j].id} overlap ({members[i].owned} and {members[j].owned})";
                            return false;
                        }
                    }
                }
                if (Math.Abs(area - baseCell.rect.Area) > Math.Max(1f, baseCell.rect.Area * 1e-6f))
                {
                    reason = $"the cells of {family} cover {area:0.#} m² of its {baseCell.rect.Area:0.#} m²: they must tile it";
                    return false;
                }
                // 6: no cell narrower than the band (whatever any setting says).
                foreach (LiveCell member in members)
                {
                    if (member.owned.Width < world.bandWidth - Eps || member.owned.Depth < world.bandWidth - Eps)
                    {
                        reason = $"{member.id} owns {member.owned}, narrower than the {world.bandWidth:0.##} m band";
                        return false;
                    }
                }
                // 7: a new or changed cut no smaller than the map's minimum cell.
                float minSize = world.minCellSizeByMap.TryGetValue(baseCell.mapId, out float m) ? m : 0f;
                foreach (LiveCell member in members)
                {
                    bool changed = !previousOwned.TryGetValue(member.id, out MeshRect before) || !before.Equals(member.owned);
                    if (changed && (member.owned.Width < minSize - Eps || member.owned.Depth < minSize - Eps))
                    {
                        reason = $"{member.id} would own {member.owned}, below the map's {minSize:0.##} m minimum cell";
                        return false;
                    }
                }
            }

            // 8: band neighbours, where the count grew.
            Dictionary<string, int> after = BandCounts(live, world);
            Dictionary<string, int> before2 = BandCounts(LiveWorld(world, previous), world);
            foreach (KeyValuePair<string, int> count in after)
            {
                int was = before2.TryGetValue(count.Key, out int b) ? b : 0;
                if (count.Value > world.maxNeighbours && count.Value > was)
                {
                    reason = $"{count.Key} would have {count.Value} band neighbours, more than {world.maxNeighbours}";
                    return false;
                }
            }
            return true;
        }

        private static bool StateFits(MeshLayoutEntry entry, out string reason)
        {
            reason = null;
            switch (entry.state)
            {
                case MeshCellState.Active:
                    if (entry.owned.IsEmpty) reason = $"{entry.id} is Active but owns nothing";
                    break;
                case MeshCellState.Pending:
                case MeshCellState.Draining:
                    if (!entry.owned.IsEmpty) reason = $"{entry.id} is {entry.state} but owns {entry.owned}";
                    break;
                case MeshCellState.Retired:
                    if (!entry.owned.IsEmpty || !entry.interest.IsEmpty) reason = $"{entry.id} is Retired but still has a rectangle";
                    break;
                default:
                    reason = $"{entry.id}: state {entry.state} cannot appear in a layout";
                    break;
            }
            if (reason == null && !entry.interest.Encloses(entry.owned, Eps))
                reason = $"{entry.id}: interest {entry.interest} does not cover owned {entry.owned}";
            return reason == null;
        }

        /// <summary>LiveWorld is every live cell after a snapshot: asset cells (changed by their entries) and children, never Retired ones.</summary>
        public static List<LiveCell> LiveWorld(MeshLayoutWorld world, MeshLayoutSnapshot snapshot)
        {
            Dictionary<string, MeshLayoutEntry> entries = new Dictionary<string, MeshLayoutEntry>(StringComparer.OrdinalIgnoreCase);
            if (snapshot != null)
            {
                foreach (MeshLayoutEntry entry in snapshot.entries)
                {
                    if (entry != null && !string.IsNullOrEmpty(entry.id) && !entries.ContainsKey(entry.id))
                        entries[entry.id] = entry;
                }
            }
            List<LiveCell> live = new List<LiveCell>();
            HashSet<string> baseIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (MeshLayoutBaseCell cell in world.baseCells)
            {
                if (cell == null || string.IsNullOrEmpty(cell.id) || !baseIds.Add(cell.id))
                    continue;
                bool dynamicMap = world.dynamicByMap.TryGetValue(cell.mapId ?? string.Empty, out bool d) && d;
                LiveCell liveCell = new LiveCell
                {
                    id = cell.id, mapId = cell.mapId, baseId = cell.id, onDynamicMap = dynamicMap,
                    state = dynamicMap ? MeshCellState.Active : MeshCellState.Static, owned = cell.rect, interest = cell.rect,
                };
                if (dynamicMap && entries.TryGetValue(cell.id, out MeshLayoutEntry own) && string.Equals(own.baseId, cell.id, StringComparison.OrdinalIgnoreCase))
                {
                    liveCell.state = own.state;
                    liveCell.owned = own.owned;
                    liveCell.interest = own.interest;
                }
                if (liveCell.state != MeshCellState.Retired)
                    live.Add(liveCell);
            }
            if (snapshot != null)
            {
                foreach (MeshLayoutEntry entry in snapshot.entries)
                {
                    if (entry == null || baseIds.Contains(entry.id) || entry.state == MeshCellState.Retired)
                        continue;
                    live.Add(new LiveCell
                    {
                        id = entry.id, mapId = entry.mapId, baseId = entry.baseId, state = entry.state,
                        owned = entry.owned, interest = entry.interest, onDynamicMap = true,
                    });
                }
            }
            return live;
        }

        private static Dictionary<string, MeshRect> OwnedById(MeshLayoutWorld world, MeshLayoutSnapshot snapshot)
        {
            Dictionary<string, MeshRect> owned = new Dictionary<string, MeshRect>(StringComparer.OrdinalIgnoreCase);
            foreach (LiveCell cell in LiveWorld(world, snapshot))
                owned[cell.id] = cell.owned;
            return owned;
        }

        private static Dictionary<string, int> BandCounts(List<LiveCell> live, MeshLayoutWorld world)
        {
            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < live.Count; ++i)
            {
                int n = 0;
                for (int j = 0; j < live.Count; ++j)
                {
                    if (i != j && BandNear(live[i], live[j], world.bandWidth, world.touchTolerance))
                        ++n;
                }
                counts[live[i].id] = n;
            }
            return counts;
        }
    }
}
