using MultiplayerARPG.GameData.Model.Playables;
using System.Collections;
using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// The sound of a weapon being drawn or put away, on a player's entity - for every client that
    /// can see them, not only the one who pressed the key.
    ///
    /// The kit's holster clips are animation states with no sound slot (`HolsterAnimation` has a
    /// clip and a trigger rate and nothing else), so this watches what every client does see:
    /// the synced `IsWeaponsSheathed` flag, through the entity's own `onIsWeaponsSheathedChange`.
    /// That is raised on the owner the instant the key sets it and on everyone else when the sync
    /// arrives, which is also how <see cref="DemoWeaponSheathing"/> hears it. Clips are assigned by
    /// the demo's audio wiring (`WeaponSheath*`, `WeaponUnsheath*`).
    ///
    /// **When it plays is read off the draw clip, not guessed.** The model swaps the weapon models
    /// at `unsheathedDurationRate` of the clip - the hand closes on the hilt - and the blade comes
    /// out in the pull that follows, so a draw sounds at that instant
    /// (<see cref="DemoWeaponSheathing.HolsterSeconds"/>, the same figure the swing is held by). A
    /// sheathe is the other way round: the blade goes down the scabbard *before* the swap, which is
    /// the moment it is seated, so it starts <see cref="sheatheLead"/> earlier and ends with it.
    ///
    /// **It stays quiet wherever the kit plays no motion**, because a sound with nothing moving is
    /// the thing that reads as a bug: a model that has only just been switched to - a character
    /// spawning, or climbing on or off the horse, where the weapons change hands inside one frame -
    /// is skipped by the kit's own one-second rule (`SwitchedTime`), and so is it here; a character
    /// with nothing in its hands has nothing to draw; and an initial sync, which arrives as a
    /// "change" from a value to itself, is not one. The model judged is the one on show
    /// (`CharacterModel`), so a mounted rider is held to the rider's body and its own switch time,
    /// not to the hidden standing body's.
    ///
    /// Played through <see cref="OneShotSound"/> - 3D, the player's SFX volume, a little pitch spread
    /// so two players drawing together are not one clip in unison - from just behind the shoulder,
    /// where the weapon is. Not on a headless server.
    /// </summary>
    [DisallowMultipleComponent]
    public class WeaponSheathSounds : MonoBehaviour
    {
        public AudioClip[] sheathClips = new AudioClip[0];
        public AudioClip[] unsheathClips = new AudioClip[0];

        [Range(0f, 1f)]
        public float volume = 1f;

        [Tooltip("How long before the weapon is on the back the sheathe sound starts, in seconds. The " +
                 "clip's loud part is its first quarter second, and it is the blade sliding home.")]
        public float sheatheLead = 0.22f;

        [Tooltip("Full volume out to this many metres, then the logarithmic fall-off to Far.")]
        public float near = 5f;
        public float far = 40f;

        [Tooltip("Where the sound comes from, above the character's feet: the shoulder, behind which the weapon rides.")]
        public float height = 1.3f;

        private BaseCharacterEntity _entity;
        // The sound waiting for its moment, if any.
        private Coroutine _pending;

        private void Awake()
        {
            _entity = GetComponent<BaseCharacterEntity>();
            // Nothing to hear on a server, and an NPC or a monster never puts a weapon away.
            if (_entity == null || Application.isBatchMode)
                enabled = false;
        }

        private void OnEnable()
        {
            if (_entity != null)
                _entity.onIsWeaponsSheathedChange += OnSheathedChanged;
        }

        private void OnDisable()
        {
            // The kit nulls its events as an entity is destroyed; `-=` on a null event is safe.
            if (_entity != null)
                _entity.onIsWeaponsSheathedChange -= OnSheathedChanged;
        }

        private void OnSheathedChanged(BaseCharacterEntity entity, bool wasSheathed, bool isSheathed)
        {
            if (wasSheathed == isSheathed || entity.IsDead() || !DemoWeaponSheathing.HasWeapon(entity))
                return;
            // Whatever was waiting to play belongs to the change this one replaces.
            if (_pending != null)
                StopCoroutine(_pending);
            _pending = null;
            var model = entity.CharacterModel as PlayableCharacterModel;
            if (model == null || Time.unscaledTime - model.SwitchedTime < 1f)
                return;
            // Taken back before the weapon moved (a draw while the sheathe was still on its way):
            // the model stops that gesture and nothing is drawn or put away, so nothing is heard.
            if (model.IsWeaponsSheathed == isSheathed)
                return;
            float trigger, length;
            DemoWeaponSheathing.HolsterSeconds(entity, isSheathed, out trigger, out length);
            float delay = isSheathed ? Mathf.Max(0f, trigger - sheatheLead) : trigger;
            _pending = StartCoroutine(PlayAfter(isSheathed ? sheathClips : unsheathClips, delay, isSheathed));
        }

        private IEnumerator PlayAfter(AudioClip[] clips, float delay, bool sheathed)
        {
            if (delay > 0f)
                yield return new WaitForSecondsRealtime(delay);
            _pending = null;
            if (_entity == null || _entity.IsDead() || _entity.IsWeaponsSheathed != sheathed)
                yield break;
            OneShotSound.PlayAt(clips, _entity.EntityTransform.position + Vector3.up * height, volume, near, far);
        }
    }
}
