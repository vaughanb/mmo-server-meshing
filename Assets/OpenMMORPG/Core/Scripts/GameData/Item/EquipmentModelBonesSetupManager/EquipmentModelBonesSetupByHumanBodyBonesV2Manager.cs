using UnityEngine;

namespace MultiplayerARPG
{
    [CreateAssetMenu(fileName = GameDataMenuConsts.EQUIPMENT_MODEL_BONES_SETUP_BY_HUMAN_BODY_BONES_V2_MANAGER_FILE, menuName = GameDataMenuConsts.EQUIPMENT_MODEL_BONES_SETUP_BY_HUMAN_BODY_BONES_V2_MANAGER_MENU, order = GameDataMenuConsts.EQUIPMENT_MODEL_BONES_SETUP_BY_HUMAN_BODY_BONES_V2_MANAGER_ORDER)]
    public class EquipmentModelBonesSetupByHumanBodyBonesV2Manager : BaseEquipmentModelBonesSetupManager
    {
        public bool changeRootBone = false;
        public bool useTheSameBonesWithTheFirstOne = false;

        public override void Setup(BaseCharacterModel characterModel, EquipmentModel equipmentModel, GameObject instantiatedObject, BaseEquipmentEntity instantiatedEntity, EquipmentInstantiatedObjectGroup instantiatedObjectGroup, EquipmentContainer equipmentContainer)
        {
            if (GameInstance.Singleton.DimensionType != DimensionType.Dimension3D)
                return;

            SetupForObject(characterModel, instantiatedObject, equipmentContainer);

            if (instantiatedObjectGroup?.instantiatedObjects == null)
                return;

            foreach (GameObject obj in instantiatedObjectGroup.instantiatedObjects)
            {
                SetupForObject(characterModel, obj, equipmentContainer);
            }
        }

        private void SetupForObject(
            BaseCharacterModel characterModel,
            GameObject instantiatedObject,
            EquipmentContainer equipmentContainer)
        {
            if (instantiatedObject == null)
                return;

            if (!(characterModel is IModelWithAnimator animatorSrc))
            {
                Debug.LogWarning($"[{nameof(EquipmentModelBonesSetupByHumanBodyBonesV2Manager)}] Character model \"{characterModel}\" has no skinned mesh renderer.");
                return;
            }

            if (animatorSrc.Animator == null || animatorSrc.Animator.avatar == null || animatorSrc.Animator.avatarRoot == null)
            {
                Debug.LogWarning($"[{nameof(EquipmentModelBonesSetupByHumanBodyBonesV2Manager)}] Character model \"{characterModel}\" has no animator or avatar.");
                return;
            }

            Animator instantiatedAnimator = instantiatedObject.GetComponentInChildren<Animator>();
            if (instantiatedAnimator == null || instantiatedAnimator.avatar == null || !instantiatedAnimator.avatar.isHuman)
                return;

            SkinnedMeshRenderer[] instantiatedMeshes = instantiatedObject.GetComponentsInChildren<SkinnedMeshRenderer>();
            Transform[] bones = null;
            Transform rootBone = null;
            for (int i = 0; i < instantiatedMeshes.Length; ++i)
            {
                SkinnedMeshRenderer instantiatedMesh = instantiatedMeshes[i];
                if (instantiatedMesh == null)
                    continue;
                Transform[] tempBones = instantiatedMesh.bones;
                Transform tempRootBone = instantiatedMesh.rootBone;

                if (useTheSameBonesWithTheFirstOne && bones != null)
                {
                    tempBones = bones;
                }
                else
                {
                    for (int j = 0; j < tempBones.Length; ++j)
                    {
                        Transform bone = tempBones[j];
                        if (bone == null)
                            continue;
                        for (HumanBodyBones k = 0; k < HumanBodyBones.LastBone; ++k)
                        {
                            if (bone == instantiatedAnimator.GetBoneTransform(k))
                            {
                                Transform tempBone = animatorSrc.Animator.GetBoneTransform(k);
                                if (tempBone == null)
                                    continue;
                                tempBones[j] = tempBone;
                                break;
                            }
                        }
                    }
                    // Set first bones if it's null, so we can use the same bones for other meshes
                    if (bones == null)
                        bones = tempBones;
                }
                if (useTheSameBonesWithTheFirstOne && rootBone != null)
                {
                    tempRootBone = rootBone;
                }
                else
                {
                    if (changeRootBone && instantiatedMesh.rootBone != null)
                    {
                        for (HumanBodyBones k = 0; k < HumanBodyBones.LastBone; ++k)
                        {
                            if (instantiatedMesh.rootBone == instantiatedAnimator.GetBoneTransform(k))
                            { 
                                Transform tempBone = animatorSrc.Animator.GetBoneTransform(k);
                                if (tempBone == null)
                                    continue;
                                tempRootBone = tempBone;
                                break;
                            }
                        }
                    }
                    // Set first root bone if it's null, so we can use the same root bone for other meshes
                    if (rootBone == null)
                        rootBone = tempRootBone;
                }
                instantiatedMesh.bones = tempBones;
                instantiatedMesh.rootBone = tempRootBone;
            }
        }
    }
}
