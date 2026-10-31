using System.Collections.Generic;
using System.Text.RegularExpressions;
using LiteNetLibManager;
using MultiplayerARPG;
using NUnit.Framework;
using OpenMMORPG.Tests;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OpenMMORPG.Demo.Tests
{
    /// <summary>
    /// Scene objects are matched between server and client, and to saved database rows, by their
    /// scene object id. Opens each demo scene alongside whatever is open and closes it again
    /// without saving.
    /// </summary>
    [Category(TestCategories.Content)]
    public class DemoSceneObjectIdTests
    {
        private static IEnumerable<string> ScenePaths()
        {
            return DemoTestPaths.Scenes();
        }

        /// <summary>A scene object's network identity, read before the scene is closed again.</summary>
        private struct SceneObject
        {
            public string name;
            public string id;
            public bool isBuilding;
        }

        private static List<SceneObject> SceneObjects(string scenePath)
        {
            List<SceneObject> result = new List<SceneObject>();
            Scene scene = SceneManager.GetSceneByPath(scenePath);
            bool opened = !scene.isLoaded;
            if (opened)
                scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            try
            {
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    foreach (LiteNetLibIdentity identity in root.GetComponentsInChildren<LiteNetLibIdentity>(true))
                    {
                        result.Add(new SceneObject()
                        {
                            name = identity.name,
                            id = identity.SceneObjectId ?? string.Empty,
                            isBuilding = identity.GetComponent<BuildingEntity>() != null,
                        });
                    }
                }
            }
            finally
            {
                if (opened)
                    EditorSceneManager.CloseScene(scene, true);
            }
            return result;
        }

        /// <summary>
        /// Two objects with one id: one of them refuses to spawn, and the client then misreads the
        /// rest of the spawn stream, so every NPC went missing at once (2026-09-19). Marek and
        /// Oswin later shared `DemoVillager_1` the same way (2026-09-30).
        /// </summary>
        [TestCaseSource(nameof(ScenePaths))]
        public void NoTwoSceneObjectsShareAnId(string scenePath)
        {
            Dictionary<string, string> seen = new Dictionary<string, string>();
            List<string> clashes = new List<string>();
            foreach (SceneObject sceneObject in SceneObjects(scenePath))
            {
                if (sceneObject.id.Length == 0)
                    continue;
                if (seen.TryGetValue(sceneObject.id, out string other))
                    clashes.Add($"\"{sceneObject.id}\": {other} and {sceneObject.name}");
                else
                    seen.Add(sceneObject.id, sceneObject.name);
            }
            Assert.That(clashes, Is.Empty);
        }

        /// <summary>
        /// A building placed in a scene is saved under its scene object id. The kit generates ids
        /// as `{name}_{n}`, which renumber when the scene is rebuilt, and a saved row whose id no
        /// longer matches crashed the map server at start-up (the craft stations, 2026-09-22).
        /// </summary>
        [TestCaseSource(nameof(ScenePaths))]
        public void SceneBuildingsHavePinnedIds(string scenePath)
        {
            List<string> generated = new List<string>();
            foreach (SceneObject sceneObject in SceneObjects(scenePath))
            {
                if (sceneObject.isBuilding && Regex.IsMatch(sceneObject.id, "^" + Regex.Escape(sceneObject.name) + @"_\d+$"))
                    generated.Add($"{sceneObject.name}: \"{sceneObject.id}\"");
            }
            Assert.That(generated, Is.Empty, "buildings whose saved rows will orphan when the scene is rebuilt");
        }
    }
}
