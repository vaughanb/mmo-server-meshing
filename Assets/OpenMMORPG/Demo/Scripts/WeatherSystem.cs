using System;
using System.Collections.Generic;
using Insthync.AudioManager;
using UnityEngine;
using UnityEngine.Rendering;
using Random = UnityEngine.Random;

namespace MultiplayerARPG
{
    /// <summary>
    /// The island's weather: showers that come and go on their own, and everything that goes with one.
    ///
    /// **When it rains is a function of the server's clock and nothing else.** Time is cut into
    /// periods (<see cref="periodMinutes"/>), each period is dealt a shower or not by a hash of the
    /// period's number and <see cref="seed"/>, and a shower is where in the period it starts, how long
    /// it runs and how hard it comes down - all from that same hash. Nothing is sent over the network
    /// and nothing is remembered: the kit already keeps every client's idea of the server's time in
    /// step (<c>ServerTimestamp</c>, the same offset-corrected clock the day cycle rides on), so two
    /// players standing next to each other see the same sky, a player who logs in mid-shower walks
    /// into it, and a restart does not reroll the weather. With no network (the editor, a
    /// single-player test) it reads the local UTC clock, which the server's is anyway.
    ///
    /// **A shower is cloud first, rain second.** The sky closes over <see cref="cloudLeadSeconds"/>
    /// before the first drop and stays grey <see cref="cloudLagSeconds"/> after the last, because rain
    /// falling out of a blue sky reads as a bug and a day that goes from storm to sunshine in one
    /// frame reads as a light switch. The cloud is handed to <see cref="DayNightSkyCycle"/> as
    /// <see cref="DayNightSkyCycle.Overcast"/>, which greys the sky, sun, ambient and haze together - the
    /// cycle already owns every one of those writes, so the weather asks and the cycle does.
    ///
    /// **The drops are placed, not simulated.** Particle-system collision was the obvious way to make
    /// rain stop at things, and it stops at what the physics world says is there - which, for a sea
    /// that is a trigger volume, a village of houses and a hill, is not where anyone expects. Instead
    /// each drop is cast down its own column from above the camera: the ray finds the ground, a roof
    /// or the sea surface, and the drop is given exactly the lifetime it takes to arrive. So rain
    /// never falls inside a house (the roof is in the way of every column that would), never under the
    /// sea, and the splash is made at the place the drop lands - by a death sub-emitter on the
    /// streak system, which is why the lifetime has to be exact. Around sixty rays a frame at the
    /// heaviest, which is nothing to a physics scene this size.
    ///
    /// Presentation only, like <see cref="FoliageWind"/>: nothing here is read by the simulation, and a
    /// headless map server switches it off.
    /// </summary>
    [DisallowMultipleComponent]
    public class WeatherSystem : MonoBehaviour
    {
        public enum WeatherMode
        {
            /// <summary>The schedule decides.</summary>
            Automatic,
            /// <summary>Dry, whatever the schedule says.</summary>
            ForceClear,
            /// <summary>Raining at <see cref="forcedIntensity"/>, whatever the schedule says.</summary>
            ForceRain,
        }

        [Header("When it rains")]
        [Tooltip("Automatic follows the schedule below. The other two override it, for testing and for photographs. Only the play-mode menu items 'Open MMORPG > Demo > Weather' should need to touch this.")]
        public WeatherMode mode = WeatherMode.Automatic;
        [Range(0.05f, 1f)] public float forcedIntensity = 1f;
        [Tooltip("Changing it deals a different run of weather. Every client and the server must agree, so it is a scene setting rather than something rolled at startup.")]
        public int seed = 7;
        [Tooltip("Time is cut into periods this long, and each holds at most one shower.")]
        public float periodMinutes = 15f;
        [Tooltip("The chance a period holds a shower.")]
        [Range(0f, 1f)] public float showerChance = 0.6f;
        [Tooltip("How long a shower lasts, from the first drop to the last, in minutes.")]
        public float minShowerMinutes = 3f;
        public float maxShowerMinutes = 8f;
        [Tooltip("The softest a shower's peak can be, as a fraction of a downpour. Each shower draws its own between this and 1.")]
        [Range(0.1f, 1f)] public float minPeak = 0.35f;
        [Tooltip("Seconds the rain takes to build to its peak and to die away.")]
        public float rampSeconds = 40f;
        [Tooltip("Seconds before the first drop that the sky starts closing over.")]
        public float cloudLeadSeconds = 50f;
        [Tooltip("Seconds after the last drop that the cloud takes to clear.")]
        public float cloudLagSeconds = 70f;
        [Tooltip("The longest the rain and cloud take to follow what is wanted. Smooths a forced change and the first frame after a hitch; the schedule's own ramps are slower than this.")]
        public float easeSeconds = 10f;

