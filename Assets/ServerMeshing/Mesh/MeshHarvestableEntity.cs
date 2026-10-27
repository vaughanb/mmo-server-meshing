// Server Meshing add-on — MeshHarvestableEntity (DESIGN.md §12, static entities across the edge).
//
// The kit's harvestable gives its yield to the attacker in ApplyReceiveDamage: items into the attacker's bag, exp
// through the gameplay rule. When the attacker is a GHOST here (the swing came over the link from the cell the
// attacker stands on), the bag is a puppet's and the items would vanish. This subclass (swapped onto the demo /
// test harvestable prefabs) computes the yield exactly as the kit does and sends it to the attacker's owner as a
// GiveItems action instead; the damage to the node is applied as the kit does. A full bag on the real character drops
// the yield on the ground next to it, as the kit does; a non-character attacker drops it here, as the kit does.
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public class MeshHarvestableEntity : HarvestableEntity
    {
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        protected override void ApplyReceiveDamage(HitBoxPosition position, Vector3 fromPosition, EntityInfo instigator, DamageElementMinMaxFloatAmounts damageAmounts, CharacterItem weapon, BaseSkill skill, int skillLevel, int randomSeed, out CombatAmountType combatAmountType, out int totalDamage)
        {
            instigator.TryGetEntity(out BaseCharacterEntity attackerCharacter);
            MeshMapNetworkManager mesh = BaseGameNetworkManager.Singleton as MeshMapNetworkManager;
            if (attackerCharacter == null || mesh == null || !MeshIdentity.IsGhostEntity(attackerCharacter))
            {
                base.ApplyReceiveDamage(position, fromPosition, instigator, damageAmounts, weapon, skill, skillLevel, randomSeed, out combatAmountType, out totalDamage);
                return;
            }
            // Mirrors HarvestableEntity.ApplyReceiveDamage with the yield routed to the ghost's owner.
            float calculatingTotalDamage = 0f;
            HarvestType skillHarvestType = HarvestType.BasedOnWeapon;
            if (skill != null && skillLevel > 0)
                skillHarvestType = skill.HarvestType;
            WeightedRandomizer<ItemDropForHarvestable> itemRandomizer = null;
            switch (skillHarvestType)
            {
                case HarvestType.BasedOnWeapon:
                    {
                        IWeaponItem weaponItem = weapon.GetWeaponItem();
                        if (weaponItem != null && harvestable.CacheHarvestEffectivenesses.TryGetValue(weaponItem.WeaponType, out HarvestEffectiveness harvestEffectiveness) &&
                            harvestable.CacheHarvestItems.TryGetValue(weaponItem.WeaponType, out itemRandomizer))
                            calculatingTotalDamage = weaponItem.HarvestDamageAmount.GetAmount(weapon.level).Random(randomSeed) * harvestEffectiveness.damageEffectiveness;
                    }
                    break;
                case HarvestType.BasedOnSkill:
                    {
                        if (harvestable.CacheSkillHarvestEffectivenesses.TryGetValue(skill, out SkillHarvestEffectiveness skillHarvestEffectiveness) &&
                            harvestable.CacheSkillHarvestItems.TryGetValue(skill, out itemRandomizer))
                            calculatingTotalDamage = skill.HarvestDamageAmount.GetAmount(skillLevel).Random(randomSeed) * skillHarvestEffectiveness.damageEffectiveness;
                    }
                    break;
            }
            if (skillHarvestType != HarvestType.None && itemRandomizer != null)
            {
                ItemDropForHarvestable receivingItem = itemRandomizer.TakeOne();
                int itemDataId = receivingItem.item.DataId;
                int itemAmount = (int)(receivingItem.amountPerDamage * calculatingTotalDamage);
                int exp = (int)(harvestable.expPerDamage * calculatingTotalDamage);
                MeshIdentity attackerId = MeshIdentity.Get(attackerCharacter);
                bool ownerReachable = attackerId != null && !string.IsNullOrEmpty(attackerId.OwnerZoneId) && mesh.IsNeighbourLinked(attackerId.OwnerZoneId);
                if (collectType == HarvestableCollectType.DropToGround || !ownerReachable)
                {
                    // The node drops on the ground here (also when the attacker's cell cannot be reached right now: the kit
                    // never loses a harvest); the drop is ghosted to the attacker's cell like any drop.
                    ItemDropEntity.Drop(this, RewardGivenType.Harvestable, CharacterItem.Create(itemDataId, 1, itemAmount), System.Array.Empty<string>()).Forget();
                    if (exp > 0)
                        attackerCharacter.RewardExp(exp, 1, RewardGivenType.Harvestable, 1, 1); // MeshGameplayRule forwards exp given to a ghost
                }
                else
                {
                    int dataId = itemDataId, amount = itemAmount, xp = exp;
                    mesh.ForwardGhostAction(attackerCharacter, GhostActionKind.GiveItems, EntityInfo.Empty, w => MeshMapNetworkManager.WriteGiveItemsPayload(w, dataId, amount, xp));
                }
            }
            combatAmountType = CombatAmountType.NormalDamage;
            totalDamage = CurrentGameInstance.GameplayRule.GetTotalDamage(fromPosition, instigator, this, calculatingTotalDamage, weapon, skill, skillLevel);
            if (totalDamage < 0)
                totalDamage = 0;
            CurrentHp -= totalDamage;
        }
#endif
    }
}
