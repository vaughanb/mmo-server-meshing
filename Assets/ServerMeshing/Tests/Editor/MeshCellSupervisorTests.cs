using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>
    /// Slice 2d's pure parts: incarnation order, the fenced set, MeshCellSupervisor.Evaluate (down, hung, key presence,
    /// the interval, the stable window), the spawner's kill rule, the grace rule and the store's fenced field.
    /// </summary>
    public class MeshCellSupervisorTests
    {
        private static readonly MeshSupervisorSettings Defaults = MeshSupervisorSettings.From(new MeshDynamicSettings());

        private static MeshSupervisorInput Input(float now, bool registered, bool keyPresent, float lastReportAt = -1f, bool killed = false)
        {
            return new MeshSupervisorInput { now = now, registered = registered, keyPresent = keyPresent, lastReportAt = lastReportAt, killed = killed };
        }

        private static MeshSupervisorVerdict Run(MeshSupervisedCell cell, float from, float to, bool registered, bool keyPresent, float lastReportAt = -1f, bool killed = false)
        {
            MeshSupervisorVerdict last = MeshSupervisorVerdict.None;
            for (float t = from; t <= to + 1e-3f; t += 1f)
            {
                last = MeshCellSupervisor.Evaluate(cell, Input(t, registered, keyPresent, lastReportAt, killed), Defaults).verdict;
                if (last != MeshSupervisorVerdict.None)
                    return last;
            }
            return last;
        }

        [Test]
        public void Incarnations_AreHexUtcTicks_AndOnlyThoseAreOrdered()
        {
            Assert.That(MeshIncarnation.TryTicks(DateTime.UtcNow.Ticks.ToString("x"), out long ticks), Is.True);
            Assert.That(ticks, Is.GreaterThan(0L));
            Assert.That(MeshIncarnation.TryTicks("pc", out _), Is.False);
            Assert.That(MeshIncarnation.TryTicks("", out _), Is.False);
            Assert.That(MeshIncarnation.IsOlder("100", "200"), Is.True);
            Assert.That(MeshIncarnation.IsOlder("200", "100"), Is.False);
            Assert.That(MeshIncarnation.IsOlder("pc", "200"), Is.False, "an unordered incarnation is never older");
        }

        [Test]
        public void FencedSet_RefusesTheFencedAndAnythingNotNewer()
        {
            MeshFencedSet set = new MeshFencedSet();
            Assert.That(set.Add("C.1", "200"), Is.True);
            Assert.That(set.Add("C.1", "200"), Is.False);
            Assert.That(set.IsFenced("C.1", "200"), Is.True);
            Assert.That(set.IsFenced("C.1", "1ff"), Is.True, "a process that started before a fenced one is stale");
            Assert.That(set.IsFenced("C.1", "201"), Is.False);
            Assert.That(set.IsFenced("c.1", "200"), Is.True, "cell ids compare as the cluster does");
            Assert.That(set.IsFenced("C.2", "100"), Is.False);
            set.Add("C.2", "xc");
            Assert.That(set.IsFenced("C.2", "xc"), Is.True);
            Assert.That(set.IsFenced("C.2", "xd"), Is.False, "unordered incarnations are fenced by name only");

            MeshFencedSet loaded = new MeshFencedSet();
            loaded.Load(set.ToStore());
            Assert.That(loaded.Count, Is.EqualTo(2));
            Assert.That(loaded.IsFenced("C.1", "1ff") && loaded.IsFenced("C.2", "xc"), Is.True);
            for (int i = 0; i < MeshFencedSet.KeptPerCell + 4; ++i)
                set.Add("C.3", (0x1000 + i).ToString("x"));
            Assert.That(set.ToStore().FindAll(e => e.cellId == "C.3").Count, Is.EqualTo(MeshFencedSet.KeptPerCell));
            Assert.That(set.IsFenced("C.3", "1000"), Is.True, "the oldest dropped entry is still older than a kept one");
        }

        [Test]
        public void Down_NeedsTheGraceWithTheKeyAbsent_OrFiveSecondsOnceKilled()
        {
            MeshSupervisedCell cell = new MeshSupervisedCell { cellId = "C.1" };
            Assert.That(Run(cell, 0f, 60f, false, true), Is.EqualTo(MeshSupervisorVerdict.None), "a registered key (a zombie, or a process that never said hello) is not down");
            Assert.That(Run(cell, 61f, 79f, false, false), Is.EqualTo(MeshSupervisorVerdict.None));
            MeshSupervisorDecision down = MeshCellSupervisor.Evaluate(cell, Input(81f, false, false), Defaults);
            Assert.That((down.verdict, down.seconds), Is.EqualTo((MeshSupervisorVerdict.Down, 20f)));

            MeshSupervisedCell killed = new MeshSupervisedCell { cellId = "C.1" };
            Assert.That(Run(killed, 0f, 4f, false, false, killed: true), Is.EqualTo(MeshSupervisorVerdict.None));
            Assert.That(MeshCellSupervisor.Evaluate(killed, Input(5f, false, false, killed: true), Defaults).verdict, Is.EqualTo(MeshSupervisorVerdict.Down));

            MeshSupervisorSettings shortGrace = MeshSupervisorSettings.From(new MeshDynamicSettings { childDownGraceSeconds = 3f });
            Assert.That(shortGrace.DownSeconds, Is.EqualTo(16f), "never under the id-reuse time, whatever the grace");
        }

        [Test]
        public void Hung_IsRegisteredButSilent_OnlyAfterAFirstReport()
        {
            MeshSupervisedCell cell = new MeshSupervisedCell { cellId = "C.1" };
            Assert.That(Run(cell, 0f, 100f, true, true, lastReportAt: -1f), Is.EqualTo(MeshSupervisorVerdict.None), "a cell that never reported is not judged");
            Assert.That(Run(cell, 101f, 129f, true, true, lastReportAt: 100f), Is.EqualTo(MeshSupervisorVerdict.None));
            MeshSupervisorDecision hung = MeshCellSupervisor.Evaluate(cell, Input(130f, true, true, 100f), Defaults);
            Assert.That((hung.verdict, hung.seconds), Is.EqualTo((MeshSupervisorVerdict.Hung, 30f)));

            MeshCellSupervisor.BeginAttempt(cell, 130f, true);
            Assert.That(cell.phase, Is.EqualTo(MeshSupervisorPhase.Killing));
            Assert.That(MeshCellSupervisor.Evaluate(cell, Input(131f, true, true, 100f), Defaults).verdict, Is.EqualTo(MeshSupervisorVerdict.None), "the key is still held");
            Assert.That(MeshCellSupervisor.Evaluate(cell, Input(132f, false, false, 100f), Defaults).verdict, Is.EqualTo(MeshSupervisorVerdict.RestartNow));
        }

        [Test]
        public void Attempts_AreSpaced_FailWithoutAHello_AndResetAfterTheStableWindow()
        {
            MeshSupervisedCell cell = new MeshSupervisedCell { cellId = "C.1" };
            Assert.That(Run(cell, 0f, 20f, false, false), Is.EqualTo(MeshSupervisorVerdict.Down));
            MeshCellSupervisor.BeginAttempt(cell, 20f, false);
            Assert.That(Run(cell, 21f, 79f, false, false), Is.EqualTo(MeshSupervisorVerdict.None));
            MeshSupervisorDecision failed = MeshCellSupervisor.Evaluate(cell, Input(80f, false, false), Defaults);
            Assert.That((failed.verdict, failed.why), Is.EqualTo((MeshSupervisorVerdict.AttemptFailed, "no hello from a new incarnation in 60 s")));
            Assert.That(MeshCellSupervisor.Failed(cell, Defaults), Is.False);
            Assert.That((cell.failures, cell.Attempt), Is.EqualTo((1, 2)));

            // A spawn error right after an attempt: the next one waits for the interval.
            MeshCellSupervisor.BeginAttempt(cell, 80f, false);
            MeshCellSupervisor.Failed(cell, Defaults);
            Assert.That(Run(cell, 81f, 89f, false, false), Is.EqualTo(MeshSupervisorVerdict.None));
            Assert.That(MeshCellSupervisor.Evaluate(cell, Input(90f, false, false), Defaults).verdict, Is.EqualTo(MeshSupervisorVerdict.Down));

            MeshCellSupervisor.BeginAttempt(cell, 90f, false);
            MeshCellSupervisor.Back(cell, 95f);
            Assert.That((cell.phase, cell.backAt), Is.EqualTo((MeshSupervisorPhase.Up, 95f)));
            Assert.That(Run(cell, 96f, 154f, true, true), Is.EqualTo(MeshSupervisorVerdict.None));
            Assert.That(MeshCellSupervisor.Evaluate(cell, Input(155f, true, true), Defaults).verdict, Is.EqualTo(MeshSupervisorVerdict.Stable));
            Assert.That((cell.failures, cell.backAt), Is.EqualTo((0, -1f)));
        }

        [Test]
        public void GoingDownAgainInsideTheStableWindow_FailsTheRestart()
        {
            MeshSupervisedCell cell = new MeshSupervisedCell { cellId = "C.1" };
            MeshCellSupervisor.BeginAttempt(cell, 0f, false);
            MeshCellSupervisor.Back(cell, 10f);
            Assert.That(Run(cell, 11f, 39f, true, true), Is.EqualTo(MeshSupervisorVerdict.None));
            Assert.That(Run(cell, 40f, 59f, false, false), Is.EqualTo(MeshSupervisorVerdict.None));
            MeshSupervisorDecision failed = MeshCellSupervisor.Evaluate(cell, Input(60f, false, false), Defaults);
            Assert.That((failed.verdict, failed.why), Is.EqualTo((MeshSupervisorVerdict.AttemptFailed, "down again 30 s after it came back")));
            Assert.That(MeshCellSupervisor.Failed(cell, Defaults), Is.False);
            Assert.That(MeshCellSupervisor.Evaluate(cell, Input(60f, false, false), Defaults).verdict, Is.EqualTo(MeshSupervisorVerdict.Down), "the next attempt follows at once");
            MeshCellSupervisor.Failed(cell, Defaults);
            Assert.That(MeshCellSupervisor.Failed(cell, Defaults), Is.True, "three failures spend the default budget");
        }

        [Test]
        public void SpawnerKillRule_NeedsTheBuildsExecutable_StartedNearTheIncarnation()
        {
            string exe = Path.Combine(Path.GetTempPath(), "Build", "Server", "kit-ce-project.exe");
            DateTime started = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
            string incarnation = started.AddSeconds(4).Ticks.ToString("x");

            Assert.That(MeshSpawnerKillRule.Check(exe, exe, started, incarnation), Is.Null);
            Assert.That(MeshSpawnerKillRule.Check(exe.ToUpperInvariant(), exe, started, incarnation), Is.Null, "Windows paths compare without case");
            Assert.That(MeshSpawnerKillRule.Check(Path.Combine(Path.GetTempPath(), "notepad.exe"), exe, started, incarnation), Is.EqualTo("it runs notepad.exe, not the build's kit-ce-project.exe"));
            Assert.That(MeshSpawnerKillRule.Check(exe, exe, started.AddSeconds(-30), incarnation), Is.Null, "a slow batchmode boot: 34 s is inside the window");
            Assert.That(MeshSpawnerKillRule.Check(exe, exe, started.AddSeconds(-90), incarnation), Is.EqualTo($"it started 94 s from incarnation {incarnation}, over 60 s: the pid was reused"));
            Assert.That(MeshSpawnerKillRule.Check(exe, exe, started, "pc"), Is.EqualTo("incarnation pc is no start time"));
            Assert.That(MeshSpawnerKillRule.Check(string.Empty, exe, started, incarnation), Does.StartWith("it runs nothing known"));
        }

        [Test]
        public void GraceRule_IsAnErrorUnderFenceAndSavePlusThree()
        {
            Assert.That(MeshLayoutValidator.SupervisionSettingsError(new MeshDynamicSettings()), Is.Null, "20 s against 10 + 5 + 3");
            Assert.That(MeshLayoutValidator.SupervisionSettingsError(new MeshDynamicSettings { childDownGraceSeconds = 18f }), Is.Null);
            Assert.That(MeshLayoutValidator.SupervisionSettingsError(new MeshDynamicSettings { childDownGraceSeconds = 15f }), Is.EqualTo(
                "dynamicSettings.childDownGraceSeconds 15 is under childFenceSeconds + fenceSaveSeconds + 3 = 18 s: a restarted cell could start beside one that is still fencing"));
        }

        [Test]
        public void Store_KeepsTheFencedSet_AndReadsAFileWithoutIt()
        {
            string dir = Path.Combine(Path.GetTempPath(), "MeshSupervisorStore-" + Guid.NewGuid().ToString("N"));
            try
            {
                MeshCoordinatorStore store = new MeshCoordinatorStore(Path.Combine(dir, "store.json"));
                MeshCoordinatorStoreData data = new MeshCoordinatorStoreData { lastIncarnation = 7 };
                data.fencedIncarnations.Add(new MeshFencedIncarnation { cellId = "C.1", incarnation = "300" });
                Assert.That(store.TrySave(data, out string error), Is.True, error);
                Assert.That(store.TryLoad(out MeshCoordinatorStoreData loaded, out error), Is.True, error);
                Assert.That(loaded.format, Is.EqualTo(MeshCoordinatorStoreData.CurrentFormat), "the format is unchanged");
                Assert.That((loaded.fencedIncarnations.Count, loaded.fencedIncarnations[0].cellId, loaded.fencedIncarnations[0].incarnation), Is.EqualTo((1, "C.1", "300")));

                File.WriteAllText(store.Path, "{\"format\":1,\"lastIncarnation\":9,\"broadcast\":{\"entries\":[]},\"hasStaged\":false,\"staged\":{\"entries\":[]},\"abortedIds\":[\"C.9\"]}");
                Assert.That(store.TryLoad(out loaded, out error), Is.True, error);
                Assert.That(loaded.fencedIncarnations, Is.Not.Null.And.Empty);
                Assert.That(loaded.abortedIds, Is.EqualTo(new List<string> { "C.9" }));
            }
            finally
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
        }
    }
}
