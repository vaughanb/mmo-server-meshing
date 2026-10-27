#if !DISABLE_ADDRESSABLES
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.Initialization;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;
using UnityEngine.Networking;
using System.IO;

namespace Insthync.AddressableAssetTools
{
    public partial class AddressableAssetDownloadManager : MonoBehaviour
    {
        public string remoteConfigUrl;
        public string cachedClientConfigFileName = "cachedAddressableConfig.json";
        public AssetReferenceDownloadManagerSettings settingsAssetReference;
        [Header("Events")]
        public UnityEvent onStart = new UnityEvent();
        public UnityEvent onEnd = new UnityEvent();
        public UnityEvent onFileSizeRetrieving = new UnityEvent();
        public UnityEvent<AsyncOperationStatus, System.Exception> onUnableToCheckForCatalogUpdates = new UnityEvent<AsyncOperationStatus, System.Exception>();
        public UnityEvent<List<string>, AsyncOperationStatus, System.Exception> onUnableToUpdateCatalogs = new UnityEvent<List<string>, AsyncOperationStatus, System.Exception>();
        public UnityEvent<AsyncOperationStatus, System.Exception> onUnableToLoadSettings = new UnityEvent<AsyncOperationStatus, System.Exception>();
        public UnityEvent<object, AsyncOperationStatus, System.Exception> onUnableToInitialObject = new UnityEvent<object, AsyncOperationStatus, System.Exception>();
        public AddressableAssetFileSizeEvent onFileSizeRetrieved = new AddressableAssetFileSizeEvent();
        public AddressableAssetTotalProgressEvent onDepsDownloading = new AddressableAssetTotalProgressEvent();
        public AddressableAssetTotalProgressEvent onDepsDownloaded = new AddressableAssetTotalProgressEvent();
        public AddressableAssetDownloadProgressEvent onDepsFileDownloading = new AddressableAssetDownloadProgressEvent();
        public System.Action<System.Exception> onDepsDownloadError;
        public UnityEvent onDownloadedAll = new UnityEvent();

        public long FileSize { get; protected set; } = 0;
        public int LoadedCount { get; protected set; } = 0;
        public int TotalCount { get; protected set; } = 0;
        public string CachedClientConfigPath => Path.Combine(Application.persistentDataPath, cachedClientConfigFileName);

        public AddressableRemoteConfig _remoteConfig;

        private async void Start()
        {
            try
            {
                await StartAsync();
            }
            catch (System.Exception ex)
            {
                Debug.LogException(ex);
                onDepsDownloadError?.Invoke(ex);
            }
        }

