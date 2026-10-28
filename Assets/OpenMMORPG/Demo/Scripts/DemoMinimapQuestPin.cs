using UnityEngine;

namespace MultiplayerARPG.Demo
{
    /// <summary>
    /// Keeps an NPC's "ready to hand in" minimap marker on the minimap when the NPC is off it,
    /// pinned to the edge in the NPC's direction.
    ///
    /// The demo's minimap is a camera filming a 40m square around the player (see
    /// `DemoMinimapBuilder`), and a marker is a canvas standing on the NPC, so anything more
    /// than 20m away is simply not in the picture. That is exactly when a player needs it: the
    /// venison is inland past the rocks, the innkeeper who wants it is in the village, and the
    /// minimap showed nothing that said where to take it (2026-09-24).
    ///
    /// Only the hand-in state is pinned. A new-quest marker on the edge would point at every
    /// quest giver in the village at once, which is clutter rather than direction; a quest in
    /// progress is going on out in the world, not at the NPC.
    ///
    /// Lives on `NpcMiniMapCanvas.prefab` beside the kit's `NpcQuestIndicator`, whose
    /// hand-in object it watches. It moves the canvas only; the NPC and everything the
    /// gameplay camera sees stay put, because the canvas is on the MiniMap layer, which only
    /// the minimap camera renders.
    /// </summary>
    [DisallowMultipleComponent]
    public class DemoMinimapQuestPin : MonoBehaviour
    {
        public NpcQuestIndicator indicator;
        [Tooltip("How far in from the minimap's edge a pinned marker sits, in metres. About " +
                 "half the marker, so it sits wholly inside the frame.")]
        public float edgeInset = 3.5f;

        private static Camera s_minimapCamera;
        private static Camera[] s_cameras = new Camera[8];
        private static float s_nextSearch;
        private Vector3 _homeLocalPosition;
        private bool _moved;

        private void Awake()
        {
            if (indicator == null)
                indicator = GetComponent<NpcQuestIndicator>();
            _homeLocalPosition = transform.localPosition;
        }

        private void LateUpdate()
        {
            Transform parent = transform.parent;
            if (parent == null)
                return;
            GameObject handIn = indicator != null ? indicator.haveTasksDoneQuestsIndicator : null;
            if (handIn == null || !handIn.activeSelf)
            {
                // Nothing to hand in: the marker rides the NPC where it was put, and only needs
                // putting back if it was pinned away from there.
                if (_moved)
                {
                    transform.localPosition = _homeLocalPosition;
                    _moved = false;
                }
                return;
            }
            Vector3 home = parent.TransformPoint(_homeLocalPosition);
            Vector3 position = home;
            Camera minimap = MinimapCamera();
            if (minimap != null && minimap.orthographic)
            {
                // The minimap camera looks straight down with world +Z up the frame, so its view
                // is an axis-aligned rectangle on the ground around its own position.
                Vector3 centre = minimap.transform.position;
                float halfX = Mathf.Max(0.5f, minimap.orthographicSize * minimap.aspect - edgeInset);
                float halfZ = Mathf.Max(0.5f, minimap.orthographicSize - edgeInset);
                float dx = home.x - centre.x;
                float dz = home.z - centre.z;
                float over = Mathf.Max(Mathf.Abs(dx) / halfX, Mathf.Abs(dz) / halfZ);
                if (over > 1f)
                {
                    position.x = centre.x + dx / over;
                    position.z = centre.z + dz / over;
                }
            }
            transform.position = position;
            _moved = true;
        }

        /// <summary>The camera that renders this marker's layer and nothing else.</summary>
        private Camera MinimapCamera()
        {
            if (s_minimapCamera != null && s_minimapCamera.isActiveAndEnabled)
                return s_minimapCamera;
            s_minimapCamera = null;
            // Looked for at most twice a second, into a buffer kept between calls: with the
            // minimap closed there is no camera to find, and `Camera.allCameras` allocated a new
            // array every frame for every pin while a hand-in was waiting.
            if (Time.unscaledTime < s_nextSearch)
                return null;
            s_nextSearch = Time.unscaledTime + 0.5f;
            if (s_cameras.Length < Camera.allCamerasCount)
                s_cameras = new Camera[Camera.allCamerasCount];
            int count = Camera.GetAllCameras(s_cameras);
            int mask = 1 << gameObject.layer;
            for (int i = 0; i < count; ++i)
            {
                if (s_cameras[i].cullingMask == mask)
                {
                    s_minimapCamera = s_cameras[i];
                    break;
                }
            }
            System.Array.Clear(s_cameras, 0, count);
            return s_minimapCamera;
        }
    }
}
