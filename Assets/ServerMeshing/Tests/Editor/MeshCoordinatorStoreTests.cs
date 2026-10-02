using System;
using System.IO;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class MeshCoordinatorStoreTests
    {
        private string _dir;
        private string _path;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "MeshStoreTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _path = Path.Combine(_dir, "store.json");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, true);
        }

        private static MeshCoordinatorStoreData Data(long incarnation)
        {
            MeshCoordinatorStoreData data = new MeshCoordinatorStoreData { lastIncarnation = incarnation };
            data.broadcast.pair = new MeshLayoutPair(incarnation, 3);
            data.broadcast.entries.Add(new MeshLayoutEntry { id = "C.1", mapId = "Map", baseId = "C", state = MeshCellState.Active, owned = MeshRect.FromMinSize(0f, 0f, 100f, 200f), interest = MeshRect.FromMinSize(0f, 0f, 100f, 200f) });
            data.hasStaged = true;
            data.staged.pair = new MeshLayoutPair(incarnation, 4);
            data.stagedStepId = "split-1";
            data.stagedFirstApplier = "C.1";
            return data;
        }

        [Test]
        public void TryLoad_CorruptJson_FailsNamingThePath()
        {
            File.WriteAllText(_path, "{ not json");
            MeshCoordinatorStore store = new MeshCoordinatorStore(_path);

            Assert.That(store.TryLoad(out MeshCoordinatorStoreData data, out string error), Is.False);
            Assert.That(data, Is.Null);
            Assert.That(error, Does.Contain(store.Path));
        }

        [Test]
        public void TryLoad_WrongFormat_Fails()
        {
            File.WriteAllText(_path, "{\"format\": 99}");

            Assert.That(new MeshCoordinatorStore(_path).TryLoad(out _, out string error), Is.False);
            Assert.That(error, Does.Contain("format 99"));
        }

        [Test]
        public void TrySave_OntoADirectory_FailsAndIsNotWritableUntilASaveSucceeds()
        {
            Directory.CreateDirectory(_path);
            MeshCoordinatorStore blocked = new MeshCoordinatorStore(_path);

            Assert.That(blocked.TrySave(Data(1), out string error), Is.False);
            Assert.That(error, Does.Contain("write failed"));
            Assert.That(blocked.Writable, Is.False);
            Assert.That(File.Exists(_path + ".tmp"), Is.False);

            Directory.Delete(_path);
            Assert.That(blocked.TrySave(Data(1), out error), Is.True, error);
            Assert.That(blocked.Writable, Is.True);
        }

        [Test]
        public void TryMoveAside_KeepsTheOriginalBytes()
        {
            File.WriteAllText(_path, "{ broken");
            MeshCoordinatorStore store = new MeshCoordinatorStore(_path);

            Assert.That(store.TryMoveAside(".unreadable-1", out string movedTo, out string error), Is.True, error);

            Assert.That(File.Exists(_path), Is.False);
            Assert.That(File.ReadAllText(movedTo), Is.EqualTo("{ broken"));
        }

        [Test]
        public void TryLoad_NoFile_IsTrueWithNothing()
        {
            Assert.That(new MeshCoordinatorStore(_path).TryLoad(out MeshCoordinatorStoreData data, out string error), Is.True, error);
            Assert.That(data, Is.Null);
        }

        [Test]
        public void SaveThenLoad_RoundTripsEverything_AndLeavesNoTemporaryFile()
        {
            MeshCoordinatorStore store = new MeshCoordinatorStore(_path);
            Assert.That(store.TrySave(Data(7), out string error), Is.True, error);
            Assert.That(store.TrySave(Data(8), out error), Is.True, error);

            Assert.That(store.TryLoad(out MeshCoordinatorStoreData data, out error), Is.True, error);

            Assert.That(data.lastIncarnation, Is.EqualTo(8));
            Assert.That(data.broadcast.pair, Is.EqualTo(new MeshLayoutPair(8, 3)));
            Assert.That(data.broadcast.entries[0].owned, Is.EqualTo(MeshRect.FromMinSize(0f, 0f, 100f, 200f)));
            Assert.That(data.hasStaged, Is.True);
            Assert.That((data.staged.pair, data.stagedStepId, data.stagedFirstApplier), Is.EqualTo((new MeshLayoutPair(8, 4), "split-1", "C.1")));
            Assert.That(File.Exists(_path + ".tmp"), Is.False);
        }

        [Test]
        public void ResolvePath_EnvironmentThenSettingThenDefault()
        {
            string saved = Environment.GetEnvironmentVariable(MeshCoordinatorStore.ENV_PATH);
            try
            {
                Environment.SetEnvironmentVariable(MeshCoordinatorStore.ENV_PATH, _path);
                Assert.That(MeshCoordinatorStore.ResolvePath("setting.json"), Is.EqualTo(Path.GetFullPath(_path)));
                Environment.SetEnvironmentVariable(MeshCoordinatorStore.ENV_PATH, null);
                Assert.That(MeshCoordinatorStore.ResolvePath("setting.json"), Is.EqualTo(Path.GetFullPath("setting.json")));
                Assert.That(MeshCoordinatorStore.ResolvePath(""), Is.EqualTo(Path.GetFullPath(MeshCoordinatorStore.DefaultRelativePath)));
            }
            finally
            {
                Environment.SetEnvironmentVariable(MeshCoordinatorStore.ENV_PATH, saved);
            }
        }
    }
}
