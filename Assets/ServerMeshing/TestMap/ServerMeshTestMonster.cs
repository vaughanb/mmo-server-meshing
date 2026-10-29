// Server Meshing test rig — ServerMeshTestMonster: one standing monster at a chosen spot, a target for the patrol's
// swings at a cell edge (DESIGN.md 1.07).
//
// A cell process started with MESH_TEST_MONSTER=<x>,<z>[,<name>] spawns one monster at (x, z) (scene frame) when the
// point is in its own rectangle: the first monster prefab whose name contains <name> (default "Deer"), through the
// kit's spawn path with the nearest spawn area as its home. Its AI is switched off and it is put back whenever it moves
// (a hit deer runs all the same), it is healed whenever it drops below a third, and every hit it takes is logged (tag MeshTestMonster) with the attacker's
// id, so a run can count the hits a crossing's swings landed: once each, on the cell that owns the monster. Rig only:
// nothing here runs without the environment variable.
//
// MESH_TEST_MONSTER=<x>,<z>,<name>,free (DESIGN 1.23) spawns it as the kit would: its AI on, never put back or healed,
// so an aggressive one chases a player across a line and is handed over like any monster.
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public class ServerMeshTestMonster : MonoBehaviour
    {
        public const string ENV = "MESH_TEST_MONSTER";
        private Vector2 _spot;
        private string _name = "Deer";
        private bool _done;
        private float _nextTry;
        private BaseMonsterCharacterEntity _monster;
        private Vector3 _position;
        private float _nextPin;
        private int _hits;
        private bool _free;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoStart()
        {
            string value = System.Environment.GetEnvironmentVariable(ENV);
            if (string.IsNullOrEmpty(value) || FindFirstObjectByType<ServerMeshTestMonster>() != null)
                return;
            string[] parts = value.Split(',');
            if (parts.Length < 2
                || !float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x)
                || !float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float z))
            {
                Logging.LogError("MeshTestMonster", $"{ENV}={value} is not x,z[,name]; no monster spawned");
                return;
            }
            GameObject go = new GameObject("[Mesh] TestMonster");
            DontDestroyOnLoad(go);
            ServerMeshTestMonster spawner = go.AddComponent<ServerMeshTestMonster>();
            spawner._spot = new Vector2(x, z);
            if (parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2]))
                spawner._name = parts[2].Trim();
            spawner._free = parts.Length > 3 && string.Equals(parts[3].Trim(), "free", System.StringComparison.OrdinalIgnoreCase);
        }

        private void Update()
        {
            if (_monster != null)
            {
                if (_free)
                    return;
                if (!_monster.IsDead() && _monster.CurrentHp < _monster.MaxHp / 3)
                    _monster.CurrentHp = _monster.MaxHp;
                // Switching its activity off does not keep a hit deer from running: put it back where it was put.
                Vector3 offset = _monster.EntityTransform.position - _position;
                offset.y = 0f;
                if (offset.sqrMagnitude > 0.25f && Time.unscaledTime >= _nextPin)
                {
                    _nextPin = Time.unscaledTime + 0.25f;
                    _monster.Teleport(_position, _monster.EntityTransform.rotation, false);
                }
                return;
            }
            if (_done || Time.unscaledTime < _nextTry)
                return;
            _nextTry = Time.unscaledTime + 1f;
            if (!(BaseGameNetworkManager.Singleton is MeshMapNetworkManager mesh) || !mesh.IsServer || mesh.OwnCell == null || !mesh.IsServerReadyToInstantiateObjects())
                return;
            _done = true;
            Vector3 world = mesh.OwnCell.SceneToWorld(new Vector3(_spot.x, 0f, _spot.y)); // the spot is in the scene frame
            if (!mesh.OwnCell.ContainsWorld(world.x, world.z))
            {
                Logging.Log("MeshTestMonster", $"spot ({_spot.x}, {_spot.y}) (world {world.x}, {world.z}) is not on {mesh.OwnZoneId}");
                return;
            }
            Spawn(mesh);
        }

        private void Spawn(MeshMapNetworkManager mesh)
        {
            // A meshed copy of a monster (one with an add-on component, as the 8 km demo's are) before the kit's own of the
            // same name: the kit's has no ghost watch, so it never wakes for a player across the line (DESIGN 1.23).
            // The scene's spawn areas name the copies the map spawns; the game instance may hold only the kit's.
            System.Collections.Generic.List<BaseMonsterCharacterEntity> candidates = new System.Collections.Generic.List<BaseMonsterCharacterEntity>(GameInstance.MonsterCharacterEntities.Values);
            foreach (MonsterSpawnArea area in FindObjectsByType<MonsterSpawnArea>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (area.prefab != null)
                    candidates.Add(area.prefab);
                if (area.spawningPrefabs != null)
                    foreach (MonsterSpawnArea.SpawnPrefabData data in area.spawningPrefabs)
                        if (data != null && data.prefab != null)
                            candidates.Add(data.prefab);
            }
            BaseMonsterCharacterEntity prefab = null;
            foreach (BaseMonsterCharacterEntity candidate in candidates)
            {
                if (candidate == null || candidate.name.IndexOf(_name, System.StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (prefab == null || (!IsMeshed(prefab) && IsMeshed(candidate)))
                    prefab = candidate;
            }
            if (prefab == null)
            {
                Logging.LogError("MeshTestMonster", $"no monster prefab named like '{_name}'");
                return;
            }
            MonsterSpawnArea home = null;
            float best = float.MaxValue;
            foreach (MonsterSpawnArea area in FindObjectsByType<MonsterSpawnArea>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                float d = Vector2.Distance(new Vector2(area.transform.position.x, area.transform.position.z), _spot);
                if (d < best)
                {
                    best = d;
                    home = area;
                }
            }
            Vector3 position = new Vector3(_spot.x, 0f, _spot.y);
            if (Physics.Raycast(new Vector3(_spot.x, 1000f, _spot.y), Vector3.down, out RaycastHit hit, 2000f, GameInstance.Singleton.GetGameEntityGroundDetectionLayerMask()))
                position.y = hit.point.y;
            mesh.Assets.RegisterPrefab(prefab.Identity); // a spawn area's copy is registered only when its area spawns
            LiteNetLibIdentity spawnObj = mesh.Assets.GetObjectInstance(prefab.Identity.HashAssetId, position, Quaternion.identity);
            if (spawnObj == null)
            {
                Logging.LogError("MeshTestMonster", $"could not instance {prefab.name}");
                return;
            }
            BaseMonsterCharacterEntity entity = spawnObj.GetComponent<BaseMonsterCharacterEntity>();
            if (home != null)
                entity.SetSpawnArea(home, prefab, 1, position);
            entity.Level = 1;
            entity.Teleport(position, Quaternion.identity, false);
            entity.InitStats();
            if (!_free)
            {
                foreach (BaseMonsterActivityComponent activity in entity.GetComponents<BaseMonsterActivityComponent>())
                    activity.enabled = false; // a target that stays where it was put
            }
            mesh.Assets.NetworkSpawn(spawnObj);
            entity.CallRpcOnSpawned();
            entity.onReceivedDamage += OnReceivedDamage;
            _monster = entity;
            _position = position;
            Logging.Log("MeshTestMonster", $"{prefab.name} ({(IsMeshed(prefab) ? "meshed" : "kit")} prefab) spawned{(_free ? " free" : string.Empty)} at {position} on {mesh.OwnZoneId} (object {entity.ObjectId}, hp {entity.CurrentHp}/{entity.MaxHp})");
        }

        private static bool IsMeshed(BaseMonsterCharacterEntity prefab)
        {
            foreach (MonoBehaviour behaviour in prefab.GetComponents<MonoBehaviour>())
            {
                if (behaviour != null && behaviour.GetType().Namespace == typeof(MeshMapNetworkManager).Namespace)
                    return true;
            }
            return false;
        }

        private void OnReceivedDamage(DamageableEntity target, HitBoxPosition position, Vector3 fromPosition, EntityInfo instigator, CombatAmountType combatAmountType, int totalDamage, CharacterItem weapon, BaseSkill skill, int skillLevel, CharacterBuff buff, bool isDamageOverTime)
        {
            _hits++;
            Logging.Log("MeshTestMonster", $"{System.DateTime.Now:HH:mm:ss.fff} hit #{_hits} {combatAmountType} {totalDamage} by {instigator.Id} hp {target.CurrentHp}");
        }
    }
}
