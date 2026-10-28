using MultiplayerARPG.GameData.Model.Playables;
using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// Drawing and sheathing weapons for the player who owns this entity: the Z key, and the
    /// draw that any attack, skill or harvest swing makes first.
    ///
    /// **The kit already does the hard half.** `IsWeaponsSheathed` is a synced flag on every
    /// character: while it is up the model puts the equipped weapons away - each item's
    /// `sheathModels` appear on the sheath socket instead of the hand - and plays the
    /// draw/holster clips, `CanAttack` and `CanUseSkill` refuse, and every other client sees
    /// the same thing. The ladder already uses it (<see cref="EasedCharacterLadderComponent"/>).
    /// What was missing was a way for a player to set it, and the two rules that make it
    /// bearable: a sheathed character that is told to fight draws first instead of doing
    /// nothing, and the swing waits until the weapon is in the hand.
    ///
    /// **The weapon is in the hand at the trigger, not at the end.** The model swaps the
    /// weapon models when the draw clip reaches `unsheathedDurationRate` of its length - the
    /// moment the hand closes on the hilt - and the rest of the clip is the pull and the
    /// stance. <see cref="ReadyAt"/> is that moment, read off the model's own clip, so the
    /// first swing starts as the hand finishes drawing and not before the weapon exists.
    /// It is enforced by vetoing the entity's own `onCanAttackValidated` family until then;
    /// a hotbar skill cannot be vetoed (the kit clears its queue whether or not the cast
    /// went off), so the controller holds those back itself - see
    /// <c>DemoPlayerController.UseHotkey</c>.
    ///
    /// **Characters start sheathed, and put the weapons away again when idle.** The flag is not
    /// saved, so every character used to spawn with a sword in its hand. It now spawns with it on
    /// its back - set in `Awake`, which for an entity is *before* it is spawned: a sync field set
    /// before the spawn just takes the value, raises nothing, and the spawn baseline carries it to
    /// the owner and to everyone else, so no client ever sees a weapon in a hand and then watches
    /// it go away. (Set after the spawn it would be a change: an animation, a sound, and a
    /// frame or two of sword on every other screen.) After <see cref="idleSheatheDelay"/> seconds
    /// with no attack, skill, harvest swing or damage taken, and nothing chasing or aiming, the
    /// owner puts them away; <see cref="NoteActivity"/> is what resets the clock, and the
    /// controller calls it for what only it can see (a chase, an aimed skill, a held press).
    ///
    /// Added to every player entity, on every peer, by <c>DemoEntitySetting</c> - the demo's
    /// hook into the kit's `InitialRequiredComponents`, which runs in the entity's `Awake` - so the
    /// default is in place on the server as well as at the keyboard. What other people see is
    /// the synced flag and their own copy of the model.
    /// </summary>
    [DisallowMultipleComponent]
    public class DemoWeaponSheathing : MonoBehaviour
    {
        /// <summary>
        /// What the kit puts in the key of every model it stows: `<socket>_SHEATH_<set>_<hand>`.
        /// Equipment scripts use it to tell a weapon on the back from one in the hand.
        /// </summary>
        public const string StowedMarker = "_SHEATH_";

        /// <summary>
        /// The name of the key setting on `GameInstance`'s input settings - an entry
        /// `DemoControllerBuilder.ConfigureKeys` adds, since the kit has no such action. Z by
        /// default, rebindable like the rest.
        /// </summary>
        public const string KeyName = "SheathWeapons";

        /// <summary>True for the socket key the kit gives a stowed (sheath) model.</summary>
        public static bool IsStowedSocket(string equipSocket)
        {
            return !string.IsNullOrEmpty(equipSocket) && equipSocket.Contains(StowedMarker);
        }

        [Tooltip("Characters spawn with their weapons put away: they appear on the back from the first " +
                 "frame, and the first attack draws them. Off is the kit's own: spawn with them in hand.")]
        public bool startSheathed = true;

        [Tooltip("Put the weapons away after this many seconds with no attack, skill, harvest swing or " +
                 "damage taken, and nothing chasing or aiming. 0 keeps them out until the player puts them away.")]
        public float idleSheatheDelay = 8f;

        [Tooltip("Put the weapons away while riding, and take them out again on dismounting. " +
                 "A rider cannot swing anyway, and a sword in a seated hand looks wrong.")]
        public bool sheatheWhileMounted = true;

        [Tooltip("Seconds added to a draw before the first swing may land, past the moment the weapon " +
                 "reaches the hand. The server checks `CanAttack` against the synced flag, which has to " +
                 "have crossed the network ahead of the swing that depends on it.")]
        public float networkMargin = 0.08f;

        private BasePlayerCharacterEntity _entity;
        private float _readyAt;
        private float _settledAt;
        private float _lastActive;
        private bool _hooked;
        private ICharacterAttackComponent _attack;
        private ICharacterUseSkillComponent _skill;
        private bool _sheathedForMount;
        private bool _drawnBeforeMount;
        // A Z press that came while the hands were busy: the state it asked for, and until when.
        private bool _wantSheathed;
        private float _wantUntil;

        /// <summary>How long a Z press made mid-swing or mid-draw waits for the hands to be free.</summary>
        private const float ToggleWaitSeconds = 1.5f;

        /// <summary>Unscaled time at which a weapon being drawn is in the hand. Zero or past: it is.</summary>
        public float ReadyAt { get { return _readyAt; } }

        /// <summary>True from the instant a draw starts until the weapon is in the hand.</summary>
        public bool IsDrawing { get { return Time.unscaledTime < _readyAt; } }

        public bool IsSheathed { get { return _entity != null && _entity.IsWeaponsSheathed; } }

        private void Awake()
        {
            _entity = GetComponent<BasePlayerCharacterEntity>();
            // The two settings live on the demo's entity-setting asset, where they can be tuned
            // without a prefab to open: this component is added by that asset, at run time.
            var setting = GameInstance.Singleton != null ? GameInstance.Singleton.EntitySetting as Demo.DemoEntitySetting : null;
            if (setting != null)
            {
                startSheathed = setting.startSheathed;
                idleSheatheDelay = setting.idleSheatheDelay;
            }
            // Before the entity is spawned the sync field only takes the value, so the spawn
            // baseline carries it everywhere. A component added to an entity that is already
            // spawned (an old path) must not sheathe a character mid-game, hence the check.
            if (startSheathed && _entity != null && !_entity.IsSpawned)
                _entity.IsWeaponsSheathed = true;
            _lastActive = Time.unscaledTime;
        }

        private void OnEnable()
        {
            if (_entity == null)
                return;
            _entity.onCanAttackValidated += HoldWhileDrawing;
            _entity.onCanUseSkillValidated += HoldWhileDrawing;
            _entity.onCanUseSkillItemValidated += HoldWhileDrawing;
            _entity.onIsWeaponsSheathedChange += OnSheathedChanged;
        }

        private void OnDisable()
        {
            if (_entity == null)
                return;
            _entity.onCanAttackValidated -= HoldWhileDrawing;
            _entity.onCanUseSkillValidated -= HoldWhileDrawing;
            _entity.onCanUseSkillItemValidated -= HoldWhileDrawing;
            _entity.onIsWeaponsSheathedChange -= OnSheathedChanged;
            Unhook();
        }

        /// <summary>
        /// The events that mean "this character is fighting", hooked on the first update rather than
        /// in `Awake`: the entity caches its attack and skill components after the demo's setting has
        /// added this one, so they are not there yet. Only the owner's idle clock matters.
        /// </summary>
        private void Hook()
        {
            _hooked = true;
            _attack = _entity.AttackComponent;
            _skill = _entity.UseSkillComponent;
            if (_attack != null)
                _attack.OnAttackStart += NoteActivity;
            if (_skill != null)
                _skill.OnUseSkillStart += NoteActivity;
            _entity.onCurrentHpChange += OnHpChanged;
        }

        private void Unhook()
        {
            if (!_hooked)
                return;
            _hooked = false;
            if (_attack != null)
                _attack.OnAttackStart -= NoteActivity;
            if (_skill != null)
                _skill.OnUseSkillStart -= NoteActivity;
            // The kit nulls its events as an entity is destroyed; `-=` on a null event is safe.
            if (_entity != null)
                _entity.onCurrentHpChange -= OnHpChanged;
            _attack = null;
            _skill = null;
        }

        private void OnHpChanged(DamageableEntity entity, int oldHp, int newHp)
        {
            // A hit taken is a fight, even if the player has not struck back yet; healing is not.
            if (newHp < oldHp)
                NoteActivity();
        }

        /// <summary>
        /// Starts the idle clock again: something is going on that the weapons are for. Called by
        /// the entity's attack and skill events and by damage taken, and by the controller for a
        /// chase, an aimed skill or a held press, which only it can see.
        /// </summary>
        public void NoteActivity()
        {
            _lastActive = Time.unscaledTime;
        }

        private void HoldWhileDrawing(BaseGameEntity target, ref bool allowed)
        {
            // The owner's own rule, not the server's: the server must accept a swing that was
            // timed on the player's clock, which it cannot be held to on its own.
            if (IsDrawing && _entity.IsOwnerClient)
                allowed = false;
        }

        /// <summary>
        /// Times every change of the flag, whoever made it: the key, an attack's auto-draw, the
        /// ladder handing the weapons back after a climb. The setter raises this at once on the
        /// owner, so the clock starts as the model's does. A draw also starts the idle clock - a
        /// weapon taken out is given its whole delay before it is put away again.
        /// </summary>
        private void OnSheathedChanged(BaseCharacterEntity target, bool wasSheathed, bool isSheathed)
        {
            if (!_entity.IsOwnerClient)
                return;
            if (!isSheathed)
                NoteActivity();
            // Taken back before the last change reached the hands - a draw while the sheathe is still
            // on its way to the back: the model stops that gesture and the weapon never moves (see
            // SeatAwarePlayableCharacterModel.SetEquipItems), so it is ready now. The model's own flag
            // is what it shows; it changes at the swap, not with this one.
            BaseCharacterModel model = _entity.CharacterModel;
            if (model != null && model.IsWeaponsSheathed == isSheathed)
            {
                _readyAt = 0f;
                _settledAt = Time.unscaledTime;
                return;
            }
            float trigger, length;
            Seconds(isSheathed, out trigger, out length);
            _readyAt = isSheathed ? 0f : Time.unscaledTime + trigger + networkMargin;
            _settledAt = Time.unscaledTime + length;
        }

        private void Update()
        {
            if (_entity == null || !_entity.IsOwnerClient)
                return;
            if (!_hooked)
                Hook();
            if (_entity.IsDead())
            {
                _readyAt = 0f;
                return;
            }
            if (sheatheWhileMounted)
                FollowMount();
            ToggleWhenFree();
            SheatheWhenIdle();
        }

        /// <summary>
        /// Plays a Z press that came while the hands were busy, once they are free - unless the
        /// character got there some other way first, or the wait ran out.
        /// </summary>
        private void ToggleWhenFree()
        {
            if (_wantUntil <= 0f)
                return;
            if (Time.unscaledTime > _wantUntil || _entity.IsWeaponsSheathed == _wantSheathed)
            {
                _wantUntil = 0f;
                return;
            }
            if (CanToggle())
            {
                _wantUntil = 0f;
                Toggle();
            }
        }

        /// <summary>
        /// Puts the weapons away once nothing has asked for them for <see cref="idleSheatheDelay"/>
        /// seconds, through the same guards as the key - never mid-swing, mid-draw, mounted or on a
        /// ladder - and never for a character with nothing to put away.
        /// </summary>
        private void SheatheWhenIdle()
        {
            if (idleSheatheDelay <= 0f || _entity.IsWeaponsSheathed)
                return;
            if (Time.unscaledTime - _lastActive < idleSheatheDelay)
                return;
            if (CanToggle())
                Sheathe();
        }

        /// <summary>
        /// Sheathes on climbing into a seat and restores what the player had on getting out of
        /// it. The restore is of the player's choice, not a blanket draw: a character that
        /// rode in sheathed gets off sheathed.
        /// </summary>
        private void FollowMount()
        {
            bool mounted = _entity.PassengingVehicleEntity != null;
            if (mounted && !_sheathedForMount)
            {
                _sheathedForMount = true;
                _drawnBeforeMount = !_entity.IsWeaponsSheathed;
                if (_drawnBeforeMount)
                    _entity.IsWeaponsSheathed = true;
            }
            else if (!mounted && _sheathedForMount)
            {
                _sheathedForMount = false;
                if (_drawnBeforeMount)
                    Draw();
            }
        }

        /// <summary>Whether there is anything in the active weapon set to put away or take out.</summary>
        public bool HasWeapon()
        {
            return HasWeapon(_entity);
        }

        /// <summary>
        /// <see cref="HasWeapon()"/> for any character: static so the draw sounds
        /// (<see cref="WeaponSheathSounds"/>), which run on every player's entity and not only the one
        /// at this keyboard, ask the same question the same way.
        /// </summary>
        public static bool HasWeapon(BaseCharacterEntity entity)
        {
            if (entity == null)
                return false;
            EquipWeapons weapons = entity.EquipWeapons;
            return !weapons.IsEmptyRightHandSlot() || !weapons.IsEmptyLeftHandSlot();
        }

        /// <summary>
        /// Whether the player can toggle right now: alive, in control, not mid-swing and not
        /// halfway through the last draw or sheathe. Mounted, climbing or sitting characters
        /// are left as they are.
        /// </summary>
        public bool CanToggle()
        {
            if (_entity == null || !_entity.IsOwnerClient || _entity.IsDead())
                return false;
            if (Time.unscaledTime < _settledAt)
                return false;
            if (_entity.PassengingVehicleEntity != null)
                return false;
            if (_entity.LadderComponent != null && _entity.LadderComponent.ClimbingLadder != null)
                return false;
            if (_entity.IsAttacking || _entity.IsUsingSkill || _entity.IsPlayingAttackOrUseSkillAnimation())
                return false;
            return HasWeapon();
        }

        /// <summary>Draws or sheathes now, if <see cref="CanToggle"/>. Returns true if it did anything.</summary>
        public bool Toggle()
        {
            if (!CanToggle())
                return false;
            return _entity.IsWeaponsSheathed ? Draw() : Sheathe();
        }

        /// <summary>
        /// The Z key: <see cref="Toggle"/> now, or - when only a swing, a cast or the last draw or
        /// sheathe is in the way - as soon as that ends. A press mid-swing used to be dropped without
        /// a sign, which read as the key not working. Returns false if the press can never be met
        /// (dead, riding, climbing, nothing equipped); true if it was done or will be.
        /// </summary>
        public bool RequestToggle()
        {
            if (Toggle())
                return true;
            if (_entity == null || !_entity.IsOwnerClient || _entity.IsDead() || !HasWeapon())
                return false;
            if (_entity.PassengingVehicleEntity != null)
                return false;
            if (_entity.LadderComponent != null && _entity.LadderComponent.ClimbingLadder != null)
                return false;
            _wantSheathed = !_entity.IsWeaponsSheathed;
            _wantUntil = Time.unscaledTime + ToggleWaitSeconds;
            return true;
        }

        /// <summary>
        /// Drops a waiting Z press. The controller calls it whenever the player asks for a fight, so a
        /// sheathe pressed mid-swing does not go off in the gap before the next swing of a new one.
        /// </summary>
        public void ForgetRequest()
        {
            _wantUntil = 0f;
        }

        /// <summary>
        /// Takes the weapons out, if they are away. True if a draw began. The swing can start
        /// at <see cref="ReadyAt"/>.
        /// </summary>
        public bool Draw()
        {
            if (_entity == null || !_entity.IsOwnerClient || !_entity.IsWeaponsSheathed || _entity.IsDead())
                return false;
            // Nothing equipped: there is nothing to see being drawn, so just clear the flag
            // rather than leave a character that refuses to fight over an empty slot.
            bool anything = HasWeapon();
            _entity.IsWeaponsSheathed = false;
            return anything;
        }

        /// <summary>Puts the weapons away, if they are out. True if it did.</summary>
        public bool Sheathe()
        {
            if (_entity == null || !_entity.IsOwnerClient || _entity.IsWeaponsSheathed || !HasWeapon())
                return false;
            _entity.IsWeaponsSheathed = true;
            return true;
        }

        /// <summary>
        /// How long until the models swap (<paramref name="trigger"/>) and until the clip is
        /// over (<paramref name="length"/>), from the model's own holster clip - the same
        /// figures the model schedules its swap with. Zero for a model with no clip, which
        /// swaps at once.
        /// </summary>
        private void Seconds(bool sheathing, out float trigger, out float length)
        {
            HolsterSeconds(_entity, sheathing, out trigger, out length);
        }

        /// <summary>
        /// <see cref="Seconds"/> for any character, for the draw sounds: they are timed off the same
        /// clip the model swaps the weapon by, so the sound and the weapon cannot drift apart.
        /// </summary>
        public static void HolsterSeconds(BaseCharacterEntity entity, bool sheathing, out float trigger, out float length)
        {
            trigger = 0f;
            length = 0f;
            var model = entity != null ? entity.CharacterModel as PlayableCharacterModel : null;
            if (model == null)
                return;
            EquipWeapons weapons = entity.EquipWeapons;
            ActionState state;
            float rate;
            if (!weapons.IsEmptyRightHandSlot())
            {
                if (sheathing)
                    model.GetRightHandSheathActionState(weapons, out state, out rate);
                else
                    model.GetRightHandUnsheathActionState(weapons, out state, out rate);
            }
            else
            {
                if (sheathing)
                    model.GetLeftHandSheathActionState(weapons, out state, out rate);
                else
                    model.GetLeftHandUnsheathActionState(weapons, out state, out rate);
            }
            if (state == null || state.clip == null)
                return;
            length = state.GetClipLength(1f);
            trigger = length * rate;
        }
    }
}