        [Header("The drops")]
        [Tooltip("The streak system the drops are emitted into.")]
        public ParticleSystem streaks;
        [Tooltip("The ring system a drop's splash is emitted into where it lands. Left empty, drops land without splashing.")]
        public ParticleSystem splashes;
        [Tooltip("The share of drops that land on level ground or the sea that make a splash. Every drop would be more rings than the eye can use.")]
        [Range(0f, 1f)] public float splashChance = 0.4f;
        [Tooltip("The width of a ring when it has opened, in metres, drawn between these two.")]
        public Vector2 splashSize = new Vector2(0.16f, 0.30f);
        [Tooltip("Drops a second at a full downpour, before the density scale.")]
        public int maxDropsPerSecond = 3800;
        [Range(0.1f, 2f)] public float density = 1f;
        [Tooltip("Metres from the camera the rain reaches.")]
        public float radius = 19f;
        [Tooltip("How far ahead of the camera the middle of the rain sits, so the view in front is wetter than the view behind.")]
        public float forwardBias = 7f;
        [Tooltip("Metres above the camera the drops start. Higher means a hill beside the camera cannot swallow the start of a column.")]
        public float spawnHeight = 20f;
        public float fallSpeed = 24f;
        [Tooltip("The width of a drop, in metres, drawn between these two.")]
        public Vector2 dropWidth = new Vector2(0.016f, 0.030f);
        [Tooltip("How slanted the rain is in a full downpour in the full wind: metres sideways per metre down.")]
        public float maxSlant = 0.32f;
        [Range(0f, 1f)] public float dropAlpha = 0.6f;
        [Tooltip("What stops rain: the ground, roofs, walls. Triggers are ignored. The sea is not here - it is a trigger volume, so it is a plane at Sea Level.")]
        public LayerMask blockers = ~0;
        [Tooltip("World height of the sea surface.")]
        public float seaLevel = 0f;

        [Header("Sky and wind")]
        [Tooltip("Handed the cloud. Left empty, the one in the scene is found.")]
        public DayNightSkyCycle skyCycle;
        [Tooltip("Handed the storm. Left empty, the one in the scene is found.")]
        public FoliageWind wind;
        [Tooltip("How much harder the wind blows in a full downpour, as a fraction of the wind as tuned.")]
        public float windBoost = 0.7f;

        [Header("Sound")]
        [Tooltip("The rain loop. The builder wires it from the RainAndThunder audio family; with none, the rain is silent.")]
        public AudioSource loopSource;
        [Tooltip("Applies the ambient volume setting every frame. On the same object as the source.")]
        public AmbientSoundLoop loop;
        [Tooltip("Muffles the loop under a roof and under the sea. On the same object as the source.")]
        public AudioLowPassFilter lowPass;
        [Range(0f, 1f)] public float loopVolume = 0.85f;
        [Tooltip("What the loop is multiplied by with a roof overhead.")]
        [Range(0f, 1f)] public float shelteredVolume = 0.5f;
        [Tooltip("The low-pass cut-off, in Hz, with a roof overhead: the patter on a roof, not the rain in the open.")]
        public float shelteredCutoff = 1700f;
        [Tooltip("The low-pass cut-off, in Hz, with the camera under the sea.")]
        public float submergedCutoff = 450f;
        [Tooltip("Ambience beds, by object name, that the rain talks over. Birdsong in a downpour is the one thing that gives it away.")]
        public string[] duckedBeds = { "Nature" };
        [Range(0f, 1f)] public float duckAmount = 0.65f;

        /// <summary>How hard it is raining now, 0 to 1.</summary>
        public float Rain { get { return _rain; } }

        /// <summary>How overcast it is now, 0 to 1. Leads the rain and outlasts it.</summary>
        public float Cloud { get { return _cloud; } }

        /// <summary>How much of the listener's sky is roofed over: 0 in the open, 1 indoors.</summary>
        public float Shelter { get { return _shelter; } }

        /// <summary>The camera the rain is placed around. Left unset, the main camera; set by tests that render without one.</summary>
        [NonSerialized] public Camera viewer;

