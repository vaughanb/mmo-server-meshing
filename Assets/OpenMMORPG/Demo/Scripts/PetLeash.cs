using UnityEngine;
using UnityEngine.AI;

namespace MultiplayerARPG
{
    /// <summary>
    /// Keeps a pet at its owner's heel: calls it off a fight the owner has walked away
    /// from, and brings it back at a run the kit's own follow cannot manage.
    ///
    /// **The leash.** Once a summoned monster has a target, the kit's activity component
    /// chases it and never looks back: the `followTargetDuration` give-up only applies to
    /// monsters without a summoner. This drops the target as soon as it is further than
    /// <see cref="leashDistance"/> from the owner. It measures owner to target, not owner
    /// to pet, so walking off leaves the fight behind however far the pet has chased; and
    /// a pet will not open a fight the owner starts from beyond the leash - it joins in
    /// when the thing reaches its owner and hits them.
    ///
    /// **The follow.** Left to the kit, a pet trails a moving owner until the teleport at
    /// `GameInstance.maxFollowSummonerDistance` pulls it in. Its follow re-aims at a fresh
    /// random point round the owner every 0.1 s, so it starts and stops rather than runs
    /// (measured in the harness: 0 to 3 m/s behind an owner at 4-5), and it has only the
    /// pet's own speed to catch up with. So when the pet has no target and falls more than
    /// <see cref="followDistance"/> behind, this switches the activity component off -
    /// the two would otherwise argue over the destination every frame, as in
    /// <see cref="FleeWhenHurt"/> - and runs the pet to a fixed spot at the owner's back,
    /// faster than the owner is moving. Control goes back to the kit once the pet is
    /// within <see cref="arriveDistance"/>, or the moment it has something to fight: the
    /// kit's enemy-spotted notifications set the target whether or not the component is
    /// enabled.
    ///
    /// Server only, like the activity component it corrects. Ticks on the entity's own update
    /// (<see cref="BaseGameEntity.onUpdate"/>), and lets go of it on a pure client.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(BaseMonsterCharacterEntity))]
    public class PetLeash : MonoBehaviour
    {
        [Tooltip("How far from its owner a target may be before the pet gives it up, in metres. " +
                 "Keep it under the game instance's Max Follow Summoner Distance (10), or the kit's teleport acts first.")]
        public float leashDistance = 8f;

        [Tooltip("How far behind its owner an idle pet may fall before it is run back, in metres.")]
        public float followDistance = 4f;

        [Tooltip("How close it runs before the kit's wander takes over again, in metres.")]
        public float arriveDistance = 2f;

        [Tooltip("How far behind the owner it aims, in metres.")]
        public float heelOffset = 1.5f;

        [Tooltip("Run speed while catching up, as a multiple of the owner's current speed.")]
        public float catchUpFactor = 1.3f;

        [Tooltip("Never slower than this while catching up, in metres a second.")]
        public float minCatchUpSpeed = 6f;

        private const float RepathInterval = 0.2f;

        private BaseMonsterCharacterEntity _entity;
        private BaseMonsterActivityComponent _activity;
        private bool _following;
        private float _nextRepath;
        private bool _subscribed;

        private void Awake()
        {
            _entity = GetComponent<BaseMonsterCharacterEntity>();
            _activity = GetComponent<BaseMonsterActivityComponent>();
        }

        private void OnEnable()
        {
            if (_subscribed)
                return;
            _entity.onUpdate += Tick;
            _subscribed = true;
        }

        private void OnDisable()
        {
            Unsubscribe();
            StopFollowing();
        }

        private void Unsubscribe()
        {
            if (!_subscribed)
                return;
            _subscribed = false;
            _entity.onUpdate -= Tick;
        }

        private void Tick(BaseGameEntity entity)
        {
            if (!_entity.IsServer)
            {
                if (_entity.IsClient)
                    Unsubscribe();
                return;
            }
            BaseCharacterEntity owner = _entity.SummonerEntity;
            if (_entity.IsDead() || owner == null || owner.IsDead())
            {
                StopFollowing();
                return;
            }

            Vector3 ownerPosition = owner.EntityTransform.position;
            if (_entity.TryGetTargetEntity(out IDamageableEntity target) && target.GetTransform() != null)
            {
                if (Vector3.Distance(ownerPosition, target.GetTransform().position) <= leashDistance)
                {
                    // Something to fight, near enough: the kit's combat has it.
                    StopFollowing();
                    return;
                }
                _entity.SetTargetEntity(null);
            }

            float gap = Vector3.Distance(_entity.EntityTransform.position, ownerPosition);
            if (!_following)
            {
                if (gap <= followDistance)
                    return;
                _following = true;
                if (_activity != null)
                    _activity.enabled = false;
                _nextRepath = 0f;
            }
            else if (gap <= arriveDistance)
            {
                StopFollowing();
                return;
            }

            if (Time.unscaledTime < _nextRepath)
                return;
            _nextRepath = Time.unscaledTime + RepathInterval;

            _entity.OverrideMoveSpeed = Mathf.Max(minCatchUpSpeed, owner.GetMoveSpeed() * catchUpFactor);
            // None is the run gait; the activity component's wander sets IsWalking.
            _entity.SetExtraMovementState(ExtraMovementState.None);
            _entity.PointClickMovement(Heel(owner));
        }

        /// <summary>
        /// A spot at the owner's back, pulled onto the navmesh: a destination off the mesh
        /// is dropped by the movement, and the pet would stop dead. Falls back on the
        /// owner's own position, which is on the mesh if the owner could walk there.
        /// </summary>
        private Vector3 Heel(BaseCharacterEntity owner)
        {
            Vector3 ownerPosition = owner.EntityTransform.position;
            Vector3 back = -owner.EntityTransform.forward;
            back.y = 0f;
            Vector3 heel = ownerPosition + back.normalized * heelOffset;
            if (NavMesh.SamplePosition(heel, out NavMeshHit hit, 2f, NavMesh.AllAreas))
                return hit.position;
            return ownerPosition;
        }

        private void StopFollowing()
        {
            if (!_following)
                return;
            _following = false;
            if (_entity != null)
                _entity.OverrideMoveSpeed = -1f;
            if (_activity != null)
                _activity.enabled = true;
        }
    }
}
