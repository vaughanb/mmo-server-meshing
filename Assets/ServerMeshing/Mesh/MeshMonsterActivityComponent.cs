// Server Meshing add-on — MeshMonsterActivityComponent (DESIGN.md §9, P3 aggro across an edge).
//
// The kit skips a monster AI update while the monster has no network subscribers. On a meshed cell the players
// standing just across the edge are represented here by ghosts, which never subscribe, so edge monsters would sleep
// exactly when a neighbour-cell player is looking at (or hitting) them. This subclass also counts a nearby player
// ghost as a watcher (core seam CE-7). Swap it in for MonsterActivityComponent on monster prefabs.
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public class MeshMonsterActivityComponent : MonsterActivityComponent, IMeshMonsterAiCarrier
    {
        [Tooltip("Extra range beyond the monster visual range in which a player ghost keeps this monster awake.")]
        public float ghostWatchMargin = 10f;
        [Tooltip("How often the ghost proximity is re-evaluated (seconds).")]
        public float ghostWatchInterval = 0.5f;

        private float _nextGhostCheck;
        private bool _ghostWatched;

        protected override bool IsWatched()
        {
            if (base.IsWatched())
                return true;
            if (Time.unscaledTime >= _nextGhostCheck)
            {
                _nextGhostCheck = Time.unscaledTime + ghostWatchInterval;
                MeshMapNetworkManager manager = BaseGameNetworkManager.Singleton as MeshMapNetworkManager;
                float range = (CharacterDatabase != null ? CharacterDatabase.VisualRange : 0f) + ghostWatchMargin;
                _ghostWatched = manager != null && manager.IsPlayerGhostNear(Entity.EntityTransform.position, range);
            }
            return _ghostWatched;
        }

        // DESIGN 1.28: the wander and leash bookkeeping crosses a monster handoff (MeshMonsterMotionCarry).
        public void WriteAiState(LiteNetLib.Utils.NetDataWriter writer)
        {
            new MeshMonsterAiState
            {
                reachedSpawnPoint = _reachedSpawnPoint,
                wanderCountDown = _randomedWanderCountDown,
                wanderDelay = _randomedWanderDelay,
                followEnemyElapsed = _followEnemyElasped,
            }.Write(writer);
        }

        public void ReadAiState(LiteNetLib.Utils.NetDataReader reader)
        {
            MeshMonsterAiState state = MeshMonsterAiState.Read(reader);
            _reachedSpawnPoint = state.reachedSpawnPoint;
            _randomedWanderCountDown = state.wanderCountDown;
            _randomedWanderDelay = state.wanderDelay;
            _followEnemyElasped = state.followEnemyElapsed;
        }
    }
}