        private async UniTask StartAsync()
        {
            await UniTask.Yield();
            onStart?.Invoke();

            if (!string.IsNullOrWhiteSpace(remoteConfigUrl))
            {
                string url;
                if (!remoteConfigUrl.Contains("?"))
                    url = $"{remoteConfigUrl}?";
                else
                    url = $"{remoteConfigUrl}&";

                url += $"time={System.DateTime.Now.Ticks / System.TimeSpan.TicksPerMillisecond}";
                url += $"&platform={Application.platform}";
                url += $"&version={Application.version}";
                url += $"&unity_version={Application.unityVersion}";

                using (UnityWebRequest webRequest = UnityWebRequest.Get(url))
                {
                    webRequest.SetRequestHeader("User-Agent", $"{Application.identifier}/{Application.version} (Unity {Application.unityVersion}; {Application.platform})");

                    UnityWebRequestAsyncOperation ayncOp = webRequest.SendWebRequest();
                    do
                    {
                        await Task.Yield();
                    } while (!ayncOp.isDone);
                    if (webRequest.result == UnityWebRequest.Result.Success)
                    {
                        string dataAsJson = webRequest.downloadHandler.text;
                        Debug.Log($"Found addressable remote config. {dataAsJson}");
                        try
                        {
                            _remoteConfig = JsonConvert.DeserializeObject<AddressableRemoteConfig>(dataAsJson);
                            File.WriteAllText(CachedClientConfigPath, dataAsJson);
                        }
                        catch (System.Exception ex)
                        {
                            _remoteConfig = null;
                            Debug.LogError($"Unable to read remote config data {ex.Message}\n{ex.StackTrace}");
                        }
                    }
                    else
                    {
                        Debug.LogError($"Not found addressable remote config. ({url}) ({webRequest.error})");
                    }
                }
            }

            if (_remoteConfig == null)
            {
                // Try to read from cached local file
                if (File.Exists(CachedClientConfigPath))
                {
                    try
                    {
                        string cachedJson = File.ReadAllText(CachedClientConfigPath);
                        _remoteConfig = JsonConvert.DeserializeObject<AddressableRemoteConfig>(cachedJson);
                    }
                    catch (System.Exception ex)
                    {
                        _remoteConfig = null;
                        Debug.LogError($"Unable to read cached config data {ex.Message}\n{ex.StackTrace}");
                    }
                }
            }

            if (_remoteConfig != null && _remoteConfig.replaceRuntimeProperties != null)
            {
                foreach (KeyValuePair<string, string> kv in _remoteConfig.replaceRuntimeProperties)
                {
                    AddressablesRuntimeProperties.SetPropertyValue(kv.Key, kv.Value);
                }
            }

            Debug.Log("Initializing addressable.");
            AsyncOperationHandle<IResourceLocator> initialResourceLocatorHandle = Addressables.InitializeAsync(false);
            try
            {
                await initialResourceLocatorHandle.Task;
            }
            finally
            {
                if (initialResourceLocatorHandle.IsValid())
                    Addressables.Release(initialResourceLocatorHandle);
            }

            if (_remoteConfig != null && _remoteConfig.catalogUrls != null)
            {
                Debug.Log($"Reading remote config and catalogs.");
                foreach (string catalogUrl in _remoteConfig.catalogUrls)
                {
                    string url;
                    if (!catalogUrl.Contains("?"))
                        url = $"{catalogUrl}?time={System.DateTime.Now.Ticks / System.TimeSpan.TicksPerMillisecond}";
                    else
                        url = $"{catalogUrl}&time={System.DateTime.Now.Ticks / System.TimeSpan.TicksPerMillisecond}";
                    AsyncOperationHandle<IResourceLocator> handle = Addressables.LoadContentCatalogAsync(url, false);
                    try
                    {
                        await handle.Task;
                    }
                    finally
                    {
                        if (handle.IsValid())
                            Addressables.Release(handle);
                    }
                }
            }

            Debug.Log("Checking for catalog updates.");
            List<string> catalogToUpdates = null;
            AsyncOperationHandle<List<string>> checkForCatalogUpdatesHandle = Addressables.CheckForCatalogUpdates(false);
            try
            {
                await checkForCatalogUpdatesHandle.Task;
                if (checkForCatalogUpdatesHandle.Status != AsyncOperationStatus.Succeeded)
                    onUnableToCheckForCatalogUpdates.Invoke(checkForCatalogUpdatesHandle.Status, checkForCatalogUpdatesHandle.OperationException);
                else
                    catalogToUpdates = new List<string>(checkForCatalogUpdatesHandle.Result);
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"Unable to check for catalog updates {ex.Message}\n{ex.StackTrace}");
                onUnableToCheckForCatalogUpdates.Invoke(checkForCatalogUpdatesHandle.Status, ex);
            }
            if (checkForCatalogUpdatesHandle.IsValid())
                Addressables.Release(checkForCatalogUpdatesHandle);

            if (catalogToUpdates != null && catalogToUpdates.Count > 0)
            {
                AsyncOperationHandle<List<IResourceLocator>> updateHandle = Addressables.UpdateCatalogs(true, catalogToUpdates, false);
                try
                {
                    await updateHandle.Task;
                    if (updateHandle.Status != AsyncOperationStatus.Succeeded)
                        onUnableToUpdateCatalogs.Invoke(catalogToUpdates, updateHandle.Status, updateHandle.OperationException);
                }
                catch (System.Exception ex)
                {
                    Debug.LogError($"Unable to check for catalog updates {ex.Message}\n{ex.StackTrace}");
                    onUnableToUpdateCatalogs.Invoke(catalogToUpdates, updateHandle.Status, ex);
                }
                if (updateHandle.IsValid())
                    Addressables.Release(updateHandle);
            }
            AddressableAssetsManager.ClearResourceLocationCache();