        private const int CoverProbes = 5;
        private const float CoverReach = 12f;
        private const int MaxDropsPerFrame = 160;

        /// <summary>
        /// How far ahead of its particle a stretched billboard's leading end is drawn, as seconds of the
        /// drop's own travel. Measured by baking the quad (BakeMesh) at several speeds, sizes and
        /// slants: always 10.1 ms of travel - 24 cm at 24 m/s - whatever the streak's width or length. A
        /// drop given the lifetime it takes to reach the surface is therefore drawn 24 cm into it for its
        /// last frame, which from inside a house is a stick coming through the ceiling. Dying this much
        /// early puts the leading end on the surface instead.
        /// </summary>
        public const float HeadLead = 0.0101f;

        /// <summary>
        /// How level a surface has to be to be splashed on: its normal must point up by at least this
        /// (0.9 is within about 25 degrees of flat). The splash is a ring lying flat, and on a steeper
        /// surface it cannot lie on it - half sinks into a pitched roof and half hangs in the air off a
        /// wall, which from inside is a ring floating in the room. A drop that strikes anything steeper
        /// still ends there, with no splash.
        /// </summary>
        private const float SplashNormalY = 0.9f;

        /// <summary>The most splashes waiting to happen at once; a hitch cannot make the queue grow without end.</summary>
        private const int MaxPendingSplashes = 8192;

        /// <summary>How far up from where a drop lands to look for something over it.</summary>
        private const float OverheadReach = 30f;
        private static readonly Vector3[] s_cover =
        {
            Vector3.zero, new Vector3(1.4f, 0f, 0f), new Vector3(-1.4f, 0f, 0f), new Vector3(0f, 0f, 1.4f), new Vector3(0f, 0f, -1.4f),
        };

        private bool _ready;
        private bool _seeded;
        private float _rain;
        private float _cloud;
        private float _carry;
        private float _shelter;
        private float _coverTarget;
        private float _under;
        private float _nextCover;
        private float _nextSearch;
        private Camera _camera;
        private AudioListener _listener;
        private Terrain _terrain;
        private float _terrainY;
        private Vector3 _fall = Vector3.down;
        private Color _dropColour = Color.white;
        private AmbientSoundLoop[] _ducked;

        // Splashes waiting for their drop to land: where, when (on _clock) and what colour.
        private Vector3[] _splashAt = new Vector3[1024];
        private float[] _splashDue = new float[1024];
        private Color[] _splashColour = new Color[1024];
        private int _splashCount;
        private float _clock;

        private static bool HasGraphics
        {
            get { return !Application.isBatchMode && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null; }
        }

        private void OnEnable()
        {
            _ready = false;
            _seeded = false;
            // A map server draws nothing and hears nothing. Nothing else reads the weather.
            if (!HasGraphics)
                enabled = false;
        }

        private void OnDisable()
        {
            if (skyCycle != null)
                skyCycle.ResetOvercast();
            if (wind != null)
                wind.StormBoost = 0f;
            if (loop != null)
                loop.RuntimeGain = 0f;
            RestoreBeds();
            if (Application.isPlaying && loopSource != null)
                loopSource.Stop();
            _rain = 0f;
            _cloud = 0f;
        }

        private void Update()
        {
            Tick(Time.deltaTime, true);
        }

        /// <summary>Forces the weather. Takes effect over <see cref="easeSeconds"/>.</summary>
        public void SetMode(WeatherMode wanted)
        {
            mode = wanted;
        }

        /// <summary>
        /// One step of the weather. <see cref="Update"/> calls it every frame; it is public so a test can
        /// step the weather in the editor, where nothing ticks, and photograph the result.
        /// </summary>
        /// <param name="simulate">False to move the weather without raining: the sky and wind follow, but
        /// no drops are made and nothing is heard.</param>
        public void Tick(float dt, bool simulate)
        {
            EnsureReady();

            float wantRain, wantCloud;
            Wanted(out wantRain, out wantCloud);
            if (!_seeded)
            {
                // Arriving mid-shower is arriving in it: the first frame is the weather as it is.
                _seeded = true;
                _rain = wantRain;
                _cloud = wantCloud;
            }
            else
            {
                float step = dt / Mathf.Max(easeSeconds, 0.1f);
                _rain = Mathf.MoveTowards(_rain, wantRain, step);
                _cloud = Mathf.MoveTowards(_cloud, wantCloud, step);
            }

            if (skyCycle != null)
                skyCycle.Overcast = _cloud;
            if (wind != null)
                wind.StormBoost = windBoost * _rain;

            Transform at = ResolveViewer();
            if (at == null)
                return;

            Vector3 eye = at.position;
            _under = Mathf.MoveTowards(_under, eye.y < seaLevel ? 1f : 0f, dt * 5f);

            if (simulate)
            {
                UpdateFall();
                Emit(dt, at);
                UpdateSound(dt, eye);
                UpdateBeds();
            }
        }

