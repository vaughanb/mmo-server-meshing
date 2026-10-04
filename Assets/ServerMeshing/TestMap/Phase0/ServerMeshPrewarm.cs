// Server Meshing test rig — pre-warm and subscription bursts (Phase 0 step 8a,
// MESH_PREWARM=<steered|kinematic|ship|range|group>:<N>@<t>[/<perFrame>]): t seconds after the map server can instantiate
// objects, N puppets arrive at once (or perFrame a frame) and the burst's frames are measured against the frame budget,
// for slice 2b's pre-warm pacing (gap D). Ghost kinds go through the real neighbour path (_Ghosts.cs); ship stand-ins are
// posed as the puppet feeder's are (ServerMeshShipPuppetBody, the space game's record at commit c2eb4154); range changes
// the interest manager's default range; group teleports owned server characters into view. Lines in _Window.cs.
using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using LiteNetLibManager;
using MultiplayerARPG.MMO.Mesh.Sync;
using UnityEngine;
using UnityEngine.Events;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshPrewarm runs one burst per process: it prepares its puppets once the server is ready, logs the Prewarm
    /// setting line, fires the burst at t and logs one Prewarm burst line 2 s after the burst's last action. Ghost puppets
    /// are re-fed at 10 Hz and ship puppets at 20 Hz until the component goes. Teardown per ship: Unregister, End,
    /// NetworkDestroy; group characters are destroyed; ghosts expire on their own. It runs just before the map network
    /// manager (MeshMapNetworkManager inherits MapNetworkManager's order), so after a long burst frame the ghost feeds land
    /// before the mesh's Update expires stale ghosts, as the real link's snapshots do (pumped before the expiry).
    /// </summary>
    [DefaultExecutionOrder(DefaultExecutionOrders.MAP_NETWORK_MANAGER - 1)]
    public sealed partial class ServerMeshPrewarm : MonoBehaviour
    {
        /// <summary>EnvironmentKey is the pre-warm setting.</summary>
        public const string EnvironmentKey = "MESH_PREWARM";
        /// <summary>PrewarmObjectName names the pre-warm's GameObject.</summary>
        public const string PrewarmObjectName = "[Mesh] Prewarm";
        /// <summary>AreaKey is the load generator's area x,z,r; its centre is the burst disc's centre when set.</summary>
        public const string AreaKey = "MESH_LOAD_AREA";
        /// <summary>PrewarmConnectionId is the made-up neighbour connection the ghost snapshots arrive on.</summary>
        public const long PrewarmConnectionId = -9001;
        /// <summary>MaxCount is the most puppets, ships or characters one burst brings.</summary>
        public const int MaxCount = 10000;
        /// <summary>MaxRangeMetres is the highest default visible range a range burst sets.</summary>
        public const float MaxRangeMetres = 10000f;
        /// <summary>PathIndexBase is the first flight path index of ship puppets (the puppet feeder uses 2000..).</summary>
        public const int PathIndexBase = 3000;
        /// <summary>Salt keys the burst points off MESH_LOAD_SEED.</summary>
        public const int Salt = 0x5057;
        /// <summary>ParkSalt keys the group's park points off MESH_LOAD_SEED.</summary>
        public const int ParkSalt = 0x5058;
        /// <summary>DiscRadiusMetres is the burst disc's radius.</summary>
        public const float DiscRadiusMetres = 50f;
        /// <summary>GroupParkRadiusMetres is the radius of the disc a group waits in before its burst.</summary>
        public const float GroupParkRadiusMetres = 10f;
        /// <summary>GroupLeadSeconds is how long before the burst the group's characters are spawned.</summary>
        public const float GroupLeadSeconds = 10f;
        /// <summary>GhostFeedHz is the ghost puppets' snapshot rate (inside the mesh's 2 s ghost expiry).</summary>
        public const int GhostFeedHz = 10;
        /// <summary>ShipFeedHz is the ship puppets' feed rate.</summary>
        public const int ShipFeedHz = 20;

        /// <summary>ServerMeshPrewarmKind is what a burst brings.</summary>
        public enum ServerMeshPrewarmKind
        {
            /// <summary>Steered is N player ghosts under MESH_PUPPET_MOTION steered (or unset).</summary>
            Steered,
            /// <summary>Kinematic is N player ghosts under MESH_PUPPET_MOTION=kinematic.</summary>
            Kinematic,
            /// <summary>Ship is N held ship stand-ins posed by puppet bodies.</summary>
            Ship,
            /// <summary>Range sets the default visible range to N metres.</summary>
            Range,
            /// <summary>Group teleports N owned server characters into the burst disc.</summary>
            Group,
        }

        private sealed class ServerMeshPrewarmPuppet
        {
            public LiteNetLibIdentity identity;
            public ServerMeshShipPuppetBody shipBody;
            public ServerMeshShipFlightPath path;
            public BasePlayerCharacterEntity character;
            public string characterId;
            public string characterName;
            public Vector3 point;
            public Vector3 park;
            public float yaw;
            public bool spawned;
            public bool registerTried;
            public double due;
            public double lastPose;
        }

        private readonly List<ServerMeshPrewarmPuppet> _puppets = new List<ServerMeshPrewarmPuppet>();
        private readonly NetDataWriter _writer = new NetDataWriter(true, 256);
        private readonly NetDataReader _reader = new NetDataReader();
        private ServerMeshPrewarmKind _kind;
        private int _count;
        private float _amount;
        private float _start;
        private int _perFrame;
        private MeshMapNetworkManager _mesh;
        private MeshMovementHandlerSlots _slots;
        private MeshStateSender _sender;
        private ServerMeshShipSettings _shipSettings;
        private int _shipHash;
        private UnityAction<LiteNetLibIdentity> _onDestroy;
        private Rect _cellRect;
        private Vector3 _centre;
        private bool _hasArea;
        private float _areaHalfSize;
        private Vector2 _park;
        private float _eyesRadius;
        private int _seed;
        private double _readyAt;
        private int _next;
        private bool _started, _groupSpawned, _burstBegun, _burstDone, _halted, _retryRegistration, _tearingDown, _quitting;
        private float _nextTry;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoStart()
        {
            string value = Environment.GetEnvironmentVariable(EnvironmentKey);
            if (string.IsNullOrEmpty(value))
                return;
            if (!ConfigReader.IsArgsProvided(Environment.GetCommandLineArgs(), ProcessArguments.ARG_START_MAP_SERVER))
                return;
            if (!TryParseSetting(value, out ServerMeshPrewarmKind kind, out float amount, out float start, out int perFrame, out string error)
                || !CheckPuppetMotion(kind, Environment.GetEnvironmentVariable(MeshPuppetMotionSetting.EnvironmentKey), out error))
            {
                Logging.LogError("Mesh", "Prewarm error: " + error + "; no burst");
                return;
            }
            if (FindFirstObjectByType<ServerMeshPrewarm>() != null)
                return;
            GameObject go = new GameObject(PrewarmObjectName);
            DontDestroyOnLoad(go);
            ServerMeshPrewarm prewarm = go.AddComponent<ServerMeshPrewarm>();
            prewarm._kind = kind;
            prewarm._amount = amount;
            prewarm._count = kind == ServerMeshPrewarmKind.Range ? 1 : (int)amount;
            prewarm._start = start;
            prewarm._perFrame = perFrame;
        }

        private void Update()
        {
            if (!_started)
            {
                if (Time.unscaledTime < _nextTry)
                    return;
                _nextTry = Time.unscaledTime + 1f;
                TryStart();
                return;
            }
            if (_mesh == null || !_mesh.IsServer)
                return;
            long ticks = System.Diagnostics.Stopwatch.GetTimestamp();
            MeasureFrame(ticks);
            double now = Time.unscaledTimeAsDouble;
            if (!_halted && _kind == ServerMeshPrewarmKind.Group && !_groupSpawned && now >= _readyAt + _start - GroupLeadSeconds)
                SpawnGroup();
            RetryRegistration();
            // Feeds before the burst step: a ghost spawned this frame is first re-fed next frame (due at once), and this
            // component runs before the mesh's Update, so that feed lands before the mesh expires stale ghosts.
            if (_kind == ServerMeshPrewarmKind.Ship)
                FeedShips(now);
            else if (_kind == ServerMeshPrewarmKind.Steered || _kind == ServerMeshPrewarmKind.Kinematic)
                FeedGhosts(now);
            if (!_burstDone && now >= _readyAt + _start)
                BurstStep(ticks, now);
        }

        private void TryStart()
        {
            if (!(BaseGameNetworkManager.Singleton is MeshMapNetworkManager mesh) || !mesh.IsServer || mesh.OwnCell == null || !mesh.IsServerReadyToInstantiateObjects())
                return;
            _started = true;
            _mesh = mesh;
            _readyAt = Time.unscaledTimeAsDouble;
            _slots = MeshMovementHandlerSlots.For(mesh);
            _sender = mesh.GetComponent<MeshStateSender>();
            MeshCell cell = mesh.OwnCell;
            _cellRect = Rect.MinMaxRect(cell.owned.minX - cell.frameOrigin.x, cell.owned.minZ - cell.frameOrigin.z, cell.owned.maxX - cell.frameOrigin.x, cell.owned.maxZ - cell.frameOrigin.z);
            Logging.Log("Mesh", SettingLine(_kind, _amount, _start, _perFrame, mesh.OwnZoneId));
            _onDestroy = OnObjectDestroy;
            mesh.Assets.onObjectDestroy.AddListener(_onDestroy);
            if (!Prepare(out string error))
            {
                Logging.LogError("Mesh", $"Prewarm error: cell {Printable(mesh.OwnZoneId)}: {MeshStateSender.CleanForLog(error)}; no burst");
                _halted = true;
                _burstDone = true;
            }
        }

        private bool Prepare(out string error)
        {
            if (!CheckMeshPuppetMotion(_kind, _mesh.puppetMotion, out error))
                return false;
            if (!ServerMeshMonsterPlacer.TryReadSeed(Environment.GetEnvironmentVariable(ServerMeshMonsterPlacer.SeedKey), out _seed, out error))
                return false;
            string area = Environment.GetEnvironmentVariable(AreaKey);
            _hasArea = !string.IsNullOrWhiteSpace(area);
            if (_hasArea)
            {
                if (!TryReadArea(area, out Vector2 areaCentre, out _areaHalfSize))
                {
                    error = $"{AreaKey}={Printable(area.Trim())} is not x,z,r with a positive r";
                    return false;
                }
                _centre = new Vector3(areaCentre.x, ServerMeshShipLoad.GroundY, areaCentre.y);
            }
            else
            {
                _centre = new Vector3(_cellRect.center.x, ServerMeshShipLoad.GroundY, _cellRect.center.y);
            }
            bool disc = _kind == ServerMeshPrewarmKind.Steered || _kind == ServerMeshPrewarmKind.Kinematic || _kind == ServerMeshPrewarmKind.Group;
            if (disc && !BurstDiscInside(_cellRect, new Vector2(_centre.x, _centre.z)))
            {
                error = $"the burst disc (centre {Metres(_centre.x)},{Metres(_centre.z)} r {Metres(DiscRadiusMetres)} m) is not inside the cell"
                    + $" (x {Metres(_cellRect.xMin)} to {Metres(_cellRect.xMax)}, z {Metres(_cellRect.yMin)} to {Metres(_cellRect.yMax)})";
                return false;
            }
            switch (_kind)
            {
                case ServerMeshPrewarmKind.Range:
                    error = _mesh.InterestManager == null ? "the map server has no interest manager" : null;
                    return error == null;
                case ServerMeshPrewarmKind.Ship:
                    return PrepareShips(out error);
                case ServerMeshPrewarmKind.Group:
                    return PrepareGroup(out error);
                default:
                    return PrepareGhosts(out error);
            }
        }

        private bool PrepareShips(out string error)
        {
            _shipSettings = ServerMeshShipLoad.Active;
            if (_shipSettings == null || _shipSettings.Scale == null)
            {
                error = "ship puppets need the ship settings, which no ship installer published";
                return false;
            }
            if (!ServerMeshShipEntity.EnsureRegistered(_mesh, out error))
                return false;
            _shipHash = ServerMeshShipEntity.LoadPrefab(out _).Identity.HashAssetId;
            if (!ServerMeshShipLoad.TryLoadArea(_cellRect, _shipSettings.HasArea, _shipSettings.Area, out Rect area, out error))
                return false;
            for (int i = 0; i < _count; ++i)
            {
                int index = PathIndexBase + i;
                if (!ServerMeshShipFlightPath.TryChooseCentre(_shipSettings.Seed, index, area, ServerMeshShipLoad.GroundY, _shipSettings.Scale, out Vector3 centre, out error))
                {
                    error = $"ship puppet {i}: {error}";
                    return false;
                }
                _puppets.Add(new ServerMeshPrewarmPuppet { path = new ServerMeshShipFlightPath(_shipSettings.Seed, index, centre, _shipSettings.Scale), point = centre });
            }
            error = null;
            return true;
        }

        private bool PrepareGroup(out string error)
        {
            if (!_hasArea)
            {
                error = $"a group needs {AreaKey}: without it the eyes' load players spread over the whole cell, so no park point is out of their view";
                return false;
            }
            _eyesRadius = EyesRadius(_areaHalfSize);
            float range = _mesh.InterestManager != null ? _mesh.InterestManager.defaultVisibleRange : 0f;
            if (!TryChooseParkCentre(_cellRect, new Vector2(_centre.x, _centre.z), _eyesRadius, range, out Vector2 park, out error))
                return false;
            _park = park;
            for (int i = 0; i < _count; ++i)
            {
                _puppets.Add(new ServerMeshPrewarmPuppet
                {
                    characterId = $"prewarm-{_mesh.OwnZoneId}-{i}",
                    characterName = $"Prewarm{_mesh.OwnZoneId}{i:000}",
                    point = ServerMeshMonsterPlacer.DiscPoint(_seed, Salt, 0, i, _centre.x, _centre.z, DiscRadiusMetres),
                    park = ServerMeshMonsterPlacer.DiscPoint(_seed, ParkSalt, 0, i, park.x, park.y, GroupParkRadiusMetres),
                    yaw = YawOf(i),
                });
            }
            return true;
        }

        private void BurstStep(long ticks, double now)
        {
            if (!_burstBegun)
            {
                _burstBegun = true;
                BeginMeasure(ticks);
            }
            int batch = _perFrame > 0 ? _perFrame : int.MaxValue;
            switch (_kind)
            {
                case ServerMeshPrewarmKind.Range:
                    _mesh.InterestManager.defaultVisibleRange = _amount;
                    _next = _count;
                    break;
                case ServerMeshPrewarmKind.Group:
                    for (int done = 0; done < batch && _next < _puppets.Count; ++done, ++_next)
                    {
                        ServerMeshPrewarmPuppet puppet = _puppets[_next];
                        if (puppet.character == null)
                            continue;
                        puppet.character.Teleport(puppet.point, Quaternion.Euler(0f, puppet.yaw, 0f), false);
                        _acted++;
                    }
                    break;
                case ServerMeshPrewarmKind.Ship:
                    for (int done = 0; done < batch && _next < _puppets.Count && !_halted; ++done, ++_next)
                        SpawnShip(_puppets[_next], _next, now);
                    break;
                default:
                    for (int done = 0; done < batch && _next < _puppets.Count; ++done, ++_next)
                        SpawnGhost(_puppets[_next], now);
                    break;
            }
            ActedThisFrame();
            if (_halted || _next >= _count)
            {
                _burstDone = true;
                EndBurst(System.Diagnostics.Stopwatch.GetTimestamp());
            }
        }

        private void SpawnGroup()
        {
            _groupSpawned = true;
            for (int i = 0; i < _puppets.Count; ++i)
            {
                ServerMeshPrewarmPuppet puppet = _puppets[i];
                BasePlayerCharacterEntity entity = ServerMeshShipLoad.SpawnServerPlayer(_mesh, puppet.characterId, puppet.characterName, puppet.park, out string error);
                if (entity == null)
                {
                    Logging.LogError("Mesh", $"Prewarm error: cell {Printable(_mesh.OwnZoneId)}: group character {i}: {MeshStateSender.CleanForLog(error)}; no more group characters spawned");
                    return;
                }
                puppet.character = entity;
                puppet.identity = entity.Identity;
            }
            CheckGroupOutOfView(); // _Window.cs
        }

        private void SpawnShip(ServerMeshPrewarmPuppet puppet, int i, double now)
        {
            puppet.path.Evaluate(_mesh.ServerTimestamp / 1000.0, out Vector3 position, out Quaternion rotation, out _, out _);
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            LiteNetLibIdentity identity = _mesh.Assets.GetObjectInstance(_shipHash, position, rotation);
            ServerMeshShipEntity ship = identity == null ? null : identity.GetComponent<ServerMeshShipEntity>();
            if (ship == null)
            {
                if (identity != null)
                    Destroy(identity.gameObject);
                Logging.LogError("Mesh", $"Prewarm error: cell {Printable(_mesh.OwnZoneId)}: no ship instance for prefab hash {_shipHash}; no more puppets spawned");
                _halted = true;
                return;
            }
            ship.Configure(ServerMeshShipAuthority.OwnerMirror, i % 2, PathIndexBase + i, puppet.point, _shipSettings.Scale, _shipSettings, ServerMeshShipClass.Base);
            _mesh.Assets.NetworkSpawn(identity);
            ship.ShipMovement.PuppetHeld = true;
            ServerMeshShipPuppetBody body = new ServerMeshShipPuppetBody(identity.ObjectId, identity, ship.transform, _shipSettings.Wire, _shipSettings.Scale);
            if (!body.Begin(_slots, out string error))
            {
                _spawnTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
                Logging.LogError("Mesh", $"Prewarm error: cell {Printable(_mesh.OwnZoneId)}: {MeshStateSender.CleanForLog(error)}; that puppet is destroyed");
                identity.NetworkDestroy();
                return;
            }
            puppet.identity = identity;
            puppet.shipBody = body;
            if (_sender != null && _sender.IsSending)
            {
                puppet.registerTried = true;
                body.SentBySender = _sender.Register(body);
            }
            else if (_sender != null)
            {
                _retryRegistration = true;
            }
            _spawnTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
            _acted++;
            puppet.spawned = true;
            puppet.lastPose = now;
            puppet.due = now + (1.0 + (double)i / Math.Max(1, _count)) / ShipFeedHz;
        }

        private void RetryRegistration()
        {
            if (!_retryRegistration || _sender == null || !_sender.IsSending)
                return;
            _retryRegistration = false;
            for (int i = 0; i < _puppets.Count; ++i)
            {
                ServerMeshPrewarmPuppet puppet = _puppets[i];
                if (puppet.identity == null || puppet.shipBody == null || puppet.registerTried)
                    continue;
                puppet.registerTried = true;
                puppet.shipBody.SentBySender = _sender.Register(puppet.shipBody);
            }
        }

        private void FeedShips(double now)
        {
            long timestamp = _mesh.ServerTimestamp;
            const double period = 1.0 / ShipFeedHz;
            for (int i = 0; i < _puppets.Count; ++i)
            {
                ServerMeshPrewarmPuppet puppet = _puppets[i];
                if (puppet.identity == null || puppet.shipBody == null || now < puppet.due)
                    continue;
                puppet.due = now - puppet.due > period ? now + period : puppet.due + period;
                puppet.path.Evaluate(timestamp / 1000.0, out Vector3 position, out Quaternion rotation, out Vector3 velocity, out Vector3 angular);
                ServerMeshShipSample sample = new ServerMeshShipSample { Position = position, Rotation = rotation, Velocity = velocity, AngularVelocity = angular, Forward = true };
                _writer.Reset();
                ServerMeshShipWire.WriteHeader56(_writer, in sample);
                _reader.SetSource(_writer);
                ServerMeshShipWire.ReadHeader56(_reader, out ServerMeshShipSample decoded);
                puppet.shipBody.Accept(in decoded, now);
                if (puppet.identity.CountSubscribers() > 0)
                    puppet.shipBody.Tick((float)(now - puppet.lastPose));
                else
                    puppet.shipBody.ApplyNow();
                puppet.lastPose = now;
            }
        }

        private void OnObjectDestroy(LiteNetLibIdentity identity)
        {
            if (_tearingDown || identity == null)
                return;
            for (int i = 0; i < _puppets.Count; ++i)
            {
                ServerMeshPrewarmPuppet puppet = _puppets[i];
                if (puppet.identity != identity)
                    continue;
                if (puppet.shipBody != null)
                    _slots.Forget(identity.ObjectId);
                puppet.identity = null;
                puppet.character = null;
                return;
            }
        }

        private static float YawOf(int i) => i * 137.508f % 360f;

        private void OnApplicationQuit() => _quitting = true;

        private void OnDestroy()
        {
            if (_mesh != null && _mesh.Assets != null && _onDestroy != null)
                _mesh.Assets.onObjectDestroy.RemoveListener(_onDestroy);
            if (_quitting || _mesh == null || !_mesh.IsServer || _slots == null)
                return; // at quit or Play stop the kit destroys every object in its own pass
            _tearingDown = true;
            for (int i = 0; i < _puppets.Count; ++i)
            {
                ServerMeshPrewarmPuppet puppet = _puppets[i];
                if (puppet.identity == null || (puppet.shipBody == null && puppet.character == null))
                    continue; // ghosts expire on their own
                if (puppet.shipBody != null)
                {
                    if (_sender != null)
                        _sender.Unregister(puppet.shipBody);
                    puppet.shipBody.End(_slots);
                }
                if (!puppet.identity.IsDestroyed)
                    puppet.identity.NetworkDestroy();
            }
            _puppets.Clear();
        }
    }
}
