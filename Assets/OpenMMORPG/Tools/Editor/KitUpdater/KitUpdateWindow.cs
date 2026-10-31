using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace OpenMMORPG.KitUpdater
{
    /// <summary>
    /// Tools &gt; Open MMORPG &gt; Update &gt; Check for Updates. Finds the kit's releases on GitHub (or takes a kit folder),
    /// shows what updating would do to each file, and applies it. Kit files the user edited are kept unless they tick
    /// Overwrite; everything changed is backed up first, and the last update can be restored from here.
    /// </summary>
    public class KitUpdateWindow : EditorWindow
    {
        private static readonly KitActionKind[] Groups =
        {
            KitActionKind.EditedByYou, KitActionKind.Update, KitActionKind.Add, KitActionKind.Delete,
            KitActionKind.KeptDeleted, KitActionKind.PathTaken, KitActionKind.OptedOut,
        };

        [NonSerialized] private KitManifest _installed;
        [NonSerialized] private string _kitRoot;
        [NonSerialized] private string _problem;
        [NonSerialized] private List<KitRelease> _releases;
        [NonSerialized] private string _error;
        [NonSerialized] private IKitSource _source;
        [NonSerialized] private KitUpdatePlan _plan;
        private int _release;
        private Vector2 _scroll;
        private readonly HashSet<KitActionKind> _open = new HashSet<KitActionKind> { KitActionKind.EditedByYou };

        public const string MenuPath = KitInstall.MenuRoot + "Check for Updates...";

        [MenuItem(MenuPath, false, 99)]
        public static void Open()
        {
            var window = GetWindow<KitUpdateWindow>(false, "Update Open MMORPG", true);
            window.minSize = new Vector2(600f, 420f);
            window.Reload();
        }

        internal static void RepaintIfOpen()
        {
            foreach (KitUpdateWindow window in Resources.FindObjectsOfTypeAll<KitUpdateWindow>())
                window.Repaint();
        }

        private void OnEnable()
        {
            Reload();
        }

        private void Reload()
        {
            _installed = KitInstall.Load(out _kitRoot, out _problem);
            Repaint();
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawLastUpdate();
            if (_problem != null)
            {
                EditorGUILayout.HelpBox(_problem, MessageType.Info);
                EditorGUILayout.EndScrollView();
                return;
            }
            EditorGUILayout.LabelField("Installed: Open MMORPG " + _installed.version + (_installed.tag == null ? " (test build)" : ""), EditorStyles.boldLabel);
            if (!KitUpdateCheck.Asked && _installed.tag != null)
                DrawCheckQuestion();
            string newer = KitUpdateCheck.NewerThan(_installed.version);
            if (newer != null && _plan == null)
                EditorGUILayout.HelpBox(newer + " is out. Check GitHub for Releases, then Compare, to see what it changes before anything is touched.", MessageType.Info);
            EditorGUILayout.Space(4f);
            DrawSource();
            if (_error != null)
                EditorGUILayout.HelpBox(_error, MessageType.Warning);
            if (_plan != null)
                DrawPlan();
            DrawCheckSetting();
            EditorGUILayout.EndScrollView();
        }

        /// <summary>Asked once per project, until answered: the daily check is opt-in.</summary>
        private void DrawCheckQuestion()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Check GitHub once a day for a new release of the kit? When the editor starts, it reads the " +
                                           "public release list and, if a newer version is out, says so in the Console. It never " +
                                           "downloads anything by itself and sends nothing about you or your project.",
                    EditorStyles.wordWrappedLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Check Daily", GUILayout.Width(110f)))
                    {
                        KitUpdateCheck.Enabled = true;
                        KitUpdateCheck.CheckNow();
                    }
                    if (GUILayout.Button("No Thanks", GUILayout.Width(110f)))
                        KitUpdateCheck.Enabled = false;
                    GUILayout.FlexibleSpace();
                }
            }
            EditorGUILayout.Space(4f);
        }

        private void DrawCheckSetting()
        {
            EditorGUILayout.Space(10f);
            using (new EditorGUILayout.HorizontalScope())
            {
                bool on = EditorGUILayout.ToggleLeft(new GUIContent("Check for a new release once a day",
                    "When the editor starts, ask GitHub's public release list whether a newer kit is out, at most once a day. " +
                    "A newer one is mentioned in the Console and ticks this window's menu item; nothing is downloaded."),
                    KitUpdateCheck.Enabled, GUILayout.Width(250f));
                if (on != KitUpdateCheck.Enabled)
                    KitUpdateCheck.Enabled = on;
                KitUpdateCheck.Result last = KitUpdateCheck.Last();
                GUILayout.Label(last == null ? "Not checked yet."
                    : "Last checked " + last.Checked.ToLocalTime().ToString("g") + (last.error != null ? ", which failed: " + last.error : "."),
                    EditorStyles.miniLabel);
            }
        }

        // ------------------------------------------------------------------------------------------------- last update

        private void DrawLastUpdate()
        {
            KitUpdater.State state = KitUpdater.LoadState();
            if (state == null || state.stage == "seen" || string.IsNullOrEmpty(state.message))
                return;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(state.message, EditorStyles.wordWrappedLabel);
                if (state.stage == "applying")
                    EditorGUILayout.LabelField("Waiting for Unity to import and compile it.", EditorStyles.wordWrappedMiniLabel);
                if (state.stage == "done" && state.kept.Count > 0)
                {
                    EditorGUILayout.LabelField(state.kept.Count + " kit file(s) you had edited were left as they were. " +
                                               "Their upstream changes are not in your copy:", EditorStyles.wordWrappedMiniLabel);
                    foreach (string path in state.kept.Take(50))
                        EditorGUILayout.LabelField("    " + path, EditorStyles.miniLabel);
                    if (state.kept.Count > 50)
                        EditorGUILayout.LabelField("    and " + (state.kept.Count - 50) + " more", EditorStyles.miniLabel);
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (state.compareUrl != null && state.stage != "restored" && GUILayout.Button("What Changed Upstream", GUILayout.Width(170f)))
                        Application.OpenURL(state.compareUrl);
                    bool backup = !string.IsNullOrEmpty(state.backup) && Directory.Exists(state.backup);
                    using (new EditorGUI.DisabledScope(!backup || state.stage == "restored" || state.stage == "applying"))
                    {
                        if (GUILayout.Button("Restore " + state.from, GUILayout.Width(130f)) &&
                            EditorUtility.DisplayDialog("Update Open MMORPG", "Put the kit back as it was before the update to " + state.to + "?\n\n" +
                                                        "Changes you made to kit files since the update are lost.", "Restore", "Cancel"))
                        {
                            KitUpdater.Restore(state.backup);
                            _plan = null;
                            Reload();
                            GUIUtility.ExitGUI();
                        }
                    }
                    using (new EditorGUI.DisabledScope(!backup))
                    {
                        if (GUILayout.Button("Show Backup", GUILayout.Width(110f)))
                            EditorUtility.RevealInFinder(state.backup);
                    }
                    GUILayout.FlexibleSpace();
                    using (new EditorGUI.DisabledScope(state.stage == "applying"))
                    {
                        if (GUILayout.Button("Dismiss", GUILayout.Width(80f)))
                            KitUpdater.Dismiss();
                    }
                }
            }
            EditorGUILayout.Space(6f);
        }

        // ------------------------------------------------------------------------------------------------------ source

        private void DrawSource()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (_releases == null)
                {
                    if (GUILayout.Button("Check GitHub for Releases", GUILayout.Width(200f)))
                        LoadReleases();
                }
                else if (_releases.Count == 0)
                {
                    GUILayout.Label("No release on GitHub carries a kit manifest yet.");
                }
                else
                {
                    string[] labels = _releases.Select((r, i) => r.tag + (i == 0 ? " (latest)" : "") + (r.prerelease ? " (pre-release)" : "") +
                                                                 (r.tag == _installed.tag ? " (installed)" : "")).ToArray();
                    _release = EditorGUILayout.Popup(Mathf.Clamp(_release, 0, labels.Length - 1), labels, GUILayout.Width(220f));
                    if (GUILayout.Button("Compare", GUILayout.Width(90f)))
                        Compare(() => new GitHubKitSource(_releases[_release].tag));
                }
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent("Use a Folder...", "A kit folder holding a kit-manifest.json, such as a checkout after a build with --manifest-out."), GUILayout.Width(120f)))
                {
                    string folder = EditorUtility.OpenFolderPanel("Kit folder with kit-manifest.json", "", "");
                    if (!string.IsNullOrEmpty(folder))
                        Compare(() => new FolderKitSource(folder));
                }
            }
        }

        private void LoadReleases()
        {
            _error = null;
            try
            {
                EditorUtility.DisplayProgressBar("Update Open MMORPG", "Asking GitHub for releases", 0.5f);
                _releases = GitHubKitSource.ListReleases();
                string newer = KitUpdateCheck.NewerThan(_installed.version);
                _release = Math.Max(0, _releases.FindIndex(r => r.tag == newer));
            }
            catch (Exception ex)
            {
                _error = ex.Message;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private void Compare(Func<IKitSource> open)
        {
            _error = null;
            _plan = null;
            var project = new AssetDatabaseKitProject();
            try
            {
                EditorUtility.DisplayProgressBar("Update Open MMORPG", "Getting the release's manifest", 0f);
                _source = open();
                KitManifest installed = _installed;
                // A kit switched to another pipeline's content updates to the target release's version of that content
                KitPipelineContent pipeline = KitPipeline.Installed(_installed, _kitRoot, project);
                if (pipeline != null)
                {
                    EditorUtility.DisplayProgressBar("Update Open MMORPG", "Getting the release's " + pipeline.id.ToUpperInvariant() + " content", 0f);
                    installed = pipeline.Apply(_installed);
                    _source = new PipelineKitSource(_source, KitPipeline.InTarget(_source, pipeline.id));
                }
                _plan = KitUpdatePlan.Make(installed, _source.Manifest, _kitRoot, project,
                    p => EditorUtility.DisplayCancelableProgressBar("Update Open MMORPG", "Comparing the kit with " + _source.Manifest.version, p));
            }
            catch (Exception ex)
            {
                _error = ex.Message;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                project.SaveCache();
            }
        }

        // -------------------------------------------------------------------------------------------------------- plan

        private void DrawPlan()
        {
            EditorGUILayout.Space(6f);
            KitManifest target = _plan.target;
            EditorGUILayout.LabelField(_installed.version + "  →  " + target.version + "   (" + _source.Description + ")", EditorStyles.boldLabel);

            List<KitAction> applying = _plan.actions.Where(a => a.Applies).ToList();
            if (applying.Count == 0 && _plan.Of(KitActionKind.EditedByYou).Count() == 0)
            {
                EditorGUILayout.HelpBox("Nothing to update: the kit already matches " + target.version + ".", MessageType.Info);
                return;
            }

            string blocker = KitUpdater.Blocker(_plan);
            if (blocker != null)
                EditorGUILayout.HelpBox(blocker, MessageType.Warning);
            EditorGUILayout.LabelField(applying.Count + " file(s) change, " + Megabytes(_plan.DownloadSize) + " to download. " +
                                       "Everything changed is backed up to " + KitUpdater.BackupRoot + " beside Assets first.",
                EditorStyles.wordWrappedMiniLabel);
            DrawUnmetPackages();

            foreach (KitActionKind kind in Groups)
                DrawGroup(kind);

            EditorGUILayout.Space(8f);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(blocker != null || !_plan.actions.Any(a => a.Applies)))
                {
                    if (GUILayout.Button("Update to " + target.version, GUILayout.Width(160f), GUILayout.Height(26f)) &&
                        EditorUtility.DisplayDialog("Update Open MMORPG", "Update the kit from " + _installed.version + " to " + target.version + "?\n\n" +
                                                    Overwrites() + "Every file it changes is backed up first.", "Update", "Cancel"))
                    {
                        if (KitUpdater.Apply(_plan, _source))
                            _plan = null;
                        Reload();
                        GUIUtility.ExitGUI();
                    }
                }
            }
        }

        /// <summary>Packages the release needs at a version the project lacks. The user adds them; the kit never does.</summary>
        private void DrawUnmetPackages()
        {
            Dictionary<string, string> project = KitUpdater.ProjectPackages();
            Dictionary<string, string> unmet = _plan.UnmetPackages(project);
            if (unmet.Count == 0)
                return;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Add or upgrade these in Package Manager, then come back:", EditorStyles.boldLabel);
                foreach (var need in unmet)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        string have = project.TryGetValue(need.Key, out string v) ? "you have " + v : "not in the project";
                        EditorGUILayout.LabelField(need.Key + " " + need.Value + " or newer  (" + have + ")", EditorStyles.miniLabel);
                        if (GUILayout.Button("Open Package Manager", EditorStyles.miniButton, GUILayout.Width(150f)))
                            UnityEditor.PackageManager.UI.Window.Open(need.Key);
                    }
                }
            }
        }

        private string Overwrites()
        {
            int kept = _plan.Of(KitActionKind.EditedByYou).Count(a => !a.overwrite);
            int lost = _plan.Of(KitActionKind.EditedByYou).Count(a => a.overwrite);
            return (kept > 0 ? kept + " file(s) you edited are kept as they are. " : "") +
                   (lost > 0 ? lost + " file(s) you edited are overwritten. " : "");
        }

        private void DrawGroup(KitActionKind kind)
        {
            List<KitAction> actions = _plan.Of(kind).ToList();
            if (actions.Count == 0)
                return;
            bool open = _open.Contains(kind);
            bool now = EditorGUILayout.Foldout(open, Title(kind) + " (" + actions.Count + ")", true);
            if (now != open)
            {
                if (now) _open.Add(kind); else _open.Remove(kind);
            }
            if (!now)
                return;
            EditorGUI.indentLevel++;
            EditorGUILayout.LabelField(Explain(kind), EditorStyles.wordWrappedMiniLabel);
            if (kind == KitActionKind.EditedByYou)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(EditorGUI.indentLevel * 15f);
                    if (GUILayout.Button("Keep All Mine", EditorStyles.miniButtonLeft, GUILayout.Width(100f)))
                        actions.ForEach(a => a.overwrite = false);
                    if (GUILayout.Button("Overwrite All", EditorStyles.miniButtonRight, GUILayout.Width(100f)))
                        actions.ForEach(a => a.overwrite = true);
                }
            }
            foreach (KitAction action in actions.Take(500))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (kind == KitActionKind.EditedByYou)
                        action.overwrite = EditorGUILayout.ToggleLeft("Overwrite", action.overwrite, GUILayout.Width(110f));
                    EditorGUILayout.LabelField(Describe(action), EditorStyles.miniLabel);
                }
                Rect row = GUILayoutUtility.GetLastRect();
                string at = action.currentPath ?? action.targetPath;
                if (at != null && Event.current.type == EventType.MouseDown && Event.current.mousePosition.x > row.x + 120f && row.Contains(Event.current.mousePosition))
                {
                    UnityEngine.Object asset = AssetDatabase.LoadMainAssetAtPath(at);
                    if (asset != null)
                    {
                        Selection.activeObject = asset;
                        EditorGUIUtility.PingObject(asset);
                    }
                    Event.current.Use();
                }
            }
            if (actions.Count > 500)
                EditorGUILayout.LabelField("and " + (actions.Count - 500) + " more", EditorStyles.miniLabel);
            EditorGUI.indentLevel--;
        }

        private static string Title(KitActionKind kind)
        {
            switch (kind)
            {
                case KitActionKind.EditedByYou: return "Edited by you";
                case KitActionKind.Update: return "Update";
                case KitActionKind.Add: return "Add";
                case KitActionKind.Delete: return "Delete";
                case KitActionKind.KeptDeleted: return "Kept: removed upstream, edited by you";
                case KitActionKind.PathTaken: return "Skipped: a file of yours is in the way";
                default: return "Skipped: inside a kit folder you deleted";
            }
        }

        private static string Explain(KitActionKind kind)
        {
            switch (kind)
            {
                case KitActionKind.EditedByYou: return "Changed upstream and by you. Kept as you have it unless you tick Overwrite; the summary afterwards lists what was kept.";
                case KitActionKind.Update: return "Changed upstream, untouched by you. Overwritten, or moved where the new release has it.";
                case KitActionKind.Add: return "New in this release.";
                case KitActionKind.Delete: return "Removed upstream, untouched by you. A folder goes only if nothing of yours is left in it.";
                case KitActionKind.KeptDeleted: return "The new release no longer has these, but you edited them, so they stay.";
                case KitActionKind.PathTaken: return "New in this release, but a file of yours already has the path. Move yours to get the kit's.";
                default: return "You deleted the folder these would go into, so it stays deleted.";
            }
        }

        private static string Describe(KitAction action)
        {
            string text = action.currentPath ?? action.targetPath ?? action.path;
            if (action.Moves)
                text += "  →  " + action.targetPath;
            else if (action.currentPath == null && action.targetPath == null)
                text = action.path;
            if (action.kind == KitActionKind.Update && !action.content && !action.meta && action.Moves)
                text += "  (moved)";
            else if (action.kind == KitActionKind.Update && !action.content && action.meta)
                text += "  (import settings)";
            return text + (action.folder ? "/" : "");
        }

        private static string Megabytes(long bytes)
        {
            return bytes < 100 * 1024 ? (bytes / 1024f).ToString("0") + " KB" : (bytes / 1048576f).ToString("0.0") + " MB";
        }
    }
}
