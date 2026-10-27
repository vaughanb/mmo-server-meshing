using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.EventSystems;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif
using UnityEngine.SceneManagement;

namespace MultiplayerARPG
{
    public class EventSystemManager : MonoBehaviour
    {
        public static EventSystem CurrentEventSystem;
        public static event System.Action onEventSystemReady;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoInit()
        {
            SceneManager.sceneLoaded -= SceneManager_sceneLoaded;
            SceneManager.sceneLoaded += SceneManager_sceneLoaded;
            SceneManager_sceneLoaded(SceneManager.GetActiveScene(), LoadSceneMode.Single);
        }

        private void OnEnable()
        {
            SceneManager.sceneLoaded -= SceneManager_sceneLoaded;
            SceneManager.sceneLoaded += SceneManager_sceneLoaded;
        }

        private void OnDisable()
        {
            SceneManager.sceneLoaded -= SceneManager_sceneLoaded;
        }

        private static async void SceneManager_sceneLoaded(Scene scene, LoadSceneMode loadSceneMode)
        {
            if (loadSceneMode != LoadSceneMode.Single)
                return;

            await UniTask.SwitchToMainThread();
            CurrentEventSystem = FindFirstObjectByType<EventSystem>();
            // Create a new event system
            if (CurrentEventSystem == null)
            {
                CurrentEventSystem = new GameObject("EventSystem").AddComponent<EventSystem>();
#if ENABLE_INPUT_SYSTEM
                CurrentEventSystem.gameObject.GetOrAddComponent<InputSystemUIInputModule>();
#else
                CurrentEventSystem.gameObject.GetOrAddComponent<StandaloneInputModule>();
#endif
            }
            else
            {
#if ENABLE_INPUT_SYSTEM
                StandaloneInputModule oldInputModule = CurrentEventSystem.GetComponent<StandaloneInputModule>();
                if (oldInputModule != null)
                    DestroyImmediate(oldInputModule);
                CurrentEventSystem.gameObject.GetOrAddComponent<InputSystemUIInputModule>();
#else
                BaseInputModule oldInputModule = CurrentEventSystem.GetComponent("InputSystemUIInputModule") as BaseInputModule;
                if (oldInputModule != null)
                    DestroyImmediate(oldInputModule);
                CurrentEventSystem.gameObject.GetOrAddComponent<StandaloneInputModule>();
#endif
            }
            CurrentEventSystem.sendNavigationEvents = false;

            if (onEventSystemReady != null)
                onEventSystemReady.Invoke();
        }
    }
}
