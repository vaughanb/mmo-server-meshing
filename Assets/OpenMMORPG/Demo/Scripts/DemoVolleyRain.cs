using System.Collections.Generic;
using UnityEngine;

namespace MultiplayerARPG.Demo
{
    /// <summary>
    /// Volley's arrows: waves of them coming down steeply onto the patch, sticking where they land,
    /// kicking up dirt, and sinking out of sight when the rain is over.
    ///
    /// Until 2026-09-25 the skill had no arrows in it at all - a glowing disc and some pale-blue
    /// motes, which read as a frost spell. This lives on `FX_VolleyRain`, a pooled GameEffect that
    /// <see cref="AreaLandEffect"/> fetches the frame Volley's area appears, so it keeps its own clock
    /// rather than the area's: the area is put away the moment its last bite is taken, and arrows
    /// standing in the ground should not vanish with it.
    ///
    /// **Timed to the bites.** The area bites every 0.75s from 0.75s after it appears
    /// (DemoSkillBuilder), so a wave lands a moment before each: what is seen coming down is what
    /// hurts. The arrows are real meshes - the demo's own arrow, the one on the string - because
    /// at the distances a volley is watched from, the shaft standing in the ground is the whole
    /// read; a streak alone is a spell.
    ///
    /// **The streaks are emitted by hand along each arrow's path**, not left to an emitter riding
    /// the arrow: over-distance emission drops particles at each frame's position, and at thirty
    /// metres a second that is clumps half a metre apart. Here each frame fills the segment it
    /// covered. Dirt and chips the same way, at the point each arrow lands.
    ///
    /// Its late update runs from the fetch until the last arrow has sunk and the last whistle has
    /// played, and then lets go until the pool hands the rain out again
    /// (<see cref="WakeableLateUpdateBehaviour"/>), rather than walking three dozen spent arrows
    /// every frame until the effect is put away.
    /// </summary>
    public class DemoVolleyRain : WakeableLateUpdateBehaviour
    {
        [Header("Arrows")]
        [Tooltip("The demo's Arrow.prefab: length on +Y, tip at +Y, origin at the middle.")]
        public GameObject arrowPrefab;

        [Tooltip("The arrows' size against the one on the string. Over 1 so a shaft reads at a volley's distance.")]
        public float arrowScale = 1f;

        [Tooltip("The patch, in metres. The skill's own radius.")]
        public float radius = 4f;

        public int waves = 4;
        public int arrowsPerWave = 9;

        [Tooltip("Seconds after the rain starts that the first wave lands.")]
        public float firstLanding = 0.45f;

        [Tooltip("Seconds between waves: the area's bite interval.")]
        public float waveInterval = 0.75f;

        [Tooltip("Seconds one wave's arrows are spread across.")]
        public float waveSpread = 0.5f;

        [Tooltip("Degrees below level the arrows come down at.")]
        public float descent = 64f;

        public float descentJitter = 6f;

        [Tooltip("Degrees either side of straight on that an arrow's heading may stray.")]
        public float yawJitter = 14f;

        [Tooltip("Metres a second an arrow falls at.")]
        public float speed = 30f;

        [Tooltip("Seconds each arrow is seen in the air before it lands.")]
        public float flightSeconds = 0.4f;

        [Tooltip("Metres of the arrow that go into the ground.")]
        public float embed = 0.2f;

        [Tooltip("Seconds after the rain starts that the stuck arrows begin to go.")]
        public float sinkAt = 3.6f;

        public float sinkSeconds = 0.6f;

        [Header("What it throws up")]
        [Tooltip("World-space motes laid along each arrow's path.")]
        public ParticleSystem streak;

        public float streakPerMetre = 14f;

        [Tooltip("World-space dirt puffed up where each arrow lands.")]
        public ParticleSystem dust;

        public int dustEach = 3;

        [Tooltip("World-space clods thrown up where each arrow lands.")]
        public ParticleSystem chips;

