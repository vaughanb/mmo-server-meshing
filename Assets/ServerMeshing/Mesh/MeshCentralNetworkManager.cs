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
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using LiteNetLib;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public class MeshCentralNetworkManager : CentralNetworkManager
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
            if (sleepCells && world != null && ClusterServer != null)
            {
                ClusterServer.RegisterRequestHandler<MeshWakeRequest, MeshWakeResponse>(MeshClusterMsg.Wake, HandleWakeRequest);
                Logging.Log("Mesh", $"Sleeping cells ON: awake rings {awakeRings}, sleep after {sleepAfterSeconds:0} s; cells start on demand");
            }
        }

        protected override void Update()
        {
            base.Update();
            if (!sleepCells || world == null || ClusterServer == null || Time.unscaledTime < _nextTick)
                return;
            _nextTick = Time.unscaledTime + awakeTickSeconds;
            AwakeTick();
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

        private UniTaskVoid HandleWakeRequest(RequestHandlerData requestHandler, MeshWakeRequest request, RequestProceedResultDelegate<MeshWakeResponse> result)
        {
            MeshCell cell = world != null ? world.GetCell(request.mapId, request.cellId) ?? world.GetCellById(request.cellId) : null;
            if (cell != null)
            {
                _wakeHolds[cell.id] = Time.unscaledTime + wakeHoldSeconds;
                _nextTick = 0f;
                Logging.Log("Mesh", $"Wake requested for cell {cell.id} ({cell.mapId}) by connection {requestHandler.ConnectionId}");
            }
            result.Invoke(AckResponseCode.Success, new MeshWakeResponse { accepted = cell != null });
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

        private void RequestStart(MeshCell cell, float now)
        {
            long spawnerConnectionId = -1;
            foreach (CentralServerPeerInfo spawner in ClusterServer.MapSpawnServerPeers.Values)
            {
                spawnerConnectionId = spawner.connectionId;
                break;
            }
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
