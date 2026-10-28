using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// The drawing half of the beach and the shallows: every footprint, droplet, ripple and puff of
    /// sand any character makes is one particle in one of a handful of world-space systems, shared by
    /// everyone in the scene. <see cref="FootstepEffects"/> is the other half - it watches a body's
    /// feet and says where and how hard.
    ///
    /// **One shared set, not one per character.** A print has to outlive the foot that made it by half
    /// a minute, and a crowd on a beach should not be able to spend more than a fixed budget on it:
    /// each system has a particle cap, and a full one simply drops the newest, which on a beach nobody
    /// will see happen. A prefab of the systems (built by <c>Build Footstep Effects</c>) is instantiated
    /// by the first character that needs it and goes with the scene it was made in.
    ///
    /// **Why particles for footprints** rather than decals: URP's decal feature needs the depth
    /// texture, which is a project setting the demo does not own (Mobile_RPAsset has it off), and a
    /// hand-pooled quad per print is just a worse particle system. A print is a flat quad laid on the
    /// ground along the terrain's normal, a couple of centimetres above it, with a colour whose alpha
    /// is how strong the print is and a lifetime that fades it out. The quad is lit, so a print is dark
    /// at night rather than glowing.
    ///
    /// Every system is `AlwaysSimulate`: a looping system with no particles has empty bounds, so the
    /// default culling would pause it whenever the camera looked away, and it would then not draw the
    /// print a character left behind until the camera happened to look at the origin.
    /// </summary>
    public class FootstepEffectsHub : MonoBehaviour
    {
        [Header("Prints (mirrored quads, one system per foot)")]
        public ParticleSystem printsLeft;
        public ParticleSystem printsRight;

        [Header("Water")]
        [Tooltip("Streaked drops that arc up and fall back to where they started.")]
        public ParticleSystem droplets;
        [Tooltip("Flat rings that spread on the surface.")]
        public ParticleSystem rings;
        [Tooltip("Flat patches of foam that linger behind a swimmer.")]
        public ParticleSystem foam;

        [Header("Sand and spray")]
        public ParticleSystem sandGrains;
        [Tooltip("Soft billows that rise and thin: dust off a hard footfall, spray off a hard splash.")]
        public ParticleSystem puffs;

        [Header("Look")]
        [Tooltip("Metres a print floats above the ground. The terrain mesh is coarser than its height map, so this keeps the print clear of it.")]
        public float printLift = 0.03f;
        [Tooltip("Metres a ripple floats above the sea's level.")]
        public float surfaceLift = 0.012f;
        public Color dropletColour = new Color(0.92f, 0.97f, 1f, 0.9f);
        public Color ringColour = new Color(0.95f, 0.98f, 1f, 1f);
        public Color foamColour = new Color(0.96f, 0.99f, 1f, 1f);
        public Color grainColour = new Color(0.93f, 0.85f, 0.65f, 0.95f);
        public Color puffColour = new Color(0.93f, 0.87f, 0.72f, 1f);
        public Color sprayColour = new Color(0.94f, 0.98f, 1f, 1f);

        private const float Gravity = 9.81f;

        private static FootstepEffectsHub s_instance;

        /// <summary>
        /// The scene's hub, made from <paramref name="prefab"/> the first time anyone asks. Null with no
        /// prefab and none made yet, and in a headless server, which has nothing to draw.
        /// </summary>
        public static FootstepEffectsHub Get(FootstepEffectsHub prefab)
        {
            if (s_instance != null)
                return s_instance;
            if (prefab == null || Application.isBatchMode)
                return null;
            s_instance = Instantiate(prefab);
            s_instance.name = "FootstepEffects";
            return s_instance;
        }

        private void Awake()
        {
            // A second hub (a scene that shipped with one) is not needed.
            if (s_instance == null)
                s_instance = this;
            Run(printsLeft);
            Run(printsRight);
            Run(droplets);
            Run(rings);
            Run(foam);
            Run(sandGrains);
            Run(puffs);
        }

        private void OnDestroy()
        {
            if (s_instance == this)
                s_instance = null;
        }

        /// <summary>
        /// Start a system that only ever emits by hand. It loops with no rate, so it stays alive for
        /// `Emit`, and plays from the start because a stopped system does not simulate what is put in it.
        /// </summary>
        private static void Run(ParticleSystem system)
        {
            if (system != null && !system.isPlaying)
                system.Play();
        }

        // ---- the beach ----------------------------------------------------------------------------

        /// <summary>
        /// A footprint: a quad `length` long and `width` wide, centred on <paramref name="centre"/>, its
        /// toe along <paramref name="forward"/> and lying on ground whose normal is
        /// <paramref name="normal"/>. The colour's alpha is the strength; the print is gone after `life`.
        /// </summary>
        public void Print(bool left, Vector3 centre, Vector3 forward, Vector3 normal, float length, float width,
                          Color colour, float life)
        {
            ParticleSystem system = left ? printsLeft : printsRight;
            if (system == null)
                return;
            forward = Vector3.ProjectOnPlane(forward, normal);
            if (forward.sqrMagnitude < 1e-5f)
                forward = Vector3.ProjectOnPlane(Vector3.forward, normal);
            Quaternion rotation = Quaternion.LookRotation(forward, normal);

            var p = new ParticleSystem.EmitParams
            {
                position = centre + normal * printLift,
                velocity = Vector3.zero,
                rotation3D = rotation.eulerAngles,
                // The quad is flat on x/z: y is its thickness and means nothing.
                startSize3D = new Vector3(width, 1f, length),
                startColor = colour,
                startLifetime = life,
            };
            system.Emit(p, 1);
        }

        /// <summary>
        /// A boot driving off the sand: a few grains thrown back and up and a small low puff of dust.
        /// <paramref name="away"/> is the way the foot pushes the sand (opposite to the way it is going).
        /// </summary>
        public void SandKick(Vector3 at, Vector3 away, float strength)
        {
            strength = Mathf.Clamp01(strength);
            away.y = 0f;
            away = away.sqrMagnitude > 1e-4f ? away.normalized : Vector3.zero;

            if (sandGrains != null)
            {
                int count = Mathf.RoundToInt(Mathf.Lerp(3f, 11f, strength));
                var p = new ParticleSystem.EmitParams();
                for (int i = 0; i < count; ++i)
                {
                    Vector3 sideways = Random.insideUnitSphere * 0.5f;
                    sideways.y = 0f;
                    float up = Random.Range(0.7f, 1.4f + 1.1f * strength);
                    p.position = at + new Vector3(Random.Range(-0.04f, 0.04f), 0.02f, Random.Range(-0.04f, 0.04f));
                    p.velocity = away * Random.Range(0.6f, 1.6f + 1.2f * strength) + sideways + Vector3.up * up;
                    // Back down at the height it left from: at 1.6 x gravity.
                    p.startLifetime = Mathf.Clamp(2f * up / (Gravity * 1.6f), 0.15f, 0.7f);
                    p.startSize = Random.Range(0.022f, 0.04f);
                    p.startColor = grainColour;
                    sandGrains.Emit(p, 1);
                }
            }
            if (puffs != null && strength > 0.25f)
            {
                var p = new ParticleSystem.EmitParams
                {
                    position = at + Vector3.up * 0.05f,
                    velocity = away * 0.35f + Vector3.up * 0.25f,
                    startLifetime = Random.Range(0.6f, 0.9f),
                    startSize = Random.Range(0.2f, 0.32f) * (0.7f + 0.6f * strength),
                    startColor = new Color(puffColour.r, puffColour.g, puffColour.b, 0.18f + 0.3f * strength),
                    rotation = Random.Range(0f, 360f),
                };
                puffs.Emit(p, 1);
            }
        }

        // ---- the water ----------------------------------------------------------------------------

        /// <summary>
        /// A foot, hand or body breaking the surface at <paramref name="at"/>: a crown of drops thrown up
        /// and out (and along <paramref name="drift"/>, the way whatever made it was going), and a ring
        /// spreading from it. A hard one adds a patch of foam. `at.y` is the water's level.
        /// </summary>
        public void Splash(Vector3 at, float strength, Vector3 drift)
        {
            strength = Mathf.Clamp01(strength);
            EmitDroplets(at, Mathf.RoundToInt(Mathf.Lerp(10f, 40f, strength)),
                         1.4f + 2.2f * strength, 0.45f + 1.2f * strength, drift, 1f + 0.8f * strength);
            Ring(at, Mathf.Lerp(0.9f, 2.0f, strength), 0.7f + 0.3f * strength);
            Puff(at, Mathf.Lerp(0.3f, 0.6f, strength), 0.3f + 0.3f * strength, drift);
            if (strength > 0.3f)
                Foam(at, Mathf.Lerp(0.6f, 1.3f, strength), 0.2f + 0.5f * strength);
        }

        /// <summary>A soft billow of spray lifted off the surface, drifting with <paramref name="drift"/> and thinning as it rises.</summary>
        public void Puff(Vector3 at, float size, float alpha, Vector3 drift)
        {
            if (puffs == null)
                return;
            drift.y = 0f;
            var p = new ParticleSystem.EmitParams
            {
                position = at + Vector3.up * 0.08f,
                velocity = Vector3.up * Random.Range(0.35f, 0.7f) + drift * 0.3f,
                startLifetime = Random.Range(0.55f, 0.85f),
                startSize = size * Random.Range(0.8f, 1.15f),
                startColor = new Color(sprayColour.r, sprayColour.g, sprayColour.b, Mathf.Clamp01(alpha)),
                rotation = Random.Range(0f, 360f),
            };
            puffs.Emit(p, 1);
        }

        /// <summary>
        /// Water running off a foot or hand as it leaves the sea: a few slow drops that fall from
        /// <paramref name="from"/> to the surface at <paramref name="surfaceY"/>.
        /// </summary>
        public void Drip(Vector3 from, float surfaceY, float strength)
        {
            if (droplets == null)
                return;
            strength = Mathf.Clamp01(strength);
            int count = Mathf.RoundToInt(Mathf.Lerp(3f, 9f, strength));
            float height = Mathf.Max(0.02f, from.y - surfaceY);
            var p = new ParticleSystem.EmitParams();
            for (int i = 0; i < count; ++i)
            {
                float up = Random.Range(0.1f, 0.6f);
                Vector3 outward = Random.insideUnitSphere * 0.25f;
                outward.y = 0f;
                p.position = from + new Vector3(Random.Range(-0.05f, 0.05f), 0f, Random.Range(-0.05f, 0.05f));
                p.velocity = outward + Vector3.up * up;
                // Up, then down by `height` more than it went up: the root of the fall.
                p.startLifetime = Mathf.Clamp((up + Mathf.Sqrt(up * up + 2f * Gravity * height)) / Gravity, 0.1f, 1.2f);
                p.startSize = Random.Range(0.025f, 0.045f);
                p.startColor = dropletColour;
                droplets.Emit(p, 1);
            }
        }

        /// <summary>The bow wave of a swimmer: a thin fan of drops thrown ahead of the chest.</summary>
        public void Spray(Vector3 at, Vector3 forward, float strength)
        {
            strength = Mathf.Clamp01(strength);
            forward.y = 0f;
            forward = forward.sqrMagnitude > 1e-4f ? forward.normalized : Vector3.forward;
            EmitDroplets(at, Mathf.RoundToInt(Mathf.Lerp(4f, 13f, strength)), 1.0f + 1.2f * strength, 0.3f,
                         forward * (0.9f + 1.6f * strength), 0.9f + 0.5f * strength);
            if (strength > 0.6f)
                Puff(at, 0.3f + 0.2f * strength, 0.25f + 0.2f * strength, forward * 0.8f);
        }

        /// <summary>A ring spreading on the surface, `diameter` across at its widest. `at.y` is the water's level.</summary>
        public void Ring(Vector3 at, float diameter, float strength, float life = 1.1f)
        {
            if (rings == null)
                return;
            var p = new ParticleSystem.EmitParams
            {
                position = at + Vector3.up * surfaceLift,
                velocity = Vector3.zero,
                startSize = diameter,
                startLifetime = life,
                rotation = Random.Range(0f, 360f),
                startColor = new Color(ringColour.r, ringColour.g, ringColour.b, Mathf.Clamp01(strength) * ringColour.a),
            };
            rings.Emit(p, 1);
        }

        /// <summary>A patch of foam on the surface that grows a little and thins away. `at.y` is the water's level.</summary>
        public void Foam(Vector3 at, float size, float strength, float life = 1.7f)
        {
            if (foam == null)
                return;
            var p = new ParticleSystem.EmitParams
            {
                position = at + Vector3.up * (surfaceLift + 0.004f),
                velocity = Vector3.zero,
                startSize = size,
                startLifetime = life * Random.Range(0.85f, 1.15f),
                rotation = Random.Range(0f, 360f),
                startColor = new Color(foamColour.r, foamColour.g, foamColour.b, Mathf.Clamp01(strength) * foamColour.a),
            };
            foam.Emit(p, 1);
        }

        /// <summary>
        /// Drops thrown from `at` and falling back to its height: each gets exactly the lifetime of its own
        /// arc (`2 v / g`), so it fades as it reaches the surface it came from rather than falling through it.
        /// </summary>
        private void EmitDroplets(Vector3 at, int count, float upMax, float outMax, Vector3 drift, float sizeScale)
        {
            if (droplets == null)
                return;
            drift.y = 0f;
            var p = new ParticleSystem.EmitParams();
            for (int i = 0; i < count; ++i)
            {
                float up = Random.Range(0.55f * upMax, upMax);
                float angle = Random.value * Mathf.PI * 2f;
                Vector3 ring = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                p.position = at + ring * Random.Range(0f, 0.08f);
                p.velocity = ring * (Random.Range(0.25f, 1f) * outMax) + drift * Random.Range(0.3f, 1f) + Vector3.up * up;
                p.startLifetime = Mathf.Clamp(2f * up / Gravity, 0.15f, 1.1f);
                p.startSize = Random.Range(0.03f, 0.055f) * sizeScale;
                p.startColor = dropletColour;
                droplets.Emit(p, 1);
            }
        }

        // ---- the ground under a foot ----------------------------------------------------------------

        private static TerrainData s_data;
        private static string s_layerName;
        private static int s_layer = -1;

        /// <summary>
        /// How much of terrain layer <paramref name="layerName"/> (by the layer asset's name) covers
        /// <paramref name="point"/>, 0 to 1, bilinear between the splat map's texels - which are half a
        /// metre apart on the island, so a nearest-texel read would step. 0 for a terrain without that layer.
        /// </summary>
        public static float LayerWeight(Terrain terrain, Vector3 point, string layerName)
        {
            if (terrain == null || terrain.terrainData == null)
                return 0f;
            TerrainData data = terrain.terrainData;
            if (data != s_data || layerName != s_layerName)
            {
                s_data = data;
                s_layerName = layerName;
                s_layer = -1;
                TerrainLayer[] layers = data.terrainLayers;
                for (int i = 0; layers != null && i < layers.Length; ++i)
                {
                    if (layers[i] != null && layers[i].name == layerName)
                    {
                        s_layer = i;
                        break;
                    }
                }
            }
            if (s_layer < 0 || data.alphamapResolution < 2)
                return 0f;

            Vector3 local = point - terrain.GetPosition();
            int res = data.alphamapResolution;
            float fx = Mathf.Clamp01(local.x / data.size.x) * (res - 1);
            float fz = Mathf.Clamp01(local.z / data.size.z) * (res - 1);
            int x0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, res - 2);
            int z0 = Mathf.Clamp(Mathf.FloorToInt(fz), 0, res - 2);
            float tx = fx - x0;
            float tz = fz - z0;
            // [z, x, layer]
            float[,,] a = data.GetAlphamaps(x0, z0, 2, 2);
            float low = Mathf.Lerp(a[0, 0, s_layer], a[0, 1, s_layer], tx);
            float high = Mathf.Lerp(a[1, 0, s_layer], a[1, 1, s_layer], tx);
            return Mathf.Lerp(low, high, tz);
        }
    }
}
