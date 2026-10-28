using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// Turns an NPC to face whoever is talking to them, and back to where they were looking
    /// once the talk is over - the way WoW's NPCs do when a gossip window opens.
    ///
    /// **Server-driven, so everyone sees it.** The kit tells nobody when a dialog opens: the
    /// server just notes the NPC on the player's <see cref="PlayerCharacterNpcActionComponent"/>
    /// (`CurrentNpcEntity`) and clears it when the window closes, the player dies, or the dialog
    /// moves on. So the server looks round the NPC a few times a second for a player who has
    /// this NPC as their current one, aims the NPC at them, and syncs two things: that it is
    /// facing someone, and the yaw it is facing. Every peer turns from those, which keeps them
    /// all agreeing even when a client's copy of the player is a step behind. A second player
    /// who starts talking takes the NPC's attention; the NPC adjusts if the speaker walks round
    /// them, and turns back <see cref="returnDelay"/> seconds after the last one is gone.
    ///
    /// **Two kinds of NPC.** One that stands in the scene has no movement component, so nothing
    /// syncs its rotation and nothing else writes it: every peer turns the root transform itself
    /// at <see cref="turnSpeed"/>. One with a navmesh mover (the patrol guard, the collie) owns
    /// its rotation and syncs it, so there only the server steers, through the entity's look
    /// rotation, and only while the mover is standing; the mover carries the result to the
    /// clients. Its home is wherever it was standing when the talk began, since a walker has no
    /// fixed one. A base NPC answers no when the mover asks whether it may turn, so this says
    /// yes through the kit's validation hook, as <see cref="NpcPatrol"/> does.
    ///
    /// Belongs on every NPC entity prefab; `DemoEntityBuilder.BuildNpc` and the wildlife builder
    /// put it there. A network behaviour, so the map server build must carry it too
    /// (**Build Map Server**).
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NpcEntity))]
    public class NpcFaceSpeaker : LiteNetLibBehaviour
    {
        [Tooltip("Degrees a second, for an NPC that stands in the scene. One with a navmesh mover turns at the mover's own pace.")]
        public float turnSpeed = 360f;

        [Tooltip("Seconds after the last player has stopped talking before the NPC turns back to where they were looking.")]
        public float returnDelay = 4f;

        [Tooltip("How much farther than the NPC's conversation reach the server still counts a player as talking, in metres: " +
                 "the player may have stepped back a little with the window open.")]
        public float reachTolerance = 1.5f;

        [Tooltip("Degrees the speaker has to walk round the NPC before it adjusts its facing.")]
        public float adjustAngle = 10f;

        /// <summary>True while someone is talking to this NPC (or has just stopped).</summary>
        [SerializeField]
        protected SyncFieldBool facing = new SyncFieldBool();

        /// <summary>World yaw the NPC is turned to while <see cref="facing"/>.</summary>
        [SerializeField]
        protected SyncFieldFloat yaw = new SyncFieldFloat();

        private const float ScanInterval = 0.2f;
        private static readonly Collider[] s_near = new Collider[64];

        private NpcEntity _entity;
        private bool _hasMover;
        private float _homeYaw;
        private bool _homeKnown;
        private BasePlayerCharacterEntity _speaker;
        private float _lastHeard;
        private float _nextScan;
        private bool _steered;
        private float _steeredYaw;

        private void Awake()
        {
            _entity = GetComponent<NpcEntity>();
        }

        public override void OnSetup()
        {
            base.OnSetup();
            facing.syncMode = LiteNetLibSyncFieldMode.ServerToClients;
            yaw.syncMode = LiteNetLibSyncFieldMode.ServerToClients;
            facing.onChange -= OnFacingChange;
            facing.onChange += OnFacingChange;
        }

        private void OnEnable()
        {
            if (_entity == null)
                return;
            _hasMover = !_entity.Movement.IsNull();
            if (_hasMover)
            {
                _entity.onCanMoveValidated += Allow;
                _entity.onCanTurnValidated += Allow;
            }
            _entity.onUpdate += Tick;
        }

        private void OnDisable()
        {
            if (_entity == null)
                return;
            if (_hasMover)
            {
                _entity.onCanMoveValidated -= Allow;
                _entity.onCanTurnValidated -= Allow;
            }
            _entity.onUpdate -= Tick;
        }

        private void OnDestroy()
        {
            facing.onChange -= OnFacingChange;
        }

        private static void Allow(BaseGameEntity entity, ref bool allowed)
        {
            allowed = true;
        }

        private void OnFacingChange(bool initial, bool wasFacing, bool isFacing)
        {
            // A walker's home is wherever it was standing when the talk began. A standing NPC's
            // was taken on its first tick, so a second talk during the turn back does not make a
            // half-turned pose the new home.
            if (_hasMover && isFacing && !wasFacing && !initial)
            {
                _homeYaw = CurrentYaw();
                _homeKnown = true;
            }
        }

        private void Tick(BaseGameEntity entity)
        {
            if (!_entity.Movement.IsNull() != _hasMover)
            {
                // The entity finds its mover in its own Awake, which can run after this OnEnable.
                OnDisable();
                OnEnable();
            }
            if (!_homeKnown)
            {
                _homeYaw = CurrentYaw();
                _homeKnown = true;
            }
            if (IsServer)
                Listen();
            Turn();
        }

        /// <summary>Server: who is talking to this NPC, and where are they.</summary>
        private void Listen()
        {
            if (Time.time < _nextScan)
                return;
            _nextScan = Time.time + ScanInterval;

            BasePlayerCharacterEntity speaker = FindSpeaker();
            if (speaker != null)
            {
                _lastHeard = Time.time;
                float toSpeaker = YawTo(speaker.EntityTransform.position);
                if (!facing.Value || speaker != _speaker || Mathf.Abs(Mathf.DeltaAngle(yaw.Value, toSpeaker)) > adjustAngle)
                {
                    _speaker = speaker;
                    yaw.Value = toSpeaker;
                    facing.Value = true;
                }
                return;
            }

            if (facing.Value && Time.time - _lastHeard > returnDelay)
            {
                _speaker = null;
                facing.Value = false;
            }
        }

        /// <summary>
        /// The player with this NPC as their current one, within reach. The one already being
        /// faced keeps the NPC's attention while they are still talking; otherwise the nearest.
        /// </summary>
        private BasePlayerCharacterEntity FindSpeaker()
        {
            Vector3 here = _entity.EntityTransform.position;
            float reach = _entity.GetActivatableDistance() + reachTolerance;
            int count = Physics.OverlapSphereNonAlloc(here, reach, s_near);
            BasePlayerCharacterEntity nearest = null;
            float nearestDistance = float.MaxValue;
            for (int i = 0; i < count; ++i)
            {
                BasePlayerCharacterEntity player = s_near[i].transform.root.GetComponent<BasePlayerCharacterEntity>();
                if (player == null || !IsTalkingToMe(player))
                    continue;
                if (player == _speaker)
                    return player;
                float distance = Vector3.Distance(here, player.EntityTransform.position);
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = player;
                }
            }
            return nearest;
        }

        private bool IsTalkingToMe(BasePlayerCharacterEntity player)
        {
            if (player.IsDead())
                return false;
            PlayerCharacterNpcActionComponent action = player.NpcActionComponent;
            return action != null && action.CurrentNpcEntity == _entity;
        }

        /// <summary>Every peer: turn towards the synced yaw, or back home.</summary>
        private void Turn()
        {
            float target = facing.Value ? yaw.Value : _homeYaw;

            if (_hasMover)
            {
                // The mover owns the rotation and carries the server's to the clients.
                if (!IsServer || IsMoving())
                    return;
                if (_steered && Mathf.Abs(Mathf.DeltaAngle(_steeredYaw, target)) < 0.5f)
                    return;
                _steered = true;
                _steeredYaw = target;
                _entity.SetLookRotation(Quaternion.Euler(0f, target, 0f), false);
                return;
            }

            Transform root = _entity.EntityTransform;
            float now = root.eulerAngles.y;
            if (Mathf.Abs(Mathf.DeltaAngle(now, target)) < 0.01f)
                return;
            float next = Mathf.MoveTowardsAngle(now, target, turnSpeed * Time.deltaTime);
            root.rotation = Quaternion.AngleAxis(Mathf.DeltaAngle(now, next), Vector3.up) * root.rotation;
        }

        private bool IsMoving()
        {
            const MovementState moving = MovementState.Forward | MovementState.Backward | MovementState.Left | MovementState.Right;
            return (_entity.MovementState & moving) != MovementState.None;
        }

        private float CurrentYaw()
        {
            return _hasMover ? _entity.GetLookRotation().eulerAngles.y : _entity.EntityTransform.eulerAngles.y;
        }

        private float YawTo(Vector3 position)
        {
            Vector3 to = position - _entity.EntityTransform.position;
            to.y = 0f;
            if (to.sqrMagnitude < 0.0001f)
                return CurrentYaw();
            return Quaternion.LookRotation(to).eulerAngles.y;
        }
    }
}
