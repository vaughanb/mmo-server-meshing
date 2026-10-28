using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// Where the sea's surface actually is: the height the `Demo/StylizedWater` vertex shader displaces the
    /// water to, worked out on the CPU so gameplay effects agree with what is drawn.
    ///
    /// The sea is a flat trigger volume as far as physics is concerned, but the shader moves the drawn surface
    /// up and down by up to `_WaveHeight` (18 cm on the island). On a beach that shelves at about 1 in 6, that
    /// carries the visible waterline a metre either way, so a flat level said "in the water" for a foot standing
    /// on sand the swell had just uncovered. This is a line-for-line copy of the shader's `MakeWarp` and
    /// `WaveTrain` (height only - the shader's gradients are for shading), read off the same material and the
    /// same clock: URP feeds `_Time.y` from `Time.time` in play mode (`ScriptableRenderer.SetShaderTimeValues`).
    /// **Change the shader's swell and this must change with it.**
    ///
    /// Not copied: the shader fades the displacement out between `_DisplaceFade` (110 to 280 m from the camera),
    /// which never matters for anything near enough to be seen splashing. Vertex interpolation across the sea
    /// mesh is ignored too: the swell is 13 m long and the mesh is fine near the shore.
    /// </summary>
    public static class StylizedWaterSurface
    {
        private static Material s_material;
        private static int s_frame = -1;
        private static float s_height, s_length, s_speed, s_heading, s_spread;
        private static float s_warpStrength, s_warpLength, s_warpSpeed;

        private static readonly Vector2 D1 = new Vector2(0.8746f, 0.4848f);
        private static readonly Vector2 D2 = new Vector2(-0.4226f, 0.9063f);
        private static readonly Vector2 D3 = new Vector2(0.2588f, -0.9659f);
        private static readonly Vector2 D4 = new Vector2(-0.9397f, -0.3420f);
        private static readonly float[] Fan = { 0.15f, -0.85f, 1.45f, -2.05f };
        private static readonly float[] Phase = { 0f, 2.399f, 4.303f, 1.117f };
        private const float TwoPi = Mathf.PI * 2f;

        /// <summary>
        /// How far above (+) or below (-) its resting level the drawn surface of <paramref name="material"/> is
        /// at world (<paramref name="x"/>, <paramref name="z"/>) right now. 0 for no material.
        /// </summary>
        public static float Offset(Material material, float x, float z)
        {
            if (material == null)
                return 0f;
            Refresh(material);
            return Swell(x, z, Time.time);
        }

        /// <summary>The material's swell settings, read once a frame however many feet ask.</summary>
        private static void Refresh(Material material)
        {
            if (material == s_material && s_frame == Time.frameCount)
                return;
            s_material = material;
            s_frame = Time.frameCount;
            s_height = material.GetFloat("_WaveHeight");
            s_length = material.GetFloat("_WaveLength");
            s_speed = material.GetFloat("_WaveSpeed");
            s_heading = material.GetFloat("_WaveHeading") * Mathf.Deg2Rad;
            s_spread = material.GetFloat("_WaveSpread") * Mathf.Deg2Rad;
            s_warpStrength = material.GetFloat("_WarpStrength");
            s_warpLength = material.GetFloat("_WarpLength");
            s_warpSpeed = material.GetFloat("_WarpSpeed");
        }

        /// <summary>The shader's `WaveTrain(positionWS.xz, _Time.y, ...)` with four octaves, height only.</summary>
        private static float Swell(float x, float z, float t)
        {
            float wavelength = s_length;

            // MakeWarp: a unit-amplitude bend of the plane, two crossed octaves.
            const float fine = 0.35f;
            const float norm = 1f / (1f + fine);
            float k1 = TwoPi / Mathf.Max(wavelength * s_warpLength, 0.01f);
            float k2 = k1 * 1.37f;
            float k3 = k1 * 2.40f;
            float k4 = k1 * 3.11f;
            float s1 = Mathf.Sin((D1.x * x + D1.y * z) * k1 + t * s_warpSpeed);
            float s2 = Mathf.Sin((D2.x * x + D2.y * z) * k2 + t * s_warpSpeed * 0.79f);
            float s3 = Mathf.Sin((D3.x * x + D3.y * z) * k3 + t * s_warpSpeed * 1.31f);
            float s4 = Mathf.Sin((D4.x * x + D4.y * z) * k4 + t * s_warpSpeed * 1.07f);
            float warpAmp = wavelength * s_warpStrength;
            float wx = x + (s1 + fine * s3) * norm * warpAmp;
            float wz = z + (s2 + fine * s4) * norm * warpAmp;

            // The octaves, fanned round the heading, each at its deep-water speed.
            float k0 = TwoPi / Mathf.Max(wavelength, 0.01f);
            float k = k0;
            float amp = 1f;
            float total = 0f;
            float height = 0f;
            for (int i = 0; i < 4; ++i)
            {
                float angle = s_heading + s_spread * Fan[i];
                float dx = Mathf.Cos(angle);
                float dz = Mathf.Sin(angle);
                height += amp * Mathf.Sin((dx * wx + dz * wz) * k + t * (s_speed * Mathf.Sqrt(k * k0)) + Phase[i]);
                total += amp;
                k *= 2.13f;
                amp *= 0.52f;
            }
            return height * (s_height / Mathf.Max(total, 1e-4f));
        }
    }
}
