using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// Hides a HUD tracker while it has nothing to track.
    ///
    /// The kit's party and quest panels never go away. With no party you get a panel telling you
    /// so and offering to make one; with no tracked quest you get a panel telling you that and
    /// offering to open the quest log. As a tabbed window that is reasonable - you opened it, it
    /// owes you an answer. Parked permanently on the HUD it is two boxes of nothing, which is the
    /// opposite of what a tracker is for: a tracker earns its corner of the screen by being empty
    /// most of the time and appearing when there is something to say.
    ///
    /// **The kit already knows.** It toggles an "empty" object - `NotInParty`, `NoTrackedQuests` -
    /// exactly when the panel has nothing in it, so there is no need to ask whether the player is
    /// in a party or has a quest, and no second copy of that rule to drift out of step with the
    /// first. Watching the object the kit is already maintaining means this keeps working through
    /// any change to how the kit decides.
    ///
    /// **A `CanvasGroup`, not `SetActive`.** Switching the panel off would take its scripts down
    /// with it: `UICharacterQuests` subscribes in `OnEnable` and unsubscribes in `OnDisable`, so a
    /// deactivated tracker stops hearing about quests and could never notice the one that should
    /// bring it back. Alpha zero leaves everything running and listening while drawing nothing,
    /// and clearing `blocksRaycasts` stops an invisible panel from swallowing clicks meant for the
    /// world behind it.
    /// </summary>
    [RequireComponent(typeof(CanvasGroup))]
    public class UIHideWhenEmpty : MonoBehaviour
    {
        [Tooltip("The kit's own \"nothing here\" object - NotInParty, NoTrackedQuests. " +
                 "While this is active the tracker hides itself.")]
        public GameObject emptyState;

        private CanvasGroup _group;
        private bool _shown = true;

        private void Awake()
        {
            _group = GetComponent<CanvasGroup>();
        }

        /// <summary>
        /// Polled rather than driven by an event, and deliberately.
        ///
        /// It is one bool read a frame against a `GameObject` that is already in memory, which is
        /// cheaper than it is to describe. The alternative - subscribing to the party and quest
        /// events - means duplicating the kit's rule for what counts as empty, in two different
        /// subsystems, and getting it wrong in a way that only shows up as a tracker stuck on
        /// screen after the last quest is handed in. `LateUpdate` so the kit has already settled
        /// the empty object this frame.
        /// </summary>
        private void LateUpdate()
        {
            bool show = emptyState == null || !emptyState.activeInHierarchy;
            if (show == _shown)
                return;
            _shown = show;
            _group.alpha = show ? 1f : 0f;
            _group.blocksRaycasts = show;
            _group.interactable = show;
        }
    }
}
