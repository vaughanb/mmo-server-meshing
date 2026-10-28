using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// Target ring that follows the playing character's current target.
    ///
    /// This stands in for the kit's <see cref="CharacterTargetIndicator"/>, which polls
    /// <c>GameInstance.PlayingCharacterEntity.GetTargetEntity()</c> every LateUpdate.
    /// During a warp the client disconnects and the entity's network identity is
    /// unregistered before the entity itself is destroyed, so for a frame the entity
    /// still exists but its <c>Manager</c> is null and GetTargetEntity throws a
    /// NullReferenceException. The kit component cannot be patched from the demo (its
    /// LateUpdate is private and Core mirrors upstream), so the demo carries its own copy
    /// with the guards. Serialized field names match the kit component so the controller
    /// builder can copy the template's settings straight across.
    /// </summary>
    public class SafeCharacterTargetIndicator : MonoBehaviour
    {
        public enum FollowTargetMode
        {
            FollowPosition,
            FollowTopBound,
        }

        public GameObject indicatorPrefab;
        public FollowTargetMode followTargetMode;
        public Vector3 offsets;
        public bool followTargetRotation;

        private GameObject _indicatorObject;
        private BaseGameEntity _currentTarget;
        private Collider2D[] _colliders2D;
        private Collider[] _colliders;

        private void Start()
        {
            if (indicatorPrefab != null)
                _indicatorObject = Instantiate(indicatorPrefab);
        }

        private void OnDestroy()
        {
            if (_indicatorObject != null)
                Destroy(_indicatorObject);
            indicatorPrefab = null;
            _indicatorObject = null;
            _currentTarget = null;
            _colliders2D = null;
            _colliders = null;
        }

        private void LateUpdate()
        {
            if (_indicatorObject == null)
                return;

            BaseGameEntity target = ResolveTarget();
            if (_currentTarget != target)
            {
                _currentTarget = target;
                if (_currentTarget != null)
                {
                    _colliders2D = _currentTarget.GetComponentsInChildren<Collider2D>();
                    _colliders = _currentTarget.GetComponentsInChildren<Collider>();
                }
            }

            if (_currentTarget == null)
            {
                _indicatorObject.SetActive(false);
                return;
            }

            if (_currentTarget is DamageableEntity damageable && damageable.IsDead())
            {
                _indicatorObject.SetActive(false);
                return;
            }

            Vector3 position = _currentTarget.transform.position;
            if (followTargetMode == FollowTargetMode.FollowTopBound)
            {
                if (GameInstance.Singleton != null && GameInstance.Singleton.DimensionType == DimensionType.Dimension2D)
                {
                    if (_colliders2D != null)
                    {
                        for (int i = 0; i < _colliders2D.Length; ++i)
                        {
                            if (_colliders2D[i] == null)
                                continue;
                            float top = _colliders2D[i].bounds.center.y + _colliders2D[i].bounds.extents.y;
                            if (position.y < top)
                                position.y = top;
                        }
                    }
                }
                else if (_colliders != null)
                {
                    for (int i = 0; i < _colliders.Length; ++i)
                    {
                        if (_colliders[i] == null)
                            continue;
                        float top = _colliders[i].bounds.center.y + _colliders[i].bounds.extents.y;
                        if (position.y < top)
                            position.y = top;
                    }
                }
            }

            _indicatorObject.transform.position = position + offsets;
            if (followTargetRotation)
                _indicatorObject.transform.rotation = _currentTarget.transform.rotation;
            _indicatorObject.SetActive(true);
        }

        /// <summary>
        /// The playing character's target, or null whenever the entity is missing, dying,
        /// or no longer registered with a network manager (the warp frame).
        /// </summary>
        private static BaseGameEntity ResolveTarget()
        {
            BasePlayerCharacterEntity player = GameInstance.PlayingCharacterEntity;
            if (player == null || player.Manager == null || player.Manager.Assets == null)
                return null;
            return player.GetTargetEntity();
        }
    }
}
