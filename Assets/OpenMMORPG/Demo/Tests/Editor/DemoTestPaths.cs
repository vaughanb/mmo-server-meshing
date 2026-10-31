using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace OpenMMORPG.Demo.Tests
{
    /// <summary>Finds the demo's assets, wherever the kit sits in the project.</summary>
    public static class DemoTestPaths
    {
        /// <summary>The demo folder, found from one of its scripts so a moved kit still works.</summary>
        public static string Root
        {
            get
            {
                foreach (string guid in AssetDatabase.FindAssets("KillAnyMonsterQuestTask t:MonoScript"))
                {
                    string script = AssetDatabase.GUIDToAssetPath(guid);
                    if (script.EndsWith("/Scripts/KillAnyMonsterQuestTask.cs"))
                        return Path.GetDirectoryName(Path.GetDirectoryName(script)).Replace('\\', '/');
                }
                Assert.Fail("The demo's scripts were not found; is the demo in this project?");
                return null;
            }
        }

        /// <summary>Every asset of type T in the demo, sub-assets included, with its path. Fails if there are none.</summary>
        public static List<(T asset, string path)> Assets<T>() where T : Object
        {
            List<(T, string)> result = new List<(T, string)>();
            foreach (string guid in AssetDatabase.FindAssets("t:" + typeof(T).Name, new[] { Root }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(path))
                {
                    if (asset is T typed)
                        result.Add((typed, path));
                }
            }
            Assert.That(result, Is.Not.Empty, $"no {typeof(T).Name} found in the demo");
            return result;
        }

        /// <summary>Every demo prefab whose root has a T, with its path. Fails if there are none.</summary>
        public static List<(T component, string path)> Prefabs<T>() where T : Component
        {
            List<(T, string)> result = new List<(T, string)>();
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { Root }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab != null && prefab.TryGetComponent(out T component))
                    result.Add((component, path));
            }
            Assert.That(result, Is.Not.Empty, $"no demo prefab has a {typeof(T).Name}");
            return result;
        }

        /// <summary>The demo's scene files.</summary>
        public static IEnumerable<string> Scenes()
        {
            foreach (string guid in AssetDatabase.FindAssets("t:Scene", new[] { Root }))
                yield return AssetDatabase.GUIDToAssetPath(guid);
        }
    }
}