        public int chipsEach = 4;

        [Header("Sounds")]
        [Tooltip("Arrows in the air. Each wave's is started so it ends as that wave lands.")]
        public AudioClip[] whistleSounds = new AudioClip[0];

        [Tooltip("One arrow going into the ground each - single thunks, not clusters. The rain's own staggered " +
                 "landings make the patter.")]
        public AudioClip[] impactSounds = new AudioClip[0];

        [Tooltip("One impact sound for this many arrows, so a wave is a patter and not a roar.")]
        public int impactSoundEvery = 2;

        [Tooltip("Pitch the impacts play at, before their random spread. Below 1 while they are borrowed arrow-in-a-body " +
                 "thunks, so they read as arrows into earth; set by Wire Audio.")]
        [Range(0.5f, 1.5f)]
        public float impactPitch = 1f;

        [Range(0f, 1f)]
        public float soundVolume = 0.8f;

        private struct Shaft
        {
            public Transform transform;
            public float launch;
            public float land;
            public Vector3 dir;
            public Vector3 ground;
            public Vector3 stuck;
            public Quaternion rest;
            public Vector3 wobbleAxis;
            public Vector3 lastTail;
            public bool flying;
            public bool landed;
            public bool gone;
        }

        private readonly List<Shaft> _shafts = new List<Shaft>();
        private float _halfLength = 0.36f;
        private float _start = -1f;
        private bool _pending;
        private float[] _whistleAt = new float[0];
        private AudioClip[] _whistleClip = new AudioClip[0];
        private int _landedCount;
        private float _streakCarry;
        private readonly RaycastHit[] _hits = new RaycastHit[8];
        private readonly AudioClip[] _oneClip = new AudioClip[1];

        private void Awake()
        {
            if (Application.isBatchMode)
            {
                enabled = false;
                return;
            }
            if (arrowPrefab == null)
                return;
            int count = Mathf.Max(0, waves * arrowsPerWave);
            for (int i = 0; i < count; ++i)
            {
                GameObject arrow = Instantiate(arrowPrefab, transform);
                arrow.name = "Arrow";
                arrow.transform.localScale *= arrowScale;
                // Scenery, not a thing to be hit or to stand on.
                foreach (Collider collider in arrow.GetComponentsInChildren<Collider>(true))
                    Destroy(collider);
                arrow.SetActive(false);
                if (i == 0)
                    _halfLength = MeasureHalfLength(arrow);
                _shafts.Add(new Shaft { transform = arrow.transform, gone = true });
            }
        }

        private static float MeasureHalfLength(GameObject arrow)
        {
            Quaternion was = arrow.transform.rotation;
            arrow.transform.rotation = Quaternion.identity;
            bool any = false;
            Bounds bounds = default;
            foreach (MeshFilter filter in arrow.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh == null)
                    continue;
                Bounds local = filter.sharedMesh.bounds;
                Vector3 size = Vector3.Scale(local.size, filter.transform.lossyScale);
                var world = new Bounds(filter.transform.TransformPoint(local.center), size);
                if (any)
                    bounds.Encapsulate(world);
                else
                    bounds = world;
                any = true;
            }
            arrow.transform.rotation = was;
            return any && bounds.extents.y > 0.05f ? bounds.extents.y : 0.36f;
        }

        private void OnEnable()
        {
            _pending = true;
            _start = -1f;
            Wake();
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            for (int i = 0; i < _shafts.Count; ++i)
            {
                Shaft shaft = _shafts[i];
                shaft.gone = true;
                if (shaft.transform != null)
                    shaft.transform.gameObject.SetActive(false);
                _shafts[i] = shaft;
            }
        }

