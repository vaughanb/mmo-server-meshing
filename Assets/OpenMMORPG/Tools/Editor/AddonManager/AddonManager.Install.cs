/**
 * AddonManager.Install
 * Author: Denarii Games
 * Version: 1.0-rc1
 *
 * Install related functionality.
 */

using UnityEngine;
using UnityEditor;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using UnityEngine.Networking;

namespace OpenMMORPG.AddonManager
{
	public static class AddonInstallState
	{
		private const string PackageGuidKey = "AddonManager_PendingPackageGuid";
		private const string TargetFolderKey = "AddonManager_PendingPackageName";

		public static void SetPending(PackageInfo package)
		{
			EditorPrefs.SetString(PackageGuidKey, package.guid);
			EditorPrefs.SetString(TargetFolderKey, package.category + "/" + FolderNameUtility.MakeSafeFolderName(package.name));
		}

		public static bool HasPending => !string.IsNullOrEmpty(EditorPrefs.GetString(PackageGuidKey));
		public static string PackageGuid => EditorPrefs.GetString(PackageGuidKey);
		public static string TargetFolder => EditorPrefs.GetString(TargetFolderKey);

		public static void Clear()
		{
			EditorPrefs.DeleteKey(PackageGuidKey);
			EditorPrefs.DeleteKey(TargetFolderKey);
		}
	}

    public partial class AddonManagerWindow
    {
		/// <summary>
		/// Callback from button downloads and installs package from Github
		/// </summary>
		private void InstallOrUpdatePackage()
		{
			AddonInstallState.SetPending(selectedPackage);

			//is addon already installed?
			string[] guids = AssetDatabase.FindAssets(selectedPackage.guid);
			string action = "install";
			if (guids.Length > 0)
			{
				action = "reinstall";
				DeleteExistingAddonFolder();
			}

			AddonAnalytics.LogEvent(selectedPackage.guid, 
				("Action", action),
				("App version", selectedPackage.latestVersion)
			);

			uiDetailMessage = "Downloading addon...";
			string TempPath = $"Temp/{selectedPackage.name}.unitypackage";
			var www = UnityWebRequest.Get(selectedPackage.packageUrl);
			www.SendWebRequest().completed += _ =>
			{
				if (www.result == UnityWebRequest.Result.Success)
				{
					//download package
					Directory.CreateDirectory(Path.GetDirectoryName(TempPath));
					File.WriteAllBytes(TempPath, www.downloadHandler.data);

					//import package
					uiDetailMessage = "Importing addon...";
					ImportDownloadedAddon(TempPath);
				}
				else
				{
					Debug.LogError($"[AddonManager {Time.time}] {selectedPackage.name} installation failed from {selectedPackage.packageUrl}: {www.error}");
					uiDetailMessage = $"Addon installation failed: {www.error}";
				}
			};
		}

		/// <summary>
		/// Imports a downloaded addon archive.
		///
		/// ImportPackage is asynchronous, so the archive can only be deleted once Unity
		/// reports it is done; deleting it straight away raced the import and large
		/// addons never finished importing. Finishing the install from the same callback
		/// also covers addons that contain no scripts, which never trigger the domain
		/// reload that the pending state in OnEnable relies on.
		/// </summary>
		private void ImportDownloadedAddon(string tempPath)
		{
			AssetDatabase.ImportPackageCallback onCompleted = null;
			AssetDatabase.ImportPackageFailedCallback onFailed = null;
			AssetDatabase.ImportPackageCallback onCancelled = null;

			System.Action finish = () =>
			{
				AssetDatabase.importPackageCompleted -= onCompleted;
				AssetDatabase.importPackageFailed -= onFailed;
				AssetDatabase.importPackageCancelled -= onCancelled;
				try
				{
					if (File.Exists(tempPath))
						File.Delete(tempPath);
				}
				catch (System.Exception)
				{
					//a leftover file under Temp is harmless
				}
			};

			onCompleted = _ =>
			{
				finish();

				//the window survives when the addon has no scripts, so complete here
				if (AddonInstallState.HasPending)
				{
					//the pending state stays until CompleteInstall finishes, so a domain
					//reload from the addon's own scripts resumes it from OnEnable
					CompleteInstall(AddonInstallState.PackageGuid, ADDON_FOLDER + AddonInstallState.TargetFolder);
				}
			};

			onFailed = (name, error) =>
			{
				finish();
				AddonInstallState.Clear();
				Debug.LogError($"[AddonManager {Time.time}] failed to import {name}: {error}");
				uiDetailMessage = $"Addon import failed: {error}";
				Repaint();
			};

			onCancelled = _ =>
			{
				finish();
				AddonInstallState.Clear();
				uiDetailMessage = "Addon import cancelled.";
				Repaint();
			};

			AssetDatabase.importPackageCompleted += onCompleted;
			AssetDatabase.importPackageFailed += onFailed;
			AssetDatabase.importPackageCancelled += onCancelled;

			AssetDatabase.ImportPackage(tempPath, false);
		}

		/// <summary>
		/// Waits for an asset matching the filter to be indexed, up to a timeout. A large
		/// addon is still being imported and indexed well after the import callback, so a
		/// single fixed delay is not enough to find its marker file.
		/// </summary>
		private static async Task<string[]> FindAssetsWhenIndexed(string filter, double timeoutSeconds = 60d)
		{
			double deadline = EditorApplication.timeSinceStartup + timeoutSeconds;
			AssetDatabase.Refresh();

			while (true)
			{
				if (!EditorApplication.isUpdating && !EditorApplication.isCompiling)
				{
					string[] hits = AssetDatabase.FindAssets(filter);
					if (hits.Length > 0)
						return hits;
				}

				if (EditorApplication.timeSinceStartup > deadline)
					return new string[0];

				await Task.Delay(500);
			}
		}

