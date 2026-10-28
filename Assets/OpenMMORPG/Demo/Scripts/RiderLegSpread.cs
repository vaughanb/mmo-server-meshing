using Insthync.ManagedUpdating;
using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// Opens a mounted rider's legs so they straddle the horse instead of passing through it.
    ///
    /// The animation library has no riding clip. Its chair-sitting loop is close — seated,
    /// knees bent — but the thighs are parallel and horizontal, which on a horse puts them
    /// straight through the widest part of the barrel: measured, **24% of the rider's leg
    /// vertices ended up inside the horse's mesh**.
    ///
    /// This is done here, after the animator has posed the skeleton, rather than by editing
    /// the clip, because **the library's clips cannot be re-posed by their muscle curves**.
    /// They carry humanoid IK goal curves (`LeftFootT`/`LeftFootQ` and the right-hand pair),
    /// and the goals win: a derived clip with the thigh muscles rewritten evaluates with the
    /// feet in exactly their original positions and the legs barely moved. Stripping the goal
    /// curves does not help either — without them the pose collapses. Rotating the bones
    /// afterwards sidesteps the whole mechanism, and has the side benefit that the same
    /// adjustment holds for whatever clip is playing.
    ///
    /// Runs in the kit's IK slot (<see cref="DefaultExecutionOrders.GAME_ENTITY_MODEL_IK"/>),
    /// after the animation has been evaluated and before the kit copies bone poses onto
    /// separately-skinned equipment, so the rider's armour follows the opened legs. The riding
    /// model is switched off while its owner is on foot, and this with it.
    ///
    /// Attach to the riding model, beside its <see cref="Animator"/>.
    /// See <see cref="EditorTools.DemoMountBuilder"/>.
    /// </summary>
    [RequireComponent(typeof(Animator))]
    public class RiderLegSpread : MonoBehaviour, IManagedLateUpdate
    {
        [Tooltip("Degrees each thigh is swung outward, away from the horse's spine.")]
        [SerializeField]
        private float abductionDegrees = 35f;

        [Tooltip("Degrees each thigh is dropped from the chair-sitting pose's horizontal, " +
                 "so it lies down the barrel rather than across the top of it.")]
        [SerializeField]
        private float dropDegrees = 45f;

        private Transform _leftThigh;
        private Transform _rightThigh;
        private bool _resolved;

        private void Awake()
        {
            Resolve();
        }

        /// <summary>
        /// Lazy so that <see cref="Apply"/> works on a model the editor has only just
        /// instantiated, where Awake has not run.
        /// </summary>
        private void Resolve()
        {
            if (_resolved)
                return;
            _resolved = true;
            Animator animator = GetComponent<Animator>();
            if (animator == null || !animator.isHuman)
                return;
            _leftThigh = animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
            _rightThigh = animator.GetBoneTransform(HumanBodyBones.RightUpperLeg);
        }

        private void OnEnable()
        {
            if (!Application.isBatchMode)
                UpdateManager.Register(DefaultExecutionOrders.GAME_ENTITY_MODEL_IK, this);
        }

        private void OnDisable()
        {
            UpdateManager.Unregister(DefaultExecutionOrders.GAME_ENTITY_MODEL_IK, this);
        }

        // After the animator has written the pose, so this wins.
        public void ManagedLateUpdate()
        {
            Apply();
        }

        /// <summary>
        /// Public so the editor can pose a model the same way the game will, without
        /// entering play mode — the mount builder measures the rider's seat against it.
        /// </summary>
        public void Apply()
        {
            Resolve();
            Swing(_leftThigh, 1f);
            Swing(_rightThigh, -1f);
        }

        /// <summary>
        /// The thigh points *forward* in a chair-sitting pose, so swinging the knee out to
        /// the side is a turn about the body's up axis — not its forward axis, which would
        /// only twist the leg along its own length and move the knee nowhere.
        /// </summary>
        private void Swing(Transform thigh, float side)
        {
            if (thigh == null)
                return;
            thigh.rotation = Quaternion.AngleAxis(dropDegrees, transform.right)
                           * Quaternion.AngleAxis(-side * abductionDegrees, transform.up)
                           * thigh.rotation;
        }

        public void Configure(float abduction, float drop)
        {
            abductionDegrees = abduction;
            dropDegrees = drop;
        }
    }
}
