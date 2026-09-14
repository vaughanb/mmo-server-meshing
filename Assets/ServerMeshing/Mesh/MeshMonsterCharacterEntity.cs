// Server Meshing add-on — MeshMonsterCharacterEntity (DESIGN.md §12).
// Add-on only: the demo enemy prefab uses this class instead of MonsterCharacterEntity.
//  - A puppet monster must not take buffs/debuffs locally: the kit's ApplyBuff on a ghost is forwarded to the owner.
//  - A kill by a real player here shares with party members standing on another cell (their ghost is here): the kit's
//    GivingRewardToParty only sees members online on this server, so it is re-run with the ghosts in the split.
using System.Collections.Generic;

namespace MultiplayerARPG.MMO.Mesh
{
    public class MeshMonsterCharacterEntity : MonsterCharacterEntity
    {
        /// <summary>
        /// Set the summoner through the kit's protected setters, so its private entity cache follows the change (the
        /// public sync field alone leaves a re-linked puppet following, and dying with, a stale entity). Null unlinks.
        /// </summary>
        public void MeshLinkSummoner(BaseCharacterEntity summoner, SummonType type)
        {
            SummonType = summoner != null ? type : SummonType.None;
            SummonerEntity = summoner;
        }

        public override void ApplyBuff(int dataId, BuffType type, int level, EntityInfo buffApplier, CharacterItem buffApplierWeapon)
        {
            if (IsServer && MeshIdentity.IsGhostEntity(this))
            {
                MeshMapNetworkManager mesh = BaseGameNetworkManager.Singleton as MeshMapNetworkManager;
                if (mesh != null && mesh.ForwardGhostAction(this, GhostActionKind.ApplyBuff, buffApplier,
                        writer => MeshMapNetworkManager.WriteApplyBuffPayload(writer, dataId, type, level, buffApplierWeapon)))
                    return;
            }
            base.ApplyBuff(dataId, type, level, buffApplier, buffApplierWeapon);
        }

#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        /// <summary>
        /// A killer that is a ghost here (a player who crossed the edge and keeps fighting in the band keeps its party
        /// and guild ids) is rewarded by its OWNER cell: the kit's reward to the ghost is forwarded raw (MeshGameplayRule)
        /// and the owner runs the guild and party split with the real character, and GhostKill carries the kill
        /// progress. Sharing here as well would pay and count every member twice.
        /// </summary>
        protected override void GivingRewardToGuild(BasePlayerCharacterEntity playerCharacterEntity, Reward reward, float rewardRate, out float shareGuildExpRate)
        {
            if (MeshIdentity.IsGhostEntity(playerCharacterEntity))
            {
                shareGuildExpRate = 0f;
                return;
            }
            base.GivingRewardToGuild(playerCharacterEntity, reward, rewardRate, out shareGuildExpRate);
        }

        protected override void GivingRewardToParty(BasePlayerCharacterEntity playerCharacterEntity, bool isLastAttacker, Reward reward, float rewardRate, float shareGuildExpRate, bool makeMostDamage, out bool givenRewardExp, out bool givenRewardGold, out bool givenRewardCurrencies)
        {
            givenRewardExp = false;
            givenRewardGold = false;
            givenRewardCurrencies = false;
            if (MeshIdentity.IsGhostEntity(playerCharacterEntity))
                return; // see GivingRewardToGuild: the kit rewards the ghost in full, the owner cell splits it
            MeshMapNetworkManager mesh = BaseGameNetworkManager.Singleton as MeshMapNetworkManager;
            if (mesh == null || !GameInstance.ServerPartyHandlers.TryGetParty(playerCharacterEntity.PartyId, out PartyData party) || party == null)
            {
                base.GivingRewardToParty(playerCharacterEntity, isLastAttacker, reward, rewardRate, shareGuildExpRate, makeMostDamage, out givenRewardExp, out givenRewardGold, out givenRewardCurrencies);
                return;
            }
            MeshMapNetworkManager.PartyShareSets sets = mesh.CollectPartyMembers(playerCharacterEntity, party);
            if (!sets.AnyAcrossTheLine)
            {
                // Nobody across the line: the kit's own routine is exact.
                base.GivingRewardToParty(playerCharacterEntity, isLastAttacker, reward, rewardRate, shareGuildExpRate, makeMostDamage, out givenRewardExp, out givenRewardGold, out givenRewardCurrencies);
                return;
            }
            // Mirrors BaseMonsterCharacterEntity.GivingRewardToParty with the ghosts and remote members counted in the
            // split; their share and kill progress go to the cell that owns them.
            if (isLastAttacker)
                mesh.CreditKillToPartyMembers(playerCharacterEntity, party, Level, Level, DataId, member => { member.OnKillMonster(this); return true; }, out _);
            if (CurrentGameInstance.monsterExpRewardingMode == RewardingMode.Immediately && !reward.NoExp())
                mesh.SharePartyReward(playerCharacterEntity, sets.expMembers, sets.expGhosts, sets.expRemote, reward.exp, (1f - shareGuildExpRate) * rewardRate, 0, 0f, null, Level, Level);
            bool giveGold = CurrentGameInstance.monsterGoldRewardingMode == RewardingMode.Immediately && !reward.NoGold();
            bool giveCurrencies = CurrentGameInstance.monsterCurrencyRewardingMode == RewardingMode.Immediately && !reward.NoCurrencies();
            if (giveGold || giveCurrencies)
            {
                if (makeMostDamage)
                {
                    // As the kit does for every sharing member: a member across the line loots the drop through its
                    // ghost (GhostPickup validates the same id); a remote one only if it comes within reach.
                    foreach (BasePlayerCharacterEntity member in sets.itemMembers) _looters.Add(member.Id);
                    foreach (BasePlayerCharacterEntity ghost in sets.itemGhosts) _looters.Add(ghost.Id);
                    foreach (string remote in sets.itemRemote) _looters.Add(remote);
                }
                mesh.SharePartyReward(playerCharacterEntity, sets.itemMembers, sets.itemGhosts, sets.itemRemote, 0, 0f, giveGold ? reward.gold : 0, rewardRate, giveCurrencies ? reward.currencies : null, Level, Level);
            }
            if (party.shareExp)
                givenRewardExp = true;
            if (party.shareItem)
            {
                givenRewardGold = true;
                givenRewardCurrencies = true;
            }
        }
#endif
    }
}
