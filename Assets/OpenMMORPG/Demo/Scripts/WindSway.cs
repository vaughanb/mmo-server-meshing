using Insthync.ManagedUpdating;
using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// Leans a plant back and forth so the menu's hillside is not dead still.
    ///
    /// **Transform sway, not a wind shader.** A vertex-displacement shader is the better answer in
    /// a world you walk around - it can bend a trunk while leaving its roots planted, and it costs
    /// nothing per instance. But it means a custom shader for every material the Quaternius packs
    /// use, and the demo's whole art rule is that those packs are used as they ship. Here there are
    /// about twenty plants on one screen that nobody stands next to, so rotating each one a degree
    /// or two about its base buys most of the effect for a component and no new shaders. If the
    /// island ever wants wind, that is where a shader would earn its keep.
    ///
    /// The pivot of every one of these models sits at its base, which is what makes the cheap
    /// version work: rotating the object tips it like a plant bending rather than sliding it
    /// sideways like a sticker.
    ///
    /// Two waves, not one. A single sine at a single rate reads as a metronome the moment there is
    /// more than one plant on screen; a slow swell laid over a quicker sway reads as air moving.
    ///
    /// Ticked by the kit's update manager rather than an Update of its own: the menu stage has
    /// a couple of dozen of these.
    /// </summary>
    public class WindSway : BaseManagedUpdateBehaviour
    {
        [Header("Wind")]
        [Tooltip("Compass direction the wind blows towards, in degrees. 90 is to the right of the camera.")]
        [SerializeField]
        private float windYaw = 75f;

        [Header("Sway")]
        [Tooltip("Peak lean of the quick sway, in degrees.")]
        [SerializeField]
        private float degrees = 1.6f;
        [Tooltip("Seconds for one sway.")]
        [SerializeField]
        private float period = 4.2f;
        [Tooltip("Peak lean of the slow swell laid over the sway.")]
        [SerializeField]
        private float gustDegrees = 0.8f;
        [Tooltip("Seconds for one swell.")]
        [SerializeField]
        private float gustPeriod = 11f;

        private Quaternion _rest;
        private Vector3 _axis;
        private float _phase;

        /// <summary>Called by the stage builder, which decides the strength from what the prop is.</summary>
        public void Configure(float windDegrees, float swayDegrees, float swaySeconds,
                              float swellDegrees, float swellSeconds)
        {
            windYaw = windDegrees;
            degrees = swayDegrees;
            period = swaySeconds;
            gustDegrees = swellDegrees;
            gustPeriod = swellSeconds;
        }

        private void Awake()
        {
            _rest = transform.rotation;

            Vector3 wind = Quaternion.Euler(0f, windYaw, 0f) * Vector3.forward;
            // Leaning *towards* the wind means turning about the axis across it. Cross with up
            // rather than naming an axis, so changing the direction needs no other edit.
            _axis = Vector3.Cross(Vector3.up, wind).normalized;

            // Phase from where the plant stands, so neighbours are out of step with each other but
            // the scene looks identical every time it loads - a random phase would make the menu
            // subtly different on every launch and impossible to compare screenshots of.
            Vector3 at = transform.position;
            _phase = (at.x * 0.37f) + (at.z * 0.71f);
        }

        public override void ManagedUpdate()
        {
            // Unscaled: the menu should keep breathing whatever the game does to the timescale.
            float t = Time.unscaledTime;
            float lean = Mathf.Sin(((t / Mathf.Max(0.01f, period)) + _phase) * Mathf.PI * 2f) * degrees;
            lean += Mathf.Sin(((t / Mathf.Max(0.01f, gustPeriod)) + (_phase * 0.31f)) * Mathf.PI * 2f) * gustDegrees;
            transform.rotation = Quaternion.AngleAxis(lean, _axis) * _rest;
        }
    }
}
