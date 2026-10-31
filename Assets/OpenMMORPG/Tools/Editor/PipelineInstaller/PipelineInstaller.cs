using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace OpenMMORPG.Setup
{
    /// <summary>
    /// Puts the right render pipeline's version of the demo's content in the project.
    ///
    /// The kit ships as URP. The materials, shaders, scenes, lights, cameras and the few scripts that call pipeline APIs
    /// differ for HDRP, and one file cannot be both, so the kit carries both versions of just those files as two archives in
    /// <c>Tools/Install/Pipelines</c> (see <c>Tools~/pipeline_packs.py</c>). This class reads the manifest beside them, works
    /// out which version the project holds by hashing the files, and installs the other when asked.
    ///
    /// An install does four things, in this order: any file about to be overwritten or removed that the user has changed from
    /// both stock versions is copied to <c>OpenMMORPG_PipelineBackups</c> beside Assets; the archive is imported without
    /// prompts; the files the new version does not want (the other pipeline's shaders, pipeline assets and scripts) are
    /// removed; and once Unity has finished, the result is checked against the manifest. Nothing else in the project is
    /// touched, and Unity's own project settings are left alone.
    ///
    /// This file uses no render pipeline type, only the pipeline asset's type name, so it compiles in a project with either
    /// pipeline, both or neither.
    /// </summary>
    [InitializeOnLoad]
    public static class PipelineInstaller
    {
        public const string MenuPath = "Tools/Open MMORPG/Install/Render Pipeline Content...";
        private const string ManifestName = "pipelines.json";
        private const string BackupFolder = "OpenMMORPG_PipelineBackups";

        // Survive the domain reload an import causes.
        private const string PendingKey = "OpenMMORPG.PipelineInstall.Pending";
        private const string StageKey = "OpenMMORPG.PipelineInstall.Stage";
        private const string StageImporting = "importing";
        private const string StageSettling = "settling";
        private const string StartedKey = "OpenMMORPG.PipelineInstall.Started";
        private const string ImportedKey = "OpenMMORPG.PipelineInstall.Imported";
        private const string DismissedConfigKey = "OpenMMORPG.PipelineInstallerDismissed";
        /// <summary>What "Don't ask again" stores under <see cref="DismissedConfigKey"/>: every pipeline, not only the current one.</summary>
        private const string DismissedForGood = "*";
        private const string SnoozedKey = "OpenMMORPG.PipelineInstall.Snoozed";
        private const string LastResultKey = "OpenMMORPG.PipelineInstall.LastResult";

        /// <summary>How long to wait for an import that has not reported, before checking what is on disk anyway.</summary>
        private static readonly TimeSpan ImportPatience = TimeSpan.FromSeconds(90);

        /// <summary>How well one pipeline's version of the content matches what is in the project.</summary>
        public class Status
        {
            public int total;
            public int matching;
            public readonly List<string> missing = new List<string>();
            public readonly List<string> different = new List<string>();
            /// <summary>Files that belong to the other pipeline and are still there.</summary>
            public readonly List<string> removalsPresent = new List<string>();
            public bool Complete { get { return missing.Count == 0 && different.Count == 0 && removalsPresent.Count == 0; } }
        }

        private static PipelineManifest s_manifest;
        private static string s_manifestAssetPath;
        private static Dictionary<string, Status> s_status;
        private static Dictionary<string, HashSet<string>> s_known;

        static PipelineInstaller()
        {
            AssetDatabase.importPackageCompleted += OnImportCompleted;
            AssetDatabase.importPackageFailed += OnImportFailed;
            AssetDatabase.importPackageCancelled += OnImportCancelled;
            EditorApplication.update += ResumeWhenSettled;
        }

        // ------------------------------------------------------------------------------------------------ manifest

        public static PipelineManifest Manifest
        {
            get
            {
                if (s_manifest == null)
                    Load();
                return s_manifest;
            }
        }

        /// <summary>Project-relative path of the folder holding the manifest and the archives.</summary>
        public static string PacksFolder
        {
            get
            {
                Load();
                return s_manifestAssetPath == null ? null : s_manifestAssetPath.Substring(0, s_manifestAssetPath.LastIndexOf('/'));
            }
        }

        private static void Load()
        {
            if (s_manifest != null)
                return;
            foreach (string guid in AssetDatabase.FindAssets("pipelines", new[] { "Assets" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith("/Install/Pipelines/" + ManifestName, StringComparison.Ordinal))
                    continue;
                try
                {
                    var manifest = JsonUtility.FromJson<PipelineManifest>(File.ReadAllText(AbsolutePath(path)));
                    if (manifest != null && manifest.pipelines != null && manifest.pipelines.Length > 0)
                    {
                        s_manifest = manifest;
                        s_manifestAssetPath = path;
                        BuildKnownHashes();
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("Could not read " + path + ": " + ex.Message);
                }
            }
        }

        private static void BuildKnownHashes()
        {
            s_known = new Dictionary<string, HashSet<string>>();
            foreach (PipelineInfo pipeline in s_manifest.pipelines)
            {
                foreach (PipelineFile file in pipeline.files ?? new PipelineFile[0])
                {
                    HashSet<string> hashes;
                    if (!s_known.TryGetValue(file.path, out hashes))
                        s_known[file.path] = hashes = new HashSet<string>();
                    hashes.Add(file.hash);
                }
            }
        }

        public static string ProjectRoot
        {
            get { return Directory.GetParent(Application.dataPath).FullName; }
        }

        private static string AbsolutePath(string projectRelative)
        {
            return Path.GetFullPath(Path.Combine(ProjectRoot, projectRelative));
        }

        private static string KitFile(string kitRelative)
        {
            return AbsolutePath(Manifest.prefix + "/" + kitRelative);
        }

        /// <summary>
        /// The kit has to be where its archives were made to import into; a moved kit would be duplicated, not updated.
        /// </summary>
        public static string KitLocationProblem()
        {
            if (Manifest == null)
                return null;
            string packs = PacksFolder;
            string expected = Manifest.prefix + "/Tools/Install/Pipelines";
            if (packs != expected)
                return "The kit is at " + packs.Replace("/Tools/Install/Pipelines", "") + ", but the pipeline archives import into " +
                       Manifest.prefix + ". Move the kit back to " + Manifest.prefix + " to install pipeline content.";
            return null;
        }

        // ---------------------------------------------------------------------------------------------- the project

        /// <summary>The id of the pipeline the project renders with, or null if it is neither of the manifest's (Built-in, a custom one).</summary>
        public static string ProjectPipelineId()
        {
            if (Manifest == null)
                return null;
            RenderPipelineAsset current = GraphicsSettings.currentRenderPipeline;
            if (current == null)
                return null;
            string type = current.GetType().Name;
            foreach (PipelineInfo p in Manifest.pipelines)
            {
                if (p.assetType == type)
                    return p.id;
            }
            return null;
        }

        /// <summary>A short name for what the project is using, for the window.</summary>
        public static string ProjectPipelineDescription()
        {
            RenderPipelineAsset current = GraphicsSettings.currentRenderPipeline;
            if (current == null)
                return "the Built-in Render Pipeline";
            PipelineInfo known = Manifest == null ? null : Manifest.Find(ProjectPipelineId());
            return known != null ? known.name : current.GetType().Name;
        }

        public static string PackageVersion(string packageName)
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/" + packageName);
            return info == null ? null : info.version;
        }

        // ------------------------------------------------------------------------------------------ what is installed

        /// <summary>
        /// SHA-1 of a file as the manifest records it: text (no NUL in its first 8,000 bytes) with CRLF read as LF, because
        /// the same file is CRLF in a Windows working tree and LF in the package, and is the same file.
        /// </summary>
        public static string Hash(string absolutePath)
        {
            byte[] data = File.ReadAllBytes(absolutePath);
            int probe = Math.Min(data.Length, 8000);
            bool text = true;
            for (int i = 0; i < probe; ++i)
            {
                if (data[i] == 0) { text = false; break; }
            }
            if (text)
            {
                int w = 0;
                for (int r = 0; r < data.Length; ++r)
                {
                    if (data[r] == 0x0D && r + 1 < data.Length && data[r + 1] == 0x0A)
                        continue;
                    data[w++] = data[r];
                }
                Array.Resize(ref data, w);
            }
            using (SHA1 sha = SHA1.Create())
                return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }

        public static void Invalidate()
        {
            s_status = null;
        }

        public static Status StatusOf(string id)
        {
            Evaluate();
            Status status;
            return s_status != null && s_status.TryGetValue(id, out status) ? status : null;
        }

        private static void Evaluate()
        {
            if (s_status != null || Manifest == null)
                return;
            s_status = new Dictionary<string, Status>();
            foreach (PipelineInfo pipeline in Manifest.pipelines)
            {
                var status = new Status { total = pipeline.files.Length };
                foreach (PipelineFile file in pipeline.files)
                {
                    string abs = KitFile(file.path);
                    if (!File.Exists(abs))
                        status.missing.Add(file.path);
                    else if (Hash(abs) != file.hash)
                        status.different.Add(file.path);
                    else
                        status.matching++;
                }
                foreach (string rel in pipeline.remove ?? new string[0])
                {
                    if (File.Exists(KitFile(rel)))
                        status.removalsPresent.Add(rel);
                }
                s_status[pipeline.id] = status;
            }
        }

        /// <summary>The id of the pipeline whose content the project holds in full, or null for none or a mixture.</summary>
        public static string InstalledId()
        {
            Evaluate();
            if (s_status == null)
                return null;
            foreach (PipelineInfo pipeline in Manifest.pipelines)
            {
                if (s_status[pipeline.id].Complete)
                    return pipeline.id;
            }
            return null;
        }

        /// <summary>How much of a pipeline's version of the content is in the project exactly as shipped, 0 to 1.</summary>
        public static float MatchFraction(string id)
        {
            Status status = StatusOf(id);
            return status == null || status.total == 0 ? 0f : (float)status.matching / status.total;
        }

        /// <summary>
        /// Past this much of a version in place, the content is that version with edits of the user's own (a developer working
        /// in the project changes scenes and materials), not the other pipeline's.
        /// </summary>
        public const float MostlyThere = 0.6f;

        /// <summary>The project renders with a pipeline the kit has content for, and what it holds is the other one's (or too little of its own).</summary>
        public static bool NeedsContent
        {
            get
            {
                string project = ProjectPipelineId();
                return project != null && InstalledId() != project && MatchFraction(project) < MostlyThere;
            }
        }

        public static string PendingInstall { get { return SessionState.GetString(PendingKey, ""); } }

        /// <summary>
        /// True while the window should be put in front of the user: an install is running, or the content is wrong and the user
        /// has neither said not now in this session nor said don't ask again in this project.
        /// </summary>
        public static bool IsContentPending
        {
            get
            {
                if (PendingInstall != "")
                    return true;
                if (SessionState.GetBool(SnoozedKey, false))
                    return false;
                // A pipeline id here is what "Not now" used to store, for good, before it meant this session only.
                string dismissed = EditorUserSettings.GetConfigValue(DismissedConfigKey);
                return NeedsContent && dismissed != DismissedForGood && dismissed != ProjectPipelineId();
            }
        }

        /// <summary>"Not now", or closing the window: it does not open by itself again until Unity restarts.</summary>
        public static void Snooze()
        {
            SessionState.SetBool(SnoozedKey, true);
        }

        /// <summary>"Don't ask again": it never opens by itself in this project again. The menu item still opens it.</summary>
        public static void Dismiss()
        {
            EditorUserSettings.SetConfigValue(DismissedConfigKey, DismissedForGood);
        }

        public static void ClearDismissal()
        {
            EditorUserSettings.SetConfigValue(DismissedConfigKey, "");
            SessionState.EraseBool(SnoozedKey);
        }

        public static string LastResult
        {
            get { return SessionState.GetString(LastResultKey, ""); }
        }

        // -------------------------------------------------------------------------------------------------- install

        /// <summary>What an install of this pipeline would do, for the confirmation and the window.</summary>
        public class Plan
        {
            public PipelineInfo target;
            public readonly List<string> overwrite = new List<string>();
            public readonly List<string> add = new List<string>();
            public readonly List<string> remove = new List<string>();
            /// <summary>Files that differ from both stock versions: the user's own edits, copied aside before they are replaced.</summary>
            public readonly List<string> modified = new List<string>();
        }

        public static Plan PlanInstall(string id)
        {
            PipelineInfo target = Manifest == null ? null : Manifest.Find(id);
            if (target == null)
                return null;
            var plan = new Plan { target = target };
            foreach (PipelineFile file in target.files)
            {
                string abs = KitFile(file.path);
                if (!File.Exists(abs))
                {
                    plan.add.Add(file.path);
                    continue;
                }
                string hash = Hash(abs);
                if (hash == file.hash)
                    continue;
                plan.overwrite.Add(file.path);
                if (!IsStock(file.path, hash))
                    plan.modified.Add(file.path);
            }
            foreach (string rel in target.remove ?? new string[0])
            {
                string abs = KitFile(rel);
                if (!File.Exists(abs))
                    continue;
                plan.remove.Add(rel);
                if (!IsStock(rel, Hash(abs)))
                    plan.modified.Add(rel);
            }
            return plan;
        }

        private static bool IsStock(string kitRelative, string hash)
        {
            HashSet<string> hashes;
            return s_known != null && s_known.TryGetValue(kitRelative, out hashes) && hashes.Contains(hash);
        }

        /// <summary>
        /// Asks, backs up, imports, then removes. Returns false if it did not start. With <paramref name="confirm"/> false it asks
        /// nothing and reports problems to the Console, for scripts and tests.
        ///
        /// Import comes first and removal after, not the other way round: an HDRP script that calls DemoLights would fail to
        /// compile in the gap if DemoLights were removed before the URP scripts that no longer call it had arrived.
        /// </summary>
        public static bool Install(string id, bool confirm = true)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
                return Refuse("Leave Play mode and wait for Unity to finish compiling and importing, then try again.", confirm);
            string problem = KitLocationProblem();
            if (problem != null)
                return Refuse(problem, confirm);
            Plan plan = PlanInstall(id);
            if (plan == null)
                return Refuse("There is no pipeline called '" + id + "' in the manifest.", confirm);
            string package = PacksFolder + "/" + plan.target.package;
            if (!File.Exists(AbsolutePath(package)))
                return Refuse("Could not find " + package + ". Reimport the kit.", confirm);

            if (confirm)
            {
                string summary = plan.overwrite.Count + " files replaced, " + plan.add.Count + " added, " + plan.remove.Count +
                                 " removed.";
                string backupNote = plan.modified.Count == 0
                    ? "None of them has been edited, so nothing needs saving."
                    : plan.modified.Count + " of them have been edited (they match neither version), and are copied to " +
                      BackupFolder + " beside Assets first.";
                if (!EditorUtility.DisplayDialog("Install " + plan.target.name + " content",
                        summary + "\n\n" + backupNote + "\n\nOnly the demo's pipeline-specific files change: shaders, materials, " +
                        "scenes, prefabs with lights or cameras, and a few scripts. Unity will recompile afterwards.",
                        "Install", "Cancel"))
                    return false;
            }

            string backup = null;
            if (plan.modified.Count > 0)
                backup = BackUp(plan.modified);

            SessionState.SetString(PendingKey, id);
            SessionState.SetString(StageKey, StageImporting);
            SessionState.SetString(StartedKey, DateTime.UtcNow.Ticks.ToString());
            SessionState.SetBool(ImportedKey, false);
            SessionState.SetString(LastResultKey, backup == null ? "" : "Your edited files were copied to " + backup + ".");
            Invalidate();
            AssetDatabase.ImportPackage(package, false);
            return true;
        }

        private static bool Refuse(string message, bool interactive)
        {
            if (interactive)
                EditorUtility.DisplayDialog("Install Render Pipeline Content", message, "OK");
            else
                Debug.LogWarning("[Open MMORPG] " + message);
            return false;
        }

        private static void RemoveFiles(IEnumerable<string> kitRelativePaths)
        {
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (string rel in kitRelativePaths)
                {
                    string assetPath = Manifest.prefix + "/" + rel;
                    if (File.Exists(AbsolutePath(assetPath)) && !AssetDatabase.DeleteAsset(assetPath))
                        Debug.LogWarning("[Open MMORPG] Could not remove " + assetPath);
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
        }

        private static string BackUp(List<string> kitRelativePaths)
        {
            string folder = Path.Combine(Path.Combine(ProjectRoot, BackupFolder), DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            foreach (string rel in kitRelativePaths)
            {
                string source = KitFile(rel);
                if (!File.Exists(source))
                    continue;
                string target = Path.Combine(folder, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(source, target, true);
            }
            return folder;
        }

        public static void RevealBackups()
        {
            string folder = Path.Combine(ProjectRoot, BackupFolder);
            if (Directory.Exists(folder))
                EditorUtility.RevealInFinder(folder);
        }

        // --------------------------------------------------------------------------------------------------- after

        private static void OnImportCompleted(string packageName)
        {
            if (PendingInstall != "")
                SessionState.SetBool(ImportedKey, true);
        }

        private static void OnImportFailed(string packageName, string errorMessage)
        {
            if (PendingInstall == "")
                return;
            SessionState.SetString(LastResultKey, "The import failed: " + errorMessage);
            SessionState.EraseString(PendingKey);
            Invalidate();
        }

        private static void OnImportCancelled(string packageName)
        {
            if (PendingInstall == "")
                return;
            SessionState.SetString(LastResultKey, "The import was cancelled.");
            SessionState.EraseString(PendingKey);
            Invalidate();
        }

        private static void ResumeWhenSettled()
        {
            string pending = PendingInstall;
            if (pending == "")
                return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return;

            long started;
            long.TryParse(SessionState.GetString(StartedKey, "0"), out started);
            TimeSpan waited = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - started);

            if (SessionState.GetString(StageKey, StageImporting) == StageImporting)
            {
                // The import is queued when Install returns and may not have begun: wait for it to say it is done, or for
                // patience to run out.
                if (!SessionState.GetBool(ImportedKey, false) && waited < ImportPatience)
                    return;
                PipelineInfo target = Manifest.Find(pending);
                RemoveFiles(target.remove ?? new string[0]);
                SessionState.SetString(StageKey, StageSettling);
                SessionState.SetString(StartedKey, DateTime.UtcNow.Ticks.ToString());
                return;
            }

            // The deletions are registered and any recompile they cause has started within a moment of the last call.
            if (waited < TimeSpan.FromSeconds(2))
                return;

            SessionState.EraseString(PendingKey);
            SessionState.EraseString(StageKey);
            Invalidate();
            Status status = StatusOf(pending);
            string earlier = SessionState.GetString(LastResultKey, "");
            if (status != null && status.Complete)
            {
                PipelineInfo info = Manifest.Find(pending);
                SessionState.SetString(LastResultKey, ("Installed the " + info.name + " content. " + earlier).Trim());
                Debug.Log("[Open MMORPG] Installed the " + info.name + " content (" + status.total + " files checked).");
            }
            else
            {
                int bad = status == null ? 0 : status.missing.Count + status.different.Count + status.removalsPresent.Count;
                SessionState.SetString(LastResultKey, "The install finished but " + bad + " files are not as they should be. " +
                    "Look at the Console, then try again.");
                if (status != null)
                {
                    foreach (string p in status.missing.Concat(status.different).Concat(status.removalsPresent).Take(20))
                        Debug.LogWarning("[Open MMORPG] After the install, " + p + " is not as it should be.");
                }
            }
            PipelineInstallerWindow.RepaintIfOpen();
        }

        // --------------------------------------------------------------------------------- packages no longer needed

        /// <summary>
        /// Packages the installed content does not need and that are in the project: the other pipeline's. The window lists
        /// them for the user to remove in Package Manager; the kit never adds or removes a package itself (Asset Store
        /// guideline 2.5.1.e).
        /// </summary>
        public static List<string> UnneededPackages()
        {
            var result = new List<string>();
            string installed = InstalledId();
            PipelineInfo info = installed == null || Manifest == null ? null : Manifest.Find(installed);
            if (info == null || info.packagesNoLongerNeeded == null)
                return result;
            foreach (string name in info.packagesNoLongerNeeded)
            {
                if (PackageVersion(name) != null)
                    result.Add(name);
            }
            return result;
        }
    }
}
