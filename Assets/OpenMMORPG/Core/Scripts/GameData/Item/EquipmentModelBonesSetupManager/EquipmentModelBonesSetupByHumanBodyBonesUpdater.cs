using System.Collections.Generic;
using UnityEngine;

namespace MultiplayerARPG
{
    [DefaultExecutionOrder(int.MaxValue - 1)]
    public class EquipmentModelBonesSetupByHumanBodyBonesUpdater : MonoBehaviour
    {
        [System.Serializable]
        public struct PredefinedBone
        {
            public HumanBodyBones boneType;
            public Transform boneTransform;
        }

        public PredefinedBone[] predefinedBones = new PredefinedBone[0];

        private Dictionary<HumanBodyBones, Transform> _predefinedBonesDict;
        public Dictionary<HumanBodyBones, Transform> PredefinedBonesDict
        {
            get
            {
                if (_predefinedBonesDict == null)
                {
                    _predefinedBonesDict = new Dictionary<HumanBodyBones, Transform>();
                    for (int i = 0; i < predefinedBones.Length; ++i)
                    {
                        _predefinedBonesDict.Add(predefinedBones[i].boneType, predefinedBones[i].boneTransform);
                    }
                }
                return _predefinedBonesDict;
            }
        }

        public void SetupForObject(BaseCharacterModel characterModel, GameObject instantiatedObject, EquipmentContainer equipmentContainer)
        {
            if (equipmentContainer != null && equipmentContainer.CachedDefaultModelAnimator != null)
            {
                PrepareTransforms(equipmentContainer.CachedDefaultModelAnimator, instantiatedObject.GetComponentInChildren<Animator>());
            }
            else
            {
                if (!(characterModel is IModelWithAnimator animatorSrc))
                {
                    Debug.LogWarning($"[{nameof(EquipmentModelBonesSetupByBoneNamesManager)}] Cannot setup bones for \"{instantiatedObject}\", character model \"{characterModel}\" is not a model with animator");
                    return;
                }
                PrepareTransforms(animatorSrc.Animator, instantiatedObject.GetComponentInChildren<Animator>());
            }
        }

        public void PrepareTransforms(Animator src, Animator dst)
        {
#if !UNITY_SERVER
            if (src == null || dst == null)
                return;

            if (dst.avatar == null || !dst.avatar.isHuman)
                return;

            AnimatorHandle srcAnimatorHandle = src.gameObject.GetOrAddComponent<AnimatorHandle>();
            AnimatorHandle dstAnimatorHandle = dst.gameObject.GetOrAddComponent<AnimatorHandle>();

            int length = (int)HumanBodyBones.LastBone;
            Transform[] srcTransforms = new Transform[length];
            Transform[] dstTransforms = new Transform[length];
            for (int i = 0; i < length; ++i)
            {
                HumanBodyBones bone = (HumanBodyBones)i;
                // Add all bones althrough it is null
                // Priority: predefined bones > bones from src animator
                Transform srcTransform;
                if (!PredefinedBonesDict.TryGetValue(bone, out srcTransform))
                    srcTransform = src.GetBoneTransform(bone);
                srcTransforms[i] = srcTransform;

                // Priority: predefined bones > bones from dst animator
                Transform dstTransform;
                dstTransform = dst.GetBoneTransform(bone);
                dstTransforms[i] = dstTransform;
            }

            EquipmentModelBonesSetupByHumanBodyBonesUpdateManager.Instance.Register(srcAnimatorHandle, srcTransforms, dstAnimatorHandle, dstTransforms);
#endif
        }
    }
}
