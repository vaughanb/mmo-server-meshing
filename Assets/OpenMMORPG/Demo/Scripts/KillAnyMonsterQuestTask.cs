using Cysharp.Text;
using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// A quest task: kill a number of monsters, any mix of the kinds listed. Rowan's "Thin the
    /// Camp" counts the plain bandits, their archers and the marauders alike (2026-10-02) - a
    /// player clearing the headland kills whatever stands there, and the kit's own
    /// `KillMonster` task names exactly one `MonsterCharacter`, so seven archers and a marauder
    /// counted for nothing.
    ///
    /// The kills themselves are still recorded by the kit. `CharacterQuest.AddKillMonster` keeps
    /// a count per monster in `killedMonsters` (saved, and synced to the client) for every
    /// monster id in `Quest.CacheKillMonsterIds`, and credits the last hitter and their nearby
    /// party exactly as for a kit kill task. That set is built only from `KillMonster` tasks,
    /// so <see cref="RegisterKillIds"/> adds this task's monsters to it once the game data has
    /// loaded; progress is then the sum of those counts. A character who took the quest when it
    /// counted plain bandits keeps those kills, since the bandit is on the list.
    ///
    /// The tracker line uses the kit's own kill format, so it reads like any other kill task;
    /// the kit's quest toasts skip custom tasks, which is what `UICustomQuestTaskToast` is for.
    ///
    /// Written by `DemoNpcBuilder`, one asset per quest that needs it.
    /// </summary>
    public class KillAnyMonsterQuestTask : BaseCustomQuestTask
    {
        [Tooltip("What the tracker calls them, e.g. \"Bandits\".")]
        public string title;
        public MonsterCharacter[] monsters = new MonsterCharacter[0];
        public int amount = 1;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Subscribe()
        {
            // Static events outlive a play session when domain reload is off.
            GameInstance.OnGameDataLoadedEvent -= RegisterKillIds;
            GameInstance.OnGameDataLoadedEvent += RegisterKillIds;
        }

        private static void RegisterKillIds()
        {
            foreach (Quest quest in GameInstance.Quests.Values)
            {
                if (quest == null || quest.randomTasks == null)
                    continue;
                foreach (QuestTasks tasks in quest.randomTasks)
                {
                    if (tasks.tasks == null)
                        continue;
                    foreach (QuestTask task in tasks.tasks)
                    {
                        if (task.taskType != QuestTaskType.Custom || !(task.customQuestTask is KillAnyMonsterQuestTask killAny))
                            continue;
                        foreach (MonsterCharacter monster in killAny.monsters)
                        {
                            if (monster != null)
                                quest.CacheKillMonsterIds.Add(monster.DataId);
                        }
                    }
                }
            }
        }

        public override string GetTaskDescription(IPlayerCharacterData playerCharacter, int progress)
        {
            if (progress >= amount)
                return ZString.Format(LanguageManager.GetText(UIFormatKeys.UI_FORMAT_QUEST_TASK_KILL_MONSTER_COMPLETE.ToString()), title);
            return ZString.Format(LanguageManager.GetText(UIFormatKeys.UI_FORMAT_QUEST_TASK_KILL_MONSTER.ToString()), title, progress.ToString("N0"), amount.ToString("N0"));
        }

        public override int GetTaskProgress(IPlayerCharacterData playerCharacter, Quest quest, int taskIndex, out string targetTitle, out int maxProgress, out bool isComplete)
        {
            targetTitle = title;
            maxProgress = amount;
            int progress = 0;
            int questIndex = playerCharacter.IndexOfQuest(quest.DataId);
            if (questIndex >= 0)
            {
                CharacterQuest characterQuest = playerCharacter.Quests[questIndex];
                foreach (MonsterCharacter monster in monsters)
                {
                    if (monster != null)
                        progress += characterQuest.CountKillMonster(monster.DataId);
                }
            }
            // The kit caps nothing, and a camp keeps respawning; "9/8" reads like a bug.
            if (progress > amount)
                progress = amount;
            isComplete = progress >= amount;
            return progress;
        }
    }
}
