using UnityEngine;

namespace MultiplayerARPG
{
    public class ShooterRecoilUpdater : MonoBehaviour
    {
        public ShooterPlayerCharacterController Controller { get; set; }
        public BasePlayerCharacterEntity PlayingCharacterEntity => Controller.PlayingCharacterEntity;
        public CrosshairSetting CurrentCrosshairSetting => PlayingCharacterEntity.GetCrosshairSetting();
        [Min(0.01f)]
        public float recoilRateWhileSwimming = 1.5f;
        [Min(0.01f)]
        public float recoilRateWhileSprinting = 2f;
        [Min(0.01f)]
        public float recoilRateWhileWalking = 0.5f;
        [Min(0.01f)]
        public float recoilRateWhileMoving = 1f;
        [Min(0.01f)]
        public float recoilRateWhileCrouching = 0.5f;
        [Min(0.01f)]
        public float recoilRateWhileCrawling = 0.5f;
        [Header("Recoil scale while TPS view mode")]
        [Min(0f)]
        public float recoilPitchScale = 1f;
        [Min(0f)]
        public float recoilYawScale = 1f;
        [Min(0f)]
        public float recoilRollScale = 1f;
        [Header("Recoil scale while FPS view mode")]
        [Min(0f)]
        public float recoilPitchScaleWhileFpsView = 1f;
        [Min(0f)]
        public float recoilYawScaleWhileFpsView = 1f;
        [Min(0f)]
        public float recoilRollScaleWhileFpsView = 1f;
        [Header("Recoil scale while shoulder view mode")]
        [Min(0f)]
        public float recoilPitchScaleWhileShoulderView = 1f;
        [Min(0f)]
        public float recoilYawScaleWhileShoulderView = 1f;
        [Min(0f)]
        public float recoilRollScaleWhileShoulderView = 1f;
        [Header("Recoil scale while aiming")]
        [Min(0f)]
        public float recoilPitchScaleWhileAiming = 1f;
        [Min(0f)]
        public float recoilYawScaleWhileAiming = 1f;
        [Min(0f)]
        public float recoilRollScaleWhileAiming = 1f;
        [Header("Additional recoil while TPS view mode (increasing directly)")]
        [Min(0f)]
        public float additioanlPitch = 0f;
        [Min(0f)]
        public float additioanlYaw = 0f;
        [Min(0f)]
        public float additioanlRoll = 0f;
        [Header("Additional recoil while FPS view mode (increasing directly)")]
        [Min(0f)]
        public float additioanlPitchWhileFpsView = 0f;
        [Min(0f)]
        public float additioanlYawWhileFpsView = 0f;
        [Min(0f)]
        public float additioanlRollWhileFpsView = 0f;
        [Header("Additional recoil while shoulder view mode (increasing directly)")]
        [Min(0f)]
        public float additioanlPitchWhileShoulderView = 0f;
        [Min(0f)]
        public float additioanlYawWhileShoulderView = 0f;
        [Min(0f)]
        public float additioanlRollWhileShoulderView = 0f;
        [Header("Additional recoil while aiming (increasing directly)")]
        [Min(0f)]
        public float additioanlPitchWhileAiming = 0f;
        [Min(0f)]
        public float additioanlYawWhileAiming = 0f;
        [Min(0f)]
        public float additioanlRollWhileAiming = 0f;

