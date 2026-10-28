using System.Collections.Generic;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// The "Kills Bandits: 3/8" pop-up for custom quest tasks. The kit's
    /// `UIQuestNotificationManager` raises one for kill, item and talk tasks and skips custom
    /// ones, so once Thin the Camp counted any bandit through <see cref="KillAnyMonsterQuestTask"/>
    /// its kills went quiet. This watches the same quest list and posts the task's own
    /// description through the kit manager's handler and message prefab, so the two look alike.
    ///
    /// Sits next to the kit manager on the demo's `UIGameMessageHandler` prefab.
    /// </summary>
    [RequireComponent(typeof(UIQuestNotificationManager))]
    public class UICustomQuestTaskToast : MonoBehaviour
    {
        private UIQuestNotificationManager _kit;
        private BasePlayerCharacterEntity _character;
        private readonly Dictionary<(int quest, int task), int> _progress = new Dictionary<(int quest, int task), int>();
        private float _awakenTime;

        private void Awake()
        {
            _kit = GetComponent<UIQuestNotificationManager>();
            _awakenTime = Time.unscaledTime;
        }

        private void OnEnable()
        {
            _character = GameInstance.PlayingCharacterEntity;
            if (_character == null)
                return;
            _progress.Clear();
            for (int i = 0; i < _character.Quests.Count; ++i)
                Check(i, false);
            _character.onQuestsOperation += OnQuestsOperation;
        }

        private void OnDisable()
        {
            if (_character != null)
                _character.onQuestsOperation -= OnQuestsOperation;
            _character = null;
        }

        private void OnQuestsOperation(LiteNetLibSyncListOp operation, int index, CharacterQuest oldItem, CharacterQuest newItem)
        {
            switch (operation)
            {
                case LiteNetLibSyncListOp.Add:
                case LiteNetLibSyncListOp.Insert:
                    Check(index, false);
                    break;
                case LiteNetLibSyncListOp.Set:
                case LiteNetLibSyncListOp.Dirty:
                    Check(index, true);
                    break;
            }
        }

        private void Check(int index, bool announce)
        {
            if (index < 0 || index >= _character.Quests.Count)
                return;
            CharacterQuest characterQuest = _character.Quests[index];
            Quest quest = characterQuest.GetQuest();
            if (quest == null)
                return;
            QuestTask[] tasks = quest.GetTasks(characterQuest.randomTasksIndex);
            for (int i = 0; i < tasks.Length; ++i)
            {
                if (tasks[i].taskType != QuestTaskType.Custom || tasks[i].customQuestTask == null)
                    continue;
                var key = (quest.DataId, i);
                // A repeatable quest taken again starts from nothing; so does a handed-in one.
                int progress = characterQuest.isComplete ? 0 : characterQuest.GetProgress(_character, i, out bool _);
                bool known = _progress.TryGetValue(key, out int previous);
                _progress[key] = progress;
                if (!announce || !known || progress <= previous || characterQuest.isComplete)
                    continue;
                if (Time.unscaledTime - _awakenTime < _kit.delayBeforeShowingMessages || _kit.messageHandler == null)
                    continue;
                TextWrapper message = _kit.messageHandler.AddMessage(_kit.questTaskUpdateMessagePrefab);
                if (message != null)
                    message.text = tasks[i].customQuestTask.GetTaskDescription(_character, progress);
            }
        }
    }
}
