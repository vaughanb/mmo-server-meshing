using System.Collections.Generic;
using UnityEngine;

namespace MultiplayerARPG
{
    [CreateAssetMenu(fileName = GameDataMenuConsts.EQUIPMENT_MODEL_BONES_SETUP_BY_BONE_NAMES_MANAGER_FILE, menuName = GameDataMenuConsts.EQUIPMENT_MODEL_BONES_SETUP_BY_BONE_NAMES_MANAGER_MENU, order = GameDataMenuConsts.EQUIPMENT_MODEL_BONES_SETUP_BY_BONE_NAMES_MANAGER_ORDER)]
    public class EquipmentModelBonesSetupByBoneNamesManager : BaseEquipmentModelBonesSetupManager
    {
        public bool changeRootBone = false;

        public override void Setup(BaseCharacterModel characterModel, EquipmentModel equipmentModel, GameObject instantiatedObject, BaseEquipmentEntity instantiatedEntity, EquipmentInstantiatedObjectGroup instantiatedObjectGroup, EquipmentContainer equipmentContainer)
        {
            if (GameInstance.Singleton.DimensionType != DimensionType.Dimension3D)
                return;

            SetupForObject(characterModel, instantiatedObject, equipmentContainer);
            if (instantiatedObjectGroup != null && instantiatedObjectGroup.instantiatedObjects != null)
            {
                foreach (GameObject obj in instantiatedObjectGroup.instantiatedObjects)
                {
                    SetupForObject(characterModel, obj, equipmentContainer);
                }
            }
        }

        private void SetupForObject(BaseCharacterModel characterModel, GameObject instantiatedObject, EquipmentContainer equipmentContainer)
        {
            if (instantiatedObject == null)
                return;

            if (!(characterModel is IModelWithSkinnedMeshRenderer skinnedMeshSrc))
            {
                Debug.LogWarning($"[{nameof(EquipmentModelBonesSetupByBoneNamesManager)}] Cannot setup bones for \"{instantiatedObject}\", character model \"{characterModel}\" is not a model with skinned mesh");
                return;
            }

            SkinnedMeshRenderer defaultSkinnedMesh = equipmentContainer.CachedDefaultModelSkinnedMesh;
            SkinnedMeshRenderer skinnedMeshRenderer = skinnedMeshSrc.SkinnedMeshRenderer;
            if (defaultSkinnedMesh == null && skinnedMeshRenderer == null)
            {
                Debug.LogWarning($"[{nameof(EquipmentModelBonesSetupByBoneNamesManager)}] Cannot setup bones for \"{instantiatedObject}\", character model \"{characterModel}\" and equipment container has no skinned mesh renderer");
                return;
            }

            // Prepare bone maps by get bones from skinned mesh renderer
            Dictionary<string, Transform> bonesMap = new Dictionary<string, Transform>();
            Transform rootBone = null;
            if (defaultSkinnedMesh != null)
            {
                StoreToBoneMap(defaultSkinnedMesh, bonesMap);
                rootBone = defaultSkinnedMesh.rootBone;
            }
            else if (skinnedMeshRenderer != null)
            {
                StoreToBoneMap(skinnedMeshRenderer, bonesMap);
                rootBone = skinnedMeshRenderer.rootBone;
            }
            SkinnedMeshRenderer[] skinnedMeshes = instantiatedObject.GetComponentsInChildren<SkinnedMeshRenderer>();
            for (int i = 0; i < skinnedMeshes.Length; ++i)
            {
                SkinnedMeshRenderer skinnedMesh = skinnedMeshes[i];
                if (skinnedMesh == null)
                    continue;
                // Set new model bones by using default model bones or character model bones
                Transform[] newBones = new Transform[skinnedMesh.bones.Length];
                for (int j = 0; j < skinnedMesh.bones.Length; ++j)
                {
                    Transform newBone = skinnedMesh.bones[j];
                    if (bonesMap.TryGetValue(newBone.name, out newBones[j]))
                    {
                        // Can find a bone with the same name, use it
                        continue;
                    }
                    // Really cannot find the bone, show error message
                    Debug.LogWarning($"[{nameof(EquipmentModelBonesSetupByBoneNamesManager)}] {instantiatedObject} unable to find mapped bone for \"{newBone}\"");
                }
                skinnedMesh.bones = newBones;
                if (changeRootBone && rootBone != null)
                    skinnedMesh.rootBone = rootBone;
            }
        }

        private void StoreToBoneMap(SkinnedMeshRenderer renderer, Dictionary<string, Transform> map)
        {
            if (renderer == null)
                return;

            foreach (Transform bone in renderer.rootBone.GetComponentsInChildren<Transform>())
            {
                map[bone.name] = bone;
            }
        }
    }
}