using MultiplayerARPG;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// The exp table: entry N is what level N needs to reach level N + 1, so a table of N entries
    /// tops out at level N + 1 (its own calculator builds maxLevel - 1 entries), where there is
    /// no next level. Levelling and the exp bar both read it.
    /// </summary>
    [Category(TestCategories.Logic)]
    public class ExpTableTests : KitTestFixture
    {
        private ExpTable _table;

        [SetUp]
        public void SetUp()
        {
            _table = Objects.Create<ExpTable>();
            _table.expTree = new[] { 10, 20, 30 };
        }

        [Test]
        public void EachLevelNeedsItsOwnEntry()
        {
            Assert.That(_table.GetNextLevelExp(1), Is.EqualTo(10));
            Assert.That(_table.GetNextLevelExp(2), Is.EqualTo(20));
            Assert.That(_table.GetNextLevelExp(3), Is.EqualTo(30));
        }

        [Test]
        public void TheMaxLevelAndLevelZeroHaveNoNextLevel()
        {
            Assert.That(_table.GetNextLevelExp(4), Is.EqualTo(0), "max level");
            Assert.That(_table.GetNextLevelExp(99), Is.EqualTo(0));
            Assert.That(_table.GetNextLevelExp(0), Is.EqualTo(0));
        }

        [Test]
        public void TheExpBarNeverShowsMoreThanTheLevelNeeds()
        {
            _table.GetProperCurrentByNextLevelExp(1, 50, out int current, out int next);

            Assert.That(next, Is.EqualTo(10));
            Assert.That(current, Is.EqualTo(10));
        }

        [Test]
        public void TheExpBarIsFullAtTheMaxLevel()
        {
            _table.GetProperCurrentByNextLevelExp(4, 0, out int current, out int next);

            Assert.That(next, Is.GreaterThan(0));
            Assert.That(current, Is.EqualTo(next));
        }

        [Test]
        public void TheExpBarOnTheLastLevelBeforeMaxShowsRealProgress()
        {
            _table.GetProperCurrentByNextLevelExp(3, 5, out int current, out int next);

            Assert.That(next, Is.EqualTo(30), "level 3 needs 30 to reach level 4");
            Assert.That(current, Is.EqualTo(5));
        }
    }
}
