using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// Turns the kit's time of day into a sky, a sun and an ambient.
    ///
    /// The kit ticks a clock on the server and replicates it, but nothing in it draws the
    /// result: <c>SampleDayNightTimeApplyer</c> moves the light and sets the ambient, and
    /// leaves the sky alone entirely — so with a fixed skybox the sun sets, the shadows
    /// go, and the sky stays broad daylight overhead. This drives the sky as well, which
    /// is the part a player actually looks at.
    ///
    /// Everything here is presentation. The clock itself is the server's, so every client
    /// reads the same hour and arrives at the same sky without any of this being sent.
    /// </summary>
    [ExecuteAlways]
    public class DayNightSkyCycle : MonoBehaviour
    {
        [Header("What it drives")]
        public Light sun;
        public Material sky;

        [Header("The day")]
        [Tooltip("Hour the sun clears the horizon, and the hour it goes back under.")]
        [Range(0f, 12f)] public float sunrise = 6f;
        [Range(12f, 24f)] public float sunset = 19f;
        [Tooltip("How long dawn and dusk take, in hours. The sky blends across this.")]
        [Range(0.25f, 4f)] public float twilight = 1.5f;
        [Tooltip("The compass bearing the sun crosses the sky along, in degrees. Off due east-west so the shadows fall across the land rather than straight down it.")]
        [Range(0f, 360f)] public float sunYaw = 138f;

        [Header("Colours through the day")]
        public Gradient sunColour;
        public Gradient skyAmbient;
        public Gradient horizonAmbient;
        public Gradient groundAmbient;
        public Gradient fog;

        [Header("Strength")]
        public float noonIntensity = 1.15f;
        public float nightIntensity = 0.12f;

        [Header("Overcast (driven by WeatherSystem)")]
        [Tooltip("The grey the sky, the sun and the haze are washed towards under heavy cloud. Only its hue matters: it is applied to the luminance each already had.")]
        public Color overcastTint = new Color(0.86f, 0.90f, 0.96f);
        [Tooltip("How much of its brightness the sky and the haze keep in full overcast.")]
        [Range(0.2f, 1f)] public float overcastSkyDim = 0.55f;
        [Tooltip("How much of the direct sunlight is lost in full overcast. The shadows go with it.")]
        [Range(0f, 1f)] public float overcastSunLoss = 0.65f;
        [Tooltip("What the three ambient colours are multiplied by in full overcast, after they are greyed. Above the sky's dim: the light under cloud is diffuse, and the shade does not get as dark as the sun does.")]
        [Range(0.2f, 1.5f)] public float overcastAmbient = 0.95f;
        [Tooltip("The sun's shadow strength with the sky clear. The cycle owns the light's shadow strength, so tune it here and not on the light.")]
        [Range(0f, 1f)] public float clearShadowStrength = 0.72f;
        [Tooltip("The sun's shadow strength in full overcast.")]
        [Range(0f, 1f)] public float overcastShadowStrength = 0.18f;
        [Tooltip("How far the haze reaches, in metres, on a clear day. The cycle owns the scene's linear fog distances, so tune them here and not in the Lighting window.")]
        public float clearFogStart = 180f;
        public float clearFogEnd = 620f;
        [Tooltip("How far the haze reaches in full overcast. The rain closes the island in.")]
        public float overcastFogStart = 15f;
        public float overcastFogEnd = 260f;

        [Header("Editing")]
        [Tooltip("The hour shown while the game is not running, so the scene can be lit and looked at.")]
        [Range(0f, 24f)] public float previewHour = 12f;
        [Tooltip("How overcast the sky is while the game is not running, so the rain's light can be tuned without playing. The builder leaves it at 0; leave it there when saving.")]
        [Range(0f, 1f)] public float previewOvercast = 0f;

        /// <summary>
        /// How overcast the sky is while playing: 0 clear, 1 full cloud. Set every frame by
        /// <see cref="WeatherSystem"/>. (Out of play mode it is <see cref="previewOvercast"/>.)
        ///
        /// A property, not a field, so that a game that ends mid-shower can never leave a grey day saved
        /// in the scene. At 0 nothing here changes anything - every grade below is an identity - so a
        /// scene with no weather in it lights exactly as it did before overcast existed.
        /// </summary>
        public float Overcast { get; set; }

        /// <summary>Puts the sky material's overcast back to clear. The material is an asset, so a shower
        /// left on it when the game stops would be there the next time the project opens.</summary>
        public void ResetOvercast()
        {
            Overcast = 0f;
            if (sky != null && sky.HasProperty(OvercastId))
                sky.SetFloat(OvercastId, 0f);
        }

        private static readonly int BlendId = Shader.PropertyToID("_Blend");
        private static readonly int OvercastId = Shader.PropertyToID("_Overcast");
        private static readonly int OvercastTintId = Shader.PropertyToID("_OvercastTint");
        private static readonly int OvercastDimId = Shader.PropertyToID("_OvercastDim");

        /// <summary>The blend the environment probe was last built from.</summary>
        private float _litFor = -1f;
        private float _litOvercast = -1f;

        /// <summary>The hour the world is at: the server's while playing, the preview otherwise.</summary>
        public float Hour
        {
            get
            {
                if (!Application.isPlaying)
                    return previewHour;
                BaseGameNetworkManager manager = BaseGameNetworkManager.Singleton;
                if (manager == null || !manager.IsNetworkActive)
                    return previewHour;
                BaseDayNightTimeUpdater clock = GameInstance.Singleton.DayNightTimeUpdater;
                return clock == null ? previewHour : clock.TimeOfDay;
            }
        }

        private void Awake()
        {
            // A map server has no sky to draw, and rebuilding the environment probe for nobody is
            // not free. Hour and NightAmount still answer for anything that asks.
            if (Application.isBatchMode)
                enabled = false;
        }

        private void Update()
        {
            Apply(Hour);
        }

        public void Apply(float hour)
        {
            float night = NightAmount(hour);
            float through = hour / 24f;
            float cloud = Mathf.Clamp01(Application.isPlaying ? Overcast : Mathf.Max(Overcast, previewOvercast));

            if (sky != null && sky.HasProperty(BlendId))
            {
                sky.SetFloat(BlendId, night);
                if (sky.HasProperty(OvercastId))
                {
                    sky.SetFloat(OvercastId, cloud);
                    sky.SetColor(OvercastTintId, overcastTint);
                    sky.SetFloat(OvercastDimId, overcastSkyDim);
                }
                // Unity builds the environment reflection from the sky once and keeps it.
                // Changing the sky material does not rebuild it, so anything reflective
                // goes on mirroring whatever sky was up when it was last built - which is
                // why the sea lay there at midnight as a bright band of noon. Rebuilding
                // is not cheap, so it is done only when the sky has actually moved on -
                // and cloud moves it as surely as night does: the sea under a grey sky
                // must not go on mirroring a blue one.
                if (Mathf.Abs(night - _litFor) > 0.02f || Mathf.Abs(cloud - _litOvercast) > 0.04f)
                {
                    _litFor = night;
                    _litOvercast = cloud;
                    DynamicGI.UpdateEnvironment();
                }
            }

            if (sun != null)
            {
                // Turned about the horizon so that it rises at sunrise and sets at sunset
                // rather than at six and eighteen, and kept off due east (sunYaw) so the
                // shadows fall across the island rather than straight down it.
                float above = Mathf.InverseLerp(sunrise, sunset, hour);
                sun.transform.rotation = Quaternion.Euler(above * 180f, sunYaw, 0f);
                sun.color = Grade(sunColour.Evaluate(through), cloud, overcastTint, 1f);
                sun.intensity = Mathf.Lerp(noonIntensity, nightIntensity, night) * (1f - overcastSunLoss * cloud);
                // A light below the horizon still lights the world in Unity, so it is
                // switched off rather than left shining upward through the ground.
                sun.enabled = hour > sunrise - twilight && hour < sunset + twilight;

                // Cloud takes the shadows as well as the light.
                sun.shadowStrength = Mathf.Lerp(clearShadowStrength, overcastShadowStrength, cloud);
            }

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = Grade(skyAmbient.Evaluate(through), cloud, overcastTint, overcastAmbient);
            RenderSettings.ambientEquatorColor = Grade(horizonAmbient.Evaluate(through), cloud, overcastTint, overcastAmbient);
            RenderSettings.ambientGroundColor = Grade(groundAmbient.Evaluate(through), cloud, overcastTint, overcastAmbient);
            RenderSettings.fogColor = Grade(fog.Evaluate(through), cloud, overcastTint, overcastSkyDim);
            // Written every frame like the colour, which is what lets UnderwaterCameraEffect borrow both in
            // LateUpdate with nothing to agree: it overrides what this set a moment ago, and hands
            // it back to the next frame's.
            RenderSettings.fogStartDistance = Mathf.Lerp(clearFogStart, overcastFogStart, cloud);
            RenderSettings.fogEndDistance = Mathf.Lerp(clearFogEnd, overcastFogEnd, cloud);
        }

        /// <summary>
        /// Washes a colour towards a grey of its own brightness, then dims it: the overcast grade.
        ///
        /// The same arithmetic as the sky shader's (<c>_Overcast</c>), which is the point of having it
        /// here. The fog the sea fades into has to be the colour of the sky at the horizon at every
        /// moment of the change, or the sea's edge shows against it as a band - so the sky and the fog
        /// are graded by one rule, in linear space where the shader does it, not by two that merely
        /// look alike. Amount 0 returns the colour untouched.
        /// </summary>
        public static Color Grade(Color colour, float amount, Color tint, float dim)
        {
            if (amount <= 0f)
                return colour;
            Color lin = colour.linear;
            float lum = 0.2126f * lin.r + 0.7152f * lin.g + 0.0722f * lin.b;
            Color t = tint.linear;
            float tl = Mathf.Max(0.2126f * t.r + 0.7152f * t.g + 0.0722f * t.b, 1e-4f);
            var grey = new Color(lum * t.r / tl, lum * t.g / tl, lum * t.b / tl, 1f);
            Color mixed = Color.Lerp(lin, grey, amount);
            float k = Mathf.Lerp(1f, dim, amount);
            return new Color(mixed.r * k, mixed.g * k, mixed.b * k, colour.a).gamma;
        }

        /// <summary>
        /// How far into night it is: 0 in full day, 1 in full night, and sliding across
        /// the twilight either side. This is what the sky blends on.
        /// </summary>
        public float NightAmount(float hour)
        {
            float dawn = Mathf.InverseLerp(sunrise - twilight, sunrise + twilight, hour);
            float dusk = Mathf.InverseLerp(sunset - twilight, sunset + twilight, hour);
            // Full night before dawn finishes and after dusk begins; day in between.
            return 1f - Mathf.Clamp01(dawn - dusk);
        }
    }
}
