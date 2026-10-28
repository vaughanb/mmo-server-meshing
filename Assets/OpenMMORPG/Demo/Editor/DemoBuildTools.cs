using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
#if UNITY_6000_0_OR_NEWER
using UnityEditor.Build.Profile;
#endif
using UnityEngine;

namespace MultiplayerARPG.Demo.EditorTools
{
    public static class DemoBuildTools
    {
        public const string DemoServerProfilePath = "Assets/OpenMMORPG/Demo/Build Profiles/Demo Map Server.asset";
        public const string ServerBuildRelativePath = "builds/OpenMMORPG.exe";

        public static readonly string[] DemoScenes = new string[]
        {
            "Assets/OpenMMORPG/Demo/Scenes/00Init.unity",
            "Assets/OpenMMORPG/Demo/Scenes/01Home.unity",
            "Assets/OpenMMORPG/Demo/Scenes/DemoMap.unity",
            "Assets/OpenMMORPG/Demo/Scenes/DemoDungeon.unity",
        };

        public static string GetServerBuildFullPath()
        {
            return Path.Combine(Path.GetDirectoryName(Application.dataPath), ServerBuildRelativePath);
        }

        public static bool IsServerBuilt()
        {
            return File.Exists(GetServerBuildFullPath());
        }

        [MenuItem("Tools/Open MMORPG/Demo/Build Demo Map Server", false, 1)]
        [MenuItem("Tools/Open MMORPG/Build/Build Demo Map Server", false, -1001)]
        public static void BuildDemoMapServerMenu()
        {
            BuildDemoMapServer(false);
        }

        public static bool BuildDemoMapServer(bool silent = false)
        {
            string fullExePath = GetServerBuildFullPath();
            string buildDir = Path.GetDirectoryName(fullExePath);

            if (!Directory.Exists(buildDir))
            {
                Directory.CreateDirectory(buildDir);
            }

            BuildReport report = null;

#if UNITY_6000_0_OR_NEWER
            BuildProfile profile = AssetDatabase.LoadAssetAtPath<BuildProfile>(DemoServerProfilePath);
            if (profile != null)
            {
                try
                {
                    Debug.Log($"[Open MMORPG] Building Demo Map Server using profile '{profile.name}' to: {fullExePath}");
                    var profileOptions = new BuildPlayerWithProfileOptions
                    {
                        buildProfile = profile,
                        locationPathName = fullExePath,
                        options = BuildOptions.None,
                    };
                    report = BuildPipeline.BuildPlayer(profileOptions);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Open MMORPG] Profile build threw exception: {ex.Message}. Falling back to standard standalone build...");
                }
            }
#endif

            // Fallback if profile build was not used or failed
            if (report == null || report.summary.result != BuildResult.Succeeded)
            {
                Debug.Log($"[Open MMORPG] Building Demo Map Server using standalone player fallback to: {fullExePath}");
                var playerOptions = new BuildPlayerOptions
                {
                    scenes = DemoScenes,
                    locationPathName = fullExePath,
                    target = EditorUserBuildSettings.activeBuildTarget,
                    subtarget = (int)StandaloneBuildSubtarget.Player,
                    options = BuildOptions.None,
                };
                report = BuildPipeline.BuildPlayer(playerOptions);
            }

            bool success = report != null && report.summary.result == BuildResult.Succeeded;

            if (success)
            {
                Debug.Log($"[Open MMORPG] Demo Map Server successfully built! Output: {fullExePath} (Duration: {report.summary.totalTime.TotalSeconds:F1}s)");
                if (!silent)
                {
                    EditorUtility.DisplayDialog(
                        "Demo Map Server Built",
                        $"The Demo Map Server has been successfully built to:\n\n{fullExePath}\n\nYou can now open '00Init' and press Play to experience the demo!",
                        "OK");
                }
            }
            else
            {
                string errors = report != null ? $"Errors: {report.summary.totalErrors}" : "Unknown error";
                Debug.LogError($"[Open MMORPG] Failed to build Demo Map Server. {errors}");
                if (!silent)
                {
                    EditorUtility.DisplayDialog(
                        "Build Failed",
                        $"Failed to build the Demo Map Server.\n\n{errors}\n\nPlease check the Unity Console for detailed build errors.",
                        "OK");
                }
            }

            return success;
        }
    }
}
