// Server Meshing add-on — package builder (Server Meshing ▸ Build Packages).
//
// Exports two Unity packages into Build/Packages/:
//   ServerMeshing-<version>.unitypackage         the add-on: Assets/ServerMeshing/Mesh (runtime + Editor), the README and a
//                                                Documentation/ snapshot of SETUP.md, DESIGN.md and MULTIHOST.md (the spike notes, the upstream
//                                                patch series and the raw capacity table stay in the repository)
//   ServerMeshing-TestRig-<version>.unitypackage the test rig: Assets/ServerMeshing/TestMap (scenes, prefabs, test data, auto
//                                                client, launcher, play runner, load generator) and a snapshot of
//                                                Tools/ServerMeshing (regress.py, loadtest.py) under TestMap/Tools/
// Dependencies are NOT followed (the rig references the kit's demo prefabs; the add-on references the kit): the target
// project must have the kit installed, and the demo for the rig. The snapshots are copied into Assets for the export and
// removed again, so the canonical docs and tools stay outside Assets.
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Editor
{
    public static class ServerMeshPackageBuilder
    {
        public const string Version = "0.70.0";
        private const string AddOnRoot = "Assets/ServerMeshing";
        private const string DocsSnapshot = AddOnRoot + "/Documentation";
        private const string ToolsSnapshot = AddOnRoot + "/TestMap/Tools";

        [MenuItem("Server Meshing/Build Packages")]
        public static void BuildAll()
        {
            string outDir = Path.Combine(ProjectRoot(), "Build", "Packages");
            Directory.CreateDirectory(outDir);
            string addOn = BuildAddOn(outDir);
            string rig = BuildTestRig(outDir);
            Debug.Log($"[Mesh] Packages written:\n  {addOn}\n  {rig}");
            EditorUtility.RevealInFinder(addOn);
        }

        public static string BuildAddOn(string outDir)
        {
            string file = Path.Combine(outDir, $"ServerMeshing-{Version}.unitypackage");
            CopyFiles(Path.Combine(ProjectRoot(), "Docs", "ServerMeshing"), DocsSnapshot, "SETUP.md", "DESIGN.md", "MULTIHOST.md");
            try
            {
                AssetDatabase.Refresh();
                AssetDatabase.ExportPackage(new[] { AddOnRoot + "/Mesh", DocsSnapshot, AddOnRoot + "/README.md" }, file, ExportPackageOptions.Recurse);
            }
            finally
            {
                RemoveTree(DocsSnapshot);
                AssetDatabase.Refresh();
            }
            return file;
        }

        public static string BuildTestRig(string outDir)
        {
            string file = Path.Combine(outDir, $"ServerMeshing-TestRig-{Version}.unitypackage");
            CopyTree(Path.Combine(ProjectRoot(), "Tools", "ServerMeshing"), ToolsSnapshot, ".py", ".md");
            try
            {
                AssetDatabase.Refresh();
                AssetDatabase.ExportPackage(new[] { AddOnRoot + "/TestMap" }, file, ExportPackageOptions.Recurse);
            }
            finally
            {
                RemoveTree(ToolsSnapshot);
                AssetDatabase.Refresh();
            }
            return file;
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
                if (System.Array.IndexOf(extensions, Path.GetExtension(source).ToLowerInvariant()) < 0)
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
    }
}
