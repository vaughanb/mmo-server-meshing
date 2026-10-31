using System;
using MultiplayerARPG;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// Bags, plain items and the server log for inventory and economy tests. Needs a
    /// <see cref="TestGameInstance"/>.
    ///
    /// The kit's inventory actions (sell, refine, repair, dismantle, socket, storage moves) end by
    /// writing to GameInstance.ServerLogHandlers, which only a network manager sets; without one
    /// they throw after they have already changed the bag. <see cref="UseServerLog"/> installs
    /// the kit's do-nothing handlers for the test.
    /// </summary>
    public static class TestInventory
    {
        /// <summary>
        /// A plain (junk) item registered in GameInstance.Items, so a CharacterItem of it is not
        /// an empty slot.
        /// </summary>
        public static Item Item(TestObjects objects, int maxStack = 1, int sellPrice = 0, float weight = 0f, string name = null)
        {
            Item item = objects.Create<Item>(name);
            item.MaxStack = maxStack;
            item.SellPrice = sellPrice;
            item.Weight = weight;
            GameInstance.Items[item.DataId] = item;
            return item;
        }

        /// <summary>
        /// A player as <see cref="TestCharacters.NewPlayer"/> makes it, whose stats cache (slot
        /// and weight limits, price rates) is dropped after the test.
        /// </summary>
        public static PlayerCharacterData Player(TestObjects objects, string characterName = "Tester")
        {
            PlayerCharacterData player = TestCharacters.NewPlayer(characterName);
            objects.Own(new CacheEntry(player));
            player.MarkToMakeCaches();
            return player;
        }

        /// <summary>Switches the game to slot-limited bags of <paramref name="slots"/> slots.</summary>
        public static void LimitSlots(TestGameInstance gameInstance, int slots)
        {
            gameInstance.Instance.inventorySystem = InventorySystem.LimitSlots;
            gameInstance.Instance.baseSlotLimit = slots;
        }

        /// <summary>How many of the item the character carries in its bag, over all stacks.</summary>
        public static int Count(ICharacterData character, BaseItem item)
        {
            return character.CountNonEquipItems(item.DataId);
        }

        /// <summary>Installs the kit's do-nothing server log handlers until the test ends.</summary>
        public static void UseServerLog(TestObjects objects)
        {
            IServerLogHandlers previous = GameInstance.ServerLogHandlers;
            GameInstance.ServerLogHandlers = objects.NewGameObject("ServerLog").AddComponent<DefaultServerLogHandlers>();
            objects.Own(new Restore(() => GameInstance.ServerLogHandlers = previous));
        }

        private sealed class CacheEntry : IDisposable
        {
            private readonly ICharacterData _character;

            public CacheEntry(ICharacterData character)
            {
                _character = character;
            }

            public void Dispose()
            {
                _character.RemoveCaches();
            }
        }

        private sealed class Restore : IDisposable
        {
            private readonly Action _restore;

            public Restore(Action restore)
            {
                _restore = restore;
            }

            public void Dispose()
            {
                _restore();
            }
        }
    }
}
