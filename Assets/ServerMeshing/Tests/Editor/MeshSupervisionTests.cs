using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using static MultiplayerARPG.MMO.Mesh.Tests.MeshFakeWorlds;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>
    /// The coordinator's supervision (Slice 2d decisions 1-5 and 7), driven through fakes: C is split at x = 0 (child C.1
    /// takes x [0, 100)), then C.1 (or C) crashes, hangs or is killed. The operations' crash handling (decision 6) is in
    /// MeshSupervisionTests_Ops.cs, the kills, stale incarnations, id reuse and the store in MeshSupervisionTests_Kills.cs,
    /// the pure parts in MeshCellSupervisorTests.
    /// </summary>
    public partial class MeshSupervisionTests
    {
        private const long ConnC = 7, ConnE = 8, ConnC2 = 9, ConnX = 20, ConnY = 21;
        private const int PidC = 1000, PidX = 4242;
        private static readonly MeshRect Whole = new MeshRect(-100f, -100f, 100f, 100f);
        private static readonly MeshRect West = new MeshRect(-100f, -100f, 0f, 100f);
        private static readonly MeshRect East = new MeshRect(0f, -100f, 100f, 100f);
        private MeshWorldRegistry _world;
        private MeshFakeClock _clock;
        private MeshFakeCoordinatorTransport _transport;
        private MeshFakeLog _log;
        private MeshFakeSplitHost _host;
        private string _dir;
        private MeshCoordinatorStore _store;
        private BandEnvironment _band;
        private MeshLayoutCoordinator _c;
        private readonly List<string> _lines = new List<string>();
        private readonly Dictionary<string, (long connection, string incarnation)> _bound = new Dictionary<string, (long, string)>(StringComparer.OrdinalIgnoreCase);

        [SetUp]
        public void SetUp()
        {
            _band = new BandEnvironment();
            _world = RigLike(map1Dynamic: true);
            _clock = new MeshFakeClock();
            _transport = new MeshFakeCoordinatorTransport();
            _log = new MeshFakeLog();
            _host = new MeshFakeSplitHost();
            _dir = Path.Combine(Path.GetTempPath(), "MeshSupervisionTests-" + Guid.NewGuid().ToString("N"));
            _store = new MeshCoordinatorStore(Path.Combine(_dir, "store.json"));
            _lines.Clear();
            _bound.Clear();
            _c = new MeshLayoutCoordinator(_world, _clock, _transport, _store, _log) { SplitHost = _host };
            _c.OpLine += _lines.Add;
            _c.Start();
            Bind(ConnC, "C", "pc", PidC);
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_world);
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, true);
            _band.Dispose();
        }

        // ---- helpers -------------------------------------------------------------------------------------------------

        private void Hello(long connection, string cellId, string incarnation, int pid = 0)
        {
            _transport.Register(connection, cellId, Map1);
            _c.OnLayoutHello(connection, new MeshLayoutHelloMessage { cellId = cellId, mapId = Map1, incarnation = incarnation, dynamicMap = true, held = new MeshLayoutSnapshot(), processId = pid });
        }

        private void Bind(long connection, string cellId, string incarnation, int pid = 0)
        {
            Hello(connection, cellId, incarnation, pid);
            _bound[cellId] = (connection, incarnation);
        }

        private void Ack(string cellId, MeshLayoutPair pair, bool applied = true)
        {
            (long connection, string incarnation) = _bound[cellId];
            _c.OnLayoutAck(connection, new MeshLayoutAckMessage { cellId = cellId, incarnation = incarnation, pair = pair, result = applied ? MeshLayoutAckResult.Applied : MeshLayoutAckResult.Rejected, reason = applied ? null : "refused" });
        }

        private void AckAll(MeshLayoutPair pair)
        {
            foreach (string cell in new List<string>(_bound.Keys))
            {
                if (_transport.peers.ContainsKey(_bound[cell].connection))
                    Ack(cell, pair);
            }
        }

        private string[] OthersRegistered(string cellId)
        {
            List<string> others = new List<string>();
            foreach (MeshMapServerPeer peer in _transport.peers.Values)
            {
                if (!string.Equals(peer.cellId, cellId, StringComparison.OrdinalIgnoreCase))
                    others.Add(peer.cellId);
            }
            return others.ToArray();
        }

        private MeshSplitOperation RunSplit(string parent, MeshSplitAxis axis, float at, bool positive, string child, long connection, string incarnation, int pid = PidX)
        {
            Assert.That(_c.RequestSplit(parent, axis, at, positive, child, out string error), Is.True, error);
            MeshSplitOperation op = _c.CurrentSplit;
            _c.OnSpawnResult(op.opId, true, "Success");
            Bind(connection, child, incarnation, pid);
            Ack(child, op.e1Pair);
            AckAll(op.e1Pair);
            _c.OnLayoutEvidence(connection, MeshSplitOperationTests.Prewarm(child, incarnation, op.e1Pair, OthersRegistered(child)));
            Ack(child, op.e2Pair);
            AckAll(op.e2Pair);
            (long parentConnection, string parentIncarnation) = _bound[parent];
            _c.OnLayoutEvidence(parentConnection, MeshSplitOperationTests.Drain(parent, parentIncarnation, op.e2Pair, 0));
            AckAll(op.e3Pair);
            Assert.That(op.step, Is.EqualTo(MeshSplitStep.Done), op.lastGateWait);
            return op;
        }

        private void SplitC(string incarnation = "a1", bool waitCooldown = true)
        {
            RunSplit("C", MeshSplitAxis.X, 0f, true, "C.1", ConnX, incarnation);
            if (waitCooldown)
                _clock.Now += 31f;
        }

        /// <summary>Drop: the cell's bound process leaves the cluster, and a tick notices.</summary>
        private void Drop(string cellId)
        {
            _transport.peers.Remove(_bound[cellId].connection);
            _c.Tick();
        }

        /// <summary>Tick: the coordinator's once-a-second tick for that many seconds.</summary>
        private void Tick(int seconds)
        {
            for (int i = 0; i < seconds; ++i)
            {
                _clock.Now += 1f;
                _c.Tick();
            }
        }

        private void Report(string cellId)
        {
            (long connection, string incarnation) = _bound[cellId];
            _c.OnLoadReport(connection, new MeshLoadReportMessage { cellId = cellId, mapId = Map1, incarnation = incarnation });
        }

        /// <summary>DriveCollapse: the collapse merge in hand, with only C answering, to its done.</summary>
        private MeshMergeOperation DriveCollapse()
        {
            MeshMergeOperation op = _c.CurrentMerge;
            Assert.That(op != null && op.collapse && op.InProgress, Is.True, "a collapse runs");
            AckAll(op.e1Pair);
            _c.OnLayoutEvidence(_bound["C"].connection, MeshSplitOperationTests.Prewarm("C", _bound["C"].incarnation, op.e1Pair, OthersRegistered("C")));
            Assert.That(op.step, Is.EqualTo(MeshMergeStep.E2Staged), op.lastGateWait);
            Ack("C", op.e2Pair);
            Assert.That(op.step, Is.EqualTo(MeshMergeStep.E3Broadcast), op.lastGateWait);
            AckAll(op.e3Pair);
            AckAll(op.tidyPair);
            Assert.That(op.step, Is.EqualTo(MeshMergeStep.Done), op.lastGateWait);
            return op;
        }

        private static string Sup(string text) => "Supervisor: " + text;

        private List<string> SupLines() => _lines.FindAll(l => l.StartsWith("Supervisor: ", StringComparison.Ordinal));

        private List<string> MergeLines() => _lines.FindAll(l => l.StartsWith("Merge ", StringComparison.Ordinal));

        private List<string> SplitLines() => _lines.FindAll(l => l.StartsWith("Split ", StringComparison.Ordinal));

        private MeshRetireMessage LastRetire() => _transport.retires[_transport.retires.Count - 1].message;

        // ---- down, restart and back ----------------------------------------------------------------------------------

        [Test]
        public void ADownChild_IsRestartedAfterTheGrace_KilledFirst_AndBackOnlyAsANewIncarnation()
        {
            SplitC();
            Drop("C.1");
            Tick(19);
            Assert.That(_host.restarts, Is.Empty);
            Assert.That(SupLines(), Is.Empty);

            Tick(1);

            Assert.That(SupLines(), Is.EqualTo(new[] { Sup("C.1 down 20 s, restart 1 of 3 requested") }));
            Assert.That(_host.restarts, Is.EqualTo(new[] { "C.1 C Map1" }));
            Assert.That(_host.kills.Count, Is.EqualTo(1), "any process of the channel is killed before the attempt");
            (MeshKillChannelMessage kill, string baseId) = _host.kills[0];
            Assert.That((kill.channelId, kill.mapId, kill.processId, kill.incarnation, kill.reason, baseId), Is.EqualTo(("C.1", Map1, PidX, "a1", "restart 1", "C")));
            Assert.That(_c.IsFencedIncarnation("C.1", "a1"), Is.True, "the down incarnation joins the fenced set");
            Assert.That(_c.SupervisedOf("C.1").phase, Is.EqualTo(MeshSupervisorPhase.Restarting));

            // The old incarnation coming back (a zombie) is no success: it is refused.
            Hello(ConnX + 5, "C.1", "a1", PidX);
            Assert.That((LastRetire().reason, LastRetire().incarnation), Is.EqualTo(("fenced: stale incarnation", "a1")));
            Assert.That(_lines[_lines.Count - 1], Is.EqualTo("Layout hello from C.1 incarnation a1 (fenced: stale incarnation) answered with Retire"));
            Assert.That(_c.SupervisedOf("C.1").phase, Is.EqualTo(MeshSupervisorPhase.Restarting));
            _transport.peers.Remove(ConnX + 5);

            Bind(ConnX + 1, "C.1", "a2", PidX + 1);

            Assert.That(SupLines()[SupLines().Count - 1], Is.EqualTo(Sup("C.1 back, incarnation a2")));
            Assert.That(_c.SupervisedOf("C.1").phase, Is.EqualTo(MeshSupervisorPhase.Up));
            Assert.That((_c.BindingOf("C.1").incarnation, _c.BindingOf("C.1").processId), Is.EqualTo(("a2", PidX + 1)));
            MeshLayoutMessage answer = _transport.layouts[_transport.layouts.Count - 1].message;
            Assert.That((answer.kind, answer.targetIncarnation, answer.snapshot.pair), Is.EqualTo((MeshLayoutKind.HelloAnswer, "a2", _c.BroadcastPair)), "answered with the current layout");
            Assert.That(_store.TryLoad(out MeshCoordinatorStoreData data, out _), Is.True);
            Assert.That(data.fencedIncarnations.Exists(f => f.cellId == "C.1" && f.incarnation == "a1"), Is.True, "the fenced set is stored");
        }

        [Test]
        public void ARegisteredKey_HoldsOffTheRestart()
        {
            SplitC();
            Drop("C.1");
            _transport.Register(ConnX + 5, "C.1", Map1);   // a process of C.1 that never said hello holds the key
            Tick(40);
            Assert.That(_host.restarts, Is.Empty);

            _transport.peers.Remove(ConnX + 5);
            _c.Tick();
            Tick(19);
            Assert.That(_host.restarts, Is.Empty, "the grace runs from the key's absence");
            Tick(1);
            Assert.That(SupLines(), Is.EqualTo(new[] { Sup("C.1 down 20 s, restart 1 of 3 requested") }));
        }

        [Test]
        public void FailedRestarts_AreCountedAndSpaced_ThenTheChildCollapsesIntoItsBase_DespiteTheCooldown()
        {
            _world.dynamicSettings.splitCooldownSeconds = 600f;
            SplitC();
            Drop("C.1");
            Tick(20);
            Tick(59);
            Assert.That(SupLines().Count, Is.EqualTo(1));
            Tick(1);
            Assert.That(SupLines().GetRange(1, 2), Is.EqualTo(new[]
            {
                Sup("C.1 restart 1 failed (no hello from a new incarnation in 60 s; no known process id, left to the kit)"),
                Sup("C.1 down 80 s, restart 2 of 3 requested"),
            }));
            Assert.That(_host.kills.Count, Is.EqualTo(1), "the binding's process was killed once already");
            Tick(120);
            Assert.That(SupLines().GetRange(3, 4), Is.EqualTo(new[]
            {
                Sup("C.1 restart 2 failed (no hello from a new incarnation in 60 s; no known process id, left to the kit)"),
                Sup("C.1 down 140 s, restart 3 of 3 requested"),
                Sup("C.1 restart 3 failed (no hello from a new incarnation in 60 s; no known process id, left to the kit)"),
                Sup("C.1 collapse queued after 3 failed restart(s)"),
            }));
            Assert.That(_host.restarts.Count, Is.EqualTo(3));
            Assert.That(MergeLines(), Is.Empty);

            // A late process of a failed attempt is fenced while the collapse is queued.
            Hello(ConnX + 7, "C.1", "b9");
            Assert.That(LastRetire().reason, Is.EqualTo("fenced: collapsing"));
            _transport.peers.Remove(ConnX + 7);

            Tick(1);
            MeshMergeOperation op = _c.CurrentMerge;
            Assert.That(MergeLines()[0], Is.EqualTo("Merge merge-1: requested C C.1 collapse"));
            Assert.That(_c.SupervisedOf("C.1").phase, Is.EqualTo(MeshSupervisorPhase.Collapsing));
            DriveCollapse();

            Assert.That(MergeLines(), Is.EqualTo(new[]
            {
                "Merge merge-1: requested C C.1 collapse", $"Merge merge-1: e1 broadcast {op.e1Pair}", "Merge merge-1: e1 gate open",
                "Merge merge-1: e2 staged", "Merge merge-1: e2 acked", $"Merge merge-1: e2 broadcast {op.e2Pair}",
                "Merge merge-1: C.1 exited before drained", "Merge merge-1: channel C.1 removed", $"Merge merge-1: e3 broadcast {op.e3Pair}",
                $"Merge merge-1: tidy broadcast {op.tidyPair}", "Merge merge-1: done",
            }));
            Assert.That(_transport.retires.FindAll(r => r.message.reason == "merged into C"), Is.Empty, "no Retire to a cell that is gone");
            Assert.That(_host.removed, Is.EqualTo(new[] { "C.1" }));
            MeshCell c = _world.GetCellById("C");
            Assert.That((c.owned, c.interest), Is.EqualTo((Whole, Whole)));
            Tick(1);
            Assert.That(_c.SupervisedOf("C.1"), Is.Null);
            Assert.That(_c.IsSplitFamily("C"), Is.False);
        }

        [Test]
        public void AChildSharingNoFullEdgeWithItsBase_CannotCollapse_AndIsLeftDown()
        {
            SplitC();
            RunSplit("C", MeshSplitAxis.Z, 0f, true, "C.2", ConnY, "c2");
            _clock.Now += 31f;
            _host.restartOk = false;
            Drop("C.1");

            Tick(40);

            Assert.That(SupLines(), Is.EqualTo(new[]
            {
                Sup("C.1 down 20 s, restart 1 of 3 requested"), Sup("C.1 restart 1 failed (spawn error: spawner gone)"),
                Sup("C.1 down 30 s, restart 2 of 3 requested"), Sup("C.1 restart 2 failed (spawn error: spawner gone)"),
                Sup("C.1 down 40 s, restart 3 of 3 requested"), Sup("C.1 restart 3 failed (spawn error: spawner gone)"),
                Sup("C.1 collapse queued after 3 failed restart(s)"), Sup("C.1 cannot collapse (Slice 3); left down"),
            }));
            Assert.That(_c.SupervisedOf("C.1").phase, Is.EqualTo(MeshSupervisorPhase.LeftDown));
            Tick(61);
            Assert.That(SupLines().Count, Is.EqualTo(8), "no more restarts");
            Assert.That(_c.CurrentOp, Is.InstanceOf<MeshSplitOperation>(), "no merge was started");
            List<(long connection, MeshCoordinatorNoteMessage message)> notes = _transport.notes.FindAll(n => n.message.code == "cannot-collapse");
            Assert.That(notes.Count, Is.EqualTo(2), "an error note to the base once a minute");
            Assert.That((notes[0].connection, notes[0].message.level, notes[0].message.subjectCellId), Is.EqualTo((ConnC, MeshNoteLevel.Error, "C")));
        }

        [Test]
        public void AStableRestart_ResetsTheCount_AGoneAgainOneFailsAndKillsTheNewProcessFirst()
        {
            SplitC();
            Drop("C.1");
            Tick(20);
            Bind(ConnX + 1, "C.1", "a2", PidX + 1);
            Tick(30);
            Drop("C.1");
            Tick(20);

            Assert.That(SupLines().GetRange(2, 2), Is.EqualTo(new[] { Sup("C.1 restart 1 failed (down again 30 s after it came back)"), Sup("C.1 down 20 s, restart 2 of 3 requested") }));
            Assert.That(_host.kills.Count, Is.EqualTo(2));
            Assert.That((_host.kills[1].message.processId, _host.kills[1].message.incarnation, _host.kills[1].message.reason), Is.EqualTo((PidX + 1, "a2", "restart 2")));

            Bind(ConnX + 2, "C.1", "a3", PidX + 2);
            Tick(59);
            Assert.That(_c.SupervisedOf("C.1").failures, Is.EqualTo(1));
            Tick(1);
            Assert.That(_c.SupervisedOf("C.1").failures, Is.EqualTo(0), "up for childRestartStableSeconds");
            Drop("C.1");
            Tick(20);
            Assert.That(SupLines()[SupLines().Count - 1], Is.EqualTo(Sup("C.1 down 20 s, restart 1 of 3 requested")));
        }

        [Test]
        public void AKitBase_IsLeftToTheKit_ASleepModeBase_IsRestarted_AndNeitherCollapses()
        {
            SplitC();
            Drop("C");
            Tick(20);
            Assert.That(SupLines(), Is.EqualTo(new[] { Sup("C down 20 s, left to the kit's restart") }));
            Assert.That(_host.restarts, Is.Empty);
            Assert.That(_c.IsFencedIncarnation("C", "pc"), Is.True);
            Bind(ConnC + 100, "C", "pc2", PidC + 1);
            Assert.That(SupLines()[SupLines().Count - 1], Is.EqualTo(Sup("C back, incarnation pc2")));

            _host.kitRestartsBaseCells = false;
            _host.restartOk = false;
            Drop("C");
            Tick(40);
            Assert.That(SupLines().GetRange(2, 7), Is.EqualTo(new[]
            {
                Sup("C down 20 s, restart 1 of 3 requested"), Sup("C restart 1 failed (spawn error: spawner gone)"),
                Sup("C down 30 s, restart 2 of 3 requested"), Sup("C restart 2 failed (spawn error: spawner gone)"),
                Sup("C down 40 s, restart 3 of 3 requested"), Sup("C restart 3 failed (spawn error: spawner gone)"),
                Sup("C failed 3 restart(s); a base cell is never collapsed, left to the kit"),
            }));
            Tick(60);
            Assert.That(MergeLines(), Is.Empty);
            Assert.That(SupLines().Count, Is.EqualTo(9));
        }

        [Test]
        public void ASleepModeBase_IsRestartedThroughItsSpawner()
        {
            _host.kitRestartsBaseCells = false;
            SplitC();
            Drop("C");
            Tick(20);
            Assert.That(SupLines(), Is.EqualTo(new[] { Sup("C down 20 s, restart 1 of 3 requested") }));
            Assert.That(_host.restarts, Is.EqualTo(new[] { "C C Map1" }));
        }

        [Test]
        public void AHungChild_IsKilled_ThenRestartedOnceItsKeyIsGone()
        {
            SplitC();
            Report("C.1");
            Tick(29);
            Assert.That(SupLines(), Is.Empty);
            Tick(1);
            Assert.That(SupLines(), Is.EqualTo(new[] { Sup("C.1 hung 30 s, restart 1 of 3 requested"), Sup("C.1 killed by its spawner (hung 30 s)") }));
            Assert.That((_host.kills[0].message.processId, _host.kills[0].message.reason), Is.EqualTo((PidX, "hung 30 s")));
            Assert.That(_host.restarts, Is.Empty, "the hung process still holds the key");
            Tick(3);
            Assert.That(_host.restarts, Is.Empty);

            Drop("C.1");

            Assert.That(_host.restarts, Is.EqualTo(new[] { "C.1 C Map1" }));
            Assert.That(_c.SupervisedOf("C.1").phase, Is.EqualTo(MeshSupervisorPhase.Restarting));
        }
    }
}
