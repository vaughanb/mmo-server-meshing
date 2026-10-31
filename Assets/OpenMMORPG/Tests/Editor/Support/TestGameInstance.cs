using System;
using MultiplayerARPG;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// A GameInstance for edit-mode tests. The real one sets itself up in Awake, which never runs
    /// for AddComponent outside play mode and would throw if called (it uses DontDestroyOnLoad).
    /// This sets GameInstance.Singleton by reflection and fills in the defaults Awake would make:
    /// weapon and armour types, the default and monster weapon items, the default damage element
    /// (registered in slot zero), the gameplay rule at rate 1, the inventory and message managers
    /// and an exp table. The default weapon is registered in GameInstance.Items, as
    /// LoadedGameData does. Disposing it empties the kit's game-data dictionaries and puts the
    /// previous Singleton back.
    ///
    /// Register game data straight into GameInstance's dictionaries, e.g.
    /// <c>GameInstance.Quests[quest.DataId] = quest;</c>.
    /// </summary>
    public sealed class TestGameInstance : IDisposable
    {
        private readonly GameInstance _previous;

        public GameInstance Instance { get; }
        public DefaultGameplayRule GameplayRule { get; }
        public ExpTable ExpTable { get; }
        public DamageElement DefaultDamageElement => Instance.DefaultDamageElement;
        public Item DefaultWeaponItem { get; }

        public static TestGameInstance Create(TestObjects objects)
        {
            return objects.Own(new TestGameInstance(objects));
        }

        private TestGameInstance(TestObjects objects)
        {
            _previous = GameInstance.Singleton;
            Instance = objects.NewGameObject(nameof(TestGameInstance)).AddComponent<GameInstance>();
            TestReflection.SetStaticProperty(typeof(GameInstance), nameof(GameInstance.Singleton), Instance);

            ArmorType armorType = objects.Create<ArmorType>().GenerateDefaultArmorType();
            WeaponType weaponType = objects.Create<WeaponType>().GenerateDefaultWeaponType();
            TestReflection.SetProperty(Instance, nameof(GameInstance.DefaultArmorType), armorType);
            TestReflection.SetProperty(Instance, nameof(GameInstance.DefaultWeaponType), weaponType);

            DefaultWeaponItem = objects.Create<Item>().GenerateDefaultItem(weaponType);
            TestReflection.SetField(Instance, "defaultWeaponItem", DefaultWeaponItem);
            TestReflection.SetProperty(Instance, nameof(GameInstance.MonsterWeaponItem), DefaultWeaponItem);

            DamageElement element = objects.Create<DamageElement>().GenerateDefaultDamageElement(new GameEffect[0]
#if !DISABLE_ADDRESSABLES
                , new AssetReferenceGameEffect[0]
#endif
                );
            TestReflection.SetField(Instance, "defaultDamageElement", element);

            GameplayRule = objects.Create<DefaultGameplayRule>();
            GameplayRule.GoldRate = 1f;
            GameplayRule.ExpRate = 1f;
            GameplayRule.ItemDropRate = 1f;
            TestReflection.SetField(Instance, "gameplayRule", GameplayRule);
            TestReflection.SetField(Instance, "inventoryManager", objects.Create<DefaultInventoryManager>());
            TestReflection.SetField(Instance, "messageManager", objects.Create<DefaultMessageManager>());

            ExpTable = objects.Create<ExpTable>();
            ExpTable.expTree = new int[0];
            TestReflection.SetField(Instance, "expTable", ExpTable);

            // Empties every dictionary and registers the default element now that Singleton is set.
            GameInstance.ClearData();
            // LoadedGameData registers the default weapon as an item; stats code looks it up there.
            GameInstance.Items[DefaultWeaponItem.DataId] = DefaultWeaponItem;
        }

        public void Dispose()
        {
            GameInstance.ClearData();
            TestReflection.SetStaticProperty(typeof(GameInstance), nameof(GameInstance.Singleton), _previous);
        }
    }
}
