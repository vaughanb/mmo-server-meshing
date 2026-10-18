using NUnit.Framework;
using static MultiplayerARPG.MMO.Mesh.Tests.MeshFakeWorlds;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>The kill command and the retire kill, stale incarnations, id reuse and the store (Slice 2d decisions 4, 5 and 7).</summary>
    public partial class MeshSupervisionTests
    {
        // ---- kills, stale incarnations, id reuse, the store --------------------------------------------------------

        [Test]
        public void TheKillCommand_HasTheSpawnerKillTheProcess_AndRefusesWhatItCannotKill()
        {
            SplitC();
            Assert.That(_c.RequestKill("C.1", out string error), Is.True, error);
            Assert.That(_host.kills.Count, Is.EqualTo(1));
            Assert.That((_host.kills[0].message.processId, _host.kills[0].message.incarnation, _host.kills[0].message.reason, _host.kills[0].baseId), Is.EqualTo((PidX, "a1", "operator", "C")));
            Assert.That(_lines[_lines.Count - 1], Is.EqualTo(Sup("C.1 killed by its spawner (operator)")));
            Assert.That(_c.IsFencedIncarnation("C.1", "a1"), Is.True);

            Drop("C.1");
            Tick(5);
            Assert.That(SupLines()[SupLines().Count - 1], Is.EqualTo(Sup("C.1 down 5 s, restart 1 of 3 requested")), "a killed process needs no fence time");
            Assert.That(_host.kills.Count, Is.EqualTo(1), "it was killed already");

            Assert.That(_c.RequestKill("Q", out error), Is.False);
            Assert.That(_lines[_lines.Count - 1], Is.EqualTo("Layout op refused: Q is not a cell of a Dynamic map that said hello"));
            Bind(ConnE, "E", "pe");
            Assert.That(_c.RequestKill("E", out error), Is.False);
            Assert.That(error, Is.EqualTo("E has no known process id"));
            _host.killOk = false;
            Bind(ConnE, "E", "pe", 77);
            Assert.That(_c.RequestKill("E", out error), Is.False);
            Assert.That(error, Is.EqualTo("no map-spawn server is registered"));
        }

        [Test]
        public void ARetireStepPastRetireKillAfterSeconds_KillsTheRetiringCellOnce()
        {
            SplitC();
            Assert.That(_c.RequestMerge("C", "C.1", out string error), Is.True, error);
            MeshMergeOperation op = _c.CurrentMerge;
            AckAll(op.e1Pair);
            _c.OnLayoutEvidence(ConnC, MeshSplitOperationTests.Prewarm("C", "pc", op.e1Pair, OthersRegistered("C")));
            Ack("C", op.e2Pair);
            AckAll(op.e2Pair);
            _clock.Now += 5f;
            _c.OnLayoutEvidence(ConnX, MeshMergeOperationTests.Drained("C.1", "a1", op.e2Pair));
            Assert.That(op.step, Is.EqualTo(MeshMergeStep.Retiring), op.lastGateWait);

            for (int i = 0; i < 26; ++i)
            {
                _clock.Now += 10f;
                _c.Tick();
            }
            Assert.That(_host.kills, Is.Empty);
            _clock.Now += 10f;
            _c.Tick();
            Assert.That(_host.kills.Count, Is.EqualTo(1));
            Assert.That((_host.kills[0].message.channelId, _host.kills[0].message.processId, _host.kills[0].message.reason), Is.EqualTo(("C.1", PidX, "retire straggling 270 s")));
            Assert.That(SupLines(), Is.EqualTo(new[] { Sup("C.1 killed by its spawner (retire straggling 270 s)") }));
            for (int i = 0; i < 10; ++i)
            {
                _clock.Now += 10f;
                _c.Tick();
            }
            Assert.That(_host.kills.Count, Is.EqualTo(1), "once");

            Drop("C.1");
            Tick(5);
            Assert.That(op.step, Is.EqualTo(MeshMergeStep.E3Broadcast), "the merge sees the cell exit");
        }

        [Test]
        public void StaleIncarnations_AreRefusedWithAFencedRetire_AndNeverRebound()
        {
            RunSplit("C", MeshSplitAxis.X, 0f, true, "C.1", ConnX, "200");
            Hello(ConnX + 1, "C.1", "100");
            Assert.That((LastRetire().cellId, LastRetire().incarnation, LastRetire().reason), Is.EqualTo(("C.1", "100", "fenced: stale incarnation")));
            Assert.That((_c.BindingOf("C.1").incarnation, _c.BindingOf("C.1").connectionId), Is.EqualTo(("200", ConnX)));

            Assert.That(_c.RequestKill("C.1", out string error), Is.True, error);
            Hello(ConnX + 2, "C.1", "200");
            Assert.That(LastRetire().reason, Is.EqualTo("fenced: stale incarnation"));
            Hello(ConnX + 3, "C.1", "201");
            Assert.That(_c.BindingOf("C.1").incarnation, Is.EqualTo("201"));
        }

        [Test]
        public void AChildId_IsNamedAgainOnlyAfterTheFenceTime_OrOnceKilled()
        {
            Assert.That(_c.RequestSplit("C", MeshSplitAxis.X, 0f, true, "C.1", out string error), Is.True, error);
            MeshSplitOperation op = _c.CurrentSplit;
            Bind(ConnX, "C.1", "a1", PidX);
            Ack("C.1", op.e1Pair, applied: false);
            Assert.That(op.step, Is.EqualTo(MeshSplitStep.Aborted));
            Drop("C.1");
            _clock.Now += 10f;

            Assert.That(_c.RequestSplit("C", MeshSplitAxis.X, 0f, true, "C.1", out error), Is.False);
            Assert.That(error, Is.EqualTo("C.1 left the cluster 10 s ago, under the 16 s a fencing cell may take"));
            _clock.Now += 6f;
            Assert.That(_c.RequestSplit("C", MeshSplitAxis.X, 0f, true, "C.1", out error), Is.True, error);
            Assert.That(_c.CurrentSplit.opId, Is.EqualTo("split-2"));
        }

        [Test]
        public void AKilledChildId_MayBeNamedAgainAtOnce()
        {
            Assert.That(_c.RequestSplit("C", MeshSplitAxis.X, 0f, true, "C.1", out string error), Is.True, error);
            MeshSplitOperation op = _c.CurrentSplit;
            Bind(ConnX, "C.1", "a1", PidX);
            Ack("C.1", op.e1Pair, applied: false);
            Assert.That(_c.RequestKill("C.1", out error), Is.True, error);
            Drop("C.1");
            _clock.Now += 1f;
            Assert.That(_c.RequestSplit("C", MeshSplitAxis.X, 0f, true, "C.1", out error), Is.True, error);
        }

        [Test]
        public void TheStoresAbortedIdsAndFencedSet_AreLoadedAtStart_AndKept()
        {
            _c.Stop();
            MeshCoordinatorStoreData data = new MeshCoordinatorStoreData { lastIncarnation = 5 };
            data.abortedIds.Add("C.9");
            data.fencedIncarnations.Add(new MeshFencedIncarnation { cellId = "C.1", incarnation = "300" });
            Assert.That(_store.TrySave(data, out string error), Is.True, error);
            _transport.peers.Clear();
            MeshLayoutCoordinator restarted = new MeshLayoutCoordinator(_world, _clock, _transport, _store, _log) { SplitHost = _host };
            restarted.Start();

            Assert.That(restarted.IsAborted("C.9"), Is.True);
            Assert.That(restarted.IsFencedIncarnation("C.1", "2ff"), Is.True);
            Assert.That(restarted.IsFencedIncarnation("C.1", "301"), Is.False);
            _transport.Register(ConnY, "C.9", Map1);
            restarted.OnLayoutHello(ConnY, new MeshLayoutHelloMessage { cellId = "C.9", mapId = Map1, incarnation = "z", dynamicMap = true, held = new MeshLayoutSnapshot() });
            Assert.That(LastRetire().reason, Is.EqualTo("aborted by an earlier split"));
            _transport.peers.Remove(ConnY);

            _transport.Register(ConnC + 50, "C", Map1);
            restarted.OnLayoutHello(ConnC + 50, new MeshLayoutHelloMessage { cellId = "C", mapId = Map1, incarnation = "pc", dynamicMap = true, held = new MeshLayoutSnapshot() });
            restarted.Tick();
            Assert.That(restarted.RequestSplit("C", MeshSplitAxis.X, 0f, true, "C.2", out error), Is.True, error);
            Assert.That(_store.TryLoad(out MeshCoordinatorStoreData saved, out _), Is.True);
            Assert.That(saved.abortedIds, Does.Contain("C.9"), "the first save keeps them");
            Assert.That(saved.fencedIncarnations.Exists(f => f.cellId == "C.1" && f.incarnation == "300"), Is.True);
        }
    }
}
