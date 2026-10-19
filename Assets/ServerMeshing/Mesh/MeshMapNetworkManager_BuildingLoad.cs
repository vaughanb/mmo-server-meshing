// Server Meshing add-on — a Dynamic map's cell loads its buildings once the central has answered its layout, and only
// the trees whose root stands in its owned rectangle (Slice 4, slices/slice4_design.md decisions 3-5). The kit loads
// buildings in PreSpawnEntities, before the cluster client starts; a cell of a Dynamic map does no building database
// work before its layout gate opens (the base channel's rows are read once, by rectangle: a child takes its share, a
// base restarted with children live takes its kept rectangle). Invariant: no building write before this process's own
// GetBuildings returned, because the database manager's per-channel cache is created by the first write or read.
// While the load runs, placements are held (the layout gate's queue) and transfers are refused. Static maps: the kit's
// load, byte-identical.
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class MeshMapNetworkManager
    {
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        private enum BuildingLoadState { NotStarted, Loading, Done }
        private BuildingLoadState _buildingLoad;
        private readonly Dictionary<string, BuildingEntity> _sceneBuildings = new Dictionary<string, BuildingEntity>(System.StringComparer.Ordinal);

        /// <summary>BuildingsLoaded: a Static map's cell always; a Dynamic map's cell once its load has run (decision 4).</summary>
        private bool BuildingsLoaded => !IsDynamicMap || _buildingLoad == BuildingLoadState.Done;

        /// <summary>HoldsPlacements: a placement waits while the layout gate is closed or the buildings are still loading (decision 5).</summary>
        private bool HoldsPlacements => IsLayoutGated || !BuildingsLoaded;

        private void BuildingLoadOnStartServer()
        {
            _buildingLoad = BuildingLoadState.NotStarted;
            _sceneBuildings.Clear();
        }

        /// <summary>
        /// Decision 3: on a Dynamic map the kit's load does not run here; the in-scene buildings are remembered and get
        /// their updater only after the read (an updater's first dirty save would create the database manager's cache key
        /// before this cell's load and hide every placed row). Static maps: the kit's.
        /// </summary>
        protected override async UniTask PreSpawnEntities()
        {
            if (!IsDynamicMap)
            {
                await base.PreSpawnEntities();
                return;
            }
            _sceneBuildings.Clear();
            if (IsAllocate)
                return;
            BuildingEntity[] inScene = FindObjectsByType<BuildingEntity>(FindObjectsSortMode.None);
            for (int i = 0; i < inScene.Length; ++i)
            {
                if (inScene[i] != null && inScene[i].IsSceneObject)
                    _sceneBuildings[inScene[i].Id] = inScene[i];
            }
            await UniTask.CompletedTask;
        }

        /// <summary>LayoutTick's hook on the first open tick (decision 4): the load, once per process.</summary>
        private void BuildingsOnGateOpen()
        {
            if (!IsDynamicMap || _buildingLoad != BuildingLoadState.NotStarted)
                return;
            _buildingLoad = BuildingLoadState.Loading;
            LoadBuildingsRoutine().Forget();
        }

        private async UniTaskVoid LoadBuildingsRoutine()
        {
            try
            {
                await LoadBuildings();
            }
            catch (System.Exception ex)
            {
                Logging.LogException("Mesh", ex);
            }
            finally
            {
                _buildingLoad = BuildingLoadState.Done;
                _sceneBuildings.Clear();
            }
        }

        private static bool IsStoragePrefab(int entityId)
        {
            return GameInstance.BuildingEntities.TryGetValue(entityId, out BuildingEntity prefab) && prefab is StorageEntity;
        }

        private static bool IsKnownBuildingPrefab(int entityId)
        {
            return GameInstance.BuildingEntities.TryGetValue(entityId, out BuildingEntity prefab) && prefab != null;
        }

        /// <summary>
        /// The load (decision 4): the kit's loop by channel, partitioned into this cell's scene copies, other cells' scene
        /// rows (skipped), rows already here, and placed trees, of which the ones rooted in the owned rectangle are created
        /// parent before child with each storage loaded before its entity; then the kit's tail for the in-scene buildings
        /// no row matched, and the updaters.
        /// </summary>
        private async UniTask LoadBuildings()
        {
            string cell = LayoutCellId;
            if (IsAllocate)
            {
                Logging.Log("Mesh", $"Buildings loaded on {cell}: 0 placed in 0 tree(s) and 0 scene, 0 tree(s) owned elsewhere, 0 scene row(s) of other cells skipped, 0 already here (allocated map, no read)");
                return;
            }
            string channel = BuildingPersistenceChannelId;
            List<BuildingSaveData> rows = new List<BuildingSaveData>();
            if (!IsInstanceMap())
            {
                DatabaseApiResult<BuildingsResp> resp;
                do
                {
                    resp = await DatabaseClient.GetBuildingsAsync(new GetBuildingsReq()
                    {
                        ChannelId = channel,
                        MapName = CurrentMapInfo.Id,
                    });
                } while (!resp.IsSuccess);
                if (resp.Response.List != null)
                    rows = resp.Response.List;
            }
            Dictionary<string, BuildingSaveData> byId = new Dictionary<string, BuildingSaveData>(System.StringComparer.Ordinal);
            List<MeshBuildingNode> nodes = new List<MeshBuildingNode>(rows.Count);
            for (int i = 0; i < rows.Count; ++i)
            {
                BuildingSaveData row = rows[i];
                if (row == null || string.IsNullOrEmpty(row.Id))
                    continue;
                byId[row.Id] = row;
                Vector3 pos = row.Position;
                nodes.Add(new MeshBuildingNode { id = row.Id, parentId = row.ParentId, isSceneObject = row.IsSceneObject, x = pos.x, z = pos.z });
            }
            HashSet<string> ownedIds = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (IBuildingSaveData owned in ServerBuildingHandlers.GetBuildings())
            {
                if (owned != null && !string.IsNullOrEmpty(owned.Id))
                    ownedIds.Add(owned.Id);
            }
            MeshBuildingPartition part = MeshBuildingTrees.Partition(nodes, _sceneBuildings.Keys, ownedIds);

            // This cell's scene copies with a row: the kit's treatment.
            int scene = 0;
            for (int i = 0; i < part.sceneMatched.Count; ++i)
            {
                string id = part.sceneMatched[i];
                BuildingSaveData row = byId[id];
                BuildingEntity entity = _sceneBuildings[id];
                _sceneBuildings.Remove(id);
                if (entity == null)
                    continue;
                if (row.CurrentHp <= 0)
                {
                    entity.NetworkDestroy();
                    continue;
                }
                entity.CurrentHp = row.CurrentHp;
                ServerBuildingHandlers.AddBuilding(entity.Id, entity);
                scene++;
                if (entity is StorageEntity)
                    await LoadStorageRoutine(new StorageId(StorageType.Building, entity.Id));
            }

            // Placed trees rooted in the owned rectangle: parent before child, a storage before its entity.
            int placed = 0, ours = 0, elsewhere = 0;
            for (int t = 0; t < part.trees.Count; ++t)
            {
                MeshBuildingTree tree = part.trees[t];
                BuildingSaveData root = byId[tree.rootId];
                MeshCell owner = world != null && OwnCell != null ? world.OwnerOf(OwnCell, root.Position) : null;
                if (owner == null || !MeshCell.Same(owner, OwnCell))
                {
                    elsewhere++;
                    continue;
                }
                ours++;
                for (int i = 0; i < tree.ids.Count; ++i)
                {
                    BuildingSaveData row = byId[tree.ids[i]];
                    if (!IsKnownBuildingPrefab(row.EntityId))
                    {
                        Logging.LogWarning("Mesh", $"Building {row.Id} of channel {channel} not created on {cell}: unknown building {row.EntityId}");
                        continue;
                    }
                    if (IsStoragePrefab(row.EntityId))
                        await LoadStorageRoutine(new StorageId(StorageType.Building, row.Id));
                    if (ServerBuildingHandlers.TryGetBuilding(row.Id, out IBuildingSaveData _))
                        continue; // placed here while this load ran
                    BuildingEntity entity = await CreateBuildingEntity(row, true);
                    if (entity != null)
                        placed++;
                }
            }

            // The in-scene buildings no row matched: the kit's tail (their rows under the persistence channel).
            List<UniTask> tail = new List<UniTask>();
            foreach (BuildingEntity initializing in _sceneBuildings.Values)
            {
                if (initializing == null)
                    continue;
                initializing.InitSceneObject();
                ServerBuildingHandlers.AddBuilding(initializing.Id, initializing);
                scene++;
                if (IsInstanceMap())
                    continue;
                tail.Add(DatabaseClient.CreateBuildingAsync(new CreateBuildingReq()
                {
                    ChannelId = channel,
                    MapName = CurrentMapInfo.Id,
                    BuildingData = initializing.CloneTo(new BuildingSaveData()),
                }));
                if (initializing is StorageEntity)
                {
                    StorageId storageId = new StorageId(StorageType.Building, initializing.Id);
                    tail.Add(DatabaseClient.UpdateStorageItemsAsync(new UpdateStorageItemsReq()
                    {
                        StorageType = storageId.storageType,
                        StorageOwnerId = storageId.storageOwnerId,
                        StorageItems = ServerStorageHandlers.GetStorageItems(storageId),
                    }));
                }
            }
            await UniTask.WhenAll(tail);
            _sceneBuildings.Clear();
            // The updaters of every in-scene building, after the read (decision 3's invariant).
            BuildingEntity[] inScene = FindObjectsByType<BuildingEntity>(FindObjectsSortMode.None);
            for (int i = 0; i < inScene.Length; ++i)
            {
                BuildingEntity b = inScene[i];
                if (b != null && b.IsSceneObject && b.Identity != null && !b.Identity.IsDestroyed && !b.TryGetComponent<BuildingDataUpdater>(out _))
                    b.gameObject.AddComponent<BuildingDataUpdater>();
            }
            Logging.Log("Mesh", $"Buildings loaded on {cell}: {placed} placed in {ours} tree(s) and {scene} scene, {elsewhere} tree(s) owned elsewhere, {part.sceneSkipped.Count} scene row(s) of other cells skipped, {part.alreadyHere.Count} already here (channel {channel}, {rows.Count} row(s))");
        }
#else
        private void BuildingLoadOnStartServer() { }
#endif
    }
}
