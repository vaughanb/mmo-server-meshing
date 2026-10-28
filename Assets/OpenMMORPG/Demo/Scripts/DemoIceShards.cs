using UnityEngine;

namespace MultiplayerARPG.Demo
{
    /// <summary>
    /// Ice crystals that break out of the ground, stand, and shatter: the ring Frost Nova throws
    /// up, and the ice that pins a frozen character's feet.
    ///
    /// Crystals rather than particles because they have to stand *on* the island: each one is
    /// set on the ground found under it (with the kit's mask for what an area skill lands on), so
    /// a ring thrown across a hillside sits in the slope instead of floating off it or sinking
    /// into it - which a flat particle shape cannot do. They are real meshes with a lit, glassy
    /// material, so their facets catch the sun and read as ice rather than as light.
    ///
    /// A crystal breaks the ground when the nova's wave reaches it (<see cref="waveSpeed"/>),
    /// thrusts up with a little overshoot, stands for <see cref="holdSeconds"/>, then shatters -
    /// sinking and shrinking while it throws glitter into <see cref="fragments"/>. With a negative
    /// hold it stands until <see cref="Shatter"/> is called, which is how a frozen character's
    /// ice lasts exactly as long as the freeze (DemoFrozenEffect).
    ///
    /// It makes its own noise, because it is the one thing that knows the moment: the Freeze
    /// family as the first crystal forms, the Shatter family as they break (wired by the audio
    /// builder, and silent until the clips exist).
    ///
    /// Pooled with its effect, so everything is placed afresh each time it is switched on, on the
    /// first frame rather than in OnEnable: a pooled effect is not guaranteed to be at its new
    /// spot at that moment.
    ///
    /// Its late update stops once every crystal has gone (<see cref="WakeableLateUpdateBehaviour"/>),
    /// and a crystal that has finished growing is written once and then left alone until it
    /// breaks - unless the effect itself has moved, since the crystals are pinned to the ground
    /// where they formed, not carried with whatever the effect follows.
    /// </summary>
    public class DemoIceShards : WakeableLateUpdateBehaviour
    {
        [Tooltip("The crystal: base on the ground at the origin, tip up at y = 1, one unit across.")]
        public Mesh mesh;
        public Material material;
        [Tooltip("Where the glitter goes when a crystal shatters. Emitted by hand, so it needs no emission of its own.")]
        public ParticleSystem fragments;
        [Min(1)]
        public int count = 16;
        [Tooltip("Crystals come up in clumps of this many: one big one and the rest smaller, leaning off its foot.")]
        [Min(1)]
        public int clump = 1;
        [Tooltip("The crystals stand between these distances from the middle, in metres.")]
        public float innerRadius = 1.2f;
        public float outerRadius = 4.2f;
        [Tooltip("Height of a crystal, least to most, in metres.")]
        public Vector2 length = new Vector2(0.6f, 1.6f);
        [Tooltip("Width of a crystal, least to most, in metres.")]
        public Vector2 thickness = new Vector2(0.18f, 0.4f);
        [Tooltip("How far a crystal leans away from the middle, in degrees, give or take a third.")]
        public float lean = 28f;
        [Tooltip("Metres a second the eruption runs outward. Zero breaks them all at once.")]
        public float waveSpeed = 14f;
        public float growSeconds = 0.12f;
        [Tooltip("Seconds a crystal stands before shattering. Negative stands until Shatter is called.")]
        public float holdSeconds = 1.1f;
        public float shatterSeconds = 0.35f;
        [Tooltip("Glitter thrown by each crystal as it breaks.")]
        public int fragmentsEach = 6;
        [Tooltip("Chips of ice thrown up by each crystal as it breaks the ground.")]
        public int chipsEach = 0;

