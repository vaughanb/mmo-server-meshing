using System.Collections.Generic;
using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// A weapon skill that is a multiple of the character's *swing*, not of the weapon's
    /// item damage.
    ///
    /// The kit's `BasedOnWeapon` skill reads `GetCaches().RightHandWeaponDamage`, which is the
    /// weapon item's own damage at its refine level and nothing else: no attribute damage, no
    /// buff damage, no weapon-type effectiveness. It also ignores the skill's own
    /// `damageAmount` for that type. So once the warrior's Strength actually reached the
    /// sword (2026-10-02), a plain swing was 48-80 and Cleave - "125% of the weapon" - was
    /// 8-12 times 2.25, measured live at 26. The user's report: "Cleave is doing no damage".
    ///
    /// Here the base amount is the character's whole hand damage (`RightHandDamages`: weapon,
    /// Strength, Rallying Cry, refines - everything a swing carries, every element summed on
    /// the weapon's element), times <see cref="Skill.weaponDamageMultiplicator"/>, plus the
    /// skill's own <see cref="Skill.damageAmount"/> and its effectiveness attributes. The
    /// multiplicator is the **whole** multiple here - 1.25 is a hit and a quarter - where
    /// the kit adds it on top of the base weapon (1.25 there is 225%); measured live, the
    /// on-top reading made Cleave 124-204 against a 53-87 swing, and "half again a swing" is
    /// what DemoSkillBuilder's table has always said. The kit's own multiplicator step is
    /// switched off so the raw weapon is not added again. Everything else - reach, arc,
    /// debuffs, effects - is the kit's.
    ///
    /// `DemoSkillBuilder` puts this script on every skill whose spec has a `WeaponRate` (the
    /// warrior's Cleave and Shield Bash, the ranger's Aimed and Crippling Shot) by swapping the
    /// asset's script in place, so ids and every reference to the asset survive.
    /// </summary>
    public class SwingScaledWeaponSkill : Skill
    {
        public override bool TryGetBaseAttackDamageAmount(ICharacterData skillUser, int skillLevel, bool isLeftHand, out KeyValuePair<DamageElement, MinMaxFloat> result)
        {
            if (skillAttackType != SkillAttackType.BasedOnWeapon)
                return base.TryGetBaseAttackDamageAmount(skillUser, skillLevel, isLeftHand, out result);

            CharacterDataCache caches = skillUser.GetCaches();
            bool left = isLeftHand && caches.LeftHandWeaponDamage.HasValue;
            KeyValuePair<DamageElement, MinMaxFloat> weapon = left ? caches.LeftHandWeaponDamage.Value : caches.RightHandWeaponDamage.Value;
            Dictionary<DamageElement, MinMaxFloat> swing = left ? caches.LeftHandDamages : caches.RightHandDamages;

            MinMaxFloat total = default;
            if (swing != null)
            {
                foreach (MinMaxFloat part in swing.Values)
                    total += part;
            }
            else
            {
                total = weapon.Value;
            }

            float rate = weaponDamageMultiplicator.GetAmount(skillLevel);
            MinMaxFloat amount = total * rate
                + damageAmount.amount.GetAmount(skillLevel)
                + caches.IndexedAttributes.GetWeightedAmount(CacheEffectivenessAttributes);
            DamageElement element = weapon.Key == null ? GameInstance.Singleton.DefaultDamageElement : weapon.Key;
            result = new KeyValuePair<DamageElement, MinMaxFloat>(element, amount);
            return true;
        }

        public override bool TryGetAttackWeaponDamageMultiplicator(ICharacterData skillUser, int skillLevel, out float result)
        {
            // The multiple is folded into the base amount above, where it can apply to the
            // whole swing; left on, the kit would add the raw weapon times the rate again.
            if (skillAttackType == SkillAttackType.BasedOnWeapon)
            {
                result = 0f;
                return false;
            }
            return base.TryGetAttackWeaponDamageMultiplicator(skillUser, skillLevel, out result);
        }
    }
}
