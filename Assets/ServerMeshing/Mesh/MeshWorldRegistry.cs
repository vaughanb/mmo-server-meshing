// Server Meshing add-on — MeshWorldRegistry (DESIGN.md §9 routing, Q12/Q13 groundwork: the world as a graph of cells).
//
// A cell is the unit the mesh reasons about: a rectangle of the world served by one map server (its channel id is the
// cell id), cut from a kit map by a per-map layout (a uniform grid placed in one shared world frame). Neighbours are
// derived from placement: two cells whose rectangles touch (edge or corner) are neighbours, whichever map each comes
// from — so continents placed next to each other in the world join through their touching cells with no extra data.
// Positions on a server are in that cell's frame (the scene frame of its map today; a per-cell origin later — every
// conversion already goes through the cell so that change stays local).
//
// Dynamic cells (DESIGN.md 0.72): a map whose layout is Dynamic treats its asset cells as base cells that the central
// may cut at runtime. ApplyLayout overlays a snapshot on the asset: a base cell's owned rectangle shrinks, children
// appear with their own ids, and cells that stream to each other are band neighbours, which on a Dynamic map can be
// cells that do not touch. A Static map's cells never change and their band neighbours are their touching neighbours.
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>An explicit cell: a rectangle in the map's scene frame. Cells of one map may differ in size (a town small, wilderness large) but must not overlap.</summary>
    [Serializable]
    public class MeshCellRect
    {
        [Tooltip("Cell id: the channel id of the map server serving it; unique across the whole world.")]
        public string id;
        [Tooltip("Scene-frame minimum corner (x, z).")]
        public Vector2 min;
        [Tooltip("Edge lengths (x, z) in metres.")]
        public Vector2 size = new Vector2(200f, 200f);
    }

    /// <summary>One kit map's cut into cells — a uniform grid, or explicit rectangles of any size — placed in the shared world frame.</summary>
    [Serializable]
    public class MeshMapLayout
    {
        [Tooltip("The kit map this layout cuts (BaseMapInfo.Id).")]
        public string mapId;
        [Tooltip("Where this map's scene origin sits in the shared world frame that adjacency is computed in. Two maps meant to touch are placed so their cell rectangles meet.")]
        public Vector3 worldOrigin;
        [Tooltip("Scene-frame corner of cell (0,0): the grid's minimum x and z.")]
        public Vector3 gridOrigin = new Vector3(-300f, 0f, -300f);
        [Tooltip("Cell edge length in metres (square cells).")]
        public float cellSize = 200f;
        [Tooltip("Cells along x.")]
        public int columns = 3;
        [Tooltip("Cells along z.")]
        public int rows = 3;
        [Tooltip("Cell id pattern; {map}, {x} and {y} are replaced. A cell id is the channel id of the map server serving it and must be unique across the whole world.")]
        public string cellIdFormat = "{map}-{x}-{y}";
        [Tooltip("Optional explicit ids, row-major (index = y * columns + x); overrides the pattern.")]
        public string[] cellIds;
        [Tooltip("Optional explicit cells (scene-frame rectangles of any size, non-overlapping). When set, these ARE the map's cells and the grid fields above are ignored: small cells where players gather, large ones over empty land.")]
        public MeshCellRect[] cellRects;
        [Tooltip("Static: these cells are the map's cut for good (the default, and how every map behaved before 0.72). Dynamic: they are base cells that the central may cut into more cells while they are busy and merge back when they are quiet.")]
        public MeshCellMode cellMode = MeshCellMode.Static;
        [Tooltip("Dynamic maps: the narrowest a new cut may make a cell, on either axis, in metres (0 = the registry's default). Never below the band width.")]
        public float minCellSize;
        [Tooltip("Dynamic maps: the most cells one base cell may become (0 = the registry's default).")]
        public int maxCellsPerBase;
        [Tooltip("Dynamic maps: let the central split busy cells by itself (off: only splits an operator asks for).")]
        public bool autoSplit;
        [Tooltip("Dynamic maps: let the central merge quiet cells back by itself.")]
        public bool autoMerge;
        [Tooltip("On: this map's cells link only to the cells they need (band neighbours, and the cells a handoff, ghost or lent record names), opening a link when one is needed and closing it once idle; linkAllZones is ignored for them. Off: the map managers' linkAllZones decides (every cell of the map, which a link server's 64 connections caps at about 64 cells).")]
        public bool onDemandLinks;

        public bool IsExplicit => cellRects != null && cellRects.Length > 0;
        public bool IsDynamic => cellMode == MeshCellMode.Dynamic;

        /// <summary>CopyModeFrom takes another layout's cell mode and dynamic settings.</summary>
        public void CopyModeFrom(MeshMapLayout other)
        {
            if (other == null)
                return;
            cellMode = other.cellMode;
            minCellSize = other.minCellSize;
            maxCellsPerBase = other.maxCellsPerBase;
            autoSplit = other.autoSplit;
            autoMerge = other.autoMerge;
            onDemandLinks = other.onDemandLinks;
        }

        public string CellIdAt(int x, int y)
        {
            int index = y * columns + x;
            if (cellIds != null && index >= 0 && index < cellIds.Length && !string.IsNullOrEmpty(cellIds[index]))
                return cellIds[index];
            return (cellIdFormat ?? "{map}-{x}-{y}").Replace("{map}", mapId).Replace("{x}", x.ToString()).Replace("{y}", y.ToString());
        }

        /// <summary>Grid coordinate of a scene-frame position, clamped into the grid (outside the grid = the nearest cell).</summary>
        public Vector2Int GridCoordOf(Vector3 scenePos)
        {
            int x = Mathf.FloorToInt((scenePos.x - gridOrigin.x) / cellSize);
            int y = Mathf.FloorToInt((scenePos.z - gridOrigin.z) / cellSize);
            return new Vector2Int(Mathf.Clamp(x, 0, columns - 1), Mathf.Clamp(y, 0, rows - 1));
        }
    }

    /// <summary>A cell at runtime: its rectangle in the world, the frame its positions are expressed in, and its neighbours.</summary>
    public sealed class MeshCell
    {
        public string id;
        public string mapId;
        public MeshMapLayout layout;
        public int gridX, gridY;
        /// <summary>World position of the origin that positions on this cell's server are relative to.</summary>
        public Vector3 frameOrigin;
        /// <summary>What the cell is responsible for (empty for a dynamic cell being made or emptied).</summary>
        public MeshRect owned;
        /// <summary>What the cell simulates and streams around: owned, or more while a split or merge moves things.</summary>
        public MeshRect interest;
        /// <summary>The rectangle of the asset cell this one was cut from (its own rectangle for an asset cell).</summary>
        public MeshRect baseRect;
        /// <summary>The asset cell this one was cut from (an asset cell names itself).</summary>
        public string baseId;
        public MeshCellState state;
        /// <summary>A child the central made at runtime (not a cell of the asset).</summary>
        public bool isDynamic;
        /// <summary>Touching neighbours: who owns what lies across an edge, where handoffs go and which cells stay awake together.</summary>
        public readonly List<MeshCell> neighbours = new List<MeshCell>();
        /// <summary>Band neighbours: the cells this one streams puppets to and links with (its touching neighbours on a Static map).</summary>
        public readonly List<MeshCell> bandNeighbours = new List<MeshCell>();

        public float worldMinX => owned.minX;
        public float worldMinZ => owned.minZ;
        public float worldMaxX => owned.maxX;
        public float worldMaxZ => owned.maxZ;

        /// <summary>Whether this cell belongs to a Dynamic map.</summary>
        public bool OnDynamicMap => layout != null && layout.IsDynamic;
        /// <summary>Whether players may be routed here: it serves and owns something.</summary>
        public bool IsRoutable => (state == MeshCellState.Static || state == MeshCellState.Active) && !owned.IsEmpty;

        public Vector3 SceneToWorld(Vector3 p) => p + frameOrigin;
        public Vector3 WorldToScene(Vector3 p) => p - frameOrigin;
        /// <summary>Add to a position in this cell's frame to express it in <paramref name="other"/>'s frame.</summary>
        public Vector3 OffsetTo(MeshCell other) => frameOrigin - other.frameOrigin;

        public bool ContainsWorld(float wx, float wz) => owned.Contains(wx, wz);

        /// <summary>Chebyshev distance from a position in this cell's frame to <paramref name="other"/>'s owned rectangle (0 inside it, infinite when it owns nothing): the shared line for an edge neighbour, the corner for a diagonal one.</summary>
        public float DistanceTo(MeshCell other, Vector3 scenePos)
        {
            return other.owned.DistanceTo(scenePos.x + frameOrigin.x, scenePos.z + frameOrigin.z);
        }

        /// <summary>Chebyshev distance from a position in this cell's frame to <paramref name="other"/>'s interest rectangle: how far a band reaches into a cell that is being made.</summary>
        public float InterestDistanceTo(MeshCell other, Vector3 scenePos)
        {
            return other.interest.DistanceTo(scenePos.x + frameOrigin.x, scenePos.z + frameOrigin.z);
        }

        /// <summary>Signed distance from a position in this cell's frame to its own boundary: positive inside, negative once past it (negative infinity when it owns nothing).</summary>
        public float DistanceToEdge(Vector3 scenePos)
        {
            return owned.DistanceToEdge(scenePos.x + frameOrigin.x, scenePos.z + frameOrigin.z);
        }

        /// <summary>Gap between the two owned rectangles (0 when they touch or overlap).</summary>
        public float GapTo(MeshCell other) => owned.GapTo(other.owned);

        /// <summary>Gap between the two interest rectangles.</summary>
        public float InterestGapTo(MeshCell other) => interest.GapTo(other.interest);

        /// <summary>SameAs: the same cell, even as another instance (a registry rebuilt or re-cut keeps cells by id and map).</summary>
        public bool SameAs(MeshCell other)
        {
            return other != null && (ReferenceEquals(this, other)
                || (string.Equals(id, other.id, StringComparison.OrdinalIgnoreCase) && string.Equals(mapId, other.mapId, StringComparison.Ordinal)));
        }

        /// <summary>Same compares two cells that may be null, like == on the references but by identity.</summary>
        public static bool Same(MeshCell a, MeshCell b) => ReferenceEquals(a, b) || (a != null && a.SameAs(b));

        public override string ToString() => id;
    }

    [CreateAssetMenu(fileName = "ServerMeshWorld", menuName = "Server Meshing/World Registry", order = 100)]
    public class MeshWorldRegistry : ScriptableObject
    {
        /// <summary>Band membership is a bit per neighbour in an int.</summary>
        public const int MaxNeighbours = 32;

        [Tooltip("One layout per meshed map. Maps without a layout keep the kit's single-server behaviour.")]
        public List<MeshMapLayout> maps = new List<MeshMapLayout>();
        [Tooltip("Two cells whose world rectangles come within this many metres of each other (edge or corner) are neighbours, so grids of different maps can touch without exact float alignment.")]
        public float touchTolerance = 0.5f;
        [Tooltip("Settings of dynamic cells shared by every process (only read when a map is Dynamic).")]
        public MeshDynamicSettings dynamicSettings = new MeshDynamicSettings();

        private List<MeshCell> _cells;
        private List<MeshCell> _baseCells;
        private Dictionary<string, MeshCell> _byKey;
        private Dictionary<string, MeshCell> _byId;
        private Dictionary<string, MeshMapLayout> _layouts;
        private Dictionary<string, List<MeshCell>> _cellsByMap;
        private Dictionary<string, List<MeshCell>> _familyByBase;
        [NonSerialized] private MeshLayoutSnapshot _snapshot;
        [NonSerialized] private MeshLayoutPair _pair;
        private static readonly List<MeshCell> s_none = new List<MeshCell>();

        /// <summary>LayoutChanged is raised after a runtime layout is applied (never for a Static-only world).</summary>
        public event Action<MeshLayoutChange> LayoutChanged;

        public IReadOnlyList<MeshCell> Cells { get { Ensure(); return _cells; } }

        /// <summary>The ghost band every process uses for band neighbours and the narrowest cut: MESH_BAND, else dynamicSettings.bandWidth.</summary>
        public float BandWidth { get; private set; } = 80f;

        /// <summary>The pair of the runtime layout applied (None when there is none).</summary>
        public MeshLayoutPair AppliedPair => _pair;

        /// <summary>A copy of the runtime layout applied, or null.</summary>
        public MeshLayoutSnapshot AppliedSnapshot => _snapshot?.Clone();

        public bool HasDynamicMap
        {
            get
            {
                Ensure();
                foreach (MeshMapLayout layout in _layouts.Values)
                {
                    if (layout.IsDynamic)
                        return true;
                }
                return false;
            }
        }

        public bool IsDynamicMap(string mapId)
        {
            MeshMapLayout layout = GetLayout(mapId);
            return layout != null && layout.IsDynamic;
        }

        public MeshMapLayout GetLayout(string mapId)
        {
            Ensure();
            return !string.IsNullOrEmpty(mapId) && _layouts.TryGetValue(mapId, out MeshMapLayout layout) ? layout : null;
        }

        public bool HasLayout(string mapId) => GetLayout(mapId) != null;

        public IReadOnlyList<MeshCell> CellsOf(string mapId)
        {
            Ensure();
            return !string.IsNullOrEmpty(mapId) && _cellsByMap.TryGetValue(mapId, out List<MeshCell> cells) ? cells : s_none;
        }

        /// <summary>The live cells cut from one asset cell: itself alone on a Static map or an uncut base.</summary>
        public IReadOnlyList<MeshCell> FamilyOf(string baseId)
        {
            Ensure();
            return !string.IsNullOrEmpty(baseId) && _familyByBase.TryGetValue(baseId, out List<MeshCell> family) ? family : s_none;
        }

        public MeshCell GetCell(string mapId, string cellId)
        {
            Ensure();
            return !string.IsNullOrEmpty(mapId) && !string.IsNullOrEmpty(cellId) && _byKey.TryGetValue(Key(mapId, cellId), out MeshCell cell) ? cell : null;
        }

        /// <summary>A cell by its id alone (ids are unique across the world: they are the map servers' channel ids). Retired cells still resolve.</summary>
        public MeshCell GetCellById(string cellId)
        {
            Ensure();
            return !string.IsNullOrEmpty(cellId) && _byId.TryGetValue(cellId, out MeshCell cell) ? cell : null;
        }

        public MeshCell CellAtGrid(string mapId, int x, int y)
        {
            MeshMapLayout layout = GetLayout(mapId);
            if (layout == null || x < 0 || y < 0 || x >= layout.columns || y >= layout.rows)
                return null;
            return GetCell(mapId, layout.CellIdAt(x, y));
        }

        /// <summary>The cell of this map owning a scene-frame position, clamped to the map's grid (outside it = the nearest cell); null when the map has no layout.</summary>
        public MeshCell CellAt(string mapId, Vector3 scenePos)
        {
            MeshMapLayout layout = GetLayout(mapId);
            if (layout == null)
                return null;
            float wx = scenePos.x + layout.worldOrigin.x, wz = scenePos.z + layout.worldOrigin.z;
            if (!layout.IsExplicit)
            {
                Vector2Int coord = layout.GridCoordOf(scenePos);
                MeshCell baseCell = CellAtGrid(mapId, coord.x, coord.y);
                if (baseCell == null)
                    return null;
                IReadOnlyList<MeshCell> family = FamilyOf(baseCell.id);
                return family.Count > 1 ? ContainingOrNearest(family, wx, wz) : baseCell;
            }
            // Explicit rectangles: the one containing the position, else the nearest one of the map.
            return ContainingOrNearest(CellsOf(mapId), wx, wz);
        }

        private static MeshCell ContainingOrNearest(IReadOnlyList<MeshCell> cells, float wx, float wz)
        {
            MeshCell nearest = null;
            float nearestGap = float.MaxValue;
            for (int i = 0; i < cells.Count; ++i)
            {
                MeshCell cell = cells[i];
                if (cell.owned.IsEmpty)
                    continue;
                if (cell.ContainsWorld(wx, wz))
                    return cell;
                float gap = cell.owned.DistanceTo(wx, wz);
                if (gap < nearestGap) { nearestGap = gap; nearest = cell; }
            }
            return nearest;
        }

        /// <summary>The cell of any map whose rectangle contains a world position, or null.</summary>
        public MeshCell CellAtWorld(Vector3 worldPos)
        {
            Ensure();
            for (int i = 0; i < _cells.Count; ++i)
            {
                if (_cells[i].ContainsWorld(worldPos.x, worldPos.z))
                    return _cells[i];
            }
            return null;
        }

        /// <summary>
        /// Who owns a position given in <paramref name="from"/>'s frame: <paramref name="from"/> itself when inside it,
        /// else the neighbour (of any map) or any cell containing it, else the nearest cell of <paramref name="from"/>'s map.
        /// </summary>
        public MeshCell OwnerOf(MeshCell from, Vector3 scenePos)
        {
            if (from == null)
                return null;
            float wx = scenePos.x + from.frameOrigin.x, wz = scenePos.z + from.frameOrigin.z;
            if (from.ContainsWorld(wx, wz))
                return from;
            for (int i = 0; i < from.neighbours.Count; ++i)
            {
                if (from.neighbours[i].ContainsWorld(wx, wz))
                    return from.neighbours[i];
            }
            MeshCell any = CellAtWorld(new Vector3(wx, 0f, wz));
            return any ?? CellAt(from.mapId, scenePos);
        }

        private static string Key(string mapId, string cellId) => mapId + "|" + cellId;

        private void Ensure()
        {
            if (_cells == null)
                Rebuild();
        }

        private void OnEnable()
        {
            _cells = null;
#if UNITY_EDITOR
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
#endif
        }

        private void OnValidate() { _cells = null; }

#if UNITY_EDITOR
        private void OnDisable() { EditorApplication.playModeStateChanged -= OnPlayModeStateChanged; }

        // The editor does not reload the domain when leaving Play, and the central, login, spawner and editor client share
        // this one asset: a layout applied during Play would otherwise still be applied in the next one.
        private void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingPlayMode || change == PlayModeStateChange.EnteredEditMode)
                ResetRuntimeLayout();
        }
