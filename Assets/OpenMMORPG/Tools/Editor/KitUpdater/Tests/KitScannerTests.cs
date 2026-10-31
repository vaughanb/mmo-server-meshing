using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NUnit.Framework;

namespace OpenMMORPG.KitUpdater.Tests
{
    public class KitScannerTests
    {
        private const string Root = "Assets/OpenMMORPG";

        /// <summary>A project held in dictionaries: GUID to path, and each path's contents and .meta hash.</summary>
        private class FakeProject : IKitProject
        {
            public readonly Dictionary<string, string> paths = new Dictionary<string, string>();
            public readonly Dictionary<string, string> hashes = new Dictionary<string, string>();
            public readonly Dictionary<string, string> metas = new Dictionary<string, string>();

            public string PathOf(string guid) { return paths.TryGetValue(guid, out string p) ? p : null; }
            public string HashOf(string projectPath) { return hashes[projectPath]; }
            public string MetaHashOf(string projectPath) { return metas.TryGetValue(projectPath, out string m) ? m : null; }
            public bool Exists(string projectPath) { return paths.ContainsValue(projectPath) || hashes.ContainsKey(projectPath); }
        }

        private KitManifest _manifest;
        private FakeProject _project;

        [SetUp]
        public void SetUp()
        {
            _manifest = new KitManifest { format = 1, version = "1.1.5", tag = "v1.1.5", files = new Dictionary<string, KitManifestEntry>() };
            _project = new FakeProject();
            Folder("d1", "Core");
            File("f1", "Core/A.cs");
            File("f2", "Core/B.cs");
            Folder("d2", "Core/Sub");
            File("f3", "Core/Sub/C.cs");
            File("f4", "README.md");
        }

        /// <summary>Adds a folder to the release, and puts it in the project where the release put it.</summary>
        private void Folder(string guid, string path)
        {
            _manifest.files[guid] = new KitManifestEntry { path = path, folder = true, meta = "meta-" + guid };
            _project.paths[guid] = Root + "/" + path;
        }

        /// <summary>Adds a file to the release, and puts it in the project unchanged.</summary>
        private void File(string guid, string path)
        {
            _manifest.files[guid] = new KitManifestEntry { path = path, hash = "hash-" + guid, meta = "meta-" + guid };
            Place(guid, Root + "/" + path);
        }

        private void Place(string guid, string projectPath)
        {
            _project.paths[guid] = projectPath;
            _project.hashes[projectPath] = "hash-" + guid;
            _project.metas[projectPath] = "meta-" + guid;
        }

        private KitScan Scan()
        {
            return KitScanner.Scan(_manifest, Root, _project);
        }

        [Test]
        public void UntouchedKit_HasNoChanges()
        {
            KitScan scan = Scan();
            Assert.That(scan.changes, Is.Empty);
            Assert.That(scan.unchanged, Is.EqualTo(4));
        }

        [Test]
        public void EditedContents_IsEdited()
        {
            _project.hashes[Root + "/Core/A.cs"] = "mine";
            KitChange change = Scan().changes.Single();
            Assert.That(change.kind, Is.EqualTo(KitChangeKind.Edited));
            Assert.That(change.path, Is.EqualTo("Core/A.cs"));
            Assert.That(change.currentPath, Is.EqualTo(Root + "/Core/A.cs"));
        }

        [Test]
        public void OnlyTheMetaChanged_IsImportSettings()
        {
            _project.metas[Root + "/README.md"] = "mine";
            Assert.That(Scan().changes.Single().kind, Is.EqualTo(KitChangeKind.ImportSettings));
        }

        [Test]
        public void EditedContentsAndMeta_IsEditedOnly()
        {
            _project.hashes[Root + "/README.md"] = "mine";
            _project.metas[Root + "/README.md"] = "mine";
            Assert.That(Scan().changes.Single().kind, Is.EqualTo(KitChangeKind.Edited));
        }

        [Test]
        public void MissingFile_IsDeleted()
        {
            _project.paths.Remove("f2");
            KitChange change = Scan().changes.Single();
            Assert.That(change.kind, Is.EqualTo(KitChangeKind.Deleted));
            Assert.That(change.path, Is.EqualTo("Core/B.cs"));
            Assert.That(change.currentPath, Is.Null);
        }

        [Test]
        public void FileElsewhere_IsMoved()
        {
            Place("f1", "Assets/MyGame/A.cs");
            KitChange change = Scan().changes.Single();
            Assert.That(change.kind, Is.EqualTo(KitChangeKind.Moved));
            Assert.That(change.currentPath, Is.EqualTo("Assets/MyGame/A.cs"));
            Assert.That(change.edited, Is.False);
        }

        [Test]
        public void MovedAndEdited_IsOneMovedRowMarkedEdited()
        {
            Place("f1", "Assets/MyGame/A.cs");
            _project.hashes["Assets/MyGame/A.cs"] = "mine";
            KitChange change = Scan().changes.Single();
            Assert.That(change.kind, Is.EqualTo(KitChangeKind.Moved));
            Assert.That(change.edited, Is.True);
        }

