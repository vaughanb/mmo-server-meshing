using Cysharp.Threading.Tasks;
using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// Puts a monster's gold into its loot corpse, so the coins have to be looted like
    /// everything else instead of landing in the purse the moment the monster dies.
    ///
    /// **The kit has no "gold into the corpse" mode.** `monsterGoldRewardingMode` is either
    /// `Immediately`, which credits the killers (and shares with the party) inside `Killed`,
    /// or `DropOnGround`, which spawns the `goldDropEntityPrefab` next to the body as a pile
    /// with the amount, the looters and the given type already set on it. Items, meanwhile,
    /// go into an `ItemsContainerEntity` corpse under `CorpseLooting`. There is no hook between
    /// the two, and `OnRandomDropItem` turns a gold item in a drop table back into reward gold.
    ///
    /// **So the pile is the hook.** The demo runs gold in `DropOnGround` mode and this sits on
    /// the gold drop prefab, which has no model. On the server, the frame after it spawns, it
    /// wraps its amount in the gold represent item (<c>GoldCoins</c>, see `GameInstance.goldDropRepresentItem`)
    /// and adds it to the corpse the same kill just dropped (see <see cref="FindCorpse"/>). A
    /// monster that rolled no item drops no corpse, so then it drops one of its own holding only
    /// the coins, with the usual corpse lifetime; its looter lock is whatever the pile carried,
    /// which is usually nobody (see the note on FindCorpse), so such a purse is open to anyone
    /// at once. Either way the pile then destroys itself; clients see nothing of it.
    ///
    /// Verified 2026-10-02 in the LAN harness: bandit corpse `[Ranger Bracers] [Potion x2]
    /// [Jerkin] [Gold x3]`, purse unchanged on the kill, 100 -> 103 on taking the coins, the
    /// other items left in place; a wolf's corpse held only its fang.
    ///
    /// Picking the coins out of the corpse goes through the kit's own container pickup, which
    /// recognises the represent item and credits gold (`BaseCharacterEntity_NetworkResponse`),
    /// applying the gold rate and the guild bonus at that point rather than on the kill.
    /// </summary>
    [RequireComponent(typeof(GoldDropEntity))]
    public class GoldDropToCorpse : MonoBehaviour
    {
        [Tooltip("How far from the coins the corpse of the same kill may lie. The kit scatters a drop up to " +
                 "`dropDistance` (1 m in the demo) from the body; the corpse itself sits on the body.")]
        public float corpseSearchRadius = 3f;

        private GoldDropEntity _entity;
        private bool _handled;

        private void Awake()
        {
            _entity = GetComponent<GoldDropEntity>();
        }

        private void OnEnable()
        {
            // Pooled: a reused instance starts over.
            _handled = false;
        }

        private void Update()
        {
            // The kit sets Amount, Looters and GivenType before NetworkSpawn, so by the first
            // Update they are all there, and so is the corpse the same Killed call dropped.
            if (_handled || !_entity.IsServer || _entity.Amount <= 0)
                return;
            _handled = true;
            Deliver().Forget();
        }

        private async UniTaskVoid Deliver()
        {
            BaseItem gold = GameInstance.Singleton.GoldDropRepresentItem;
            if (gold == null)
            {
                Debug.LogWarning("[GoldDropToCorpse] GameInstance has no gold represent item; the coins stay on the ground as the kit's pile.");
                return;
            }

            CharacterItem coins = CharacterItem.Create(gold, 1, _entity.Amount);
            Vector3 position = _entity.EntityTransform.position;
            ItemsContainerEntity corpse = _entity.GivenType == RewardGivenType.KillMonster ? FindCorpse(position) : null;
            if (corpse != null)
            {
                corpse.Items.Add(coins);
            }
            else
            {
                ItemsContainerEntity prefab = await GameInstance.Singleton.GetLoadedMonsterCorpsePrefab();
                if (prefab == null)
                {
                    Debug.LogWarning("[GoldDropToCorpse] GameInstance has no monster corpse prefab; the coins stay on the ground as the kit's pile.");
                    return;
                }
                float duration = _entity.GivenType == RewardGivenType.KillMonster
                    ? GameInstance.Singleton.monsterCorpseAppearDuration
                    : GameInstance.Singleton.itemAppearDuration;
                ItemsContainerEntity.DropItems(prefab, null, position, _entity.EntityTransform.rotation, _entity.GivenType, new[] { coins }, _entity.Looters, duration);
            }
            _entity.NetworkDestroy(0f);
        }

        /// <summary>
        /// The corpse this kill dropped. `Killed` spawns the corpse and then the pile, so the
        /// corpse normally has the object id just below this pile's: that is an exact match.
        /// Otherwise the newest unlooted monster corpse near the coins, and with the same
        /// looters when the pile knows any - it usually does not: `GoldDropEntity.Drop` awaits
        /// its prefab, and by the time it copies the monster's looter set `Killed` has cleared
        /// it (measured 2026-10-02: corpse with one looter, pile with none). Kills are rare
        /// enough for a scene scan.
        /// </summary>
        private ItemsContainerEntity FindCorpse(Vector3 position)
        {
            float radiusSqr = corpseSearchRadius * corpseSearchRadius;
            ItemsContainerEntity best = null;
            foreach (ItemsContainerEntity container in FindObjectsByType<ItemsContainerEntity>(FindObjectsSortMode.None))
            {
                if (container.GivenType != RewardGivenType.KillMonster || container.Items.Count == 0)
                    continue;
                if ((container.EntityTransform.position - position).sqrMagnitude > radiusSqr)
                    continue;
                if (container.ObjectId + 1 == _entity.ObjectId)
                    return container;
                if (_entity.Looters.Count > 0 && (container.Looters == null || !container.Looters.SetEquals(_entity.Looters)))
                    continue;
                if (best == null || container.ObjectId > best.ObjectId)
                    best = container;
            }
            return best;
        }
    }
}