            HashSet<object> keys = new HashSet<object>();
            foreach (IResourceLocator resourceLocator in Addressables.ResourceLocators)
            {
                foreach (object key in resourceLocator.Keys)
                {
                    keys.Add(key);
                }
            }

            // Downloads
            Debug.Log("Start assets downloading...");
            TotalCount = 1;
            try
            {
                await DownloadMany(keys,
                    OnFileSizeRetrieving,
                    OnFileSizeRetrieved,
                    OnDepsDownloading,
                    OnDepsFileDownloading,
                    OnDepsDownloaded,
                    OnDepsDownloadError);
            }
            catch (System.Exception ex)
            {
                Debug.LogException(ex);
                return;
            }
            finally
            {
                keys.Clear();
            }
            LoadedCount++;

            await UniTask.Yield();
            onDownloadedAll?.Invoke();

            // Read settings to find which assets will be instantiated
            Debug.Log("Read addressable asset download manager settings.");
            AddressableAssetDownloadManagerSettings settings = null;
            AsyncOperationHandle<AddressableAssetDownloadManagerSettings> loadSettingsHandle;
            loadSettingsHandle = settingsAssetReference.LoadAssetAsync();
            try
            {
                await loadSettingsHandle.Task;
                settings = loadSettingsHandle.Result;
                if (loadSettingsHandle.Status != AsyncOperationStatus.Succeeded)
                {
                    onUnableToLoadSettings.Invoke(loadSettingsHandle.Status, loadSettingsHandle.OperationException);
                    Addressables.Release(loadSettingsHandle);
                    return;
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"Unable to load settings {ex.Message}\n{ex.StackTrace}");
                onUnableToLoadSettings.Invoke(loadSettingsHandle.Status, ex);
                Addressables.Release(loadSettingsHandle);
                return;
            }
            try
            {
                // Instantiates
                for (int i = 0; i < settings.InitialObjects.Count; ++i)
                {
                    AssetReference assetRef = settings.InitialObjects[i];
                    if (assetRef == null)
                    {
                        Debug.LogWarning($"Null initial object {i}, skipping...");
                        continue;
                    }
                    object runtimeKey = assetRef.RuntimeKey;
                    Debug.Log($"Initializing {runtimeKey}");
                    AsyncOperationHandle<GameObject> instantiateOp = Addressables.InstantiateAsync(runtimeKey);
                    try
                    {
                        await instantiateOp.Task;
                        if (instantiateOp.Status != AsyncOperationStatus.Succeeded)
                        {
                            onUnableToInitialObject.Invoke(runtimeKey, instantiateOp.Status, instantiateOp.OperationException);
                            if (instantiateOp.IsValid())
                                Addressables.Release(instantiateOp);
                            continue;
                        }
                        GameObject instance = instantiateOp.Result;
                        if (instance.GetComponent<AssetReferenceReleaser>() == null)
                            instance.AddComponent<AssetReferenceReleaser>();
                        Debug.Log($"Initialized {instance.name}");
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogError($"Unable to initialize {runtimeKey} {ex.Message}\n{ex.StackTrace}");
                        onUnableToInitialObject.Invoke(runtimeKey, instantiateOp.Status, ex);
                        if (instantiateOp.IsValid())
                            Addressables.Release(instantiateOp);
                    }
                }

                // Warmup shader variant collections
                for (int i = 0; i < settings.ShaderVariantCollections.Count; ++i)
                {
                    AssetReferenceShaderVariantCollection svcRef = settings.ShaderVariantCollections[i];
                    if (svcRef == null)
                    {
                        Debug.LogWarning($"Null shader variant collection {i}, skipping...");
                        continue;
                    }
                    object runtimeKey = svcRef.RuntimeKey;
                    Debug.Log($"Warming up shader variant collection {runtimeKey}");
                    AsyncOperationHandle<ShaderVariantCollection> loadSvcOp = svcRef.LoadAssetAsync<ShaderVariantCollection>();
                    try
                    {
                        ShaderVariantCollection svc = await loadSvcOp.Task;
                        if (loadSvcOp.Status != AsyncOperationStatus.Succeeded)
                        {
                            Debug.LogError($"Unable to load shader variant collection {runtimeKey} {loadSvcOp.OperationException}");
                            continue;
                        }
                        svc.WarmUp();
                        Debug.Log($"Warmed up shader variant collection {svc.name}");
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogError($"Unable to load shader variant collection {runtimeKey} {ex.Message}\n{ex.StackTrace}");
                    }
                    finally
                    {
                        if (loadSvcOp.IsValid())
                            Addressables.Release(loadSvcOp);
                    }
                }
            }
            finally
            {
                if (loadSettingsHandle.IsValid())
                    Addressables.Release(loadSettingsHandle);
            }
            onEnd?.Invoke();
        }

