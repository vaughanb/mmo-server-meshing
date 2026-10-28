using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// A sound that goes with an effect prefab, played through <see cref="OneShotSound.PlayAt"/>
    /// each time the effect is fetched from the pool.
    ///
    /// Not the kit's `GameEffect.randomSoundEffects`, which is `PlayClipAtPoint` at Unity's default
    /// rolloff - full volume within a metre, a quarter at four. That is right for a sword on the
    /// body in front of you and wrong for everything the ranger does: an arrow lands on a bandit
    /// twenty metres off, and at that rolloff it lands in silence.
    ///
    /// Played on the first frame after the effect is enabled rather than in OnEnable, because a
    /// pooled object is not guaranteed to be at its new place yet when it is switched on - the same
    /// reason as AreaLandEffect. Clips are written by DemoAudioWiring (Wire Audio, and Build Skill
    /// Effects), so an empty array is a family nobody has recorded yet, not a fault.
    ///
    /// Its late update runs only from the fetch until the sound has played
    /// (<see cref="WakeableLateUpdateBehaviour"/>).
    /// </summary>
    public class GameEffectSounds : WakeableLateUpdateBehaviour
    {
        public AudioClip[] clips = new AudioClip[0];

        [Range(0f, 1f)]
        public float volume = 0.8f;

        [Tooltip("Metres out to which it plays at full volume.")]
        public float near = 6f;

        [Tooltip("Metres beyond which it is inaudible.")]
        public float far = 60f;

        [Tooltip("Seconds after the effect appears before the sound plays.")]
        public float delay;

        private float _playAt = -1f;

        private void Awake()
        {
            if (Application.isBatchMode)
                enabled = false;
        }

        private void OnEnable()
        {
            _playAt = Time.time + Mathf.Max(0f, delay);
            Wake();
        }

        public override void ManagedLateUpdate()
        {
            if (_playAt >= 0f && Time.time < _playAt)
                return;
            Sleep();
            if (_playAt < 0f)
                return;
            _playAt = -1f;
            OneShotSound.PlayAt(clips, transform.position, volume, near, far);
        }
    }
}
