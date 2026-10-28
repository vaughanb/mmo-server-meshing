using System.Collections.Generic;
using UnityEngine;

namespace MultiplayerARPG.Demo
{
    /// <summary>
    /// Hangs an equipment item on a character in the editor the way the kit hangs it at
    /// runtime — same socket, same offsets, read from the item's own game data — so a grip
    /// can be judged and nudged without entering play mode or equipping anything.
    ///
    /// Point <see cref="item"/> at a weapon or shield and its model appears on the matching
    /// socket. Tick <see cref="overrideOffsets"/> to nudge position, rotation and scale live;
    /// the numbers you settle on are the ones to put into `DemoItemBuilder`, which is what
    /// writes `equipmentModels` for real — hand-edits to the item asset are wiped by the next
    /// `Build Items`. <see cref="LogOffsets"/> prints them ready to paste.
    ///
    /// The spawned model is marked `DontSave`, so previewing does not dirty the scene or
    /// leave stray objects behind in a build.
    ///
    /// **Both kinds of equipment work, and they are not the same mechanism.** A weapon or a
    /// shield is a rigid prop: a socket, a mesh and a transform, and the offsets above are
    /// all there is to tune. Armour is skinned — it has no meaningful transform of its own,
    /// it is placed entirely by bones, and wearing it means rebinding those bones to this
    /// character's skeleton and switching off the bare body part it covers. The offsets do
    /// nothing to a garment, and the inspector says so rather than inviting you to drag one
    /// about.
    ///
    /// **One component holds one item, so a character wearing two takes two of them.** The
    /// demo's equipment uses exactly two weapon sockets — `RightHand` for the swords, the
    /// axe and the staves, `LeftHand` for the bows and the shield — and a sword-and-board
    /// grip can only be judged with both of them on at once. Each instance owns its
    /// own spawned model and its own offsets, so they do not interfere; pointing two at the
    /// same socket is the only way to make them fight, and that is the operator's business.
    /// `DemoWeaponGripCapture` already saves every preview in the scene in one go.
    /// </summary>
    [ExecuteAlways]
    public class DemoEquipPreview : MonoBehaviour
    {
        [Tooltip("Weapon, shield or armour to show. Its own equipSocket is used, exactly as at runtime.")]
        public BaseEquipmentItem item;

        [Tooltip("Use the offsets below instead of the item's stored ones, so they can be nudged live.")]
        public bool overrideOffsets;
        public Vector3 localPosition = Vector3.zero;
        public Vector3 localEulerAngles = Vector3.zero;
        public Vector3 localScale = Vector3.one;

        [Tooltip("Which entry of the item's equipmentModels to show. Almost always 0.")]
        public int modelIndex;

        [Tooltip("Whether the model is on. Equipping re-reads the item's saved game data, " +
                 "so unequip/equip is the quick way to see whether a grip actually saved.")]
        public bool equipped = true;

        private GameObject _spawned;
        /// <summary>The bare part a worn garment is covering, to be switched back on.</summary>
        private GameObject _hidden;
        private bool _shownEquipped;

        /// <summary>The live preview object, or null when nothing is equipped.</summary>
        public Transform Spawned
        {
            get { return _spawned != null ? _spawned.transform : null; }
        }
        private BaseEquipmentItem _shownItem;
        private int _shownIndex = -1;
        private bool _shownOverride;
        private Vector3 _shownPosition, _shownEuler, _shownScale;

        /// <summary>Set by OnValidate, which cannot rebuild the preview itself.</summary>
        private bool _revalidate;

        private void OnEnable()
        {
            Refresh();
        }

        private void OnDisable()
        {
            Clear();
        }

        private void OnValidate()
        {
            // OnValidate can fire during serialisation, where destroying is illegal, so the
            // rebuild waits for the next editor tick instead.
            _revalidate = true;
        }

