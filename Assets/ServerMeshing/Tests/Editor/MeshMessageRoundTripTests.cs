// Server Meshing add-on — the add-on's rows in the kit's generic message round trip (MessageRoundTripTests, which sends
// every INetSerializable type in the assembly, the add-on's included), and the branches of its messages the generic
// filler does not take.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MultiplayerARPG.MMO.Mesh;
using NUnit.Framework;
using UnityEngine;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// The add-on's entries in the round trip's tables, kept beside the add-on rather than in the kit copy's test. Static
    /// field initializers run before this constructor, so the tables exist when it adds to them.
    /// </summary>
    public partial class MessageRoundTripTests
    {
        static MessageRoundTripTests()
        {
            // A ghost snapshot writes its full rotation only for a building and its items only for a container, which is
            // the branch that writes the most; the age is the receiver's own measurement. Every kind is in
            // MultiplayerARPG.MMO.Mesh.Tests.MeshMessageBranchTests.
            Branches[typeof(GhostSnapshotMessage)] = o => TestReflection.SetField(o, "kind", GhostKind.Container);
            NotRoundTripped.Add(typeof(GhostSnapshotMessage), Wire.NotWritten, BranchOnly, "fullRotation");
            NotRoundTripped.Add(typeof(GhostSnapshotMessage), Wire.NotWritten, "receiver only: how late the snapshot arrived against its link's fastest, measured where it is read (DESIGN 1.19)", "ageSeconds");
            // An occupancy grid is LayerCount arrays of columns x rows counts, and its reader checks the header against
            // MaxSide; the filler's two arrays of two under a header of 70,000 columns would not serialize. Not a branch,
            // but the same hook: it sees the grid alone and inside a MeshLoadReportMessage.
            Branches[typeof(MeshOccupancyGrid)] = o => ShapeGrid((MeshOccupancyGrid)o);
        }

        private static void ShapeGrid(MeshOccupancyGrid grid)
        {
            grid.columns = 3;
            grid.rows = 2;
            grid.layers = new ushort[MeshOccupancyGrid.LayerCount][];
            for (int layer = 0; layer < grid.layers.Length; ++layer)
            {
                ushort[] counts = new ushort[grid.columns * grid.rows];
                for (int bucket = 0; bucket < counts.Length; ++bucket)
                    counts[bucket] = (ushort)(100 * (layer + 1) + bucket);
                grid.layers[layer] = counts;
            }
        }
    }
}

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>
    /// The add-on's messages whose layout depends on a kind: the generic round trip takes one branch per type, these take
    /// every one and check that exactly the fields that branch sends arrive and the others stay at their defaults.
    /// </summary>
    [Category(OpenMMORPG.Tests.TestCategories.Logic)]
    public class MeshMessageBranchTests : OpenMMORPG.Tests.KitTestFixture
    {
        private static GhostSnapshotMessage FilledSnapshot(GhostKind kind)
        {
            return (GhostSnapshotMessage)OpenMMORPG.Tests.TestNetData.Fill(typeof(GhostSnapshotMessage), o =>
            {
                if (o is GhostSnapshotMessage)
                    OpenMMORPG.Tests.TestReflection.SetField(o, "kind", kind);
            });
        }

        [TestCase(GhostKind.Player)]
        [TestCase(GhostKind.Monster)]
        [TestCase(GhostKind.ItemDrop)]
        [TestCase(GhostKind.Harvestable)]
        [TestCase(GhostKind.Building)]
        [TestCase(GhostKind.Container)]
        public void AGhostSnapshotSendsTheFieldsOfItsKind(GhostKind kind)
        {
            GhostSnapshotMessage sent = FilledSnapshot(kind);

            GhostSnapshotMessage received = OpenMMORPG.Tests.TestNetData.RoundTrip(sent);

            List<string> common = OpenMMORPG.Tests.TestNetData.Differences(sent, received, (owner, member) =>
                owner == typeof(GhostSnapshotMessage) && (member == "fullRotation" || member == "items" || member == "ageSeconds")
                    ? OpenMMORPG.Tests.TestNetData.MemberRule.Ignore
                    : OpenMMORPG.Tests.TestNetData.MemberRule.Exact);
            Assert.That(common, Is.Empty, $"the fields every kind sends, for {kind}");
            Assert.That(received.fullRotation, Is.EqualTo(kind == GhostKind.Building ? sent.fullRotation : Vector3.zero),
                "the full rotation travels with a building only");
            if (kind == GhostKind.Container)
                Assert.That(OpenMMORPG.Tests.TestNetData.Differences(sent.items, received.items), Is.Empty, "a container's items");
            else
                Assert.That(received.items, Is.Null, "items travel with a container only");
            Assert.That(received.ageSeconds, Is.Zero, "the age is measured by the receiver, never sent");
            // Written after the kind-specific rotation: a branch taken on one side only would shift it.
            Assert.That(received.sampleMs, Is.EqualTo(sent.sampleMs), "the sample stamp after the branch");
        }

        [Test]
        public void EveryGhostKindHasACase()
        {
            // A new kind with no case above could be written and read differently unnoticed.
            MethodInfo test = GetType().GetMethod(nameof(AGhostSnapshotSendsTheFieldsOfItsKind));
            IEnumerable<object> covered = test.GetCustomAttributes<TestCaseAttribute>().Select(c => c.Arguments[0]);
            Assert.That(covered, Is.EquivalentTo(Enum.GetValues(typeof(GhostKind)).Cast<object>()));
        }
    }
}
