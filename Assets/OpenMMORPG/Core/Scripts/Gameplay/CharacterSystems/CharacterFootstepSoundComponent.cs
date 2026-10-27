using Insthync.AudioManager;
using Insthync.ManagedUpdating;
using LiteNetLibManager;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace MultiplayerARPG
{
    public class CharacterFootstepSoundComponent : BaseGameEntityComponent<BaseGameEntity>, IManagedUpdate
    {
        public AudioSource audioSource;
        public AudioComponentSettingType settingType = AudioComponentSettingType.Sfx;
        public string otherSettingId;
        public FootstepSettings walkFootstepSettings;
        public FootstepSettings moveFootstepSettings;
        public FootstepSettings sprintFootstepSettings;
        public FootstepSettings crouchFootstepSettings;
        public FootstepSettings crawlFootstepSettings;
        public FootstepSettings swimFootstepSettings;

        public string SettingId
        {
            get
            {
                switch (settingType)
                {
                    case AudioComponentSettingType.Master:
                        return AudioManager.Singleton.masterVolumeSetting.id;
                    case AudioComponentSettingType.Bgm:
                        return AudioManager.Singleton.bgmVolumeSetting.id;
                    case AudioComponentSettingType.Sfx:
                        return AudioManager.Singleton.sfxVolumeSetting.id;
                    case AudioComponentSettingType.Ambient:
                        return AudioManager.Singleton.ambientVolumeSetting.id;
                }
                return otherSettingId;
            }
        }

        #region Deprecated settings
        [HideInInspector]
        public FootstepSoundData soundData;
        [HideInInspector]
        [Tooltip("This is delay to play future footstep sounds")]
        public float stepDelay = 0.35f;
        [HideInInspector]
        [Tooltip("This is threshold to play footstep sounds, for example if this value is 0.1 and velocity.magnitude more or equals to 0.1 it will play sounds")]
        public float stepThreshold = 0.1f;
        [HideInInspector]
        [Range(0f, 1f)]
        public float randomVolumeMin = 0.75f;
        [HideInInspector]
        [Range(0f, 1f)]
        public float randomVolumeMax = 1f;
        [HideInInspector]
        [Range(-3f, 3f)]
        public float randomPitchMin = 0.75f;
        [HideInInspector]
        [Range(-3f, 3f)]
        public float randomPitchMax = 1f;
        #endregion

        private FootstepSettings currentFootstepSettings;
        private float delayCounter = 0f;
        private Vector3 _lastPosition;
        private float _lastYaw;
        private bool _hasLastPosition;
        private bool _wasMovingVehicle;
        private bool _wasMovingNonVehicle;

        private void Start()
        {
            if (Application.isBatchMode)
            {
                enabled = false;
                return;
            }
            MigrateSettings();
            if (audioSource == null)
            {
                GameObject audioSourceObject = new GameObject("_FootstepAudioSource");
                audioSourceObject.transform.parent = EntityTransform;
                audioSourceObject.transform.localPosition = Vector3.zero;
                audioSourceObject.transform.localRotation = Quaternion.identity;
                audioSourceObject.transform.localScale = Vector3.one;
                audioSource = audioSourceObject.AddComponent<AudioSource>();
            }
            if (audioSource != null)
            {
                audioSource.spatialBlend = 1f;
                audioSource.minDistance = 3f;
                audioSource.maxDistance = 20f;
                audioSource.rolloffMode = AudioRolloffMode.Linear;
            }
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (MigrateSettings())
                EditorUtility.SetDirty(this);
        }
#endif

        private bool MigrateSettings()
        {
            if (soundData.randomAudioClips != null && soundData.randomAudioClips.Length > 0 &&
                (moveFootstepSettings == null ||
                moveFootstepSettings.soundData.randomAudioClips == null ||
                moveFootstepSettings.soundData.randomAudioClips.Length == 0))
            {
                Logging.LogWarning(ToString(), "Migration run to setup old footstep settings to new footstep settings due to codes structure changes");
                moveFootstepSettings = new FootstepSettings()
                {
                    soundData = soundData,
                    stepDelay = stepDelay,
                    stepThreshold = stepThreshold,
                    randomVolumeMin = randomVolumeMin,
                    randomVolumeMax = randomVolumeMax,
                    randomPitchMin = randomPitchMin,
                    randomPitchMax = randomPitchMax,
                };
                return true;
            }
            return false;
        }

        private void OnEnable()
        {
            UpdateManager.Register(this);
        }

        private void OnDisable()
        {
            UpdateManager.Unregister(this);
        }

        public void ManagedUpdate()
        {
            if (Entity != null && Entity.IsServer && !Entity.IsClient)
                return;

            if (audioSource == null)
                return;

            audioSource.mute = AudioManager.Singleton != null && !AudioManager.Singleton.sfxVolumeSetting.IsOn;

            // Don't play footstep sounds for dead living entities (vehicles are not damageable living characters)
            if (!(Entity is IVehicleEntity) && Entity is DamageableEntity damageable && damageable.IsDead())
            {
                delayCounter = 0f;
                return;
            }

            Vector3 currentPos = EntityTransform.position;

            // Distance culling: exempt the local player's character and the vehicle currently ridden by local player
            bool isRiddenByLocalPlayer = GameInstance.PlayingCharacterEntity != null &&
                GameInstance.PlayingCharacterEntity.PassengingVehicleEntity != null &&
                GameInstance.PlayingCharacterEntity.PassengingVehicleEntity.Entity == Entity;

            if (Entity != GameInstance.PlayingCharacterEntity && !isRiddenByLocalPlayer)
            {
                if (GameInstance.PlayingCharacterEntity != null)
                {
                    Vector3 listenerPos = GameInstance.PlayingCharacterEntity.EntityTransform.position;
                    if ((currentPos - listenerPos).sqrMagnitude > 484f) // 22m squared (maxDistance is 20m)
                    {
                        _lastPosition = currentPos;
                        _lastYaw = EntityTransform.eulerAngles.y;
                        _hasLastPosition = true;
                        delayCounter = 0f;
                        return;
                    }
                }
                else if (Camera.main != null)
                {
                    Vector3 listenerPos = Camera.main.transform.position;
                    if ((currentPos - listenerPos).sqrMagnitude > 484f)
                    {
                        _lastPosition = currentPos;
                        _lastYaw = EntityTransform.eulerAngles.y;
                        _hasLastPosition = true;
                        delayCounter = 0f;
                        return;
                    }
                }
            }

            if (!_hasLastPosition)
            {
                _lastPosition = currentPos;
                _lastYaw = EntityTransform.eulerAngles.y;
                _hasLastPosition = true;
                return;
            }
            Vector3 delta = currentPos - _lastPosition;
            delta.y = 0f;
            float speed = Time.deltaTime > 0f ? delta.magnitude / Time.deltaTime : 0f;
            _lastPosition = currentPos;

            float yawDelta = Mathf.Abs(Mathf.DeltaAngle(_lastYaw, EntityTransform.eulerAngles.y));
            float turnSpeed = Time.deltaTime > 0f ? yawDelta / Time.deltaTime : 0f;
            _lastYaw = EntityTransform.eulerAngles.y;

            bool isVehicle = Entity is IVehicleEntity;
            if (isVehicle)
            {
                float threshold = (moveFootstepSettings != null && moveFootstepSettings.stepThreshold > 0f)
                    ? moveFootstepSettings.stepThreshold
                    : 0.1f;

                bool isTurning = turnSpeed > 20f;
                bool isMoving = speed >= threshold || isTurning;
                if (!isMoving)
                {
                    delayCounter = 0f;
                    _wasMovingVehicle = false;
                    return;
                }

                float effectiveSpeed = isTurning ? Mathf.Max(speed, 2.0f) : speed;

                if (Entity.MovementState.Has(MovementState.IsUnderWater))
                {
                    currentFootstepSettings = swimFootstepSettings ?? moveFootstepSettings;
                }
                else if (effectiveSpeed < 3.5f)
                {
                    currentFootstepSettings = walkFootstepSettings ?? moveFootstepSettings;
                }
                else if (effectiveSpeed < 6.5f)
                {
                    currentFootstepSettings = moveFootstepSettings;
                }
                else
                {
                    currentFootstepSettings = sprintFootstepSettings ?? moveFootstepSettings;
                }

                float stepDelay = (currentFootstepSettings != null && currentFootstepSettings.stepDelay > 0f)
                    ? currentFootstepSettings.stepDelay
                    : 0.3f;
                float speedMultiplier = Mathf.Max(0.1f, Entity.MoveAnimationSpeedMultiplier);

                if (!_wasMovingVehicle)
                {
                    _wasMovingVehicle = true;
                    // Trigger first step right away on starting to move
                    delayCounter = stepDelay / speedMultiplier;
                }
                else
                {
                    delayCounter += Time.deltaTime;
                }

                if (delayCounter >= stepDelay / speedMultiplier)
                {
                    if (Entity.MovementState.Has(MovementState.IsUnderWater) || !Entity.MovementState.Has(MovementState.IsJump))
                        PlaySound();
                    delayCounter = 0f;
                }
                return;
            }

            if (Entity.MovementState.Has(MovementState.IsUnderWater))
            {
                currentFootstepSettings = swimFootstepSettings ?? moveFootstepSettings;
            }
            else
            {
                switch (Entity.ExtraMovementState)
                {
                    case ExtraMovementState.IsWalking:
                        currentFootstepSettings = walkFootstepSettings ?? moveFootstepSettings;
                        break;
                    case ExtraMovementState.IsSprinting:
                        currentFootstepSettings = sprintFootstepSettings ?? moveFootstepSettings;
                        break;
                    case ExtraMovementState.IsCrouching:
                        currentFootstepSettings = crouchFootstepSettings ?? moveFootstepSettings;
                        break;
                    case ExtraMovementState.IsCrawling:
                        currentFootstepSettings = crawlFootstepSettings ?? moveFootstepSettings;
                        break;
                    default:
                        if (speed > 0.05f && speed < 3.0f && walkFootstepSettings != null && walkFootstepSettings.soundData.randomAudioClips != null && walkFootstepSettings.soundData.randomAudioClips.Length > 0)
                            currentFootstepSettings = walkFootstepSettings;
                        else
                            currentFootstepSettings = moveFootstepSettings;
                        break;
                }
            }

            bool hasDirectionMovement = Entity.MovementState.Has(MovementState.Forward) ||
                Entity.MovementState.Has(MovementState.Backward) ||
                Entity.MovementState.Has(MovementState.Right) ||
                Entity.MovementState.Has(MovementState.Left);

            float thresholdNonVehicle = (currentFootstepSettings != null && currentFootstepSettings.stepThreshold > 0f)
                ? currentFootstepSettings.stepThreshold
                : 0.1f;

            bool isMovingNonVehicle = hasDirectionMovement || speed >= thresholdNonVehicle;
            if (!isMovingNonVehicle)
            {
                // No movement
                delayCounter = 0f;
                _wasMovingNonVehicle = false;
                return;
            }

            float stepDelayNonVehicle = (currentFootstepSettings != null && currentFootstepSettings.stepDelay > 0f)
                ? currentFootstepSettings.stepDelay
                : 0.35f;
            float speedMultiplierNonVehicle = Mathf.Max(0.1f, Entity.MoveAnimationSpeedMultiplier);

            if (!_wasMovingNonVehicle)
            {
                _wasMovingNonVehicle = true;
                delayCounter = (stepDelayNonVehicle / speedMultiplierNonVehicle) * 0.75f;
            }
            else
            {
                delayCounter += Time.deltaTime;
            }

            if (delayCounter >= stepDelayNonVehicle / speedMultiplierNonVehicle)
            {
                if (Entity.MovementState.Has(MovementState.IsUnderWater) || Entity.MovementState.Has(MovementState.IsGrounded) || !Entity.MovementState.Has(MovementState.IsJump))
                    PlaySound();
                delayCounter = 0f;
            }
        }

        public void PlaySound()
        {
            if (Application.isBatchMode || AudioListener.pause)
                return;

            // Don't play sound while muting footstep sound
            if (Entity.MuteFootstepSound)
                return;

            // Don't play sound while passenging vehicle
            if (Entity.PassengingVehicleEntity != null)
                return;

            if (currentFootstepSettings == null)
                return;

            AudioClip clip = currentFootstepSettings.soundData.GetRandomedAudioClip();
            if (clip == null)
                return;

            float volumeLevel = AudioManager.Singleton != null ? AudioManager.Singleton.GetVolumeLevel(SettingId) : 1f;
            audioSource.pitch = Random.Range(currentFootstepSettings.randomPitchMin, currentFootstepSettings.randomPitchMax);
            audioSource.PlayOneShot(clip, Random.Range(currentFootstepSettings.randomVolumeMin, currentFootstepSettings.randomVolumeMax) * volumeLevel);
        }
    }

    [System.Serializable]
    public struct FootstepSoundData
    {
        public AudioClip[] randomAudioClips;

        public AudioClip GetRandomedAudioClip()
        {
            if (randomAudioClips == null || randomAudioClips.Length == 0)
                return null;
            return randomAudioClips[Random.Range(0, randomAudioClips.Length)];
        }
    }

    [System.Serializable]
    public class FootstepSettings
    {
        public FootstepSoundData soundData;
        [Tooltip("This is delay to play next footstep sounds")]
        public float stepDelay = 0.35f;
        [Tooltip("This is threshold to play footstep sounds, for example if this value is 0.1 and velocity.magnitude more or equals to 0.1 it will play sounds")]
        public float stepThreshold = 0.1f;
        [Range(0f, 1f)]
        public float randomVolumeMin = 0.75f;
        [Range(0f, 1f)]
        public float randomVolumeMax = 1f;
        [Range(-3f, 3f)]
        public float randomPitchMin = 0.75f;
        [Range(-3f, 3f)]
        public float randomPitchMax = 1f;
    }
}