        // ---- the schedule -------------------------------------------------------------------------

        /// <summary>Whether it is raining and how overcast it is, as the mode and the schedule have them.</summary>
        private void Wanted(out float rain, out float cloud)
        {
            switch (mode)
            {
                case WeatherMode.ForceRain:
                    rain = forcedIntensity;
                    cloud = 1f;
                    return;
                case WeatherMode.ForceClear:
                    rain = 0f;
                    cloud = 0f;
                    return;
            }
            Sample(NowSeconds(), out rain, out cloud);
        }

        /// <summary>
        /// The time the weather is a function of: the server's, where there is one, else this machine's
        /// UTC. The two are the same clock - the kit's <c>ServerTimestamp</c> is local UTC plus an offset
        /// it measures against the server - so this switches between them without a visible step.
        /// </summary>
        public static double NowSeconds()
        {
            if (Application.isPlaying)
            {
                BaseGameNetworkManager manager = BaseGameNetworkManager.Singleton;
                if (manager != null && manager.IsNetworkActive)
                    return manager.ServerTimestamp * 0.001;
            }
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 0.001;
        }

        /// <summary>One shower: when it starts and ends, in seconds on the server's clock, and how hard it peaks.</summary>
        public struct Shower
        {
            public double start;
            public double end;
            public float peak;
        }

        /// <summary>
        /// The shower dealt to a period, if any. A shower is placed so that its cloud, at both ends, lies
        /// inside its own period: it starts at least <see cref="cloudLeadSeconds"/> in and ends at least
        /// <see cref="cloudLagSeconds"/> before the next. That is what lets <see cref="Sample"/> look at one
        /// period and no other, and what keeps two showers from fighting over one patch of sky.
        /// </summary>
        public bool ShowerOf(long period, out Shower shower)
        {
            shower = default(Shower);
            if (Unit(period, 0) >= showerChance)
                return false;

            double length = Math.Max(periodMinutes, 1f) * 60.0;
            double lo = Math.Min(minShowerMinutes, maxShowerMinutes) * 60.0;
            double hi = Math.Max(minShowerMinutes, maxShowerMinutes) * 60.0;
            // Never longer than the period can hold with its cloud either side.
            double duration = Math.Min(lo + (hi - lo) * Unit(period, 1), Math.Max(length - cloudLeadSeconds - cloudLagSeconds, 30.0));
            double room = Math.Max(length - duration - cloudLeadSeconds - cloudLagSeconds, 0.0);

            shower.start = period * length + cloudLeadSeconds + room * Unit(period, 2);
            shower.end = shower.start + duration;
            shower.peak = Mathf.Lerp(minPeak, 1f, (float)Unit(period, 3));
            return true;
        }

        /// <summary>The rain and the cloud at a moment on the server's clock, from the schedule alone.</summary>
        public void Sample(double seconds, out float rain, out float cloud)
        {
            rain = 0f;
            cloud = 0f;
            double length = Math.Max(periodMinutes, 1f) * 60.0;
            long period = (long)Math.Floor(seconds / length);

            Shower s;
            if (!ShowerOf(period, out s))
                return;

            float ramp = Mathf.Max(rampSeconds, 1f);
            float lead = Mathf.Max(cloudLeadSeconds, 1f);
            float lag = Mathf.Max(cloudLagSeconds, 1f);
            float rainEnvelope = Smooth((float)((seconds - s.start) / ramp)) * Smooth((float)((s.end - seconds) / ramp));
            float cloudEnvelope = Smooth((float)((seconds - (s.start - lead)) / lead)) * Smooth((float)((s.end + lag - seconds) / lag));

            // A shower is not one steady strength: it swells and eases over half a minute or so.
            float swell = Mathf.Lerp(0.78f, 1f, Mathf.PerlinNoise((float)(seconds / 35.0 % 4096.0), (float)(period % 997) * 0.37f));
            rain = rainEnvelope * s.peak * swell;
            // A light shower does not darken the day as a downpour does.
            cloud = cloudEnvelope * Mathf.Lerp(0.55f, 1f, s.peak);
        }

