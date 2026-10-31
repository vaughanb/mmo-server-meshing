using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace OpenMMORPG.KitUpdater
{
    /// <summary>
    /// Asks GitHub, at most once a day when the editor starts, whether a newer kit release is out. If there is one it says
    /// so in one Console line and ticks Tools &gt; Open MMORPG &gt; Update &gt; Check for Updates; nothing opens and nothing
    /// is downloaded. One unsigned call to the public releases list; nothing about the user or the project is sent.
    ///
    /// Off until the user turns it on: the Check for Updates window asks once, and the box at its foot changes it later (per
    /// project, in EditorUserSettings). The Asset Store wants anything that reaches the network unasked to be opt-in
    /// (submission guideline 1.5.e asks it of analytics; this sends nothing, but is held to the same standard). Never runs
    /// in batch mode, in a kit with no manifest (a git checkout, or one installed from an older release), or for a test build.
    /// </summary>
    [InitializeOnLoad]
    public static class KitUpdateCheck
    {
        private const string SettingKey = "OpenMMORPG.KitUpdater.DailyCheck";
        private const string SessionKey = "OpenMMORPG.KitUpdater.CheckedThisSession";
        private const string CachePath = "Library/OpenMMORPGUpdater/check.json";
        public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

        /// <summary>What the last check found.</summary>
        public class Result
        {
            public long checkedTicks;
            /// <summary>The newest published release that carries a manifest, or null.</summary>
            public string latestTag;
            public string error;

            [JsonIgnore]
            public DateTime Checked { get { return new DateTime(checkedTicks, DateTimeKind.Utc); } }
        }

        private static Task<List<KitRelease>> s_request;
        private static string s_installedVersion;

        static KitUpdateCheck()
        {
            // Not a delayCall: a fresh project reloads several times before it settles (see the pipeline installer's launcher).
            EditorApplication.update += WhenSettled;
        }

        public static bool Enabled
        {
            get { return EditorUserSettings.GetConfigValue(SettingKey) == "on"; }
            set { EditorUserSettings.SetConfigValue(SettingKey, value ? "on" : "off"); }
        }

        /// <summary>Whether the user has answered, either way; until then the window asks.</summary>
        public static bool Asked
        {
            get
            {
                string value = EditorUserSettings.GetConfigValue(SettingKey);
                return value == "on" || value == "off";
            }
        }

        public static Result Last()
        {
            try
            {
                return File.Exists(CachePath) ? JsonConvert.DeserializeObject<Result>(File.ReadAllText(CachePath)) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The newer release the last check found for this version, or null.</summary>
        public static string NewerThan(string installedVersion)
        {
            Result last = Last();
            return last != null && IsNewer(last.latestTag, installedVersion) ? last.latestTag : null;
        }

        /// <summary>Whether a release tag ("v1.2.0") is a later version than an installed one ("1.1.5"). A test build is never behind.</summary>
        public static bool IsNewer(string tag, string installedVersion)
        {
            return KitVersion.Compare(tag, installedVersion) > 0;
        }

        /// <summary>Whether a check is due: none yet, or the last one (found something or failed) is a day old or dated in the future.</summary>
        public static bool Due(Result last, DateTime now)
        {
            return last == null || now - last.Checked >= Interval || last.Checked > now;
        }

        private static void WhenSettled()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            EditorApplication.update -= WhenSettled;
            if (Application.isBatchMode)
                return;
            KitManifest installed = KitInstall.Load(out string kitRoot, out string problem);
            s_installedVersion = installed == null || installed.tag == null ? null : installed.version;
            Mark();
            if (s_installedVersion == null || !Enabled || SessionState.GetBool(SessionKey, false) || !Due(Last(), DateTime.UtcNow))
                return;
            SessionState.SetBool(SessionKey, true);
            CheckNow();
        }

        /// <summary>Asks GitHub now, off the main thread; the window's button and the daily check both come here.</summary>
        public static void CheckNow()
        {
            if (s_request != null && !s_request.IsCompleted)
                return;
            s_request = Task.Run(() => GitHubKitSource.ListReleases());
            EditorApplication.update += WhenAnswered;
        }

        private static void WhenAnswered()
        {
            if (!s_request.IsCompleted)
                return;
            EditorApplication.update -= WhenAnswered;
            var result = new Result { checkedTicks = DateTime.UtcNow.Ticks };
            if (s_request.IsFaulted)
                result.error = s_request.Exception.GetBaseException().Message;
            else
                result.latestTag = s_request.Result.Where(r => !r.prerelease).Select(r => r.tag).FirstOrDefault();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath));
                File.WriteAllText(CachePath, JsonConvert.SerializeObject(result));
            }
            catch (Exception)
            {
                // Not saving only means asking again at the next start.
            }
            if (s_installedVersion != null && IsNewer(result.latestTag, s_installedVersion))
                Debug.Log("Open MMORPG " + result.latestTag + " is out (you have " + s_installedVersion + "). " +
                          "Tools > Open MMORPG > Update > Check for Updates shows what it changes before anything is touched.");
            Mark();
            KitUpdateWindow.RepaintIfOpen();
        }

        /// <summary>Ticks the Check for Updates menu item while a newer release is waiting.</summary>
        private static void Mark()
        {
            bool newer = s_installedVersion != null && NewerThan(s_installedVersion) != null;
            Menu.SetChecked(KitUpdateWindow.MenuPath, newer);
        }
    }
}
