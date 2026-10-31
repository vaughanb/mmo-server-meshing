using System.Collections.Generic;
using LiteNetLib.Utils;
using MultiplayerARPG;
using MultiplayerARPG.MMO;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// A player character in its network form. In MMO mode this is how a character travels
    /// between the map server and the database server on every load and save, and to the
    /// client on the character-select screen, so a field lost here is lost from the game. The
    /// generic round trip fills each list with two plain elements; these build a character the
    /// way play does (socketed gear, active buffs, quests part done, hotkeys, currencies, custom
    /// data) and check the counted lists element by element.
    /// </summary>
    [Category(TestCategories.Logic)]
    public class CharacterDataSerializationTests : KitTestFixture
    {
        private static CharacterItem Sword()
        {
            return new CharacterItem
            {
                id = "sword-1",
                dataId = 501,
                level = 7,
                amount = 1,
                equipSlotIndex = 2,
                durability = 87.5f,
                exp = 340,
                lockRemainsDuration = 12.5f,
                expireTime = 1893456000000L,
                randomSeed = 99,
                ammoDataId = 77,
                ammo = 18,
                sockets = new CharacterItemSockets { socket1 = 9001, socket3 = 9003, socket8 = 9008 },
                version = 3,
            };
        }

        /// <summary>A character with every list holding entries, as a long play session leaves it.</summary>
        private static PlayerCharacterData Veteran()
        {
            PlayerCharacterData character = TestCharacters.NewPlayer("Veteran");
            character.UserId = "user-7";
            character.DataId = 11;
            character.EntityId = 12;
            character.FactionId = 3;
            character.Level = 42;
            character.Exp = 123456;
            character.CurrentHp = 900;
            character.CurrentMp = 300;
            character.CurrentStamina = 80;
            character.CurrentFood = 60;
            character.CurrentWater = 40;
            character.StatPoint = 4.5f;
            character.SkillPoint = 2f;
            character.Gold = 98765;
            character.UserGold = 15;
            character.UserCash = 25;
            character.PartyId = 5;
            character.GuildId = 6;
            character.GuildRole = 2;
            character.CurrentPosition = new Vec3(101.25f, 4.5f, -33.75f);
            character.CurrentRotation = new Vec3(0f, 270f, 0f);
            character.CurrentSafeArea = "Town";
            character.RespawnMapName = "Island";
            character.RespawnPosition = new Vec3(1f, 2f, 3f);
            character.IconDataId = 21;
            character.FrameDataId = 22;
            character.TitleDataId = 23;
            character.LastDeadTime = 1700000000L;
            character.UnmuteTime = 1700000500L;
            character.LastUpdate = 1700001000L;
            character.IsPkOn = true;
            character.LastPkOnTime = 1700000100L;
            character.PkPoint = 31;
            character.ConsecutivePkKills = 4;
            character.HighestPkPoint = 55;
            character.HighestConsecutivePkKills = 9;
            character.Reputation = -12;

            character.Attributes.Add(new CharacterAttribute { dataId = 1001, amount = 15 });
            character.Attributes.Add(new CharacterAttribute { dataId = 1002, amount = 8 });
            character.Skills.Add(new CharacterSkill { dataId = 2001, level = 5 });
            character.SkillUsages.Add(new CharacterSkillUsage { type = SkillUsageType.Skill, dataId = 2001, coolDownRemainsDuration = 3.25f });
            character.Buffs.Add(new CharacterBuff { id = "buff-1", type = BuffType.SkillBuff, dataId = 2001, level = 5, buffRemainsDuration = 17.5f });
            character.Buffs.Add(new CharacterBuff { id = "buff-2", type = BuffType.PotionBuff, dataId = 3001, level = 1, buffRemainsDuration = 60f });
            character.Summons.Add(new CharacterSummon { id = "pet-1", type = SummonType.PetItem, dataId = 4001, summonRemainsDuration = 0f });
            character.EquipItems.Add(Sword());
            CharacterItem potions = new CharacterItem { id = "potion-1", dataId = 601, level = 1, amount = 20 };
            character.NonEquipItems.Add(potions);
            character.NonEquipItems.Add(new CharacterItem { id = "empty-slot" });
            character.Hotkeys.Add(new CharacterHotkey { hotkeyId = "HK1", type = HotkeyType.Skill, relateId = "2001" });
            character.Hotkeys.Add(new CharacterHotkey { hotkeyId = "HK2", type = HotkeyType.Item, relateId = "potion-1" });
            character.Quests.Add(new CharacterQuest
            {
                dataId = 5001,
                randomTasksIndex = 1,
                isTracking = true,
                killedMonsters = new Dictionary<int, int> { [6001] = 3, [6002] = 12 },
                completedTasks = new List<int> { 0, 2 },
            });
            character.Quests.Add(new CharacterQuest
            {
                dataId = 5002,
                isComplete = true,
                completeTime = 1700000200L,
                killedMonsters = new Dictionary<int, int>(),
                completedTasks = new List<int>(),
            });
            character.Currencies.Add(new CharacterCurrency { dataId = 7001, amount = 450 });
            character.ServerBools.Add(new CharacterDataBoolean { hashedKey = 1, value = true });
            character.ServerInts.Add(new CharacterDataInt32 { hashedKey = 2, value = 222 });
            character.ServerFloats.Add(new CharacterDataFloat32 { hashedKey = 3, value = 3.5f });
            character.PrivateBools.Add(new CharacterDataBoolean { hashedKey = 4, value = true });
            character.PrivateInts.Add(new CharacterDataInt32 { hashedKey = 5, value = 555 });
            character.PrivateFloats.Add(new CharacterDataFloat32 { hashedKey = 6, value = 6.5f });
            character.PublicBools.Add(new CharacterDataBoolean { hashedKey = 7, value = true });
            character.PublicInts.Add(new CharacterDataInt32 { hashedKey = 8, value = 888 });
            character.PublicFloats.Add(new CharacterDataFloat32 { hashedKey = 9, value = 9.5f });
            character.SelectableWeaponSets.Add(new EquipWeapons { rightHand = Sword() });
            character.SelectableWeaponSets.Add(new EquipWeapons());
            character.EquipWeaponSet = 0;
            character.Mount = new CharacterMount { type = MountType.MountItem, sourceId = "horse-1", mountRemainsDuration = 30f, level = 2, currentHp = 400 };
            return character;
        }

        /// <summary>Everything but what is known not to travel (see MessageRoundTripTests).</summary>
        private static TestNetData.MemberRule NotTheGuildSkills(System.Type owner, string member)
        {
            return member == nameof(PlayerCharacterData.GuildSkills)
                ? TestNetData.MemberRule.Ignore
                : TestNetData.MemberRule.Exact;
        }

        [Test]
        public void AVeteranWithEveryListFilledSurvivesTheNetworkForm()
        {
            PlayerCharacterData veteran = Veteran();

            PlayerCharacterData received = TestNetData.RoundTrip(veteran);

            Assert.That(TestNetData.Differences(veteran, received, NotTheGuildSkills), Is.Empty);
            // The lists the generic round trip only fills with two plain elements.
            CharacterItem sword = received.EquipItems[0];
            Assert.That(new[] { sword.sockets.socket1, sword.sockets.socket2, sword.sockets.socket3, sword.sockets.socket8 }, Is.EqualTo(new[] { 9001, 0, 9003, 9008 }), "sockets, gaps included");
            Assert.That(received.NonEquipItems[1].amount, Is.Zero, "an empty bag slot stays empty");
            Assert.That(received.NonEquipItems[1].id, Is.EqualTo("empty-slot"), "an empty slot keeps its id");
            Assert.That(received.Quests[0].killedMonsters, Is.EquivalentTo(new Dictionary<int, int> { [6001] = 3, [6002] = 12 }));
            Assert.That(received.Quests[0].completedTasks, Is.EqualTo(new[] { 0, 2 }));
            Assert.That(received.Quests[1].isComplete, "the finished quest");
            Assert.That(received.Hotkeys[1].relateId, Is.EqualTo("potion-1"));
            Assert.That(received.SelectableWeaponSets[0].rightHand.sockets.socket3, Is.EqualTo(9003), "the equipped weapon's sockets");
            Assert.That(received.Mount.sourceId, Is.EqualTo("horse-1"));
        }

        [Test]
        public void ThePlayerBackgroundSurvivesTheNetworkForm()
        {
            PlayerCharacterData veteran = Veteran();
            veteran.BackgroundDataId = 24;

            PlayerCharacterData received = TestNetData.RoundTrip(veteran);

            Assert.That(received.BackgroundDataId, Is.EqualTo(24));
        }

        [TestCase(TransactionUpdateCharacterState.None)]
        [TestCase(TransactionUpdateCharacterState.Character)]
        [TestCase(TransactionUpdateCharacterState.Items | TransactionUpdateCharacterState.Quests)]
        [TestCase(TransactionUpdateCharacterState.All)]
        public void AnUpdateRequestCarriesTheWholeCharacterWhateverItsStateFlags(TransactionUpdateCharacterState state)
        {
            // The flags tell the database what to write; they gate nothing on the wire, so a
            // partial save still sends (and the database still receives) the whole character.
            UpdateCharacterReq request = new UpdateCharacterReq
            {
                State = state,
                CharacterData = Veteran(),
                SummonBuffs = new List<CharacterBuff> { new CharacterBuff { id = "summon-buff", type = BuffType.SkillBuff, dataId = 2002, level = 3, buffRemainsDuration = 5f } },
                DeleteStorageReservation = true,
            };

            UpdateCharacterReq received = TestNetData.RoundTrip(request);

            Assert.That(received.State, Is.EqualTo(state));
            Assert.That(received.DeleteStorageReservation, Is.True);
            Assert.That(received.SummonBuffs, Has.Count.EqualTo(1));
            Assert.That(received.SummonBuffs[0].id, Is.EqualTo("summon-buff"));
            Assert.That(TestNetData.Differences(request.CharacterData, received.CharacterData, NotTheGuildSkills), Is.Empty);
        }

        [Test]
        public void AnUpdateRequestWithNoSummonBuffsReadsBackAnEmptyList()
        {
            UpdateCharacterReq request = new UpdateCharacterReq { State = TransactionUpdateCharacterState.All, CharacterData = Veteran(), SummonBuffs = null };

            UpdateCharacterReq received = TestNetData.RoundTrip(request);

            Assert.That(received.SummonBuffs, Is.Not.Null.And.Empty);
        }

        [Test]
        public void TheCharacterSelectListSendsWhatThePreviewNeedsAndNoMore()
        {
            PlayerCharacterData veteran = Veteran();
            ResponseCharactersMessage message = new ResponseCharactersMessage
            {
                message = UITextKeys.NONE,
                characters = new List<PlayerCharacterData> { veteran, Veteran() },
            };

            ResponseCharactersMessage received = TestNetData.RoundTrip(message);

            Assert.That(received.characters, Has.Count.EqualTo(2));
            PlayerCharacterData shown = received.characters[0];
            // What the select screen draws: name, level, class, worn gear and its looks.
            Assert.That(shown.CharacterName, Is.EqualTo("Veteran"));
            Assert.That(shown.Level, Is.EqualTo(42));
            Assert.That(shown.EquipItems[0].sockets.socket8, Is.EqualTo(9008));
            Assert.That(shown.SelectableWeaponSets[0].rightHand.id, Is.EqualTo("sword-1"));
            Assert.That(shown.Attributes, Has.Count.EqualTo(2));
            Assert.That(shown.Skills, Has.Count.EqualTo(1));
            Assert.That(shown.PublicInts[0].value, Is.EqualTo(888), "public custom data (appearance) is sent");
            Assert.That(shown.Mount.sourceId, Is.EqualTo("horse-1"));
            // What it leaves out.
            Assert.That(shown.NonEquipItems, Is.Empty, "bag");
            Assert.That(shown.Quests, Is.Empty, "quests");
            Assert.That(shown.Buffs, Is.Empty, "buffs");
            Assert.That(shown.PrivateInts, Is.Empty, "private custom data");
            Assert.That(shown.ServerInts, Is.Empty, "server custom data");
            Assert.That((UnityEngine.Vector3)shown.CurrentPosition, Is.EqualTo(UnityEngine.Vector3.zero), "position");
        }

        [Test]
        public void AnEmptyCharacterSelectListIsSentAsNoCharacters()
        {
            ResponseCharactersMessage received = TestNetData.RoundTrip(new ResponseCharactersMessage { characters = null });

            Assert.That(received.characters, Is.Not.Null.And.Empty);
        }

        [Test]
        public void AQuestWithNoProgressListsReadsBackEmptyLists()
        {
            // The database hands out quests whose progress lists were never made.
            CharacterQuest quest = new CharacterQuest { dataId = 5003, killedMonsters = null, completedTasks = null };

            CharacterQuest received = TestNetData.RoundTrip(quest);

            Assert.That(received.dataId, Is.EqualTo(5003));
            Assert.That(received.killedMonsters, Is.Not.Null.And.Empty);
            Assert.That(received.completedTasks, Is.Not.Null.And.Empty);
        }

        [Test]
        public void NoMountSendsOnlyItsType()
        {
            // With type None the rest is not written, so stale values on the sender do not
            // reach the receiver.
            CharacterMount mount = new CharacterMount { type = MountType.None, sourceId = "stale", level = 9, currentHp = 9 };

            byte[] bytes = TestNetData.Write(mount);
            CharacterMount received = TestNetData.Read<CharacterMount>(bytes);

            Assert.That(bytes, Has.Length.EqualTo(1), "the type byte alone");
            Assert.That(received.sourceId, Is.Null);
            Assert.That(received.level, Is.Zero);
        }

        [Test]
        public void AnEmptyBagSlotSendsOnlyItsId()
        {
            // An item with no amount or no data id is an empty slot: the receiver gets the id and
            // zeros, whatever stale values the sender's struct still holds.
            CharacterItem stale = Sword();
            stale.amount = 0;

            CharacterItem received = TestNetData.RoundTrip(stale);

            Assert.That(received.id, Is.EqualTo("sword-1"));
            Assert.That(received.dataId, Is.Zero);
            Assert.That(received.durability, Is.Zero);
            Assert.That(received.sockets.socket1, Is.Zero);
        }

        [Test]
        public void ADatabaseCharacterResponseMaySayNoCharacter()
        {
            CharacterResp found = TestNetData.RoundTrip(new CharacterResp { CharacterData = Veteran() });
            CharacterResp missing = TestNetData.RoundTrip(new CharacterResp { CharacterData = null });

            Assert.That(found.CharacterData.CharacterName, Is.EqualTo("Veteran"));
            Assert.That(missing.CharacterData, Is.Null);
        }

        [Test]
        public void ADatabaseRequestWrapsItsPayloadUnchanged()
        {
            DbRequestMessage<UpdateCharacterReq> request = new DbRequestMessage<UpdateCharacterReq>
            {
                RequestTimeUtc = 1700000000123L,
                Data = new UpdateCharacterReq { State = TransactionUpdateCharacterState.All, CharacterData = Veteran() },
            };

            DbRequestMessage<UpdateCharacterReq> received = TestNetData.RoundTrip(request);

            Assert.That(received.RequestTimeUtc, Is.EqualTo(1700000000123L));
            Assert.That(TestNetData.Differences(request.Data.CharacterData, received.Data.CharacterData, NotTheGuildSkills), Is.Empty);
        }
    }
}
