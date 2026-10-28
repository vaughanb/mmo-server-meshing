using UnityEditor;
using UnityEngine;

namespace MultiplayerARPG.Demo.EditorTools
{
    /// <summary>
    /// Buttons for <see cref="DemoEquipPreview"/>, plus the thing the buttons are usually
    /// being pressed to find out: whether what is on the character right now matches what the
    /// item actually stores.
    ///
    /// Unequip/Equip answers that by reloading from the item, but the readout answers it
    /// without touching anything — and, unlike the reload, it says which of position, rotation
    /// or scale differs and by how much.
    /// </summary>
    [CustomEditor(typeof(DemoEquipPreview))]
    public class DemoEquipPreviewEditor : Editor
    {
        /// <summary>So the readout keeps up while a weapon is being dragged in the scene view.</summary>
        public override bool RequiresConstantRepaint()
        {
            return true;
        }

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var preview = (DemoEquipPreview)target;

            EditorGUILayout.Space();
            DrawSavedState(preview);

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Equip", GUILayout.Height(24f)))
                    preview.Equip();
                if (GUILayout.Button("Unequip", GUILayout.Height(24f)))
                    preview.Unequip();
            }

            using (new EditorGUI.DisabledScope(preview.item == null || preview.IsWearingSkinned))
            {
                if (GUILayout.Button("Save grip to weapon game data", GUILayout.Height(24f)))
                    preview.SaveGripToGameData();
            }

            EditorGUILayout.HelpBox(
                "Equip reloads from the item's saved game data and drops any unsaved offsets, " +
                "so unequip/equip shows you what is really stored.\n\n" +
                "Saving writes both the item asset and WeaponGripOverrides.asset — the second is " +
                "what survives Build Items.",
                MessageType.None);
        }

        private static void DrawSavedState(DemoEquipPreview preview)
        {
            if (preview.item == null)
            {
                EditorGUILayout.HelpBox("No item assigned.", MessageType.None);
                return;
            }

            EquipmentModel[] models = preview.item.EquipmentModels;
            if (models == null || preview.modelIndex < 0 || preview.modelIndex >= models.Length)
            {
                EditorGUILayout.HelpBox($"\"{preview.item.name}\" has no equipment model at index " +
                                        $"{preview.modelIndex}.", MessageType.Warning);
                return;
            }

            if (DemoEquipPreview.IsSkinned(models[preview.modelIndex].MeshPrefab))
            {
                // A garment has no stored grip to compare against, so the readout below would
                // be measuring the distance from one meaningless number to another.
                EditorGUILayout.HelpBox(
                    $"\"{preview.item.name}\" is armour: skinned to the \"{models[preview.modelIndex].equipSocket}\" " +
                    "slot and placed by its bones. The offsets above do nothing to it, and there is no grip to save.",
                    MessageType.Info);
                return;
            }

            Transform live = preview.Spawned;
            if (live == null)
            {
                EditorGUILayout.HelpBox("Unequipped. Press Equip to load the saved grip.", MessageType.None);
                return;
            }

            EquipmentModel model = models[preview.modelIndex];
            Vector3 storedScale = model.doNotChangeScale ? Vector3.one : model.localScale;

            float positionError = Vector3.Distance(live.localPosition, model.localPosition);
            float rotationError = Quaternion.Angle(live.localRotation, Quaternion.Euler(model.localEulerAngles));
            float scaleError = Vector3.Distance(live.localScale, storedScale);

            bool matches = positionError < 0.0001f && rotationError < 0.05f && scaleError < 0.0001f;
            if (matches)
            {
                EditorGUILayout.HelpBox($"Matches the saved game data for \"{preview.item.name}\".",
                                        MessageType.Info);
                return;
            }

            var differences = "";
            if (positionError >= 0.0001f)
                differences += $"\n    position by {positionError * 100f:0.##} cm";
            if (rotationError >= 0.05f)
                differences += $"\n    rotation by {rotationError:0.#} degrees";
            if (scaleError >= 0.0001f)
                differences += $"\n    scale by {scaleError:0.###}";
            EditorGUILayout.HelpBox($"Unsaved — differs from what \"{preview.item.name}\" stores:{differences}",
                                    MessageType.Warning);
        }
    }
}
