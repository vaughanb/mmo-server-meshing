using UnityEngine;

namespace MultiplayerARPG
{
    public interface IShooterGameplayCameraController : IGameplayCameraController
    {
        Transform LookForwardTransform { get; }
        bool EnableAimAssist { get; set; }
        bool EnableAimAssistX { get; set; }
        bool EnableAimAssistY { get; set; }
        bool AimAssistPlayer { get; set; }
        bool AimAssistMonster { get; set; }
        bool AimAssistBuilding { get; set; }
        bool AimAssistHarvestable { get; set; }
        float AimAssistRadius { get; set; }
        float AimAssistXSpeed { get; set; }
        float AimAssistYSpeed { get; set; }
        float AimAssistMaxAngleFromFollowingTarget { get; set; }
        void Recoil(float pitch, float yaw, float roll);
    }
}
