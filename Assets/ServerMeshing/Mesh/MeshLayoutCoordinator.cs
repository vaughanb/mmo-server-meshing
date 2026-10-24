// Server Meshing add-on — the central's layout coordinator (DESIGN.md 0.72, plan D3). It owns the runtime layout of
// every Dynamic map: it recovers it when the central starts (MeshLayoutCoordinator_Recovery.cs: the asset cut when nothing
// dynamic exists anywhere, else the stored and held layouts, Slice 5), answers each cell's hello with the layout to hold
// (which opens the cell's gate), and tells the cells what it decided. Splits run in MeshLayoutCoordinator_Split.cs (Slice
// 2b), merges in MeshLayoutCoordinator_Merge.cs (Slice 2c), a stored operation's resumption after a restart in
// MeshLayoutCoordinator_Resume.cs (Slice 5). A plain class with its clock, transport, store and log injected, so it is
// tested without Unity networking. Nothing throws out of a handler or out of Tick: each validates, then changes state.
// Supervision (Slice 2d: restarts, collapse, kills, the fenced set, ops pausing for a crashed member) is in
// MeshLayoutCoordinator_Supervision.cs.
using System;
using System.Collections.Generic;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>MeshLayoutCoordinator is the central's owner of the runtime layout.</summary>
    public sealed partial class MeshLayoutCoordinator
    {
        /// <summary>Seconds a Dynamic map's cell may stay registered without a hello before one warning.</summary>
        public const float HelloMissingWarnSeconds = 10f;
        /// <summary>Seconds between two notes with the same code to the same cell.</summary>
        public const float NoteRepeatSeconds = 60f;

        private readonly MeshWorldRegistry _world;
        private readonly IMeshClock _clock;
        private readonly IMeshCoordinatorTransport _transport;
        private readonly MeshCoordinatorStore _store;
        private readonly IMeshLog _log;

        private readonly Dictionary<string, MeshCellBinding> _bindings = new Dictionary<string, MeshCellBinding>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, MapRecovery> _maps = new Dictionary<string, MapRecovery>(StringComparer.Ordinal);
        private readonly Dictionary<string, float> _firstSeenWithoutHello = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _helloMissingWarned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, float> _noteSentAt = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private readonly List<MeshMapServerPeer> _peers = new List<MeshMapServerPeer>();
        private MeshCoordinatorStoreData _storeData;
        private MeshLayoutSnapshot _broadcast;
        private bool _storeTrusted = true;
        private float _startedAt;

        public MeshLayoutCoordinator(MeshWorldRegistry world, IMeshClock clock, IMeshCoordinatorTransport transport, MeshCoordinatorStore store, IMeshLog log)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public MeshCoordinatorPhase Phase { get; private set; } = MeshCoordinatorPhase.Stopped;
        public long Incarnation { get; private set; }
        public MeshLayoutPair BroadcastPair => _broadcast != null ? _broadcast.pair : MeshLayoutPair.None;
        /// <summary>Whether operations may run: every map recovered and the store writable.</summary>
        public bool OpsAllowed => Phase == MeshCoordinatorPhase.Running && _store.Writable;
        public bool IsDynamicMap(string mapId) => _world.IsDynamicMap(mapId);
        public bool IsLayoutAnswered(string cellId) => _bindings.TryGetValue(cellId ?? string.Empty, out MeshCellBinding b) && b.answered && b.unregisteredAt < 0f;
        public MeshCellBinding BindingOf(string cellId) => _bindings.TryGetValue(cellId ?? string.Empty, out MeshCellBinding b) ? b : null;
        public MeshRecoveryOutcome OutcomeOf(string mapId) => _maps.TryGetValue(mapId ?? string.Empty, out MapRecovery m) ? m.outcome : MeshRecoveryOutcome.AdoptStatic;

        /// <summary>Start loads the store, takes a new incarnation, persists it, and recovers every Dynamic map.</summary>
        public void Start()
        {
            _startedAt = _clock.Now;
            string storeState = "fresh";
            if (!_store.TryLoad(out _storeData, out string loadError))
            {
                _storeTrusted = false;
                storeState = "unreadable";
                _log.Error(loadError);
                string suffix = ".unreadable-" + _clock.UtcTicks;
                if (_store.TryMoveAside(suffix, out string movedTo, out string moveError))
                    _log.Error($"Coordinator store moved aside to {movedTo}; recovery waits for every cell's hello instead");
                else
                    _log.Error(moveError);
                _storeData = null;
            }
            else if (_storeData != null)
                storeState = "loaded";
            MeshCoordinatorStoreData data = _storeData ?? new MeshCoordinatorStoreData();
            Incarnation = Math.Max(_clock.UtcTicks, data.lastIncarnation + 1);
            data.lastIncarnation = Incarnation;
            if (!_store.TrySave(data, out string saveError))
                _log.Error($"{saveError}; no operation will run until the store is writable again (hellos are still answered)");
            _storeData = data;
            SupervisionStart(data);
            PolicyStart();   // before the recovery: one that ends at once (the store adopted) starts the policy's fresh cooldowns
            RecoveryStart(data, storeState);
        }

        /// <summary>Stop forgets every binding and recovery; the registry's own reset owns the applied layout.</summary>
        public void Stop()
        {
            SplitStop();
            SupervisionStop();
            PolicyStop();
            RecoveryStop();
            _bindings.Clear();
            _maps.Clear();
            _firstSeenWithoutHello.Clear();
            _helloMissingWarned.Clear();
            _noteSentAt.Clear();
            Phase = MeshCoordinatorPhase.Stopped;
        }

        /// <summary>OnLayoutHello: a registered map server says hello with the layout it holds.</summary>
        public void OnLayoutHello(long connectionId, MeshLayoutHelloMessage hello)
        {
            if (Phase == MeshCoordinatorPhase.Stopped || hello == null || string.IsNullOrEmpty(hello.cellId))
                return;
            if (!_transport.TryGetMapServer(connectionId, out MeshMapServerPeer peer)
                || !string.Equals(peer.cellId, hello.cellId, StringComparison.OrdinalIgnoreCase) || peer.mapId != hello.mapId)
            {
                _log.Warning($"Layout hello from {hello.cellId} ({hello.mapId}) on connection {connectionId} does not match a registered map server{(peer.cellId != null ? $" (connection {connectionId} is {peer.cellId} of {peer.mapId})" : string.Empty)}; ignored");
                return;
            }
            if (!_world.IsDynamicMap(hello.mapId))
            {
                // Slice 5 decision 1: a Static map's cell waits for the Dynamic maps' recovery, else it would apply an
                // empty layout newer than the one the cells hold and prune its links to their children.
                if (Phase == MeshCoordinatorPhase.Recovering)
                    PendStaticHello(connectionId, hello);
                else
                    AnswerStaticHello(connectionId, hello);
                return;
            }
            if (HelloGetsRetire(connectionId, hello) || HelloIsFenced(connectionId, hello))
                return;
            string previousIncarnation = null;
            if (_bindings.TryGetValue(hello.cellId, out MeshCellBinding binding))
            {
                previousIncarnation = binding.incarnation;
                if (binding.connectionId != connectionId || binding.incarnation != hello.incarnation)
                    _log.Info($"Cell {hello.cellId} re-bound: incarnation {binding.incarnation} -> {hello.incarnation}, connection {binding.connectionId} -> {connectionId}");
            }
            else
            {
                binding = new MeshCellBinding { cellId = hello.cellId };
                _bindings[hello.cellId] = binding;
            }
            binding.mapId = hello.mapId;
            binding.connectionId = connectionId;
            binding.incarnation = hello.incarnation ?? string.Empty;
            binding.helloAt = _clock.Now;
            binding.answered = false;
            binding.applied = hello.held != null ? hello.held.pair : MeshLayoutPair.None;
            binding.held = hello.held != null ? hello.held.Clone() : new MeshLayoutSnapshot();
            binding.unregisteredAt = -1f;
            binding.mismatchLogged = false;
            binding.hasEvidence = false;
            binding.processId = hello.processId;
            if (!string.Equals(previousIncarnation, binding.incarnation, StringComparison.Ordinal))
            {
                binding.lastReportAt = -1f;
                binding.killedAt = -1f;
                binding.report = null;
            }
            _firstSeenWithoutHello.Remove(hello.cellId);

            MapRecovery map = MapOf(hello.mapId);
            RecoveryOnHello(hello, map);
            switch (map.outcome)
            {
                case MeshRecoveryOutcome.Waiting:
                    map.pending[hello.cellId] = (connectionId, binding.incarnation);
                    ResolveAll();
                    break;
                case MeshRecoveryOutcome.Frozen:
                    Note(connectionId, MeshNoteLevel.Error, hello.cellId, "recovery-frozen", $"map {hello.mapId} is frozen: {map.reason}; cell {hello.cellId} gets no layout");
                    break;
                default:
                    if (SupervisionAnswersHello(connectionId, binding, previousIncarnation) || OpAnswersHello(connectionId, hello, binding))
                        break;
                    Answer(connectionId, hello.cellId, binding.incarnation);
                    binding.answered = true;
                    break;
            }
        }

        /// <summary>AnswerStaticHello answers a Static map's cell with the current layout: a Static neighbour of a Dynamic map is bound (Slice 2b decision 14), anyone else noted.</summary>
        private void AnswerStaticHello(long connectionId, MeshLayoutHelloMessage hello)
        {
            Answer(connectionId, hello.cellId, hello.incarnation);
            if (!hello.dynamicMap && BandsWithDynamicMap(hello.cellId))
            {
                MeshCellBinding binding = BindStaticNeighbour(connectionId, hello.cellId, hello.mapId, hello.incarnation);
                binding.held = hello.held != null ? hello.held.Clone() : new MeshLayoutSnapshot();
                return;
            }
            Note(connectionId, MeshNoteLevel.Warning, hello.cellId, "mode-mismatch",
                $"map {hello.mapId} is Static on the central{(hello.dynamicMap ? " but Dynamic on cell " + hello.cellId : string.Empty)}; the cell was answered with no runtime layout");
        }

        private void Answer(long connectionId, string cellId, string incarnation)
        {
            _transport.Send(connectionId, new MeshLayoutMessage
            {
                kind = MeshLayoutKind.HelloAnswer,
                targetCellId = cellId,
                targetIncarnation = incarnation ?? string.Empty,
                stepId = string.Empty,
                snapshot = _broadcast.Clone(),
            });
        }

        /// <summary>OnLayoutAck: a cell applied or rejected a layout (only the bound connection and incarnation count).</summary>
        public void OnLayoutAck(long connectionId, MeshLayoutAckMessage ack)
        {
            if (Phase == MeshCoordinatorPhase.Stopped)
                return;
            MeshCellBinding binding = BindingOf(ack.cellId);
            if (binding == null || (binding.staticNeighbour && (binding.connectionId != connectionId || binding.incarnation != (ack.incarnation ?? string.Empty))))
                binding = BindStaticFromAck(connectionId, ack) ?? binding;
            if (binding == null || binding.connectionId != connectionId || binding.incarnation != (ack.incarnation ?? string.Empty))
                return;
            if (ack.result == MeshLayoutAckResult.Applied)
            {
                if (ack.pair.IsNewerThan(binding.applied))
                    binding.applied = ack.pair;
            }
            else
                _log.Warning($"Cell {ack.cellId} rejected layout {ack.pair}: {ack.reason}");
            OpOnAck(ack);
        }

        /// <summary>OnLoadReport: a bound cell's report is evidence it is alive and what layout it measured under.</summary>
        public void OnLoadReport(long connectionId, MeshLoadReportMessage report)
        {
            if (report == null)
                return;
            MeshCellBinding binding = BindingOf(report.cellId);
            if (binding == null || binding.connectionId != connectionId || binding.incarnation != (report.incarnation ?? string.Empty))
                return;
            binding.lastEvidenceAt = _clock.Now;
            binding.lastReportAt = _clock.Now;
            binding.report = report;
            MeshLayoutPair reported = new MeshLayoutPair(report.layoutIncarnation, report.layoutVersion);
            // A report measured before the cell's latest apply lags by design (a split moves the pair every step); one
            // ahead of every ack means the cell holds a layout this coordinator never heard it apply.
            if (!binding.applied.IsNone && reported.IsNewerThan(binding.applied) && !binding.mismatchLogged)
            {
                binding.mismatchLogged = true;
                _log.Warning($"Cell {report.cellId} reports layout {reported} but acknowledged {binding.applied}");
            }
        }

        /// <summary>Tick runs once a second: registrations, missing hellos, recovery, supervision, and the operation's step.</summary>
        public void Tick()
        {
            if (Phase == MeshCoordinatorPhase.Stopped)
                return;
            float now = _clock.Now;
            _peers.Clear();
            _transport.GetMapServers(_peers);
            foreach (MeshCellBinding binding in _bindings.Values)
            {
                bool present = false;
                foreach (MeshMapServerPeer peer in _peers)
                {
                    if (peer.connectionId == binding.connectionId && string.Equals(peer.cellId, binding.cellId, StringComparison.OrdinalIgnoreCase))
                    {
                        present = true;
                        break;
                    }
                }
                if (!present && binding.unregisteredAt < 0f)
                {
                    binding.unregisteredAt = now;
                    _log.Info($"Cell {binding.cellId} unregistered from the cluster (connection {binding.connectionId}, incarnation {binding.incarnation})");
                }
            }
            foreach (MeshMapServerPeer peer in _peers)
            {
                if (string.IsNullOrEmpty(peer.cellId) || !_world.IsDynamicMap(peer.mapId))
                    continue;
                MeshCellBinding binding = BindingOf(peer.cellId);
                if (binding != null && binding.connectionId == peer.connectionId && binding.unregisteredAt < 0f)
                    continue;
                if (!_firstSeenWithoutHello.TryGetValue(peer.cellId, out float since))
                {
                    _firstSeenWithoutHello[peer.cellId] = now;
                    continue;
                }
                if (now - since >= HelloMissingWarnSeconds && _helloMissingWarned.Add(peer.cellId))
                    _log.Warning($"Cell {peer.cellId} of Dynamic map {peer.mapId} is registered but sent no LayoutHello after {now - since:0} s (an older build, or its map is Static there)");
            }
            if (Phase == MeshCoordinatorPhase.Recovering)
                ResolveAll();
            SupervisionTick();
            UnfreezeTick();
            OpTick();
            PolicyTick();
        }

        private MapRecovery MapOf(string mapId)
        {
            if (!_maps.TryGetValue(mapId, out MapRecovery map))
                _maps[mapId] = map = new MapRecovery();
            return map;
        }

        /// <summary>Note logs a decision here and on the cell it concerns, at most once a minute per code and cell.</summary>
        private void Note(long connectionId, MeshNoteLevel level, string cellId, string code, string text)
        {
            string key = code + "|" + cellId;
            float now = _clock.Now;
            if (_noteSentAt.TryGetValue(key, out float sentAt) && now - sentAt < NoteRepeatSeconds)
                return;
            _noteSentAt[key] = now;
            switch (level)
            {
                case MeshNoteLevel.Error: _log.Error($"Coordinator note to {cellId} ({code}): {text}"); break;
                case MeshNoteLevel.Warning: _log.Warning($"Coordinator note to {cellId} ({code}): {text}"); break;
                default: _log.Info($"Coordinator note to {cellId} ({code}): {text}"); break;
            }
            _transport.Send(connectionId, new MeshCoordinatorNoteMessage { level = level, subjectCellId = cellId, code = code, text = text, pair = BroadcastPair });
        }
    }
}
