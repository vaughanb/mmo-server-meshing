// Server Meshing add-on — a building tree follows its root's ground (Slice 4, slices/slice4_design.md decisions 8-13).
// Once a second on a Dynamic map, every owned root (a non-scene building whose parent is not here) whose position
// another cell of the map owns in the live layout is released to that cell, in a fixed order: the handlers dropped
// (the kit then refuses storage opens and item moves), each storage closed and its users waited out, the saver
// stopped, every node force-saved (a storage's rows too, then its cache evicted), every node demoted in place to a
// puppet, then BuildingTransfer (131). The receiver loads each storage first, promotes its puppet in place or creates
// the building without a database write (initialize: true), and answers BuildingTransferReply (132); a failure or a
// lost reply re-promotes the holder's copies from the clones it saved. Both drain gates count the roots still here
// and the releases in flight. Static maps: nothing here runs.
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class MeshMapNetworkManager
    {
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        /// <summary>One tree being released: its entities and the clones saved before the send (decision 9).</summary>
        private sealed class BuildingRelease
        {
            public string rootId;
            public string toZone;
            public List<BuildingEntity> entities;
            public List<BuildingSaveData> clones = new List<BuildingSaveData>();
            public bool sent;
            public float sentAt;
            public bool ended;
        }

        private readonly Dictionary<string, BuildingRelease> _buildingReleases = new Dictionary<string, BuildingRelease>(System.StringComparer.Ordinal);
        private readonly Dictionary<string, float> _buildingBackoff = new Dictionary<string, float>(System.StringComparer.Ordinal);
        private readonly HashSet<string> _buildingReceiving = new HashSet<string>(System.StringComparer.Ordinal);
        private readonly List<string> _buildingScratch = new List<string>();
        private readonly List<KeyValuePair<float, BuildingEntity>> _buildingCandidates = new List<KeyValuePair<float, BuildingEntity>>();
        private readonly Dictionary<BuildingEntity, MeshCell> _buildingCandidateOwner = new Dictionary<BuildingEntity, MeshCell>();

        /// <summary>Releases this cell has started and not settled (decision 6: part of the drain state).</summary>
        private int BuildingTransfersInFlight => _buildingReleases.Count;
        private int BuildingTransferCap => world != null ? Mathf.Max(1, world.dynamicSettings.buildingTransfersInFlight) : 2;
        private float BuildingStorageCloseSeconds => world != null ? Mathf.Max(0f, world.dynamicSettings.buildingStorageCloseSeconds) : 5f;

        private void BuildingTransferOnStartServer()
        {
            _buildingReleases.Clear();
            _buildingBackoff.Clear();
            _buildingReceiving.Clear();
        }

        private static void LogBuildingTransfer(string rootId, string from, string to, string what)
        {
            Logging.Log("Mesh", $"Building transfer {rootId} zone {from}->{to}: {what}");
        }

        private bool IsBuildingRoot(BuildingEntity entity)
        {
            return string.IsNullOrEmpty(entity.ParentId) || !ServerBuildingHandlers.TryGetBuilding(entity.ParentId, out IBuildingSaveData _);
        }

        /// <summary>Decision 12: roots owned here rooted in the given-away rectangle (no hysteresis) plus releases toward its owner.</summary>
        private int CountDrainBuildings(string toZone)
        {
            int n = 0;
            if (_splitShrunk && OwnCell != null)
            {
                foreach (IBuildingSaveData b in ServerBuildingHandlers.GetBuildings())
                {
                    BuildingEntity entity = b as BuildingEntity;
                    if (entity == null || entity.IsSceneObject || entity.Identity == null || entity.Identity.IsDestroyed || !IsBuildingRoot(entity))
                        continue;
                    Vector3 w = OwnCell.SceneToWorld(entity.EntityTransform.position);
                    if (_splitGivenAway.Contains(w.x, w.z))
                        n++;
                }
            }
            foreach (BuildingRelease r in _buildingReleases.Values)
            {
                if (string.Equals(r.toZone, toZone, System.StringComparison.OrdinalIgnoreCase))
                    n++;
            }
            return n;
        }

        /// <summary>StaticHandoffTick's hook (decision 8): keep demoted copies in flight alive, time out lost replies, start releases.</summary>
        private void BuildingTransferTick(float now)
        {
            if (!BuildingsLoaded)
                return;
            _buildingScratch.Clear();
            foreach (BuildingRelease r in _buildingReleases.Values)
            {
                for (int i = 0; i < r.entities.Count; ++i)
                {
                    BuildingEntity e = r.entities[i];
                    if (e != null && _ghostStatics.TryGetValue(e.Id, out GhostStatic puppet))
                        puppet.lastSeen = now;
                }
                if (r.sent && now - r.sentAt > HandoffAckGuardSeconds)
                    _buildingScratch.Add(r.rootId);
            }
            for (int i = 0; i < _buildingScratch.Count; ++i)
            {
                if (_buildingReleases.TryGetValue(_buildingScratch[i], out BuildingRelease lost))
                    RepromoteBuildings(lost, "no reply within the ack guard").Forget();
            }
            _buildingScratch.Clear();
            int cap = BuildingTransferCap;
            if (_buildingReleases.Count >= cap || OwnCell == null || world == null)
                return;
            _buildingCandidates.Clear();
            _buildingCandidateOwner.Clear();
            foreach (IBuildingSaveData b in ServerBuildingHandlers.GetBuildings())
            {
                BuildingEntity entity = b as BuildingEntity;
                if (entity == null || entity.IsSceneObject || entity.Identity == null || entity.Identity.IsDestroyed || !IsBuildingRoot(entity))
                    continue;
                if (_buildingReleases.ContainsKey(entity.Id))
                    continue;
                bool inBackoff = _buildingBackoff.TryGetValue(entity.Id, out float until) && now < until;
                Vector3 pos = entity.EntityTransform.position;
                MeshCell owner = world.OwnerOf(OwnCell, pos);
                Vector3 w = OwnCell.SceneToWorld(pos);
                bool otherOwner = owner != null && !MeshCell.Same(owner, OwnCell)
                    && string.Equals(owner.mapId, OwnCell.mapId, System.StringComparison.Ordinal) && owner.ContainsWorld(w.x, w.z);
                bool linked = otherOwner && IsNeighbourLinked(owner.id);
                bool settled = otherOwner && IsNeighbourSettled(owner.id);
                if (!MeshBuildingTransferRules.MayStart(true, true, otherOwner, linked, settled, inBackoff, _buildingReleases.Count, cap))
                    continue;
                _buildingCandidates.Add(new KeyValuePair<float, BuildingEntity>(-OwnCell.DistanceToEdge(pos), entity));
                _buildingCandidateOwner[entity] = owner;
            }
            _buildingCandidates.Sort((a, b) => a.Key.CompareTo(b.Key));
            for (int i = 0; i < _buildingCandidates.Count && _buildingReleases.Count < cap; ++i)
            {
                BuildingEntity root = _buildingCandidates[i].Value;
                StartBuildingRelease(root, _buildingCandidateOwner[root].id).Forget();
            }
            _buildingCandidates.Clear();
            _buildingCandidateOwner.Clear();
        }

        /// <summary>The tree under a root, parent before child, from the handlers.</summary>
        private List<BuildingEntity> CollectBuildingTree(BuildingEntity root)
        {
            List<BuildingEntity> all = new List<BuildingEntity>();
            foreach (IBuildingSaveData b in ServerBuildingHandlers.GetBuildings())
            {
                if (b is BuildingEntity e && e != null && !e.IsSceneObject && e.Identity != null && !e.Identity.IsDestroyed)
                    all.Add(e);
            }
            List<BuildingEntity> tree = new List<BuildingEntity> { root };
            HashSet<string> seen = new HashSet<string>(System.StringComparer.Ordinal) { root.Id };
            for (int i = 0; i < tree.Count; ++i)
            {
                string parentId = tree[i].Id;
                for (int j = 0; j < all.Count; ++j)
                {
                    if (string.Equals(all[j].ParentId, parentId, System.StringComparison.Ordinal) && seen.Add(all[j].Id))
                        tree.Add(all[j]);
                }
            }
            return tree;
        }

        private static bool Alive(BuildingEntity e) => e != null && e.Identity != null && !e.Identity.IsDestroyed;

        private bool StorageMoveInFlight(StorageId storageId)
        {
            foreach (KeyValuePair<string, ConcurrentCollections.ConcurrentHashSet<StorageId>> kv in storageUsers)
            {
                if (kv.Value != null && kv.Value.Contains(storageId))
                    return true;
            }
            return false;
        }

        /// <summary>Decision 9 step 1 for a storage node: close it for the players in reach, wait for its users and in-flight moves.</summary>
        private async UniTask WaitForStorageUsers(BuildingRelease r, StorageEntity storage)
        {
            StorageId storageId = new StorageId(StorageType.Building, storage.Id);
            float reach = storage.GetActivatableDistance() + 5f;
            Vector3 at = storage.EntityTransform.position;
            foreach (IPlayerCharacterData data in ServerUserHandlers.GetPlayerCharacters())
            {
                if (data is BasePlayerCharacterEntity player && player != null && Vector3.Distance(player.EntityTransform.position, at) <= reach)
                    ServerStorageHandlers.CloseStorage(player.ConnectionId, storageId);
            }
            float start = Time.unscaledTime;
            while (true)
            {
                bool open = ServerStorageHandlers.IsStorageEntityOpen(storage);
                bool moving = StorageMoveInFlight(storageId);
                float waited = Time.unscaledTime - start;
                if (MeshBuildingTransferRules.StorageReleased(open, moving, waited, BuildingStorageCloseSeconds))
                {
                    if (open || moving)
                        Logging.LogWarning("Mesh", $"Building transfer {r.rootId} zone {OwnZoneId}->{r.toZone}: storage {storage.Id} still in use after {waited:0} s");
                    return;
                }
                await UniTask.Yield();
            }
        }

        private void StopBuildingSaver(BuildingEntity entity)
        {
            if (entity.TryGetComponent(out BuildingDataUpdater updater))
            {
                MapNetworkManagerDataUpdater.BuildingDataUpdaters.Remove(updater);
                Destroy(updater);
            }
            DataUpdater.BuildingDataSaved(entity.Id);
        }

        /// <summary>Decision 9: the holder's release of one tree, in order; any failure re-promotes from the clones.</summary>
        private async UniTaskVoid StartBuildingRelease(BuildingEntity root, string toZone)
        {
            BuildingRelease r = new BuildingRelease { rootId = root.Id, toZone = toZone, entities = CollectBuildingTree(root) };
            _buildingReleases[r.rootId] = r;
            try
            {
                // 1. Refuse new users, close and wait out the storages.
                for (int i = 0; i < r.entities.Count; ++i)
                    ServerBuildingHandlers.RemoveBuilding(r.entities[i].Id);
                for (int i = 0; i < r.entities.Count; ++i)
                {
                    if (r.entities[i] is StorageEntity storage && Alive(storage))
                        await WaitForStorageUsers(r, storage);
                }
                // 2. Stop the saver, force-save every node (its storage too), evict the storage cache.
                for (int i = 0; i < r.entities.Count; ++i)
                {
                    if (Alive(r.entities[i]))
                        StopBuildingSaver(r.entities[i]);
                }
                for (int i = 0; i < r.entities.Count; ++i)
                {
                    BuildingEntity e = r.entities[i];
                    if (!Alive(e))
                    {
                        await RepromoteBuildings(r, "a building went away before its save");
                        return;
                    }
                    BuildingSaveData clone = e.CloneTo(new BuildingSaveData());
                    bool saved = await WaitAndForceSaveBuilding(TransactionUpdateBuildingState.Building, clone);
                    DataUpdater.BuildingDataSaved(e.Id);
                    if (!saved)
                    {
                        LogBuildingTransfer(r.rootId, OwnZoneId, toZone, "save failed");
                        await RepromoteBuildings(r, "save failed");
                        return;
                    }
                    r.clones.Add(clone);
                    if (e is StorageEntity)
                    {
                        StorageId storageId = new StorageId(StorageType.Building, e.Id);
                        bool storageSaved = await WaitAndSaveStorage(storageId, ServerStorageHandlers.GetStorageItems(storageId), false);
                        if (!storageSaved)
                        {
                            LogBuildingTransfer(r.rootId, OwnZoneId, toZone, "save failed");
                            await RepromoteBuildings(r, "storage save failed");
                            return;
                        }
                        ServerStorageHandlers.GetAllStorageItems().Remove(storageId);
                    }
                }
                // 3. Demote every node in place.
                List<string> ids = new List<string>(r.entities.Count);
                for (int i = 0; i < r.entities.Count; ++i)
                {
                    BuildingEntity e = r.entities[i];
                    if (!Alive(e))
                    {
                        await RepromoteBuildings(r, "a building went away before it was demoted");
                        return;
                    }
                    MeshIdentity id = MeshIdentity.GetOrAdd(e);
                    if (string.IsNullOrEmpty(id.MeshEntityId))
                        id.MeshEntityId = e.Id;
                    DemoteStaticOnSend(e, id, e.Id, GhostKind.Building, toZone);
                    e.RemainsLifeTime = float.MaxValue;
                    e.CreatorId = string.Empty;
                    ids.Add(e.Id);
                }
                RememberDemotedTree(r.rootId, ids);
                // 4. Send.
                if (!_linkClients.TryGetValue(toZone, out MeshLinkClient link) || !link.IsConnected)
                {
                    await RepromoteBuildings(r, "link lost");
                    return;
                }
                link.SendBuildingTransfer(new BuildingTransferMessage { rootId = r.rootId, nodes = r.clones.ToArray() });
                r.sent = true;
                r.sentAt = Time.unscaledTime;
                LogBuildingTransfer(r.rootId, OwnZoneId, toZone, $"sent ({r.clones.Count} node(s))");
            }
            catch (System.Exception ex)
            {
                Logging.LogException("Mesh", ex);
                if (_buildingReleases.ContainsKey(r.rootId))
                    await RepromoteBuildings(r, "error");
            }
        }

        /// <summary>Copies a node's save data onto an entity (its id stays).</summary>
        private static void ApplyBuildingData(BuildingEntity entity, BuildingSaveData data)
        {
            entity.ParentId = data.ParentId ?? string.Empty;
            entity.CurrentHp = data.CurrentHp;
            entity.RemainsLifeTime = data.RemainsLifeTime;
            entity.IsLocked = data.IsLocked;
            entity.LockPassword = data.LockPassword;
            entity.CreatorId = data.CreatorId ?? string.Empty;
            entity.CreatorName = data.CreatorName ?? string.Empty;
            entity.ExtraData = data.ExtraData;
        }

        /// <summary>A puppet (or demoted copy) becomes the real building in place: ours, in the handlers, saved again.</summary>
        private void PromoteBuildingInPlace(BuildingEntity entity, BuildingSaveData data)
        {
            string meshId = entity.Id;
            if (_ghostStatics.TryGetValue(meshId, out GhostStatic puppet) && ReferenceEquals(puppet.entity, entity))
            {
                _ghostStatics.Remove(meshId);
                if (puppet.hitHook != null)
                    entity.onReceiveDamage -= puppet.hitHook;
            }
            ForgetDemotedTreeOf(meshId);
            GhostSuppression.Unghostify(entity, this, null);
            entity.IsInvincible = false;
            ApplyBuildingData(entity, data);
            MeshIdentity id = MeshIdentity.GetOrAdd(entity);
            id.MeshEntityId = meshId;
            id.IsGhost = false;
            id.OwnerZoneId = OwnZoneId;
            id.LastHandoffTime = Time.unscaledTime;
            ServerBuildingHandlers.AddBuilding(entity.Id, entity);
            if (!entity.TryGetComponent<BuildingDataUpdater>(out _))
                entity.gameObject.AddComponent<BuildingDataUpdater>();
            TrackOwnedStatic(entity);
        }

        /// <summary>Decision 9 step 5's failure path: every node ours again, from its clone; a gone node re-created.</summary>
        private async UniTask RepromoteBuildings(BuildingRelease r, string reason)
        {
            if (r.ended)
                return;
            r.ended = true;
            _buildingReleases.Remove(r.rootId);
            _buildingBackoff[r.rootId] = Time.unscaledTime + staticHandoffRetrySeconds;
            ForgetDemotedTreeOf(r.rootId);
            for (int i = 0; i < r.entities.Count; ++i)
            {
                BuildingEntity e = r.entities[i];
                BuildingSaveData clone = i < r.clones.Count ? r.clones[i] : null;
                bool storage = e is StorageEntity || (clone != null && IsStoragePrefab(clone.EntityId));
                string id = clone != null ? clone.Id : (e != null ? e.Id : null);
                if (string.IsNullOrEmpty(id))
                    continue;
                if (storage)
                {
                    StorageId storageId = new StorageId(StorageType.Building, id);
                    ServerStorageHandlers.GetAllStorageItems().Remove(storageId);
                    await LoadStorageRoutine(storageId);
                }
                if (clone == null)
                {
                    // Never saved: still ours in truth; only the handlers (and perhaps the saver) were touched.
                    if (!Alive(e))
                        continue;
                    ServerBuildingHandlers.AddBuilding(e.Id, e);
                    if (!e.TryGetComponent<BuildingDataUpdater>(out _))
                        e.gameObject.AddComponent<BuildingDataUpdater>();
                    continue;
                }
                if (!Alive(e))
                {
                    if (IsKnownBuildingPrefab(clone.EntityId) && !ServerBuildingHandlers.TryGetBuilding(clone.Id, out IBuildingSaveData _))
                        await CreateBuildingEntity(clone, true);
                    continue;
                }
                PromoteBuildingInPlace(e, clone);
            }
            LogBuildingTransfer(r.rootId, OwnZoneId, r.toZone, $"released ({reason})");
        }

        internal void OnBuildingTransferReply(long connectionId, BuildingTransferReplyMessage msg)
        {
            if (!IsServer || string.IsNullOrEmpty(msg.rootId))
                return;
            string fromZone = _neighbourZoneByConnection.TryGetValue(connectionId, out string z) ? z : "?";
            if (!_buildingReleases.TryGetValue(msg.rootId, out BuildingRelease r) || !r.sent
                || !string.Equals(r.toZone, fromZone, System.StringComparison.OrdinalIgnoreCase))
                return;
            if (msg.ok)
            {
                r.ended = true;
                _buildingReleases.Remove(msg.rootId);
                float now = Time.unscaledTime;
                for (int i = 0; i < r.entities.Count; ++i)
                {
                    BuildingEntity e = r.entities[i];
                    if (e != null && _ghostStatics.TryGetValue(e.Id, out GhostStatic puppet))
                        puppet.lastSeen = now; // an ordinary puppet from here on: the new owner's snapshots keep it, or it expires
                }
                LogBuildingTransfer(msg.rootId, OwnZoneId, fromZone, "done");
                return;
            }
            RepromoteBuildings(r, string.IsNullOrEmpty(msg.reason) ? "refused" : msg.reason).Forget();
        }

        internal void OnBuildingTransfer(long connectionId, BuildingTransferMessage msg)
        {
            if (!IsServer || msg.nodes == null || msg.nodes.Length == 0)
                return;
            ReceiveBuildingTransfer(connectionId, msg).Forget();
        }

        /// <summary>Decision 10: the receiver's side, whole tree refused or taken; each storage loaded before its entity.</summary>
        private async UniTaskVoid ReceiveBuildingTransfer(long connectionId, BuildingTransferMessage msg)
        {
            string fromZone = _neighbourZoneByConnection.TryGetValue(connectionId, out string z) ? z : "?";
            _linkClients.TryGetValue(fromZone, out MeshLinkClient reply);
            string rootId = msg.rootId;
            Vector3 offset = OffsetFrom(connectionId);
            for (int i = 0; i < msg.nodes.Length; ++i)
            {
                if (msg.nodes[i] != null)
                    msg.nodes[i].Position = (Vector3)msg.nodes[i].Position + offset;
            }
            BuildingSaveData root = msg.nodes[0];
            bool ownerHere = root != null && OwnCell != null && world != null && MeshCell.Same(world.OwnerOf(OwnCell, root.Position), OwnCell);
            int unknown = -1;
            for (int i = 0; i < msg.nodes.Length && unknown < 0; ++i)
            {
                if (msg.nodes[i] == null || !IsKnownBuildingPrefab(msg.nodes[i].EntityId))
                    unknown = i;
            }
            string refuse = MeshBuildingTransferRules.RefuseReason(IsLayoutGated, !BuildingsLoaded, ownerHere, unknown,
                unknown >= 0 && msg.nodes[unknown] != null ? msg.nodes[unknown].EntityId : 0);
            if (refuse == null && !_buildingReceiving.Add(rootId))
                return; // the same tree is being taken already; the sender resends after its guard if the reply is lost
            if (refuse != null)
            {
                reply?.SendBuildingTransferReply(new BuildingTransferReplyMessage { rootId = rootId, ok = false, reason = refuse });
                LogBuildingTransfer(rootId, fromZone, OwnZoneId, $"failed ({refuse})");
                return;
            }
            int promoted = 0, spawned = 0, owned = 0;
            try
            {
                for (int i = 0; i < msg.nodes.Length; ++i)
                {
                    BuildingSaveData node = msg.nodes[i];
                    if (IsStoragePrefab(node.EntityId))
                    {
                        StorageId storageId = new StorageId(StorageType.Building, node.Id);
                        ServerStorageHandlers.GetAllStorageItems().Remove(storageId);
                        await LoadStorageRoutine(storageId);
                    }
                    bool ownedAlready = ServerBuildingHandlers.TryGetBuilding(node.Id, out IBuildingSaveData existing);
                    bool puppetHere = _ghostStatics.TryGetValue(node.Id, out GhostStatic puppet) && puppet.kind == GhostKind.Building
                        && puppet.entity is BuildingEntity puppetBuilding && Alive(puppetBuilding);
                    switch (MeshBuildingTransferRules.Receive(ownedAlready, puppetHere))
                    {
                        case MeshBuildingReceipt.OwnedAlready:
                            if (existing is BuildingEntity ours && Alive(ours))
                                ApplyBuildingData(ours, node);
                            owned++;
                            break;
                        case MeshBuildingReceipt.Promote:
                            PromoteBuildingInPlace((BuildingEntity)puppet.entity, node);
                            promoted++;
                            break;
                        default:
                            BuildingEntity made = await CreateBuildingEntity(node, true);
                            if (made != null)
                                spawned++;
                            break;
                    }
                }
                reply?.SendBuildingTransferReply(new BuildingTransferReplyMessage { rootId = rootId, ok = true, promoted = promoted, spawned = spawned, ownedAlready = owned });
                LogBuildingTransfer(rootId, fromZone, OwnZoneId, $"promoted {promoted}, spawned {spawned}, owned already {owned}");
            }
            catch (System.Exception ex)
            {
                Logging.LogException("Mesh", ex);
                reply?.SendBuildingTransferReply(new BuildingTransferReplyMessage { rootId = rootId, ok = false, reason = "error" });
            }
            finally
            {
                _buildingReceiving.Remove(rootId);
            }
        }
#else
        private void BuildingTransferOnStartServer() { }
        internal void OnBuildingTransfer(long connectionId, BuildingTransferMessage msg) { }
        internal void OnBuildingTransferReply(long connectionId, BuildingTransferReplyMessage msg) { }
#endif
    }
}
