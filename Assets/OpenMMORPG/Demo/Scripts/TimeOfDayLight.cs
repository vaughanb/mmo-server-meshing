using Insthync.ManagedUpdating;
using System.Collections.Generic;
using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// A fire that keeps the village's hours: lit as the dusk comes on, out again after
    /// dawn, and burning in between.
    ///
    /// It reads the hour off <see cref="DayNightSkyCycle"/>, which is already the one thing
    /// in the scene that knows what time it is, so a torch and the sky can never
    /// disagree about whether it is night. Nothing here is sent over the network: the
    /// clock is the server's, and every client lights its torches off the same hour.
    ///
    /// Nothing switches. The fire has a strength between out and full, and both the
    /// light and the flame follow it: the strength climbs with the dusk over a window of
    /// the night amount, and is also rate-limited in real seconds, so a torch never
    /// pops on in a single frame even when the clock jumps. Each torch also lights a
    /// little before or after its neighbours, taken from where it stands, because a
    /// dozen torches all catching in the same instant reads as a switch being thrown.
    ///
    /// **One tick for every fire.** The island has three dozen of these and the crypt
    /// nearly twenty, and each used to run an Update of its own every frame, in which it
    /// looked the hour up again and rewrote its light and its flames whether anything had
    /// changed or not. Now the fires keep a shared list, and one registration with the kit's
    /// update manager works out the night once a frame and hands it to each. A fire at rest -
    /// fully out, or fully lit with nothing wavering - costs that one comparison and writes
    /// nothing; only a fire that is fading or flickering touches its light, and the flames'
    /// emission is written only when the strength moves. Out of play mode (the update manager
    /// does not tick there) the editor's own update drives the same list, so the scene still
    /// lights to the sky's preview hour. A headless server, with no one to show a fire to,
    /// does none of it.
    /// </summary>
    [ExecuteAlways]
    public class TimeOfDayLight : MonoBehaviour
    {
        public enum Schedule
        {
            /// <summary>Lit through the night, out by day: a street torch.</summary>
            Night,
            /// <summary>Lit through the day, out at night: daylight through a window.</summary>
            Day,
            /// <summary>Never out: a campfire someone keeps fed.</summary>
            Always,
        }

        [Header("What burns")]
        [Tooltip("The light the fire throws. Its intensity is driven from here, so set the strength below rather than on the light.")]
        public Light lamp;
        [Tooltip("The flame, embers and smoke. Their emission is scaled with the fire's strength, so they build up and die down rather than start and stop.")]
        public ParticleSystem[] flames;

        [Header("When it burns")]
        public Schedule schedule = Schedule.Night;
        [Tooltip("How far into night the fire is half lit. 0 is full day, 1 full night, and 0.5 is the moment the sun touches the horizon.")]
        [Range(0f, 1f)] public float ignitesAt = 0.55f;
        [Tooltip("How much of the night amount the fade spreads across, either side of ignitesAt.")]
        [Range(0.02f, 0.5f)] public float ignitionSpread = 0.15f;
        [Tooltip("How far each torch's ignition drifts from ignitesAt, so neighbours do not all catch at once.")]
        [Range(0f, 0.3f)] public float stagger = 0.08f;
        [Tooltip("The least time, in real seconds, the fire takes to come up from nothing or die down to it.")]
        public float fadeSeconds = 8f;

        [Header("How it burns")]
        [Tooltip("The light's intensity when the fire is fully lit.")]
        public float intensity = 1.6f;
        [Tooltip("How much the light wavers, as a fraction of its intensity.")]
        [Range(0f, 1f)] public float flicker = 0.18f;
        public float flickerSpeed = 11f;
        [Tooltip("How far the light wanders, in metres, as the flame leans about.")]
        public float sway = 0.03f;

        /// <summary>How lit the fire is now, from 0 (out) to 1 (full).</summary>
        public float Lit { get { return _lit; } }

        private float _lit = -1f;
        /// <summary>The strength the light and flames were last written for; -1 before the first write.</summary>
        private float _burntAt = -1f;
        private float _seed;
        private Vector3 _lampHome;
        private float[] _rates;
        private static DayNightSkyCycle _sky;
        /// <summary>When the torches may next look for a sky, shared like the sky itself: one search a second between them.</summary>
        private static float _nextSkySearch;

        /// <summary>Every fire that is enabled, which the one tick walks.</summary>
        private static readonly List<TimeOfDayLight> s_fires = new List<TimeOfDayLight>();
        private static readonly Ticker s_ticker = new Ticker();
        private static bool s_tickerRegistered;

        private void OnEnable()
        {
            // Taken from where the torch stands so it is the same every time the scene
            // loads, and different from the torch next door.
            Vector3 p = transform.position;
            _seed = Mathf.Repeat(Mathf.Abs(p.x * 12.9898f + p.y * 78.233f + p.z * 37.719f), 1000f);
            if (lamp != null)
                _lampHome = lamp.transform.localPosition;
            if (flames != null)
            {
                _rates = new float[flames.Length];
                for (int i = 0; i < flames.Length; ++i)
                    _rates[i] = flames[i] == null ? 0f : flames[i].emission.rateOverTimeMultiplier;
            }
            _lit = -1f;
            _burntAt = -1f;

            if (Application.isBatchMode)
                return;
            s_fires.Add(this);
            if (Application.isPlaying && !s_tickerRegistered)
            {
                UpdateManager.Register(s_ticker);
                s_tickerRegistered = true;
            }
        }

        private void OnDisable()
        {
            s_fires.Remove(this);
            if (s_fires.Count == 0 && s_tickerRegistered)
            {
                UpdateManager.Unregister(s_ticker);
                s_tickerRegistered = false;
            }
        }

        /// <summary>
        /// Puts the fire straight into the state the hour calls for, with no fade. For
        /// the builder, so a scene is saved with its torches out at the noon it is
        /// previewed at rather than however the prefab happened to be left.
        /// </summary>
        public void Settle()
        {
            _lit = Target(CurrentNight());
            _burntAt = -1f;
            Burn(0f);
        }

        private sealed class Ticker : IManagedUpdate
        {
            public void ManagedUpdate()
            {
                TickAll();
            }
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        private static void HookEditorTick()
        {
            UnityEditor.EditorApplication.update += () =>
            {
                if (!Application.isPlaying)
                    TickAll();
            };
        }
#endif

        /// <summary>The night amount every fire is lit by this frame, worked out once for all of them.</summary>
        private static void TickAll()
        {
            if (s_fires.Count == 0)
                return;
            float night = CurrentNight();
            bool playing = Application.isPlaying;
            float time = playing ? Time.time : 0f;
            float deltaTime = playing ? Time.deltaTime : 0f;
            for (int i = s_fires.Count - 1; i >= 0; --i)
            {
                TimeOfDayLight fire = s_fires[i];
                if (fire == null)
                {
                    s_fires.RemoveAt(i);
                    continue;
                }
                fire.Tick(night, playing, time, deltaTime);
            }
        }

        /// <summary>
        /// How far into night the sky is, or -1 with no sky in the scene. Looked for at most
        /// once a second: a scene with no sky cycle (the menu stage has six night torches and
        /// none) used to search the whole scene every frame for each torch.
        /// </summary>
        private static float CurrentNight()
        {
            if (_sky == null && Time.realtimeSinceStartup >= _nextSkySearch)
            {
                _sky = FindAnyObjectByType<DayNightSkyCycle>();
                _nextSkySearch = Time.realtimeSinceStartup + 1f;
            }
            return _sky != null ? _sky.NightAmount(_sky.Hour) : -1f;
        }

        private void Tick(float night, bool playing, float time, float deltaTime)
        {
            float target = Target(night);
            if (playing && _lit >= 0f)
                _lit = Mathf.MoveTowards(_lit, target, deltaTime / Mathf.Max(0.1f, fadeSeconds));
            else
                _lit = target;

            // At rest: the strength has not moved since it was last written, and nothing in
            // the light wavers - out, or burning with no flicker or sway (a window's daylight).
            bool wavering = playing && _lit > 0.005f && lamp != null && (flicker > 0f || sway > 0f);
            if (_lit == _burntAt && !wavering)
                return;
            Burn(time);
        }

        /// <summary>Where the fire is heading: how lit the hour says it should be.</summary>
        private float Target(float night)
        {
            if (schedule == Schedule.Always)
                return 1f;
            if (night < 0f)
                return schedule == Schedule.Night ? 1f : 0f;

            float at = ignitesAt + (Mathf.Repeat(_seed * 0.618f, 1f) - 0.5f) * 2f * stagger;
            float lit = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(at - ignitionSpread, at + ignitionSpread, night));
            return schedule == Schedule.Night ? lit : 1f - lit;
        }

        private void Burn(float time)
        {
            if (lamp != null)
            {
                float waver = 1f;
                Vector3 lean = Vector3.zero;
                if (time > 0f && flicker > 0f)
                {
                    // Two noises, one quick and one slow, so it neither strobes nor
                    // breathes. Both are Perlin, which never jumps.
                    float quick = Mathf.PerlinNoise(time * flickerSpeed, _seed);
                    float slow = Mathf.PerlinNoise(time * flickerSpeed * 0.23f, _seed + 17f);
                    waver = 1f + flicker * ((quick * 0.65f + slow * 0.35f) * 2f - 1f);
                    lean = new Vector3(
                        Mathf.PerlinNoise(time * 2.3f, _seed + 5f) - 0.5f,
                        Mathf.PerlinNoise(time * 3.1f, _seed + 9f) - 0.5f,
                        Mathf.PerlinNoise(time * 2.7f, _seed + 13f) - 0.5f) * (2f * sway);
                }
                lamp.intensity = intensity * _lit * waver;
                lamp.enabled = _lit > 0.005f;
                lamp.transform.localPosition = _lampHome + lean;
            }

            bool strengthMoved = _lit != _burntAt;
            _burntAt = _lit;
            // Out of play mode the flames are left alone: the editor does not simulate
            // them unless they are selected, and driving their modules would write
            // overrides into every torch in the saved scene. In play they follow the
            // strength, so only a change in it touches them.
            if (!Application.isPlaying || flames == null || _rates == null || !strengthMoved)
                return;
            for (int i = 0; i < flames.Length; ++i)
            {
                ParticleSystem flame = flames[i];
                if (flame == null)
                    continue;
                ParticleSystem.EmissionModule emission = flame.emission;
                emission.rateOverTimeMultiplier = _rates[i] * _lit;
                if (_lit > 0.001f)
                {
                    if (!flame.isPlaying)
                        flame.Play(false);
                }
                else if (flame.isPlaying)
                {
                    // Stop feeding it and let what is burning burn out, rather than
                    // clearing the flame in one frame.
                    flame.Stop(false, ParticleSystemStopBehavior.StopEmitting);
                }
            }
        }
    }
}
