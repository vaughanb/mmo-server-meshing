using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// Holds part of an effect a little above whoever wears it: the Hunter's Mark.
    ///
    /// There is no socket for "over the head" that every creature has. The humans have a `Head`
    /// socket and the wildlife do not - a wolf has only `Floor` and `Body` - and the kit skips an
    /// effect whose socket is missing without a word, so a mark on `Head` would never appear on
    /// the thing a ranger most often marks. So the effect rides `Floor`, which everything has, and
    /// this lifts <see cref="pivot"/> to the top of the model's rendered bounds plus
    /// <see cref="clearance"/>, measured once when it lands (a wolf is 0.9 m, a bandit 1.8 m).
    /// Kept upright in the world, whatever the socket's rotation.
    /// </summary>
    public class OverheadEffectPivot : MonoBehaviour
    {
        public Transform pivot;

        [Tooltip("Metres above the top of the model.")]
        public float clearance = 0.45f;

        [Tooltip("Used when there is nothing to measure.")]
        public float fallbackHeight = 1.9f;

        private GameEffect _effect;
        private float _height = -1f;

        private void Awake()
        {
            _effect = GetComponent<GameEffect>();
            if (Application.isBatchMode)
                enabled = false;
        }

        private void OnEnable()
        {
            _height = -1f;
        }

        private void LateUpdate()
        {
            if (pivot == null)
                return;
            if (_height < 0f)
            {
                Transform target = _effect != null ? _effect.FollowingTarget : null;
                if (target == null)
                    return;
                _height = Measure(target);
            }
            pivot.position = transform.position + Vector3.up * _height;
            pivot.rotation = Quaternion.identity;
        }

        private float Measure(Transform target)
        {
            BaseCharacterModel model = target.GetComponentInParent<BaseCharacterModel>();
            if (model == null)
                return fallbackHeight;
            bool any = false;
            float top = 0f;
            foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>())
            {
                // Only the body: particles, nameplates and the effect's own renderers would each
                // lift the mark by however tall they happen to be.
                if (!(renderer is SkinnedMeshRenderer) || !renderer.enabled)
                    continue;
                top = any ? Mathf.Max(top, renderer.bounds.max.y) : renderer.bounds.max.y;
                any = true;
            }
            if (!any)
                return fallbackHeight;
            return Mathf.Max(0.5f, top - transform.position.y) + clearance;
        }
    }
}
