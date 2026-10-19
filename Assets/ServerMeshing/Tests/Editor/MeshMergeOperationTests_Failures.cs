using NUnit.Framework;
using static MultiplayerARPG.MMO.Mesh.Tests.MeshFakeWorlds;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>The merge's refusals, aborts (with the reverted broadcast), freezes, restarts and straggling (Slice 2c decisions 3, 5, 6).</summary>
    public partial class MeshMergeOperationTests
    {
        private string Refused(string a, string b)
        {
            Assert.That(_c.RequestMerge(a, b, out string error), Is.False);
            Assert.That(_lines[_lines.Count - 1], Is.EqualTo("Layout op refused: " + error));
            return error;
        }

        [Test]
        public void Refusals_NameTheRule()
        {
            Assert.That(Refused("C", "C.1"), Is.EqualTo("cell C.1 is not in the layout"));
            RunSplit("C", MeshSplitAxis.X, 0f, true, "C.1", ConnX, "xc");
            Assert.That(Refused("C.1", "C"), Is.EqualTo("cooldown 30 s left on C"), "a split's done starts the family's cooldown");
            _clock.Now += 31f;
            Assert.That(Refused("C", "C"), Is.EqualTo("a merge needs two different cells, not C twice"));
            Assert.That(Refused("C", "E"), Is.EqualTo("E is not running with a layout answer"));
            Bind(ConnE, "E", "pe");
            Assert.That(Refused("C.1", "E"), Is.EqualTo("C.1 and E are not of one family (bases C and E)"));
            Assert.That(Refused("C2", "C"), Is.EqualTo("C2 is on map Map2, which is Static"));
            _host.canSpawn = false;
            Assert.That(_c.RequestMerge("C", "C.1", out string error), Is.True, "a merge spawns nothing: " + error);
            Assert.That(Refused("C", "C.1"), Is.EqualTo("merge-1 is in progress"));
            Assert.That(_c.IsSplitFamily("C"), Is.True);
            Assert.That(_c.IsOperationChild("C.1"), Is.True);
            Assert.That(_c.IsOperationChild("C"), Is.False);
        }

        [Test]
        public void MergeOfTwoChildren_AndAUnionThatIsNoRectangle_AreRefused()
        {
            SplitC();
            RunSplit("C", MeshSplitAxis.Z, 0f, true, "C.2", ConnY, "x2");
            _clock.Now += 31f;

            // Two children with no report each hold 0 players: the lower id would survive, but their rectangles make none (Slice 3 decision 11).
            Assert.That(Refused("C.1", "C.2"), Does.StartWith("C.1 and C.2 do not make a rectangle: C.1 owns x [0, 100) z [-100, 100), C.2 owns x [-100, 0) z [0, 100)"));
            Assert.That(Refused("C", "C.1"), Does.StartWith("C and C.1 do not make a rectangle: C owns x [-100, 0) z [-100, 0), C.1 owns x [0, 100) z [-100, 100)"));
            Assert.That(_c.CurrentOp, Is.InstanceOf<MeshSplitOperation>(), "nothing refused started an operation");
        }

        [Test]
        public void APendingSettle_IsRefused()
        {
            // A drain timeout leaves the parent's interest over the child's half until its stragglers are gone.
            Assert.That(_c.RequestSplit("C", MeshSplitAxis.X, 0f, true, "C.1", out string error), Is.True, error);
            MeshSplitOperation split = _c.CurrentSplit;
            _c.OnSpawnResult(split.opId, true, "Success");
            Bind(ConnX, "C.1", "xc");
            Ack("C.1", split.e1Pair);
            AckAll(split.e1Pair);
            _c.OnLayoutEvidence(ConnX, MeshSplitOperationTests.Prewarm("C.1", "xc", split.e1Pair, "C"));
            Ack("C.1", split.e2Pair);
            AckAll(split.e2Pair);
            _c.OnLayoutEvidence(ConnC, MeshSplitOperationTests.Drain("C", "pc", split.e2Pair, 3));
            _clock.Now += 31f;
            _c.Tick();
            AckAll(split.e3Pair);
            Assert.That(split.step, Is.EqualTo(MeshSplitStep.Done));
            _clock.Now += 31f;

            Assert.That(Refused("C", "C.1"), Is.EqualTo("C is interested in x [-100, 100) z [-100, 100) beyond its owned x [-100, 0) z [-100, 100): a split's settle is pending"));
        }

        [Test]
        public void RejectedE1_AbortsWithARevertedBroadcast_AndRetiresNothing()
        {
            SplitC();
            int retires = _transport.retires.Count;
            MeshMergeOperation op = RequestMerge();

            Ack("C.1", op.e1Pair, applied: false);

            Assert.That(op.step, Is.EqualTo(MeshMergeStep.Aborted));
            Assert.That(MergeLines().GetRange(MergeLines().Count - 2, 2), Is.EqualTo(new[] { L("aborted C.1 rejected e1 f4240/5: refused"), L("reverted broadcast f4240/6") }));
            Assert.That(_log.warnings, Has.Some.EqualTo(L("aborted C.1 rejected e1 f4240/5: refused")));
            MeshLayoutMessage reverted = Layouts(MeshLayoutKind.Broadcast)[Layouts(MeshLayoutKind.Broadcast).Count - 1].message;
            Assert.That(reverted.stepId, Is.EqualTo("merge-1/revert"));
            MeshCell c = _world.GetCellById("C"), child = _world.GetCellById("C.1");
            Assert.That((c.owned, c.interest), Is.EqualTo((West, West)), "the survivor's interest is back to its own rectangle");
            Assert.That((child.state, child.owned, child.interest), Is.EqualTo((MeshCellState.Active, East, East)));
            Assert.That(_world.AppliedPair, Is.EqualTo(reverted.snapshot.pair));
            Assert.That(_host.removed, Is.Empty, "nothing removed");
            Assert.That(_transport.retires.Count, Is.EqualTo(retires), "nothing retired");
            Assert.That(_c.IsAborted("C.1"), Is.False);
            _clock.Now += 1f;
            Assert.That(_c.RequestMerge("C", "C.1", out string error), Is.True, "an abort starts no cooldown: " + error);
            Assert.That(_c.CurrentMerge.opId, Is.EqualTo("merge-2"));
        }

        [Test]
        public void SurvivorRejectingTheStagedE2_IsStillUndone()
        {
            SplitC();
            MeshMergeOperation op = RequestMerge();
            ToE2Staged(op);

            Ack("C", op.e2Pair, applied: false);

            Assert.That(op.step, Is.EqualTo(MeshMergeStep.Aborted), "nobody else saw e2");
            Assert.That(_lines, Has.Some.EqualTo(L("reverted broadcast f4240/7")));
            Assert.That(_world.GetCellById("C").interest, Is.EqualTo(West));
        }

        [Test]
        public void E1GateTimeout_Aborts_NamingTheWait()
        {
            SplitC();
            MeshMergeOperation op = RequestMerge();
            AckAll(op.e1Pair);

            _clock.Now += 61f;
            _c.Tick();

            Assert.That(op.step, Is.EqualTo(MeshMergeStep.Aborted));
            Assert.That(op.endReason, Is.EqualTo("e1 gate not open in 60 s: evidence at f4240/3, before e1 f4240/5"));
            Assert.That(_lines[_lines.Count - 1], Is.EqualTo(L("reverted broadcast f4240/6")));
        }

        [Test]
        public void RetiringCellUnregisteringBeforeE2_Aborts()
        {
            SplitC();
            MeshMergeOperation op = RequestMerge();
            _transport.peers.Remove(ConnX);

            _c.Tick();

            Assert.That((op.step, op.endReason), Is.EqualTo((MeshMergeStep.Aborted, "C.1 unregistered")));
            Assert.That(_world.GetCellById("C.1").state, Is.EqualTo(MeshCellState.Active));
        }

        [Test]
        public void ARestartBeforeE2_Aborts()
        {
            SplitC();
            MeshMergeOperation op = RequestMerge();
            _transport.peers.Remove(ConnC);

            Bind(ConnC + 100, "C", "pc2");

            Assert.That((op.step, op.endReason), Is.EqualTo((MeshMergeStep.Aborted, "C restarted: incarnation pc -> pc2")));
            MeshLayoutMessage answer = _transport.layouts[_transport.layouts.Count - 1].message;
            Assert.That((answer.kind, answer.snapshot.pair), Is.EqualTo((MeshLayoutKind.HelloAnswer, _world.AppliedPair)), "the new C is answered with the reverted layout");
            Assert.That(answer.snapshot.entries.Find(e => e.id == "C").interest, Is.EqualTo(West));
        }

        [Test]
        public void SurvivorRestartingWhileE2IsStaged_GetsTheStagedE2_AndTheMergeGoesOn()
        {
            SplitC();
            MeshMergeOperation op = RequestMerge();
            ToE2Staged(op);
            _transport.peers.Remove(ConnC);

            Bind(ConnC + 100, "C", "pc2");

            MeshLayoutMessage answer = _transport.layouts[_transport.layouts.Count - 1].message;
            Assert.That((answer.kind, answer.targetIncarnation, answer.stepId, answer.snapshot.pair), Is.EqualTo((MeshLayoutKind.HelloAnswer, "pc2", "merge-1/e2", op.e2Pair)));
            Assert.That((op.step, op.survivorIncarnation), Is.EqualTo((MeshMergeStep.E2Staged, "pc2")));
            Ack("C", op.e2Pair);
            Assert.That(op.step, Is.EqualTo(MeshMergeStep.DrainGate));
        }

        [Test]
        public void SurvivorRestartingAfterE2_IsAnsweredWithTheCurrentLayout_AndTheMergeFinishes()
        {
            SplitC();
            MeshMergeOperation op = RequestMerge();
            ToDrainGate(op);
            _transport.peers.Remove(ConnC);

            Bind(ConnC + 100, "C", "pc2");

            MeshLayoutMessage answer = _transport.layouts[_transport.layouts.Count - 1].message;
            Assert.That((answer.kind, answer.snapshot.pair), Is.EqualTo((MeshLayoutKind.HelloAnswer, op.e2Pair)));
            Assert.That(op.step, Is.EqualTo(MeshMergeStep.DrainGate));
            _clock.Now += 5f;
            _c.OnLayoutEvidence(ConnX, Drained("C.1", "xc", op.e2Pair));
            Exit(op);
            AckAll(op.e3Pair);
            AckAll(op.tidyPair);
            Assert.That(op.step, Is.EqualTo(MeshMergeStep.Done));
            Assert.That(MergeLines(), Has.None.Contains("aborted"));
            Assert.That(MergeLines(), Has.None.Contains("frozen"));
        }

        [Test]
        public void SilentE2Ack_Freezes_AndRefusesTheNextOperation()
        {
            SplitC();
            MeshMergeOperation op = RequestMerge();
            ToE2Staged(op);
            Ack("C", op.e2Pair);

            _clock.Now += 61f;
            _c.Tick();

            Assert.That(op.step, Is.EqualTo(MeshMergeStep.Frozen));
            Assert.That(_lines[_lines.Count - 1], Is.EqualTo(L("frozen e2 not acknowledged in 60 s: no e2 ack from C.1")));
            Assert.That(_transport.retires.FindAll(r => r.message.reason == "merged into C"), Is.Empty);
            Assert.That(_host.removed, Is.Empty);
            Assert.That(Refused("C", "C.1"), Does.StartWith("merge-1 is frozen"));
            Assert.That(_c.RequestSplit("E", MeshSplitAxis.X, 200f, true, "E.1", out string error), Is.False);
            Assert.That(error, Does.StartWith("merge-1 is frozen"));
            Assert.That(_c.IsSplitFamily("C"), Is.True, "a frozen family stays out of the awake set");
            Assert.That(_store.TryLoad(out MeshCoordinatorStoreData data, out _), Is.True);
            Assert.That((data.opStep, data.opId), Is.EqualTo(("Frozen", "")));
        }

        [Test]
        public void RejectedE3_Freezes()
        {
            SplitC();
            MeshMergeOperation op = RequestMerge();
            ToRetiring(op);
            Exit(op);

            Ack("C", op.e3Pair, applied: false);

            Assert.That(op.step, Is.EqualTo(MeshMergeStep.Frozen));
            Assert.That(_lines[_lines.Count - 1], Is.EqualTo(L("frozen C rejected e3 f4240/7: refused")));
        }

        [Test]
        public void SilentTidyAck_Freezes()
        {
            Bind(ConnE, "E", "pe");
            SplitC();
            MeshMergeOperation op = RequestMerge();
            ToRetiring(op);
            Exit(op);
            AckAll(op.e3Pair);
            Assert.That(op.step, Is.EqualTo(MeshMergeStep.TidyBroadcast));
            _transport.peers.Remove(ConnC);   // the survivor never acks the tidy

            _clock.Now += 61f;
            _c.Tick();

            Assert.That(op.step, Is.EqualTo(MeshMergeStep.Frozen));
            Assert.That(_lines[_lines.Count - 1], Is.EqualTo(L("frozen tidy not acknowledged in 60 s: no tidy ack from C")));
        }

        [Test]
        public void ALongDrain_Straggles_NeverFreezes_AndNotesTheCellOnceAMinute()
        {
            SplitC();
            MeshMergeOperation op = RequestMerge();
            ToDrainGate(op);
            _c.OnLayoutEvidence(ConnX, Drained("C.1", "xc", op.e2Pair, false, new MeshDrainState { characters = 1, pendingAcks = 1 }));

            for (int i = 0; i < 20; ++i)
            {
                _clock.Now += 10f;
                _c.Tick();
            }

            Assert.That(op.step, Is.EqualTo(MeshMergeStep.DrainGate), "players are never kicked; the drain has no deadline");
            Assert.That(_lines.FindAll(l => l.StartsWith(L("drain straggling"), System.StringComparison.Ordinal)), Is.EqualTo(new[]
            {
                L("drain straggling 90 s: characters=1 pendingAcks=1"), L("drain straggling 120 s: characters=1 pendingAcks=1"),
                L("drain straggling 150 s: characters=1 pendingAcks=1"), L("drain straggling 180 s: characters=1 pendingAcks=1"),
            }));
            var notes = _transport.notes.FindAll(n => n.message.code == "retire-straggler");
            Assert.That(notes.Count, Is.EqualTo(2));
            Assert.That((notes[0].connection, notes[0].message.level, notes[0].message.subjectCellId), Is.EqualTo((ConnX, MeshNoteLevel.Error, "C.1")));
            Assert.That(_transport.retires.FindAll(r => r.message.reason == "merged into C"), Is.Empty);
        }

        [Test]
        public void AHungQuit_StragglesInTheRetireStep()
        {
            SplitC();
            MeshMergeOperation op = RequestMerge();
            ToRetiring(op);

            for (int i = 0; i < 12; ++i)
            {
                _clock.Now += 10f;
                _c.Tick();
            }

            Assert.That(op.step, Is.EqualTo(MeshMergeStep.Retiring));
            Assert.That(_lines.FindAll(l => l.StartsWith(L("retire straggling"), System.StringComparison.Ordinal)), Is.EqualTo(new[] { L("retire straggling 90 s"), L("retire straggling 120 s") }));
            Assert.That(Retires(ConnX).Count, Is.EqualTo(13), "the first Retire and one every 10 s");
            Assert.That(_transport.notes.FindAll(n => n.message.code == "retire-straggler").Count, Is.EqualTo(1));
        }
    }
}
