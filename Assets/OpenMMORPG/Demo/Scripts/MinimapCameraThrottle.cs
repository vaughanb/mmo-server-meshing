using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// Renders the minimap camera a number of times per second instead of every frame.
    ///
    /// The minimap is a small orthographic camera drawing into a 256 x 256 texture: a baked picture of the
    /// map and the marker icons of whoever is near. It costs a full camera render - culling, setup and a
    /// submit - which on the island measured about **1 ms of main-thread time per frame**, to repaint a
    /// picture that moves a couple of texels between frames. At 20 renders a second the map still keeps up
    /// with a running character to within about two texels, and the cost falls by three quarters at 90 fps.
    ///
    /// It works by switching the camera off between renders: a disabled camera is skipped by the render
    /// pipeline, and its target texture keeps the last picture, which is what the minimap UI keeps showing.
    /// Nothing in the kit toggles this camera, and it never exists on a map server.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera))]
    public class MinimapCameraThrottle : MonoBehaviour
    {
        [Tooltip("How many times a second the minimap is redrawn. 0 or less draws it every frame.")]
        [SerializeField]
        private float rendersPerSecond = 20f;

        private Camera _camera;
        private float _next;

        private void Awake()
        {
            _camera = GetComponent<Camera>();
        }

        private void OnEnable()
        {
            _next = 0f;
        }

        private void OnDisable()
        {
            // Left on, so a prefab or a scene saved while this is running holds a working camera.
            if (_camera != null)
                _camera.enabled = true;
        }

        private void LateUpdate()
        {
            if (rendersPerSecond <= 0f)
            {
                _camera.enabled = true;
                return;
            }
            float now = Time.unscaledTime;
            bool due = now >= _next;
            if (due)
            {
                float interval = 1f / rendersPerSecond;
                // Steps by whole intervals so the rate holds, but a long frame must not be followed
                // by a burst of catch-up renders: when behind by more than one, start over from now.
                _next = now - _next > interval ? now + interval : _next + interval;
            }
            // The camera is switched on for the frame it is due and off for the rest, so it is
            // drawn once per interval, in the same frame's render, after the follow script has placed it.
            _camera.enabled = due;
        }
    }
}
