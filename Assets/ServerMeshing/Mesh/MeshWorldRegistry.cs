// Server Meshing add-on — MeshWorldRegistry (DESIGN.md §9 routing, Q12/Q13 groundwork: the world as a graph of cells).
//
// A cell is the unit the mesh reasons about: a rectangle of the world served by one map server (its channel id is the
// cell id), cut from a kit map by a per-map layout (a uniform grid placed in one shared world frame). Neighbours are
// derived from placement: two cells whose rectangles touch (edge or corner) are neighbours, whichever map each comes
// from — so continents placed next to each other in the world join through their touching cells with no extra data.
// Positions on a server are in that cell's frame (the scene frame of its map today; a per-cell origin later — every
// conversion already goes through the cell so that change stays local).
using System;
using System.Collections.Generic;
using UnityEngine;

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

        public bool IsExplicit => cellRects != null && cellRects.Length > 0;

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
        public float worldMinX, worldMinZ, worldMaxX, worldMaxZ;
        public readonly List<MeshCell> neighbours = new List<MeshCell>();

        public Vector3 SceneToWorld(Vector3 p) => p + frameOrigin;
        public Vector3 WorldToScene(Vector3 p) => p - frameOrigin;
        /// <summary>Add to a position in this cell's frame to express it in <paramref name="other"/>'s frame.</summary>
        public Vector3 OffsetTo(MeshCell other) => frameOrigin - other.frameOrigin;

        public bool ContainsWorld(float wx, float wz) => wx >= worldMinX && wx < worldMaxX && wz >= worldMinZ && wz < worldMaxZ;

        /// <summary>Chebyshev distance from a position in this cell's frame to <paramref name="other"/>'s rectangle (0 inside it): the shared line for an edge neighbour, the corner for a diagonal one.</summary>
        public float DistanceTo(MeshCell other, Vector3 scenePos)
        {
            float wx = scenePos.x + frameOrigin.x, wz = scenePos.z + frameOrigin.z;
            float dx = Mathf.Max(other.worldMinX - wx, 0f, wx - other.worldMaxX);
            float dz = Mathf.Max(other.worldMinZ - wz, 0f, wz - other.worldMaxZ);
            return Mathf.Max(dx, dz);
        }

        /// <summary>Signed distance from a position in this cell's frame to its own boundary: positive inside, negative once past it.</summary>
        public float DistanceToEdge(Vector3 scenePos)
        {
            float wx = scenePos.x + frameOrigin.x, wz = scenePos.z + frameOrigin.z;
            return Mathf.Min(Mathf.Min(wx - worldMinX, worldMaxX - wx), Mathf.Min(wz - worldMinZ, worldMaxZ - wz));
        }

        /// <summary>Gap between the two rectangles (0 when they touch or overlap).</summary>
        public float GapTo(MeshCell other)
        {
            float dx = Mathf.Max(other.worldMinX - worldMaxX, worldMinX - other.worldMaxX, 0f);
            float dz = Mathf.Max(other.worldMinZ - worldMaxZ, worldMinZ - other.worldMaxZ, 0f);
            return Mathf.Max(dx, dz);
        }

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

        private List<MeshCell> _cells;
        private Dictionary<string, MeshCell> _byKey;
        private Dictionary<string, MeshCell> _byId;
        private Dictionary<string, MeshMapLayout> _layouts;
        private Dictionary<string, List<MeshCell>> _cellsByMap;
        private static readonly List<MeshCell> s_none = new List<MeshCell>();

        public IReadOnlyList<MeshCell> Cells { get { Ensure(); return _cells; } }

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

        public MeshCell GetCell(string mapId, string cellId)
        {
            Ensure();
            return !string.IsNullOrEmpty(mapId) && !string.IsNullOrEmpty(cellId) && _byKey.TryGetValue(Key(mapId, cellId), out MeshCell cell) ? cell : null;
        }

        /// <summary>A cell by its id alone (ids are unique across the world: they are the map servers' channel ids).</summary>
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
            if (!layout.IsExplicit)
            {
                Vector2Int coord = layout.GridCoordOf(scenePos);
                return CellAtGrid(mapId, coord.x, coord.y);
            }
            // Explicit rectangles: the one containing the position, else the nearest one of the map.
            IReadOnlyList<MeshCell> cells = CellsOf(mapId);
            float wx = scenePos.x + layout.worldOrigin.x, wz = scenePos.z + layout.worldOrigin.z;
            MeshCell nearest = null;
            float nearestGap = float.MaxValue;
            for (int i = 0; i < cells.Count; ++i)
            {
                MeshCell cell = cells[i];
                if (cell.ContainsWorld(wx, wz))
                    return cell;
                float gap = Mathf.Max(Mathf.Max(cell.worldMinX - wx, 0f, wx - cell.worldMaxX), Mathf.Max(cell.worldMinZ - wz, 0f, wz - cell.worldMaxZ));
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

        private void OnEnable() { _cells = null; }
        private void OnValidate() { _cells = null; }

        /// <summary>Build the cells and their adjacency from the layouts (once; again after the asset changes).</summary>
        public void Rebuild()
        {
            _cells = new List<MeshCell>();
            _byKey = new Dictionary<string, MeshCell>(StringComparer.OrdinalIgnoreCase);
            _byId = new Dictionary<string, MeshCell>(StringComparer.OrdinalIgnoreCase);
            _layouts = new Dictionary<string, MeshMapLayout>();
            _cellsByMap = new Dictionary<string, List<MeshCell>>();
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
                        MeshCell cell = new MeshCell { id = rect.id, mapId = layout.mapId, layout = layout, gridX = i, gridY = -1, frameOrigin = layout.worldOrigin };
                        cell.worldMinX = layout.worldOrigin.x + rect.min.x;
                        cell.worldMinZ = layout.worldOrigin.z + rect.min.y;
                        cell.worldMaxX = cell.worldMinX + rect.size.x;
                        cell.worldMaxZ = cell.worldMinZ + rect.size.y;
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
                        _byKey[Key(layout.mapId, cell.id)] = cell;
                        if (!_byId.ContainsKey(cell.id)) _byId[cell.id] = cell;
                        _cells.Add(cell);
                        cells.Add(cell);
                    }
                    continue;
                }
                for (int y = 0; y < layout.rows; ++y)
                {
                    for (int x = 0; x < layout.columns; ++x)
                    {
                        MeshCell cell = new MeshCell
                        {
                            id = layout.CellIdAt(x, y), mapId = layout.mapId, layout = layout, gridX = x, gridY = y,
                            frameOrigin = layout.worldOrigin,
                        };
                        cell.worldMinX = layout.worldOrigin.x + layout.gridOrigin.x + x * layout.cellSize;
                        cell.worldMinZ = layout.worldOrigin.z + layout.gridOrigin.z + y * layout.cellSize;
                        cell.worldMaxX = cell.worldMinX + layout.cellSize;
                        cell.worldMaxZ = cell.worldMinZ + layout.cellSize;
                        if (!ids.Add(cell.id))
                            Debug.LogError($"[Mesh] World registry: cell id '{cell.id}' (map {layout.mapId}) is not unique across the world; the map servers' cluster keys collide.");
                        _byKey[Key(layout.mapId, cell.id)] = cell;
                        if (!_byId.ContainsKey(cell.id)) _byId[cell.id] = cell;
                        _cells.Add(cell);
                        cells.Add(cell);
                    }
                }
            }
            for (int i = 0; i < _cells.Count; ++i)
            {
                MeshCell a = _cells[i];
                for (int j = 0; j < _cells.Count; ++j)
                {
                    if (i == j) continue;
                    MeshCell b = _cells[j];
                    if (a.GapTo(b) <= touchTolerance)
                        a.neighbours.Add(b);
                }
                if (a.neighbours.Count > MaxNeighbours)
                {
                    Debug.LogError($"[Mesh] World registry: cell {a.id} touches {a.neighbours.Count} cells; only the first {MaxNeighbours} are neighbours (band membership is a bit per neighbour).");
                    a.neighbours.RemoveRange(MaxNeighbours, a.neighbours.Count - MaxNeighbours);
                }
            }
        }
    }
}