        private void OnDestroy()
        {
            onStart?.RemoveAllListeners();
            onStart = null;
            onEnd?.RemoveAllListeners();
            onEnd = null;
            onFileSizeRetrieving?.RemoveAllListeners();
            onFileSizeRetrieving = null;
            onFileSizeRetrieved?.RemoveAllListeners();
            onFileSizeRetrieved = null;
            onDepsDownloading?.RemoveAllListeners();
            onDepsDownloading = null;
            onDepsFileDownloading?.RemoveAllListeners();
            onDepsFileDownloading = null;
            onDepsDownloaded?.RemoveAllListeners();
            onDepsDownloaded = null;
            onDepsDownloadError = null;
            onDownloadedAll?.RemoveAllListeners();
            onDownloadedAll = null;
        }

        protected virtual void OnFileSizeRetrieving()
        {
            FileSize = 0;
            onFileSizeRetrieving?.Invoke();
        }

        protected virtual void OnFileSizeRetrieved(long fileSize)
        {
            FileSize = fileSize;
            onFileSizeRetrieved?.Invoke(fileSize);
        }

        protected virtual void OnDepsDownloading()
        {
            onDepsDownloading?.Invoke(LoadedCount, TotalCount);
        }

        protected virtual void OnDepsFileDownloading(long downloadSize, long fileSize, float percentComplete)
        {
            onDepsFileDownloading?.Invoke(downloadSize, fileSize, percentComplete);
        }

        protected virtual void OnDepsDownloaded()
        {
            onDepsDownloaded?.Invoke(LoadedCount, TotalCount);
        }

        protected virtual void OnDepsDownloadError(System.Exception ex)
        {
            onDepsDownloadError?.Invoke(ex);
        }

        public static async Task<SceneInstance> DownloadAndLoadScene(
            object runtimeKey,
            LoadSceneParameters loadSceneParameters,
            System.Action onFileSizeRetrieving,
            AddressableAssetFileSizeDelegate onFileSizeRetrieved,
            System.Action onDepsDownloading,
            AddressableAssetDownloadProgressDelegate onDepsFileDownloading,
            System.Action onDepsDownloaded,
            System.Action<System.Exception> onError)
        {
            await Download(runtimeKey, onFileSizeRetrieving, onFileSizeRetrieved, onDepsDownloading, onDepsFileDownloading, onDepsDownloaded, onError);
            AsyncOperationHandle<SceneInstance> loadSceneOp = Addressables.LoadSceneAsync(runtimeKey, loadSceneParameters);
            try
            {
                while (!loadSceneOp.IsDone)
                    await UniTask.Yield();
                if (loadSceneOp.Status != AsyncOperationStatus.Succeeded)
                    throw loadSceneOp.OperationException ?? new System.Exception($"Unable to load addressable scene: {runtimeKey}");
                return loadSceneOp.Result;
            }
            catch
            {
                if (loadSceneOp.IsValid())
                    Addressables.Release(loadSceneOp);
                throw;
            }
        }

        public static async Task<GameObject> DownloadAndInstantiate(
            object runtimeKey,
            System.Action onFileSizeRetrieving,
            AddressableAssetFileSizeDelegate onFileSizeRetrieved,
            System.Action onDepsDownloading,
            AddressableAssetDownloadProgressDelegate onDepsFileDownloading,
            System.Action onDepsDownloaded,
            System.Action<System.Exception> onError)
        {
            await Download(runtimeKey, onFileSizeRetrieving, onFileSizeRetrieved, onDepsDownloading, onDepsFileDownloading, onDepsDownloaded, onError);
            AsyncOperationHandle<GameObject> instantiateOp = Addressables.InstantiateAsync(runtimeKey);
            try
            {
                while (!instantiateOp.IsDone)
                    await UniTask.Yield();
                if (instantiateOp.Status != AsyncOperationStatus.Succeeded)
                    throw instantiateOp.OperationException ?? new System.Exception($"Unable to instantiate addressable asset: {runtimeKey}");
                return instantiateOp.Result;
            }
            catch
            {
                if (instantiateOp.IsValid())
                    Addressables.Release(instantiateOp);
                throw;
            }
        }

