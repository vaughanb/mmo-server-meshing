using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// Reads zone assignment from command-line args (-zoneX, -zoneY, -zoneId) at startup
    /// and exposes the active zone info for other systems to query.
    /// Attach to a persistent GameObject in the ServerMeshTestMap scene.
    /// </summary>
    public class ServerMeshZoneBootstrap : MonoBehaviour
    {
        [Header("Configuration")]
        [Tooltip("Reference to the mesh test config asset.")]
        [SerializeField] private ServerMeshTestConfig config;

        /// <summary>The zone grid coordinate assigned to this server instance.</summary>
        public Vector2Int AssignedZoneCoord { get; private set; } = new Vector2Int(-1, -1);

        /// <summary>The zone ID string (e.g. "NW", "C", "SE").</summary>
        public string AssignedZoneId { get; private set; } = string.Empty;

        /// <summary>The resolved zone definition from config, or null if not yet assigned.</summary>
        public ServerMeshTestConfig.ZoneDefinition AssignedZone { get; private set; }

        /// <summary>Whether this instance has a valid zone assignment.</summary>
        public bool HasZoneAssignment => AssignedZoneCoord.x >= 0 && AssignedZoneCoord.y >= 0;

        /// <summary>Singleton accessor for convenience.</summary>
        public static ServerMeshZoneBootstrap Instance { get; private set; }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            ReadZoneArguments();
        }

        private void ReadZoneArguments()
        {
            string[] args = System.Environment.GetCommandLineArgs();

            // Read zone coordinates
            if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_ZONE_X, out int zoneX, -1) &&
                ConfigReader.ReadArgs(args, ProcessArguments.ARG_ZONE_Y, out int zoneY, -1))
            {
                AssignedZoneCoord = new Vector2Int(zoneX, zoneY);
                Debug.Log($"[ServerMesh] Zone assignment from args: ({zoneX}, {zoneY})");
            }

            // Read zone ID (optional, for logging/display)
            if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_ZONE_ID, out string zoneId, string.Empty))
            {
                AssignedZoneId = zoneId;
                Debug.Log($"[ServerMesh] Zone ID from args: {zoneId}");
            }

            // Resolve zone definition from config
            if (HasZoneAssignment && config != null && config.zones != null)
            {
                for (int i = 0; i < config.zones.Length; i++)
                {
                    if (config.zones[i].gridX == AssignedZoneCoord.x &&
                        config.zones[i].gridY == AssignedZoneCoord.y)
                    {
                        AssignedZone = config.zones[i];
                        Debug.Log($"[ServerMesh] Resolved zone: {AssignedZone.zoneId} " +
                                  $"at ({AssignedZone.gridX}, {AssignedZone.gridY}), " +
                                  $"port {AssignedZone.serverPort}");
                        return;
                    }
                }

                Debug.LogWarning($"[ServerMesh] No zone definition found for coord ({AssignedZoneCoord.x}, {AssignedZoneCoord.y})");
            }
            else if (!HasZoneAssignment)
            {
                Debug.Log("[ServerMesh] No zone assignment args provided. Running as single-server or editor mode.");
            }
        }
    }
}
