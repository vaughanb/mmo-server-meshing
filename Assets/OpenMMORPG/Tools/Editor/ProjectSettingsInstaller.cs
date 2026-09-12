using UnityEditor;
using UnityEngine;

namespace OpenMMORPG.Setup
{
    /// <summary>
    /// Imports the project settings the kit expects: input, physics, tags and layers,
    /// quality and time.
    ///
    /// These settings ship inside the kit rather than only in the installer package, so
    /// that anyone who imports the kit on its own, for example from the Asset Store,
    /// still gets the option instead of a project running on Unity's defaults.
    /// </summary>
    public static class ProjectSettingsInstaller
    {
        private const string ARCHIVE_NAME = "OpenMMORPG_Settings";
        private const string RELEASES_URL = "https://github.com/open-mmorpg/OpenMMORPG/releases";

        /// <summary>Files the archive replaces, shown before anything is overwritten.</summary>
        private static readonly string[] ReplacedFiles =
        {
            "ProjectSettings/DynamicsManager.asset",
            "ProjectSettings/InputManager.asset",
            "ProjectSettings/ProjectSettings.asset",
            "ProjectSettings/QualitySettings.asset",
            "ProjectSettings/TagManager.asset",
            "ProjectSettings/TimeManager.asset",
        };

        [MenuItem("Open MMORPG/Install/Import Project Settings", false, -1000)]
        private static void ImportProjectSettings()
        {
            string path = FindArchive();
            if (string.IsNullOrEmpty(path))
            {
                EditorUtility.DisplayDialog(
                    "Settings Not Found",
                    "Could not find " + ARCHIVE_NAME + ".unitypackage in this project.\n\n" +
                    "It normally sits in the kit's Tools/Install folder. Reimport the kit, or download the " +
                    "archive from " + RELEASES_URL + " and import it through Assets > Import Package > Custom Package.",
                    "OK");
                return;
            }

            string fileList = string.Join("\n", ReplacedFiles);
            bool proceed = EditorUtility.DisplayDialog(
                "Import Open MMORPG Project Settings",
                "This replaces the following files:\n\n" + fileList + "\n\n" +
                "Input, physics, tags and layers, quality and time settings will then match what the kit " +
                "expects. Anything you have customised in those files is lost, so do this on a new project " +
                "or when you want to reset them.\n\n" +
                "Unity will ask once more before importing.",
                "Import Settings", "Cancel");

            if (!proceed)
                return;

            // Interactive on purpose: Unity lists what will be written and the user can
            // still back out or deselect individual files.
            AssetDatabase.ImportPackage(path, true);
        }

        /// <summary>
        /// Locates the archive by name rather than a fixed path, so it still works when
        /// the kit folder has been renamed or moved.
        /// </summary>
        private static string FindArchive()
        {
            foreach (string guid in AssetDatabase.FindAssets(ARCHIVE_NAME))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path) && path.EndsWith(ARCHIVE_NAME + ".unitypackage"))
                    return path;
            }

            return null;
        }
    }
}