        private void Update()
        {
            // a drag in the scene view is an edit too, and must be taken before the
            // field comparison below decides nothing has changed
            if (CaptureDrag())
                return;

            // A tuned grip belongs to the item it was tuned against. Switching item has to
            // drop the override, or the previous weapon's offsets are pasted onto the new
            // one — and then saved over its real ones. That is how BanditAxe and
            // ApprenticeStaff once ended up with byte-identical grips, and why a weapon
            // looked like it "forgot" a saved offset: the stale override hid it.
            if (item != _shownItem || modelIndex != _shownIndex)
            {
                overrideOffsets = false;
                _revalidate = false;
                Refresh();
                return;
            }

            if (!_revalidate &&
                equipped == _shownEquipped &&
                overrideOffsets == _shownOverride &&
                localPosition == _shownPosition &&
                localEulerAngles == _shownEuler &&
                localScale == _shownScale)
                return;
            _revalidate = false;
            Refresh();
        }

        /// <summary>
        /// Notices the preview being dragged in the scene view and copies its transform into
        /// this component's serialised fields.
        ///
        /// Without this a drag is lost the moment anything rebuilds the preview — a script
        /// recompile is enough — because the spawned object is <see cref="HideFlags.DontSave"/>
        /// and never reaches the scene file. Capturing it here makes the adjustment stick, and
        /// gives "the grip currently in the scene" one unambiguous source.
        /// </summary>
        private bool CaptureDrag()
        {
            // A garment has nothing to drag: it is drawn from the skeleton, and "capturing"
            // the zeroed transform it hangs under would write a meaningless grip into an
            // armour item.
            if (_spawned == null || IsWearingSkinned)
                return false;

            Transform t = _spawned.transform;
            // rotation is compared as a rotation, not as euler numbers: Unity re-expresses
            // localEulerAngles freely (80 becomes 79.99999, 0 becomes 360) and comparing the
            // components would fire this every frame
            bool moved = (t.localPosition - _shownPosition).sqrMagnitude > 1e-10f;
            bool scaled = (t.localScale - _shownScale).sqrMagnitude > 1e-10f;
            bool turned = Quaternion.Angle(t.localRotation, Quaternion.Euler(_shownEuler)) > 0.01f;
            if (!moved && !scaled && !turned)
                return false;

            localPosition = t.localPosition;
            localEulerAngles = t.localEulerAngles;
            localScale = t.localScale;
            overrideOffsets = true;

            _shownPosition = localPosition;
            _shownEuler = localEulerAngles;
            _shownScale = localScale;
            _shownOverride = true;
#if UNITY_EDITOR
            if (!Application.isPlaying)
                UnityEditor.EditorUtility.SetDirty(this);
#endif
            return true;
        }

        /// <summary>Rebuilds the preview. Safe to call at any time.</summary>
        public void Refresh()
        {
            Clear();

            _shownItem = item;
            _shownIndex = modelIndex;
            _shownEquipped = equipped;
            _shownOverride = overrideOffsets;
            _shownPosition = localPosition;
            _shownEuler = localEulerAngles;
            _shownScale = localScale;

            if (!equipped)
                return;

            if (item == null || item.EquipmentModels == null ||
                modelIndex < 0 || modelIndex >= item.EquipmentModels.Length)
                return;

            EquipmentModel model = item.EquipmentModels[modelIndex];
            if (model == null || model.MeshPrefab == null)
                return;

            if (IsSkinned(model.MeshPrefab))
            {
                Wear(model);
                return;
            }

            Transform socket = FindSocket(model.equipSocket);
            if (socket == null)
            {
                Debug.LogWarning($"[{nameof(DemoEquipPreview)}] No equipment container named " +
                                 $"\"{model.equipSocket}\" on \"{name}\", so \"{item.name}\" cannot be shown. " +
                                 "Rebuild the character models if the sockets have changed.", this);
                return;
            }

            if (!overrideOffsets)
            {
                // show what the item actually stores, so the fields start from the real values
                localPosition = model.localPosition;
                localEulerAngles = model.localEulerAngles;
                localScale = model.doNotChangeScale ? Vector3.one : model.localScale;
                _shownPosition = localPosition;
                _shownEuler = localEulerAngles;
                _shownScale = localScale;
            }

            _spawned = Instantiate(model.MeshPrefab, socket);
            _spawned.name = $"~Preview_{item.name}";
            _spawned.hideFlags = HideFlags.DontSave;
            _spawned.transform.localPosition = localPosition;
            _spawned.transform.localEulerAngles = localEulerAngles;
            _spawned.transform.localScale = localScale;
        }

