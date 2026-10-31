using MultiplayerARPG;

namespace OpenMMORPG.Tests
{
    /// <summary>Player characters that the databases accept.</summary>
    public static class TestCharacters
    {
        /// <summary>
        /// A level 1 player on "TestMap". The channel, map and safe-area columns are NOT NULL in
        /// every database schema and a bare PlayerCharacterData leaves them null, so the insert
        /// would fail.
        /// </summary>
        public static PlayerCharacterData NewPlayer(string characterName = "Tester")
        {
            return new PlayerCharacterData()
            {
                Id = GenericUtils.GetUniqueId(),
                CharacterName = characterName,
                Level = 1,
                CurrentChannel = "default",
                CurrentMapName = "TestMap",
                RespawnMapName = "TestMap",
                CurrentSafeArea = string.Empty,
            };
        }
    }
}
