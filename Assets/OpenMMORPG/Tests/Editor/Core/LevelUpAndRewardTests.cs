using MultiplayerARPG;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// The default rule's exp and gold rewards, levelling and the exp lost on death. Exp carries
    /// over across as many levels as it pays for, each level grants stat and skill points, nothing
    /// is collected at the max level, the server rate and the character's own rate both scale kill
    /// rewards, and monsters (other than pets) never level from kills. Dying to a monster costs a
    /// percentage of the current level's requirement, rounded up.
    /// </summary>
    [Category(TestCategories.Harnessed)]
    public class LevelUpAndRewardTests : KitTestFixture
    {
        private TestGameInstance _gameInstance;
        private DefaultGameplayRule _rule;
        private PlayerCharacter _class;
        private IServerGuildHandlers _previousGuildHandlers;

        [SetUp]
        public void SetUp()
        {
            _gameInstance = UseGameInstance();
            _rule = _gameInstance.GameplayRule;
            ExpTable table = Objects.Create<ExpTable>();
            table.expTree = new[] { 10, 20, 30 };
            _class = Objects.Create<PlayerCharacter>("TestClass").With("expTable", table);
            GameInstance.Characters[_class.DataId] = _class;
            // Kill rewards and death look the character's guild up.
            _previousGuildHandlers = GameInstance.ServerGuildHandlers;
            GameInstance.ServerGuildHandlers = Objects.NewGameObject("Guilds").AddComponent<DefaultServerGuildHandlers>();
        }

        [TearDown]
        public void TearDown()
        {
            GameInstance.ServerGuildHandlers = _previousGuildHandlers;
        }

        private PlayerCharacterEntity Player(int level = 1, CharacterStats stats = default)
        {
            if (stats.hp == 0f)
                stats.hp = 100f;
            PlayerCharacterEntity player = TestCombatants.Create<PlayerCharacterEntity>(Objects, level, stats);
            player.DataId = _class.DataId;
            return player;
        }

        private bool Reward(BaseCharacterEntity character, int exp, RewardGivenType type = RewardGivenType.Quest)
        {
            return _rule.RewardExp(character, exp, 1f, type, 1, 1, out _);
        }

        [Test]
        public void ExpShortOfTheNextLevelIsKeptWithoutALevelUp()
        {
            PlayerCharacterEntity player = Player();

            Assert.That(Reward(player, 5), Is.False);
            Assert.That(player.Level, Is.EqualTo(1));
            Assert.That(player.Exp, Is.EqualTo(5));
        }

        [Test]
        public void ExpForSeveralLevelsLevelsThroughEachAndKeepsTheRemainder()
        {
            PlayerCharacterEntity player = Player();

            Assert.That(Reward(player, 35), Is.True);
            Assert.That(player.Level, Is.EqualTo(3), "10 for level 2, 20 for level 3");
            Assert.That(player.Exp, Is.EqualTo(5));
        }

        [Test]
        public void EachLevelGainedGrantsStatAndSkillPoints()
        {
            _rule.increaseStatPointEachLevel = 5f;
            _rule.increaseSkillPointEachLevel = 1f;
            PlayerCharacterEntity player = Player();

            Reward(player, 30);

            Assert.That(player.StatPoint, Is.EqualTo(10f));
            Assert.That(player.SkillPoint, Is.EqualTo(2f));
        }

        [Test]
        public void ExpIsNotCollectedOnceTheMaxLevelIsReached()
        {
            PlayerCharacterEntity player = Player();

            Reward(player, 100);

            Assert.That(player.Level, Is.EqualTo(4), "the table's three entries top out at level 4");
            Assert.That(player.Exp, Is.EqualTo(0));

            Assert.That(Reward(player, 50), Is.False);
            Assert.That(player.Level, Is.EqualTo(4));
            Assert.That(player.Exp, Is.EqualTo(0));
        }

        [Test]
        public void KillExpScalesWithTheServerRateAndTheCharactersOwnRate()
        {
            _class.With("expTable", Objects.Create<ExpTable>().With("expTree", new[] { 1000 }));
            PlayerCharacterEntity plain = Player();
            PlayerCharacterEntity boosted = Player(stats: new CharacterStats { expRate = 0.5f });

            _rule.ExpRate = 2f;
            _rule.RewardExp(plain, 10, 1f, RewardGivenType.KillMonster, 1, 1, out int plainExp);
            _rule.RewardExp(boosted, 10, 1f, RewardGivenType.KillMonster, 1, 1, out int boostedExp);

            Assert.That(plainExp, Is.EqualTo(20), "server rate 2");
            Assert.That(boostedExp, Is.EqualTo(25), "server rate 2 plus the character's 0.5");
            Assert.That(boosted.Exp, Is.EqualTo(25));
        }

        [Test]
        public void AMonsterGainsNoExpFromKills()
        {
            MonsterCharacterEntity monster = TestCombatants.Create<MonsterCharacterEntity>(Objects);

            Assert.That(_rule.RewardExp(monster, 50, 1f, RewardGivenType.KillMonster, 1, 1, out int rewarded), Is.False);
            Assert.That(rewarded, Is.EqualTo(0));
            Assert.That(monster.Exp, Is.EqualTo(0));
        }

        [Test]
        public void LevellingUpRefillsHealthOnlyWhenTheRuleSaysSo()
        {
            PlayerCharacterEntity kept = Player();
            Reward(kept, 10);
            Assert.That(kept.CurrentHp, Is.EqualTo(1));

            _rule.recoverHpWhenLevelUp = true;
            PlayerCharacterEntity refilled = Player();
            Reward(refilled, 10);
            Assert.That(refilled.CurrentHp, Is.EqualTo(100));
        }

        [Test]
        public void KillGoldScalesWithTheServerRateAndMonstersGetNone()
        {
            PlayerCharacterEntity player = Player();
            MonsterCharacterEntity monster = TestCombatants.Create<MonsterCharacterEntity>(Objects);

            _rule.GoldRate = 2f;
            _rule.RewardGold(player, 15, 1f, RewardGivenType.KillMonster, 1, 1, out int playerGold);
            _rule.RewardGold(monster, 15, 1f, RewardGivenType.KillMonster, 1, 1, out int monsterGold);

            Assert.That(playerGold, Is.EqualTo(30));
            Assert.That(player.Gold, Is.EqualTo(30));
            Assert.That(monsterGold, Is.EqualTo(0));
        }

        [Test]
        public void DyingToAMonsterCostsAShareOfTheLevelsExpRoundedUp()
        {
            _rule.expLostPercentageWhenDeath = 15f;
            PlayerCharacterEntity player = Player(level: 3);

            _rule.GetPlayerDeadPunishment(DeadPunishmentType.Unspecified, player, null,
                out int decreaseExp, out _, out _, out int attackerPkPoint);

            Assert.That(decreaseExp, Is.EqualTo(5), "15% of the 30 that level 3 needs is 4.5");
            Assert.That(attackerPkPoint, Is.EqualTo(0));
        }

        [Test]
        public void APlayerKillScoresPkPointsAndCostsNoExpWithoutPkTiers()
        {
            _rule.pkPointEachKills = 10;
            PlayerCharacterEntity victim = Player(level: 2);
            PlayerCharacterEntity killer = Player(level: 2);

            _rule.GetPlayerDeadPunishment(DeadPunishmentType.PK, victim, killer,
                out int decreaseExp, out int decreaseGold, out _, out int attackerPkPoint);

            Assert.That(attackerPkPoint, Is.EqualTo(10));
            Assert.That(decreaseExp, Is.EqualTo(0));
            Assert.That(decreaseGold, Is.EqualTo(0));
        }
    }
}
