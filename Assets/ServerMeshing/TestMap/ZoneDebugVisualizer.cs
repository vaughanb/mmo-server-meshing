using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// Draws one map's cells from the world registry — their rectangles and ids — as Gizmos in the editor and as debug
    /// lines at runtime, in that map's scene frame. Works for grid and explicit (any-size) layouts alike.
    /// </summary>
    public class ZoneDebugVisualizer : MonoBehaviour
    {
        [Header("Configuration")]
        [Tooltip("World registry (every meshed map's cells).")]
        public MeshWorldRegistry world;

        [Tooltip("The map whose cells to draw (this scene's map).")]
        public string mapId = "ServerMeshTestMap";

        [Header("Visualization Settings")]
        [Tooltip("Height of boundary lines drawn in the scene.")]
        public float lineHeight = 50f;

        [Tooltip("Toggle runtime debug line drawing.")]
        public bool showAtRuntime = true;

        [Tooltip("Toggle cell id labels in scene view.")]
        public bool showZoneLabels = true;

        [Tooltip("Color of boundary lines.")]
        public Color boundaryColor = Color.red;

        [Tooltip("Color to highlight the player's current cell.")]
        public Color currentZoneColor = Color.green;

        private IReadOnlyList<MeshCell> Cells => world != null ? world.CellsOf(mapId) : null;

        private void Update()
        {
            IReadOnlyList<MeshCell> cells = Cells;
            if (!showAtRuntime || cells == null)
                return;
            DrawCells(cells, (a, b) => Debug.DrawLine(a, b, boundaryColor));
        }

        /// <summary>Every cell's four edges as floor segments, top segments and corner uprights, in the scene frame.</summary>
        private void DrawCells(IReadOnlyList<MeshCell> cells, System.Action<Vector3, Vector3> line)
        {
            for (int i = 0; i < cells.Count; ++i)
            {
                MeshCell cell = cells[i];
                Vector3 min = new Vector3(cell.worldMinX, 0f, cell.worldMinZ) - cell.frameOrigin;
                Vector3 max = new Vector3(cell.worldMaxX, 0f, cell.worldMaxZ) - cell.frameOrigin;
                Vector3 bl = new Vector3(min.x, 0f, min.z), br = new Vector3(max.x, 0f, min.z), tl = new Vector3(min.x, 0f, max.z), tr = new Vector3(max.x, 0f, max.z);
                Vector3 up = Vector3.up * lineHeight;
                line(bl, br); line(br, tr); line(tr, tl); line(tl, bl);
                line(bl + up, br + up); line(br + up, tr + up); line(tr + up, tl + up); line(tl + up, bl + up);
                line(bl, bl + up); line(br, br + up); line(tr, tr + up); line(tl, tl + up);
            }
        }

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            IReadOnlyList<MeshCell> cells = Cells;
            if (cells == null || cells.Count == 0)
                return;
            Gizmos.color = boundaryColor;
            DrawCells(cells, Gizmos.DrawLine);

            if (showZoneLabels)
            {
                GUIStyle labelStyle = new GUIStyle();
                labelStyle.normal.textColor = Color.white;
                labelStyle.fontSize = 14;
                labelStyle.fontStyle = FontStyle.Bold;
                labelStyle.alignment = TextAnchor.MiddleCenter;
                for (int i = 0; i < cells.Count; ++i)
                {
                    MeshCell cell = cells[i];
                    Vector3 center = new Vector3((cell.worldMinX + cell.worldMaxX) * 0.5f, 1f, (cell.worldMinZ + cell.worldMaxZ) * 0.5f) - cell.frameOrigin;
                    Handles.Label(center, $"{cell.id}  ({cell.worldMaxX - cell.worldMinX:0} x {cell.worldMaxZ - cell.worldMinZ:0} m, {cell.neighbours.Count} neighbours)", labelStyle);
                }
            }

            if (Application.isPlaying)
            {
                BasePlayerCharacterEntity player = GameInstance.PlayingCharacterEntity;
                MeshCell cell = player != null ? world.CellAt(mapId, player.EntityTransform.position) : null;
                if (cell != null)
                {
                    Vector3 min = new Vector3(cell.worldMinX, 0f, cell.worldMinZ) - cell.frameOrigin;
                    Vector3 size = new Vector3(cell.worldMaxX - cell.worldMinX, 1f, cell.worldMaxZ - cell.worldMinZ);
                    Gizmos.color = currentZoneColor * 0.3f;
                    Gizmos.DrawCube(min + new Vector3(size.x * 0.5f, 0.5f, size.z * 0.5f), size);
                }
            }
        }
#endif
    }
}
