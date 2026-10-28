using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// Lifts the visible model to the water's surface while the character swims.
    ///
    /// The kit decides a character is swimming once its root sits a fixed fraction of its
    /// height below the water (the movement's underWaterThreshold, 0.75 of the capsule)
    /// and holds it there, which suits an upright treading pose: the head stays above the
    /// surface. The demo's swim clips lie the body flat at root height, so at that depth
    /// the whole character is a metre and more under water. The fraction cannot just be
    /// lowered, because the same number decides when wading turns into swimming - at a
    /// small value the character would start to swim in ankle-deep water. So the capsule
    /// keeps the kit's depth, and this lifts only the model, up to a little below the
    /// surface, blending in and out so the change reads as the character floating up.
    ///
    /// The movement state is replicated, so every client sees the same. Added by the demo
    /// builder to the player entities and the horse.
    ///
    /// Runs in the entity's own late update (<see cref="BaseGameEntity.onLateUpdate"/>), which
    /// is before the kit's IK slot, so anything planting the model's limbs sees it lifted. A
    /// headless server, with no model to show, skips it.
    /// </summary>
    public class SurfaceSwimmer : MonoBehaviour
    {
        [Tooltip("The model to lift. Found from the entity's CharacterModelManager, or a child named Model, when empty.")]
        public Transform model;
        [Tooltip("How far below the surface the model's root sits while swimming. Small for a body lying flat; deeper for a mount whose legs should stay under.")]
        public float depthBelowSurface = 0.15f;
        [Tooltip("How fast the model rises and settles, in metres a second.")]
        public float blendSpeed = 4f;

        private CharacterControllerEntityMovement _movement;
        private BaseGameEntity _entity;
        private float _restY;
        private float _lift;

        private void Awake()
        {
            _movement = GetComponent<CharacterControllerEntityMovement>();
            _entity = GetComponent<BaseGameEntity>();
            if (model == null)
            {
                var manager = GetComponent<CharacterModelManager>();
                if (manager != null && manager.MainTpsModel != null)
                    model = manager.MainTpsModel.transform;
            }
            if (model == null)
                model = transform.Find("Model");
            if (model != null)
                _restY = model.localPosition.y;
        }

        private void OnEnable()
        {
            if (_entity != null && !Application.isBatchMode)
                _entity.onLateUpdate += Tick;
        }

        private void OnDisable()
        {
            if (_entity != null)
                _entity.onLateUpdate -= Tick;
        }

        private void Tick(BaseGameEntity entity)
        {
            if (model == null || _movement == null)
                return;
            float target = 0f;
            if ((_movement.MovementState & MovementState.IsUnderWater) != 0 &&
                _movement.Functions != null &&
                _movement.Functions.TryGetWaterSurfacePoint(out Vector3 surface))
            {
                target = Mathf.Max(0f, surface.y - depthBelowSurface - transform.position.y);
            }
            // On dry land, settled: nothing to write.
            if (_lift == 0f && target == 0f)
                return;
            _lift = Mathf.MoveTowards(_lift, target, blendSpeed * Time.deltaTime);
            Vector3 local = model.localPosition;
            local.y = _restY + _lift;
            model.localPosition = local;
        }
    }
}
