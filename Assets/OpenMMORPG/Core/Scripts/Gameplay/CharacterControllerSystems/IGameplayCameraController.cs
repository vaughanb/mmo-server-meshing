using UnityEngine;

namespace MultiplayerARPG
{
    public interface IGameplayCameraController
    {
        GameObject gameObject { get; }
        Camera Camera { get; }
        Transform CameraTransform { get; }
        bool UpdateRotation { get; set; }
        bool UpdateRotationX { get; set; }
        bool UpdateRotationY { get; set; }
        bool UpdateZoom { get; set; }
        void Init(BasePlayerCharacterController controller);
        void Setup(BasePlayerCharacterEntity characterEntity);
        void Desetup(BasePlayerCharacterEntity characterEntity);
    }
}
