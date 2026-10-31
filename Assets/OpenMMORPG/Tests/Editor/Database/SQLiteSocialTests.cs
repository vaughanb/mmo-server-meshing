#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
using System.Collections.Generic;
using System.Linq;
using Mono.Data.Sqlite;
using MultiplayerARPG;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// Guilds, parties and friends. Membership lives on the characters row (guildId, guildRole,
    /// partyId), so creating, joining and disbanding must keep the characters and the group in
    /// step: a disbanded guild that leaves its members' guildId set strands them in a guild that
    /// no longer exists. Guild roles, skills, gold and settings are read back on every server
    /// start.
    /// </summary>
    [Category(TestCategories.Harnessed)]
    public class SQLiteSocialTests : KitTestFixture
    {
        private static readonly GuildRoleData[] DefaultRoles =
        {
            new GuildRoleData() { roleName = "Master", canInvite = true, canKick = true, canUseStorage = true, shareExpPercentage = 0 },
            new GuildRoleData() { roleName = "Officer", canInvite = true, canKick = false, canUseStorage = true, shareExpPercentage = 0 },
            new GuildRoleData() { roleName = "Member", canInvite = false, canKick = false, canUseStorage = false, shareExpPercentage = 0 },
        };

        private TempSqliteDatabase _db;
        private string _userId;

        [SetUp]
        public void SetUp()
        {
            _db = TempSqliteDatabase.Create(Objects);
            _userId = _db.CreateUser();
        }

        private PlayerCharacterData NewCharacter(string name, int level = 1)
        {
            PlayerCharacterData character = TestCharacters.NewPlayer(name);
            character.Level = level;
            TestAsync.Wait(_db.Database.CreateCharacter(_userId, character));
            return character;
        }

        private PlayerCharacterData Load(PlayerCharacterData character)
        {
            return TestAsync.Wait(_db.Database.GetCharacter(character.Id));
        }

        private GuildData Guild(int id)
        {
            return TestAsync.Wait(_db.Database.GetGuild(id, DefaultRoles));
        }

        // ---- Guild ----

        [Test]
        public void CreatingAGuildMakesTheLeaderItsMember()
        {
            PlayerCharacterData leader = NewCharacter("Leader", 12);

            int id = TestAsync.Wait(_db.Database.CreateGuild("Wolves", leader.Id));

            Assert.That(id, Is.GreaterThan(0));
            GuildData guild = Guild(id);
            Assert.That(guild.guildName, Is.EqualTo("Wolves"));
            Assert.That(guild.leaderId, Is.EqualTo(leader.Id));
            Assert.That(guild.level, Is.EqualTo(1));
            Assert.That(guild.GetMember(leader.Id).characterName, Is.EqualTo("Leader"));
            Assert.That(guild.GetMember(leader.Id).level, Is.EqualTo(12));
            Assert.That(Load(leader).GuildId, Is.EqualTo(id));
        }

        [Test]
        public void GuildSettingsAreSaved()
        {
            PlayerCharacterData leader = NewCharacter("Leader");
            PlayerCharacterData heir = NewCharacter("Heir");
            int id = TestAsync.Wait(_db.Database.CreateGuild("Wolves", leader.Id));

            TestAsync.Wait(_db.Database.UpdateGuildLevel(id, 4, 350, 6));
            TestAsync.Wait(_db.Database.UpdateGuildMessage(id, "Raid at eight"));
            TestAsync.Wait(_db.Database.UpdateGuildMessage2(id, "Be nice"));
            TestAsync.Wait(_db.Database.UpdateGuildScore(id, 1200));
            TestAsync.Wait(_db.Database.UpdateGuildOptions(id, "{\"lang\":\"en\"}"));
            TestAsync.Wait(_db.Database.UpdateGuildAutoAcceptRequests(id, true));
            TestAsync.Wait(_db.Database.UpdateGuildRank(id, 3));
            TestAsync.Wait(_db.Database.UpdateGuildLeader(id, heir.Id));

            GuildData guild = Guild(id);
            Assert.That(guild.level, Is.EqualTo(4));
            Assert.That(guild.exp, Is.EqualTo(350));
            Assert.That(guild.skillPoint, Is.EqualTo(6));
            Assert.That(guild.guildMessage, Is.EqualTo("Raid at eight"));
            Assert.That(guild.guildMessage2, Is.EqualTo("Be nice"));
            Assert.That(guild.score, Is.EqualTo(1200));
            Assert.That(guild.options, Is.EqualTo("{\"lang\":\"en\"}"));
            Assert.That(guild.autoAcceptRequests, Is.True);
            Assert.That(guild.rank, Is.EqualTo(3));
            Assert.That(guild.leaderId, Is.EqualTo(heir.Id));
        }

        [Test]
        public void GuildGoldChangesByTheAmountGiven()
        {
            int id = TestAsync.Wait(_db.Database.CreateGuild("Wolves", NewCharacter("Leader").Id));

            Assert.That(TestAsync.Wait(_db.Database.ChangeGuildGold(id, 500)), Is.EqualTo(500));
            Assert.That(TestAsync.Wait(_db.Database.ChangeGuildGold(id, -120)), Is.EqualTo(380));
            Assert.That(TestAsync.Wait(_db.Database.GetGuildGold(id)), Is.EqualTo(380));
            Assert.That(Guild(id).gold, Is.EqualTo(380));
        }

        [Test]
        public void GuildRolesAndMemberRolesAreSaved()
        {
            PlayerCharacterData leader = NewCharacter("Leader");
            PlayerCharacterData member = NewCharacter("Member");
            int id = TestAsync.Wait(_db.Database.CreateGuild("Wolves", leader.Id));
            GuildRoleData officer = new GuildRoleData() { roleName = "Captain", canInvite = true, canKick = true, canUseStorage = false, shareExpPercentage = 15 };

            TestAsync.Wait(_db.Database.UpdateGuildRole(id, 1, officer));
            TestAsync.Wait(_db.Database.UpdateCharacterGuild(member.Id, id, 2));
            TestAsync.Wait(_db.Database.UpdateGuildMemberRole(member.Id, 1));

            GuildData guild = Guild(id);
            GuildRoleData role = guild.GetRole(1);
            Assert.That(role.roleName, Is.EqualTo("Captain"));
            Assert.That(role.canInvite, Is.True);
            Assert.That(role.canKick, Is.True);
            Assert.That(role.canUseStorage, Is.False);
            Assert.That(role.shareExpPercentage, Is.EqualTo(15));
            Assert.That(guild.GetRole(2).roleName, Is.EqualTo("Member"), "an unsaved role keeps its default");
            Assert.That(guild.IsMember(member.Id), Is.True);
            Assert.That(guild.GetMemberRole(member.Id), Is.EqualTo(1));
            Assert.That(Load(member).GuildRole, Is.EqualTo(1));
        }

        [Test]
        public void ARaisedGuildSkillReplacesItsOldLevel()
        {
            int id = TestAsync.Wait(_db.Database.CreateGuild("Wolves", NewCharacter("Leader").Id));

            TestAsync.Wait(_db.Database.UpdateGuildSkillLevel(id, 77, 1, 5));
            TestAsync.Wait(_db.Database.UpdateGuildSkillLevel(id, 77, 2, 4));
            TestAsync.Wait(_db.Database.UpdateGuildSkillLevel(id, 78, 1, 3));

            GuildData guild = Guild(id);
            Assert.That(guild.GetSkillLevel(77), Is.EqualTo(2));
            Assert.That(guild.GetSkillLevel(78), Is.EqualTo(1));
            Assert.That(guild.skillPoint, Is.EqualTo(3));
            Assert.That(_db.Database.ExecuteScalar("SELECT COUNT(*) FROM guildskill WHERE guildId=@id", new SqliteParameter("@id", id)), Is.EqualTo(2L));
        }

        [Test]
        public void DisbandingAGuildReleasesItsMembers()
        {
            PlayerCharacterData leader = NewCharacter("Leader");
            PlayerCharacterData member = NewCharacter("Member");
            int id = TestAsync.Wait(_db.Database.CreateGuild("Wolves", leader.Id));
            TestAsync.Wait(_db.Database.UpdateCharacterGuild(member.Id, id, 2));

            TestAsync.Wait(_db.Database.DeleteGuild(id));

            Assert.That(Guild(id), Is.Null);
            Assert.That(Load(leader).GuildId, Is.Zero);
            Assert.That(Load(member).GuildId, Is.Zero);
        }

        [Test]
        public void GuildNamesAreFound()
        {
            TestAsync.Wait(_db.Database.CreateGuild("Wolves", NewCharacter("Leader").Id));

            Assert.That(TestAsync.Wait(_db.Database.FindGuildName("Wolves")), Is.EqualTo(1));
            Assert.That(TestAsync.Wait(_db.Database.FindGuildName("Bears")), Is.Zero);
        }

        [Test]
        public void GuildRequestsAreCountedUntilDeleted()
        {
            int id = TestAsync.Wait(_db.Database.CreateGuild("Wolves", NewCharacter("Leader").Id));
            PlayerCharacterData applicant = NewCharacter("Applicant");

            TestAsync.Wait(_db.Database.CreateGuildRequest(id, applicant.Id));
            TestAsync.Wait(_db.Database.CreateGuildRequest(id, applicant.Id));
            Assert.That(TestAsync.Wait(_db.Database.GetGuildRequestsNotification(id)), Is.EqualTo(1), "a repeated request is one request");

            TestAsync.Wait(_db.Database.DeleteGuildRequest(id, applicant.Id));
            Assert.That(TestAsync.Wait(_db.Database.GetGuildRequestsNotification(id)), Is.Zero);
        }

        [Test]
        public void GuildRequestsListTheApplicants()
        {
            int id = TestAsync.Wait(_db.Database.CreateGuild("Wolves", NewCharacter("Leader").Id));
            PlayerCharacterData applicant = NewCharacter("Applicant", 7);
            TestAsync.Wait(_db.Database.CreateGuildRequest(id, applicant.Id));

            List<SocialCharacterData> requests = TestAsync.Wait(_db.Database.GetGuildRequests(id, 0, 10));

            Assert.That(requests.Select(r => r.id), Is.EqualTo(new[] { applicant.Id }));
            Assert.That(requests[0].level, Is.EqualTo(7));
        }

        [Test]
        public void GuildsAreFoundByPartOfTheirName()
        {
            TestAsync.Wait(_db.Database.CreateGuild("Grey Wolves", NewCharacter("Leader").Id));
            TestAsync.Wait(_db.Database.CreateGuild("Bears", NewCharacter("Other").Id));

            List<GuildListEntry> found = TestAsync.Wait(_db.Database.FindGuilds("finder", "Wolves", 0, 10));

            Assert.That(found.Select(g => g.GuildName), Is.EqualTo(new[] { "Grey Wolves" }));
        }

        [Test]
        public void TheGuildMemberCountIsSaved()
        {
            PlayerCharacterData leader = NewCharacter("Leader");
            int id = TestAsync.Wait(_db.Database.CreateGuild("Wolves", leader.Id));
            TestAsync.Wait(_db.Database.UpdateCharacterGuild(NewCharacter("Member").Id, id, 2));

            TestAsync.Wait(_db.Database.UpdateGuildMemberCount(id, 30));

            Assert.That(_db.Database.ExecuteScalar("SELECT currentMembers FROM guild WHERE id=@id", new SqliteParameter("@id", id)), Is.EqualTo(2L));
            Assert.That(_db.Database.ExecuteScalar("SELECT maxMembers FROM guild WHERE id=@id", new SqliteParameter("@id", id)), Is.EqualTo(30L));
        }

        // ---- Party ----

        [Test]
        public void CreatingAPartyMakesTheLeaderItsMember()
        {
            PlayerCharacterData leader = NewCharacter("Leader");

            int id = TestAsync.Wait(_db.Database.CreateParty(true, false, leader.Id));

            Assert.That(id, Is.GreaterThan(0));
            PartyData party = TestAsync.Wait(_db.Database.GetParty(id));
            Assert.That(party.shareExp, Is.True);
            Assert.That(party.shareItem, Is.False);
            Assert.That(party.leaderId, Is.EqualTo(leader.Id));
            Assert.That(party.GetMember(leader.Id).characterName, Is.EqualTo("Leader"));
            Assert.That(Load(leader).PartyId, Is.EqualTo(id));
        }

        [Test]
        public void PartyMembersSettingsAndLeaderAreSaved()
        {
            PlayerCharacterData leader = NewCharacter("Leader");
            PlayerCharacterData member = NewCharacter("Member");
            int id = TestAsync.Wait(_db.Database.CreateParty(true, false, leader.Id));

            TestAsync.Wait(_db.Database.UpdateCharacterParty(member.Id, id));
            TestAsync.Wait(_db.Database.UpdateParty(id, false, true));
            TestAsync.Wait(_db.Database.UpdatePartyLeader(id, member.Id));

            PartyData party = TestAsync.Wait(_db.Database.GetParty(id));
            Assert.That(party.GetMemberIds(), Is.EquivalentTo(new[] { leader.Id, member.Id }));
            Assert.That(party.shareExp, Is.False);
            Assert.That(party.shareItem, Is.True);
            Assert.That(party.leaderId, Is.EqualTo(member.Id));
        }

        [Test]
        public void DisbandingAPartyReleasesItsMembers()
        {
            PlayerCharacterData leader = NewCharacter("Leader");
            PlayerCharacterData member = NewCharacter("Member");
            int id = TestAsync.Wait(_db.Database.CreateParty(true, true, leader.Id));
            TestAsync.Wait(_db.Database.UpdateCharacterParty(member.Id, id));

            TestAsync.Wait(_db.Database.DeleteParty(id));

            Assert.That(TestAsync.Wait(_db.Database.GetParty(id)), Is.Null);
            Assert.That(Load(leader).PartyId, Is.Zero);
            Assert.That(Load(member).PartyId, Is.Zero);
        }

        // ---- Friends ----

        private const byte Friend = 0;
        private const byte Request = 1;

        [Test]
        public void AFriendRequestShowsOnBothSides()
        {
            PlayerCharacterData asker = NewCharacter("Asker");
            PlayerCharacterData asked = NewCharacter("Asked", 9);

            TestAsync.Wait(_db.Database.CreateFriend(asker.Id, asked.Id, Request));

            List<SocialCharacterData> sent = TestAsync.Wait(_db.Database.GetFriends(asker.Id, false, Request, 0, 10));
            List<SocialCharacterData> received = TestAsync.Wait(_db.Database.GetFriends(asked.Id, true, Request, 0, 10));
            Assert.That(sent.Select(f => f.characterName), Is.EqualTo(new[] { "Asked" }));
            Assert.That(sent[0].level, Is.EqualTo(9));
            Assert.That(received.Select(f => f.id), Is.EqualTo(new[] { asker.Id }));
            Assert.That(TestAsync.Wait(_db.Database.GetFriendRequestNotification(asked.Id)), Is.EqualTo(1));
            Assert.That(TestAsync.Wait(_db.Database.GetFriends(asker.Id, false, Friend, 0, 10)), Is.Empty, "not a friend yet");
        }

        [Test]
        public void AcceptingAndRemovingAFriendIsSaved()
        {
            PlayerCharacterData a = NewCharacter("Alpha");
            PlayerCharacterData b = NewCharacter("Bravo");
            TestAsync.Wait(_db.Database.CreateFriend(a.Id, b.Id, Request));

            TestAsync.Wait(_db.Database.CreateFriend(a.Id, b.Id, Friend));
            Assert.That(TestAsync.Wait(_db.Database.GetFriends(a.Id, false, Friend, 0, 10)).Select(f => f.id), Is.EqualTo(new[] { b.Id }));
            Assert.That(TestAsync.Wait(_db.Database.GetFriendRequestNotification(b.Id)), Is.Zero, "the request became a friendship");

            TestAsync.Wait(_db.Database.DeleteFriend(a.Id, b.Id));
            Assert.That(TestAsync.Wait(_db.Database.GetFriends(a.Id, false, Friend, 0, 10)), Is.Empty);
        }

        [Test]
        public void FindingCharactersSkipsTheFinderAndTheirFriends()
        {
            PlayerCharacterData finder = NewCharacter("Rowan");
            PlayerCharacterData friend = NewCharacter("Rowena");
            NewCharacter("Rowland");
            NewCharacter("Alder");
            TestAsync.Wait(_db.Database.CreateFriend(finder.Id, friend.Id, Friend));

            List<SocialCharacterData> found = TestAsync.Wait(_db.Database.FindCharacters(finder.Id, "Row", 0, 10));

            Assert.That(found.Select(c => c.characterName), Is.EquivalentTo(new[] { "Rowland" }));
        }
    }
}
#endif
