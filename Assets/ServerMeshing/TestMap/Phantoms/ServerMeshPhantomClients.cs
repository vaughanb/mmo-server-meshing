// Server Meshing add-on — rig phantom binder: makes MESH_PHANTOM's phantoms real, ready kit players, each owning one character, eye or ship, on the cells it names.
using System.Collections.Generic;
using LiteNetLib.Utils;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshPhantomClients is the binder the installer creates. Once the map server serves a cell MESH_PHANTOM names and
    /// can spawn, it lays out the phantoms (ServerMeshPhantomLayout), spawns the server-owned filler, and then, paced at
    /// MESH_PHANTOM_ARRIVAL_PER_S: virtual mode queues a connect, makes the kit player ready and spawns the phantom's one
    /// owned object (a character fed by ServerMeshPhantomUploads, or a hidden eye); sink mode binds each hello's index to its
    /// real connection and replies with the bind data. It logs the "Phantoms:" and "Phantom group" lines, steps, and a
    /// "Phantom window" line with every Work window. Nothing is registered. Everything resets when the server stops.
    /// </summary>
    public partial class ServerMeshPhantomClients : MonoBehaviour
    {
        /// <summary>HostRefreshSeconds is how often host mode re-reads the cell's movers when it needs a host.</summary>
        public const float HostRefreshSeconds = 1f;
        /// <summary>HealSeconds is how often every phantom character and filler is healed when low.</summary>
        public const float HealSeconds = 1f;
        /// <summary>StandSalt and FightSalt pick the standing and fighting phantoms from the seed.</summary>
        public const int StandSalt = 1, FightSalt = 2, PhaseSalt = 3, HostSalt = 4;

        private ServerMeshPhantomSettings _settings;
        private IMeshLog _log;
        private ServerMeshPhantomUploads _uploads;
        private ServerMeshPhantomFighter _fighter;
        private ServerMeshPhantomViewSampler _viewSampler;
        private int _viewRound;
        private readonly ServerMeshPhantomWindow _window = new ServerMeshPhantomWindow();
        private readonly List<ServerMeshPhantomSlot> _slots = new List<ServerMeshPhantomSlot>();
        private readonly List<BasePlayerCharacterEntity> _filler = new List<BasePlayerCharacterEntity>();
        private readonly List<int> _fillerGroups = new List<int>();
        private readonly List<Vector2> _fillerPending = new List<Vector2>();
        private readonly List<int> _fillerPendingGroups = new List<int>();
        private readonly List<LiteNetLibIdentity> _hosts = new List<LiteNetLibIdentity>();
        private readonly Queue<long> _helloConnections = new Queue<long>();
        private readonly Queue<int> _helloIndices = new Queue<int>();
        private readonly HashSet<long> _sinkConnections = new HashSet<long>();
        private readonly long[] _swallowedCallsBase = new long[ServerMeshPhantomTransport.DeliverySlots];
        private readonly long[] _swallowedBytesBase = new long[ServerMeshPhantomTransport.DeliverySlots];
        private readonly NetDataWriter _replyWriter = new NetDataWriter(true, 64);
        private ServerMeshKinematicWalk[] _walks;
        private ServerMeshPhantomGroup[] _groups;

        private MeshMapNetworkManager _mesh;
        private ServerMeshPhantomTransport _transport;
        private bool _started, _decided, _serving, _planned;
        private bool _kitSpawned;
        private string _cell = string.Empty;
        private int _classId, _entityId;
        private int _fillerNext;
        private int _fillerTotal;
        private ServerMeshLoadMotionKind _fillerMotion;
        private bool _fillerFight;
        private double _nextBind;
        private double _nextHostRefresh;
        private double _nextHeal;
        private int _stepIndex = -1;
        private double _stepReachedAt = -1;
        private int _target;
        private int _lastWorkWindow = -1;
        private double _windowStart;
        private int _windowFrames;
        private long _binderTicks;
        private int _sampleRound;
        private bool _warnedHelloRange, _warnedHelloLive, _warnedRate, _warnedNoHost, _warnedLimit, _halted;

        /// <summary>Setup gives the binder its settings and the components it drives; called once by the installer.</summary>
        public void Setup(ServerMeshPhantomSettings settings, IMeshLog log)
        {
            _settings = settings ?? throw new System.ArgumentNullException(nameof(settings));
            _log = log ?? throw new System.ArgumentNullException(nameof(log));
            _uploads = gameObject.AddComponent<ServerMeshPhantomUploads>();
            _fighter = gameObject.AddComponent<ServerMeshPhantomFighter>();
            _window.ShowFighting = settings.FightSet;
        }

        private void Update()
        {
            if (_settings == null)
                return;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            Tick();
            _binderTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
        }

        private void Tick()
        {
            MeshMapNetworkManager mesh = BaseGameNetworkManager.Singleton as MeshMapNetworkManager;
            if (mesh == null || !mesh.IsServer)
            {
                if (_started)
                    ResetAll();
                return;
            }
            if (!_started)
            {
                _started = true;
                _mesh = mesh;
                // The kit registers and spawns its scene entities after IsServerReadyToInstantiateObjects turns true;
                // a spawn in that frame finds no player prefab instance, so binding waits for the kit's finish event.
                mesh.onSpawnEntitiesFinish.AddListener(OnKitSpawnedEntities);
            }
            if (!_decided)
            {
                if (mesh.OwnCell == null)
                    return;
                _decided = true;
                _cell = mesh.OwnZoneId;
                _serving = _settings.ServesCell(_cell);
                if (_serving && mesh.OwnCell.OnDynamicMap)
                {
                    _serving = false;
                    _log.Warning($"Phantoms: cell {_cell} is on a Dynamic map; phantoms count as owned players and are never handed off, so none are bound");
                }
            }
            if (!_serving)
                return;
            if (!_planned)
            {
                if (!_kitSpawned || !mesh.IsServerReadyToInstantiateObjects())
                    return;
                _transport = MeshTransportUnwrap.Innermost<ServerMeshPhantomTransport>(mesh.ServerTransport);
                if (_transport == null)
                {
                    _serving = false;
                    _log.Error($"Phantoms error: cell {_cell}: the server transport chain has no phantom transport (useWebSocket replaces the factory); none bound");
                    return;
                }
                if (!Plan(mesh))
                {
                    _serving = false;
                    return;
                }
                _planned = true;
            }
            double now = Time.unscaledTimeAsDouble;
            ++_windowFrames;
            Maintain(mesh, now);
            Steps(now);
            Pace(mesh, now);
            PollConnecting(mesh, now);
            if (now >= _nextHeal)
            {
                _nextHeal = now + HealSeconds;
                HealAll();
            }
            WindowIfDue(mesh, now);
        }

        private bool Plan(MeshMapNetworkManager mesh)
        {
            foreach (int id in GameInstance.PlayerCharacters.Keys) { _classId = id; break; }
            foreach (int id in GameInstance.PlayerCharacterEntities.Keys) { _entityId = id; break; }
            if (_settings.Kind == ServerMeshPhantomKind.Character || _settings.View > 0)
            {
                if (_classId == 0 || _entityId == 0)
                {
                    _log.Error($"Phantoms error: cell {_cell}: no player class or entity in the game database; none bound");
                    return false;
                }
            }
            MeshCell cell = mesh.OwnCell;
            Rect scene = Rect.MinMaxRect(cell.owned.minX - cell.frameOrigin.x, cell.owned.minZ - cell.frameOrigin.z, cell.owned.maxX - cell.frameOrigin.x, cell.owned.maxZ - cell.frameOrigin.z);
            float range = mesh.InterestManager != null ? mesh.InterestManager.defaultVisibleRange : 0f;
            BandEdges(cell, out bool west, out bool east, out bool south, out bool north);
            int phantoms = _settings.Phantoms;
            bool eyes = _settings.Kind == ServerMeshPhantomKind.Eye;
            bool ships = _settings.Kind == ServerMeshPhantomKind.Ship;
            Vector2[] positions = new Vector2[phantoms];
            Rect[] regions = new Rect[phantoms];
            int[] groupOf = new int[phantoms];
            if (ships)
            {
                if (!PlanShips(scene, positions, regions))
                    return false;
            }
            else if (_settings.HasArea)
            {
                Rect region = _settings.AreaRegion;
                if (region.xMin < scene.xMin || region.xMax > scene.xMax || region.yMin < scene.yMin || region.yMax > scene.yMax)
                {
                    _log.Error(System.FormattableString.Invariant(
                        $"Phantoms error: cell {_cell}: {ServerMeshPhantomSettings.ENV_AREA} walks x {region.xMin:0.0} {region.xMax:0.0} z {region.yMin:0.0} {region.yMax:0.0}, outside the cell's x {scene.xMin:0.0} {scene.xMax:0.0} z {scene.yMin:0.0} {scene.yMax:0.0}; none bound"));
                    return false;
                }
                positions = ServerMeshPhantomLayout.PlaceInArea(phantoms, _settings.AreaIsDisc, _settings.AreaCentre, _settings.AreaSize, _settings.AreaRadius, _settings.Seed);
                for (int k = 0; k < phantoms; ++k)
                    regions[k] = region;
                _groups = new ServerMeshPhantomGroup[0];
            }
            else
            {
                Vector2[] portals = WarpPortalPoints();
                _groups = ServerMeshPhantomLayout.Build(phantoms, _settings.View, range, scene, mesh.ghostBandWidth, west, east, south, north, _settings.Seed, eyes, portals, PortalKeepOut, out string error);
                if (_groups == null)
                {
                    _log.Error($"Phantoms error: cell {_cell}: {error}; none bound");
                    return false;
                }
                int k = 0;
                for (int g = 0; g < _groups.Length; ++g)
                {
                    ServerMeshPhantomGroup group = _groups[g];
                    for (int m = 0; m < group.PhantomCount && k < phantoms; ++m, ++k)
                    {
                        positions[k] = group.PhantomPositions[m];
                        regions[k] = group.Region;
                        groupOf[k] = g;
                    }
                    for (int f = 0; f < group.FillerCount; ++f)
                    {
                        _fillerPending.Add(group.FillerPositions[f]);
                        _fillerPendingGroups.Add(g);
                    }
                }
            }
            _fillerTotal = _fillerPending.Count;
            PrepareFillerMotion();
            for (int k = 0; k < phantoms; ++k)
            {
                ServerMeshPhantomSlot slot = new ServerMeshPhantomSlot
                {
                    index = k,
                    spawn = positions[k],
                    region = regions[k],
                    group = groupOf[k],
                    input = _settings.Payload == ServerMeshPhantomPayloadKind.Input,
                    standing = !eyes && ServerMeshPhantomUploads.InShare(_settings.Seed, k, StandSalt, _settings.StandShare),
                    fighting = !eyes && ServerMeshPhantomUploads.InShare(_settings.Seed, k, FightSalt, _settings.FightShare),
                };
                _slots.Add(slot);
            }
            _target = _settings.Steps != null ? 0 : phantoms;
            _uploads.Slots = _slots;
            _uploads.Transport = _transport;
            _uploads.Mesh = mesh;
            _uploads.Window = _window;
            _uploads.Log = _log;
            _uploads.Period = 1.0 / _settings.UploadHz;
            _uploads.Payload = _settings.Payload == ServerMeshPhantomPayloadKind.Input ? (IServerMeshPhantomPayload)ServerMeshPhantomInputPayload.Instance : ServerMeshPhantomTransformPayload.Instance;
            _uploads.ShipPayload = ships ? new ServerMeshPhantomShipPayload(_settings.Wire) : null;
            if (_settings.Route != ServerMeshPhantomRoute.Sink)
            {
                _viewSampler = new ServerMeshPhantomViewSampler(phantoms, _settings.Seed);
                _transport.ViewSampler = _viewSampler;
                _uploads.ViewSampler = _viewSampler;
                _window.ShowViewErr = true;
            }
            _fighter.Begin(mesh, _window, _log);
            _nextBind = Time.unscaledTimeAsDouble;
            int view = _settings.View < 0 ? 0 : _settings.View;
            _log.Info(System.FormattableString.Invariant(
                $"Phantoms: cell {_cell} port {mesh.networkPort} mode {ServerMeshPhantomSettings.RouteName(_settings.Route)} kind {ServerMeshPhantomSettings.KindName(_settings.Kind)} P {phantoms} V {view} groups {_groups.Length}"));
            for (int g = 0; g < _groups.Length; ++g)
                _log.Info(_groups[g].Line());
            if (_settings.HasArea)
            {
                Rect region = _settings.AreaRegion;
                _log.Info(System.FormattableString.Invariant(
                    $"Phantom area: centre {region.center.x:0.0} {region.center.y:0.0} side {region.width:0.0} {region.height:0.0} phantoms {phantoms} {(_settings.AreaIsDisc ? "disc" : "rect")}"));
            }
            return true;
        }

        private static void BandEdges(MeshCell cell, out bool west, out bool east, out bool south, out bool north)
        {
            const float touch = 0.01f;
            west = east = south = north = false;
            MeshRect own = cell.owned;
            for (int i = 0; i < cell.bandNeighbours.Count; ++i)
            {
                MeshRect other = cell.bandNeighbours[i].owned;
                if (other.IsEmpty)
                    continue;
                bool overlapZ = other.minZ <= own.maxZ + touch && other.maxZ >= own.minZ - touch;
                bool overlapX = other.minX <= own.maxX + touch && other.maxX >= own.minX - touch;
                if (overlapZ && Mathf.Abs(other.minX - own.maxX) <= touch)
                    east = true;
                if (overlapZ && Mathf.Abs(other.maxX - own.minX) <= touch)
                    west = true;
                if (overlapX && Mathf.Abs(other.minZ - own.maxZ) <= touch)
                    north = true;
                if (overlapX && Mathf.Abs(other.maxZ - own.minZ) <= touch)
                    south = true;
            }
        }

        private void PrepareFillerMotion()
        {
            if (_fillerTotal == 0)
                return;
            _fillerMotion = ServerMeshLoadMotion.ReadMotion();
            _fillerFight = ServerMeshLoadMotion.ReadFight();
            _walks = new ServerMeshKinematicWalk[_groups.Length];
            if (_fillerMotion == ServerMeshLoadMotionKind.Steered)
                return;
            for (int g = 0; g < _groups.Length; ++g)
            {
                if (_groups[g].FillerCount == 0)
                    continue;
                _walks[g] = gameObject.AddComponent<ServerMeshKinematicWalk>();
                _walks[g].Setup(_groups[g].Region, false);
            }
        }

        private void Steps(double now)
        {
            int[] steps = _settings.Steps;
            if (steps == null)
                return;
            int bound = CountVirtualBound();
            if (_stepIndex >= 0 && _stepReachedAt < 0 && bound == steps[_stepIndex])
                _stepReachedAt = now;
            float since = _stepReachedAt >= 0 ? (float)(now - _stepReachedAt) : -1f;
            int index = ServerMeshPhantomSettings.StepTarget(steps, _settings.StepHoldSeconds, _stepIndex, since, bound, out int target);
            if (index != _stepIndex)
            {
                _log.Info(System.FormattableString.Invariant($"Phantom step {index + 1}/{steps.Length}: P {_target} -> {target}"));
                _stepIndex = index;
                _stepReachedAt = -1;
                _target = target;
            }
            for (int k = _slots.Count - 1; k >= 0 && CountVirtualActive() > _target; --k)
            {
                ServerMeshPhantomSlot slot = _slots[k];
                if (slot.connectionId >= 0 && !slot.sink)
                    Unbind(slot);
            }
        }

        private void Pace(MeshMapNetworkManager mesh, double now)
        {
            if (_halted)
                return;
            double interval = 1.0 / _settings.ArrivalPerSecond;
            if (_nextBind < now - interval)
                _nextBind = now - interval;
            while (_nextBind <= now && BindOne(mesh, now))
                _nextBind += interval;
        }

        private bool BindOne(MeshMapNetworkManager mesh, double now)
        {
            if (_fillerPending.Count > 0)
            {
                SpawnFiller(mesh);
                return true;
            }
            if (_settings.Route == ServerMeshPhantomRoute.Sink)
                return BindHello(mesh, now);
            if (CountVirtualActive() >= _target)
                return false;
            for (int k = 0; k < _slots.Count && k < _target; ++k)
            {
                ServerMeshPhantomSlot slot = _slots[k];
                if (slot.connectionId >= 0)
                    continue;
                if (!_transport.TryQueueConnect(out long id))
                {
                    if (!_warnedLimit)
                    {
                        _warnedLimit = true;
                        _log.Warning(System.FormattableString.Invariant($"Phantoms: cell {_cell}: the server's connection limit {_transport.ServerMaxConnections} stops phantom binds at {CountVirtualActive()} phantoms and {_transport.Inner.ServerPeersCount} real peers"));
                    }
                    return false;
                }
                slot.connectionId = id;
                slot.connecting = true;
                return true;
            }
            return false;
        }

        private void PollConnecting(MeshMapNetworkManager mesh, double now)
        {
            if (_settings.Route == ServerMeshPhantomRoute.Sink)
                return;
            for (int k = 0; k < _slots.Count; ++k)
            {
                ServerMeshPhantomSlot slot = _slots[k];
                if (!slot.connecting || !mesh.TryGetPlayer(slot.connectionId, out LiteNetLibPlayer player))
                    continue;
                if (!TrySpawn(mesh, slot, player, now))
                {
                    if (_halted)
                        Unbind(slot);
                    continue;
                }
                slot.connecting = false;
            }
        }

        private void Maintain(MeshMapNetworkManager mesh, double now)
        {
            for (int k = 0; k < _slots.Count; ++k)
            {
                ServerMeshPhantomSlot slot = _slots[k];
                if (!slot.bound)
                    continue;
                if (!IsAlive(mesh, slot))
                {
                    if (slot.sink)
                        Release(slot);
                    else
                        Unbind(slot);
                    continue;
                }
                if (slot.eye != null && (slot.eye.Host == null || !slot.eye.Host.gameObject.activeInHierarchy))
                {
                    Transform host = PickHost(mesh, slot, now);
                    if (host != null)
                        slot.eye.Follow(host);
                }
            }
        }

        private static bool IsAlive(MeshMapNetworkManager mesh, ServerMeshPhantomSlot slot)
        {
            if (!mesh.TryGetPlayer(slot.connectionId, out _))
                return false;
            if (slot.connecting)
                return true;
            return slot.identity != null && !slot.identity.IsDestroyed;
        }

        private void Unbind(ServerMeshPhantomSlot slot)
        {
            if (slot.entity != null && !slot.entity.IsDestroyed && slot.input)
                SetSecure(slot.entity.Identity, MovementSecure.NotSecure);
            if (slot.connectionId >= 0)
                _transport.QueueDisconnect(slot.connectionId);
            Release(slot);
        }

        private void Release(ServerMeshPhantomSlot slot)
        {
            if (_viewSampler != null && slot.connectionId >= 0)
                _viewSampler.Untrack(slot.connectionId);
            if (slot.entity != null)
                _fighter.Remove(slot.entity);
            _sinkConnections.Remove(slot.connectionId);
            ReleaseShip(slot);
            slot.Release();
        }

        private void HealAll()
        {
            for (int k = 0; k < _slots.Count; ++k)
            {
                BasePlayerCharacterEntity entity = _slots[k].entity;
                if (_slots[k].bound && entity != null && !entity.IsDestroyed)
                    SyntheticPlayerBrain.HealWhenLow(entity);
            }
        }

        private void Halt()
        {
            if (_halted)
                return;
            _halted = true;
            _log.Error($"Phantoms error: cell {_cell}: a spawn failed (logged above); no further phantoms or filler are bound");
        }

        /// <summary>PortalKeepOut is how far phantom groups stay from a warp portal: its trigger plus a margin.</summary>
        public const float PortalKeepOut = 10f;

        private static Vector2[] WarpPortalPoints()
        {
            WarpPortalEntity[] portals = FindObjectsByType<WarpPortalEntity>(FindObjectsSortMode.None);
            Vector2[] points = new Vector2[portals.Length];
            for (int i = 0; i < portals.Length; ++i)
            {
                Vector3 p = portals[i].EntityTransform.position;
                points[i] = new Vector2(p.x, p.z);
            }
            return points;
        }

        private void OnKitSpawnedEntities(string sceneName, bool isOnline, bool isAdditive, float progress)
        {
            _kitSpawned = true;
        }

        private void ResetAll()
        {
            if (_mesh != null)
                _mesh.onSpawnEntitiesFinish.RemoveListener(OnKitSpawnedEntities);
            _started = _decided = _serving = _planned = _halted = _kitSpawned = false;
            _mesh = null;
            if (_transport != null)
                _transport.ViewSampler = null;
            _transport = null;
            _viewSampler = null;
            _viewRound = 0;
            _window.ShowViewErr = false;
            ReleaseShips();
            _slots.Clear();
            _filler.Clear();
            _fillerGroups.Clear();
            _fillerPending.Clear();
            _fillerPendingGroups.Clear();
            _hosts.Clear();
            _helloConnections.Clear();
            _helloIndices.Clear();
            _sinkConnections.Clear();
            if (_walks != null)
            {
                for (int g = 0; g < _walks.Length; ++g)
                {
                    if (_walks[g] != null)
                        Destroy(_walks[g]);
                }
            }
            _walks = null;
            _groups = null;
            _stepIndex = -1;
            _stepReachedAt = -1;
            _target = 0;
            _lastWorkWindow = -1;
            _uploads.Clear();
            _fighter.Clear();
            _window.Reset();
            _warnedHelloRange = _warnedHelloLive = _warnedNoHost = _warnedLimit = false;
        }
    }
}
