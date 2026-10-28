using UnityEngine;
using UnityEngine.EventSystems;

namespace MultiplayerARPG.Demo
{
    /// <summary>
    /// Asks the camera rig for the close framing while a character screen is up, and lets the
    /// player drag to turn the character and scroll to move in and out.
    ///
    /// **It does not move the camera.** It asks <see cref="DemoMenuCamera"/> to, and hands it a
    /// zoom offset. That rig lives on the camera rather than on a screen, so the travel between
    /// the two framings still finishes after this screen has gone - which is the one thing a
    /// component sitting here could never do, because its `Update` stops the moment the screen is
    /// switched off. It also means exactly one script writes the camera transform.
    ///
    /// Why two framings at all: all six home screens share one camera and one lens cannot do both
    /// jobs. A portrait framing is what a character needs, and from there the hill road behind runs
    /// away to nothing - the whole descent compresses into about 9% of the frame's height.
    ///
    /// Lives on a full-screen transparent graphic inside the character select and create screens,
    /// as their **first** child. That position is the whole trick for hit testing: uGUI delivers a
    /// pointer event to the topmost graphic under the cursor, and later siblings draw on top - so
    /// the panels down the sides swallow their own drags and this only sees the ones that land on
    /// the character. Nothing needs to ask "is the pointer over UI".
    ///
    /// Implemented with the event interfaces rather than by polling `Input` for the same reason,
    /// and because they cost nothing when the screen is off, follow the kit's own EventSystem
    /// (`GameInstance` requires an `EventSystemManager`, which is why the home scene contains no
    /// EventSystem of its own), and treat a touch drag as a drag for free.
    ///
    /// The model container is read from whichever kit screen this sits under - `UICharacterList`
    /// and `UICharacterCreate` each expose `characterModelContainer` as a public field. It is a
    /// **scene** object assigned per instance (see [[demo-character-model-is-a-child]]), so it
    /// cannot be wired into the canvas prefab and has to be found at runtime. The rig is found the
    /// same way, and for the same reason.
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    public class DemoCharacterPreviewControl : MonoBehaviour, IDragHandler, IScrollHandler
    {
        [Header("Control")]
        [Tooltip("Whether the player may turn and zoom here. Off still holds the close framing and " +
                 "still resets both to default; it just ignores the mouse.")]
        [SerializeField]
        private bool allowPlayerControl = true;

        [Header("Turning")]
        [Tooltip("Degrees the character turns per pixel of horizontal drag.")]
        [SerializeField]
        private float degreesPerPixel = 0.35f;

        [Header("Zoom")]
        [Tooltip("Metres the camera moves per notch of the scroll wheel.")]
        [SerializeField]
        private float metresPerNotch = 0.35f;
        [Tooltip("How far the camera may close on the character, in metres from its resting place.")]
        [SerializeField]
        private float closest = 1.15f;
        [Tooltip("Higher is snappier. The wheel is a coarse input, so the move is smoothed rather than stepped.")]
        [SerializeField]
        private float zoomSharpness = 12f;

        private Transform _container;
        private DemoMenuCamera _rig;

        private bool _containerRestCaptured;
        private Quaternion _containerRest;

        private float _zoom;
        private float _targetZoom;

        /// <summary>
        /// Set by the stage builder, per screen.
        ///
        /// **Both character screens still carry this component**, because it is what asks the rig
        /// for the close framing - drop it from the selection screen and that screen inherits the
        /// wide menu lens. What differs is only whether the mouse does anything: on selection the
        /// character is a row you are flipping through, so it is always shown square-on at the
        /// resting distance; on creation it is the thing you are making, so you can turn it.
        /// </summary>
        public void SetPlayerControl(bool allowed)
        {
            allowPlayerControl = allowed;
        }

        private void OnEnable()
        {
            Resolve();
            // Each visit starts square-on at the resting distance; a turn the player left behind
            // on another screen is not a setting they chose.
            _zoom = 0f;
            _targetZoom = 0f;
            RestoreContainer();
            if (_rig != null)
            {
                _rig.SetZoom(0f);
                // Balanced with the release in OnDisable. The rig counts the holders, so both
                // character screens can ask for the close framing and it only travels back out
                // when the last of them has gone, in whichever order the kit switches them.
                _rig.HoldCharacterFraming();
            }
        }

        private void OnDisable()
        {
            RestoreContainer();
            if (_rig != null)
                _rig.ReleaseCharacterFraming();
        }

        /// <summary>
        /// Finds the camera **in this object's own scene**, falling back to `Camera.main`.
        ///
        /// `Camera.main` alone is wrong here. It returns the first active camera tagged MainCamera
        /// across *every* loaded scene, and the boot scene has one of its own - so with both loaded
        /// it hands back the wrong camera, and this drives a camera nobody is looking through.
        /// Caught by reading the camera's forward vector: it came back as (0,0,1) when this menu's
        /// camera is pitched down.
        /// </summary>
        private Camera FindSceneCamera()
        {
            UnityEngine.SceneManagement.Scene scene = gameObject.scene;
            if (scene.IsValid())
            {
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    Camera found = root.GetComponentInChildren<Camera>(true);
                    if (found != null)
                        return found;
                }
            }
            return Camera.main;
        }

