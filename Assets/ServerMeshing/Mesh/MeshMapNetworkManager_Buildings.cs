// Server Meshing add-on — buildings across a cut, the channel and the despawn guard (Slice 4, slices/slice4_design.md
// decisions 2 and 7). The kit persists buildings per channel and map; on a Dynamic map every cell of a split family
// reads and writes them under the base cell's channel (the kit's BuildingPersistenceChannelId, core edit CE-10), so
// a building keeps its row when its ground changes hands. With one shared channel a building's despawn must not
// delete its row unless this cell owns it: the kit deletes on every NetworkDestroy, and the add-on despawns puppets
// (stale expiry, link loss, the prune, a neighbour's new incarnation) and demoted copies all the time. A demoted tree
// despawns leaf first, so a root's chain destroy finds no child. Static maps: the kit's paths, byte-identical.
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class MeshMapNetworkManager
    {
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        private bool _buildingChannelLogged;
        // Trees this cell demoted on a release (decision 9 step 3): root id -> ids parent before child; node id -> root id.
        private readonly Dictionary<string, List<string>> _demotedTrees = new Dictionary<string, List<string>>(System.StringComparer.Ordinal);
        private readonly Dictionary<string, string> _demotedRootOf = new Dictionary<string, string>(System.StringComparer.Ordinal);

        /// <summary>Slice 4 decision 2: a Dynamic map's cell persists buildings under its family's base channel.</summary>
        protected override string BuildingPersistenceChannelId
        {
            get
            {
                string channel = MeshBuildingChannel.For(ChannelId, IsDynamicMap, OwnCell != null ? OwnCell.baseId : null, LayoutCellId);
                if (IsDynamicMap && !_buildingChannelLogged)
                {
                    _buildingChannelLogged = true;
                    Logging.Log("Mesh", $"Building channel on {LayoutCellId}: {channel}");
                }
                return channel;
            }
        }

        /// <summary>
        /// Slice 4 decision 7: on a Dynamic map only an owned building (in the handlers) deletes its row; a puppet, a
        /// demoted copy or a building released to another cell keeps it (the row belongs to whoever owns it now).
        /// </summary>
        public override async UniTask DestroyBuildingEntity(string id, bool isSceneObject)
        {
            bool ownedHere = ServerBuildingHandlers.TryGetBuilding(id, out IBuildingSaveData _);
            if (!MeshBuildingTransferRules.DeleteRowOnDespawn(IsDynamicMap, ownedHere))
            {
                await UniTask.Yield();
                return;
            }
            await base.DestroyBuildingEntity(id, isSceneObject);
        }

        private void BuildingsOnStartServer()
        {
            _buildingChannelLogged = false;
            _demotedTrees.Clear();
            _demotedRootOf.Clear();
            BuildingLoadOnStartServer();
            BuildingTransferOnStartServer();
        }

        /// <summary>Records a tree this cell demoted (its ids parent before child), for the leaf-first despawn.</summary>
        private void RememberDemotedTree(string rootId, List<string> ids)
        {
            _demotedTrees[rootId] = new List<string>(ids);
            for (int i = 0; i < ids.Count; ++i)
                _demotedRootOf[ids[i]] = rootId;
        }

        /// <summary>Drops the demoted-tree record a node belongs to (it is owned again, here or by a promotion).</summary>
        private void ForgetDemotedTreeOf(string id)
        {
            if (!_demotedRootOf.TryGetValue(id, out string rootId))
                return;
            if (_demotedTrees.TryGetValue(rootId, out List<string> ids))
            {
                for (int i = 0; i < ids.Count; ++i)
                    _demotedRootOf.Remove(ids[i]);
                _demotedTrees.Remove(rootId);
            }
            _demotedRootOf.Remove(id);
        }

        /// <summary>
        /// DestroyGhostStatic's hook (decision 7): a demoted root's children go first (deepest first, this frame) and the
        /// root itself next frame, so the kit's chain destroy on the root finds no child to destroy again (and drop
        /// items from). True when this call took the destroy over.
        /// </summary>
        private bool DeferDemotedRootDestroy(string meshId, string reason)
        {
            if (!_demotedTrees.TryGetValue(meshId, out List<string> ids))
            {
                _demotedRootOf.Remove(meshId); // a child despawned on its own: its record goes with it
                return false;
            }
            _demotedTrees.Remove(meshId);
            List<string> order = MeshBuildingTransferRules.DespawnOrder(ids);
            for (int i = 0; i < order.Count; ++i)
            {
                _demotedRootOf.Remove(order[i]);
                if (!string.Equals(order[i], meshId, System.StringComparison.Ordinal))
                    DestroyGhostStatic(order[i], reason);
            }
            DestroyDemotedRootNextFrame(meshId, reason).Forget();
            return true;
        }

        private async UniTaskVoid DestroyDemotedRootNextFrame(string meshId, string reason)
        {
            await UniTask.NextFrame();
            if (this == null || !IsServer)
                return;
            DestroyGhostStatic(meshId, reason);
        }
#else
        private void BuildingsOnStartServer() { }
#endif
    }
}
