using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// Makes the quest log show the first quest's details the very first time it opens.
    ///
    /// `UICharacterQuests` selects the first quest the instant it builds the list, in its own
    /// `OnEnable`, and the selection handler shows the details panel (`uiDialog`). The quest
    /// window starts switched off, so on the first open its `OnEnable` runs *before* the details
    /// panel beneath it has woken up. `Show()` on a panel that has not yet had its `Awake` goes
    /// through, the panel's `Awake` then runs, sees `hideOnAwake`, and hides it again - leaving
    /// the first quest highlighted with nothing in the Information box. The list is in `Toggle`
    /// mode, so clicking the highlighted quest does nothing either; the player has to pick
    /// another quest and come back.
    ///
    /// By `Start` every `Awake` and `OnEnable` of that activation has run, so the details panel
    /// is awake and `Show()` sticks. This asks the selection to happen once more, only if the
    /// panel is still hidden while a quest is selected. It runs once, because `Start` does, which
    /// is exactly the one open where the order is wrong; every later open finds the panel awake.
    /// </summary>
    [RequireComponent(typeof(UICharacterQuests))]
    public class UIQuestLogFirstOpen : MonoBehaviour
    {
        private void Start()
        {
            UICharacterQuests quests = GetComponent<UICharacterQuests>();
            UICharacterQuest selected = quests.CacheSelectionManager.SelectedUI;
            if (quests.uiDialog == null || selected == null || quests.uiDialog.IsVisible())
                return;
            selected.SelectByManager();
        }
    }
}
