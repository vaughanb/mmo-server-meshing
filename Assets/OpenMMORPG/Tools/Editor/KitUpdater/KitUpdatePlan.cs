using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenMMORPG.KitUpdater
{
    public enum KitActionKind
    {
        /// <summary>Changed upstream and not edited: overwritten (contents and/or .meta), or moved where upstream moved it.</summary>
        Update,
        /// <summary>New in the target release.</summary>
        Add,
        /// <summary>Removed upstream and not edited.</summary>
        Delete,
        /// <summary>Changed upstream and edited by the user: kept, unless the user chooses to overwrite it.</summary>
        EditedByYou,
        /// <summary>Removed upstream but edited by the user: kept.</summary>
        KeptDeleted,
        /// <summary>Added upstream at a path where a file of the user's already is: skipped.</summary>
        PathTaken,
        /// <summary>Inside a kit folder the user deleted: skipped, as the folder stays deleted.</summary>
        OptedOut,
    }

    /// <summary>What an update does to one kit file or folder.</summary>
    public class KitAction
    {
        public KitActionKind kind;
        public string guid;
        /// <summary>Relative to the kit folder: the target release's path, or the installed one's for a removal.</summary>
        public string path;
        public bool folder;
        /// <summary>Where it is now, relative to the project; null if it is not in the project.</summary>
        public string currentPath;
        /// <summary>Where it ends up; differs from <see cref="currentPath"/> when it moves.</summary>
        public string targetPath;
        /// <summary>Write the target release's contents.</summary>
        public bool content;
        /// <summary>Write the target release's .meta.</summary>
        public bool meta;
        /// <summary>The download, in bytes, for <see cref="content"/>.</summary>
        public long size;
        /// <summary><see cref="KitActionKind.EditedByYou"/> only: the user chose to overwrite their edit.</summary>
        public bool overwrite;

        public bool Moves { get { return currentPath != null && targetPath != null && currentPath != targetPath; } }

        /// <summary>Whether applying the update touches this file at all.</summary>
        public bool Applies
        {
            get
            {
                switch (kind)
                {
                    case KitActionKind.Update:
                    case KitActionKind.Add:
                    case KitActionKind.Delete:
                        return true;
                    case KitActionKind.EditedByYou:
                        return overwrite || Moves || meta;
                    default:
                        return false;
                }
            }
        }

        /// <summary>The target release's contents are written: an update, an add, or an edit the user chose to overwrite.</summary>
        public bool WritesContent { get { return content && (kind != KitActionKind.EditedByYou || overwrite); } }
    }

    /// <summary>
    /// What updating from the installed release to a target release would do, file by file. Built from the two releases'
    /// manifests and the project as it is; changes nothing.
    ///
    /// A file is overwritten only when it changed upstream and the user has not edited it. A file the user edited is kept
    /// (they may choose to overwrite it), a file the user deleted stays deleted, and nothing goes into a kit folder the
    /// user deleted. A file upstream moved or renamed is moved too, unless the user had moved it themselves; Unity matches
    /// a MonoBehaviour to its file name, so a renamed script has to be renamed, not only rewritten.
    /// </summary>
    public class KitUpdatePlan
    {
        public KitManifest installed;
        public KitManifest target;
        public string kitRoot;
        public readonly List<KitAction> actions = new List<KitAction>();
        /// <summary>Files that are already as the target release has them.</summary>
        public int unchanged;

        public IEnumerable<KitAction> Of(KitActionKind kind)
        {
            return actions.Where(a => a.kind == kind);
        }

        public long DownloadSize
        {
            get { return actions.Where(a => a.WritesContent).Sum(a => a.size); }
        }

        /// <summary>Packages the target release needs at a version the installed one did not ask for: name to version.</summary>
        public Dictionary<string, string> PackageChanges()
        {
            var changes = new Dictionary<string, string>();
            if (target.dependencies == null)
                return changes;
            foreach (var dep in target.dependencies)
            {
                string before = null;
                if (installed.dependencies == null || !installed.dependencies.TryGetValue(dep.Key, out before) || before != dep.Value)
                    changes[dep.Key] = dep.Value;
            }
            return changes;
        }

        /// <summary>
        /// Of <see cref="PackageChanges"/>, the packages the project does not have at that version or newer: name to the
        /// version needed. The updater never adds them itself (Asset Store guideline 2.5.1.e: an asset must not add, update
        /// or remove packages in the user's project), so the user adds them before updating.
        /// </summary>
        /// <param name="projectPackages">Name to version of every package the project has.</param>
        public Dictionary<string, string> UnmetPackages(IDictionary<string, string> projectPackages)
        {
            var unmet = new Dictionary<string, string>();
            foreach (var need in PackageChanges())
            {
                if (!projectPackages.TryGetValue(need.Key, out string have) || !(KitVersion.Compare(have, need.Value) >= 0))
                    unmet[need.Key] = need.Value;
            }
            return unmet;
        }

        public static KitUpdatePlan Make(KitManifest installed, KitManifest target, string kitRoot, IKitProject project,
            Func<float, bool> progress = null)
        {
            var plan = new KitUpdatePlan { installed = installed, target = target, kitRoot = kitRoot };
            var places = new Places(installed, target, kitRoot, project);

            // Parents before children, so a folder's place is known before what goes into it.
            var guids = target.files.Keys.Union(installed.files.Keys)
                .OrderBy(g => (target.files.TryGetValue(g, out KitManifestEntry t) ? t : installed.files[g]).path, StringComparer.Ordinal)
                .ToList();
            for (int i = 0; i < guids.Count; ++i)
            {
                if (progress != null && i % 50 == 0 && progress((float)i / guids.Count))
                    return null;
                string guid = guids[i];
                installed.files.TryGetValue(guid, out KitManifestEntry before);
                target.files.TryGetValue(guid, out KitManifestEntry after);
                KitAction action = after == null ? Removed(guid, before, project)
                    : before == null ? Added(guid, after, places, project)
                    : Kept(guid, before, after, places, project);
                if (action == null)
                    plan.unchanged += (after ?? before).folder ? 0 : 1;
                else
                    plan.actions.Add(action);
            }
            return plan;
        }

        /// <summary>In the installed release, not in the target.</summary>
        private static KitAction Removed(string guid, KitManifestEntry before, IKitProject project)
        {
            string now = project.PathOf(guid);
            if (now == null)
                return null;
            var action = new KitAction { guid = guid, path = before.path, folder = before.folder, currentPath = now };
            // A folder goes only if the deletions leave it empty; the apply step checks.
            action.kind = before.folder || project.HashOf(now) == before.hash ? KitActionKind.Delete : KitActionKind.KeptDeleted;
            return action;
        }

        /// <summary>New in the target release.</summary>
        private static KitAction Added(string guid, KitManifestEntry after, Places places, IKitProject project)
        {
            var action = new KitAction { guid = guid, path = after.path, folder = after.folder, size = after.size };
            string now = project.PathOf(guid);
            if (now != null)
            {
                // Already in the project (a user who copied it in from a newer kit): treat it as the user's copy.
                action.currentPath = action.targetPath = now;
                if (after.folder || project.HashOf(now) == after.hash)
                    return null;
                action.kind = KitActionKind.EditedByYou;
                action.content = true;
                return action;
            }
            string place = places.Of(guid);
            if (place == null)
            {
                action.kind = KitActionKind.OptedOut;
                return action;
            }
            action.targetPath = place;
            if (project.Exists(place))
            {
                action.kind = KitActionKind.PathTaken;
                return action;
            }
            action.kind = KitActionKind.Add;
            action.content = !after.folder;
            action.meta = true;
            return action;
        }

        /// <summary>In both releases.</summary>
        private static KitAction Kept(string guid, KitManifestEntry before, KitManifestEntry after, Places places, IKitProject project)
        {
            string now = project.PathOf(guid);
            if (now == null)
                return null; // the user deleted it, so it stays deleted
            var action = new KitAction { guid = guid, path = after.path, folder = after.folder, currentPath = now, size = after.size };
            action.targetPath = places.Of(guid) ?? now;
            if (after.folder)
            {
                if (!action.Moves)
                    return null;
                action.kind = KitActionKind.Update;
                return action;
            }

            string hash = project.HashOf(now);
            bool edited = hash != before.hash;
            if (hash != after.hash && after.hash != before.hash)
            {
                action.content = true;
                action.kind = edited ? KitActionKind.EditedByYou : KitActionKind.Update;
            }
            else
            {
                action.kind = KitActionKind.Update;
            }
            if (after.meta != before.meta)
            {
                // The user's own import settings win, as their edits do.
                string meta = project.MetaHashOf(now);
                action.meta = meta == before.meta && meta != after.meta;
            }
            if (!action.content && !action.meta && !action.Moves)
                return null;
            return action;
        }

        /// <summary>
        /// Where each target entry belongs in the project. A file or folder still where the installed release put it (in its
        /// installed folder, under its installed name, wherever that folder is now) goes where the target release puts it,
        /// under its target folder's place. One the user moved stays where they put it. Under a folder the user deleted,
        /// there is no place.
        /// </summary>
        private class Places
        {
            private readonly KitManifest _installed;
            private readonly KitManifest _target;
            private readonly string _kitRoot;
            private readonly IKitProject _project;
            private readonly Dictionary<string, string> _installedFolders;
            private readonly Dictionary<string, string> _targetFolders;
            private readonly Dictionary<string, string> _memo = new Dictionary<string, string>();

            public Places(KitManifest installed, KitManifest target, string kitRoot, IKitProject project)
            {
                _installed = installed;
                _target = target;
                _kitRoot = kitRoot;
                _project = project;
                _installedFolders = FoldersByPath(installed);
                _targetFolders = FoldersByPath(target);
            }

            private static Dictionary<string, string> FoldersByPath(KitManifest manifest)
            {
                var folders = new Dictionary<string, string>();
                foreach (var kv in manifest.files)
                {
                    if (kv.Value.folder)
                        folders[kv.Value.path] = kv.Key;
                }
                return folders;
            }

            /// <summary>The place of a target entry, or null when it falls inside a folder the user deleted.</summary>
            public string Of(string guid)
            {
                if (_memo.TryGetValue(guid, out string place))
                    return place;
                KitManifestEntry after = _target.files[guid];
                string parent = ParentPlace(after.path, _targetFolders, Of);
                if (parent == null)
                {
                    place = null;
                }
                else if (!_installed.files.TryGetValue(guid, out KitManifestEntry before))
                {
                    place = parent + "/" + Name(after.path);
                }
                else
                {
                    string now = _project.PathOf(guid);
                    if (now == null)
                        place = null;
                    else if (now == ParentPlace(before.path, _installedFolders, _project.PathOf) + "/" + Name(before.path))
                        place = parent + "/" + Name(after.path);
                    else
                        place = now;
                }
                _memo[guid] = place;
                return place;
            }

            private string ParentPlace(string path, Dictionary<string, string> folders, Func<string, string> placeOf)
            {
                int slash = path.LastIndexOf('/');
                if (slash < 0)
                    return _kitRoot;
                return folders.TryGetValue(path.Substring(0, slash), out string parent) ? placeOf(parent) : null;
            }

            private static string Name(string path)
            {
                return path.Substring(path.LastIndexOf('/') + 1);
            }
        }
    }
}
