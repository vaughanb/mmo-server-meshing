// Server Meshing add-on — keeps a handoff stand-in's footsteps going.
//
// The kit's CharacterFootstepSoundComponent sits on the entity and plays a random clip of its current footstep settings
// every stepDelay (over the move animation speed) while the entity moves. During ClientMigrationController's seamless
// handoff the entity is gone and its stand-in has no scripts, so the steps stop for the swap. This component, added to
// the stand-in, carries on with the same footstep settings and volume, from an AudioSource set up like the entity's,
// stepping while the stand-in moves: in time with the stand-in's locomotion clip when MeshStandInAnimation plays one,
// else on the kit's timer and its phase. Vehicles and riders are left silent, as the
// kit leaves a passenger.
using System.Reflection;
using Insthync.AudioManager;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public sealed class MeshStandInFootsteps : MonoBehaviour
    {
        private static readonly FieldInfo s_currentSettings = typeof(CharacterFootstepSoundComponent).GetField("currentFootstepSettings", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo s_delayCounter = typeof(CharacterFootstepSoundComponent).GetField("delayCounter", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>A kit timer slower than this is one switched off, not a slow walk.</summary>
        private const float MaxTimerInterval = 5f;
        /// <summary>How long the stand-in must stand still before the rhythm restarts: the swap has the odd frame it does not move.</summary>
        private const float StillSeconds = 0.2f;

        private FootstepSettings _settings;
        private AudioSource _audio;
        private float _interval;
        private float _threshold;
        private float _counter;
        private float _volumeLevel;
        private Vector3 _lastPosition;
        private bool _hasLastPosition;
        private int _played;
        private float _stillFor;

        /// <summary>Attach adds the component to <paramref name="clone"/> when <paramref name="entity"/> plays footsteps.</summary>
        public static void Attach(BaseGameEntity entity, GameObject clone)
        {
            if (Application.isBatchMode || entity is IVehicleEntity || entity.PassengingVehicleEntity != null || entity.MuteFootstepSound)
                return;
            CharacterFootstepSoundComponent footsteps = entity.GetComponentInChildren<CharacterFootstepSoundComponent>();
            if (footsteps == null || !footsteps.enabled || footsteps.audioSource == null)
                return;
            // What the entity was stepping with: its current settings (walk, jog, sprint...), else its move settings.
            FootstepSettings settings = s_currentSettings?.GetValue(footsteps) as FootstepSettings ?? footsteps.moveFootstepSettings;
            if (settings == null || settings.soundData.randomAudioClips == null || settings.soundData.randomAudioClips.Length == 0)
                return;
            // The rhythm: the stand-in's own locomotion clip when it plays one (a step every half cycle, in time with the
            // feet), else the kit's timer. A game may turn the timer off (the kit demo sets stepDelay to "never" and plays
            // each step from a foot plant instead); then without a clip there is nothing to keep time by.
            float interval, counter;
            MeshStandInAnimation animation = clone.GetComponent<MeshStandInAnimation>();
            if (animation == null || !animation.TryGetStepCadence(out interval, out counter))
            {
                interval = (settings.stepDelay > 0f ? settings.stepDelay : 0.35f) / Mathf.Max(0.1f, entity.MoveAnimationSpeedMultiplier);
                counter = s_delayCounter != null ? (float)s_delayCounter.GetValue(footsteps) : 0f;
                if (interval > MaxTimerInterval)
                    return;
            }
            MeshStandInFootsteps steps = clone.AddComponent<MeshStandInFootsteps>();
            steps._settings = settings;
            steps._interval = interval;
            steps._threshold = settings.stepThreshold > 0f ? settings.stepThreshold : 0.1f;
            steps._counter = counter;
            steps._volumeLevel = AudioManager.Singleton != null ? AudioManager.Singleton.GetVolumeLevel(footsteps.SettingId) : 1f;
            steps._audio = CopyAudioSource(footsteps.audioSource, clone);
            Logging.Log("Spike2", $"Stand-in {clone.name}: footsteps every {steps._interval:0.00} s carried on");
        }

        private static AudioSource CopyAudioSource(AudioSource from, GameObject to)
        {
            AudioSource audio = to.AddComponent<AudioSource>();
            audio.playOnAwake = false;
            audio.loop = false;
            audio.outputAudioMixerGroup = from.outputAudioMixerGroup;
            audio.volume = from.volume;
            audio.priority = from.priority;
            audio.spatialBlend = from.spatialBlend;
            audio.spread = from.spread;
            audio.dopplerLevel = from.dopplerLevel;
            audio.rolloffMode = from.rolloffMode;
            audio.minDistance = from.minDistance;
            audio.maxDistance = from.maxDistance;
            if (from.rolloffMode == AudioRolloffMode.Custom)
                audio.SetCustomCurve(AudioSourceCurveType.CustomRolloff, from.GetCustomCurve(AudioSourceCurveType.CustomRolloff));
            return audio;
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            Vector3 position = transform.position;
            if (!_hasLastPosition || dt <= 0f)
            {
                _lastPosition = position;
                _hasLastPosition = true;
                return;
            }
            Vector3 delta = position - _lastPosition;
            delta.y = 0f;
            _lastPosition = position;
            if (delta.magnitude / dt < _threshold)
            {
                _stillFor += dt;
                if (_stillFor >= StillSeconds)
                    _counter = 0f;
                return;
            }
            _stillFor = 0f;
            _counter += dt;
            if (_counter < _interval)
                return;
            _counter = 0f;
            if (AudioListener.pause)
                return;
            AudioClip clip = _settings.soundData.GetRandomedAudioClip();
            if (clip == null)
                return;
            _audio.pitch = Random.Range(_settings.randomPitchMin, _settings.randomPitchMax);
            _audio.PlayOneShot(clip, Random.Range(_settings.randomVolumeMin, _settings.randomVolumeMax) * _volumeLevel);
            _played++;
        }

        private void OnDestroy()
        {
            Logging.Log("Spike2", $"Stand-in {name}: {_played} footstep(s) played during the swap (one every {_interval:0.00} s)");
        }
    }
}
