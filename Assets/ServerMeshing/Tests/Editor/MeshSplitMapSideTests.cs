using System.Collections.Generic;
using LiteNetLib.Utils;
using NUnit.Framework;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>The pure pieces of Slice 2b's map side: pacer, puppet queue, static loans, drain and evidence, retire, target check, puppet default.</summary>
    public class MeshSplitMapSideTests
    {
        private static readonly MeshRect PrevOwned = new MeshRect(-100f, -100f, 100f, 100f);

        // ---- pacer ----

        [Test]
        public void Pacer_DrainIsInThePreviousRectAndSlow_EverythingElseIsAMovementCrossing()
        {
            Assert.That(MeshHandoffPacer.IsDrain(PrevOwned, 50f, 0f, 0f), Is.True);
            Assert.That(MeshHandoffPacer.IsDrain(PrevOwned, 50f, 0f, 1.99f), Is.True);
            Assert.That(MeshHandoffPacer.IsDrain(PrevOwned, 50f, 0f, 2f), Is.False, "2 m/s and faster crosses by itself");
            Assert.That(MeshHandoffPacer.IsDrain(PrevOwned, 150f, 0f, 0f), Is.False, "outside the previous owned rectangle");
            Assert.That(MeshHandoffPacer.IsDrain(MeshRect.Empty, 0f, 0f, 0f), Is.False, "no shrink, no drain");
        }

        [Test]
        public void Pacer_AdmitsAboutTheRateOverTime()
        {
            MeshHandoffPacer pacer = new MeshHandoffPacer(7) { RatePerSecond = 5f };
            int total = 0;
            for (int tick = 0; tick < 100; ++tick)
                total += pacer.Admit(tick * 0.1f, 0, 50);
            Assert.That(total, Is.InRange(45, 56), "5 per second over 10 s, +-20 % jitter on each interval");
        }

        [Test]
        public void Pacer_NoBurstAfterIdle_AndTheCapHoldsInFlight()
        {
            MeshHandoffPacer pacer = new MeshHandoffPacer(1) { RatePerSecond = 5f, MaxConcurrent = 20 };
            Assert.That(pacer.Admit(100f, 0, 50), Is.LessThanOrEqualTo(2), "an idle spell carries over by one interval at most");
            Assert.That(pacer.Admit(200f, 20, 50), Is.EqualTo(0), "20 in flight toward the destination");
            Assert.That(pacer.Admit(300f, 19, 50), Is.EqualTo(1), "one slot left");
        }

        [Test]
        public void Pacer_UnpacedAdmitsWhatTheCapAllows()
        {
            MeshHandoffPacer pacer = new MeshHandoffPacer(1) { RatePerSecond = 0f, MaxConcurrent = 3 };
            Assert.That(pacer.Admit(0f, 1, 10), Is.EqualTo(2));
            pacer.MaxConcurrent = 0;
            Assert.That(pacer.Admit(0f, 100, 10), Is.EqualTo(10));
        }

        [Test]
        public void Pacer_NoRedrainWithinTwoSeconds()
        {
            MeshHandoffPacer pacer = new MeshHandoffPacer(1);
            pacer.NoteDrained("p1", 10f);
            Assert.That(pacer.RecentlyDrained("p1", 11.9f), Is.True);
            Assert.That(pacer.RecentlyDrained("p1", 12f), Is.False);
            Assert.That(pacer.RecentlyDrained("p2", 10f), Is.False);
            pacer.Prune(12.5f);
            Assert.That(pacer.RecentlyDrained("p1", 10.5f), Is.False, "pruned");
        }

        // ---- the cell's view of an operation ----

        [Test]
        public void OpInProgress_PendingDrainingOrInterestBeyondOwned()
        {
            MeshRect a = new MeshRect(-100f, -100f, 0f, 100f), r = new MeshRect(0f, -100f, 100f, 100f);
            MeshCell settled = new MeshCell { state = MeshCellState.Active, owned = a, interest = a };
            Assert.That(MeshSplitState.OpInProgress(new List<MeshCell> { settled }), Is.False);
            Assert.That(MeshSplitState.OpInProgress(new List<MeshCell> { settled, new MeshCell { state = MeshCellState.Pending, owned = MeshRect.Empty, interest = r } }), Is.True);
            Assert.That(MeshSplitState.OpInProgress(new List<MeshCell> { new MeshCell { state = MeshCellState.Active, owned = a, interest = PrevOwned } }), Is.True, "e2: P owns P minus R, interest P");
            Assert.That(MeshSplitState.OpInProgress(new List<MeshCell> { new MeshCell { state = MeshCellState.Draining, owned = MeshRect.Empty, interest = MeshRect.Empty } }), Is.True);
            Assert.That(MeshSplitState.OpInProgress(null), Is.False);
        }

        [Test]
        public void GivenAway_IsTheStripACutLeaves()
        {
            Assert.That(MeshSplitState.TryGivenAway(PrevOwned, new MeshRect(-100f, -100f, 0f, 100f), out MeshRect east), Is.True);
            Assert.That(east, Is.EqualTo(new MeshRect(0f, -100f, 100f, 100f)));
            Assert.That(MeshSplitState.TryGivenAway(PrevOwned, new MeshRect(0f, -100f, 100f, 100f), out MeshRect west), Is.True);
            Assert.That(west, Is.EqualTo(new MeshRect(-100f, -100f, 0f, 100f)));
            Assert.That(MeshSplitState.TryGivenAway(PrevOwned, new MeshRect(-100f, 20f, 100f, 100f), out MeshRect south), Is.True);
            Assert.That(south, Is.EqualTo(new MeshRect(-100f, -100f, 100f, 20f)));
            Assert.That(MeshSplitState.TryGivenAway(PrevOwned, MeshRect.Empty, out MeshRect all), Is.True);
            Assert.That(all, Is.EqualTo(PrevOwned));
            Assert.That(MeshSplitState.TryGivenAway(PrevOwned, new MeshRect(-50f, -50f, 50f, 50f), out _), Is.False, "not one line");
            Assert.That(MeshSplitState.TryGivenAway(new MeshRect(-100f, -100f, 0f, 100f), PrevOwned, out _), Is.False, "a growth");
            Assert.That(MeshSplitState.TryGivenAway(PrevOwned, PrevOwned, out _), Is.False);
        }

        // ---- puppet spawn queue ----

        [Test]
        public void SpawnQueue_ServesNearestFirst_AndFoldsLaterSnapshots()
        {
            MeshPuppetSpawnQueue<int> queue = new MeshPuppetSpawnQueue<int>();
            Assert.That(queue.Enqueue("far", 1, 50f), Is.True);
            Assert.That(queue.Enqueue("near", 2, 5f), Is.True);
            Assert.That(queue.Enqueue("mid", 3, 20f), Is.True);
            Assert.That(queue.Enqueue("far", 4, 1f), Is.False, "a later snapshot of a queued id is folded in");
            Assert.That(queue.Count, Is.EqualTo(3));
            List<(string, int)> served = new List<(string, int)>();
            while (queue.TryPop(out string id, out int payload))
                served.Add((id, payload));
            Assert.That(served, Is.EqualTo(new List<(string, int)> { ("far", 4), ("near", 2), ("mid", 3) }));
            Assert.That(queue.Count, Is.EqualTo(0));
        }

        [Test]
        public void SpawnQueue_PromotionArrivalAndRemoveConsumeAnEntry()
        {
            MeshPuppetSpawnQueue<int> queue = new MeshPuppetSpawnQueue<int>();
            queue.Enqueue("promoted", 1, 1f);
            queue.Enqueue("arrived", 2, 2f);
            queue.Enqueue("removed", 3, 3f);
            queue.Enqueue("kept", 4, 4f);
            Assert.That(queue.Consume("promoted"), Is.True);
            Assert.That(queue.Consume("arrived"), Is.True);
            Assert.That(queue.Consume("removed"), Is.True);
            Assert.That(queue.Consume("removed"), Is.False, "once");
            Assert.That(queue.Consume("never"), Is.False);
            Assert.That(queue.Contains("arrived"), Is.False);
            Assert.That(queue.TryPop(out string id, out int payload), Is.True);
            Assert.That((id, payload), Is.EqualTo(("kept", 4)));
            Assert.That(queue.TryPop(out _, out _), Is.False);
        }

        [Test]
        public void SpawnQueue_AConsumedIdCanBeQueuedAgain()
        {
            MeshPuppetSpawnQueue<int> queue = new MeshPuppetSpawnQueue<int>();
            queue.Enqueue("a", 1, 1f);
            queue.Consume("a");
            Assert.That(queue.Enqueue("a", 2, 1f), Is.True);
            Assert.That(queue.TryPop(out string id, out int payload), Is.True);
            Assert.That((id, payload), Is.EqualTo(("a", 2)));
            Assert.That(queue.TryPop(out _, out _), Is.False, "the consumed entry is not served");
        }

        [Test]
        public void SpawnQueue_APromotionTakesItsEntryOutOfTurn()
        {
            MeshPuppetSpawnQueue<int> queue = new MeshPuppetSpawnQueue<int>();
            queue.Enqueue("near", 1, 1f);
            queue.Enqueue("handed", 2, 50f);
            queue.Enqueue("handed", 3, 40f);
            Assert.That(queue.TryTake("handed", out int payload), Is.True);
            Assert.That(payload, Is.EqualTo(3), "the latest snapshot");
            Assert.That(queue.TryTake("handed", out _), Is.False);
            Assert.That(queue.TryPop(out string id, out _), Is.True);
            Assert.That(id, Is.EqualTo("near"));
            Assert.That(queue.TryPop(out _, out _), Is.False);
        }

        // ---- static handoff ----

        [Test]
        public void StaticReceipt_DedupesByMeshId_ThenPromotesOrSpawnsOrFails()
        {
            Assert.That(MeshStaticHandoffRules.Receive(true, true, true), Is.EqualTo(MeshStaticReceipt.AckDuplicate));
            Assert.That(MeshStaticHandoffRules.Receive(false, true, false), Is.EqualTo(MeshStaticReceipt.Promote));
            Assert.That(MeshStaticHandoffRules.Receive(false, false, true), Is.EqualTo(MeshStaticReceipt.Spawn));
            Assert.That(MeshStaticHandoffRules.Receive(false, false, false), Is.EqualTo(MeshStaticReceipt.Fail));
        }

        [Test]
        public void StaticLoans_OneLoanPerMeshId_SettledOnlyByItsDestination()
        {
            MeshStaticLoanBook<string> book = new MeshStaticLoanBook<string>();
            Assert.That(book.TryBegin("s1", "C.1", 0f, "demoted"), Is.True);
            Assert.That(book.TryBegin("s1", "C.1", 0.5f, "again"), Is.False, "in flight: no second send");
            Assert.That(book.CountToward("c.1"), Is.EqualTo(1), "zones compare ignoring case");
            Assert.That(book.TrySettle("s1", "E", out _), Is.False, "an ack from another cell");
            Assert.That(book.TrySettle("s1", "C.1", out MeshStaticLoanBook<string>.Loan loan), Is.True);
            Assert.That(loan.data, Is.EqualTo("demoted"));
            Assert.That(book.IsInFlight("s1"), Is.False);
            Assert.That(book.TryBegin("s1", "C.1", 1f, "next"), Is.True, "settled: may go again (it came back)");
        }

        [Test]
        public void StaticLoans_AFailureRepromotesAndBacksOff()
        {
            MeshStaticLoanBook<string> book = new MeshStaticLoanBook<string>();
            book.TryBegin("s1", "C.1", 0f, "x");
            Assert.That(book.TryFail("s1", "C.1", 1f, 10f, out MeshStaticLoanBook<string>.Loan failed), Is.True);
            Assert.That(failed.meshId, Is.EqualTo("s1"));
            Assert.That(book.InBackoff("s1", 5f), Is.True);
            Assert.That(book.TryBegin("s1", "C.1", 5f, "y"), Is.False);
            Assert.That(book.TryBegin("s1", "C.1", 11f, "z"), Is.True);
        }

        [Test]
        public void StaticLoans_PastTheGuardOrTowardARestartedCellAreTakenBack()
        {
            MeshStaticLoanBook<string> book = new MeshStaticLoanBook<string>();
            book.TryBegin("old", "C.1", 0f, "a");
            book.TryBegin("new", "C.1", 15f, "b");
            book.TryBegin("other", "E", 15f, "c");
            List<MeshStaticLoanBook<string>.Loan> taken = new List<MeshStaticLoanBook<string>.Loan>();
            book.TakeExpired(21f, 20f, 10f, taken);
            Assert.That(taken.ConvertAll(l => l.meshId), Is.EqualTo(new List<string> { "old" }));
            Assert.That(book.InBackoff("old", 25f), Is.True);
            taken.Clear();
            book.TakeToward("C.1", 16f, taken);
            Assert.That(taken.ConvertAll(l => l.meshId), Is.EqualTo(new List<string> { "new" }));
            Assert.That(book.Count, Is.EqualTo(1));
            Assert.That(book.CountToward(null), Is.EqualTo(1));
        }

        // ---- drain counting and evidence ----

        [Test]
        public void Drain_CountsInTheGivenAwayRectangle_PastTheHysteresis()
        {
            MeshRect owned = new MeshRect(-100f, -100f, 0f, 100f), given = new MeshRect(0f, -100f, 100f, 100f);
            Assert.That(MeshSplitEvidence.InGivenAway(given, owned, 50f, 0f, 1f), Is.True);
            Assert.That(MeshSplitEvidence.InGivenAway(given, owned, 0.5f, 0f, 1f), Is.False, "on the line, inside the hysteresis: still legitimately the parent's");
            Assert.That(MeshSplitEvidence.InGivenAway(given, owned, 1f, 0f, 1f), Is.True);
            Assert.That(MeshSplitEvidence.InGivenAway(given, owned, -50f, 0f, 1f), Is.False, "in what it still owns");
            Assert.That(MeshSplitEvidence.InGivenAway(given, owned, 150f, 0f, 1f), Is.False, "beyond the given-away rectangle");
            Assert.That(MeshSplitEvidence.InGivenAway(given, MeshRect.Empty, 0f, 0f, 1f), Is.True, "a cell that gave everything away");
        }

        [Test]
        public void DrainWindow_KeepsTheMostOfEachCount()
        {
            MeshDrainWindow window = new MeshDrainWindow();
            Assert.That(window.HasSample, Is.False);
            window.Sample(new MeshDrainCounts { characters = 3, monsters = 1 });
            window.Sample(new MeshDrainCounts { characters = 0, monsters = 2, pendingHandoffs = 1 });
            Assert.That((window.Max.characters, window.Max.monsters, window.Max.pendingHandoffs, window.Max.Total), Is.EqualTo((3, 2, 1, 6)));
            window.Reset();
            window.Sample(default);
            Assert.That((window.HasSample, window.Max.Total), Is.EqualTo((true, 0)), "all zero over a window opens the gate");
        }

        [Test]
        public void Evidence_NeighbourHelloAgeAndDatagrams()
        {
            MeshNeighbourEvidence none = MeshSplitEvidence.Neighbour("C", false, 0f, 10f, 0, 0);
            Assert.That((none.zoneId, none.helloAgeSeconds, none.datagramsSinceHello), Is.EqualTo(("C", -1f, 0)));
            MeshNeighbourEvidence heard = MeshSplitEvidence.Neighbour("C", true, 7f, 10f, 100, 130);
            Assert.That((heard.helloAgeSeconds, heard.datagramsSinceHello), Is.EqualTo((3f, 30)));
            Assert.That(MeshSplitEvidence.Neighbour("C", true, 7f, 10f, 100, -1).datagramsSinceHello, Is.EqualTo(0), "count unknown");
        }

        [Test]
        public void Evidence_SentByADynamicCellWhileAnOperationRunsOrItIsPending()
        {
            Assert.That(MeshSplitEvidence.ShouldSend(true, true, true, MeshCellState.Active), Is.True);
            Assert.That(MeshSplitEvidence.ShouldSend(true, true, false, MeshCellState.Pending), Is.True);
            Assert.That(MeshSplitEvidence.ShouldSend(true, true, false, MeshCellState.Active), Is.False, "idle");
            Assert.That(MeshSplitEvidence.ShouldSend(false, true, true, MeshCellState.Static), Is.False, "a Static map never");
            Assert.That(MeshSplitEvidence.ShouldSend(true, false, true, MeshCellState.Pending), Is.False, "no cell yet");
        }

        [Test]
        public void Evidence_AssembledAndRoundTripped()
        {
            MeshRect given = new MeshRect(0f, -100f, 100f, 100f);
            List<MeshNeighbourEvidence> neighbours = new List<MeshNeighbourEvidence> { MeshSplitEvidence.Neighbour("C", true, 1f, 4f, 5, 9) };
            MeshLayoutEvidenceMessage built = MeshSplitEvidence.Assemble("C", "inc", new MeshLayoutPair(2, 5), 12f, 10f, neighbours, 3, 0.1f, 9.5f, 16.67f,
                true, given, new MeshDrainCounts { characters = 2, staticsAndDrops = 1 });
            Assert.That(built.windowStartedAfterApplySeconds, Is.EqualTo(2f));
            NetDataWriter writer = new NetDataWriter();
            built.Serialize(writer);
            Assert.That(MeshWholeMessage.TryRead(new NetDataReader(writer.CopyData()), out MeshLayoutEvidenceMessage read, out string error), Is.True, error);
            Assert.That((read.cellId, read.incarnation, read.pair, read.prewarmQueue, read.shrunk, read.givenAway), Is.EqualTo(("C", "inc", new MeshLayoutPair(2, 5), 3, true, given)));
            Assert.That((read.drain.characters, read.drain.staticsAndDrops, read.drain.Total), Is.EqualTo((2, 1, 3)));
            Assert.That((read.neighbours.Count, read.neighbours[0].zoneId, read.neighbours[0].helloAgeSeconds, read.neighbours[0].datagramsSinceHello), Is.EqualTo((1, "C", 3f, 4)));

            MeshLayoutEvidenceMessage unshrunk = MeshSplitEvidence.Assemble("C.1", "i", MeshLayoutPair.None, 0f, 5f, null, 0, 0f, 0f, 0f, false, given, new MeshDrainCounts { characters = 9 });
            Assert.That((unshrunk.givenAway, unshrunk.drain.Total, unshrunk.neighbours.Count, unshrunk.windowStartedAfterApplySeconds), Is.EqualTo((MeshRect.Empty, 0, 0, -5f)));
        }

        // ---- retire, staged target, puppet default ----

        [Test]
        public void Retire_OnlyANamedCellThatOwnsNothingYet()
        {
            Assert.That(MeshRetireRule.ShouldRetire("C.1", "i1", "c.1", "i1", false, MeshCellState.Pending), Is.True, "no layout yet");
            Assert.That(MeshRetireRule.ShouldRetire("C.1", "i1", "C.1", "i1", true, MeshCellState.Pending), Is.True, "Pending after e1");
            Assert.That(MeshRetireRule.ShouldRetire("C.1", "i1", "C.1", "i1", true, MeshCellState.Active), Is.False, "owns something: 2c drains it first");
            Assert.That(MeshRetireRule.ShouldRetire("C.1", "i0", "C.1", "i1", false, MeshCellState.Pending), Is.False, "another incarnation");
            Assert.That(MeshRetireRule.ShouldRetire("C.2", "i1", "C.1", "i1", false, MeshCellState.Pending), Is.False, "another cell");
        }

        [Test]
        public void StagedAndAnswer_AreTargetChecked_BroadcastIsNot()
        {
            Assert.That(MeshLayoutTarget.Accepts(MeshLayoutKind.Broadcast, "", "", "C", "i"), Is.True);
            Assert.That(MeshLayoutTarget.Accepts(MeshLayoutKind.Staged, "c.1", "i", "C.1", "i"), Is.True);
            Assert.That(MeshLayoutTarget.Accepts(MeshLayoutKind.Staged, "C.1", "old", "C.1", "i"), Is.False, "meant for a predecessor");
            Assert.That(MeshLayoutTarget.Accepts(MeshLayoutKind.Staged, "C.1", "i", "C", "i"), Is.False, "meant for another cell");
            Assert.That(MeshLayoutTarget.Accepts(MeshLayoutKind.HelloAnswer, "C", "i", "C", "i"), Is.True);
            Assert.That(MeshLayoutTarget.Accepts(MeshLayoutKind.HelloAnswer, "C", "x", "C", "i"), Is.False);
        }

        [Test]
        public void PuppetMotion_EnvironmentWins_ThenTheDynamicMapDefault_ThenTheInspector()
        {
            Assert.That(MeshPuppetDefault.Resolve("steered", true, MeshPuppetMotion.Steered, out MeshPuppetMotionSource s1), Is.EqualTo(MeshPuppetMotion.Steered));
            Assert.That(s1, Is.EqualTo(MeshPuppetMotionSource.Environment));
            Assert.That(MeshPuppetDefault.Resolve(null, true, MeshPuppetMotion.Steered, out MeshPuppetMotionSource s2), Is.EqualTo(MeshPuppetMotion.Kinematic));
            Assert.That(s2, Is.EqualTo(MeshPuppetMotionSource.DynamicMapDefault));
            Assert.That(MeshPuppetDefault.Resolve("bogus", true, MeshPuppetMotion.Steered, out MeshPuppetMotionSource s3), Is.EqualTo(MeshPuppetMotion.Kinematic), "an unreadable value is ignored");
            Assert.That(s3, Is.EqualTo(MeshPuppetMotionSource.DynamicMapDefault));
            Assert.That(MeshPuppetDefault.Resolve("", false, MeshPuppetMotion.Steered, out MeshPuppetMotionSource s4), Is.EqualTo(MeshPuppetMotion.Steered), "a Static map keeps the inspector's");
            Assert.That(s4, Is.EqualTo(MeshPuppetMotionSource.Inspector));
            Assert.That(MeshPuppetDefault.Resolve("kinematic", false, MeshPuppetMotion.Steered, out _), Is.EqualTo(MeshPuppetMotion.Kinematic));
        }
    }
}