        [Header("Sound")]
        [Tooltip("Played once, one picked at random, as the first crystal forms.")]
        public AudioClip[] formSounds = new AudioClip[0];
        [Tooltip("Played as the crystals break, one picked at random each time.")]
        public AudioClip[] shatterSounds = new AudioClip[0];
        [Tooltip("Play another shatter sound every this many breaks after the first. Zero plays it once.")]
        public int shatterSoundEvery = 0;
        [Range(0f, 1f)]
        public float soundVolume = 1f;

        private struct Crystal
        {
            public Transform transform;
            public Renderer renderer;
            public Vector3 ground;
            public Quaternion rotation;
            public float width;
            public float height;
            public float appearAt;
            public float breakAt;
            public bool appeared;
            public bool broken;
            /// <summary>Grown, written at full height, and not yet breaking: nothing to redraw.</summary>
            public bool standing;
        }

        private Crystal[] _crystals;
        private readonly RaycastHit[] _hits = new RaycastHit[8];
        private float _age;
        private Vector3 _anchorPosition;
        private Quaternion _anchorRotation;
        private bool _placed;
        private float _shatterCalledAt = -1f;
        private bool _formed;
        private int _broken;

        private void Awake()
        {
            if (Application.isBatchMode)
            {
                enabled = false;
                return;
            }
            _crystals = new Crystal[Mathf.Max(1, count)];
            for (int i = 0; i < _crystals.Length; ++i)
            {
                var go = new GameObject("Crystal");
                go.layer = gameObject.layer;
                go.transform.SetParent(transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.enabled = false;
                _crystals[i].transform = go.transform;
                _crystals[i].renderer = renderer;
            }
        }

        private void OnEnable()
        {
            _age = 0f;
            _placed = false;
            _shatterCalledAt = -1f;
            _formed = false;
            _broken = 0;
            // Glitter still in the air when the effect was put away would come back with it,
            // wherever it was left.
            if (fragments != null)
                fragments.Clear();
            if (_crystals == null)
                return;
            for (int i = 0; i < _crystals.Length; ++i)
            {
                _crystals[i].appeared = false;
                _crystals[i].broken = false;
                _crystals[i].standing = false;
                if (_crystals[i].renderer != null)
                    _crystals[i].renderer.enabled = false;
            }
            Wake();
        }

        /// <summary>
        /// Breaks every crystal still standing, a little apart from each other. For a hold of
        /// less than zero this is the only way they go; otherwise it only brings it forward.
        /// </summary>
        public void Shatter()
        {
            if (_shatterCalledAt < 0f)
                _shatterCalledAt = _age;
            Wake();
        }

        public override void ManagedLateUpdate()
        {
            if (_crystals == null)
            {
                Sleep();
                return;
            }
            if (!_placed)
                Place();
            _age += Time.deltaTime;

            // Standing crystals are pinned to the world; only if the effect has moved under them
            // do they need writing again.
            bool moved = transform.position != _anchorPosition || transform.rotation != _anchorRotation;
            if (moved)
            {
                _anchorPosition = transform.position;
                _anchorRotation = transform.rotation;
            }
            bool anyLeft = false;
            for (int i = 0; i < _crystals.Length; ++i)
            {
                ref Crystal crystal = ref _crystals[i];
                float since = _age - crystal.appearAt;
                if (crystal.transform == null)
                    continue;
                if (since < 0f)
                {
                    anyLeft = true;
                    continue;
                }
                if (!crystal.appeared)
                {
                    crystal.appeared = true;
                    // Out of the ground at its foot, mostly upward: the ground breaking.
                    Throw(crystal, chipsEach, 0f, 0.08f, 1.6f, 2.2f, 4.8f);
                    if (!_formed)
                    {
                        _formed = true;
                        OneShotSound.PlayAt(formSounds, transform.position, soundVolume);
                    }
                }

                float breakAt = crystal.breakAt;
                if (_shatterCalledAt >= 0f)
                    breakAt = Mathf.Min(breakAt < 0f ? float.MaxValue : breakAt, _shatterCalledAt + (crystal.appearAt % 0.13f));
                float breaking = breakAt >= 0f ? (_age - breakAt) / Mathf.Max(0.01f, shatterSeconds) : -1f;
                if (breaking >= 1f)
                {
                    crystal.renderer.enabled = false;
                    continue;
                }
                anyLeft = true;
                if (breaking < 0f && crystal.standing && !moved)
                    continue;

                float rise = Overshoot(Mathf.Clamp01(since / Mathf.Max(0.01f, growSeconds)));
                float height = crystal.height * rise;
                float width = crystal.width * Mathf.Lerp(0.6f, 1f, Mathf.Clamp01(rise));
                Vector3 position = crystal.ground;
                if (breaking >= 0f)
                {
                    if (!crystal.broken)
                    {
                        crystal.broken = true;
                        Throw(crystal, fragmentsEach, 0.1f, 0.9f, 0.6f, 1.2f, 3.2f);
                        // Once for the first to go and again every so many after, not once a
                        // crystal: a nova breaks three dozen in half a second.
                        if (_broken++ == 0 || (shatterSoundEvery > 0 && (_broken - 1) % shatterSoundEvery == 0))
                            OneShotSound.PlayAt(shatterSounds, crystal.ground, soundVolume);
                    }
                    float left = 1f - breaking;
                    height *= left * left;
                    width *= left;
                    // Sinks back into the ground as it goes, so it breaks up rather than simply
                    // shrinking where it stood.
                    position -= crystal.rotation * Vector3.up * (crystal.height * 0.3f * breaking);
                }
                crystal.renderer.enabled = height > 0.001f;
                crystal.transform.SetPositionAndRotation(position, crystal.rotation);
                crystal.transform.localScale = new Vector3(width, Mathf.Max(0.0001f, height), width);
                crystal.standing = breaking < 0f && since >= growSeconds;
            }
            if (!anyLeft)
                Sleep();
        }

        /// <summary>
        /// Stands every crystal on the ground under its spot. The ring is laid out afresh each
        /// time, seeded from where it is, so no two novas throw up the same ice.
        ///
        /// Crystals come up in clumps (<see cref="clump"/>) - a big one with smaller ones leaning
        /// off its foot - because single spikes evenly spread read as a fence of stakes, and ice
        /// grows in clusters.
        /// </summary>
        private void Place()
        {
            _placed = true;
            Vector3 centre = transform.position;
            int mask = GameInstance.Singleton != null
                ? GameInstance.Singleton.GetAreaSkillGroundDetectionLayerMask()
                : Physics.DefaultRaycastLayers;
            var random = new System.Random(Mathf.RoundToInt(centre.x * 97f + centre.z * 131f + Time.frameCount));
            float spin = (float)random.NextDouble() * 360f;
            int size = Mathf.Max(1, clump);
            int clumps = Mathf.CeilToInt(_crystals.Length / (float)size);
            Vector3 spot = centre;
            Vector3 radial = Vector3.forward;
            float leadHeight = 0f;
            float leadWidth = 0f;
            float appear = 0f;
            for (int i = 0; i < _crystals.Length; ++i)
            {
                ref Crystal crystal = ref _crystals[i];
                int member = i % size;
                if (member == 0)
                {
                    float angle = (spin + (i / size + Next(random, -0.35f, 0.35f)) * 360f / clumps) * Mathf.Deg2Rad;
                    radial = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                    // Spread evenly over the ring's area rather than bunched at its inner edge.
                    float r = Mathf.Sqrt(Mathf.Lerp(innerRadius * innerRadius, outerRadius * outerRadius, (float)random.NextDouble()));
                    spot = centre + radial * r;
                    // The biggest crystals out on the wave front, where the cold ran hardest.
                    float outer = Mathf.InverseLerp(innerRadius, outerRadius, r);
                    leadHeight = Mathf.Lerp(length.x, length.y, Mathf.Clamp01(0.35f * (float)random.NextDouble() + 0.65f * outer));
                    leadWidth = Next(random, thickness.x, thickness.y);
                    appear = waveSpeed > 0f ? r / waveSpeed : Next(random, 0f, 0.06f);
                }

                Vector3 at = spot;
                Vector3 away = radial;
                float height = leadHeight;
                float width = leadWidth;
                float tilt = lean * Next(random, 0.67f, 1.33f);
                if (member > 0)
                {
                    float side = Next(random, 0f, Mathf.PI * 2f);
                    var offset = new Vector3(Mathf.Cos(side), 0f, Mathf.Sin(side));
                    at += offset * (leadWidth * Next(random, 0.6f, 1.1f));
                    away = (radial + offset * 1.5f).normalized;
                    height *= Next(random, 0.35f, 0.7f);
                    width *= Next(random, 0.55f, 0.8f);
                    tilt = Mathf.Min(70f, tilt * Next(random, 1.3f, 1.9f));
                }
                at.y = GroundHeight(at.x, at.z, centre.y, mask) - 0.05f;
                crystal.ground = at;
                crystal.rotation = Quaternion.AngleAxis(tilt, Vector3.Cross(Vector3.up, away)) *
                                   Quaternion.AngleAxis(Next(random, 0f, 360f), Vector3.up);
                crystal.height = height;
                crystal.width = width;
                crystal.appearAt = appear + (member > 0 ? Next(random, 0.01f, 0.05f) : 0f);
                crystal.breakAt = holdSeconds < 0f
                    ? -1f
                    : crystal.appearAt + growSeconds + holdSeconds + Next(random, 0f, 0.25f);
                crystal.appeared = false;
                crystal.broken = false;
                crystal.standing = false;
                crystal.renderer.enabled = false;
            }
        }

        /// <summary>
        /// Glitter off a crystal: <paramref name="count"/> bits from between <paramref name="from"/>
        /// and <paramref name="to"/> of the way up it, flung out and up (<paramref name="upward"/>
        /// weights up against every other way).
        /// </summary>
        private void Throw(in Crystal crystal, int count, float from, float to, float upward, float slowest, float fastest)
        {
            if (fragments == null || count <= 0)
                return;
            Vector3 up = crystal.rotation * Vector3.up;
            var emit = new ParticleSystem.EmitParams { applyShapeToPosition = false };
            for (int i = 0; i < count; ++i)
            {
                emit.position = crystal.ground + up * (crystal.height * Random.Range(from, to));
                emit.velocity = (Random.insideUnitSphere + up * upward).normalized * Random.Range(slowest, fastest);
                fragments.Emit(emit, 1);
            }
        }

        /// <summary>
        /// The ground nearest the expected height, or that height if there is none within reach.
        /// Five metres either way: a crystal four metres out on a hillside can stand well above
        /// or below the middle of the ring.
        /// </summary>
        private float GroundHeight(float x, float z, float expected, int mask)
        {
            int hits = Physics.RaycastNonAlloc(new Vector3(x, expected + 5f, z), Vector3.down, _hits, 10f, mask,
                                               QueryTriggerInteraction.Ignore);
            float found = expected;
            float nearest = float.MaxValue;
            for (int i = 0; i < hits; ++i)
            {
                float gap = Mathf.Abs(_hits[i].point.y - expected);
                if (gap < nearest)
                {
                    nearest = gap;
                    found = _hits[i].point.y;
                }
            }
            return found;
        }

        /// <summary>Up past full height and back: the thrust of ice breaking through.</summary>
        private static float Overshoot(float k)
        {
            const float back = 1.9f;
            float t = k - 1f;
            return 1f + (back + 1f) * t * t * t + back * t * t;
        }

        private static float Next(System.Random random, float min, float max)
        {
            return min + (float)random.NextDouble() * (max - min);
        }
    }
}
