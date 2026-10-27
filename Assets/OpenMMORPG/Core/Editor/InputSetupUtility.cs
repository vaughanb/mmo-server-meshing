using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MultiplayerARPG
{
    public class InputSetupUtility : EditorWindow
    {
        private const string INPUT_ACTIONS_PATH = "Assets/OpenMMORPG/Core/Input/OpenMMORPG_InputActions.inputactions";
        private const string GAME_INSTANCE_PREFAB_PATH = "Assets/OpenMMORPG/Demo/Prefabs/GameInstance.prefab";

        public enum InputHandlingOption
        {
            NewInputSystem = 1,
            Both = 2,
            LegacyInputManager = 0,
        }

        [MenuItem("Tools/Open MMORPG/Input System Setup", false, 50)]
        public static void OpenWindow()
        {
            var window = GetWindow<InputSetupUtility>("Input Setup");
            window.minSize = new Vector2(480, 420);
            window.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(10);
            GUILayout.Label("Open MMORPG - Input Backend Configuration", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Configure how Open MMORPG receives input. You can choose between the modern Unity New Input System (default in Unity 6 / URP), " +
                "the Legacy Input Manager, or Hybrid mode (Both) where both systems are active.",
                MessageType.Info);

            EditorGUILayout.Space(10);

            // Current Status
            int currentHandler = GetActiveInputHandler();
            string statusText = currentHandler == 1 ? "New Input System (1)" :
                                (currentHandler == 2 ? "Both / Hybrid (2)" : "Legacy Input Manager (0)");

            EditorGUILayout.LabelField("Current PlayerSettings Backend:", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Active Input Handling:", statusText);

            var inputAsset = AssetDatabase.LoadAssetAtPath<InputActionAsset>(INPUT_ACTIONS_PATH);
            EditorGUILayout.LabelField("Default InputActionAsset:", inputAsset != null ? "Found (" + inputAsset.name + ")" : "Missing");

            var gameInstance = AssetDatabase.LoadAssetAtPath<GameObject>(GAME_INSTANCE_PREFAB_PATH);
            bool isAssigned = false;
#if ENABLE_INPUT_SYSTEM
            if (gameInstance != null)
            {
                var inputSetting = gameInstance.GetComponent<Insthync.CameraAndInput.InputSettingManager>();
                if (inputSetting != null && inputSetting.inputActionAsset != null)
                {
                    isAssigned = true;
                }
            }
#endif
            EditorGUILayout.LabelField("GameInstance.prefab Link:", isAssigned ? "Assigned" : "Not Assigned");

            EditorGUILayout.Space(15);
            GUILayout.Label("Choose Input Option:", EditorStyles.boldLabel);

            // Option 1: New Input System
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label("Option 1: Modern / URP (New Input System)", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("• Recommended for Unity 6 and URP projects.", EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.LabelField("• Native Gamepad/Cross-platform support & zero restart prompts.", EditorStyles.wordWrappedMiniLabel);
            if (GUILayout.Button("Select: New Input System (Modern)"))
            {
                ApplyInputConfiguration(InputHandlingOption.NewInputSystem);
            }
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(5);

            // Option 2: Both / Hybrid
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label("Option 2: Hybrid Mode (Both)", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("• Enables both backends simultaneously.", EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.LabelField("• Allows classic KeyCode inspector overrides and new InputAction mappings.", EditorStyles.wordWrappedMiniLabel);
            if (GUILayout.Button("Select: Both / Hybrid (Safe Coexistence)"))
            {
                ApplyInputConfiguration(InputHandlingOption.Both);
            }
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(5);

            // Option 3: Legacy Input Manager
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label("Option 3: Classic (Legacy Input Manager)", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("• Uses Unity's classic InputManager.asset and KeyCode settings.", EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.LabelField("• Recommended only if working with legacy third-party plugins.", EditorStyles.wordWrappedMiniLabel);
            if (GUILayout.Button("Select: Legacy Input Manager (Classic)"))
            {
                ApplyInputConfiguration(InputHandlingOption.LegacyInputManager);
            }
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(15);
            if (GUILayout.Button("Re-link InputActionAsset to GameInstance Prefab"))
            {
                LinkInputActionAssetToGameInstance();
            }
        }

        public static int GetActiveInputHandler()
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset");
            if (assets != null && assets.Length > 0)
            {
                var so = new SerializedObject(assets[0]);
                var prop = so.FindProperty("activeInputHandler");
                if (prop != null)
                    return prop.intValue;
            }
            return 2; // Default to Both if not found
        }

        public static void SetActiveInputHandler(int value)
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset");
            if (assets != null && assets.Length > 0)
            {
                var so = new SerializedObject(assets[0]);
                var prop = so.FindProperty("activeInputHandler");
                if (prop != null)
                {
                    prop.intValue = value;
                    so.ApplyModifiedProperties();
                    AssetDatabase.SaveAssets();
                }
            }
        }

        public static void ApplyInputConfiguration(InputHandlingOption option)
        {
            int targetValue = (int)option;
            int currentValue = GetActiveInputHandler();

            LinkInputActionAssetToGameInstance();

            if (currentValue != targetValue)
            {
                if (EditorUtility.DisplayDialog(
                    "Switch Input Backend",
                    $"Switch active input handling to '{option}'?\n\nUnity may require a restart to apply the backend changes.",
                    "Yes, Switch",
                    "Cancel"))
                {
                    SetActiveInputHandler(targetValue);
                    EditorUtility.DisplayDialog("Input Backend Changed", $"Input handling set to {option}. If prompted by Unity, allow the editor to restart.", "OK");
                }
            }
            else
            {
                EditorUtility.DisplayDialog("Already Configured", $"Active input handling is already set to {option}.", "OK");
            }
        }

        public static void LinkInputActionAssetToGameInstance()
        {
            var inputAsset = AssetDatabase.LoadAssetAtPath<InputActionAsset>(INPUT_ACTIONS_PATH);
            if (inputAsset == null)
            {
                EditorUtility.DisplayDialog("Error", $"Could not find InputActionAsset at {INPUT_ACTIONS_PATH}", "OK");
                return;
            }

            var gameInstanceGo = AssetDatabase.LoadAssetAtPath<GameObject>(GAME_INSTANCE_PREFAB_PATH);
            if (gameInstanceGo == null)
            {
                EditorUtility.DisplayDialog("Error", $"Could not find GameInstance prefab at {GAME_INSTANCE_PREFAB_PATH}", "OK");
                return;
            }

            var inputSettingManager = gameInstanceGo.GetComponent<Insthync.CameraAndInput.InputSettingManager>();
            if (inputSettingManager == null)
            {
                EditorUtility.DisplayDialog("Error", "Could not find InputSettingManager on GameInstance prefab.", "OK");
                return;
            }

            var so = new SerializedObject(inputSettingManager);
            var prop = so.FindProperty("inputActionAsset");
            if (prop != null)
            {
                prop.objectReferenceValue = inputAsset;
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(gameInstanceGo);
                AssetDatabase.SaveAssets();
                Debug.Log("[InputSetupUtility] Successfully linked OpenMMORPG_InputActions to GameInstance.prefab!");
            }
        }
    }
}