        /// <summary>Whether what is currently shown is a garment rather than a prop.</summary>
        public bool IsWearingSkinned { get; private set; }

        /// <summary>Armour and clothing are skinned; weapons and shields are not.</summary>
        public static bool IsSkinned(GameObject meshPrefab)
        {
            return meshPrefab != null && meshPrefab.GetComponentInChildren<SkinnedMeshRenderer>(true) != null;
        }

        /// <summary>
        /// Puts a garment on the way `EquipmentModelBonesSetupByBoneNamesManager` does at
        /// runtime: match every bone the garment was skinned to against a bone of the same
        /// name on this character, then switch off the bare part it covers.
        ///
        /// The kit's own manager cannot be borrowed for it — it reads
        /// `GameInstance.Singleton` and the container's runtime bone cache, neither of which
        /// exists in the editor — so the mapping is done here off the same source that
        /// manager uses, the model's own skinned mesh renderer.
        ///
        /// The garment is parented to the model root rather than to a socket, which is not
        /// cosmetic: a skinned mesh is drawn entirely from its bones, and all its parent
        /// decides is what switching that parent off would hide. It is also what
        /// `DemoCharacterBuilder.GraftModel` does for the outfits baked into the NPCs.
        /// </summary>
        private void Wear(EquipmentModel model)
        {
            BaseCharacterModel character = GetComponentInChildren<BaseCharacterModel>(true);
            SkinnedMeshRenderer reference = Reference(character);
            if (reference == null)
            {
                Debug.LogWarning($"[{nameof(DemoEquipPreview)}] \"{name}\" has no skinned mesh to take a " +
                                 $"skeleton from, so \"{item.name}\" cannot be fitted.", this);
                return;
            }

            var skeleton = new Dictionary<string, Transform>();
            foreach (Transform bone in reference.bones)
            {
                if (bone != null)
                    skeleton[bone.name] = bone;
            }

            _spawned = Instantiate(model.MeshPrefab, character.transform);
            _spawned.name = $"~Preview_{item.name}";
            _spawned.hideFlags = HideFlags.DontSave;
            IsWearingSkinned = true;
            // the garment's own transform means nothing once it is on a skeleton, but an
            // inherited offset or scale would still be applied on top of the skinning
            _spawned.transform.localPosition = Vector3.zero;
            _spawned.transform.localRotation = Quaternion.identity;
            _spawned.transform.localScale = Vector3.one;

            foreach (SkinnedMeshRenderer renderer in _spawned.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                Rebind(renderer, skeleton, reference.rootBone);
                // These bodies are cut from a T-pose, so a garment's bind bounds are a thin
                // slab up at shoulder height; animate an arm down and a close camera culls
                // the whole renderer. DemoCharacterBuilder.WidenBounds does this to the body
                // parts for the same reason, and borrowing the body's bounds keeps the
                // garment in step with whatever figure that used.
                renderer.localBounds = reference.localBounds;
            }

            Hide(character, model.equipSocket);
        }

        /// <summary>
        /// The renderer whose bone list is this character's skeleton. The model names one,
        /// and it is the same one the kit's bones setup would use; anything else skinned will
        /// do as a fallback, since every part of these characters shares one skeleton.
        /// </summary>
        private SkinnedMeshRenderer Reference(BaseCharacterModel character)
        {
            var withSkin = character as IModelWithSkinnedMeshRenderer;
            if (withSkin != null && withSkin.SkinnedMeshRenderer != null &&
                withSkin.SkinnedMeshRenderer.bones != null && withSkin.SkinnedMeshRenderer.bones.Length > 0)
                return withSkin.SkinnedMeshRenderer;

            foreach (SkinnedMeshRenderer candidate in GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (candidate.bones != null && candidate.bones.Length > 0 &&
                    !candidate.name.StartsWith("~Preview_"))
                    return candidate;
            }
            return null;
        }

