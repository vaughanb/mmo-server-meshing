using Insthync.CameraAndInput;
using UnityEngine;

namespace MultiplayerARPG.Demo
{
    /// <summary>
    /// Escape, in WoW's order: it takes back a ground-targeted skill that is being aimed, and only
    /// when nothing is being aimed does it open or close the game menu.
    ///
    /// The kit binds a window to a key through `UISceneGameplay.toggleUis` and toggles it on every
    /// press, with no way to hand a press on. With the game menu bound there, Escape could not
    /// cancel an aim without the menu opening over the game as well. So the menu's entry on that
    /// list is left without a key - the menu bar still opens it - and this owns Escape instead,
    /// deciding what a press means before anything acts on it.
    ///
    /// Added to CanvasGameplay's root, with <see cref="menu"/> pointed at the game menu, by
    /// DemoWindowKeysBuilder. The right click that also cancels an aim is the player controller's.
    /// </summary>
    public class DemoEscapeKey : MonoBehaviour
    {
        [Tooltip("The game menu, opened and closed by the key when nothing is being aimed.")]
        public UIBase menu;
        public KeyCode key = KeyCode.Escape;

        private void Update()
        {
            // As the kit's own window keys: a key typed into the chat box is not a command. The
            // key first: the focus test looks components up, and the key is down one frame in thousands.
            if (!InputManager.GetKeyDown(key) || GenericUtils.IsFocusInputField())
                return;
            if (DemoPlayerController.CancelAiming())
                return;
            if (menu != null)
                menu.Toggle();
        }
    }
}
