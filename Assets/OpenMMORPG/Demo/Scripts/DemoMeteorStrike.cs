using Insthync.AudioManager;
using UnityEngine;

namespace MultiplayerARPG.Demo
{
    /// <summary>
    /// The meteor itself: a fireball that comes down out of the sky onto the circle a Meteor
    /// leaves, and bursts where it lands.
    ///
    /// Until 2026-09-24 the skill had no meteor at all - the mage finished the cast and a glowing
    /// disc appeared on the ground, with a spray of sparks, and whatever stood in it was hurt a
    /// second later with nothing to show for why. This sits on the Meteor's area entity (built by
    /// DemoSkillBuilder, dressed by DemoSkillEffectBuilder), so it arrives with the area on every
    /// client, and it is a client-side show only: the damage is the kit's.
    ///
    /// **The strike is timed to the damage.** The area bites once, `applyDuration` after it
    /// appears, and the builder makes <see cref="fallSeconds"/> that same number - so the rock
    /// lands on the frame the numbers come up. The area outlives the bite by less than a second
    /// fall, so it cannot bite twice, and long enough for what the fireball shed on the way down
    /// (smoke, embers - world-space particles, which vanish with the area) to burn out.
    ///
    /// **The explosion is its own effect, not part of the area.** The kit plays every particle
    /// system under a damage entity the moment it spawns, and puts the entity away the moment its
    /// time is up; an explosion parented here would go off at the cast and be cut short after
    /// the strike. So the burst is <see cref="impactEffect"/>, a pooled GameEffect fetched at the
    /// point of impact, which plays out on its own clock.
    ///
    /// **The sound rides the fall.** `Meteor.wav` is a rising whoosh that peaks on a boom
    /// <see cref="soundImpactAt"/> seconds in, so it is started that long before the strike and
    /// played from where the rock will land.
    ///
    /// Its late update runs from the area appearing until the rock has struck and the sound is
    /// away, then lets go until the pool hands the area out again
    /// (<see cref="WakeableLateUpdateBehaviour"/>).
    /// </summary>
    public class DemoMeteorStrike : WakeableLateUpdateBehaviour
    {
        [Tooltip("Seconds from the area appearing to the rock striking the ground. Built to the " +
                 "area's applyDuration, which is when the damage lands.")]
        public float fallSeconds = 1.2f;
        [Tooltip("How high above the point of impact the fall begins, in metres.")]
        public float height = 16f;
        [Tooltip("How far out from the point of impact the fall begins, on the level, in metres.")]
        public float lead = 20f;
        [Tooltip("Where the fall begins, in degrees round from straight back toward the caster: " +
                 "180 is beyond the target, dead ahead. Mirrored to either side at random.")]
        public float approachYaw = 130f;
        [Tooltip("The falling fireball. Moved by this; everything under it is its look.")]
        public Transform body;
        [Tooltip("A light carried by the fireball, brightening as it comes down, so the ground and " +
                 "anyone standing on it light up before the rock itself is in view.")]
        public Light glow;
        [Tooltip("The glow light's intensity as the rock lands; it starts at a third of this. High, " +
                 "because URP's point light falls off with the square of distance: at 4 the " +
                 "ground under a rock five metres up got a sixth of that, and nothing showed.")]
        public float glowIntensity = 45f;
        [Tooltip("The circle the area lays on the ground, the warning of where the rock will " +
                 "land. Put out when it lands, so the warning does not outlast what it warned of.")]
        public Renderer telegraph;

        [Header("Impact")]
        [Tooltip("The explosion, fetched from the kit's pool where the rock lands.")]
        public GameEffect impactEffect;
        [Tooltip("Played from the point of impact, one picked at random.")]
        public AudioClip[] sounds = new AudioClip[0];
        [Tooltip("Seconds into the sound at which it booms, so the boom can land with the rock.")]
        public float soundImpactAt = 0.85f;
        [Tooltip("The local camera is jolted by a strike within this many metres of what it follows.")]
        public float joltRange = 22f;
        [Tooltip("Degrees the camera is knocked by a strike at its feet.")]
        public float jolt = 2.2f;

        private float _elapsed;
        private bool _falling;
        private bool _struck;
        private bool _sounded;
        private Vector3 _from;
        private Vector3 _to;
        private TrailRenderer[] _trails;
        private ParticleSystem[] _particles;

        private void Awake()
        {
            // A dedicated server spawns the area to do its damage and draws nothing.
            if (Application.isBatchMode)
            {
                enabled = false;
                return;
            }
            if (body != null)
            {
                _trails = body.GetComponentsInChildren<TrailRenderer>(true);
                _particles = body.GetComponentsInChildren<ParticleSystem>(true);
            }
        }

        private void OnEnable()
        {
            // The area comes from a pool, so everything is reset here, and the fall itself is set
            // up on the first frame rather than now: a pooled instance is not guaranteed to be at
            // its new spot at the moment it is switched on.
            _elapsed = 0f;
            _falling = false;
            _struck = false;
            _sounded = false;
            if (body != null)
                body.gameObject.SetActive(false);
            if (telegraph != null)
                telegraph.enabled = true;
            Wake();
        }

