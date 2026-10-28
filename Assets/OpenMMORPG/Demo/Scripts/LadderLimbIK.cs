using Insthync.ManagedUpdating;
using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// Puts a climber's hands and feet on the rungs. The climb loop is an in-place clip
    /// played under a body that the kit moves up the ladder, so left alone its hands and
    /// feet only pass through the rungs by chance. This reads the animation's own rhythm -
    /// a limb is planted when it is still in the world, swinging when it moves - and while
    /// a limb is planted holds it to the rung it was nearest to when it landed, by two-bone
    /// IK on that arm or leg. Between rungs the animation has the limb to itself. Hanging
    /// still, all four hold.
    ///
    /// Reads the planted phase off the limb's world speed along the ladder, which the clip
    /// makes legible: DemoAnimationSet rates the loop so a planted limb's descent through
    /// the body matches the body's climb, so planted is near-zero and swinging is metres a
    /// second. The body's lateral placement is the animation's; only height and depth are
    /// taken from the rung, so hands stay where the shoulders put them.
    ///
    /// Same frame discipline as <see cref="HumanoidFootIK"/>: runs after the animation, writes
    /// bone rotations, and puts the animated pose back first on a frame the graph skipped.
    /// The rungs come from <see cref="LadderRungs"/> on the ladder being climbed; a
    /// ladder without one is climbed as before. Put on every humanoid body by the character
    /// builder; disables itself on anything else, and on any character with no ladder
    /// component - the monsters and NPCs share the player's bodies but never climb, so they do
    /// not tick it at all.
    /// </summary>
    public class LadderLimbIK : MonoBehaviour, IManagedLateUpdate
    {
        [Header("Grip")]
        [Tooltip("Where the wrist sits in front of a rung's centre when gripping it, in metres: the palm lies over the rung.")]
        public float handForward = 0.06f;

        [Tooltip("Where the wrist sits above a rung's centre when gripping it, in metres.")]
        public float handLift = 0.05f;

        [Tooltip("How far below level a gripping hand's fingers point as they reach over the rung, in degrees.")]
        [Range(0f, 80f)]
        public float gripFingerDrop = 40f;

        [Tooltip("Where the ankle sits in front of a rung's centre with the foot on it, in metres.")]
        public float footForward = 0.1f;

        [Tooltip("Where the ankle sits above a rung's centre with the foot on it, in metres: the " +
                 "rung's radius plus the ankle's height over the sole.")]
        public float footLift = 0.12f;

        [Header("Rhythm")]
        [Tooltip("A limb moving slower than this along the ladder, in metres a second, is planted.")]
        public float plantedSpeed = 0.6f;

        [Tooltip("A held limb moving faster than this along the ladder, in metres a second, is swinging and lets go. " +
                 "Above the planted speed, so a limb hovering about the one does not flicker between the two.")]
        public float swingSpeed = 1.2f;

        [Tooltip("A limb landing farther than this from the nearest rung is left where the animation put it.")]
        public float maxReach = 0.3f;

        [Tooltip("How quickly a limb takes hold of a rung, per second.")]
        public float gripSpeed = 14f;

        [Tooltip("How quickly a limb lets go of a rung as it swings away, per second.")]
        public float releaseSpeed = 14f;

        [Tooltip("How fast a held hand or foot may slide to its place on the rung, in metres a second: the " +
                 "limit on any correction, so a limb taking hold off-centre, or taking a rung back, glides there.")]
        public float holdSpeed = 3f;

        [Tooltip("How much of an arm's or leg's full length a rung may be from the shoulder or hip and still be " +
                 "taken. Beyond it the next rung in is taken instead, so a hand does not strain at one it cannot reach.")]
        [Range(0.5f, 1f)]
        public float reach = 0.95f;

        [Tooltip("No IK beyond this distance from the camera.")]
        public float maxCameraDistance = 40f;

        [Header("Stance")]
        [Tooltip("How far the body is drawn toward the ladder while climbing, in metres. The kit holds the " +
                 "capsule clear of the ladder's collider, which leaves the shoulders half a metre from the rungs - " +
                 "an arm's length on this rig, so the hands could only reach with straight arms, or not at all. " +
                 "The model alone moves; the capsule, and so the collision, stays where the kit puts it.")]
        public float lean = 0.15f;

        [Tooltip("How far inside the rungs' ends a hand or foot is kept, in metres.")]
        public float edgeMargin = 0.04f;

        private class Limb
        {
            public Transform upper;
            public Transform lower;
            public Transform end;
            // Hands only: the middle finger and thumb roots, which say which way the hand is facing.
            public Transform finger;
            public Transform thumb;
            public bool hand;
            public bool left;
            public Quaternion upperAnimated, lowerAnimated, endAnimated;
            public Quaternion upperWritten;
            public Vector3 lastAnimated;
            public bool hasLast;
            public int rung = -1;
            // The rung last held, taken up again if the limb plants while still letting it go.
            public int lastRung = -1;
            public float weight;
            // Where the limb is being held, which glides toward its place on the rung.
            public Vector3 hold;
        }

        private Animator _animator;
        private BaseGameEntity _entity;
        private BaseCharacterEntity _character;
        private Limb[] _limbs;
        private bool _valid;
        private bool _registered;
        private bool _hasWritten;
        private Vector3 _restLocalPosition;
        private float _leanWeight;
        private bool _leaning;
        private Ladder _rungsOf;
        private LadderRungs _rungs;

        private void Awake()
        {
            _animator = GetComponent<Animator>();
            _entity = GetComponentInParent<BaseGameEntity>();
            _character = _entity as BaseCharacterEntity;
            // Nor on anything that cannot climb: the monsters and NPCs share these bodies, and
            // have no ladder component to climb with. Read off the entity's object rather than
            // its LadderComponent property, which the entity fills in its own Awake.
            if (Application.isBatchMode || _character == null || GetComponent<RiderLegSpread>() != null ||
                _animator == null || !_animator.isHuman || _character.GetComponent<CharacterLadderComponent>() == null)
            {
                enabled = false;
                return;
            }
            _limbs = new[]
            {
                Make(HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, true, true),
                Make(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, true, false),
                Make(HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, false, true),
                Make(HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot, false, false),
            };
            _valid = true;
            foreach (Limb limb in _limbs)
                _valid &= limb != null;
            if (!_valid)
                enabled = false;
            _restLocalPosition = transform.localPosition;
        }

        private Limb Make(HumanBodyBones upper, HumanBodyBones lower, HumanBodyBones end, bool hand, bool left)
        {
            var limb = new Limb
            {
                upper = _animator.GetBoneTransform(upper),
                lower = _animator.GetBoneTransform(lower),
                end = _animator.GetBoneTransform(end),
                hand = hand,
                left = left,
            };
            if (hand)
            {
                limb.finger = _animator.GetBoneTransform(left ? HumanBodyBones.LeftMiddleProximal : HumanBodyBones.RightMiddleProximal);
                limb.thumb = _animator.GetBoneTransform(left ? HumanBodyBones.LeftThumbProximal : HumanBodyBones.RightThumbProximal);
            }
            return limb.upper != null && limb.lower != null && limb.end != null ? limb : null;
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
            if (_leaning)
                StopLeaning();
            Forget();
        }

        private void Forget()
        {
            foreach (Limb limb in _limbs)
            {
                limb.weight = 0f;
                limb.rung = -1;
                limb.lastRung = -1;
                limb.hasLast = false;
            }
        }

        public void ManagedLateUpdate()
        {
            if (!_entity.enabled)
                return;

            RestoreIfNotReanimated();

            LadderRungs rungs = ClimbedRungs();
            float deltaTime = Time.deltaTime;
            Lean(rungs, deltaTime);
            if (rungs == null)
            {
                // Not on a ladder (or on one with no rungs to find): let go of everything
                // at once. The pose is the animation's again from this frame.
                if (_hasWritten || AnyHeld())
                    Forget();
                _hasWritten = false;
                return;
            }

            bool near = true;
            Camera camera = Camera.main;
            if (camera != null && (camera.transform.position - transform.position).sqrMagnitude > maxCameraDistance * maxCameraDistance)
                near = false;

            Vector3 up = rungs.Ladder.Up;
            Vector3 forward = rungs.Ladder.ForwardWithYAngleOffsets;
            Vector3 right = rungs.Ladder.RightWithYAngleOffsets;
            float halfWidth = Mathf.Max(0f, rungs.halfWidth - edgeMargin);
            bool wrote = false;

            foreach (Limb limb in _limbs)
            {
                limb.upperAnimated = limb.upper.localRotation;
                limb.lowerAnimated = limb.lower.localRotation;
                limb.endAnimated = limb.end.localRotation;

                // The animated end of the limb, and how fast it is moving along the ladder in
                // the world. Still means planted; the first frame counts as still.
                Vector3 animated = limb.end.position;
                float speed = limb.hasLast && deltaTime > 0f ? Vector3.Dot(animated - limb.lastAnimated, up) / deltaTime : 0f;
                limb.lastAnimated = animated;
                limb.hasLast = true;

                float lift = limb.hand ? handLift : footLift;
                float along = rungs.Along(animated) - lift;
                if (limb.rung >= 0)
                {
                    // Held: let go only once the limb is clearly swinging.
                    if (!near || Mathf.Abs(speed) > swingSpeed)
                    {
                        limb.lastRung = limb.rung;
                        limb.rung = -1;
                    }
                }
                else if (near && Mathf.Abs(speed) < plantedSpeed)
                {
                    // Landing: the rung it was just letting go of, if the hold has not run out
                    // and it is still in reach, so a limb that merely hesitated does not hop
                    // to a neighbour; otherwise the nearest it can reach.
                    if (limb.weight > 0f && limb.lastRung >= 0 && Mathf.Abs(along - rungs.rungs[limb.lastRung]) <= maxReach)
                    {
                        limb.rung = limb.lastRung;
                    }
                    else
                    {
                        int nearest = rungs.Nearest(along, out float off);
                        if (nearest >= 0 && Mathf.Abs(off) <= maxReach)
                            limb.rung = Reachable(limb, rungs, nearest, animated, lift, right, up, forward, halfWidth);
                    }
                    if (limb.rung >= 0 && limb.weight <= 0f)
                        limb.hold = animated;
                }

                bool holding = limb.rung >= 0;
                limb.weight = Mathf.MoveTowards(limb.weight, holding ? 1f : 0f, (holding ? gripSpeed : releaseSpeed) * deltaTime);
                if (limb.weight <= 0f)
                    continue;

                // The hold glides to its place on the rung; letting go, the rung is gone and
                // the hold stays put while its weight runs down to nothing.
                if (holding)
                    limb.hold = Vector3.MoveTowards(limb.hold, Place(limb, rungs.Centre(limb.rung), animated, lift, right, up, forward, halfWidth), holdSpeed * deltaTime);
                Vector3 goal = Vector3.Lerp(animated, limb.hold, limb.weight);

                // Knees bend toward the ladder, elbows away from it.
                Quaternion endRotation = limb.end.rotation;
                LegIK.SolveTwoBone(limb.upper, limb.lower, limb.end, goal, limb.hand ? -transform.forward : transform.forward);
                limb.end.rotation = limb.hand ? Grip(limb, endRotation, up, forward) : endRotation;
                limb.upperWritten = limb.upper.localRotation;
                wrote = true;
            }

            _hasWritten = wrote;
        }

        /// <summary>
        /// Where the end of the limb goes to hold this rung: height and depth from the rung,
        /// the animation's own spread across it, kept within the rung's ends.
        /// </summary>
        private Vector3 Place(Limb limb, Vector3 centre, Vector3 animated, float lift, Vector3 right, Vector3 up, Vector3 forward, float halfWidth)
        {
            float lateral = Mathf.Clamp(Vector3.Dot(animated - centre, right), -halfWidth, halfWidth);
            return centre + right * lateral + up * lift + forward * (limb.hand ? handForward : footForward);
        }

        /// <summary>
        /// The rung nearest the one given that the limb can hold without straining: the
        /// nearest, else the one below it, else the one above (a foot the other way round).
        /// -1 when none will do.
        /// </summary>
        private int Reachable(Limb limb, LadderRungs rungs, int nearest, Vector3 animated, float lift, Vector3 right, Vector3 up, Vector3 forward, float halfWidth)
        {
            float length = (limb.lower.position - limb.upper.position).magnitude + (limb.end.position - limb.lower.position).magnitude;
            int step = limb.hand ? -1 : 1;
            foreach (int rung in new[] { nearest, nearest + step, nearest - step })
            {
                if (rung < 0 || rung >= rungs.Count)
                    continue;
                Vector3 place = Place(limb, rungs.Centre(rung), animated, lift, right, up, forward, halfWidth);
                if ((place - limb.upper.position).magnitude <= length * reach)
                    return rung;
            }
            return -1;
        }

        /// <summary>
        /// Draws the body toward the ladder while it is climbed (see <see cref="lean"/>),
        /// and lets it back out when it is not. Moves the model's own position under the
        /// entity; the bones hang off it, so the hands and feet, and the IK that follows, go
        /// with it.
        ///
        /// Off the ladder the model is not this component's: <see cref="SurfaceSwimmer"/>
        /// lifts it to the surface while swimming, in the entity's late update, which runs
        /// before this one. So the rest position is put back once, on the frame the lean runs
        /// out, and never written again until the next climb - writing it every frame pinned
        /// a swimmer 1.2 m under the water.
        /// </summary>
        private void Lean(LadderRungs rungs, float deltaTime)
        {
            float target = rungs != null ? 1f : 0f;
            _leanWeight = Mathf.MoveTowards(_leanWeight, target, gripSpeed * deltaTime);
            if (_leanWeight <= 0f)
            {
                if (_leaning)
                    StopLeaning();
                return;
            }
            _leaning = true;
            Vector3 toward = rungs != null ? -rungs.Ladder.ForwardWithYAngleOffsets : transform.forward;
            transform.position = transform.parent != null
                ? transform.parent.TransformPoint(_restLocalPosition) + toward * (lean * _leanWeight)
                : transform.position;
        }

        private void StopLeaning()
        {
            transform.localPosition = _restLocalPosition;
            _leanWeight = 0f;
            _leaning = false;
        }

        /// <summary>
        /// A hand's rotation while it holds a rung: turned from its animated facing so the
        /// fingers reach over the rung, pointing in and down, with the palm on it. Read off
        /// the finger and thumb roots, so it is right whichever way the rig's hand bone
        /// points; a hand with no fingers keeps the animation's facing.
        /// </summary>
        private Quaternion Grip(Limb limb, Quaternion animated, Vector3 up, Vector3 forward)
        {
            if (limb.finger == null || limb.thumb == null || limb.weight <= 0f)
                return animated;
            Vector3 fingers = (limb.finger.position - limb.end.position).normalized;
            Vector3 palm = Vector3.Cross(fingers, (limb.thumb.position - limb.end.position).normalized).normalized;
            if (limb.left)
                palm = -palm;
            float drop = gripFingerDrop * Mathf.Deg2Rad;
            Vector3 fingersWanted = -forward * Mathf.Cos(drop) - up * Mathf.Sin(drop);
            Vector3 palmWanted = -up * Mathf.Cos(drop) + forward * Mathf.Sin(drop);
            Quaternion turn = Quaternion.FromToRotation(fingers, fingersWanted);
            Vector3 palmTurned = turn * palm;
            // Already perpendicular to the fingers, so this twists about them.
            turn = Quaternion.FromToRotation(palmTurned, palmWanted) * turn;
            return Quaternion.Slerp(Quaternion.identity, turn, limb.weight) * animated;
        }

        private bool AnyHeld()
        {
            foreach (Limb limb in _limbs)
            {
                if (limb.weight > 0f || limb.rung >= 0)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// The rungs of the ladder this body is climbing, or null when it is not climbing,
        /// is getting on or off, is dead, or the ladder has no rungs listed.
        /// </summary>
        private LadderRungs ClimbedRungs()
        {
            CharacterLadderComponent climb = _character.LadderComponent;
            if (climb == null || climb.ClimbingLadder == null || climb.EnterExitState != EnterExitState.None || _character.IsDead())
                return null;
            // Looked up once per ladder, not every frame of the climb.
            if (climb.ClimbingLadder != _rungsOf)
            {
                _rungsOf = climb.ClimbingLadder;
                _rungs = _rungsOf.GetComponent<LadderRungs>();
            }
            return _rungs != null && _rungs.Count > 0 ? _rungs : null;
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
            foreach (Limb limb in _limbs)
            {
                if (limb.weight > 0f && limb.upper.localRotation != limb.upperWritten)
                    return;
            }
            foreach (Limb limb in _limbs)
            {
                if (limb.weight <= 0f)
                    continue;
                limb.upper.localRotation = limb.upperAnimated;
                limb.lower.localRotation = limb.lowerAnimated;
                limb.end.localRotation = limb.endAnimated;
            }
        }
    }
}
