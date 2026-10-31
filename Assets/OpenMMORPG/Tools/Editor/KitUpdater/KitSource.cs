using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace OpenMMORPG.KitUpdater
{
    /// <summary>Where a target release comes from: its manifest, and each of its files by kit-relative path.</summary>
    public interface IKitSource
    {
        /// <summary>For the window: "v1.1.6 from GitHub".</summary>
        string Description { get; }
        KitManifest Manifest { get; }
        /// <summary>The manifest as published, written into the kit once the update is applied.</summary>
        string ManifestText { get; }
        /// <summary>A file's bytes (a <c>.meta</c> is <c>path + ".meta"</c>). Safe to call from several threads at once.</summary>
        Task<byte[]> FetchAsync(string kitRelativePath, CancellationToken cancel);
        /// <summary>A page showing what changed since the installed tag, or null.</summary>
        string CompareUrl(string fromTag);
    }

    /// <summary>A published release on GitHub, with the kit manifest attached.</summary>
    public class KitRelease
    {
        public string tag;
        public string name;
        public bool prerelease;
        public DateTime published;
    }

    /// <summary>
    /// Releases of the kit repository on GitHub. The manifest is a release asset; files come from the tagged tree, which
    /// is exactly what the release archive was built from.
    /// </summary>
    public class GitHubKitSource : IKitSource
    {
        public const string Repository = "open-mmorpg/OpenMMORPG";
        private const string ManifestAsset = "kit-manifest.json";

        private static readonly HttpClient Http = CreateClient();
        private readonly string _tag;

        public string Description { get { return _tag + " from GitHub"; } }
        public KitManifest Manifest { get; private set; }
        public string ManifestText { get; private set; }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            // GitHub's API refuses requests without one.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("OpenMMORPG-KitUpdater");
            return client;
        }

        /// <summary>Published releases that carry a kit manifest, newest first. One API call; GitHub allows 60 an hour unsigned.</summary>
        public static List<KitRelease> ListReleases()
        {
            string json = Run(() => Http.GetStringAsync("https://api.github.com/repos/" + Repository + "/releases?per_page=30"));
            var releases = new List<KitRelease>();
            foreach (JToken release in JArray.Parse(json))
            {
                if ((bool)release["draft"])
                    continue;
                if (!release["assets"].Any(a => (string)a["name"] == ManifestAsset))
                    continue;
                releases.Add(new KitRelease
                {
                    tag = (string)release["tag_name"],
                    name = (string)release["name"],
                    prerelease = (bool)release["prerelease"],
                    published = (DateTime?)release["published_at"] ?? DateTime.MinValue,
                });
            }
            return releases.OrderByDescending(r => r.published).ToList();
        }

        public GitHubKitSource(string tag)
        {
            _tag = tag;
            ManifestText = Run(async () =>
            {
                using (HttpResponseMessage response = await Http.GetAsync("https://github.com/" + Repository + "/releases/download/" + tag + "/" + ManifestAsset).ConfigureAwait(false))
                {
                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                        throw new IOException(tag + " has no " + ManifestAsset + ". Releases made before the updater do not carry one, so they cannot be updated to from here.");
                    response.EnsureSuccessStatusCode();
                    return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
            });
            Manifest = KitManifest.Parse(ManifestText);
        }

        public async Task<byte[]> FetchAsync(string kitRelativePath, CancellationToken cancel)
        {
            string path = string.Join("/", kitRelativePath.Split('/').Select(Uri.EscapeDataString));
            using (HttpResponseMessage response = await Http.GetAsync("https://raw.githubusercontent.com/" + Repository + "/" + _tag + "/" + path, cancel).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                    throw new IOException(kitRelativePath + ": HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase);
                return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            }
        }

        public string CompareUrl(string fromTag)
        {
            return string.IsNullOrEmpty(fromTag) ? null : "https://github.com/" + Repository + "/compare/" + fromTag + "..." + _tag;
        }

        /// <summary>Waits for a request off the main thread, so Unity's synchronization context cannot deadlock it.</summary>
        private static T Run<T>(Func<Task<T>> request)
        {
            try
            {
                return Task.Run(request).GetAwaiter().GetResult();
            }
            catch (HttpRequestException ex)
            {
                throw new IOException("GitHub request failed: " + ex.Message, ex);
            }
            catch (TaskCanceledException)
            {
                throw new IOException("GitHub did not answer in time.");
            }
        }
    }

    /// <summary>
    /// A kit on disk: a folder holding <c>kit-manifest.json</c> and the kit's files at their kit-relative paths, such as a
    /// checkout of the kit after <c>build_unitypackage.py --manifest-out &lt;checkout&gt;/kit-manifest.json</c>. For trying
    /// a release before it is published, and for updating without GitHub.
    /// </summary>
    public class FolderKitSource : IKitSource
    {
        public const string ManifestFile = "kit-manifest.json";
        private readonly string _folder;

        public string Description { get { return Manifest.version + " from " + _folder; } }
        public KitManifest Manifest { get; private set; }
        public string ManifestText { get; private set; }

        public FolderKitSource(string folder)
        {
            _folder = folder;
            string manifest = Path.Combine(folder, ManifestFile);
            if (!File.Exists(manifest))
                throw new IOException("There is no " + ManifestFile + " in " + folder + ".");
            ManifestText = File.ReadAllText(manifest);
            Manifest = KitManifest.Parse(ManifestText);
        }

        public Task<byte[]> FetchAsync(string kitRelativePath, CancellationToken cancel)
        {
            return Task.Run(() => File.ReadAllBytes(KitPath.Disk(Path.Combine(_folder, kitRelativePath))), cancel);
        }

        public string CompareUrl(string fromTag)
        {
            return null;
        }
    }
}
