using UnityEngine;

namespace MultiplayerARPG.Demo
{
    /// <summary>
    /// The home scene's camera rig: holds the two framings the menu uses and travels between them.
    ///
    /// **It lives on the camera, not on a screen, and that is the whole reason it exists.** The
    /// framings used to be applied by <see cref="DemoCharacterPreviewControl"/> in its own
    /// `OnEnable`/`OnDisable`, which can only ever snap: the moment a character screen is switched
    /// off its `Update` stops, so there is nobody left to carry the camera back. A rig on the
    /// camera is running whenever the scene is, so a move that starts as a screen closes still
    /// finishes.
    ///
    /// It is also the **only** thing that writes the camera's transform. The preview control hands
    /// it a zoom offset rather than moving the camera itself, so the two cannot fight over the same
    /// three numbers on the same frame.
    ///
    /// A scene component, so nothing in the canvas prefab can hold a reference to it (see
    /// [[demo-character-model-is-a-child]] for the same constraint on the model container) - the
    /// preview control finds it through the camera at runtime.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public class DemoMenuCamera : MonoBehaviour
    {
        [Header("Menu framing")]
        [Tooltip("Wide, up the hill, looking down the road. What the login and server screens are composed for.")]
        [SerializeField]
        private Vector3 menuPosition = new Vector3(0f, 3.4f, -5.6f);
        [SerializeField]
        private float menuPitch = 14f;
        [SerializeField]
        private float menuFieldOfView = 55f;

        [Header("Character framing")]
        [Tooltip("Close in on the character standing at the origin.")]
        [SerializeField]
        private Vector3 portraitPosition = new Vector3(0f, 1.55f, -3.4f);
        [SerializeField]
        private float portraitPitch = 6f;
        [SerializeField]
        private float portraitFieldOfView = 60f;

        [Header("Travel")]
        [Tooltip("Seconds to cross between the two framings.")]
        [SerializeField]
        private float travelSeconds = 0.85f;

        private Camera _camera;

        /// <summary>0 is the menu framing, 1 is the character framing. Everything is read off this.</summary>
        private float _at;
        private float _wanted;

        /// <summary>Metres along the character framing's forward, handed over by the preview control.</summary>
        private float _zoom;

        /// <summary>Called by the stage builder, which is the one place both framings are decided.</summary>
        public void Configure(Vector3 menuAt, float menuDown, float menuFov,
                              Vector3 portraitAt, float portraitDown, float portraitFov)
        {
            menuPosition = menuAt;
            menuPitch = menuDown;
            menuFieldOfView = menuFov;
            portraitPosition = portraitAt;
            portraitPitch = portraitDown;
            portraitFieldOfView = portraitFov;
        }

        /// <summary>
        /// How many character screens are up. **Counted here rather than in a static on the
        /// screens themselves**, for two reasons.
        ///
        /// One is ordering: stepping between select and create fires an enable and a disable in
        /// either order, so a screen that released the close framing on its own disable would send
        /// the camera away from a screen that had just asked for it. Counting survives both orders.
        ///
        /// The other is that a static would **not reset** under Unity's fast enter-play-mode, which
        /// leaves domain reload off: stop play while the character screen is up and the count stays
        /// at one, and the next session opens the login screen in the close framing. A field on a
        /// scene component is new every time the scene is.
        /// </summary>
        private int _holders;

        /// <summary>Paired with <see cref="ReleaseCharacterFraming"/>; the camera travels in.</summary>
        public void HoldCharacterFraming()
        {
            ++_holders;
            _wanted = 1f;
        }

        /// <summary>The camera travels back out, once the last screen holding it has gone.</summary>
        public void ReleaseCharacterFraming()
        {
            if (--_holders > 0)
                return;
            _holders = 0;
            _wanted = 0f;
        }

        public void SetZoom(float metres)
        {
            _zoom = metres;
        }

        private void Awake()
        {
            _camera = GetComponent<Camera>();
        }

        private void OnEnable()
        {
            // Start wherever we are wanted rather than travelling on the first frame: the scene
            // opens on the login screen, and a camera that swoops in the moment the game launches
            // reads as a glitch rather than as a transition.
            _at = _wanted;
            Apply();
        }

        private void LateUpdate()
        {
            if (!Mathf.Approximately(_at, _wanted))
            {
                // MoveTowards over a fixed duration rather than an exponential ease, because this
                // is a deliberate move with a length - exponential smoothing approaches forever and
                // has no honest answer to "how long does it take". Unscaled, so a paused or
                // slowed game still lets the menu work.
                _at = Mathf.MoveTowards(_at, _wanted, Time.unscaledDeltaTime / Mathf.Max(0.01f, travelSeconds));
            }
            Apply();
        }

        /// <summary>
        /// One place, every frame, writing position, rotation and field of view together.
        ///
        /// Smoothstepped rather than linear: a linear move starts and stops dead, which on a
        /// camera reads as a jerk at both ends however long it takes. Both framings are a pure
        /// pitch, so the angle can be interpolated as a number - no need for a Slerp, and no risk
        /// of picking up roll along the way.
        ///
        /// The zoom is scaled by the same curve, so it folds away as the camera leaves the
        /// character and there is never a stale offset waiting on the menu framing.
        /// </summary>
        private void Apply()
        {
            if (_camera == null)
                _camera = GetComponent<Camera>();

            float k = Mathf.SmoothStep(0f, 1f, _at);
            float pitch = Mathf.Lerp(menuPitch, portraitPitch, k);
            Vector3 forward = Quaternion.Euler(portraitPitch, 0f, 0f) * Vector3.forward;

            transform.position = Vector3.Lerp(menuPosition, portraitPosition, k) + (forward * (_zoom * k));
            transform.rotation = Quaternion.Euler(pitch, 0f, 0f);
            if (_camera != null)
                _camera.fieldOfView = Mathf.Lerp(menuFieldOfView, portraitFieldOfView, k);
        }
    }
}
