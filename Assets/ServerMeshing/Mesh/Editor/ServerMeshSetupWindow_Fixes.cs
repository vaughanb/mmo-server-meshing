// Server Meshing add-on — Setup window, the prefab and asset edits behind its Fix buttons (moved out of
// ServerMeshSetupWindow.cs for size, Slice 6). Each is an asset edit you could also do by hand.
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Editor
{
    public partial class ServerMeshSetupWindow
    {
        private static string ShortPath(string path)
        {
            return System.IO.Path.GetFileNameWithoutExtension(path);
        }

        /// <summary>
        /// The prefabs the window validates: the game's, the add-on's, and the kit's demo. The rest of the kit is skipped,
        /// but the demo installs under it (Assets/OpenMMORPG/Demo) and a game built on the demo runs its server instance,
        /// player and monsters, so skipping the whole kit folder left them unvalidated with no Swap line.
        /// </summary>
        private static List<string> GamePrefabPaths()
        {
            List<string> paths = new List<string>();
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.StartsWith("Assets/") || (path.StartsWith(KitFolder) && !path.StartsWith(KitDemoFolder))) continue;
                paths.Add(path);
            }
            return paths;
        }

        private static MonoScript ScriptOf(Type type)
        {
            if (s_scripts == null)
            {
                s_scripts = new Dictionary<Type, MonoScript>();
                foreach (string guid in AssetDatabase.FindAssets("t:MonoScript"))
                {
                    MonoScript script = AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(guid));
                    Type scriptType = script != null ? script.GetClass() : null;
                    if (scriptType != null) s_scripts[scriptType] = script;
                }
            }
            return s_scripts.TryGetValue(type, out MonoScript found) ? found : null;
        }

        private void SwapScript(string prefabPath, Type kit, Type mesh)
        {
            MonoScript script = ScriptOf(mesh);
            if (script == null) { Debug.LogError($"[Mesh] No MonoScript for {mesh.Name}"); return; }
            GameObject contents = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                foreach (Component component in contents.GetComponentsInChildren(kit, true))
                {
                    if (component == null || mesh.IsInstanceOfType(component)) continue;
                    SerializedObject so = new SerializedObject(component);
                    so.FindProperty("m_Script").objectReferenceValue = script;
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
                PrefabUtility.SaveAsPrefabAsset(contents, prefabPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(contents); }
            AssignRegistry(prefabPath, mesh);
        }

        /// <summary>
        /// Swap all: every listed prefab's kit components for the add-on's subclasses, then the registry on each, one
        /// prefab load per pass. Only the components a prefab owns are touched: one inherited from a base prefab (a
        /// variant's, or a nested prefab's) is swapped on its source, which is in the list too, and the change flows down,
        /// so no prefab gets an override of a script it does not own.
        /// </summary>
        private void SwapAll(ICollection<string> prefabPaths)
        {
            int done = 0;
            try
            {
                foreach (string path in prefabPaths)
                {
                    EditorUtility.DisplayProgressBar("Swap all scripts", ShortPath(path), (float)done++ / prefabPaths.Count);
                    EditOwned(path, contents =>
                    {
                        bool clientInstance = contents.GetComponentInChildren<MMOClientInstance>(true) != null;
                        bool changed = false;
                        foreach ((Type kit, Type mesh) in s_swaps)
                        {
                            if (clientInstance && typeof(LiteNetLibManager.LiteNetLibManager).IsAssignableFrom(kit))
                                continue; // the client halves of the login and central managers: not servers
                            MonoScript script = ScriptOf(mesh);
                            if (script == null) { Debug.LogError($"[Mesh] No MonoScript for {mesh.Name}"); continue; }
                            foreach (Component component in OwnedComponents(contents, kit))
                            {
                                if (mesh.IsInstanceOfType(component)) continue;
                                SerializedObject so = new SerializedObject(component);
                                so.FindProperty("m_Script").objectReferenceValue = script;
                                so.ApplyModifiedPropertiesWithoutUndo();
                                changed = true;
                            }
                        }
                        return changed;
                    });
                }
                // A second load per prefab: the swapped components are the subclasses only once the prefab is reloaded.
                foreach (string path in prefabPaths)
                {
                    EditOwned(path, contents =>
                    {
                        bool changed = false;
                        foreach ((Type kit, Type mesh) in s_swaps)
                        {
                            foreach (Component component in OwnedComponents(contents, mesh))
                            {
                                SerializedObject so = new SerializedObject(component);
                                SerializedProperty world = so.FindProperty("world");
                                if (world == null || world.objectReferenceValue != null || _registry == null) continue;
                                world.objectReferenceValue = _registry;
                                so.ApplyModifiedPropertiesWithoutUndo();
                                changed = true;
                            }
                        }
                        return changed;
                    });
                }
            }
            finally { EditorUtility.ClearProgressBar(); }
        }

        /// <summary>Loads a prefab's contents, runs an edit, and saves only when the edit changed something.</summary>
        private static void EditOwned(string prefabPath, Func<GameObject, bool> edit)
        {
            GameObject contents = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                if (edit(contents))
                    PrefabUtility.SaveAsPrefabAsset(contents, prefabPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(contents); }
        }

        /// <summary>The components of a type this prefab owns: neither inherited from a base prefab nor from a nested one.</summary>
        private static IEnumerable<Component> OwnedComponents(GameObject contents, Type type)
        {
            foreach (Component component in contents.GetComponentsInChildren(type, true))
            {
                if (component != null && PrefabUtility.GetCorrespondingObjectFromSource(component) == null)
                    yield return component;
            }
        }

        private void AssignRegistry(string prefabPath, Type mesh)
        {
            if (_registry == null) return;
            GameObject contents = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                foreach (Component component in contents.GetComponentsInChildren(mesh, true))
                {
                    SerializedObject so = new SerializedObject(component);
                    SerializedProperty world = so.FindProperty("world");
                    if (world == null) continue;
                    world.objectReferenceValue = _registry;
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
                PrefabUtility.SaveAsPrefabAsset(contents, prefabPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(contents); }
        }

        private void AssignGameplayRule(string prefabPath)
        {
            string[] guids = AssetDatabase.FindAssets("t:MeshGameplayRule");
            MeshGameplayRule rule = guids.Length > 0 ? AssetDatabase.LoadAssetAtPath<MeshGameplayRule>(AssetDatabase.GUIDToAssetPath(guids[0])) : null;
            if (rule == null)
            {
                rule = CreateInstance<MeshGameplayRule>();
                AssetDatabase.CreateAsset(rule, AssetDatabase.GenerateUniqueAssetPath("Assets/ServerMeshing/MeshGameplayRule.asset"));
                AssetDatabase.SaveAssets();
            }
            GameObject contents = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                foreach (GameInstance instance in contents.GetComponentsInChildren<GameInstance>(true))
                {
                    SerializedObject so = new SerializedObject(instance);
                    so.FindProperty("gameplayRule").objectReferenceValue = rule;
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
                PrefabUtility.SaveAsPrefabAsset(contents, prefabPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(contents); }
        }

        private static void SetBool(string prefabPath, Type componentType, string property, bool value)
        {
            GameObject contents = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                foreach (Component component in contents.GetComponentsInChildren(componentType, true))
                {
                    SerializedObject so = new SerializedObject(component);
                    SerializedProperty p = so.FindProperty(property);
                    if (p == null) continue;
                    p.boolValue = value;
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
                PrefabUtility.SaveAsPrefabAsset(contents, prefabPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(contents); }
        }

        private static void SetInt(string prefabPath, Type componentType, string property, int value)
        {
            GameObject contents = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                foreach (Component component in contents.GetComponentsInChildren(componentType, true))
                {
                    SerializedObject so = new SerializedObject(component);
                    SerializedProperty p = so.FindProperty(property);
                    if (p == null) continue;
                    p.intValue = value;
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
                PrefabUtility.SaveAsPrefabAsset(contents, prefabPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(contents); }
        }
    }
}
