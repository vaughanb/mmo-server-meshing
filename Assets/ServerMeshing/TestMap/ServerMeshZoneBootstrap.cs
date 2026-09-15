using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// Reads the zone assignment from the command line (-zoneX, -zoneY, -zoneId) at start-up and exposes it. The map
    /// server resolves the cell itself against the world registry (MeshMapNetworkManager.TryResolveOwnZone); with no
    /// arguments the server's channel id names its cell (the map-spawner convention).
    /// Attach to a persistent GameObject in the map scene.
    /// </summary>
    public class ServerMeshZoneBootstrap : MonoBehaviour
    {
        /// <summary>The grid coordinate given on the command line, or (-1,-1).</summary>
        public Vector2Int AssignedZoneCoord { get; private set; } = new Vector2Int(-1, -1);

        /// <summary>The cell id given on the command line, or empty.</summary>
        public string AssignedZoneId { get; private set; } = string.Empty;

        /// <summary>Whether the command line named a cell (by id or by grid coordinate).</summary>
        public bool HasZoneAssignment => (AssignedZoneCoord.x >= 0 && AssignedZoneCoord.y >= 0) || !string.IsNullOrEmpty(AssignedZoneId);

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
            if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_ZONE_X, out int zoneX, -1) &&
                ConfigReader.ReadArgs(args, ProcessArguments.ARG_ZONE_Y, out int zoneY, -1))
            {
                AssignedZoneCoord = new Vector2Int(zoneX, zoneY);
                Debug.Log($"[ServerMesh] Zone assignment from args: ({zoneX}, {zoneY})");
            }
            if (ConfigReader.ReadArgs(args, ProcessArguments.ARG_ZONE_ID, out string zoneId, string.Empty) && !string.IsNullOrEmpty(zoneId))
            {
                AssignedZoneId = zoneId;
                Debug.Log($"[ServerMesh] Zone ID from args: {zoneId}");
            }
            if (!HasZoneAssignment)
                Debug.Log("[ServerMesh] No zone assignment args provided. Running as single-server or editor mode.");
        }
    }
}
