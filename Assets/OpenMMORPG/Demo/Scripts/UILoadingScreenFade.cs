using System.Collections;
using System.Reflection;
using LiteNetLibManager;
using UnityEngine;
using UnityEngine.UI;

namespace MultiplayerARPG
{
    /// <summary>
    /// Keeps the kit's loading canvas on screen for every scene change, and fades it out
    /// over the new scene.
    ///
    /// The kit ships the pieces of a loading screen - `UISceneLoading` for the home scene and
    /// `UINetworkSceneLoading` for the map loads - and the demo's `CanvasLoading` carries both,
    /// but two things kept them from ever showing:
    ///
    /// - `UISceneLoading.Singleton` is never assigned. Its `Awake` only checks the property, so
    ///   `GameInstance.LoadHomeSceneTask` always takes the no-UI branch and the home scene loads
    ///   behind nothing at all. (Core bug, to be raised upstream; the setter is private, so the
    ///   demo assigns it by reflection.)
    /// - `UINetworkSceneLoadingEventsSetup` on the map network manager subscribes the kit's
    ///   handlers in `Awake`, and gives up silently if the canvas has not run its own `Awake`
    ///   yet. Both are root objects in `00Init`, so it comes down to Unity's unspecified
    ///   `Awake` order. This rebinds the same handlers in `Start`, after every `Awake`, and
    ///   again if another manager turns up (the editor test harness makes its own).
    ///
    /// Visibility stays the kit's call: it activates its `rootObject` when a load starts and
    /// deactivates it a quarter second after the load ends. This watches that object and
    /// drives the backdrop from it - shown the same frame the kit shows its bar, faded out once
    /// the kit hides it - so the picture goes up before the old scene is torn down and comes
    /// down over the new one instead of cutting.
    ///
    /// **Runs its `Awake` after every default-order `Awake`** (execution order 1000). The
    /// singleton must be assigned only once `UISceneLoading.Awake` has run on this object:
    /// that `Awake` reads "already assigned" as "I am a duplicate" and destroys the whole
    /// canvas, and Unity gives no order among the components of one object. The first live
    /// test lost the canvas exactly that way.
    /// </summary>
    [DefaultExecutionOrder(1000)]
    public class UILoadingScreenFade : MonoBehaviour
    {
        [Tooltip("The kit's `rootObject` on both loading components - active while a load runs")]
        public GameObject kitRoot;
        [Tooltip("The picture and shade; faded out after the kit hides its bar")]
        public CanvasGroup overlay;
        [Tooltip("Map title above the bar; falls back to `defaultTitle` for the home scene")]
        public Text uiTextTitle;
        public string defaultTitle = "Loading";
        public float fadeOutDuration = 0.6f;

        private LiteNetLibAssets _boundAssets;
        private bool _wasShown;
        private bool _titleSet;
        private Coroutine _fade;

        private void Awake()
        {
            AssignSceneLoadingSingleton();
            if (overlay != null)
            {
                overlay.alpha = 0f;
                overlay.gameObject.SetActive(false);
            }
        }

        private void Start()
        {
            BindManager();
        }

        private void Update()
        {
            BindManager();
        }

        private void LateUpdate()
        {
            bool shown = kitRoot != null && kitRoot.activeInHierarchy;
            if (shown == _wasShown)
                return;
            _wasShown = shown;
            if (shown)
                Show();
            else
                Hide();
        }

        /// <summary>
        /// The kit never sets this, so its home-scene load skips the UI. Only the first canvas
        /// claims it: a duplicate is already being destroyed by `UINetworkSceneLoading.Awake`.
        /// </summary>
        private void AssignSceneLoadingSingleton()
        {
            if (UISceneLoading.Singleton != null)
                return;
            UISceneLoading sceneLoading = GetComponent<UISceneLoading>();
            if (sceneLoading == null)
                return;
            PropertyInfo property = typeof(UISceneLoading).GetProperty("Singleton", BindingFlags.Public | BindingFlags.Static);
            if (property != null && property.CanWrite)
                property.SetValue(null, sceneLoading, null);
        }

