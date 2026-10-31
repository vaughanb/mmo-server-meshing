using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace OpenMMORPG.KitUpdater.Tests
{
    public class KitUpdatePlanTests
    {
        private const string Root = "Assets/OpenMMORPG";

        /// <summary>A project as files by path, each with its GUID, contents hash and .meta hash.</summary>
        private class FakeProject : IKitProject
        {
            public class File { public string guid; public string hash; public string meta; }
            public readonly Dictionary<string, File> files = new Dictionary<string, File>();

            public string PathOf(string guid) { return files.Where(f => f.Value.guid == guid).Select(f => f.Key).FirstOrDefault(); }
            public string HashOf(string projectPath) { return files[projectPath].hash; }
            public string MetaHashOf(string projectPath) { return files[projectPath].meta; }
            public bool Exists(string projectPath) { return files.ContainsKey(projectPath); }

            public void Move(string from, string to) { files[to] = files[from]; files.Remove(from); }
        }

        private KitManifest _installed;
        private KitManifest _target;
        private FakeProject _project;

        [SetUp]
        public void SetUp()
        {
            _installed = Manifest("1.1.5");
            _target = Manifest("1.1.6");
            _project = new FakeProject();
            Folder("dC", "Core");
            Folder("dS", "Core/Sub");
            Folder("dD", "Demo");
            File("a", "Core/A.cs");
            File("b", "Core/B.cs");
            File("c", "Core/Sub/C.cs");
            File("d", "Demo/D.png");
            File("r", "README.md");
        }

        private static KitManifest Manifest(string version)
        {
            return new KitManifest
            {
                format = 1, version = version, tag = "v" + version,
                dependencies = new Dictionary<string, string>(), files = new Dictionary<string, KitManifestEntry>(),
            };
        }

        /// <summary>A folder in both releases and in the project.</summary>
        private void Folder(string guid, string path)
        {
            _installed.files[guid] = new KitManifestEntry { path = path, folder = true, meta = "m" };
            _target.files[guid] = new KitManifestEntry { path = path, folder = true, meta = "m" };
            _project.files[Root + "/" + path] = new FakeProject.File { guid = guid };
        }

        /// <summary>A file, the same in both releases, installed untouched.</summary>
        private void File(string guid, string path)
        {
            _installed.files[guid] = new KitManifestEntry { path = path, hash = "h-" + guid, meta = "m-" + guid, size = 10 };
            _target.files[guid] = new KitManifestEntry { path = path, hash = "h-" + guid, meta = "m-" + guid, size = 10 };
            _project.files[Root + "/" + path] = new FakeProject.File { guid = guid, hash = "h-" + guid, meta = "m-" + guid };
        }

        private KitUpdatePlan Plan()
        {
            return KitUpdatePlan.Make(_installed, _target, Root, _project);
        }

        private KitAction Only()
        {
            return Plan().actions.Single();
        }

        [Test]
        public void SameRelease_DoesNothing()
        {
            KitUpdatePlan plan = Plan();
            Assert.That(plan.actions, Is.Empty);
            Assert.That(plan.unchanged, Is.EqualTo(5));
        }

        [Test]
        public void ChangedUpstream_Untouched_IsOverwritten()
        {
            _target.files["a"].hash = "h-new";
            KitAction action = Only();
            Assert.That(action.kind, Is.EqualTo(KitActionKind.Update));
            Assert.That(action.content && action.WritesContent && action.Applies, Is.True);
            Assert.That(action.meta || action.Moves, Is.False);
            Assert.That(action.targetPath, Is.EqualTo(Root + "/Core/A.cs"));
        }

        [Test]
        public void ChangedUpstream_Edited_IsKeptUnlessOverwritten()
        {
            _target.files["a"].hash = "h-new";
            _project.files[Root + "/Core/A.cs"].hash = "mine";
            KitAction action = Only();
            Assert.That(action.kind, Is.EqualTo(KitActionKind.EditedByYou));
            Assert.That(action.Applies || action.WritesContent, Is.False);
            action.overwrite = true;
            Assert.That(action.Applies && action.WritesContent, Is.True);
        }

        [Test]
        public void Edited_NotChangedUpstream_IsLeftAlone()
        {
            _project.files[Root + "/Core/A.cs"].hash = "mine";
            Assert.That(Plan().actions, Is.Empty);
        }

        [Test]
        public void Edited_AlreadyLikeTheTarget_IsLeftAlone()
        {
            _target.files["a"].hash = "h-new";
            _project.files[Root + "/Core/A.cs"].hash = "h-new";
            Assert.That(Plan().actions, Is.Empty);
        }

        [Test]
        public void MetaChangedUpstream_WritesTheMeta_UnlessTheUserChangedTheirs()
        {
            _target.files["a"].meta = "m-new";
            KitAction action = Only();
            Assert.That(action.kind, Is.EqualTo(KitActionKind.Update));
            Assert.That(action.meta, Is.True);
            Assert.That(action.content, Is.False);

            _project.files[Root + "/Core/A.cs"].meta = "mine";
            Assert.That(Plan().actions, Is.Empty);
        }

        [Test]
        public void ContentAndMetaChanged_UserChangedOnlyTheMeta_UpdatesContentKeepsMeta()
        {
            _target.files["a"].hash = "h-new";
            _target.files["a"].meta = "m-new";
            _project.files[Root + "/Core/A.cs"].meta = "mine";
            KitAction action = Only();
            Assert.That(action.kind, Is.EqualTo(KitActionKind.Update));
            Assert.That(action.content, Is.True);
            Assert.That(action.meta, Is.False);
        }

        [Test]
        public void RemovedUpstream_Untouched_IsDeleted_Edited_IsKept()
        {
            _target.files.Remove("a");
            Assert.That(Only().kind, Is.EqualTo(KitActionKind.Delete));
            _project.files[Root + "/Core/A.cs"].hash = "mine";
            Assert.That(Only().kind, Is.EqualTo(KitActionKind.KeptDeleted));
        }

        [Test]
        public void DeletedByTheUser_StaysDeleted_EvenIfChangedUpstream()
        {
            _project.files.Remove(Root + "/Core/A.cs");
            _target.files["a"].hash = "h-new";
            Assert.That(Plan().actions, Is.Empty);
        }

        [Test]
        public void AddedUpstream_IsAdded_WithItsMeta()
        {
            _target.files["n"] = new KitManifestEntry { path = "Core/New.cs", hash = "h-n", meta = "m-n", size = 42 };
            KitAction action = Only();
            Assert.That(action.kind, Is.EqualTo(KitActionKind.Add));
            Assert.That(action.targetPath, Is.EqualTo(Root + "/Core/New.cs"));
            Assert.That(action.content && action.meta, Is.True);
            Assert.That(Plan().DownloadSize, Is.EqualTo(42));
        }

        [Test]
        public void AddedUpstream_IntoAFolderTheUserMoved_GoesWhereTheFolderIs()
        {
            _project.Move(Root + "/Core/Sub", "Assets/Vendor/Sub");
            _project.Move(Root + "/Core/Sub/C.cs", "Assets/Vendor/Sub/C.cs");
            _target.files["n"] = new KitManifestEntry { path = "Core/Sub/New.cs", hash = "h-n", meta = "m-n" };
            KitAction action = Plan().actions.Single(a => a.guid == "n");
            Assert.That(action.kind, Is.EqualTo(KitActionKind.Add));
            Assert.That(action.targetPath, Is.EqualTo("Assets/Vendor/Sub/New.cs"));
            Assert.That(Plan().actions.Count, Is.EqualTo(1), "the user's own moves are not undone");
        }

        [Test]
        public void AddedUpstream_IntoAFolderTheUserDeleted_IsSkipped()
        {
            _project.files.Remove(Root + "/Demo");
            _project.files.Remove(Root + "/Demo/D.png");
            _target.files["n"] = new KitManifestEntry { path = "Demo/New.png", hash = "h-n", meta = "m-n" };
            _target.files["d"].hash = "h-new";
            KitAction action = Only();
            Assert.That(action.kind, Is.EqualTo(KitActionKind.OptedOut));
            Assert.That(action.Applies, Is.False);
        }

        [Test]
        public void AddedUpstream_WhereAUserFileIs_IsSkipped()
        {
            _project.files[Root + "/Core/New.cs"] = new FakeProject.File { guid = "users-own", hash = "x", meta = "y" };
            _target.files["n"] = new KitManifestEntry { path = "Core/New.cs", hash = "h-n", meta = "m-n" };
            Assert.That(Only().kind, Is.EqualTo(KitActionKind.PathTaken));
        }

        [Test]
        public void AddedFolder_AndAFileInIt_GoTogether()
        {
            _target.files["dN"] = new KitManifestEntry { path = "Core/Net", folder = true, meta = "m-dN" };
            _target.files["n"] = new KitManifestEntry { path = "Core/Net/New.cs", hash = "h-n", meta = "m-n" };
            KitUpdatePlan plan = Plan();
            KitAction folder = plan.actions.Single(a => a.guid == "dN");
            Assert.That(folder.kind, Is.EqualTo(KitActionKind.Add));
            Assert.That(folder.folder && folder.meta && !folder.content, Is.True);
            Assert.That(plan.actions.Single(a => a.guid == "n").targetPath, Is.EqualTo(Root + "/Core/Net/New.cs"));
        }

        [Test]
        public void RenamedUpstream_Untouched_IsMoved()
        {
            _target.files["b"].path = "Core/Renamed.cs";
            KitAction action = Only();
            Assert.That(action.kind, Is.EqualTo(KitActionKind.Update));
            Assert.That(action.Moves, Is.True);
            Assert.That(action.currentPath, Is.EqualTo(Root + "/Core/B.cs"));
            Assert.That(action.targetPath, Is.EqualTo(Root + "/Core/Renamed.cs"));
            Assert.That(action.content, Is.False);
        }

        [Test]
        public void RenamedUpstream_Edited_IsMovedButKeepsTheEdit()
        {
            _target.files["b"].path = "Core/Renamed.cs";
            _project.files[Root + "/Core/B.cs"].hash = "mine";
            KitAction action = Only();
            Assert.That(action.Moves, Is.True);
            Assert.That(action.WritesContent, Is.False);
        }

        [Test]
        public void RenamedUpstream_ButTheUserMovedIt_StaysWhereTheUserPutIt()
        {
            _project.Move(Root + "/Core/B.cs", "Assets/MyGame/B.cs");
            _target.files["b"].path = "Core/Renamed.cs";
            Assert.That(Plan().actions, Is.Empty);
        }

        [Test]
        public void FolderRenamedUpstream_MovesTheFolder_AndItsFilesFollow()
        {
            _target.files["dS"].path = "Core/Sub2";
            _target.files["c"].path = "Core/Sub2/C.cs";
            KitUpdatePlan plan = Plan();
            KitAction folder = plan.actions.Single(a => a.guid == "dS");
            Assert.That(folder.targetPath, Is.EqualTo(Root + "/Core/Sub2"));
            Assert.That(plan.actions.Single(a => a.guid == "c").targetPath, Is.EqualTo(Root + "/Core/Sub2/C.cs"));
        }

        [Test]
        public void FolderRemovedUpstream_IsADelete()
        {
            _target.files.Remove("dS");
            _target.files.Remove("c");
            KitUpdatePlan plan = Plan();
            Assert.That(plan.actions.Select(a => a.kind), Is.All.EqualTo(KitActionKind.Delete));
            Assert.That(plan.actions.Single(a => a.folder).currentPath, Is.EqualTo(Root + "/Core/Sub"));
        }

        [Test]
        public void PackageChanges_ListsOnlyWhatTheTargetChanged()
        {
            _installed.dependencies["com.unity.ugui"] = "2.0.0";
            _installed.dependencies["com.unity.burst"] = "1.8.30";
            _target.dependencies["com.unity.ugui"] = "2.0.0";
            _target.dependencies["com.unity.burst"] = "1.8.31";
            _target.dependencies["com.unity.collections"] = "2.6.8";
            Dictionary<string, string> changes = Plan().PackageChanges();
            Assert.That(changes.Keys, Is.EquivalentTo(new[] { "com.unity.burst", "com.unity.collections" }));
            Assert.That(changes["com.unity.burst"], Is.EqualTo("1.8.31"));
        }

        [Test]
        public void UnmetPackages_AreTheChangedOnesTheProjectLacksOrHasOlder()
        {
            _installed.dependencies["com.unity.burst"] = "1.8.30";
            _installed.dependencies["com.unity.ugui"] = "2.0.0";
            _target.dependencies["com.unity.burst"] = "1.8.31";
            _target.dependencies["com.unity.collections"] = "2.6.8";
            _target.dependencies["com.unity.mathematics"] = "1.3.3";
            _target.dependencies["com.unity.ugui"] = "2.0.0";
            var project = new Dictionary<string, string>
            {
                { "com.unity.burst", "1.8.30" },        // older than needed
                { "com.unity.mathematics", "1.4.0" },   // newer than needed: fine
                // collections missing; ugui missing too, but the release did not change it, so it is not the update's business
            };
            Dictionary<string, string> unmet = Plan().UnmetPackages(project);
            Assert.That(unmet.Keys, Is.EquivalentTo(new[] { "com.unity.burst", "com.unity.collections" }));
            Assert.That(unmet["com.unity.burst"], Is.EqualTo("1.8.31"));

            project["com.unity.burst"] = "1.8.31";
            project["com.unity.collections"] = "2.6.8";
            Assert.That(Plan().UnmetPackages(project), Is.Empty);
        }

        [TestCase("1.8.31", "1.8.30", 1)]
        [TestCase("1.10.0", "1.9.9", 1)]
        [TestCase("2.0", "2.0.0", 0)]
        [TestCase("v1.1.5", "1.1.5", 0)]
        [TestCase("1.8.30-pre.1", "1.8.30", 0)]
        [TestCase("1.0.0", "1.0.1", -1)]
        public void KitVersion_Compares(string a, string b, int sign)
        {
            Assert.That(System.Math.Sign(KitVersion.Compare(a, b).Value), Is.EqualTo(sign));
        }

        [Test]
        public void KitVersion_IsNullForNonVersions()
        {
            Assert.That(KitVersion.Compare("develop", "1.0.0"), Is.Null);
            Assert.That(KitVersion.Compare(null, "1.0.0"), Is.Null);
        }

        [Test]
        public void EditorVersionCheck_ComparesMajorAndMinor()
        {
            Assert.That(KitUpdater.EditorIsAtLeast("6000.0"), Is.True);
            Assert.That(KitUpdater.EditorIsAtLeast(null), Is.True);
            Assert.That(KitUpdater.EditorIsAtLeast("99999.0"), Is.False);
        }
    }
}
