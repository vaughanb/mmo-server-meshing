using System;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// Defines the 3x3 grid configuration for the server mesh test map.
    /// Each zone represents a region owned by a separate map server (channelId == zoneId).
    /// </summary>
    [CreateAssetMenu(fileName = "ServerMeshTestConfig", menuName = "Server Meshing/Test Map Config", order = 100)]
    public class ServerMeshTestConfig : ScriptableObject
    {
        [Header("Grid Dimensions")]
        [Tooltip("Number of zones along the X axis.")]
        public int gridWidth = 3;

        [Tooltip("Number of zones along the Z axis.")]
        public int gridHeight = 3;

        [Tooltip("Size of each zone in world units (square).")]
        public float zoneSize = 200f;

        [Tooltip("World-space position of the grid's bottom-left corner.")]
        public Vector3 gridOrigin = new Vector3(-300f, 0f, -300f);

        [Header("Zone Definitions")]
        [Tooltip("Array of zone definitions, one per cell in the grid.")]
        public ZoneDefinition[] zones;

        /// <summary>
        /// Converts a world position to a zone grid coordinate (clamped to grid bounds).
        /// </summary>
        public Vector2Int WorldToZoneCoord(Vector3 worldPos)
        {
            float relativeX = worldPos.x - gridOrigin.x;
            float relativeZ = worldPos.z - gridOrigin.z;

            int x = Mathf.FloorToInt(relativeX / zoneSize);
            int y = Mathf.FloorToInt(relativeZ / zoneSize);

            x = Mathf.Clamp(x, 0, gridWidth - 1);
            y = Mathf.Clamp(y, 0, gridHeight - 1);

            return new Vector2Int(x, y);
        }

        /// <summary>
        /// Returns world-space bounds for a zone at the given grid coordinate.
        /// </summary>
        public Bounds GetZoneBounds(int x, int y)
        {
            Vector3 min = gridOrigin + new Vector3(x * zoneSize, 0f, y * zoneSize);
            Vector3 max = min + new Vector3(zoneSize, 0f, zoneSize);
            Vector3 center = (min + max) * 0.5f;
            Vector3 size = new Vector3(zoneSize, 0f, zoneSize);
            return new Bounds(center, size);
        }

        /// <summary>
        /// Returns the zone definition for the zone containing the given world position.
        /// Returns null if no matching zone is found.
        /// </summary>
        public ZoneDefinition GetZoneAt(Vector3 worldPos)
        {
            return GetZone(WorldToZoneCoord(worldPos));
        }

        /// <summary>Returns the zone definition at a grid coordinate, or null.</summary>
        public ZoneDefinition GetZone(Vector2Int coord)
        {
            if (zones == null) return null;
            for (int i = 0; i < zones.Length; i++)
            {
                if (zones[i].gridX == coord.x && zones[i].gridY == coord.y)
                    return zones[i];
            }
            return null;
        }

        /// <summary>Returns the zone definition with the given zoneId (case-insensitive), or null.</summary>
        public ZoneDefinition GetZoneById(string zoneId)
        {
            if (zones == null || string.IsNullOrEmpty(zoneId)) return null;
            for (int i = 0; i < zones.Length; i++)
            {
                if (string.Equals(zones[i].zoneId, zoneId, StringComparison.OrdinalIgnoreCase))
                    return zones[i];
            }
            return null;
        }

        /// <summary>
        /// Checks if a position is within threshold units of any zone boundary.
        /// </summary>
        public bool IsNearBoundary(Vector3 worldPos, float threshold)
        {
            // Check distance to vertical grid lines (X boundaries)
            for (int i = 1; i < gridWidth; i++)
            {
                float lineX = gridOrigin.x + i * zoneSize;
                if (Mathf.Abs(worldPos.x - lineX) < threshold)
                    return true;
            }

            // Check distance to horizontal grid lines (Z boundaries)
            for (int j = 1; j < gridHeight; j++)
            {
                float lineZ = gridOrigin.z + j * zoneSize;
                if (Mathf.Abs(worldPos.z - lineZ) < threshold)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Defines a single zone in the server mesh grid.
        /// </summary>
        [Serializable]
        public class ZoneDefinition
        {
            [Tooltip("Grid X coordinate (0-based, left to right).")]
            public int gridX;

            [Tooltip("Grid Y coordinate (0-based, bottom to top).")]
            public int gridY;

            [Tooltip("Human-readable zone identifier (e.g. NW, N, NE). Also used as the map server's channelId.")]
            public string zoneId;

            [Tooltip("Server address for the map server owning this zone (only used by the client-driven debug migration).")]
            public string serverAddress = "127.0.0.1";

            [Tooltip("Server port for the map server owning this zone (only used by the client-driven debug migration).")]
            public int serverPort = 7770;
        }
    }
}
