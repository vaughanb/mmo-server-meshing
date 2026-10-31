#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
using System;
using System.Collections.Generic;
using System.Linq;
using MultiplayerARPG;
using MultiplayerARPG.MMO;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// Every table a player character is saved in, through CreateCharacter, UpdateCharacter and
    /// GetCharacter. Each table must read back what was written, an update with the table's
    /// TransactionUpdateCharacterState flag must save changes and delete rows dropped from the
    /// list, and an update WITHOUT the flag must leave the table alone. The last one matters most:
    /// v1.1.2 lost level, exp and position on logout because one save's flags were dropped.
    ///
    /// Each test runs once per table (the string argument names it); the characters row, PK and
    /// mount are "tables" too.
    /// </summary>
    [Category(TestCategories.Harnessed)]
    public class SQLiteCharacterTablesTests : KitTestFixture
    {
        private TempSqliteDatabase _db;
        private string _userId;

        /// <summary>One saved table: its flag, how to change it in memory and how to read it as text.</summary>
        private sealed class Table
        {
            public TransactionUpdateCharacterState Flag;
            /// <summary>Drops a row, edits a row and adds a row (or edits the values of a single-row table).</summary>
            public Action<PlayerCharacterData> Change;
            /// <summary>The table's contents as one comparable string.</summary>
            public Func<PlayerCharacterData, string> Read;
        }

        private static readonly Dictionary<string, Table> Tables = new Dictionary<string, Table>()
        {
            ["Character"] = new Table
            {
                Flag = TransactionUpdateCharacterState.Character,
                Change = c =>
                {
                    c.Level = 9; c.Exp = 900; c.Gold = 77; c.CurrentHp = 51; c.CurrentMp = 52;
                    c.CurrentStamina = 53; c.CurrentFood = 54; c.CurrentWater = 55; c.EquipWeaponSet = 1;
                    c.StatPoint = 2.5f; c.SkillPoint = 3.5f; c.DataId = 21; c.EntityId = 22; c.FactionId = 23;
                    c.CurrentChannel = "ch2"; c.CurrentMapName = "OtherMap"; c.CurrentSafeArea = "Town";
                    c.CurrentPosition = new Vec3(10f, 20f, 30f); c.CurrentRotation = new Vec3(0f, 90f, 0f);
#if !DISABLE_DIFFER_MAP_RESPAWNING
                    c.RespawnMapName = "RespawnMap"; c.RespawnPosition = new Vec3(4f, 5f, 6f);
#endif
                    c.IconDataId = 31; c.FrameDataId = 32; c.BackgroundDataId = 33; c.TitleDataId = 34;
                    c.Reputation = 35; c.LastDeadTime = 1700000000L; c.UnmuteTime = 1800000000L;
                },
                Read = c => string.Join("|", new object[]
                {
                    c.DataId, c.EntityId, c.FactionId, c.CharacterName, c.Level, c.Exp, c.CurrentHp, c.CurrentMp,
                    c.CurrentStamina, c.CurrentFood, c.CurrentWater, c.EquipWeaponSet, c.StatPoint, c.SkillPoint,
                    c.Gold, c.CurrentChannel, c.CurrentMapName, V(c.CurrentPosition), V(c.CurrentRotation),
                    c.CurrentSafeArea,
#if !DISABLE_DIFFER_MAP_RESPAWNING
                    c.RespawnMapName, V(c.RespawnPosition),
#endif
                    c.IconDataId, c.FrameDataId, c.BackgroundDataId, c.TitleDataId, c.Reputation,
                    c.LastDeadTime, c.UnmuteTime,
                }),
            },
#if !DISABLE_CLASSIC_PK
            ["Pk"] = new Table
            {
                Flag = TransactionUpdateCharacterState.Pk,
                Change = c =>
                {
                    c.IsPkOn = false; c.LastPkOnTime = 222L; c.PkPoint = 7; c.ConsecutivePkKills = 1;
                    c.HighestPkPoint = 70; c.HighestConsecutivePkKills = 10;
                },
                Read = c => $"{c.IsPkOn}|{c.LastPkOnTime}|{c.PkPoint}|{c.ConsecutivePkKills}|{c.HighestPkPoint}|{c.HighestConsecutivePkKills}",
            },
#endif
            ["Mount"] = new Table
            {
                Flag = TransactionUpdateCharacterState.Mount,
                Change = c => c.Mount = new CharacterMount() { type = MountType.Skill, sourceId = "skill-mount", mountRemainsDuration = 12.5f, level = 3, currentHp = 40 },
                Read = c => $"{c.Mount.type}|{c.Mount.sourceId}|{c.Mount.mountRemainsDuration}|{c.Mount.level}|{c.Mount.currentHp}",
            },
            ["Attributes"] = new Table
            {
                Flag = TransactionUpdateCharacterState.Attributes,
                Change = c => c.Attributes = new List<CharacterAttribute>()
                {
                    new CharacterAttribute() { dataId = 102, amount = 25 },
                    new CharacterAttribute() { dataId = 103, amount = 3 },
                },
                Read = c => Rows(c.Attributes, a => $"{a.dataId}:{a.amount}"),
            },
            ["Skills"] = new Table
            {
                Flag = TransactionUpdateCharacterState.Skills,
                Change = c => c.Skills = new List<CharacterSkill>()
                {
                    new CharacterSkill() { dataId = 202, level = 5 },
                    new CharacterSkill() { dataId = 203, level = 1 },
                },
                Read = c => Rows(c.Skills, s => $"{s.dataId}:{s.level}"),
            },
            ["SkillUsages"] = new Table
            {
                Flag = TransactionUpdateCharacterState.SkillUsages,
                Change = c => c.SkillUsages = new List<CharacterSkillUsage>()
                {
                    new CharacterSkillUsage() { type = SkillUsageType.GuildSkill, dataId = 302, coolDownRemainsDuration = 9.5f },
                    new CharacterSkillUsage() { type = SkillUsageType.UsableItem, dataId = 303, coolDownRemainsDuration = 1.5f },
                },
                Read = c => Rows(c.SkillUsages, s => $"{s.type}:{s.dataId}:{s.coolDownRemainsDuration}"),
            },
            ["Buffs"] = new Table
            {
                Flag = TransactionUpdateCharacterState.Buffs,
                Change = c => c.Buffs = new List<CharacterBuff>()
                {
                    new CharacterBuff() { id = "buff-2", type = BuffType.PotionBuff, dataId = 402, level = 4, buffRemainsDuration = 60.5f },
                    new CharacterBuff() { id = "buff-3", type = BuffType.StatusEffect, dataId = 403, level = 1, buffRemainsDuration = 2.5f },
                },
                Read = c => Rows(c.Buffs, b => $"{b.id}:{b.type}:{b.dataId}:{b.level}:{b.buffRemainsDuration}"),
            },
            ["Items"] = new Table
            {
                Flag = TransactionUpdateCharacterState.Items,
                Change = c =>
                {
                    c.NonEquipItems = new List<CharacterItem>() { Item("bag-2", 502, 1), Item("bag-3", 503, 8) };
                    c.EquipItems = new List<CharacterItem>() { Item("armour-2", 512, 1) };
                    c.SelectableWeaponSets = new List<EquipWeapons>()
                    {
                        new EquipWeapons() { rightHand = Item("axe-1", 522, 1) },
                    };
                },
                // Inventory order is the slot order, so it must survive as written.
                Read = c => "bag " + string.Join(",", c.NonEquipItems.Select(ItemText)) +
                    " / equip " + string.Join(",", c.EquipItems.Select(ItemText)) +
                    " / weapons " + string.Join(",", c.SelectableWeaponSets.Select(w => ItemText(w.rightHand) + "+" + ItemText(w.leftHand))),
            },
            ["Summons"] = new Table
            {
                Flag = TransactionUpdateCharacterState.Summons,
                Change = c => c.Summons = new List<CharacterSummon>()
                {
                    new CharacterSummon() { type = SummonType.PetItem, sourceId = "pet-item", dataId = 602, summonRemainsDuration = 0f, level = 6, exp = 60, currentHp = 600, currentMp = 66 },
                    new CharacterSummon() { type = SummonType.Custom, sourceId = "custom", dataId = 603, summonRemainsDuration = 4.5f, level = 1, exp = 0, currentHp = 10, currentMp = 0 },
                },
                Read = c => Rows(c.Summons, s => $"{s.type}:{s.sourceId}:{s.dataId}:{s.summonRemainsDuration}:{s.level}:{s.exp}:{s.currentHp}:{s.currentMp}"),
            },
            ["Hotkeys"] = new Table
            {
                Flag = TransactionUpdateCharacterState.Hotkeys,
                Change = c => c.Hotkeys = new List<CharacterHotkey>()
                {
                    new CharacterHotkey() { hotkeyId = "HK2", type = HotkeyType.Item, relateId = "bag-2" },
                    new CharacterHotkey() { hotkeyId = "HK3", type = HotkeyType.GuildSkill, relateId = "703" },
                },
                Read = c => Rows(c.Hotkeys, h => $"{h.hotkeyId}:{h.type}:{h.relateId}"),
            },
            ["Quests"] = new Table
            {
                Flag = TransactionUpdateCharacterState.Quests,
                Change = c => c.Quests = new List<CharacterQuest>()
                {
                    Quest(802, isComplete: true, killed: new Dictionary<int, int>() { [9001] = 10 }, tasks: new List<int>() { 0, 1 }),
                    Quest(803, isComplete: false, killed: new Dictionary<int, int>(), tasks: new List<int>()),
                },
                Read = c => Rows(c.Quests, q => $"{q.dataId}:{q.randomTasksIndex}:{q.isComplete}:{q.completeTime}:{q.isTracking}:" +
                    "[" + string.Join(",", (q.killedMonsters ?? new Dictionary<int, int>()).OrderBy(k => k.Key).Select(k => k.Key + "=" + k.Value)) + "]:" +
                    "[" + string.Join(",", q.completedTasks ?? new List<int>()) + "]"),
            },
#if !DISABLE_CUSTOM_CHARACTER_CURRENCIES
            ["Currencies"] = new Table
            {
                Flag = TransactionUpdateCharacterState.Currencies,
                Change = c => c.Currencies = new List<CharacterCurrency>()
                {
                    new CharacterCurrency() { dataId = 902, amount = 1000 },
                    new CharacterCurrency() { dataId = 903, amount = 1 },
                },
                Read = c => Rows(c.Currencies, x => $"{x.dataId}:{x.amount}"),
            },
#endif
#if !DISABLE_CUSTOM_CHARACTER_DATA
            ["ServerCustomData"] = new Table
            {
                Flag = TransactionUpdateCharacterState.ServerCustomData,
                Change = c => { c.ServerBools = ChangedBools(); c.ServerInts = ChangedInts(); c.ServerFloats = ChangedFloats(); },
                Read = c => CustomData(c.ServerBools, c.ServerInts, c.ServerFloats),
            },
            ["PrivateCustomData"] = new Table
            {
                Flag = TransactionUpdateCharacterState.PrivateCustomData,
                Change = c => { c.PrivateBools = ChangedBools(); c.PrivateInts = ChangedInts(); c.PrivateFloats = ChangedFloats(); },
                Read = c => CustomData(c.PrivateBools, c.PrivateInts, c.PrivateFloats),
            },
            ["PublicCustomData"] = new Table
            {
                Flag = TransactionUpdateCharacterState.PublicCustomData,
                Change = c => { c.PublicBools = ChangedBools(); c.PublicInts = ChangedInts(); c.PublicFloats = ChangedFloats(); },
                Read = c => CustomData(c.PublicBools, c.PublicInts, c.PublicFloats),
            },
#endif
        };

        private static IEnumerable<string> TableNames => Tables.Keys;

        [SetUp]
        public void SetUp()
        {
            _db = TempSqliteDatabase.Create(Objects);
            _userId = _db.CreateUser();
        }

        // ---- Data ----

        private static string V(Vec3 v) => $"{v.x},{v.y},{v.z}";

        /// <summary>Rows of a table whose read order is not the write order, sorted so they compare.</summary>
        private static string Rows<T>(IEnumerable<T> rows, Func<T, string> format)
        {
            return string.Join(",", rows.Select(format).OrderBy(s => s, StringComparer.Ordinal));
        }

        private static CharacterItem Item(string id, int dataId, int amount)
        {
            CharacterItem item = new CharacterItem()
            {
                id = id, dataId = dataId, level = 2, amount = amount, equipSlotIndex = 1, durability = 45.5f,
                exp = 12, lockRemainsDuration = 3.5f, expireTime = 1900000000L, randomSeed = 4242, ammo = 6, version = 1,
            };
            item.sockets.socket1 = 7001;
            item.sockets.socket3 = 7003;
            return item;
        }

        private static string ItemText(CharacterItem i)
        {
            return $"{i.id}:{i.dataId}:{i.level}:{i.amount}:{i.equipSlotIndex}:{i.durability}:{i.exp}:{i.lockRemainsDuration}:" +
                $"{i.expireTime}:{i.randomSeed}:{i.ammo}:{i.WriteSockets()}:{i.version}";
        }

        private static CharacterQuest Quest(int dataId, bool isComplete, Dictionary<int, int> killed, List<int> tasks)
        {
            return new CharacterQuest()
            {
                dataId = dataId, randomTasksIndex = 1, isComplete = isComplete, completeTime = isComplete ? 1650000000L : 0L,
                isTracking = !isComplete, killedMonsters = killed, completedTasks = tasks,
            };
        }

        private static List<CharacterDataBoolean> ChangedBools() => new List<CharacterDataBoolean>()
        {
            new CharacterDataBoolean() { hashedKey = 12, value = false }, new CharacterDataBoolean() { hashedKey = 13, value = true },
        };

        private static List<CharacterDataInt32> ChangedInts() => new List<CharacterDataInt32>()
        {
            new CharacterDataInt32() { hashedKey = 22, value = -5 }, new CharacterDataInt32() { hashedKey = 23, value = 99 },
        };

        private static List<CharacterDataFloat32> ChangedFloats() => new List<CharacterDataFloat32>()
        {
            new CharacterDataFloat32() { hashedKey = 32, value = 0.25f }, new CharacterDataFloat32() { hashedKey = 33, value = 8.5f },
        };

        private static string CustomData(IList<CharacterDataBoolean> bools, IList<CharacterDataInt32> ints, IList<CharacterDataFloat32> floats)
        {
            return Rows(bools, b => $"b{b.hashedKey}={b.value}") + "/" + Rows(ints, i => $"i{i.hashedKey}={i.value}") + "/" + Rows(floats, f => $"f{f.hashedKey}={f.value}");
        }

        /// <summary>A player with two rows in every list table and non-default values everywhere else.</summary>
        private static PlayerCharacterData FullCharacter()
        {
            PlayerCharacterData c = TestCharacters.NewPlayer("Full");
            // CreateCharacter always writes an empty channel and safe area (a new character is in
            // neither); the update case checks both are saved.
            c.CurrentChannel = string.Empty;
            c.Level = 5; c.Exp = 500; c.Gold = 40; c.CurrentHp = 41; c.CurrentMp = 42; c.CurrentStamina = 43;
            c.CurrentFood = 44; c.CurrentWater = 45; c.StatPoint = 1.5f; c.SkillPoint = 0.5f; c.DataId = 11;
            c.EntityId = 12; c.FactionId = 13; c.CurrentPosition = new Vec3(1f, 2f, 3f); c.CurrentRotation = new Vec3(0f, 45f, 0f);
#if !DISABLE_DIFFER_MAP_RESPAWNING
            c.RespawnPosition = new Vec3(7f, 8f, 9f);
#endif
            c.IconDataId = 14; c.FrameDataId = 15; c.BackgroundDataId = 16; c.TitleDataId = 17;
#if !DISABLE_CLASSIC_PK
            c.IsPkOn = true; c.LastPkOnTime = 111L; c.PkPoint = 3; c.ConsecutivePkKills = 2; c.HighestPkPoint = 30; c.HighestConsecutivePkKills = 5;
#endif
            c.Mount = new CharacterMount() { type = MountType.MountItem, sourceId = "horse-item", mountRemainsDuration = 0f, level = 2, currentHp = 80 };
            c.Attributes = new List<CharacterAttribute>()
            {
                new CharacterAttribute() { dataId = 101, amount = 10 }, new CharacterAttribute() { dataId = 102, amount = 20 },
            };
            c.Skills = new List<CharacterSkill>()
            {
                new CharacterSkill() { dataId = 201, level = 1 }, new CharacterSkill() { dataId = 202, level = 2 },
            };
            c.SkillUsages = new List<CharacterSkillUsage>()
            {
                new CharacterSkillUsage() { type = SkillUsageType.Skill, dataId = 301, coolDownRemainsDuration = 5.5f },
                new CharacterSkillUsage() { type = SkillUsageType.GuildSkill, dataId = 302, coolDownRemainsDuration = 3.5f },
            };
            c.Buffs = new List<CharacterBuff>()
            {
                new CharacterBuff() { id = "buff-1", type = BuffType.SkillBuff, dataId = 401, level = 1, buffRemainsDuration = 30.5f },
                new CharacterBuff() { id = "buff-2", type = BuffType.SkillDebuff, dataId = 402, level = 2, buffRemainsDuration = 10.5f },
            };
            c.NonEquipItems = new List<CharacterItem>() { Item("bag-1", 501, 3), Item("bag-2", 502, 20) };
            c.EquipItems = new List<CharacterItem>() { Item("armour-1", 511, 1) };
            c.SelectableWeaponSets = new List<EquipWeapons>()
            {
                new EquipWeapons() { rightHand = Item("sword-1", 521, 1), leftHand = Item("shield-1", 531, 1) },
                new EquipWeapons() { rightHand = Item("bow-1", 541, 1) },
            };
            c.Summons = new List<CharacterSummon>()
            {
                new CharacterSummon() { type = SummonType.Skill, sourceId = "skill-src", dataId = 601, summonRemainsDuration = 20.5f, level = 1, exp = 0, currentHp = 100, currentMp = 10 },
                new CharacterSummon() { type = SummonType.PetItem, sourceId = "pet-item", dataId = 602, summonRemainsDuration = 0f, level = 3, exp = 30, currentHp = 300, currentMp = 33 },
            };
            c.Hotkeys = new List<CharacterHotkey>()
            {
                new CharacterHotkey() { hotkeyId = "HK1", type = HotkeyType.Skill, relateId = "201" },
                new CharacterHotkey() { hotkeyId = "HK2", type = HotkeyType.Item, relateId = "bag-1" },
            };
            c.Quests = new List<CharacterQuest>()
            {
                Quest(801, isComplete: false, killed: new Dictionary<int, int>() { [9001] = 2, [9002] = 5 }, tasks: new List<int>() { 1 }),
                Quest(802, isComplete: false, killed: new Dictionary<int, int>(), tasks: new List<int>()),
            };
#if !DISABLE_CUSTOM_CHARACTER_CURRENCIES
            c.Currencies = new List<CharacterCurrency>()
            {
                new CharacterCurrency() { dataId = 901, amount = 50 }, new CharacterCurrency() { dataId = 902, amount = 60 },
            };
#endif
#if !DISABLE_CUSTOM_CHARACTER_DATA
            c.ServerBools = Bools(); c.ServerInts = Ints(); c.ServerFloats = Floats();
            c.PrivateBools = Bools(); c.PrivateInts = Ints(); c.PrivateFloats = Floats();
            c.PublicBools = Bools(); c.PublicInts = Ints(); c.PublicFloats = Floats();
#endif
            return c;
        }

        private static List<CharacterDataBoolean> Bools() => new List<CharacterDataBoolean>()
        {
            new CharacterDataBoolean() { hashedKey = 11, value = true }, new CharacterDataBoolean() { hashedKey = 12, value = true },
        };

        private static List<CharacterDataInt32> Ints() => new List<CharacterDataInt32>()
        {
            new CharacterDataInt32() { hashedKey = 21, value = 1 }, new CharacterDataInt32() { hashedKey = 22, value = 2 },
        };

        private static List<CharacterDataFloat32> Floats() => new List<CharacterDataFloat32>()
        {
            new CharacterDataFloat32() { hashedKey = 31, value = 1.5f }, new CharacterDataFloat32() { hashedKey = 32, value = 2.5f },
        };

        private PlayerCharacterData CreateFullCharacter()
        {
            PlayerCharacterData character = FullCharacter();
            TestAsync.Wait(_db.Database.CreateCharacter(_userId, character));
            return character;
        }

        private void Update(TransactionUpdateCharacterState state, PlayerCharacterData character, List<CharacterBuff> summonBuffs = null)
        {
            TestAsync.Wait(_db.Database.UpdateCharacter(state, character, summonBuffs, false));
        }

        private PlayerCharacterData Load(string id)
        {
            PlayerCharacterData loaded = TestAsync.Wait(_db.Database.GetCharacter(id));
            Assert.That(loaded, Is.Not.Null, "character found");
            return loaded;
        }

        // ---- Every table ----

        [Test]
        public void EveryTableCaseCoversAFlag()
        {
            TransactionUpdateCharacterState covered = TransactionUpdateCharacterState.None;
            foreach (Table table in Tables.Values)
                covered |= table.Flag;
#if !DISABLE_CLASSIC_PK && !DISABLE_CUSTOM_CHARACTER_CURRENCIES && !DISABLE_CUSTOM_CHARACTER_DATA
            Assert.That(covered, Is.EqualTo(TransactionUpdateCharacterState.All), "a new flag needs a table case here");
#endif
        }

        [Test, TestCaseSource(nameof(TableNames))]
        public void ATableReadsBackAfterCreate(string tableName)
        {
            Table table = Tables[tableName];
            PlayerCharacterData character = CreateFullCharacter();

            Assert.That(table.Read(Load(character.Id)), Is.EqualTo(table.Read(character)));
        }

        [Test, TestCaseSource(nameof(TableNames))]
        public void AnUpdateWithTheTablesFlagSavesItsChangesAndDeletesDroppedRows(string tableName)
        {
            Table table = Tables[tableName];
            PlayerCharacterData character = CreateFullCharacter();
            string before = table.Read(character);

            table.Change(character);
            Assume.That(table.Read(character), Is.Not.EqualTo(before), "the change changes something");
            Update(table.Flag, character);

            Assert.That(table.Read(Load(character.Id)), Is.EqualTo(table.Read(character)));
        }

        [Test, TestCaseSource(nameof(TableNames))]
        public void AnUpdateWithoutTheTablesFlagLeavesItUntouched(string tableName)
        {
            Table table = Tables[tableName];
            PlayerCharacterData character = CreateFullCharacter();
            string before = table.Read(character);

            table.Change(character);
            Update(TransactionUpdateCharacterState.All & ~table.Flag, character);

            Assert.That(table.Read(Load(character.Id)), Is.EqualTo(before));
        }

        // ---- Details ----

        [Test]
        public void AnEmptiedListDeletesEveryRowOfThatTable()
        {
            PlayerCharacterData character = CreateFullCharacter();

            character.NonEquipItems = new List<CharacterItem>();
            character.Quests = new List<CharacterQuest>();
            Update(TransactionUpdateCharacterState.Items | TransactionUpdateCharacterState.Quests, character);

            PlayerCharacterData loaded = Load(character.Id);
            Assert.That(loaded.NonEquipItems, Is.Empty);
            Assert.That(loaded.Quests, Is.Empty);
            Assert.That(loaded.EquipItems.Count, Is.EqualTo(1), "equipped items are another inventory");
        }

        [Test]
        public void AnItemWithoutAnIdIsNotSaved()
        {
            PlayerCharacterData character = FullCharacter();
            character.NonEquipItems = new List<CharacterItem>() { Item(null, 501, 1), Item("bag-2", 502, 1) };

            TestAsync.Wait(_db.Database.CreateCharacter(_userId, character));

            Assert.That(Load(character.Id).NonEquipItems.Select(i => i.id), Is.EqualTo(new[] { "bag-2" }));
        }

        [Test]
        public void AWeaponsLoadedAmmoTypeIsSaved()
        {
            PlayerCharacterData character = FullCharacter();
            EquipWeapons weapons = character.SelectableWeaponSets[1];
            weapons.rightHand.ammoDataId = 8001;
            character.SelectableWeaponSets[1] = weapons;

            TestAsync.Wait(_db.Database.CreateCharacter(_userId, character));

            Assert.That(Load(character.Id).SelectableWeaponSets[1].rightHand.ammoDataId, Is.EqualTo(8001));
        }

        // ---- Summon buffs (saved with the character, not by a flag) ----

        private static List<CharacterBuff> SummonBuffs() => new List<CharacterBuff>()
        {
            new CharacterBuff() { id = "sbuff-1", type = BuffType.SkillBuff, dataId = 1001, level = 2, buffRemainsDuration = 15.5f },
            new CharacterBuff() { id = "sbuff-2", type = BuffType.PotionBuff, dataId = 1002, level = 1, buffRemainsDuration = 5.5f },
        };

        private List<string> SavedSummonBuffs(string characterId)
        {
            return TestAsync.Wait(_db.Database.GetSummonBuffs(characterId))
                .Select(b => $"{b.id}:{b.type}:{b.dataId}:{b.level}:{b.buffRemainsDuration}").ToList();
        }

        [Test]
        public void SummonBuffsReadBackAfterAnUpdate()
        {
            PlayerCharacterData character = CreateFullCharacter();

            Update(TransactionUpdateCharacterState.Summons, character, SummonBuffs());

            // Ordered by remaining duration.
            Assert.That(SavedSummonBuffs(character.Id), Is.EqualTo(new[] { "sbuff-2:PotionBuff:1002:1:5.5", "sbuff-1:SkillBuff:1001:2:15.5" }));
        }

        [Test]
        public void AnUpdateWithoutSummonBuffsKeepsTheSavedOnes()
        {
            PlayerCharacterData character = CreateFullCharacter();
            Update(TransactionUpdateCharacterState.Summons, character, SummonBuffs());

            Update(TransactionUpdateCharacterState.All, character, null);

            Assert.That(SavedSummonBuffs(character.Id).Count, Is.EqualTo(2));
        }

        [Test]
        public void AnEmptySummonBuffListDeletesTheSavedOnes()
        {
            PlayerCharacterData character = CreateFullCharacter();
            Update(TransactionUpdateCharacterState.Summons, character, SummonBuffs());

            Update(TransactionUpdateCharacterState.Summons, character, new List<CharacterBuff>());

            Assert.That(SavedSummonBuffs(character.Id), Is.Empty);
        }
    }
}
#endif
