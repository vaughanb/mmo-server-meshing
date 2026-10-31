using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenMMORPG.KitUpdater
{
    public enum KitChangeKind
    {
        /// <summary>The file's contents differ from the release.</summary>
        Edited,
        /// <summary>Only the <c>.meta</c> differs: import settings, labels, asset bundle.</summary>
        ImportSettings,
        /// <summary>The file or folder is somewhere else in the project.</summary>
        Moved,
        /// <summary>Nothing in the project has the GUID any more.</summary>
        Deleted,
    }

    /// <summary>One kit file or folder that is not as the release shipped it.</summary>
    public class KitChange
    {
        public KitChangeKind kind;
        public string guid;
        /// <summary>Where the release put it, relative to the kit folder.</summary>
        public string path;
        /// <summary>Where it is now, relative to the project; null when deleted.</summary>
        public string currentPath;
        public bool folder;
        /// <summary>For <see cref="KitChangeKind.Moved"/>: the contents were edited as well.</summary>
        public bool edited;
        /// <summary>A deleted or moved folder stands for the files that went with it, which get no row of their own.</summary>
        public int files;
    }

    public class KitScan
    {
        public KitManifest manifest;
        /// <summary>The kit folder, relative to the project: "Assets/OpenMMORPG".</summary>
        public string kitRoot;
        public readonly List<KitChange> changes = new List<KitChange>();
        /// <summary>Files exactly as the release shipped them, where it put them (or where a moved folder took them).</summary>
        public int unchanged;

        public int Count(KitChangeKind kind)
        {
            return changes.Count(c => c.kind == kind);
        }
    }

    /// <summary>What the scanner asks of the project. The editor's answers come from the AssetDatabase and the disk.</summary>
    public interface IKitProject
    {
        /// <summary>The project-relative path of the asset with this GUID, or null if there is none.</summary>
        string PathOf(string guid);
        /// <summary>See <see cref="KitHash"/>.</summary>
        string HashOf(string projectPath);
        /// <summary>The hash of the asset's <c>.meta</c>, or null if it has none.</summary>
        string MetaHashOf(string projectPath);
        /// <summary>Whether a file or folder is at the project-relative path.</summary>
        bool Exists(string projectPath);
    }

    /// <summary>Compares the installed kit with the manifest of the release it came from.</summary>
    public static class KitScanner
    {
        private class MovedFolder
        {
            public string path;
            public string currentPath;
            public KitChange row;
        }

        /// <param name="progress">Called now and then with the fraction of files done; returning true cancels, and Scan returns null.</param>
        public static KitScan Scan(KitManifest manifest, string kitRoot, IKitProject project, Func<float, bool> progress = null)
        {
            var scan = new KitScan { manifest = manifest, kitRoot = kitRoot };
            // Path order puts every folder before what is inside it.
            var entries = manifest.files.OrderBy(kv => kv.Value.path, StringComparer.Ordinal).ToList();
            var deletedFolders = new List<KitChange>();
            var movedFolders = new List<MovedFolder>();

            foreach (var kv in entries.Where(kv => kv.Value.folder))
            {
                KitManifestEntry entry = kv.Value;
                string now = project.PathOf(kv.Key);
                if (now == null)
                {
                    if (Within(deletedFolders, entry.path) != null)
                        continue;
                    var row = new KitChange { kind = KitChangeKind.Deleted, guid = kv.Key, path = entry.path, folder = true };
                    deletedFolders.Add(row);
                    scan.changes.Add(row);
                }
                else if (now != Expected(kitRoot, movedFolders, entry.path))
                {
                    var row = new KitChange { kind = KitChangeKind.Moved, guid = kv.Key, path = entry.path, currentPath = now, folder = true };
                    movedFolders.Add(new MovedFolder { path = entry.path, currentPath = now, row = row });
                    scan.changes.Add(row);
                }
                // A folder's .meta holds nothing a user edits, so it is not compared.
            }

            var files = entries.Where(kv => !kv.Value.folder).ToList();
            for (int i = 0; i < files.Count; ++i)
            {
                if (progress != null && i % 50 == 0 && progress((float)i / files.Count))
                    return null;
                string guid = files[i].Key;
                KitManifestEntry entry = files[i].Value;
                string now = project.PathOf(guid);
                if (now == null)
                {
                    KitChange folder = Within(deletedFolders, entry.path);
                    if (folder != null)
                        folder.files++;
                    else
                        scan.changes.Add(new KitChange { kind = KitChangeKind.Deleted, guid = guid, path = entry.path });
                    continue;
                }

                KitChange moved = null;
                if (now != Expected(kitRoot, movedFolders, entry.path))
                {
                    moved = new KitChange { kind = KitChangeKind.Moved, guid = guid, path = entry.path, currentPath = now };
                    scan.changes.Add(moved);
                }
                else
                {
                    MovedFolder carrier = Deepest(movedFolders, entry.path);
                    if (carrier != null)
                        carrier.row.files++;
                }

                bool edited = project.HashOf(now) != entry.hash;
                if (moved != null)
                    moved.edited = edited;
                else if (edited)
                    scan.changes.Add(new KitChange { kind = KitChangeKind.Edited, guid = guid, path = entry.path, currentPath = now });
                else if (project.MetaHashOf(now) != entry.meta)
                    scan.changes.Add(new KitChange { kind = KitChangeKind.ImportSettings, guid = guid, path = entry.path, currentPath = now });
                else
                    scan.unchanged++;
            }
            return scan;
        }

        /// <summary>Where a kit path should be now: under the kit folder, unless a folder above it was moved.</summary>
        private static string Expected(string kitRoot, List<MovedFolder> movedFolders, string path)
        {
            MovedFolder carrier = Deepest(movedFolders, path);
            return carrier == null ? kitRoot + "/" + path : carrier.currentPath + path.Substring(carrier.path.Length);
        }

        private static MovedFolder Deepest(List<MovedFolder> movedFolders, string path)
        {
            MovedFolder found = null;
            foreach (MovedFolder folder in movedFolders)
            {
                if (IsInside(path, folder.path) && (found == null || folder.path.Length > found.path.Length))
                    found = folder;
            }
            return found;
        }

        private static KitChange Within(List<KitChange> folders, string path)
        {
            return folders.FirstOrDefault(f => IsInside(path, f.path));
        }

        private static bool IsInside(string path, string folder)
        {
            return path.Length > folder.Length && path[folder.Length] == '/' && path.StartsWith(folder, StringComparison.Ordinal);
        }
    }
}
