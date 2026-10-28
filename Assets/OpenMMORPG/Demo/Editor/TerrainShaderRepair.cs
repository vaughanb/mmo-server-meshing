using UnityEditor;
using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// Puts URP's far-terrain shader back after a dedicated-server build leaves it broken.
    ///
    /// **The symptom:** play the demo in the editor after building the map server (the
    /// `Demo Map Server` build profile, see the README), walk or swim more than
    /// <c>Terrain.basemapDistance</c> (220 m) from the island, and the whole island goes solid magenta.
    ///
    /// **The cause:** a terrain does not draw itself with its own shader past that distance; it swaps
    /// to <c>Hidden/Universal Render Pipeline/Terrain/Lit (Base Pass)</c>, which the terrain material
    /// names only as a *dependency*, so nothing in a scene ever references it. A dedicated server is
    /// built by switching the editor to the Server subtarget, which has no graphics,
    /// so that shader is imported with every subshader removed (the editor log says "Shader Unsupported:
    /// ... All subshaders removed"). Switching back to the Player subtarget reimports everything that a
    /// scene or a material reaches, but not this one, so it stays a one-pass stub that Unity draws as the
    /// error shader. Verified by doing the round trip with no build at all: the base pass went from 8 passes
    /// to 1 and a quad drawn with it came out (1, 0, 1).
    ///
    /// It is the editor's loaded copy that is wrong, not the project - a player build compiles its own, and
    /// nothing on disk changes - so reimporting the shader is the whole repair. This does that, on its own,
    /// whenever the editor comes up on the Player subtarget and finds the shader degraded, so a map-server
    /// build can no longer leave the demo with a pink island. <c>Repair Terrain Shaders</c> does it by hand.
    /// </summary>
    [InitializeOnLoad]
    public static class TerrainShaderRepair
    {
        /// <summary>The terrain's far-distance shaders; the first is the one that breaks.</summary>
        private static readonly string[] Shaders =
        {
            "Hidden/Universal Render Pipeline/Terrain/Lit (Base Pass)",
            "Hidden/Universal Render Pipeline/Terrain/Lit (Add Pass)",
            "Hidden/Universal Render Pipeline/Terrain/Lit (Basemap Gen)",
        };

        private const string Folder = "Packages/com.unity.render-pipelines.universal/Shaders/Terrain/";
        private static readonly string[] Sources = { "TerrainLitBase.shader", "TerrainLitAdd.shader", "TerrainLitBasemapGen.shader" };

        /// <summary>
        /// The healthy base pass has eight passes; the broken one reports a single stub pass. Anything
        /// under two means "degraded", which keeps this from tripping on a shader that URP later trims.
        /// </summary>
        private const int HealthyPassFloor = 2;

        private const double WatchSeconds = 60.0;
        private static double _stopAt;

        static TerrainShaderRepair()
        {
            // Not on the spot: the switch back to the Player subtarget recompiles and reimports around
            // the domain reload, and a repair made before that finishes would be undone by it. Watch
            // for a while after every reload instead, and act once the editor has settled.
            _stopAt = EditorApplication.timeSinceStartup + WatchSeconds;
            EditorApplication.update += Poll;
        }

        private static void Poll()
        {
            if (EditorApplication.timeSinceStartup > _stopAt)
            {
                EditorApplication.update -= Poll;
                return;
            }
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            // On the Server subtarget the shader is *meant* to be a stub, and reimporting it there
            // would only reproduce the stub. Wait for the way back.
            if (EditorUserBuildSettings.standaloneBuildSubtarget != StandaloneBuildSubtarget.Player)
                return;

            if (Degraded())
            {
                Repair();
                EditorApplication.update -= Poll;
            }
        }

        private static bool Degraded()
        {
            Shader baseShader = Shader.Find(Shaders[0]);
            return baseShader != null && baseShader.passCount < HealthyPassFloor;
        }

        [MenuItem("Tools/Open MMORPG/Demo/Repair Terrain Shaders")]
        public static void Repair()
        {
            foreach (string source in Sources)
                AssetDatabase.ImportAsset(Folder + source, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            Shader baseShader = Shader.Find(Shaders[0]);
            Debug.Log($"[{nameof(TerrainShaderRepair)}] Reimported URP's terrain base-pass shaders" +
                      (baseShader != null ? $" (the base pass has {baseShader.passCount} passes). " : ". ") +
                      "A dedicated-server build leaves them stubbed, which draws terrain magenta past the base-map distance.");
        }
    }
}
