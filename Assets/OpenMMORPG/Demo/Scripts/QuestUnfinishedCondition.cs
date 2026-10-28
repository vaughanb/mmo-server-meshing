using Cysharp.Threading.Tasks;

namespace MultiplayerARPG
{
    /// <summary>
    /// An NPC dialog menu condition: the character has taken this quest and is still working on
    /// it - accepted, not handed in, and its tasks not yet done.
    ///
    /// The kit's own conditions cannot say this. `QuestOngoing` is also true once the tasks are
    /// done, and conditions on a menu are all ANDed with no way to negate one, so an NPC who
    /// keeps a quest behind a menu line (Hilde, the tower guard) could not have a "how is it
    /// going" line and a separate "here it is" line without both showing at hand-in. That was
    /// the bug (2026-09-24): Hilde's only venison line read "Is the pot empty again?", which is
    /// where the Complete button was, and a player carrying four cuts of venison never thought
    /// to pick it.
    ///
    /// A menu line still has to lead to the quest while it is under way, not only at the start
    /// and the end: the kit finds an NPC's quests for its overhead and minimap markers by
    /// walking the menus that currently pass (`NpcEntity.FindQuestFromDialog`), so a quest with
    /// no live line loses its in-progress marker.
    ///
    /// Written by `DemoNpcBuilder`, one asset per quest that needs it.
    /// </summary>
    public class QuestUnfinishedCondition : BaseCustomNpcDialogCondition
    {
        public Quest quest;

        public override UniTask<bool> IsPass(IPlayerCharacterData player)
        {
            if (quest == null || player == null)
                return UniTask.FromResult(false);
            int index = player.IndexOfQuest(quest.DataId);
            if (index < 0)
                return UniTask.FromResult(false);
            CharacterQuest characterQuest = player.Quests[index];
            return UniTask.FromResult(!characterQuest.isComplete && !characterQuest.IsAllTasksDone(player, out _));
        }
    }
}
