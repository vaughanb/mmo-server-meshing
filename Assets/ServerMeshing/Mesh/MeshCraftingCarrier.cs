// Server Meshing add-on — a crafting queue crosses a hop (DESIGN.md 1.46).
//
// The kit's crafting queue is not character data: a player's own queue is a synced list on its crafting component, and
// a public workbench's queue is a list on the workbench naming each crafter by object id. Neither is saved, so a hop
// dropped every queued craft (nothing was paid for them: the kit takes the materials as each one completes). And a
// demoted puppet keeps the crafter's object id, so a workbench on the origin went on crafting for it, on a puppet's
// inventory. This carrier, built in for every player (MeshHandoffCarriers.BuiltIn), takes the crafter's entries out of
// its own queue and out of the public workbenches near it as the handoff is sent, and puts them back on the
// destination: its own entries on its new crafting component, a workbench's on that cell's copy of the same workbench
// (found by its building id; a puppet when the workbench stands across the line), each with its time left. An entry
// whose workbench the destination does not have is dropped, as the kit drops a craft whose crafter walked too far.
using System.Collections.Generic;
using LiteNetLib.Utils;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public sealed class MeshCraftingCarrier : IMeshHandoffCarrier
    {
        public const string Key = "mesh.crafting";

        /// <summary>How far around the crafter the origin looks for public workbench queues holding its entries (the kit's workbench reach is 5 m).</summary>
        public const float WorkbenchSearchRadius = 20f;

        public string CarrierKey => Key;

        private struct Entry
        {
            public int dataId;
            public int amount;
            public float remains;
            public bool onBench;    // in a public workbench's queue, not the crafter's own
            public string sourceId; // the workbench's building id; empty: the crafter itself
        }

        public bool WriteHandoff(BasePlayerCharacterEntity player, NetDataWriter writer)
        {
            if (player == null)
                return false;
            List<Entry> entries = new List<Entry>();
            float timeCounter = 0f;
            PlayerCharacterCraftingComponent crafting = player.CraftingComponent;
            if (crafting != null && crafting.QueueItems.Count > 0)
            {
                timeCounter = crafting.TimeCounter;
                for (int i = 0; i < crafting.QueueItems.Count; ++i)
                {
                    CraftingQueueItem queued = crafting.QueueItems[i];
                    string sourceId = queued.sourceObjectId == player.ObjectId ? string.Empty : BuildingIdOf(queued.sourceObjectId);
                    if (sourceId == null)
                        continue; // its workbench is gone already
                    entries.Add(new Entry { dataId = queued.dataId, amount = queued.amount, remains = queued.craftRemainsDuration, sourceId = sourceId });
                }
                crafting.QueueItems.Clear(); // the destination crafts them from here on
            }
            foreach (QueuedWorkbenchEntity bench in BenchesNear(player))
            {
                for (int i = 0; i < bench.QueueItems.Count; ++i)
                {
                    CraftingQueueItem queued = bench.QueueItems[i];
                    if (queued.crafterId != player.ObjectId)
                        continue;
                    entries.Add(new Entry { dataId = queued.dataId, amount = queued.amount, remains = queued.craftRemainsDuration, onBench = true, sourceId = bench.Id ?? string.Empty });
                }
                for (int i = bench.QueueItems.Count - 1; i >= 0; --i)
                {
                    if (bench.QueueItems[i].crafterId == player.ObjectId)
                        bench.QueueItems.RemoveAt(i); // a demoted puppet keeps the object id: the bench would craft on its inventory
                }
            }
            if (entries.Count == 0)
                return false;
            writer.Put(timeCounter);
            writer.Put(entries.Count);
            foreach (Entry entry in entries)
            {
                writer.Put(entry.dataId);
                writer.Put(entry.amount);
                writer.Put(entry.remains);
                writer.Put(entry.onBench);
                writer.Put(entry.sourceId ?? string.Empty);
            }
            return true;
        }

        public void ReadHandoff(BasePlayerCharacterEntity player, NetDataReader reader)
        {
            float timeCounter = reader.GetFloat();
            int count = reader.GetInt();
            int own = 0, onBenches = 0, dropped = 0;
            PlayerCharacterCraftingComponent crafting = player.CraftingComponent;
            for (int i = 0; i < count; ++i)
            {
                Entry entry = new Entry { dataId = reader.GetInt(), amount = reader.GetInt(), remains = reader.GetFloat(), onBench = reader.GetBool(), sourceId = reader.GetString() };
                uint sourceObjectId = player.ObjectId;
                QueuedWorkbenchEntity bench = null;
                if (!string.IsNullOrEmpty(entry.sourceId))
                {
                    BuildingEntity building = FindBuilding(entry.sourceId);
                    if (building == null)
                    {
                        dropped++;
                        continue;
                    }
                    sourceObjectId = building.ObjectId;
                    bench = building as QueuedWorkbenchEntity;
                }
                CraftingQueueItem queued = new CraftingQueueItem
                {
                    crafterId = player.ObjectId,
                    dataId = entry.dataId,
                    amount = entry.amount,
                    craftRemainsDuration = entry.remains,
                    sourceObjectId = sourceObjectId,
                };
                if (entry.onBench)
                {
                    if (bench == null || !bench.PublicQueue || bench.QueueItems.Count >= bench.MaxQueueSize)
                    {
                        dropped++;
                        continue;
                    }
                    bench.QueueItems.Add(queued);
                    onBenches++;
                }
                else if (crafting != null)
                {
                    crafting.QueueItems.Add(queued);
                    own++;
                }
                else
                    dropped++;
            }
            if (crafting != null && own > 0)
                crafting.TimeCounter = timeCounter;
            Logging.Log("Mesh", $"Crafting: {player.CharacterName}'s queue carried with the handoff, {own} own entr{(own == 1 ? "y" : "ies")}{(own > 0 ? $" (first {(crafting.QueueItems.Count > 0 ? crafting.QueueItems[0].amount : 0)} to go)" : string.Empty)}, {onBenches} on workbenches, {dropped} dropped");
        }

        private static string BuildingIdOf(uint objectId)
        {
            BaseGameNetworkManager manager = BaseGameNetworkManager.Singleton;
            if (manager != null && manager.TryGetEntityByObjectId(objectId, out BuildingEntity building) && building != null)
                return building.Id ?? string.Empty;
            return null;
        }

        private static BuildingEntity FindBuilding(string id)
        {
            BaseGameNetworkManager manager = BaseGameNetworkManager.Singleton;
            if (manager == null || string.IsNullOrEmpty(id))
                return null;
            Dictionary<uint, LiteNetLibIdentity>.Enumerator spawned = manager.Assets.GetSpawnedObjects();
            while (spawned.MoveNext())
            {
                LiteNetLibIdentity identity = spawned.Current.Value;
                if (identity != null && identity.TryGetComponent(out BuildingEntity building) && building.Id == id)
                    return building;
            }
            return null;
        }

        private static List<QueuedWorkbenchEntity> BenchesNear(BasePlayerCharacterEntity player)
        {
            List<QueuedWorkbenchEntity> benches = new List<QueuedWorkbenchEntity>();
            BaseGameNetworkManager manager = BaseGameNetworkManager.Singleton;
            if (manager == null || player == null)
                return benches;
            Vector3 at = player.EntityTransform.position;
            float radiusSqr = WorkbenchSearchRadius * WorkbenchSearchRadius;
            Dictionary<uint, LiteNetLibIdentity>.Enumerator spawned = manager.Assets.GetSpawnedObjects();
            while (spawned.MoveNext())
            {
                LiteNetLibIdentity identity = spawned.Current.Value;
                if (identity == null || (identity.transform.position - at).sqrMagnitude > radiusSqr)
                    continue;
                if (identity.TryGetComponent(out QueuedWorkbenchEntity bench) && bench.PublicQueue && bench.QueueItems.Count > 0)
                    benches.Add(bench);
            }
            return benches;
        }
    }
}
