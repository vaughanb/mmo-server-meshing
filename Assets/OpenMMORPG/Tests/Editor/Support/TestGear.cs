using MultiplayerARPG;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// Equipment for stat tests, made in memory and registered in GameInstance.Items so a
    /// CharacterItem of it resolves. Needs a <see cref="TestGameInstance"/>. Each piece raises
    /// the given stats flat (the same at every item level) and may belong to an equipment set
    /// or wear out (a max durability above zero).
    /// </summary>
    public static class TestGear
    {
        public static T Equipment<T>(TestObjects objects, CharacterStats stats, EquipmentSet set = null, float maxDurability = 0f)
            where T : BaseEquipmentItem
        {
            T item = objects.Create<T>()
                .With("increaseStats", new CharacterStatsIncremental { baseStats = stats })
                .With("equipmentSet", set)
                .With("maxDurability", maxDurability);
            GameInstance.Items[item.DataId] = item;
            return item;
        }

        /// <summary>An equipment set whose bonuses are, in order, the two-piece, three-piece, ... stats.</summary>
        public static EquipmentSet Set(TestObjects objects, params CharacterStats[] bonuses)
        {
            EquipmentBonus[] effects = new EquipmentBonus[bonuses.Length];
            for (int i = 0; i < bonuses.Length; ++i)
                effects[i] = new EquipmentBonus().With("stats", bonuses[i]);
            EquipmentSet set = objects.Create<EquipmentSet>().With("effects", effects);
            GameInstance.EquipmentSets[set.DataId] = set;
            return set;
        }

        /// <summary>The character's total stats, as its stats cache would work them out.</summary>
        public static CharacterStats StatsOf(ICharacterData character, bool sumWithEquipments = true, bool sumWithBuffs = true, bool sumWithSkills = true)
        {
            CharacterStats result = default;
            character.GetAllStats(sumWithEquipments, sumWithBuffs, sumWithSkills, stats => result = stats);
            return result;
        }
    }
}
