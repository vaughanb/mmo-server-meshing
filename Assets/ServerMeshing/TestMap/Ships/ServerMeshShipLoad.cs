// Server Meshing test rig — the stand-in ships' installer and load (Phase 0 step 7): under any ship key it registers the
// ship prefab (map servers, the editor and bots alike), publishes the process's ship settings, adds the bot watch for
// -meshRigShips and, on a map server, spawns MESH_LOAD_SHIPS and logs the Ships window. The space game (commit 1a3a58d9)
// spawns its AI in waves (ShipWaveSpawner); the rig spawns a fixed count per cell instead. Window in _Window.cs, pilots
// in _Pilots.cs.
using System;
using System.Collections.Generic;
using LiteNetLibManager;
using MultiplayerARPG.MMO.Mesh.Sync;
using UnityEngine;
using UnityEngine.Events;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshShipLoad spawns its cell's MESH_LOAD_SHIPS groups once the server can instantiate objects: AI ships at
    /// seeded points inside the cell (or MESH_LOAD_AREA ∩ cell) inset so their patrol orbit stays clear of the rectangle,
    /// parked ships at seeded points lifted above the path plane, mirror ships on seeded flight paths fed in-process. It
    /// keeps every stand-in ship on the server (phantom ships included) for the Ships window. With no ship key and no
    /// -meshRigShips argument nothing is installed and nothing is logged.
    /// </summary>
    public sealed partial class ServerMeshShipLoad : MonoBehaviour
    {
        /// <summary>LoadObjectName names the load's GameObject.</summary>
        public const string LoadObjectName = "[Mesh] ShipLoad";
        /// <summary>AiSalt keys AI spawn points off MESH_LOAD_SEED.</summary>
        public const int AiSalt = 0x5352;
        /// <summary>ParkedSalt keys parked spawn points off MESH_LOAD_SEED.</summary>
        public const int ParkedSalt = 0x5353;
        /// <summary>GroundY is the test map's ground height that flight altitudes are measured from.</summary>
        public const float GroundY = 0f;
        /// <summary>AiOrbitMarginMetres is the extra inset beyond the patrol radius for AI spawn points, at scale 1.</summary>
        public const float AiOrbitMarginMetres = 10f;

        private readonly List<ServerMeshShipEntity> _ships = new List<ServerMeshShipEntity>(256);
        private readonly List<ServerMeshShipEntity> _loadShips = new List<ServerMeshShipEntity>(256);
        private ServerMeshShipSettings _settings;
        private MeshMapNetworkManager _mesh;
        private ServerMeshShipEntity _prefab;
        private UnityAction<LiteNetLibIdentity> _onSpawn;
        private UnityAction<LiteNetLibIdentity> _onDestroy;
        private bool _started;
        private float _nextTry;
        private Rect _cellRect;
        private bool _quitting;

        /// <summary>Active is this process's ship settings (environment, or -meshRigShips on a bot); null with no ship key.</summary>
        public static ServerMeshShipSettings Active { get; private set; }

        /// <summary>LoadShips is the ships this load spawned that are still alive.</summary>
        public IReadOnlyList<ServerMeshShipEntity> LoadShips => _loadShips;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoStart()
        {
            Active = null;
            string[] args = Environment.GetCommandLineArgs();
            bool bot = HasArgument(args, ServerMeshShipSettings.BotArgument);
            ServerMeshShipSettings settings;
            string error;
            if (bot)
            {
                settings = ResolveBotArgument(ReadArgument(args, ServerMeshShipSettings.BotArgument), out error);
                if (settings == null)
                {
                    Logging.LogError("Mesh", "Bot ships error: " + MeshStateSender.CleanForLog(error) + "; nothing registered");
                    return;
                }
            }
            else
            {
                settings = ServerMeshShipSettings.TryResolve(Environment.GetEnvironmentVariable, out error);
                if (settings == null)
                {
                    Logging.LogError("Mesh", "Ships error: " + MeshStateSender.CleanForLog(error) + "; no ship installed");
                    return;
                }
                if (!settings.AnyShipKey)
                    return;
            }
            Active = settings;
            if (!ServerMeshShipEntity.EnsureRegistered(null, out error))
            {
                Logging.LogError("Mesh", (bot ? "Bot ships error: " : "Ships error: ") + MeshStateSender.CleanForLog(error));
                return;
            }
            if (bot && FindFirstObjectByType<ServerMeshShipWatch>() == null)
            {
                GameObject watch = new GameObject(ServerMeshShipWatch.WatchObjectName);
                DontDestroyOnLoad(watch);
                watch.AddComponent<ServerMeshShipWatch>().Setup(settings);
                Logging.Log("Mesh", $"Bot ships: watching wire {WireName(settings.Wire)} seed {settings.Seed} scale {ScaleText(settings.Scale)}");
            }
            if (!ConfigReader.IsArgsProvided(args, ProcessArguments.ARG_START_MAP_SERVER) || FindFirstObjectByType<ServerMeshShipLoad>() != null)
                return;
            GameObject go = new GameObject(LoadObjectName);
            DontDestroyOnLoad(go);
            go.AddComponent<ServerMeshShipLoad>()._settings = settings;
        }

        /// <summary>
        /// ResolveBotArgument reads a bot's -meshRigShips value (wire:seed:scale) into ship settings, the seed and scale texts
        /// passed on verbatim so server and bot parse the same numbers; null with the reason when the value is missing or bad.
        /// </summary>
        public static ServerMeshShipSettings ResolveBotArgument(string value, out string error)
        {
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("-", StringComparison.Ordinal))
            {
                error = ServerMeshShipSettings.BotArgument + " needs a value wire:seed:scale";
                return null;
            }
            if (!ServerMeshShipSettings.TryParseBotArgument(value, out _, out _, out _, out error))
                return null;
            string[] parts = value.Split(':');
            if (parts.Length != 3)
            {
                error = ServerMeshShipSettings.BotArgument + " " + value + " is not wire:seed:scale";
                return null;
            }
            return ServerMeshShipSettings.TryResolve(key =>
            {
                switch (key)
                {
                    case ServerMeshShipSettings.RigShipsKey: return "1";
                    case ServerMeshShipSettings.WireKey: return parts[0];
                    case ServerMeshShipSettings.SeedKey: return parts[1];
                    case ServerMeshShipSettings.ScaleKey: return parts[2];
                    default: return null;
                }
            }, out error);
        }

        /// <summary>HasArgument is true when <paramref name="args"/> holds <paramref name="name"/> exactly.</summary>
        public static bool HasArgument(string[] args, string name)
        {
            if (args == null)
                return false;
            for (int i = 0; i < args.Length; ++i)
            {
                if (string.Equals(args[i], name, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>ReadArgument is the text after the first <paramref name="name"/> in <paramref name="args"/>, or null when it is absent or last.</summary>
        public static string ReadArgument(string[] args, string name)
        {
            if (args == null)
                return null;
            for (int i = 0; i < args.Length - 1; ++i)
            {
                if (string.Equals(args[i], name, StringComparison.Ordinal))
                    return args[i + 1];
            }
            return null;
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
            SeatingUpdate();
            SampleIfDue();
            WindowIfDue();
        }

        private void TryStart()
        {
            if (!(BaseGameNetworkManager.Singleton is MeshMapNetworkManager mesh) || !mesh.IsServer || mesh.OwnCell == null)
                return;
            if (!mesh.IsServerReadyToInstantiateObjects())
                return;
            _started = true;
            _mesh = mesh;
            if (!ServerMeshShipEntity.EnsureRegistered(mesh, out string error))
            {
                Logging.LogError("Mesh", "Ships error: " + MeshStateSender.CleanForLog(error) + "; no load ships spawned");
                return;
            }
            _prefab = ServerMeshShipEntity.LoadPrefab(out _);
            ServerMeshShipProjectile.EnsureTemplate();
            _onSpawn = OnObjectSpawn;
            _onDestroy = OnObjectDestroy;
            mesh.Assets.onObjectSpawn.AddListener(_onSpawn);
            mesh.Assets.onObjectDestroy.AddListener(_onDestroy);
            var spawned = mesh.Assets.GetSpawnedObjects();
            while (spawned.MoveNext())
                OnObjectSpawn(spawned.Current.Value);
            MeshCell cell = mesh.OwnCell;
            _cellRect = Rect.MinMaxRect(cell.owned.minX - cell.frameOrigin.x, cell.owned.minZ - cell.frameOrigin.z, cell.owned.maxX - cell.frameOrigin.x, cell.owned.maxZ - cell.frameOrigin.z);
            CountGroups(mesh.OwnZoneId, out int ai, out int mirror, out int parked);
            int pilots = _settings.Pilots ? ai + mirror + parked : 0;
            Logging.Log("Mesh", SettingLine(mesh.OwnZoneId, ai, mirror, parked, pilots, _settings.Wire, _settings.Scale, _settings.PooledProjectiles,
                _settings.RealMirrorVelocity, _settings.SelfAck, _settings.WaveAi, _settings.HasArea, _settings.Area, _prefab.Identity.HashAssetId));
            StartWindow();
            if (ai + mirror + parked > 0)
                SpawnLoad(mesh.OwnZoneId);
            if (_settings.Pilots)
                SeatPilots(mesh.OwnZoneId);
        }

        private void CountGroups(string cellId, out int ai, out int mirror, out int parked)
        {
            ai = mirror = parked = 0;
            if (_settings.Load == null)
                return;
            IReadOnlyList<ServerMeshShipLoadGroup> groups = _settings.Load.Groups;
            for (int i = 0; i < groups.Count; ++i)
            {
                if (!string.Equals(groups[i].Cell, cellId, StringComparison.OrdinalIgnoreCase))
                    continue;
                switch (groups[i].Authority)
                {
                    case ServerMeshShipAuthority.ServerAI: ai += groups[i].Count; break;
                    case ServerMeshShipAuthority.Parked: parked += groups[i].Count; break;
                    default: mirror += groups[i].Count; break;
                }
            }
        }

        private void SpawnLoad(string cellId)
        {
            if (!TryLoadArea(_cellRect, _settings.HasArea, _settings.Area, out Rect area, out string error))
            {
                Logging.LogError("Mesh", $"Ships error: cell {cellId}: {error}; no load ships spawned");
                return;
            }
            ServerMeshShipScale scale = _settings.Scale;
            int seed = _settings.Seed;
            float flightY = FlightHeight();
            List<ServerMeshShipEntity> mirrors = new List<ServerMeshShipEntity>();
            List<ServerMeshShipFlightPath> paths = new List<ServerMeshShipFlightPath>();
            IReadOnlyList<ServerMeshShipLoadGroup> groups = _settings.Load.Groups;
            for (int g = 0; g < groups.Count; ++g)
            {
                ServerMeshShipLoadGroup group = groups[g];
                if (!string.Equals(group.Cell, cellId, StringComparison.OrdinalIgnoreCase))
                    continue;
                switch (group.Authority)
                {
                    case ServerMeshShipAuthority.ServerAI:
                        if (!TryAiSpawnRect(area, scale, out Rect aiRect))
                            Logging.LogWarning("Mesh", $"Ships: cell {cellId}: the AI patrol orbit does not fit inside the area; AI ships spawn inside its 10 % inset");
                        for (int k = 0; k < group.Count; ++k)
                        {
                            System.Random random = new System.Random(ServerMeshShipFlightPath.MixSeed(seed ^ AiSalt, k));
                            Vector3 point = DrawPoint(random, aiRect, flightY);
                            ServerMeshShipEntity ship = Spawn(ServerMeshShipAuthority.ServerAI, k % 2, -1, point, point, DrawYaw(random));
                            if (ship == null)
                                return;
                            ship.ShipPilot.Begin(seed, k, _cellRect, flightY, scale);
                        }
                        break;
                    case ServerMeshShipAuthority.Parked:
                        Rect parkedRect = ServerMeshShipPilot.InsetRectangle(area, scale.RectangleInset);
                        float parkedY = ParkedHeight(scale);
                        for (int k = 0; k < group.Count; ++k)
                        {
                            System.Random random = new System.Random(ServerMeshShipFlightPath.MixSeed(seed ^ ParkedSalt, k));
                            Vector3 point = DrawPoint(random, parkedRect, parkedY);
                            if (Spawn(ServerMeshShipAuthority.Parked, k % 2, -2, point, point, DrawYaw(random)) == null)
                                return;
                        }
                        break;
                    default:
                        double now = _mesh.ServerTimestamp / 1000.0;
                        for (int k = 0; k < group.Count; ++k)
                        {
                            if (!ServerMeshShipFlightPath.TryChooseCentre(seed, k, area, GroundY, scale, out Vector3 centre, out error))
                            {
                                Logging.LogError("Mesh", $"Ships error: cell {cellId} mirror ship {k}: {MeshStateSender.CleanForLog(error)}; no more mirror ships spawned");
                                break;
                            }
                            ServerMeshShipFlightPath path = new ServerMeshShipFlightPath(seed, k, centre, scale);
                            path.Evaluate(now, out Vector3 position, out Quaternion rotation, out _, out _);
                            ServerMeshShipEntity ship = Spawn(ServerMeshShipAuthority.OwnerMirror, k % 2, k, centre, position, rotation);
                            if (ship == null)
                                return;
                            mirrors.Add(ship);
                            paths.Add(path);
                        }
                        break;
                }
            }
            if (mirrors.Count > 0)
                gameObject.AddComponent<ServerMeshShipMirrorFeeder>().Setup(_mesh, mirrors, paths, _settings.Wire);
        }

        private ServerMeshShipEntity Spawn(ServerMeshShipAuthority authority, int team, int pathIndex, Vector3 centre, Vector3 position, Quaternion rotation)
        {
            LiteNetLibIdentity identity = _mesh.Assets.GetObjectInstance(_prefab.Identity.HashAssetId, position, rotation);
            ServerMeshShipEntity ship = identity == null ? null : identity.GetComponent<ServerMeshShipEntity>();
            if (ship == null)
            {
                if (identity != null)
                    Destroy(identity.gameObject);
                Logging.LogError("Mesh", $"Ships error: cell {_mesh.OwnZoneId}: no ship instance for prefab hash {_prefab.Identity.HashAssetId}; no more load ships spawned");
                return null;
            }
            ship.Configure(authority, team, pathIndex, centre, _settings.Scale, _settings, ClassFor(authority, _settings.AiClass));
            _mesh.Assets.NetworkSpawn(identity);
            _loadShips.Add(ship);
            return ship;
        }

        /// <summary>
        /// ClassFor is the class a load ship of <paramref name="authority"/> takes: <paramref name="aiClass"/> (MESH_SHIP_AI_PREFAB)
        /// for a ServerAI ship, Base for every other.
        /// </summary>
        public static ServerMeshShipClass ClassFor(ServerMeshShipAuthority authority, ServerMeshShipClass aiClass)
        {
            return authority == ServerMeshShipAuthority.ServerAI ? aiClass : ServerMeshShipClass.Base;
        }

        private void OnObjectSpawn(LiteNetLibIdentity identity)

        {
            if (identity == null)
                return;
            ServerMeshShipEntity ship = identity.GetComponent<ServerMeshShipEntity>();
            if (ship != null && !_ships.Contains(ship))
                _ships.Add(ship);
        }

        private void OnObjectDestroy(LiteNetLibIdentity identity)
        {
            if (identity == null)
                return;
            ServerMeshShipEntity ship = identity.GetComponent<ServerMeshShipEntity>();
            if (ship == null)
                return;
            _ships.Remove(ship);
            _loadShips.Remove(ship);
        }

        private void OnDestroy()
        {
            if (_mesh != null && _mesh.Assets != null)
            {
                if (_onSpawn != null)
                    _mesh.Assets.onObjectSpawn.RemoveListener(_onSpawn);
                if (_onDestroy != null)
                    _mesh.Assets.onObjectDestroy.RemoveListener(_onDestroy);
            }
            Teardown();
        }

        private void OnApplicationQuit() => _quitting = true;

        /// <summary>
        /// Teardown destroys the load ships first, then the pilots (destroying a seated pilot would destroy its ship). At
        /// quit or Play stop it does nothing: the kit destroys every object in its own pass.
        /// </summary>
        private void Teardown()
        {
            if (_quitting || _mesh == null || !_mesh.IsServer)
                return;
            for (int i = _loadShips.Count - 1; i >= 0; --i)
            {
                ServerMeshShipEntity ship = _loadShips[i];
                if (ship != null && ship.Identity != null && !ship.Identity.IsDestroyed)
                    ship.Identity.NetworkDestroy();
            }
            _loadShips.Clear();
            DestroyPilots();
        }

        /// <summary>
        /// TryLoadArea is where load ships go: the cell rectangle, or MESH_LOAD_AREA ∩ cell when <paramref name="hasArea"/>;
        /// false with the reason when that intersection is empty.
        /// </summary>
        public static bool TryLoadArea(Rect cell, bool hasArea, Rect area, out Rect result, out string error)
        {
            if (!hasArea)
            {
                result = cell;
                error = null;
                return true;
            }
            float minX = Mathf.Max(cell.xMin, area.xMin), maxX = Mathf.Min(cell.xMax, area.xMax);
            float minZ = Mathf.Max(cell.yMin, area.yMin), maxZ = Mathf.Min(cell.yMax, area.yMax);
            result = Rect.MinMaxRect(minX, minZ, maxX, maxZ);
            if (minX < maxX && minZ < maxZ)
            {
                error = null;
                return true;
            }
            error = ServerMeshShipSettings.AreaKey + " does not overlap the cell";
            return false;
        }

        /// <summary>
        /// TryAiSpawnRect is where AI ships spawn in <paramref name="area"/>: inset by the scale's share, then by the patrol
        /// radius plus AiOrbitMarginMetres times the scale, so a patrolling ship never meets the rectangle. When that leaves
        /// nothing it is false and <paramref name="spawn"/> is the share inset alone.
        /// </summary>
        public static bool TryAiSpawnRect(Rect area, ServerMeshShipScale scale, out Rect spawn)
        {
            if (scale == null)
                throw new ArgumentNullException(nameof(scale));
            Rect inset = ServerMeshShipPilot.InsetRectangle(area, scale.RectangleInset);
            float orbit = scale.PatrolRadius + AiOrbitMarginMetres * scale.Factor;
            Rect shrunk = Rect.MinMaxRect(inset.xMin + orbit, inset.yMin + orbit, inset.xMax - orbit, inset.yMax - orbit);
            if (shrunk.xMin < shrunk.xMax && shrunk.yMin < shrunk.yMax)
            {
                spawn = shrunk;
                return true;
            }
            spawn = inset;
            return false;
        }

        /// <summary>FlightHeight is where pathed and AI ships fly: GroundY plus the flight path altitude.</summary>
        public static float FlightHeight() => GroundY + ServerMeshShipFlightPath.AltitudeMetres;

        /// <summary>ParkedHeight is where parked ships float: FlightHeight plus the parked lift, clear of mirrors passing on the path plane.</summary>
        public static float ParkedHeight(ServerMeshShipScale scale) => FlightHeight() + ServerMeshShipFlightPath.ParkedLiftMetres(scale);

        /// <summary>DrawPoint is a uniform point of <paramref name="rect"/> (X/Z, y is z) at height <paramref name="y"/>: two draws from <paramref name="random"/>.</summary>
        public static Vector3 DrawPoint(System.Random random, Rect rect, float y)
        {
            if (random == null)
                throw new ArgumentNullException(nameof(random));
            float x = rect.xMin + (float)random.NextDouble() * rect.width;
            float z = rect.yMin + (float)random.NextDouble() * rect.height;
            return new Vector3(x, y, z);
        }

        private static Quaternion DrawYaw(System.Random random) => Quaternion.Euler(0f, (float)random.NextDouble() * 360f, 0f);
    }
}
