using Insthync.CameraAndInput;
using UnityEngine;

namespace MultiplayerARPG
{
    public class DefaultGameplayCameraController : MonoBehaviour, IGameplayCameraController
    {
        [SerializeField]
        protected FollowCameraControls gameplayCameraPrefab;
        [SerializeField]
        protected bool aimAssistPlayer = true;
        [SerializeField]
        protected bool aimAssistMonster = true;
        [SerializeField]
        protected bool aimAssistBuilding = false;
        [SerializeField]
        protected bool aimAssistHarvestable = false;
        public BasePlayerCharacterEntity PlayerCharacterEntity { get; protected set; }
        public FollowCameraControls CameraControls { get; protected set; }
        public Camera Camera { get => CameraControls.CacheCamera; }
        public Transform CameraTransform { get => CameraControls.CacheCameraTransform; }
        public bool UpdateRotation { get => CameraControls.updateRotation; set => CameraControls.updateRotation = value; }
        public bool UpdateRotationX { get => CameraControls.updateRotationX; set => CameraControls.updateRotationX = value; }
        public bool UpdateRotationY { get => CameraControls.updateRotationY; set => CameraControls.updateRotationY = value; }
        public bool UpdateZoom { get => CameraControls.updateZoom; set => CameraControls.updateZoom = value; }
        public BasePlayerCharacterController PlayerCharacterController { get; protected set; }

        public virtual void Init(BasePlayerCharacterController controller)
        {
            if (gameplayCameraPrefab == null)
            {
                Debug.LogWarning("`gameplayCameraPrefab` is empty, `DefaultGameplayCameraController` component is disabling.");
                enabled = false;
            }
            CameraControls = Instantiate(gameplayCameraPrefab);
            PlayerCharacterController = controller;
            PlayerCharacterController.AssignedCameraTargetOffset = CameraControls.targetOffset;
            PlayerCharacterController.AssignedCameraZoomDistance = CameraControls.zoomDistance;
            PlayerCharacterController.AssignedCameraFov = Camera.fieldOfView;
            PlayerCharacterController.AssignedCameraNearClipPlane = Camera.nearClipPlane;
            PlayerCharacterController.AssignedCameraFarClipPlane = Camera.farClipPlane;
            PlayerCharacterController.AssignedCameraRotationSpeedScale = CameraControls.rotationSpeedScale;
            PlayerCharacterController.AssignedEnableWallHitSpring = CameraControls.enableWallHitSpring;
        }

        public virtual void SetData(FollowCameraControls gameplayCameraPrefab,
            bool aimAssistPlayer = true,
            bool aimAssistMonster = true,
            bool aimAssistBuilding = true,
            bool aimAssistHarvestable = true)
        {
            this.gameplayCameraPrefab = gameplayCameraPrefab;
            this.aimAssistPlayer = aimAssistPlayer;
            this.aimAssistMonster = aimAssistMonster;
            this.aimAssistBuilding = aimAssistBuilding;
            this.aimAssistHarvestable = aimAssistHarvestable;
        }

        protected virtual void OnDestroy()
        {
            if (CameraControls != null)
                Destroy(CameraControls.gameObject);
        }

        protected virtual void Update()
        {
            CameraControls.target = PlayerCharacterController.CameraTargetTransform;
            CameraControls.targetOffset = PlayerCharacterController.CameraTargetOffset;
            Camera.fieldOfView = PlayerCharacterController.CameraFov;
            Camera.nearClipPlane = PlayerCharacterController.CameraNearClipPlane;
            Camera.farClipPlane = PlayerCharacterController.CameraFarClipPlane;
            CameraControls.rotationSpeedScale = PlayerCharacterController.CameraRotationSpeedScale;
            CameraControls.enableWallHitSpring = PlayerCharacterController.EnableWallHitSpring;
        }

        public virtual void Setup(BasePlayerCharacterEntity characterEntity)
        {
            PlayerCharacterEntity = characterEntity;
        }

        public virtual void Desetup(BasePlayerCharacterEntity characterEntity)
        {
            PlayerCharacterEntity = null;
        }
    }
}
