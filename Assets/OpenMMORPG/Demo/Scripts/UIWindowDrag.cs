using UnityEngine;
using UnityEngine.EventSystems;

namespace MultiplayerARPG
{
    /// <summary>
    /// Lets a window be moved by its title bar, the way MMO windows are.
    ///
    /// Every dialog in the kit's template is a `Window` panel with a `Title` bar anchored across
    /// its top; this goes on the `Title` and moves the `Window`. The kit has no window dragging of
    /// its own - its drag handlers are for items and skills - so each window opened where the
    /// template put it, and on the right-hand side that was on top of the quest tracker
    /// (2026-09-24, when the user asked for it).
    ///
    /// - **Kept on screen.** A window is clamped inside the canvas as it moves, and again when it
    ///   is first shown, so a position saved at a larger resolution cannot strand it off-screen.
    ///   One taller or wider than the screen keeps its top-left corner, where the title and the
    ///   close button are.
    /// - **Brought to the front** when its title is pressed, among the windows that share its
    ///   container - not past them: the container's other children (item tooltips, messages) are
    ///   meant to stay over every window.
    /// - **Remembered** per window across sessions, in PlayerPrefs - a per-client preference, like
    ///   the key bindings. **Double-click the title to put a window back** where the layout has it.
    ///
    /// Placed on the demo's dialog prefabs by `DemoWindowDragBuilder`.
    /// </summary>
    [DisallowMultipleComponent]
    public class UIWindowDrag : MonoBehaviour, IPointerDownHandler, IBeginDragHandler, IDragHandler,
        IEndDragHandler, IPointerClickHandler
    {
        [Tooltip("The panel that moves. Left empty, the parent of this title bar.")]
        public RectTransform window;
        [Tooltip("Remember where the player left this window, across sessions.")]
        public bool rememberPosition = true;

        private const string PrefsPrefix = "DemoWindow.";
        private Vector2 _home;
        private bool _homeKnown;
        private Vector2 _grabOffset;
        private bool _dragging;
        private string _prefsKey;

        private void Awake()
        {
            if (window == null)
                window = transform.parent as RectTransform;
            if (window == null)
                return;
            _home = window.anchoredPosition;
            _homeKnown = true;
            _prefsKey = PrefsPrefix + PathOf(window);
            if (rememberPosition && PlayerPrefs.HasKey(_prefsKey + ".x"))
            {
                window.anchoredPosition = new Vector2(
                    PlayerPrefs.GetFloat(_prefsKey + ".x"),
                    PlayerPrefs.GetFloat(_prefsKey + ".y"));
            }
        }

        private void OnEnable()
        {
            // Wait for a layout pass: a window's size is not known the frame it is switched on.
            if (window != null)
                StartCoroutine(ClampNextFrame());
        }

        private System.Collections.IEnumerator ClampNextFrame()
        {
            yield return null;
            Clamp();
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            BringToFront();
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            RectTransform parent = window == null ? null : window.parent as RectTransform;
            if (parent == null || eventData.button != PointerEventData.InputButton.Left)
                return;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, eventData.position,
                    eventData.pressEventCamera, out Vector2 pointer))
                return;
            _grabOffset = window.anchoredPosition - pointer;
            _dragging = true;
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (!_dragging)
                return;
            var parent = (RectTransform)window.parent;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, eventData.position,
                    eventData.pressEventCamera, out Vector2 pointer))
                return;
            window.anchoredPosition = pointer + _grabOffset;
            Clamp();
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (!_dragging)
                return;
            _dragging = false;
            Save();
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.clickCount != 2 || window == null || !_homeKnown)
                return;
            window.anchoredPosition = _home;
            Clamp();
            if (!string.IsNullOrEmpty(_prefsKey))
            {
                PlayerPrefs.DeleteKey(_prefsKey + ".x");
                PlayerPrefs.DeleteKey(_prefsKey + ".y");
            }
        }

        private void Save()
        {
            if (!rememberPosition || string.IsNullOrEmpty(_prefsKey))
                return;
            PlayerPrefs.SetFloat(_prefsKey + ".x", window.anchoredPosition.x);
            PlayerPrefs.SetFloat(_prefsKey + ".y", window.anchoredPosition.y);
            PlayerPrefs.Save();
        }

        /// <summary>
        /// Moves the window back inside the canvas. Measured in the root canvas's own units and
        /// applied as a world-space shift, so it does not matter how the window's parents are
        /// anchored or scaled.
        /// </summary>
        private void Clamp()
        {
            if (window == null)
                return;
            Canvas canvas = window.GetComponentInParent<Canvas>();
            if (canvas == null)
                return;
            var area = (RectTransform)canvas.rootCanvas.transform;
            var corners = new Vector3[4];
            window.GetWorldCorners(corners);
            Vector2 min = area.InverseTransformPoint(corners[0]);
            Vector2 max = area.InverseTransformPoint(corners[2]);
            Rect bounds = area.rect;

            Vector2 shift = Vector2.zero;
            // Left and top win when the window cannot fit: that is where the title and close are.
            if (max.x > bounds.xMax)
                shift.x = bounds.xMax - max.x;
            if (min.x + shift.x < bounds.xMin)
                shift.x = bounds.xMin - min.x;
            if (min.y < bounds.yMin)
                shift.y = bounds.yMin - min.y;
            if (max.y + shift.y > bounds.yMax)
                shift.y = bounds.yMax - max.y;
            if (shift != Vector2.zero)
                window.position += area.TransformVector(shift);
        }

        /// <summary>
        /// Draws this window over the other windows beside it: the highest sibling index any of
        /// them holds, so whatever the container keeps above its windows stays there.
        /// </summary>
        private void BringToFront()
        {
            Transform dialog = window;
            // The window's dialog root is the ancestor whose parent also holds other windows.
            while (dialog != null && dialog.parent != null)
            {
                Transform container = dialog.parent;
                int top = -1;
                foreach (Transform sibling in container)
                {
                    if (sibling != dialog && sibling.GetComponentInChildren<UIWindowDrag>(true) != null)
                        top = Mathf.Max(top, sibling.GetSiblingIndex());
                }
                if (top >= 0)
                {
                    if (top > dialog.GetSiblingIndex())
                        dialog.SetSiblingIndex(top);
                    return;
                }
                if (container.GetComponent<Canvas>() != null && container.GetComponent<Canvas>().isRootCanvas)
                    return;
                dialog = container;
            }
        }

        private static string PathOf(Transform target)
        {
            string path = target.name;
            for (Transform up = target.parent; up != null; up = up.parent)
            {
                if (up.GetComponent<Canvas>() != null && up.GetComponent<Canvas>().isRootCanvas)
                    break;
                path = up.name + "/" + path;
            }
            return path;
        }
    }
}
