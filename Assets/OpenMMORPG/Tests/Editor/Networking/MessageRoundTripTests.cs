using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LiteNetLib.Utils;
using MultiplayerARPG;
using MultiplayerARPG.MMO;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// Every INetSerializable type in the kit, filled with distinct values, sent through
    /// LiteNetLib and read back. A field written but not read, read out of order or behind a
    /// different #if or flag silently corrupts everything after it in the packet, so each type
    /// must come back member for member, use up exactly the bytes it wrote, and write the same
    /// bytes again when the copy is sent on.
    ///
    /// Members that are not meant to survive (not sent, sent only on another branch) are listed
    /// with the reason, and so are kit bugs; nothing is skipped silently. A new type that cannot
    /// be built generically fails <see cref="EveryKitTypeIsCoveredOrListed"/> until it is listed,
    /// and a listed member that no longer exists fails <see cref="TheTablesNameRealMembers"/>.
    /// The branches the filler does not take are tested by hand in MessageBranchTests.
    /// </summary>
    [Category(TestCategories.Logic)]
    public class MessageRoundTripTests : KitTestFixture
    {
        private const string BranchOnly = "written only for some values of the message's type field; every branch is in MessageBranchTests";
        private const string CharacterSelectList = "the character-select list leaves it out (ResponseCharactersMessage passes withTransforms: false and the like to SerializeCharacterData)";

        /// <summary>Open generic types, tested through one closed type.</summary>
        private static readonly Dictionary<Type, Type> GenericStandIns = new Dictionary<Type, Type>
        {
            [typeof(DbRequestMessage<>)] = typeof(DbRequestMessage<UpdateCharacterReq>),
        };

        /// <summary>Types that cannot be built generically.</summary>
        private static readonly Dictionary<Type, string> NotBuiltGenerically = new Dictionary<Type, string>
        {
            [typeof(MapInfo)] = "a ScriptableObject game-data asset, not a message; it cannot be created with a constructor",
        };

        /// <summary>
        /// Branch choices: the filler sets every enum to its first non-default value, which for
        /// these types writes only part of the message. Each is put on the branch that writes
        /// the most (at any depth); the other branches are in MessageBranchTests.
        /// </summary>
        private static readonly Dictionary<Type, Action<object>> Branches = new Dictionary<Type, Action<object>>
        {
            // Position leaves the direction off the wire.
            [typeof(AimPosition)] = o => TestReflection.SetField(o, "type", AimPositionType.Direction),
            // channelId is written only for party and guild chat.
            [typeof(ChatMessage)] = o => TestReflection.SetField(o, "channel", ChatChannel.Guild),
            // ChangeLeader leaves the share settings off.
            [typeof(UpdatePartyMessage)] = o => TestReflection.SetField(o, "type", UpdatePartyMessage.UpdateType.Create),
            // Remove sends only the ids; Online sends the whole character.
            [typeof(UpdateUserCharacterMessage)] = o => TestReflection.SetField(o, "type", UpdateUserCharacterMessage.UpdateType.Online),
        };

        /// <summary>
        /// Members that legitimately do not come back. The type is the member's owner (or a base
        /// of it), or the message it travels in, so a rule can apply to one message only.
        /// </summary>
        private static readonly MemberTable NotRoundTripped = new MemberTable
        {
            { typeof(PlayerCharacterData), Wire.NotWritten, "the guild's skills, not the character's: never saved with the character or sent with it; the map server takes them from the guild", "GuildSkills" },
            { typeof(CraftingQueueItem), Wire.NotWritten, "server only: the workbench the craft was started at, checked by the server's queue update; clients never use it", "sourceObjectId" },
            { typeof(CentralServerPeerInfo), Wire.NotWritten, "the receiving server's own connection id for the peer, set when it registers; not meaningful on the other side", "connectionId" },
            { typeof(CentralUserPeerInfo), Wire.NotWritten, "the receiving server's own connection id for the peer, set when it registers; not meaningful on the other side", "connectionId" },
            { typeof(Mail), Wire.Altered, "sent as text (CharacterDataExtensions.WriteItems), which drops items missing from the game data and gives each item a new id; with the item registered they survive, see MailSerializationTests", "Items" },
            { typeof(UpdateGuildMessage), Wire.NotWritten, BranchOnly, "guildName", "guildMessage", "guildRole", "guildRoleData", "level", "exp", "skillPoint", "gold", "score", "options", "autoAcceptRequests", "rank", "dataId" },
            { typeof(ResponseCharactersMessage), Wire.NotWritten, CharacterSelectList, "CurrentPosition", "CurrentRotation", "RespawnMapName", "RespawnPosition", "Buffs", "SkillUsages", "NonEquipItems", "Summons", "Hotkeys", "Quests", "ServerBools", "ServerInts", "ServerFloats", "PrivateBools", "PrivateInts", "PrivateFloats" },
        };

        /// <summary>
        /// Members lost to a kit bug. Left out of the comparison so the rest of each message is
        /// still checked; each has an ignored hand-written test that asserts the right behaviour.
        /// </summary>
        private static readonly MemberTable KnownBugs = new MemberTable
        {
        };

        /// <summary>
        /// Floats packed or quantized on the wire, compared within a tolerance. None so far: the
        /// kit quantizes when a value is converted into a packed type (DirectionVector3 keeps
        /// hundredths, see MessageBranchTests), and the packed fields themselves travel exactly.
        /// </summary>
        private static readonly Dictionary<(Type type, string member), float> Quantized = new Dictionary<(Type, string), float>
        {
        };

        /// <summary>Whether a listed member is on the wire at all.</summary>
        private enum Wire
        {
            /// <summary>Not written: the bytes of a re-sent copy are still compared.</summary>
            NotWritten,
            /// <summary>Written but read back changed: a re-sent copy may write different bytes.</summary>
            Altered,
        }

        /// <summary>(type, member) pairs, each group with one reason, written as a collection initializer.</summary>
        private sealed class MemberTable : IEnumerable<KeyValuePair<(Type type, string member), string>>
        {
            private readonly Dictionary<(Type, string), string> _reasons = new Dictionary<(Type, string), string>();
            private readonly HashSet<(Type, string)> _altered = new HashSet<(Type, string)>();

            public void Add(Type type, Wire wire, string reason, params string[] members)
            {
                foreach (string member in members)
                {
                    _reasons.Add((type, member), reason);
                    if (wire == Wire.Altered)
                        _altered.Add((type, member));
                }
            }

            public bool Contains(Type type, string member, bool notWrittenOnly = false)
            {
                return _reasons.ContainsKey((type, member)) && !(notWrittenOnly && _altered.Contains((type, member)));
            }

            public IEnumerator<KeyValuePair<(Type type, string member), string>> GetEnumerator() => _reasons.GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        private static bool IsKitNetSerializable(Type type)
        {
            string ns = type.Namespace ?? string.Empty;
            return typeof(INetSerializable).IsAssignableFrom(type) && !type.IsInterface && !type.IsAbstract
                && (ns.StartsWith("MultiplayerARPG") || ns.StartsWith("OpenMMORPG"));
        }

        /// <summary>The kit's own INetSerializable types: Assembly-CSharp, MultiplayerARPG and OpenMMORPG namespaces.</summary>
        private static IEnumerable<Type> KitTypes()
        {
            return typeof(PlayerCharacterData).Assembly.GetTypes().Where(IsKitNetSerializable).OrderBy(t => t.FullName);
        }

        private static string CaseName(Type type)
        {
            string name = type.IsGenericType
                ? type.Name.Substring(0, type.Name.IndexOf('`')) + "<" + string.Join(",", type.GetGenericArguments().Select(a => a.Name)) + ">"
                : type.Name;
            return (type.Namespace == "MultiplayerARPG.MMO" ? "MMO." : string.Empty) + name;
        }

        public static IEnumerable<TestCaseData> Types()
        {
            foreach (Type kitType in KitTypes())
            {
                if (NotBuiltGenerically.ContainsKey(kitType))
                    continue;
                Type type = kitType;
                if (type.ContainsGenericParameters && !GenericStandIns.TryGetValue(type, out type))
                    continue;
                yield return new TestCaseData(type).SetName($"RoundTrips({CaseName(type)})");
            }
        }

        private static bool Matches(MemberTable table, Type message, Type owner, string member, bool notWrittenOnly)
        {
            if (table.Contains(message, member, notWrittenOnly))
                return true;
            for (Type t = owner; t != null; t = t.BaseType)
            {
                if (table.Contains(t.IsGenericType ? t.GetGenericTypeDefinition() : t, member, notWrittenOnly))
                    return true;
            }
            return false;
        }

        private static TestNetData.MemberRule Rule(Type message, Type owner, string member, bool notWrittenOnly = false)
        {
            if (Matches(NotRoundTripped, message, owner, member, notWrittenOnly) || Matches(KnownBugs, message, owner, member, notWrittenOnly))
                return TestNetData.MemberRule.Ignore;
            if (Quantized.TryGetValue((owner, member), out float tolerance))
                return TestNetData.MemberRule.Within(tolerance);
            return TestNetData.MemberRule.Exact;
        }

        private static void TakeBranch(object built)
        {
            if (Branches.TryGetValue(built.GetType(), out Action<object> choose))
                choose(built);
        }

        [TestCaseSource(nameof(Types))]
        public void RoundTrips(Type type)
        {
            object sent = TestNetData.Fill(type, TakeBranch);
            byte[] bytes = TestNetData.Write((INetSerializable)sent);

            object received = TestNetData.New(type);
            NetDataReader reader = new NetDataReader(bytes);
            ((INetSerializable)received).Deserialize(reader);

            Assert.That(reader.AvailableBytes, Is.EqualTo(0), $"bytes left unread out of {bytes.Length}");
            List<string> differences = TestNetData.Differences(sent, received, (owner, member) => Rule(type, owner, member));
            Assert.That(differences, Is.Empty, "members that did not survive the round trip");
            // A copy equal to the original in every member that is on the wire must also write the
            // same bytes; this catches what the member comparison cannot see (private fields).
            // When a listed member is written but read back changed, the bytes may differ too.
            if (TestNetData.Differences(sent, received, (owner, member) => Rule(type, owner, member, notWrittenOnly: true)).Count == 0)
            {
                Assert.That(TestNetData.Write((INetSerializable)received), Is.EqualTo(bytes),
                    "sending the received copy on writes different bytes");
            }
        }

        [Test]
        public void EveryKitTypeIsCoveredOrListed()
        {
            List<string> problems = new List<string>();
            List<Type> kitTypes = KitTypes().ToList();
            foreach (Type type in kitTypes)
            {
                if (NotBuiltGenerically.ContainsKey(type))
                    continue;
                if (type.ContainsGenericParameters)
                {
                    if (!GenericStandIns.ContainsKey(type))
                        problems.Add($"{type.FullName}: generic; add a closed stand-in to GenericStandIns");
                    continue;
                }
                if (typeof(UnityEngine.Object).IsAssignableFrom(type))
                    problems.Add($"{type.FullName}: a Unity object; list it in NotBuiltGenerically");
                else if (!type.IsValueType && type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null) == null)
                    problems.Add($"{type.FullName}: no parameterless constructor; list it in NotBuiltGenerically and test it by hand");
            }

            foreach (Type listed in NotBuiltGenerically.Keys.Concat(GenericStandIns.Keys).Concat(Branches.Keys))
            {
                if (!kitTypes.Contains(listed))
                    problems.Add($"{listed.FullName}: listed but no longer a kit INetSerializable type; remove it");
            }

            Assert.That(problems, Is.Empty);
            // Discovery that finds nothing would pass every case vacuously.
            Assert.That(kitTypes.Count, Is.GreaterThan(300), "kit INetSerializable types found");
        }

        [Test]
        public void TheTablesNameRealMembers()
        {
            List<string> stale = new List<string>();
            IEnumerable<(Type type, string member)> listed = NotRoundTripped.Select(p => p.Key)
                .Concat(KnownBugs.Select(p => p.Key))
                .Concat(Quantized.Keys);
            foreach ((Type type, string member) in listed)
            {
                if (!HasMember(type, member) && !(type == typeof(ResponseCharactersMessage) && HasMember(typeof(PlayerCharacterData), member)))
                    stale.Add($"{type.Name}.{member}");
            }
            Assert.That(stale, Is.Empty, "listed members that no longer exist");
        }

        private static bool HasMember(Type type, string member)
        {
            return type.GetMember(member, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Length > 0;
        }
    }
}