        private void Rebind(SkinnedMeshRenderer renderer, Dictionary<string, Transform> skeleton, Transform rootBone)
        {
            Transform[] rebound = renderer.bones;
            for (int i = 0; i < rebound.Length; ++i)
            {
                if (rebound[i] == null)
                    continue;
                Transform match;
                if (skeleton.TryGetValue(rebound[i].name, out match))
                    rebound[i] = match;
                else
                    Debug.LogWarning($"[{nameof(DemoEquipPreview)}] \"{item.name}\" is skinned to a bone named " +
                                     $"\"{rebound[i].name}\", which \"{name}\" has not got — the garment will be " +
                                     "torn wherever that bone holds it.", this);
            }
            renderer.bones = rebound;
            if (rootBone != null)
                renderer.rootBone = rootBone;
        }

        /// <summary>
        /// Switches off whatever the container for this socket says the garment replaces —
        /// the bare chest under a tunic, the hair under a hood. `EquipmentContainer` already
        /// holds that decision as its `defaultModel`, and this is the same thing the kit does
        /// with it.
        /// </summary>
        private void Hide(BaseCharacterModel character, string equipSocket)
        {
            if (character == null || character.EquipmentContainers == null)
                return;
            foreach (EquipmentContainer container in character.EquipmentContainers)
            {
                if (container.equipSocket != equipSocket || container.defaultModel == null)
                    continue;
                _hidden = container.defaultModel;
                _hidden.SetActive(false);
                return;
            }
        }

        private void Clear()
        {
            if (_hidden != null)
            {
                _hidden.SetActive(true);
                _hidden = null;
            }
            IsWearingSkinned = false;
            if (_spawned == null)
                return;
            if (Application.isPlaying)
                Destroy(_spawned);
            else
                DestroyImmediate(_spawned);
            _spawned = null;
        }

        /// <summary>
        /// The socket a rigid prop hangs on. Read off the model's own `EquipmentContainers`,
        /// which is what the kit looks it up in, with a search by object name as the fallback
        /// for a character whose containers have not been built.
        /// </summary>
        private Transform FindSocket(string equipSocket)
        {
            if (string.IsNullOrEmpty(equipSocket))
                return null;

            BaseCharacterModel character = GetComponentInChildren<BaseCharacterModel>(true);
            if (character == null)
                return null;

            if (character.EquipmentContainers != null)
            {
                foreach (EquipmentContainer container in character.EquipmentContainers)
                {
                    if (container.equipSocket == equipSocket && container.transform != null)
                        return container.transform;
                }
            }

            foreach (Transform candidate in GetComponentsInChildren<Transform>(true))
            {
                if (candidate.name == equipSocket)
                    return candidate;
            }
            return null;
        }

        /// <summary>
        /// Puts the model on, reading the item's **saved** game data rather than any tuning
        /// still held on this component. Unequip followed by Equip is therefore a direct
        /// answer to "did that grip actually save?" — whatever comes back is what the item
        /// stores.
        ///
        /// Unsaved offsets are dropped, which is the point: they are exactly what would be
        /// masking the stored values.
        /// </summary>
        [ContextMenu("Equip (reload from saved game data)")]
        public void Equip()
        {
            equipped = true;
            overrideOffsets = false;
            Refresh();
        }

