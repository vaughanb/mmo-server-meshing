using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace OpenMMORPG.KitUpdater
{
    /// <summary>
    /// Applies a <see cref="KitUpdatePlan"/>: downloads what it writes, checks every file against the target manifest's hash,
    /// backs up everything it is about to change, then moves, writes and deletes, records the target manifest as the
    /// installed one and lets Unity import and compile. If the result does not compile it offers to restore the backup.
    /// It never changes the project's packages: a release that needs newer ones waits until the user has added them.
    ///
    /// Progress is kept in <c>Library/OpenMMORPGUpdater/state.json</c>, because a successful compile reloads the editor's
    /// code and this class with it. Backups go to <c>OpenMMORPGBackups</c> beside Assets, out of Unity's sight.
    /// </summary>
    [InitializeOnLoad]
    public static class KitUpdater
    {
        public const string BackupRoot = "OpenMMORPGBackups";
        private const string WorkRoot = "Library/OpenMMORPGUpdater";
        private const string StatePath = WorkRoot + "/state.json";
        private const string StagingRoot = WorkRoot + "/Staging";
        private const string RestoreFile = "restore.json";
        private const int Downloads = 8;
        /// <summary>How long Unity must sit idle after an update before the compile result counts.</summary>
        private static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);

        public class State
        {
            /// <summary>"applying" until Unity has imported and compiled, then "done" or "failed"; "restored" after a restore.</summary>
            public string stage;
            public string from;
            public string to;
            public string fromTag;
            public string toTag;
            /// <summary>The backup folder, relative to the project.</summary>
            public string backup;
            public string compareUrl;
            /// <summary>Kit files left as the user had them, for the summary: project-relative paths.</summary>
            public List<string> kept = new List<string>();
            public long appliedTicks;
            public string message;
        }

        private class RestoreRecord
        {
            public string from;
            public string to;
            /// <summary>Project-relative paths copied into the backup, each with its .meta when it had one.</summary>
            public List<string> files = new List<string>();
            /// <summary>Project-relative paths the update created, files and folders.</summary>
            public List<string> added = new List<string>();
            public List<Move> moves = new List<Move>();
        }

        private class Move
        {
            public string guid;
            public string from;
            public string to;
        }

        private static DateTime s_idleSince = DateTime.MaxValue;

        static KitUpdater()
        {
            EditorApplication.update += Watch;
        }

        public static State LoadState()
        {
            try
            {
                return File.Exists(StatePath) ? JsonConvert.DeserializeObject<State>(File.ReadAllText(StatePath)) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Hides the last update's summary. Its backup stays where it is.</summary>
        public static void Dismiss()
        {
            State state = LoadState();
            if (state == null || state.stage == "applying")
                return;
            state.stage = "seen";
            SaveState(state);
        }

        private static void SaveState(State state)
        {
            Directory.CreateDirectory(WorkRoot);
            File.WriteAllText(StatePath, JsonConvert.SerializeObject(state, Formatting.Indented));
        }

        /// <summary>Why the kit cannot be updated in place right now, or null.</summary>
        public static string Blocker(KitUpdatePlan plan)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return "Leave Play mode first.";
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return "Wait for Unity to finish importing and compiling.";
            if (EditorUtility.scriptCompilationFailed)
                return "The project has compile errors. Fix them first, so the update's own result can be checked.";
            if (Directory.Exists(D(plan.kitRoot + "/.git")))
                return "This kit is a git checkout. Update it with git instead.";
            if (!EditorIsAtLeast(plan.target.unity))
                return "Open MMORPG " + plan.target.version + " needs Unity " + plan.target.unity + " or newer; this is " + Application.unityVersion + ".";
            if (plan.UnmetPackages(ProjectPackages()).Count > 0)
                return "Open MMORPG " + plan.target.version + " needs the package versions listed below. Add them in Package Manager first; " +
                       "the kit does not change your project's packages itself.";
            return null;
        }

        /// <summary>Name to version of every package the project has.</summary>
        public static Dictionary<string, string> ProjectPackages()
        {
            var packages = new Dictionary<string, string>();
            foreach (UnityEditor.PackageManager.PackageInfo package in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
                packages[package.name] = package.version;
            return packages;
        }

        /// <summary>Whether this editor is at least "6000.3" (major.minor; the patch is not compared).</summary>
        public static bool EditorIsAtLeast(string required)
        {
            if (string.IsNullOrEmpty(required))
                return true;
            int[] want = required.Split('.').Take(2).Select(p => int.TryParse(p, out int n) ? n : 0).ToArray();
            int[] have = Application.unityVersion.Split('.').Take(2).Select(p => int.TryParse(new string(p.TakeWhile(char.IsDigit).ToArray()), out int n) ? n : 0).ToArray();
            for (int i = 0; i < want.Length; ++i)
            {
                int h = i < have.Length ? have[i] : 0;
                if (h != want[i])
                    return h > want[i];
            }
            return true;
        }

        // ------------------------------------------------------------------------------------------------------- apply

        /// <summary>Downloads, backs up and applies the plan. Returns false if the user cancelled or something failed (said in a dialog).</summary>
        public static bool Apply(KitUpdatePlan plan, IKitSource source)
        {
            string blocker = Blocker(plan);
            if (blocker != null)
            {
                EditorUtility.DisplayDialog("Update Open MMORPG", blocker, "OK");
                return false;
            }
            string staging = Path.Combine(StagingRoot, Safe(plan.target.version));
            try
            {
                if (!Download(plan, source, staging))
                    return false;
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("Update Open MMORPG", "The download failed, so nothing was changed.\n\n" + Unwrap(ex).Message, "OK");
                return false;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            string backup = Path.Combine(BackupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss") + " " + Safe(plan.installed.version) + " to " + Safe(plan.target.version))
                .Replace('\\', '/');
            var record = new RestoreRecord { from = plan.installed.version, to = plan.target.version };
            string manifestPath = AssetDatabase.GUIDToAssetPath(KitManifest.Guid);
            try
            {
                BackUp(plan, manifestPath, backup, record);
                Write(plan, source, staging, manifestPath, record);
            }
            catch (Exception ex)
            {
                SaveRecord(backup, record);
                Debug.LogException(ex);
                bool restore = EditorUtility.DisplayDialog("Update Open MMORPG",
                    "The update stopped part way: " + Unwrap(ex).Message + "\n\nEverything it changed is backed up in " + backup + ".",
                    "Restore the Backup", "Leave It");
                if (restore)
                    Restore(backup);
                return false;
            }
            SaveRecord(backup, record);

            var state = new State
            {
                stage = "applying",
                from = plan.installed.version,
                to = plan.target.version,
                fromTag = plan.installed.tag,
                toTag = plan.target.tag,
                backup = backup,
                compareUrl = source.CompareUrl(plan.installed.tag),
                kept = plan.actions.Where(a => a.kind == KitActionKind.KeptDeleted || a.kind == KitActionKind.PathTaken
                                               || (a.kind == KitActionKind.EditedByYou && !a.overwrite))
                    .Select(a => a.currentPath ?? a.targetPath).ToList(),
                appliedTicks = DateTime.UtcNow.Ticks,
            };
            SaveState(state);
            s_idleSince = DateTime.MaxValue;
            // An update that changes no script reloads nothing, so this domain watches the result itself.
            EditorApplication.update -= Watch;
            EditorApplication.update += Watch;

            AssetDatabase.Refresh();
            try { Directory.Delete(D(staging), true); } catch (Exception) { }
            return true;
        }

        private static bool Download(KitUpdatePlan plan, IKitSource source, string staging)
        {
            var wanted = new List<KeyValuePair<string, string>>(); // kit-relative path, expected hash
            foreach (KitAction action in plan.actions.Where(a => a.Applies))
            {
                KitManifestEntry entry = plan.target.files.TryGetValue(action.guid, out KitManifestEntry e) ? e : null;
                if (entry == null)
                    continue;
                if (action.WritesContent && !action.folder)
                    wanted.Add(new KeyValuePair<string, string>(entry.path, entry.hash));
                if (action.meta)
                    wanted.Add(new KeyValuePair<string, string>(entry.path + ".meta", entry.meta));
            }
            if (Directory.Exists(D(staging)))
                Directory.Delete(D(staging), true);
            Directory.CreateDirectory(D(staging));

            int done = 0;
            using (var cancel = new CancellationTokenSource())
            using (var slots = new SemaphoreSlim(Downloads))
            {
                Task all = Task.WhenAll(wanted.Select(async file =>
                {
                    await slots.WaitAsync(cancel.Token).ConfigureAwait(false);
                    try
                    {
                        byte[] data = await source.FetchAsync(file.Key, cancel.Token).ConfigureAwait(false);
                        if (KitHash.Of((byte[])data.Clone()) != file.Value)
                            throw new IOException(file.Key + " does not match the release's manifest.");
                        string to = D(Path.Combine(staging, file.Key));
                        Directory.CreateDirectory(Path.GetDirectoryName(to));
                        File.WriteAllBytes(to, data);
                        Interlocked.Increment(ref done);
                    }
                    finally
                    {
                        slots.Release();
                    }
                }));
                while (!all.IsCompleted)
                {
                    if (EditorUtility.DisplayCancelableProgressBar("Update Open MMORPG",
                            "Downloading " + plan.target.version + ": " + done + " of " + wanted.Count + " files", wanted.Count == 0 ? 1f : (float)done / wanted.Count))
                    {
                        cancel.Cancel();
                        try { all.Wait(); } catch (Exception) { }
                        return false;
                    }
                    Thread.Sleep(50);
                }
                all.GetAwaiter().GetResult(); // rethrows the first failure
            }
            return true;
        }

        private static void BackUp(KitUpdatePlan plan, string manifestPath, string backup, RestoreRecord record)
        {
            EditorUtility.DisplayProgressBar("Update Open MMORPG", "Backing up the files the update changes", 0f);
            Directory.CreateDirectory(D(backup));
            foreach (KitAction action in plan.actions.Where(a => a.Applies && a.currentPath != null))
                Keep(action.currentPath, backup, record);
            Keep(manifestPath, backup, record);
            File.WriteAllText(D(Path.Combine(backup, "README.txt")),
                "Open MMORPG update from " + plan.installed.version + " to " + plan.target.version + ", " + DateTime.Now + ".\n\n" +
                "This folder holds every kit file the update changed or deleted, as it was before, at its path in the project.\n" +
                "Tools > Open MMORPG > Update > Check for Updates restores it. By hand: close Unity, delete the files listed under\n" +
                "\"added\" in restore.json, move the files listed under \"moves\" back from \"to\" to \"from\", copy everything in\n" +
                "files/ over the project, and open Unity.\n");
        }

        /// <summary>Copies a file (or just a folder's .meta) and its .meta into the backup.</summary>
        private static void Keep(string projectPath, string backup, RestoreRecord record)
        {
            if (string.IsNullOrEmpty(projectPath) || record.files.Contains(projectPath))
                return;
            string to = D(Path.Combine(backup, "files", projectPath));
            Directory.CreateDirectory(Path.GetDirectoryName(to));
            if (File.Exists(D(projectPath)))
                File.Copy(D(projectPath), to, true);
            if (File.Exists(D(projectPath + ".meta")))
                File.Copy(D(projectPath + ".meta"), to + ".meta", true);
            record.files.Add(projectPath);
        }

        private static void Write(KitUpdatePlan plan, IKitSource source, string staging, string manifestPath, RestoreRecord record)
        {
            EditorUtility.DisplayProgressBar("Update Open MMORPG", "Writing " + plan.target.version, 0.5f);
            try
            {
                var applying = plan.actions.Where(a => a.Applies).ToList();

                // New folders first, with their own GUIDs, so moves and new files have somewhere to go.
                foreach (KitAction action in applying.Where(a => a.kind == KitActionKind.Add && a.folder).OrderBy(a => Depth(a.targetPath)))
                {
                    Directory.CreateDirectory(D(action.targetPath));
                    File.Copy(D(Path.Combine(staging, action.path + ".meta")), D(action.targetPath + ".meta"), true);
                    record.added.Add(action.targetPath);
                    AssetDatabase.ImportAsset(action.targetPath);
                }

                // Moves, outer folders first; whatever is inside a moved folder goes with it.
                foreach (KitAction action in applying.Where(a => a.Moves).OrderBy(a => Depth(a.targetPath)))
                {
                    string from = AssetDatabase.GUIDToAssetPath(action.guid);
                    if (from == action.targetPath)
                        continue;
                    string error = AssetDatabase.MoveAsset(from, action.targetPath);
                    if (!string.IsNullOrEmpty(error))
                        throw new IOException("Could not move " + from + " to " + action.targetPath + ": " + error);
                    record.moves.Add(new Move { guid = action.guid, from = from, to = action.targetPath });
                }

                AssetDatabase.StartAssetEditing();
                try
                {
                    foreach (KitAction action in applying.Where(a => !a.folder))
                    {
                        if (action.kind == KitActionKind.Delete)
                        {
                            string at = PathNow(action);
                            File.Delete(D(at));
                            File.Delete(D(at + ".meta"));
                            continue;
                        }
                        if (action.kind == KitActionKind.Add)
                            record.added.Add(action.targetPath);
                        // .meta before the file, so Unity meets a new file with its GUID already beside it.
                        if (action.meta)
                            File.Copy(D(Path.Combine(staging, action.path + ".meta")), D(action.targetPath + ".meta"), true);
                        if (action.WritesContent)
                            File.Copy(D(Path.Combine(staging, action.path)), D(action.targetPath), true);
                    }
                    File.WriteAllText(D(manifestPath), source.ManifestText);
                }
                finally
                {
                    AssetDatabase.StopAssetEditing();
                }

                // Folders removed upstream go once nothing is left in them; the user's own files keep them.
                foreach (string at in applying.Where(a => a.kind == KitActionKind.Delete && a.folder).Select(PathNow).OrderByDescending(Depth))
                {
                    if (Directory.Exists(D(at)) && !Directory.EnumerateFileSystemEntries(D(at)).Any())
                    {
                        Directory.Delete(D(at));
                        File.Delete(D(at + ".meta"));
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        /// <summary>Where a file or folder is once the moves are done: inside a folder upstream moved, it went with the folder.</summary>
        private static string PathNow(KitAction action)
        {
            string now = AssetDatabase.GUIDToAssetPath(action.guid);
            return string.IsNullOrEmpty(now) ? action.currentPath : now;
        }


        // ----------------------------------------------------------------------------------------------------- restore

        /// <summary>Puts the kit back as it was before the update that made this backup.</summary>
        public static void Restore(string backup)
        {
            var record = JsonConvert.DeserializeObject<RestoreRecord>(File.ReadAllText(D(Path.Combine(backup, RestoreFile))));
            EditorUtility.DisplayProgressBar("Update Open MMORPG", "Restoring " + record.from, 0.5f);
            try
            {
                for (int i = record.moves.Count - 1; i >= 0; --i)
                {
                    Move move = record.moves[i];
                    string now = AssetDatabase.GUIDToAssetPath(move.guid);
                    if (!string.IsNullOrEmpty(now) && now != move.from)
                        AssetDatabase.MoveAsset(now, move.from);
                }
                AssetDatabase.StartAssetEditing();
                try
                {
                    foreach (string added in record.added.Where(p => File.Exists(D(p))))
                    {
                        File.Delete(D(added));
                        File.Delete(D(added + ".meta"));
                    }
                    foreach (string path in record.files)
                    {
                        string from = D(Path.Combine(backup, "files", path));
                        if (File.Exists(from))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(D(path)));
                            File.Copy(from, D(path), true);
                        }
                        if (File.Exists(from + ".meta"))
                        {
                            if (!File.Exists(from))
                                Directory.CreateDirectory(D(path)); // a folder: only its .meta was kept
                            File.Copy(from + ".meta", D(path + ".meta"), true);
                        }
                    }
                }
                finally
                {
                    AssetDatabase.StopAssetEditing();
                }
                foreach (string added in record.added.Where(p => Directory.Exists(D(p))).OrderByDescending(Depth))
                {
                    if (!Directory.EnumerateFileSystemEntries(D(added)).Any())
                    {
                        Directory.Delete(D(added));
                        File.Delete(D(added + ".meta"));
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            State state = LoadState() ?? new State();
            state.stage = "restored";
            state.message = "Restored " + record.from + " from " + backup + ".";
            SaveState(state);
            AssetDatabase.Refresh();
        }

        private static void SaveRecord(string backup, RestoreRecord record)
        {
            Directory.CreateDirectory(D(backup));
            File.WriteAllText(D(Path.Combine(backup, RestoreFile)), JsonConvert.SerializeObject(record, Formatting.Indented));
        }

        // ------------------------------------------------------------------------------------------------------- watch

        /// <summary>Once Unity has settled after an update, records whether the kit compiled, and offers a restore if not.</summary>
        private static void Watch()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                s_idleSince = DateTime.MaxValue;
                return;
            }
            if (s_idleSince == DateTime.MaxValue)
            {
                s_idleSince = DateTime.UtcNow;
                return;
            }
            if (DateTime.UtcNow - s_idleSince < Settle)
                return;
            State state = LoadState();
            if (state == null || state.stage != "applying")
            {
                EditorApplication.update -= Watch;
                return;
            }
            EditorApplication.update -= Watch;
            if (EditorUtility.scriptCompilationFailed)
            {
                state.stage = "failed";
                state.message = "Open MMORPG " + state.to + " was written, but the project does not compile.";
                SaveState(state);
                if (!Application.isBatchMode && EditorUtility.DisplayDialog("Update Open MMORPG",
                        state.message + " The Console lists the errors. Restore " + state.from + " from the backup?",
                        "Restore " + state.from, "Keep " + state.to))
                    Restore(state.backup);
            }
            else
            {
                state.stage = "done";
                state.message = "Updated Open MMORPG from " + state.from + " to " + state.to + ".";
                SaveState(state);
                Debug.Log(state.message + (state.kept.Count > 0 ? " " + state.kept.Count + " kit file(s) you had edited were left as they were." : ""));
                if (!Application.isBatchMode)
                    KitUpdateWindow.Open();
            }
        }

        private static string D(string path)
        {
            return KitPath.Disk(path);
        }

        private static int Depth(string path)
        {
            return path.Count(c => c == '/');
        }

        private static string Safe(string name)
        {
            return string.Concat((name ?? "unknown").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        }

        private static Exception Unwrap(Exception ex)
        {
            while (ex is AggregateException && ex.InnerException != null)
                ex = ex.InnerException;
            return ex;
        }
    }
}
