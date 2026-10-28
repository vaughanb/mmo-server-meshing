using Insthync.ManagedUpdating;
using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// A door leaf swinging on its hinge: the swing, its sounds, and the leaf's collision. The
    /// part a scenery door (<see cref="SceneryDoor"/>) and a player-built one
    /// (<see cref="BuildingDoorLeaf"/>) have in common; what opens and shuts them is theirs.
    ///
    /// **The close sound is timed to the slam.** The slam is the sound of a door, and it is some
    /// way into the clip, so the clip starts when the leaf is that far from the frame and the
    /// two arrive together (<see cref="closeSoundLead"/>). Played at rest instead, the creak came
    /// after the door had stopped and the slam a fifth of a second after that.
    ///
    /// **The leaf is intangible while it travels.** A moving solid would shove whoever it caught
    /// - and a non-convex mesh collider being dragged through the world every frame is the one
    /// thing Unity asks you not to do with one. Nobody can cross in the half second it takes to
    /// swing anyway. It is solid again the moment it stops.
    ///
    /// **It ticks only while there is something to do.** A shut door at rest is not registered
    /// with the kit's update manager at all; opening or closing it registers it, and it lets go
    /// again once the leaf has stopped and <see cref="StayAwake"/> has nothing to wait for.
    /// </summary>
    public abstract class DoorSwing : MonoBehaviour, IManagedUpdate
    {
        [Tooltip("The transform that swings. Its pivot sits on the hinge.")]
        public Transform pivot;

        [Tooltip("How far the door swings, in degrees. Negative swings it the other way.")]
        public float openAngle = 100f;

        [Tooltip("Degrees per second.")]
        public float swingSpeed = 260f;

        [Tooltip("Played as the leaf starts to swing open. One picked at random; none is silent.")]
        public AudioClip[] openSounds;

        [Tooltip("Played so that its slam lands as the leaf meets the frame; see closeSoundLead.")]
        public AudioClip[] closeSounds;

        /// <summary>
        /// How far into the close clip its slam is, in seconds - the clip starts this long
        /// before the leaf arrives, so the two coincide. Measured off DoorClose1.wav: a short
        /// creak, then the slam peaking 0.20 s in.
        /// </summary>
        [Tooltip("Seconds from the start of the close clip to its slam. The clip starts this long before the leaf meets the frame.")]
        [Min(0f)]
        public float closeSoundLead = 0.2f;

        [Tooltip("Loudness of both, before the player's SFX setting.")]
        [Range(0f, 1f)]
        public float soundVolume = 0.8f;

        /// <summary>Full volume this close to the hinge, in metres; the room, not the whole green.</summary>
        private const float SoundNear = 2f;
        private const float SoundFar = 25f;

        /// <summary>Whether the leaf is moving right now.</summary>
        public bool IsSwinging { get; private set; }

        private Collider[] _leaf;
        private float _angle;
        private float _target;
        private bool _closeSoundPlayed;
        private bool _awake;

        protected virtual void Reset()
        {
            pivot = transform;
        }

        protected virtual void Awake()
        {
            _leaf = pivot != null ? pivot.GetComponentsInChildren<Collider>(true) : new Collider[0];
        }

        protected virtual void OnEnable()
        {
            if (!Mathf.Approximately(_angle, _target) || StayAwake)
                Wake();
        }

        protected virtual void OnDisable()
        {
            Sleep();
        }

        /// <summary>
        /// Whether to keep ticking with the leaf at rest - a door that shuts itself has a clock
        /// to watch while it stands open. <see cref="Tick"/> runs every frame while this holds.
        /// </summary>
        protected virtual bool StayAwake => false;

        /// <summary>The subclass's own per-frame work, run before the leaf moves; only while awake.</summary>
        protected virtual void Tick()
        {
        }

        /// <summary>Sets the leaf swinging toward an angle, with the open sound if it is now opening.</summary>
        protected void SwingTo(float angle)
        {
            bool opening = !Mathf.Approximately(angle, 0f) && !Mathf.Approximately(_target, angle);
            if (opening)
            {
                _closeSoundPlayed = false;
                EffectSoundPlayAt(openSounds);
            }
            _target = angle;
            Wake();
        }

        /// <summary>Puts the leaf at an angle with no swing and no sound, ending any swing under way.</summary>
        protected void SetAtOnce(float angle)
        {
            _target = _angle = angle;
            Apply();
            if (IsSwinging)
            {
                IsSwinging = false;
                SetLeafSolid(true);
            }
        }

        /// <summary>Starts ticking, if this is not already.</summary>
        protected void Wake()
        {
            if (_awake || !isActiveAndEnabled)
                return;
            _awake = true;
            UpdateManager.Register(this);
        }

        private void Sleep()
        {
            if (!_awake)
                return;
            _awake = false;
            UpdateManager.Unregister(this);
        }

        public void ManagedUpdate()
        {
            if (pivot == null)
            {
                Sleep();
                return;
            }
            Tick();

            bool swinging = !Mathf.Approximately(_angle, _target);
            if (swinging)
            {
                if (Mathf.Approximately(_target, 0f) && !_closeSoundPlayed && Mathf.Abs(_angle) / swingSpeed <= closeSoundLead)
                {
                    _closeSoundPlayed = true;
                    EffectSoundPlayAt(closeSounds);
                }
                _angle = Mathf.MoveTowards(_angle, _target, swingSpeed * Time.deltaTime);
                Apply();
            }
            if (swinging != IsSwinging)
            {
                IsSwinging = swinging;
                SetLeafSolid(!swinging);
            }
            if (!swinging && !StayAwake)
                Sleep();
        }

        private void Apply()
        {
            if (pivot != null)
                pivot.localRotation = Quaternion.Euler(0f, _angle, 0f);
        }

        private void SetLeafSolid(bool solid)
        {
            if (_leaf == null)
                return;
            for (int i = 0; i < _leaf.Length; ++i)
            {
                if (_leaf[i] != null)
                    _leaf[i].enabled = solid;
            }
        }

        private void EffectSoundPlayAt(AudioClip[] clips)
        {
            OneShotSound.PlayAt(clips, pivot != null ? pivot.position : transform.position, soundVolume, SoundNear, SoundFar);
        }
    }
}
