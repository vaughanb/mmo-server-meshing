// Server Meshing add-on — package builder (Server Meshing ▸ Build Packages).
//
// Emits the two add-ons in the layout the Open MMORPG Addon Manager expects, under Addons/ in the repository root:
//
//   Addons/ServerMeshing/dist/ServerMeshing.unitypackage             the add-on: Assets/ServerMeshing/Mesh (runtime + Editor),
//   Addons/ServerMeshing/dist/package.json                           the README and a Documentation/ snapshot of SETUP.md,
//                                                                    DESIGN.md and MULTIHOST.md (the spike notes, the upstream
//                                                                    patch series and the raw capacity table stay in the repository)
//   Addons/ServerMeshingTestRig/dist/ServerMeshingTestRig.unitypackage  the test rig: Assets/ServerMeshing/TestMap (scenes, prefabs,
//   Addons/ServerMeshingTestRig/dist/package.json                       test data, auto client, launcher, play runner, load
//                                                                       generator) and a snapshot of Tools/ServerMeshing
//                                                                       (regress.py, loadtest.py) under TestMap/Tools/
//
// Each package carries an empty, extensionless marker file named after the add-on's guid — that is how the Addon Manager
// recognises an installed add-on, so the marker name and the "guid" field in package.json must stay in step. The README.md
// and screenshot.png beside each dist/ are authored by hand and are left alone here.
//
// Dependencies are NOT followed (the rig references the kit's demo prefabs; the add-on references the kit): the target
// project must have the kit installed, and the demo for the rig. The docs and tools snapshots are copied into Assets for
// the export and removed again, so the canonical copies stay outside Assets.
//
// Batch mode:
//   Unity.exe -batchmode -quit -projectPath <repo> -executeMethod MultiplayerARPG.MMO.Mesh.Editor.ServerMeshPackageBuilder.BuildAll
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Editor
{
    public static class ServerMeshPackageBuilder
    {
        // Release metadata. Version is the add-on version reported by the Addon Manager; UpdateDate drives its recency
        // filter and "New" badge, and is bumped by hand with the version rather than tracking the build clock.
        public const string Version = "0.70.0";
        public const string UpdateDate = "2026-09-18";

        // Where the published packages will be fetched from. The Addon Manager downloads "packageUrl" verbatim, so this
        // has to point at wherever dist/ is finally hosted — change it here if these move to another repository.
        private const string DistBaseUrl = "https://github.com/vaughanb/mmo-server-meshing/raw/refs/heads/master/Addons";

        private const string AddOnRoot = "Assets/ServerMeshing";
        private const string DocsSnapshot = AddOnRoot + "/Documentation";
        private const string ToolsSnapshot = AddOnRoot + "/TestMap/Tools";

        // The categories the Addon Manager filters on; anything else is shown as "Uncategorized" and hidden from filters.
        private static readonly string[] Categories =
        {
            "Demo", "Characters", "Monsters", "NPCs", "Combat", "Economy",
            "Items", "Gameplay", "UI", "MMO", "Integration", "Tools",
        };

        private static readonly Addon MeshAddon = new Addon
        {
            Folder = "ServerMeshing",
            Name = "Server Meshing",
            Guid = "87f3a860-3714-407a-8bd2-3ae097a78867",
            Category = "MMO",
            Description =
                "Splits a map across several map-server processes (\"cells\") with seamless handoff, ghosting across cell " +
                "edges, seams between maps, cells of any size and on-demand (\"sleeping\") cells.\n" +
                "Players cross a cell edge without a loading screen; neighbouring cells exchange ghosts of everything near " +
                "their shared edge, so combat, loot, quests and building resolve across it. Empty land costs nothing: a cell " +
                "with nobody near it does not run.\n" +
                "Subclasses on the kit's managers and entity prefabs plus one world-registry asset — the kit's core is not " +
                "modified. Includes a Setup window that validates a project and fixes what it can.",
            // The marker sits inside Mesh/ so it lands with the code wherever the Addon Manager installs the add-on.
            MarkerFolder = AddOnRoot + "/Mesh",
            ExportPaths = new[] { AddOnRoot + "/Mesh", DocsSnapshot, AddOnRoot + "/README.md" },
        };

        private static readonly Addon TestRigAddon = new Addon
        {
            Folder = "ServerMeshingTestRig",
            Name = "Server Meshing Test Rig",
            Guid = "6a71ac12-0405-44d7-b1c1-003df8ffd085",
            Category = "Demo",
            Description =
                "The test rig for the Server Meshing add-on: two test maps cut into ten cells (a 3x3 grid plus one cell " +
                "across a seam), test items, skills, quests, mounts, pets, harvestables and buildings that exercise every " +
                "path across a cell edge.\n" +
                "Adds an auto client, an editor launcher and play runner, a load generator, and the Python regression and " +
                "capacity harnesses under TestMap/Tools.\n" +
                "Needs the Server Meshing add-on and the kit's BaseDemo (the test data references its enemy prefab and " +
                "default weapon).",
            Dependencies = new[] { "87f3a860-3714-407a-8bd2-3ae097a78867" },
            MarkerFolder = AddOnRoot + "/TestMap",
            ExportPaths = new[] { AddOnRoot + "/TestMap" },
        };

        [MenuItem("Server Meshing/Build Packages")]
        public static void BuildAllFromMenu()
        {
            string[] written = Build();
            EditorUtility.RevealInFinder(written[0]);
        }

        // Batch-mode entry point: no dialogs, and a non-zero exit code if anything throws.
        public static void BuildAll()
        {
            try
            {
                Build();
                if (Application.isBatchMode)
                    EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Mesh] Package build failed: {e}");
                if (Application.isBatchMode)
                    EditorApplication.Exit(1);
                else
                    throw;
            }
        }

        private static string[] Build()
        {
            string addOn = BuildAddOn();
            string rig = BuildTestRig();
            Debug.Log($"[Mesh] Packages written:\n  {addOn}\n  {rig}");
            return new[] { addOn, rig };
        }

        public static string BuildAddOn()
        {
            CopyFiles(Path.Combine(ProjectRoot(), "Docs", "ServerMeshing"), DocsSnapshot, "SETUP.md", "DESIGN.md", "MULTIHOST.md");
            try
            {
                return Export(MeshAddon);
            }
            finally
            {
                RemoveTree(DocsSnapshot);
                AssetDatabase.Refresh();
            }
        }

        public static string BuildTestRig()
        {
            CopyTree(Path.Combine(ProjectRoot(), "Tools", "ServerMeshing"), ToolsSnapshot, ".py", ".md");
            try
            {
                return Export(TestRigAddon);
            }
            finally
            {
                RemoveTree(ToolsSnapshot);
                AssetDatabase.Refresh();
            }
        }

        // Exports one add-on: drops the guid marker into the tree, writes the package and its package.json, then takes the
        // marker out again so it does not sit in the working copy between builds.
        private static string Export(Addon addon)
        {
            // The docs and tools snapshots were copied straight to disk, so the database has to catch up before the
            // export paths can be checked or exported.
            AssetDatabase.Refresh();
            addon.Validate();

            string distDir = Path.Combine(ProjectRoot(), "Addons", addon.Folder, "dist");
            Directory.CreateDirectory(distDir);
            string file = Path.Combine(distDir, addon.Folder + ".unitypackage");

            string markerAssetPath = addon.MarkerFolder + "/" + addon.Guid;
            string markerFile = Path.Combine(ProjectRoot(), markerAssetPath.Replace('/', Path.DirectorySeparatorChar));
            File.WriteAllBytes(markerFile, new byte[0]);
            try
            {
                AssetDatabase.Refresh();
                // ExportPackage takes the add-on's own folders, never "Assets" itself: the Addon Manager installs the
                // package under Open MMORPG_addons/<category>/, and a top-level Assets entry would fight that.
                List<string> paths = new List<string>(addon.ExportPaths) { markerAssetPath };
                AssetDatabase.ExportPackage(paths.ToArray(), file, ExportPackageOptions.Recurse);
            }
            finally
            {
                AssetDatabase.DeleteAsset(markerAssetPath);
                if (File.Exists(markerFile))
                    File.Delete(markerFile);
                if (File.Exists(markerFile + ".meta"))
                    File.Delete(markerFile + ".meta");
                AssetDatabase.Refresh();
            }

            File.WriteAllText(Path.Combine(distDir, "package.json"), Manifest(addon), new UTF8Encoding(false));
            return file;
        }

        // package.json in the Addon Manager's field order. Hand-written rather than JsonUtility, which cannot express the
        // nested author object or a bare string array without a wrapper type per field.
        private static string Manifest(Addon addon)
        {
            StringBuilder json = new StringBuilder();
            json.Append("{\n");
            json.Append($"  \"name\": {Quote(addon.Name)},\n");
            json.Append($"  \"guid\": {Quote(addon.Guid)},\n");
            json.Append($"  \"packageUrl\": {Quote($"{DistBaseUrl}/{addon.Folder}/dist/{addon.Folder}.unitypackage")},\n");
            json.Append($"  \"version\": {Quote(Version)},\n");
            json.Append($"  \"updateDate\": {Quote(UpdateDate)},\n");
            json.Append($"  \"category\": {Quote(addon.Category)},\n");
            json.Append($"  \"description\": {Quote(addon.Description)},\n");
            json.Append("  \"author\": {\n");
            json.Append("    \"name\": \"vaughanb\",\n");
            json.Append("    \"url\": \"https://github.com/vaughanb\"\n");
            json.Append("  },\n");
            json.Append("  \"screenshot\": \"screenshot.png\"");
            if (addon.Dependencies != null && addon.Dependencies.Length > 0)
            {
                json.Append(",\n  \"dependencies\": [");
                for (int i = 0; i < addon.Dependencies.Length; ++i)
                    json.Append((i > 0 ? ", " : string.Empty) + Quote(addon.Dependencies[i]));
                json.Append("]");
            }
            json.Append("\n}\n");
            return json.ToString();
        }

        private static string Quote(string value)
        {
            StringBuilder escaped = new StringBuilder("\"");
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': escaped.Append("\\\""); break;
                    case '\\': escaped.Append("\\\\"); break;
                    case '\n': escaped.Append("\\n"); break;
                    case '\r': escaped.Append("\\r"); break;
                    case '\t': escaped.Append("\\t"); break;
                    default:
                        if (c < ' ')
                            escaped.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            escaped.Append(c);
                        break;
                }
            }
            return escaped.Append('"').ToString();
        }

        private static string ProjectRoot()
        {
            return Path.GetDirectoryName(Application.dataPath);
        }

        private static void CopyFiles(string from, string toAssetPath, params string[] names)
        {
            string to = Path.Combine(ProjectRoot(), toAssetPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(to);
            foreach (string name in names)
                File.Copy(Path.Combine(from, name), Path.Combine(to, name), true);
        }

        private static void CopyTree(string from, string toAssetPath, params string[] extensions)
        {
            string to = Path.Combine(ProjectRoot(), toAssetPath.Replace('/', Path.DirectorySeparatorChar));
            foreach (string source in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                if (Array.IndexOf(extensions, Path.GetExtension(source).ToLowerInvariant()) < 0)
                    continue;
                if (Path.GetFileName(source) == "buildpackages.py")
                    continue; // the repository's own packaging script is not part of the rig
                string relative = source.Substring(from.Length).TrimStart(Path.DirectorySeparatorChar, '/');
                string target = Path.Combine(to, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(source, target, true);
            }
        }

        private static void RemoveTree(string assetPath)
        {
            if (AssetDatabase.IsValidFolder(assetPath))
                AssetDatabase.DeleteAsset(assetPath);
            string dir = Path.Combine(ProjectRoot(), assetPath.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
            if (File.Exists(dir + ".meta"))
                File.Delete(dir + ".meta");
        }

        private sealed class Addon
        {
            public string Folder;        // repository folder and .unitypackage file name
            public string Name;          // shown in the Addon Manager, and used for the installed folder name
            public string Guid;          // unique id; matches the marker file inside the package
            public string Category;      // one of Categories
            public string Description;
            public string[] Dependencies;
            public string MarkerFolder;  // asset folder the guid marker is written into
            public string[] ExportPaths; // asset paths handed to ExportPackage

            public void Validate()
            {
                if (Array.IndexOf(Categories, Category) < 0)
                    throw new InvalidOperationException($"{Name}: \"{Category}\" is not an Addon Manager category ({string.Join(", ", Categories)}).");
                if (!System.Guid.TryParse(Guid, out _))
                    throw new InvalidOperationException($"{Name}: guid \"{Guid}\" is not a guid.");
                if (!AssetDatabase.IsValidFolder(MarkerFolder))
                    throw new InvalidOperationException($"{Name}: marker folder \"{MarkerFolder}\" does not exist.");
                foreach (string path in ExportPaths)
                {
                    if (!AssetDatabase.IsValidFolder(path) && AssetDatabase.LoadMainAssetAtPath(path) == null)
                        throw new InvalidOperationException($"{Name}: export path \"{path}\" does not exist.");
                }
            }
        }
    }
}
