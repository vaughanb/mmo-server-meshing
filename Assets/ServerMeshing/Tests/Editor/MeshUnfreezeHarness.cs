using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using static MultiplayerARPG.MMO.Mesh.Tests.MeshFakeWorlds;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>
    /// The operator's unfreeze (DESIGN 0.85, slices/unfreeze_design.md): Slice 5's restart harness (a first coordinator,
    /// its store, a second coordinator on the same store with the cells' held layouts) shared by the family and the
    /// frozen-operation tests.
    /// </summary>
    public abstract class MeshUnfreezeHarness
    {
        protected const long ConnC = 7, ConnE = 8, ConnX = 20;
        protected static readonly MeshRect West = new MeshRect(-100f, -100f, 0f, 100f);
        protected static readonly MeshRect East = new MeshRect(0f, -100f, 100f, 100f);
        protected static readonly MeshRect Whole = new MeshRect(-100f, -100f, 100f, 100f);
        protected MeshWorldRegistry _world;
        private protected MeshFakeClock _clock;
        private protected MeshFakeCoordinatorTransport _transport;
        private protected MeshFakeLog _log;
        private protected MeshFakeSplitHost _host;
        protected MeshCoordinatorStore _store;
        protected MeshLayoutCoordinator _c;
        protected readonly List<string> _lines = new List<string>();
        protected readonly Dictionary<string, (long connection, string incarnation)> _bound = new Dictionary<string, (long, string)>(StringComparer.OrdinalIgnoreCase);
        /// <summary>The recovery ceiling every new central's registry gets (Slice 5's default 60 s).</summary>
        protected float _ceiling = 60f;
        private string _dir;
        private BandEnvironment _band;

        [SetUp]
        public void SetUp()
        {
            _band = new BandEnvironment();
            _ceiling = 60f;   // NUnit keeps one fixture instance for every test
            _clock = new MeshFakeClock();
            _dir = Path.Combine(Path.GetTempPath(), "MeshUnfreezeTests-" + Guid.NewGuid().ToString("N"));
            _store = new MeshCoordinatorStore(Path.Combine(_dir, "store.json"));
            NewCentral();
        }

        [TearDown]
        public void TearDown()
        {
            if (_world != null)
                UnityEngine.Object.DestroyImmediate(_world);
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, true);
            _band.Dispose();
        }

        /// <summary>NewCentral is a new process: a fresh registry, transport, log and host on the same store and clock.</summary>
        protected void NewCentral()
        {
            if (_world != null)
                UnityEngine.Object.DestroyImmediate(_world);
            _world = RigLike(map1Dynamic: true);
            _world.dynamicSettings.recoveryCeilingSeconds = _ceiling;
            _transport = new MeshFakeCoordinatorTransport();
            _log = new MeshFakeLog();
            _host = new MeshFakeSplitHost();
            _lines.Clear();
            _bound.Clear();
            _c = new MeshLayoutCoordinator(_world, _clock, _transport, _store, _log) { SplitHost = _host };
            _c.OpLine += _lines.Add;
        }

        protected void Hello(long connection, string cellId, string incarnation, MeshLayoutSnapshot held = null)
        {
            _transport.Register(connection, cellId, Map1);
            _c.OnLayoutHello(connection, new MeshLayoutHelloMessage { cellId = cellId, mapId = Map1, incarnation = incarnation, dynamicMap = true, held = held ?? new MeshLayoutSnapshot(), processId = 100 + (int)connection });
            _bound[cellId] = (connection, incarnation);
        }

        protected void Ack(string cellId, MeshLayoutPair pair)
        {
            (long connection, string incarnation) = _bound[cellId];
            _c.OnLayoutAck(connection, new MeshLayoutAckMessage { cellId = cellId, incarnation = incarnation, pair = pair, result = MeshLayoutAckResult.Applied });
        }

        protected void AckAll(MeshLayoutPair pair)
        {
            foreach (string cell in new List<string>(_bound.Keys))
            {
                if (_transport.peers.ContainsKey(_bound[cell].connection))
                    Ack(cell, pair);
            }
        }

        protected string[] OthersRegistered(string cellId)
        {
            List<string> others = new List<string>();
            foreach (MeshMapServerPeer peer in _transport.peers.Values)
            {
                if (!string.Equals(peer.cellId, cellId, StringComparison.OrdinalIgnoreCase))
                    others.Add(peer.cellId);
            }
            return others.ToArray();
        }

        /// <summary>HeldOf is the newest layout the central sent to a cell: what the cell holds at a restart.</summary>
        protected MeshLayoutSnapshot HeldOf(string cellId)
        {
            long connection = _bound[cellId].connection;
            MeshLayoutSnapshot newest = null;
            foreach ((long to, MeshLayoutMessage message) in _transport.layouts)
            {
                if (to == connection && message.snapshot != null && (newest == null || message.snapshot.pair.IsNewerThan(newest.pair)))
                    newest = message.snapshot;
            }
            return newest != null ? newest.Clone() : new MeshLayoutSnapshot();
        }

        /// <summary>FirstCentral starts a central with C and E bound (incarnations c1, e1) and returns it running.</summary>
        protected void FirstCentral()
        {
            _c.Start();
            Hello(ConnC, "C", "c1");
            Hello(ConnE, "E", "e1");
            Assert.That(_c.Phase, Is.EqualTo(MeshCoordinatorPhase.Running));
        }

        /// <summary>SplitTo runs `split C x 0 + C.1` (child incarnation x1) up to the given step.</summary>
        protected MeshSplitOperation SplitTo(MeshSplitStep step)
        {
            Assert.That(_c.RequestSplit("C", MeshSplitAxis.X, 0f, true, "C.1", out string error), Is.True, error);
            MeshSplitOperation op = _c.CurrentSplit;
            _c.OnSpawnResult(op.opId, true, "Success");
            Hello(ConnX, "C.1", "x1");
            if (step == MeshSplitStep.E1Gate)
                return op;
            Ack("C.1", op.e1Pair);
            AckAll(op.e1Pair);
            _c.OnLayoutEvidence(ConnX, MeshSplitOperationTests.Prewarm("C.1", "x1", op.e1Pair, OthersRegistered("C.1")));
            Assert.That(op.step, Is.EqualTo(MeshSplitStep.E2Staged), op.lastGateWait);
            if (step == MeshSplitStep.E2Staged)
                return op;
            Ack("C.1", op.e2Pair);
            Assert.That(op.step, Is.EqualTo(MeshSplitStep.DrainGate), op.lastGateWait);
            if (step == MeshSplitStep.DrainGate)
                return op;
            AckAll(op.e2Pair);
            _c.OnLayoutEvidence(ConnC, MeshSplitOperationTests.Drain("C", "c1", op.e2Pair, 0));
            Assert.That(op.step, Is.EqualTo(MeshSplitStep.E3Broadcast), op.lastGateWait);
            if (step == MeshSplitStep.E3Broadcast)
                return op;
            AckAll(op.e3Pair);
            Assert.That(op.step, Is.EqualTo(MeshSplitStep.Done), op.lastGateWait);
            return op;
        }

        /// <summary>Restart: the central dies, a second one starts on the store 10 s later and the named cells say hello holding what they were sent.</summary>
        protected void Restart(params string[] cells)
        {
            Dictionary<string, MeshLayoutSnapshot> held = new Dictionary<string, MeshLayoutSnapshot>(StringComparer.OrdinalIgnoreCase);
            foreach (string cell in _bound.Keys)
                held[cell] = HeldOf(cell);
            Dictionary<string, (long connection, string incarnation)> was = new Dictionary<string, (long, string)>(_bound, StringComparer.OrdinalIgnoreCase);
            _c.Stop();
            NewCentral();
            _clock.Now += 10f;
            _clock.UtcTicks += 1000;
            _c.Start();
            foreach (string cell in cells)
                Hello(was[cell].connection, cell, was[cell].incarnation, held[cell]);
        }

        /// <summary>Tick runs the coordinator's once-a-second tick after <paramref name="seconds"/> more.</summary>
        protected void TickAfter(float seconds)
        {
            _clock.Now += seconds;
            _c.Tick();
        }

        protected bool Unfreeze(string baseId, out string error) => _c.RequestUnfreeze(baseId, out error);

        protected List<string> LinesStarting(string prefix) => _lines.FindAll(l => l.StartsWith(prefix, StringComparison.Ordinal));

        protected MeshRetireMessage LastRetire() => _transport.retires[_transport.retires.Count - 1].message;

        protected MeshCoordinatorStoreData Stored()
        {
            Assert.That(_store.TryLoad(out MeshCoordinatorStoreData data, out string error), Is.True, error);
            return data;
        }

        /// <summary>DriveCollapseToDone: the collapse merge in hand, with C the survivor and the dead cell unregistered, to its done.</summary>
        protected MeshMergeOperation DriveCollapseToDone()
        {
            MeshMergeOperation op = _c.CurrentMerge;
            Assert.That(op != null && op.collapse && op.InProgress, Is.True, "a collapse runs");
            AckAll(op.e1Pair);
            _c.OnLayoutEvidence(ConnC, MeshSplitOperationTests.Prewarm("C", _bound["C"].incarnation, op.e1Pair, OthersRegistered("C")));
            Assert.That(op.step, Is.EqualTo(MeshMergeStep.E2Staged), op.lastGateWait);
            Ack("C", op.e2Pair);
            AckAll(op.e2Pair);
            TickAfter(5f);
            Assert.That(op.step, Is.EqualTo(MeshMergeStep.E3Broadcast), op.lastGateWait);
            AckAll(op.e3Pair);
            AckAll(op.tidyPair);
            Assert.That(op.step, Is.EqualTo(MeshMergeStep.Done), op.lastGateWait);
            return op;
        }
    }
}
