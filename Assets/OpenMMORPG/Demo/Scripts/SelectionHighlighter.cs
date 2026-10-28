using Insthync.ManagedUpdating;
using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>Something that lights up while it is what the local player has selected.</summary>
    public interface ISelectionHighlightable
    {
        void SetSelectionHighlight(bool on);
    }

    /// <summary>
    /// Lights up whatever the local player's controller has selected, if it is an
    /// <see cref="ISelectionHighlightable"/>, and puts out what it lit before.
    ///
    /// The kit raises no event when the selection changes - <c>SelectedEntity</c> is a plain
    /// property the controller sets as it re-finds its target every frame - so something has to
    /// look. This is the one look for every highlightable thing in the scene, instead of each of
    /// them comparing itself with the selection every frame. It is registered with the kit's
    /// update manager only while at least one highlightable is enabled.
    ///
    /// The controller only accepts an activatable inside its activate distance, so "is the
    /// selection" already means "is close enough to use", which is what the light should say.
    /// </summary>
    public static class SelectionHighlighter
    {
        private static readonly Ticker s_ticker = new Ticker();
        private static int s_members;
        private static bool s_registered;
        private static ISelectionHighlightable s_lit;

        /// <summary>Call from the highlightable's OnEnable.</summary>
        public static void Add(ISelectionHighlightable member)
        {
            ++s_members;
            if (s_registered)
                return;
            s_registered = true;
            UpdateManager.Register(s_ticker);
        }

        /// <summary>Call from the highlightable's OnDisable; puts its light out if it was lit.</summary>
        public static void Remove(ISelectionHighlightable member)
        {
            if (ReferenceEquals(s_lit, member))
            {
                s_lit = null;
                member.SetSelectionHighlight(false);
            }
            s_members = Mathf.Max(0, s_members - 1);
            if (s_members > 0 || !s_registered)
                return;
            s_registered = false;
            UpdateManager.Unregister(s_ticker);
        }

        private sealed class Ticker : IManagedUpdate
        {
            public void ManagedUpdate()
            {
                BasePlayerCharacterController controller = BasePlayerCharacterController.Singleton;
                var selected = controller != null ? controller.SelectedEntity as ISelectionHighlightable : null;
                // A destroyed selection is not one: the interface reference outlives the object.
                if (selected is Object destroyed && destroyed == null)
                    selected = null;
                if (ReferenceEquals(selected, s_lit))
                    return;
                if (s_lit != null && !(s_lit is Object gone && gone == null))
                    s_lit.SetSelectionHighlight(false);
                s_lit = selected;
                if (selected != null)
                    selected.SetSelectionHighlight(true);
            }
        }
    }
}
