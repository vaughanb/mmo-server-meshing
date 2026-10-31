using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace OpenMMORPG.KitUpdater
{
    /// <summary>
    /// Lists the kit files that differ from the release the kit was installed from: edited, import settings changed, moved
    /// or deleted. Read-only; files the user added are not listed. <c>Tools &gt; Open MMORPG &gt; Update &gt; Changes Since
    /// Install</c>.
    /// </summary>
    public class KitChangesWindow : EditorWindow
    {
        private static readonly string[] FilterNames = { "All", "Edited", "Import settings", "Moved", "Deleted" };

        // Not serialized: after a domain reload the window asks for a new scan instead of showing a stale one.
        [NonSerialized] private KitScan _scan;
        [NonSerialized] private string _problem;
        [NonSerialized] private double _seconds;
        /// <summary>The pipeline content the kit holds instead of its own, as "HDRP", or null.</summary>
        [NonSerialized] private string _pipeline;
        [NonSerialized] private SearchField _searchField;
        private int _filter;
        private string _search = "";
        private Vector2 _scroll;

        [MenuItem(KitInstall.MenuRoot + "Changes Since Install...", false, 100)]
        public static void Open()
        {
            var window = GetWindow<KitChangesWindow>(false, "Kit Changes", true);
            window.minSize = new Vector2(560f, 360f);
            window.Scan();
        }

        private void Scan()
        {
            _scan = null;
            _pipeline = null;
            KitManifest manifest = KitInstall.Load(out string kitRoot, out _problem);
            if (manifest != null)
            {
                var timer = Stopwatch.StartNew();
                var project = new AssetDatabaseKitProject();
                try
                {
                    // A kit switched to another pipeline's content is compared with that content, not with the kit's own
                    KitPipelineContent pipeline = KitPipeline.Installed(manifest, kitRoot, project);
                    if (pipeline != null)
                    {
                        manifest = pipeline.Apply(manifest);
                        _pipeline = pipeline.id.ToUpperInvariant();
                    }
                    _scan = KitScanner.Scan(manifest, kitRoot, project,
                        p => EditorUtility.DisplayCancelableProgressBar("Kit Changes", "Comparing the kit with the release it came from", p));
                }
                catch (Exception ex)
                {
                    _problem = "The scan stopped: " + ex.Message;
                }
                finally
                {
                    EditorUtility.ClearProgressBar();
                    project.SaveCache();
                }
                _seconds = timer.Elapsed.TotalSeconds;
            }
            Repaint();
        }

        private void OnGUI()
        {
            if (_problem != null)
            {
                EditorGUILayout.HelpBox(_problem, MessageType.Info);
                if (GUILayout.Button("Scan Again", GUILayout.Width(120f)))
                    Scan();
                return;
            }
            if (_scan == null)
            {
                EditorGUILayout.HelpBox("Compare the kit with the release it was installed from.", MessageType.None);
                if (GUILayout.Button("Scan", GUILayout.Width(120f)))
                    Scan();
                return;
            }

            KitManifest manifest = _scan.manifest;
            EditorGUILayout.LabelField("Installed from Open MMORPG " + manifest.version + (manifest.tag == null ? " (test build)" : "") +
                                       (_pipeline == null ? "" : ", with its " + _pipeline + " content"), EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Kit files that differ from that release. Files you added are not listed.",
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(4f);

            DrawToolbar();

            if (_scan.changes.Count == 0)
            {
                EditorGUILayout.HelpBox("No kit file has changed since " + manifest.version + " was installed (" + _scan.unchanged +
                                        " files checked).", MessageType.Info);
                GUILayout.FlexibleSpace();
            }
            else
            {
                DrawList();
            }

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label(_scan.unchanged + " unchanged, scanned in " + _seconds.ToString("0.0") + " s", EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(_scan.changes.Count == 0))
                {
                    if (GUILayout.Button("Copy List", EditorStyles.toolbarButton))
                        EditorGUIUtility.systemCopyBuffer = ListText();
                }
            }
        }

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                string[] labels = new string[FilterNames.Length];
                labels[0] = FilterNames[0] + " (" + _scan.changes.Count + ")";
                for (int i = 1; i < FilterNames.Length; ++i)
                    labels[i] = FilterNames[i] + " (" + _scan.Count((KitChangeKind)(i - 1)) + ")";
                _filter = GUILayout.Toolbar(_filter, labels, EditorStyles.toolbarButton);
                GUILayout.FlexibleSpace();
                if (_searchField == null)
                    _searchField = new SearchField();
                _search = _searchField.OnToolbarGUI(_search, GUILayout.MaxWidth(200f));
                if (GUILayout.Button("Rescan", EditorStyles.toolbarButton))
                    Scan();
            }
        }

        private IEnumerable<KitChange> Shown()
        {
            return _scan.changes.Where(c =>
                (_filter == 0 || c.kind == (KitChangeKind)(_filter - 1)) &&
                (string.IsNullOrEmpty(_search) || Describe(c).IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0));
        }

        private void DrawList()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (KitChange change in Shown())
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(KindLabel(change), GUILayout.Width(110f));
                    GUILayout.Label(Describe(change));
                }
                Rect row = GUILayoutUtility.GetLastRect();
                if (change.currentPath != null && Event.current.type == EventType.MouseDown && row.Contains(Event.current.mousePosition))
                {
                    UnityEngine.Object asset = AssetDatabase.LoadMainAssetAtPath(change.currentPath);
                    if (asset != null)
                    {
                        if (Event.current.clickCount == 2)
                            AssetDatabase.OpenAsset(asset);
                        Selection.activeObject = asset;
                        EditorGUIUtility.PingObject(asset);
                    }
                    Event.current.Use();
                }
            }
            EditorGUILayout.EndScrollView();
        }

        private static string KindLabel(KitChange change)
        {
            switch (change.kind)
            {
                case KitChangeKind.Edited: return "Edited";
                case KitChangeKind.ImportSettings: return "Import settings";
                case KitChangeKind.Moved: return change.edited ? "Moved, edited" : "Moved";
                default: return "Deleted";
            }
        }

        private string Describe(KitChange change)
        {
            string text = change.path + (change.folder ? "/" : "");
            if (change.kind == KitChangeKind.Moved)
                text += "  →  " + change.currentPath + (change.folder ? "/" : "");
            if (change.folder)
                text += "  (folder, " + change.files + (change.files == 1 ? " file)" : " files)");
            return text;
        }

        private string ListText()
        {
            var text = new StringBuilder();
            text.AppendLine("Open MMORPG " + _scan.manifest.version + ": kit files changed since install");
            foreach (KitChange change in _scan.changes)
                text.Append(KindLabel(change)).Append('\t').AppendLine(Describe(change));
            return text.ToString();
        }
    }
}
