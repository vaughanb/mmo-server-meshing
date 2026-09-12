using UnityEngine;

namespace MultiplayerARPG
{
    [CreateAssetMenu(fileName = GameDataMenuConsts.EQUIPMENT_MODEL_BONES_SETUP_BY_BONE_NAMES_V2_MANAGER_FILE, menuName = GameDataMenuConsts.EQUIPMENT_MODEL_BONES_SETUP_BY_BONE_NAMES_V2_MANAGER_MENU, order = GameDataMenuConsts.EQUIPMENT_MODEL_BONES_SETUP_BY_BONE_NAMES_V2_MANAGER_ORDER)]
    public class EquipmentModelBonesSetupByBoneNamesV2Manager : BaseEquipmentModelBonesSetupManager
    {
        public bool changeRootBone = false;
        public bool useTheSameBonesWithTheFirstOne = false;
        public bool cacheTransformWithFullPath = false;
        public string topMostRootBoneName = "Root";

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
            EquipmentModelBonesSetupByBoneNamesV2Updater updater = instantiatedObject.GetOrAddComponent<EquipmentModelBonesSetupByBoneNamesV2Updater>(OnAddEquipmentModelBonesSetupByBoneNamesV2Updater);
            updater.SetupForObject(characterModel, instantiatedObject, equipmentContainer);
        }

        private void OnAddEquipmentModelBonesSetupByBoneNamesV2Updater(EquipmentModelBonesSetupByBoneNamesV2Updater updater)
        {
            updater.changeRootBone = changeRootBone;
            updater.useTheSameBonesWithTheFirstOne = useTheSameBonesWithTheFirstOne;
            updater.cacheTransformWithFullPath = cacheTransformWithFullPath;
            updater.topMostRootBoneName = topMostRootBoneName;
        }
    }
}