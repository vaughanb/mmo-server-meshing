using Insthync.CameraAndInput;
using UnityEngine;

namespace MultiplayerARPG
{
    public class DefaultMinimapCameraController : MonoBehaviour, IMinimapCameraController
    {
        [SerializeField]
        protected FollowCameraControls minimapCameraPrefab;
        public FollowCameraControls CameraControls { get; protected set; }
        public Camera Camera { get { return CameraControls.CacheCamera; } }
        public Transform CameraTransform { get { return CameraControls.CacheCameraTransform; } }
        public BasePlayerCharacterController PlayerCharacterController { get; protected set; }

        public virtual void Init(BasePlayerCharacterController controller)
        {
            if (minimapCameraPrefab == null)
            {
                Debug.LogWarning("`minimapCameraPrefab` is empty, `DefaultMinimapCameraController` component is disabling.");
                enabled = false;
                return;
            }
            CameraControls = Instantiate(minimapCameraPrefab);
            PlayerCharacterController = controller;
        }

        public virtual void SetData(FollowCameraControls minimapCameraPrefab)
        {
            this.minimapCameraPrefab = minimapCameraPrefab;
        }

        protected virtual void OnDestroy()
        {
            if (CameraControls != null)
                Destroy(CameraControls.gameObject);
        }

        protected virtual void Update()
        {
            CameraControls.target = PlayerCharacterController.CameraTargetTransform;
            switch (MinimapRotationSetting.CameraRotationMode)
            {
                case MinimapRotationSetting.ECameraRotationMode.LockRotation:
                    CameraControls.yRotation = CameraControls.minYRotation = CameraControls.maxYRotation = MinimapRotationSetting.LockYRotation;
                    break;
                case MinimapRotationSetting.ECameraRotationMode.FollowCharacterRotation:
                    CameraControls.yRotation = CameraControls.minYRotation = CameraControls.maxYRotation = PlayerCharacterController.CameraTargetTransform.eulerAngles.y;
                    break;
                case MinimapRotationSetting.ECameraRotationMode.FollowGameplayCameraRotation:
                    CameraControls.yRotation = CameraControls.minYRotation = CameraControls.maxYRotation = PlayerCharacterController.MainCameraTransform.eulerAngles.y;
                    break;
            }
        }

        public virtual void Setup(BasePlayerCharacterEntity characterEntity)
        {

        }

        public virtual void Desetup(BasePlayerCharacterEntity characterEntity)
        {

        }
    }
}
