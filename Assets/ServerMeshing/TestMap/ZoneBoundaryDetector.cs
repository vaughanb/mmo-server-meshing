using System;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// Monitors the local player's position and reports zone boundary crossings.
    /// Optionally (clientDrivenMigration) triggers a client-driven masked migration to the zone's
    /// fixed server address — a debug path only. The production trigger is the SERVER-side boundary
    /// tick in <see cref="MeshMapNetworkManager"/>, which resolves the destination through the cluster.
    /// </summary>
    public class ZoneBoundaryDetector : MonoBehaviour
    {
        [Header("Configuration")]
        [Tooltip("Reference to the grid configuration asset.")]
        public ServerMeshTestConfig config;

        [Tooltip("How often to check boundary crossing (seconds).")]
        public float checkInterval = 0.1f;

        [Tooltip("Hysteresis deadband (metres): a crossing only commits once the player is clear of the boundary line by this much. Prevents thrashing while straddling a boundary.")]
        public float boundaryThreshold = 2f;

        [Tooltip("Minimum seconds between migrations (anti-thrash cooldown).")]
        public float migrationCooldown = 3f;

        [Tooltip("DEBUG ONLY: let the client itself trigger a migration to the zone's fixed serverAddress:serverPort. Leave off when the server-side handoff is enabled, or both will fire.")]
        public bool clientDrivenMigration = false;

        /// <summary>
        /// Fired when a zone crossing is detected with (fromZone, toZone) coordinates.
        /// </summary>
        public event Action<Vector2Int, Vector2Int> OnZoneCrossing;

        /// <summary>Zone coordinate the local player is currently in (as last observed).</summary>
        public Vector2Int CurrentZoneCoord => _currentZoneCoord;

        private Vector2Int _currentZoneCoord;
        private float _nextCheckTime;
        private bool _initialized;
        private float _lastMigrationTime = -999f;

        private void Start()
        {
            if (config == null)
            {
                Debug.LogError($"[ZoneBoundaryDetector] No ServerMeshTestConfig assigned.", this);
                enabled = false;
                return;
            }

            // Attempt initial zone coord from player position
            TryInitialize();
        }

        private void Update()
        {
            if (Time.time < _nextCheckTime)
                return;

            _nextCheckTime = Time.time + checkInterval;

            BasePlayerCharacterEntity player = GameInstance.PlayingCharacterEntity;
            if (player == null)
            {
                // Player despawns during a migration; re-initialise from the re-spawned entity.
                _initialized = false;
                return;
            }

            if (!_initialized)
            {
                TryInitialize();
                return;
            }

            Vector3 playerPos = player.EntityTransform.position;
            Vector2Int newZoneCoord = config.WorldToZoneCoord(playerPos);

            if (newZoneCoord == _currentZoneCoord)
                return;

            // Hysteresis: don't commit while the player is still within the boundary deadband
            // (straddling the line) — wait until they are clearly inside the new zone.
            if (config.IsNearBoundary(playerPos, boundaryThreshold))
                return;

            // Cooldown: avoid rapid re-migration right after a crossing.
            if (Time.time - _lastMigrationTime < migrationCooldown)
                return;

            _lastMigrationTime = Time.time;
            HandleZoneCrossing(_currentZoneCoord, newZoneCoord);
        }

        /// <summary>
        /// Handles zone crossing by invoking the event and (optionally) triggering masked migration.
        /// </summary>
        private void HandleZoneCrossing(Vector2Int fromCoord, Vector2Int toCoord)
        {
            Debug.Log($"[ZoneBoundaryDetector] Zone crossing detected: ({fromCoord.x},{fromCoord.y}) -> ({toCoord.x},{toCoord.y})");

            Vector2Int previousCoord = _currentZoneCoord;
            _currentZoneCoord = toCoord;

            OnZoneCrossing?.Invoke(previousCoord, toCoord);

            if (!clientDrivenMigration)
                return;

            // Look up destination zone's server info
            ServerMeshTestConfig.ZoneDefinition destZone = config.GetZone(toCoord);
            if (destZone == null)
            {
                Debug.LogWarning($"[ZoneBoundaryDetector] No zone definition found for coord ({toCoord.x},{toCoord.y})");
                return;
            }

            // Trigger migration through MeshMapNetworkManager
            MeshMapNetworkManager meshManager = FindAnyObjectByType<MeshMapNetworkManager>();
            if (meshManager != null)
            {
                Debug.Log($"[ZoneBoundaryDetector] Initiating migration to {destZone.serverAddress}:{destZone.serverPort} (zone: {destZone.zoneId})");
                meshManager.DebugMigrate(destZone.serverAddress, destZone.serverPort);
            }
            else
            {
                Debug.LogWarning("[ZoneBoundaryDetector] MeshMapNetworkManager not found. Cannot trigger migration.");
            }
        }

        /// <summary>
        /// Attempts to initialize the current zone from the player's position.
        /// </summary>
        private void TryInitialize()
        {
            BasePlayerCharacterEntity player = GameInstance.PlayingCharacterEntity;
            if (player == null)
                return;

            _currentZoneCoord = config.WorldToZoneCoord(player.EntityTransform.position);
            _initialized = true;
            Debug.Log($"[ZoneBoundaryDetector] Initialized in zone ({_currentZoneCoord.x},{_currentZoneCoord.y})");
        }
    }
}
