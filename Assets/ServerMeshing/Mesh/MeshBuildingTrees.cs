// Server Meshing add-on — the pure parts of buildings across a cut (Slice 4, slices/slice4_design.md): the persistence
// channel of a cell (decision 2), building rows as trees and their partition at a cell's load (decision 4), the
// transfer rules a holder and a receiver apply (decisions 8-10), and the predicate behind the despawn guard (decision
// 7). No Unity object is touched here; the partials MeshMapNetworkManager_Buildings / _BuildingLoad / _BuildingTransfer
// drive the kit with what these decide. Game-neutral.
using System;
using System.Collections.Generic;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>MeshBuildingChannel names the channel a cell's buildings persist under (Slice 4 decision 2).</summary>
    public static class MeshBuildingChannel
    {
        /// <summary>
        /// BaseOf strips one ".&lt;digits&gt;" suffix (the form the coordinator mints a child id in): "C.1" and "C.12" give
        /// "C"; "C", "C2" and "C.x" are their own base. Null or empty gives empty.
        /// </summary>
        public static string BaseOf(string cellId)
        {
            if (string.IsNullOrEmpty(cellId))
                return string.Empty;
            int dot = cellId.LastIndexOf('.');
            if (dot <= 0 || dot == cellId.Length - 1)
                return cellId;
            for (int i = dot + 1; i < cellId.Length; ++i)
            {
                if (cellId[i] < '0' || cellId[i] > '9')
                    return cellId;
            }
            return cellId.Substring(0, dot);
        }

        /// <summary>
        /// For: the channel a cell persists buildings under. A Static map's cell: its own channel. A Dynamic map's cell:
        /// the base id its layout gave it, else the id's own base by syntax, else its channel.
        /// </summary>
        public static string For(string channelId, bool dynamicMap, string layoutBaseId, string layoutCellId)
        {
            if (!dynamicMap)
                return channelId ?? string.Empty;
            if (!string.IsNullOrEmpty(layoutBaseId))
                return layoutBaseId;
            string parsed = BaseOf(string.IsNullOrEmpty(layoutCellId) ? channelId : layoutCellId);
            return string.IsNullOrEmpty(parsed) ? channelId ?? string.Empty : parsed;
        }
    }

    /// <summary>MeshBuildingNode is one building row as the trees see it: its id, its parent's, and where its root stands.</summary>
    public struct MeshBuildingNode
    {
        public string id;
        public string parentId;
        public bool isSceneObject;
        public float x;
        public float z;
    }

    /// <summary>MeshBuildingTree is a root and its descendants, parent before child (breadth-first from the root).</summary>
    public sealed class MeshBuildingTree
    {
        public string rootId;
        public readonly List<string> ids = new List<string>();
        public float rootX;
        public float rootZ;
    }

    /// <summary>MeshBuildingPartition is what a cell's load does with each row (Slice 4 decision 4).</summary>
    public sealed class MeshBuildingPartition
    {
        /// <summary>sceneMatched: IsSceneObject rows whose id names an in-scene building here (the kit's treatment).</summary>
        public readonly List<string> sceneMatched = new List<string>();
        /// <summary>sceneSkipped: IsSceneObject rows of another cell's scene copies (their ids embed that cell's channel).</summary>
        public readonly List<string> sceneSkipped = new List<string>();
        /// <summary>alreadyHere: rows whose id is already in the building handlers (placed while the load ran).</summary>
        public readonly List<string> alreadyHere = new List<string>();
        /// <summary>trees: every placed tree; IsOurs decides which are created here.</summary>
        public readonly List<MeshBuildingTree> trees = new List<MeshBuildingTree>();
    }

    /// <summary>MeshBuildingTrees builds trees from rows and partitions a load (Slice 4 decision 4).</summary>
    public static class MeshBuildingTrees
    {
        /// <summary>
        /// Build groups placed rows into trees: children by parentId; a node whose parent is not among the rows is a root;
        /// a cycle (no node of it reaches a root) is broken at its lowest id, which becomes the root. Each tree lists its
        /// ids parent before child, breadth-first. Rows in <paramref name="skip"/> (scene rows, rows already here) are
        /// left out, and a child of a skipped row becomes a root.
        /// </summary>
        public static List<MeshBuildingTree> Build(IList<MeshBuildingNode> rows, ICollection<string> skip = null)
        {
            Dictionary<string, MeshBuildingNode> byId = new Dictionary<string, MeshBuildingNode>(StringComparer.Ordinal);
            for (int i = 0; i < rows.Count; ++i)
            {
                MeshBuildingNode node = rows[i];
                if (string.IsNullOrEmpty(node.id) || (skip != null && skip.Contains(node.id)))
                    continue;
                byId[node.id] = node;
            }
            Dictionary<string, List<string>> children = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            List<string> roots = new List<string>();
            foreach (KeyValuePair<string, MeshBuildingNode> kv in byId)
            {
                string parent = kv.Value.parentId;
                if (string.IsNullOrEmpty(parent) || !byId.ContainsKey(parent) || string.Equals(parent, kv.Key, StringComparison.Ordinal))
                    roots.Add(kv.Key);
                else
                {
                    if (!children.TryGetValue(parent, out List<string> list))
                        children[parent] = list = new List<string>();
                    list.Add(kv.Key);
                }
            }
            roots.Sort(StringComparer.Ordinal);
            HashSet<string> placed = new HashSet<string>(StringComparer.Ordinal);
            List<MeshBuildingTree> trees = new List<MeshBuildingTree>();
            for (int i = 0; i < roots.Count; ++i)
                trees.Add(Walk(roots[i], byId, children, placed));
            if (placed.Count < byId.Count)
            {
                // Cycles: whatever no root reached. Break each at its lowest id.
                List<string> left = new List<string>();
                foreach (string id in byId.Keys)
                    if (!placed.Contains(id)) left.Add(id);
                left.Sort(StringComparer.Ordinal);
                for (int i = 0; i < left.Count; ++i)
                {
                    if (placed.Contains(left[i]))
                        continue;
                    trees.Add(Walk(left[i], byId, children, placed));
                }
            }
            return trees;
        }

        private static MeshBuildingTree Walk(string rootId, Dictionary<string, MeshBuildingNode> byId, Dictionary<string, List<string>> children, HashSet<string> placed)
        {
            MeshBuildingNode root = byId[rootId];
            MeshBuildingTree tree = new MeshBuildingTree { rootId = rootId, rootX = root.x, rootZ = root.z };
            Queue<string> queue = new Queue<string>();
            queue.Enqueue(rootId);
            placed.Add(rootId);
            while (queue.Count > 0)
            {
                string id = queue.Dequeue();
                tree.ids.Add(id);
                if (!children.TryGetValue(id, out List<string> list))
                    continue;
                list.Sort(StringComparer.Ordinal);
                for (int i = 0; i < list.Count; ++i)
                {
                    if (placed.Add(list[i]))
                        queue.Enqueue(list[i]);
                }
            }
            return tree;
        }

        /// <summary>
        /// Partition sorts a load's rows (decision 4): scene rows matched to an in-scene id, scene rows of other cells,
        /// rows already in the handlers, and the placed rows as trees.
        /// </summary>
        public static MeshBuildingPartition Partition(IList<MeshBuildingNode> rows, ICollection<string> sceneIds, ICollection<string> ownedIds)
        {
            MeshBuildingPartition part = new MeshBuildingPartition();
            HashSet<string> skip = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < rows.Count; ++i)
            {
                MeshBuildingNode node = rows[i];
                if (string.IsNullOrEmpty(node.id))
                    continue;
                if (node.isSceneObject)
                {
                    (sceneIds != null && sceneIds.Contains(node.id) ? part.sceneMatched : part.sceneSkipped).Add(node.id);
                    skip.Add(node.id);
                }
                else if (ownedIds != null && ownedIds.Contains(node.id))
                {
                    part.alreadyHere.Add(node.id);
                    skip.Add(node.id);
                }
            }
            part.trees.AddRange(Build(rows, skip));
            return part;
        }
    }

    /// <summary>MeshBuildingReceipt is what a receiver does with one node of a transferred tree (Slice 4 decision 10).</summary>
    public enum MeshBuildingReceipt { Refuse, OwnedAlready, Promote, Spawn }

    /// <summary>MeshBuildingTransferRules are the holder's and the receiver's decisions (Slice 4 decisions 8-10).</summary>
    public static class MeshBuildingTransferRules
    {
        /// <summary>
        /// MayStart: the holder may begin releasing a root when its load is done, the root is owned here, another cell of
        /// this map owns its position, that cell is linked and settled, the root is not in back-off and fewer than the
        /// cap of releases run.
        /// </summary>
        public static bool MayStart(bool loaded, bool ownedHere, bool otherOwnerSameMap, bool targetLinked, bool targetSettled, bool inBackoff, int inFlight, int cap)
        {
            return loaded && ownedHere && otherOwnerSameMap && targetLinked && targetSettled && !inBackoff && inFlight < Math.Max(1, cap);
        }

        /// <summary>
        /// RefuseReason: why a receiver refuses a whole tree before it makes anything: gated, loading, not the owner of
        /// the root's position, or a node whose prefab it does not know (the index of the first such node, or -1). Null
        /// when it takes the tree.
        /// </summary>
        public static string RefuseReason(bool gated, bool loading, bool ownerHere, int unknownPrefabIndex, int unknownEntityId)
        {
            if (gated)
                return "gated";
            if (loading)
                return "loading";
            if (!ownerHere)
                return "not the owner here";
            if (unknownPrefabIndex >= 0)
                return $"unknown building {unknownEntityId}";
            return null;
        }

        /// <summary>Receive: one node, once the tree is accepted: owned already, promote the puppet, or spawn.</summary>
        public static MeshBuildingReceipt Receive(bool ownedAlready, bool puppetHere)
        {
            if (ownedAlready)
                return MeshBuildingReceipt.OwnedAlready;
            return puppetHere ? MeshBuildingReceipt.Promote : MeshBuildingReceipt.Spawn;
        }

        /// <summary>
        /// StorageReleased: the holder may go on past a storage node when nobody has it open and no item move holds it,
        /// or the wait is over (past the close budget).
        /// </summary>
        public static bool StorageReleased(bool open, bool moveInFlight, float waitedSeconds, float closeSeconds)
        {
            return (!open && !moveInFlight) || waitedSeconds >= Math.Max(0f, closeSeconds);
        }

        /// <summary>
        /// DeleteRowOnDespawn: the despawn guard (decision 7). On a Dynamic map only an owned building (in the handlers)
        /// deletes its row; a puppet, a demoted copy or a released building keeps it. Static maps: the kit's path.
        /// </summary>
        public static bool DeleteRowOnDespawn(bool dynamicMap, bool ownedHere)
        {
            return !dynamicMap || ownedHere;
        }

        /// <summary>
        /// DespawnOrder: a demoted tree's ids deepest first (children before their parent), so a root's chain destroy
        /// finds no child; the root is last. <paramref name="parentBeforeChild"/> is the tree as sent.
        /// </summary>
        public static List<string> DespawnOrder(IList<string> parentBeforeChild)
        {
            List<string> order = new List<string>(parentBeforeChild.Count);
            for (int i = parentBeforeChild.Count - 1; i >= 0; --i)
                order.Add(parentBeforeChild[i]);
            return order;
        }
    }
}
