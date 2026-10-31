using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace OpenMMORPG.KitUpdater
{
    /// <summary>
    /// One render pipeline's version of the demo's content, read from a release's <c>Tools/Install/Pipelines</c>: the files in
    /// its archive (same GUIDs as the kit's own, so they stand in for them) and the kit files it removes.
    ///
    /// The kit manifest describes the kit as released, which is the URP version. A project the pipeline installer switched to
    /// HDRP holds the HDRP archive's files instead, so compared with the manifest alone every one of them would look edited
    /// and every URP-only file deleted, and an update would write URP files into it. <see cref="Apply"/> makes the manifest
    /// describe the project's version instead; the scanner and the planner then need nothing else.
    /// </summary>
    public class KitPipelineContent
    {
        /// <summary>Where the pipeline manifest and archives sit, relative to the kit folder.</summary>
        public const string Folder = "Tools/Install/Pipelines";
        public const string ManifestName = "pipelines.json";

        /// <summary>"hdrp".</summary>
        public string id;
        /// <summary>"High Definition Render Pipeline (HDRP)".</summary>
        public string name;
        /// <summary>GUID to entry: every file in the archive, with its hashes as the kit manifest records them.</summary>
        public readonly Dictionary<string, KitManifestEntry> files = new Dictionary<string, KitManifestEntry>();
        /// <summary>Kit-relative paths of the kit's files that this version does without.</summary>
        public readonly HashSet<string> remove = new HashSet<string>();
        /// <summary>Kit-relative path (a <c>.meta</c> as <c>path + ".meta"</c>) to bytes: the archive's files, and the pipeline
        /// manifest and archive themselves, which are kit files too.</summary>
        public readonly Dictionary<string, byte[]> data = new Dictionary<string, byte[]>();

        /// <summary>The pipelines a release has content for, from its <c>pipelines.json</c>: id to archive file name.</summary>
        public static Dictionary<string, string> Packages(string pipelinesJson)
        {
            var packages = new Dictionary<string, string>();
            foreach (JToken pipeline in JObject.Parse(pipelinesJson)["pipelines"] ?? new JArray())
                packages[(string)pipeline["id"]] = (string)pipeline["package"];
            return packages;
        }

        /// <param name="pipelinesJson">The release's <c>pipelines.json</c>.</param>
        /// <param name="archive">The release's archive for <paramref name="id"/>.</param>
        public static KitPipelineContent Read(string pipelinesJson, byte[] archive, string id)
        {
            JObject manifest = JObject.Parse(pipelinesJson);
            JToken pipeline = (manifest["pipelines"] ?? new JArray()).FirstOrDefault(p => (string)p["id"] == id);
            if (pipeline == null)
                throw new FormatException(ManifestName + " has no pipeline \"" + id + "\"");
            string prefix = ((string)manifest["prefix"] ?? "Assets/OpenMMORPG").TrimEnd('/') + "/";

            var content = new KitPipelineContent { id = id, name = (string)pipeline["name"] ?? id };
            foreach (JToken path in pipeline["remove"] ?? new JArray())
                content.remove.Add((string)path);
            content.data[Folder + "/" + ManifestName] = Encoding.UTF8.GetBytes(pipelinesJson);
            content.data[Folder + "/" + (string)pipeline["package"]] = archive;

            // A .unitypackage is a gzipped tar of <guid>/pathname, <guid>/asset and <guid>/asset.meta.
            var entries = new Dictionary<string, Dictionary<string, byte[]>>();
            foreach (var file in Untar(Gunzip(archive)))
            {
                int slash = file.Key.IndexOf('/');
                if (slash <= 0)
                    continue;
                string guid = file.Key.Substring(0, slash);
                if (!entries.TryGetValue(guid, out var parts))
                    entries[guid] = parts = new Dictionary<string, byte[]>();
                parts[file.Key.Substring(slash + 1)] = file.Value;
            }
            foreach (var entry in entries)
            {
                if (!entry.Value.TryGetValue("pathname", out byte[] pathname))
                    continue;
                string path = Encoding.UTF8.GetString(pathname).Split('\n')[0].Trim();
                if (!path.StartsWith(prefix, StringComparison.Ordinal))
                    continue;
                path = path.Substring(prefix.Length);
                entry.Value.TryGetValue("asset", out byte[] asset);
                entry.Value.TryGetValue("asset.meta", out byte[] meta);
                var manifestEntry = new KitManifestEntry { path = path, folder = asset == null };
                if (asset != null)
                {
                    content.data[path] = asset;
                    manifestEntry.size = asset.Length;
                    manifestEntry.hash = KitHash.Of((byte[])asset.Clone());
                }
                if (meta != null)
                {
                    content.data[path + ".meta"] = meta;
                    manifestEntry.meta = KitHash.Of((byte[])meta.Clone());
                }
                content.files[entry.Key] = manifestEntry;
            }
            return content;
        }

        /// <summary>The manifest as it describes a kit holding this version: its files in place of the kit's, its removals gone.</summary>
        public KitManifest Apply(KitManifest manifest)
        {
            var applied = new KitManifest
            {
                format = manifest.format,
                version = manifest.version,
                tag = manifest.tag,
                unity = manifest.unity,
                dependencies = manifest.dependencies,
                files = new Dictionary<string, KitManifestEntry>(),
            };
            foreach (var kv in manifest.files)
            {
                if (!remove.Contains(kv.Value.path))
                    applied.files[kv.Key] = kv.Value;
            }
            foreach (var kv in files)
                applied.files[kv.Key] = kv.Value;
            return applied;
        }

        /// <summary>
        /// Whether the project holds this version rather than the kit's own: more of the files the two versions disagree on are
        /// as this one has them than as the kit has them. A file the user edited counts for neither.
        /// </summary>
        public bool IsInstalled(KitManifest manifest, IKitProject project)
        {
            int mine = 0, kits = 0;
            foreach (var kv in files.Where(f => !f.Value.folder))
            {
                manifest.files.TryGetValue(kv.Key, out KitManifestEntry kit);
                if (kit != null && kit.hash == kv.Value.hash)
                    continue;
                string now = project.PathOf(kv.Key);
                if (now == null)
                    continue;
                string hash = project.HashOf(now);
                if (hash == kv.Value.hash)
                    mine++;
                else if (kit != null && hash == kit.hash)
                    kits++;
            }
            return mine > kits;
        }

        private static byte[] Gunzip(byte[] data)
        {
            using (var input = new GZipStream(new MemoryStream(data), CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                input.CopyTo(output);
                return output.ToArray();
            }
        }

        /// <summary>The regular files of a tar archive (ustar, with GNU long names and pax paths), name to bytes.</summary>
        private static IEnumerable<KeyValuePair<string, byte[]>> Untar(byte[] tar)
        {
            string longName = null;
            for (int at = 0; at + 512 <= tar.Length;)
            {
                if (tar[at] == 0)
                    yield break; // the zero blocks that end the archive
                string name = Field(tar, at, 100);
                long size = Convert.ToInt64(Field(tar, at + 124, 12).Trim().PadLeft(1, '0'), 8);
                char type = (char)tar[at + 156];
                // Only POSIX ustar has a name prefix there; GNU tar ("ustar  ") keeps other fields in its place.
                string prefix = Field(tar, at + 257, 6) == "ustar" && tar[at + 262] == 0 ? Field(tar, at + 345, 155) : "";
                if (prefix.Length > 0)
                    name = prefix + "/" + name;
                int body = at + 512;
                at = body + (int)((size + 511) / 512 * 512);
                if (type == 'L')
                {
                    longName = Encoding.UTF8.GetString(tar, body, (int)size).TrimEnd('\0');
                    continue;
                }
                if (type == 'x')
                {
                    foreach (string record in Encoding.UTF8.GetString(tar, body, (int)size).Split('\n'))
                    {
                        int equals = record.IndexOf(" path=", StringComparison.Ordinal);
                        if (equals >= 0)
                            longName = record.Substring(equals + 6);
                    }
                    continue;
                }
                if (longName != null)
                {
                    name = longName;
                    longName = null;
                }
                if (type != '0' && type != '\0')
                    continue;
                var bytes = new byte[size];
                Buffer.BlockCopy(tar, body, bytes, 0, (int)size);
                yield return new KeyValuePair<string, byte[]>(name.TrimStart('.', '/'), bytes);
            }
        }

        private static string Field(byte[] tar, int at, int length)
        {
            int end = at;
            while (end < at + length && tar[end] != 0)
                end++;
            return Encoding.UTF8.GetString(tar, at, end - at);
        }
    }

    /// <summary>Finds the pipeline content a project holds, and the same pipeline's content in a release it updates to.</summary>
    public static class KitPipeline
    {
        /// <summary>
        /// The pipeline version the installed kit holds other than the kit's own, read from the kit's own
        /// <c>Tools/Install/Pipelines</c>, or null if it holds the kit's own (or the release has no pipeline content).
        /// </summary>
        public static KitPipelineContent Installed(KitManifest installed, string kitRoot, IKitProject project)
        {
            string folder = kitRoot + "/" + KitPipelineContent.Folder + "/";
            string manifestPath = KitPath.Disk(folder + KitPipelineContent.ManifestName);
            if (!File.Exists(manifestPath))
                return null;
            string json = File.ReadAllText(manifestPath);
            foreach (var package in KitPipelineContent.Packages(json))
            {
                string archive = KitPath.Disk(folder + package.Value);
                if (!File.Exists(archive))
                    continue;
                KitPipelineContent content = KitPipelineContent.Read(json, File.ReadAllBytes(archive), package.Key);
                if (content.IsInstalled(installed, project))
                    return content;
            }
            return null;
        }

        /// <summary>The same pipeline's content in the release <paramref name="source"/> offers, fetched from it.</summary>
        public static KitPipelineContent InTarget(IKitSource source, string id)
        {
            string folder = KitPipelineContent.Folder + "/";
            string json = Encoding.UTF8.GetString(Fetch(source, folder + KitPipelineContent.ManifestName));
            if (!KitPipelineContent.Packages(json).TryGetValue(id, out string package))
                throw new IOException(source.Manifest.version + " has no " + id.ToUpperInvariant() + " content, so updating to it would put the " +
                                      "kit's own render pipeline content into this project.");
            return KitPipelineContent.Read(json, Fetch(source, folder + package), id);
        }

        private static byte[] Fetch(IKitSource source, string path)
        {
            return Task.Run(() => source.FetchAsync(path, CancellationToken.None)).GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// A release as a project on another pipeline sees it: the manifest with that pipeline's content applied, and that
    /// content's files served from the release's archive. The manifest written as installed stays the release's own.
    /// </summary>
    public class PipelineKitSource : IKitSource
    {
        private readonly IKitSource _release;
        private readonly KitPipelineContent _content;

        public PipelineKitSource(IKitSource release, KitPipelineContent content)
        {
            _release = release;
            _content = content;
            Manifest = content.Apply(release.Manifest);
        }

        public string Description { get { return _release.Description + ", " + _content.id.ToUpperInvariant() + " content"; } }
        public KitManifest Manifest { get; private set; }
        public string ManifestText { get { return _release.ManifestText; } }

        public Task<byte[]> FetchAsync(string kitRelativePath, CancellationToken cancel)
        {
            return _content.data.TryGetValue(kitRelativePath, out byte[] bytes)
                ? Task.FromResult((byte[])bytes.Clone())
                : _release.FetchAsync(kitRelativePath, cancel);
        }

        public string CompareUrl(string fromTag)
        {
            return _release.CompareUrl(fromTag);
        }
    }
}
