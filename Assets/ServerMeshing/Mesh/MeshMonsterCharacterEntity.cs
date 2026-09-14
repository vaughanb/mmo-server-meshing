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
        protected override void GivingRewardToParty(BasePlayerCharacterEntity playerCharacterEntity, bool isLastAttacker, Reward reward, float rewardRate, float shareGuildExpRate, bool makeMostDamage, out bool givenRewardExp, out bool givenRewardGold, out bool givenRewardCurrencies)
        {
            givenRewardExp = false;
            givenRewardGold = false;
            givenRewardCurrencies = false;
            MeshMapNetworkManager mesh = BaseGameNetworkManager.Singleton as MeshMapNetworkManager;
            if (mesh == null || !GameInstance.ServerPartyHandlers.TryGetParty(playerCharacterEntity.PartyId, out PartyData party) || party == null)
            {
                base.GivingRewardToParty(playerCharacterEntity, isLastAttacker, reward, rewardRate, shareGuildExpRate, makeMostDamage, out givenRewardExp, out givenRewardGold, out givenRewardCurrencies);
                return;
            }
            List<BasePlayerCharacterEntity> expMembers = new List<BasePlayerCharacterEntity>();
            List<BasePlayerCharacterEntity> itemMembers = new List<BasePlayerCharacterEntity>();
            List<BasePlayerCharacterEntity> expGhosts = new List<BasePlayerCharacterEntity>();
            List<BasePlayerCharacterEntity> itemGhosts = new List<BasePlayerCharacterEntity>();
            List<string> expRemote = new List<string>();
            List<string> itemRemote = new List<string>();
            mesh.CollectPartyMembers(playerCharacterEntity, party, expMembers, itemMembers, expGhosts, itemGhosts, expRemote, itemRemote);
            if (expGhosts.Count == 0 && itemGhosts.Count == 0 && expRemote.Count == 0 && itemRemote.Count == 0)
            {
                // Nobody across the line: the kit's own routine is exact.
                base.GivingRewardToParty(playerCharacterEntity, isLastAttacker, reward, rewardRate, shareGuildExpRate, makeMostDamage, out givenRewardExp, out givenRewardGold, out givenRewardCurrencies);
                return;
            }
            // Mirrors BaseMonsterCharacterEntity.GivingRewardToParty with the ghosts counted in the split; their
            // share and kill progress go to the cell that owns them.
            if (isLastAttacker)
            {
                // Kill progress for every online member, as the kit does (independent of the share flags); a member on
                // another cell gets it through its own cell.
                foreach (string memberId in party.GetMemberIds())
                {
                    if (memberId == playerCharacterEntity.Id)
                        continue;
                    if (GameInstance.ServerUserHandlers.TryGetPlayerCharacterById(memberId, out BasePlayerCharacterEntity member) && member != null)
                    {
                        if (!member.IsDead())
                            member.OnKillMonster(this);
                    }
                    else if (mesh.TryGetPlayerGhost(memberId, out BasePlayerCharacterEntity memberGhost))
                        mesh.ForwardPartyShare(memberGhost, 0, 0f, 0, 0f, null, Level, Level, DataId, true);
                    else if (mesh.linkAllZones)
                        mesh.BroadcastPartyShare(memberId, 0, 0f, 0, 0f, null, Level, Level, DataId, true);
                }
            }
            if (CurrentGameInstance.monsterExpRewardingMode == RewardingMode.Immediately && !reward.NoExp() && expMembers.Count + expGhosts.Count + expRemote.Count > 0)
            {
                float count = expMembers.Count + expGhosts.Count + expRemote.Count;
                float expMultiplier = (1f - shareGuildExpRate) / count * rewardRate;
                foreach (BasePlayerCharacterEntity member in expMembers)
                    mesh.GivePartyShareHere(member, reward.exp, expMultiplier, 0, 0f, null, member.ObjectId == playerCharacterEntity.ObjectId ? RewardGivenType.KillMonster : RewardGivenType.PartyShare, Level, Level);
                foreach (BasePlayerCharacterEntity ghost in expGhosts)
                    mesh.ForwardPartyShare(ghost, reward.exp, expMultiplier, 0, 0f, null, Level, Level, DataId, false);
                foreach (string remote in expRemote)
                    mesh.BroadcastPartyShare(remote, reward.exp, expMultiplier, 0, 0f, null, Level, Level, DataId, false);
            }
            bool giveGold = CurrentGameInstance.monsterGoldRewardingMode == RewardingMode.Immediately && !reward.NoGold();
            bool giveCurrencies = CurrentGameInstance.monsterCurrencyRewardingMode == RewardingMode.Immediately && !reward.NoCurrencies();
            if ((giveGold || giveCurrencies) && itemMembers.Count + itemGhosts.Count + itemRemote.Count > 0)
            {
                float count = itemMembers.Count + itemGhosts.Count + itemRemote.Count;
                float multiplier = 1f / count * rewardRate;
                foreach (BasePlayerCharacterEntity member in itemMembers)
                {
                    if (makeMostDamage)
                        _looters.Add(member.Id);
                    mesh.GivePartyShareHere(member, 0, 0f, giveGold ? reward.gold : 0, multiplier, giveCurrencies ? reward.currencies : null, member.ObjectId == playerCharacterEntity.ObjectId ? RewardGivenType.KillMonster : RewardGivenType.PartyShare, Level, Level);
                }
                foreach (BasePlayerCharacterEntity ghost in itemGhosts)
                {
                    if (makeMostDamage)
                        _looters.Add(ghost.Id); // its real character loots the drop through its ghost (GhostPickup validates the same id)
                    mesh.ForwardPartyShare(ghost, 0, 0f, giveGold ? reward.gold : 0, multiplier, giveCurrencies ? reward.currencies : null, Level, Level, DataId, false);
                }
                foreach (string remote in itemRemote)
                {
                    if (makeMostDamage)
                        _looters.Add(remote); // as the kit does for every sharing member; it can only loot if it comes within reach
                    mesh.BroadcastPartyShare(remote, 0, 0f, giveGold ? reward.gold : 0, multiplier, giveCurrencies ? reward.currencies : null, Level, Level, DataId, false);
                }
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
