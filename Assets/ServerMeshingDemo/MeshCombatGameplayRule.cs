// Server Meshing test rig — the kit demo's CombatGameplayRule with the add-on's kill credit (MeshGameplayRule's P3
// forwarding: a reward given to an attacker's ghost goes raw to its owner cell). MeshGameplayRule derives from
// SimpleGameplayRule, so the demo copy (ServerMeshDemo8kBuilder) would lose the demo's combat rules on it; this keeps both.
using System.Collections.Generic;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    [CreateAssetMenu(fileName = "MeshCombatGameplayRule", menuName = "Open MMORPG/Server Meshing/Mesh Combat Gameplay Rule")]
    public class MeshCombatGameplayRule : CombatGameplayRule
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
            manager.ForwardGhostReward(player, exp, gold, currencies, multiplier, rewardGivenType, giverLevel, sourceLevel);
            return true;
        }
    }
}
