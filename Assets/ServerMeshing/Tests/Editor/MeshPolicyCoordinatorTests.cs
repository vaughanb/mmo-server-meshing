using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using static MultiplayerARPG.MMO.Mesh.Tests.MeshFakeWorlds;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>
    /// The coordinator with the automatic policy on (Slice 3): a hot C is split by itself, measured, and merged back once
    /// cool; two children merge with the survivor by players (decision 11); a child collapses into a sibling (decision 14).
    /// </summary>
    public class MeshPolicyCoordinatorTests
    {
        private const long ConnC = 7, ConnX = 20, ConnY = 21;
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
            _world.maps[0].autoSplit = true;
            _world.maps[0].autoMerge = true;
            MeshDynamicSettings s = _world.dynamicSettings;
            s.splitDwellSeconds = 10f;
            s.mergeDwellSeconds = 20f;
            s.opCooldownSeconds = 5f;
            s.splitCooldownSeconds = 5f;
            s.measureAfterSeconds = 5f;
            s.splitBenefitRatio = 1f;
            _clock = new MeshFakeClock();
            _transport = new MeshFakeCoordinatorTransport();
            _log = new MeshFakeLog();
            _host = new MeshFakeSplitHost { host = new MeshHostInfo { spawnerConnection = 1, hostName = "H", cores = 64 } };
            _dir = Path.Combine(Path.GetTempPath(), "MeshPolicyTests-" + Guid.NewGuid().ToString("N"));
            _store = new MeshCoordinatorStore(Path.Combine(_dir, "store.json"));
            _lines.Clear();
            _bound.Clear();
            _c = new MeshLayoutCoordinator(_world, _clock, _transport, _store, _log) { SplitHost = _host };
            _c.OpLine += _lines.Add;
            _c.Start();
            Bind(ConnC, "C", "pc");
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_world);
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, true);
            _band.Dispose();
        }

        private void Bind(long connection, string cellId, string incarnation)
        {
            _transport.Register(connection, cellId, Map1);
            _c.OnLayoutHello(connection, new MeshLayoutHelloMessage { cellId = cellId, mapId = Map1, incarnation = incarnation, dynamicMap = true, held = new MeshLayoutSnapshot() });
            _bound[cellId] = (connection, incarnation);
        }

        private void Ack(string cellId, MeshLayoutPair pair)
        {
            (long connection, string incarnation) = _bound[cellId];
            _c.OnLayoutAck(connection, new MeshLayoutAckMessage { cellId = cellId, incarnation = incarnation, pair = pair, result = MeshLayoutAckResult.Applied });
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

        private void Report(string cellId, MeshRect owned, float util, IEnumerable<(float, float)> players)
        {
            (long connection, string incarnation) = _bound[cellId];
            _c.OnLoadReport(connection, MeshFakeReports.Report(cellId, owned, util, players, incarnation: incarnation));
        }

        private void Tick(int seconds)
        {
            for (int i = 0; i < seconds; ++i)
            {
                _clock.Now += 1f;
                _c.Tick();
            }
        }

        /// <summary>Drives a split the policy requested to done: the child's hello, acks, pre-warm and drain evidence.</summary>
        private MeshSplitOperation FinishSplit(string parent, string child, long connection, string incarnation)
        {
            MeshSplitOperation op = _c.CurrentSplit;
            Assert.That(op, Is.Not.Null, "a split was requested");
            _c.OnSpawnResult(op.opId, true, "Success");
            Bind(connection, child, incarnation);
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

        private List<string> Policy() => _lines.FindAll(l => l.StartsWith("Policy: ", StringComparison.Ordinal));

        private static List<(float, float)> Both(float x, int n)
        {
            List<(float, float)> points = MeshFakeReports.Spread(-x, n);
            points.AddRange(MeshFakeReports.Spread(x, n));
            return points;
        }

        [Test]
        public void AHotCell_IsSplitByThePolicy_MeasuredAndMergedBackWhenCool()
        {
            Assert.That(_c.PolicyEnabled, Is.True);
            Assert.That(_log.infos, Has.Some.StartsWith("Coordinator policy: automatic split on Map1, automatic merge on Map1"));
            Report("C", Whole, 0.9f, Both(88f, 4));
            Tick(10);   // the first tick sees the hot report: the dwell runs from it, 9 s so far
            Assert.That(Policy(), Is.Empty, "the dwell");
            Tick(1);
            Assert.That(Policy(), Is.EqualTo(new[] { "Policy: split of C planned: x 0 + child C.1, util 0.9 -> peak 0.69 (C 0.69, C.1 0.69), 2 cut(s) tried" }));
            Assert.That(_lines, Has.Some.EqualTo("Split split-1: requested C x 0 + child C.1"));
            Assert.That(_c.Policy.Records.Count, Is.EqualTo(1));

            MeshSplitOperation op = FinishSplit("C", "C.1", ConnX, "xc");
            Assert.That(_c.Policy.Records[0].doneAt, Is.EqualTo(_clock.Now));
            for (int i = 0; i < 6; ++i)
            {
                // Reports arrive every tick; the measurement reads the first ones after done + measureAfterSeconds (5 s).
                Report("C", West, 0.5f, MeshFakeReports.Spread(-88f, 4));
                Report("C.1", East, 0.45f, MeshFakeReports.Spread(88f, 4));
                Tick(1);
            }
            Assert.That(Policy().Count, Is.EqualTo(2), string.Join(" | ", Policy()));
            Assert.That(Policy()[1], Is.EqualTo("Policy: split split-1 measured: predicted peak 0.69, measured 0.5 (C 0.5, C.1 0.45), relief 44 %"));
            Assert.That(_c.Policy.MultiplierOf("C"), Is.EqualTo(1f));

            // Both cells cool off: after the merge dwell (20 s of fresh cool reports) the policy merges C.1 into C.
            for (int i = 0; i < 20; ++i)
            {
                Report("C", West, 0.1f, MeshFakeReports.Spread(-50f, 2));
                Report("C.1", East, 0.1f, MeshFakeReports.Spread(50f, 2));
                Tick(1);
            }
            Assert.That(_c.CurrentMerge, Is.Null, string.Join(" | ", Policy()));
            Report("C", West, 0.1f, MeshFakeReports.Spread(-50f, 2));
            Report("C.1", East, 0.1f, MeshFakeReports.Spread(50f, 2));
            Tick(1);
            Assert.That(Policy().Count, Is.EqualTo(3), string.Join(" | ", Policy()));
            Assert.That(Policy()[2], Does.StartWith("Policy: merge of C C.1 planned: predicted 0.").And.EndWith("(C 0.1, C.1 0.1)"));
            Assert.That(_lines, Has.Some.EqualTo("Merge merge-1: requested C C.1"));
            Assert.That(_c.Policy.Records.Count, Is.EqualTo(1));
            Assert.That(_c.Policy.Records[0].merge, Is.True);
        }

        [Test]
        public void TwoChildrenSharingAFullEdge_Merge_TheOneWithMorePlayersSurviving()
        {
            Report("C", Whole, 0.9f, Both(88f, 4));
            Tick(11);
            FinishSplit("C", "C.1", ConnX, "xc");
            _clock.Now += 31f;
            // C.1 (x [0, 100)) is split at z = 0 by hand: C.2 takes the north half, so C.1 and C.2 share a full edge.
            Assert.That(_c.RequestSplit("C.1", MeshSplitAxis.Z, 0f, true, "C.2", out string error), Is.True, error);
            FinishSplit("C.1", "C.2", ConnY, "yc");
            _clock.Now += 31f;
            Report("C.1", new MeshRect(0f, -100f, 100f, 0f), 0.2f, MeshFakeReports.Spread(50f, 2));
            Report("C.2", new MeshRect(0f, 0f, 100f, 100f), 0.3f, new List<(float, float)> { (50f, 20f), (50f, 40f), (50f, 60f), (50f, 80f), (60f, 50f) });

            Assert.That(_c.RequestMerge("C.1", "C.2", out error), Is.True, error);

            MeshMergeOperation merge = _c.CurrentMerge;
            Assert.That((merge.survivorId, merge.retiringId, merge.union), Is.EqualTo(("C.2", "C.1", East)));
            Assert.That(_lines, Has.Some.EqualTo("Merge merge-1: requested C.2 C.1"));
            Assert.That(_world.GetCellById("C.2").interest, Is.EqualTo(East), "e1 widened the survivor's interest");
        }

        [Test]
        public void TwoChildrenWithNoReports_TheLowerIdSurvives()
        {
            Report("C", Whole, 0.9f, Both(88f, 4));
            Tick(11);
            FinishSplit("C", "C.1", ConnX, "xc");
            _clock.Now += 31f;
            Assert.That(_c.RequestSplit("C.1", MeshSplitAxis.Z, 0f, true, "C.2", out string error), Is.True, error);
            FinishSplit("C.1", "C.2", ConnY, "yc");
            _clock.Now += 31f;

            Assert.That(_c.RequestMerge("C.2", "C.1", out error), Is.True, error);
            Assert.That((_c.CurrentMerge.survivorId, _c.CurrentMerge.retiringId), Is.EqualTo(("C.1", "C.2")));
        }

        [Test]
        public void AChildThatCannotCollapseIntoItsBase_CollapsesIntoASibling()
        {
            Report("C", Whole, 0.9f, Both(88f, 4));
            Tick(11);
            FinishSplit("C", "C.1", ConnX, "xc");
            _clock.Now += 31f;
            Assert.That(_c.RequestSplit("C.1", MeshSplitAxis.Z, 0f, true, "C.2", out string error), Is.True, error);
            FinishSplit("C.1", "C.2", ConnY, "yc");
            _clock.Now += 31f;
            _host.restartOk = false;
            _transport.peers.Remove(ConnX);   // C.1 (x [0,100) z [-100,0)) dies: C shares no full edge with it, C.2 does

            Tick(45);

            Assert.That(_lines, Has.Some.EqualTo("Supervisor: C.1 collapse queued after 3 failed restart(s)"));
            Assert.That(_lines, Has.None.Contains("cannot collapse"));
            Assert.That(_lines, Has.Some.EqualTo("Merge merge-1: requested C.2 C.1 collapse"));
            Assert.That((_c.CurrentMerge.survivorId, _c.CurrentMerge.retiringId, _c.CurrentMerge.collapse), Is.EqualTo(("C.2", "C.1", true)));
        }
    }
}
