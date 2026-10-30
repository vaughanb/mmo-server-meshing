// Server Meshing test rig — the kit demo's LineOfSightMonsterActivityComponent with MeshMonsterActivityComponent's rule:
// a player ghost near the monster (a player standing across the cell edge) keeps its AI awake, as a subscribed player
// would. The demo's monsters keep their line-of-sight targeting on the 8 km map (ServerMeshLargeMapBuilder puts this on
// its meshed copies of them).
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public class MeshLineOfSightMonsterActivityComponent : LineOfSightMonsterActivityComponent, IMeshMonsterAiCarrier
    {
        [Tooltip("Extra range beyond the monster visual range in which a player ghost keeps this monster awake.")]
        public float ghostWatchMargin = 10f;
        [Tooltip("How often the ghost proximity is re-evaluated (seconds).")]
        public float ghostWatchInterval = 0.5f;

        private float _nextGhostCheck;
        private bool _ghostWatched;
        private float _outOfReachSince = -1f;
        private bool _chaseSpeedSet;
        private bool _homePending;

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

        /// <summary>
        /// DESIGN 1.28: the demo rule's chase (MeshCombatGameplayRule) runs before the kit's AI each update: the chase speed,
        /// and a chase given up by distance rather than the kit's follow timer.
        /// </summary>
        public override void ManagedUpdate()
        {
            if (Entity.IsServer && !MeshIdentity.IsGhostEntity(Entity))
                UpdateChase();
            base.ManagedUpdate();
        }

        private void UpdateChase()
        {
            MeshCombatGameplayRule rule = GameInstance.Singleton != null ? GameInstance.Singleton.GameplayRule as MeshCombatGameplayRule : null;
            BaseCharacterEntity target = null;
            bool chasing = rule != null && !Entity.IsDead() && Entity.TryGetTargetEntity(out target) && target != null && !target.IsDead();
            if (!chasing)
            {
                _outOfReachSince = -1f;
                ClearChaseSpeed();
                if (_homePending)
                    TryWalkHome();
                return;
            }
            _homePending = false;
            Vector3 toTarget = target.EntityTransform.position - Entity.EntityTransform.position;
            toTarget.y = 0f;
            float distance = toTarget.magnitude;
            if (rule.chaseLoseRange > 0f && rule.chaseLoseSeconds > 0f)
            {
                _followEnemyElasped = 0f; // the kit's timer ends no chase: distance does
                if (distance <= rule.chaseLoseRange)
                {
                    _outOfReachSince = -1f;
                }
                else
                {
                    float now = Time.unscaledTime;
                    if (_outOfReachSince < 0f)
                        _outOfReachSince = now;
                    if (now - _outOfReachSince >= rule.chaseLoseSeconds)
                    {
                        GiveUpChase(target, distance);
                        return;
                    }
                }
            }
            if (rule.chaseSpeedOfTargetRun > 0f && distance > GetAttackDistance() + 0.5f)
            {
                MovementState running = MovementState.IsGrounded | MovementState.Forward;
                float own = Entity.GetMoveSpeed_Implementation(running, ExtraMovementState.None);
                float ownSprint = Entity.GetMoveSpeed_Implementation(running, ExtraMovementState.IsSprinting);
                float targetRun = target.GetMoveSpeed_Implementation(running, ExtraMovementState.None);
                SetChaseSpeed(Mathf.Clamp(targetRun * rule.chaseSpeedOfTargetRun, own, Mathf.Max(own, ownSprint)));
            }
            else
            {
                ClearChaseSpeed(); // in reach: the kit's own speed while it attacks
            }
        }

        /// <summary>The target got away: as the kit's follow timer ends a chase, the monster walks back home.</summary>
        private void GiveUpChase(BaseCharacterEntity target, float distance)
        {
            _outOfReachSince = -1f;
            ClearChaseSpeed();
            Entity.SetTargetEntity(null);
            ClearActionState();
            _homePending = true;
            TryWalkHome();
            Logging.Log("Mesh", $"Chase given up: {Entity.name} lost {target.CharacterName} at {distance:0.0} m{(_homePending ? "; walking home once it can move" : "; walking home")}");
        }

        /// <summary>
        /// Heads back to the spawn point as the kit does when its follow timer ends a chase. The kit refuses a destination
        /// within 0.1 s of the last one (the chase set one this frame) and while the monster cannot move, so a refusal is
        /// retried every update until one is taken; only then is the spawn point unreached (the kit's walk home).
        /// </summary>
        private void TryWalkHome()
        {
            _lastSetDestinationTime = 0f;
            if (!RandomWanderDestination())
                return;
            _homePending = false;
            _reachedSpawnPoint = false;
            _randomedWanderCountDown = _randomedWanderDelay;
        }

        private void SetChaseSpeed(float speed)
        {
            if (!_chaseSpeedSet || Mathf.Abs(Entity.OverrideMoveSpeed - speed) > 0.05f)
                Entity.OverrideMoveSpeed = speed;
            _chaseSpeedSet = true;
        }

        private void ClearChaseSpeed()
        {
            if (!_chaseSpeedSet)
                return;
            _chaseSpeedSet = false;
            Entity.OverrideMoveSpeed = -1f;
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
