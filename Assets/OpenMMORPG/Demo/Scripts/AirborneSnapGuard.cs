using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// Stops the kit's ground snap from cutting a jump short.
    ///
    /// While a character is not rising, `CharacterControllerEntityMovement.GetSnapToGroundMotion`
    /// raycasts down by `groundSnapDistance` and, when it finds ground that is not perfectly
    /// level, adds the whole distance to that frame's motion. It is there to keep a walker
    /// glued to a downhill slope, and it does that job. But the template sets it to 2 m, and a
    /// jump rises exactly 2 m (`jumpHeight`): the frame the character tips over the apex the
    /// ground is inside the snap range, and the character is **moved to the floor in one
    /// frame**. On the island's terrain, whose normals are almost never exactly up, every jump
    /// went up smoothly, jerked to the ground from the top, and then played the landing clip -
    /// which starts in the same airborne pose as the jump, so it read as the animation playing
    /// twice. (Logged 2026-10-04: y 1.97 to 0.00 between two frames, MovementState flat
    /// `None` all the way up, the landing clip starting that frame.)
    ///
    /// The snap is only wanted while the character is on the ground, so it is switched off
    /// for as long as the movement state says the character is airborne.
    ///
    /// **It stays off for <see cref="settleTime"/> after the state turns grounded.** The kit
    /// calls a character grounded once the ground is within 0.42 m of its feet (the ground
    /// and airborne checks are one sphere reaching that far), well before it has touched
    /// down. Put the snap back at that moment and it pulls the last 0.3-0.4 m of every
    /// landing in at once - a smaller version of the same jerk, logged as a 0.34 m step at
    /// the frame the landing clip started. A landing is followed by the kit's movement pause
    /// anyway, so nothing is walking on a slope in that window.
    ///
    /// A shorter snap distance would also stop the jerk, but it would still pull the last
    /// stretch of every fall in at once and would change how walking off a ledge behaves;
    /// this changes neither. Walking down a slope never reaches the airborne state, so the
    /// snap is on for all of it.
    ///
    /// Added by the demo builder to the player entities. Core is untouched: the field is
    /// public and the kit reads it fresh every frame. Ticks on the entity's own update
    /// (<see cref="BaseGameEntity.onUpdate"/>) rather than a Unity one of its own.
    /// </summary>
    [RequireComponent(typeof(CharacterControllerEntityMovement))]
    public class AirborneSnapGuard : MonoBehaviour
    {
        [Tooltip("How long the snap stays off after the character counts as grounded again. Longer than the slowest fall through the kit's 0.42 m grounded range (a step off a low ledge, about 0.25 s).")]
        public float settleTime = 0.35f;

        private CharacterControllerEntityMovement _movement;
        private BaseGameEntity _entity;
        private float _authoredDistance;
        private float _suppressedUntil;
        private bool _suppressed;

        private void Awake()
        {
            _movement = GetComponent<CharacterControllerEntityMovement>();
            _entity = GetComponent<BaseGameEntity>();
            if (_movement != null)
                _authoredDistance = _movement.groundSnapDistance;
        }

        private void OnEnable()
        {
            if (_entity != null)
                _entity.onUpdate += Tick;
        }

        private void Tick(BaseGameEntity entity)
        {
            if (_movement == null || _movement.Functions == null)
                return;
            float now = Time.time;
            if (!_movement.MovementState.Has(MovementState.IsGrounded))
                _suppressedUntil = now + settleTime;
            bool suppress = now < _suppressedUntil;
            if (suppress == _suppressed)
                return;
            _suppressed = suppress;
            _movement.groundSnapDistance = suppress ? 0f : _authoredDistance;
        }

        private void OnDisable()
        {
            if (_entity != null)
                _entity.onUpdate -= Tick;
            if (_movement != null && _suppressed)
                _movement.groundSnapDistance = _authoredDistance;
            _suppressed = false;
            _suppressedUntil = 0f;
        }
    }
}