        /// <summary>Lays out every arrow of every wave, on the first frame the rain is where it will fall.</summary>
        private void Plan()
        {
            _start = Time.time;
            _landedCount = 0;
            _streakCarry = 0f;
            int mask = GameInstance.Singleton != null
                ? GameInstance.Singleton.GetAreaSkillGroundDetectionLayerMask()
                : Physics.DefaultRaycastLayers;
            Vector3 centre = transform.position;
            Vector3 forward = transform.forward;
            forward.y = 0f;
            forward = forward.sqrMagnitude > 1e-4f ? forward.normalized : Vector3.forward;

            for (int i = 0; i < _shafts.Count; ++i)
            {
                int wave = i / Mathf.Max(1, arrowsPerWave);
                Shaft shaft = _shafts[i];
                shaft.land = firstLanding + wave * waveInterval + Random.Range(-0.5f, 0.5f) * waveSpread;
                shaft.launch = shaft.land - flightSeconds;

                // Evenly over the disc rather than bunched in the middle.
                Vector2 spot = Random.insideUnitCircle * (radius * 0.95f);
                Vector3 point = centre + new Vector3(spot.x, 0f, spot.y);
                shaft.ground = Ground(point, centre.y, mask);

                float down = (descent + Random.Range(-descentJitter, descentJitter)) * Mathf.Deg2Rad;
                Vector3 heading = Quaternion.AngleAxis(Random.Range(-yawJitter, yawJitter), Vector3.up) * forward;
                shaft.dir = (heading * Mathf.Cos(down) + Vector3.down * Mathf.Sin(down)).normalized;
                shaft.stuck = shaft.ground - shaft.dir * (_halfLength - embed);
                shaft.rest = Quaternion.FromToRotation(Vector3.up, shaft.dir) *
                             Quaternion.AngleAxis(Random.Range(0f, 360f), Vector3.up);
                shaft.wobbleAxis = Vector3.Cross(shaft.dir, Vector3.up).normalized;
                shaft.flying = false;
                shaft.landed = false;
                shaft.gone = false;
                shaft.transform.gameObject.SetActive(false);
                _shafts[i] = shaft;
            }

            _whistleAt = new float[waves];
            _whistleClip = new AudioClip[waves];
            for (int w = 0; w < waves; ++w)
            {
                AudioClip clip = whistleSounds != null && whistleSounds.Length > 0
                    ? whistleSounds[Random.Range(0, whistleSounds.Length)]
                    : null;
                _whistleClip[w] = clip;
                // Started so its end - the loudest part - is the wave arriving.
                float landing = firstLanding + w * waveInterval;
                _whistleAt[w] = clip != null ? Mathf.Max(0f, landing - clip.length) : -1f;
            }
        }

