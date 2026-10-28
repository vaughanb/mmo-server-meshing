using Insthync.ManagedUpdating;
using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// Plants a humanoid's feet on the ground actually under them: on a slope, a step or a
    /// rock, the downhill foot reaches down, the hips drop to let it, and the uphill knee
    /// bends. Each planted foot is also tipped to lie along the ground, and no sole is let
    /// through the floor.
    ///
    /// The kit animates every character on a flat plane through the entity's root, so without
    /// this a character on a hillside stands with one foot in the air and the other buried.
    /// The kit's own "foot IK" switch (<c>applyFootIk</c> on each animation state) is not this:
    /// it only makes a humanoid clip honour its own foot goal curves, and knows nothing of the
    /// ground.
    ///
    /// **How it works.** Each frame, after the animation has posed the skeleton, one ray goes
    /// down under each ankle. The ground found there, relative to the ground the animation
    /// assumes (the root's plane), is how far that foot has to move. The hips drop by the
    /// lower of the two, then a two-bone solve moves each ankle the rest of its way. A foot
    /// keeps its animated height above the ground, so a stride still lifts it: the ground only
    /// moves the plane it steps on. Tilt fades out as the animation lifts the foot, so a foot
    /// in mid-stride is not flattened against a slope it has left.
    ///
    /// **Foot locking: the sole, not the ankle.** Some of the library's clips put the planted
    /// foot through the floor: `Walk_Loop` sinks the stance foot to -5.5cm mid-step, and the
    /// side jogs land on the ball of the foot 6-9cm under - while the rest of their cycle is
    /// in the air, so no single height offset on the clip can fix them. So each foot carries
    /// three contact points measured off the idle's sole: the heel and the ball (in the foot
    /// bone) and the toe tip (in the toe bone, so it follows the toe's bend - when the toes
    /// curl up at push-off the ball is what stays down). Relative to where those points rest
    /// in the idle, the lowest is the foot's clearance, and it is:
    /// <list type="bullet">
    /// <item>never below zero - a sinking foot is lifted onto the ground, and the knee bends
    /// to take it;</item>
    /// <item>pulled onto the ground when it is within <see cref="lockHeight"/> of it, easing
    /// to nothing at that height - so a foot the animation almost plants is planted, and it
    /// stays planted until the animation lifts it clear.</item>
    /// </list>
    /// The same clearance decides how planted a foot is for the tilt, which the ankle's height
    /// could not: a forefoot landing holds the ankle high with the ball on the ground.
    /// This is vertical only. Nothing pins a foot sideways, because the kit already scales the
    /// gait to the move speed and a horizontal pin would stretch the leg on every turn.
    ///
    /// **Runs in the kit's IK slot** (<see cref="DefaultExecutionOrders.GAME_ENTITY_MODEL_IK"/>),
    /// the managed late update after the animation graph has been evaluated and before
    /// <see cref="DefaultExecutionOrders.GAME_ENTITY_MODEL_POST_IK"/>, where the kit copies bone
    /// poses onto separately-skinned equipment - so gear follows the corrected legs.
    ///
    /// **The kit throttles animation by distance** (<c>animationLodUpdater</c>: 30, 15, then 5
    /// frames a second past 25, 50 and 75 m). On a frame the graph is not evaluated the bones
    /// still hold last frame's result, IK included, and solving on top of that would compound
    /// every frame until the legs folded up. So the animated pose is kept, and when the bones
    /// come back exactly as this left them - which only happens when nothing evaluated in
    /// between - it is put back before solving again.
    ///
    /// **A dead body is laid on the ground instead.** Nothing is planted any more, so the legs
    /// keep the clip's pose, and the hips - and with them the whole body - are moved onto the
    /// ground under them and tipped to lie along it (<see cref="LegIK.LayOnGround"/>).
    /// Without that a monster's corpse lay at its navmesh root's height, a hand's breadth above
    /// the grass.
    ///
    /// Purely cosmetic and local: nothing is synced, the capsule does not move, and a headless
    /// server does none of it. Switched off (faded, not snapped) while airborne, swimming,
    /// climbing or mounted. On the create and select screens the entity is disabled
    /// (see <see cref="CharacterSize"/>) and this does nothing.
    ///
    /// Belongs on the model, beside its <see cref="Animator"/>. Added to every humanoid body by
    /// <c>DemoFootIKBuilder</c> (Open MMORPG > Demo > Build Foot IK) and by
    /// <c>DemoCharacterBuilder</c> on a rebuild, which also measure the contact points. The
    /// seated copy that rides the horse is built from the same prefab and so carries one too;
    /// it switches itself off there, because a <see cref="RiderLegSpread"/> owns those legs.
    /// </summary>
    [RequireComponent(typeof(Animator))]
    [DisallowMultipleComponent]
    public class HumanoidFootIK : MonoBehaviour, IManagedLateUpdate
    {
        [Tooltip("Height of the ankle bone above the model's origin while the foot is flat on the ground, " +
                 "at scale one. Measured from the idle by the builder. Only used when there are no contact points.")]
        public float plantedAnkleHeight = 0.085f;

        [Header("Sole contact points (measured by the builder)")]
        [Tooltip("True once the contact points below have been measured. Without them the sole is not kept out of the floor.")]
        public bool hasContacts;

        [Tooltip("The left heel's contact point, in the left foot bone's space.")]
        public Vector3 leftHeel;

        [Tooltip("The left ball of the foot's contact point, under the toe joint, in the left foot bone's space.")]
        public Vector3 leftBall;

        [Tooltip("The left toe tip's contact point, in the left toe bone's space (the foot bone's when there is no toe bone).")]
        public Vector3 leftToe;

        [Tooltip("The right heel's contact point, in the right foot bone's space.")]
        public Vector3 rightHeel;

        [Tooltip("The right ball of the foot's contact point, under the toe joint, in the right foot bone's space.")]
        public Vector3 rightBall;

        [Tooltip("The right toe tip's contact point, in the right toe bone's space (the foot bone's when there is no toe bone).")]
        public Vector3 rightToe;

        [Tooltip("Height of the lower contact point above the model's origin while standing in the idle, at scale one: " +
                 "the height a sole stands at on the ground.")]
        public float soleRestHeight = -0.007f;

        [Tooltip("A sole the animation brings within this height of the ground is pulled onto it, easing off to " +
                 "nothing at this height. Zero turns the pull off; the sole is still kept out of the floor.")]
        public float lockHeight = 0.03f;

        [Header("Ground")]
        [Tooltip("How far above the root's plane a foot may be lifted to meet the ground: a step, a rock, " +
                 "the uphill side of a slope.")]
        public float maxStepUp = 0.35f;

        [Tooltip("How far the hips may drop so a foot can reach ground below the root's plane.")]
        public float maxStepDown = 0.4f;

        [Tooltip("How far the hips may rise, for a root that sits a little below the ground - a navmesh " +
                 "agent stands on the navmesh, which only approximates the terrain.")]
        public float maxHipsRaise = 0.1f;

        [Tooltip("Height the animation must lift a foot above planted before its tilt has fully faded out.")]
        public float liftFade = 0.12f;

        [Tooltip("The steepest ground a foot is tipped to match, in degrees.")]
        [Range(0f, 60f)]
        public float maxFootTilt = 30f;

        [Tooltip("The steepest ground a dead body is tipped to lie along, in degrees.")]
        [Range(0f, 45f)]
        public float maxCorpseTilt = 25f;

        [Header("Smoothing")]
        [Tooltip("How quickly the hips follow the ground, per second. Higher is snappier.")]
        public float hipsSharpness = 12f;

        [Tooltip("How quickly each foot's tilt follows the slope under it, per second.")]
        public float footSharpness = 20f;

        [Tooltip("How quickly the whole effect fades in and out, per second.")]
        public float fadeSpeed = 5f;

        [Tooltip("No IK beyond this distance from the camera: the feet are too small to see and the rays are not free.")]
        public float maxCameraDistance = 40f;

        private Animator _animator;
        private BaseGameEntity _entity;
        private BaseCharacterEntity _character;
        private Transform _hips;
        private Leg _left;
        private Leg _right;
        private bool _valid;
        private bool _registered;

        private float _weight;
        private float _hipsGroundY;
        private Vector3 _corpseNormal = Vector3.up;
        private Vector3 _hipsAnimated;
        private Quaternion _hipsAnimatedRotation;
        private Vector3 _hipsWritten;
        private Quaternion _hipsWrittenRotation;
        private bool _hasWritten;

        private class Leg
        {
            public Transform upper;
            public Transform lower;
            public Transform foot;
            public Transform toes;
            public Vector3 heelPoint;
            public Vector3 ballPoint;
            public Vector3 toePoint;
            // How far the ground under the foot sits from the root's plane, this frame.
            public float offset;
            public Vector3 normal = Vector3.up;
            // This frame: how far the sole has to move to stay out of (or lock onto) the
            // ground, and how far above the ground the sole then stands.
            public float soleShift;
            public float clearance;
            // The animated local pose, and what this wrote over it - see the class summary.
            public Quaternion upperAnimated, lowerAnimated, footAnimated;
            public Quaternion upperWritten;
        }

        private void Awake()
        {
            _animator = GetComponent<Animator>();
            _entity = GetComponentInParent<BaseGameEntity>();
            _character = _entity as BaseCharacterEntity;
            // The horse rider's seated copy of this body: its legs are posed by RiderLegSpread.
            if (Application.isBatchMode || _entity == null || GetComponent<RiderLegSpread>() != null ||
                _animator == null || !_animator.isHuman)
            {
                enabled = false;
                return;
            }
            _hips = _animator.GetBoneTransform(HumanBodyBones.Hips);
            _left = MakeLeg(HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot,
                HumanBodyBones.LeftToes, leftHeel, leftBall, leftToe);
            _right = MakeLeg(HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot,
                HumanBodyBones.RightToes, rightHeel, rightBall, rightToe);
            _valid = _hips != null && _left != null && _right != null;
            if (!_valid)
                enabled = false;
        }

        private Leg MakeLeg(HumanBodyBones upper, HumanBodyBones lower, HumanBodyBones foot, HumanBodyBones toes,
            Vector3 heelPoint, Vector3 ballPoint, Vector3 toePoint)
        {
            var leg = new Leg
            {
                upper = _animator.GetBoneTransform(upper),
                lower = _animator.GetBoneTransform(lower),
                foot = _animator.GetBoneTransform(foot),
                toes = _animator.GetBoneTransform(toes),
                heelPoint = heelPoint,
                ballPoint = ballPoint,
                toePoint = toePoint,
            };
            if (leg.toes == null)
                leg.toes = leg.foot;
            return leg.upper != null && leg.lower != null && leg.foot != null ? leg : null;
        }

        private void OnEnable()
        {
            if (!_valid || _registered)
                return;
            UpdateManager.Register(DefaultExecutionOrders.GAME_ENTITY_MODEL_IK, this);
            _registered = true;
        }

        private void OnDisable()
        {
            if (!_registered)
                return;
            UpdateManager.Unregister(DefaultExecutionOrders.GAME_ENTITY_MODEL_IK, this);
            _registered = false;
            _hasWritten = false;
            _weight = 0f;
        }

        public void ManagedLateUpdate()
        {
            // A menu preview: the screens switch the entity off, and there is no ground to stand on.
            if (!_entity.enabled)
                return;

            RestoreIfNotReanimated();

            float deltaTime = Time.deltaTime;
            bool dead = _character != null && _character.IsDead();
            if (!dead)
                _corpseNormal = Vector3.up;
            bool active = dead ? LegIK.ShouldGroundCorpse(_entity, _character, transform, maxCameraDistance) : ShouldBeActive();
            // Coming back from off (or first run): take the ground as it is.
            bool fresh = _weight <= 0f;
            // Off at once in the air; see LegIK.IsAirborne. Everything else fades.
            if (!dead && !active && LegIK.IsAirborne(_entity))
                _weight = 0f;
            else
                _weight = Mathf.MoveTowards(_weight, active ? 1f : 0f, fadeSpeed * deltaTime);
            if (_weight <= 0f)
            {
                _hasWritten = false;
                return;
            }

            // Remember the animated pose before anything is changed.
            _hipsAnimated = _hips.localPosition;
            _hipsAnimatedRotation = _hips.localRotation;
            Keep(_left);
            Keep(_right);

            Transform root = _entity.EntityTransform;
            float rootY = root.position.y;
            float scale = transform.lossyScale.y;
            int mask = GameInstance.Singleton != null ? GameInstance.Singleton.GetGameEntityGroundDetectionLayerMask() : Physics.DefaultRaycastLayers;
            float hipsBlend = 1f - Mathf.Exp(-hipsSharpness * deltaTime);

            if (dead)
            {
                // A body, not a stance: nothing is planted, so the legs keep the clip's pose and
                // the hips carry all of it onto the ground.
                LegIK.LayOnGround(_hips, rootY, maxStepUp, maxStepDown, maxCorpseTilt, mask, hipsBlend, _weight, fresh,
                    ref _hipsGroundY, ref _corpseNormal);
            }
            else
            {
                // Read off the animated pose, before the hips or legs are moved.
                MeasureSole(_left, rootY, scale);
                MeasureSole(_right, rootY, scale);

                float footBlend = 1f - Mathf.Exp(-footSharpness * deltaTime);
                Probe(_left, rootY, mask, footBlend);
                Probe(_right, rootY, mask, footBlend);

                // The hips are what is eased, not the feet, and as a world height: a navmesh
                // agent's root jumps as it crosses polygon edges, and an offset from it would read
                // every such jump as the ground moving. See LegIK.Follow.
                float hipsTarget = Mathf.Clamp(Mathf.Min(_left.offset, _right.offset), -maxStepDown, maxHipsRaise);
                _hipsGroundY = LegIK.Follow(_hipsGroundY, rootY + hipsTarget, hipsBlend, fresh);
                float hips = (_hipsGroundY - rootY) * _weight;

                _hips.position += Vector3.up * hips;
                Solve(_left, hips);
                Solve(_right, hips);
            }

            _hipsWritten = _hips.localPosition;
            _hipsWrittenRotation = _hips.localRotation;
            _left.upperWritten = _left.upper.localRotation;
            _right.upperWritten = _right.upper.localRotation;
            _hasWritten = true;
        }

        private bool ShouldBeActive()
        {
            return LegIK.ShouldBeActive(_entity, _character, transform, maxCameraDistance);
        }

        /// <summary>
        /// When the animation graph skipped this frame the bones still hold what the solve
        /// wrote last frame. Put the animated pose back, so the solve starts from it again
        /// rather than from its own output.
        /// </summary>
        private void RestoreIfNotReanimated()
        {
            if (!_hasWritten)
                return;
            _hasWritten = false;
            if (_hips.localPosition != _hipsWritten ||
                _hips.localRotation != _hipsWrittenRotation ||
                _left.upper.localRotation != _left.upperWritten ||
                _right.upper.localRotation != _right.upperWritten)
                return;
            _hips.localPosition = _hipsAnimated;
            _hips.localRotation = _hipsAnimatedRotation;
            Restore(_left);
            Restore(_right);
        }

        private static void Keep(Leg leg)
        {
            leg.upperAnimated = leg.upper.localRotation;
            leg.lowerAnimated = leg.lower.localRotation;
            leg.footAnimated = leg.foot.localRotation;
        }

        private static void Restore(Leg leg)
        {
            leg.upper.localRotation = leg.upperAnimated;
            leg.lower.localRotation = leg.lowerAnimated;
            leg.foot.localRotation = leg.footAnimated;
        }

        /// <summary>
        /// How far the animated sole stands above the root's plane, and how far to move it:
        /// up out of the floor when it is below, down onto it when it is nearly there. See
        /// the class summary. Falls back to the ankle's height, unshifted, on a body whose
        /// contact points were never measured.
        /// </summary>
        private void MeasureSole(Leg leg, float rootY, float scale)
        {
            float animated;
            if (hasContacts)
            {
                float heel = leg.foot.TransformPoint(leg.heelPoint).y;
                float ball = leg.foot.TransformPoint(leg.ballPoint).y;
                float toe = leg.toes.TransformPoint(leg.toePoint).y;
                animated = Mathf.Min(heel, Mathf.Min(ball, toe)) - rootY - soleRestHeight * scale;
            }
            else
            {
                animated = leg.foot.position.y - rootY - plantedAnkleHeight * scale;
                leg.soleShift = 0f;
                leg.clearance = Mathf.Max(0f, animated);
                return;
            }
            float locked = LegIK.Lock(animated, lockHeight * scale);
            leg.soleShift = locked - animated;
            leg.clearance = locked;
        }

        /// <summary>
        /// How far the ground under this foot sits above (+) or below (-) the root's plane,
        /// which is the plane the animation stands the character on. Raw, not smoothed: the
        /// ground under a moving foot is continuous already, and smoothing it lags a foot
        /// that sweeps across a slope as the character turns.
        /// </summary>
        private void Probe(Leg leg, float rootY, int mask, float blend)
        {
            Vector3 ankle = leg.foot.position;
            Vector3 origin = new Vector3(ankle.x, rootY + maxStepUp, ankle.z);
            float groundY = rootY;
            Vector3 normal = Vector3.up;
            if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, maxStepUp + maxStepDown, mask, QueryTriggerInteraction.Ignore))
            {
                groundY = hit.point.y;
                normal = hit.normal;
            }
            leg.offset = groundY - rootY;
            leg.normal = Vector3.Slerp(leg.normal, normal, blend);
        }

        private void Solve(Leg leg, float hips)
        {
            Quaternion footRotation = leg.foot.rotation;
            // The animated ankle, moved by the ground under it and by the sole's correction.
            // The hips already carried it by `hips`, so the leg itself only covers the rest.
            Vector3 target = leg.foot.position + Vector3.up * ((leg.offset + leg.soleShift) * _weight - hips);
            LegIK.SolveTwoBone(leg.upper, leg.lower, leg.foot, target, transform.forward);

            // The foot keeps its animated orientation in the world, then tips onto the slope -
            // fully while planted, not at all once the stride has lifted it.
            float plantedness = 1f - Mathf.Clamp01(leg.clearance / Mathf.Max(liftFade, 0.001f));
            leg.foot.rotation = LegIK.Tilt(leg.normal, maxFootTilt, plantedness * _weight) * footRotation;
        }
    }
}
