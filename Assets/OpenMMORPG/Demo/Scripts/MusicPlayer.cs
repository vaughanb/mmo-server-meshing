using Insthync.AudioManager;
using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// The demo's music: one track set, played either continuously (the menu) or now and
    /// then with long silences between (the island), always at the player's **BGM** level.
    ///
    /// The kit has no music system at all - <see cref="AudioManager"/> keeps a BGM setting
    /// and the settings dialog has always had a slider for it, but nothing in the demo ever
    /// read it. This is what reads it: the level is applied **every frame**, like
    /// <see cref="AmbientSoundLoop"/> does for ambience, so moving the slider is heard at
    /// once rather than at the next track. Muting BGM silences the music and leaves the
    /// schedule running, so unmuting does not start a track from the top.
    ///
    /// <see cref="PlayMode.Occasional"/> is the point of the component. A three-minute piece
    /// on a loop over a small island becomes wallpaper within the hour, so on the map the
    /// music comes and goes: a gap of <see cref="gapMin"/> to <see cref="gapMax"/> seconds,
    /// then a track, then silence again. Fades at both ends keep it from snapping in.
    ///
    /// The **first** gap is separate, because arriving somewhere is not the same as being
    /// there. The island waits (<see cref="firstGapMin"/>) so its music steals up on a place
    /// you are already living in; the crypt sets it to zero so the stair down is scored. Zero
    /// is measured from the listener appearing, not from the scene loading, which in a map
    /// scene is the moment the player walks in.
    ///
    /// Two things it will not do: play while there is no <see cref="AudioListener"/> (on the
    /// island the listener rides the owning character and does not exist until the player is
    /// in the world, so without this the first track would play to an empty room and the
    /// player would arrive to silence), and play on a headless server, where the kit's
    /// AudioManager sets <see cref="AudioListener.pause"/> and every other audio component
    /// checks it. Built into the scenes by DemoMenuStageBuilder and DemoSceneBuilder.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public class MusicPlayer : MonoBehaviour
    {
        public enum PlayMode
        {
            /// <summary>Always playing. One track loops; several play one after another.</summary>
            Continuous,
            /// <summary>A track, then a long silence, then another.</summary>
            Occasional,
        }

        [Tooltip("The tracks to play, in the order they are picked when there is more than one.")]
        public AudioClip[] tracks = new AudioClip[0];
        public PlayMode mode = PlayMode.Continuous;

        [Range(0f, 1f)]
        [Tooltip("How loud the music plays before the player's BGM setting scales it.")]
        public float volume = 0.5f;

        [Tooltip("Seconds to fade a track in and out. The fade out starts this long before the end.")]
        public float fadeSeconds = 2.5f;

        [Header("Occasional")]
        [Tooltip("Shortest silence between tracks, in seconds.")]
        public float gapMin = 180f;
        [Tooltip("Longest silence between tracks, in seconds.")]
        public float gapMax = 420f;
        [Tooltip("Shortest wait before the first track, in seconds. Timed from when there is a " +
                 "listener to hear it, so zero means \"as the player arrives\", not \"as the scene loads\".")]
        public float firstGapMin = 30f;
        [Tooltip("Longest wait before the first track, in seconds. Set both to zero to play on arrival.")]
        public float firstGapMax = 75f;

        private AudioSource _source;
        private AudioListener _listener;
        private float _nextListenerSearch;
        private float _nextStart;
        private bool _scheduled;
        /// <summary>The track played last, or -1 before any. <see cref="PickTrack"/> avoids repeating it.</summary>
        private int _lastPlayed = -1;

        private void Awake()
        {
            _source = GetComponent<AudioSource>();
            // The source is driven from here alone: anything that plays itself would play at
            // the volume the prefab was saved with, which is not the player's setting.
            _source.playOnAwake = false;
            _source.spatialBlend = 0f;
            _source.volume = 0f;
            _source.Stop();
        }

        private void Update()
        {
            if (Application.isBatchMode || AudioListener.pause || tracks.Length == 0)
                return;

            if (!HasListener())
            {
                // Nobody to hear it. Hold the schedule where it is rather than letting the
                // first gap run out during the loading screen.
                if (_source.isPlaying)
                    _source.Stop();
                _scheduled = false;
                return;
            }

            if (_source.isPlaying)
            {
                _source.volume = volume * Fade() * BgmLevel();
                return;
            }

            if (!_scheduled)
            {
                _scheduled = true;
                _nextStart = Time.time + (mode == PlayMode.Continuous ? 0f : Random.Range(firstGapMin, firstGapMax));
                return;
            }

            if (Time.time < _nextStart)
                return;

            PlayNext();
        }

        private void PlayNext()
        {
            AudioClip clip = tracks[PickTrack()];
            // A lone track in Continuous mode is the menu theme: Unity's own loop is seamless,
            // and re-starting it from here would leave a frame of silence every pass.
            _source.loop = mode == PlayMode.Continuous && tracks.Length == 1;
            _source.clip = clip;
            _source.volume = 0f;
            _source.Play();
            // Where the *next* one goes. A looping source never comes back here.
            _nextStart = Time.time + clip.length +
                (mode == PlayMode.Continuous ? 0f : Random.Range(gapMin, gapMax));
        }

        /// <summary>
        /// Which track to play next: **at random, but never the one just played**.
        ///
        /// The MMO convention for a zone with several pieces, and it is not the same as
        /// cycling them in order. A fixed order is audibly an order - the same piece always
        /// follows the same piece - and over a session on one island that is as noticeable
        /// as a single track on a loop, which is the thing the silences exist to avoid.
        /// Excluding the last one played is what keeps random from stuttering the same piece
        /// twice across a silence, which reads as a bug rather than as chance.
        ///
        /// With one track there is nothing to choose and it simply plays again.
        /// </summary>
        private int PickTrack()
        {
            if (tracks.Length <= 1)
                return 0;
            int pick;
            if (_lastPlayed < 0)
            {
                // Nothing to avoid yet, so the whole set is in play. Folding an index out
                // here would make track 0 unreachable as the first piece after a login.
                pick = Random.Range(0, tracks.Length);
            }
            else
            {
                // Fold the excluded index out of the range rather than re-rolling, so this
                // cannot spin and the distribution over the rest stays even.
                pick = Random.Range(0, tracks.Length - 1);
                if (pick >= _lastPlayed)
                    pick++;
            }
            _lastPlayed = pick;
            return pick;
        }

        /// <summary>
        /// 0 to 1 across the fade in, and back down across the fade out. A looping source
        /// never fades out - it has no end to fade towards - and a clip shorter than two
        /// fades gets half of one at each end rather than a gap in the middle.
        /// </summary>
        private float Fade()
        {
            if (fadeSeconds <= 0f)
                return 1f;
            float length = _source.clip == null ? 0f : _source.clip.length;
            float fade = Mathf.Min(fadeSeconds, length * 0.5f);
            if (fade <= 0f)
                return 1f;
            float time = _source.time;
            float factor = Mathf.Clamp01(time / fade);
            if (!_source.loop)
                factor = Mathf.Min(factor, Mathf.Clamp01((length - time) / fade));
            return factor;
        }

        private static float BgmLevel()
        {
            AudioManager manager = AudioManager.Singleton;
            return manager == null ? 1f : manager.GetVolumeLevel(manager.bgmVolumeSetting.id);
        }

        /// <summary>
        /// Whether anything can hear this. On the island the listener is the owning
        /// character's, so it appears only once the player has spawned; it is looked for
        /// again once a second until it does.
        /// </summary>
        private bool HasListener()
        {
            if (_listener != null && _listener.isActiveAndEnabled)
                return true;
            if (Time.time < _nextListenerSearch)
                return false;
            // Only ever scanned while there is nothing to hear the music - once a listener is
            // found it is cached and this is not reached at all - so the interval is short
            // enough that a player arriving is picked up promptly. It has to be: the crypt
            // asks for its track on arrival (a zero first gap), and a one-second throttle
            // would put up to a second of silence between walking in and the music starting.
            _nextListenerSearch = Time.time + 0.25f;
            foreach (AudioListener listener in FindObjectsByType<AudioListener>(FindObjectsSortMode.None))
            {
                if (listener.isActiveAndEnabled)
                {
                    _listener = listener;
                    return true;
                }
            }
            return false;
        }
    }
}
