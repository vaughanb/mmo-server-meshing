using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// Walks an NPC round a loop of points, standing a while at each.
    ///
    /// The kit's NPCs stand still; its walking things are monsters, with a whole AI
    /// behind them. A guard on his rounds needs none of that: the server hands the
    /// entity's navmesh mover one point at a time and waits for it to get there, and
    /// the kit does the rest - the mover paths round whatever is in the way, turns him to
    /// face where he is going, syncs him to the clients, and the base entity plays the
    /// walk from the movement state. That last part needs the entity's `model` field to
    /// point at the child model object: `BaseGameEntity` only animates `if (Model != null)`
    /// and fills that field with `GetComponent`, which never reaches a child. With it unset
    /// the guard slides along his route without moving his legs (see `DemoEntityBuilder`). An NPC has no move speed of its own, so his comes
    /// from the entity's override, which the kit syncs too.
    ///
    /// He holds still while a player is close enough to talk, so that a conversation is
    /// not walked out of, and carries on when they step away.
    ///
    /// The one thing the kit does not give an NPC is permission: a base entity answers
    /// no to "can it move" and "can it turn", and the navmesh mover drops every
    /// destination on that answer. It asks through a validation hook, though, which is
    /// where this says yes.
    ///
    /// Ticks on the entity's own update (<see cref="BaseGameEntity.onUpdate"/>); a pure client
    /// lets go of it once the footsteps are back on.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NpcEntity))]
    public class NpcPatrol : MonoBehaviour
    {
        [Tooltip("World positions, walked in order and then round again.")]
        public Vector3[] waypoints = new Vector3[0];

        /// <summary>
        /// Walking pace, in metres a second.
        ///
        /// **This is the walk clip's own ground speed, not a taste.** An NPC gets no
        /// animation speed scaling - `BaseGameEntity.MoveAnimationSpeedMultiplier` is a
        /// flat 1, and only `BaseCharacterEntity` overrides it with
        /// `moveSpeed / baseMoveSpeed` - so a walk clip authored to cover 0.96 m/s plays
        /// at exactly that pace whatever the mover is doing underneath. Any other number
        /// here is a guard skating.
        /// </summary>
        [Tooltip("Walking pace, in metres a second. Matched to the walk clip's own ground speed; see the code.")]
        public float walkSpeed = 0.96f;

        [Tooltip("How long to stand at each point, in seconds.")]
        public float pause = 5f;

        [Tooltip("How near a point counts as reaching it, in metres.")]
        public float arriveDistance = 0.7f;

        [Tooltip("A player within this many metres holds the patrol until they leave.")]
        public float holdForPlayersWithin = 3.5f;

        [Tooltip("Give up on a point after this many seconds and go on to the next.")]
        public float legTimeout = 30f;

        private NpcEntity _entity;
        private CharacterFootstepSoundComponent _footsteps;
        private bool _footstepsChecked;
        private int _next;
        private float _waitUntil;
        private float _legStarted;
        private bool _walking;
        private bool _held;
        private float _nextPlayerCheck;
        private bool _ticking;
        private static readonly Collider[] s_near = new Collider[64];

        private void Awake()
        {
            _entity = GetComponent<NpcEntity>();
            _footsteps = GetComponent<CharacterFootstepSoundComponent>();
        }

        /// <summary>
        /// Switches the footstep sounds back on.
        ///
        /// `CharacterFootstepSoundComponent.Start` disables itself when the entity is not yet
        /// a client - and for anything standing in the scene it never is at that moment,
        /// because Start runs on scene load and the network identity only spawns the entity
        /// afterwards. The flag is never revisited, so the component stays off for good and
        /// the walker slides along in silence. Nothing placed in the map has ever had
        /// footsteps because of it; a monster out of a spawn area is created after
        /// networking is up and so escapes it entirely, which is what makes the two behave
        /// differently for no visible reason.
        ///
        /// Only the walkers need this, and they are exactly the things carrying this
        /// component, so the repair lives here rather than on every silent villager.
        /// Footsteps are a client's business, so this runs before the server-only work below.
        /// </summary>
        private void EnableFootstepsOnceSpawned()
        {
            if (_footstepsChecked || _footsteps == null)
                return;
            if (!_entity.IsClient)
                return;
            _footstepsChecked = true;
            if (!_footsteps.enabled)
                _footsteps.enabled = true;
        }

        // An NPC answers no when the kit asks whether it may move or turn - its movers
        // refuse every destination on that answer - and the kit asks through these
        // hooks so that something else can answer for it. This guard may.
        private void OnEnable()
        {
            _entity.onCanMoveValidated += Allow;
            _entity.onCanTurnValidated += Allow;
            _entity.onCanWalkValidated += Allow;
            _entity.onUpdate += Tick;
            _ticking = true;
        }

        private void OnDisable()
        {
            _entity.onCanMoveValidated -= Allow;
            _entity.onCanTurnValidated -= Allow;
            _entity.onCanWalkValidated -= Allow;
            StopTicking();
        }

        private void StopTicking()
        {
            if (!_ticking)
                return;
            _ticking = false;
            _entity.onUpdate -= Tick;
        }

        private static void Allow(BaseGameEntity entity, ref bool allowed)
        {
            allowed = true;
        }

        private void Tick(BaseGameEntity entity)
        {
            EnableFootstepsOnceSpawned();

            if (!_entity.IsServer)
            {
                // A pure client only ever had the footsteps to see to.
                if (_entity.IsClient && (_footstepsChecked || _footsteps == null))
                    StopTicking();
                return;
            }
            if (waypoints == null || waypoints.Length == 0)
                return;
            if (_entity.OverrideMoveSpeed != walkSpeed)
                _entity.OverrideMoveSpeed = walkSpeed;

            // He walks, rather than jogging along at a stroll's pace.
            //
            // The gait is chosen by `ExtraMovementState`, not by how fast the mover is
            // actually going: with it left at `None` the model plays `moveStates`, which is
            // the jog, and the jog clip covers 5.3 m/s against the 1.4 he was moving at -
            // legs whirring, body creeping. `IsWalking` selects `walkStates` instead.
            //
            // Set every tick rather than once, because it cannot be set before the entity
            // is spawned: `NavMeshEntityMovement.SetExtraMovementState` only takes while
            // `CanPredictMovement()`, which needs the server to own the entity, and that is
            // not true yet in Awake or OnEnable. It is a field assignment behind two bools.
            //
            // Two permissions, not one. `ValidateExtraMovementState` drops `IsWalking` back
            // to `None` unless `Entity.CanWalk()` - and a base entity answers no to that for
            // the same reason it answers no to "can it move", which is why `onCanWalkValidated`
            // is hooked above. Without it the state is set, accepted, and silently discarded
            // a frame later, and he jogs exactly as before.
            _entity.SetExtraMovementState(ExtraMovementState.IsWalking);

            if (Time.time >= _nextPlayerCheck)
            {
                _nextPlayerCheck = Time.time + 0.5f;
                bool near = PlayerNearby();
                if (near && _walking)
                {
                    _entity.StopMove();
                    _walking = false;
                }
                if (near)
                    _waitUntil = Time.time + pause;
                _held = near;
            }
            if (_held)
                return;

            if (_walking)
            {
                Vector3 here = _entity.EntityTransform.position;
                Vector3 there = waypoints[_next];
                here.y = 0f;
                there.y = 0f;
                if (Vector3.Distance(here, there) <= arriveDistance || Time.time - _legStarted > legTimeout)
                {
                    _entity.StopMove();
                    _walking = false;
                    _next = (_next + 1) % waypoints.Length;
                    _waitUntil = Time.time + pause;
                }
                return;
            }

            if (Time.time >= _waitUntil)
            {
                _entity.PointClickMovement(waypoints[_next]);
                _legStarted = Time.time;
                _walking = true;
            }
        }

        private bool PlayerNearby()
        {
            int count = Physics.OverlapSphereNonAlloc(_entity.EntityTransform.position, holdForPlayersWithin, s_near);
            for (int i = 0; i < count; ++i)
            {
                if (s_near[i].transform.root.GetComponent<BasePlayerCharacterEntity>() != null)
                    return true;
            }
            return false;
        }
    }
}
