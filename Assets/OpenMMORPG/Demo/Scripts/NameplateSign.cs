using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// Holds a character's world-space sign - the safe-area shield, the vending title - a
    /// fixed height over its nameplate.
    ///
    /// The kit spawns these signs as children of the entity root, at its feet, and the
    /// safe-area one relies on the kit's `FollowBone` to lift it 1.5m over the head bone. That
    /// component looks for the Animator in its **parents**; in the demo the model is a child
    /// of the entity (see `SeatAwarePlayableCharacterModel`), so it finds none, gives up, and the sign stays
    /// on the ground. The vending sign has nothing to lift it at all. Both were invisible
    /// until the village became a safe area, and then every player in town had a blue shield
    /// lying at their feet.
    ///
    /// The nameplate is the kit's own anchor for a character's UI (`CharacterUiTransform`),
    /// and it already follows mounting, so the sign follows it rather than a bone. Runs last,
    /// so nothing moves it afterwards in the same frame. Put it on the sign's root; it is
    /// the root the billboard turns, so the sign still faces the camera from up there.
    /// See <see cref="EditorTools.DemoFeedbackBuilder"/>.
    /// </summary>
    [DefaultExecutionOrder(int.MaxValue)]
    public class NameplateSign : MonoBehaviour
    {
        [Tooltip("World-space offset from the character's nameplate anchor.")]
        public Vector3 offset = new Vector3(0f, 0.5f, 0f);

        private BaseCharacterEntity _entity;

        private void Awake()
        {
            _entity = GetComponentInParent<BaseCharacterEntity>();
        }

        private void LateUpdate()
        {
            if (_entity == null)
                return;
            Transform anchor = _entity.CharacterUiTransform;
            if (anchor != null)
                transform.position = anchor.position + offset;
        }
    }
}
