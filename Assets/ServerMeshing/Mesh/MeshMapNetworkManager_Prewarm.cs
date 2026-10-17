// Server Meshing add-on — pre-warm on a Dynamic map (Slice 2b, slices/slice2b_design.md decisions 6 and 7). Character
// puppets from snapshots are spawned through a queue served by a per-frame time budget (prewarmSpawnBudgetShare of the
// frame budget), nearest to this cell's rectangle first, later snapshots of a queued id folded in: a new child receives
// every puppet of the rectangle it is about to own at once, and b8 measured what an all-at-once burst costs. Promotion,
// arrival, adoption and GhostRemove consume a queued entry, and an id that became owned is dropped when it is served
// (OnGhostSnapshot's own dedup), so no puppet is ever spawned beside an owned entity. Also here: when each band
// neighbour's inbound MeshHello arrived and the link's datagram count then, for the e1 gate's evidence, and the Prewarm
// window line. Static maps queue nothing and log nothing.
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// MeshPuppetSpawnQueue holds one pending spawn per id, served nearest first; enqueueing an id already queued folds the
    /// new payload and distance into its entry. Pure, and allocation-free once warm (entries are pooled).
    /// </summary>
    public sealed class MeshPuppetSpawnQueue<T>
    {
        private sealed class Entry
        {
            public string id;
            public T payload;
            public float distance;
            public bool live;
        }

        private sealed class FarFirst : IComparer<Entry>
        {
            public int Compare(Entry a, Entry b) => b.distance.CompareTo(a.distance);
        }

        private static readonly FarFirst s_farFirst = new FarFirst();
        private readonly Dictionary<string, Entry> _byId = new Dictionary<string, Entry>(System.StringComparer.Ordinal);
        private readonly List<Entry> _order = new List<Entry>(); // sorted far to near when clean: the nearest is popped from the end
        private readonly Stack<Entry> _pool = new Stack<Entry>();
        private bool _dirty;

        public int Count => _byId.Count;

        public bool Contains(string id) => id != null && _byId.ContainsKey(id);

        /// <summary>Enqueue queues an id, or folds a later payload into its entry; true when the id was not queued.</summary>
        public bool Enqueue(string id, T payload, float distance)
        {
            if (id == null)
                return false;
            if (_byId.TryGetValue(id, out Entry entry))
            {
                entry.payload = payload;
                if (entry.distance != distance)
                {
                    entry.distance = distance;
                    _dirty = true;
                }
                return false;
            }
            entry = _pool.Count > 0 ? _pool.Pop() : new Entry();
            entry.id = id;
            entry.payload = payload;
            entry.distance = distance;
            entry.live = true;
            _byId[id] = entry;
            _order.Add(entry);
            _dirty = true;
            return true;
        }

        /// <summary>Consume removes a queued id (promoted, arrived, removed by its owner); true when it was queued.</summary>
        public bool Consume(string id)
        {
            if (id == null || !_byId.TryGetValue(id, out Entry entry))
                return false;
            _byId.Remove(id);
            entry.live = false; // left in the order list, recycled when it is reached
            return true;
        }

        /// <summary>TryTake removes a queued id and hands back its latest payload, to be served at once (out of turn).</summary>
        public bool TryTake(string id, out T payload)
        {
            if (id == null || !_byId.TryGetValue(id, out Entry entry))
            {
                payload = default;
                return false;
            }
            payload = entry.payload;
            Consume(id);
            return true;
        }

        /// <summary>TryPop takes the nearest queued id and its latest payload.</summary>
        public bool TryPop(out string id, out T payload)
        {
            if (_dirty)
                Compact();
            while (_order.Count > 0)
            {
                Entry entry = _order[_order.Count - 1];
                _order.RemoveAt(_order.Count - 1);
                bool live = entry.live;
                id = entry.id;
                payload = entry.payload;
                if (live)
                    _byId.Remove(entry.id);
                Recycle(entry);
                if (live)
                    return true;
            }
            id = null;
            payload = default;
            return false;
        }

        public void Clear()
        {
            for (int i = 0; i < _order.Count; ++i)
                Recycle(_order[i]);
            _order.Clear();
            _byId.Clear();
            _dirty = false;
        }

        private void Compact()
        {
            int kept = 0;
            for (int i = 0; i < _order.Count; ++i)
            {
                Entry entry = _order[i];
                if (entry.live)
                    _order[kept++] = entry;
                else
                    Recycle(entry);
            }
            _order.RemoveRange(kept, _order.Count - kept);
            _order.Sort(s_farFirst);
            _dirty = false;
        }

        private void Recycle(Entry entry)
        {
            entry.id = null;
            entry.payload = default;
            entry.live = false;
            _pool.Push(entry);
        }
    }

    public partial class MeshMapNetworkManager
    {
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        /// <summary>A snapshot waiting for its puppet's spawn; the position is the sender's frame (the offset is added again when served).</summary>
        private struct QueuedPuppet
        {
            public long connectionId;
            public GhostSnapshotMessage msg;
        }

        private struct NeighbourHello
        {
            public float time;
            public long connectionId;
            public long packetsAtHello;
        }

        private readonly MeshPuppetSpawnQueue<QueuedPuppet> _puppetQueue = new MeshPuppetSpawnQueue<QueuedPuppet>();
        private readonly Dictionary<string, NeighbourHello> _neighbourHellos = new Dictionary<string, NeighbourHello>(System.StringComparer.OrdinalIgnoreCase);
        private bool _servingPuppetQueue;
        private int _prewarmSpawned;
        private float _prewarmMaxFrame;

        private void PrewarmOnStartServer()
        {
            _puppetQueue.Clear();
            _neighbourHellos.Clear();
            _servingPuppetQueue = false;
            _prewarmSpawned = 0;
            _prewarmMaxFrame = 0f;
        }

        private void PrewarmOnStopServer()
        {
            _puppetQueue.Clear();
            _neighbourHellos.Clear();
        }

        /// <summary>
        /// OnGhostSnapshot's hook for a puppet it is about to spawn: on a Dynamic map the spawn is queued (or the snapshot
        /// folded into the queued one) and true is returned; false on a Static map and while the queue is being served.
        /// </summary>
        private bool QueuePuppetSpawn(long connectionId, GhostSnapshotMessage msg)
        {
            if (!IsDynamicMap || _servingPuppetQueue)
                return false;
            float distance = PuppetSpawnDistance(msg.position);
            msg.position -= OffsetFrom(connectionId);
            _puppetQueue.Enqueue(msg.characterId, new QueuedPuppet { connectionId = connectionId, msg = msg }, distance);
            return true;
        }

        /// <summary>A queued spawn of this id is no longer wanted: its entity was promoted, arrived, was adopted, or its owner removed it.</summary>
        private void PrewarmConsume(string meshId)
        {
            if (_puppetQueue.Count > 0)
                _puppetQueue.Consume(meshId);
        }

        /// <summary>
        /// A promotion (monster handoff) or a client's arrival needs the puppet now: a queued spawn of the id is served at
        /// once, out of turn, so the caller promotes it in place instead of spawning a second entity beside it.
        /// </summary>
        private void PrewarmServeNow(string meshId)
        {
            if (_puppetQueue.Count == 0 || !_puppetQueue.TryTake(meshId, out QueuedPuppet queued))
                return;
            ServeQueuedPuppet(meshId, queued);
        }

        private void ServeQueuedPuppet(string meshId, QueuedPuppet queued)
        {
            bool had = _ghosts.TryGetValue(meshId, out MeshGhost before) && before.entity != null;
            _servingPuppetQueue = true;
            try
            {
                OnGhostSnapshot(queued.connectionId, queued.msg);
            }
            finally
            {
                _servingPuppetQueue = false;
            }
            if (!had && _ghosts.TryGetValue(meshId, out MeshGhost after) && after.entity != null)
                _prewarmSpawned++;
        }

        /// <summary>Distance of a position (this cell's frame) to the rectangle this cell owns, or the one it is about to (its interest while it owns nothing).</summary>
        private float PuppetSpawnDistance(Vector3 scenePos)
        {
            if (OwnCell == null)
                return 0f;
            MeshRect rect = OwnCell.owned.IsEmpty ? OwnCell.interest : OwnCell.owned;
            Vector3 world = OwnCell.SceneToWorld(scenePos);
            float distance = rect.DistanceTo(world.x, world.z);
            return float.IsInfinity(distance) ? float.MaxValue : distance;
        }

        /// <summary>Per-frame budget of the queue: prewarmSpawnBudgetShare of the frame budget (the cap's, else the nominal rate's).</summary>
        private float PrewarmBudgetMs
        {
            get
            {
                float frame = MeshWorkProbe.BudgetMs > 0f ? MeshWorkProbe.BudgetMs : MeshWorkProbe.EffectiveBudgetMs;
                if (frame <= 0f)
                    frame = 1000f / 60f;
                float share = world != null ? world.dynamicSettings.prewarmSpawnBudgetShare : 0.25f;
                return Mathf.Max(0.1f, share * frame);
            }
        }

        /// <summary>Every frame on a Dynamic map: note the frame time, then spawn queued puppets, nearest first, until the budget is spent (at least one).</summary>
        private void PrewarmUpdate()
        {
            if (!IsDynamicMap)
                return;
            float dt = Time.unscaledDeltaTime;
            if (dt > _prewarmMaxFrame)
                _prewarmMaxFrame = dt;
            if (_puppetQueue.Count == 0)
                return;
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            long budget = (long)(PrewarmBudgetMs / 1000f * System.Diagnostics.Stopwatch.Frequency);
            while (_puppetQueue.TryPop(out string id, out QueuedPuppet queued))
            {
                ServeQueuedPuppet(id, queued);
                if (System.Diagnostics.Stopwatch.GetTimestamp() - start >= budget)
                    break;
            }
        }

        /// <summary>OnMeshNeighbourHello's hook: when the neighbour's inbound hello arrived and how many datagrams its connection had delivered by then.</summary>
        private void NoteNeighbourHello(long connectionId, string zoneId)
        {
            if (!IsDynamicMap || string.IsNullOrEmpty(zoneId))
                return;
            _neighbourHellos[zoneId] = new NeighbourHello { time = Time.unscaledTime, connectionId = connectionId, packetsAtHello = InboundPackets(connectionId) };
        }

        /// <summary>Datagrams received so far on an inbound link connection (snapshots and the transport's pings alike), or -1 when unknown.</summary>
        private long InboundPackets(long connectionId)
        {
            LiteNetLibTransport transport = _linkServer != null ? _linkServer.Transport as LiteNetLibTransport : null;
            NetManager server = transport != null ? transport.Server : null;
            if (server == null || connectionId < 0 || connectionId > int.MaxValue || !server.TryGetPeerById((int)connectionId, out LiteNetPeer peer) || peer == null)
                return -1;
            return peer.Statistics.PacketsReceived;
        }

        /// <summary>What this cell has heard from each band neighbour since its hello, for the evidence message.</summary>
        private List<MeshNeighbourEvidence> NeighbourEvidence(float now)
        {
            List<MeshNeighbourEvidence> list = new List<MeshNeighbourEvidence>(OwnCell != null ? OwnCell.bandNeighbours.Count : 0);
            if (OwnCell == null)
                return list;
            for (int i = 0; i < OwnCell.bandNeighbours.Count; ++i)
            {
                string zone = OwnCell.bandNeighbours[i].id;
                bool known = _neighbourHellos.TryGetValue(zone, out NeighbourHello hello)
                    && _neighbourZoneByConnection.TryGetValue(hello.connectionId, out string linked)
                    && string.Equals(linked, zone, System.StringComparison.OrdinalIgnoreCase);
                long packets = known ? InboundPackets(hello.connectionId) : -1;
                list.Add(MeshSplitEvidence.Neighbour(zone, known, hello.time, now, hello.packetsAtHello, packets));
            }
            return list;
        }

        /// <summary>The Prewarm window line (Dynamic maps, when the queue did anything or an operation is under way), then a fresh window.</summary>
        private void LogPrewarmWindow(float window, bool splitting)
        {
            if (_prewarmSpawned > 0 || _puppetQueue.Count > 0 || splitting)
                Logging.Log("Mesh", System.FormattableString.Invariant($"Prewarm window {window:0.00} s: queue {_puppetQueue.Count} spawned {_prewarmSpawned} | max frame {_prewarmMaxFrame * 1000f:0.0} ms"));
            _prewarmSpawned = 0;
            _prewarmMaxFrame = 0f;
        }
#endif
    }
}
