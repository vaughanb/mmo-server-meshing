// Server Meshing test rig — ServerMeshLoadGenerator: synthetic players for the capacity test (DESIGN.md Q4).
//
// A cell process started with MESH_LOAD_PLAYERS=<n> and MESH_LOAD_CELL=<cell id> spawns <n> server-owned player
// entities (real player prefabs with new-character data, no client) spread over the cell, each with a small brain:
// walk to a random point every few seconds, attack the nearest monster or synthetic player in reach, heal instead of
// dying. They are owned like registered players (streamed to neighbours when they stand in a band) so the cost
// measured is the cell's simulation plus the mesh's ghosting; the client-facing send cost is measured with a few
// real bots on the same cell (Mesh stats: clientOut). Rig only: nothing here runs without the environment variables.
// MESH_LOAD_MOTION (steered, kinematic, inert) and MESH_LOAD_FIGHT (ServerMeshLoadMotion) change how they move and fight.
using System.Collections.Generic;
using LiteNetLibManager;
using MultiplayerARPG.MMO.Mesh;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public class ServerMeshLoadGenerator : MonoBehaviour
    {
        public const string ENV_PLAYERS = "MESH_LOAD_PLAYERS";
        public const string ENV_CELL = "MESH_LOAD_CELL";
        public const string ENV_UNCAPPED = "MESH_LOAD_UNCAPPED"; // 1: the loaded cell runs without a frame-rate cap, so its frame time is pure work per frame
        public const string ENV_AREA = "MESH_LOAD_AREA"; // x,z,r (scene frame): crowd the synthetic players into the square of half-size r around (x,z)
        public const string ENV_PLAN = "MESH_LOAD_PLAN"; // cell:count,cell:count -- a count per cell (wins over MESH_LOAD_PLAYERS/MESH_LOAD_CELL)
        public const string ENV_SEED = "MESH_LOAD_SEED"; // placement seed (default 12345); an A/B varies it per repeat so repeats sample placement
        public const int DefaultSeed = 12345;

        private int _count;
        private string _cell;
        private bool _spawned;
        private float _nextTry;
        private readonly List<BasePlayerCharacterEntity> _entities = new List<BasePlayerCharacterEntity>();

        private Dictionary<string, int> _plan;
        private ServerMeshLoadMotionKind _motion = ServerMeshLoadMotion.DefaultMotion;
        private bool _fight = ServerMeshLoadMotion.DefaultFight;
        private ServerMeshKinematicWalk _walk;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoStart()
        {
            string planValue = System.Environment.GetEnvironmentVariable(ENV_PLAN);
            Dictionary<string, int> plan = null;
            if (!string.IsNullOrEmpty(planValue))
            {
                plan = ParsePlan(planValue);
                if (plan == null)
                    return;
            }
            string players = System.Environment.GetEnvironmentVariable(ENV_PLAYERS);
            string cell = System.Environment.GetEnvironmentVariable(ENV_CELL);
            int count = 0;
            if (plan == null && (string.IsNullOrEmpty(players) || string.IsNullOrEmpty(cell) || !int.TryParse(players, out count) || count <= 0))
                return;
            if (FindFirstObjectByType<ServerMeshLoadGenerator>() != null)
                return;
            GameObject go = new GameObject("[Mesh] LoadGenerator");
            DontDestroyOnLoad(go);
            ServerMeshLoadGenerator generator = go.AddComponent<ServerMeshLoadGenerator>();
            generator._count = count;
            generator._cell = cell;
            generator._plan = plan;
        }

        /// <summary>The per-cell counts of MESH_LOAD_PLAN ("C:150,E:150"), or null (logged) when it cannot be read.</summary>
        private static Dictionary<string, int> ParsePlan(string value)
        {
            Dictionary<string, int> plan = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
            foreach (string entry in value.Split(','))
            {
                string[] kv = entry.Split(':');
                if (kv.Length != 2 || string.IsNullOrWhiteSpace(kv[0]) || !int.TryParse(kv[1].Trim(), out int n) || n < 0)
                {
                    Logging.LogError("Mesh", $"Load generator: {ENV_PLAN}={value}: '{entry}' is not cell:count; no load spawned");
                    return null;
                }
                plan[kv[0].Trim()] = n;
            }
            return plan;
        }

        private void Update()
        {
            if (_spawned || Time.unscaledTime < _nextTry)
                return;
            _nextTry = Time.unscaledTime + 1f;
            if (!(BaseGameNetworkManager.Singleton is MeshMapNetworkManager mesh) || !mesh.IsServer || mesh.OwnCell == null)
                return;
            if (_plan != null)
            {
                if (!_plan.TryGetValue(mesh.OwnZoneId, out _count) || _count <= 0)
                {
                    _spawned = true; // not in the plan: nothing to do here
                    return;
                }
            }
            else if (!ServesThisCell(mesh.OwnZoneId))
            {
                _spawned = true; // another cell: nothing to do here
                return;
            }
            if (!mesh.IsServerReadyToInstantiateObjects())
                return;
            _spawned = true;
            if (System.Environment.GetEnvironmentVariable(ENV_UNCAPPED) == "1")
            {
                if (ServerMeshRates.OwnsFrameRate)
                    Logging.Log("Mesh", $"Load generator: {ENV_UNCAPPED}=1 ignored; the frame rate is left to {ServerMeshRates.ENV_RATES} / {ServerMeshRates.ENV_UNCAPPED_CELLS}");
                else
                {
                    // The Work ticks line keeps the budget the cell was capped at; the probe itself reads uncapped.
                    if (Application.targetFrameRate > 0)
                        MeshWorkProbe.NominalFrameRate = Application.targetFrameRate;
                    Application.targetFrameRate = -1;
                    Logging.Log("Mesh", "Load generator: frame rate uncapped on this cell (frame time = work per frame)");
                }
            }
            Spawn(mesh);
        }

        /// <summary>
        /// Whether this cell should spawn synthetic players. One id loads one cell, which is what a per-cell
        /// capacity measurement wants; a comma-separated list or "all" loads several at once, which is what asking
        /// "how much does this whole machine hold" needs -- the per-cell number was always measured with the other
        /// cells idle, and idling cells do not compete for the box.
        /// </summary>
        private bool ServesThisCell(string ownZoneId)
        {
            if (string.IsNullOrEmpty(_cell))
                return false;
            if (string.Equals(_cell, "all", System.StringComparison.OrdinalIgnoreCase))
                return true;
            foreach (string id in _cell.Split(','))
            {
                if (string.Equals(id.Trim(), ownZoneId))
                    return true;
            }
            return false;
        }

        private void Spawn(MeshMapNetworkManager mesh)
        {
            int classId = 0, entityId = 0;
            foreach (int id in GameInstance.PlayerCharacters.Keys) { classId = id; break; }
            foreach (int id in GameInstance.PlayerCharacterEntities.Keys) { entityId = id; break; }
            if (classId == 0 || entityId == 0)
            {
                Logging.LogError("Mesh", "Load generator: no player class or entity in the game database");
                return;
            }
            int seed = ReadSeed();
            bool crowded = TryReadCrowdArea(out Rect crowd);
            List<Placement> placements;
            Rect area;
            if (_plan != null)
            {
                if (!TryPlaceShared(mesh, seed, crowded, crowd, out placements, out area))
                    return;
            }
            else if (!TryPlaceOnCell(mesh, seed, crowded, crowd, out placements, out area))
                return;
            PrepareMotion(area);
            int spawned = 0;
            foreach (Placement placement in placements)
            {
                Vector3 position = placement.position;
                PlayerCharacterData data = new PlayerCharacterData();
                data.SetNewPlayerCharacterData(placement.name, classId, entityId, 0);
                data.Id = placement.id;
                data.Level = 5;
                data.CurrentMapName = mesh.OwnMapId;
                data.CurrentPosition = position;
                LiteNetLibIdentity spawnObj = mesh.Assets.GetObjectInstance(GameInstance.GetPlayerCharacterEntityHashAssetId(data.EntityId, out int metaDataId), position, Quaternion.identity);
                if (spawnObj == null)
                    break;
                BasePlayerCharacterEntity entity = spawnObj.GetComponent<BasePlayerCharacterEntity>();
                if (entity == null)
                {
                    Destroy(spawnObj.gameObject);
                    break;
                }
                GameInstance.SetupByMetaData(entity, metaDataId);
                data.CloneTo(entity);
                mesh.Assets.NetworkSpawn(ServerMeshSpaceTags.Tag(spawnObj, false));
                entity.ForceMakeCaches();
                entity.FillEmptySlots();
                entity.CurrentHp = entity.MaxHp;
                entity.CurrentMp = entity.MaxMp;
                mesh.TrackSyntheticEntity(entity);
                StartMotion(entity, area, placement.brainSeed);
                _entities.Add(entity);
                spawned++;
            }
            string message = $"Load generator: spawned {spawned} synthetic players on cell {mesh.OwnZoneId} over x [{area.xMin:0}, {area.xMax:0}] z [{area.yMin:0}, {area.yMax:0}] (seed {seed}, planned {_count})";
            if (spawned < _count)
                Logging.LogError("Mesh", message);
            else
                Logging.Log("Mesh", message);
        }

        /// <summary>Reads MESH_LOAD_MOTION and MESH_LOAD_FIGHT; brainless motions get the one walk loop, and a setting other than the default is logged.</summary>
        private void PrepareMotion(Rect area)
        {
            _motion = ServerMeshLoadMotion.ReadMotion();
            _fight = ServerMeshLoadMotion.ReadFight();
            if (_motion != ServerMeshLoadMotionKind.Steered)
            {
                _walk = gameObject.AddComponent<ServerMeshKinematicWalk>();
                _walk.Setup(area, _fight);
            }
            if (_motion != ServerMeshLoadMotion.DefaultMotion || _fight != ServerMeshLoadMotion.DefaultFight)
                Logging.Log("Mesh", ServerMeshLoadMotion.SettingLine(_motion, _fight));
        }

        /// <summary>
        /// Steered players get the brain (fighting unless MESH_LOAD_FIGHT=0); kinematic players go to the walk loop, which
        /// switches their movement component off once it has started; inert players get no brain and keep their movement
        /// component, standing, and the walk loop only heals them.
        /// </summary>
        private void StartMotion(BasePlayerCharacterEntity entity, Rect area, int seed)
        {
            switch (_motion)
            {
                case ServerMeshLoadMotionKind.Kinematic:
                    _walk.AddWalking(entity, seed);
                    break;
                case ServerMeshLoadMotionKind.Inert:
                    _walk.AddStanding(entity, seed);
                    break;
                default:
                    entity.gameObject.AddComponent<SyntheticPlayerBrain>().Setup(entity, area, seed, _fight);
                    break;
            }
        }

        private struct Placement
        {
            public Vector3 position;
            public string id, name;
            public int brainSeed;
        }

        private static int ReadSeed()
        {
            string value = System.Environment.GetEnvironmentVariable(ENV_SEED);
            if (string.IsNullOrEmpty(value))
                return DefaultSeed;
            if (int.TryParse(value.Trim(), out int seed))
                return seed;
            Logging.LogWarning("Mesh", $"Load generator: {ENV_SEED}={value} is not an integer; using {DefaultSeed}");
            return DefaultSeed;
        }

        /// <summary>A deterministic seed for player k's brain, so its wandering does not depend on which cell drew it.</summary>
        private static int BrainSeed(int seed, int k)
        {
            unchecked { return (seed * 486187739) ^ (k * 16777619 + 97); }
        }

        /// <summary>A cell's half-open owned rectangle [min, max) in its scene frame.</summary>
        private static Rect SceneRect(MeshCell cell) => SceneRect(cell.owned, cell.frameOrigin);

        private static Rect SceneRect(MeshRect rect, Vector3 frameOrigin)
        {
            return Rect.MinMaxRect(rect.minX - frameOrigin.x, rect.minZ - frameOrigin.z, rect.maxX - frameOrigin.x, rect.maxZ - frameOrigin.z);
        }

        private static bool TryIntersect(Rect a, Rect b, out Rect result)
        {
            float minX = Mathf.Max(a.xMin, b.xMin), maxX = Mathf.Min(a.xMax, b.xMax);
            float minZ = Mathf.Max(a.yMin, b.yMin), maxZ = Mathf.Min(a.yMax, b.yMax);
            result = Rect.MinMaxRect(minX, minZ, maxX, maxZ);
            return minX < maxX && minZ < maxZ;
        }

        private static Rect Shrink(Rect r, float by) => Rect.MinMaxRect(r.xMin + by, r.yMin + by, r.xMax - by, r.yMax - by);

        /// <summary>
        /// A/B placement (MESH_LOAD_PLAN): every cell draws the same stream of points over the whole load area -- the map's
        /// ground, or the crowd square -- and keeps, in draw order, the first planned count that fall in its own half-open
        /// rectangle. Point k is the same world position whichever way the map is cut, so a player present in two layouts
        /// of one repeat stands at the same place in both; which players are present differs by a few percent, because
        /// each cell fills its own area-proportional quota.
        /// Players keep 0.5 m off the map's outer edge only, never off a cut: a cut layout must not get an empty lane.
        /// </summary>
        private bool TryPlaceShared(MeshMapNetworkManager mesh, int seed, bool crowded, Rect crowd, out List<Placement> placements, out Rect area)
        {
            placements = new List<Placement>();
            area = default;
            if (mesh.OwnCell.owned.IsEmpty)
            {
                Logging.LogError("Mesh", $"Load generator: cell {mesh.OwnZoneId} owns nothing, which the plan gave {_count} players; none spawned");
                return false;
            }
            // The ground is the union of the map's asset cells, whatever the runtime cut: every layout draws over it.
            IReadOnlyList<MeshCell> cells = mesh.world.CellsOf(mesh.OwnMapId);
            Rect ground = SceneRect(mesh.OwnCell.baseRect, mesh.OwnCell.frameOrigin);
            for (int i = 0; i < cells.Count; ++i)
            {
                Rect r = SceneRect(cells[i].baseRect, cells[i].frameOrigin);
                ground = Rect.MinMaxRect(Mathf.Min(ground.xMin, r.xMin), Mathf.Min(ground.yMin, r.yMin), Mathf.Max(ground.xMax, r.xMax), Mathf.Max(ground.yMax, r.yMax));
            }
            Rect draw = Shrink(ground, 0.5f);
            if (crowded && !TryIntersect(draw, crowd, out draw))
            {
                Logging.LogError("Mesh", $"Load generator: {ENV_AREA} does not overlap map {mesh.OwnMapId}; none spawned on cell {mesh.OwnZoneId}");
                return false;
            }
            Rect own = SceneRect(mesh.OwnCell);
            // Wandering stays a hair inside the cell's own rectangle (synthetic players are never handed off).
            if (!TryIntersect(Shrink(own, 0.01f), draw, out area))
            {
                // The plan put players here because of the crowd; spreading them elsewhere would change the load.
                Logging.LogError("Mesh", $"Load generator: the load area does not overlap cell {mesh.OwnZoneId}, which the plan gave {_count} players; none spawned");
                return false;
            }
            System.Random random = new System.Random(seed);
            int maxDraws = Mathf.Max(100000, _count * 1000);
            for (int k = 0; placements.Count < _count && k < maxDraws; ++k)
            {
                float x = draw.xMin + (float)random.NextDouble() * draw.width;
                float z = draw.yMin + (float)random.NextDouble() * draw.height;
                if (x < own.xMin || x >= own.xMax || z < own.yMin || z >= own.yMax)
                    continue;
                placements.Add(new Placement { position = new Vector3(x, 0f, z), id = $"load-{k}", name = $"Load{k:0000}", brainSeed = BrainSeed(seed, k) });
            }
            return true;
        }

        /// <summary>
        /// Single-cell placement (MESH_LOAD_PLAYERS/MESH_LOAD_CELL, the capacity sweeps): uniform over the cell, 5 m off its
        /// edges (0.5 m inside a crowd square), from the cell's own stream.
        /// </summary>
        private bool TryPlaceOnCell(MeshMapNetworkManager mesh, int seed, bool crowded, Rect crowd, out List<Placement> placements, out Rect area)
        {
            placements = new List<Placement>();
            area = default;
            if (mesh.OwnCell.owned.IsEmpty)
            {
                Logging.LogError("Mesh", $"Load generator: cell {mesh.OwnZoneId} owns nothing; none spawned");
                return false;
            }
            area = Shrink(SceneRect(mesh.OwnCell), crowded ? 0.5f : 5f);
            if (crowded && !TryIntersect(area, crowd, out area))
            {
                Logging.LogError("Mesh", $"Load generator: {ENV_AREA} does not overlap cell {mesh.OwnZoneId}; none spawned");
                return false;
            }
            System.Random random = new System.Random(seed);
            for (int i = 0; i < _count; ++i)
            {
                Vector3 position = new Vector3(area.xMin + (float)random.NextDouble() * area.width, 0f, area.yMin + (float)random.NextDouble() * area.height);
                // Per cell: with MESH_LOAD_CELL=all every cell used to mint the same ids.
                placements.Add(new Placement { position = position, id = $"load-{mesh.OwnZoneId}-{i}", name = $"Load{mesh.OwnZoneId}{i:000}", brainSeed = random.Next() });
            }
            return true;
        }

        /// <summary>The crowd square from MESH_LOAD_AREA (x,z,r in the scene frame), when it is set and readable.</summary>
        private static bool TryReadCrowdArea(out Rect area)
        {
            area = default;
            string value = System.Environment.GetEnvironmentVariable(ENV_AREA);
            if (string.IsNullOrEmpty(value))
                return false;
            string[] parts = value.Split(',');
            float[] n = new float[3];
            if (parts.Length != 3)
            {
                Logging.LogWarning("Mesh", $"Load generator: {ENV_AREA}={value} is not x,z,r; ignored");
                return false;
            }
            for (int i = 0; i < 3; ++i)
            {
                if (!float.TryParse(parts[i].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out n[i]))
                {
                    Logging.LogWarning("Mesh", $"Load generator: {ENV_AREA}={value}: '{parts[i]}' is not a number; ignored");
                    return false;
                }
            }
            if (n[2] <= 0f)
            {
                Logging.LogWarning("Mesh", $"Load generator: {ENV_AREA}={value} needs a positive half-size; ignored");
                return false;
            }
            area = new Rect(n[0] - n[2], n[1] - n[2], 2f * n[2], 2f * n[2]);
            return true;
        }
    }

    /// <summary>SyntheticPlayerBrain is a synthetic player's behaviour: wander, fight what is in reach (unless told not to), never die.</summary>
    public class SyntheticPlayerBrain : MonoBehaviour
    {
        private BasePlayerCharacterEntity _entity;
        private Rect _area;
        private System.Random _random;
        private float _nextThink;
        private float _nextAttack;
        private bool _fight = true;
        private static readonly Collider[] s_hits = new Collider[32];

        /// <summary>FightReach is how far a synthetic player looks for a monster to fight.</summary>
        public const float FightReach = 10f;
        /// <summary>MeleeReach is the distance within which a synthetic player swings without chasing.</summary>
        public const float MeleeReach = 2.5f;
        /// <summary>AttackIntervalSeconds is the shortest time between two attacks of one synthetic player.</summary>
        public const float AttackIntervalSeconds = 1.5f;

        /// <summary>
        /// Setup starts the brain on entity: it wanders inside area (scene frame, y is z) from the seed's stream and, when
        /// fight is true, attacks the nearest monster in reach; with fight false it never looks for one.
        /// </summary>
        public void Setup(BasePlayerCharacterEntity entity, Rect area, int seed, bool fight)
        {
            _entity = entity;
            _area = area;
            _random = new System.Random(seed);
            _nextThink = Time.unscaledTime + (float)_random.NextDouble() * 3f;
            _fight = fight;
        }

        /// <summary>HealWhenLow restores a synthetic player to full health once it is below a third of it: a synthetic player is load, not a corpse.</summary>
        public static void HealWhenLow(BasePlayerCharacterEntity entity)
        {
            if (entity.CurrentHp < entity.MaxHp / 3)
                entity.CurrentHp = entity.MaxHp;
        }

        private void Update()
        {
            if (_entity == null || _random == null || Time.unscaledTime < _nextThink)
                return;
            _nextThink = Time.unscaledTime + 2f + (float)_random.NextDouble() * 4f;
            HealWhenLow(_entity);
            Vector3 pos = _entity.EntityTransform.position;
            if (_fight && TryAttackInReach(pos))
                return;
            // Wander: a point up to 20 m away, reflected back into the area at an edge. Clamping would pile players on
            // the edge, which in a cut layout is the cut: more of them in the band than an uncut layout has there.
            float angle = (float)_random.NextDouble() * Mathf.PI * 2f;
            float dist = 5f + (float)_random.NextDouble() * 15f;
            Vector3 goal = pos + new Vector3(Mathf.Cos(angle) * dist, 0f, Mathf.Sin(angle) * dist);
            goal.x = Reflect(goal.x, _area.xMin, _area.xMax);
            goal.z = Reflect(goal.z, _area.yMin, _area.yMax);
            _entity.PointClickMovement(goal);
        }

        /// <summary>
        /// FindMonsterInReach is the monster a synthetic player at position fights: the nearest live, non-ghost monster within
        /// <see cref="FightReach"/>, or null; distance is how far it is (float.MaxValue when there is none).
        /// </summary>
        public static BaseCharacterEntity FindMonsterInReach(BaseCharacterEntity self, Vector3 position, out float distance)
        {
            BaseCharacterEntity target = null;
            distance = float.MaxValue;
            int n = Physics.OverlapSphereNonAlloc(position, FightReach, s_hits);
            for (int i = 0; i < n; ++i)
            {
                BaseMonsterCharacterEntity monster = s_hits[i].GetComponentInParent<BaseMonsterCharacterEntity>();
                if (monster == null || monster == (object)self || monster.IsDead() || ServerMeshFightGhosts.Skips(monster))
                    continue;
                float d = Vector3.Distance(position, monster.EntityTransform.position);
                if (d < distance) { distance = d; target = monster; }
            }
            return target;
        }

        /// <summary>Fights what is in reach: a monster, or another synthetic player (they are enemies to nobody, so only monsters bite). True when it attacked.</summary>
        private bool TryAttackInReach(Vector3 pos)
        {
            BaseCharacterEntity target = FindMonsterInReach(_entity, pos, out float best);
            if (target == null || Time.unscaledTime < _nextAttack)
                return false;
            _nextAttack = Time.unscaledTime + AttackIntervalSeconds;
            _entity.SetTargetEntity(target);
            // The chase stays inside the area too: a synthetic player is never handed off, so one that followed a
            // monster over a cut would be simulated by a cell it no longer stands in.
            if (best > MeleeReach)
            {
                Vector3 chase = target.EntityTransform.position;
                chase.x = Mathf.Clamp(chase.x, _area.xMin, _area.xMax);
                chase.z = Mathf.Clamp(chase.z, _area.yMin, _area.yMax);
                _entity.PointClickMovement(chase);
            }
            WeaponHandlingState state = WeaponHandlingState.None;
            _entity.Attack(ref state);
            return true;
        }

        /// <summary>Reflect folds a coordinate that left [min, max] back inside, clamping only when the range is narrower than the step.</summary>
        public static float Reflect(float value, float min, float max)
        {
            if (value > max)
                value = 2f * max - value;
            else if (value < min)
                value = 2f * min - value;
            return Mathf.Clamp(value, min, max);
        }
    }
}