        /// <summary>
        /// Subscribes the kit's network loading handlers to the current manager's scene events,
        /// exactly once each, whatever order the two objects woke in. `RemoveListener` on a
        /// listener that was never added is a no-op, so remove-then-add is the idempotent form.
        /// </summary>
        private void BindManager()
        {
            BaseGameNetworkManager manager = BaseGameNetworkManager.Singleton;
            if (manager == null || manager.Assets == null || manager.Assets == _boundAssets)
                return;
            _boundAssets = manager.Assets;
            UINetworkSceneLoading net = UINetworkSceneLoading.Singleton;
            if (net != null)
            {
                _boundAssets.onLoadSceneStart.RemoveListener(net.OnLoadSceneStart);
                _boundAssets.onLoadSceneStart.AddListener(net.OnLoadSceneStart);
                _boundAssets.onLoadSceneProgress.RemoveListener(net.OnLoadSceneProgress);
                _boundAssets.onLoadSceneProgress.AddListener(net.OnLoadSceneProgress);
                _boundAssets.onLoadSceneFinish.RemoveListener(net.OnLoadSceneFinish);
                _boundAssets.onLoadSceneFinish.AddListener(net.OnLoadSceneFinish);
                _boundAssets.onSceneFileSizeRetrieving.RemoveListener(net.OnSceneFileSizeRetrieving);
                _boundAssets.onSceneFileSizeRetrieving.AddListener(net.OnSceneFileSizeRetrieving);
                _boundAssets.onSceneDepsFileDownloading.RemoveListener(net.OnSceneDepsFileDownloading);
                _boundAssets.onSceneDepsFileDownloading.AddListener(net.OnSceneDepsFileDownloading);
                _boundAssets.onSceneDepsDownloaded.RemoveListener(net.OnSceneDepsDownloaded);
                _boundAssets.onSceneDepsDownloaded.AddListener(net.OnSceneDepsDownloaded);
                _boundAssets.onLoadAdditiveSceneStart.RemoveListener(net.OnLoadAdditiveSceneStart);
                _boundAssets.onLoadAdditiveSceneStart.AddListener(net.OnLoadAdditiveSceneStart);
                _boundAssets.onLoadAdditiveSceneProgress.RemoveListener(net.OnLoadAdditiveSceneProgress);
                _boundAssets.onLoadAdditiveSceneProgress.AddListener(net.OnLoadAdditiveSceneProgress);
                _boundAssets.onLoadAdditiveSceneFinish.RemoveListener(net.OnLoadAdditiveSceneFinish);
                _boundAssets.onLoadAdditiveSceneFinish.AddListener(net.OnLoadAdditiveSceneFinish);
            }
            _boundAssets.onLoadSceneStart.RemoveListener(OnLoadSceneStart);
            _boundAssets.onLoadSceneStart.AddListener(OnLoadSceneStart);
        }

        /// <summary>Names the map being entered; the kit only passes the scene name.</summary>
        private void OnLoadSceneStart(string sceneName, bool isAdditive, bool isOnline, float progress)
        {
            if (isAdditive || uiTextTitle == null)
                return;
            string title = defaultTitle;
            if (GameInstance.MapInfos != null)
            {
                foreach (BaseMapInfo mapInfo in GameInstance.MapInfos.Values)
                {
                    if (mapInfo == null || mapInfo.Scene == null || !mapInfo.Scene.IsSameSceneName(sceneName))
                        continue;
                    if (!string.IsNullOrEmpty(mapInfo.Title))
                        title = mapInfo.Title;
                    break;
                }
            }
            uiTextTitle.text = title;
            _titleSet = true;
        }

        private void Show()
        {
            if (_fade != null)
            {
                StopCoroutine(_fade);
                _fade = null;
            }
            if (!_titleSet && uiTextTitle != null)
                uiTextTitle.text = defaultTitle;
            if (overlay != null)
            {
                overlay.gameObject.SetActive(true);
                overlay.alpha = 1f;
                overlay.blocksRaycasts = true;
            }
        }

        private void Hide()
        {
            _titleSet = false;
            if (overlay == null || !overlay.gameObject.activeInHierarchy)
                return;
            if (_fade != null)
                StopCoroutine(_fade);
            _fade = StartCoroutine(FadeOut());
        }

        private IEnumerator FadeOut()
        {
            overlay.blocksRaycasts = false;
            float duration = Mathf.Max(0.01f, fadeOutDuration);
            float start = overlay.alpha;
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;
                overlay.alpha = Mathf.Lerp(start, 0f, elapsed / duration);
                yield return null;
            }
            overlay.alpha = 0f;
            overlay.gameObject.SetActive(false);
            _fade = null;
        }
    }
}
