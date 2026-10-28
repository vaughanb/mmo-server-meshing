using Insthync.ManagedUpdating;
using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// Foot IK for the four-legged: the deer, the village collie, the wolves and the horse.
    /// The body pitches to the slope it stands on, each leg reaches the ground under its own
    /// paw or hoof, and a planted foot is kept out of the floor and tipped along it. The
    /// humanoid version is <see cref="HumanoidFootIK"/>; the two share <see cref="LegIK"/>.
    ///
    /// **These rigs are not humanoid, so nothing is found for free.** All four animals carry
    /// the Quaternius *Ultimate Animated Animals* skeleton (the wolf is the collie's), and the
    /// builder wires this component by that rig's bone names (<c>DemoFootIKBuilder</c>).
    ///
    /// **The paw is not on the end of the leg.** In this rig the leg chain
    /// (<c>FrontUpperLeg</c> > <c>FrontLowerLeg</c> > <c>_end</c>) stops at the wrist or hock,
    /// and the paw or hoof is skinned to a separate bone, <c>FF.L</c> / <c>FFB.L</c>, hung off an
    /// IK control (<c>IKFrontLeg.L</c> / <c>IKBackLeg.L</c>) that sits at the armature root. The
    /// baked clips move the control to wherever the leg ends. So a leg is moved in two parts:
    /// the chain is solved to its new end, then the control is moved by exactly the distance
    /// the end actually went - not the distance it was asked to go, so the paw can never come
    /// away from the leg even when a reach is clamped.
    ///
    /// **The body pitches rather than just dropping.** A horse on a hillside with its hind
    /// legs folded under it to reach down and its front legs straight looks wrong; one with
    /// its back tipped to the slope looks right. The ground under the front pair and the hind
    /// pair are found separately, and the body (the <c>Body</c> bone, which carries spine, neck,
    /// head, tail and every leg chain) is tipped about the point between its shoulders and hips
    /// so each end drops or rises to its own pair's ground.
    ///
    /// **Floor and lock** are the humanoid's: each foot's contacts (the paw bone and its tip)
    /// may not go below where they rest in the idle, and one the clip brings within
    /// <see cref="lockHeight"/> of the ground is eased onto it. Unlike the humanoid, each
    /// contact is probed where it stands rather than under the ankle: a horse's hoof reaches
    /// 14cm ahead of the joint it hangs from, and measured on the island's hillside the ground
    /// under its toe stood 12cm above the ground under that joint.
    ///
    /// **Each paw has its own rest height.** The wolf was fitted onto the collie's skeleton, and
    /// its idle plants the hind paws on the floor but holds the front two 4.3cm above it. With
    /// one rest height for all four, the front paws read as lifted and stayed in the air; with
    /// one each, what the idle calls planted is put on the ground.
    ///
    /// **The body is eased; the paws are not - and nothing is eased against the root.** A
    /// navmesh agent's root jumps: the deer's dropped 14cm in five frames crossing a navmesh
    /// polygon edge, with the terrain flat under it. Easing heights measured *from the root*
    /// turns that into a moving ground that the body and paws trail into the hill (24cm under,
    /// walking). So the body's height is eased as a world height, measured against the root
    /// afresh each frame. The paws read the ground raw: it is continuous under them already,
    /// and easing it lags a paw sweeping across a slope as the animal turns.
    ///
    /// **A navmesh agent's root can float.** The deer, collie and wolves stand on the navmesh,
    /// which on a slope sat 11-19cm off the terrain where this was tested. That gap is found
    /// under the root and taken up by moving the whole body down, not by the legs, which on
    /// a collie are 28cm long.
    ///
    /// Sizes are authored at scale one, measured off each animal's leg by the builder, and
    /// multiplied by the model's scale at run time - the wolf pup is the wolf at a smaller
    /// scale and carries the wolf's numbers.
    ///
    /// Runs in the kit's IK slot and puts back the animated pose wherever the animation did
    /// not write it again - per property, because on these rigs the animation never writes the
    /// body's position at all (see <see cref="RestoreWhatWasNotAnimated"/>). Purely
    /// cosmetic and local. Off (faded) while airborne or swimming; a dead animal is laid on
    /// the ground under it instead, the body moved and tipped onto it with the legs left as the
    /// clip has them (<see cref="LegIK.LayOnGround"/>). A ridden horse keeps it; its
    /// rider is glued to the pitched body by <see cref="MountAnimator"/>, which runs after this.
    /// </summary>
    [RequireComponent(typeof(Animator))]
    [DisallowMultipleComponent]
    public class QuadrupedFootIK : MonoBehaviour, IManagedLateUpdate
    {
        [System.Serializable]
        public class Leg
        {
            [Tooltip("The upper bone of the two the solve bends.")]
            public Transform upper;
            [Tooltip("The lower bone of the two the solve bends.")]
            public Transform lower;
            [Tooltip("The bottom of the leg chain: the point the solve moves.")]
            public Transform end;
            [Tooltip("The bone the paw or hoof hangs from. Moved with the end of the leg and tipped onto the ground.")]
            public Transform foot;
            [Tooltip("Points on the paw or hoof that must not go below the ground.")]
            public Transform[] contacts = new Transform[0];
            [Tooltip("A front leg. The body pitches between the ground under the front pair and under the hind pair.")]
            public bool front;
            [Tooltip("Which way the middle joint bulges when the leg is dead straight: along the body's forward, or back. " +
                     "Read off the idle by the builder.")]
            public bool bendsForward;
            [Tooltip("Height of this paw's lowest contact above the model's origin while planted in the idle, at scale one. " +
                     "Per paw, because a retargeted clip need not plant all four at the same height.")]
            public float restHeight;

            // The highest ground under the paw as a world height, this frame's paw shift, the
            // ground's normal, and how far the animation holds the paw off the ground.
            [System.NonSerialized] public float groundY;
            [System.NonSerialized] public float shift;
            [System.NonSerialized] public Vector3 normal = Vector3.up;
            [System.NonSerialized] public float clearance;
            [System.NonSerialized] public Quaternion tilt = Quaternion.identity;
            [System.NonSerialized] public Vector3 endAnimated;
            [System.NonSerialized] public Vector3 footAnimated;
            [System.NonSerialized] public Quaternion footRotationAnimated;
            // The animated local pose, and what this wrote over it - see RestoreWhatWasNotAnimated.
            [System.NonSerialized] public Quaternion upperLocal, lowerLocal, footLocalRotation;
            [System.NonSerialized] public Vector3 footLocalPosition;
            [System.NonSerialized] public Quaternion upperWritten, lowerWritten, footRotationWritten;
            [System.NonSerialized] public Vector3 footPositionWritten;
        }

        [Tooltip("The bone the whole body hangs from - spine, head and every leg chain. Pitched to the slope.")]
        public Transform body;

        public Leg[] legs = new Leg[0];

        [Tooltip("A foot the animation brings within this height of the ground is eased onto it, at scale one.")]
        public float lockHeight = 0.02f;

        [Tooltip("How far above the root's plane a foot may be lifted to meet the ground, at scale one.")]
        public float maxStepUp = 0.2f;

        [Tooltip("How far each end of the body may drop so its feet reach ground below the root's plane, at scale one.")]
        public float maxStepDown = 0.25f;

        [Tooltip("How far the body may rise, for a root a little below the ground (a navmesh agent), at scale one.")]
        public float maxBodyRaise = 0.05f;

        [Tooltip("The steepest the body is tipped to follow a slope, in degrees.")]
        [Range(0f, 45f)]
        public float maxPitch = 25f;

        [Tooltip("Height the animation must lift a foot above planted before its tilt has fully faded out, at scale one.")]
        public float liftFade = 0.06f;

        [Tooltip("The steepest ground a foot is tipped to match, in degrees.")]
        [Range(0f, 60f)]
        public float maxFootTilt = 30f;

        [Tooltip("How quickly the body follows the ground, per second.")]
        public float bodySharpness = 10f;

        [Tooltip("How quickly each paw's tilt follows the slope under it, per second.")]
        public float footSharpness = 20f;

        [Tooltip("How quickly the whole effect fades in and out, per second.")]
        public float fadeSpeed = 5f;

        [Tooltip("No IK beyond this distance from the camera.")]
        public float maxCameraDistance = 40f;

        private BaseGameEntity _entity;
        private BaseCharacterEntity _character;
        private bool _valid;
        private bool _registered;

        /// <summary>How far a root may stand off the ground under it and still be pulled onto it, in metres.</summary>
        private const float MaxRootGap = 0.3f;

        private float _weight;
        private float _pitch;
        private float _height;
        private float _rootGap;
        private float _rootGroundY;
        private float _bodyGroundY;
        private Vector3 _corpseNormal = Vector3.up;
        private readonly float[] _grounds = new float[4];
        private readonly float[] _animated = new float[4];
        private Vector3 _bodyLocalPosition;
        private Quaternion _bodyLocalRotation;
        private Vector3 _bodyWrittenPosition;
        private Quaternion _bodyWrittenRotation;
        private bool _hasWritten;

        private void Awake()
        {
            _entity = GetComponentInParent<BaseGameEntity>();
            _character = _entity as BaseCharacterEntity;
            _valid = !Application.isBatchMode && _entity != null && body != null && legs != null && legs.Length > 0;
            if (_valid)
            {
                foreach (Leg leg in legs)
                {
                    if (leg == null || leg.upper == null || leg.lower == null || leg.end == null || leg.foot == null)
                        _valid = false;
                }
            }
            if (!_valid)
                enabled = false;
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
            // Switched off, the animation would take back everything but the body's position,
            // which it never writes - so a body left dropped would stay dropped.
            RestoreWhatWasNotAnimated();
            _weight = _pitch = _height = _rootGap = 0f;
        }

        public void ManagedLateUpdate()
        {
            if (!_entity.enabled)
                return;

            RestoreWhatWasNotAnimated();

            float deltaTime = Time.deltaTime;
            bool dead = _character != null && _character.IsDead();
            if (!dead)
                _corpseNormal = Vector3.up;
            bool active = dead
                ? LegIK.ShouldGroundCorpse(_entity, _character, transform, maxCameraDistance)
                : LegIK.ShouldBeActive(_entity, _character, transform, maxCameraDistance);
            // Coming back from off (or first run): take the ground as it is rather than
            // easing toward it from wherever it was last seen.
            bool fresh = _weight <= 0f;
            _weight = Mathf.MoveTowards(_weight, active ? 1f : 0f, fadeSpeed * deltaTime);
            if (_weight <= 0f)
            {
                _pitch = _height = _rootGap = 0f;
                _hasWritten = false;
                return;
            }

            Keep();

            Vector3 root = _entity.EntityTransform.position;
            float rootY = root.y;
            float scale = transform.lossyScale.y;
            int mask = GameInstance.Singleton != null ? GameInstance.Singleton.GetGameEntityGroundDetectionLayerMask() : Physics.DefaultRaycastLayers;
            float footBlend = 1f - Mathf.Exp(-footSharpness * deltaTime);
            float bodyBlend = 1f - Mathf.Exp(-bodySharpness * deltaTime);

            if (dead)
            {
                // A body, not a stance: the legs keep the clip's pose and the body carries all
                // of it onto the ground, looking as far as a navmesh root can stand off it.
                LegIK.LayOnGround(body, rootY, MaxRootGap + maxStepUp * scale, MaxRootGap + maxStepDown * scale,
                    maxPitch, mask, bodyBlend, _weight, fresh, ref _bodyGroundY, ref _corpseNormal);
                _pitch = _height = _rootGap = 0f;
                RecordWritten();
                return;
            }

            // How far the root stands off the ground under it. A navmesh agent stands on the
            // navmesh, which on a slope can sit 10-20cm off the terrain - more than a collie's
            // legs can make up. That gap moves the whole body, not the legs.
            float groundUnderRoot = rootY;
            if (Physics.Raycast(root + Vector3.up * MaxRootGap, Vector3.down, out RaycastHit rootHit, MaxRootGap * 2f, mask, QueryTriggerInteraction.Ignore))
                groundUnderRoot = rootHit.point.y;
            _rootGroundY = LegIK.Follow(_rootGroundY, groundUnderRoot, bodyBlend, fresh);
            _rootGap = _rootGroundY - rootY;

            // Everything read off the animated pose, before anything moves.
            float front = float.MaxValue, back = float.MaxValue;
            Vector3 frontFeet = Vector3.zero, backFeet = Vector3.zero;
            Vector3 frontPivot = Vector3.zero, backPivot = Vector3.zero;
            int frontCount = 0, backCount = 0;
            foreach (Leg leg in legs)
            {
                leg.endAnimated = leg.end.position;
                leg.footAnimated = leg.foot.position;
                leg.footRotationAnimated = leg.foot.rotation;
                PlaceFoot(leg, rootY, scale, mask, footBlend);
                if (leg.front)
                {
                    front = Mathf.Min(front, leg.groundY - rootY);
                    frontFeet += leg.footAnimated;
                    frontPivot += leg.upper.position;
                    ++frontCount;
                }
                else
                {
                    back = Mathf.Min(back, leg.groundY - rootY);
                    backFeet += leg.footAnimated;
                    backPivot += leg.upper.position;
                    ++backCount;
                }
            }

            // Tip the body so each end sits over its own pair's ground: the pitch is the rise
            // between the two pairs of feet over the distance between them, turned about the
            // point between shoulders and hips.
            Vector3 forward = transform.forward;
            if (frontCount > 0 && backCount > 0)
            {
                frontFeet /= frontCount;
                backFeet /= backCount;
                frontPivot /= frontCount;
                backPivot /= backCount;
                Vector3 span = frontFeet - backFeet;
                span.y = 0f;
                float length = span.magnitude;
                if (length > 0.01f)
                {
                    forward = span / length;
                    // The body is what is eased, not the paws. Its height as a world height, so
                    // a root jump is not read as the ground moving; its pitch as an angle, which
                    // a root jump does not change.
                    float pitchTarget = Mathf.Clamp(Mathf.Atan2(front - back, length) * Mathf.Rad2Deg, -maxPitch, maxPitch);
                    float heightTarget = Mathf.Clamp((front + back) * 0.5f, _rootGap - maxStepDown * scale, _rootGap + maxBodyRaise * scale);
                    _pitch = fresh ? pitchTarget : Mathf.Lerp(_pitch, pitchTarget, bodyBlend);
                    _bodyGroundY = LegIK.Follow(_bodyGroundY, rootY + heightTarget, bodyBlend, fresh);
                    _height = _bodyGroundY - rootY;
                    // About cross(forward, up), a positive angle turns forward toward up: nose up.
                    body.RotateAround((frontPivot + backPivot) * 0.5f, Vector3.Cross(forward, Vector3.up), _pitch * _weight);
                    body.position += Vector3.up * (_height * _weight);
                }
            }

            foreach (Leg leg in legs)
                Solve(leg, forward);

            RecordWritten();
        }

        /// <summary>What this frame left in the bones, for <see cref="RestoreWhatWasNotAnimated"/>.</summary>
        private void RecordWritten()
        {
            _bodyWrittenPosition = body.localPosition;
            _bodyWrittenRotation = body.localRotation;
            foreach (Leg leg in legs)
            {
                leg.upperWritten = leg.upper.localRotation;
                leg.lowerWritten = leg.lower.localRotation;
                leg.footPositionWritten = leg.foot.localPosition;
                leg.footRotationWritten = leg.foot.localRotation;
            }
            _hasWritten = true;
        }

        private void Keep()
        {
            _bodyLocalPosition = body.localPosition;
            _bodyLocalRotation = body.localRotation;
            foreach (Leg leg in legs)
            {
                leg.upperLocal = leg.upper.localRotation;
                leg.lowerLocal = leg.lower.localRotation;
                leg.footLocalPosition = leg.foot.localPosition;
                leg.footLocalRotation = leg.foot.localRotation;
            }
        }

        /// <summary>
        /// Puts back the animated value of everything this wrote last frame that nothing has
        /// written since - property by property, never all or nothing.
        ///
        /// Two things leave a value standing. The animation LOD skips whole frames, as for
        /// <see cref="HumanoidFootIK"/>. And **the animation never writes <c>Body</c>'s position at
        /// all**: <c>Body</c> is these rigs' root node, so the Animator takes its position curve
        /// as root motion and, with root motion off, drops it - while still writing its
        /// rotation. An all-or-nothing check sees the rotation change, decides the frame was
        /// animated, restores nothing, and the body shift is then added on top of last frame's
        /// every frame. That sank the collie's body a third of a metre into the ground before
        /// it was found. A value still exactly as this left it was not animated since, so it
        /// is the one to restore.
        /// </summary>
        private void RestoreWhatWasNotAnimated()
        {
            if (!_hasWritten)
                return;
            _hasWritten = false;
            if (body.localPosition == _bodyWrittenPosition)
                body.localPosition = _bodyLocalPosition;
            if (body.localRotation == _bodyWrittenRotation)
                body.localRotation = _bodyLocalRotation;
            foreach (Leg leg in legs)
            {
                if (leg.upper.localRotation == leg.upperWritten)
                    leg.upper.localRotation = leg.upperLocal;
                if (leg.lower.localRotation == leg.lowerWritten)
                    leg.lower.localRotation = leg.lowerLocal;
                if (leg.foot.localPosition == leg.footPositionWritten)
                    leg.foot.localPosition = leg.footLocalPosition;
                if (leg.foot.localRotation == leg.footRotationWritten)
                    leg.foot.localRotation = leg.footLocalRotation;
            }
        }

        /// <summary>
        /// Where this paw goes. Each contact is probed where it actually is - a hoof is long,
        /// and on a hillside the ground under its toe can be 10cm above the ground under its
        /// heel - and where it will be once the paw is tipped onto the slope. The paw then
        /// rises by whatever the most demanding contact needs to stand out of the floor, with
        /// the lock easing a nearly planted contact the rest of the way down.
        /// </summary>
        private void PlaceFoot(Leg leg, float rootY, float scale, int mask, float blend)
        {
            float rest = leg.restHeight * scale;
            float lockRange = lockHeight * scale;
            float up = maxStepUp * scale;
            float reach = up + maxStepDown * scale + MaxRootGap;
            float highestGround = float.MinValue;
            float lowestClearance = float.MaxValue;
            Vector3 normal = Vector3.zero;
            int hits = 0;
            int count = leg.contacts != null ? leg.contacts.Length : 0;
            float[] grounds = count <= _grounds.Length ? _grounds : new float[count];
            float[] animated = count <= _animated.Length ? _animated : new float[count];
            for (int i = 0; i < count; ++i)
            {
                Transform contact = leg.contacts[i];
                grounds[i] = float.NaN;
                if (contact == null)
                    continue;
                Vector3 at = contact.position;
                animated[i] = at.y - rootY - rest;
                lowestClearance = Mathf.Min(lowestClearance, animated[i]);
                // Probed where the contact will be once tipped (by last frame's tilt, which is
                // this frame's while standing), not where the clip has it: the tip swings a
                // hoof's toe several centimetres along the ground, and on the 30-degree bump the
                // horse was tested on that put its toe 6cm into the hill.
                Vector3 probe = leg.footAnimated + leg.tilt * (at - leg.footAnimated);
                Vector3 origin = new Vector3(probe.x, rootY + _rootGap + up, probe.z);
                if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, reach, mask, QueryTriggerInteraction.Ignore))
                {
                    // Raw, not smoothed: the terrain under a moving paw is continuous already,
                    // and smoothing it lags a paw that is sweeping across a slope - a deer
                    // turning on the spot swung its hind paw half a metre across the hill and
                    // the smoothed ground buried it 35cm for eight frames.
                    grounds[i] = hit.point.y - rootY;
                    highestGround = Mathf.Max(highestGround, grounds[i]);
                    normal += hit.normal;
                    ++hits;
                }
            }
            if (hits == 0)
            {
                // Nothing in reach - over an edge, or a leg with no contacts. Leave the paw
                // where the animation has it, on the ground under the root.
                highestGround = _rootGap;
                normal = Vector3.up;
            }
            if (lowestClearance == float.MaxValue)
                lowestClearance = 0f;

            leg.normal = Vector3.Slerp(leg.normal, normal.normalized, blend);
            leg.clearance = LegIK.Lock(lowestClearance, lockRange);
            float plantedness = 1f - Mathf.Clamp01(leg.clearance / Mathf.Max(liftFade * scale, 0.001f));
            leg.tilt = LegIK.Tilt(leg.normal, maxFootTilt, plantedness * _weight);

            // How far must the paw move for every contact to stand at its locked height above
            // the ground under it, once the paw is tipped? The most demanding one decides.
            //
            // Planted or lifted is read off the pose *before* the tip, which is what the
            // animation means; the tip only moves the contacts, and that is undone here. The
            // tip turns the paw about the wrist control, well behind and above the pads, so on
            // a 19-degree slope it lifted the wolf's pads 4-6cm - and judged after the tip, a
            // planted paw read as a lifted one and was left standing in the air.
            float shift = float.MinValue;
            for (int i = 0; i < count; ++i)
            {
                Transform contact = leg.contacts[i];
                if (contact == null)
                    continue;
                float ground = float.IsNaN(grounds[i]) ? _rootGap : grounds[i];
                Vector3 tipped = leg.footAnimated + leg.tilt * (contact.position - leg.footAnimated);
                float tippedClearance = tipped.y - rootY - rest;
                shift = Mathf.Max(shift, ground + LegIK.Lock(animated[i], lockRange) - tippedClearance);
            }
            if (shift == float.MinValue)
                shift = highestGround;

            leg.groundY = rootY + highestGround;
            leg.shift = shift;
        }

        private void Solve(Leg leg, Vector3 forward)
        {
            Vector3 target = leg.endAnimated + Vector3.up * (leg.shift * _weight);
            LegIK.SolveTwoBone(leg.upper, leg.lower, leg.end, target, leg.bendsForward ? forward : -forward);

            // The paw follows the leg by what the leg actually did, so the two never part.
            leg.foot.position = leg.footAnimated + (leg.end.position - leg.endAnimated);
            leg.foot.rotation = leg.tilt * leg.footRotationAnimated;
        }
    }
}
