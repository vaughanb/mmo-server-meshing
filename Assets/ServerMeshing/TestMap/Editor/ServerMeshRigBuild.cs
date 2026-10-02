// Server Meshing test rig — builds the player the harness runs: the zone servers and the headless bots
// (Build/Server/kit-ce-project.exe). A Player build, not the Server subtarget: the bots are clients and need the client
// code the Server subtarget strips. The kit's Builder.BuildWindows64Server is no substitute here — with
// DISABLE_ADDRESSABLES defined it builds nothing and still exits 0.
//
// Reached from the menu, from batch mode (-executeMethod MultiplayerARPG.MMO.Mesh.Editor.ServerMeshRigBuild.BuildFromCommandLine)
// and from the harness through ServerMeshPlayRunner's Temp/ServerMesh.BuildRequest.txt leg.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Editor
{
    /// <summary>ServerMeshRigBuild builds the rig's Windows player into Build/Server.</summary>
    public static class ServerMeshRigBuild
    {
        public const string OutputRelative = "Build/Server/kit-ce-project.exe";
        /// <summary>What the build was made from: a SHA-1 per player-side source and asset (Tools/ServerMeshing/rigtools.py
        /// computes the same set and reports the build stale when anything differs).</summary>
        public const string ManifestRelative = "Build/Server/rigbuild-manifest.txt";
        private static readonly string[] ManifestExtensions = { ".cs", ".asmdef", ".asset", ".prefab", ".unity" };

        /// <summary>Build builds the player and returns "ok ..." or "error: ..." describing the outcome.</summary>
        public static string Build()
        {
            string root = Path.GetDirectoryName(Application.dataPath) ?? "";
            string[] scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            if (scenes.Length == 0)
                return "error: no enabled scenes in the build settings";
            BuildPlayerOptions options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = Path.Combine(root, OutputRelative),
                target = BuildTarget.StandaloneWindows64,
                subtarget = (int)StandaloneBuildSubtarget.Player,
                options = BuildOptions.None,
            };
            string manifest = BuildManifest(root);
            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;
            if (summary.result != BuildResult.Succeeded)
                return $"error: build {summary.result} with {summary.totalErrors} error(s)";
            try
            {
                File.WriteAllText(Path.Combine(root, ManifestRelative), manifest);
            }
            catch (IOException ex)
            {
                return $"error: the build succeeded but its manifest could not be written: {ex.Message}";
            }
            return $"ok {summary.totalSize / (1024 * 1024)} MB in {summary.totalTime.TotalSeconds:0} s, {scenes.Length} scenes";
        }

        /// <summary>
        /// BuildManifest lists "sha1 path" for every player-side source and asset: .cs and .asmdef files outside Editor
        /// folders, and .asset, .prefab and .unity files, under Assets and ProjectSettings. Taken before the build, so
        /// an edit made while it runs marks the build stale rather than passing unnoticed. Each file is hashed with its
        /// CRLF line ends turned into LF (rigtools.normalized_bytes does the same): the editor re-saves YAML assets with
        /// either, a render texture even during a build, and a line-end change does not change what is built.
        /// </summary>
        public static string BuildManifest(string root)
        {
            List<string> paths = new List<string>();
            foreach (string top in new[] { "Assets", "ProjectSettings" })
            {
                string dir = Path.Combine(root, top);
                if (!Directory.Exists(dir))
                    continue;
                foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    string rel = file.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/');
                    if (InManifest(rel))
                        paths.Add(rel);
                }
            }
            paths.Sort(StringComparer.Ordinal);
            StringBuilder text = new StringBuilder();
            using (SHA1 sha = SHA1.Create())
            {
                foreach (string rel in paths)
                {
                    byte[] hash = sha.ComputeHash(NormalizedBytes(File.ReadAllBytes(Path.Combine(root, rel))));
                    text.Append(BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant()).Append(' ').Append(rel).Append('\n');
                }
            }
            return text.ToString();
        }

        /// <summary>NormalizedBytes is the content with every CR that ends a line (CR followed by LF) removed.</summary>
        public static byte[] NormalizedBytes(byte[] data)
        {
            byte[] result = new byte[data.Length];
            int n = 0;
            for (int i = 0; i < data.Length; ++i)
            {
                if (data[i] == (byte)'\r' && i + 1 < data.Length && data[i + 1] == (byte)'\n')
                    continue;
                result[n++] = data[i];
            }
            Array.Resize(ref result, n);
            return result;
        }

        /// <summary>InManifest says whether a project-relative path (forward slashes) belongs in the build manifest.</summary>
        public static bool InManifest(string rel)
        {
            string ext = Path.GetExtension(rel).ToLowerInvariant();
            if (Array.IndexOf(ManifestExtensions, ext) < 0)
                return false;
            if (ext == ".cs" || ext == ".asmdef")
                return Array.IndexOf(rel.Split('/'), "Editor") < 0; // editor-only code is not in the player
            return true;
        }

        [MenuItem("Server Meshing/Test Rig/Build Player (Build/Server)")]
        public static void BuildFromMenu()
        {
            string result = Build();
            if (result.StartsWith("ok"))
                Debug.Log("[ServerMesh] Rig build " + result);
            else
                Debug.LogError("[ServerMesh] Rig build " + result);
        }

        /// <summary>BuildFromCommandLine is the batch-mode entry point; it exits 0 on success and 1 on failure.</summary>
        public static void BuildFromCommandLine()
        {
            string result;
            try
            {
                result = Build();
            }
            catch (Exception ex)
            {
                result = "error: " + ex;
            }
            Debug.Log("[ServerMesh] Rig build " + result);
            EditorApplication.Exit(result.StartsWith("ok") ? 0 : 1);
        }
    }
}
