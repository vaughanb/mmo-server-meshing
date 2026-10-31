using MultiplayerARPG;
using NUnit.Framework;
using UnityEngine;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// Workbench crafting queues (CraftingQueueSourceExtensions). Only a craft the player can
    /// afford when it is queued goes in, with its recipe's duration and who asked for it; a full
    /// queue or a station that cannot craft takes nothing; on a shared station a player can change
    /// or cancel only their own entries. The queue's timed update needs a running network manager
    /// to find the crafter, so it is not run here; ItemCraftTests covers the craft it ends in.
    /// </summary>
    [Category(TestCategories.Harnessed)]
    public class CraftingQueueTests : KitTestFixture
    {
        private const uint CrafterId = 7;
        private const uint OtherCrafterId = 8;
        private const uint StationId = 100;

        private PlayerCharacterEntity _crafter;
        private PlayerCharacterEntity _otherCrafter;
        private Item _ore;
        private ItemCraftFormula _formula;

        /// <summary>A crafting station: a workbench, or a player's own queue when its id is the player's.</summary>
        private sealed class Station : ICraftingQueueSource
        {
            public SyncListCraftingQueueItem QueueItems { get; } = new SyncListCraftingQueueItem();
            public int MaxQueueSize { get; set; } = 5;
            public float CraftingDistance { get; set; }
            public bool PublicQueue { get; set; } = true;
            public bool CanCraft { get; set; } = true;
            public float TimeCounter { get; set; }
            public int SourceId { get; set; }
            public uint ObjectId { get; set; } = StationId;
            public Transform transform { get; set; }
        }

        [SetUp]
        public void SetUp()
        {
            TestGameInstance gameInstance = UseGameInstance();
            gameInstance.Instance.noInventoryWeightLimit = true;
            _crafter = Crafter(CrafterId);
            _otherCrafter = Crafter(OtherCrafterId);
            _ore = TestInventory.Item(Objects, maxStack: 10, name: "Ore");
            Item sword = TestInventory.Item(Objects, name: "Sword");
            _formula = Objects.Create<ItemCraftFormula>("SwordFormula")
                .With("itemCraft", new ItemCraft()
                    .With("craftingItem", sword)
                    .With("requireItems", new[] { new ItemAmount { item = _ore, amount = 2 } }))
                .With("craftDuration", 6f);
            GameInstance.ItemCraftFormulas[_formula.DataId] = _formula;
        }

        private PlayerCharacterEntity Crafter(uint objectId)
        {
            PlayerCharacterEntity crafter = TestCombatants.Create<PlayerCharacterEntity>(Objects);
            TestCombatants.SetObjectId(crafter, objectId);
            return crafter;
        }

        private Station NewStation(Vector3 position = default)
        {
            Transform where = Objects.NewGameObject("Station").transform;
            where.position = position;
            return new Station { transform = where };
        }

        private void GiveOre(PlayerCharacterEntity crafter, int amount)
        {
            crafter.IncreaseItems(CharacterItem.Create(_ore, 1, amount));
        }

        private bool Queue(Station station, PlayerCharacterEntity crafter, int amount, out UITextKeys message)
        {
            return station.AppendCraftingQueueItem(crafter, _formula.DataId, amount, out message);
        }

        [Test]
        public void AnAffordableCraftIsQueuedWithItsDurationAndCrafter()
        {
            Station station = NewStation();
            GiveOre(_crafter, 2);

            Assert.That(Queue(station, _crafter, 3, out _), Is.True);

            Assert.That(station.QueueItems, Has.Count.EqualTo(1));
            CraftingQueueItem queued = station.QueueItems[0];
            Assert.That(queued.crafterId, Is.EqualTo(CrafterId));
            Assert.That(queued.dataId, Is.EqualTo(_formula.DataId));
            Assert.That(queued.amount, Is.EqualTo(3));
            Assert.That(queued.craftRemainsDuration, Is.EqualTo(_formula.CraftDuration));
            Assert.That(queued.sourceObjectId, Is.EqualTo(StationId));
        }

        [Test]
        public void ACraftThePlayerCannotAffordIsNotQueued()
        {
            Station station = NewStation();
            GiveOre(_crafter, 1);

            Assert.That(Queue(station, _crafter, 1, out UITextKeys message), Is.False);

            Assert.That(message, Is.EqualTo(UITextKeys.UI_ERROR_NOT_ENOUGH_ITEMS));
            Assert.That(station.QueueItems, Is.Empty);
        }

        [Test]
        public void AnUnknownRecipeIsNotQueued()
        {
            Station station = NewStation();
            GiveOre(_crafter, 2);

            Assert.That(station.AppendCraftingQueueItem(_crafter, _formula.DataId + 1, 1, out _), Is.False);
            Assert.That(station.QueueItems, Is.Empty);
        }

        [Test]
        public void AFullQueueTakesNoMore()
        {
            Station station = NewStation();
            station.MaxQueueSize = 1;
            GiveOre(_crafter, 4);

            Assert.That(Queue(station, _crafter, 1, out _), Is.True);
            Assert.That(Queue(station, _crafter, 1, out _), Is.False);

            Assert.That(station.QueueItems, Has.Count.EqualTo(1));
        }

        [Test]
        public void AStationThatCannotCraftQueuesNothingAndDropsWhatItHad()
        {
            Station station = NewStation();
            GiveOre(_crafter, 2);
            Queue(station, _crafter, 1, out _);

            station.CanCraft = false;

            Assert.That(Queue(station, _crafter, 1, out _), Is.False);
            station.UpdateQueue();
            Assert.That(station.QueueItems, Is.Empty);
        }

        [Test]
        public void OnASharedStationOnlyTheCrafterCanChangeOrCancelTheirEntry()
        {
            Station station = NewStation();
            GiveOre(_crafter, 2);
            Queue(station, _crafter, 2, out _);

            station.ChangeCraftingQueueItem(_otherCrafter, 0, 9);
            station.CancelCraftingQueueItem(_otherCrafter, 0);
            Assert.That(station.QueueItems, Has.Count.EqualTo(1), "someone else cannot cancel it");
            Assert.That(station.QueueItems[0].amount, Is.EqualTo(2), "or change it");

            station.ChangeCraftingQueueItem(_crafter, 0, 5);
            Assert.That(station.QueueItems[0].amount, Is.EqualTo(5));
        }

        [Test]
        public void ChangingAnEntryToNothingRemovesIt()
        {
            Station station = NewStation();
            GiveOre(_crafter, 2);
            Queue(station, _crafter, 2, out _);

            station.ChangeCraftingQueueItem(_crafter, 0, 0);

            Assert.That(station.QueueItems, Is.Empty);
        }

        [Test]
        public void CancellingRemovesOnlyThatEntry()
        {
            Station station = NewStation();
            GiveOre(_crafter, 2);
            GiveOre(_otherCrafter, 2);
            Queue(station, _crafter, 1, out _);
            Queue(station, _otherCrafter, 4, out _);

            station.CancelCraftingQueueItem(_crafter, 0);

            Assert.That(station.QueueItems, Has.Count.EqualTo(1));
            Assert.That(station.QueueItems[0].crafterId, Is.EqualTo(OtherCrafterId));
            Assert.That(station.QueueItems[0].amount, Is.EqualTo(4));
        }

        [Test]
        public void AStationWithACraftingDistanceServesOnlyThoseNearIt()
        {
            Station anywhere = NewStation(Vector3.zero);
            Station bench = NewStation(Vector3.zero);
            bench.CraftingDistance = 3f;

            Assert.That(anywhere.IsInCraftDistance(new Vector3(500f, 0f, 0f)), Is.True, "no distance set");
            Assert.That(bench.IsInCraftDistance(new Vector3(3f, 0f, 0f)), Is.True);
            Assert.That(bench.IsInCraftDistance(new Vector3(3.1f, 0f, 0f)), Is.False);
        }
    }
}
