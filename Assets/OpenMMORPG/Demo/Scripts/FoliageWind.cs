using UnityEngine;
using UnityEngine.Rendering;

namespace MultiplayerARPG
{
    /// <summary>
    /// The island's wind: one component that sets the globals the foliage shader reads.
    ///
    /// **A vertex shader, not a transform sway.** <see cref="WindSway"/> rocks a whole prop about
    /// its base, which is right for twenty plants on a menu hillside and wrong here: it bends nothing
    /// (a rigid rotation), it needs a component per object, and it cannot touch terrain details at
    /// all, which is where nearly all of the island's grass is - the terrain welds every plant in a
    /// patch into one mesh. `OpenMMORPG/Demo/Wind Foliage` moves vertices instead, so a trunk stays
    /// planted and its crown moves, and the grass is covered by exactly the same code as the trees.
    ///
    /// This component owns everything that is not per-vertex: which way it blows, how hard, the slow
    /// comings and goings of a breeze, and the terrain heightmap that grass measures its height
    /// from (a blade in a combined mesh has no pivot of its own, so it asks the ground). With no
    /// FoliageWind in a scene the shader's strength global reads zero and every material on it is plain
    /// Lit, which is why the menu, the icon rigs and the animation bench need nothing.
    ///
    /// Cosmetic only and graphics-only: nothing here is read by the simulation, so a headless map
    /// server skips it entirely.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class FoliageWind : MonoBehaviour
    {
        private static readonly int WindId = Shader.PropertyToID("_DemoWind");
        private static readonly int TreeId = Shader.PropertyToID("_DemoWindTree");
        private static readonly int SmallId = Shader.PropertyToID("_DemoWindSmall");
        private static readonly int GroundId = Shader.PropertyToID("_DemoWindGround");
        private static readonly int Ground2Id = Shader.PropertyToID("_DemoWindGround2");
        private static readonly int HeightId = Shader.PropertyToID("_DemoWindHeight");

        [Header("Wind")]
        [Tooltip("Overall strength. 1 is a steady breeze; 0 switches the wind off. Everything below is scaled by it.")]
        [Range(0f, 2f)]
        public float strength = 1f;
        [Tooltip("Compass direction the wind blows towards, in degrees. 0 is +Z (north), 90 is +X (east).")]
        public float windYaw = 60f;
        [Tooltip("How far the direction drifts either side of Wind Yaw over a minute or two.")]
        public float yawWander = 14f;
        [Tooltip("How fast the waves roll across the island. 1 is a gentle breeze.")]
        public float speed = 1f;

        [Header("Weather")]
        [Tooltip("How much the strength rises and falls over time, as a fraction of it. 0 is a perfectly steady wind.")]
        [Range(0f, 0.8f)]
        public float variability = 0.35f;
        [Tooltip("Seconds for the breeze to come and go once.")]
        public float variabilityPeriod = 45f;

        [Header("Trees")]
        [Tooltip("Metres a tree leans at the reference height in a steady breeze.")]
        public float treeBend = 0.35f;
        [Tooltip("Height a tree has to be for the bend above to apply in full. Taller leans further, shorter less, as the square.")]
        public float treeReferenceHeight = 10f;
        [Tooltip("Metres a leaf shivers, on top of the bend.")]
        public float treeFlutter = 0.06f;
        [Tooltip("The most the height factor can reach, so the tallest trees do not swing in proportion to their size squared. 2 is a tree about 1.4x the reference height; the 17 m trees are past it.")]
        public float treeMaxFactor = 2f;

        [Header("Grass, flowers and bushes")]
        [Tooltip("Metres a plant leans at its reference height in a steady breeze.")]
        public float smallBend = 0.4f;
        [Tooltip("Height at which the bend above applies in full. A blade half that tall leans a quarter as far.")]
        public float smallReferenceHeight = 0.8f;
        [Tooltip("Metres a leaf or blade shivers, on top of the bend.")]
        public float smallFlutter = 0.015f;
        [Tooltip("The most the height factor can reach. The tall, stiff plants (the agaves, the bushes) reach it first and then lean no further.")]
        public float smallMaxFactor = 0.8f;

        [Header("Ground")]
        [Tooltip("Terrain that grass measures its height from. Left empty, the active terrain is used.")]
        public Terrain terrain;

        /// <summary>
        /// Extra strength on top of everything above, as a fraction of it: 0 is the wind as tuned, 0.7 is
        /// seventy per cent harder. Set every frame by <see cref="WeatherSystem"/> so a storm blows the trees
        /// about more than a fine day does. A property so it is never saved into the scene - a wind left
        /// stormy by a preview would otherwise open that way - and zero for anything that does not set it.
        /// </summary>
        public float StormBoost { get; set; }

        /// <summary>
        /// The way the wind is blowing right now, as a unit vector on the ground (x east, y north),
        /// drifting with <see cref="yawWander"/>. What the rain slants along. Zero until the first frame.
        /// </summary>
        public Vector2 Direction { get; private set; }

        private Terrain _bound;
        private Texture2D _heights;
        private double _phase;
        private double _lastTime;

        private static bool HasGraphics => SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;

        private void OnEnable()
        {
            _lastTime = Now();
            if (HasGraphics)
                Apply();
        }

        private void OnDisable()
        {
            if (!HasGraphics)
                return;
            // Zero strength is what every wind material reads as "off", so leaving for a scene with
            // no FoliageWind (the menu) puts those materials straight back to plain Lit.
            Shader.SetGlobalVector(WindId, Vector4.zero);
            Shader.SetGlobalVector(Ground2Id, Vector4.zero);
            ReleaseGround();
        }

        private void Update()
        {
            if (HasGraphics)
                Apply();
        }

        // Unscaled and real, not Time.deltaTime: the edit-mode player loop only runs when something
        // changes, and a paused game should still have leaves moving behind its menu.
        private static double Now() => Time.realtimeSinceStartupAsDouble;

        private void Apply()
        {
            double now = Now();
            float dt = Mathf.Min((float)(now - _lastTime), 0.1f);
            _lastTime = now;
            float t = (float)now;

            // A breeze that comes and goes. Perlin noise is 0..1 around 0.5; the second sample
            // keeps the speed from rising and falling in lock step with the strength.
            float breeze = Mathf.PerlinNoise(t / Mathf.Max(variabilityPeriod, 1f), 0.37f) * 2f - 1f;
            float pace = Mathf.PerlinNoise(t / Mathf.Max(variabilityPeriod * 1.7f, 1f), 4.1f) * 2f - 1f;
            float level = Mathf.Max(0f, 1f + variability * breeze) * strength * (1f + Mathf.Max(0f, StormBoost));

            // Accumulated, not t * speed: scaling the clock would make the whole island's leaves jump
            // every time the speed changed. Integrating the rate keeps the motion continuous.
            _phase += dt * speed * (1f + 0.25f * variability * pace);

            float yaw = (windYaw + yawWander * (Mathf.PerlinNoise(t / 70f, 8.3f) * 2f - 1f)) * Mathf.Deg2Rad;
            Direction = new Vector2(Mathf.Sin(yaw), Mathf.Cos(yaw));
            Shader.SetGlobalVector(WindId, new Vector4(Direction.x, Direction.y, level, (float)_phase));
            Shader.SetGlobalVector(TreeId, new Vector4(treeBend, treeReferenceHeight, treeFlutter, treeMaxFactor));
            Shader.SetGlobalVector(SmallId, new Vector4(smallBend, smallReferenceHeight, smallFlutter, smallMaxFactor));

            Terrain wanted = terrain != null ? terrain : Terrain.activeTerrain;
            if (wanted != _bound)
                BindGround(wanted);
        }

        /// <summary>
        /// Uploads the terrain's heights as a texture the vertex shader can sample.
        ///
        /// Terrain's own heightmap texture would do, but its encoding is the terrain shader's
        /// business; a plain normalised R16 copy is one line to decode and does not move if Unity
        /// changes how it stores its own. 513 x 513 at 16 bits is half a megabyte.
        /// </summary>
        private void BindGround(Terrain wanted)
        {
            ReleaseGround();
            _bound = wanted;
            Shader.SetGlobalVector(Ground2Id, Vector4.zero);
            if (wanted == null || wanted.terrainData == null || !SystemInfo.SupportsTextureFormat(TextureFormat.R16))
                return;

            TerrainData data = wanted.terrainData;
            int res = data.heightmapResolution;
            float[,] heights = data.GetHeights(0, 0, res, res);
            var raw = new ushort[res * res];
            for (int z = 0; z < res; ++z)
                for (int x = 0; x < res; ++x)
                    raw[z * res + x] = (ushort)Mathf.RoundToInt(Mathf.Clamp01(heights[z, x]) * 65535f);

            _heights = new Texture2D(res, res, TextureFormat.R16, false, true)
            {
                name = "DemoWindHeight",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };
            _heights.SetPixelData(raw, 0);
            _heights.Apply(false, true);

            // The samples sit on the corners of the terrain's grid, res - 1 cells across, so the
            // first and last texel centres land on the first and last sample: scale by (res - 1)
            // over res rather than by 1 over the size, and shift half a texel in.
            Vector3 origin = wanted.transform.position;
            Vector3 size = data.size;
            Shader.SetGlobalTexture(HeightId, _heights);
            Shader.SetGlobalVector(GroundId, new Vector4(origin.x, origin.z, (res - 1f) / (size.x * res), 0.5f / res));
            Shader.SetGlobalVector(Ground2Id, new Vector4(origin.y, size.y, (res - 1f) / (size.z * res), 1f));
        }

        private void ReleaseGround()
        {
            if (_heights != null)
            {
                if (Application.isPlaying)
                    Destroy(_heights);
                else
                    DestroyImmediate(_heights);
                _heights = null;
            }
            _bound = null;
        }
    }
}
