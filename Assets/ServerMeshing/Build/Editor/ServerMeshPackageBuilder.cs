// Server Meshing add-on — package builder (Tools ▸ Open MMORPG ▸ Server Meshing ▸ Build Packages).
//
// Repository only: it lives in Build/, outside every export path, so a project that installs the add-on never gets the
// menu. Keep it out of Mesh/.
//
// Emits the add-on in the layout the Open MMORPG Addon Manager expects, under Addons/ in the repository root:
//
//   Addons/ServerMeshing/dist/ServerMeshing.unitypackage   Assets/ServerMeshing/Mesh (runtime + Editor), the README and a
//   Addons/ServerMeshing/dist/package.json                 Documentation/ snapshot of SETUP.md, DESIGN.md and MULTIHOST.md
//                                                          (the spike notes, the upstream patch series and the raw capacity
//                                                          table stay in the repository). The test rig
//                                                          (Assets/ServerMeshing/TestMap, Tools/ServerMeshing) stays in the
//                                                          repository too: it needs the repository's layout and BaseDemo.
//
// The package carries an empty, extensionless marker file named after the add-on's guid — that is how the Addon Manager
// recognises an installed add-on, so the marker name and the "guid" field in package.json must stay in step. The README.md
// and screenshot.png beside dist/ are authored by hand and are left alone here.
//
// Dependencies are NOT followed (the add-on references the kit): the target project must have the kit installed. The docs
// snapshot is copied into Assets for the export and removed again, so the canonical copies stay outside Assets.
//
// Batch mode:
//   Unity.exe -batchmode -quit -projectPath <repo> -executeMethod MultiplayerARPG.MMO.Mesh.Editor.ServerMeshPackageBuilder.BuildAll
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Editor
{
    public static class ServerMeshPackageBuilder
    {
        // Release metadata. Version is the add-on version reported by the Addon Manager; UpdateDate drives its recency
        // filter and "New" badge, and is bumped by hand with the version rather than tracking the build clock.
        public const string Version = "0.97.4";
        public const string UpdateDate = "2026-10-28";

        // Where the published packages will be fetched from. The Addon Manager downloads "packageUrl" verbatim, so this
        // has to point at wherever dist/ is finally hosted — change it here if these move to another repository.
        private const string DistBaseUrl = "https://github.com/open-mmorpg/open-mmorpg-addons/raw/refs/heads/master";

        private const string AddOnRoot = "Assets/ServerMeshing";
        private const string DocsSnapshot = AddOnRoot + "/Documentation";

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
                "edges, seams between maps, cells of any size, on-demand (\"sleeping\") cells, and dynamic cells the central " +
                "cuts and merges at runtime, by command or by its own load policy.\n" +
                "Dynamic cells are supervised (a dead or hung cell is restarted, a cell cut off from the cluster fences " +
                "itself), buildings follow the cut, and a restarted central adopts the cut layout and resumes the operation " +
                "it was running. A world can span several machines, each running its own map spawner: a cut family's " +
                "cells start, restart and stop on the machine that serves its base. A map can link its cells on demand (each cell only to the cells it needs), so a map is not " +
                "capped at the 64 links one cell takes.\n" +
                "Players cross a cell edge without a loading screen; neighbouring cells exchange ghosts of everything near " +
                "their shared edge, so combat, loot, quests and building resolve across it. Empty land costs nothing: a cell " +
                "with nobody near it does not run.\n" +
                "Subclasses on the kit's managers and entity prefabs plus one world-registry asset; the package changes no " +
                "kit file. Needs OpenMMORPG v1.1.4 (2026-10-25) or later: the add-on does not compile on an " +
                "older kit (see the README), or on a build target without the DISABLE_ADDRESSABLES scripting define. Includes a Setup window that validates a project, judges " +
                "every Dynamic map's cut and settings before anything starts, and fixes what it can.",
            // The marker sits inside Mesh/ so it lands with the code wherever the Addon Manager installs the add-on.
            MarkerFolder = AddOnRoot + "/Mesh",
            ExportPaths = new[] { AddOnRoot + "/Mesh", DocsSnapshot, AddOnRoot + "/README.md" },
        };

        private static readonly Addon[] AllAddons = { MeshAddon };

        [MenuItem("Tools/Open MMORPG/Server Meshing/Build Packages")]
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

        /// <summary>Build writes the package and returns its path, as an array (the menu, batch mode and the rig's package request).</summary>
        internal static string[] Build()
        {
            string addOn = BuildAddOn();
            Debug.Log($"[Mesh] Package written:\n  {addOn}");
            return new[] { addOn };
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

        // Exports one add-on: drops the guid marker into the tree, writes the package and its package.json, then takes the
        // marker out again so it does not sit in the working copy between builds.
        private static string Export(Addon addon)
        {
            // The docs snapshot was copied straight to disk, so the database has to catch up before the
            // export paths can be checked or exported.
            AssetDatabase.Refresh();
            addon.Validate();
            CheckStrayReferences(addon);

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

        // A script the package carries must not use a type declared in a script it leaves behind — unless a declared
        // dependency carries that script. Runtime code that still lived in the spike and rig folders shipped that way
        // once (GhostSuppression, ServerMeshZoneBootstrap, the ProcessArguments partial), and the target project only
        // found out at compile time. Declarations are matched at the start of a line so a "class" in a comment does not
        // count; a reference is any whole-word use of the name, which errs on the side of failing the build.
        private static void CheckStrayReferences(Addon addon)
        {
            List<string> strays = FindStrayReferences(ReadAddOnScripts(), CarriedBy(addon));
            if (strays.Count > 0)
                throw new InvalidOperationException($"{addon.Name}: the package would not compile on its own:\n  " + string.Join("\n  ", strays));
        }

        /// <summary>StrayReferencesOfEveryPackage is the check above over this project's tree, for every package, without building any.</summary>
        internal static List<string> StrayReferencesOfEveryPackage()
        {
            List<KeyValuePair<string, string>> scripts = ReadAddOnScripts();
            List<string> strays = new List<string>();
            foreach (Addon addon in AllAddons)
                foreach (string stray in FindStrayReferences(scripts, CarriedBy(addon)))
                    strays.Add($"{addon.Name}: {stray}");
            return strays;
        }

        /// <summary>
        /// FindStrayReferences lists each use, in a script <paramref name="carried"/> carries, of a type declared in a script it
        /// leaves behind. Scripts are (asset path, text).
        /// </summary>
        internal static List<string> FindStrayReferences(IEnumerable<KeyValuePair<string, string>> scripts, Func<string, bool> carried)
        {
            // A declaration is a class, struct, enum, interface or record (group 1) or a delegate (group 2), after any modifiers.
            Regex declaration = new Regex(@"^\s*(?:(?:public|internal|private|protected|static|sealed|abstract|partial|readonly|ref|unsafe)\s+)*"
                + @"(?:(?:record\s+(?:class\s+|struct\s+)?|class\s+|struct\s+|enum\s+|interface\s+)([A-Za-z_]\w*)|delegate\s+[^(){};=]+?\s+([A-Za-z_]\w*)\s*[(<])", RegexOptions.Multiline);
            Dictionary<string, string> leftBehind = new Dictionary<string, string>(); // type -> declaring asset path
            List<KeyValuePair<string, string>> shipped = new List<KeyValuePair<string, string>>(); // asset path, text
            foreach (KeyValuePair<string, string> script in scripts)
            {
                if (carried(script.Key))
                    shipped.Add(script);
                else
                    foreach (Match m in declaration.Matches(script.Value))
                        leftBehind[m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value] = script.Key;
            }

            List<string> strays = new List<string>();
            foreach (KeyValuePair<string, string> script in shipped)
                foreach (KeyValuePair<string, string> type in leftBehind)
                    if (Regex.IsMatch(script.Value, $@"\b{Regex.Escape(type.Key)}\b"))
                        strays.Add($"{script.Key} uses {type.Key} ({type.Value})");
            return strays;
        }

        /// <summary>Which asset paths a package exports: its own export paths and those of the packages it depends on.</summary>
        private static Func<string, bool> CarriedBy(Addon addon)
        {
            List<string> carried = new List<string>(addon.ExportPaths);
            foreach (string guid in addon.Dependencies ?? new string[0])
                foreach (Addon other in AllAddons)
                    if (other.Guid == guid)
                        carried.AddRange(other.ExportPaths);
            return assetPath => carried.Exists(p => assetPath == p || assetPath.StartsWith(p + "/"));
        }

        /// <summary>Every script under the add-on root, as (asset path, text).</summary>
        private static List<KeyValuePair<string, string>> ReadAddOnScripts()
        {
            string root = Path.Combine(ProjectRoot(), AddOnRoot.Replace('/', Path.DirectorySeparatorChar));
            List<KeyValuePair<string, string>> scripts = new List<KeyValuePair<string, string>>();
            foreach (string file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                string assetPath = AddOnRoot + "/" + file.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, '/').Replace('\\', '/');
                scripts.Add(new KeyValuePair<string, string>(assetPath, File.ReadAllText(file)));
            }
            return scripts;
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
