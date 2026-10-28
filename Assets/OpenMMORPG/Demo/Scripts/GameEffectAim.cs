using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// Points part of an effect where the character is shooting rather than where its hand happens
    /// to be turned, and fires that part's bursts once it is pointed.
    ///
    /// A release effect hangs off the bow hand, and the hand's own axes are whatever the rig says:
    /// a spray "forward" out of `hand_l` goes into the archer's thigh. So the directional emitters
    /// live under <see cref="pivot"/>, which is turned to the character's facing, lifted by
    /// <see cref="pitch"/> degrees - level for a shot, steep for a volley loosed into the sky.
    ///
    /// **Why this emits by hand.** The kit plays every particle system under a GameEffect the
    /// moment it is fetched, before it has been told what to follow, and the first simulation step
    /// runs before any script's LateUpdate - so a burst left to the kit would leave pointing
    /// wherever the pooled object last pointed. The aimed systems are therefore built with no
    /// emission of their own, and this turns the pivot and then emits their counts, in that order,
    /// on the first frame the effect knows its character. Its late update runs only until then
    /// (<see cref="WakeableLateUpdateBehaviour"/>).
    /// </summary>
    public class GameEffectAim : WakeableLateUpdateBehaviour
    {
        [Tooltip("Turned to the character's facing; the aimed emitters are under it.")]
        public Transform pivot;

        [Tooltip("Degrees above level.")]
        public float pitch;

        public ParticleSystem[] systems = new ParticleSystem[0];

        public int[] counts = new int[0];

        private GameEffect _effect;
        private bool _pending;

        private void Awake()
        {
            _effect = GetComponent<GameEffect>();
            if (Application.isBatchMode)
                enabled = false;
        }

        private void OnEnable()
        {
            _pending = true;
            Wake();
        }

        public override void ManagedLateUpdate()
        {
            if (!_pending || pivot == null)
            {
                Sleep();
                return;
            }
            Transform target = _effect != null ? _effect.FollowingTarget : null;
            // Not following anything yet: InstantiateEffect sets that just after the fetch.
            if (_effect != null && target == null && !_effect.stayInPlace)
                return;
            _pending = false;
            Sleep();

            Vector3 forward = Facing(target != null ? target : transform);
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            if (right.sqrMagnitude < 1e-4f)
                right = Vector3.right;
            Vector3 aim = Quaternion.AngleAxis(-pitch, right.normalized) * forward;
            pivot.rotation = Quaternion.LookRotation(aim, Vector3.up);

            for (int i = 0; i < systems.Length; ++i)
            {
                if (systems[i] != null && i < counts.Length && counts[i] > 0)
                    systems[i].Emit(counts[i]);
            }
        }

        /// <summary>The level facing of the character the effect is on, or of the effect itself.</summary>
        private static Vector3 Facing(Transform from)
        {
            BaseCharacterModel model = from.GetComponentInParent<BaseCharacterModel>();
            Vector3 forward = model != null ? model.transform.forward : from.forward;
            forward.y = 0f;
            return forward.sqrMagnitude > 1e-4f ? forward.normalized : Vector3.forward;
        }
    }
}