        public virtual void Trigger(
            bool isLeftHand,
            CharacterItem weapon,
            int simulateSeed,
            byte triggerIndex,
            byte spreadIndex,
            BaseSkill skill,
            int skillLevel)
        {
            IWeaponItem weaponItem = weapon.GetWeaponItem();
            if (weaponItem == null)
                return;

            CharacterDataCache cachedData = PlayingCharacterEntity.GetCaches();

            float recoilPitch;
            float recoilYaw;
            float recoilRoll;

            float weaponRecoilPitch;
            float weaponRecoilYaw;
            float weaponRecoilRoll;

            weaponRecoilPitch = weaponItem.Recoil;

            weaponRecoilYaw = weaponItem.Recoil;
            if (weaponItem.RecoilYaw > 0f)
                weaponRecoilYaw = weaponItem.RecoilYaw;

            weaponRecoilRoll = weaponItem.Recoil;
            if (weaponItem.RecoilRoll > 0f)
                weaponRecoilRoll = weaponItem.RecoilRoll;

            if (Controller.IsZoomAimming)
            {
                if (weaponItem.RecoilWhileAiming > 0f)
                    weaponRecoilPitch = weaponRecoilYaw = weaponRecoilRoll = weaponItem.RecoilWhileAiming;
                if (weaponItem.RecoilYawWhileAiming > 0f)
                    weaponRecoilYaw = weaponItem.RecoilYawWhileAiming;
                if (weaponItem.RecoilRollWhileAiming > 0f)
                    weaponRecoilRoll = weaponItem.RecoilRollWhileAiming;
            }
            else
            {
                switch (Controller.ActiveViewMode)
                {
                    case ShooterControllerViewMode.Fps:
                        if (weaponItem.RecoilWhileFpsViewMode > 0f)
                            weaponRecoilPitch = weaponRecoilYaw = weaponRecoilRoll = weaponItem.RecoilWhileFpsViewMode;
                        if (weaponItem.RecoilYawWhileFpsViewMode > 0f)
                            weaponRecoilYaw = weaponItem.RecoilYawWhileFpsViewMode;
                        if (weaponItem.RecoilRollWhileFpsViewMode > 0f)
                            weaponRecoilRoll = weaponItem.RecoilRollWhileFpsViewMode;
                        break;
                    case ShooterControllerViewMode.Shoulder:
                        if (weaponItem.RecoilWhileShoulderViewMode > 0f)
                            weaponRecoilPitch = weaponRecoilYaw = weaponRecoilRoll = weaponItem.RecoilWhileShoulderViewMode;
                        if (weaponItem.RecoilYawWhileShoulderViewMode > 0f)
                            weaponRecoilYaw = weaponItem.RecoilYawWhileShoulderViewMode;
                        if (weaponItem.RecoilRollWhileShoulderViewMode > 0f)
                            weaponRecoilRoll = weaponItem.RecoilRollWhileShoulderViewMode;
                        break;
                }
            }

            float modifierPitch;
            float modifierYaw;
            float modifierRoll;

            modifierPitch = cachedData.RecoilModifier;

            modifierYaw = cachedData.RecoilModifier;
            if (cachedData.RecoilYawModifier != 0f)
                modifierYaw = cachedData.RecoilYawModifier;

            modifierRoll = cachedData.RecoilModifier;
            if (cachedData.RecoilRollModifier != 0f)
                modifierRoll = cachedData.RecoilRollModifier;

            float recoilPitchWithModifier = weaponRecoilPitch + modifierPitch;
            float recoilYawWithModifier = weaponRecoilYaw + modifierYaw;
            float recoilRollWithModifier = weaponRecoilRoll + modifierRoll;

            if (PlayingCharacterEntity.MovementState.Has(MovementState.Forward) ||
                PlayingCharacterEntity.MovementState.Has(MovementState.Backward) ||
                PlayingCharacterEntity.MovementState.Has(MovementState.Left) ||
                PlayingCharacterEntity.MovementState.Has(MovementState.Right))
            {
                if (PlayingCharacterEntity.MovementState.Has(MovementState.IsUnderWater))
                {
                    recoilPitch = recoilPitchWithModifier * recoilRateWhileSwimming;
                    recoilYaw = recoilYawWithModifier * recoilRateWhileSwimming;
                    recoilRoll = recoilRollWithModifier * recoilRateWhileSwimming;
                }
                else if (PlayingCharacterEntity.ExtraMovementState == ExtraMovementState.IsSprinting)
                {
                    recoilPitch = recoilPitchWithModifier * recoilRateWhileSprinting;
                    recoilYaw = recoilYawWithModifier * recoilRateWhileSprinting;
                    recoilRoll = recoilRollWithModifier * recoilRateWhileSprinting;
                }
                else if (PlayingCharacterEntity.ExtraMovementState == ExtraMovementState.IsWalking)
                {
                    recoilPitch = recoilPitchWithModifier * recoilRateWhileWalking;
                    recoilYaw = recoilYawWithModifier * recoilRateWhileWalking;
                    recoilRoll = recoilRollWithModifier * recoilRateWhileWalking;
                }
                else
                {
                    recoilPitch = recoilPitchWithModifier * recoilRateWhileMoving;
                    recoilYaw = recoilYawWithModifier * recoilRateWhileMoving;
                    recoilRoll = recoilRollWithModifier * recoilRateWhileMoving;
                }
            }
            else if (PlayingCharacterEntity.ExtraMovementState == ExtraMovementState.IsCrouching)
            {
                recoilPitch = recoilPitchWithModifier * recoilRateWhileCrouching;
                recoilYaw = recoilYawWithModifier * recoilRateWhileCrouching;
                recoilRoll = recoilRollWithModifier * recoilRateWhileCrouching;
            }
            else if (PlayingCharacterEntity.ExtraMovementState == ExtraMovementState.IsCrawling)
            {
                recoilPitch = recoilPitchWithModifier * recoilRateWhileCrawling;
                recoilYaw = recoilYawWithModifier * recoilRateWhileCrawling;
                recoilRoll = recoilRollWithModifier * recoilRateWhileCrawling;
            }
            else
            {
                recoilPitch = recoilPitchWithModifier;
                recoilYaw = recoilYawWithModifier;
                recoilRoll = recoilRollWithModifier;
            }

            float ratePitch;
            float rateYaw;
            float rateRoll;

            ratePitch = cachedData.RecoilRate;

            rateYaw = cachedData.RecoilRate;
            if (cachedData.RecoilYawRate != 0f)
                rateYaw = cachedData.RecoilYawRate;

            rateRoll = cachedData.RecoilRate;
            if (cachedData.RecoilRollRate != 0f)
                rateRoll = cachedData.RecoilRollRate;

            recoilPitch += weaponRecoilPitch * (1f + ratePitch);
            recoilYaw += weaponRecoilYaw * (1f + rateYaw);
            recoilRoll += weaponRecoilRoll * (1f + rateRoll);

            if (Controller.IsZoomAimming)
            {
                recoilPitch *= recoilPitchScaleWhileAiming;
                recoilYaw *= recoilYawScaleWhileAiming;
                recoilRoll *= recoilRollScaleWhileAiming;
                recoilPitch += additioanlPitchWhileAiming;
                recoilYaw += additioanlYawWhileAiming;
                recoilRoll += additioanlRollWhileAiming;
            }
            else
            {
                switch (Controller.ActiveViewMode)
                {
                    case ShooterControllerViewMode.Fps:
                        recoilPitch *= recoilPitchScaleWhileFpsView;
                        recoilYaw *= recoilYawScaleWhileFpsView;
                        recoilRoll *= recoilRollScaleWhileFpsView;
                        recoilPitch += additioanlPitchWhileFpsView;
                        recoilYaw += additioanlYawWhileFpsView;
                        recoilRoll += additioanlRollWhileFpsView;
                        break;
                    case ShooterControllerViewMode.Shoulder:
                        recoilPitch *= recoilPitchScaleWhileShoulderView;
                        recoilYaw *= recoilYawScaleWhileShoulderView;
                        recoilRoll *= recoilRollScaleWhileShoulderView;
                        recoilPitch += additioanlPitchWhileShoulderView;
                        recoilYaw += additioanlYawWhileShoulderView;
                        recoilRoll += additioanlRollWhileShoulderView;
                        break;
                    default:
                        recoilPitch *= recoilPitchScale;
                        recoilYaw *= recoilYawScale;
                        recoilRoll *= recoilRollScale;
                        recoilPitch += additioanlPitch;
                        recoilYaw += additioanlYaw;
                        recoilRoll += additioanlRoll;
                        break;
                }
            }

            if (recoilPitch > 0f || recoilYaw > 0f || recoilRoll > 0f)
            {
                Controller.CacheGameplayCameraController.Recoil(-recoilPitch, Random.Range(-recoilYaw, recoilYaw), Random.Range(-recoilRoll, recoilRoll));
            }
        }
    }
}