        private Vector3 Ground(Vector3 point, float expected, int mask)
        {
            int count = Physics.RaycastNonAlloc(new Vector3(point.x, expected + 4f, point.z), Vector3.down, _hits, 10f,
                                                mask, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            Vector3 found = new Vector3(point.x, expected, point.z);
            for (int i = 0; i < count; ++i)
            {
                if (_hits[i].distance < best)
                {
                    best = _hits[i].distance;
                    found = _hits[i].point;
                }
            }
            return found;
        }

        public override void ManagedLateUpdate()
        {
            if (_pending)
            {
                _pending = false;
                Plan();
            }
            if (_start < 0f)
            {
                Sleep();
                return;
            }
            float t = Time.time - _start;
            bool anyLeft = false;

            for (int w = 0; w < _whistleAt.Length; ++w)
            {
                if (_whistleAt[w] < 0f)
                    continue;
                if (t < _whistleAt[w])
                {
                    anyLeft = true;
                    continue;
                }
                _oneClip[0] = _whistleClip[w];
                OneShotSound.PlayAt(_oneClip, transform.position + Vector3.up * 3f, soundVolume, 8f, 70f);
                _whistleAt[w] = -1f;
            }

            float sink = sinkSeconds > 0f ? Mathf.Clamp01((t - sinkAt) / sinkSeconds) : (t >= sinkAt ? 1f : 0f);
            for (int i = 0; i < _shafts.Count; ++i)
            {
                Shaft shaft = _shafts[i];
                if (shaft.gone)
                    continue;
                anyLeft = true;
                if (t < shaft.launch)
                    continue;

                if (t < shaft.land)
                {
                    Vector3 centre = shaft.stuck - shaft.dir * (speed * (shaft.land - t));
                    Vector3 tail = centre - shaft.dir * _halfLength;
                    if (!shaft.flying)
                    {
                        shaft.flying = true;
                        shaft.lastTail = tail;
                        shaft.transform.gameObject.SetActive(true);
                        shaft.transform.rotation = shaft.rest;
                    }
                    shaft.transform.position = centre;
                    Streak(shaft.lastTail, tail);
                    shaft.lastTail = tail;
                    _shafts[i] = shaft;
                    continue;
                }

                if (!shaft.landed)
                {
                    shaft.landed = true;
                    if (!shaft.flying)
                    {
                        shaft.flying = true;
                        shaft.transform.gameObject.SetActive(true);
                    }
                    else
                    {
                        Streak(shaft.lastTail, shaft.stuck - shaft.dir * _halfLength);
                    }
                    Land(shaft.ground);
                }

                // A shiver as it goes in, then still.
                float since = t - shaft.land;
                float wobble = since < 0.4f ? 7f * Mathf.Exp(-9f * since) * Mathf.Sin(2f * Mathf.PI * 11f * since) : 0f;
                shaft.transform.rotation = Quaternion.AngleAxis(wobble, shaft.wobbleAxis) * shaft.rest;
                // Into the ground along its own line, as if the earth took it.
                shaft.transform.position = shaft.stuck + shaft.dir * (sink * (2f * _halfLength + 0.1f));
                if (sink >= 1f)
                {
                    shaft.gone = true;
                    shaft.transform.gameObject.SetActive(false);
                }
                _shafts[i] = shaft;
            }
            if (!anyLeft)
                Sleep();
        }

        private void Streak(Vector3 from, Vector3 to)
        {
            if (streak == null)
                return;
            Vector3 span = to - from;
            float length = span.magnitude;
            float want = length * streakPerMetre + _streakCarry;
            int count = Mathf.FloorToInt(want);
            _streakCarry = want - count;
            var emit = new ParticleSystem.EmitParams { applyShapeToPosition = false, velocity = Vector3.zero };
            for (int i = 0; i < count; ++i)
            {
                emit.position = from + span * ((i + Random.value) / Mathf.Max(1, count));
                streak.Emit(emit, 1);
            }
        }

        private void Land(Vector3 point)
        {
            var emit = new ParticleSystem.EmitParams { applyShapeToPosition = false };
            if (dust != null)
            {
                for (int i = 0; i < dustEach; ++i)
                {
                    Vector2 out2 = Random.insideUnitCircle * 0.6f;
                    emit.position = point + new Vector3(out2.x * 0.2f, 0.08f, out2.y * 0.2f);
                    emit.velocity = new Vector3(out2.x, Random.Range(0.35f, 0.8f), out2.y);
                    dust.Emit(emit, 1);
                }
            }
            if (chips != null)
            {
                for (int i = 0; i < chipsEach; ++i)
                {
                    Vector2 out2 = Random.insideUnitCircle.normalized * Random.Range(0.6f, 1.6f);
                    emit.position = point + Vector3.up * 0.03f;
                    emit.velocity = new Vector3(out2.x, Random.Range(1.6f, 3.2f), out2.y);
                    chips.Emit(emit, 1);
                }
            }
            if (impactSounds != null && impactSounds.Length > 0 && impactSoundEvery > 0 &&
                _landedCount++ % impactSoundEvery == 0)
                OneShotSound.PlayAt(impactSounds, point, soundVolume, 5f, 50f, 0.1f, impactPitch);
        }
    }
}
