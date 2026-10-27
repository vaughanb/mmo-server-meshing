// Server Meshing add-on — MeshGameplayRule (DESIGN.md §9 "Combat across an edge", P3 kill credit).
//
// When a monster dies on cell E, the kit rewards the entities in its damage records — which, for an attacker that
// lives on cell C, is the attacker's GHOST on E. This rule intercepts rewards given to a ghost and forwards the RAW
// amounts (before the gameplay rule's exp/gold rates, guild bonuses, pk reductions…) to the ghost's owner cell over
// the mesh link, where the same rule applies them to the real character with its real caches/guild/pk state.
//
// Add-on only: the demo's GameInstance is pointed at a MeshGameplayRule asset instead of the stock rule asset.
using System.Collections.Generic;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    [CreateAssetMenu(fileName = "MeshGameplayRule", menuName = "Open MMORPG/Server Meshing/Mesh Gameplay Rule")]
    public class MeshGameplayRule : SimpleGameplayRule
    {
        public override bool RewardExp(BaseCharacterEntity character, int exp, float multiplier, RewardGivenType rewardGivenType, int giverLevel, int sourceLevel, out int rewardedExp)
        {
            if (TryForward(character, exp, 0, null, multiplier, rewardGivenType, giverLevel, sourceLevel))
            {
                rewardedExp = 0;
                return false;
            }
            return base.RewardExp(character, exp, multiplier, rewardGivenType, giverLevel, sourceLevel, out rewardedExp);
        }

        public override void RewardGold(BaseCharacterEntity character, int gold, float multiplier, RewardGivenType rewardGivenType, int giverLevel, int sourceLevel, out int rewardedGold)
        {
            if (TryForward(character, 0, gold, null, multiplier, rewardGivenType, giverLevel, sourceLevel))
            {
                rewardedGold = 0;
                return;
            }
            base.RewardGold(character, gold, multiplier, rewardGivenType, giverLevel, sourceLevel, out rewardedGold);
        }

        public override void RewardCurrencies(BaseCharacterEntity character, IEnumerable<CurrencyAmount> currencies, float multiplier, RewardGivenType rewardGivenType, int giverLevel, int sourceLevel)
        {
            if (TryForward(character, 0, 0, currencies, multiplier, rewardGivenType, giverLevel, sourceLevel))
                return;
            base.RewardCurrencies(character, currencies, multiplier, rewardGivenType, giverLevel, sourceLevel);
        }

        private static bool TryForward(BaseCharacterEntity character, int exp, int gold, IEnumerable<CurrencyAmount> currencies, float multiplier, RewardGivenType rewardGivenType, int giverLevel, int sourceLevel)
        {
            BasePlayerCharacterEntity player = character as BasePlayerCharacterEntity;
            if (player == null || !MeshIdentity.IsGhostEntity(player))
                return false;
            MeshMapNetworkManager manager = BaseGameNetworkManager.Singleton as MeshMapNetworkManager;
            if (manager == null)
                return false;
            // Forwarded or not, a ghost never keeps a reward: if the owner link is down the reward is dropped, which
            // is the same outcome the kit produces for a player that logged out mid-kill.
            manager.ForwardGhostReward(player, exp, gold, currencies, multiplier, rewardGivenType, giverLevel, sourceLevel);
            return true;
        }
    }
}
