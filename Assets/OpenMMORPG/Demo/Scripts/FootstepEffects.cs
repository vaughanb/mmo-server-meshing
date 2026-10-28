using Insthync.ManagedUpdating;
using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// What a body leaves behind it on the beach and stirs up in the sea: boot prints in the sand, and
    /// splashes, ripples and a wake in the water. Everything is drawn by the scene's shared
    /// <see cref="FootstepEffectsHub"/>; this decides where and how hard.
    ///
    /// **It reads the feet, not the footstep sound.** The kit's footstep sound is a timer
    /// (`stepDelay / MoveAnimationSpeedMultiplier`) that knows nothing of where the feet are, so a print
    /// laid on it would land beside the foot, and a splash would fire with the foot in the air. Instead
    /// each foot's three sole contact points (the ones <see cref="HumanoidFootIK"/> measures off the
    /// idle, so a sole is the same thing to both) are read after the IK has run, and a foot **plants**
    /// the moment the nearest of them comes within a couple of centimetres of the ground it is over, and
    /// lifts again when it is clear by twice that. Because it is the pose that is read, the same events fall
    /// out of a walk, a jog, a strafe, a crouch, a landing and another player's synced animation, with
    /// nothing networked.
    ///
    /// **Clearance is measured across the slope, not straight down** (fixed 2026-10-05, "double footprints
    /// with each step"). It was first the world-lowest contact point against the ground under it. Going
    /// downhill that point flips from heel to toe as the foot rolls, and the ground under the toe is a few
    /// centimetres lower, so one footfall read as plant, lift, plant: each foot printed twice, 9-32 cm apart
    /// and a tenth of a second apart, measured on the beach. Now one ray under the middle of the sole gives
    /// the ground's plane, and each point's height is taken along its normal. And a foot does not print
    /// again until it has moved <see cref="restepDistance"/> from where it last planted: a real step carries
    /// the same foot well over half a metre, so this only ever stops a stumble printing twice.
    ///
    /// **A plant has to hold.** The jog on the beach's downhill also brushes the sand once before it lands:
    /// the heel dips to a centimetre while the foot is still sweeping back at several metres a second, lifts
    /// 10-14 cm, and comes down for real 30 cm further back - the second print of a pair, and past the re-step
    /// distance. That touch lasts a couple of frames; a stance lasts ten and more. So a foot is only taken as
    /// planted once it has stayed down for <see cref="plantConfirm"/>, and the print is laid from the pose it
    /// has settled into then rather than the one it first touched with.
    ///
    /// **The jog's footfall is two contacts** (found 2026-10-06 putting the footstep sound on this event, which
    /// doubled it): replayed offline on both `Jog_Fwd` clips with the IK off, and measured live, a jog gives four
    /// plants a cycle, not two - `L 0.94 L 1.14 R 1.41 R 1.60` - because the heel touches at a centimetre while the
    /// foot is still skating back over the ground at 2 m/s, rises to 7 cm, and the ball and toe land 0.2 s later.
    /// Neither contact is a still stance, so no height or hold separates them, and they are further apart than
    /// <see cref="restepDistance"/> once the body has moved. The same foot's real steps are never closer than a
    /// sprint's 0.67 s, so <see cref="minStepInterval"/> keeps the first contact (the heel strike, where a footstep
    /// sounds) and drops the second. The sprint, with one clean contact a step, was always exact.
    ///
    /// **The beach.** A planted foot over terrain asks the splat map how much of the sand layer is
    /// under it; that is the print's strength, so a print fades out where the sand gives way to grass
    /// rather than stopping at a line. Sand near the sea is wet, and so are the boots of anyone who has
    /// just been in it (a soaked foot leaves wet prints for a few steps): wet prints are darker, and in the
    /// swash zone at the water's edge they are washed away in seconds. A hard run kicks sand up.
    /// Elsewhere - grass, earth, stone, a floor - nothing is left.
    ///
    /// **The sea is where it is drawn.** The physics sea is a flat trigger volume, but the shader moves the
    /// drawn surface up and down by up to 18 cm, which on the beach carries the waterline a metre either way.
    /// Judged against the flat level, a foot on sand the swell had just uncovered was "in the water" and
    /// splashed (fixed 2026-10-05). So every wet-or-dry question asks <see cref="StylizedWaterSurface"/> for
    /// the drawn height at that spot, off <see cref="seaMaterial"/>. A foot going under splashes in proportion
    /// to how fast it was moving and how deep the water is (an inch of swash does nothing); a foot standing
    /// still that the swell washes over only rings, and the water leaving it again does nothing. One coming
    /// out sheds drops; one planted on the bed under water, which never leaves it at knee depth, makes a
    /// ring. A standing body in the water breathes a slow ripple. Swimming is the same thing for the
    /// hands and feet breaking the surface, plus a trail of foam and rings behind the chest and a little
    /// bow spray at speed.
    ///
    /// **Sound.** A foot going into the sea, or a step through it at knee depth, plays one of the
    /// `WaterSplash` clips there (lighter and higher for a gentle step, fuller for a run or a landing), and
    /// while a body is wading the kit's dry footstep is turned down to nothing - otherwise every step in the
    /// water was a boot on sand with a splash laid over it. The kit's footstep component gives no way to
    /// silence it (its `MuteFootstepSound` comes from buffs), but it never sets its source's volume, only
    /// its pitch and mute, so that volume is borrowed while wading and always put back. Swimming keeps the
    /// kit's `SwimStroke` sounds and makes no splash sounds of its own. Without splash clips nothing is muted.
    ///
    /// **The dry footstep is played from the plant, not the timer** (2026-10-06, "the footsteps seem to play
    /// twice for every one step"). The kit's timer fires every `stepDelay / MoveAnimationSpeedMultiplier` whatever
    /// the feet are doing, and the delays the demo wires (0.36 s jog, 0.55 walk, 0.28 sprint) are not the clips'
    /// steps (0.47, 0.67, 0.33 s), so the sound runs 20-30% fast and drifts across the footfalls: every few steps
    /// two sounds land inside one. So with <see cref="syncStepSounds"/> on, the timer is set to never fire for
    /// walking, jogging and sprinting, and <see cref="OnPlant"/> - the one event per foot per step the prints
    /// already rely on, debounced and strictly alternating - calls the kit's own `PlaySound`. Its clips, volume,
    /// pitch spread, mute rules and the wading mute are all still the kit's. Swimming (no plants), crawling
    /// (hands and knees) and crouching (see <see cref="SilenceStepTimer"/>) keep the timer, and a body this
    /// component does not run on keeps it too.
    ///
    /// Cosmetic and local, so a remote player shows exactly what their pose says. Off for a headless
    /// server, a menu preview (the entity is disabled), the dead, riders, climbers, and anyone beyond
    /// <see cref="maxCameraDistance"/>. Runs in the managed late update just after the kit's IK slot, so
    /// it reads the feet where they were finally put. Belongs on the player entity beside its
    /// <see cref="SurfaceSwimmer"/>; added by `Build Footstep Effects` and by the entity builder.
    /// </summary>
    [DisallowMultipleComponent]
    public class FootstepEffects : MonoBehaviour, IManagedLateUpdate
    {
        [Header("Drawing")]
        [Tooltip("The shared particle systems. Instantiated once per scene by the first character to need it.")]
        public FootstepEffectsHub hubPrefab;

        [Header("Ground")]
        [Tooltip("World height the sea rests at (DemoIslandBuilder.WaterLevel).")]
        public float seaLevel = 0f;

        [Tooltip("The sea's material (Demo/StylizedWater). Its swell decides where the water actually is; without it the sea is flat at the sea level.")]
        public Material seaMaterial;

        [Tooltip("Name of the terrain layer that takes a print.")]
        public string printLayer = "Island_Sand";

        [Tooltip("Sand this many metres above the sea is dry; below, it is wet, and darker for it.")]
        public float wetBand = 1.2f;

        [Tooltip("Sand this close above the sea is washed by the surf: prints there last seconds.")]
        public float swashBand = 0.25f;

        [Header("Footprints (lengths at model scale one)")]
        [Tooltip("The print's quad, which is bigger than the boot by the rim of sand pushed up round it.")]
        public float printLength = 0.36f;
        public float printWidth = 0.17f;

        [Tooltip("Print colour on dry and on wet sand: the sand's own, so the rim melts into it and the depth is the lighting's work. Lit, so it darkens with the evening.")]
        public Color dryTint = new Color(0.72f, 0.66f, 0.50f);
        public Color wetTint = new Color(0.46f, 0.40f, 0.29f);

        [Range(0f, 1f)] public float dryOpacity = 0.9f;
        [Range(0f, 1f)] public float wetOpacity = 0.95f;

        [Tooltip("Seconds a print lasts on dry sand, on wet sand, and in the surf.")]
        public float dryLife = 30f;
        public float wetLife = 20f;
        public float swashLife = 6f;

        [Tooltip("A foot whose sole comes within this height of the ground has planted. Metres at model scale one.")]
        public float plantHeight = 0.025f;

        [Tooltip("... and has lifted again once it is clear of the ground by this.")]
        public float liftHeight = 0.05f;

        [Tooltip("A foot does not plant again until it is this far from where it last planted. Metres at model scale one.")]
        public float restepDistance = 0.3f;

        [Tooltip("Seconds a foot must stay down before it counts as planted: longer than a passing brush, shorter than a sprint's stance.")]
        public float plantConfirm = 0.05f;

        [Tooltip("The same foot does not step again within this many seconds of its last step. The jog's footfall is two contacts about 0.2 s apart (the heel, then the ball after the foot has skated back and lifted); the same foot's real steps are 0.67 s apart at a sprint and more at every other gait.")]
        public float minStepInterval = 0.35f;

        [Tooltip("How many prints a soaked boot leaves wet, coming out of the sea.")]
        public int wetPrints = 6;

        [Tooltip("Body speed above which a planting foot kicks the sand up, in metres a second.")]
        public float sandKickSpeed = 3.2f;

        [Header("Water")]
        [Tooltip("Water shallower than this under a foot makes no splash: the film of the swash is nearly invisible.")]
        public float splashDepth = 0.03f;

        [Tooltip("A foot moving slower than this when the water reaches it is standing in the swell, not stepping into it: a ring, no splash.")]
        public float stillFoot = 0.5f;

        [Tooltip("Seconds between the slow ripples of someone standing still in the water.")]
        public float idleRippleEvery = 1.5f;

        [Header("Sound")]
        [Tooltip("Played where a foot goes into the sea or steps through it (the WaterSplash family, written by Wire Audio). Empty: silent, and the dry footstep is left alone.")]
        public AudioClip[] splashClips = new AudioClip[0];

        [Range(0f, 1f)]
        [Tooltip("Volume of the hardest splash; a gentle step plays at under half of it.")]
        public float splashVolume = 0.6f;

        [Tooltip("Metres out to which a splash plays at full volume.")]
        public float splashNear = 4f;

        [Tooltip("Metres beyond which a splash is inaudible.")]
        public float splashFar = 35f;

        [Tooltip("Turn the kit's dry footstep down while wading, so a step through the water is a splash and not a boot on sand.")]
        public bool muteDryStepsInWater = true;

        [Tooltip("Play the kit's dry footstep when a foot actually lands, and stop its timer for walking, jogging and sprinting. Off: the kit's timer, which is not tied to the feet.")]
        public bool syncStepSounds = true;

        /// <summary>
        /// Raised for every step this component takes - a foot that has come down and held, and is not a
        /// stumble on the spot it just left. The argument is whether it was the left foot. Prints and the dry
        /// footstep both come from this moment.
        /// </summary>
        public event System.Action<bool> onStep;

        [Header("Cost")]
        [Tooltip("No effects beyond this distance from the camera.")]
        public float maxCameraDistance = 35f;

        private class Foot
        {
            public bool left;
            public Transform bone;
            public Transform toes;
            public bool hasContacts;
            public Vector3 heel, ball, toe;

            // For quadrupeds:
            public Transform contactHeel;
            public Transform contactToe;
            public float printLengthScale = 1f;
            public float printWidthScale = 1f;

            public bool planted;
            // Down, but not yet for long enough to count: see plantConfirm.
            public bool pending;
            public float plantedAt;
            public bool submerged;
            public Vector3 last;
            public bool hasLast;
            public float lastWater = -10f;
            public float lastSplash = -10f;
            public Vector3 lastPlant;
            public bool hasPlant;
            public float lastStep = -10f;
        }

        private class Hand
        {
            public Transform bone;
            public bool submerged;
            public Vector3 last;
            public bool hasLast;
            public float lastWater = -10f;
        }

        private BaseGameEntity _entity;
        private BaseCharacterEntity _character;
        private Animator _animator;
        private Transform _hips;
        private Foot[] _feet;
        private bool _isQuadruped;
        private Foot _left;
        private Foot _right;
        private Hand _handLeft;
        private Hand _handRight;
        private FootstepEffectsHub _hub;
        private bool _valid;
        private bool _registered;
        private bool _primed;

        private Vector3 _lastRoot;
        private Vector3 _moveDirection = Vector3.forward;
        private float _speed;
        // 0 to 1: how soaked the boots are. Full on leaving the sea, one print's worth less per print.
        private float _soaked;
        private float _airborneSince = -1f;
        private float _impactUntil = -1f;
        private float _wakeTimer;
        private float _wakeRingTimer;
        private float _bowTimer;
        private float _idleTimer;
        private float _lastSplashSound = -10f;
        // The kit's footstep source, and the volume it had before wading turned it down.
        private AudioSource _drySteps;
        private float _dryStepsVolume = 1f;
        private bool _drySilenced;
        private bool _wading;
        // The kit's footstep component, and the delays its timer had before this took the ground gaits over.
        private CharacterFootstepSoundComponent _kitSteps;
        private FootstepSettings[] _timed;
        private float[] _timedDelays;
        private bool _timerSilenced;

        private void Awake()
        {
            _entity = GetComponent<BaseGameEntity>();
            _character = _entity as BaseCharacterEntity;
            if (Application.isBatchMode || _entity == null || hubPrefab == null)
            {
                enabled = false;
                return;
            }

            Transform model = null;
            var manager = GetComponent<CharacterModelManager>();
            if (manager != null && manager.MainTpsModel != null)
                model = manager.MainTpsModel.transform;
            if (model == null)
                model = transform.Find("Model");
            _animator = model != null ? model.GetComponent<Animator>() : null;
            if (_animator == null && model != null)
                _animator = model.GetComponentInChildren<Animator>();
            if (_animator == null)
                _animator = GetComponentInChildren<Animator>();
            if (_animator == null)
            {
                enabled = false;
                return;
            }

            if (_animator.isHuman)
            {
                _isQuadruped = false;
                _hips = _animator.GetBoneTransform(HumanBodyBones.Hips);
                HumanoidFootIK ik = _animator.GetComponent<HumanoidFootIK>();
                _left = MakeFoot(true, HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes, ik);
                _right = MakeFoot(false, HumanBodyBones.RightFoot, HumanBodyBones.RightToes, ik);
                _feet = new[] { _left, _right };
                Transform leftHand = _animator.GetBoneTransform(HumanBodyBones.LeftHand);
                Transform rightHand = _animator.GetBoneTransform(HumanBodyBones.RightHand);
                _handLeft = leftHand != null ? new Hand { bone = leftHand } : null;
                _handRight = rightHand != null ? new Hand { bone = rightHand } : null;
                _valid = _left != null && _right != null;
            }
            else
            {
                _isQuadruped = true;
                syncStepSounds = false;
                QuadrupedFootIK qik = _animator.GetComponent<QuadrupedFootIK>();
                if (qik == null)
                    qik = GetComponentInChildren<QuadrupedFootIK>();

                if (qik != null && qik.legs != null && qik.legs.Length > 0)
                {
                    _hips = qik.body != null ? qik.body : _animator.transform;

                    float lenScale = 0.45f;
                    float widthScale = 0.70f;
                    string n = gameObject.name.ToLower();
                    if (n.Contains("horse")) { lenScale = 0.55f; widthScale = 0.95f; }
                    else if (n.Contains("deer")) { lenScale = 0.35f; widthScale = 0.55f; }
                    else if (n.Contains("pup")) { lenScale = 0.25f; widthScale = 0.45f; }
                    else if (n.Contains("wolf")) { lenScale = 0.38f; widthScale = 0.65f; }

                    var feetList = new System.Collections.Generic.List<Foot>();
                    for (int i = 0; i < qik.legs.Length; i++)
                    {
                        var leg = qik.legs[i];
                        if (leg == null) continue;
                        bool left = (i % 2 == 0);
                        var f = new Foot
                        {
                            left = left,
                            bone = leg.foot != null ? leg.foot : leg.end,
                            toes = leg.foot != null ? leg.foot : leg.end,
                            printLengthScale = lenScale,
                            printWidthScale = widthScale,
                        };
                        if (leg.contacts != null && leg.contacts.Length >= 2)
                        {
                            f.contactHeel = leg.contacts[0];
                            f.contactToe = leg.contacts[1];
                        }
                        else if (leg.contacts != null && leg.contacts.Length == 1)
                        {
                            f.contactHeel = leg.contacts[0];
                            f.contactToe = leg.contacts[0];
                        }
                        feetList.Add(f);
                    }
                    _feet = feetList.ToArray();
                    if (_feet.Length >= 2)
                    {
                        _left = _feet[0];
                        _right = _feet[1];
                    }
                    else if (_feet.Length == 1)
                    {
                        _left = _feet[0];
                        _right = _feet[0];
                    }
                    _valid = _feet.Length > 0;
                }
            }

            if (!_valid)
                enabled = false;
        }

        private Foot MakeFoot(bool left, HumanBodyBones foot, HumanBodyBones toes, HumanoidFootIK ik)
        {
            Transform bone = _animator.GetBoneTransform(foot);
            if (bone == null)
                return null;
            Transform toeBone = _animator.GetBoneTransform(toes);
            var result = new Foot { left = left, bone = bone, toes = toeBone != null ? toeBone : bone };
            // The contact points the IK measured off this body's idle: the heel and ball in the foot
            // bone's space, the toe tip in the toe bone's. Without them the ankle stands in.
            if (ik != null && ik.hasContacts)
            {
                result.hasContacts = true;
                result.heel = left ? ik.leftHeel : ik.rightHeel;
                result.ball = left ? ik.leftBall : ik.rightBall;
                result.toe = left ? ik.leftToe : ik.rightToe;
            }
            return result;
        }

        private void OnEnable()
        {
            if (!_valid || _registered)
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
            _primed = false;
            SilenceDrySteps(false);
            SilenceStepTimer(false);
        }

        public void ManagedLateUpdate()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f)
                return;
            // Here, not in OnEnable: the kit's component settles its settings in its own Start, which runs after.
            SilenceStepTimer(syncStepSounds);
            if (!ShouldRun())
            {
                _primed = false;
                SilenceDrySteps(false);
                return;
            }

            Vector3 root = _entity.EntityTransform.position;
            // First frame, or a teleport: take things as they are and make no events from the jump.
            if (!_primed || (root - _lastRoot).sqrMagnitude > 4f)
            {
                Prime(root);
                return;
            }

            Vector3 flat = root - _lastRoot;
            flat.y = 0f;
            _lastRoot = root;
            _speed = Mathf.Lerp(_speed, flat.magnitude / dt, 1f - Mathf.Exp(-10f * dt));
            if (flat.sqrMagnitude > 1e-6f)
                _moveDirection = Vector3.Lerp(_moveDirection, flat.normalized, 1f - Mathf.Exp(-8f * dt)).normalized;

            _hub = FootstepEffectsHub.Get(hubPrefab);
            if (_hub == null)
                return;

            MovementState state = _entity.MovementState;
            bool swimming = (state & MovementState.IsUnderWater) != 0;
            TrackImpact(state, swimming);

            float scale = Mathf.Max(0.1f, _animator.transform.lossyScale.y);
            int mask = GameInstance.Singleton != null ? GameInstance.Singleton.GetGameEntityGroundDetectionLayerMask() : Physics.DefaultRaycastLayers;

            if (_feet != null)
            {
                for (int i = 0; i < _feet.Length; i++)
                {
                    if (_feet[i] != null)
                        StepFoot(_feet[i], swimming, scale, dt, mask);
                }
            }
            TrackWading(root, swimming);
            if (swimming)
            {
                StepHand(_handLeft, dt);
                StepHand(_handRight, dt);
                Wake(root, scale, dt);
            }
            else
            {
                Standing(root, dt);
            }
        }

        private bool ShouldRun()
        {
            if (_entity == null || !_entity.enabled)
                return false;
            if (!_entity.PassengingVehicleEntity.IsNull())
                return false;
            if (_character != null)
            {
                if (_character.IsDead())
                    return false;
                if (_character.LadderComponent != null && _character.LadderComponent.ClimbingLadder != null)
                    return false;
            }
            if ((_entity.MovementState & MovementState.IsClimbing) != 0)
                return false;
            Camera camera = Camera.main;
            if (camera != null && (camera.transform.position - _animator.transform.position).sqrMagnitude > maxCameraDistance * maxCameraDistance)
                return false;
            return true;
        }

        /// <summary>The height of the sea's drawn surface at (x, z) now: the resting level plus the swell.</summary>
        private float SurfaceAt(float x, float z)
        {
            return seaLevel + StylizedWaterSurface.Offset(seaMaterial, x, z);
        }

        /// <summary>Takes the pose as the starting state: feet planted wherever they are, no event for anything already wet.</summary>
        private void Prime(Vector3 root)
        {
            _lastRoot = root;
            _speed = 0f;
            _airborneSince = -1f;
            _impactUntil = -1f;
            float scale = Mathf.Max(0.1f, _animator.transform.lossyScale.y);
            if (_feet != null)
            {
                for (int i = 0; i < _feet.Length; i++)
                {
                    if (_feet[i] != null)
                        PrimeFoot(_feet[i], scale);
                }
            }
            PrimeHand(_handLeft);
            PrimeHand(_handRight);
            _primed = true;
        }

        private void PrimeFoot(Foot f, float scale)
        {
            SolePoints(f, scale, out _, out _, out _, out Vector3 low);
            f.planted = true;
            f.pending = false;
            f.submerged = low.y < SurfaceAt(low.x, low.z);
            f.hasLast = false;
            f.hasPlant = false;
            f.lastStep = -10f;
        }

        private void PrimeHand(Hand h)
        {
            if (h == null)
                return;
            Vector3 p = h.bone.position;
            h.submerged = p.y < SurfaceAt(p.x, p.z);
            h.hasLast = false;
        }

        /// <summary>
        /// A landing hits harder than a step: the feet that arrive within a moment of one print and
        /// splash for more. The air time is read off the movement state, so a remote player's is the same.
        /// </summary>
        private void TrackImpact(MovementState state, bool swimming)
        {
            bool inAir = !swimming && (state & MovementState.IsGrounded) == 0;
            if (inAir)
            {
                if (_airborneSince < 0f)
                    _airborneSince = Time.time;
            }
            else if (_airborneSince >= 0f)
            {
                // Only a real drop: a hop over a rock is not a landing.
                if (Time.time - _airborneSince > 0.25f)
                    _impactUntil = Time.time + 0.25f;
                _airborneSince = -1f;
            }
        }

        private float Impact()
        {
            return Time.time < _impactUntil ? 1f : 0f;
        }

        // ---- feet ---------------------------------------------------------------------------------

        private void StepFoot(Foot f, bool swimming, float scale, float dt, int mask)
        {
            SolePoints(f, scale, out Vector3 heel, out Vector3 ball, out Vector3 toe, out Vector3 low);

            Vector3 bone = f.bone.position;
            Vector3 velocity = f.hasLast ? (bone - f.last) / dt : Vector3.zero;
            f.last = bone;
            f.hasLast = true;

            // The ground this foot is over, as a plane: one ray under the middle of the sole, then each
            // contact point's height along its normal. Not asked for in the water: a swimmer's feet are not on it.
            bool grounded = false;
            RaycastHit hit = default;
            float clearance = float.PositiveInfinity;
            if (!swimming)
            {
                Vector3 middle = (heel + toe) * 0.5f;
                Vector3 origin = new Vector3(middle.x, low.y + 0.35f * scale, middle.z);
                grounded = Physics.Raycast(origin, Vector3.down, out hit, 0.35f * scale + 0.6f, mask, QueryTriggerInteraction.Ignore);
                if (grounded)
                {
                    clearance = Mathf.Min(Above(heel, hit), Mathf.Min(Above(ball, hit), Above(toe, hit)));
                }
            }

            // Against the drawn surface. A little hysteresis, or the swell flickers a foot in and out.
            float surface = SurfaceAt(low.x, low.z);
            // How deep the water is where this foot is (a swimmer: out of its depth; no ground: none).
            float depth = swimming ? 1f : (grounded ? surface - hit.point.y : 0f);
            bool wasWet = f.submerged;
            bool isWet = wasWet ? low.y < surface + 0.012f : low.y < surface - 0.004f;
            f.submerged = isWet;
            if (isWet && (swimming || depth >= splashDepth))
                _soaked = 1f;
            if (isWet != wasWet && Time.time - f.lastWater > 0.1f)
            {
                f.lastWater = Time.time;
                if (isWet)
                    OnEnterWater(f, low, velocity, depth, surface, swimming);
                else
                    OnLeaveWater(f, low, velocity, depth, surface, swimming);
            }

            if (swimming)
            {
                f.planted = false;
                f.pending = false;
                return;
            }
            if (f.planted)
            {
                float lift = (_isQuadruped ? 0.04f : liftHeight) * scale;
                if (!grounded || clearance > lift)
                {
                    // Lifted before it had held: a brush, not a step.
                    f.planted = false;
                    f.pending = false;
                }
                else if (f.pending && Time.time - f.plantedAt >= plantConfirm)
                {
                    f.pending = false;
                    OnPlant(f, heel, toe, hit, scale);
                }
            }
            else if (grounded && clearance < (_isQuadruped ? 0.035f : plantHeight) * scale)
            {
                f.planted = true;
                f.pending = true;
                f.plantedAt = Time.time;
            }
        }

        /// <summary>How far <paramref name="point"/> stands off the ground the ray found, along the ground's normal.</summary>
        private static float Above(Vector3 point, RaycastHit ground)
        {
            return Vector3.Dot(point - ground.point, ground.normal);
        }

        /// <summary>
        /// The foot's contact points in the world: heel, ball and toe tip, and the lowest of them, which is the
        /// sole's height against the water. Falls back to the ankle, dropped to where a sole would be, for a body
        /// whose contacts were never measured.
        /// </summary>
        private void SolePoints(Foot f, float scale, out Vector3 heel, out Vector3 ball, out Vector3 toe, out Vector3 low)
        {
            if (f.contactHeel != null && f.contactToe != null)
            {
                heel = f.contactHeel.position;
                toe = f.contactToe.position;
                if ((toe - heel).sqrMagnitude < 1e-4f)
                    toe = heel + (f.bone != null ? f.bone.forward : _moveDirection) * (0.08f * scale);
                ball = Vector3.Lerp(heel, toe, 0.5f);
            }
            else if (f.hasContacts)
            {
                heel = f.bone.TransformPoint(f.heel);
                ball = f.bone.TransformPoint(f.ball);
                toe = f.toes.TransformPoint(f.toe);
            }
            else
            {
                Vector3 down = Vector3.down * (0.08f * scale);
                heel = (f.bone != null ? f.bone.position : transform.position) + down;
                toe = ((f.toes != null && f.toes != f.bone) ? f.toes.position : heel + _moveDirection * 0.15f * scale) + down;
                ball = Vector3.Lerp(heel, toe, 0.7f);
            }
            low = heel;
            if (ball.y < low.y)
                low = ball;
            if (toe.y < low.y)
                low = toe;
        }

        private void OnPlant(Foot f, Vector3 heel, Vector3 toe, RaycastHit hit, float scale)
        {
            Vector3 centre = (heel + toe) * 0.5f;
            centre -= hit.normal * Vector3.Dot(centre - hit.point, hit.normal);
            // Still near where this foot last came down: it has shifted or stumbled, not stepped.
            float restepBase = _isQuadruped ? 0.18f : restepDistance;
            float restep = restepBase * scale;
            if (f.hasPlant && (centre - f.lastPlant).sqrMagnitude < restep * restep)
                return;
            // The second contact of a jog's footfall: the first was the step.
            float minInterval = _isQuadruped ? 0.18f : minStepInterval;
            if (Time.time - f.lastStep < minInterval)
                return;
            f.lastStep = Time.time;
            f.lastPlant = centre;
            f.hasPlant = true;

            StepSound();
            if (onStep != null)
                onStep(f.left);

            Vector3 along = toe - heel;
            along.y = 0f;
            if (along.sqrMagnitude < 1e-4f)
                along = _moveDirection;

            // The bed of the sea, under water deep enough to see: a step through the water, not a print.
            float depth = SurfaceAt(centre.x, centre.z) - hit.point.y;
            if (depth >= splashDepth)
            {
                WadeStep(f, centre, depth);
                return;
            }
            Print(f, centre, along, hit, scale);
        }

        private void Print(Foot f, Vector3 centre, Vector3 along, RaycastHit hit, float scale)
        {
            Terrain terrain = hit.collider != null ? hit.collider.GetComponent<Terrain>() : null;
            if (terrain == null)
                return;
            float sand = FootstepEffectsHub.LayerWeight(terrain, hit.point, printLayer);
            if (sand < 0.12f)
                return;

            float height = hit.point.y - seaLevel;
            float byHeight = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.15f, Mathf.Max(0.2f, wetBand), height));
            float wet = Mathf.Max(byHeight, _soaked);
            float swash = 1f - Mathf.Clamp01(Mathf.InverseLerp(swashBand * 0.4f, swashBand, height));
            float life = Mathf.Lerp(Mathf.Lerp(dryLife, wetLife, wet), swashLife, swash) * Random.Range(0.9f, 1.1f);

            Color colour = Color.Lerp(dryTint, wetTint, wet);
            colour.a = sand * Mathf.Lerp(dryOpacity, wetOpacity, wet);

            _hub.Print(f.left, centre, along, hit.normal, printLength * scale * f.printLengthScale, printWidth * scale * f.printWidthScale, colour, life);
            // A boot that came out of the sea stops dripping a step at a time.
            _soaked = Mathf.Max(0f, _soaked - 1f / Mathf.Max(1, wetPrints));

            // A hard run, or a landing, throws sand back.
            float kick = Mathf.Max(Mathf.Clamp01((_speed - sandKickSpeed) / 3f), Impact());
            if (kick > 0f && wet < 0.65f && sand > 0.4f)
                _hub.SandKick(centre, -_moveDirection, kick);
        }

        // ---- water --------------------------------------------------------------------------------

        /// <summary>0 in the swash's film, rising to 1 by a hand's depth: how much a foot going in there can throw.</summary>
        private float Shallow(float depth, bool swimming)
        {
            if (swimming)
                return 1f;
            return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(splashDepth, splashDepth + 0.12f, depth));
        }

        private void OnEnterWater(Foot f, Vector3 low, Vector3 velocity, float depth, float surface, bool swimming)
        {
            float shallow = Shallow(depth, swimming);
            if (shallow <= 0f)
                return;
            Vector3 at = new Vector3(low.x, surface, low.z);
            // The swell coming up round a foot that is standing in it: the water moved, not the foot.
            if (!swimming && velocity.magnitude < stillFoot)
            {
                _hub.Ring(at, 0.6f, 0.3f * shallow);
                return;
            }
            Vector3 flat = new Vector3(velocity.x, 0f, velocity.z);
            float strength = (0.3f + 0.08f * flat.magnitude + 0.2f * Mathf.Max(0f, -velocity.y) + 0.5f * Impact()) * shallow;
            if (swimming)
                strength *= 0.75f;
            _hub.Splash(at, strength, Vector3.ClampMagnitude(flat * 0.3f, 1.8f));
            f.lastSplash = Time.time;
            // A swimmer's strokes are the kit's SwimStroke sounds.
            if (!swimming)
                SplashSound(at, strength);
        }

        private void OnLeaveWater(Foot f, Vector3 low, Vector3 velocity, float depth, float surface, bool swimming)
        {
            float shallow = Shallow(depth, swimming);
            // The swell falling away from a foot standing still sheds nothing.
            if (shallow <= 0f || (!swimming && velocity.magnitude < stillFoot))
                return;
            float strength = Mathf.Clamp01(0.3f + 0.06f * velocity.magnitude) * shallow;
            _hub.Drip(low, surface, strength);
            _hub.Ring(new Vector3(low.x, surface, low.z), 0.6f, 0.35f * shallow);
        }

        /// <summary>A foot planted on the bed under the water, which at knee depth never comes up to splash: a ring, and a few drops at speed.</summary>
        private void WadeStep(Foot f, Vector3 at, float depth)
        {
            // The foot has only just gone under: the splash of that is enough.
            if (Time.time - f.lastSplash < 0.25f)
                return;
            f.lastSplash = Time.time;
            float strength = Mathf.Clamp01(0.3f + 0.07f * _speed + 0.5f * Impact()) * Shallow(depth, false);
            Vector3 surface = new Vector3(at.x, SurfaceAt(at.x, at.z), at.z);
            _hub.Ring(surface, Mathf.Lerp(0.9f, 1.6f, strength), 0.55f + 0.3f * strength);
            if (_speed > 1f)
                _hub.Splash(surface, strength * 0.8f, _moveDirection * 0.5f);
            SplashSound(surface, strength * 0.8f);
        }

        // ---- sound --------------------------------------------------------------------------------

        /// <summary>
        /// One splash clip at <paramref name="at"/>: quieter and a little higher for a gentle step, full and
        /// a touch lower for a run or a landing. Two feet in one frame, or a foot going in and then planting a
        /// moment later, are one sound.
        /// </summary>
        private void SplashSound(Vector3 at, float strength)
        {
            if (splashClips == null || splashClips.Length == 0 || Time.time - _lastSplashSound < 0.15f)
                return;
            _lastSplashSound = Time.time;
            strength = Mathf.Clamp01(strength);
            OneShotSound.PlayAt(splashClips, at, splashVolume * Mathf.Lerp(0.45f, 1f, strength), splashNear, splashFar,
                                0.06f, Mathf.Lerp(1.1f, 0.95f, strength));
        }

        /// <summary>
        /// Whether this body is wading - standing in water deep enough to splash, not swimming - with a little
        /// hysteresis so the swell running over the boots at the edge does not flick it, and the dry footstep
        /// turned down to match.
        /// </summary>
        private void TrackWading(Vector3 root, bool swimming)
        {
            float depth = SurfaceAt(root.x, root.z) - root.y;
            if (swimming)
                _wading = false;
            else if (_wading)
                _wading = depth > splashDepth * 0.5f;
            else
                _wading = depth > splashDepth + 0.02f;
            SilenceDrySteps(_wading && muteDryStepsInWater && splashClips != null && splashClips.Length > 0);
        }

        /// <summary>Turns the kit's footstep source down to nothing, or back to what it was.</summary>
        private void SilenceDrySteps(bool silence)
        {
            if (silence == _drySilenced)
                return;
            if (_drySteps == null)
            {
                var footsteps = GetComponent<CharacterFootstepSoundComponent>();
                _drySteps = footsteps != null ? footsteps.audioSource : null;
                if (_drySteps == null)
                    return;
            }
            if (silence)
            {
                _dryStepsVolume = _drySteps.volume;
                _drySteps.volume = 0f;
            }
            else
            {
                _drySteps.volume = _dryStepsVolume;
            }
            _drySilenced = silence;
        }

        /// <summary>
        /// Stops the kit's footstep timer for walking, jogging and sprinting, or starts it again, by setting each
        /// gait's `stepDelay` to never. Swimming is left to the timer (no plants), crawling too (hands and
        /// knees), and so is crouching: the crouch clip drives the soles 5-8 cm below the ground, so with the
        /// IK holding them up only one foot ever reads as landing (measured live: all six plants in 8 s were
        /// the same foot), and a sound on each plant would leave every other crouch step silent. Only an
        /// instance's own copy of the settings is touched, never the prefab's; and it is only done once this
        /// component is running, so a body it cannot run on keeps the timer.
        /// </summary>
        private void SilenceStepTimer(bool silence)
        {
            if (silence == _timerSilenced)
                return;
            if (_kitSteps == null)
                _kitSteps = GetComponent<CharacterFootstepSoundComponent>();
            // Not a client (its Start turned it off), or no audio source to play from: nothing to hand over.
            if (_kitSteps == null || !_kitSteps.enabled || _kitSteps.audioSource == null)
                return;

            if (silence)
            {
                _timed = new[]
                {
                    _kitSteps.walkFootstepSettings, _kitSteps.moveFootstepSettings, _kitSteps.sprintFootstepSettings,
                };
                _timedDelays = new float[_timed.Length];
                for (int i = 0; i < _timed.Length; ++i)
                {
                    if (_timed[i] == null)
                        continue;
                    _timedDelays[i] = _timed[i].stepDelay;
                    _timed[i].stepDelay = float.MaxValue;
                }
            }
            else if (_timed != null)
            {
                // Backwards, so that a settings object shared by two gaits gets its real delay back last.
                for (int i = _timed.Length - 1; i >= 0; --i)
                {
                    if (_timed[i] != null)
                        _timed[i].stepDelay = _timedDelays[i];
                }
            }
            _timerSilenced = silence;
        }

        /// <summary>
        /// The kit's dry footstep, now, for the foot that has just landed. Held to the kit's own rules: a body
        /// that is not trying to move makes none, and a gait with no clips makes none (the kit would play a
        /// null clip). Does nothing while the kit's timer is still running - it is not ours to play then.
        /// </summary>
        private void StepSound()
        {
            if (!_timerSilenced)
                return;
            MovementState state = _entity.MovementState;
            const MovementState walking = MovementState.Forward | MovementState.Backward | MovementState.Left | MovementState.Right;
            if ((state & walking) == 0)
                return;
            FootstepSettings settings;
            switch (_entity.ExtraMovementState)
            {
                case ExtraMovementState.IsWalking: settings = _kitSteps.walkFootstepSettings; break;
                case ExtraMovementState.IsSprinting: settings = _kitSteps.sprintFootstepSettings; break;
                // Still on the kit's timer: see SilenceStepTimer.
                case ExtraMovementState.IsCrouching:
                case ExtraMovementState.IsCrawling:
                    return;
                default: settings = _kitSteps.moveFootstepSettings; break;
            }
            AudioClip[] clips = settings != null ? settings.soundData.randomAudioClips : null;
            if (clips == null || clips.Length == 0)
                return;
            _kitSteps.PlaySound();
        }

        /// <summary>Someone standing in the water still disturbs it, slowly.</summary>
        private void Standing(Vector3 root, float dt)
        {
            bool inWater = false;
            if (_feet != null)
            {
                for (int i = 0; i < _feet.Length; i++)
                {
                    if (_feet[i] != null && _feet[i].submerged)
                    {
                        inWater = true;
                        break;
                    }
                }
            }
            float depth = SurfaceAt(root.x, root.z) - root.y;
            if (!inWater || depth < splashDepth + 0.05f || _speed > 0.35f)
            {
                _idleTimer = 0f;
                return;
            }
            _idleTimer += dt;
            if (_idleTimer < idleRippleEvery)
                return;
            _idleTimer = 0f;
            _hub.Ring(new Vector3(root.x, SurfaceAt(root.x, root.z), root.z), 1.2f, 0.45f, 1.6f);
        }

        // ---- swimming -----------------------------------------------------------------------------

        private void StepHand(Hand h, float dt)
        {
            if (h == null)
                return;
            Vector3 p = h.bone.position;
            Vector3 velocity = h.hasLast ? (p - h.last) / dt : Vector3.zero;
            h.last = p;
            h.hasLast = true;

            float surface = SurfaceAt(p.x, p.z);
            bool wasWet = h.submerged;
            bool isWet = wasWet ? p.y < surface + 0.02f : p.y < surface - 0.01f;
            h.submerged = isWet;
            if (isWet == wasWet || Time.time - h.lastWater < 0.12f)
                return;
            h.lastWater = Time.time;
            if (isWet)
            {
                Vector3 flat = new Vector3(velocity.x, 0f, velocity.z);
                float strength = 0.3f + 0.08f * velocity.magnitude;
                _hub.Splash(new Vector3(p.x, surface, p.z), Mathf.Clamp01(strength), Vector3.ClampMagnitude(flat * 0.25f, 1.5f));
            }
            else
            {
                _hub.Drip(p, surface, Mathf.Clamp01(0.35f + 0.05f * velocity.magnitude));
            }
        }

        /// <summary>
        /// The trail of a swimmer: patches of foam laid behind the chest at a rate that rises with speed,
        /// widening rings, and a little spray off the bow once they are really going.
        /// </summary>
        private void Wake(Vector3 root, float scale, float dt)
        {
            Vector3 body = _hips != null ? _hips.position : root;
            Vector3 at = new Vector3(body.x, SurfaceAt(body.x, body.z), body.z);
            float pace = Mathf.Clamp01(_speed / 3.2f);

            _wakeTimer -= dt;
            _wakeRingTimer -= dt;
            _bowTimer -= dt;

            if (_speed < 0.3f)
            {
                // Treading water.
                _idleTimer += dt;
                if (_idleTimer >= idleRippleEvery * 0.7f)
                {
                    _idleTimer = 0f;
                    _hub.Ring(at, 1.5f, 0.45f, 1.7f);
                }
                return;
            }

            Vector3 side = Vector3.Cross(Vector3.up, _moveDirection);
            if (_wakeTimer <= 0f)
            {
                _wakeTimer = Mathf.Lerp(0.30f, 0.12f, pace);
                Vector3 behind = at - _moveDirection * (0.45f * scale) + side * Random.Range(-0.15f, 0.15f) * scale;
                _hub.Foam(behind, Mathf.Lerp(0.8f, 1.5f, pace) * scale, 0.45f + 0.35f * pace);
            }
            if (_wakeRingTimer <= 0f)
            {
                _wakeRingTimer = Mathf.Lerp(0.7f, 0.35f, pace);
                _hub.Ring(at - _moveDirection * (0.25f * scale), Mathf.Lerp(1.4f, 2.6f, pace), 0.5f, 1.4f);
            }
            if (pace > 0.5f && _bowTimer <= 0f)
            {
                _bowTimer = 0.16f;
                _hub.Spray(at + _moveDirection * (0.35f * scale), _moveDirection, pace);
            }
        }
    }
}
