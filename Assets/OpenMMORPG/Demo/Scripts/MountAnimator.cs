using Insthync.ManagedUpdating;
using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// Animates a mount: drives its gait from how fast it is actually travelling, and keeps its
    /// riders on its back.
    ///
    /// **The gait.** `VehicleEntity` carries no model and no animator — it is seats, speed and
    /// hit points, and nothing in the kit ever touches an `Animator` on it. So a mount imported
    /// with walk and gallop clips just slides around the map until something plays them, and
    /// this is that something. It reads **measured** speed rather than asking the entity for its
    /// move speed. `GetMoveSpeed` returns what the vehicle is *allowed* to do, not what it is
    /// doing, so a horse standing still would gallop on the spot. Position delta is also the
    /// only thing that works unchanged on a remote client, where the vehicle is moved by the
    /// sync layer rather than by its own movement component.
    ///
    /// **The riders.** The kit seats a passenger by snapping its root to the seat transform every
    /// frame (<c>BaseGameEntity.ManagedLateUpdate</c>, order <c>BASE_GAME_ENTITY</c>), and a seat
    /// is a plain child of the entity root. So the rider sat still in the air while the back under
    /// it did everything a horse's back does: rose and fell with the gallop, rocked with the idle's
    /// breathing, and on a hill pitched and dropped with <see cref="QuadrupedFootIK"/> - which
    /// left the rider floating a third of a metre over the saddle facing uphill. Here each seat is
    /// re-expressed, once, in the <see cref="body"/> bone's **bind pose** frame, and every frame
    /// each passenger is placed as if it were a vertex skinned wholly to that bone: the bone's
    /// current world matrix times the seat's bind-space pose. The bind pose is the right reference
    /// because the seat was measured against the unposed mesh (<c>DemoMountBuilder.MeasureSaddle</c>
    /// reads <c>sharedMesh.vertices</c>), and on this rig it is also the rest pose, so the capture
    /// needs no particular animation state. Cosmetic: the kit's seat snap stays the authoritative
    /// position.
    ///
    /// **One tick, two slots.** Registered once with the kit's update manager in its post-IK slot
    /// (<c>GAME_ENTITY_MODEL_POST_IK</c>): the gait in its update, after the entities have moved
    /// this frame, and the riders in its late update, after the animation (evaluated in Update)
    /// and the foot IK (<c>GAME_ENTITY_MODEL_IK</c>) have both posed the bone. A dedicated server,
    /// with no one to show either to, skips it all. These were two components (the riders were
    /// `DemoRiderSeat`) on the same model, used only together.
    ///
    /// Attach to the model, beside the `Animator`. See <see cref="EditorTools.DemoMountBuilder"/>.
    /// </summary>
    [RequireComponent(typeof(Animator))]
    [DisallowMultipleComponent]
    public class MountAnimator : MonoBehaviour, IManagedUpdate, IManagedLateUpdate
    {
        private static readonly int SpeedHash = Animator.StringToHash("Speed");
        private static readonly int DirectionHash = Animator.StringToHash("Direction");

        [Header("Gait")]
        [Tooltip("Transform whose motion is measured. Defaults to the entity root above this model.")]
        [SerializeField]
        private Transform motionSource = null;

        [Tooltip("Seconds of smoothing on the measured speed. Keeps the blend from " +
                 "flickering between gaits when the controller stutters for a frame.")]
        [SerializeField]
        private float smoothing = 0.12f;

        [Tooltip("The pace the walk plays at while the horse turns on the spot, in metres a second. " +
                 "Keep it at the blend tree's walk threshold, or the walk plays part-blended with the idle.")]
        [SerializeField]
        private float turnPace = 1.6f;

        [Tooltip("Degrees a second the horse must turn faster than before it counts as turning at all. " +
                 "Keeps the legs still for the settling of a stopped turn.")]
        [SerializeField]
        private float turnDeadZone = 10f;

        [Tooltip("Degrees a second at which the turning walk is at full weight.")]
        [SerializeField]
        private float turnFullRate = 45f;

        [Header("Riders")]
        [Tooltip("The bone the riders are glued to - a spine bone that carries the back under the saddle " +
                 "(the demo horse uses Torso2; the pelvis-level Body bone left the rider floating in a gallop). " +
                 "Taken from the quadruped foot IK's body when empty.")]
        public Transform body;

        [Tooltip("The skinned mesh whose bind pose places the seat on the bone. Found under this object when empty.")]
        public SkinnedMeshRenderer skin;

        private Animator _animator;
        private bool _hasDirection;
        private Vector3 _previousPosition;
        private float _previousYaw;
        private float _speed;

        private BaseGameEntity _entity;
        private IVehicleEntity _vehicle;
        private Matrix4x4[] _seatInBody;
        private bool _registered;

        private void Awake()
        {
            _animator = GetComponent<Animator>();
            // A controller built before the reverse existed has no such parameter, and setting
            // a missing one logs a warning every frame.
            foreach (AnimatorControllerParameter parameter in _animator.parameters)
            {
                if (parameter.nameHash == DirectionHash)
                    _hasDirection = true;
            }
            if (motionSource == null)
                motionSource = transform.parent != null ? transform.parent : transform;
            _previousPosition = motionSource.position;

            if (Application.isBatchMode)
            {
                enabled = false;
                return;
            }
            CaptureSeats();
        }

        /// <summary>
        /// Puts each seat in the body bone's bind-space frame, or leaves the riders to the kit's
        /// own seat snap when the mount has no such bone.
        /// </summary>
        private void CaptureSeats()
        {
            _entity = GetComponentInParent<BaseGameEntity>();
            _vehicle = _entity as IVehicleEntity;
            if (body == null)
            {
                QuadrupedFootIK ik = GetComponent<QuadrupedFootIK>();
                if (ik != null)
                    body = ik.body;
            }
            if (skin == null)
                skin = GetComponentInChildren<SkinnedMeshRenderer>();
            if (_vehicle == null || body == null || skin == null || skin.sharedMesh == null)
                return;
            int bone = System.Array.IndexOf(skin.bones, body);
            if (bone < 0 || bone >= skin.sharedMesh.bindposes.Length)
            {
                Debug.LogWarning($"[{nameof(MountAnimator)}] {body.name} is not a bone of {skin.name}; riders will not follow it.", this);
                return;
            }
            // bindposes[bone] takes mesh space to the bone's bind space; the skin's transform
            // takes the world to mesh space. Seat and skin never move within the hierarchy,
            // so the product is the same wherever the mount happens to stand when this runs.
            Matrix4x4 worldToBind = skin.sharedMesh.bindposes[bone] * skin.transform.worldToLocalMatrix;
            var seats = _vehicle.Seats;
            _seatInBody = new Matrix4x4[seats.Count];
            for (int i = 0; i < seats.Count; ++i)
            {
                Transform seat = seats[i].passengingTransform != null ? seats[i].passengingTransform : _entity.EntityTransform;
                _seatInBody[i] = worldToBind * Matrix4x4.TRS(seat.position, seat.rotation, Vector3.one);
            }
        }

        private void OnEnable()
        {
            // Without this a mount that was pooled and respawned elsewhere measures the
            // whole teleport as one frame of movement, and bursts into a gallop on spawn.
            _previousPosition = motionSource.position;
            _previousYaw = motionSource.eulerAngles.y;
            _speed = 0f;
            if (_hasDirection)
                _animator.SetFloat(DirectionHash, 1f);
            if (_registered)
                return;
            UpdateManager.Register(DefaultExecutionOrders.GAME_ENTITY_MODEL_POST_IK, this);
            _registered = true;
        }

        private void OnDisable()
        {
            if (!_registered)
                return;
            UpdateManager.Unregister(DefaultExecutionOrders.GAME_ENTITY_MODEL_POST_IK, this);
            _registered = false;
        }

        /// <summary>The gait, from this frame's measured motion.</summary>
        public void ManagedUpdate()
        {
            if (Time.deltaTime <= 0f)
                return;

            Vector3 position = motionSource.position;
            Vector3 delta = position - _previousPosition;
            _previousPosition = position;

            // Vertical motion is falling and terrain, not gait.
            delta.y = 0f;
            float measured = delta.magnitude / Time.deltaTime;

            // Travelling against its own facing: there is no reverse clip, so the walk plays
            // backwards, which is how a horse backs up. A mount that is merely pushed
            // sideways is not reversing, hence the share of the motion that must be astern.
            // `Animator.speed` cannot do this - it clamps at zero - so the controller's
            // gait state takes its speed multiplier from the "Direction" parameter instead.
            Vector3 facing = motionSource.forward;
            facing.y = 0f;
            bool astern = measured > ReverseMinSpeed && Vector3.Dot(delta, facing) < -ReverseShare * delta.magnitude * facing.magnitude;

            // Turning on the spot: the library has no turning clip, and a horse that swings
            // round with its legs planted skates. A pivot is stepping, so while the body is
            // yawing the walk plays at its own pace - however slowly the horse is travelling,
            // including not at all. Past a plausible rate it is a teleport or a snap, not a turn.
            float yaw = motionSource.eulerAngles.y;
            float yawRate = Mathf.Abs(Mathf.DeltaAngle(_previousYaw, yaw)) / Time.deltaTime;
            _previousYaw = yaw;
            float turning = yawRate > MaxTurnRate
                ? 0f
                : Mathf.Clamp01((yawRate - turnDeadZone) / Mathf.Max(0.01f, turnFullRate - turnDeadZone));
            float pace = Mathf.Max(measured, turning * turnPace);

            _speed = smoothing > 0f
                ? Mathf.Lerp(_speed, pace, 1f - Mathf.Exp(-Time.deltaTime / smoothing))
                : pace;

            _animator.SetFloat(SpeedHash, _speed);
            if (_hasDirection)
                _animator.SetFloat(DirectionHash, astern ? -1f : 1f);
        }

        /// <summary>The riders, onto the back as posed this frame.</summary>
        public void ManagedLateUpdate()
        {
            // The driver check is the cheap gate: the demo's horse has one seat, and a mount
            // standing empty should not build a passenger list every frame. (The kit's
            // GetPassenger throws for an empty seat, so the list is the safe way to walk them.)
            if (_seatInBody == null || !_entity.enabled || !_vehicle.HasDriver)
                return;
            Matrix4x4 bodyToWorld = body.localToWorldMatrix;
            foreach (BaseGameEntity passenger in _vehicle.GetAllPassengers())
            {
                int seat = passenger.PassengingVehicleSeatIndex;
                if (seat < 0 || seat >= _seatInBody.Length)
                    continue;
                Matrix4x4 pose = bodyToWorld * _seatInBody[seat];
                passenger.EntityTransform.SetPositionAndRotation(pose.GetColumn(3), pose.rotation);
            }
        }

        /// <summary>Slower than this is stopping or settling, not backing up.</summary>
        private const float ReverseMinSpeed = 0.3f;

        /// <summary>Fraction of the motion that must be astern of the horse to count as reversing.</summary>
        private const float ReverseShare = 0.5f;

        /// <summary>A yaw faster than this, in degrees a second, is a teleport or a snap, not a turn.</summary>
        private const float MaxTurnRate = 720f;
    }
}
