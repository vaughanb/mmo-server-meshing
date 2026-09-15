using System;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// Client-side observer: watches the local player's position against the world registry and reports cell
    /// crossings (an event for debug UI and tests). The production trigger is the SERVER-side boundary tick in
    /// <see cref="MeshMapNetworkManager"/>, which resolves the destination through the cluster.
    /// </summary>
    public class ZoneBoundaryDetector : MonoBehaviour
    {
        [Header("Configuration")]
        [Tooltip("World registry (every meshed map's cells).")]
        public MeshWorldRegistry world;

        [Tooltip("How often to check for a crossing (seconds).")]
        public float checkInterval = 0.1f;

        [Tooltip("Hysteresis deadband (metres): a crossing only counts once the player is clear of the line by this much.")]
        public float boundaryThreshold = 2f;

        /// <summary>Fired when the local player's cell changes: (from, to).</summary>
        public event Action<MeshCell, MeshCell> OnZoneCrossing;

        /// <summary>The cell the local player was last seen in.</summary>
        public MeshCell CurrentCell => _currentCell;

        private MeshCell _currentCell;
        private float _nextCheckTime;

        private void Start()
        {
            if (world == null)
            {
                Debug.LogError("[ZoneBoundaryDetector] No world registry assigned.", this);
                enabled = false;
            }
        }

        private void Update()
        {
            if (Time.time < _nextCheckTime)
                return;
            _nextCheckTime = Time.time + checkInterval;

            BasePlayerCharacterEntity player = GameInstance.PlayingCharacterEntity;
            if (player == null)
            {
                _currentCell = null; // despawns during a hop; re-initialised from the new entity
                return;
            }
            string mapId = BaseGameNetworkManager.CurrentMapInfo != null ? BaseGameNetworkManager.CurrentMapInfo.Id : null;
            Vector3 pos = player.EntityTransform.position;
            if (_currentCell == null || !string.Equals(_currentCell.mapId, mapId))
            {
                _currentCell = world.CellAt(mapId, pos);
                if (_currentCell != null)
                    Debug.Log($"[ZoneBoundaryDetector] Initialized in cell {_currentCell.id}");
                return;
            }
            MeshCell owner = world.OwnerOf(_currentCell, pos);
            if (owner == null || owner == _currentCell)
                return;
            // Hysteresis: don't commit while the player is still within the deadband of the line.
            if (-_currentCell.DistanceToEdge(pos) < boundaryThreshold)
                return;
            MeshCell previous = _currentCell;
            _currentCell = owner;
            Debug.Log($"[ZoneBoundaryDetector] Cell crossing: {previous.id} -> {owner.id}");
            OnZoneCrossing?.Invoke(previous, owner);
        }
    }
}
