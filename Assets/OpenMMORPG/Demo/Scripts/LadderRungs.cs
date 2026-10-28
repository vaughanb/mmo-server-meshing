using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// Where a ladder's rungs are, for <see cref="LadderLimbIK"/> to put hands and feet on
    /// them. Sits beside the kit's <see cref="Ladder"/>, whose climb line the rungs are
    /// measured against: each rung is a distance along that line from its bottom anchor,
    /// and all of them sit the same distance behind it (toward whatever the ladder leans on;
    /// the line runs just in front of the rungs so the climber's capsule clears them).
    /// Written by DemoSceneBuilder from the ladder model's measurements.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Ladder))]
    public class LadderRungs : MonoBehaviour
    {
        [Tooltip("Each rung's centre, as a distance along the climb line from the ladder's bottom anchor, lowest first.")]
        public float[] rungs = new float[0];

        [Tooltip("How far behind the climb line the rung centres sit, in metres.")]
        public float depth = 0.084f;

        [Tooltip("The rungs' radius, in metres.")]
        public float radius = 0.037f;

        [Tooltip("Half the rungs' length, in metres: how far either side of the climb line a hand or foot can be placed.")]
        public float halfWidth = 0.25f;

        private Ladder _ladder;

        public Ladder Ladder
        {
            get
            {
                if (_ladder == null)
                    _ladder = GetComponent<Ladder>();
                return _ladder;
            }
        }

        public int Count => rungs != null ? rungs.Length : 0;

        /// <summary>Where a point this far along the climb line is, in the world.</summary>
        public float Along(Vector3 worldPoint)
        {
            return Vector3.Dot(worldPoint - Ladder.bottomTransform.position, Ladder.Up);
        }

        /// <summary>The rung's centre, in the world.</summary>
        public Vector3 Centre(int index)
        {
            return Ladder.bottomTransform.position + Ladder.Up * rungs[index] - Ladder.ForwardWithYAngleOffsets * depth;
        }

        /// <summary>
        /// The rung nearest to a point this far along the line, and how far off it is
        /// (positive above). -1 when there are none.
        /// </summary>
        public int Nearest(float along, out float offset)
        {
            int best = -1;
            offset = 0f;
            for (int i = 0; i < Count; ++i)
            {
                float d = along - rungs[i];
                if (best < 0 || Mathf.Abs(d) < Mathf.Abs(offset))
                {
                    best = i;
                    offset = d;
                }
            }
            return best;
        }

        private void OnDrawGizmosSelected()
        {
            if (Ladder == null || Ladder.bottomTransform == null || Ladder.topTransform == null)
                return;
            Gizmos.color = Color.yellow;
            Vector3 right = Ladder.RightWithYAngleOffsets * 0.27f;
            for (int i = 0; i < Count; ++i)
            {
                Vector3 c = Centre(i);
                Gizmos.DrawLine(c - right, c + right);
            }
        }
    }
}
