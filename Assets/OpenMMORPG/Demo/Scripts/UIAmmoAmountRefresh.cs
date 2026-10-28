using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// Keeps the HUD's arrow count true. Sits beside the kit's <see cref="UIAmmoAmount"/>, which
    /// Wire Demo UI adds it to.
    ///
    /// The kit's counter never redraws on its own in the demo (user report, 2026-10-02: "the UI
    /// said I had 31 arrows left, but when I tried to attack it said out of ammo" - the
    /// character had none). It redraws only when it is enabled and when the weapon's simulated
    /// clip changes, and it subscribes to the second in `OnEnable`, which bails out while there
    /// is no playing character yet. The HUD is enabled before the character spawns, so the
    /// subscription never happens and the number holds whatever it read on the last enable. A
    /// bow has no clip anyway (`ammoCapacity` 0, arrows are drawn straight from the pack), so
    /// even subscribed it would not hear about the pack changing.
    ///
    /// This watches what the number is made of - the arrows in the pack and the weapons in hand
    /// - and asks the counter to redraw when either changes. A Core bug; fixed here rather than
    /// in Core, which mirrors upstream.
    /// </summary>
    [RequireComponent(typeof(UIAmmoAmount))]
    public class UIAmmoAmountRefresh : MonoBehaviour
    {
        private UIAmmoAmount _counter;
        private BasePlayerCharacterEntity _player;
        private int _rightCount = -1, _leftCount = -1;
        private int _rightWeapon, _leftWeapon;

        private void Awake()
        {
            _counter = GetComponent<UIAmmoAmount>();
        }

        private void OnEnable()
        {
            // Force a redraw on the first frame there is a character to read.
            _player = null;
        }

        private void Update()
        {
            BasePlayerCharacterEntity player = GameInstance.PlayingCharacterEntity;
            if (player == null)
                return;

            CharacterItem right = player.EquipWeapons.rightHand;
            CharacterItem left = player.EquipWeapons.leftHand;
            int rightCount = Count(player, right);
            int leftCount = Count(player, left);
            if (player == _player && right.dataId == _rightWeapon && left.dataId == _leftWeapon &&
                rightCount == _rightCount && leftCount == _leftCount)
                return;

            _player = player;
            _rightWeapon = right.dataId;
            _leftWeapon = left.dataId;
            _rightCount = rightCount;
            _leftCount = leftCount;
            _counter.UpdateData();
        }

        private static int Count(BasePlayerCharacterEntity player, CharacterItem weapon)
        {
            IWeaponItem item = weapon.GetWeaponItem();
            return item == null ? 0 : player.CountAllAmmos(item) + weapon.ammo;
        }
    }
}