        public static async Task Download(
            object runtimeKey,
            System.Action onFileSizeRetrieving,
            AddressableAssetFileSizeDelegate onFileSizeRetrieved,
            System.Action onDepsDownloading,
            AddressableAssetDownloadProgressDelegate onDepsFileDownloading,
            System.Action onDepsDownloaded,
            System.Action<System.Exception> onError)
        {
            await DownloadInternal(
                () => Addressables.GetDownloadSizeAsync(runtimeKey),
                () => Addressables.DownloadDependenciesAsync(runtimeKey),
                onFileSizeRetrieving, onFileSizeRetrieved, onDepsDownloading,
                onDepsFileDownloading, onDepsDownloaded, onError);
        }

        public static async Task DownloadMany(
            IEnumerable runtimeKeys,
            System.Action onFileSizeRetrieving,
            AddressableAssetFileSizeDelegate onFileSizeRetrieved,
            System.Action onDepsDownloading,
            AddressableAssetDownloadProgressDelegate onDepsFileDownloading,
            System.Action onDepsDownloaded,
            System.Action<System.Exception> onError)
        {
            await DownloadInternal(
                () => Addressables.GetDownloadSizeAsync(runtimeKeys),
                () => Addressables.DownloadDependenciesAsync(runtimeKeys, Addressables.MergeMode.Union),
                onFileSizeRetrieving, onFileSizeRetrieved, onDepsDownloading,
                onDepsFileDownloading, onDepsDownloaded, onError);
        }

        private static async Task DownloadInternal(
            System.Func<AsyncOperationHandle<long>> getSize,
            System.Func<AsyncOperationHandle> download,
            System.Action onFileSizeRetrieving,
            AddressableAssetFileSizeDelegate onFileSizeRetrieved,
            System.Action onDepsDownloading,
            AddressableAssetDownloadProgressDelegate onDepsFileDownloading,
            System.Action onDepsDownloaded,
            System.Action<System.Exception> onError)
        {
            try
            {
                onFileSizeRetrieving?.Invoke();
                long fileSize;
                AsyncOperationHandle<long> getSizeOp = default;
                try
                {
                    getSizeOp = getSize();
                    while (!getSizeOp.IsDone)
                        await UniTask.Yield();
                    if (getSizeOp.Status != AsyncOperationStatus.Succeeded)
                        throw getSizeOp.OperationException ?? new System.Exception("Unable to get addressable download size.");
                    fileSize = getSizeOp.Result;
                }
                finally
                {
                    if (getSizeOp.IsValid())
                        Addressables.Release(getSizeOp);
                }

                onFileSizeRetrieved?.Invoke(fileSize);
                onDepsDownloading?.Invoke();
                if (fileSize > 0)
                {
                    AsyncOperationHandle downloadOp = default;
                    try
                    {
                        downloadOp = download();
                        while (!downloadOp.IsDone)
                        {
                            await UniTask.Yield();
                            if (!downloadOp.IsDone)
                            {
                                float percent = Mathf.Clamp01(downloadOp.GetDownloadStatus().Percent);
                                onDepsFileDownloading?.Invoke((long)(percent * fileSize), fileSize, percent);
                            }
                        }
                        if (downloadOp.Status != AsyncOperationStatus.Succeeded)
                            throw downloadOp.OperationException ?? new System.Exception("Unable to download addressable dependencies.");
                    }
                    finally
                    {
                        if (downloadOp.IsValid())
                            Addressables.Release(downloadOp);
                    }
                }

                onDepsFileDownloading?.Invoke(fileSize, fileSize, 1f);
                onDepsDownloaded?.Invoke();
            }
            catch (System.Exception ex)
            {
                onError?.Invoke(ex);
                throw;
            }
        }
    }
}
#endif
