using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// Makes a harmless animal bolt when it is hurt.
    ///
    /// The kit's monsters have two settings — fight, or ignore you. A
    /// <see cref="MonsterCharacteristic.NoHarm"/> creature is the ignoring kind, which is
    /// right for game animals in that they never attack, and wrong in that a deer will
    /// otherwise carry on grazing while it is being shot. This is the missing third
    /// option, and it is what makes the deer worth hunting rather than a slow target.
    ///
    /// It runs on the server only; the flight is movement, and the kit syncs movement to
    /// the clients on its own.
    ///
    /// While it is running the monster's own activity component is switched off. That
    /// component decides each tick where a monster ought to be going, and it has no notion
    /// of being frightened, so leaving it on means it and this argue over the destination
    /// every frame and the animal jitters on the spot instead of leaving.
    ///
    /// Nothing ticks while the animal is at peace: the entity's update
    /// (<see cref="BaseGameEntity.onUpdate"/>) is listened to only for as long as it runs.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(BaseMonsterCharacterEntity))]
    public class FleeWhenHurt : MonoBehaviour
    {
        [Tooltip("How far it runs from whatever hurt it, in metres.")]
        public float distance = 22f;

        [Tooltip("How long it keeps running before settling, in seconds.")]
        public float duration = 6f;

        [Tooltip("Run speed while fleeing, in metres a second. A deer outruns a person.")]
        public float fleeSpeed = 7f;

        private BaseMonsterCharacterEntity _entity;
        private BaseMonsterActivityComponent _activity;
        private float _until;
        private bool _fleeing;

        private void Awake()
        {
            _entity = GetComponent<BaseMonsterCharacterEntity>();
            _activity = GetComponent<BaseMonsterActivityComponent>();
        }

        private void OnEnable()
        {
            _entity.onReceivedDamage += Hurt;
        }

        private void OnDisable()
        {
            _entity.onReceivedDamage -= Hurt;
            Settle();
        }

        private void Hurt(DamageableEntity target, HitBoxPosition position, Vector3 fromPosition,
                          EntityInfo instigator, CombatAmountType combatAmountType, int totalDamage,
                          CharacterItem weapon, BaseSkill skill, int skillLevel,
                          CharacterBuff buff, bool isDamageOverTime)
        {
            if (!_entity.IsServer || _entity.IsDead())
                return;
            Bolt(fromPosition);
        }

        /// <summary>
        /// Heads directly away from the threat. Where that would run it into something —
        /// a cliff, the sea, a wall — the straight line is swung round until a way out is
        /// found, so a cornered animal breaks sideways rather than pressing into the
        /// scenery. The navmesh sampling matters: `PointClickMovement` on a destination
        /// off the mesh is dropped, and the deer would stand still having been shot.
        /// </summary>
        private void Bolt(Vector3 threat)
        {
            Vector3 here = _entity.EntityTransform.position;
            Vector3 away = here - threat;
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f)
                away = _entity.EntityTransform.forward;
            away.Normalize();

            for (int turn = 0; turn < 8; ++turn)
            {
                // 0, then +-45, +-90, +-135 degrees off straight away from the threat.
                float degrees = ((turn + 1) / 2) * 45f * (turn % 2 == 0 ? 1f : -1f);
                Vector3 tryDir = Quaternion.Euler(0f, degrees, 0f) * away;
                Vector3 target = here + tryDir * distance;
                if (UnityEngine.AI.NavMesh.SamplePosition(target, out UnityEngine.AI.NavMeshHit hit, 6f, UnityEngine.AI.NavMesh.AllAreas))
                {
                    Run(hit.position);
                    return;
                }
            }
        }

        private void Run(Vector3 to)
        {
            if (!_fleeing)
            {
                _fleeing = true;
                _entity.onUpdate += Tick;
                if (_activity != null)
                    _activity.enabled = false;
            }
            _entity.OverrideMoveSpeed = fleeSpeed;
            // Cleared while running, so the model plays its gallop rather than the wander
            // walk — the kit picks the gait off this flag.
            _entity.SetExtraMovementState(ExtraMovementState.None);
            _entity.PointClickMovement(to);
            _until = Time.time + duration;
        }

        private void Tick(BaseGameEntity entity)
        {
            if (!_fleeing || !_entity.IsServer)
                return;
            if (Time.time >= _until || _entity.IsDead())
                Settle();
        }

        private void Settle()
        {
            if (!_fleeing)
                return;
            _fleeing = false;
            _entity.onUpdate -= Tick;
            // -1, not 0: the kit applies any override >= 0, so 0 would root the animal.
            _entity.OverrideMoveSpeed = -1f;
            if (_entity != null && !_entity.IsDead())
                _entity.StopMove();
            if (_activity != null)
                _activity.enabled = true;
        }
    }
}
