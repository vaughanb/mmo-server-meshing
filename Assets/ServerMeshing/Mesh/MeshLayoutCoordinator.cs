// Server Meshing add-on — the central's layout coordinator (DESIGN.md 0.72, plan D3). It owns the runtime layout of
// every Dynamic map: it recovers it when the central starts, answers each cell's hello with the layout to hold (which
// opens the cell's gate), and tells the cells what it decided. Splits and merges come in later slices; this is the
// skeleton they plug into. A plain class with its clock, transport, store and log injected, so it is tested without
// Unity networking. Nothing throws out of a handler or out of Tick: each validates, then changes state.
using System;
using System.Collections.Generic;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>IMeshClock is the coordinator's time.</summary>
    public interface IMeshClock
    {
        /// <summary>Seconds, monotonic.</summary>
        float Now { get; }
        /// <summary>Wall-clock UTC ticks (a new coordinator incarnation is at least this).</summary>
        long UtcTicks { get; }
    }

    /// <summary>MeshUnityClock is Unity's unscaled time and the wall clock.</summary>
    public sealed class MeshUnityClock : IMeshClock
    {
        public float Now => Time.unscaledTime;
        public long UtcTicks => DateTime.UtcNow.Ticks;
    }

    /// <summary>IMeshLog is where the coordinator writes its lines.</summary>
    public interface IMeshLog
    {
        void Info(string message);
        void Warning(string message);
        void Error(string message);
    }

    /// <summary>MeshKitLog writes to the kit's log under the Mesh tag.</summary>
    public sealed class MeshKitLog : IMeshLog
    {
        public void Info(string message) => Logging.Log("Mesh", message);
        public void Warning(string message) => Logging.LogWarning("Mesh", message);
        public void Error(string message) => Logging.LogError("Mesh", message);
    }

    /// <summary>MeshMapServerPeer is a registered map server as the coordinator sees it.</summary>
    public struct MeshMapServerPeer
    {
        public long connectionId;
        public string cellId;
        public string mapId;
        public string address;
        public int port;
    }

    /// <summary>IMeshCoordinatorTransport is how the coordinator reaches the map servers.</summary>
    public interface IMeshCoordinatorTransport
    {
        /// <summary>Every registered map server (cells, not instances or spawners), into a reused list.</summary>
        void GetMapServers(List<MeshMapServerPeer> into);
        bool TryGetMapServer(long connectionId, out MeshMapServerPeer peer);
        void Send(long connectionId, MeshLayoutMessage message);
        void Send(long connectionId, MeshRetireMessage message);
        void Send(long connectionId, MeshCoordinatorNoteMessage message);
    }

    /// <summary>MeshCellBinding is one cell of a Dynamic map as the coordinator knows it: the map server that said hello.</summary>
    public sealed class MeshCellBinding
    {
        public string cellId;
        public string mapId;
        public long connectionId;
        public string incarnation;
        public float helloAt;
        /// <summary>Whether the cell was answered, so its gate is open.</summary>
        public bool answered;
        /// <summary>The last layout the cell acknowledged as applied.</summary>
        public MeshLayoutPair applied;
        public float lastEvidenceAt = -1f;
        /// <summary>When the cell was seen gone from the cluster (-1 while registered).</summary>
        public float unregisteredAt = -1f;
        public bool mismatchLogged;
    }

    /// <summary>MeshCoordinatorPhase is where the coordinator is.</summary>
    public enum MeshCoordinatorPhase
    {
        Stopped,
        Recovering,
        Running,
    }

    /// <summary>MeshLayoutCoordinator is the central's owner of the runtime layout.</summary>
    public sealed class MeshLayoutCoordinator
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

        private sealed class MapRecovery
        {
            public MeshRecoveryOutcome outcome = MeshRecoveryOutcome.Waiting;
            public int storedEntries;
            public bool helloHeldEntries;
            public string reason = "";
            public readonly Dictionary<string, (long connection, string incarnation)> pending = new Dictionary<string, (long, string)>(StringComparer.OrdinalIgnoreCase);
        }

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
            _broadcast = new MeshLayoutSnapshot { pair = new MeshLayoutPair(Incarnation, 1) };
            Phase = MeshCoordinatorPhase.Recovering;
            List<string> summary = new List<string>();
            foreach (MeshMapLayout layout in _world.maps)
            {
                if (layout == null || string.IsNullOrEmpty(layout.mapId) || _maps.ContainsKey(layout.mapId))
                    continue;
                MapRecovery map = new MapRecovery { storedEntries = StoredEntries(data, layout.mapId) };
                _maps[layout.mapId] = map;
                if (!layout.IsDynamic && map.storedEntries > 0)
                    _log.Warning($"Coordinator: map {layout.mapId} is Static now; {map.storedEntries} stored dynamic entries ignored");
            }
            ResolveAll();
            foreach (KeyValuePair<string, MapRecovery> map in _maps)
            {
                if (_world.IsDynamicMap(map.Key))
                    summary.Add($"{map.Key} {map.Value.outcome}");
            }
            _log.Info($"Coordinator {Incarnation:x}: Dynamic maps {(summary.Count > 0 ? string.Join(", ", summary) : "none")}; store {_store.Path} ({storeState}); broadcast {_broadcast.pair}");
        }

        private static int StoredEntries(MeshCoordinatorStoreData data, string mapId)
        {
            int count = 0;
            foreach (MeshLayoutEntry entry in data.broadcast.entries)
            {
                if (entry != null && entry.mapId == mapId)
                    count++;
            }
            if (data.hasStaged)
            {
                foreach (MeshLayoutEntry entry in data.staged.entries)
                {
                    if (entry != null && entry.mapId == mapId)
                        count++;
                }
            }
            return count;
        }

        /// <summary>Stop forgets every binding and recovery; the registry's own reset owns the applied layout.</summary>
        public void Stop()
        {
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
                Answer(connectionId, hello.cellId, hello.incarnation);
                Note(connectionId, MeshNoteLevel.Warning, hello.cellId, "mode-mismatch",
                    $"map {hello.mapId} is Static on the central{(hello.dynamicMap ? " but Dynamic on cell " + hello.cellId : string.Empty)}; the cell was answered with no runtime layout");
                return;
            }
            if (_bindings.TryGetValue(hello.cellId, out MeshCellBinding binding))
            {
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
            binding.unregisteredAt = -1f;
            binding.mismatchLogged = false;
            _firstSeenWithoutHello.Remove(hello.cellId);

            MapRecovery map = MapOf(hello.mapId);
            if (HeldEntriesOf(hello) > 0 && !map.helloHeldEntries)
            {
                // A cell still holds a runtime layout from before this coordinator: the map cannot run its asset cut blindly.
                map.helloHeldEntries = true;
                if (map.outcome != MeshRecoveryOutcome.Frozen)
                {
                    map.outcome = MeshRecoveryOutcome.Waiting;
                    Phase = MeshCoordinatorPhase.Recovering;
                    _log.Warning($"Cell {hello.cellId} holds layout {hello.held.pair} of map {hello.mapId} from an earlier coordinator: map {hello.mapId} re-opens recovery");
                }
                ResolveAll();
            }
            switch (map.outcome)
            {
                case MeshRecoveryOutcome.Waiting:
                    map.pending[hello.cellId] = (connectionId, binding.incarnation);
                    break;
                case MeshRecoveryOutcome.Frozen:
                    Note(connectionId, MeshNoteLevel.Error, hello.cellId, "recovery-frozen", $"map {hello.mapId} is frozen: {map.reason}; cell {hello.cellId} gets no layout");
                    break;
                default:
                    Answer(connectionId, hello.cellId, binding.incarnation);
                    binding.answered = true;
                    break;
            }
        }

        private int HeldEntriesOf(MeshLayoutHelloMessage hello)
        {
            int count = 0;
            if (hello.held != null && hello.held.entries != null)
            {
                foreach (MeshLayoutEntry entry in hello.held.entries)
                {
                    if (entry != null && entry.mapId == hello.mapId)
                        count++;
                }
            }
            return count;
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
            MeshCellBinding binding = BindingOf(ack.cellId);
            if (binding == null || binding.connectionId != connectionId || binding.incarnation != (ack.incarnation ?? string.Empty))
                return;
            if (ack.result == MeshLayoutAckResult.Applied)
                binding.applied = ack.pair;
            else
                _log.Warning($"Cell {ack.cellId} rejected layout {ack.pair}: {ack.reason}");
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
            MeshLayoutPair reported = new MeshLayoutPair(report.layoutIncarnation, report.layoutVersion);
            if (!binding.applied.IsNone && reported != binding.applied && !binding.mismatchLogged)
            {
                binding.mismatchLogged = true;
                _log.Warning($"Cell {report.cellId} reports layout {reported} but acknowledged {binding.applied}");
            }
        }

        /// <summary>Tick runs once a second: registrations, missing hellos, and recovery.</summary>
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
        }

        private MapRecovery MapOf(string mapId)
        {
            if (!_maps.TryGetValue(mapId, out MapRecovery map))
                _maps[mapId] = map = new MapRecovery();
            return map;
        }

        private void ResolveAll()
        {
            bool allSettled = true;
            foreach (KeyValuePair<string, MapRecovery> pair in _maps)
            {
                MapRecovery map = pair.Value;
                if (map.outcome != MeshRecoveryOutcome.Waiting)
                    continue;
                MeshRecoveryInput input = new MeshRecoveryInput
                {
                    mapId = pair.Key,
                    mapDynamic = _world.IsDynamicMap(pair.Key),
                    storeTrusted = _storeTrusted,
                    storedEntries = map.storedEntries,
                    helloHeldEntries = map.helloHeldEntries,
                    elapsedSeconds = _clock.Now - _startedAt,
                    ceilingSeconds = _world.dynamicSettings.recoveryCeilingSeconds,
                };
                _peers.Clear();
                _transport.GetMapServers(_peers);
                foreach (MeshMapServerPeer peer in _peers)
                {
                    if (peer.mapId == pair.Key && !string.IsNullOrEmpty(peer.cellId))
                        input.registeredCells.Add(peer.cellId);
                }
                foreach (MeshCellBinding binding in _bindings.Values)
                {
                    if (binding.mapId == pair.Key && binding.unregisteredAt < 0f)
                        input.helloCells.Add(binding.cellId);
                }
                MeshRecoveryDecision decision = MeshRecoveryResolver.Resolve(input);
                if (decision.outcome == MeshRecoveryOutcome.Waiting)
                {
                    allSettled = false;
                    continue;
                }
                map.outcome = decision.outcome;
                map.reason = decision.reason;
                if (decision.outcome == MeshRecoveryOutcome.AdoptStatic)
                    Adopt(pair.Key, map, decision);
                else
                    Freeze(pair.Key, map, decision);
            }
            if (allSettled && Phase == MeshCoordinatorPhase.Recovering)
                Phase = MeshCoordinatorPhase.Running;
        }

        private void Adopt(string mapId, MapRecovery map, MeshRecoveryDecision decision)
        {
            if (decision.isError)
                _log.Error("Coordinator recovery: " + decision.reason);
            else if (_world.IsDynamicMap(mapId))
                _log.Info("Coordinator recovery: " + decision.reason);
            // The central's own registry (read by the login) holds the broadcast pair too.
            if (_world.IsDynamicMap(mapId) && _world.AppliedPair != _broadcast.pair && !_world.ApplyLayout(_broadcast, out string reason))
            {
                map.outcome = MeshRecoveryOutcome.Frozen;
                map.reason = $"the central could not apply {_broadcast.pair}: {reason}";
                _log.Error($"Coordinator recovery: map {mapId} frozen, {map.reason}");
                return;
            }
            foreach (KeyValuePair<string, (long connection, string incarnation)> pending in map.pending)
            {
                MeshCellBinding binding = BindingOf(pending.Key);
                if (binding == null || binding.connectionId != pending.Value.connection || binding.incarnation != pending.Value.incarnation)
                    continue;
                Answer(pending.Value.connection, pending.Key, pending.Value.incarnation);
                binding.answered = true;
            }
            map.pending.Clear();
        }

        private void Freeze(string mapId, MapRecovery map, MeshRecoveryDecision decision)
        {
            _log.Error("Coordinator recovery: frozen: " + decision.reason);
            foreach (MeshCellBinding binding in _bindings.Values)
            {
                if (binding.mapId == mapId && binding.unregisteredAt < 0f)
                    Note(binding.connectionId, MeshNoteLevel.Error, binding.cellId, "recovery-frozen", $"map {mapId} is frozen: {decision.reason}");
            }
            map.pending.Clear();
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
