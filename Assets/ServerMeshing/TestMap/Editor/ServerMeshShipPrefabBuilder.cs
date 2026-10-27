// Server Meshing test rig — builds the stand-in ship prefab (Phase 0 step 7) in a build window, modelled on the space
// game's Ship.prefab at commit 1a3a58d9 (read there, not copied): one seat, hull 2,500, a mass-1 rigidbody with damping
// 3/8 and no gravity, the 8.4 x 3 x 11.9 box, two barrels. Its asset id is the prefab's own GUID, checked against every
// project prefab's hash, and a Resources reference asset reaches it so a player build includes it.
//
// Reached from the menu and from batch mode (-executeMethod MultiplayerARPG.MMO.Mesh.Editor.ServerMeshShipPrefabBuilder.BuildFromCommandLine).

using System;
using System.Collections.Generic;
using LiteNetLibManager;
using MultiplayerARPG.MMO.Mesh.Sync;
using UnityEditor;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Editor
{
    /// <summary>
    /// ServerMeshShipPrefabBuilder builds TestMap/Prefabs/ServerMeshShip.prefab and TestMap/Resources/ServerMeshShipPrefabRef.asset.
    /// The identity's asset id setter is internal to the networking assembly and a GUID exists only once the asset does,
    /// so the prefab is saved first and its assetId is then written through a SerializedObject on the saved asset.
    /// </summary>
    public static class ServerMeshShipPrefabBuilder
    {
        /// <summary>PrefabPath is where the prefab is saved (deliberately not under a Resources folder).</summary>
        public const string PrefabPath = "Assets/ServerMeshing/TestMap/Prefabs/ServerMeshShip.prefab";
        /// <summary>ReferenceFolder is the Resources folder that holds the reference asset.</summary>
        public const string ReferenceFolder = "Assets/ServerMeshing/TestMap/Resources";
        /// <summary>ReferencePath is the reference asset's path.</summary>
        public const string ReferencePath = ReferenceFolder + "/" + ServerMeshShipEntity.PrefabRefPath + ".asset";

        /// <summary>Menu builds the prefab from the editor menu.</summary>
        [MenuItem("Tools/Open MMORPG/Server Meshing/Testing/Build Ship Prefab")]
        public static void Menu()
        {
            Build();
        }

        /// <summary>BuildFromCommandLine is the batch-mode entry point; it exits 0 on success and 1 on failure.</summary>
        public static void BuildFromCommandLine()
        {
            bool ok;
            try
            {
                ok = Build();
            }
            catch (Exception ex)
            {
                Debug.LogError("Ships error: the ship prefab build failed: " + MeshStateSender.CleanForLog(ex.Message));
                ok = false;
            }
            EditorApplication.Exit(ok ? 0 : 1);
        }

        /// <summary>
        /// Build builds and saves the prefab, writes its GUID as its asset id, refuses (deleting the saved prefab again) on a
        /// hash clash with any other project prefab, then creates or updates the reference asset. It logs one Ship prefab
        /// line on success and one Ships error line on failure.
        /// </summary>
        public static bool Build()
        {
            GameObject root = CreateShip();
            GameObject saved;
            try
            {
                saved = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath, out bool success);
                if (!success || saved == null)
                {
                    Debug.LogError("Ships error: the ship prefab could not be saved at " + PrefabPath);
                    return false;
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
            string guid = AssetDatabase.AssetPathToGUID(PrefabPath);
            LiteNetLibIdentity identity = saved.GetComponent<LiteNetLibIdentity>();
            SerializedObject serializedIdentity = new SerializedObject(identity);
            serializedIdentity.FindProperty("assetId").stringValue = guid;
            serializedIdentity.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(saved);
            AssetDatabase.SaveAssets();
            int hash = identity.HashAssetId;
            string clash = FindHashClash(hash, PrefabPath);
            if (clash != null)
            {
                AssetDatabase.DeleteAsset(PrefabPath);
                Debug.LogError($"Ships error: the ship prefab's hash {hash} (asset id {guid}) clashes with {clash}; the prefab was deleted again");
                return false;
            }
            ServerMeshShipPrefabRef reference = AssetDatabase.LoadAssetAtPath<ServerMeshShipPrefabRef>(ReferencePath);
            if (reference == null)
            {
                if (!AssetDatabase.IsValidFolder(ReferenceFolder))
                    AssetDatabase.CreateFolder("Assets/ServerMeshing/TestMap", "Resources");
                reference = ScriptableObject.CreateInstance<ServerMeshShipPrefabRef>();
                reference.prefab = saved;
                AssetDatabase.CreateAsset(reference, ReferencePath);
            }
            else
            {
                reference.prefab = saved;
                EditorUtility.SetDirty(reference);
            }
            AssetDatabase.SaveAssets();
            Debug.Log($"Ship prefab: {PrefabPath} assetId {guid} hash {hash} components {ComponentList(saved)} ref {ReferencePath}");
            return true;
        }

        private static GameObject CreateShip()
        {
            GameObject root = new GameObject("ServerMeshShip");
            root.AddComponent<LiteNetLibIdentity>();
            ServerMeshShipEntity entity = root.AddComponent<ServerMeshShipEntity>();
            root.AddComponent<ServerMeshShipMovement>();
            ServerMeshShipPilot pilot = root.AddComponent<ServerMeshShipPilot>();
            pilot.enabled = false;
            root.AddComponent<ServerMeshShipCombat>();
            Rigidbody body = root.AddComponent<Rigidbody>();
            body.mass = 1f;
            body.linearDamping = ServerMeshShipClass.Base.LinearDrag;
            body.angularDamping = ServerMeshShipClass.Base.AngularDrag;
            body.useGravity = false;
            body.interpolation = RigidbodyInterpolation.None;
            body.collisionDetectionMode = CollisionDetectionMode.Discrete;
            BoxCollider box = root.AddComponent<BoxCollider>();
            box.size = new Vector3(8.4f, 3f, 11.9f);
            box.center = new Vector3(0f, 0.4f, 3.4f);

            GameObject hull = GameObject.CreatePrimitive(PrimitiveType.Cube);
            hull.name = "Hull";
            UnityEngine.Object.DestroyImmediate(hull.GetComponent<Collider>());
            hull.transform.SetParent(root.transform, false);
            hull.transform.localPosition = box.center;
            hull.transform.localScale = box.size;

            Transform left = Child(root, "BarrelLeft", new Vector3(-ServerMeshShipEntity.BarrelOffset.x, ServerMeshShipEntity.BarrelOffset.y, ServerMeshShipEntity.BarrelOffset.z));
            Transform right = Child(root, "BarrelRight", ServerMeshShipEntity.BarrelOffset);
            Transform seat = Child(root, "Seat", new Vector3(0f, 0.5f, 0f));
            Transform exit = Child(root, "Exit", new Vector3(0f, 0f, -8f));

            SerializedObject serializedEntity = new SerializedObject(entity);
            SerializedProperty seats = serializedEntity.FindProperty("seats");
            seats.arraySize = 1;
            SerializedProperty driver = seats.GetArrayElementAtIndex(0);
            driver.FindPropertyRelative("passengingTransform").objectReferenceValue = seat;
            driver.FindPropertyRelative("exitTransform").objectReferenceValue = exit;
            serializedEntity.FindProperty("canBeAttacked").boolValue = false;
            serializedEntity.FindProperty("hp").FindPropertyRelative("baseAmount").intValue = ServerMeshShipEntity.DefaultHull;
            SerializedProperty barrels = serializedEntity.FindProperty("barrels");
            barrels.arraySize = 2;
            barrels.GetArrayElementAtIndex(0).objectReferenceValue = left;
            barrels.GetArrayElementAtIndex(1).objectReferenceValue = right;
            serializedEntity.ApplyModifiedPropertiesWithoutUndo();
            return root;
        }

        private static Transform Child(GameObject root, string name, Vector3 localPosition)
        {
            GameObject child = new GameObject(name);
            child.transform.SetParent(root.transform, false);
            child.transform.localPosition = localPosition;
            return child.transform;
        }

        /// <summary>FindHashClash is the path of another project prefab whose root identity has <paramref name="hash"/>, or null.</summary>
        private static string FindHashClash(int hash, string ownPath)
        {
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.Equals(path, ownPath, StringComparison.OrdinalIgnoreCase))
                    continue;
                LiteNetLibIdentity other = AssetDatabase.LoadAssetAtPath<LiteNetLibIdentity>(path);
                if (other != null && !string.IsNullOrEmpty(other.AssetId) && other.HashAssetId == hash)
                    return path;
            }
            return null;
        }

        private static string ComponentList(GameObject saved)
        {
            List<string> names = new List<string>();
            foreach (Component component in saved.GetComponents<Component>())
                names.Add(component == null ? "missing" : component.GetType().Name);
            return string.Join(",", names);
        }
    }
}
