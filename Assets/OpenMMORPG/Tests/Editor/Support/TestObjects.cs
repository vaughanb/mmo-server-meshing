using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// Owns everything a test creates - assets, GameObjects and harnesses such as
    /// <see cref="TestGameInstance"/> - and releases it all in reverse order on Dispose, so a
    /// failing test leaves nothing behind in the editor. Everything is HideAndDontSave: it never
    /// shows in the hierarchy and is never written into the open scene.
    /// </summary>
    public sealed class TestObjects : IDisposable
    {
        // Unity objects and IDisposables in creation order; released last-first.
        private readonly List<object> _owned = new List<object>();

        /// <summary>
        /// Creates a ScriptableObject. Game data takes its id - and so its DataId - from the
        /// asset name, so the default name is unique per test.
        /// </summary>
        public T Create<T>(string name = null) where T : ScriptableObject
        {
            T asset = ScriptableObject.CreateInstance<T>();
            asset.name = name ?? $"{typeof(T).Name}_{_owned.Count}";
            asset.hideFlags = HideFlags.HideAndDontSave;
            _owned.Add(asset);
            return asset;
        }

        public GameObject NewGameObject(string name)
        {
            GameObject gameObject = new GameObject(name);
            gameObject.hideFlags = HideFlags.HideAndDontSave;
            _owned.Add(gameObject);
            return gameObject;
        }

        /// <summary>Takes ownership of an object made some other way.</summary>
        public T Own<T>(T owned) where T : class
        {
            _owned.Add(owned);
            return owned;
        }

        public void Dispose()
        {
            for (int i = _owned.Count - 1; i >= 0; --i)
            {
                switch (_owned[i])
                {
                    case Object unityObject:
                        if (unityObject != null)
                            Object.DestroyImmediate(unityObject);
                        break;
                    case IDisposable disposable:
                        disposable.Dispose();
                        break;
                }
            }
            _owned.Clear();
        }
    }
}