        /// <summary>The next showers from a moment on, soonest first, for the log and the inspector.</summary>
        public List<Shower> Forecast(double fromSeconds, int count)
        {
            var found = new List<Shower>();
            double length = Math.Max(periodMinutes, 1f) * 60.0;
            long period = (long)Math.Floor(fromSeconds / length);
            for (long p = period; found.Count < count && p < period + 4096; p++)
            {
                Shower s;
                if (ShowerOf(p, out s) && s.end > fromSeconds)
                    found.Add(s);
            }
            return found;
        }

        private static float Smooth(float x)
        {
            if (x <= 0f)
                return 0f;
            if (x >= 1f)
                return 1f;
            return x * x * (3f - 2f * x);
        }

        /// <summary>A repeatable number in [0, 1) for a period and a purpose: the same on every machine.</summary>
        private double Unit(long period, int stream)
        {
            unchecked
            {
                ulong h = Mix((ulong)(uint)seed * 0x9E3779B97F4A7C15UL + Mix((ulong)period) + ((ulong)(uint)stream << 40));
                return (h >> 11) * (1.0 / 9007199254740992.0);
            }
        }

        private static ulong Mix(ulong x)
        {
            unchecked
            {
                x += 0x9E3779B97F4A7C15UL;
                x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
                x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
                return x ^ (x >> 31);
            }
        }

        // ---- finding things -----------------------------------------------------------------------

        private void EnsureReady()
        {
            if (_ready)
                return;
            _ready = true;

            if (skyCycle == null)
                skyCycle = FindFirstObjectByType<DayNightSkyCycle>();
            if (wind == null)
                wind = FindFirstObjectByType<FoliageWind>();
            if (loop == null && loopSource != null)
                loop = loopSource.GetComponent<AmbientSoundLoop>();
            if (lowPass == null && loopSource != null)
                lowPass = loopSource.GetComponent<AudioLowPassFilter>();

            if (loopSource != null)
            {
                loopSource.loop = true;
                loopSource.playOnAwake = false;
                loopSource.spatialBlend = 0f;
                loopSource.volume = 0f;
            }

            if (streaks != null && !streaks.isPlaying)
                streaks.Play();
            if (splashes != null && !splashes.isPlaying)
                splashes.Play();

            _terrain = Terrain.activeTerrain;
            _terrainY = _terrain != null ? _terrain.transform.position.y : 0f;
            FindBeds();
        }

        private Transform ResolveViewer()
        {
            if (viewer != null)
                return viewer.transform;
            if (_camera == null || !_camera.isActiveAndEnabled)
            {
                if (Time.realtimeSinceStartup < _nextSearch)
                    return null;
                _nextSearch = Time.realtimeSinceStartup + 0.5f;
                _camera = Camera.main;
            }
            return _camera != null ? _camera.transform : null;
        }

        // ---- the drops ----------------------------------------------------------------------------

        private void UpdateFall()
        {
            // The rain leans with the wind, and more in a downpour than in a drizzle. Without a wind
            // there is a faint lean anyway: rain that is exactly vertical looks like a texture.
            Vector2 w = wind != null ? wind.Direction : new Vector2(0.5f, 0.85f);
            float lean = maxSlant * (0.25f + 0.75f * _rain) * (wind != null ? 1f : 0.3f);
            _fall = new Vector3(w.x * lean, -1f, w.y * lean).normalized;

            // What a streak looks like depends on the light. Taken from the haze, which the sky cycle
            // already moves with the hour and the cloud: bright by day, and nearly gone at night, where
            // a streak of unlit white would glow like a neon sign.
            Color fog = RenderSettings.fogColor;
            float brightness = Mathf.Clamp01(fog.grayscale * 2.1f + 0.08f);
            Color tint = Color.Lerp(fog, Color.white, 0.6f);
            _dropColour = new Color(tint.r * brightness, tint.g * brightness, tint.b * brightness, dropAlpha);
        }

