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