        [Test]
        public void DeletedFolder_IsOneRowCountingItsFiles()
        {
            _project.paths.Remove("d1");
            _project.paths.Remove("d2");
            _project.paths.Remove("f1");
            _project.paths.Remove("f2");
            _project.paths.Remove("f3");
            KitScan scan = Scan();
            KitChange change = scan.changes.Single();
            Assert.That(change.kind, Is.EqualTo(KitChangeKind.Deleted));
            Assert.That(change.folder, Is.True);
            Assert.That(change.path, Is.EqualTo("Core"));
            Assert.That(change.files, Is.EqualTo(3));
            Assert.That(scan.unchanged, Is.EqualTo(1));
        }

        [Test]
        public void MovedFolder_IsOneRow_AndEditsInsideItAreStillFound()
        {
            const string moved = "Assets/Vendor/Core";
            _project.paths["d1"] = moved;
            _project.paths["d2"] = moved + "/Sub";
            Place("f1", moved + "/A.cs");
            Place("f2", moved + "/B.cs");
            Place("f3", moved + "/Sub/C.cs");
            _project.hashes[moved + "/Sub/C.cs"] = "mine";

            KitScan scan = Scan();
            Assert.That(scan.changes.Count, Is.EqualTo(2));
            KitChange folder = scan.changes.Single(c => c.folder);
            Assert.That(folder.kind, Is.EqualTo(KitChangeKind.Moved));
            Assert.That(folder.currentPath, Is.EqualTo(moved));
            Assert.That(folder.files, Is.EqualTo(3));
            KitChange edited = scan.changes.Single(c => !c.folder);
            Assert.That(edited.kind, Is.EqualTo(KitChangeKind.Edited));
            Assert.That(edited.currentPath, Is.EqualTo(moved + "/Sub/C.cs"));
            Assert.That(scan.unchanged, Is.EqualTo(3));
        }

        [Test]
        public void FileMovedOutOfAMovedFolder_IsMoved()
        {
            _project.paths["d1"] = "Assets/Vendor/Core";
            _project.paths["d2"] = "Assets/Vendor/Core/Sub";
            Place("f1", "Assets/Vendor/Core/A.cs");
            Place("f2", "Assets/Elsewhere/B.cs");
            Place("f3", "Assets/Vendor/Core/Sub/C.cs");
            KitChange file = Scan().changes.Single(c => !c.folder);
            Assert.That(file.kind, Is.EqualTo(KitChangeKind.Moved));
            Assert.That(file.path, Is.EqualTo("Core/B.cs"));
        }

        [Test]
        public void Cancelling_ReturnsNull()
        {
            Assert.That(KitScanner.Scan(_manifest, Root, _project, p => true), Is.Null);
        }

        [Test]
        public void Parse_ReadsTheBuildersFormat()
        {
            const string json = "{\n  \"format\": 1,\n  \"version\": \"1.1.5\",\n  \"tag\": \"v1.1.5\",\n  \"unity\": \"6000.3\",\n" +
                                "  \"dependencies\": {\"com.unity.ugui\": \"2.0.0\"},\n  \"files\": {\n" +
                                "    \"aa\": {\"path\": \"Core\", \"folder\": true, \"meta\": \"sha1:1\"},\n" +
                                "    \"bb\": {\"path\": \"Core/A.cs\", \"hash\": \"sha1:2\", \"meta\": \"sha1:3\", \"size\": 12}\n  }\n}\n";
            KitManifest manifest = KitManifest.Parse(json);
            Assert.That(manifest.tag, Is.EqualTo("v1.1.5"));
            Assert.That(manifest.unity, Is.EqualTo("6000.3"));
            Assert.That(manifest.files["aa"].folder, Is.True);
            Assert.That(manifest.files["bb"].hash, Is.EqualTo("sha1:2"));
            Assert.That(manifest.files["bb"].size, Is.EqualTo(12));
        }

        [Test]
        public void Parse_RefusesANewerFormat()
        {
            Assert.Throws<FormatException>(() => KitManifest.Parse("{\"format\": 2, \"files\": {}}"));
        }

        [Test]
        public void Hash_MatchesSha1_AndReadsCrlfAsLf()
        {
            Assert.That(KitHash.Of(Encoding.ASCII.GetBytes("abc")),
                Is.EqualTo("sha1:a9993e364706816aba3e25717850c26c9cd0d89d"));
            Assert.That(KitHash.Of(Encoding.ASCII.GetBytes("a\r\nb\r\n")), Is.EqualTo(KitHash.Of(Encoding.ASCII.GetBytes("a\nb\n"))));
        }

        [Test]
        public void Hash_LeavesBinaryAlone()
        {
            Assert.That(KitHash.Of(new byte[] { 0, 13, 10 }), Is.Not.EqualTo(KitHash.Of(new byte[] { 0, 10 })));
        }
    }
}
