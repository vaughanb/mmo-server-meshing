using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace MultiplayerARPG.Demo.EditorTools
{
    /// <summary>
    /// Shown once, the first time a project opens with the demo in it: the four steps between
    /// importing the kit and walking around the island, the one that is easy to miss being the
    /// map server. The demo runs the kit's MMO flow, where each map is hosted by a separate
    /// process the map spawner launches - a build of this project at <c>builds/OpenMMORPG.exe</c>
    /// beside <c>Assets</c> - so pressing Play gets as far as character select and no further
    /// until that build exists.
    ///
    /// Every button acts only when pressed. Nothing here opens a web page, changes a setting or
    /// starts a build on its own. `Open MMORPG > Demo > Welcome` brings the window back.
    /// </summary>
    [InitializeOnLoad]
    internal static class DemoWelcomeLauncher
    {
        static DemoWelcomeLauncher()
        {
            // Not a delayCall: a fresh project goes through several domain reloads while its
            // packages resolve and its scripts compile, and a call queued before the last of them
            // is lost. Wait until the editor has settled instead.
            EditorApplication.update += ShowWhenSettled;
        }

        private static void ShowWhenSettled()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            // The render pipeline installer comes first: the steps below assume the demo's content matches the pipeline.
            // This keeps waiting, and the window opens once the content is installed or the user has said not now.
            if (OpenMMORPG.Setup.PipelineInstaller.IsContentPending)
                return;
            EditorApplication.update -= ShowWhenSettled;
            if (Application.isBatchMode || DemoWelcomeWindow.IsShown)
                return;
            DemoWelcomeWindow.IsShown = true;
            DemoWelcomeWindow.Open();
        }
    }

    /// <summary>
    /// Shown once, the first time a project opens with the demo in it: the four steps between
    /// importing the kit and walking around the island, the one that is easy to miss being the
    /// map server. The demo runs the kit's MMO flow, where each map is hosted by a separate
    /// process the map spawner launches - a build of this project at <c>builds/OpenMMORPG.exe</c>
    /// beside <c>Assets</c> - so pressing Play gets as far as character select and no further
    /// until that build exists.
    ///
    /// Every button acts only when pressed. Nothing here opens a web page, changes a setting or
    /// starts a build on its own. `Tools > Open MMORPG > Demo > Welcome` brings the window back.
    /// </summary>
    public class DemoWelcomeWindow : EditorWindow
    {
        private const string DocumentationUrl = "https://open-mmorpg.github.io/documentation/";
        private const string SettingsMenu = "Tools/Open MMORPG/Install/Import Project Settings";
        private const string DemoDir = "Assets/OpenMMORPG/Demo";
        private const string RenderPipelinePath = DemoDir + "/Settings/DemoURP.asset";
        private const string LowRenderPipelinePath = DemoDir + "/Settings/DemoURP_Low.asset";
        private const string ServerProfilePath = DemoDir + "/Build Profiles/Demo Map Server.asset";
        private const string ClientProfilePath = DemoDir + "/Build Profiles/Demo Client.asset";
        private const string FirstScenePath = DemoDir + "/Scenes/00Init.unity";
        private const string ReadmePath = "Assets/OpenMMORPG/README.md";
        private const string LogoPath = DemoDir + "/Textures/OpenMMORPG-title.png";
        private const string FallbackLogoPath = "Assets/OpenMMORPG/Resources/OpenMMORPG.png";
        private const string ShownConfigKey = "OpenMMORPG.DemoWelcomeShown";

        /// <summary>Where the map spawner looks for the map server, relative to the project folder.</summary>
        private const string ServerBuildPath = "builds/OpenMMORPG.exe";

        private Vector2 _scroll;
        [System.NonSerialized]
        private Texture2D _logoTexture;

        private Texture2D LogoTexture
        {
            get
            {
                if (_logoTexture == null)
                {
                    _logoTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(LogoPath);
                    if (_logoTexture == null)
                        _logoTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(FallbackLogoPath);
                }
                return _logoTexture;
            }
        }

        /// <summary>
        /// Tracked per project in UserSettings (ignored by git). Unlike machine-global EditorPrefs,
        /// this ensures recreating a fresh project at the same folder path still shows the welcome window.
        /// </summary>
        internal static bool IsShown
        {
            get { return EditorUserSettings.GetConfigValue(ShownConfigKey) == "true"; }
            set { EditorUserSettings.SetConfigValue(ShownConfigKey, value ? "true" : "false"); }
        }

        [MenuItem("Tools/Open MMORPG/Demo/Welcome", false, 0)]
        public static void Open()
        {
            var window = GetWindow<DemoWelcomeWindow>(true, "Welcome to Open MMORPG", true);
            window.minSize = new Vector2(560f, 620f);
            Rect main = EditorGUIUtility.GetMainWindowPosition();
            window.position = new Rect(main.x + (main.width - 560f) * 0.5f, main.y + (main.height - 680f) * 0.5f, 560f, 680f);
        }

        private void OnGUI()
        {
            var wrap = new GUIStyle(EditorStyles.label) { wordWrap = true, richText = true };
            var heading = new GUIStyle(EditorStyles.boldLabel) { fontSize = 13, wordWrap = true };

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            GUILayout.Space(8);

            Texture2D logo = LogoTexture;
            if (logo != null)
            {
                float maxWidth = 380f;
                float availableWidth = Mathf.Max(200f, EditorGUIUtility.currentViewWidth - 40f);
                float drawWidth = Mathf.Min(maxWidth, availableWidth);
                float drawHeight = drawWidth * ((float)logo.height / logo.width);

                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                Rect logoRect = GUILayoutUtility.GetRect(drawWidth, drawHeight, GUILayout.Width(drawWidth), GUILayout.Height(drawHeight));
                GUI.DrawTexture(logoRect, logo, ScaleMode.ScaleToFit, true);
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();

                GUILayout.Space(8);
            }
            else
            {
                GUILayout.Label("Welcome to Open MMORPG", new GUIStyle(EditorStyles.boldLabel) { fontSize = 16 });
            }
            GUILayout.Label(
                "The demo is a small island and a dungeon running on the kit's MMO servers. Four steps get " +
                "you from here to playing it. The third is the one people miss: each map is hosted by a " +
                "separate build of this project, so until it exists Play stops at character select.", wrap);

            Step(heading, wrap, "1. Import the project settings",
                "Input, physics, tags and layers, quality and time settings the kit expects. Best done " +
                "on a new project, since it replaces those files.");
            if (GUILayout.Button("Import Project Settings", GUILayout.Height(24)))
                EditorApplication.ExecuteMenuItem(SettingsMenu);

            PipelineStep(heading, wrap);

            Step(heading, wrap, "3. Build the map server",
                "The demo runs as an MMO cluster where maps run as separate server processes. " +
                "Click <b>Build Demo Map Server</b> below to automatically compile the demo map server to " +
                "<b>" + ServerBuildPath + "</b> without having to switch build profiles manually.\n\n" +
                "Rebuild it after changing any scene, entity or game data the server uses.");
            bool built = File.Exists(Path.Combine(Path.GetDirectoryName(Application.dataPath), ServerBuildPath));
            EditorGUILayout.HelpBox(built ? "Map server found at " + ServerBuildPath + "."
                                          : "No map server at " + ServerBuildPath + " yet.",
                                    built ? MessageType.Info : MessageType.Warning);
            if (GUILayout.Button(built ? "Rebuild Demo Map Server" : "Build Demo Map Server", GUILayout.Height(28)))
            {
                DemoBuildTools.BuildDemoMapServer();
            }
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Open Build Profiles", GUILayout.Height(24)))
                EditorApplication.ExecuteMenuItem("File/Build Profiles");
            if (GUILayout.Button("Show Server Profile", GUILayout.Height(24)))
                Reveal(ServerProfilePath);
            if (GUILayout.Button("Show Client Profile", GUILayout.Height(24)))
                Reveal(ClientProfilePath);
            EditorGUILayout.EndHorizontal();

            Step(heading, wrap, "4. Play",
                "Open the 00Init scene and press Play. It starts the login, central, database and map " +
                "spawn servers in the editor and shows the server list; register an account, make a " +
                "character and start.");
            if (GUILayout.Button("Open 00Init", GUILayout.Height(24)) &&
                EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                EditorSceneManager.OpenScene(FirstScenePath);

            GUILayout.Space(14);
            GUILayout.Label("More", heading);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Show README", GUILayout.Height(24)))
                Reveal(ReadmePath);
            if (GUILayout.Button("Open Documentation (web)", GUILayout.Height(24)))
                Application.OpenURL(DocumentationUrl);
            EditorGUILayout.EndHorizontal();
            GUILayout.Label("This window opens once. Open MMORPG > Demo > Welcome brings it back.", EditorStyles.miniLabel);
            GUILayout.Space(8);
            EditorGUILayout.EndScrollView();
        }

        private static void Step(GUIStyle heading, GUIStyle wrap, string title, string body)
        {
            GUILayout.Space(14);
            GUILayout.Label(title, heading);
            GUILayout.Label(body, wrap);
        }

        private enum Pipeline { BuiltIn, Urp, Hdrp, Other }

        /// <summary>
        /// Which pipeline the project is rendering with, by the pipeline asset's type name, so this window needs neither
        /// package to compile and can be the same file in a URP project and an HDRP one.
        /// </summary>
        private static Pipeline DetectPipeline()
        {
            RenderPipelineAsset current = GraphicsSettings.currentRenderPipeline;
            if (current == null)
                return Pipeline.BuiltIn;
            string type = current.GetType().Name;
            if (type == "UniversalRenderPipelineAsset")
                return Pipeline.Urp;
            if (type == "HDRenderPipelineAsset")
                return Pipeline.Hdrp;
            return Pipeline.Other;
        }

        private static string CurrentPipeline()
        {
            RenderPipelineAsset current = GraphicsSettings.defaultRenderPipeline;
            if (current == null)
                return "none (Built-in Render Pipeline)";
            return AssetDatabase.GetAssetPath(current) == RenderPipelinePath ? "the demo's" : current.name;
        }

        private void PipelineStep(GUIStyle heading, GUIStyle wrap)
        {
            Pipeline pipeline = DetectPipeline();
            switch (pipeline)
            {
                case Pipeline.Urp:
                    if (OpenMMORPG.Setup.PipelineInstaller.NeedsContent)
                    {
                        Step(heading, wrap, "2. Install the URP content",
                            "This project renders with URP but the demo's materials, shaders, scenes and lights are the HDRP " +
                            "version. One click installs the URP version of them.");
                        if (GUILayout.Button("Install URP Content", GUILayout.Height(24)))
                            OpenMMORPG.Setup.PipelineInstallerWindow.Open();
                        break;
                    }
                    Step(heading, wrap, "2. Use the demo's render settings",
                        "The demo is lit for its own Universal Render Pipeline asset, and the sea needs the depth " +
                        "and opaque textures it turns on. This also gives the lower half of your Quality levels a " +
                        "lighter one (no ambient occlusion, two shadow cascades, hard shadows), so the Quality " +
                        "setting does something on a slower machine. Currently in use: <b>" + CurrentPipeline() + "</b>.");
                    if (GUILayout.Button("Use Demo Render Pipeline", GUILayout.Height(24)))
                        UseDemoPipeline();
                    break;

                case Pipeline.Hdrp:
                    // The demo's content is installed for the pipeline by Tools > Open MMORPG > Install > Render Pipeline Content.
                    if (OpenMMORPG.Setup.PipelineInstaller.NeedsContent)
                    {
                        Step(heading, wrap, "2. Install the HDRP content",
                            "This project renders with HDRP but the demo's materials, shaders, scenes and lights are the URP " +
                            "version. One click installs the HDRP version of them.");
                        if (GUILayout.Button("Install HDRP Content", GUILayout.Height(24)))
                            OpenMMORPG.Setup.PipelineInstallerWindow.Open();
                        break;
                    }
                    Step(heading, wrap, "2. Check the HDRP settings",
                        "This project renders with HDRP (<b>" + GraphicsSettings.currentRenderPipeline.name + "</b>), and the " +
                        "demo needs nothing special of it: the sky, fog, exposure and sun shadows are Volumes in the scenes, " +
                        "and the lights are in physical units. Your own pipeline asset is left alone.\n\n" +
                        "One optional setting: the Resolution Scaling option in the graphics settings uses HDRP's dynamic " +
                        "resolution, which is " + (DynamicResolutionEnabled() ? "<b>on</b>" : "<b>off</b>") + " in this asset. " +
                        "If it is off the game turns it on the first time a player moves the slider, which rebuilds the " +
                        "pipeline once (a short hitch); enabling it here avoids that.");
                    using (new EditorGUI.DisabledScope(DynamicResolutionEnabled()))
                    {
                        if (GUILayout.Button("Enable Dynamic Resolution", GUILayout.Height(24)))
                            EnableDynamicResolution();
                    }
                    break;

                default:
                    Step(heading, wrap, "2. Render pipeline",
                        "This project is using <b>" + CurrentPipeline() + "</b>. The demo is made for the Universal " +
                        "Render Pipeline and for HDRP; install and select one of them under Project Settings > Graphics " +
                        "before going on.");
                    break;
            }
        }

        // The HDRP asset is read and written by property path, so this file needs no reference to the HDRP package.
        private const string DynamicResolutionPath = "m_RenderPipelineSettings.dynamicResolutionSettings";

        private static bool DynamicResolutionEnabled()
        {
            var so = new SerializedObject(GraphicsSettings.currentRenderPipeline);
            SerializedProperty enabled = so.FindProperty(DynamicResolutionPath + ".enabled");
            return enabled != null && enabled.boolValue;
        }

        /// <summary>Turns dynamic resolution on with a range down to 30%, the lowest the Resolution Scaling slider goes.</summary>
        private static void EnableDynamicResolution()
        {
            RenderPipelineAsset asset = GraphicsSettings.currentRenderPipeline;
            var so = new SerializedObject(asset);
            SerializedProperty enabled = so.FindProperty(DynamicResolutionPath + ".enabled");
            SerializedProperty min = so.FindProperty(DynamicResolutionPath + ".minPercentage");
            SerializedProperty max = so.FindProperty(DynamicResolutionPath + ".maxPercentage");
            if (enabled == null || min == null || max == null)
            {
                EditorUtility.DisplayDialog("Dynamic Resolution", "Could not find the dynamic resolution settings on " + asset.name +
                    ". Turn it on under the asset's Quality > Dynamic Resolution.", "OK");
                return;
            }
            enabled.boolValue = true;
            min.floatValue = Mathf.Min(min.floatValue, 30f);
            max.floatValue = 100f;
            so.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();
        }

        private static void UseDemoPipeline()
        {
            var pipeline = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(RenderPipelinePath);
            if (pipeline == null)
            {
                EditorUtility.DisplayDialog("Render Pipeline Not Found", "Could not find " + RenderPipelinePath + ".", "OK");
                return;
            }
            if (!EditorUtility.DisplayDialog("Use Demo Render Pipeline",
                    "Sets the project's default render pipeline to the demo's URP asset, and gives each Quality " +
                    "level one: the lower half the lighter asset, the rest the full one. You can change it back " +
                    "under Project Settings > Graphics and Quality.",
                    "Use It", "Cancel"))
                return;
            ApplyDemoPipeline();
        }

        /// <summary>
        /// The default pipeline, and a pipeline for every Quality level: the lower half of the levels get
        /// `DemoURP_Low`, the rest `DemoURP`. Six levels are Very Low to Medium on the lighter one.
        /// </summary>
        internal static void ApplyDemoPipeline()
        {
            var full = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(RenderPipelinePath);
            var low = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(LowRenderPipelinePath);
            if (full == null)
                return;
            GraphicsSettings.defaultRenderPipeline = full;
            int levels = QualitySettings.names.Length;
            int previous = QualitySettings.GetQualityLevel();
            for (int i = 0; i < levels; ++i)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.renderPipeline = i < levels / 2 && low != null ? low : full;
            }
            QualitySettings.SetQualityLevel(previous, false);
            AssetDatabase.SaveAssets();
        }

        private static void Reveal(string path)
        {
            Object asset = AssetDatabase.LoadMainAssetAtPath(path);
            if (asset == null)
                return;
            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
        }
    }
}
