using Insthync.AudioManager;
using Insthync.ManagedUpdating;
using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// A character's voice: a grunt when it takes damage, and the kit's death cry held back
    /// until it has actually been alive.
    ///
    /// **The grunt.** The kit has a footstep component and a death component but nothing for
    /// being hit: its damage event fires on the server, where there is no audio, and the hurt
    /// animation is a plain state with no clip slot. What every client does see is the synced
    /// HP, so this plays a clip whenever it drops, throttled so a burst of hits is one grunt.
    /// Clients only. Clips are assigned by the demo's audio wiring (ManHit* for the men,
    /// WomanHit* for the women).
    ///
    /// **The death cry's gate - works around an upstream bug.** `CharacterDeathSoundComponent`
    /// watches for a change:
    ///
    /// <code>
    /// if (_dirtyIsDead != Entity.IsDead()) { _dirtyIsDead = Entity.IsDead(); if (_dirtyIsDead) PlaySound(); }
    /// </code>
    ///
    /// `_dirtyIsDead` is a plain `private bool`, so it starts **false** - the component begins life
    /// certain the character is alive. But `IsDead()` is just `CurrentHp &lt;= 0`, and a character
    /// that has only just spawned has **0 health until the server's first sync lands**. So the very
    /// first update sees false-to-true, calls that a death, and plays the cry over a character who
    /// has never been hurt. It is loudest on a brand new character, which is where it was noticed.
    ///
    /// The fix upstream is one line - seed `_dirtyIsDead` from the entity in `Start` instead of
    /// assuming. It is private, and its `ManagedUpdate` is neither virtual nor overridable, so from
    /// outside the only lever is **when the component is allowed to run at all**. This disables it
    /// before it can register, and switches it on the moment the character reads as alive; at that
    /// point `_dirtyIsDead` being false is simply *true*, and the next transition is a real death.
    /// A character that spawns genuinely dead keeps the component off until it revives, which is
    /// also right: the cry belongs to the moment of dying, not to arriving as a corpse.
    ///
    /// Disabling in `Awake` is safe for the death component's audio source: Unity defers its
    /// `Start` until it is first enabled, so the source is still created, just later. Its `Start`
    /// also turns itself off again on a server, so nothing here needs to know the difference.
    /// The gate watches only until it opens - a few frames after a spawn - on the managed update,
    /// and then lets go; the grunt is driven by the HP event alone.
    ///
    /// The two used to be separate components on the same entities (the gate was
    /// `DemoDeathSoundGate`); every character that had the gate had this too.
    /// </summary>
    // Deliberately no [RequireComponent] for the death component: that would stop Unity removing
    // it while this is attached, and the audio builder removes it from anything with no death
    // clips. The null checks below cover the same ground without the hazard.
    public class CharacterHurtSoundComponent : MonoBehaviour
    {
        public AudioClip[] clips = new AudioClip[0];
        [Tooltip("Shortest gap between two grunts, in seconds.")]
        public float cooldown = 0.35f;
        public float minPitch = 0.95f;
        public float maxPitch = 1.05f;
        [Range(0f, 1f)]
        public float volume = 1f;

        private DamageableEntity _entity;
        private AudioSource _source;
        private float _readyAt;
        private DeathSoundGate _gate;

        private void Awake()
        {
            var death = GetComponent<CharacterDeathSoundComponent>();
            var character = GetComponent<BaseCharacterEntity>();
            if (death == null || character == null)
                return;
            death.enabled = false;
            _gate = new DeathSoundGate(death, character);
            UpdateManager.Register(_gate);
        }

        private void Start()
        {
            _entity = GetComponent<DamageableEntity>();
            if (_entity == null || !_entity.IsClient)
            {
                // Off for the grunt only: the gate is not tied to this component's switch.
                enabled = false;
                return;
            }
            _entity.onCurrentHpChange += OnHpChange;
        }

        private void OnDestroy()
        {
            if (_entity != null)
                _entity.onCurrentHpChange -= OnHpChange;
            if (_gate != null)
                _gate.Close();
        }

        private void OnHpChange(DamageableEntity entity, int oldHp, int newHp)
        {
            if (newHp >= oldHp || clips.Length == 0 || Time.time < _readyAt)
                return;
            if (Application.isBatchMode || AudioListener.pause)
                return;
            if (_source == null)
            {
                var go = new GameObject("_HurtAudioSource");
                go.transform.SetParent(transform, false);
                go.transform.localPosition = new Vector3(0f, 1.5f, 0f);
                _source = go.AddComponent<AudioSource>();
                _source.spatialBlend = 1f;
            }
            _readyAt = Time.time + cooldown;
            _source.pitch = Random.Range(minPitch, maxPitch);
            float level = AudioManager.Singleton.GetVolumeLevel(AudioManager.Singleton.sfxVolumeSetting.id);
            _source.PlayOneShot(clips[Random.Range(0, clips.Length)], volume * level);
        }

        /// <summary>
        /// Watches for the first frame the character reads as alive, switches the death cry on,
        /// and stops watching. See the class summary.
        /// </summary>
        private sealed class DeathSoundGate : IManagedUpdate
        {
            private readonly CharacterDeathSoundComponent _death;
            private readonly BaseCharacterEntity _character;
            private bool _open;

            public DeathSoundGate(CharacterDeathSoundComponent death, BaseCharacterEntity character)
            {
                _death = death;
                _character = character;
            }

            public void ManagedUpdate()
            {
                if (_death == null || _character == null)
                {
                    Close();
                    return;
                }
                if (_character.IsDead())
                    return;
                _death.enabled = true;
                Close();
            }

            public void Close()
            {
                if (_open)
                    return;
                _open = true;
                UpdateManager.Unregister(this);
            }
        }
    }
}
