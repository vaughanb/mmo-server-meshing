using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// Switches other components off for as long as this object is active, and back on when it
    /// goes.
    ///
    /// Made for an equipment slot's name ("Arms", "R-Hand"), which sits under the slot's icon: the
    /// kit switches the icon on when the slot is filled and off when it empties
    /// (<c>SetImageGameDataIcon</c>), and with a transparent icon the name showed through the item.
    /// Put this on the icon and list the name's Text.
    ///
    /// **Components, not objects:** these run while a window is being opened or closed, when
    /// Unity refuses to switch objects in that hierarchy on or off. And the targets come back on
    /// whenever this object goes off, the window closing included, so a slot emptied while the
    /// window was shut still shows its name - the icon, off, raises nothing on the reopen.
    ///
    /// Runs only on this object's own enable and disable - nothing per frame.
    /// </summary>
    [DisallowMultipleComponent]
    public class HideWhileActive : MonoBehaviour
    {
        [Tooltip("Switched off while this object is active, on while it is not.")]
        public Behaviour[] hide = new Behaviour[0];

        private void OnEnable()
        {
            SetHidden(true);
        }

        private void OnDisable()
        {
            SetHidden(false);
        }

        private void SetHidden(bool hidden)
        {
            foreach (Behaviour target in hide)
            {
                if (target != null)
                    target.enabled = !hidden;
            }
        }
    }
}
