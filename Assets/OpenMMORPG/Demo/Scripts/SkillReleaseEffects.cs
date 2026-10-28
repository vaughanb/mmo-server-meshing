using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// What a skill sets off at the instant it goes - the arrow leaving the string - as opposed to
    /// when its animation starts, which is the only moment the kit offers.
    ///
    /// `BaseSkill.SkillActivateEffects` is instantiated as the action animation begins. For a mage
    /// that is near enough: the spell leaves the hand 0.3s later. For a bow shot it is 1.2 seconds
    /// early - the whole draw - so a release flash hung there would go off as the archer reaches
    /// for the arrow. The moment that matters is the skill's trigger, which the kit announces on
    /// every peer (`ICharacterUseSkillComponent.OnUseSkillTrigger`) with nothing attached to it.
    /// <see cref="BowEquipmentEntity"/> listens for it and plays whatever this table lists for the skill.
    ///
    /// Written by DemoSkillBuilder (Build Skills) from each skill's `ReleaseEffect`.
    /// </summary>
    [CreateAssetMenu(menuName = "Open MMORPG/Skill Release Effects")]
    public class SkillReleaseEffects : ScriptableObject
    {
        [System.Serializable]
        public class Entry
        {
            public BaseSkill skill;
            public GameEffect[] effects = new GameEffect[0];
        }

        public Entry[] entries = new Entry[0];

        public GameEffect[] For(BaseSkill skill)
        {
            if (skill == null || entries == null)
                return null;
            foreach (Entry entry in entries)
            {
                if (entry != null && entry.skill == skill)
                    return entry.effects;
            }
            return null;
        }
    }
}