        private void Emit(float dt, Transform eye)
        {
            if (streaks == null || dt <= 0f)
                return;
            _clock += dt;
            // Splashes are made at the end of the drop's fall, which is later than the frame it was
            // dropped in. Flushed first, so a ring is never held back by the frame that made no new drops.
            FlushSplashes();
            if (_rain < 0.003f || _under > 0.5f)
            {
                // Under the sea or out of the rain, nothing is waiting to land on anything the viewer sees.
                if (_under > 0.5f)
                    _splashCount = 0;
                return;
            }

            _carry += maxDropsPerSecond * density * Mathf.Pow(_rain, 0.85f) * dt;
            int count = Mathf.Min((int)_carry, MaxDropsPerFrame);
            _carry = Mathf.Min(_carry - count, 1f);

            Vector3 forward = eye.forward;
            forward.y = 0f;
            forward = forward.sqrMagnitude < 1e-4f ? Vector3.forward : forward.normalized;
            Vector3 middle = eye.position + forward * forwardBias;
            float reach = (spawnHeight + 30f) / Mathf.Max(-_fall.y, 0.1f);

            for (int i = 0; i < count; i++)
            {
                Vector2 d = Random.insideUnitCircle * radius;
                var start = new Vector3(middle.x + d.x, eye.position.y + spawnHeight, middle.z + d.y);

                // Inside a hill the ray would find nothing above it and the drop would fall through
                // the hill to whatever is underneath.
                if (_terrain != null && _terrain.SampleHeight(start) + _terrainY > start.y - 0.5f)
                    continue;

                float distance = reach;
                Vector3 normal = Vector3.up;
                RaycastHit hit;
                if (Physics.Raycast(start, _fall, out hit, reach, blockers, QueryTriggerInteraction.Ignore))
                {
                    distance = hit.distance;
                    normal = hit.normal;
                }
                // The sea is a plane: it is a trigger in the physics world, which rain cannot land on.
                float toSea = (seaLevel - start.y) / _fall.y;
                if (toSea > 0f && toSea < distance)
                {
                    distance = toSea;
                    normal = Vector3.up;
                }
                if (distance < 1.5f)
                    continue;

                // Rain does not land under cover. The column above the drop is clear - that is how it
                // got here - but a slanted drop can still slip in under an eave, through a doorway or in
                // at a window, and land on a floor with a roof over it. Those drops are not made: this
                // is what keeps the inside of a house dry, and it is the one check that does not care
                // how the building is put together. Looked for from just off the surface, so a roof's own
                // collider is not what it finds.
                if (Physics.Raycast(start + _fall * distance + normal * 0.12f, Vector3.up, OverheadReach, blockers, QueryTriggerInteraction.Ignore))
                    continue;

                float speed = fallSpeed * Random.Range(0.88f, 1.12f);
                Color c = _dropColour;
                c.a *= Random.Range(0.55f, 1f);
                var p = new ParticleSystem.EmitParams
                {
                    position = start,
                    velocity = _fall * speed,
                    // Dies a streak's head lead early, so that its leading end is on the surface when it
                    // does and not 24 cm inside it.
                    startLifetime = Mathf.Max(distance / speed - HeadLead, 0.02f),
                    startSize = Random.Range(dropWidth.x, dropWidth.y),
                    startColor = c,
                };
                streaks.Emit(p, 1);

                // A ring where it lands, on level ground, a flat roof or the sea - not on a slope, where
                // a ring lying flat is half in the surface or half in the air. Placed at the hit point
                // itself and queued for the moment the drop gets there. (A death sub-emitter on the
                // streak puts it where the particle is when it dies, which is up to a whole frame of fall
                // past the surface - 40 cm at 60 fps - so the rings sank into roofs at random.)
                if (splashes != null && normal.y >= SplashNormalY && Random.value < splashChance)
                    QueueSplash(start + _fall * distance + normal * 0.03f, _clock + distance / speed, c);
            }
        }

        private void QueueSplash(Vector3 at, float due, Color colour)
        {
            if (_splashCount >= MaxPendingSplashes)
                return;
            if (_splashCount == _splashAt.Length)
            {
                int size = _splashAt.Length * 2;
                Array.Resize(ref _splashAt, size);
                Array.Resize(ref _splashDue, size);
                Array.Resize(ref _splashColour, size);
            }
            _splashAt[_splashCount] = at;
            _splashDue[_splashCount] = due;
            _splashColour[_splashCount] = colour;
            _splashCount++;
        }