        private void Resolve()
        {
            if (_rig == null)
            {
                Camera camera = FindSceneCamera();
                if (camera != null)
                    _rig = camera.GetComponent<DemoMenuCamera>();
            }
            if (_container == null)
            {
                UICharacterList list = GetComponentInParent<UICharacterList>();
                if (list != null && list.characterModelContainer != null)
                {
                    _container = list.characterModelContainer;
                }
                else
                {
                    UICharacterCreate create = GetComponentInParent<UICharacterCreate>();
                    if (create != null && create.characterModelContainer != null)
                        _container = create.characterModelContainer;
                }
            }
            if (_container != null && !_containerRestCaptured)
            {
                // Captured once and kept. Re-reading it on a later enable would eventually record
                // a *turned* character as square-on - all it takes is one enable landing before
                // the previous screen's disable - and the model would drift a little further every
                // time the player stepped between select and create.
                _containerRest = _container.rotation;
                _containerRestCaptured = true;
            }
        }

        private void RestoreContainer()
        {
            if (_containerRestCaptured && _container != null)
                _container.rotation = _containerRest;
        }

        private void Update()
        {
            if (_rig == null)
                return;
            // No "already at the target, skip" guard. The smoothing only ever *approaches* the
            // target, so a guard like that decides the move is finished while the camera is still
            // short of where it should be, and whether it ever arrives depends on the frame rate.
            // One float a frame is not worth defending against.
            _zoom = Mathf.Lerp(_zoom, _targetZoom, 1f - Mathf.Exp(-zoomSharpness * Time.unscaledDeltaTime));
            _rig.SetZoom(_zoom);
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (!allowPlayerControl)
                return;
            Resolve();
            if (_container == null)
                return;
            // Negative, so that dragging right pushes the character's near side to the right - the
            // way a turntable would go under your hand. Rotating by the positive delta spins it
            // the other way and reads as the model resisting the drag.
            _container.Rotate(Vector3.up, -eventData.delta.x * degreesPerPixel, Space.World);
        }

        public void OnScroll(PointerEventData eventData)
        {
            if (!allowPlayerControl)
                return;
            // **The resting distance is the limit, not the middle.** Zoom only ever goes in.
            //
            // There used to be a matching `furthest` allowance of 1.6m, on the assumption that a
            // preview should work like a game camera and pull back as well as push in. It cannot
            // here: the close framing is already composed to the edges of what exists. Draw back
            // from it and the set runs out - the title band appears across the top, and past that
            // the painted backdrop's own top edge. There is nothing out there to see, so the
            // floor is 0.
            _targetZoom = Mathf.Clamp(_targetZoom + (eventData.scrollDelta.y * metresPerNotch), 0f, closest);
        }
    }
}
