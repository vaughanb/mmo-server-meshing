using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEditor;

namespace OpenMMORPG.KitUpdater
{
    /// <summary>The installed kit: its manifest, its folder, and the project as the scanner sees it.</summary>
    public static class KitInstall
    {
        public const string MenuRoot = "Tools/Open MMORPG/Update/";

        /// <summary>The installed release's manifest, or null with the reason in <paramref name="problem"/>.</summary>
        /// <param name="kitRoot">The kit folder, relative to the project: "Assets/OpenMMORPG".</param>
        public static KitManifest Load(out string kitRoot, out string problem)
        {
            kitRoot = null;
            problem = null;
            string path = AssetDatabase.GUIDToAssetPath(KitManifest.Guid);
            if (string.IsNullOrEmpty(path) || !File.Exists(KitPath.Disk(path)))
            {
                problem = "This kit has no " + Path.GetFileName(KitManifest.KitRelativePath) + ", the record of the release it was " +
                          "installed from, so there is nothing to compare it with. Kits installed from older releases do not have " +
                          "one; importing a newer release adds it. A git checkout of the kit never has one: use git status there.";
                return null;
            }
            string suffix = "/" + KitManifest.KitRelativePath;
            if (!path.EndsWith(suffix, StringComparison.Ordinal))
            {
                problem = "The kit manifest is at " + path + ", but it belongs at " + KitManifest.KitRelativePath +
                          " inside the kit folder. Move it back to compare the kit with its release.";
                return null;
            }
            kitRoot = path.Substring(0, path.Length - suffix.Length);
            try
            {
                return KitManifest.Parse(File.ReadAllText(KitPath.Disk(path)));
            }
            catch (Exception ex)
            {
                problem = "Could not read " + path + ": " + ex.Message;
                return null;
            }
        }
    }

    /// <summary>The project as it is on disk, through the AssetDatabase's GUIDs. Call <see cref="SaveCache"/> after a scan.</summary>
    public class AssetDatabaseKitProject : IKitProject
    {
        private readonly KitHashCache _cache = KitHashCache.Load();

        public void SaveCache()
        {
            _cache.Save();
        }

        public string PathOf(string guid)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path))
                return null;
            // The AssetDatabase can still answer for an asset deleted outside Unity that it has not noticed yet.
            string full = KitPath.Disk(path);
            return File.Exists(full) || Directory.Exists(full) ? path : null;
        }

        public string HashOf(string projectPath)
        {
            return _cache.HashOf(projectPath);
        }

        public string MetaHashOf(string projectPath)
        {
            return _cache.HashOf(projectPath + ".meta");
        }

        public bool Exists(string projectPath)
        {
            string full = KitPath.Disk(projectPath);
            return File.Exists(full) || Directory.Exists(full);
        }
    }

    /// <summary>
    /// Each file's hash with the size and modification time it had, so a rescan reads only the files that changed since the
    /// last one, the way git's index does. Hashing the whole kit reads about 400 MB; with the cache warm a scan takes seconds.
    /// Kept in Library, which is per machine and never committed.
    /// </summary>
    public class KitHashCache
    {
        private const string CachePath = "Library/OpenMMORPGUpdater/hashes.json";

        private class Entry
        {
            public long length;
            public long ticks;
            public string hash;
        }

        private Dictionary<string, Entry> _entries = new Dictionary<string, Entry>();
        private readonly Dictionary<string, Entry> _used = new Dictionary<string, Entry>();
        private bool _changed;

        public static KitHashCache Load()
        {
            var cache = new KitHashCache();
            try
            {
                if (File.Exists(CachePath))
                    cache._entries = JsonConvert.DeserializeObject<Dictionary<string, Entry>>(File.ReadAllText(CachePath)) ?? cache._entries;
            }
            catch (Exception)
            {
                // A damaged cache only costs a full rehash.
            }
            return cache;
        }

        /// <summary>The file's hash (see <see cref="KitHash"/>), or null if there is no such file.</summary>
        public string HashOf(string projectPath)
        {
            string disk = KitPath.Disk(projectPath);
            var info = new FileInfo(disk);
            if (!info.Exists)
                return null;
            long ticks = info.LastWriteTimeUtc.Ticks;
            if (!_entries.TryGetValue(projectPath, out Entry entry) || entry.length != info.Length || entry.ticks != ticks)
            {
                entry = new Entry { length = info.Length, ticks = ticks, hash = KitHash.OfFile(disk) };
                _changed = true;
            }
            _used[projectPath] = entry;
            return entry.hash;
        }

        /// <summary>Writes what this scan hashed; files it did not look at are forgotten.</summary>
        public void Save()
        {
            if (!_changed && _used.Count == _entries.Count)
                return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath));
                File.WriteAllText(CachePath, JsonConvert.SerializeObject(_used));
                _entries = new Dictionary<string, Entry>(_used);
                _changed = false;
            }
            catch (Exception)
            {
                // Not saving only costs the next scan its speed.
            }
        }
    }
}
