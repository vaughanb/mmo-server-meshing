// Server Meshing add-on — MeshCentralNetworkManager: the kit's central server with the world registry's cells as channels,
// and (sleeping cells, DESIGN.md Q13) the keeper of the awake set.
//
// Channels: a cell is served by the map server whose channel id is the cell id, and the kit's login and cluster code
// accept a channel only if the central server lists it (character selection, the per-channel connection limit, the
// channel list the client sees). This subclass appends every cell of every layout in the registry to the central's
// channel list before it is read (the configured channels are kept), so no cell has to be typed into the server config.
//
// Sleeping cells: with `sleepCells` on, no cell is started at boot. Every tick the awake set is computed from the
// cluster's per-channel user counts — every cell with a player, plus `awakeRings` rings of neighbours around it, plus
// cells someone asked to wake (a login into it) — and compared with the registered map servers: a wanted cell that is
// not up is started through a map-spawn server with the kit's own spawn request (which does not auto-restart, so the
// loop is also the supervisor for a crashed cell), and a cell that has been unwanted for `sleepAfterSeconds` is told
// to exit. A player therefore never reaches a sleeping cell: the ring wakes it a full cell ahead (about 3 s to serve
// on the rig against 10 s or more of travel).
//
// Dynamic cells (DESIGN.md 0.72): when the world has a Dynamic map, this server also runs the layout coordinator
// (MeshLayoutCoordinator), which answers each cell's layout hello and runs manual splits (Slice 2b,
// MeshCentralNetworkManager_Split.cs); with none, nothing of it runs. A split family is never slept or started by the
// awake set, and a wake request for a dynamic id is refused: the coordinator runs those cells, and its supervisor (Slice
// 2d) restarts a crashed or hung one of them, a sleep-mode base included, through the map-spawn servers.
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using LiteNetLib;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class MeshCentralNetworkManager : CentralNetworkManager
    {
        [Header("Server Meshing")]
        [Tooltip("World registry: every cell of every layout becomes a channel of this central server.")]
        public MeshWorldRegistry world;

        [Header("Sleeping cells")]
        [Tooltip("Cells run only while a player is in them or within awakeRings of them; started on demand, stopped after sleepAfterSeconds. (Rig: MESH_SLEEP=1.)")]
        public bool sleepCells = false;
        [Tooltip("Rings of neighbours kept awake around a cell with players. 2 keeps a cell awake a full cell ahead of a player going anywhere.")]
        public int awakeRings = 2;
        [Tooltip("Seconds a cell stays up after it stopped being wanted.")]
        public float sleepAfterSeconds = 300f;
        [Tooltip("Seconds between awake-set evaluations.")]
        public float awakeTickSeconds = 2f;
        [Tooltip("Seconds a wake request (a login into a sleeping cell) keeps the cell wanted while the player arrives.")]
        public float wakeHoldSeconds = 60f;
        [Tooltip("Seconds before a start request that produced no registration is repeated.")]
        public float spawnRequestTimeoutSeconds = 20f;

        private readonly Dictionary<string, float> _wakeHolds = new Dictionary<string, float>();      // cell id -> wanted until
        private readonly Dictionary<string, float> _spawnRequested = new Dictionary<string, float>(); // cell id -> when
        private readonly Dictionary<string, float> _unwantedSince = new Dictionary<string, float>();  // cell id -> when it stopped being wanted
        private readonly Dictionary<string, float> _sleepSent = new Dictionary<string, float>();      // cell id -> when told to sleep
        private readonly HashSet<string> _wanted = new HashSet<string>();
        private readonly List<MeshCell> _ring = new List<MeshCell>();
        private float _nextTick;

        protected override void Initialize()
        {
            world = MeshLayoutOverride.Resolve(world);
            if (world != null)
            {
                if (channels == null)
                    channels = new List<ChannelData>();
                HashSet<string> known = new HashSet<string>();
                foreach (ChannelData channel in channels)
                    known.Add(channel.id);
                int added = 0;
                IReadOnlyList<MeshCell> cells = world.Cells;
                for (int i = 0; i < cells.Count; ++i)
                {
                    if (!known.Add(cells[i].id))
                        continue;
                    channels.Add(new ChannelData { id = cells[i].id, title = cells[i].id, maxConnections = defaultChannelMaxConnections > 0 ? defaultChannelMaxConnections : 500 });
                    added++;
                }
                Logging.Log("Mesh", $"Central channels from the world registry: {channels.Count} ({added} added)");
            }
            if (MeshSleepConfig.EnvEnabled)
            {
                sleepCells = true;
                awakeRings = MeshSleepConfig.EnvRings(awakeRings);
                sleepAfterSeconds = MeshSleepConfig.EnvAfterSeconds(sleepAfterSeconds);
            }
            base.Initialize();
            // Registered whether or not cells sleep: a spawner advertises its cells as soon as it registers, which
            // can be long before anyone turns sleeping on, and an unhandled message is simply dropped.
            if (ClusterServer != null)
            {
                ClusterServer.RegisterMessageHandler(MeshClusterMsg.Serves, HandleServesMessage);
                ClusterServer.RegisterMessageHandler(MeshClusterMsg.LoadReport, HandleLoadReport);
                // Registered whether or not a map is Dynamic here: a cell whose asset says otherwise still gets an
                // answer (and a note), so its gate never waits for ever.
                ClusterServer.RegisterMessageHandler(MeshClusterMsg.LayoutHello, HandleLayoutHello);
                ClusterServer.RegisterMessageHandler(MeshClusterMsg.LayoutAck, HandleLayoutAck);
                ClusterServer.RegisterMessageHandler(MeshClusterMsg.LayoutEvidence, HandleLayoutEvidence);
                // Answered whether or not cells sleep: a cell asks whenever a neighbour it needs is not registered, and an
                // unregistered request type makes the kit log an error for every one of them.
                ClusterServer.RegisterRequestHandler<MeshWakeRequest, MeshWakeResponse>(MeshClusterMsg.Wake, HandleWakeRequest);
            }
            if (sleepCells && world != null && ClusterServer != null)
                Logging.Log("Mesh", $"Sleeping cells ON: awake rings {awakeRings}, sleep after {sleepAfterSeconds:0} s; cells start on demand");
        }

#if NET || NETCOREAPP || ((UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE)
        public override void OnStartServer()
        {
            base.OnStartServer();   // starts the cluster server, then awaits (the rest of the kit's start runs later)
            if (world == null || !world.HasDynamicMap)
                return;
            if (ClusterServer == null || !ClusterServer.IsNetworkActive)
            {
                Logging.LogError("Mesh", "The cluster server did not start: no layout coordinator, so the cells of Dynamic maps stay gated");
                return;
            }
            MeshCoordinatorStore store = new MeshCoordinatorStore(MeshCoordinatorStore.ResolvePath(world.dynamicSettings.coordinatorStorePath));
            _coordinator = new MeshLayoutCoordinator(world, new MeshUnityClock(), new MeshClusterCoordinatorTransport(ClusterServer), store, new MeshKitLog());
            AttachSplit(_coordinator);
            _coordinator.Start();
            _nextCoordinatorTick = 0f;
        }

        public override void OnStopServer()
        {
            _coordinator?.Stop();
            _coordinator = null;
            base.OnStopServer();
        }
#endif

        private MeshLayoutCoordinator _coordinator;
        private float _nextCoordinatorTick;

        /// <summary>The layout coordinator, when the world has a Dynamic map and this process is the central.</summary>
        public MeshLayoutCoordinator Coordinator => _coordinator;

        protected override void Update()
        {
            base.Update();
            if (_coordinator != null && IsServer && Time.unscaledTime >= _nextCoordinatorTick)
            {
                _nextCoordinatorTick = Time.unscaledTime + 1f;
                _coordinator.Tick();
            }
            if (loadSummarySeconds > 0f && Time.unscaledTime >= _nextLoadSummary && LoadBoard.Count > 0)
            {
                _nextLoadSummary = Time.unscaledTime + loadSummarySeconds;
                Logging.Log("Mesh", "Cell load: " + LoadBoard.Summary(Time.unscaledTime, loadSummarySeconds));
            }
            if (!sleepCells || world == null || ClusterServer == null || Time.unscaledTime < _nextTick)
                return;
            _nextTick = Time.unscaledTime + awakeTickSeconds;
            AwakeTick();
        }

        [Header("Load reports")]
        [Tooltip("Seconds between the central's one-line summary of every cell's latest load report. 0 = off.")]
        public float loadSummarySeconds = 30f;
        private float _nextLoadSummary;

        /// <summary>LoadBoard holds every cell's latest load report (MeshLoadReportMessage).</summary>
        public MeshLoadBoard LoadBoard { get; } = new MeshLoadBoard();

        private void HandleLoadReport(MessageHandlerData messageHandler)
        {
            MeshLoadReportMessage report = new MeshLoadReportMessage();
            try
            {
                report.Deserialize(messageHandler.Reader);
            }
            catch (System.Exception ex)
            {
                Logging.LogWarning("Mesh", $"Unreadable load report from connection {messageHandler.ConnectionId}: {ex.Message}");
                return;
            }
            if (messageHandler.Reader.AvailableBytes != 0)
            {
                // A report that does not end where the packet does was written by another version of the message.
                Logging.LogWarning("Mesh", $"Load report of cell {report.cellId} from connection {messageHandler.ConnectionId} has {messageHandler.Reader.AvailableBytes} unread bytes; dropped");
                return;
            }
            if (!LoadBoard.Record(report, Time.unscaledTime))
                Logging.LogWarning("Mesh", $"Load report without a cell id from connection {messageHandler.ConnectionId}; ignored");
            else if (_coordinator != null && _coordinator.IsDynamicMap(report.mapId))
                _coordinator.OnLoadReport(messageHandler.ConnectionId, report);
        }

        private void HandleLayoutHello(MessageHandlerData messageHandler)
        {
            if (!ReadWhole(messageHandler, "Layout hello", out MeshLayoutHelloMessage hello))
                return;
            if (_coordinator != null)
            {
                _coordinator.OnLayoutHello(messageHandler.ConnectionId, hello);
                return;
            }
            // No Dynamic map here: answer with no runtime layout, so a cell that runs its map Dynamic is not gated for ever.
            MeshLayoutMessage answer = new MeshLayoutMessage { kind = MeshLayoutKind.HelloAnswer, targetCellId = hello.cellId, targetIncarnation = hello.incarnation, snapshot = new MeshLayoutSnapshot() };
            ClusterServer.SendPacket(messageHandler.ConnectionId, 0, DeliveryMethod.ReliableOrdered, MeshClusterMsg.Layout, writer => answer.Serialize(writer));
            MeshCoordinatorNoteMessage note = new MeshCoordinatorNoteMessage
            {
                level = MeshNoteLevel.Warning, subjectCellId = hello.cellId, code = "mode-mismatch",
                text = $"map {hello.mapId} is not Dynamic on the central (no layout coordinator runs); the cell was answered with no runtime layout",
            };
            ClusterServer.SendPacket(messageHandler.ConnectionId, 0, DeliveryMethod.ReliableOrdered, MeshClusterMsg.CoordinatorNote, writer => note.Serialize(writer));
            Logging.LogWarning("Mesh", $"Layout hello from cell {hello.cellId} ({hello.mapId}) but no map is Dynamic here: answered with no runtime layout");
        }

        private void HandleLayoutAck(MessageHandlerData messageHandler)
        {
            if (!ReadWhole(messageHandler, "Layout ack", out MeshLayoutAckMessage ack))
                return;
            _coordinator?.OnLayoutAck(messageHandler.ConnectionId, ack);
        }

        private static bool ReadWhole<T>(MessageHandlerData messageHandler, string what, out T message) where T : LiteNetLib.Utils.INetSerializable, new()
        {
            if (MeshWholeMessage.TryRead(messageHandler.Reader, out message, out string error))
                return true;
            Logging.LogWarning("Mesh", $"{what} from connection {messageHandler.ConnectionId} dropped: {error}");
            return false;
        }

        /// <summary>A cell's map server is registered with the cluster.</summary>
        public bool IsCellUp(MeshCell cell)
        {
            return cell != null && ClusterServer != null && ClusterServer.MapServerPeersByKey.ContainsKey(MeshSleepConfig.PeerKey(cell));
        }

        /// <summary>Keep a cell (and its rings) wanted for a while: someone is about to arrive there. Returns when it is up, or on timeout.</summary>
        public async UniTask<bool> WakeAndWait(MeshCell cell, float timeoutSeconds)
        {
            if (cell == null)
                return false;
            if (cell.isDynamic)
                return IsCellUp(cell);   // the coordinator runs dynamic cells; nothing here starts one
            _wakeHolds[cell.id] = Time.unscaledTime + wakeHoldSeconds;
            if (IsCellUp(cell))
                return true;
            _nextTick = 0f; // evaluate now
            float deadline = Time.unscaledTime + timeoutSeconds;
            while (Time.unscaledTime < deadline)
            {
                if (IsCellUp(cell))
                    return true;
                await UniTask.Delay(200);
            }
            return IsCellUp(cell);
        }

        /// <summary>Which map-spawn server may start a given cell, from what each spawner advertised at registration.</summary>
        private readonly Dictionary<string, long> _spawnerByCell = new Dictionary<string, long>(System.StringComparer.OrdinalIgnoreCase); // cell ids compare as the spawner's Serves does

        private void HandleServesMessage(MessageHandlerData messageHandler)
        {
            MeshServesMessage message = messageHandler.ReadMessage<MeshServesMessage>();
            if (message.cellIds == null)
                return;
            for (int i = 0; i < message.cellIds.Length; ++i)
            {
                if (!string.IsNullOrEmpty(message.cellIds[i]))
                    _spawnerByCell[message.cellIds[i]] = messageHandler.ConnectionId;
            }
            Logging.Log("Mesh", $"Map-spawn server on connection {messageHandler.ConnectionId} serves {string.Join(",", message.cellIds)}");
        }

        private UniTaskVoid HandleWakeRequest(RequestHandlerData requestHandler, MeshWakeRequest request, RequestProceedResultDelegate<MeshWakeResponse> result)
        {
            MeshCell cell = world != null ? world.GetCell(request.mapId, request.cellId) ?? world.GetCellById(request.cellId) : null;
            if ((cell != null && IsSplitFamilyCell(cell)) || (_coordinator != null && _coordinator.IsOperationChild(request.cellId)))
            {
                Logging.LogWarning("Mesh", $"Wake requested for {(cell != null && !cell.isDynamic ? "split-family base cell" : "dynamic cell")} {request.cellId} by connection {requestHandler.ConnectionId}: refused, the layout coordinator runs it");
                result.Invoke(AckResponseCode.Success, new MeshWakeResponse { accepted = false, reason = MeshWakeRefusal.UnknownCell });
                return default;
            }
            if (!sleepCells)
            {
                result.Invoke(AckResponseCode.Success, new MeshWakeResponse { accepted = false, reason = MeshWakeRefusal.SleepingOff });
                return default;
            }
            if (cell != null)
            {
                _wakeHolds[cell.id] = Time.unscaledTime + wakeHoldSeconds;
                _nextTick = 0f;
                Logging.Log("Mesh", $"Wake requested for cell {cell.id} ({cell.mapId}) by connection {requestHandler.ConnectionId}");
            }
            result.Invoke(AckResponseCode.Success, new MeshWakeResponse { accepted = cell != null, reason = cell != null ? MeshWakeRefusal.None : MeshWakeRefusal.UnknownCell });
            return default;
        }

        private void AddWithRings(MeshCell cell)
        {
            _wanted.Add(cell.id);
            _ring.Clear();
            _ring.Add(cell);
            int start = 0;
            for (int ring = 0; ring < awakeRings; ++ring)
            {
                int end = _ring.Count;
                for (int i = start; i < end; ++i)
                {
                    for (int n = 0; n < _ring[i].neighbours.Count; ++n)
                    {
                        MeshCell neighbour = _ring[i].neighbours[n];
                        if (_wanted.Add(neighbour.id))
                            _ring.Add(neighbour);
                    }
                }
                start = end;
            }
        }

        private void AwakeTick()
        {
            float now = Time.unscaledTime;
            _wanted.Clear();
            IReadOnlyList<MeshCell> cells = world.Cells;
            for (int i = 0; i < cells.Count; ++i)
            {
                MeshCell cell = cells[i];
                if (ClusterServer.CountUsers(cell.id) > 0 || (_wakeHolds.TryGetValue(cell.id, out float until) && until > now))
                    AddWithRings(cell);
            }
            for (int i = 0; i < cells.Count; ++i)
            {
                MeshCell cell = cells[i];
                if (IsSplitFamilyCell(cell))
                {
                    // A split family is never slept and never started here: its cells merge first (plan D3).
                    _unwantedSince.Remove(cell.id);
                    _sleepSent.Remove(cell.id);
                    continue;
                }
                bool up = IsCellUp(cell);
                bool wanted = _wanted.Contains(cell.id);
                if (wanted)
                {
                    _unwantedSince.Remove(cell.id);
                    _sleepSent.Remove(cell.id);
                    if (up)
                    {
                        _spawnRequested.Remove(cell.id);
                    }
                    else if (!_spawnRequested.TryGetValue(cell.id, out float requestedAt) || now - requestedAt > spawnRequestTimeoutSeconds)
                    {
                        RequestStart(cell, now);
                    }
                    continue;
                }
                if (!up)
                {
                    _unwantedSince.Remove(cell.id);
                    _sleepSent.Remove(cell.id);
                    continue;
                }
                if (!_unwantedSince.TryGetValue(cell.id, out float since))
                {
                    _unwantedSince[cell.id] = now;
                    continue;
                }
                if (now - since >= sleepAfterSeconds && (!_sleepSent.TryGetValue(cell.id, out float sentAt) || now - sentAt > 10f))
                {
                    _sleepSent[cell.id] = now;
                    CentralServerPeerInfo peer = ClusterServer.MapServerPeersByKey[MeshSleepConfig.PeerKey(cell)];
                    MeshSleepMessage message = new MeshSleepMessage { cellId = cell.id };
                    ClusterServer.SendPacket(peer.connectionId, 0, DeliveryMethod.ReliableOrdered, MeshClusterMsg.Sleep, writer => message.Serialize(writer));
                    Logging.Log("Mesh", $"Cell {cell.id} ({cell.mapId}) unwanted for {now - since:0} s: told to sleep");
                }
            }
        }

        /// <summary>
        /// SpawnerFor is the map-spawn server to ask for a cell: the one that advertised it (for a dynamic cell, its base
        /// cell's) while that one is still registered, else the first registered spawner, else -1.
        /// </summary>
        public static long SpawnerFor(MeshCell cell, IReadOnlyDictionary<string, long> spawnerByCell, ICollection<long> registered, out bool advertised)
        {
            advertised = false;
            string key = cell.isDynamic ? cell.baseId : cell.id;
            if (!string.IsNullOrEmpty(key) && spawnerByCell.TryGetValue(key, out long owner) && registered.Contains(owner))
            {
                advertised = true;
                return owner;
            }
            foreach (long connectionId in registered)
                return connectionId;
            return -1;
        }

        private void RequestStart(MeshCell cell, float now)
        {
            // The spawner that advertised this cell, if any: the kit's spawner starts whatever it is asked to, so
            // asking a host that does not serve the cell starts it on the wrong machine. Falling back to the first
            // known spawner keeps the single-host case, where every spawner serves everything, exactly as it was.
            long spawnerConnectionId = SpawnerFor(cell, _spawnerByCell, ClusterServer.MapSpawnServerPeers.Keys, out bool advertised);
            if (!advertised && spawnerConnectionId >= 0 && _spawnerByCell.Count > 0 && !_spawnRequested.ContainsKey(cell.id))
                Logging.LogWarning("Mesh", $"No spawner advertised cell {cell.id}; asking {spawnerConnectionId}, which starts it on its own host");
            if (spawnerConnectionId < 0)
            {
                if (!_spawnRequested.ContainsKey(cell.id))
                    Logging.LogWarning("Mesh", $"Cell {cell.id} is wanted but no map-spawn server is registered yet");
                _spawnRequested[cell.id] = now;
                return;
            }
            _spawnRequested[cell.id] = now;
            string key = MeshSleepConfig.PeerKey(cell);
            Logging.Log("Mesh", $"Waking cell {cell.id} ({cell.mapId}): start requested from map-spawn server {spawnerConnectionId}");
            ClusterServer.RequestSpawnMap(spawnerConnectionId, new RequestSpawnMapMessage { channelId = cell.id, mapName = cell.mapId, instanceId = string.Empty }, key,
                (responseCode, response, extraSerializer) =>
                {
                    if (responseCode != AckResponseCode.Success)
                        Logging.LogWarning("Mesh", $"Cell {cell.id}: start request answered {responseCode} ({response.message})");
                }).Forget();
        }
    }
}
