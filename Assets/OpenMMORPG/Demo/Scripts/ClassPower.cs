namespace MultiplayerARPG
{
    /// <summary>
    /// What a class's MP slot holds. World of Warcraft's model: every character has one power
    /// bar in the same place, and the class decides what it is.
    ///
    /// - <see cref="Mana"/> - the kit's own MP: a pool that grows with level and Intelligence,
    ///   refilled by regeneration. The mage.
    /// - <see cref="Rage"/> - starts empty, is earned by landing weapon blows and by taking damage,
    ///   does not regenerate, and drains away once out of combat. The warrior.
    /// - <see cref="Focus"/> - a small fixed pool that refills quickly, in a fight or out of one, so
    ///   it paces shots rather than budgeting them. The ranger.
    ///
    /// Rage and focus live in the MP slot (user's call, 2026-10-06) rather than on stamina, which
    /// the kit spends on sprinting, or as a resource of their own: in the MP slot the kit's own cost
    /// check, deduction, tooltip, hotbar greying, error message and syncing all work unchanged. The
    /// rules are in <see cref="CombatGameplayRule"/> (gain, regen, respawn, level-up) and
    /// <see cref="ClassPowerUpkeep"/> (the out-of-combat drain); the bar's colour and wording in
    /// <see cref="ClassPowerBar"/>. Which class is which is set on the rule asset.
    /// </summary>
    public enum ClassPowerType
    {
        Mana,
        Rage,
        Focus,
    }

    public static class ClassPower
    {
        /// <summary>The demo's rule, if the game is running it.</summary>
        public static CombatGameplayRule Rule
        {
            get { return GameInstance.Singleton != null ? GameInstance.Singleton.GameplayRule as CombatGameplayRule : null; }
        }

        /// <summary>The power of the player class with this data id. Mana for anything unlisted.</summary>
        public static ClassPowerType OfClass(int playerCharacterDataId)
        {
            CombatGameplayRule rule = Rule;
            return rule != null ? rule.GetClassPower(playerCharacterDataId) : ClassPowerType.Mana;
        }

        /// <summary>
        /// The power of a character. Monsters, pets and summons are always <see cref="ClassPowerType.Mana"/>:
        /// only a player character's data id is a class.
        /// </summary>
        public static ClassPowerType Of(ICharacterData character)
        {
            if (!(character is IPlayerCharacterData))
                return ClassPowerType.Mana;
            return OfClass(character.DataId);
        }
    }
}
