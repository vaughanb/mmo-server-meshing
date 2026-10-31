using MultiplayerARPG;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// Socketing gems into gear (EnhanceSocketItem) and taking them out (RemoveEnhancerFromItem).
    /// Socketing uses up exactly one gem and only fits a gem of the socket's type into an empty
    /// socket the item actually has; a refused socketing keeps the gem. Removal empties the
    /// socket, gives the gem back when the game is set to, and charges the removal fee.
    /// </summary>
    [Category(TestCategories.Harnessed)]
    public class SocketEnhanceTests : KitTestFixture
    {
        private TestGameInstance _gameInstance;
        private PlayerCharacterData _player;
        private ArmorItem _helm;
        private SocketEnhancerItem _ruby;

        [SetUp]
        public void SetUp()
        {
            _gameInstance = UseGameInstance();
            TestInventory.UseServerLog(Objects);
            _player = TestInventory.Player(Objects);
            _helm = TestGear.Equipment<ArmorItem>(Objects, default)
                .With("availableSocketEnhancerTypes", new[] { SocketEnhancerType.Type1, SocketEnhancerType.Type2 });
            _ruby = Gem("Ruby", SocketEnhancerType.Type1);
        }

        private SocketEnhancerItem Gem(string name, SocketEnhancerType type)
        {
            SocketEnhancerItem gem = Objects.Create<SocketEnhancerItem>(name).With("socketEnhancerType", type);
            gem.MaxStack = 10;
            GameInstance.Items[gem.DataId] = gem;
            return gem;
        }

        private CharacterItem Helm()
        {
            return _player.NonEquipItems[_player.IndexOfNonEquipItem(_helm.DataId)];
        }

        private bool Socket(BaseItem gem, int socketIndex, out UITextKeys message)
        {
            int helmIndex = _player.IndexOfNonEquipItem(_helm.DataId);
            return _player.EnhanceSocketItem(InventoryType.NonEquipItems, helmIndex, 0, gem.DataId, socketIndex, out message);
        }

        [Test]
        public void SocketingFillsTheFirstEmptySocketAndUsesOneGem()
        {
            _player.NonEquipItems.Add(CharacterItem.Create(_helm));
            _player.NonEquipItems.Add(CharacterItem.Create(_ruby, 1, 3));

            Assert.That(Socket(_ruby, -1, out _), Is.True);

            Assert.That(Helm().sockets[0], Is.EqualTo(_ruby.DataId));
            Assert.That(TestInventory.Count(_player, _ruby), Is.EqualTo(2));
        }

        [Test]
        public void AGemOfTheWrongTypeIsRefusedAndKept()
        {
            _player.NonEquipItems.Add(CharacterItem.Create(_helm));
            _player.NonEquipItems.Add(CharacterItem.Create(_ruby, 1, 1));

            Assert.That(Socket(_ruby, 1, out UITextKeys message), Is.False, "socket 1 takes Type2");

            Assert.That(message, Is.EqualTo(UITextKeys.UI_ERROR_CANNOT_ENHANCE_SOCKET));
            Assert.That(Helm().sockets[1], Is.Zero);
            Assert.That(TestInventory.Count(_player, _ruby), Is.EqualTo(1));
        }

        [Test]
        public void AFilledSocketIsNotOverwritten()
        {
            CharacterItem helm = CharacterItem.Create(_helm);
            helm.sockets[0] = _ruby.DataId;
            _player.NonEquipItems.Add(helm);
            _player.NonEquipItems.Add(CharacterItem.Create(_ruby, 1, 1));

            Assert.That(Socket(_ruby, 0, out UITextKeys message), Is.False);

            Assert.That(message, Is.EqualTo(UITextKeys.UI_ERROR_SOCKET_NOT_EMPTY));
            Assert.That(TestInventory.Count(_player, _ruby), Is.EqualTo(1));
        }

        [Test]
        public void AGemThatIsNotCarriedCannotBeSocketed()
        {
            _player.NonEquipItems.Add(CharacterItem.Create(_helm));

            Assert.That(Socket(_ruby, 0, out UITextKeys message), Is.False);

            Assert.That(message, Is.EqualTo(UITextKeys.UI_ERROR_NOT_ENOUGH_SOCKET_ENCHANER));
            Assert.That(Helm().sockets[0], Is.Zero);
        }

        [Test]
        public void AnItemWithEveryRealSocketFilledReportsNoEmptySocket()
        {
            CharacterItem helm = CharacterItem.Create(_helm);
            helm.sockets[0] = _ruby.DataId;
            helm.sockets[1] = Gem("Sapphire", SocketEnhancerType.Type2).DataId;
            _player.NonEquipItems.Add(helm);
            _player.NonEquipItems.Add(CharacterItem.Create(_ruby, 1, 1));

            Assert.That(Socket(_ruby, -1, out UITextKeys message), Is.False);

            Assert.That(message, Is.EqualTo(UITextKeys.UI_ERROR_NO_EMPTY_SOCKET));
            Assert.That(TestInventory.Count(_player, _ruby), Is.EqualTo(1));
        }

        [Test]
        public void AGemListedBeforeTheItemDoesNotShiftTheSocketingOntoAnotherItem()
        {
            Item potion = TestInventory.Item(Objects, maxStack: 10, name: "Potion");
            _player.NonEquipItems.Add(CharacterItem.Create(_ruby, 1, 1));
            _player.NonEquipItems.Add(CharacterItem.Create(_helm));
            _player.NonEquipItems.Add(CharacterItem.Create(potion, 1, 4));

            Socket(_ruby, 0, out _);

            Assert.That(TestInventory.Count(_player, _helm), Is.EqualTo(1), "one helm, not two");
            Assert.That(TestInventory.Count(_player, potion), Is.EqualTo(4), "the potions are not overwritten");
        }

        [Test]
        public void RemovingAGemEmptiesTheSocketReturnsTheGemAndChargesTheFee()
        {
            _gameInstance.Instance.enhancerRemoval = new EnhancerRemoval()
                .With("returnEnhancerItem", true)
                .With("requireGold", 25);
            CharacterItem helm = CharacterItem.Create(_helm);
            helm.sockets[0] = _ruby.DataId;
            _player.NonEquipItems.Add(helm);
            _player.Gold = 30;

            Assert.That(_player.RemoveEnhancerFromItem(InventoryType.NonEquipItems, 0, 0, 0, out _), Is.True);

            Assert.That(Helm().sockets[0], Is.Zero);
            Assert.That(TestInventory.Count(_player, _ruby), Is.EqualTo(1));
            Assert.That(_player.Gold, Is.EqualTo(5));
        }

        [Test]
        public void WithoutTheFeeTheGemStaysInItsSocket()
        {
            _gameInstance.Instance.enhancerRemoval = new EnhancerRemoval()
                .With("returnEnhancerItem", true)
                .With("requireGold", 25);
            CharacterItem helm = CharacterItem.Create(_helm);
            helm.sockets[0] = _ruby.DataId;
            _player.NonEquipItems.Add(helm);
            _player.Gold = 24;

            Assert.That(_player.RemoveEnhancerFromItem(InventoryType.NonEquipItems, 0, 0, 0, out UITextKeys message), Is.False);

            Assert.That(message, Is.EqualTo(UITextKeys.UI_ERROR_NOT_ENOUGH_CURRENCY_AMOUNTS));
            Assert.That(Helm().sockets[0], Is.EqualTo(_ruby.DataId));
            Assert.That(TestInventory.Count(_player, _ruby), Is.Zero);
            Assert.That(_player.Gold, Is.EqualTo(24));
        }
    }
}
