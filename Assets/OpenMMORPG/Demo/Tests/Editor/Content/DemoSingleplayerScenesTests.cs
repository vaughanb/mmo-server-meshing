using System.Linq;
using MultiplayerARPG;
using MultiplayerARPG.MMO;
using NUnit.Framework;
using OpenMMORPG.Tests;
using UnityEditor;
using UnityEditor.Build.Profile;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OpenMMORPG.Demo.Tests
{
    [Category(TestCategories.Content)]
    public class DemoSingleplayerScenesTests
    {
        private static string InitSingleplayerPath => DemoTestPaths.Root + "/Scenes/00Init-Singleplayer.unity";
        private static string HomeSingleplayerPath => DemoTestPaths.Root + "/Scenes/01Home-Singleplayer.unity";

        private static string ClientProfilePath => DemoTestPaths.Root + "/Build Profiles/Demo Client.asset";

        // The kit ships the scene list in the Demo Client build profile, not in the project's build settings,
        // which belong to the user's project (and are empty in a clean one).
        [Test]
        public void SingleplayerScenesAreEnabledInTheDemoClientProfile()
        {
            BuildProfile profile = AssetDatabase.LoadAssetAtPath<BuildProfile>(ClientProfilePath);
            Assert.That(profile, Is.Not.Null, $"{ClientProfilePath} must exist.");
            Assert.That(profile.scenes.Any(s => s.path == InitSingleplayerPath && s.enabled),
                Is.True, $"{InitSingleplayerPath} must be enabled in the Demo Client profile.");
            Assert.That(profile.scenes.Any(s => s.path == HomeSingleplayerPath && s.enabled),
                Is.True, $"{HomeSingleplayerPath} must be enabled in the Demo Client profile.");
        }

        [Test]
        public void InitSingleplayerSceneConfiguresSingleplayerNetworkManagerAndHomeScene()
        {
            Scene alreadyLoaded = SceneManager.GetSceneByPath(InitSingleplayerPath);
            bool wasLoaded = alreadyLoaded.isLoaded;
            Scene scene = wasLoaded
                ? alreadyLoaded
                : EditorSceneManager.OpenScene(InitSingleplayerPath, OpenSceneMode.Additive);
            try
            {
                GameObject[] roots = scene.GetRootGameObjects();
                GameInstance gameInstance = roots.Select(r => r.GetComponentInChildren<GameInstance>(true)).FirstOrDefault(c => c != null);
                LanRpgNetworkManager lanManager = roots.Select(r => r.GetComponentInChildren<LanRpgNetworkManager>(true)).FirstOrDefault(c => c != null);
                MMOServerInstance mmoServer = roots.Select(r => r.GetComponentInChildren<MMOServerInstance>(true)).FirstOrDefault(c => c != null);
                MMOClientInstance mmoClient = roots.Select(r => r.GetComponentInChildren<MMOClientInstance>(true)).FirstOrDefault(c => c != null);
                MapNetworkManager mapManager = roots.Select(r => r.GetComponentInChildren<MapNetworkManager>(true)).FirstOrDefault(c => c != null);

                Assert.That(gameInstance, Is.Not.Null, "00Init-Singleplayer must have a GameInstance.");
                Assert.That(gameInstance.homeScene.SceneName, Is.EqualTo("01Home-Singleplayer"));
                Assert.That(lanManager, Is.Not.Null, "00Init-Singleplayer must have a LanRpgNetworkManager.");
                Assert.That(lanManager.startType, Is.EqualTo(LanRpgNetworkManager.GameStartType.SinglePlayer));
                Assert.That(mmoServer, Is.Null, "00Init-Singleplayer must not contain MMOServerInstance.");
                Assert.That(mmoClient, Is.Null, "00Init-Singleplayer must not contain MMOClientInstance.");
                Assert.That(mapManager, Is.Null, "00Init-Singleplayer must not contain MapNetworkManager.");
            }
            finally
            {
                if (!wasLoaded)
                    EditorSceneManager.CloseScene(scene, true);
            }
        }

        [Test]
        public void HomeSingleplayerSceneOpensStraightIntoCharacterList()
        {
            Scene alreadyLoaded = SceneManager.GetSceneByPath(HomeSingleplayerPath);
            bool wasLoaded = alreadyLoaded.isLoaded;
            Scene scene = wasLoaded
                ? alreadyLoaded
                : EditorSceneManager.OpenScene(HomeSingleplayerPath, OpenSceneMode.Additive);
            try
            {
                GameObject[] roots = scene.GetRootGameObjects();
                UISceneHome sceneHome = roots.Select(r => r.GetComponentInChildren<UISceneHome>(true)).FirstOrDefault(c => c != null);
                UIMmoSceneHome mmoSceneHome = roots.Select(r => r.GetComponentInChildren<UIMmoSceneHome>(true)).FirstOrDefault(c => c != null);
                UICharacterList charList = roots.Select(r => r.GetComponentInChildren<UICharacterList>(true)).FirstOrDefault(c => c != null);
                UICharacterCreate charCreate = roots.Select(r => r.GetComponentInChildren<UICharacterCreate>(true)).FirstOrDefault(c => c != null);

                Assert.That(sceneHome, Is.Not.Null, "01Home-Singleplayer must have a UISceneHome.");
                Assert.That(mmoSceneHome, Is.Null, "01Home-Singleplayer must not have a UIMmoSceneHome.");
                Assert.That(charList, Is.Not.Null, "01Home-Singleplayer must have a UICharacterList.");
                Assert.That(charList, Is.Not.InstanceOf<UIMmoCharacterList>(), "01Home-Singleplayer must use singleplayer UICharacterList.");
                Assert.That(charCreate, Is.Not.Null, "01Home-Singleplayer must have a UICharacterCreate.");
                Assert.That(charCreate, Is.Not.InstanceOf<UIMmoCharacterCreate>(), "01Home-Singleplayer must use singleplayer UICharacterCreate.");
                Assert.That(sceneHome.firstUI, Is.EqualTo(charList), "UISceneHome.firstUI must be UICharacterList.");
                Assert.That(sceneHome.uiCharacterList, Is.EqualTo(charList), "UISceneHome.uiCharacterList must be UICharacterList.");
                Assert.That(charList.characterModelContainer, Is.Not.Null, "UICharacterList.characterModelContainer must be wired.");
                Assert.That(charCreate.characterModelContainer, Is.Not.Null, "UICharacterCreate.characterModelContainer must be wired.");
            }
            finally
            {
                if (!wasLoaded)
                    EditorSceneManager.CloseScene(scene, true);
            }
        }
    }
}