        public override void ManagedLateUpdate()
        {
            if (body == null)
            {
                Sleep();
                return;
            }
            if (!_falling)
                Begin();

            _elapsed += Time.deltaTime;
            if (!_sounded && _elapsed >= fallSeconds - soundImpactAt)
                PlaySound();
            if (_struck)
            {
                if (_sounded)
                    Sleep();
                return;
            }

            float k = Mathf.Clamp01(_elapsed / Mathf.Max(0.01f, fallSeconds));
            body.position = Vector3.LerpUnclamped(_from, _to, Travel(k));
            if (glow != null)
                glow.intensity = glowIntensity * Mathf.Lerp(0.33f, 1f, k * k);
            if (k >= 1f)
                Strike();
        }

        private void Begin()
        {
            _falling = true;
            _to = transform.position;
            // The area is spawned facing the way its caster faces (the kit's summon rotation),
            // so back along its forward is back toward the caster - and toward the camera,
            // which sits behind the caster. Straight down out of the sky behind the camera, the
            // first try, the rock was above the top of the frame for all but the last third of a
            // second, and seen end-on its tail was a vertical beam. Coming in low from beyond the
            // target and to one side, it crosses the frame on a diagonal and is in view for more
            // of the fall. Only with the camera tilted up, though: at the demo's default pitch
            // (30 degrees, 60 of view) the top of the frame is the horizon, and nothing more than
            // a few metres up is on screen at all - which is what the glow light is for.
            Vector3 forward = transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f)
                forward = Vector3.forward;
            float side = Random.value < 0.5f ? -1f : 1f;
            Vector3 back = Quaternion.AngleAxis(approachYaw * side, Vector3.up) * -forward.normalized;
            _from = _to + back * lead + Vector3.up * height;

            body.gameObject.SetActive(true);
            body.position = _from;
            body.rotation = Quaternion.LookRotation(_to - _from);
            if (glow != null)
                glow.enabled = true;
            if (_trails != null)
            {
                foreach (TrailRenderer trail in _trails)
                {
                    trail.Clear();
                    trail.emitting = true;
                }
            }
            if (_particles != null)
            {
                foreach (ParticleSystem particles in _particles)
                {
                    particles.Clear(false);
                    particles.Play(false);
                }
            }
        }

        /// <summary>
        /// Share of the way down after share <paramref name="k"/> of the time: already moving when
        /// it comes into view, and still gathering speed when it hits - a third of the average
        /// speed at the top and five thirds of it at the bottom.
        /// </summary>
        private static float Travel(float k)
        {
            return 0.35f * k + 0.65f * k * k;
        }

        private void Strike()
        {
            _struck = true;
            body.position = _to;
            if (_trails != null)
            {
                // Left to fade over their own time rather than cut, so the streak finishes
                // coming down behind the flash.
                foreach (TrailRenderer trail in _trails)
                    trail.emitting = false;
            }
            if (_particles != null)
            {
                foreach (ParticleSystem particles in _particles)
                {
                    // The fireball's own glow rides with it and goes out at once; what it shed on
                    // the way down (flames, smoke, embers) is in the world and lives out its life.
                    bool rides = particles.main.simulationSpace == ParticleSystemSimulationSpace.Local;
                    particles.Stop(false, rides
                        ? ParticleSystemStopBehavior.StopEmittingAndClear
                        : ParticleSystemStopBehavior.StopEmitting);
                }
            }
            // The explosion's flash carries its own light from here.
            if (glow != null)
                glow.enabled = false;
            if (telegraph != null)
                telegraph.enabled = false;
            if (impactEffect != null)
                PoolSystem.GetInstance(impactEffect, _to, Quaternion.identity);
            Jolt();
        }

        /// <summary>
        /// From where the rock will land, not from the caster, and on its own object, so the
        /// boom plays out after the area has been put away. Heard from further than the kit's
        /// one-shots, which fall to a whisper ten metres off: this is the loudest thing on the
        /// island.
        /// </summary>
        private void PlaySound()
        {
            _sounded = true;
            if (sounds == null || sounds.Length == 0 || AudioListener.pause)
                return;
            AudioClip clip = sounds[Random.Range(0, sounds.Length)];
            if (clip == null)
                return;
            var go = new GameObject("MeteorSound");
            go.transform.position = _to;
            var source = go.AddComponent<AudioSource>();
            source.clip = clip;
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Logarithmic;
            source.minDistance = 8f;
            source.maxDistance = 80f;
            source.volume = AudioManager.Singleton != null ? AudioManager.Singleton.GetSfxVolume() : 1f;
            source.Play();
            Destroy(go, clip.length + 0.1f);
        }

        /// <summary>
        /// Knocks the local player's camera with the kit's own recoil, harder the nearer the
        /// strike is to the character it follows. Nothing on anyone else's screen: each client
        /// runs its own copy of this.
        /// </summary>
        private void Jolt()
        {
            var controller = BasePlayerCharacterController.Singleton as PlayerCharacterController;
            var cameraController = controller != null ? controller.CacheGameplayCameraController as DefaultGameplayCameraController : null;
            if (cameraController == null || cameraController.CameraControls == null || controller.PlayingCharacterEntity == null)
                return;
            float distance = Vector3.Distance(controller.PlayingCharacterEntity.EntityTransform.position, _to);
            float strength = 1f - Mathf.Clamp01(distance / Mathf.Max(0.01f, joltRange));
            if (strength <= 0f)
                return;
            strength *= strength;
            cameraController.CameraControls.Recoil(jolt * strength, Random.Range(-0.5f, 0.5f) * jolt * strength, 0f);
        }
    }
}