        /// <summary>Takes the model off. The offsets on this component are left untouched.</summary>
        [ContextMenu("Unequip")]
        public void Unequip()
        {
            equipped = false;
            Refresh();
        }

#if UNITY_EDITOR
        /// <summary>
        /// Writes the grip currently shown in the scene into the weapon's game data.
        ///
        /// The values are taken from the live preview object, so whatever was dragged in the
        /// scene view is what gets saved. They go two places: the item asset, so the change
        /// is live immediately, and <see cref="DemoWeaponGripOverrides"/>, so it survives the
        /// next `Build Items` — which rewrites every item's `equipmentModels` from scratch and
        /// would otherwise wipe it.
        ///
        /// Saving replaces the builder's default orientation for that item outright, so the
        /// captured transform must be the whole grip, not a delta.
        /// </summary>
        [ContextMenu("Save grip to weapon game data")]
        public void SaveGripToGameData()
        {
            if (item == null)
            {
                Debug.LogWarning($"[{nameof(DemoEquipPreview)}] No item on \"{name}\", nothing to save.", this);
                return;
            }
            if (item.EquipmentModels == null || modelIndex < 0 || modelIndex >= item.EquipmentModels.Length)
            {
                Debug.LogWarning($"[{nameof(DemoEquipPreview)}] \"{item.name}\" has no equipment model " +
                                 $"at index {modelIndex}, nothing to save.", this);
                return;
            }
            if (IsSkinned(item.EquipmentModels[modelIndex].MeshPrefab))
            {
                Debug.LogWarning($"[{nameof(DemoEquipPreview)}] \"{item.name}\" is skinned. Armour is placed by " +
                                 "its bones, so it has no grip to save; writing one would only put a number in " +
                                 "the item that nothing reads.", this);
                return;
            }

            // the live object is the truth - it carries any scene-view drag
            Vector3 position = localPosition;
            Vector3 euler = localEulerAngles;
            Vector3 scale = localScale;
            if (_spawned != null)
            {
                position = _spawned.transform.localPosition;
                euler = _spawned.transform.localEulerAngles;
                scale = _spawned.transform.localScale;
            }

            DemoWeaponGripOverrides overrides = LoadOrCreateOverrides();
            overrides.Set(item.name, position, euler, scale);
            UnityEditor.EditorUtility.SetDirty(overrides);

            // apply to the item too, so the grip is right without waiting for a rebuild
            var serialized = new UnityEditor.SerializedObject(item);
            string root = $"equipmentModels.Array.data[{modelIndex}]";
            serialized.FindProperty(root + ".localPosition").vector3Value = position;
            serialized.FindProperty(root + ".localEulerAngles").vector3Value = euler;
            serialized.FindProperty(root + ".localScale").vector3Value = scale;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            UnityEditor.EditorUtility.SetDirty(item);
            UnityEditor.AssetDatabase.SaveAssets();

            Debug.Log($"[{nameof(DemoEquipPreview)}] Saved grip for \"{item.name}\"\n" +
                      $"    localPosition    = {position.ToString("F4")}\n" +
                      $"    localEulerAngles = {euler.ToString("F3")}\n" +
                      $"    localScale       = {scale.ToString("F4")}\n" +
                      $"    -> {item.name}.asset and {DemoWeaponGripOverrides.AssetPath}", item);
        }

        private static DemoWeaponGripOverrides LoadOrCreateOverrides()
        {
            var existing = UnityEditor.AssetDatabase.LoadAssetAtPath<DemoWeaponGripOverrides>(
                DemoWeaponGripOverrides.AssetPath);
            if (existing != null)
                return existing;
            var created = ScriptableObject.CreateInstance<DemoWeaponGripOverrides>();
            UnityEditor.AssetDatabase.CreateAsset(created, DemoWeaponGripOverrides.AssetPath);
            Debug.Log($"[{nameof(DemoEquipPreview)}] Created {DemoWeaponGripOverrides.AssetPath}.");
            return created;
        }
#endif

        /// <summary>
        /// Prints the current offsets in the shape `DemoItemBuilder` wants, so a value tuned
        /// here can be moved somewhere it survives a rebuild.
        /// </summary>
        [ContextMenu("Log offsets for DemoItemBuilder")]
        public void LogOffsets()
        {
            Debug.Log($"[{nameof(DemoEquipPreview)}] {(item != null ? item.name : "(no item)")}\n" +
                      $"    localPosition   = new Vector3({localPosition.x:0.####}f, {localPosition.y:0.####}f, {localPosition.z:0.####}f)\n" +
                      $"    localEulerAngles= new Vector3({localEulerAngles.x:0.####}f, {localEulerAngles.y:0.####}f, {localEulerAngles.z:0.####}f)\n" +
                      $"    localScale      = new Vector3({localScale.x:0.####}f, {localScale.y:0.####}f, {localScale.z:0.####}f)", this);
        }
    }
}