		/// <summary>
		/// Callback from OnEnable after package import and script recompilation
		/// </summary>
		/// <param name="guid"></param>
		private async void CompleteInstall(string pendingGuid, string targetFolder)
		{
			//find asset with the pendingGuid to locate the imported folder
			string[] sourceGuids = await FindAssetsWhenIndexed(pendingGuid);
			if (sourceGuids.Length == 0)
			{
				Debug.LogError($"[AddonManager {Time.time}] no assets found with GUID filter: {pendingGuid}");
				return;
			}
		    string guidPath = AssetDatabase.GUIDToAssetPath(sourceGuids[0]);

			//get folder containing guid file
			string importFolder = Path.GetDirectoryName(guidPath).Replace("\\", "/");
			if (!AssetDatabase.IsValidFolder(importFolder))
			{
				Debug.LogError($"[AddonManager {Time.time}] import folder not found: {importFolder}");
				return;
			}

			//ensure targetFolder exists
			if (!AssetDatabase.IsValidFolder(targetFolder))
			{
				CreateFolderHierarchy(targetFolder);
                await Task.Delay(1000);
			}

			//move all assets from source to target
			string[] guids = AssetDatabase.FindAssets("", new[] { importFolder });
			foreach (string guid in guids)
			{
				string oldPath = AssetDatabase.GUIDToAssetPath(guid);
				string relative;
				if (oldPath.Contains(targetFolder))
					relative = oldPath.Substring(targetFolder.Length);
				else
					relative = oldPath.Substring(importFolder.Length);
				string newPath = targetFolder + relative;

				if (!string.Equals(newPath, oldPath))
				{
					//create nested directories if needed
					string newDir = Path.GetDirectoryName(newPath);
					if (!AssetDatabase.IsValidFolder(newDir))
					{
						CreateFolderHierarchy(newDir);
						await Task.Delay(1000);
					}
					
					string error = AssetDatabase.MoveAsset(oldPath, newPath);
					if (!string.IsNullOrEmpty(error))
					{
						Debug.LogError($"[AddonManager {Time.time}] failed to move {oldPath} → {newPath}: {error}");
					}
				}
			}

			//clean up empty source folder
			if (AssetDatabase.IsValidFolder(importFolder))
			{
				string[] remaining = AssetDatabase.FindAssets("", new[] { importFolder });
				if (remaining.Length == 0)
				{
					AssetDatabase.DeleteAsset(importFolder);
					//Debug.Log($"[AddonManager {Time.time}] cleaned up empty source folder: {importFolder}");
				}
			}

			//and any empty parents it left behind, such as the folder an addon was
			//packaged under, which is not necessarily the folder addons install into
			string parent = Path.GetDirectoryName(importFolder)?.Replace("\\", "/");
			while (!string.IsNullOrEmpty(parent)
				&& parent != "Assets"
				&& !targetFolder.StartsWith(parent + "/")
				&& AssetDatabase.IsValidFolder(parent)
				&& AssetDatabase.FindAssets("", new[] { parent }).Length == 0)
			{
				AssetDatabase.DeleteAsset(parent);
				parent = Path.GetDirectoryName(parent)?.Replace("\\", "/");
			}

			//final refresh to make sure everything is synced
			//AssetDatabase.Refresh();

			await Task.Delay(3000);
			if (packages.Count > 0)
			{
				PackageInfo installedPackage = packages.FirstOrDefault(p => p.guid == pendingGuid);
				if (installedPackage != null)
				{
					selectedPackage = installedPackage;
					uiDetailMessage = $"{selectedPackage.name} installed!";
					Repaint();

					//re-find the guid file
					string[] postInstallGuids = await FindAssetsWhenIndexed(pendingGuid, 30d);
					if (postInstallGuids.Length == 0)
					{
						Debug.LogError($"[AddonManager {Time.time}] no installed assets found with GUID filter: {pendingGuid}");
					}
					else
					{
						//write version to guid file
						string installedGuidPath = AssetDatabase.GUIDToAssetPath(postInstallGuids[0]);
						//Debug.Log($"[AddonManager {Time.time}] writing version {selectedPackage?.latestVersion} to {installedGuidPath}");
						try
						{
							File.WriteAllText(installedGuidPath, selectedPackage?.latestVersion);
							AssetDatabase.ImportAsset(installedGuidPath);
						}
						catch (System.Exception e)
						{
							Debug.LogError($"[AddonManager {Time.time}] failed to update version file: {e.Message}");
						}
					}
				}
			}
			else
			{
				Debug.LogError($"[AddonManager {Time.time}] failed to get installed packages");
			}

			//only now is the install finished. Clearing the pending state earlier meant
			//an addon whose scripts trigger a domain reload part way through lost the
			//rest of the install, because the state that resumes it was already gone.
			AddonInstallState.Clear();
		}

		/// <summary>
		/// Recursively create folders from path
		/// </summary>
		/// <param name="path"></param>
		private static void CreateFolderHierarchy(string path)
		{
			if (AssetDatabase.IsValidFolder(path)) return;

			string parent = Path.GetDirectoryName(path).Replace("\\", "/");
			string folderName = Path.GetFileName(path);

			CreateFolderHierarchy(parent);

			AssetDatabase.CreateFolder(parent, folderName);
			AssetDatabase.Refresh();
		}
	}
}