using System.Collections.Generic;
using MultiplayerARPG;
using NUnit.Framework;
using OpenMMORPG.Tests;

namespace OpenMMORPG.Demo.Tests
{
    /// <summary>
    /// Rules the demo's game data must keep. Each one is a bug the demo shipped with once; the
    /// summary on each test says which. They check that the data is wired, never what it is
    /// tuned to.
    /// </summary>
    [Category(TestCategories.Content)]
    public class DemoGameplayDataTests
    {
        /// <summary>
        /// With no default element the kit invents a nameless one at runtime, so nothing could ever
        /// resist physical damage: there was no asset for armour to name (2026-09-22).
        /// </summary>
        [Test]
        public void TheGameInstanceNamesADefaultDamageElement()
        {
            foreach ((GameInstance gameInstance, string path) in DemoTestPaths.Prefabs<GameInstance>())
                Assert.That(gameInstance.DefaultDamageElement, Is.Not.Null, path);
        }

        /// <summary>
        /// A critical hit deals damage x criDmgRate. Monster stats never wrote it, so every monster
        /// crit did 0 damage (2026-10-06).
        /// </summary>
        [Test]
        public void EveryCharacterThatCanCritHitsHarderWhenItDoes()
        {
            List<string> broken = new List<string>();
            foreach ((BaseCharacter character, string path) in DemoTestPaths.Assets<BaseCharacter>())
            {
                CharacterStats stats = character.GetCharacterStats(1);
                if (stats.criRate > 0f && stats.criDmgRate < 1f)
                    broken.Add($"{path}: crit chance {stats.criRate}, crit damage x{stats.criDmgRate}");
            }
            Assert.That(broken, Is.Empty);
        }

        /// <summary>
        /// `QuestTaskType.Custom` is 254, so a builder writing it as an enum index silently left the
        /// task a KillMonster task and its custom task was never asked (2026-10-02).
        /// </summary>
        [Test]
        public void EveryCustomQuestTaskIsMarkedCustom()
        {
            List<string> broken = new List<string>();
            foreach ((Quest quest, string path) in DemoTestPaths.Assets<Quest>())
            {
                if (quest.randomTasks == null)
                    continue;
                foreach (QuestTasks tasks in quest.randomTasks)
                {
                    if (tasks.tasks == null)
                        continue;
                    for (int i = 0; i < tasks.tasks.Length; ++i)
                    {
                        QuestTask task = tasks.tasks[i];
                        if (task.customQuestTask != null && task.taskType != QuestTaskType.Custom)
                            broken.Add($"{path} task {i}: has {task.customQuestTask.name} but is {task.taskType}");
                        if (task.taskType == QuestTaskType.Custom && task.customQuestTask == null)
                            broken.Add($"{path} task {i}: is Custom with no custom task");
                    }
                }
            }
            Assert.That(broken, Is.Empty);
        }

        /// <summary>
        /// A sell price of 0 is free, not "use the item's price", and an amount of 0 sells a whole
        /// stack; every shop gave its goods away (2026-09-25).
        /// </summary>
        [Test]
        public void EveryShopItemHasAPriceAndAnAmount()
        {
            List<string> broken = new List<string>();
            foreach ((NpcDialog dialog, string path) in DemoTestPaths.Assets<NpcDialog>())
            {
                if (dialog.type != NpcDialogType.Shop || dialog.sellItems == null)
                    continue;
                foreach (NpcSellItem sellItem in dialog.sellItems)
                {
                    string name = sellItem.item != null ? sellItem.item.name : "(no item)";
                    if (sellItem.item == null)
                        broken.Add($"{path}: an entry has no item");
                    if (sellItem.sellPrice <= 0 && !HasPrice(sellItem.sellPrices))
                        broken.Add($"{path}: {name} is free");
                    if (sellItem.amount < 1)
                        broken.Add($"{path}: {name} sells a whole stack (amount {sellItem.amount})");
                }
            }
            Assert.That(broken, Is.Empty);
        }

        private static bool HasPrice(CurrencyAmount[] prices)
        {
            if (prices == null)
                return false;
            foreach (CurrencyAmount price in prices)
            {
                if (price.currency != null && price.amount > 0)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// A harvestable takes the tool's harvest damage, not its weapon damage, and pays
        /// (int)(amountPerDamage x damage) per swing. Harvest damage was 0 on every weapon and the
        /// rates rounded to 0 anyway, so harvesting never worked (2026-09-25).
        /// </summary>
        [Test]
        public void EveryHarvestToolYieldsSomethingPerSwing()
        {
            List<(IWeaponItem weapon, string name)> weapons = new List<(IWeaponItem, string)>();
            foreach ((BaseItem item, string _) in DemoTestPaths.Assets<BaseItem>())
            {
                if (item.IsWeapon() && item is IWeaponItem weapon && weapon.WeaponType != null)
                    weapons.Add((weapon, item.name));
            }

            List<string> broken = new List<string>();
            foreach ((Harvestable harvestable, string path) in DemoTestPaths.Assets<Harvestable>())
            {
                foreach (HarvestEffectiveness tool in harvestable.harvestEffectivenesses)
                {
                    if (tool.weaponType == null || tool.items == null)
                        continue;
                    float bestSwing = 0f;
                    foreach ((IWeaponItem weapon, string _) in weapons)
                    {
                        if (weapon.WeaponType == tool.weaponType)
                            bestSwing = System.Math.Max(bestSwing, weapon.HarvestDamageAmount.GetAmount(1).min * tool.damageEffectiveness);
                    }
                    foreach (ItemDropForHarvestable drop in tool.items)
                    {
                        if (drop.item != null && (int)(drop.amountPerDamage * bestSwing) < 1)
                            broken.Add($"{path}: the best {tool.weaponType.name} swing ({bestSwing}) yields no {drop.item.name} (amountPerDamage {drop.amountPerDamage})");
                    }
                }
            }
            Assert.That(broken, Is.Empty);
        }
    }
}
