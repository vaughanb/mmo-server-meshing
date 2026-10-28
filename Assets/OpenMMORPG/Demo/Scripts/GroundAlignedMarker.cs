using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// Lays the click-to-move destination ring along the ground under it instead of level.
    ///
    /// The kit only ever moves the marker to where the click landed. Level, a ring on a hill
    /// is half buried in the slope; the island is mostly hills. So whenever the marker moves,
    /// this looks straight down through it for the ground - with the kit's own mask for what
    /// a character stands on, so it never lands on the character walking in - and tilts it to
    /// match. The ring itself sits a few centimetres up its own local up, clear of the surface.
    /// See <see cref="EditorTools.DemoFeedbackBuilder"/>.
    /// </summary>
    public class GroundAlignedMarker : MonoBehaviour
    {
        private Vector3 _placedAt = new Vector3(float.NaN, float.NaN, float.NaN);

        private void LateUpdate()
        {
            Vector3 position = transform.position;
            if (position == _placedAt)
                return;
            _placedAt = position;
            int mask = GameInstance.Singleton != null
                ? GameInstance.Singleton.GetGameEntityGroundDetectionLayerMask()
                : Physics.DefaultRaycastLayers;
            transform.rotation = Physics.Raycast(position + Vector3.up, Vector3.down, out RaycastHit hit, 2f, mask, QueryTriggerInteraction.Ignore)
                ? Quaternion.FromToRotation(Vector3.up, hit.normal)
                : Quaternion.identity;
        }
    }
}
