using System.Collections.Generic;
using LiteNetLib.Utils;
using NUnit.Framework;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>Slice 4 (slices/slice4_design.md): the building channel, trees and their partition, the transfer rules, the wire.</summary>
    public class MeshBuildingTests
    {
        private static MeshBuildingNode Node(string id, string parent = "", bool scene = false, float x = 0f, float z = 0f)
        {
            return new MeshBuildingNode { id = id, parentId = parent, isSceneObject = scene, x = x, z = z };
        }

        private static List<string> Ids(MeshBuildingTree tree) => tree.ids;

        // ---- decision 2: the channel ----
        [TestCase("C", "C")]
        [TestCase("C.1", "C")]
        [TestCase("C.12", "C")]
        [TestCase("C2", "C2")]
        [TestCase("C.x", "C.x")]
        [TestCase("C.", "C.")]
        [TestCase(".1", ".1")]
        [TestCase("", "")]
        [TestCase(null, "")]
        public void BaseOf_StripsOneNumericSuffix(string id, string expected)
        {
            Assert.That(MeshBuildingChannel.BaseOf(id), Is.EqualTo(expected));
        }

        [Test]
        public void For_StaticMapKeepsTheChannel_DynamicMapTakesTheBase()
        {
            Assert.That(MeshBuildingChannel.For("C.1", false, "C", "C.1"), Is.EqualTo("C.1"), "a Static map's cell: its own channel, whatever the layout says");
            Assert.That(MeshBuildingChannel.For("C.1", true, "C", "C.1"), Is.EqualTo("C"), "the layout's base id first");
            Assert.That(MeshBuildingChannel.For("C.1", true, null, "C.1"), Is.EqualTo("C"), "else the id's syntax");
            Assert.That(MeshBuildingChannel.For("C", true, null, null), Is.EqualTo("C"), "a base cell before any layout");
            Assert.That(MeshBuildingChannel.For("E", true, "E", "E"), Is.EqualTo("E"), "a base cell equals its channel");
        }

        // ---- decision 4: trees ----
        [Test]
        public void Build_RootsAndChildrenParentBeforeChild()
        {
            List<MeshBuildingTree> trees = MeshBuildingTrees.Build(new[] { Node("chest", "wall"), Node("door", "wall"), Node("wall", "", false, 63f, 70f), Node("lone", "", false, -5f, 1f) });
            Assert.That(trees.Count, Is.EqualTo(2));
            MeshBuildingTree wall = trees.Find(t => t.rootId == "wall");
            Assert.That(Ids(wall), Is.EqualTo(new List<string> { "wall", "chest", "door" }));
            Assert.That((wall.rootX, wall.rootZ), Is.EqualTo((63f, 70f)));
            Assert.That(Ids(trees.Find(t => t.rootId == "lone")), Is.EqualTo(new List<string> { "lone" }));
        }

        [Test]
        public void Build_OrphanIsARoot_GrandchildrenFollow_CycleBrokenAtLowestId()
        {
            List<MeshBuildingTree> trees = MeshBuildingTrees.Build(new[] { Node("b", "a"), Node("c", "b"), Node("a", "missing"), Node("y", "z"), Node("z", "y") });
            Assert.That(trees.Count, Is.EqualTo(2));
            Assert.That(Ids(trees[0]), Is.EqualTo(new List<string> { "a", "b", "c" }), "a's parent is not among the rows: a root");
            Assert.That(Ids(trees[1]), Is.EqualTo(new List<string> { "y", "z" }), "a cycle: broken at its lowest id");
        }

        [Test]
        public void Build_SkippedRowsLeaveTheirChildrenAsRoots()
        {
            List<MeshBuildingTree> trees = MeshBuildingTrees.Build(new[] { Node("wall"), Node("chest", "wall") }, new HashSet<string> { "wall" });
            Assert.That(trees.Count, Is.EqualTo(1));
            Assert.That(Ids(trees[0]), Is.EqualTo(new List<string> { "chest" }));
        }

        [Test]
        public void Partition_SceneMatched_SceneSkipped_AlreadyHere_Trees()
        {
            MeshBuildingPartition part = MeshBuildingTrees.Partition(
                new[] { Node("C_Map_1", scene: true), Node("C.1_Map_1", scene: true), Node("placed"), Node("here"), Node("kid", "here") },
                new HashSet<string> { "C_Map_1" }, new HashSet<string> { "here" });
            Assert.That(part.sceneMatched, Is.EqualTo(new List<string> { "C_Map_1" }));
            Assert.That(part.sceneSkipped, Is.EqualTo(new List<string> { "C.1_Map_1" }), "another cell's scene copy");
            Assert.That(part.alreadyHere, Is.EqualTo(new List<string> { "here" }));
            List<string> roots = part.trees.ConvertAll(t => t.rootId);
            roots.Sort(System.StringComparer.Ordinal);
            Assert.That(roots, Is.EqualTo(new List<string> { "kid", "placed" }), "a child of a row already here is its own tree");
        }

        [Test]
        public void Ownership_ByTheRootsPositionInTheLiveLayout()
        {
            using (new MeshFakeWorlds.BandEnvironment())
            {
                MeshWorldRegistry world = MeshFakeWorlds.RigLike(true);
                try
                {
                    Assert.That(world.ApplyLayout(MeshFakeWorlds.SplitC(1), out string error), Is.True, error);
                    MeshCell c = world.GetCellById("C"), c1 = world.GetCellById("C.1");
                    Assert.That(MeshCell.Same(world.OwnerOf(c, new Vector3(63f, 0f, 70f)), c1), Is.True, "a root in R belongs to the child");
                    Assert.That(MeshCell.Same(world.OwnerOf(c, new Vector3(-20f, 0f, 70f)), c), Is.True, "a root in the kept half stays");
                    Assert.That(MeshCell.Same(world.OwnerOf(c1, new Vector3(0f, 0f, 0f)), c1), Is.True, "the half-open cut: x = 0 is the child's");
                    Assert.That(c1.baseId, Is.EqualTo("C"));
                    Assert.That(c.baseId, Is.EqualTo("C"));
                }
                finally
                {
                    Object.DestroyImmediate(world);
                }
            }
        }

        // ---- decisions 8-10: the rules ----
        [Test]
        public void MayStart_EveryConditionCounts()
        {
            Assert.That(MeshBuildingTransferRules.MayStart(true, true, true, true, true, false, 0, 2), Is.True);
            Assert.That(MeshBuildingTransferRules.MayStart(false, true, true, true, true, false, 0, 2), Is.False, "not before the load");
            Assert.That(MeshBuildingTransferRules.MayStart(true, false, true, true, true, false, 0, 2), Is.False, "not owned here");
            Assert.That(MeshBuildingTransferRules.MayStart(true, true, false, true, true, false, 0, 2), Is.False, "ours by position");
            Assert.That(MeshBuildingTransferRules.MayStart(true, true, true, false, true, false, 0, 2), Is.False, "not linked");
            Assert.That(MeshBuildingTransferRules.MayStart(true, true, true, true, false, false, 0, 2), Is.False, "not settled (registered, gate open)");
            Assert.That(MeshBuildingTransferRules.MayStart(true, true, true, true, true, true, 0, 2), Is.False, "in back-off");
            Assert.That(MeshBuildingTransferRules.MayStart(true, true, true, true, true, false, 2, 2), Is.False, "the cap");
            Assert.That(MeshBuildingTransferRules.MayStart(true, true, true, true, true, false, 0, 0), Is.True, "a cap under 1 reads as 1");
        }

        [Test]
        public void RefuseReason_InOrder_NullWhenTaken()
        {
            Assert.That(MeshBuildingTransferRules.RefuseReason(true, true, false, 0, 7), Is.EqualTo("gated"));
            Assert.That(MeshBuildingTransferRules.RefuseReason(false, true, false, 0, 7), Is.EqualTo("loading"));
            Assert.That(MeshBuildingTransferRules.RefuseReason(false, false, false, 0, 7), Is.EqualTo("not the owner here"));
            Assert.That(MeshBuildingTransferRules.RefuseReason(false, false, true, 1, 7), Is.EqualTo("unknown building 7"));
            Assert.That(MeshBuildingTransferRules.RefuseReason(false, false, true, -1, 0), Is.Null);
        }

        [Test]
        public void Receive_OwnedAlready_Promote_Spawn()
        {
            Assert.That(MeshBuildingTransferRules.Receive(true, true), Is.EqualTo(MeshBuildingReceipt.OwnedAlready));
            Assert.That(MeshBuildingTransferRules.Receive(false, true), Is.EqualTo(MeshBuildingReceipt.Promote));
            Assert.That(MeshBuildingTransferRules.Receive(false, false), Is.EqualTo(MeshBuildingReceipt.Spawn));
        }

        [Test]
        public void StorageReleased_NobodyOnItOrTheWaitIsOver()
        {
            Assert.That(MeshBuildingTransferRules.StorageReleased(false, false, 0f, 5f), Is.True);
            Assert.That(MeshBuildingTransferRules.StorageReleased(true, false, 1f, 5f), Is.False, "open");
            Assert.That(MeshBuildingTransferRules.StorageReleased(false, true, 1f, 5f), Is.False, "a move in flight");
            Assert.That(MeshBuildingTransferRules.StorageReleased(true, true, 5f, 5f), Is.True, "the budget is up");
        }

        [Test]
        public void DeleteRowOnDespawn_OnlyAnOwnedBuildingOfADynamicMap()
        {
            Assert.That(MeshBuildingTransferRules.DeleteRowOnDespawn(false, false), Is.True, "Static map: the kit's path");
            Assert.That(MeshBuildingTransferRules.DeleteRowOnDespawn(false, true), Is.True);
            Assert.That(MeshBuildingTransferRules.DeleteRowOnDespawn(true, true), Is.True, "owned here");
            Assert.That(MeshBuildingTransferRules.DeleteRowOnDespawn(true, false), Is.False, "a puppet or a demoted copy keeps the row");
        }

        [Test]
        public void DespawnOrder_LeafFirstRootLast()
        {
            Assert.That(MeshBuildingTransferRules.DespawnOrder(new[] { "wall", "chest", "door" }), Is.EqualTo(new List<string> { "door", "chest", "wall" }));
        }

        // ---- the wire ----
        [Test]
        public void BuildingTransfer_RoundTripsTheTree()
        {
            BuildingTransferMessage msg = new BuildingTransferMessage
            {
                rootId = "wall",
                nodes = new[]
                {
                    new BuildingSaveData { Id = "wall", ParentId = "", EntityId = 7, CurrentHp = 40, RemainsLifeTime = 0f, Position = new Vector3(63f, 0f, 70f), Rotation = Vector3.zero, CreatorId = "c", CreatorName = "MeshTester2", IsLocked = false, LockPassword = "", ExtraData = "" },
                    new BuildingSaveData { Id = "chest", ParentId = "wall", EntityId = 8, CurrentHp = 40, RemainsLifeTime = 0f, Position = new Vector3(63f, 0f, 71.5f), Rotation = Vector3.zero, CreatorId = "c", CreatorName = "MeshTester2", IsLocked = true, LockPassword = "1234", ExtraData = "" },
                },
            };
            NetDataWriter writer = new NetDataWriter();
            msg.Serialize(writer);
            BuildingTransferMessage read = default;
            read.Deserialize(new NetDataReader(writer.CopyData()));
            Assert.That(read.rootId, Is.EqualTo("wall"));
            Assert.That(read.nodes.Length, Is.EqualTo(2));
            Assert.That((read.nodes[1].Id, read.nodes[1].ParentId, read.nodes[1].EntityId, read.nodes[1].IsLocked, read.nodes[1].LockPassword), Is.EqualTo(("chest", "wall", 8, true, "1234")));
            Assert.That((Vector3)read.nodes[1].Position, Is.EqualTo(new Vector3(63f, 0f, 71.5f)));

            BuildingTransferReplyMessage reply = new BuildingTransferReplyMessage { rootId = "wall", ok = true, reason = "", promoted = 1, spawned = 2, ownedAlready = 0 };
            writer.Reset();
            reply.Serialize(writer);
            BuildingTransferReplyMessage readReply = default;
            readReply.Deserialize(new NetDataReader(writer.CopyData()));
            Assert.That((readReply.rootId, readReply.ok, readReply.promoted, readReply.spawned, readReply.ownedAlready), Is.EqualTo(("wall", true, 1, 2, 0)));
        }

        [Test]
        public void Evidence_V3_CarriesTheBuildingCount_V2Refused()
        {
            MeshLayoutEvidenceMessage built = MeshSplitEvidence.Assemble("C", "inc", new MeshLayoutPair(3, 9), 20f, 10f, new List<MeshNeighbourEvidence>(), 0, 0f, 4f, 16.67f,
                true, new MeshRect(0f, -100f, 100f, 100f), new MeshDrainCounts { characters = 1, buildings = 2 });
            Assert.That(built.drain.Total, Is.EqualTo(3), "buildings count toward the drain total");
            NetDataWriter writer = new NetDataWriter();
            built.Serialize(writer);
            Assert.That(MeshWholeMessage.TryRead(new NetDataReader(writer.CopyData()), out MeshLayoutEvidenceMessage read, out string error), Is.True, error);
            Assert.That((read.drain.characters, read.drain.buildings, read.drain.Total), Is.EqualTo((1, 2, 3)));
            byte[] bytes = writer.CopyData();
            bytes[0] = 2;
            Assert.That(MeshWholeMessage.TryRead(new NetDataReader(bytes), out read, out error), Is.False, "version 2 refused");
        }

        [Test]
        public void SplitGate_NamesBuildingsLeft()
        {
            MeshLayoutPair e2 = new MeshLayoutPair(3, 5);
            MeshLayoutEvidenceMessage evidence = MeshSplitEvidence.Assemble("C", "inc", e2, 20f, 10f, new List<MeshNeighbourEvidence>(), 0, 0f, 4f, 16.67f,
                true, new MeshRect(0f, -100f, 100f, 100f), new MeshDrainCounts { buildings = 1 });
            Assert.That(MeshSplitGates.Drained(evidence, e2, out string why), Is.False);
            Assert.That(why, Does.Contain("1 building(s)"));
        }

        [Test]
        public void Settings_BuildingFieldsValidated()
        {
            MeshDynamicSettings s = new MeshDynamicSettings();
            Assert.That(MeshLayoutValidator.PolicySettingsError(s), Is.Null);
            Assert.That((s.buildingTransfersInFlight, s.buildingStorageCloseSeconds), Is.EqualTo((2, 5f)));
            s.buildingTransfersInFlight = 0;
            Assert.That(MeshLayoutValidator.PolicySettingsError(s), Does.Contain("buildingTransfersInFlight"));
            s.buildingTransfersInFlight = 2;
            s.buildingStorageCloseSeconds = 0f;
            Assert.That(MeshLayoutValidator.PolicySettingsError(s), Does.Contain("buildingStorageCloseSeconds"));
        }
    }
}
