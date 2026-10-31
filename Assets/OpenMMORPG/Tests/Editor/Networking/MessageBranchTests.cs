using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LiteNetLib.Utils;
using LiteNetLibManager;
using MultiplayerARPG;
using MultiplayerARPG.MMO;
using NUnit.Framework;
using UnityEngine;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// Messages whose layout depends on a type field, a flag or a null check: the writer and the
    /// reader must take the same branch, or every field after the branch is read from the
    /// wrong bytes. The generic round trip takes one branch per type; these take every branch,
    /// check that exactly the fields that branch sends arrive, that the others stay at their
    /// defaults on the receiver, and that every byte written is read.
    /// </summary>
    [Category(TestCategories.Logic)]
    public class MessageBranchTests : KitTestFixture
    {
        private static object ValueOf(MemberInfo member, object owner)
        {
            return member is FieldInfo field ? field.GetValue(owner) : ((PropertyInfo)member).GetValue(owner);
        }

        private static object DefaultOf(MemberInfo member)
        {
            Type type = member is FieldInfo field ? field.FieldType : ((PropertyInfo)member).PropertyType;
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }

        /// <summary>
        /// Fills a <typeparamref name="T"/>, sets <paramref name="discriminator"/> to
        /// <paramref name="branch"/>, round-trips it and checks that the members in
        /// <paramref name="sent"/> (comma separated; "member.sub" for part of a nested struct)
        /// arrive and every other member is left at its default.
        /// </summary>
        private static void AssertBranch<T>(string discriminator, object branch, string sent) where T : struct, INetSerializable
        {
            string[] listed = sent.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToArray();
            T message = (T)TestNetData.Fill(typeof(T), o =>
            {
                if (!(o is T))
                    return;
                if (typeof(T).GetField(discriminator) != null)
                    TestReflection.SetField(o, discriminator, branch);
                else
                    TestReflection.SetProperty(o, discriminator, branch);
            });

            T received = TestNetData.RoundTrip(message);

            foreach (MemberInfo member in TestNetData.Members(typeof(T)))
            {
                string name = member.Name;
                string[] subs = listed.Where(l => l.StartsWith(name + ".")).Select(l => l.Substring(name.Length + 1)).ToArray();
                if (name == discriminator || listed.Contains(name))
                {
                    Assert.That(TestNetData.Differences(ValueOf(member, message), ValueOf(member, received)), Is.Empty, $"{name} is sent for {branch}");
                }
                else if (subs.Length > 0)
                {
                    object expectedPart = ValueOf(member, message);
                    object receivedPart = ValueOf(member, received);
                    foreach (MemberInfo sub in TestNetData.Members(expectedPart.GetType()))
                    {
                        object expected = subs.Contains(sub.Name) ? ValueOf(sub, expectedPart) : DefaultOf(sub);
                        Assert.That(TestNetData.Differences(expected, ValueOf(sub, receivedPart)), Is.Empty,
                            $"{name}.{sub.Name} is {(subs.Contains(sub.Name) ? string.Empty : "not ")}sent for {branch}");
                    }
                }
                else
                {
                    Assert.That(TestNetData.Differences(DefaultOf(member), ValueOf(member, received)), Is.Empty, $"{name} is not sent for {branch}");
                }
            }
        }

        [TestCase(UpdateGuildMessage.UpdateType.Create, "guildName, characterId")]
        [TestCase(UpdateGuildMessage.UpdateType.ChangeLeader, "characterId")]
        [TestCase(UpdateGuildMessage.UpdateType.SetGuildMessage, "guildMessage")]
        [TestCase(UpdateGuildMessage.UpdateType.SetGuildMessage2, "guildMessage")]
        [TestCase(UpdateGuildMessage.UpdateType.SetGuildRole, "guildRole, guildRoleData")]
        [TestCase(UpdateGuildMessage.UpdateType.SetGuildMemberRole, "characterId, guildRole")]
        [TestCase(UpdateGuildMessage.UpdateType.SetSkillLevel, "dataId, level")]
        [TestCase(UpdateGuildMessage.UpdateType.LevelExpSkillPoint, "level, exp, skillPoint")]
        [TestCase(UpdateGuildMessage.UpdateType.Terminate, "")]
        [TestCase(UpdateGuildMessage.UpdateType.SetGold, "gold")]
        [TestCase(UpdateGuildMessage.UpdateType.SetScore, "score")]
        [TestCase(UpdateGuildMessage.UpdateType.SetOptions, "options")]
        [TestCase(UpdateGuildMessage.UpdateType.SetAutoAcceptRequests, "autoAcceptRequests")]
        [TestCase(UpdateGuildMessage.UpdateType.SetRank, "rank")]
        [TestCase(UpdateGuildMessage.UpdateType.UpdateStorage, "")]
        [TestCase(UpdateGuildMessage.UpdateType.Member, "")]
        public void AGuildUpdateSendsTheFieldsOfItsType(UpdateGuildMessage.UpdateType type, string sent)
        {
            AssertBranch<UpdateGuildMessage>("type", type, "id, " + sent);
        }

        [Test]
        public void EveryGuildUpdateTypeHasACase()
        {
            // A new update type with no case above could be written and read differently unnoticed.
            MethodInfo test = GetType().GetMethod(nameof(AGuildUpdateSendsTheFieldsOfItsType));
            IEnumerable<object> covered = test.GetCustomAttributes<TestCaseAttribute>().Select(c => c.Arguments[0]);
            Assert.That(covered, Is.EquivalentTo(Enum.GetValues(typeof(UpdateGuildMessage.UpdateType)).Cast<object>()));
        }

        [TestCase(UpdatePartyMessage.UpdateType.Create, "shareExp, shareItem, characterId")]
        [TestCase(UpdatePartyMessage.UpdateType.ChangeLeader, "characterId")]
        [TestCase(UpdatePartyMessage.UpdateType.Setting, "shareExp, shareItem")]
        [TestCase(UpdatePartyMessage.UpdateType.Terminate, "")]
        [TestCase(UpdatePartyMessage.UpdateType.Member, "")]
        public void APartyUpdateSendsTheFieldsOfItsType(UpdatePartyMessage.UpdateType type, string sent)
        {
            AssertBranch<UpdatePartyMessage>("type", type, "id, " + sent);
        }

        [TestCase(UpdateSocialMemberMessage.UpdateType.Add, "character")]
        [TestCase(UpdateSocialMemberMessage.UpdateType.Update, "character")]
        [TestCase(UpdateSocialMemberMessage.UpdateType.Remove, "character.id")]
        [TestCase(UpdateSocialMemberMessage.UpdateType.Clear, "")]
        public void ASocialMemberUpdateSendsTheFieldsOfItsType(UpdateSocialMemberMessage.UpdateType type, string sent)
        {
            AssertBranch<UpdateSocialMemberMessage>("type", type, "socialId, " + sent);
        }

        [TestCase(UpdateUserCharacterMessage.UpdateType.Online, "character")]
        [TestCase(UpdateUserCharacterMessage.UpdateType.Add, "character.id, character.userId, character.characterName, character.dataId, character.level, character.factionId, character.partyId, character.guildId, character.guildRole, character.iconDataId, character.frameDataId, character.backgroundDataId, character.titleDataId")]
        [TestCase(UpdateUserCharacterMessage.UpdateType.Remove, "character.id, character.userId")]
        public void AClusterCharacterUpdateSendsTheFieldsOfItsType(UpdateUserCharacterMessage.UpdateType type, string sent)
        {
            // Add leaves out HP and MP (SerializeWithoutHpMp); Online sends them.
            AssertBranch<UpdateUserCharacterMessage>("type", type, sent);
        }

        [Test]
        public void LocalChatWithNoGuildSendsNeitherGuildNameNorChannelId()
        {
            ChatMessage message = new ChatMessage { channel = ChatChannel.Local, message = "hello", senderName = "Bram", guildId = 0, guildName = "stale", channelId = 9, sendByServer = true, timestamp = 1700000000L };

            ChatMessage received = TestNetData.RoundTrip(message);

            Assert.That(received.message, Is.EqualTo("hello"));
            Assert.That(received.guildName, Is.Null, "guild name is sent only with a guild id");
            Assert.That(received.channelId, Is.Zero, "channel id is sent only for party and guild chat");
            Assert.That(received.sendByServer, Is.True, "the fields after the branches still line up");
            Assert.That(received.timestamp, Is.EqualTo(1700000000L));
        }

        [TestCase(ChatChannel.Party)]
        [TestCase(ChatChannel.Guild)]
        public void PartyAndGuildChatSendTheirChannelAndGuild(ChatChannel channel)
        {
            ChatMessage message = new ChatMessage { channel = channel, message = "regroup", guildId = 4, guildName = "Wardens", channelId = 12, timestamp = 1700000000L };

            ChatMessage received = TestNetData.RoundTrip(message);

            Assert.That(received.guildName, Is.EqualTo("Wardens"));
            Assert.That(received.channelId, Is.EqualTo(12));
            Assert.That(received.timestamp, Is.EqualTo(1700000000L));
        }

        [Test]
        public void AnAimWithNoTypeSendsOnlyTheType()
        {
            AssertBranch<AimPosition>("type", AimPositionType.None, "");
        }

        [Test]
        public void AnAimAtAPositionSendsThePositionButNoDirection()
        {
            AssertBranch<AimPosition>("type", AimPositionType.Position, "position");
        }

        [Test]
        public void AnAimInADirectionSendsTheDirectionToTheNearestHundredth()
        {
            // DirectionVector3 packs each axis into a signed byte of hundredths.
            Vector3 direction = new Vector3(0.6f, -0.48f, 0.64f);
            AimPosition aim = AimPosition.CreateDirection(new Vector3(10.5f, 2f, -3.25f), direction);

            AimPosition received = TestNetData.RoundTrip(aim);

            Assert.That(received.type, Is.EqualTo(AimPositionType.Direction));
            Assert.That(received.position, Is.EqualTo(new Vector3(10.5f, 2f, -3.25f)));
            Vector3 got = received.direction;
            Assert.That(Vector3.Distance(got, direction), Is.LessThan(0.02f), $"direction {got}");
        }

        [Test]
        public void AGuildListEntryWithNoOptionsSendsOnlyTheBasics()
        {
            AssertBranch<GuildListEntry>(nameof(GuildListEntry.FieldOptions), GuildListFieldOptions.None, "Id, GuildName, Level");
        }

        [Test]
        public void AGuildListEntrySendsOnlyTheOptionalFieldsItsOptionsAskFor()
        {
            AssertBranch<GuildListEntry>(nameof(GuildListEntry.FieldOptions), GuildListFieldOptions.Score | GuildListFieldOptions.MaxMembers, "Id, GuildName, Level, Score, MaxMembers");
        }

        [Test]
        public void AFailedFriendListResponseSendsNoList()
        {
            ResponseGetFriendsMessage failed = new ResponseGetFriendsMessage
            {
                message = UITextKeys.UI_ERROR_NOT_LOGGED_IN,
                friends = new List<SocialCharacterData> { new SocialCharacterData { id = "stale" } },
            };
            ResponseGetFriendsMessage succeeded = new ResponseGetFriendsMessage
            {
                message = UITextKeys.NONE,
                friends = new List<SocialCharacterData> { new SocialCharacterData { id = "friend-1", characterName = "Hilde", level = 12 } },
            };

            ResponseGetFriendsMessage failedReceived = TestNetData.RoundTrip(failed);
            ResponseGetFriendsMessage succeededReceived = TestNetData.RoundTrip(succeeded);

            Assert.That(failedReceived.message, Is.EqualTo(UITextKeys.UI_ERROR_NOT_LOGGED_IN));
            Assert.That(failedReceived.friends, Is.Null, "an error sends no list");
            Assert.That(succeededReceived.friends.Single().characterName, Is.EqualTo("Hilde"));
        }

        [Test]
        public void APartyResponseMaySayNoParty()
        {
            PartyData party = new PartyData(5, true, false, "leader-1");
            ResponseGetPartyMessage found = TestNetData.RoundTrip(new ResponseGetPartyMessage { party = party });
            ResponseGetPartyMessage missing = TestNetData.RoundTrip(new ResponseGetPartyMessage { party = null });

            Assert.That(found.party.id, Is.EqualTo(5));
            Assert.That(found.party.leaderId, Is.EqualTo("leader-1"));
            Assert.That(missing.party, Is.Null);
        }

        [Test]
        public void ASpawnRequestForAPlainMapSendsNoInstanceWarp()
        {
            AssertBranch<RequestSpawnMapMessage>(nameof(RequestSpawnMapMessage.instanceId), string.Empty, "channelId, mapName");
        }

        [Test]
        public void ASpawnRequestForAnInstanceSendsTheWarp()
        {
            AssertBranch<RequestSpawnMapMessage>(nameof(RequestSpawnMapMessage.instanceId), "dungeon-7", "channelId, mapName, instanceWarpPosition, instanceWarpOverrideRotation, instanceWarpRotation");
        }

        [Test]
        public void AHitWithNoDestinationReadsBackNoDestination()
        {
            HitRegisterData hit = new HitRegisterData { SimulateSeed = 5, Origin = new Vector3(1f, 2f, 3f), HitObjectId = 77, HitBoxIndex = 2, HitOrigin = new Vector3(4f, 5f, 6f), HitDestination = null };
            HitRegisterData aimed = hit;
            aimed.HitDestination = new Vector3(7f, 8f, 9f);

            HitRegisterData received = TestNetData.RoundTrip(hit);
            HitRegisterData aimedReceived = TestNetData.RoundTrip(aimed);

            Assert.That(received.HitDestination.HasValue, Is.False);
            Assert.That(received.HitBoxIndex, Is.EqualTo(2));
            Assert.That(aimedReceived.HitDestination, Is.EqualTo(new Vector3(7f, 8f, 9f)));
        }
    }
}
