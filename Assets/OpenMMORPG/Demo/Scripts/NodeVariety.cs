using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// Gives a harvestable node its own size, and seats it on the ground it stands on.
    ///
    /// **Size.** A spawn area places its nodes at a position and a rotation, and takes their size
    /// from the prefab - so a wood grown out of spawned entities is a wood in which every
    /// oak is exactly as tall as every other oak, which reads as a copied object rather
    /// than as trees. The scatter this replaced varied the scale of every rock and tree it
    /// put down, and losing that was the one thing the entities were visibly worse at.
    ///
    /// The size is worked out from where the node stands rather than randomed, because
    /// every client builds its own copy of the world: a random number would give the same
    /// tree a different height on each machine, and a player's landmark would not be their
    /// friend's. Position is the one thing all of them agree on, so hashing it gives every
    /// client the same answer without a byte crossing the network.
    ///
    /// It scales the whole entity, collider included, so what the player swings at stays
    /// the size of what they can see.
    ///
    /// **Seating** (2026-10-03: "a lot of boulders are spawning like this", sitting level on a
    /// hillside with their downhill edge in the air). The spawn area stands a node at the exact
    /// ground height of its spot, with only a random spin, so on a slope every node is perfectly
    /// upright with a flat base on ground that is not. The terrain is read here, from the node's own
    /// footprint, and the *model* is settled onto it: leant over to lie on the slope (a boulder, by
    /// <see cref="tiltToGround"/>) and sunk until no part of its base hangs over a gap (a boulder or a
    /// trunk, both). Only the model child moves - the root keeps its collider upright, its hit box
    /// and its aim point - and, like the size, it is arithmetic on the terrain and the position, so
    /// the server and every client agree without a byte on the wire.
    /// </summary>
    public class NodeVariety : MonoBehaviour
    {
        [Tooltip("Smallest and largest this node may come out, as a multiple of the model.")]
        public float smallest = 0.85f;
        public float largest = 1.25f;

        [Tooltip("How far the model leans to lie on the slope under it: 0 stays upright (a tree, a mushroom), 1 lies right on the ground (a boulder).")]
        [Range(0f, 1f)]
        public float tiltToGround = 0f;

        [Tooltip("The steepest lean, in degrees, however steep the ground.")]
        public float maxTilt = 38f;

        [Tooltip("The most the model may be sunk to close a gap under its base, in metres.")]
        public float maxSink = 0.7f;

        [Tooltip("Radius of the base to seat, in metres. 0 uses the collider's own radius.")]
        public float footprint = 0f;

        private void Start()
        {
            transform.localScale = Vector3.one * SizeAt(transform.position);
            SeatOnGround();
        }

        /// <summary>
        /// The size this node comes out at, standing at a position. DemoSceneBuilder asks it
        /// before the node exists, to know how much room it will take.
        /// </summary>
        public float SizeAt(Vector3 position)
        {
            return Mathf.Lerp(smallest, largest, FromPosition(position));
        }

        /// <summary>
        /// A number between 0 and 1 that depends only on where a thing is standing.
        ///
        /// Rounded to a tenth of a metre first: a spawn position that differs in the last
        /// decimal place between a server and a client would otherwise hash to something
        /// else entirely and the node would change size as it was replicated.
        /// </summary>
        private static float FromPosition(Vector3 position)
        {
            int x = Mathf.RoundToInt(position.x * 10f);
            int z = Mathf.RoundToInt(position.z * 10f);
            // A cheap integer hash. The multipliers are odd and coprime so that points on
            // a grid - which spawn positions very often are - do not land on a pattern.
            unchecked
            {
                int hash = x * 73856093 ^ z * 19349663;
                hash = hash & 0x7FFFFFFF;
                return (hash % 10000) / 10000f;
            }
        }

        // ---- seating ---------------------------------------------------------------

        private void SeatOnGround()
        {
            Vector3 foot = transform.position;
            Terrain terrain = TerrainAt(foot);
            if (terrain == null)
                return;

            var body = GetComponent<CapsuleCollider>();
            float scale = Mathf.Max(transform.lossyScale.x, transform.lossyScale.z);
            float radius = footprint > 0f ? footprint * scale : (body != null ? body.radius * scale : 0.6f);
            radius = Mathf.Max(radius, 0.25f);

            // The slope across the footprint: height a radius either side, each way.
            float dhdx = (HeightAt(terrain, foot + Vector3.right * radius) - HeightAt(terrain, foot - Vector3.right * radius)) / (2f * radius);
            float dhdz = (HeightAt(terrain, foot + Vector3.forward * radius) - HeightAt(terrain, foot - Vector3.forward * radius)) / (2f * radius);
            Vector3 ground = new Vector3(-dhdx, 1f, -dhdz).normalized;
            ground = Vector3.RotateTowards(Vector3.up, ground, maxTilt * Mathf.Deg2Rad, 0f);

            Quaternion lean = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(Vector3.up, ground), tiltToGround);
            Vector3 baseNormal = lean * Vector3.up;

            // Where the base plane - through the foot, tilted as the model now is - stands above the
            // terrain around the footprint. Wherever it stands above, that is a gap you could see under
            // the node; lowering the model by the biggest one buries the rest.
            float worstGap = 0f;
            for (int k = 0; k < 8; ++k)
            {
                float angle = k * Mathf.PI * 0.25f;
                float dx = Mathf.Cos(angle) * radius;
                float dz = Mathf.Sin(angle) * radius;
                float plane = foot.y - (baseNormal.x * dx + baseNormal.z * dz) / baseNormal.y;
                float gap = plane - HeightAt(terrain, foot + new Vector3(dx, 0f, dz));
                if (gap > worstGap)
                    worstGap = gap;
            }
            float sink = Mathf.Clamp(worstGap + 0.04f, 0f, maxSink);

            Quaternion turn = lean * transform.rotation;
            Vector3 seat = foot + Vector3.down * sink;
            foreach (Transform child in transform)
            {
                // The model, not the aim point or the hit box. The model is whatever carries renderers.
                if (child.GetComponentInChildren<Renderer>(true) == null)
                    continue;
                child.SetPositionAndRotation(seat, turn);
            }
        }

        /// <summary>The terrain under a point, or null when it is not on one (a node on a roof, say).</summary>
        private static Terrain TerrainAt(Vector3 point)
        {
            foreach (Terrain terrain in Terrain.activeTerrains)
            {
                Vector3 origin = terrain.GetPosition();
                Vector3 size = terrain.terrainData.size;
                if (point.x >= origin.x && point.x <= origin.x + size.x && point.z >= origin.z && point.z <= origin.z + size.z)
                    return terrain;
            }
            return null;
        }

        /// <summary>World height of the terrain at a point. `SampleHeight` is relative to the terrain's own origin.</summary>
        private static float HeightAt(Terrain terrain, Vector3 point)
        {
            return terrain.SampleHeight(point) + terrain.GetPosition().y;
        }
    }
}
