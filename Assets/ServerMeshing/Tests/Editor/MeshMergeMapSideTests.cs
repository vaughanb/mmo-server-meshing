using LiteNetLib.Utils;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>The pure pieces of Slice 2c's map side: retire of a Draining cell, the enter-game rule, idle connections, the prune book, the pacer.</summary>
    public class MeshMergeMapSideTests
    {
        // ---- retire (decision 13) ----

        [Test]
        public void Retire_ADrainingCellQuitsOnlyOnceDrained()
        {
            Assert.That(MeshRetireRule.Decide("C.1", "i1", "c.1", "i1", true, MeshCellState.Draining, true), Is.EqualTo(MeshRetireDecision.QuitDrained));
            Assert.That(MeshRetireRule.Decide("C.1", "i1", "C.1", "i1", true, MeshCellState.Draining, false), Is.EqualTo(MeshRetireDecision.AwaitDrain), "keeps handing off; never kicks");
            Assert.That(MeshRetireRule.Decide("C.1", "i0", "C.1", "i1", true, MeshCellState.Draining, true), Is.EqualTo(MeshRetireDecision.StayUp), "another incarnation");
            Assert.That(MeshRetireRule.Decide("C.2", "i1", "C.1", "i1", true, MeshCellState.Draining, true), Is.EqualTo(MeshRetireDecision.StayUp), "another cell");
            Assert.That(MeshRetireRule.Decide("C.1", "i1", "C.1", "i1", true, MeshCellState.Active, true), Is.EqualTo(MeshRetireDecision.StayUp), "an Active cell is not merged away");
            Assert.That(MeshRetireRule.Decide("C", "i1", "C", "i1", true, MeshCellState.Static, true), Is.EqualTo(MeshRetireDecision.StayUp));
        }

        [Test]
        public void Retire_TheSplitRetireOfACellThatOwnsNothingIsUnchanged()
        {
            Assert.That(MeshRetireRule.Decide("C.1", "i1", "C.1", "i1", false, MeshCellState.Pending, false), Is.EqualTo(MeshRetireDecision.RetireUnowned), "no layout yet");
            Assert.That(MeshRetireRule.Decide("C.1", "i1", "C.1", "i1", true, MeshCellState.Pending, false), Is.EqualTo(MeshRetireDecision.RetireUnowned), "Pending after e1");
            Assert.That(MeshRetireRule.ShouldRetire("C.1", "i1", "C.1", "i1", true, MeshCellState.Draining), Is.False, "2b's rule never retires a Draining cell by itself");
        }

        [Test]
        public void Straggling_FirstAtTheMaximumThenEveryThirtySeconds()
        {
            float next = 0f;
            Assert.That(MeshDrainRules.StragglingDue(100f, 189f, 90f, 30f, ref next), Is.False);
            Assert.That(MeshDrainRules.StragglingDue(100f, 190f, 90f, 30f, ref next), Is.True);
            Assert.That(MeshDrainRules.StragglingDue(100f, 191f, 90f, 30f, ref next), Is.False);
            Assert.That(MeshDrainRules.StragglingDue(100f, 219.9f, 90f, 30f, ref next), Is.False);
            Assert.That(MeshDrainRules.StragglingDue(100f, 220f, 90f, 30f, ref next), Is.True);
        }

        // ---- arrivals on a Draining cell (decision 12) ----

        [Test]
        public void EnterGame_ADrainingCellTakesOnlyWhatItHandsOnOrTakesIn()
        {
            Assert.That(MeshDrainRules.AcceptsEnterGame(false, false, false, false, false), Is.True, "not Draining: the kit decides");
            Assert.That(MeshDrainRules.AcceptsEnterGame(true, false, false, false, false), Is.False, "a stale client's fresh login");
            Assert.That(MeshDrainRules.AcceptsEnterGame(true, true, false, false, false), Is.True, "its own handoff awaiting the ack: the client falls back here");
            Assert.That(MeshDrainRules.AcceptsEnterGame(true, false, true, false, false), Is.True, "rolled back: the client returns");
            Assert.That(MeshDrainRules.AcceptsEnterGame(true, false, false, true, false), Is.True, "announced by a neighbour's HandoffNotice");
            Assert.That(MeshDrainRules.AcceptsEnterGame(true, false, false, false, true), Is.True, "a puppet held: the notice can land after the enter-game");
        }

        [Test]
        public void EnterGame_IdsArePeekedWithoutMovingTheReader()
        {
            NetDataWriter writer = new NetDataWriter();
            writer.Put((byte)7); // something the reader has already consumed
            writer.Put("user-1");
            writer.Put("token");
            writer.Put("char-1");
            writer.Put(42);
            NetDataReader reader = new NetDataReader(writer.CopyData());
            Assert.That(reader.GetByte(), Is.EqualTo((byte)7));
            int position = reader.Position;
            Assert.That(MeshDrainRules.TryPeekEnterGameIds(reader, out string userId, out string characterId), Is.True);
            Assert.That((userId, characterId, reader.Position), Is.EqualTo(("user-1", "char-1", position)));
            Assert.That((reader.GetString(), reader.GetString(), reader.GetString(), reader.GetInt()), Is.EqualTo(("user-1", "token", "char-1", 42)), "the kit reads it all as before");

            NetDataWriter shortWriter = new NetDataWriter();
            shortWriter.Put("user-only");
            NetDataReader shortReader = new NetDataReader(shortWriter.CopyData());
            Assert.That(MeshDrainRules.TryPeekEnterGameIds(shortReader, out _, out string none), Is.False);
            Assert.That((none, shortReader.Position), Is.EqualTo(((string)null, 0)));
            Assert.That(MeshDrainRules.TryPeekEnterGameIds(null, out _, out _), Is.False);
        }

        // ---- idle connections (decision 9) ----

        [Test]
        public void IdleConnection_NoCharacterAndNoPendingSpawnForTenSeconds()
        {
            Assert.That(MeshDrainRules.IsIdleConnection(false, false), Is.True);
            Assert.That(MeshDrainRules.IsIdleConnection(true, false), Is.False);
            Assert.That(MeshDrainRules.IsIdleConnection(false, true), Is.False, "entering: the kit spawns it at the ready step");
            Assert.That(MeshDrainRules.IdleExpired(5f, 14.9f), Is.False);
            Assert.That(MeshDrainRules.IdleExpired(5f, 15f), Is.True);
        }

        // ---- neighbours of a retired cell (decision 14) ----

        [Test]
        public void PruneBook_OneLinePerZone_CountingWhatLinkLossReleasedWhileDraining()
        {
            MeshRetiredZoneBook book = new MeshRetiredZoneBook();
            book.NoteReleased("C.1", true, 2);
            book.NoteReleased("c.1", true, 1);
            book.NoteReleased("C.1", false, 5); // released while the zone was not Draining: not this retire's
            book.NoteReleased("E", true, 0);
            Assert.That(book.ReleasedWhileDraining("C.1"), Is.EqualTo(3));
            Assert.That(book.IsRetired("C.1"), Is.False);
            Assert.That(book.TryRetire("C.1", 1, out int total), Is.True);
            Assert.That(total, Is.EqualTo(4), "one released at the prune plus three by link loss");
            Assert.That(book.IsRetired("c.1"), Is.True, "cell ids compare ignoring case");
            Assert.That(book.TryRetire("C.1", 0, out _), Is.False, "a later layout naming it Retired again logs nothing");
            book.NoteReleased("C.1", true, 9);
            Assert.That(book.ReleasedWhileDraining("C.1"), Is.EqualTo(0), "a pruned zone counts nothing more");
            Assert.That(book.Count, Is.EqualTo(1));
        }

        [Test]
        public void PruneBook_CleanRetireIsInfo_AReusedIdPrunesAgain()
        {
            MeshRetiredZoneBook book = new MeshRetiredZoneBook();
            Assert.That(book.TryRetire("C.1", 0, out int clean), Is.True);
            Assert.That((clean, MeshRetiredZoneBook.PruneIsWarning(clean)), Is.EqualTo((0, false)));
            Assert.That(MeshRetiredZoneBook.PruneLine("C.1", clean), Is.EqualTo("Retired zone C.1: link closed, 0 record(s) released"));
            Assert.That(MeshRetiredZoneBook.PruneIsWarning(2), Is.True);
            Assert.That(book.Revive("C.1"), Is.True, "a split reused the id (decision 7)");
            Assert.That(book.IsRetired("C.1"), Is.False);
            Assert.That(book.TryRetire("C.1", 2, out int again), Is.True);
            Assert.That(again, Is.EqualTo(2));
            book.Clear();
            Assert.That(book.Count, Is.EqualTo(0));
        }

        // ---- pacing (decisions 4 and 11) ----

        [Test]
        public void Pacer_OwningNothing_OrdersByThePreviousRectangle()
        {
            MeshRect prev = new MeshRect(0f, -100f, 100f, 100f);
            float nearEdge = MeshHandoffPacer.DrainDepth(MeshRect.Empty, prev, 5f, 0f);
            float centre = MeshHandoffPacer.DrainDepth(MeshRect.Empty, prev, 50f, 0f);
            Assert.That(nearEdge, Is.EqualTo(5f));
            Assert.That(centre, Is.EqualTo(50f));
            Assert.That(nearEdge, Is.LessThan(centre), "nearest the edge first, not every entity tied at infinity");
            Assert.That(float.IsInfinity(MeshHandoffPacer.DrainDepth(MeshRect.Empty, prev, 70f, 30f)), Is.False);
            Assert.That(MeshHandoffPacer.DrainDepth(MeshRect.Empty, MeshRect.Empty, 70f, 30f), Is.EqualTo(0f));
        }

        [Test]
        public void Pacer_OwningSomething_OrdersAsBefore()
        {
            MeshRect owned = new MeshRect(-100f, -100f, 0f, 100f);
            MeshRect prev = new MeshRect(-100f, -100f, 100f, 100f);
            Assert.That(MeshHandoffPacer.DrainDepth(owned, prev, 10f, 0f), Is.EqualTo(-owned.DistanceToEdge(10f, 0f)));
            Assert.That(MeshHandoffPacer.DrainDepth(owned, prev, 10f, 0f), Is.LessThan(MeshHandoffPacer.DrainDepth(owned, prev, 60f, 0f)));
        }

        [Test]
        public void Pacer_SplitDrainStateEndsWhenTheOperationDoes()
        {
            Assert.That(MeshSplitState.EndsSplitDrain(true, false), Is.True, "the split's e3 settled: the base drops its split-era state");
            Assert.That(MeshSplitState.EndsSplitDrain(true, true), Is.False, "still under way");
            Assert.That(MeshSplitState.EndsSplitDrain(false, true), Is.False, "a merge's e1 starts a new one");
            Assert.That(MeshSplitState.EndsSplitDrain(false, false), Is.False);
        }
    }
}
