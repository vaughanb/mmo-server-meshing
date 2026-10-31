using System;
using System.Collections.Generic;
using MultiplayerARPG;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// Stands in for GameInstance.ServerGameMessageHandlers, which only a network manager sets.
    /// The kit's quest, crafting and building actions on a character entity send the player a
    /// game message when they refuse and a reward notice when they give something; with no
    /// handler they throw part-way through, and the kit's own handler throws without a running
    /// network manager. This one sends nothing and records the messages and rewards, so a test
    /// can check what the player would have been told. <see cref="Use"/> installs it until the
    /// test ends.
    /// </summary>
    public sealed class TestGameMessages : IServerGameMessageHandlers
    {
        /// <summary>The game messages sent, in order.</summary>
        public List<UITextKeys> Messages { get; } = new List<UITextKeys>();

        /// <summary>The reward-item notices sent, as (item data id, amount), in order.</summary>
        public List<(int dataId, int amount)> RewardedItems { get; } = new List<(int dataId, int amount)>();

        public static TestGameMessages Use(TestObjects objects)
        {
            IServerGameMessageHandlers previous = GameInstance.ServerGameMessageHandlers;
            TestGameMessages messages = new TestGameMessages();
            GameInstance.ServerGameMessageHandlers = messages;
            objects.Own(new Restore(() => GameInstance.ServerGameMessageHandlers = previous));
            return messages;
        }

        public void SendGameMessage(long connectionId, UITextKeys message) => Messages.Add(message);
        public void SendFormattedGameMessage(long connectionId, UIFormatKeys format, params string[] args) { }
        public void NotifyRewardExp(long connectionId, RewardGivenType givenType, int exp) { }
        public void NotifyRewardGold(long connectionId, RewardGivenType givenType, int gold) { }
        public void NotifyRewardItem(long connectionId, RewardGivenType givenType, int dataId, int amount) => RewardedItems.Add((dataId, amount));
        public void NotifyRewardCurrency(long connectionId, RewardGivenType givenType, int dataId, int amount) { }
        public void NotifyStorageItems(long connectionId, StorageType storageType, string storageOwnerId, List<CharacterItem> storageItems) { }
        public void NotifyStorageOpened(long connectionId, StorageType storageType, string storageOwnerId, uint objectId, int weightLimit, int slotLimit) { }
        public void NotifyStorageClosed(long connectionId, StorageType storageType, string storageOwnerId) { }
        public void SendSetPartyData(long connectionId, int id, bool shareExp, bool shareItem, string leaderId) { }
        public void SendSetPartyLeader(long connectionId, int id, string leaderId) { }
        public void SendSetPartySetting(long connectionId, int id, bool shareExp, bool shareItem) { }
        public void SendClearPartyData(long connectionId, int id) { }
        public void SendAddPartyMember(long connectionId, int id, SocialCharacterData member) { }
        public void SendUpdatePartyMember(long connectionId, int id, SocialCharacterData member) { }
        public void SendRemovePartyMember(long connectionId, int id, string characterId) { }
        public void SendNotifyPartyInvitation(long connectionId, PartyInvitationData invitation) { }
        public void SendSetGuildData(long connectionId, int id, string guildName, string leaderId) { }
        public void SendSetGuildLeader(long connectionId, int id, string leaderId) { }
        public void SendSetGuildMessage(long connectionId, int id, string message) { }
        public void SendSetGuildMessage2(long connectionId, int id, string message) { }
        public void SendSetGuildOptions(long connectionId, int id, string options) { }
        public void SendSetGuildAutoAcceptRequests(long connectionId, int id, bool autoAcceptRequests) { }
        public void SendSetGuildScore(long connectionId, int id, int score) { }
        public void SendSetGuildRank(long connectionId, int id, int rank) { }
        public void SendSetGuildRole(long connectionId, int id, byte guildRole, GuildRoleData guildRoleData) { }
        public void SendSetGuildMemberRole(long connectionId, int id, string characterId, byte guildRole) { }
        public void SendClearGuildData(long connectionId, int id) { }
        public void SendAddGuildMember(long connectionId, int id, SocialCharacterData member) { }
        public void SendUpdateGuildMember(long connectionId, int id, SocialCharacterData member) { }
        public void SendRemoveGuildMember(long connectionId, int id, string characterId) { }
        public void SendSetGuildSkillLevel(long connectionId, int id, int dataId, int level) { }
        public void SendSetGuildGold(long connectionId, int id, int gold) { }
        public void SendSetGuildLevelExpSkillPoint(long connectionId, int id, int level, int exp, int skillPoint) { }
        public void SendNotifyGuildInvitation(long connectionId, GuildInvitationData invitation) { }

        private sealed class Restore : IDisposable
        {
            private readonly Action _restore;

            public Restore(Action restore)
            {
                _restore = restore;
            }

            public void Dispose()
            {
                _restore();
            }
        }
    }
}
