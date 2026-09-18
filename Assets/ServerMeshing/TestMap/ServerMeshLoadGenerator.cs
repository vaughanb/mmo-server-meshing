// Server Meshing test rig — ServerMeshLoadGenerator: synthetic players for the capacity test (DESIGN.md Q4).
//
// A cell process started with MESH_LOAD_PLAYERS=<n> and MESH_LOAD_CELL=<cell id> spawns <n> server-owned player
// entities (real player prefabs with new-character data, no client) spread over the cell, each with a small brain:
// walk to a random point every few seconds, attack the nearest monster or synthetic player in reach, heal instead of
// dying. They are owned like registered players (streamed to neighbours when they stand in a band) so the cost
// measured is the cell's simulation plus the mesh's ghosting; the client-facing send cost is measured with a few
// real bots on the same cell (Mesh stats: clientOut). Rig only: nothing here runs without the environment variables.
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

        private int _count;
        private string _cell;
        private bool _spawned;
        private float _nextTry;
        private readonly List<BasePlayerCharacterEntity> _entities = new List<BasePlayerCharacterEntity>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoStart()
        {
            string players = System.Environment.GetEnvironmentVariable(ENV_PLAYERS);
            string cell = System.Environment.GetEnvironmentVariable(ENV_CELL);
            if (string.IsNullOrEmpty(players) || string.IsNullOrEmpty(cell) || !int.TryParse(players, out int count) || count <= 0)
                return;
            if (FindFirstObjectByType<ServerMeshLoadGenerator>() != null)
                return;
            GameObject go = new GameObject("[Mesh] LoadGenerator");
            DontDestroyOnLoad(go);
            ServerMeshLoadGenerator generator = go.AddComponent<ServerMeshLoadGenerator>();
            generator._count = count;
            generator._cell = cell;
        }

        private void Update()
        {
            if (_spawned || Time.unscaledTime < _nextTry)
                return;
            _nextTry = Time.unscaledTime + 1f;
            if (!(BaseGameNetworkManager.Singleton is MeshMapNetworkManager mesh) || !mesh.IsServer || mesh.OwnCell == null)
                return;
            if (!ServesThisCell(mesh.OwnZoneId))
            {
                _spawned = true; // another cell: nothing to do here
                return;
            }
            if (!mesh.IsServerReadyToInstantiateObjects())
                return;
            _spawned = true;
            if (System.Environment.GetEnvironmentVariable(ENV_UNCAPPED) == "1")
            {
                Application.targetFrameRate = -1;
                Logging.Log("Mesh", "Load generator: frame rate uncapped on this cell (frame time = work per frame)");
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
            MeshCell cell = mesh.OwnCell;
            // The cell's rectangle in its frame, inset so nobody spawns on the line (they may still wander into the band).
            float minX = cell.worldMinX - cell.frameOrigin.x + 5f, maxX = cell.worldMaxX - cell.frameOrigin.x - 5f;
            float minZ = cell.worldMinZ - cell.frameOrigin.z + 5f, maxZ = cell.worldMaxZ - cell.frameOrigin.z - 5f;
            Rect area = new Rect(minX, minZ, maxX - minX, maxZ - minZ);
            System.Random random = new System.Random(12345);
            int spawned = 0;
            for (int i = 0; i < _count; ++i)
            {
                Vector3 position = new Vector3(minX + (float)random.NextDouble() * (maxX - minX), 0f, minZ + (float)random.NextDouble() * (maxZ - minZ));
                PlayerCharacterData data = new PlayerCharacterData();
                data.SetNewPlayerCharacterData($"Load{_cell}{i:000}", classId, entityId, 0);
                data.Id = $"load-{_cell}-{i}";
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
                mesh.Assets.NetworkSpawn(spawnObj);
                entity.ForceMakeCaches();
                entity.FillEmptySlots();
                entity.CurrentHp = entity.MaxHp;
                entity.CurrentMp = entity.MaxMp;
                mesh.TrackSyntheticEntity(entity);
                SyntheticPlayerBrain brain = entity.gameObject.AddComponent<SyntheticPlayerBrain>();
                brain.Setup(entity, area, random.Next());
                _entities.Add(entity);
                spawned++;
            }
            Logging.Log("Mesh", $"Load generator: spawned {spawned} synthetic players on cell {_cell} over x [{minX:0}, {maxX:0}] z [{minZ:0}, {maxZ:0}]");
        }
    }

    /// <summary>A synthetic player's behaviour: wander, fight what is in reach, never die.</summary>
    public class SyntheticPlayerBrain : MonoBehaviour
    {
        private BasePlayerCharacterEntity _entity;
        private Rect _area;
        private System.Random _random;
        private float _nextThink;
        private float _nextAttack;
        private static readonly Collider[] s_hits = new Collider[32];

        public void Setup(BasePlayerCharacterEntity entity, Rect area, int seed)
        {
            _entity = entity;
            _area = area;
            _random = new System.Random(seed);
            _nextThink = Time.unscaledTime + (float)_random.NextDouble() * 3f;
        }

        private void Update()
        {
            if (_entity == null || _random == null || Time.unscaledTime < _nextThink)
                return;
            _nextThink = Time.unscaledTime + 2f + (float)_random.NextDouble() * 4f;
            if (_entity.CurrentHp < _entity.MaxHp / 3)
                _entity.CurrentHp = _entity.MaxHp; // a synthetic player is load, not a corpse
            Vector3 pos = _entity.EntityTransform.position;
            // Fight what is in reach: a monster, or another synthetic player (they are enemies to nobody, so only monsters bite).
            BaseCharacterEntity target = null;
            float best = float.MaxValue;
            int n = Physics.OverlapSphereNonAlloc(pos, 10f, s_hits);
            for (int i = 0; i < n; ++i)
            {
                BaseMonsterCharacterEntity monster = s_hits[i].GetComponentInParent<BaseMonsterCharacterEntity>();
                if (monster == null || monster == (object)_entity || monster.IsDead() || MeshIdentity.IsGhostEntity(monster))
                    continue;
                float d = Vector3.Distance(pos, monster.EntityTransform.position);
                if (d < best) { best = d; target = monster; }
            }
            if (target != null && Time.unscaledTime >= _nextAttack)
            {
                _nextAttack = Time.unscaledTime + 1.5f;
                _entity.SetTargetEntity(target);
                if (best > 2.5f)
                    _entity.PointClickMovement(target.EntityTransform.position);
                WeaponHandlingState state = WeaponHandlingState.None;
                _entity.Attack(ref state);
                return;
            }
            // Wander: a point up to 20 m away, kept inside the cell.
            float angle = (float)_random.NextDouble() * Mathf.PI * 2f;
            float dist = 5f + (float)_random.NextDouble() * 15f;
            Vector3 goal = pos + new Vector3(Mathf.Cos(angle) * dist, 0f, Mathf.Sin(angle) * dist);
            goal.x = Mathf.Clamp(goal.x, _area.xMin, _area.xMax);
            goal.z = Mathf.Clamp(goal.z, _area.yMin, _area.yMax);
            _entity.PointClickMovement(goal);
        }
    }
}
