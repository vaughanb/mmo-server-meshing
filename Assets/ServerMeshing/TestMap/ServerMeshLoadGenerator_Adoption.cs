// Server Meshing test rig — the load generator's seam for the synthetic adopter (Slice 2b, slices/slice2b_design.md
// decision 13): which players are the generator's, how each moves, spawning one again on another cell, and confining
// the wander areas when the cell's owned rectangle shrinks. Nothing here runs unless ServerMeshSyntheticAdopter asks.
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class ServerMeshLoadGenerator
    {
        /// <summary>Owns is true when entity is one of the synthetic players this generator spawned.</summary>
        public bool Owns(BasePlayerCharacterEntity entity) => entity != null && _entities.Contains(entity);

        /// <summary>Motion is how this generator's players move (read from MESH_LOAD_MOTION when they spawned).</summary>
        public ServerMeshLoadMotionKind Motion => _motion;

        /// <summary>Fight is whether this generator's players fight (MESH_LOAD_FIGHT when they spawned).</summary>
        public bool Fight => _fight;

        /// <summary>Walk is the loop that moves this generator's brainless players, or null for steered ones.</summary>
        public ServerMeshKinematicWalk Walk => _walk;

        /// <summary>
        /// SpawnSynthetic spawns one server-owned player as the generator does (new-character data of classId and
        /// entityId, at level, full health), tracked by the mesh as a synthetic entity; null (logged) when the game
        /// database has no such entity.
        /// </summary>
        public static BasePlayerCharacterEntity SpawnSynthetic(MeshMapNetworkManager mesh, string id, string name, int classId, int entityId, int level, Vector3 position, float yaw)
        {
            PlayerCharacterData data = new PlayerCharacterData();
            data.SetNewPlayerCharacterData(name, classId, entityId, 0);
            data.Id = id;
            data.Level = level > 0 ? level : 5;
            data.CurrentMapName = mesh.OwnMapId;
            data.CurrentPosition = position;
            LiteNetLibIdentity spawnObj = mesh.Assets.GetObjectInstance(GameInstance.GetPlayerCharacterEntityHashAssetId(data.EntityId, out int metaDataId), position, Quaternion.Euler(0f, yaw, 0f));
            if (spawnObj == null)
                return null;
            BasePlayerCharacterEntity entity = spawnObj.GetComponent<BasePlayerCharacterEntity>();
            if (entity == null)
            {
                Destroy(spawnObj.gameObject);
                return null;
            }
            GameInstance.SetupByMetaData(entity, metaDataId);
            data.CloneTo(entity);
            mesh.Assets.NetworkSpawn(ServerMeshSpaceTags.Tag(spawnObj, false));
            entity.ForceMakeCaches();
            entity.FillEmptySlots();
            entity.CurrentHp = entity.MaxHp;
            entity.CurrentMp = entity.MaxMp;
            mesh.TrackSyntheticEntity(entity);
            return entity;
        }

        /// <summary>
        /// ConfineTo intersects every wander area this generator gave out with area (scene frame, y is z): the brains'
        /// and the walk loop's. An area that would become empty is left as it was.
        /// </summary>
        public void ConfineTo(Rect area)
        {
            if (_walk != null && ServerMeshSyntheticAdopter.TryIntersect(_walk.Area, area, out Rect walkArea))
                _walk.Setup(walkArea, _fight);
        }
    }

    public partial class SyntheticPlayerBrain
    {
        /// <summary>Area is the wander area (scene frame, y is z).</summary>
        public Rect Area => _area;

        /// <summary>Fights is whether this brain attacks monsters in reach.</summary>
        public bool Fights => _fight;

        /// <summary>ConfineTo makes area the wander area and has the brain pick its next goal at once, inside it.</summary>
        public void ConfineTo(Rect area)
        {
            _area = area;
            _nextThink = 0f;
        }
    }
}