#endif

        /// <summary>ResetRuntimeLayout drops the runtime layout and its subscribers: the asset's cut again.</summary>
        public void ResetRuntimeLayout()
        {
            _snapshot = null;
            _pair = MeshLayoutPair.None;
            LayoutChanged = null;
            _cells = null;
        }

        /// <summary>Build the cells and their adjacency from the layouts (once; again after the asset changes). A runtime layout survives it.</summary>
        public void Rebuild()
        {
            // Cells are kept by (map, id) across rebuilds, so whoever holds a MeshCell keeps a live one. Each instance is
            // claimed at most once: two asset cells sharing a key (an error reported below) must not become one object.
            Dictionary<string, MeshCell> pool = _byKey != null ? new Dictionary<string, MeshCell>(_byKey, StringComparer.OrdinalIgnoreCase) : new Dictionary<string, MeshCell>(StringComparer.OrdinalIgnoreCase);
            _cells = new List<MeshCell>();
            _baseCells = new List<MeshCell>();
            _byKey = new Dictionary<string, MeshCell>(StringComparer.OrdinalIgnoreCase);
            _byId = new Dictionary<string, MeshCell>(StringComparer.OrdinalIgnoreCase);
            _layouts = new Dictionary<string, MeshMapLayout>();
            _cellsByMap = new Dictionary<string, List<MeshCell>>();
            _familyByBase = new Dictionary<string, List<MeshCell>>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (MeshMapLayout layout in maps)
            {
                if (layout == null || string.IsNullOrEmpty(layout.mapId))
                    continue;
                if (!layout.IsExplicit && (layout.columns <= 0 || layout.rows <= 0 || layout.cellSize <= 0f))
                    continue;
                if (_layouts.ContainsKey(layout.mapId))
                {
                    Debug.LogError($"[Mesh] World registry: map '{layout.mapId}' has two layouts; the first one is used.");
                    continue;
                }
                _layouts[layout.mapId] = layout;
                List<MeshCell> cells = new List<MeshCell>(layout.IsExplicit ? layout.cellRects.Length : layout.columns * layout.rows);
                _cellsByMap[layout.mapId] = cells;
                if (layout.IsExplicit)
                {
                    for (int i = 0; i < layout.cellRects.Length; ++i)
                    {
                        MeshCellRect rect = layout.cellRects[i];
                        if (rect == null || string.IsNullOrEmpty(rect.id) || rect.size.x <= 0f || rect.size.y <= 0f)
                        {
                            Debug.LogError($"[Mesh] World registry: map '{layout.mapId}' explicit cell #{i} needs an id and a positive size; skipped.");
                            continue;
                        }
                        MeshCell cell = AssetCell(pool, layout, rect.id, i, -1, MeshRect.FromMinSize(layout.worldOrigin.x + rect.min.x, layout.worldOrigin.z + rect.min.y, rect.size.x, rect.size.y));
                        for (int j = 0; j < cells.Count; ++j)
                        {
                            MeshCell other = cells[j];
                            float overlapX = Mathf.Min(cell.worldMaxX, other.worldMaxX) - Mathf.Max(cell.worldMinX, other.worldMinX);
                            float overlapZ = Mathf.Min(cell.worldMaxZ, other.worldMaxZ) - Mathf.Max(cell.worldMinZ, other.worldMinZ);
                            if (overlapX > touchTolerance && overlapZ > touchTolerance)
                                Debug.LogError($"[Mesh] World registry: cells {cell.id} and {other.id} of map {layout.mapId} overlap by {overlapX:0.#} x {overlapZ:0.#} m; ownership there is ambiguous.");
                        }
                        if (!ids.Add(cell.id))
                            Debug.LogError($"[Mesh] World registry: cell id '{cell.id}' (map {layout.mapId}) is not unique across the world; the map servers' cluster keys would collide.");
                        AddAssetCell(cell, cells);
                    }
                    continue;
                }
                for (int y = 0; y < layout.rows; ++y)
                {
                    for (int x = 0; x < layout.columns; ++x)
                    {
                        float minX = layout.worldOrigin.x + layout.gridOrigin.x + x * layout.cellSize;
                        float minZ = layout.worldOrigin.z + layout.gridOrigin.z + y * layout.cellSize;
                        MeshCell cell = AssetCell(pool, layout, layout.CellIdAt(x, y), x, y, MeshRect.FromMinSize(minX, minZ, layout.cellSize, layout.cellSize));
                        if (!ids.Add(cell.id))
                            Debug.LogError($"[Mesh] World registry: cell id '{cell.id}' (map {layout.mapId}) is not unique across the world; the map servers' cluster keys collide.");
                        AddAssetCell(cell, cells);
                    }
                }
            }
            BandWidth = ReadBandWidth();
            if (_snapshot != null)
            {
                // Judged against itself: every rectangle is carried over, so only rules the asset itself now breaks drop it.
                MeshLayoutWorld world = ValidationWorld();
                if (MeshLayoutValidator.Validate(world, _snapshot, _snapshot, out string reason))
                    ApplyEntries(_snapshot, pool);
                else
                {
                    Debug.LogError($"[Mesh] World registry: the applied layout {_pair} no longer fits the asset ({reason}); dropped");
                    _snapshot = null;
                    _pair = MeshLayoutPair.None;
                }
            }
            RecomputeAdjacency();
        }

        private static MeshCell AssetCell(Dictionary<string, MeshCell> pool, MeshMapLayout layout, string id, int gridX, int gridY, MeshRect rect)
        {
            string key = Key(layout.mapId, id);
            if (pool.TryGetValue(key, out MeshCell cell))
                pool.Remove(key);
            else
                cell = new MeshCell();
            cell.id = id;
            cell.mapId = layout.mapId;
            cell.layout = layout;
            cell.gridX = gridX;
            cell.gridY = gridY;
            cell.frameOrigin = layout.worldOrigin;
            ResetToBase(cell, rect);
            return cell;
        }

        private static void ResetToBase(MeshCell cell, MeshRect rect)
        {
            cell.owned = rect;
            cell.interest = rect;
            cell.baseRect = rect;
            cell.baseId = cell.id;
            cell.isDynamic = false;
            cell.state = cell.OnDynamicMap ? MeshCellState.Active : MeshCellState.Static;
            cell.neighbours.Clear();
            cell.bandNeighbours.Clear();
        }

        private void AddAssetCell(MeshCell cell, List<MeshCell> mapCells)
        {
            _byKey[Key(cell.mapId, cell.id)] = cell;
            if (!_byId.ContainsKey(cell.id)) _byId[cell.id] = cell;
            _cells.Add(cell);
            _baseCells.Add(cell);
            mapCells.Add(cell);
            if (!_familyByBase.ContainsKey(cell.id))
                _familyByBase[cell.id] = new List<MeshCell> { cell };
        }

        private float ReadBandWidth()
        {
            string env = Environment.GetEnvironmentVariable(MeshMapNetworkManager.ENV_BAND);
            if (!string.IsNullOrEmpty(env) && float.TryParse(env, NumberStyles.Float, CultureInfo.InvariantCulture, out float band) && band > 0f)
                return band;
            return dynamicSettings != null && dynamicSettings.bandWidth > 0f ? dynamicSettings.bandWidth : 80f;
        }

        /// <summary>The world as the validator sees it: the asset's cells and the settings every process shares.</summary>
        public MeshLayoutWorld ValidationWorld()
        {
            Ensure();
            MeshLayoutWorld world = new MeshLayoutWorld { bandWidth = BandWidth, touchTolerance = touchTolerance, maxNeighbours = MaxNeighbours };
            foreach (MeshCell cell in _baseCells)
                world.baseCells.Add(new MeshLayoutBaseCell { id = cell.id, mapId = cell.mapId, rect = cell.baseRect });
            foreach (MeshMapLayout layout in _layouts.Values)
            {
                world.dynamicByMap[layout.mapId] = layout.IsDynamic;
                if (layout.IsDynamic)
                    world.minCellSizeByMap[layout.mapId] = Mathf.Max(BandWidth, layout.minCellSize > 0f ? layout.minCellSize : dynamicSettings.defaultMinCellSize);
            }
            return world;
        }

        /// <summary>ApplyLayout applies a runtime layout that is newer than the one applied and keeps every rule; nothing changes when it fails.</summary>
        public bool ApplyLayout(MeshLayoutSnapshot snapshot, out string reason) => ApplyLayout(snapshot, out _, out reason);

        /// <summary>
        /// ApplyLayout applies a runtime layout: base cells of Dynamic maps take their entry's cut, children appear, dynamic
        /// cells missing from it are Retired, and adjacency is recomputed. A pair that is not newer is refused as stale, an
        /// equal one is "already applied" (true, no change). An invalid snapshot changes nothing.
        /// </summary>
        public bool ApplyLayout(MeshLayoutSnapshot snapshot, out MeshLayoutChange change, out string reason)
        {
            change = null;
            reason = null;
            Ensure();
            if (snapshot == null)
            {
                reason = "no snapshot";
                return false;
            }
            if (snapshot.pair == _pair)
            {
                reason = "already applied";
                return true;
            }
            if (!snapshot.pair.IsNewerThan(_pair))
            {
                reason = $"stale: {snapshot.pair} is older than the applied {_pair}";
                return false;
            }
            if (!MeshLayoutValidator.Validate(ValidationWorld(), snapshot, _snapshot, out reason))
                return false;

            Dictionary<string, CellShape> before = Shapes();
            MeshLayoutSnapshot copy = snapshot.Clone();
            ApplyEntries(copy, null);
            RecomputeAdjacency();
            change = Diff(before, snapshot.pair);
            _snapshot = copy;
            _pair = snapshot.pair;
            RaiseLayoutChanged(change);
            return true;
        }

        private void RaiseLayoutChanged(MeshLayoutChange change)
        {
            Action<MeshLayoutChange> handlers = LayoutChanged;
            if (handlers == null)
                return;
            foreach (Delegate handler in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<MeshLayoutChange>)handler)(change);
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
            }
        }

        /// <summary>Put a (validated) snapshot's entries on the cells: base cells of Dynamic maps are reset first, so the snapshot is the whole truth.</summary>
        private void ApplyEntries(MeshLayoutSnapshot snapshot, Dictionary<string, MeshCell> pool)
        {
            foreach (MeshCell cell in _baseCells)
            {
                if (cell.OnDynamicMap)
                    ResetToBase(cell, cell.baseRect);
            }
            HashSet<string> named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (MeshLayoutEntry entry in snapshot.entries)
            {
                named.Add(entry.id);
                MeshCell cell = GetCellById(entry.id);
                if (cell == null && pool != null && pool.TryGetValue(Key(entry.mapId, entry.id), out MeshCell pooled))
                {
                    pool.Remove(Key(entry.mapId, entry.id));
                    cell = pooled;
                }
                if (cell == null)
                {
                    MeshCell baseCell = GetCellById(entry.baseId);
                    cell = new MeshCell
                    {
                        id = entry.id, mapId = entry.mapId, layout = baseCell.layout, gridX = -1, gridY = -1,
                        frameOrigin = baseCell.frameOrigin, baseRect = baseCell.baseRect,
                    };
                }
                if (!_byId.ContainsKey(entry.id))
                {
                    _byId[entry.id] = cell;
                    _byKey[Key(entry.mapId, entry.id)] = cell;
                }
                cell.baseId = entry.baseId;
                cell.isDynamic = !string.Equals(entry.id, entry.baseId, StringComparison.OrdinalIgnoreCase);
                if (cell.isDynamic)
                    cell.baseRect = GetCellById(entry.baseId).baseRect;
                cell.state = entry.state;
                cell.owned = entry.owned;
                cell.interest = entry.interest;
            }
            // A child the snapshot no longer names is gone: it stays resolvable by id, Retired and owning nothing.
            foreach (MeshCell cell in _byId.Values)
            {
                if (cell.isDynamic && !named.Contains(cell.id))
                {
                    cell.state = MeshCellState.Retired;
                    cell.owned = MeshRect.Empty;
                    cell.interest = MeshRect.Empty;
                }
            }
            RebuildLiveLists();
        }

        /// <summary>The live lists: the asset's cells in its order, then children by id; Retired cells in none of them.</summary>
        private void RebuildLiveLists()
        {
            List<MeshCell> children = new List<MeshCell>();
            foreach (MeshCell cell in _byId.Values)
            {
                if (cell.isDynamic && cell.state != MeshCellState.Retired)
                    children.Add(cell);
            }
            children.Sort((a, b) => string.CompareOrdinal(a.id, b.id));
            _cells.Clear();
            foreach (List<MeshCell> mapCells in _cellsByMap.Values)
                mapCells.Clear();
            _familyByBase.Clear();
            foreach (MeshCell cell in _baseCells)
                AddLive(cell);
            foreach (MeshCell cell in children)
                AddLive(cell);
        }

        private void AddLive(MeshCell cell)
        {
            if (cell.state == MeshCellState.Retired)
                return;
            _cells.Add(cell);
            if (_cellsByMap.TryGetValue(cell.mapId, out List<MeshCell> mapCells))
                mapCells.Add(cell);
            if (!_familyByBase.TryGetValue(cell.baseId, out List<MeshCell> family))
                _familyByBase[cell.baseId] = family = new List<MeshCell>();
            family.Add(cell);
        }

        /// <summary>Touching and band neighbours for every live cell, in one ordered pass (a Static cell's two lists are identical).</summary>
        private void RecomputeAdjacency()
        {
            for (int i = 0; i < _cells.Count; ++i)
            {
                _cells[i].neighbours.Clear();
                _cells[i].bandNeighbours.Clear();
            }
            MeshLayoutValidator.LiveCell a2 = new MeshLayoutValidator.LiveCell(), b2 = new MeshLayoutValidator.LiveCell();
            for (int i = 0; i < _cells.Count; ++i)
            {
                MeshCell a = _cells[i];
                Describe(a, a2);
                for (int j = 0; j < _cells.Count; ++j)
                {
                    if (i == j) continue;
                    MeshCell b = _cells[j];
                    Describe(b, b2);
                    if (MeshLayoutValidator.Touching(a.owned, b.owned, touchTolerance))
                        a.neighbours.Add(b);
                    if (MeshLayoutValidator.BandNear(a2, b2, BandWidth, touchTolerance))
                        a.bandNeighbours.Add(b);
                }
                bool bandDiffers = !SameIds(a.neighbours, a.bandNeighbours);
                if (a.neighbours.Count > MaxNeighbours)
                {
                    Debug.LogError($"[Mesh] World registry: cell {a.id} touches {a.neighbours.Count} cells; only the first {MaxNeighbours} are neighbours (band membership is a bit per neighbour).");
                    a.neighbours.RemoveRange(MaxNeighbours, a.neighbours.Count - MaxNeighbours);
                }
                if (a.bandNeighbours.Count > MaxNeighbours)
                {
                    if (bandDiffers)
                        Debug.LogError($"[Mesh] World registry: cell {a.id} has {a.bandNeighbours.Count} band neighbours; only the first {MaxNeighbours} stream to it.");
                    a.bandNeighbours.RemoveRange(MaxNeighbours, a.bandNeighbours.Count - MaxNeighbours);
                }
            }
        }

        private static void Describe(MeshCell cell, MeshLayoutValidator.LiveCell into)
        {
            into.id = cell.id;
            into.mapId = cell.mapId;
            into.baseId = cell.baseId;
            into.state = cell.state;
            into.owned = cell.owned;
            into.interest = cell.interest;
            into.onDynamicMap = cell.OnDynamicMap;
        }

        private static bool SameIds(List<MeshCell> a, List<MeshCell> b)
        {
            if (a.Count != b.Count)
                return false;
            for (int i = 0; i < a.Count; ++i)
            {
                if (!ReferenceEquals(a[i], b[i]))
                    return false;
            }
            return true;
        }

        private sealed class CellShape
        {
            public MeshCellState state;
            public MeshRect owned, interest;
            public string neighbours, band;
        }

        private Dictionary<string, CellShape> Shapes()
        {
            Dictionary<string, CellShape> shapes = new Dictionary<string, CellShape>(StringComparer.OrdinalIgnoreCase);
            foreach (MeshCell cell in _byId.Values)
            {
                shapes[cell.id] = new CellShape
                {
                    state = cell.state, owned = cell.owned, interest = cell.interest,
                    neighbours = IdList(cell.neighbours), band = IdList(cell.bandNeighbours),
                };
            }
            return shapes;
        }

        private static string IdList(List<MeshCell> cells)
        {
            string[] ids = new string[cells.Count];
            for (int i = 0; i < cells.Count; ++i)
                ids[i] = cells[i].id;
            return string.Join(",", ids);
        }

        private MeshLayoutChange Diff(Dictionary<string, CellShape> before, MeshLayoutPair pair)
        {
            MeshLayoutChange change = new MeshLayoutChange { pair = pair };
            foreach (MeshCell cell in _byId.Values)
            {
                if (!before.TryGetValue(cell.id, out CellShape was))
                {
                    change.changedIds.Add(cell.id);
                    change.neighbourChangedIds.Add(cell.id);
                    change.bandChangedIds.Add(cell.id);
                    continue;
                }
                if (was.state != cell.state || !was.owned.Equals(cell.owned) || !was.interest.Equals(cell.interest))
                    change.changedIds.Add(cell.id);
                if (was.neighbours != IdList(cell.neighbours))
                    change.neighbourChangedIds.Add(cell.id);
                if (was.band != IdList(cell.bandNeighbours))
                    change.bandChangedIds.Add(cell.id);
            }
            return change;
        }
    }
}
