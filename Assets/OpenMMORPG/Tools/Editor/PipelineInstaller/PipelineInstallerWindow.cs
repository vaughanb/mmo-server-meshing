using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace OpenMMORPG.Setup
{
    /// <summary>
    /// Opens by itself when the project renders with a pipeline the demo content is not made for, and says what is wrong in a
    /// sentence and what to press. "Not now" (or closing it) keeps it shut until Unity restarts, "Don't ask again" for good.
    /// <c>Tools &gt; Open MMORPG &gt; Install &gt; Render Pipeline Content</c> brings it back, to switch pipelines or to check
    /// what is installed.
    /// </summary>
    [InitializeOnLoad]
    internal static class PipelineInstallerLauncher
    {
        static PipelineInstallerLauncher()
        {
            // Not a delayCall, for the reason the welcome window gives: a fresh project reloads several times before it settles.
            EditorApplication.update += ShowWhenSettled;
        }

        private static void ShowWhenSettled()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            EditorApplication.update -= ShowWhenSettled;
            if (Application.isBatchMode)
                return;
            if (PipelineInstaller.IsContentPending)
                PipelineInstallerWindow.Open();
        }
    }

    public class PipelineInstallerWindow : EditorWindow
    {
        private Vector2 _scroll;
        private bool _showFiles;

        [MenuItem(PipelineInstaller.MenuPath, false, -990)]
        public static void Open()
        {
            PipelineInstaller.Invalidate();
            var window = GetWindow<PipelineInstallerWindow>(true, "Render Pipeline Content", true);
            window.minSize = new Vector2(520f, 420f);
            Rect main = EditorGUIUtility.GetMainWindowPosition();
            window.position = new Rect(main.x + (main.width - 540f) * 0.5f, main.y + (main.height - 520f) * 0.5f, 540f, 520f);
        }

        internal static void RepaintIfOpen()
        {
            if (HasOpenInstances<PipelineInstallerWindow>())
                GetWindow<PipelineInstallerWindow>().Repaint();
        }

        private void OnFocus()
        {
            PipelineInstaller.Invalidate();
        }

        private void OnDestroy()
        {
            // Closed without an answer is "not now": every script reload (a server build makes two) would bring it back.
            PipelineInstaller.Snooze();
        }

        private void OnInspectorUpdate()
        {
            // While an import runs this is what keeps the window honest.
            if (PipelineInstaller.PendingInstall != "")
                Repaint();
        }

        private void OnGUI()
        {
            var wrap = new GUIStyle(EditorStyles.label) { wordWrap = true, richText = true };
            var heading = new GUIStyle(EditorStyles.boldLabel) { fontSize = 14, wordWrap = true };
            PipelineManifest manifest = PipelineInstaller.Manifest;

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            GUILayout.Space(8);
            GUILayout.Label("Render pipeline content", heading);
            GUILayout.Space(4);

            if (manifest == null)
            {
                EditorGUILayout.HelpBox("The pipeline manifest (Tools/Install/Pipelines/pipelines.json) is missing, so there is " +
                                        "nothing to install. Reimport the kit.", MessageType.Warning);
                EditorGUILayout.EndScrollView();
                return;
            }

            string pending = PipelineInstaller.PendingInstall;
            if (pending != "")
            {
                EditorGUILayout.HelpBox("Installing the " + manifest.Find(pending).name + " content. Unity is importing and " +
                                        "will recompile; this window updates when it has finished.", MessageType.Info);
                EditorGUILayout.EndScrollView();
                return;
            }

            string locationProblem = PipelineInstaller.KitLocationProblem();
            if (locationProblem != null)
                EditorGUILayout.HelpBox(locationProblem, MessageType.Error);

            string last = PipelineInstaller.LastResult;
            if (last != "")
                EditorGUILayout.HelpBox(last, MessageType.Info);

            string project = PipelineInstaller.ProjectPipelineId();
            string installed = PipelineInstaller.InstalledId();

            GUILayout.Label("This project renders with <b>" + PipelineInstaller.ProjectPipelineDescription() + "</b>.", wrap);
            GUILayout.Label("The demo content in it is: <b>" + DescribeInstalled(manifest, installed) + "</b>.", wrap);
            GUILayout.Space(8);

            if (project == null)
            {
                EditorGUILayout.HelpBox("The demo is made for URP and for HDRP. Select one of them under Project Settings > " +
                                        "Graphics (and install its package if it is not in the project) first.", MessageType.Warning);
            }
            else if (installed == project)
            {
                EditorGUILayout.HelpBox("The demo content matches this project's render pipeline. Nothing to install.", MessageType.Info);
            }
            else if (PipelineInstaller.MatchFraction(project) >= PipelineInstaller.MostlyThere)
            {
                // Edits of the user's own: not a reason to ask anything, but a way back to the shipped files if they want one.
                PipelineInfo wanted = manifest.Find(project);
                PipelineInstaller.Status status = PipelineInstaller.StatusOf(project);
                int changed = status.different.Count + status.missing.Count + status.removalsPresent.Count;
                EditorGUILayout.HelpBox("The demo content is the " + wanted.name + " version, with " + changed + " file" +
                                        (changed == 1 ? "" : "s") + " changed or removed since it was installed. Nothing needs installing.",
                                        MessageType.Info);
                if (GUILayout.Button("Reinstall the shipped " + wanted.name + " content", GUILayout.Height(22)))
                    PipelineInstaller.Install(project);
            }
            else
            {
                PipelineInfo wanted = manifest.Find(project);
                GUILayout.Label("The demo's materials, shaders, scenes and lights are the " +
                                (installed == null ? "wrong (or a mixture of both)" : manifest.Find(installed).name) +
                                " version. Install the " + wanted.name + " version to match this project.", wrap);
                GUILayout.Space(6);
                PackageNote(wanted);
                if (GUILayout.Button("Install " + wanted.name + " content", GUILayout.Height(32)))
                {
                    PipelineInstaller.ClearDismissal();
                    PipelineInstaller.Install(project);
                }
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Not now", GUILayout.Height(22)))
                {
                    PipelineInstaller.Snooze();
                    Close();
                    GUIUtility.ExitGUI();
                }
                if (GUILayout.Button("Don't ask again", GUILayout.Height(22)))
                {
                    PipelineInstaller.Dismiss();
                    Close();
                    GUIUtility.ExitGUI();
                }
                GUILayout.EndHorizontal();
                GUILayout.Label("<i>Not now</i> asks again the next time Unity starts. <i>Don't ask again</i> is for projects that " +
                                "do not use the demo; this window stays under Tools > Open MMORPG > Install.", wrap);
            }

            UnneededPackagesSection(manifest);
            SwitchSection(manifest, project, installed);
            FilesSection(manifest, project, installed);

            GUILayout.Space(8);
            if (GUILayout.Button("Show backups of files you had edited", GUILayout.Height(20)))
                PipelineInstaller.RevealBackups();
            EditorGUILayout.EndScrollView();
        }

        private static string DescribeInstalled(PipelineManifest manifest, string installed)
        {
            if (installed != null)
                return manifest.Find(installed).name;
            string best = null;
            float bestFraction = 0f;
            foreach (PipelineInfo p in manifest.pipelines)
            {
                PipelineInstaller.Status s = PipelineInstaller.StatusOf(p.id);
                float fraction = s == null || s.total == 0 ? 0f : (float)s.matching / s.total;
                if (fraction > bestFraction)
                {
                    bestFraction = fraction;
                    best = p.name + ", with changes (" + Mathf.RoundToInt(fraction * 100f) + "% of its files as shipped)";
                }
            }
            return best ?? "neither version";
        }

        /// <summary>Warns when the pipeline's own package is not in the project, or is a version the content was not made with.</summary>
        private static void PackageNote(PipelineInfo pipeline)
        {
            string version = PipelineInstaller.PackageVersion(pipeline.unityPackage);
            if (version == null)
            {
                EditorGUILayout.HelpBox(pipeline.name + " is not installed in this project (" + pipeline.unityPackage + "). " +
                                        "Install it from the Package Manager and make it the project's pipeline before installing its content.",
                                        MessageType.Warning);
            }
            else if (!string.IsNullOrEmpty(pipeline.testedWith) && version.Split('.')[0] != pipeline.testedWith.Split('.')[0])
            {
                EditorGUILayout.HelpBox("The content was made with " + pipeline.unityPackage + " " + pipeline.testedWith +
                                        ". This project has " + version + "; it may need adjusting.", MessageType.Warning);
            }
        }

        /// <summary>Lists the other pipeline's packages for the user to remove. The kit never removes a package itself.</summary>
        private void UnneededPackagesSection(PipelineManifest manifest)
        {
            List<string> packages = PipelineInstaller.UnneededPackages();
            if (packages.Count == 0)
                return;
            GUILayout.Space(10);
            GUILayout.Label("Packages you no longer need", EditorStyles.boldLabel);
            GUILayout.Label("The kit adds the Universal Render Pipeline to a project so that it compiles before the " +
                            "installer has run. The HDRP content does not use these packages, so you can remove them in " +
                            "Package Manager. Anything of your own that uses URP stops compiling without them, and importing " +
                            "the kit again adds them back.", new GUIStyle(EditorStyles.label) { wordWrap = true });
            foreach (string package in packages)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(package, EditorStyles.miniLabel);
                    if (GUILayout.Button("Open Package Manager", EditorStyles.miniButton, GUILayout.Width(150f)))
                        UnityEditor.PackageManager.UI.Window.Open(package);
                }
            }
        }

        private void SwitchSection(PipelineManifest manifest, string project, string installed)
        {
            // Offered once the content is right: switching the other way is for changing the project's pipeline.
            if (project == null || installed != project)
                return;
            PipelineInfo other = manifest.pipelines.FirstOrDefault(p => p.id != project);
            if (other == null)
                return;
            GUILayout.Space(10);
            GUILayout.Label("Changing pipeline later", EditorStyles.boldLabel);
            GUILayout.Label("If you move the project to " + other.name + ", come back here and install its content. " +
                            "You can also install it now without switching.", new GUIStyle(EditorStyles.label) { wordWrap = true });
            if (GUILayout.Button("Install " + other.name + " content anyway", GUILayout.Height(22)))
                PipelineInstaller.Install(other.id);
        }

        private void FilesSection(PipelineManifest manifest, string project, string installed)
        {
            string target = installed == project ? null : project;
            if (target == null)
                return;
            PipelineInstaller.Plan plan = PipelineInstaller.PlanInstall(target);
            if (plan == null)
                return;
            GUILayout.Space(10);
            _showFiles = EditorGUILayout.Foldout(_showFiles, plan.overwrite.Count + " replaced, " + plan.add.Count + " added, " +
                                                              plan.remove.Count + " removed", true);
            if (!_showFiles)
                return;
            foreach (string p in plan.overwrite.Take(300))
                EditorGUILayout.LabelField("replace  " + p, EditorStyles.miniLabel);
            foreach (string p in plan.add.Take(300))
                EditorGUILayout.LabelField("add      " + p, EditorStyles.miniLabel);
            foreach (string p in plan.remove.Take(300))
                EditorGUILayout.LabelField("remove   " + p, EditorStyles.miniLabel);
        }
    }
}
