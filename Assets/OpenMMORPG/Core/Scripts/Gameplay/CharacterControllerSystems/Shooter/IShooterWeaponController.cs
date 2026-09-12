using UnityEngine;

namespace MultiplayerARPG
{
    public interface IShooterWeaponController
    {
        ShooterControllerViewMode ViewMode { get; set; }
        ShooterControllerViewMode ActiveViewMode { get; }
        ValueOverride<Vector3> OverrideCameraTargetOffset { get; }
        Vector3 CameraTargetOffset { get; }
        ValueOverride<float> OverrideCameraZoomDistance { get; }
        float CameraZoomDistance { get; }
        ValueOverride<float> OverrideCameraFov { get; }
        float CameraFov { get; }
        ValueOverride<float> OverrideCameraNearClipPlane { get; }
        float CameraNearClipPlane { get; }
        ValueOverride<float> OverrideCameraFarClipPlane { get; }
        float CameraFarClipPlane { get; }
        ValueOverride<float> OverrideCameraRotationSpeedScale { get; }
        float CameraRotationSpeedScale { get; }
        ValueOverride<bool> OverrideHideCrosshair { get; }
        bool HideCrosshair { get; }
        ValueOverride<bool> OverrideIsLeftViewSide { get; }
        bool IsLeftViewSide { get; }
        ValueOverride<bool> OverrideIsZoomAimming { get; }
        bool IsZoomAimming { get; }
        void UpdateCameraSettings();
    }
}
