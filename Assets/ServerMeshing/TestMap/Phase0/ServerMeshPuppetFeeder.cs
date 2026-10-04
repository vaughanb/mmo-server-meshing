// Server Meshing test rig — in-process puppets fed at a fixed rate (Phase 0 step 7, MESH_PUPPET_FEEDER=<ship|character>:<N>@<Hz>):
// N untracked puppets whose snapshots are encoded, decoded, accepted and posed on this server, costing the receive side
// of a neighbour's snapshot stream (the per-snapshot cost) without a neighbour. Ship puppets use the space game's record
// (ServerMeshShipWire, modelled on ShipMovementComponent at commit 1a3a58d9) and ServerMeshShipPuppetBody; character
// puppets use the mesh's own GhostSnapshotMessage and MeshKinematicCharacterBody, as the ghost link does.
using System;
using System.Collections.Generic;
using System.Globalization;
using LiteNetLib.Utils;
using LiteNetLibManager;
using MultiplayerARPG.MMO.Mesh.Sync;
using UnityEngine;
using UnityEngine.Events;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshPuppetFeeder spawns its puppets once the map server can instantiate objects, registers each body with the
    /// state sender (polling IsSending, then one Register), feeds every puppet at its rate (encode, decode, Accept, then
    /// Tick when observed or ApplyNow when not) and logs a Feeder window line with every Work window. Teardown per puppet:
    /// Unregister, End, NetworkDestroy. Only the ship path is allocation-free; the character path allocates exactly as the
    /// real link does (GhostSnapshotMessage.Deserialize reads two strings).
    /// </summary>
    public sealed partial class ServerMeshPuppetFeeder : MonoBehaviour
    {
        /// <summary>EnvironmentKey is the feeder's setting.</summary>
        public const string EnvironmentKey = "MESH_PUPPET_FEEDER";
        /// <summary>FeederObjectName names the feeder's GameObject.</summary>
        public const string FeederObjectName = "[Mesh] PuppetFeeder";
        /// <summary>MaxCount is the most puppets one feeder spawns.</summary>
        public const int MaxCount = 2000;
        /// <summary>MaxHz is the highest feed rate.</summary>
        public const int MaxHz = 60;
        /// <summary>PathIndexBase is the first flight path index of ship puppets (load mirrors use 0.., phantom ships 1000..).</summary>
        public const int PathIndexBase = 2000;
        /// <summary>CharacterSalt keys character puppet centres off MESH_LOAD_SEED.</summary>
        public const int CharacterSalt = 0x5346;
        /// <summary>CharacterOrbitMetres is the radius character puppets walk around their centre.</summary>
        public const float CharacterOrbitMetres = 10f;
        /// <summary>CharacterOrbitRate is the character puppets' angular rate in radians per second (3 m/s at 10 m).</summary>
        public const float CharacterOrbitRate = 0.3f;

        private readonly List<ServerMeshFeederPuppet> _puppets = new List<ServerMeshFeederPuppet>();
        private readonly NetDataWriter _writer = new NetDataWriter(true, 256);
        private readonly NetDataReader _reader = new NetDataReader();
        private GhostSnapshotMessage _outgoing;
        private GhostSnapshotMessage _incoming;
        private bool _ships;
        private int _count;
        private int _hz;
        private MeshMapNetworkManager _mesh;
        private MeshMovementHandlerSlots _slots;
        private MeshStateSender _sender;
        private UnityAction<LiteNetLibIdentity> _onDestroy;
        private bool _retryRegistration;
        private bool _started;
        private bool _tearingDown;
        private bool _quitting;
        private float _nextTry;
        private int _lastWorkWindow = -1;
        private double _windowStart;
        private int _gcBase;
        private long _snapshots, _encodeTicks, _decodeTicks, _acceptTicks, _poseTicks, _totalTicks;

        private sealed class ServerMeshFeederPuppet
        {
            public LiteNetLibIdentity identity;
            public ServerMeshShipPuppetBody shipBody;
            public ServerMeshShipFlightPath path;
            public BasePlayerCharacterEntity character;
            public MeshKinematicCharacterBody characterBody;
            public string characterId;
            public string characterName;
            public Vector3 centre;
            public double due;
            public double lastPose;

            public IMeshStateSource Source => shipBody != null ? (IMeshStateSource)shipBody : characterBody;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoStart()
        {
            string value = Environment.GetEnvironmentVariable(EnvironmentKey);
            if (string.IsNullOrEmpty(value))
                return;
            if (!ConfigReader.IsArgsProvided(Environment.GetCommandLineArgs(), ProcessArguments.ARG_START_MAP_SERVER))
                return;
            if (!TryParseSetting(value, out bool ships, out int count, out int hz, out string error))
            {
                Logging.LogError("Mesh", "Feeder error: " + error + "; no puppets fed");
                return;
            }
            if (FindFirstObjectByType<ServerMeshPuppetFeeder>() != null)
                return;
            GameObject go = new GameObject(FeederObjectName);
            DontDestroyOnLoad(go);
            ServerMeshPuppetFeeder feeder = go.AddComponent<ServerMeshPuppetFeeder>();
            feeder._ships = ships;
            feeder._count = count;
            feeder._hz = hz;
        }

        /// <summary>
        /// TryParseSetting reads <c>&lt;ship|character&gt;:&lt;N&gt;@&lt;Hz&gt;</c> (trimmed, the kind ignoring case), N 1..2000
        /// and Hz 1..60; false with an error naming the key and the bad part otherwise.
        /// </summary>
        public static bool TryParseSetting(string text, out bool ships, out int count, out int hz, out string error)
        {
            ships = false;
            count = hz = 0;
            string value = text == null ? string.Empty : text.Trim();
            int colon = value.IndexOf(':');
            int at = value.IndexOf('@');
            if (colon <= 0 || at <= colon + 1 || at == value.Length - 1 || value.IndexOf(':', colon + 1) >= 0 || value.IndexOf('@', at + 1) >= 0)
            {
                error = $"{EnvironmentKey}={Printable(value)} is not <ship|character>:<N>@<Hz>";
                return false;
            }
            string kind = value.Substring(0, colon).Trim();
            if (string.Equals(kind, "ship", StringComparison.OrdinalIgnoreCase))
                ships = true;
            else if (!string.Equals(kind, "character", StringComparison.OrdinalIgnoreCase))
            {
                error = $"{EnvironmentKey}={Printable(value)}: the kind {Printable(kind)} is not ship or character";
                return false;
            }
            if (!int.TryParse(value.Substring(colon + 1, at - colon - 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out count) || count < 1 || count > MaxCount)
            {
                ships = false;
                count = 0;
                error = $"{EnvironmentKey}={Printable(value)}: the count must be a whole number from 1 to {MaxCount}";
                return false;
            }
            if (!int.TryParse(value.Substring(at + 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out hz) || hz < 1 || hz > MaxHz)
            {
                ships = false;
                count = hz = 0;
                error = $"{EnvironmentKey}={Printable(value)}: the rate must be a whole number of Hz from 1 to {MaxHz}";
                return false;
            }
            error = null;
            return true;
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
            RetryRegistration();
            Feed();
            WindowIfDue();
        }

        private void TryStart()
        {
            if (!(BaseGameNetworkManager.Singleton is MeshMapNetworkManager mesh) || !mesh.IsServer || mesh.OwnCell == null || !mesh.IsServerReadyToInstantiateObjects())
                return;
            _started = true;
            _mesh = mesh;
            _slots = MeshMovementHandlerSlots.For(mesh);
            _sender = mesh.GetComponent<MeshStateSender>();
            _retryRegistration = _sender != null;
            MeshCell cell = mesh.OwnCell;
            Rect cellRect = Rect.MinMaxRect(cell.owned.minX - cell.frameOrigin.x, cell.owned.minZ - cell.frameOrigin.z, cell.owned.maxX - cell.frameOrigin.x, cell.owned.maxZ - cell.frameOrigin.z);
            Logging.Log("Mesh", SettingLine(_ships, _count, _hz, mesh.OwnZoneId));
            _onDestroy = OnObjectDestroy;
            mesh.Assets.onObjectDestroy.AddListener(_onDestroy);
            if (_ships)
                SpawnShips(cellRect);
            else
                SpawnCharacters(cellRect);
            double now = Time.unscaledTimeAsDouble;
            for (int i = 0; i < _puppets.Count; ++i)
            {
                _puppets[i].due = now + (double)i / _puppets.Count / _hz;
                _puppets[i].lastPose = now;
            }
            StartWindow();
        }

        private void SpawnShips(Rect cellRect)
        {
            ServerMeshShipSettings settings = ServerMeshShipLoad.Active;
            if (settings == null || settings.Scale == null)
            {
                Logging.LogError("Mesh", "Feeder error: ship puppets need the ship settings, which no ship installer published; no puppets fed");
                return;
            }
            if (!ServerMeshShipEntity.EnsureRegistered(_mesh, out string error))
            {
                Logging.LogError("Mesh", "Feeder error: " + MeshStateSender.CleanForLog(error) + "; no puppets fed");
                return;
            }
            int hash = ServerMeshShipEntity.LoadPrefab(out _).Identity.HashAssetId;
            ServerMeshShipScale scale = settings.Scale;
            double now = _mesh.ServerTimestamp / 1000.0;
            for (int i = 0; i < _count; ++i)
            {
                int index = PathIndexBase + i;
                if (!ServerMeshShipFlightPath.TryChooseCentre(settings.Seed, index, cellRect, ServerMeshShipLoad.GroundY, scale, out Vector3 centre, out error))
                {
                    Logging.LogError("Mesh", $"Feeder error: ship puppet {i}: {MeshStateSender.CleanForLog(error)}; no more puppets spawned");
                    return;
                }
                ServerMeshShipFlightPath path = new ServerMeshShipFlightPath(settings.Seed, index, centre, scale);
                path.Evaluate(now, out Vector3 position, out Quaternion rotation, out _, out _);
                LiteNetLibIdentity identity = _mesh.Assets.GetObjectInstance(hash, position, rotation);
                ServerMeshShipEntity ship = identity == null ? null : identity.GetComponent<ServerMeshShipEntity>();
                if (ship == null)
                {
                    if (identity != null)
                        Destroy(identity.gameObject);
                    Logging.LogError("Mesh", $"Feeder error: no ship instance for prefab hash {hash}; no more puppets spawned");
                    return;
                }
                ship.Configure(ServerMeshShipAuthority.OwnerMirror, i % 2, index, centre, scale, settings, ServerMeshShipClass.Base);
                _mesh.Assets.NetworkSpawn(identity);
                ship.ShipMovement.PuppetHeld = true;
                ServerMeshShipPuppetBody body = new ServerMeshShipPuppetBody(identity.ObjectId, identity, ship.transform, settings.Wire, scale);
                if (!body.Begin(_slots, out error))
                {
                    Logging.LogError("Mesh", "Feeder error: " + MeshStateSender.CleanForLog(error) + "; that puppet is destroyed");
                    identity.NetworkDestroy();
                    continue;
                }
                _puppets.Add(new ServerMeshFeederPuppet { identity = identity, shipBody = body, path = path, centre = centre });
            }
        }

        private void SpawnCharacters(Rect cellRect)
        {
            ServerMeshShipSettings settings = ServerMeshShipLoad.Active;
            int seed = settings != null ? settings.Seed : ServerMeshLoadGenerator.DefaultSeed;
            Rect area = ServerMeshShipPilot.InsetRectangle(cellRect, 0.1f);
            bool gridDisabled = DefaultGridManagerComponent.Instance == null || DefaultGridManagerComponent.Instance.IsDisabled;
            for (int i = 0; i < _count; ++i)
            {
                Vector3 centre = ServerMeshShipLoad.DrawPoint(new System.Random(ServerMeshShipFlightPath.MixSeed(seed ^ CharacterSalt, i)), area, 0f);
                string id = $"feeder-{_mesh.OwnZoneId}-{i}";
                string name = $"Feeder{_mesh.OwnZoneId}{i:000}";
                BasePlayerCharacterEntity entity = ServerMeshShipLoad.SpawnServerPlayer(_mesh, id, name, centre + Vector3.right * CharacterOrbitMetres, out string error);
                if (entity == null)
                {
                    Logging.LogError("Mesh", "Feeder error: " + MeshStateSender.CleanForLog(error) + "; no more puppets spawned");
                    return;
                }
                if (!_slots.TryGetKitHandler(entity.ObjectId, out IEntityMovementDataHandler kit) || kit == null)
                    error = $"object {entity.ObjectId} has no kit movement handler";
                else if (MeshKinematicCharacterBody.Refusal(kit, false, false, gridDisabled) != MeshPuppetFallback.None)
                    error = $"object {entity.ObjectId} cannot move kinematically ({MeshKinematicCharacterBody.Refusal(kit, false, false, gridDisabled)})";
                MeshKinematicCharacterBody body = null;
                if (error == null)
                {
                    MeshKinematicTrack track = new MeshKinematicTrack(snapDistance: _mesh.ghostTeleportDistance);
                    Transform entityTransform = entity.EntityTransform;
                    if (!track.TryAccept(entityTransform.position, Vector3.zero, entityTransform.eulerAngles.y, false, Time.unscaledTime))
                        error = $"object {entity.ObjectId} does not stand at a finite pose";
                    else
                    {
                        body = new MeshKinematicCharacterBody(entity.ObjectId, entity.Identity, entityTransform, kit, kit is NavMeshEntityMovement, track);
                        if (!body.Begin(entity, _slots, out error))
                            body = null;
                    }
                }
                if (body == null)
                {
                    Logging.LogError("Mesh", "Feeder error: " + MeshStateSender.CleanForLog(error) + "; that puppet is destroyed");
                    entity.Identity.NetworkDestroy();
                    continue;
                }
                _puppets.Add(new ServerMeshFeederPuppet
                {
                    identity = entity.Identity, character = entity, characterBody = body, characterId = id, characterName = name, centre = centre,
                });
            }
        }

        private void RetryRegistration()
        {
            if (!_retryRegistration)
                return;
            if (_sender == null)
            {
                _retryRegistration = false;
                return;
            }
            if (!_sender.IsSending)
                return;
            _retryRegistration = false;
            for (int i = 0; i < _puppets.Count; ++i)
            {
                ServerMeshFeederPuppet puppet = _puppets[i];
                if (puppet.identity == null || !_sender.Register(puppet.Source))
                    continue;
                if (puppet.shipBody != null)
                    puppet.shipBody.SentBySender = true;
                else
                    puppet.characterBody.SentBySender = true;
            }
        }

        private void Feed()
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            double now = Time.unscaledTimeAsDouble;
            float nowSeconds = Time.unscaledTime;
            long timestamp = _mesh.ServerTimestamp;
            double period = 1.0 / _hz;
            bool fed = false;
            for (int i = 0; i < _puppets.Count; ++i)
            {
                ServerMeshFeederPuppet puppet = _puppets[i];
                if (puppet.identity == null || now < puppet.due)
                    continue;
                puppet.due = now - puppet.due > period ? now + period : puppet.due + period;
                fed = true;
                if (puppet.shipBody != null)
                    FeedShip(puppet, timestamp, now);
                else
                    FeedCharacter(puppet, timestamp, nowSeconds);
                _snapshots++;
            }
            if (fed)
                _totalTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
        }

        private void FeedShip(ServerMeshFeederPuppet puppet, long timestamp, double now)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            puppet.path.Evaluate(timestamp / 1000.0, out Vector3 position, out Quaternion rotation, out Vector3 velocity, out Vector3 angular);
            ServerMeshShipSample sample = new ServerMeshShipSample
            {
                Position = position,
                Rotation = rotation,
                Velocity = velocity,
                AngularVelocity = angular,
                Forward = true,
            };
            _writer.Reset();
            ServerMeshShipWire.WriteHeader56(_writer, in sample);
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            _reader.SetSource(_writer);
            ServerMeshShipWire.ReadHeader56(_reader, out ServerMeshShipSample decoded);
            long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
            puppet.shipBody.Accept(in decoded, now);
            long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (puppet.identity.CountSubscribers() > 0)
                puppet.shipBody.Tick((float)(now - puppet.lastPose));
            else
                puppet.shipBody.ApplyNow();
            puppet.lastPose = now;
            long t4 = System.Diagnostics.Stopwatch.GetTimestamp();
            _encodeTicks += t1 - t0;
            _decodeTicks += t2 - t1;
            _acceptTicks += t3 - t2;
            _poseTicks += t4 - t3;
        }

        private void FeedCharacter(ServerMeshFeederPuppet puppet, long timestamp, float now)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            double angle = timestamp / 1000.0 * CharacterOrbitRate % (2.0 * Math.PI);
            float cos = (float)Math.Cos(angle), sin = (float)Math.Sin(angle);
            BasePlayerCharacterEntity entity = puppet.character;
            _outgoing.kind = GhostKind.Player;
            _outgoing.characterId = puppet.characterId;
            _outgoing.characterName = puppet.characterName;
            _outgoing.entityId = entity.EntityId;
            _outgoing.dataId = entity.DataId;
            _outgoing.level = entity.Level;
            _outgoing.position = puppet.centre + new Vector3(cos, 0f, sin) * CharacterOrbitMetres;
            _outgoing.velocity = new Vector3(-sin, 0f, cos) * (CharacterOrbitMetres * CharacterOrbitRate);
            _outgoing.yaw = Mathf.Atan2(-sin, cos) * Mathf.Rad2Deg;
            _outgoing.movementState = (byte)MovementState.Forward;
            _outgoing.currentHp = entity.CurrentHp;
            _outgoing.maxHp = entity.MaxHp;
            _writer.Reset();
            _outgoing.Serialize(_writer);
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            _reader.SetSource(_writer);
            _incoming.Deserialize(_reader);
            long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
            puppet.characterBody.Accept(_incoming.position, _incoming.velocity, _incoming.yaw, _incoming.movementState, _incoming.extraMovementState, false, now);
            long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (puppet.identity.CountSubscribers() > 0)
                puppet.characterBody.Tick(now);
            else
                puppet.characterBody.ApplyNow(now);
            long t4 = System.Diagnostics.Stopwatch.GetTimestamp();
            _encodeTicks += t1 - t0;
            _decodeTicks += t2 - t1;
            _acceptTicks += t3 - t2;
            _poseTicks += t4 - t3;
        }

        private void OnObjectDestroy(LiteNetLibIdentity identity)
        {
            if (_tearingDown || identity == null)
                return;
            for (int i = 0; i < _puppets.Count; ++i)
            {
                ServerMeshFeederPuppet puppet = _puppets[i];
                if (puppet.identity != identity)
                    continue;
                _slots.Forget(identity.ObjectId);
                puppet.identity = null;
                return;
            }
        }

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
                ServerMeshFeederPuppet puppet = _puppets[i];
                if (puppet.identity == null)
                    continue;
                if (_sender != null)
                    _sender.Unregister(puppet.Source);
                if (puppet.shipBody != null)
                    puppet.shipBody.End(_slots);
                else
                    puppet.characterBody.End(puppet.character, _slots);
                if (!puppet.identity.IsDestroyed)
                    puppet.identity.NetworkDestroy();
            }
            _puppets.Clear();
        }
    }
}
