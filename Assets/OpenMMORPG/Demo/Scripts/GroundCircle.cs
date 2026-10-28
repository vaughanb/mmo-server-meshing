using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// A textured circle laid over the ground, following it: the ring an area skill shows under
    /// the cursor while it is aimed, and the disc it leaves where it lands.
    ///
    /// **Aiming.** The kit already has the hook: every area skill carries a `targetObjectPrefab`,
    /// which `DefaultAreaSkillAimController` instantiates when aiming starts, moves to the aimed
    /// point every frame and destroys on the click. The demo never gave one to any skill, so
    /// pressing Volley or Meteor showed nothing at all until the click. DemoSkillBuilder now builds
    /// one per aimed skill, with this as its one component, pulsing so it reads as live.
    ///
    /// **Landing.** The area entity's disc was a flat quad laid level at the aim point, and on the
    /// island's hills half of it sank into the slope - a straight edge cut across the circle where
    /// the ground rose through it. The same draping fixes it; the landing disc does not pulse.
    ///
    /// **Why a draped mesh rather than a decal.** URP's decal projector would do this for nothing,
    /// but it needs a renderer feature, and the renderer is the project's own asset rather than the
    /// demo's - a demo that relied on one would draw no circle at all in anybody else's project. So
    /// whenever the circle moves, each vertex is dropped onto the ground under it, with the kit's
    /// own mask for what an area skill lands on: it lies across terrain, floors and rooftops, never
    /// across a character standing inside it. Only moving costs anything - a circle at rest casts
    /// no rays, and a landing disc drapes once.
    ///
    /// **The rim is the true radius.** The texture's bright ring sits at <see cref="rimAt"/> of
    /// the way out, and the disc is sized so that ring lands on <see cref="radius"/> - the number
    /// the area entity's trigger is built from - with the glow running a little past it.
    ///
    /// **Frost.** Frost Nova's frost is the same thing with a lifetime: it spreads from its middle
    /// (<see cref="growSeconds"/>, draped again each frame while it grows), stands, and fades
    /// (<see cref="holdSeconds"/>, <see cref="fadeSeconds"/>, or <see cref="FadeOut"/> for frost
    /// that lasts as long as something else does).
    ///
    /// Its late update runs while it is visible and stops once it has faded out
    /// (<see cref="WakeableLateUpdateBehaviour"/>). It cannot stop sooner: a circle can follow
    /// something that moves - a crippled enemy's ring walks with them - and is re-draped wherever
    /// it goes, so even one standing still has to look each frame.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class GroundCircle : WakeableLateUpdateBehaviour
    {
        [Tooltip("The skill's area radius in metres: where the rim is drawn.")]
        public float radius = 4f;
        [Tooltip("Where the texture's rim peaks, as a share of the way from its middle to its edge.")]
        [Range(0.1f, 1f)]
        public float rimAt = 0.9f;
        [Tooltip("Vertices around each ring.")]
        [Min(8)]
        public int segments = 48;
        [Tooltip("Rings of vertices from the centre out. More follows bumpier ground more closely.")]
        [Min(1)]
        public int rings = 8;
        [Tooltip("How far the circle floats over the ground, so it is not lost in it between vertices.")]
        public float lift = 0.12f;
        [Tooltip("How far above and below its neighbour each vertex looks for ground.")]
        public float reach = 3f;
        [Tooltip("Brightness pulses a second.")]
        public float pulseRate = 1.1f;
        [Tooltip("How far the brightness dips on each pulse. Zero holds it steady.")]
        [Range(0f, 1f)]
        public float pulseDepth = 0f;

        [Header("Over time")]
        [Tooltip("Seconds to spread from its middle to full size once it appears. Zero appears whole.")]
        public float growSeconds = 0f;
        [Tooltip("Seconds it stands at full strength once grown, before fading. Negative stands until FadeOut is called.")]
        public float holdSeconds = -1f;
        [Tooltip("Seconds it takes to fade away.")]
        public float fadeSeconds = 0.5f;

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        private Mesh _mesh;
        /// <summary>Each vertex's offset from the centre on the level; its height is found under it.</summary>
        private Vector3[] _offsets;
        private Vector3[] _vertices;
        private float[] _heights;
        private readonly RaycastHit[] _hits = new RaycastHit[8];
        private Vector3 _drapedAt = new Vector3(float.NaN, float.NaN, float.NaN);
        private float _drapedScale = -1f;
        private MeshRenderer _renderer;
        private MaterialPropertyBlock _block;
        private Color _colour = Color.white;
        private float _age;
        private float _fadeFrom = -1f;
        private float _fadeLength;
        private bool _tinted;

        private void Awake()
        {
            // A dedicated server spawns the landing disc with its area and never draws it.
            if (Application.isBatchMode)
            {
                enabled = false;
                return;
            }
            _renderer = GetComponent<MeshRenderer>();
            Material material = _renderer.sharedMaterial;
            if (material != null && material.HasProperty(BaseColorId))
                _colour = material.GetColor(BaseColorId);
            BuildMesh();
        }

        private void OnDestroy()
        {
            if (_mesh != null)
                Destroy(_mesh);
        }

        /// <summary>Pooled with its effect, so it starts over each time it is switched on.</summary>
        private void OnEnable()
        {
            _age = 0f;
            _fadeFrom = -1f;
            _drapedAt = new Vector3(float.NaN, float.NaN, float.NaN);
            _drapedScale = -1f;
            if (_renderer != null)
                _renderer.enabled = true;
            Wake();
        }

        /// <summary>
        /// Fades it away over <paramref name="seconds"/> from now, if it is not already going: how
        /// frost that stands until told to goes when its freeze ends.
        /// </summary>
        public void FadeOut(float seconds)
        {
            if (_fadeFrom >= 0f)
                return;
            _fadeFrom = _age;
            _fadeLength = Mathf.Max(0.01f, seconds);
        }

        /// <summary>
        /// After the aim controller has moved it, which it does in `Update`, so the circle is draped
        /// where it is drawn this frame rather than where it was the last.
        /// </summary>
        public override void ManagedLateUpdate()
        {
            // The mesh is written in offsets from the centre on the level, so it must not turn -
            // and a landing disc's parent does: the kit spawns an area facing its caster.
            if (transform.rotation != Quaternion.identity)
                transform.rotation = Quaternion.identity;
            _age += Time.deltaTime;
            float scale = growSeconds > 0f ? Spread(Mathf.Clamp01(_age / growSeconds)) : 1f;
            Vector3 centre = transform.position;
            if (centre != _drapedAt || scale != _drapedScale)
            {
                _drapedAt = centre;
                _drapedScale = scale;
                Drape(centre, scale);
            }

            float opacity = Opacity();
            if (opacity <= 0f)
            {
                // Gone until the pool hands it out again, which switches it back on.
                _renderer.enabled = false;
                Sleep();
                return;
            }
            if (pulseDepth > 0f || opacity < 1f || _tinted)
                Paint(opacity);
        }

        /// <summary>Full strength, then down to nothing over whichever fade applies.</summary>
        private float Opacity()
        {
            if (_fadeFrom < 0f && holdSeconds >= 0f && _age >= growSeconds + holdSeconds)
            {
                _fadeFrom = growSeconds + holdSeconds;
                _fadeLength = Mathf.Max(0.01f, fadeSeconds);
            }
            if (_fadeFrom < 0f)
                return 1f;
            return 1f - Mathf.Clamp01((_age - _fadeFrom) / _fadeLength);
        }

        /// <summary>Fast off the middle and settling at the edge, as frost runs out and slows.</summary>
        private static float Spread(float k)
        {
            float left = 1f - k;
            return 1f - left * left * left;
        }

        /// <summary>
        /// A centre vertex and <see cref="rings"/> rings of <see cref="segments"/> around it,
        /// UV-mapped flat so the texture's middle is the circle's centre.
        /// </summary>
        private void BuildMesh()
        {
            int spokes = Mathf.Max(8, segments);
            int bands = Mathf.Max(1, rings);
            float disc = radius / Mathf.Max(0.1f, rimAt);
            int count = 1 + spokes * bands;
            _offsets = new Vector3[count];
            _vertices = new Vector3[count];
            _heights = new float[count];
            var uvs = new Vector2[count];
            uvs[0] = new Vector2(0.5f, 0.5f);
            for (int b = 0; b < bands; ++b)
            {
                float r = disc * (b + 1) / bands;
                for (int s = 0; s < spokes; ++s)
                {
                    float angle = s * Mathf.PI * 2f / spokes;
                    var direction = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                    int i = 1 + b * spokes + s;
                    _offsets[i] = direction * r;
                    float share = 0.5f * r / disc;
                    uvs[i] = new Vector2(0.5f + direction.x * share, 0.5f + direction.z * share);
                }
            }

            // Clockwise seen from above, which is the front face in Unity.
            var triangles = new int[spokes * 3 + (bands - 1) * spokes * 6];
            int t = 0;
            for (int s = 0; s < spokes; ++s)
            {
                int next = (s + 1) % spokes;
                triangles[t++] = 0;
                triangles[t++] = 1 + next;
                triangles[t++] = 1 + s;
            }
            for (int b = 0; b < bands - 1; ++b)
            {
                int inner = 1 + b * spokes;
                int outer = inner + spokes;
                for (int s = 0; s < spokes; ++s)
                {
                    int next = (s + 1) % spokes;
                    triangles[t++] = inner + s;
                    triangles[t++] = inner + next;
                    triangles[t++] = outer + s;
                    triangles[t++] = inner + next;
                    triangles[t++] = outer + next;
                    triangles[t++] = outer + s;
                }
            }

            _mesh = new Mesh { name = "GroundCircle" };
            _mesh.MarkDynamic();
            _mesh.vertices = _offsets;
            _mesh.uv = uvs;
            _mesh.triangles = triangles;
            _mesh.RecalculateNormals();
            GetComponent<MeshFilter>().sharedMesh = _mesh;
        }

        /// <summary>
        /// Drops every vertex onto the ground under it, working outward along each spoke: a vertex
        /// looks for ground near the height of the one inside it, so the circle follows a slope up
        /// or down a hillside rather than being measured against the middle's height alone - and
        /// out of several things under one vertex (the ground, a tree's collider, a roof's eave)
        /// it takes the one that continues the surface it is already on. <paramref name="scale"/>
        /// shrinks it toward its middle, for frost still spreading.
        /// </summary>
        private void Drape(Vector3 centre, float scale)
        {
            int mask = GameInstance.Singleton != null
                ? GameInstance.Singleton.GetAreaSkillGroundDetectionLayerMask()
                : Physics.DefaultRaycastLayers;
            int spokes = Mathf.Max(8, segments);
            // Aim points and spawned areas are both put on the ground by the kit already; this is
            // only in case one arrives a little above or below it.
            float middle = GroundHeight(centre.x, centre.z, centre.y, mask);
            _heights[0] = middle;
            _vertices[0] = new Vector3(0f, middle - centre.y + lift, 0f);
            for (int i = 1; i < _offsets.Length; ++i)
            {
                float expected = i <= spokes ? middle : _heights[i - spokes];
                Vector3 offset = _offsets[i] * scale;
                float height = GroundHeight(centre.x + offset.x, centre.z + offset.z, expected, mask);
                _heights[i] = height;
                _vertices[i] = new Vector3(offset.x, height - centre.y + lift, offset.z);
            }
            _mesh.vertices = _vertices;
            _mesh.RecalculateBounds();
        }

        /// <summary>The ground nearest the expected height, or that height if there is none within reach.</summary>
        private float GroundHeight(float x, float z, float expected, int mask)
        {
            int count = Physics.RaycastNonAlloc(new Vector3(x, expected + reach, z), Vector3.down, _hits,
                                                reach * 2f, mask, QueryTriggerInteraction.Ignore);
            float found = expected;
            float nearest = float.MaxValue;
            for (int i = 0; i < count; ++i)
            {
                float gap = Mathf.Abs(_hits[i].point.y - expected);
                if (gap < nearest)
                {
                    nearest = gap;
                    found = _hits[i].point.y;
                }
            }
            return found;
        }

        /// <summary>
        /// The pulse and the fade, through a property block rather than the material, which is
        /// shared by every copy of the prefab. Only a pulsing or fading circle sets one; a steady
        /// one keeps its renderer batchable.
        /// </summary>
        private void Paint(float opacity)
        {
            if (_block == null)
                _block = new MaterialPropertyBlock();
            float brightness = 1f;
            if (pulseDepth > 0f)
            {
                float wave = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * pulseRate * Mathf.PI * 2f);
                brightness = Mathf.Lerp(1f - pulseDepth, 1f, wave);
            }
            Color colour = _colour;
            colour.r *= brightness;
            colour.g *= brightness;
            colour.b *= brightness;
            colour.a *= opacity;
            _block.SetColor(BaseColorId, colour);
            _renderer.SetPropertyBlock(_block);
            // Once faded it stays tinted until switched off, so a pooled copy is repainted whole
            // rather than left at the last frame's opacity.
            _tinted = opacity < 1f;
        }
    }
}
