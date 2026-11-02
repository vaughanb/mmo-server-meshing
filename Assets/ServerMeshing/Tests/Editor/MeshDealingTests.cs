// Server Meshing add-on — the trade's two halves (MeshDealing, DESIGN 1.44): what a trader's cell takes out and gives
// in, over character data alone.
using System.Collections.Generic;
using MultiplayerARPG;
using NUnit.Framework;
using OpenMMORPG.Tests;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    [Category(TestCategories.Logic)]
    public class MeshDealingTests : KitTestFixture
    {
        private Item _ore, _gem;
        private PlayerCharacterData _trader;

        [SetUp]
        public void SetUp()
        {
            UseGameInstance();
            _ore = TestInventory.Item(Objects, maxStack: 20, name: "Ore");
            _gem = TestInventory.Item(Objects, maxStack: 1, name: "Gem");
            _trader = TestInventory.Player(Objects, "Trader");
            _trader.Gold = 100;
            _trader.NonEquipItems.Add(CharacterItem.Create(_ore, 1, 10));
            _trader.NonEquipItems.Add(CharacterItem.Create(_gem, 1, 1));
            _trader.FillEmptySlots();
        }

        private CharacterItem Offer(int dataId, int amount)
        {
            int index = _trader.IndexOfNonEquipItem(dataId);
            CharacterItem offer = _trader.NonEquipItems[index].Clone();
            offer.amount = amount;
            return offer;
        }

        [Test]
        public void TakeOffered_RemovesTheOfferedAmountsAndGold()
        {
            List<CharacterItem> taken = new List<CharacterItem>();
            bool ok = MeshDealing.TakeOffered(_trader, new[] { Offer(_ore.DataId, 4), Offer(_gem.DataId, 1) }, 30, taken);

            Assert.That(ok, Is.True);
            Assert.That(TestInventory.Count(_trader, _ore), Is.EqualTo(6), "the stack is reduced");
            Assert.That(TestInventory.Count(_trader, _gem), Is.EqualTo(0), "a whole item is gone");
            Assert.That(_trader.Gold, Is.EqualTo(70));
            Assert.That(taken.Count, Is.EqualTo(2));
            Assert.That(taken[0].dataId, Is.EqualTo(_ore.DataId));
            Assert.That(taken[0].amount, Is.EqualTo(4), "what was taken is the offered amount, not the stack");
            Assert.That(taken[1].dataId, Is.EqualTo(_gem.DataId));
        }

        [Test]
        public void TakeOffered_RefusesAndChangesNothing_WhenAnAmountIsNotThere()
        {
            List<CharacterItem> taken = new List<CharacterItem>();
            bool ok = MeshDealing.TakeOffered(_trader, new[] { Offer(_ore.DataId, 4), Offer(_gem.DataId, 2) }, 30, taken);

            Assert.That(ok, Is.False);
            Assert.That(TestInventory.Count(_trader, _ore), Is.EqualTo(10), "nothing taken, not even the item that was there");
            Assert.That(_trader.Gold, Is.EqualTo(100));
            Assert.That(taken, Is.Empty);
        }

        [Test]
        public void TakeOffered_RefusesWhenTheGoldIsShort()
        {
            List<CharacterItem> taken = new List<CharacterItem>();
            Assert.That(MeshDealing.TakeOffered(_trader, new[] { Offer(_ore.DataId, 1) }, 101, taken), Is.False);
            Assert.That(TestInventory.Count(_trader, _ore), Is.EqualTo(10));
            Assert.That(_trader.Gold, Is.EqualTo(100));
        }

        [Test]
        public void Give_AddsItemsAndGold_AndStacks()
        {
            int given = 0;
            MeshDealing.Give(_trader, new[] { CharacterItem.Create(_ore, 1, 5), CharacterItem.Create(_gem, 1, 1) }, 25, item => given++);

            Assert.That(TestInventory.Count(_trader, _ore), Is.EqualTo(15));
            Assert.That(TestInventory.Count(_trader, _gem), Is.EqualTo(2));
            Assert.That(_trader.Gold, Is.EqualTo(125));
            Assert.That(given, Is.EqualTo(2), "the reward hook per item given");
        }

        [Test]
        public void TakeThenGive_IsTheKitsExchange_ForEachSide()
        {
            // Two traders, each side run on its own data: what A takes out, B is given, and the other way round.
            PlayerCharacterData other = TestInventory.Player(Objects, "Other");
            other.Gold = 10;
            other.NonEquipItems.Add(CharacterItem.Create(_gem, 1, 1));
            other.FillEmptySlots();
            List<CharacterItem> fromTrader = new List<CharacterItem>(), fromOther = new List<CharacterItem>();
            int otherIndex = other.IndexOfNonEquipItem(_gem.DataId);
            CharacterItem otherOffer = other.NonEquipItems[otherIndex].Clone();

            Assert.That(MeshDealing.TakeOffered(_trader, new[] { Offer(_ore.DataId, 3) }, 20, fromTrader), Is.True);
            Assert.That(MeshDealing.TakeOffered(other, new[] { otherOffer }, 0, fromOther), Is.True);
            MeshDealing.Give(other, fromTrader, 20, null);
            MeshDealing.Give(_trader, fromOther, 0, null);

            Assert.That((TestInventory.Count(_trader, _ore), TestInventory.Count(_trader, _gem), _trader.Gold), Is.EqualTo((7, 2, 80)));
            Assert.That((TestInventory.Count(other, _ore), TestInventory.Count(other, _gem), other.Gold), Is.EqualTo((3, 0, 30)));
        }

        [Test]
        public void WillOverwhelm_IsFalseForNothing()
        {
            Assert.That(MeshDealing.WillOverwhelm(_trader, new List<CharacterItem>()), Is.False);
            Assert.That(MeshDealing.WillOverwhelm(null, new[] { CharacterItem.Create(_ore, 1, 1) }), Is.True, "no receiver: refuse");
        }
    }
}
