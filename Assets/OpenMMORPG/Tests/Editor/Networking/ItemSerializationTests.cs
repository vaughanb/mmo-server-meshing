using MultiplayerARPG;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// Items on the wire, which depend on the game data: a CharacterItem writes its gear fields
    /// (durability, exp, sockets, seed) only for equipment, its ammo only for weapons and its exp
    /// for pets, choosing from the registered item; reader and writer must agree on every
    /// combination. Mail sends its attachments as text, which drops items the game data does
    /// not know and must keep everything else a mailed item carries.
    /// </summary>
    [Category(TestCategories.Harnessed)]
    public class ItemSerializationTests : KitTestFixture
    {
        [SetUp]
        public void SetUp()
        {
            UseGameInstance();
        }

        /// <summary>A stack of the item with every field set; a fresh id so no cached lookup is reused.</summary>
        private static CharacterItem Stack(BaseItem item)
        {
            return new CharacterItem
            {
                id = GenericUtils.GetUniqueId(),
                dataId = item.DataId,
                level = 4,
                amount = 3,
                equipSlotIndex = 1,
                durability = 55.25f,
                exp = 120,
                lockRemainsDuration = 30.5f,
                expireTime = 1893456000000L,
                randomSeed = 77,
                ammoDataId = 501,
                ammo = 12,
                sockets = new CharacterItemSockets { socket1 = 11, socket2 = 22, socket8 = 88 },
                version = 2,
            };
        }

        private static void AssertCommonFieldsSurvive(CharacterItem sent, CharacterItem received)
        {
            Assert.That(received.id, Is.EqualTo(sent.id), "id");
            Assert.That(received.dataId, Is.EqualTo(sent.dataId), "dataId");
            Assert.That(received.level, Is.EqualTo(sent.level), "level");
            Assert.That(received.amount, Is.EqualTo(sent.amount), "amount");
            Assert.That(received.equipSlotIndex, Is.EqualTo(sent.equipSlotIndex), "equipSlotIndex");
            Assert.That(received.lockRemainsDuration, Is.EqualTo(sent.lockRemainsDuration), "lockRemainsDuration");
            Assert.That(received.expireTime, Is.EqualTo(sent.expireTime), "expireTime");
            Assert.That(received.version, Is.EqualTo(sent.version), "version");
        }

        [Test]
        public void AJunkItemSendsNoGearAmmoOrPetFields()
        {
            CharacterItem sent = Stack(TestInventory.Item(Objects, maxStack: 10, name: "NetJunk"));

            CharacterItem received = TestNetData.RoundTrip(sent);

            AssertCommonFieldsSurvive(sent, received);
            Assert.That(new float[] { received.durability, received.exp, received.randomSeed, received.ammoDataId, received.ammo, received.sockets.socket1 }, Is.All.Zero);
        }

        [Test]
        public void ArmourSendsItsGearFieldsButNoAmmo()
        {
            CharacterItem sent = Stack(TestGear.Equipment<ArmorItem>(Objects, default));

            CharacterItem received = TestNetData.RoundTrip(sent);

            AssertCommonFieldsSurvive(sent, received);
            Assert.That(received.durability, Is.EqualTo(55.25f));
            Assert.That(received.exp, Is.EqualTo(120));
            Assert.That(received.randomSeed, Is.EqualTo(77));
            Assert.That(new[] { received.sockets.socket1, received.sockets.socket2, received.sockets.socket8 }, Is.EqualTo(new[] { 11, 22, 88 }));
            Assert.That(received.ammoDataId, Is.Zero);
            Assert.That(received.ammo, Is.Zero);
        }

        [Test]
        public void AWeaponSendsItsGearFieldsAndItsLoadedAmmo()
        {
            WeaponItem bow = Objects.Create<WeaponItem>("NetBow");
            GameInstance.Items[bow.DataId] = bow;
            CharacterItem sent = Stack(bow);

            CharacterItem received = TestNetData.RoundTrip(sent);

            AssertCommonFieldsSurvive(sent, received);
            Assert.That(received.durability, Is.EqualTo(55.25f));
            Assert.That(received.sockets.socket8, Is.EqualTo(88));
            Assert.That(received.ammoDataId, Is.EqualTo(501));
            Assert.That(received.ammo, Is.EqualTo(12));
        }

        [Test]
        public void APetSendsItsExpButNoGearFields()
        {
            PetItem pup = Objects.Create<PetItem>("NetPup");
            GameInstance.Items[pup.DataId] = pup;
            CharacterItem sent = Stack(pup);

            CharacterItem received = TestNetData.RoundTrip(sent);

            AssertCommonFieldsSurvive(sent, received);
            Assert.That(received.exp, Is.EqualTo(120));
            Assert.That(received.durability, Is.Zero);
            Assert.That(received.sockets.socket1, Is.Zero);
            Assert.That(received.ammo, Is.Zero);
        }

        [Test]
        public void MailedItemsArriveWithEverythingButTheirIdAndSlot()
        {
            ArmorItem helm = TestGear.Equipment<ArmorItem>(Objects, default);
            Mail mail = new Mail { Id = "mail-1", Title = "Spoils", Gold = 40 };
            CharacterItem sent = Stack(helm);
            mail.Items.Add(sent);

            Mail received = TestNetData.RoundTrip(mail);

            Assert.That(received.Items, Has.Count.EqualTo(1));
            CharacterItem item = received.Items[0];
            Assert.That(item.id, Is.Not.Empty.And.Not.EqualTo(sent.id), "a mailed item is given a new id");
            Assert.That(item.equipSlotIndex, Is.Zero, "the slot is not mailed");
            Assert.That(item.dataId, Is.EqualTo(sent.dataId));
            Assert.That(item.amount, Is.EqualTo(3));
            Assert.That(item.level, Is.EqualTo(4));
            Assert.That(item.durability, Is.EqualTo(55.25f), "two decimals are kept");
            Assert.That(item.exp, Is.EqualTo(120));
            Assert.That(item.lockRemainsDuration, Is.EqualTo(30.5f));
            Assert.That(item.expireTime, Is.EqualTo(1893456000000L));
            Assert.That(item.randomSeed, Is.EqualTo(77));
            Assert.That(new[] { item.sockets.socket1, item.sockets.socket2, item.sockets.socket8 }, Is.EqualTo(new[] { 11, 22, 88 }));
            Assert.That(item.ammoDataId, Is.EqualTo(501));
            Assert.That(item.ammo, Is.EqualTo(12));
            Assert.That(item.version, Is.EqualTo(2));
            Assert.That(received.Gold, Is.EqualTo(40), "the fields after the attachments still line up");
        }

        [Test]
        public void MailedCurrenciesArrive()
        {
            Mail mail = new Mail { Id = "mail-2", Title = "Tokens" };
            mail.Currencies.Add(new CharacterCurrency { dataId = 11, amount = 5 });
            mail.Currencies.Add(new CharacterCurrency { dataId = 22, amount = 7 });

            Mail received = TestNetData.RoundTrip(mail);

            Assert.That(received.Currencies, Has.Count.EqualTo(2));
            Assert.That(received.Currencies[0].dataId, Is.EqualTo(11));
            Assert.That(received.Currencies[0].amount, Is.EqualTo(5));
            Assert.That(received.Currencies[1].dataId, Is.EqualTo(22));
            Assert.That(received.Currencies[1].amount, Is.EqualTo(7));
        }

        [Test]
        public void AMailedItemKeepsALongLockAndHighDurability()
        {
            Mail mail = new Mail { Id = "mail-3" };
            CharacterItem sent = Stack(TestGear.Equipment<ArmorItem>(Objects, default));
            sent.lockRemainsDuration = 3600f;
            sent.durability = 1500f;
            mail.Items.Add(sent);

            Mail received = TestNetData.RoundTrip(mail);

            Assert.That(received.Items[0].lockRemainsDuration, Is.EqualTo(3600f));
            Assert.That(received.Items[0].durability, Is.EqualTo(1500f));
        }
    }
}
