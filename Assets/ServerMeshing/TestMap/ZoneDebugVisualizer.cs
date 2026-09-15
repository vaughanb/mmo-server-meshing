using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// Draws one map's cell boundaries and ids from the world registry as Gizmos (editor) and Debug.DrawLine (runtime),
    /// in that map's scene frame.
    /// </summary>
    public class ZoneDebugVisualizer : MonoBehaviour
    {
        [Header("Configuration")]
        [Tooltip("World registry (every meshed map's cells).")]
        public MeshWorldRegistry world;

        [Tooltip("The map whose layout to draw (this scene's map).")]
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

        private MeshMapLayout Layout => world != null ? world.GetLayout(mapId) : null;

        private void Update()
        {
            MeshMapLayout layout = Layout;
            if (!showAtRuntime || layout == null)
                return;
            DrawLines(layout, (a, b) => Debug.DrawLine(a, b, boundaryColor));
        }

        /// <summary>Every interior grid line as a floor segment, a top segment and two uprights.</summary>
        private void DrawLines(MeshMapLayout layout, System.Action<Vector3, Vector3> line)
        {
            Vector3 origin = layout.gridOrigin;
            float totalWidth = layout.columns * layout.cellSize;
            float totalHeight = layout.rows * layout.cellSize;
            for (int i = 1; i < layout.columns; i++)
            {
                float x = origin.x + i * layout.cellSize;
                Vector3 start = new Vector3(x, 0f, origin.z), end = new Vector3(x, 0f, origin.z + totalHeight);
                line(start, end); line(start + Vector3.up * lineHeight, end + Vector3.up * lineHeight);
                line(start, start + Vector3.up * lineHeight); line(end, end + Vector3.up * lineHeight);
            }
            for (int j = 1; j < layout.rows; j++)
            {
                float z = origin.z + j * layout.cellSize;
                Vector3 start = new Vector3(origin.x, 0f, z), end = new Vector3(origin.x + totalWidth, 0f, z);
                line(start, end); line(start + Vector3.up * lineHeight, end + Vector3.up * lineHeight);
                line(start, start + Vector3.up * lineHeight); line(end, end + Vector3.up * lineHeight);
            }
        }

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            MeshMapLayout layout = Layout;
            if (layout == null)
                return;
            Gizmos.color = boundaryColor;
            DrawLines(layout, Gizmos.DrawLine);

            // Outer boundary
            Vector3 origin = layout.gridOrigin;
            float totalWidth = layout.columns * layout.cellSize;
            float totalHeight = layout.rows * layout.cellSize;
            Gizmos.color = boundaryColor * 0.5f;
            Vector3 bl = new Vector3(origin.x, 0f, origin.z);
            Vector3 br = new Vector3(origin.x + totalWidth, 0f, origin.z);
            Vector3 tl = new Vector3(origin.x, 0f, origin.z + totalHeight);
            Vector3 tr = new Vector3(origin.x + totalWidth, 0f, origin.z + totalHeight);
            Gizmos.DrawLine(bl, br); Gizmos.DrawLine(br, tr); Gizmos.DrawLine(tr, tl); Gizmos.DrawLine(tl, bl);

            if (showZoneLabels)
            {
                GUIStyle labelStyle = new GUIStyle();
                labelStyle.normal.textColor = Color.white;
                labelStyle.fontSize = 14;
                labelStyle.fontStyle = FontStyle.Bold;
                labelStyle.alignment = TextAnchor.MiddleCenter;
                for (int y = 0; y < layout.rows; ++y)
                {
                    for (int x = 0; x < layout.columns; ++x)
                    {
                        Vector3 center = origin + new Vector3((x + 0.5f) * layout.cellSize, 1f, (y + 0.5f) * layout.cellSize);
                        Handles.Label(center, layout.CellIdAt(x, y), labelStyle);
                    }
                }
            }

            if (Application.isPlaying)
            {
                BasePlayerCharacterEntity player = GameInstance.PlayingCharacterEntity;
                MeshCell cell = player != null ? world.CellAt(mapId, player.EntityTransform.position) : null;
                if (cell != null)
                {
                    Vector3 min = new Vector3(cell.worldMinX, 0f, cell.worldMinZ) - layout.worldOrigin;
                    Gizmos.color = currentZoneColor * 0.3f;
                    Gizmos.DrawCube(min + new Vector3(layout.cellSize * 0.5f, 0.5f, layout.cellSize * 0.5f), new Vector3(layout.cellSize, 1f, layout.cellSize));
                }
            }
        }
#endif
    }
}
