using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// Draws zone boundaries and labels as Gizmos (editor) and Debug.DrawLine (runtime)
    /// for visual debugging of the server mesh grid.
    /// </summary>
    public class ZoneDebugVisualizer : MonoBehaviour
    {
        [Header("Configuration")]
        [Tooltip("Reference to the grid configuration asset.")]
        public ServerMeshTestConfig config;

        [Header("Visualization Settings")]
        [Tooltip("Height of boundary lines drawn in the scene.")]
        public float lineHeight = 50f;

        [Tooltip("Toggle runtime debug line drawing.")]
        public bool showAtRuntime = true;

        [Tooltip("Toggle zone ID labels in scene view.")]
        public bool showZoneLabels = true;

        [Tooltip("Color of boundary lines.")]
        public Color boundaryColor = Color.red;

        [Tooltip("Color to highlight the player's current zone.")]
        public Color currentZoneColor = Color.green;

        private void Update()
        {
            if (!showAtRuntime || config == null)
                return;

            DrawBoundaryLines();
        }

        /// <summary>
        /// Draws boundary lines using Debug.DrawLine at runtime.
        /// </summary>
        private void DrawBoundaryLines()
        {
            Vector3 origin = config.gridOrigin;
            float totalWidth = config.gridWidth * config.zoneSize;
            float totalHeight = config.gridHeight * config.zoneSize;

            // Draw vertical boundary lines (X axis)
            for (int i = 1; i < config.gridWidth; i++)
            {
                float x = origin.x + i * config.zoneSize;
                Vector3 start = new Vector3(x, 0f, origin.z);
                Vector3 end = new Vector3(x, 0f, origin.z + totalHeight);
                Vector3 startTop = new Vector3(x, lineHeight, origin.z);
                Vector3 endTop = new Vector3(x, lineHeight, origin.z + totalHeight);

                Debug.DrawLine(start, end, boundaryColor);
                Debug.DrawLine(startTop, endTop, boundaryColor);
                Debug.DrawLine(start, startTop, boundaryColor);
                Debug.DrawLine(end, endTop, boundaryColor);
            }

            // Draw horizontal boundary lines (Z axis)
            for (int j = 1; j < config.gridHeight; j++)
            {
                float z = origin.z + j * config.zoneSize;
                Vector3 start = new Vector3(origin.x, 0f, z);
                Vector3 end = new Vector3(origin.x + totalWidth, 0f, z);
                Vector3 startTop = new Vector3(origin.x, lineHeight, z);
                Vector3 endTop = new Vector3(origin.x + totalWidth, lineHeight, z);

                Debug.DrawLine(start, end, boundaryColor);
                Debug.DrawLine(startTop, endTop, boundaryColor);
                Debug.DrawLine(start, startTop, boundaryColor);
                Debug.DrawLine(end, endTop, boundaryColor);
            }
        }

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            if (config == null)
                return;

            Vector3 origin = config.gridOrigin;
            float totalWidth = config.gridWidth * config.zoneSize;
            float totalHeight = config.gridHeight * config.zoneSize;

            Gizmos.color = boundaryColor;

            // Draw vertical boundary lines (X axis)
            for (int i = 1; i < config.gridWidth; i++)
            {
                float x = origin.x + i * config.zoneSize;
                Vector3 bottom1 = new Vector3(x, 0f, origin.z);
                Vector3 bottom2 = new Vector3(x, 0f, origin.z + totalHeight);
                Vector3 top1 = new Vector3(x, lineHeight, origin.z);
                Vector3 top2 = new Vector3(x, lineHeight, origin.z + totalHeight);

                Gizmos.DrawLine(bottom1, bottom2);
                Gizmos.DrawLine(top1, top2);
                Gizmos.DrawLine(bottom1, top1);
                Gizmos.DrawLine(bottom2, top2);
            }

            // Draw horizontal boundary lines (Z axis)
            for (int j = 1; j < config.gridHeight; j++)
            {
                float z = origin.z + j * config.zoneSize;
                Vector3 bottom1 = new Vector3(origin.x, 0f, z);
                Vector3 bottom2 = new Vector3(origin.x + totalWidth, 0f, z);
                Vector3 top1 = new Vector3(origin.x, lineHeight, z);
                Vector3 top2 = new Vector3(origin.x + totalWidth, lineHeight, z);

                Gizmos.DrawLine(bottom1, bottom2);
                Gizmos.DrawLine(top1, top2);
                Gizmos.DrawLine(bottom1, top1);
                Gizmos.DrawLine(bottom2, top2);
            }

            // Draw outer boundary
            Gizmos.color = boundaryColor * 0.5f;
            Vector3 bl = new Vector3(origin.x, 0f, origin.z);
            Vector3 br = new Vector3(origin.x + totalWidth, 0f, origin.z);
            Vector3 tl = new Vector3(origin.x, 0f, origin.z + totalHeight);
            Vector3 tr = new Vector3(origin.x + totalWidth, 0f, origin.z + totalHeight);
            Gizmos.DrawLine(bl, br);
            Gizmos.DrawLine(br, tr);
            Gizmos.DrawLine(tr, tl);
            Gizmos.DrawLine(tl, bl);

            // Draw zone labels
            if (showZoneLabels && config.zones != null)
            {
                GUIStyle labelStyle = new GUIStyle();
                labelStyle.normal.textColor = Color.white;
                labelStyle.fontSize = 14;
                labelStyle.fontStyle = FontStyle.Bold;
                labelStyle.alignment = TextAnchor.MiddleCenter;

                foreach (var zone in config.zones)
                {
                    Vector3 center = origin + new Vector3(
                        (zone.gridX + 0.5f) * config.zoneSize,
                        1f,
                        (zone.gridY + 0.5f) * config.zoneSize
                    );
                    Handles.Label(center, zone.zoneId, labelStyle);
                }
            }

            // Highlight current zone in play mode
            if (Application.isPlaying)
            {
                BasePlayerCharacterEntity player = GameInstance.PlayingCharacterEntity;
                if (player != null)
                {
                    Vector2Int coord = config.WorldToZoneCoord(player.EntityTransform.position);
                    Bounds zoneBounds = config.GetZoneBounds(coord.x, coord.y);
                    Gizmos.color = currentZoneColor * 0.3f;
                    Gizmos.DrawCube(zoneBounds.center + Vector3.up * 0.5f, new Vector3(zoneBounds.size.x, 1f, zoneBounds.size.z));
                }
            }
        }
#endif
    }
}
