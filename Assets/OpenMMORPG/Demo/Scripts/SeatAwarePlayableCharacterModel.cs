using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using MultiplayerARPG.GameData.Model.Playables;
using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// The demo's character model: the kit's playable model, except that it dresses itself
    /// even when it is a vehicle seat model rather than the entity's main model.
    ///
    /// **Why this exists.** The kit routes every equipment lookup on a non-main model
    /// through the main one - <see cref="BaseCharacterModel.CacheEquipmentModelContainers"/>,
    /// <see cref="BaseCharacterModel.EquippedModels"/> and the hand-entity caches all read
    /// <c>MainModel</c>'s when <c>IsMainModel</c> is false, and the inspector hides the
    /// container lists on such a model for the same reason. That assumes a seat model shares
    /// the main model's skeleton. The demo's rider (<c>ModelRiding</c>, see
    /// <c>DemoMountBuilder.BuildRider</c>) is a second copy of the whole body with its own
    /// bones, so when the kit switched to it and re-applied the equipment, every garment,
    /// hairstyle and beard was instantiated into the *main* model's containers - on the body
    /// that had just been switched off - and the rider sat on the horse in the prefab's bare
    /// default look. Nothing errored; the gear was there, on the hidden body.
    ///
    /// **The fix** is to make the seat model its own main model the moment the kit switches
    /// to it: point <c>MainModel</c> at itself and build its own caches. From then on its
    /// containers, its equipped-model bookkeeping and its hand entities are its own, and the
    /// ordinary <c>SwitchModel</c> -> <c>SetEquipItems</c> flow dresses it from the previous
    /// model's item lists exactly as it dresses the main body. The main model is untouched: it
    /// keeps its own gear while hidden and re-syncs from the rider's lists on dismount.
    ///
    /// Done in <see cref="OnSwitchingToThisModel"/> rather than Awake because the manager and
    /// the main model's cache setup both write <c>MainModel</c> during Awake, in an order that
    /// is not guaranteed; the switch hook runs after all of that and before the equipment is
    /// applied. On the main model it is a no-op, so every demo body can carry this class.
    ///
    /// **It also makes the kit's draw and sheathe hold together** - see <see cref="SetEquipItems"/>.
    /// </summary>
    public class SeatAwarePlayableCharacterModel : PlayableCharacterModel
    {
        private BaseCharacterEntity _ladderOwner;
        private bool _ladderOwnerLookedUp;

        // The draw or sheathe the kit is playing: its routine, and what it swaps the hands to.
        private Coroutine _holsterRoutine;
        private EquipWeapons _holsterTarget;

        /// <summary>
        /// The most time the animation is advanced by in one frame, in seconds. A frame the game stalls
        /// on would otherwise skip every clip ahead by the whole stall: the entity feeds the graph
        /// `Time.unscaledDeltaTime`, which Unity does not cap the way it caps `deltaTime`.
        /// </summary>
        private const float MaxAnimationStep = 0.1f;

        /// <summary>
        /// The kit's animation update, with the step capped (<see cref="MaxAnimationStep"/>), so a
        /// stall pauses the character's animation instead of skipping it. Found 2026-10-06: the first
        /// draw after logging in "just appeared in the hand". It is the first action clip the client
        /// plays, so the frame it starts in is the one the runtime compiles the whole action path in -
        /// 862ms of JIT in the editor - and the 0.93s draw was advanced 0.86s in one step: the next frame
        /// drawn was its last, with the weapon already in the fist. The weapon's swap is timed in real
        /// time from the end of that frame, so it still lands as the hand reaches the shoulder.
        /// </summary>
        public override void UpdateAnimation(float deltaTime)
        {
            base.UpdateAnimation(Mathf.Min(deltaTime, MaxAnimationStep));
        }

        /// <summary>
        /// The kit's weapon swap, with three faults of its own taken out (found 2026-10-04, when the
        /// user reported that drawing sometimes did nothing and sometimes played twice).
        ///
        /// The kit plays the draw or sheathe clip and swaps the weapon models partway through, at the
        /// trigger. Until that swap it still believes the old weapons are the ones shown. So:
        /// <list type="bullet">
        /// <item>**A change taken back before the swap was lost.** Draw during a sheathe, before the
        /// hand reached the back: the kit sees "same as shown", re-dresses at once and returns - but
        /// the sheathe it had started kept running and put the weapon on the back anyway. The
        /// character then fought empty-handed with the flag saying drawn, the next Z "sheathed" with
        /// nothing to see, and only the Z after that drew. Now the gesture is stopped and the
        /// weapon stays where it is.</item>
        /// <item>**The same change asked for twice restarted the clip.** Any other appearance update
        /// before the swap (the flag syncing, gear changing) started the routine again from the top:
        /// the first one was cut, its weapon swapped in, and the reach played a second time. Now the
        /// one under way is left to finish.</item>
        /// <item>**Every draw and sheathe played on both action layers.** The kit decides which hands
        /// changed by item id, and it records what is shown with `Clone()`, which gives an item with
        /// no id a new unique one - so an empty left hand never matched anything and always "changed".
        /// The left hand's clip went on layer 0 - for a bow, the SWORD's reach, under the bow's own on
        /// layer 1 - and the first swing, which goes on layer 0, was hidden under the rest of the
        /// draw on layer 1 for 0.4s. Empty hands are now compared as empty.</item>
        /// </list>
        /// </summary>
        public override void SetEquipItems(IList<CharacterItem> equipItems, IList<EquipWeapons> selectableWeaponSets, byte equipWeaponSet, bool isWeaponsSheathed)
        {
            EquipWeapons target = TargetWeapons(selectableWeaponSets, equipWeaponSet, isWeaponsSheathed);
            MatchEmptyHands(target);
            if (HolsterPending())
            {
                if (SameWeapons(target, _holsterTarget))
                {
                    EquipItems = equipItems;
                    SelectableWeaponSets = selectableWeaponSets;
                    EquipWeaponSet = equipWeaponSet;
                    IsWeaponsSheathed = isWeaponsSheathed;
                    UpdateEquipmentModels(equipItems, selectableWeaponSets, equipWeaponSet, isWeaponsSheathed).Forget();
                    return;
                }
                if (SameWeapons(target, _oldEquipWeapons.Value))
                    CancelHolster();
            }
            Coroutine before = _actionCoroutine;
            base.SetEquipItems(equipItems, selectableWeaponSets, equipWeaponSet, isWeaponsSheathed);
            if (_actionCoroutine != null && _actionCoroutine != before)
            {
                _holsterRoutine = _actionCoroutine;
                _holsterTarget = target;
            }
        }

        /// <summary>
        /// True while a draw or sheathe has not yet swapped the weapons: its routine is still the
        /// model's action (an attack or skill replaces it, and the kit then swaps at once) and the
        /// hands are not yet what it swaps them to. Weapon models swapped is what counts, not the
        /// clip ending - after the swap a change is a fresh draw or sheathe, as it should be.
        /// </summary>
        public bool HolsterPending()
        {
            return _isDoingAction && _holsterRoutine != null && _actionCoroutine == _holsterRoutine && _oldEquipWeapons.HasValue &&
                !SameWeapons(_oldEquipWeapons.Value, _holsterTarget);
        }

        /// <summary>The kit's comparison (item, level, sockets, seed, ammo type), with two empty hands always alike.</summary>
        private static bool SameWeapons(EquipWeapons a, EquipWeapons b)
        {
            return SameHand(a.rightHand, b.rightHand) && SameHand(a.leftHand, b.leftHand);
        }

        private static bool SameHand(CharacterItem a, CharacterItem b)
        {
            if (a.IsEmptySlot() && b.IsEmptySlot())
                return true;
            if (a.dataId != b.dataId)
                return false;
            if (string.IsNullOrWhiteSpace(a.id) || string.IsNullOrWhiteSpace(b.id))
                return a.level == b.level;
            return !a.IsDiffer(b, true, true, true, true);
        }

        private void CancelHolster()
        {
            StopCoroutine(_holsterRoutine);
            _holsterRoutine = null;
            _actionCoroutine = null;
            _onStopAction = null;
            _isDoingAction = false;
            if (Behaviour != null)
                Behaviour.StopAction();
        }

        /// <summary>What the kit swaps the hands to for these arguments - see `PlayableCharacterModel.SetEquipItems`.</summary>
        private static EquipWeapons TargetWeapons(IList<EquipWeapons> selectableWeaponSets, byte equipWeaponSet, bool isWeaponsSheathed)
        {
            if (isWeaponsSheathed || selectableWeaponSets == null || equipWeaponSet >= selectableWeaponSets.Count)
                return new EquipWeapons();
            return selectableWeaponSets[equipWeaponSet];
        }

        /// <summary>
        /// Makes an empty hand in what is shown read as the same empty hand as in the target. A plain
        /// copy, not `Clone()`: that gives an item with no id a new unique one, which is how the kit's
        /// own record of what is shown (`_oldEquipWeapons = newEquipWeapons.Clone()`) came to differ
        /// from every empty hand it was compared with.
        /// </summary>
        private void MatchEmptyHands(EquipWeapons target)
        {
            if (!_oldEquipWeapons.HasValue)
                return;
            EquipWeapons shown = _oldEquipWeapons.Value;
            if (shown.IsEmptyRightHandSlot() && target.IsEmptyRightHandSlot())
                shown.rightHand = target.rightHand;
            if (shown.IsEmptyLeftHandSlot() && target.IsEmptyLeftHandSlot())
                shown.leftHand = target.leftHand;
            _oldEquipWeapons = shown;
        }

        /// <summary>
        /// A swing or a cast ends the draw's gesture on every layer. The kit plays it on layer 1 as
        /// well when both hands change (a sword and a shield), and the swing goes on layer 0, under it.
        /// </summary>
        public override void PlayActionAnimation(AnimActionType animActionType, int dataId, int index, out bool skipMovementValidation, out bool shouldUseRootMotion, float playSpeedMultiplier, float changeClipLength, float overrideClipLength)
        {
            StopHolsterLayer();
            base.PlayActionAnimation(animActionType, dataId, index, out skipMovementValidation, out shouldUseRootMotion, playSpeedMultiplier, changeClipLength, overrideClipLength);
        }

        public override void PlaySkillCastClip(int dataId, float duration, out bool skipMovementValidation, out bool shouldUseRootMotion)
        {
            StopHolsterLayer();
            base.PlaySkillCastClip(dataId, duration, out skipMovementValidation, out shouldUseRootMotion);
        }

        private void StopHolsterLayer()
        {
            if (Behaviour != null)
                Behaviour.StopAction(1);
        }

        internal override void OnSwitchingToThisModel()
        {
            base.OnSwitchingToThisModel();
            ClaimOwnEquipment();
        }

        /// <summary>
        /// Keeps a character on a ladder in the climbing animations whether or not it is
        /// moving. The kit raises <see cref="MovementState.IsClimbing"/> only while the
        /// climber is being moved up or down; a climber hanging still, or being carried on
        /// or off the rungs, is reported as plainly grounded, and the model then played the
        /// standing idle in mid-air. The ladder component knows better, so the flag is put
        /// back here, where the entity hands the state over, before the state is played.
        /// </summary>
        public override void PlayMoveAnimation()
        {
            if ((MovementState & MovementState.IsClimbing) == 0 && OnLadder())
                MovementState |= MovementState.IsClimbing;
            base.PlayMoveAnimation();
        }

        private bool OnLadder()
        {
            if (!_ladderOwnerLookedUp)
            {
                _ladderOwner = GetComponentInParent<BaseCharacterEntity>();
                _ladderOwnerLookedUp = true;
            }
            return _ladderOwner != null && _ladderOwner.LadderComponent != null && _ladderOwner.LadderComponent.ClimbingLadder != null;
        }

        /// <summary>
        /// Makes this model the owner of its own equipment containers, effect containers and
        /// equipped-model caches. Idempotent, and a no-op on a model that is already main.
        /// </summary>
        public void ClaimOwnEquipment()
        {
            if (IsMainModel)
                return;
            MainModel = this;
            // Never ran for a non-main model: the kit only initialises the main model's
            // caches. Guarded inside, so a second call is free.
            InitCacheData();
        }
    }
}
