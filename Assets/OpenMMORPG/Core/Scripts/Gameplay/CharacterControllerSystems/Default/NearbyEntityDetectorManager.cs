using System.Collections.Generic;
using UnityEngine;

namespace MultiplayerARPG
{
    public class NearbyEntityDetectorManager : MonoBehaviour
    {
        private static NearbyEntityDetectorManager _instance;
        public static NearbyEntityDetectorManager Instance => _instance != null ? _instance : (_instance = CreateInstance());
        private static readonly HashSet<NearbyEntityDetector> _detectors = new HashSet<NearbyEntityDetector>();
        private static float _latestDetectTime = -1f;
        private static float _latestSortTime = -1f;

        public float detectDelay = 0.5f;
        public float sortDelay = 1f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void Initialize()
        {
            _instance = null;
            _latestDetectTime = -1f;
            _latestSortTime = -1f;
        }

        private static NearbyEntityDetectorManager CreateInstance()
        {
            var gameObject = new GameObject(nameof(NearbyEntityDetectorManager))
            {
                hideFlags = HideFlags.DontSave,
            };
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                gameObject.hideFlags = HideFlags.HideAndDontSave;
            }
            else
#endif
            {
                DontDestroyOnLoad(gameObject);
            }
            return gameObject.AddComponent<NearbyEntityDetectorManager>();
        }

        public static void Register(NearbyEntityDetector detector)
        {
            Instance.Register_Implementation(detector);
        }

        private void Register_Implementation(NearbyEntityDetector detector)
        {
            _detectors.Add(detector);
        }

        public static void Unregister(NearbyEntityDetector detector)
        {
            Instance.Unregister_Implementation(detector);
        }

        private void Unregister_Implementation(NearbyEntityDetector detector)
        {
            _detectors.Remove(detector);
        }

        private void Awake()
        {
            if (_instance == null)
                _instance = this;
        }

        private void Update()
        {
            if (_instance != this)
            {
                Destroy(gameObject);
                return;
            }

            if (GameInstance.PlayingCharacterEntity == null)
                return;

            float currentTime = Time.unscaledTime;
            bool willDetect = currentTime - _latestDetectTime > detectDelay;
            if (willDetect)
            {
                _latestDetectTime = currentTime;
            }
            bool willSort = currentTime - _latestSortTime > sortDelay;
            if (willSort)
            {
                _latestSortTime = currentTime;
            }
            foreach (NearbyEntityDetector entityDetector in _detectors)
            {
                bool hasChanges = false;
                if (willDetect)
                {
                    hasChanges |= entityDetector.DetectEntities();
                }
                else
                {
                    hasChanges |= entityDetector.RemoveAllInactiveEntities();
                }
                if (willDetect || willSort)
                {
                    entityDetector.SortAllEntities();
                }
                if (hasChanges)
                {
                    entityDetector.TriggerOnUpdateList();
                }
            }
        }
    }
}