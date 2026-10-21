using System.Collections.Generic;
using NUnit.Framework;
using static MultiplayerARPG.MMO.Mesh.Tests.MeshFakeWorlds;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>The unfreeze's pure parts (slices/unfreeze_design.md decisions 1, 3-5): the command, the fence floor, the classification, the retire version, the dead member.</summary>
    public class MeshUnfreezeRulesTests
    {
        private static readonly MeshRect West = new MeshRect(-100f, -100f, 0f, 100f);
        private static readonly MeshRect East = new MeshRect(0f, -100f, 100f, 100f);

        [Test]
        public void TheCommand_ParsesAndRefusesWrongWordCounts()
        {
            Assert.That(MeshLayoutCommand.TryParse("7 unfreeze C", out MeshLayoutCommand command, out string error), Is.True, error);
            Assert.That((command.seq, command.kind, command.verb, command.cellId), Is.EqualTo((7L, MeshLayoutCommandKind.Unfreeze, "unfreeze", "C")));
            Assert.That(MeshLayoutCommand.TryParse("7 UNFREEZE C", out command, out _), Is.True);
            Assert.That(MeshLayoutCommand.TryParse("7 unfreeze", out _, out error), Is.False);
            Assert.That(error, Does.StartWith("layout command 7 has 2 words, an unfreeze takes 3"));
            Assert.That(MeshLayoutCommand.TryParse("7 unfreeze C E", out _, out error), Is.False);
            Assert.That(error, Does.Contain("an unfreeze takes 3"));
            Assert.That(MeshLayoutCommand.Usage, Does.EndWith("| <seq> unfreeze <base>"));
        }

        [Test]
        public void TheFenceFloor_FencesEveryIncarnationStartedBefore_AndNoneAfter()
        {
            const long now = 638_650_000_000_000_000L;
            string floor = MeshUnfreeze.FenceFloor(now);
            Assert.That(floor, Is.EqualTo(now.ToString("x")));
            MeshFencedSet fenced = new MeshFencedSet();
            fenced.Add("C.1", floor);
            Assert.That(fenced.IsFenced("C.1", (now - 1).ToString("x")), Is.True);
            Assert.That(fenced.IsFenced("C.1", floor), Is.True);
            Assert.That(fenced.IsFenced("C.1", (now + 1).ToString("x")), Is.False);
            Assert.That(fenced.IsFenced("C.2", (now - 1).ToString("x")), Is.False);
        }

        [Test]
        public void Classify_CollapsesAnOwningDeadCell_RetiresOneOwningNothing_AndLeavesTheRest()
        {
            MeshLayoutSnapshot layout = Snapshot(9,
                Active("C", "C", West), Active("C.1", "C", East),
                Entry("C.2", "C", MeshCellState.Pending, MeshRect.Empty, East),
                Entry("C.3", "C", MeshCellState.Draining, MeshRect.Empty, East),
                Entry("C.4", "C", MeshCellState.Retired, MeshRect.Empty, MeshRect.Empty),
                Entry("C.5", "C", MeshCellState.Active, MeshRect.Empty, MeshRect.Empty),
                Active("E.1", "E", East));
            List<string> collapse = new List<string>(), retire = new List<string>();
            MeshUnfreeze.Classify(layout, "C", new[] { "C.1", "C.2", "C.3", "C.4", "C.5", "C.9", "E.1" }, collapse, retire);
            Assert.That(collapse, Is.EqualTo(new[] { "C.1" }));
            Assert.That(retire, Is.EqualTo(new[] { "C.2", "C.3", "C.5" }));
        }

        [Test]
        public void Retired_RetiresTheIdsAtTheNewPair_AndLeavesTheSourceUntouched()
        {
            MeshLayoutSnapshot layout = Snapshot(9, Active("C", "C", West), Entry("C.2", "C", MeshCellState.Pending, MeshRect.Empty, East));
            MeshLayoutSnapshot retired = MeshUnfreeze.Retired(layout, new[] { "C.2" }, new MeshLayoutPair(1, 10));
            Assert.That(retired.pair, Is.EqualTo(new MeshLayoutPair(1, 10)));
            MeshLayoutEntry entry = retired.entries.Find(e => e.id == "C.2");
            Assert.That((entry.state, entry.owned, entry.interest), Is.EqualTo((MeshCellState.Retired, MeshRect.Empty, MeshRect.Empty)));
            Assert.That(layout.entries.Find(e => e.id == "C.2").state, Is.EqualTo(MeshCellState.Pending));
            Assert.That(retired.entries.Find(e => e.id == "C").owned, Is.EqualTo(West));
        }

        [Test]
        public void DeadMember_IsAMemberOfTheStoredOperationAmongTheDead()
        {
            MeshStoredOperation split = new MeshStoredOperation { kind = MeshStoredOperation.SplitKind, opId = "split-1", parentId = "C", childId = "C.1" };
            MeshStoredOperation merge = new MeshStoredOperation { kind = MeshStoredOperation.MergeKind, opId = "merge-1", survivorId = "C.2", retiringId = "C.3" };
            Assert.That(MeshUnfreeze.DeadMember(split, new[] { "c.1" }), Is.EqualTo("C.1"));
            Assert.That(MeshUnfreeze.DeadMember(split, new[] { "C.2" }), Is.Null);
            Assert.That(MeshUnfreeze.DeadMember(merge, new[] { "C.2" }), Is.EqualTo("C.2"));
            Assert.That(MeshUnfreeze.DeadMember(null, new[] { "C.2" }), Is.Null);
        }
    }
}
