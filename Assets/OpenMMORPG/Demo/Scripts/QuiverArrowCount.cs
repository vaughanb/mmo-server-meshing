using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// How many arrows a character has for the bow they carry, as the server counts them, synced to
    /// every client - what a <see cref="QuiverArrows"/> on their back shows.
    ///
    /// **It has to be synced on its own because the pack is not.** Arrows are ammo items in the
    /// character's bag, and the kit sends the bag to its owner only (`nonEquipItems.forOwnerOnly`), so a
    /// client looking at somebody else's archer has no way to count what is in their quiver. The server
    /// can, and this is that one number: the arrows the first ammo-using weapon the character carries
    /// would draw from (the one in hand first, then the other weapon set's), capped at 254 - the quiver
    /// never shows more than a dozen.
    ///
    /// **The owner does not wait for it**: their own bag is synced to them, so they count it themselves,
    /// exactly as the server does. That keeps a player's own quiver right even against a map server built
    /// before this existed (2026-10-05: an empty quiver over a HUD reading 69 - the server had no such
    /// component and never sent a count). The synced figure is the count **plus one**, so 0 means "never
    /// heard from the server", and somebody else's archer is then shown with a full quiver rather than an
    /// empty one.
    ///
    /// Recounted when the bag or the weapons change, not every frame. Added to every player entity on every
    /// peer by <c>DemoEntitySetting</c> as it wakes, before the spawn, which is when the network identity
    /// collects its behaviours. Sync elements are keyed by type and field name, not by component order, so
    /// being added at run time rather than on the prefab costs nothing - but other players' counts need the
    /// map server to have it too (**Build Map Server** after adding it).
    /// </summary>
    [DisallowMultipleComponent]
    public class QuiverArrowCount : LiteNetLibBehaviour
    {
        /// <summary>The server's count plus one; 0 until the server has sent one.</summary>
        [SerializeField]
        protected SyncFieldByte arrows = new SyncFieldByte();

        private BaseCharacterEntity _entity;
        private bool _dirty = true;
        private int _ownCount;

        /// <summary>
        /// True once there is a count to show: always for the server and the owner, who count the bag
        /// themselves; for anyone else once the server's has arrived.
        /// </summary>
        public bool HasCount { get { return CountsOwnBag || arrows.Value > 0; } }

        /// <summary>The arrows the character carries for their bow. 0 with no bow, or with no count yet (see <see cref="HasCount"/>).</summary>
        public int Count { get { return CountsOwnBag ? _ownCount : Mathf.Max(0, arrows.Value - 1); } }

        /// <summary>The peers that hold this character's bag: the server and the owning client.</summary>
        private bool CountsOwnBag { get { return IsServer || IsOwnerClient; } }

        /// <summary>
        /// True on a character the server is counting for: a spawned one. False on the copies the menu
        /// screens and the character sheet put on show, which are never spawned.
        /// </summary>
        public bool IsLive { get { return IsSpawned; } }

        private void Awake()
        {
            _entity = GetComponent<BaseCharacterEntity>();
            if (_entity == null)
                return;
            _entity.onNonEquipItemsOperation += OnNonEquipItemsOperation;
            _entity.onSelectableWeaponSetsOperation += OnSelectableWeaponSetsOperation;
            _entity.onEquipWeaponSetChange += OnEquipWeaponSetChange;
        }

        private void OnDestroy()
        {
            if (_entity == null)
                return;
            _entity.onNonEquipItemsOperation -= OnNonEquipItemsOperation;
            _entity.onSelectableWeaponSetsOperation -= OnSelectableWeaponSetsOperation;
            _entity.onEquipWeaponSetChange -= OnEquipWeaponSetChange;
        }

        public override void OnSetup()
        {
            base.OnSetup();
            arrows.syncMode = LiteNetLibSyncFieldMode.ServerToClients;
        }

        private void OnNonEquipItemsOperation(LiteNetLibSyncListOp op, int index, CharacterItem oldItem, CharacterItem newItem)
        {
            _dirty = true;
        }

        private void OnSelectableWeaponSetsOperation(LiteNetLibSyncListOp op, int index, EquipWeapons oldItem, EquipWeapons newItem)
        {
            _dirty = true;
        }

        private void OnEquipWeaponSetChange(BaseCharacterEntity entity, byte oldSet, byte newSet)
        {
            _dirty = true;
        }

        private void Update()
        {
            // The bag fills in before the spawn, when no event is raised, so the first count is made on
            // the first frame this peer has the character - _dirty starts true.
            if (!_dirty || _entity == null || !CountsOwnBag)
                return;
            _dirty = false;
            _ownCount = Mathf.Clamp(CountFor(_entity), 0, byte.MaxValue - 1);
            if (IsServer && arrows.Value != _ownCount + 1)
                arrows.Value = (byte)(_ownCount + 1);
        }

        /// <summary>The ammo the character carries for the first weapon they hold that needs any, or 0.</summary>
        public static int CountFor(BaseCharacterEntity entity)
        {
            if (entity == null)
                return 0;
            CharacterItem weapon;
            IWeaponItem item;
            if (TryAmmoWeapon(entity.EquipWeapons, out weapon, out item))
                return entity.CountAllAmmos(item) + weapon.ammo;
            var sets = entity.SelectableWeaponSets;
            for (int i = 0; sets != null && i < sets.Count; ++i)
            {
                if (TryAmmoWeapon(sets[i], out weapon, out item))
                    return entity.CountAllAmmos(item) + weapon.ammo;
            }
            return 0;
        }

        private static bool TryAmmoWeapon(EquipWeapons weapons, out CharacterItem weapon, out IWeaponItem item)
        {
            weapon = weapons.rightHand;
            item = weapon.GetWeaponItem();
            if (UsesAmmo(item))
                return true;
            weapon = weapons.leftHand;
            item = weapon.GetWeaponItem();
            return UsesAmmo(item);
        }

        private static bool UsesAmmo(IWeaponItem item)
        {
            return item != null && item.WeaponType != null && item.WeaponType.AmmoType != null;
        }
    }
}