        /// <summary>Makes the ring for every queued drop that has landed. Order does not matter, so a finished entry is replaced by the last.</summary>
        private void FlushSplashes()
        {
            if (splashes == null)
            {
                _splashCount = 0;
                return;
            }
            for (int i = 0; i < _splashCount; i++)
            {
                if (_splashDue[i] > _clock)
                    continue;
                var ring = new ParticleSystem.EmitParams
                {
                    position = _splashAt[i],
                    startLifetime = Random.Range(0.22f, 0.38f),
                    startSize = Random.Range(splashSize.x, splashSize.y),
                    startColor = _splashColour[i],
                };
                splashes.Emit(ring, 1);
                _splashCount--;
                _splashAt[i] = _splashAt[_splashCount];
                _splashDue[i] = _splashDue[_splashCount];
                _splashColour[i] = _splashColour[_splashCount];
                i--;
            }
        }

        // ---- the sound ----------------------------------------------------------------------------

        private void UpdateSound(float dt, Vector3 eye)
        {
            if (loopSource == null || loopSource.clip == null)
                return;

            float body = _rain <= 0.0005f ? 0f : Mathf.Pow(_rain, 0.6f);
            if (body > 0f && !loopSource.isPlaying)
            {
                // From somewhere different every shower. The loop carries its thunder with it, so
                // always starting it at the top would crack at the same second of every rain.
                loopSource.time = Random.value * Mathf.Max(loopSource.clip.length - 0.1f, 0f);
                loopSource.Play();
            }

            // What is overhead. From the character, which is where the player is standing, not the
            // camera, which sits a few metres back and can be outside the door while they are in it.
            // Real time, not game time: a paused game (timeScale 0) still wants to know it is indoors.
            // Only while there is rain to hear: dry, the answer changes nothing, and the first probe
            // of a shower comes within a few frames of its first drop.
            if (body > 0f && Time.realtimeSinceStartup >= _nextCover)
            {
                _nextCover = Time.realtimeSinceStartup + 0.15f;
                Vector3 at = ListenerPosition(eye);
                int covered = 0;
                for (int i = 0; i < CoverProbes; i++)
                {
                    if (Physics.Raycast(at + Vector3.up * 0.8f + s_cover[i], Vector3.up, CoverReach, blockers, QueryTriggerInteraction.Ignore))
                        covered++;
                }
                _coverTarget = covered / (float)CoverProbes;
            }
            _shelter = Mathf.MoveTowards(_shelter, _coverTarget, dt * 2.5f);

            float volume = loopVolume * body * Mathf.Lerp(1f, shelteredVolume, _shelter) * (1f - 0.85f * _under);
            if (loop != null)
                loop.RuntimeGain = volume;
            else
                loopSource.volume = volume;

            if (lowPass != null)
                lowPass.cutoffFrequency = Mathf.Lerp(Mathf.Lerp(22000f, shelteredCutoff, _shelter), submergedCutoff, _under);

            if (body <= 0f && loopSource.isPlaying)
                loopSource.Stop();
        }

        private Vector3 ListenerPosition(Vector3 fallback)
        {
            if (_listener == null || !_listener.isActiveAndEnabled)
            {
                foreach (AudioListener listener in FindObjectsByType<AudioListener>(FindObjectsSortMode.None))
                {
                    if (listener.isActiveAndEnabled)
                    {
                        _listener = listener;
                        break;
                    }
                }
            }
            return _listener != null ? _listener.transform.position : fallback;
        }

        private void FindBeds()
        {
            _ducked = null;
            if (duckedBeds == null || duckedBeds.Length == 0)
                return;
            var beds = new List<AmbientSoundLoop>();
            foreach (AmbientSoundLoop candidate in FindObjectsByType<AmbientSoundLoop>(FindObjectsSortMode.None))
            {
                if (candidate != loop && Array.IndexOf(duckedBeds, candidate.name) >= 0)
                    beds.Add(candidate);
            }
            _ducked = beds.ToArray();
        }

        private void UpdateBeds()
        {
            if (_ducked == null)
                return;
            float duck = 1f - duckAmount * Mathf.Clamp01(_rain * 1.4f);
            for (int i = 0; i < _ducked.Length; i++)
            {
                if (_ducked[i] != null)
                    _ducked[i].RuntimeGain = duck;
            }
        }

        private void RestoreBeds()
        {
            if (_ducked == null)
                return;
            for (int i = 0; i < _ducked.Length; i++)
            {
                if (_ducked[i] != null)
                    _ducked[i].RuntimeGain = 1f;
            }
            _ducked = null;
        }
    }
}
