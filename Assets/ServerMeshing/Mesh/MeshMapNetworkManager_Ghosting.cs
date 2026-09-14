// Server Meshing add-on — border ghosting (Spike 1b / P2).
// See Docs/ServerMeshing/DESIGN.md §5, §7, §8, §10 and SPIKE1.md §0b.
//
// EXTENSION-ONLY (partial of the MapNetworkManager subclass). Server side only:
//  * every `ghostSnapshotInterval` each owned entity (registered player, or server-owned monster)
//    inside the border band B of a shared edge is streamed (full-precision position + movement state)
//    to that neighbour over the CellMeshLink;
//  * a received snapshot spawns/steers a GHOST: a normal entity spawned server-side, with its
//    server-driver components silenced (GhostSuppression) but its movement component kept, so it is
//    steered toward the snapshot with the kit's own movement API ("puppet"), animates, and reaches
//    this cell's clients through the ordinary AOI/sync path. Ghosts are invincible so the local
//    simulation cannot damage them (cross-border hits are resolved by the owner — P3);
//  * dedup/promotion (DESIGN §8): a snapshot for a character this cell owns for real is ignored, and
//    when the real character registers here (handoff-in) any ghost with the same id is destroyed;
//  * monster ownership: each cell only runs the spawn areas that sit inside its own zone, so every
//    server-spawned entity has exactly one owner; its MeshEntityId is the birth-provenance id
//    "{zone}:{incarnation}:{objectId}" (DESIGN §8).
// Ghosts are never registered as player characters, so the boundary tick and streaming ignore them.

using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using LiteNetLib;
using LiteNetLib.Utils;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class MeshMapNetworkManager
    {
        [Header("Server Meshing — border ghosting (server side)")]
        [Tooltip("Stream owned entities near a shared zone edge to the neighbour cell, and spawn ghosts for what the neighbour streams to us.")]
        public bool enableBorderGhosting = true;

        [Tooltip("Also ghost server-owned monsters (not only players).")]
        public bool ghostMonsters = true;

        [Tooltip("Only run spawn areas whose position lies inside this cell's zone (one owner per server-spawned entity).")]
        public bool ownSpawnAreasByZone = true;

        [Tooltip("Border band B (metres from the shared edge) inside which entities are ghosted into the neighbour. Size it to the kit's visible range (80 m in the demo) or the far scene pops in at the crossing; the speed rule (max speed x (AOI pass + snapshot + a link hop) + hysteresis) is far below that at any sane speed. Checked and logged at server start.")]
        public float ghostBandWidth = 80f; // sized to the kit's visible range (Q1)

        [Tooltip("Seconds between ghost snapshots.")]
        public float ghostSnapshotInterval = 0.1f;

        [Tooltip("A ghost that received no snapshot for this long is destroyed.")]
        public float ghostStaleSeconds = 2f;

        [Tooltip("If a ghost is further than this from its snapshot it is teleported instead of steered.")]
        public float ghostTeleportDistance = 4f;

        [Tooltip("Mesh link listens on mapPort + this offset.")]
        public int meshLinkPortOffset = 10000;

        public int GhostCount => _ghosts.Count;
        public int NeighbourLinkCount => _linkClients.Count;

        private sealed class MeshGhost
        {
            public BaseCharacterEntity entity;
            public GhostKind kind;
            public float lastSeen;
            public Vector3 lastTarget;
            public ReceiveDamageDelegate hitHook; // so a promoted puppet can drop its hit forwarding
            public VehicleEntity vehicle;         // the mount this ghost rides (spawned here for a puppet, kept for a demoted player)
            public bool mountPending;             // an async mount/dismount is in flight
            public bool reserved;                 // an arriving client has claimed this puppet (promotion finishes at registration)
            public string summonerMeshId;         // a pet puppet: mesh id of its summoner, from its snapshots
            public string summonId;               // ... and the CharacterSummon.id it is on that summoner
            public float holdUntil;               // a demoted handoff rolled back: kept (not stale) until then for the client's return
        }

        private MeshLinkServer _linkServer;
        private readonly Dictionary<string, MeshLinkClient> _linkClients = new Dictionary<string, MeshLinkClient>();   // zoneId -> link
        private readonly Dictionary<long, string> _neighbourZoneByConnection = new Dictionary<long, string>();
        private readonly Dictionary<string, MeshGhost> _ghosts = new Dictionary<string, MeshGhost>();                  // meshEntityId -> ghost
        private readonly Dictionary<string, HashSet<string>> _streamedToZone = new Dictionary<string, HashSet<string>>(); // zoneId -> ids streamed last tick
        private readonly List<string> _tmpIds = new List<string>();
        private readonly List<BaseCharacterEntity> _ownedCandidates = new List<BaseCharacterEntity>();
        private float _nextSnapshotTime;
        private float _nextLinkCheck;

        // ---- Load / sizing instrumentation (DESIGN §6 Q1, §14 risk 8) ----
        [Tooltip("Every this many seconds log one 'Mesh stats' line: players, owned and ghost counts, link traffic per neighbour (kB/s in and out, packets/s) and the handoffs completed in the interval with their mean and max end-to-end time. 0 = off.")]
        public float meshStatsIntervalSeconds = 10f;
        [Tooltip("The fastest a character can move on this map (m/s; the mount in the demo). Used with the AOI cadence and the snapshot rate to check the ghost band is wide enough at server start.")]
        public float expectedMaxSpeed = 8f;
        private float _nextStatsTime;
        private long _statsLinkBytesOut, _statsLinkBytesIn, _statsLinkPacketsOut, _statsLinkPacketsIn;
        private int _statsHandoffs; private float _statsHandoffSumMs, _statsHandoffMaxMs;

        private void NoteHandoffCompleted(float ms)
        {
            _statsHandoffs++;
            _statsHandoffSumMs += ms;
            if (ms > _statsHandoffMaxMs) _statsHandoffMaxMs = ms;
        }

        private void LogMeshStats()
        {
            long bytesOut = 0, packetsOut = 0, bytesIn = 0, packetsIn = 0;
            System.Text.StringBuilder links = new System.Text.StringBuilder();
            foreach (KeyValuePair<string, MeshLinkClient> kv in _linkClients)
            {
                NetStatistics st = kv.Value.Statistics;
                if (st == null) continue;
                bytesOut += st.BytesSent; packetsOut += st.PacketsSent;
                links.Append(kv.Key).Append(kv.Value.IsConnected ? "=" : "(down)=").Append(st.BytesSent / 1024f / meshStatsIntervalSeconds).Append("kB/s ");
            }
            NetStatistics sst = _linkServer != null ? _linkServer.Statistics : null;
            if (sst != null) { bytesIn = sst.BytesReceived; packetsIn = sst.PacketsReceived; }
            float dOut = (bytesOut - _statsLinkBytesOut) / 1024f / meshStatsIntervalSeconds;
            float dIn = (bytesIn - _statsLinkBytesIn) / 1024f / meshStatsIntervalSeconds;
            float pOut = (packetsOut - _statsLinkPacketsOut) / meshStatsIntervalSeconds;
            float pIn = (packetsIn - _statsLinkPacketsIn) / meshStatsIntervalSeconds;
            _statsLinkBytesOut = bytesOut; _statsLinkBytesIn = bytesIn; _statsLinkPacketsOut = packetsOut; _statsLinkPacketsIn = packetsIn;
            int players = 0;
            foreach (IPlayerCharacterData _ in ServerUserHandlers.GetPlayerCharacters()) players++;
            int ghostPlayers = 0, ghostMonsters = 0;
            foreach (MeshGhost g in _ghosts.Values) { if (g.kind == GhostKind.Player) ghostPlayers++; else ghostMonsters++; }
            string handoffs = _statsHandoffs > 0 ? $"{_statsHandoffs} (mean {_statsHandoffSumMs / _statsHandoffs:0} ms, max {_statsHandoffMaxMs:0} ms)" : "0";
            Logging.Log("Mesh", $"Mesh stats: players={players} owned={_ownedCandidates.Count} drops={_ownedDrops.Count} ghosts={ghostPlayers}p/{ghostMonsters}m/{_ghostDrops.Count}d pendingAcks={_pendingHandoffAcks.Count} reserved={_reservedArrivals.Count} | link out {dOut:0.0} kB/s {pOut:0} pkt/s, in {dIn:0.0} kB/s {pIn:0} pkt/s | handoffs {handoffs} | fps {1f / Mathf.Max(0.0001f, Time.smoothDeltaTime):0}");
            _statsHandoffs = 0; _statsHandoffSumMs = 0f; _statsHandoffMaxMs = 0f;
        }

        /// <summary>
        /// The band must give a crossing character time to exist as a ghost on the neighbour and be subscribed by
        /// its observers before the line (speed x (AOI pass + snapshot + a link hop) + hysteresis), and it must cover
        /// the kit's visible range or the far scene pops in at the crossing (entities between the band and the visible
        /// range are unknown to the origin). Logged at server start, as a warning when the configured band is short.
        /// </summary>
        private void LogBandSizing()
        {
            float aoi = 1f;
            var grid = InterestManager as JobifiedGridSpatialPartitioningAOI;
            if (grid != null) aoi = grid.updateInterval;
            float visible = InterestManager != null ? InterestManager.defaultVisibleRange : 0f;
            float speedBand = expectedMaxSpeed * (aoi + ghostSnapshotInterval + 0.2f) + handoffHysteresis;
            float required = Mathf.Max(speedBand, visible);
            string line = $"Band sizing: band={ghostBandWidth:0} m; for {expectedMaxSpeed:0.#} m/s with AOI every {aoi:0.##} s and snapshots every {ghostSnapshotInterval:0.##} s the crossing needs {speedBand:0.#} m; the visible range is {visible:0} m -> required {required:0.#} m";
            if (ghostBandWidth < required)
                Logging.LogWarning("Mesh", line + " (BAND TOO NARROW: " + (ghostBandWidth < visible ? "entities beyond the band pop in at the crossing" : "a fast crossing can outrun its ghost") + ")");
            else
                Logging.Log("Mesh", line);
        }
        private float _nextMonsterScan;
        private string _cellIncarnation;

#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        private void GhostingOnStartServer()
        {
            RegisterGhostActionHandlers();
            _cellIncarnation = System.DateTime.UtcNow.Ticks.ToString("x");
            if (!enableBorderGhosting)
                return;
            _linkServer = new MeshLinkServer(this);
            int port = networkPort + meshLinkPortOffset;
            if (_linkServer.StartServer(port, 64))
                Logging.Log("Mesh", $"Mesh link listening on {port}");
            else
                Logging.LogError("Mesh", $"Mesh link failed to listen on {port}");
            if (_linkServer.Statistics != null)
                ((LiteNetLibTransport)_linkServer.Transport).Server.EnableStatistics = true;
            LogBandSizing();
            // Owned registry: event-driven from here on (spawn/destroy), one scan for what is already in the scene.
            Assets.onObjectSpawn.AddListener(OnAssetObjectSpawn);
            Assets.onObjectDestroy.AddListener(OnAssetObjectDestroy);
            InitialOwnedScan();
        }

        private void GhostingOnStopServer()
        {
            Assets.onObjectSpawn.RemoveListener(OnAssetObjectSpawn);
            Assets.onObjectDestroy.RemoveListener(OnAssetObjectDestroy);
            foreach (MeshLinkClient client in _linkClients.Values)
                client.StopClient();
            _linkClients.Clear();
            _linkServer?.StopServer();
            _linkServer = null;
            _tmpIds.Clear(); _tmpIds.AddRange(_ghosts.Keys);
            for (int i = 0; i < _tmpIds.Count; ++i)
                DestroyGhost(_tmpIds[i], "server stopping");
            _tmpIds.Clear(); _tmpIds.AddRange(_ghostDrops.Keys);
            for (int i = 0; i < _tmpIds.Count; ++i)
                DestroyGhostDrop(_tmpIds[i], "server stopping");
            foreach (KeyValuePair<BaseMonsterCharacterEntity, CharacterEntityKilledDelegate> kv in _killHooks)
            {
                if (kv.Key != null)
                    kv.Key.onKilled -= kv.Value;
            }
            _ghosts.Clear();
            _ghostDrops.Clear();
            _playerGhosts.Clear();
            _recentlyRemoved.Clear();
            _streamedToZone.Clear();
            _neighbourZoneByConnection.Clear();
            _ownedCandidates.Clear();
            _ownedSet.Clear();
            _ownedByMeshId.Clear();
            _ownedDrops.Clear();
            _ownedDropSet.Clear();
            _ownedDropsByMeshId.Clear();
            _petGhostsBySummoner.Clear();
            _lentMonsters.Clear();
            _borrowedKillHooks.Clear();
            _killHooks.Clear();
            _reservedArrivals.Clear();
            _incomingHandoffFrom.Clear();
            _pendingHandoffAcks.Clear();
            _rolledBackHandoffs.Clear();
        }

        /// <summary>
        /// DESIGN §12 / risk #6: a map scene is shared by every cell of the logical map, so without this each
        /// cell would spawn every spawn area's monsters (double spawn). Deactivate areas outside our zone
        /// before the base class discovers them.
        /// </summary>
        public override void OnServerOnlineSceneLoaded()
        {
            if (ownSpawnAreasByZone && meshConfig != null && TryResolveOwnZone())
            {
                int kept = 0, disabled = 0;
                foreach (MonsterSpawnArea area in FindObjectsByType<MonsterSpawnArea>(FindObjectsSortMode.None))
                {
                    if (meshConfig.WorldToZoneCoord(area.transform.position) == OwnZoneCoord) { kept++; continue; }
                    area.gameObject.SetActive(false); disabled++;
                }
                foreach (HarvestableSpawnArea area in FindObjectsByType<HarvestableSpawnArea>(FindObjectsSortMode.None))
                {
                    if (meshConfig.WorldToZoneCoord(area.transform.position) == OwnZoneCoord) { kept++; continue; }
                    area.gameObject.SetActive(false); disabled++;
                }
                foreach (ItemDropSpawnArea area in FindObjectsByType<ItemDropSpawnArea>(FindObjectsSortMode.None))
                {
                    if (meshConfig.WorldToZoneCoord(area.transform.position) == OwnZoneCoord) { kept++; continue; }
                    area.gameObject.SetActive(false); disabled++;
                }
                Logging.Log("Mesh", $"Spawn areas owned by zone {OwnZoneId}: {kept} kept, {disabled} disabled (outside zone)");
            }
            base.OnServerOnlineSceneLoaded();
        }

        private void GhostingUpdate()
        {
            _linkServer?.Update();
            foreach (MeshLinkClient client in _linkClients.Values)
                client.Update();

            if (!enableBorderGhosting || meshConfig == null || !HasOwnZone)
                return;

            if (Time.unscaledTime >= _nextLinkCheck)
            {
                _nextLinkCheck = Time.unscaledTime + 1f;
                EnsureNeighbourLinks();
            }
            if (meshStatsIntervalSeconds > 0f && Time.unscaledTime >= _nextStatsTime)
            {
                _nextStatsTime = Time.unscaledTime + meshStatsIntervalSeconds;
                LogMeshStats();
            }
            if (Time.unscaledTime >= _nextMonsterScan)
            {
                _nextMonsterScan = Time.unscaledTime + 5f;
                PruneOwned();
            }
            if (Time.unscaledTime >= _nextSnapshotTime)
            {
                _nextSnapshotTime = Time.unscaledTime + Mathf.Max(0.02f, ghostSnapshotInterval);
                MonsterHandoffTick();
                TickHandoffAcks();
                SendBandSnapshots();
                ExpireGhosts();
                ExpireGhostDrops();
                ExpireReservedArrivals();
            }
        }

        /// <summary>Open (or re-open) one link per 4-neighbour zone whose map server the cluster has told us about.</summary>
        [Tooltip("Link to every cell of the logical map, not only the 8 neighbours. Ghosting still stops at the band; the extra links carry cell-to-cell notices such as a party share for a member that stands on a far cell (unlimited share distance).")]
        public bool linkAllZones = true;

        private void EnsureNeighbourLinks()
        {
            string mapName = CurrentMapInfo != null ? CurrentMapInfo.Id : string.Empty;
            if (linkAllZones && meshConfig.zones != null)
            {
                for (int i = 0; i < meshConfig.zones.Length; ++i)
                    EnsureLink(meshConfig.zones[i], mapName);
                return;
            }
            for (int i = 0; i < s_neighbourOffsets.Length; ++i)
                EnsureLink(meshConfig.GetZone(NeighbourCoord(i)), mapName);
        }

        private void EnsureLink(ServerMeshTestConfig.ZoneDefinition zone, string mapName)
        {
            if (zone == null || string.Equals(zone.zoneId, OwnZoneId))
                return;
            if (!_meshPeers.TryGetValue(PeerInfoExtensions.GetPeerInfoKey(zone.zoneId, mapName), out CentralServerPeerInfo peer))
                return;
            if (!_linkClients.TryGetValue(zone.zoneId, out MeshLinkClient client))
            {
                client = new MeshLinkClient(this, zone.zoneId);
                _linkClients[zone.zoneId] = client;
            }
            if (!client.IsConnected && Time.unscaledTime - client.LastConnectAttempt > 5f)
                client.Connect(peer.networkAddress, peer.networkPort + meshLinkPortOffset);
        }

        // 8-neighbourhood: the four edge neighbours plus the diagonals. Near a corner an entity is within the band of a
        // diagonal cell too (whose players can be a few metres away across the corner), so it is streamed there as well.
        private static readonly Vector2Int[] s_neighbourOffsets =
        {
            Vector2Int.right, Vector2Int.left, Vector2Int.up, Vector2Int.down,
            new Vector2Int(1, 1), new Vector2Int(-1, 1), new Vector2Int(1, -1), new Vector2Int(-1, -1),
        };
        private Vector2Int NeighbourCoord(int i) => OwnZoneCoord + s_neighbourOffsets[i];

        /// <summary>Distance from a world position to the edge shared with the neighbour at offset index i.</summary>
        /// <summary>
        /// Distance from a position to the boundary shared with neighbour i: the edge line for an edge neighbour, and
        /// for a diagonal neighbour the Chebyshev distance to the shared corner (within the band of both edges).
        /// </summary>
        private float DistanceToSharedEdge(Vector3 pos, int i)
        {
            Vector2Int off = s_neighbourOffsets[i];
            float dx = 0f, dz = 0f;
            if (off.x != 0)
            {
                float lineX = meshConfig.gridOrigin.x + Mathf.Max(OwnZoneCoord.x, OwnZoneCoord.x + off.x) * meshConfig.zoneSize;
                dx = Mathf.Abs(pos.x - lineX);
            }
            if (off.y != 0)
            {
                float lineZ = meshConfig.gridOrigin.z + Mathf.Max(OwnZoneCoord.y, OwnZoneCoord.y + off.y) * meshConfig.zoneSize;
                dz = Mathf.Abs(pos.z - lineZ);
            }
            return Mathf.Max(dx, dz);
        }

        // ---- Owned registry ----
        // Owned = registered players + server-owned monsters (and item drops) that are not ghosts. Kept by events:
        // the asset manager's spawn/destroy, player registration, and the handoff/promotion/demotion paths that change
        // what an entity is. No per-second FindObjectsByType; a rare prune drops references the events could not
        // (a pooled instance destroyed outside the network path).
        private readonly Dictionary<string, BaseCharacterEntity> _ownedByMeshId = new Dictionary<string, BaseCharacterEntity>();
        private readonly HashSet<BaseCharacterEntity> _ownedSet = new HashSet<BaseCharacterEntity>();   // O(1) membership for _ownedCandidates
        private readonly HashSet<ItemDropEntity> _ownedDropSet = new HashSet<ItemDropEntity>();         // O(1) membership for _ownedDrops
        private bool _spawningPuppet; // set around the ghosting code's own NetworkSpawn calls: those are never ours

        /// <summary>The key an owned entity is registered under: players by character id, everything else by mesh id.</summary>
        private static string OwnedKeyOf(Component entity)
        {
            if (entity is BasePlayerCharacterEntity player)
                return player.Id;
            MeshIdentity id = MeshIdentity.Get(entity);
            return id != null ? id.MeshEntityId : null;
        }

        /// <summary>A server-spawned entity's mesh id is its birth provenance "{zone}:{incarnation}:{objectId}", assigned once.</summary>
        private MeshIdentity EnsureBirthMeshId(Component entity, uint objectId)
        {
            MeshIdentity id = MeshIdentity.GetOrAdd(entity);
            if (string.IsNullOrEmpty(id.MeshEntityId))
            {
                id.MeshEntityId = $"{OwnZoneId}:{_cellIncarnation}:{objectId}"; // never the current owner
                id.OwnerZoneId = OwnZoneId;
            }
            return id;
        }

        private void OnAssetObjectSpawn(LiteNetLibIdentity identity)
        {
            if (!IsServer || _spawningPuppet || identity == null || !identity.TryGetComponent(out BaseGameEntity spawned))
                return;
            if (spawned is BaseMonsterCharacterEntity monster)
            {
                if (ghostMonsters && monster.IsOwnedByServer)
                    TrackOwned(monster);
            }
            else if (spawned is ItemDropEntity drop)
                TrackOwnedDrop(drop);
        }

        private void OnAssetObjectDestroy(LiteNetLibIdentity identity)
        {
            if (identity != null && identity.TryGetComponent(out BaseGameEntity destroyed))
                UntrackOwned(destroyed);
        }

        /// <summary>Register an entity this cell owns (or re-key it after a handoff changed its mesh id: untrack first).</summary>
        private void TrackOwned(BaseCharacterEntity entity)
        {
            if (entity == null || entity.Identity == null || entity.Identity.IsDestroyed || MeshIdentity.IsGhostEntity(entity))
                return;
            if (!(entity is BasePlayerCharacterEntity))
                EnsureBirthMeshId(entity, entity.ObjectId);
            _ownedByMeshId[OwnedKeyOf(entity)] = entity;
            if (_ownedSet.Add(entity))
                _ownedCandidates.Add(entity);
            if (entity is BaseMonsterCharacterEntity monster && !_killHooks.ContainsKey(monster))
            {
                BaseMonsterCharacterEntity captured = monster;
                CharacterEntityKilledDelegate hook = (target, instigator) => OnOwnedMonsterKilled(captured, instigator);
                monster.onKilled += hook;
                _killHooks[monster] = hook;
            }
        }

        private void TrackOwnedDrop(ItemDropEntity drop)
        {
            if (drop == null || !drop.IsServer || drop.Identity == null || drop.Identity.IsDestroyed || MeshIdentity.IsGhostEntity(drop))
                return;
            MeshIdentity id = EnsureBirthMeshId(drop, drop.ObjectId);
            _ownedDropsByMeshId[id.MeshEntityId] = drop;
            if (_ownedDropSet.Add(drop))
                _ownedDrops.Add(drop);
        }

        /// <summary>Forget an entity: destroyed, demoted to a puppet, or unregistered.</summary>
        private void UntrackOwned(Component entity)
        {
            if (entity == null)
                return;
            string key = OwnedKeyOf(entity);
            if (entity is BaseCharacterEntity character)
            {
                if (_ownedSet.Remove(character))
                    _ownedCandidates.Remove(character);
                if (key != null && _ownedByMeshId.TryGetValue(key, out BaseCharacterEntity under) && ReferenceEquals(under, character))
                    _ownedByMeshId.Remove(key);
                if (character is BaseMonsterCharacterEntity monster)
                    UnhookKill(monster);
            }
            else if (entity is ItemDropEntity drop)
            {
                if (_ownedDropSet.Remove(drop))
                    _ownedDrops.Remove(drop);
                if (key != null && _ownedDropsByMeshId.TryGetValue(key, out ItemDropEntity dropUnder) && ReferenceEquals(dropUnder, drop))
                    _ownedDropsByMeshId.Remove(key);
            }
        }

        /// <summary>What is already in the scene when the server starts (spawn areas spawn before the listeners exist).</summary>
        private void InitialOwnedScan()
        {
            foreach (IPlayerCharacterData data in ServerUserHandlers.GetPlayerCharacters())
            {
                if (data is BasePlayerCharacterEntity entity)
                    TrackOwned(entity);
            }
            if (ghostMonsters)
            {
                foreach (BaseMonsterCharacterEntity monster in FindObjectsByType<BaseMonsterCharacterEntity>(FindObjectsSortMode.None))
                {
                    if (monster != null && monster.IsServer && monster.IsOwnedByServer && !MeshIdentity.IsGhostEntity(monster))
                        TrackOwned(monster);
                }
            }
            foreach (ItemDropEntity drop in FindObjectsByType<ItemDropEntity>(FindObjectsSortMode.None))
                TrackOwnedDrop(drop);
            Logging.Log("Mesh", $"Owned registry: {_ownedCandidates.Count} entities, {_ownedDrops.Count} drops at start");
        }

        /// <summary>Safety net for references no event released (rare). Runs every few seconds, touches only our own lists.</summary>
        private void PruneOwned()
        {
            PruneKillHooks();
            for (int i = _ownedCandidates.Count - 1; i >= 0; --i)
            {
                BaseCharacterEntity e = _ownedCandidates[i];
                if (e == null) { _ownedCandidates.RemoveAt(i); continue; }
                if (e.Identity == null || e.Identity.IsDestroyed || MeshIdentity.IsGhostEntity(e))
                    UntrackOwned(e);
            }
            _ownedSet.RemoveWhere(e => e == null);
            for (int i = _ownedDrops.Count - 1; i >= 0; --i)
            {
                ItemDropEntity d = _ownedDrops[i];
                if (d == null) { _ownedDrops.RemoveAt(i); continue; }
                if (d.Identity == null || d.Identity.IsDestroyed || MeshIdentity.IsGhostEntity(d))
                    UntrackOwned(d);
            }
            _ownedDropSet.RemoveWhere(d => d == null);
            _tmpIds.Clear();
            foreach (KeyValuePair<string, BaseCharacterEntity> kv in _ownedByMeshId)
                if (kv.Value == null) _tmpIds.Add(kv.Key);
            for (int i = 0; i < _tmpIds.Count; ++i)
                _ownedByMeshId.Remove(_tmpIds[i]);
            _tmpIds.Clear();
            foreach (KeyValuePair<string, ItemDropEntity> kv in _ownedDropsByMeshId)
                if (kv.Value == null) _tmpIds.Add(kv.Key);
            for (int i = 0; i < _tmpIds.Count; ++i)
                _ownedDropsByMeshId.Remove(_tmpIds[i]);
        }

        private string MeshEntityIdOf(BaseCharacterEntity entity)
        {
            if (entity is BasePlayerCharacterEntity)
                return entity.Id;
            MeshIdentity id = MeshIdentity.Get(entity);
            return id != null ? id.MeshEntityId : entity.Id;
        }

        /// <summary>For a summoned monster: who summoned it (mesh id) and which CharacterSummon entry it is on them.</summary>
        private void SummonInfoOf(BaseCharacterEntity entity, out string summonerMeshId, out string summonId, out SummonType summonType)
        {
            summonerMeshId = string.Empty;
            summonId = string.Empty;
            summonType = SummonType.None;
            BaseMonsterCharacterEntity monster = entity as BaseMonsterCharacterEntity;
            if (monster == null || !monster.IsSummoned)
                return;
            BaseCharacterEntity summoner = monster.SummonerEntity;
            if (summoner == null)
                return;
            summonType = monster.SummonType;
            summonerMeshId = MeshEntityIdOf(summoner);
            for (int i = 0; i < summoner.Summons.Count; ++i)
            {
                // By the cached entity first: the kit's spawn-time re-summon does not write the new object id back
                // into the list, so a pet summoned at login carries a stale objectId until the next Update.
                CharacterSummon summon = summoner.Summons[i];
                if (ReferenceEquals(summon.CacheEntity, monster) || summon.objectId == monster.ObjectId)
                {
                    summonId = summon.id;
                    break;
                }
            }
        }

        private void SendBandSnapshots()
        {
            for (int i = 0; i < s_neighbourOffsets.Length; ++i)
            {
                ServerMeshTestConfig.ZoneDefinition zone = meshConfig.GetZone(NeighbourCoord(i));
                if (zone == null || !_linkClients.TryGetValue(zone.zoneId, out MeshLinkClient client) || !client.IsConnected)
                    continue;
                if (!_streamedToZone.TryGetValue(zone.zoneId, out HashSet<string> streamed))
                {
                    streamed = new HashSet<string>();
                    _streamedToZone[zone.zoneId] = streamed;
                }
                _tmpIds.Clear();
                _tmpIds.AddRange(streamed);
                streamed.Clear();

                for (int c = 0; c < _ownedCandidates.Count; ++c)
                {
                    BaseCharacterEntity entity = _ownedCandidates[c];
                    if (entity == null || entity.IsDead() || MeshIdentity.IsGhostEntity(entity))
                        continue; // a demoted entity is someone else's now
                    if (entity is BasePlayerCharacterEntity player && player.IsWarping)
                        continue;
                    Vector3 pos = entity.EntityTransform.position;
                    if (DistanceToSharedEdge(pos, i) > ghostBandWidth)
                        continue;
                    string meshId = MeshEntityIdOf(entity);
                    VehicleEntity mount = entity.PassengingVehicleEntity as VehicleEntity;
                    SummonInfoOf(entity, out string summonerMeshId, out string summonId, out SummonType summonType);
                    EquipWeapons weapons = entity is BasePlayerCharacterEntity ? entity.EquipWeapons : default;
                    client.SendGhostSnapshot(new GhostSnapshotMessage
                    {
                        rightWeaponDataId = weapons.rightHand.dataId,
                        rightWeaponLevel = weapons.rightHand.level,
                        leftWeaponDataId = weapons.leftHand.dataId,
                        leftWeaponLevel = weapons.leftHand.level,
                        summonerMeshId = summonerMeshId,
                        summonId = summonId,
                        summonType = (byte)summonType,
                        mountEntityId = mount != null ? mount.EntityId : 0,
                        mountLevel = mount != null ? mount.Level : 0,
                        kind = entity is BasePlayerCharacterEntity ? GhostKind.Player : GhostKind.Monster,
                        characterId = meshId,
                        entityId = entity.EntityId,
                        dataId = entity.DataId,
                        characterName = entity.CharacterName,
                        level = entity.Level,
                        position = pos,
                        yaw = entity.EntityTransform.eulerAngles.y,
                        movementState = (byte)entity.MovementState,
                        extraMovementState = (byte)entity.ExtraMovementState,
                        currentHp = entity.CurrentHp,
                        maxHp = entity.MaxHp,
                    });
                    streamed.Add(meshId);
                }

                // Item drops in the band (loot from a kill near the line must be visible, and lootable, from next door).
                for (int d = 0; d < _ownedDrops.Count; ++d)
                {
                    ItemDropEntity drop = _ownedDrops[d];
                    if (drop == null || drop.Identity == null || drop.Identity.IsDestroyed || drop.DropItems.Count == 0)
                        continue;
                    Vector3 dpos = drop.EntityTransform.position;
                    if (DistanceToSharedEdge(dpos, i) > ghostBandWidth)
                        continue;
                    MeshIdentity did = MeshIdentity.Get(drop);
                    if (did == null || string.IsNullOrEmpty(did.MeshEntityId))
                        continue;
                    CharacterItem item = drop.DropItems[0];
                    client.SendGhostSnapshot(new GhostSnapshotMessage
                    {
                        kind = GhostKind.ItemDrop,
                        characterId = did.MeshEntityId,
                        entityId = drop.EntityId,
                        dataId = item.dataId,
                        characterName = string.Empty,
                        level = item.level,
                        position = dpos,
                        yaw = drop.EntityTransform.eulerAngles.y,
                        currentHp = item.amount,
                        maxHp = item.amount,
                    });
                    streamed.Add(did.MeshEntityId);
                }

                // Anything streamed last tick but not now has left the band (or this cell, or died): tell the neighbour.
                for (int k = 0; k < _tmpIds.Count; ++k)
                {
                    if (!streamed.Contains(_tmpIds[k]))
                        client.SendGhostRemove(_tmpIds[k]);
                }
            }
        }

        private void SendGhostRemoveToAll(string meshId)
        {
            foreach (KeyValuePair<string, MeshLinkClient> kv in _linkClients)
            {
                if (_streamedToZone.TryGetValue(kv.Key, out HashSet<string> streamed) && streamed.Remove(meshId))
                    kv.Value.SendGhostRemove(meshId);
            }
        }

        private void ExpireGhosts()
        {
            _tmpIds.Clear();
            foreach (KeyValuePair<string, MeshGhost> kv in _ghosts)
            {
                if (kv.Value.entity == null || (!kv.Value.reserved && Time.unscaledTime - kv.Value.lastSeen > ghostStaleSeconds && Time.unscaledTime >= kv.Value.holdUntil && !IsAwaitingHandoffAck(kv.Key, kv.Value)))
                    _tmpIds.Add(kv.Key);
            }
            for (int i = 0; i < _tmpIds.Count; ++i)
            {
                if (_rolledBackHandoffs.Remove(_tmpIds[i]))
                    Logging.LogWarning("Mesh", $"Handoff ROLLBACK of {_tmpIds[i]} expired: its client never returned to {OwnZoneId}; a later login spawns it fresh");
                DestroyGhost(_tmpIds[i], "stale");
            }
            if (_recentlyRemoved.Count > 0)
            {
                _tmpIds.Clear();
                foreach (KeyValuePair<string, float> kv in _recentlyRemoved)
                    if (Time.unscaledTime - kv.Value > RECENTLY_REMOVED_SECONDS) _tmpIds.Add(kv.Key);
                for (int i = 0; i < _tmpIds.Count; ++i)
                    _recentlyRemoved.Remove(_tmpIds[i]);
            }
        }

        private void DestroyGhost(string meshId, string reason)
        {
            if (!_ghosts.TryGetValue(meshId, out MeshGhost ghost))
                return;
            _ghosts.Remove(meshId);
            _recentlyRemoved[meshId] = Time.unscaledTime;
            SetGhostSummoner(meshId, ghost, null, null);
            VehicleEntity mount = ghost.vehicle;
            if (ghost.entity != null)
            {
                Logging.Log("Mesh", $"Ghost destroyed {ghost.kind} {ghost.entity.CharacterName} ({meshId}) reason={reason}");
                _playerGhosts.Remove(ghost.entity);
                if (ghost.entity.PassengingVehicleEntity is VehicleEntity ridden)
                    mount = ridden;
                // Nothing of the puppet survives into a pooled reuse of the instance: the hit hook and the identity.
                if (ghost.hitHook != null)
                    ghost.entity.onReceiveDamage -= ghost.hitHook;
                UnlinkSummonPuppets(meshId);
                ResetMeshIdentity(ghost.entity);
                ghost.entity.NetworkDestroy();
            }
            if (mount != null && mount.Identity != null && !mount.Identity.IsDestroyed)
                mount.NetworkDestroy();
        }

        /// <summary>Remove the runtime identity so a pooled instance never comes back flagged as someone's ghost.</summary>
        private static void ResetMeshIdentity(Component entity)
        {
            MeshIdentity id = MeshIdentity.Get(entity);
            if (id != null)
                Destroy(id);
        }

        // Ghosts by kind for cheap proximity queries (IsPlayerGhostNear), and ids removed a moment ago so a snapshot
        // that overtakes its GhostRemove (snapshots are on the sequenced channel) does not resurrect the puppet.
        private readonly List<BaseCharacterEntity> _playerGhosts = new List<BaseCharacterEntity>();
        private readonly Dictionary<string, float> _recentlyRemoved = new Dictionary<string, float>();
        private const float RECENTLY_REMOVED_SECONDS = 1f;

        /// <summary>Called when the real character registers on this cell (handoff-in): the ghost must go.</summary>
        private void GhostingOnRealPlayerRegistered(BasePlayerCharacterEntity playerCharacter, long connectionId)
        {
            if (playerCharacter == null)
                return;
            _pendingHandoffAcks.Remove(playerCharacter.Id); // it is ours again (a handoff back before the ack, or a fresh login here)
            if (_rolledBackHandoffs.Remove(playerCharacter.Id))
                Logging.Log("Mesh", $"Handoff ROLLBACK complete: {playerCharacter.CharacterName} is back on {OwnZoneId} ({(_reservedArrivals.ContainsKey(playerCharacter.Id) ? "its puppet promoted" : "fresh spawn")})");
            TrackOwnedPlayerAfterRegistration(playerCharacter);
            bool promoted = false;
            if (_reservedArrivals.TryGetValue(playerCharacter.Id, out ReservedArrival reserved) && playerCharacter.Identity != null)
            {
                _reservedArrivals.Remove(playerCharacter.Id);
                if (!ReferenceEquals(reserved.entity, playerCharacter))
                {
                    // A fresh kit spawn registered instead (the client re-entered): the reserved puppet must go, and so
                    // must the pet puppets that were waiting for it (the kit re-summoned the pets for the fresh spawn).
                    DestroyGhost(playerCharacter.Id, "superseded by a fresh spawn");
                    DestroyLeftoverSummonPuppets(playerCharacter, "superseded by a fresh spawn");
                    AckHandoffArrival(playerCharacter.Id, playerCharacter.CharacterName, false);
                    return;
                }
                promoted = true;
                // Finish the promotion: from here on this is the real character.
                if (_ghosts.TryGetValue(playerCharacter.Id, out MeshGhost ghost))
                    PromoteGhostInPlace(playerCharacter.Id, ghost);
                TrackOwned(playerCharacter);
                // A fresh spawn is pushed to its owner at once. A re-owned entity would wait for the AOI's next pass
                // (1 s): the kit's SetOwnerClient is deferred by the AOI (manuallyApplyOwnerChanges) and the owner's
                // subscription only comes with that pass. Apply the owner now and subscribe the owner now.
                Assets.SetObjectOwnerImmediately(playerCharacter.ObjectId, connectionId);
                if (InterestManager != null)
                    InterestManager.NotifyNewObject(playerCharacter.Identity);
                // The mount: the kept vehicle is re-owned by the driver the same way; otherwise re-summon from the
                // saved data (what the kit's fresh-spawn path does). Summons likewise.
                if (reserved.keepMount && reserved.mount != null && reserved.mount.Identity != null && !reserved.mount.Identity.IsDestroyed)
                {
                    Assets.SetObjectOwnerImmediately(reserved.mount.ObjectId, connectionId);
                    if (InterestManager != null)
                        InterestManager.NotifyNewObject(reserved.mount.Identity);
                }
                else if (!playerCharacter.IsDead() && playerCharacter.Mount.type != MountType.None)
                {
                    playerCharacter.SpawnMount(playerCharacter.Mount.type, playerCharacter.Mount.sourceId, playerCharacter.Mount.mountRemainsDuration, playerCharacter.Mount.level, playerCharacter.Mount.currentHp);
                }
                if (!playerCharacter.IsDead())
                {
                    for (int i = 0; i < playerCharacter.Summons.Count; ++i)
                    {
                        CharacterSummon summon = playerCharacter.Summons[i];
                        // The pet came along as a puppet: promote it in place (no pop). Otherwise re-summon from the
                        // saved data, as the kit's fresh-spawn path does.
                        if (!TryPromoteSummonPuppet(playerCharacter, ref summon))
                            summon.Summon(playerCharacter, summon.level, summon.summonRemainsDuration, summon.exp, summon.currentHp, summon.currentMp);
                        playerCharacter.Summons[i] = summon;
                    }
                }
            }
            else if (!playerCharacter.IsDead())
            {
                // A fresh kit spawn (no puppet of this character was here to promote): the kit already re-summoned the
                // saved summons, so any pet puppet still linked to this character is a leftover of the departing cell's
                // stream — destroy it now instead of letting it stand next to the new pet until it expires stale.
                DestroyLeftoverSummonPuppets(playerCharacter, "superseded by the kit's re-summon");
            }
            // The ack goes out once the registration path has decided what happened: the origin releases the pets it
            // demoted only when we did NOT promote (their mesh ids are dead over here otherwise).
            AckHandoffArrival(playerCharacter.Id, playerCharacter.CharacterName, promoted);
            DestroyGhost(playerCharacter.Id, "promoted to real");
        }

        private void DestroyLeftoverSummonPuppets(BasePlayerCharacterEntity playerCharacter, string reason)
        {
            for (int i = 0; i < playerCharacter.Summons.Count; ++i)
                DestroySummonPuppets(playerCharacter.Id, playerCharacter.Summons[i].id, reason);
        }

        // Pet puppets are scanned by summoner on their own list: the callers sit inside loops over _tmpIds.
        private readonly List<string> _tmpPetIds = new List<string>();

        /// <summary>
        /// Destroy the pet puppets of a summoner here: a null summon id means every one of them; <paramref name="onlyBornHere"/>
        /// keeps the ones another cell streams (their mesh ids carry that cell's prefix), <paramref name="skipReserved"/>
        /// keeps the ones waiting with an arriving player.
        /// </summary>
        private void DestroySummonPuppets(string summonerMeshId, string summonId, string reason, bool onlyBornHere = false, bool skipReserved = false)
        {
            string ownPrefix = OwnZoneId + ":";
            _tmpPetIds.Clear();
            foreach (string petId in PetGhostsOf(summonerMeshId))
            {
                if (!_ghosts.TryGetValue(petId, out MeshGhost g) || g.kind != GhostKind.Monster)
                    continue;
                if (summonId != null && !string.Equals(g.summonId, summonId))
                    continue;
                if (skipReserved && g.reserved)
                    continue;
                if (onlyBornHere && !petId.StartsWith(ownPrefix))
                    continue;
                _tmpPetIds.Add(petId);
            }
            for (int i = 0; i < _tmpPetIds.Count; ++i)
                DestroyGhost(_tmpPetIds[i], reason);
        }

        // ---- Arrival by handoff: promote the ghost in place (DESIGN §7) ----
        // The kit's SetPlayerReadyRoutine has an "already spawned entity" branch (relates data, SetOwnerClient,
        // Register, respawn RPC). Handing it the ghost entity means the arriving character IS the entity observers
        // here were already looking at: no swap, no pop.

        private sealed class ReservedArrival { public BasePlayerCharacterEntity entity; public string userId; public float time; public VehicleEntity mount; public bool keepMount; }
        private readonly Dictionary<string, ReservedArrival> _reservedArrivals = new Dictionary<string, ReservedArrival>();
        [Tooltip("If an arriving client never becomes ready, its promoted ghost is destroyed after this many seconds.")]
        public float reservedArrivalTimeout = 15f;

        private bool TryPromoteGhostForArrival(long connectionId, string userId, PlayerCharacterData data, out BasePlayerCharacterEntity entity)
        {
            entity = null;
            if (data == null || !_ghosts.TryGetValue(data.Id, out MeshGhost ghost) || ghost.entity == null || ghost.reserved)
                return false;
            BasePlayerCharacterEntity player = ghost.entity as BasePlayerCharacterEntity;
            if (player == null || player.Identity == null || player.Identity.IsDestroyed || player.EntityId != data.EntityId)
                return false;
            // The puppet stays a puppet (silenced, invincible, still in _ghosts but no longer steered or expired) until
            // the client's ready step registers it; only then is it un-ghosted. If ready never comes, nothing
            // vulnerable or AI-driven ever stood here, and ExpireReservedArrivals reclaims it.
            ghost.reserved = true;
            ghost.lastSeen = Time.unscaledTime;
            // The pets that came along as puppets wait with the player: the origin stopped streaming them at demotion,
            // so without this they would expire stale before a slow client registers and could not be promoted.
            foreach (string petId in PetGhostsOf(data.Id))
            {
                if (_ghosts.TryGetValue(petId, out MeshGhost pet))
                {
                    pet.reserved = true;
                    pet.lastSeen = Time.unscaledTime;
                }
            }
            MeshIdentity id = MeshIdentity.GetOrAdd(player);
            id.LastHandoffTime = Time.unscaledTime;
            // The mount: keep the vehicle the ghost is riding when it is the one the saved data says (the usual case),
            // otherwise drop it and let the registration re-summon from the saved data like a fresh spawn would.
            VehicleEntity ridden = player.PassengingVehicleEntity as VehicleEntity;
            bool keepMount = false;
            if (ridden != null)
            {
                VehicleEntity wanted = null;
                if (data.Mount.type != MountType.None)
                    data.Mount.type.GetPrefab(data, data.Mount.sourceId, out wanted);
                keepMount = wanted != null && wanted.EntityId == ridden.EntityId;
                if (!keepMount)
                {
                    player.ExitVehicleAndForget();
                    if (ridden.Identity != null && !ridden.Identity.IsDestroyed)
                        ridden.NetworkDestroy();
                    ridden = null;
                }
            }
            // Same setup the kit does for a fresh spawn, on the existing object.
            GameInstance.GetPlayerCharacterEntityHashAssetId(data.EntityId, out int metaDataId);
            GameInstance.SetupByMetaData(player, metaDataId);
            data.CloneTo(player);
            player.Teleport(data.CurrentPosition, Quaternion.Euler(data.CurrentRotation), false);
            player.ForceMakeCaches();
            player.FillEmptySlots();
            player.Identity.DoNotDestroyWhenDisconnect = true;
            _reservedArrivals[data.Id] = new ReservedArrival { entity = player, userId = userId, time = Time.unscaledTime, mount = ridden, keepMount = keepMount };
            Logging.Log("Mesh", $"Arrival {data.CharacterName} ({data.Id}) conn={connectionId}: ghost promoted in place at {player.EntityTransform.position} mount={(keepMount ? "kept" : data.Mount.type != MountType.None ? "re-summon" : "none")}");
            entity = player;
            return true;
        }

        private void ExpireReservedArrivals()
        {
            if (_reservedArrivals.Count == 0)
                return;
            _tmpIds.Clear();
            foreach (KeyValuePair<string, ReservedArrival> kv in _reservedArrivals)
            {
                if (kv.Value.entity == null || Time.unscaledTime - kv.Value.time > reservedArrivalTimeout)
                    _tmpIds.Add(kv.Key);
            }
            for (int i = 0; i < _tmpIds.Count; ++i)
            {
                ReservedArrival r = _reservedArrivals[_tmpIds[i]];
                _reservedArrivals.Remove(_tmpIds[i]);
                if (ServerUserHandlers.TryGetPlayerCharacterById(_tmpIds[i], out _))
                    continue;
                // The kit must not hand this entity to a late ready step either.
                if (!string.IsNullOrEmpty(r.userId) && _pendingSpawnPlayerCharactersByUserId.TryGetValue(r.userId, out IPlayerCharacterData pending) && ReferenceEquals(pending, r.entity))
                    _pendingSpawnPlayerCharactersByUserId.TryRemove(r.userId, out _);
                Logging.LogWarning("Mesh", $"Arrival {_tmpIds[i]} was never claimed by its client; the reserved puppet is destroyed");
                AckHandoffFailed(_tmpIds[i], "arrival never claimed by its client");
                DestroySummonPuppets(_tmpIds[i], null, "arrival never claimed");
                if (_ghosts.TryGetValue(_tmpIds[i], out MeshGhost g) && ReferenceEquals(g.entity, r.entity))
                    DestroyGhost(_tmpIds[i], "arrival never claimed");
                else if (r.entity != null && r.entity.Identity != null && !r.entity.Identity.IsDestroyed)
                    r.entity.NetworkDestroy();
            }
        }

        private void GhostingOnRealPlayerUnregistered(long connectionId)
        {
            if (ServerUserHandlers.TryGetPlayerCharacter(connectionId, out IPlayerCharacterData data) && data != null)
            {
                SendGhostRemoveToAll(data.Id);
                if (data is BasePlayerCharacterEntity entity)
                    UntrackOwned(entity);
            }
        }

        /// <summary>
        /// A registering player is owned from now on. For a promoted arrival the ghost flag is cleared further down
        /// the registration path, so the tracking is deferred to the end of that path (see GhostingOnRealPlayerRegistered).
        /// </summary>
        private void TrackOwnedPlayerAfterRegistration(BasePlayerCharacterEntity playerCharacter)
        {
            if (!MeshIdentity.IsGhostEntity(playerCharacter))
                TrackOwned(playerCharacter);
        }

        /// <summary>
        /// Player handoff to <paramref name="toZone"/> is starting: forget that we streamed this character ANYWHERE so
        /// no GhostRemove follows. The destination promotes its ghost when the real character registers, and every
        /// other neighbour keeps its ghost, which the new owner's snapshots take over (owner zone refreshed per
        /// snapshot); a ghost the new owner does not stream to expires stale on its own.
        /// </summary>
        private void GhostingOnPlayerHandoffStart(string characterId, string toZone)
        {
            foreach (HashSet<string> streamed in _streamedToZone.Values)
                streamed.Remove(characterId);
        }

        /// <summary>
        /// The departing player's entity stays as a ghost puppet: server-owned (so the client's disconnect does not
        /// destroy it), silenced, invincible, steered by the new owner's snapshots and removed when they stop.
        /// </summary>
        private void DemotePlayerToGhost(BasePlayerCharacterEntity entity, string newOwnerZone)
        {
            if (entity == null || entity.Identity == null || entity.Identity.IsDestroyed)
                return;
            string meshId = entity.Id;
            Assets.SetObjectOwnerImmediately(entity.ObjectId, -1);
            VehicleEntity riddenMount = entity.PassengingVehicleEntity as VehicleEntity;
            if (riddenMount != null && riddenMount.Identity != null)
                Assets.SetObjectOwnerImmediately(riddenMount.ObjectId, -1); // the mount survives the disconnect too
            MeshIdentity id = MeshIdentity.GetOrAdd(entity);
            id.MeshEntityId = meshId;
            MakeGhostInPlace(entity, id, GhostKind.Player, newOwnerZone, riddenMount);
            int pets = DemoteSummonsWith(entity, newOwnerZone);
            Logging.Log("Mesh", $"Player {entity.CharacterName} demoted to ghost (owner {newOwnerZone}, mounted={riddenMount != null}, summons={pets})");
        }

        internal void OnMeshNeighbourHello(long connectionId, string zoneId)
        {
            _neighbourZoneByConnection[connectionId] = zoneId;
            Logging.Log("Mesh", $"Neighbour zone {zoneId} linked (conn {connectionId})");
        }

        /// <summary>
        /// A neighbour's link dropped (it died, or the network between us did). Nothing of ours may depend on it any
        /// longer: monsters it held for us respawn at home, monsters we hold for it go (its spawn areas own their
        /// respawn and will re-spawn them when it returns), handoffs we sent it and never saw acknowledged roll back.
        /// Its ghosts here expire stale on their own; a client mid-arrival from it is still served (the notice and
        /// the warp were sent before it died).
        /// </summary>
        internal void OnMeshNeighbourDisconnected(long connectionId)
        {
            if (_neighbourZoneByConnection.TryGetValue(connectionId, out string zoneId))
            {
                _neighbourZoneByConnection.Remove(connectionId);
                Logging.LogWarning("Mesh", $"Neighbour zone {zoneId} link lost (conn {connectionId})");
                ReleaseLentMonsters(zoneId, "neighbour link lost");
                ReleaseBorrowedMonsters(zoneId, "home zone link lost");
                RollBackHandoffsToward(zoneId, "link to the destination lost");
            }
        }

        /// <summary>Monsters handed to us by a zone that is gone: their respawn belongs to its spawn areas, which respawn everything when it returns.</summary>
        private void ReleaseBorrowedMonsters(string homeZone, string reason)
        {
            _tmpIds.Clear();
            foreach (KeyValuePair<string, BaseCharacterEntity> kv in _ownedByMeshId)
            {
                MeshIdentity id = kv.Value != null ? MeshIdentity.Get(kv.Value) : null;
                if (id != null && string.Equals(id.HomeZoneId, homeZone) && kv.Value is BaseMonsterCharacterEntity)
                    _tmpIds.Add(kv.Key);
            }
            for (int i = 0; i < _tmpIds.Count; ++i)
            {
                if (!_ownedByMeshId.TryGetValue(_tmpIds[i], out BaseCharacterEntity monster) || monster == null)
                    continue;
                if (_borrowedKillHooks.TryGetValue(_tmpIds[i], out CharacterEntityKilledDelegate hook))
                {
                    monster.onKilled -= hook;
                    _borrowedKillHooks.Remove(_tmpIds[i]);
                }
                UntrackOwned(monster);
                if (monster.Identity != null && !monster.Identity.IsDestroyed)
                    monster.NetworkDestroy();
                Logging.LogWarning("Mesh", $"Borrowed monster {_tmpIds[i]} released ({reason})");
            }
        }

        /// <summary>A neighbour is gone: every monster it was holding for us respawns from its home spawn area.</summary>
        private void ReleaseLentMonsters(string zoneId, string reason)
        {
            _tmpIds.Clear();
            foreach (KeyValuePair<string, LentMonster> kv in _lentMonsters)
                if (string.Equals(kv.Value.ownerZone, zoneId)) _tmpIds.Add(kv.Key);
            for (int i = 0; i < _tmpIds.Count; ++i)
            {
                LentMonster lent = _lentMonsters[_tmpIds[i]];
                _lentMonsters.Remove(_tmpIds[i]);
                DestroyGhost(_tmpIds[i], reason);
                if (lent.area != null && lent.prefab != null)
                    lent.area.Spawn(lent.prefab, lent.level, lent.respawnDelay, lent.respawnDelay);
                Logging.LogWarning("Mesh", $"Lent monster {_tmpIds[i]} released ({reason}); home spawn area respawns it in {lent.respawnDelay:0.#}s");
            }
        }

        internal void OnGhostSnapshot(long connectionId, GhostSnapshotMessage msg)
        {
            if (!IsServer || !enableBorderGhosting)
                return;
            if (msg.kind == GhostKind.ItemDrop)
            {
                OnGhostDropSnapshot(connectionId, msg);
                return;
            }
            // Dedup: this cell owns the real thing → the neighbour's copy is stale; ignore.
            if (msg.kind == GhostKind.Player && ServerUserHandlers.TryGetPlayerCharacterById(msg.characterId, out _))
                return;
            // A ghost already promoted for an arriving client: the departing cell's last snapshots are in flight; ignore.
            if (_reservedArrivals.ContainsKey(msg.characterId))
                return;

            if (!_ghosts.TryGetValue(msg.characterId, out MeshGhost ghost) || ghost.entity == null)
            {
                if (_recentlyRemoved.ContainsKey(msg.characterId))
                    return; // a snapshot that overtook its GhostRemove on the sequenced channel
                BaseCharacterEntity entity = SpawnGhostEntity(msg);
                if (entity == null)
                    return;
                MeshIdentity id = MeshIdentity.GetOrAdd(entity);
                id.MeshEntityId = msg.characterId;
                id.IsGhost = true;
                id.OwnerZoneId = _neighbourZoneByConnection.TryGetValue(connectionId, out string z) ? z : "?";
                int silenced = GhostSuppression.Ghostify(entity, this, null, false, false, disableMovement: false);
                entity.IsInvincible = true; // never damaged by the local simulation: hits are forwarded to the owner (P3)
                string capturedMeshId = msg.characterId;
                ReceiveDamageDelegate hitHook = (target, position, fromPosition, instigator, damageAmounts, weapon, skill, skillLevel) =>
                    ForwardGhostHit(capturedMeshId, position, fromPosition, instigator, damageAmounts, weapon, skill, skillLevel);
                entity.onReceiveDamage += hitHook;
                // Kill credit: rewards the kit gives this ghost (it was in a monster's damage records here) are intercepted
                // by MeshGameplayRule and forwarded to the owner — see ForwardGhostReward.
                ghost = new MeshGhost { entity = entity, kind = msg.kind, lastTarget = msg.position, hitHook = hitHook };
                _ghosts[msg.characterId] = ghost;
                if (msg.kind == GhostKind.Player)
                    _playerGhosts.Add(entity);
                Logging.Log("Mesh", $"Ghost spawned {msg.kind} {msg.characterName} ({msg.characterId}) from zone {id.OwnerZoneId} at {msg.position} (silenced {silenced})");
            }

            ghost.lastSeen = Time.unscaledTime;
            if (_neighbourZoneByConnection.TryGetValue(connectionId, out string ownerZone))
            {
                MeshIdentity gid = MeshIdentity.Get(ghost.entity);
                if (gid != null && gid.OwnerZoneId != ownerZone)
                    gid.OwnerZoneId = ownerZone; // ownership moved (monster handoff between neighbours)
                if (_lentMonsters.TryGetValue(msg.characterId, out LentMonster lent))
                    lent.ownerZone = ownerZone;
            }
            if (ghost.reserved)
                return; // claimed by an arriving client: it sits where the saved data put it until registration
            SyncGhostMount(ghost, msg);
            SyncGhostSummoner(ghost, msg);
            SyncGhostWeapons(ghost, msg);
            SteerGhost(ghost, msg);
        }

        // ---- Attacks on ghosts (DESIGN §12) ----
        // A puppet shows its owner's equipped weapons (so the model and the attack animation match), and replays the
        // owner's attack start and trigger RPCs to its own observers (MeshCharacterAttackComponent). Hits are not
        // computed here: the puppet's clients only see the swing and the missile.

        private void SyncGhostWeapons(MeshGhost ghost, GhostSnapshotMessage msg)
        {
            if (msg.kind != GhostKind.Player || !(ghost.entity is BasePlayerCharacterEntity player))
                return;
            EquipWeapons current = player.EquipWeapons;
            if (current.rightHand.dataId == msg.rightWeaponDataId && current.leftHand.dataId == msg.leftWeaponDataId)
                return;
            player.EquipWeapons = new EquipWeapons
            {
                rightHand = msg.rightWeaponDataId != 0 ? CharacterItem.Create(msg.rightWeaponDataId, Mathf.Max(1, msg.rightWeaponLevel)) : CharacterItem.Empty,
                leftHand = msg.leftWeaponDataId != 0 ? CharacterItem.Create(msg.leftWeaponDataId, Mathf.Max(1, msg.leftWeaponLevel)) : CharacterItem.Empty,
            };
        }

        /// <summary>Owner side: one message to every neighbour that currently sees this entity as a ghost.</summary>
        private void SendToZonesStreaming<T>(string meshId, ushort msgType, T msg) where T : INetSerializable
        {
            foreach (KeyValuePair<string, HashSet<string>> kv in _streamedToZone)
            {
                if (kv.Value.Contains(meshId) && _linkClients.TryGetValue(kv.Key, out MeshLinkClient link))
                    link.Send(msgType, msg);
            }
        }

        // ---- Replays on ghosts (attacks, skills): see MeshMapNetworkManager_Actions ----
        // A target is an object id local to the owner cell, so it travels as a mesh id.

        /// <summary>The mesh id of a local object id (a character here, real or ghost), or empty.</summary>
        private string MeshIdOfObject(uint objectId)
        {
            if (objectId == 0 || !Assets.TryGetSpawnedObject(objectId, out LiteNetLibIdentity identity) || identity == null)
                return string.Empty;
            BaseCharacterEntity character = identity.GetComponent<BaseCharacterEntity>();
            return character != null ? MeshEntityIdOf(character) : string.Empty;
        }

        /// <summary>The local object id of a mesh id (its ghost or real entity here), or 0.</summary>
        private uint ObjectIdOfMesh(string meshId)
        {
            BaseCharacterEntity entity = ResolveGhostOrReal(meshId);
            return entity != null && entity.Identity != null ? entity.ObjectId : 0u;
        }

        // ---- Summons on ghosts ----
        // A pet's snapshot names its summoner. The puppet here is linked to the summoner as this cell knows it (its
        // ghost or the real character), so it reads as that player's ally rather than a wild monster, and so the
        // arriving owner can promote it in place (TryPromoteSummonPuppet). A puppet whose summoner is not here is
        // unlinked: the kit destroys a summoned monster whose summoner is gone, a puppet must never do that itself.

        // Pet puppets by their summoner's mesh id: the reservation, promotion, release and unlink paths all ask
        // "which pet puppets belong to this character" and must not walk the whole ghost table for it.
        private readonly Dictionary<string, HashSet<string>> _petGhostsBySummoner = new Dictionary<string, HashSet<string>>();

        private void SetGhostSummoner(string meshId, MeshGhost ghost, string summonerMeshId, string summonId)
        {
            if (!string.Equals(ghost.summonerMeshId, summonerMeshId))
            {
                if (!string.IsNullOrEmpty(ghost.summonerMeshId) && _petGhostsBySummoner.TryGetValue(ghost.summonerMeshId, out HashSet<string> old))
                {
                    old.Remove(meshId);
                    if (old.Count == 0)
                        _petGhostsBySummoner.Remove(ghost.summonerMeshId);
                }
                if (!string.IsNullOrEmpty(summonerMeshId))
                {
                    if (!_petGhostsBySummoner.TryGetValue(summonerMeshId, out HashSet<string> pets))
                        _petGhostsBySummoner[summonerMeshId] = pets = new HashSet<string>();
                    pets.Add(meshId);
                }
            }
            ghost.summonerMeshId = summonerMeshId;
            ghost.summonId = summonId;
        }

        /// <summary>The mesh ids of the pet puppets linked to a summoner (empty when none).</summary>
        private IEnumerable<string> PetGhostsOf(string summonerMeshId)
        {
            return !string.IsNullOrEmpty(summonerMeshId) && _petGhostsBySummoner.TryGetValue(summonerMeshId, out HashSet<string> pets) ? pets : System.Linq.Enumerable.Empty<string>();
        }

        private void SyncGhostSummoner(MeshGhost ghost, GhostSnapshotMessage msg)
        {
            if (msg.kind != GhostKind.Monster || !(ghost.entity is BaseMonsterCharacterEntity monster))
                return;
            SetGhostSummoner(msg.characterId, ghost, msg.summonerMeshId, msg.summonId);
            BaseCharacterEntity summoner = string.IsNullOrEmpty(msg.summonerMeshId) ? null : ResolveGhostOrReal(msg.summonerMeshId);
            uint wanted = summoner != null ? summoner.ObjectId : 0u;
            SummonType type = wanted != 0u ? (SummonType)msg.summonType : SummonType.None;
            if (monster.Summoner.objectId != wanted || monster.SummonType != type)
                LinkSummoner(monster, summoner, type);
        }

        /// <summary>
        /// Set a monster's summoner. The kit caches the resolved entity privately and only re-resolves it when the cache
        /// is null, so writing the sync field alone leaves a re-linked puppet following (and dying with) a stale entity;
        /// the add-on's monster subclass reaches the protected setters, a foreign monster class gets the sync field.
        /// </summary>
        private static void LinkSummoner(BaseMonsterCharacterEntity monster, BaseCharacterEntity summoner, SummonType type)
        {
            if (monster is MeshMonsterCharacterEntity mesh)
                mesh.MeshLinkSummoner(summoner, type);
            else
                monster.Summoner = new CharacterSummoner { type = type, objectId = summoner != null ? summoner.ObjectId : 0u };
        }

        /// <summary>An entity by mesh id as this cell knows it: its ghost here, or the real one (see ResolveOwnedCharacter).</summary>
        private BaseCharacterEntity ResolveGhostOrReal(string meshId)
        {
            if (!string.IsNullOrEmpty(meshId) && _ghosts.TryGetValue(meshId, out MeshGhost g) && g.entity != null)
                return g.entity;
            return ResolveOwnedCharacter(meshId);
        }

        /// <summary>
        /// A summoner entity is about to be destroyed here: pet puppets linked to it are unlinked first, so the kit's
        /// "summoner disappeared" rule in the monster's update does not destroy the puppet underneath the ghost table.
        /// </summary>
        private void UnlinkSummonPuppets(string summonerMeshId)
        {
            foreach (string petId in PetGhostsOf(summonerMeshId))
            {
                if (_ghosts.TryGetValue(petId, out MeshGhost g) && g.entity is BaseMonsterCharacterEntity m && m.IsSummoned)
                    LinkSummoner(m, null, SummonType.None);
            }
        }

        /// <summary>
        /// The departing player's live summons go with it: each becomes a puppet in place under its own mesh id, to be
        /// steered by the new owner's snapshots once that cell promotes its puppet of the pet.
        /// </summary>
        private int DemoteSummonsWith(BasePlayerCharacterEntity player, string newOwnerZone)
        {
            int count = 0;
            for (int i = 0; i < player.Summons.Count; ++i)
            {
                CharacterSummon summon = player.Summons[i];
                BaseMonsterCharacterEntity pet = summon.CacheEntity;
                if (pet == null && Assets.TryGetSpawnedObject(summon.objectId, out LiteNetLibIdentity petIdentity) && petIdentity != null)
                    pet = petIdentity.GetComponent<BaseMonsterCharacterEntity>();
                if (pet == null || pet.Identity == null || pet.Identity.IsDestroyed || pet.IsDead() || MeshIdentity.IsGhostEntity(pet))
                    continue;
                MeshIdentity pid = EnsureBirthMeshId(pet, pet.ObjectId);
                DemoteToGhost(pet, pid, newOwnerZone);
                if (_ghosts.TryGetValue(pid.MeshEntityId, out MeshGhost g))
                    SetGhostSummoner(pid.MeshEntityId, g, player.Id, summon.id);
                count++;
            }
            return count;
        }

        /// <summary>
        /// The arriving character's summon is already standing here as a puppet (streamed by the departing cell, or
        /// demoted with the player on a cell it came back to): make it the real pet in place, no pop.
        /// </summary>
        private bool TryPromoteSummonPuppet(BasePlayerCharacterEntity player, ref CharacterSummon summon)
        {
            MeshGhost found = null;
            string foundId = null;
            foreach (string petId in PetGhostsOf(player.Id))
            {
                if (_ghosts.TryGetValue(petId, out MeshGhost g) && g.kind == GhostKind.Monster && g.entity != null && !g.entity.IsDead() && string.Equals(g.summonId, summon.id))
                {
                    found = g;
                    foundId = petId;
                    break;
                }
            }
            if (found == null || !(found.entity is BaseMonsterCharacterEntity pet) || pet.Identity == null || pet.Identity.IsDestroyed)
                return false;
            PromoteGhostInPlace(foundId, found);
            LinkSummoner(pet, player, summon.type);
            pet.Level = summon.level;
            pet.Exp = summon.exp;
            if (summon.currentHp > 0)
                pet.CurrentHp = summon.currentHp;
            if (summon.currentMp > 0)
                pet.CurrentMp = summon.currentMp;
            summon.objectId = pet.ObjectId;
            summon.CacheEntity = pet;
            TrackOwned(pet);
            if (InterestManager != null)
                InterestManager.NotifyNewObject(pet.Identity);
            Logging.Log("Mesh", $"Summon {pet.CharacterName} ({foundId}) promoted in place for {player.CharacterName}: {summon.type} lv{pet.Level} hp={pet.CurrentHp}");
            return true;
        }

        // ---- Mounts on ghosts ----
        // A rider's snapshot names the vehicle prefab; the ghost cell spawns that vehicle (server-owned) and seats
        // the ghost on it, so observers across the edge see the mount. Steering goes through the rider: the kit's
        // ActiveMovement is the vehicle's movement while seated, so PointClickMovement/Teleport drive the mount.

        private void SyncGhostMount(MeshGhost ghost, GhostSnapshotMessage msg)
        {
            if (ghost.entity == null || ghost.mountPending)
                return;
            VehicleEntity current = ghost.entity.PassengingVehicleEntity as VehicleEntity;
            if (msg.mountEntityId != 0)
            {
                if (current != null && current.EntityId == msg.mountEntityId)
                    return;
                if (!GameInstance.VehicleEntities.TryGetValue(msg.mountEntityId, out VehicleEntity prefab) || prefab == null)
                {
                    WarnThrottled($"Ghost {msg.characterName} rides unknown vehicle entity id {msg.mountEntityId}; shown on foot");
                    return;
                }
                MountGhost(ghost, prefab, msg.mountLevel, msg.position, msg.yaw).Forget();
            }
            else if (current != null)
            {
                DismountGhost(ghost).Forget();
            }
        }

        private async UniTaskVoid MountGhost(MeshGhost ghost, VehicleEntity prefab, int level, Vector3 position, float yaw)
        {
            ghost.mountPending = true;
            try
            {
                BaseCharacterEntity entity = ghost.entity;
                if (entity == null)
                    return;
                if (entity.PassengingVehicleEntity != null)
                {
                    VehicleEntity old = entity.PassengingVehicleEntity as VehicleEntity;
                    await entity.ExitVehicle();
                    if (old != null && old.Identity != null && !old.Identity.IsDestroyed)
                        old.NetworkDestroy();
                }
                LiteNetLibIdentity identity = Assets.NetworkSpawn(prefab.Identity.HashAssetId, position, Quaternion.Euler(0f, yaw, 0f));
                VehicleEntity vehicle = identity != null ? identity.GetComponent<VehicleEntity>() : null;
                if (vehicle == null)
                {
                    if (identity != null) identity.NetworkDestroy();
                    return;
                }
                vehicle.Level = Mathf.Max(1, level);
                vehicle.InitStats();
                vehicle.CurrentHp = vehicle.MaxHp;
                if (entity == null || entity.Identity == null || entity.Identity.IsDestroyed)
                {
                    vehicle.NetworkDestroy();
                    return;
                }
                await entity.EnterVehicle(vehicle, 0);
                ghost.vehicle = vehicle;
                if (traceHandoffTiming)
                    Logging.Log("Mesh", $"Ghost {entity.CharacterName} mounted on {prefab.name}");
            }
            finally
            {
                ghost.mountPending = false;
            }
        }

        private async UniTaskVoid DismountGhost(MeshGhost ghost)
        {
            ghost.mountPending = true;
            try
            {
                BaseCharacterEntity entity = ghost.entity;
                VehicleEntity vehicle = entity != null ? entity.PassengingVehicleEntity as VehicleEntity : null;
                if (entity != null)
                    await entity.ExitVehicle();
                if (vehicle != null && vehicle.Identity != null && !vehicle.Identity.IsDestroyed)
                    vehicle.NetworkDestroy();
                ghost.vehicle = null;
                if (traceHandoffTiming && entity != null)
                    Logging.Log("Mesh", $"Ghost {entity.CharacterName} dismounted");
            }
            finally
            {
                ghost.mountPending = false;
            }
        }

        private BaseCharacterEntity SpawnGhostEntity(GhostSnapshotMessage msg)
        {
            _spawningPuppet = true; // the spawn event must not register a puppet as ours
            try { return SpawnGhostEntityCore(msg); }
            finally { _spawningPuppet = false; }
        }

        private BaseCharacterEntity SpawnGhostEntityCore(GhostSnapshotMessage msg)
        {
            Quaternion rot = Quaternion.Euler(0f, msg.yaw, 0f);
            if (msg.kind == GhostKind.Player)
            {
                int hashAssetId = GameInstance.GetPlayerCharacterEntityHashAssetId(msg.entityId, out int metaDataId);
                LiteNetLibIdentity identity = Assets.NetworkSpawn(hashAssetId, msg.position, rot);
                BasePlayerCharacterEntity player = identity != null ? identity.GetComponent<BasePlayerCharacterEntity>() : null;
                if (player == null)
                {
                    WarnThrottled($"Cannot spawn player ghost for entity id {msg.entityId}");
                    if (identity != null) identity.NetworkDestroy();
                    return null;
                }
                GameInstance.SetupByMetaData(player, metaDataId);
                player.Id = msg.characterId;
                player.DataId = msg.dataId;
                player.CharacterName = msg.characterName;
                player.Level = msg.level;
                return player;
            }
            if (!GameInstance.MonsterCharacterEntities.TryGetValue(msg.entityId, out BaseMonsterCharacterEntity prefab) || prefab == null)
            {
                WarnThrottled($"Cannot spawn monster ghost: unknown monster entity id {msg.entityId}");
                return null;
            }
            LiteNetLibIdentity monsterIdentity = Assets.NetworkSpawn(prefab.Identity.HashAssetId, msg.position, rot);
            BaseMonsterCharacterEntity monster = monsterIdentity != null ? monsterIdentity.GetComponent<BaseMonsterCharacterEntity>() : null;
            if (monster == null)
            {
                WarnThrottled($"Cannot spawn monster ghost for entity id {msg.entityId}");
                if (monsterIdentity != null) monsterIdentity.NetworkDestroy();
                return null;
            }
            monster.Level = msg.level;
            return monster;
        }

        private void SteerGhost(MeshGhost ghost, GhostSnapshotMessage msg)
        {
            BaseCharacterEntity entity = ghost.entity;
            Vector3 current = entity.EntityTransform.position;
            float dist = Vector3.Distance(current, msg.position);
            if (dist > ghostTeleportDistance)
            {
                entity.Teleport(msg.position, Quaternion.Euler(0f, msg.yaw, 0f), false);
            }
            else if (dist > 0.25f)
            {
                // Puppet: let the kit's movement component walk it there (animates + syncs like any entity).
                entity.PointClickMovement(msg.position);
            }
            else
            {
                entity.StopMove();
            }
            ghost.lastTarget = msg.position;
            if (entity.CurrentHp != msg.currentHp && msg.maxHp > 0)
            {
                entity.CurrentHp = msg.currentHp;
                if (msg.currentHp <= 0)
                    UnlinkSummonPuppets(msg.characterId); // the kit unsummons a monster whose summoner is dead; puppets must not
            }
        }

        internal void OnGhostRemove(string meshId)
        {
            if (_ghostDrops.ContainsKey(meshId))
                DestroyGhostDrop(meshId, "neighbour removed");
            else
                DestroyGhost(meshId, "neighbour removed");
        }

        // ---- Loot across the edge (DESIGN §12) ----
        // A drop near the line is streamed like a monster; the neighbour shows a puppet drop nobody can loot locally.
        // A pickup on the puppet is forwarded to the owner, which validates against the requester's ghost, takes the
        // items off the real drop and sends them to the requester's cell, where they enter the real character's bag.

        private sealed class GhostDrop { public ItemDropEntity entity; public float lastSeen; }
        private readonly Dictionary<string, GhostDrop> _ghostDrops = new Dictionary<string, GhostDrop>();
        private readonly List<ItemDropEntity> _ownedDrops = new List<ItemDropEntity>();
        private readonly Dictionary<string, ItemDropEntity> _ownedDropsByMeshId = new Dictionary<string, ItemDropEntity>();
        private static readonly string[] s_ghostLooters = { "mesh-ghost-drop" }; // a looter id nobody has: the kit path never loots a puppet

        private void OnGhostDropSnapshot(long connectionId, GhostSnapshotMessage msg)
        {
            if (!_ghostDrops.TryGetValue(msg.characterId, out GhostDrop ghost) || ghost.entity == null || ghost.entity.Identity.IsDestroyed)
            {
                if (_recentlyRemoved.ContainsKey(msg.characterId))
                    return;
                if (!GameInstance.ItemDropEntities.TryGetValue(msg.entityId, out ItemDropEntity prefab) || prefab == null)
                    prefab = GameInstance.Singleton.ItemDropEntityPrefab;
                if (prefab == null)
                {
                    WarnThrottled($"Cannot spawn drop ghost: no item drop prefab for entity id {msg.entityId}");
                    return;
                }
                CharacterItem item = CharacterItem.Create(msg.dataId, Mathf.Max(1, msg.level), Mathf.Max(1, msg.currentHp));
                ItemDropEntity entity;
                _spawningPuppet = true;
                try { entity = ItemDropEntity.Drop(prefab, msg.position, Quaternion.Euler(0f, msg.yaw, 0f), RewardGivenType.None, item, s_ghostLooters, 3600f); }
                finally { _spawningPuppet = false; }
                if (entity == null)
                    return;
                MeshIdentity id = MeshIdentity.GetOrAdd(entity);
                id.MeshEntityId = msg.characterId;
                id.IsGhost = true;
                id.OwnerZoneId = _neighbourZoneByConnection.TryGetValue(connectionId, out string z) ? z : "?";
                ghost = new GhostDrop { entity = entity };
                _ghostDrops[msg.characterId] = ghost;
                if (traceHandoffTiming)
                    Logging.Log("Mesh", $"Drop ghost spawned ({msg.characterId}) from zone {id.OwnerZoneId} at {msg.position}: item {msg.dataId} x{msg.currentHp}");
            }
            ghost.lastSeen = Time.unscaledTime;
            if (_neighbourZoneByConnection.TryGetValue(connectionId, out string ownerZone))
            {
                MeshIdentity gid = MeshIdentity.Get(ghost.entity);
                if (gid != null && gid.OwnerZoneId != ownerZone)
                    gid.OwnerZoneId = ownerZone;
            }
        }

        private void DestroyGhostDrop(string meshId, string reason)
        {
            if (!_ghostDrops.TryGetValue(meshId, out GhostDrop ghost))
                return;
            _ghostDrops.Remove(meshId);
            _recentlyRemoved[meshId] = Time.unscaledTime;
            if (ghost.entity != null && ghost.entity.Identity != null && !ghost.entity.Identity.IsDestroyed)
            {
                if (traceHandoffTiming)
                    Logging.Log("Mesh", $"Drop ghost destroyed ({meshId}) reason={reason}");
                ResetMeshIdentity(ghost.entity);
                ghost.entity.NetworkDestroy();
            }
        }

        private void ExpireGhostDrops()
        {
            if (_ghostDrops.Count == 0)
                return;
            _tmpIds.Clear();
            foreach (KeyValuePair<string, GhostDrop> kv in _ghostDrops)
            {
                if (kv.Value.entity == null || Time.unscaledTime - kv.Value.lastSeen > ghostStaleSeconds)
                    _tmpIds.Add(kv.Key);
            }
            for (int i = 0; i < _tmpIds.Count; ++i)
                DestroyGhostDrop(_tmpIds[i], "stale");
        }

        /// <summary>A player here tried to loot a puppet drop: ask the owner.</summary>
        internal void RequestGhostPickup(BasePlayerCharacterEntity player, ItemDropEntity ghostDrop)
        {
            MeshIdentity id = MeshIdentity.Get(ghostDrop);
            if (player == null || id == null || !id.IsGhost || string.IsNullOrEmpty(id.OwnerZoneId))
                return;
            if (!_linkClients.TryGetValue(id.OwnerZoneId, out MeshLinkClient link) || !link.IsConnected)
            {
                GameInstance.ServerGameMessageHandlers.SendGameMessage(player.ConnectionId, UITextKeys.UI_ERROR_NOT_ABLE_TO_LOOT);
                return;
            }
            link.SendGhostPickup(new GhostPickupMessage { characterId = player.Id, dropMeshId = id.MeshEntityId });
            if (traceHandoffTiming)
                Logging.Log("Mesh", $"Pickup on drop ghost {id.MeshEntityId} by {player.CharacterName} forwarded to zone {id.OwnerZoneId}");
        }

        internal void OnGhostPickup(long connectionId, GhostPickupMessage msg)
        {
            if (!IsServer)
                return;
            string fromZone = _neighbourZoneByConnection.TryGetValue(connectionId, out string z) ? z : "?";
            if (!_linkClients.TryGetValue(fromZone, out MeshLinkClient reply) || !reply.IsConnected)
                return;
            ushort messageKey = (ushort)UITextKeys.NONE;
            CharacterItem[] items = null;
            RewardGivenType givenType = RewardGivenType.None;
            if (!_ownedDropsByMeshId.TryGetValue(msg.dropMeshId, out ItemDropEntity drop) || drop == null || drop.Identity == null || drop.Identity.IsDestroyed || drop.DropItems.Count == 0)
            {
                messageKey = (ushort)UITextKeys.UI_ERROR_INVALID_DATA;
            }
            else if (!_ghosts.TryGetValue(msg.characterId, out MeshGhost requester) || requester.entity == null)
            {
                messageKey = (ushort)UITextKeys.UI_ERROR_CHARACTER_IS_TOO_FAR; // the requester is not even within our band
            }
            else if (Vector3.Distance(requester.entity.EntityTransform.position, drop.EntityTransform.position) > GameInstance.Singleton.pickUpItemDistance + 1.5f)
            {
                messageKey = (ushort)UITextKeys.UI_ERROR_CHARACTER_IS_TOO_FAR;
            }
            else if (!drop.IsAbleToLoot(requester.entity))
            {
                messageKey = (ushort)UITextKeys.UI_ERROR_NOT_ABLE_TO_LOOT;
            }
            else
            {
                items = drop.DropItems.ToArray();
                givenType = drop.GivenType;
                drop.PickedUp(); // gone here; the streamed-set diff tells every neighbour to drop its puppet
                UntrackOwned(drop);
                if (traceHandoffTiming)
                    Logging.Log("Mesh", $"Drop {msg.dropMeshId} looted through zone {fromZone} by {msg.characterId}: {items.Length} item(s)");
            }
            reply.SendGhostLoot(new GhostLootMessage { characterId = msg.characterId, givenType = (byte)givenType, messageKey = messageKey, items = items });
        }

        internal void OnGhostLoot(GhostLootMessage msg)
        {
            if (!IsServer)
                return;
            if (!ServerUserHandlers.TryGetPlayerCharacterById(msg.characterId, out IPlayerCharacterData data) || !(data is BasePlayerCharacterEntity player))
                return;
            if (msg.messageKey != (ushort)UITextKeys.NONE || msg.items == null || msg.items.Length == 0)
            {
                if (msg.messageKey != (ushort)UITextKeys.NONE)
                    GameInstance.ServerGameMessageHandlers.SendGameMessage(player.ConnectionId, (UITextKeys)msg.messageKey);
                return;
            }
            List<CharacterItem> items = new List<CharacterItem>(msg.items);
            if (player.IncreasingItemsWillOverwhelming(items))
            {
                GameInstance.ServerGameMessageHandlers.SendGameMessage(player.ConnectionId, UITextKeys.UI_ERROR_WILL_OVERWHELMING);
                Logging.LogWarning("Mesh", $"Loot for {player.CharacterName} did not fit; {items.Count} item(s) lost (the owner already removed the drop)");
                return;
            }
            RewardGivenType givenType = (RewardGivenType)msg.givenType;
            player.IncreaseItems(items, characterItem => player.OnRewardItem(givenType, characterItem));
            player.FillEmptySlots();
            if (traceHandoffTiming)
                Logging.Log("Mesh", $"Loot applied to {player.CharacterName}: {items.Count} item(s)");
        }

        // ---- P3/P4: monster handoff (DESIGN §12) ----
        // A server-owned monster that walks clearly into the neighbour's cell (chasing a player's ghost across the
        // edge) is handed over: this cell demotes it to a ghost IN PLACE and the neighbour promotes its puppet to the
        // real monster, so neither cell's clients see a despawn/spawn. Respawn accounting stays with the home cell's
        // spawn area: when the borrowed monster dies elsewhere, the borrower tells the home cell (MonsterGone).

        [Tooltip("Hand a server-owned monster to the neighbour cell once it walks clearly into that cell.")]
        public bool handoffMonsters = true;
        [Tooltip("Minimum seconds between two handoffs of the same monster (a chase back and forth across the line would otherwise ping-pong every tick).")]
        public float monsterHandoffCooldown = 2f;

        private sealed class LentMonster
        {
            public string ownerZone;               // who has it now (refreshed from its ghost's snapshots)
            public GameSpawnArea<BaseMonsterCharacterEntity> area;
            public BaseMonsterCharacterEntity prefab;
            public int level;
            public float respawnDelay;
            public Vector3 spawnPosition;
        }

        private readonly Dictionary<string, LentMonster> _lentMonsters = new Dictionary<string, LentMonster>();                 // meshId -> home spawn info (this cell is home)
        private readonly Dictionary<string, CharacterEntityKilledDelegate> _borrowedKillHooks = new Dictionary<string, CharacterEntityKilledDelegate>();
        private readonly List<BaseMonsterCharacterEntity> _monsterHandoffCandidates = new List<BaseMonsterCharacterEntity>();

        private void MonsterHandoffTick()
        {
            if (!handoffMonsters || !enableServerBoundaryHandoff || !ghostMonsters)
                return;
            _monsterHandoffCandidates.Clear();
            for (int i = 0; i < _ownedCandidates.Count; ++i)
            {
                BaseMonsterCharacterEntity monster = _ownedCandidates[i] as BaseMonsterCharacterEntity;
                if (monster == null || monster.IsDead() || monster.IsSummoned)
                    continue;
                Vector3 pos = monster.EntityTransform.position;
                if (meshConfig.WorldToZoneCoord(pos) == OwnZoneCoord)
                    continue;
                if (meshConfig.IsNearBoundary(pos, handoffHysteresis))
                    continue; // same hysteresis as players: only once clearly past the line
                _monsterHandoffCandidates.Add(monster);
            }
            for (int i = 0; i < _monsterHandoffCandidates.Count; ++i)
                HandoffMonster(_monsterHandoffCandidates[i]);
            _monsterHandoffCandidates.Clear();
        }

        private void HandoffMonster(BaseMonsterCharacterEntity monster)
        {
            Vector3 pos = monster.EntityTransform.position;
            ServerMeshTestConfig.ZoneDefinition zone = meshConfig.GetZoneAt(pos);
            if (zone == null || string.Equals(zone.zoneId, OwnZoneId))
                return;
            if (!_linkClients.TryGetValue(zone.zoneId, out MeshLinkClient link) || !link.IsConnected)
                return; // stays ours until the link is up
            MeshIdentity id = MeshIdentity.GetOrAdd(monster);
            if (string.IsNullOrEmpty(id.MeshEntityId))
                return;
            if (Time.unscaledTime - id.LastHandoffTime < monsterHandoffCooldown)
                return;
            string meshId = id.MeshEntityId;
            string homeZone = string.IsNullOrEmpty(id.HomeZoneId) ? OwnZoneId : id.HomeZoneId;
            if (string.Equals(homeZone, OwnZoneId) && monster.SpawnArea != null && !_lentMonsters.ContainsKey(meshId))
            {
                _lentMonsters[meshId] = new LentMonster
                {
                    ownerZone = zone.zoneId,
                    area = monster.SpawnArea, prefab = monster.SpawnPrefab, level = monster.SpawnLevel,
                    respawnDelay = monster.DestroyRespawnDelay, spawnPosition = monster.SpawnPosition,
                };
            }
            string targetMeshId = string.Empty;
            if (monster.TryGetTargetEntity(out BaseCharacterEntity target) && target != null)
                targetMeshId = MeshEntityIdOf(target);
            link.SendMonsterHandoff(new MonsterHandoffMessage
            {
                meshId = meshId, entityId = monster.EntityId, dataId = monster.DataId, level = monster.Level, currentHp = monster.CurrentHp,
                position = pos, yaw = monster.EntityTransform.eulerAngles.y, spawnPosition = monster.SpawnPosition,
                homeZoneId = homeZone, targetMeshId = targetMeshId,
            });
            Logging.Log("Mesh", $"Monster handoff {meshId} {OwnZoneId}->{zone.zoneId} at {pos} hp={monster.CurrentHp}/{monster.MaxHp} home={homeZone} target={targetMeshId}");
            DemoteToGhost(monster, id, zone.zoneId);
        }

        /// <summary>The neighbour owns it now: keep the entity (no pop for our clients) but run it as a puppet.</summary>
        private void DemoteToGhost(BaseMonsterCharacterEntity monster, MeshIdentity id, string newOwnerZone)
        {
            string meshId = id.MeshEntityId;
            if (_borrowedKillHooks.TryGetValue(meshId, out CharacterEntityKilledDelegate killHook))
            {
                monster.onKilled -= killHook;
                _borrowedKillHooks.Remove(meshId);
            }
            UnhookKill(monster);
            MakeGhostInPlace(monster, id, GhostKind.Monster, newOwnerZone, null);
        }

        /// <summary>
        /// The one place a puppet turns back into an owned entity without being destroyed (player arrival, monster
        /// handoff, pet promotion): out of the ghost tables, hit forwarding off, kit components back, identity ours.
        /// The caller re-owns / re-links / re-tracks it afterwards.
        /// </summary>
        private void PromoteGhostInPlace(string meshId, MeshGhost ghost)
        {
            _ghosts.Remove(meshId);
            SetGhostSummoner(meshId, ghost, null, null);
            BaseCharacterEntity entity = ghost.entity;
            _playerGhosts.Remove(entity);
            if (ghost.hitHook != null)
                entity.onReceiveDamage -= ghost.hitHook;
            GhostSuppression.Unghostify(entity, this);
            entity.IsInvincible = false;
            MeshIdentity id = MeshIdentity.GetOrAdd(entity);
            id.MeshEntityId = meshId;
            id.IsGhost = false;
            id.OwnerZoneId = OwnZoneId;
            id.LastHandoffTime = Time.unscaledTime;
        }

        /// <summary>
        /// The one place an owned entity turns into a puppet without being destroyed (player and monster handoff):
        /// silence it, make it invincible, forward hits to the new owner, register it as a ghost and take it out of
        /// everything that describes owned entities — including the streamed sets, so no GhostRemove follows.
        /// </summary>
        private void MakeGhostInPlace(BaseCharacterEntity entity, MeshIdentity id, GhostKind kind, string newOwnerZone, VehicleEntity mount)
        {
            string meshId = id.MeshEntityId;
            entity.SetTargetEntity(null);
            entity.StopMove();
            id.IsGhost = true;
            id.OwnerZoneId = newOwnerZone;
            id.LastHandoffTime = Time.unscaledTime;
            GhostSuppression.Ghostify(entity, this, null, false, false, disableMovement: false);
            entity.IsInvincible = true;
            ReceiveDamageDelegate hitHook = (target, position, fromPosition, instigator, damageAmounts, weapon, skill, skillLevel) =>
                ForwardGhostHit(meshId, position, fromPosition, instigator, damageAmounts, weapon, skill, skillLevel);
            entity.onReceiveDamage += hitHook;
            if (_ghosts.TryGetValue(meshId, out MeshGhost stale) && stale.entity != null && stale.entity != entity)
                DestroyGhost(meshId, "replaced by demotion"); // should not happen (a ghost and the real one on the same cell), be safe
            _ghosts[meshId] = new MeshGhost { entity = entity, kind = kind, lastSeen = Time.unscaledTime, lastTarget = entity.EntityTransform.position, hitHook = hitHook, vehicle = mount };
            if (kind == GhostKind.Player)
                _playerGhosts.Add(entity);
            UntrackOwned(entity);
            foreach (HashSet<string> streamed in _streamedToZone.Values)
                streamed.Remove(meshId);
        }

        internal void OnMonsterHandoff(long connectionId, MonsterHandoffMessage msg)
        {
            if (!IsServer)
                return;
            string fromZone = _neighbourZoneByConnection.TryGetValue(connectionId, out string z) ? z : "?";
            Quaternion rot = Quaternion.Euler(0f, msg.yaw, 0f);
            BaseMonsterCharacterEntity monster = null;
            bool promoted = false;
            if (_ghosts.TryGetValue(msg.meshId, out MeshGhost ghost) && ghost.entity != null && !ghost.entity.IsDead())
            {
                monster = ghost.entity as BaseMonsterCharacterEntity;
                if (monster != null)
                {
                    // Promote the puppet in place: our clients already see this entity.
                    PromoteGhostInPlace(msg.meshId, ghost);
                    if (Vector3.Distance(monster.EntityTransform.position, msg.position) > ghostTeleportDistance)
                        monster.Teleport(msg.position, rot, false);
                    promoted = true;
                }
                else
                {
                    DestroyGhost(msg.meshId, "replaced by handoff");
                }
            }
            GameInstance.MonsterCharacterEntities.TryGetValue(msg.entityId, out BaseMonsterCharacterEntity prefab);
            if (monster == null)
            {
                if (prefab == null)
                {
                    WarnThrottled($"Monster handoff {msg.meshId} from {fromZone}: unknown monster entity id {msg.entityId}; dropped");
                    return;
                }
                LiteNetLibIdentity identity = Assets.NetworkSpawn(prefab.Identity.HashAssetId, msg.position, rot);
                monster = identity != null ? identity.GetComponent<BaseMonsterCharacterEntity>() : null;
                if (monster == null)
                {
                    WarnThrottled($"Monster handoff {msg.meshId} from {fromZone}: cannot spawn entity id {msg.entityId}; dropped");
                    if (identity != null) identity.NetworkDestroy();
                    return;
                }
            }
            UntrackOwned(monster); // a fresh spawn was registered under its birth id by the spawn event; re-key below
            MeshIdentity id = MeshIdentity.GetOrAdd(monster);
            id.MeshEntityId = msg.meshId;
            id.IsGhost = false;
            id.OwnerZoneId = OwnZoneId;
            id.HomeZoneId = msg.homeZoneId;
            id.LastHandoffTime = Time.unscaledTime;
            monster.Level = msg.level;
            if (msg.currentHp > 0)
                monster.CurrentHp = msg.currentHp;
            if (_borrowedKillHooks.TryGetValue(msg.meshId, out CharacterEntityKilledDelegate oldHook))
            {
                monster.onKilled -= oldHook;
                _borrowedKillHooks.Remove(msg.meshId);
            }
            if (string.Equals(msg.homeZoneId, OwnZoneId) && _lentMonsters.TryGetValue(msg.meshId, out LentMonster lent))
            {
                // Home again: the spawn area's respawn accounting comes back with it.
                _lentMonsters.Remove(msg.meshId);
                monster.SetSpawnArea(lent.area, lent.prefab, lent.level, lent.spawnPosition);
                monster.DestroyRespawnDelay = lent.respawnDelay;
            }
            else
            {
                // Borrowed: no local respawn; when it dies here the home cell is told so its spawn area respawns it.
                monster.SetSpawnArea(null, prefab, msg.level, msg.spawnPosition);
                string capturedMeshId = msg.meshId, home = msg.homeZoneId;
                CharacterEntityKilledDelegate killHook = (target, instigator) => OnBorrowedMonsterKilled(capturedMeshId, home);
                monster.onKilled += killHook;
                _borrowedKillHooks[msg.meshId] = killHook;
            }
            TrackOwned(monster);
            // Keep the chase going: the target is either the real player here or its ghost.
            string targetDesc = "none";
            if (!string.IsNullOrEmpty(msg.targetMeshId))
            {
                BaseCharacterEntity target = null;
                if (ServerUserHandlers.TryGetPlayerCharacterById(msg.targetMeshId, out IPlayerCharacterData data) && data is BasePlayerCharacterEntity real)
                    target = real;
                else if (_ghosts.TryGetValue(msg.targetMeshId, out MeshGhost tg) && tg.entity != null)
                    target = tg.entity;
                if (target != null)
                {
                    monster.SetAttackTarget(target);
                    targetDesc = target.CharacterName;
                }
            }
            Logging.Log("Mesh", $"Monster {(promoted ? "promoted" : "spawned")} by handoff {msg.meshId} {fromZone}->{OwnZoneId} at {monster.EntityTransform.position} hp={monster.CurrentHp}/{monster.MaxHp} home={msg.homeZoneId} target={targetDesc}");
        }

        private void OnBorrowedMonsterKilled(string meshId, string homeZone)
        {
            _borrowedKillHooks.Remove(meshId);
            if (_ownedByMeshId.TryGetValue(meshId, out BaseCharacterEntity dead))
                UntrackOwned(dead);
            if (!_linkClients.TryGetValue(homeZone, out MeshLinkClient link) || !link.IsConnected)
            {
                Logging.LogWarning("Mesh", $"Borrowed monster {meshId} died but home zone {homeZone} is not linked: its respawn is lost");
                return;
            }
            link.SendMonsterGone(meshId);
            Logging.Log("Mesh", $"Borrowed monster {meshId} died here; told home zone {homeZone}");
        }

        internal void OnMonsterGone(string meshId)
        {
            if (!IsServer)
                return;
            DestroyGhost(meshId, "died on borrower");
            if (!_lentMonsters.TryGetValue(meshId, out LentMonster lent))
                return;
            _lentMonsters.Remove(meshId);
            if (lent.area == null || lent.prefab == null)
                return;
            lent.area.Spawn(lent.prefab, lent.level, lent.respawnDelay, lent.respawnDelay);
            Logging.Log("Mesh", $"Lent monster {meshId} died on the borrower; home spawn area respawns it in {lent.respawnDelay:0.#}s");
        }

        // ---- P3: cross-border hits (DESIGN §12) ----
        // A hit on a ghost is forwarded (raw attacker damage) to the ghost's owner, which resolves it against the
        // real entity with the kit's own DamageableEntity.ApplyDamage (internal, reachable from Assembly-CSharp)
        // and reports the outcome back so the ghost's cell can show real combat text and hp.

        [Tooltip("Forward hits landing on ghosts to the owning cell and apply them to the real entity there.")]
        public bool forwardGhostHits = true;

        private void ForwardGhostHit(string targetMeshId, HitBoxPosition position, Vector3 fromPosition, EntityInfo instigator, Dictionary<DamageElement, MinMaxFloat> damageAmounts, CharacterItem weapon, BaseSkill skill, int skillLevel)
        {
            if (!forwardGhostHits || !_ghosts.TryGetValue(targetMeshId, out MeshGhost ghost) || ghost.entity == null)
                return;
            MeshIdentity id = MeshIdentity.Get(ghost.entity);
            if (id == null || string.IsNullOrEmpty(id.OwnerZoneId) || !_linkClients.TryGetValue(id.OwnerZoneId, out MeshLinkClient link))
                return;
            string instigatorMeshId = string.Empty;
            if (instigator != null)
            {
                Component instigatorComponent = instigator.Entity as Component;
                MeshIdentity instigatorId = instigatorComponent != null ? MeshIdentity.Get(instigatorComponent) : null;
                instigatorMeshId = instigatorId != null && !string.IsNullOrEmpty(instigatorId.MeshEntityId) ? instigatorId.MeshEntityId : (instigator.Id ?? string.Empty);
            }
            int n = damageAmounts != null ? damageAmounts.Count : 0;
            GhostHitMessage msg = new GhostHitMessage
            {
                targetMeshId = targetMeshId,
                instigatorMeshId = instigatorMeshId,
                hitBoxPosition = (byte)position,
                fromPosition = fromPosition,
                elementDataIds = new int[n], mins = new float[n], maxs = new float[n],
                hasWeapon = weapon.dataId != 0,
                weapon = weapon,
                skillDataId = skill != null ? skill.DataId : 0,
                skillLevel = skillLevel,
                randomSeed = Random.Range(int.MinValue, int.MaxValue),
            };
            int i = 0;
            if (damageAmounts != null)
            {
                foreach (KeyValuePair<DamageElement, MinMaxFloat> kv in damageAmounts)
                {
                    msg.elementDataIds[i] = kv.Key != null ? kv.Key.DataId : 0;
                    msg.mins[i] = kv.Value.min;
                    msg.maxs[i] = kv.Value.max;
                    i++;
                }
            }
            link.SendGhostHit(msg);
            if (traceHandoffTiming)
                Logging.Log("Mesh", $"Hit on ghost {ghost.entity.CharacterName} ({targetMeshId}) forwarded to zone {id.OwnerZoneId}");
        }

        internal void OnGhostHit(long connectionId, GhostHitMessage msg)
        {
            if (!IsServer)
                return;
            BaseCharacterEntity target = null;
            if (_ownedByMeshId.TryGetValue(msg.targetMeshId, out BaseCharacterEntity owned) && owned != null)
                target = owned;
            else if (ServerUserHandlers.TryGetPlayerCharacterById(msg.targetMeshId, out IPlayerCharacterData data) && data is BaseCharacterEntity ce)
                target = ce;
            if (target == null || target.IsDead())
                return;

            Dictionary<DamageElement, MinMaxFloat> damageAmounts = new Dictionary<DamageElement, MinMaxFloat>();
            for (int i = 0; i < msg.elementDataIds.Length; ++i)
            {
                DamageElement element = null;
                if (msg.elementDataIds[i] == 0 || !GameInstance.DamageElements.TryGetValue(msg.elementDataIds[i], out element) || element == null)
                    element = GameInstance.Singleton.DefaultDamageElement;
                if (element == null) continue;
                damageAmounts[element] = new MinMaxFloat { min = msg.mins[i], max = msg.maxs[i] };
            }
            BaseSkill skill = null;
            if (msg.skillDataId != 0)
                GameInstance.Skills.TryGetValue(msg.skillDataId, out skill);
            // The attacker is (usually) a ghost here: use it as the instigator so hit rules see a real entity.
            EntityInfo instigator = EntityInfo.Empty;
            if (!string.IsNullOrEmpty(msg.instigatorMeshId) && _ghosts.TryGetValue(msg.instigatorMeshId, out MeshGhost attackerGhost) && attackerGhost.entity != null)
                instigator = attackerGhost.entity.GetInfo();

            int hpBefore = target.CurrentHp;
            target.ApplyDamage((HitBoxPosition)msg.hitBoxPosition, msg.fromPosition, instigator, damageAmounts, msg.hasWeapon ? msg.weapon : CharacterItem.Empty, skill, msg.skillLevel, msg.randomSeed);
            int dealt = Mathf.Max(0, hpBefore - target.CurrentHp);
            if (traceHandoffTiming)
                Logging.Log("Mesh", $"Forwarded hit applied to {target.CharacterName} ({msg.targetMeshId}): {hpBefore} -> {target.CurrentHp}");

            if (_neighbourZoneByConnection.TryGetValue(connectionId, out string fromZone) && _linkClients.TryGetValue(fromZone, out MeshLinkClient back))
            {
                back.SendGhostHitResult(new GhostHitResultMessage
                {
                    targetMeshId = msg.targetMeshId,
                    combatAmountType = (byte)(dealt > 0 ? CombatAmountType.NormalDamage : CombatAmountType.Miss),
                    totalDamage = dealt,
                    currentHp = target.CurrentHp,
                });
            }
        }

        /// <summary>
        /// Called by MeshGameplayRule on the cell where a monster died: the attacker's ghost was about to be rewarded.
        /// Sends the raw reward to the ghost's owner cell.
        /// </summary>
        internal void ForwardGhostReward(BasePlayerCharacterEntity ghost, int exp, int gold, IEnumerable<CurrencyAmount> currencies, float multiplier, RewardGivenType givenType, int giverLevel, int sourceLevel)
        {
            if (!IsServer || ghost == null)
                return;
            MeshIdentity id = MeshIdentity.Get(ghost);
            if (id == null || !id.IsGhost || string.IsNullOrEmpty(id.OwnerZoneId))
                return;
            if (!_linkClients.TryGetValue(id.OwnerZoneId, out MeshLinkClient link) || !link.IsConnected)
            {
                Logging.LogWarning("Mesh", $"Reward for ghost {ghost.CharacterName} dropped: no link to owner zone {id.OwnerZoneId}");
                return;
            }
            PackCurrencies(currencies, out int[] currencyIds, out int[] currencyAmounts);
            if (exp <= 0 && gold <= 0 && currencyIds.Length == 0)
                return;
            link.SendGhostReward(new GhostRewardMessage
            {
                attackerMeshId = id.MeshEntityId, exp = exp, gold = gold, multiplier = multiplier, givenType = (byte)givenType,
                giverLevel = giverLevel, sourceLevel = sourceLevel, currencyDataIds = currencyIds, currencyAmounts = currencyAmounts,
            });
            if (traceHandoffTiming)
                Logging.Log("Mesh", $"Reward on ghost {ghost.CharacterName} forwarded to zone {id.OwnerZoneId}: exp {exp} gold {gold} currencies {currencyIds.Length} x{multiplier} ({givenType})");
        }

        /// <summary>
        /// True when a player ghost (a real player standing on a neighbouring cell) is within range of a position.
        /// Used by MeshMonsterActivityComponent to keep edge monsters awake for players watching from next door.
        /// </summary>
        internal bool IsPlayerGhostNear(Vector3 position, float range)
        {
            float sqr = range * range;
            for (int i = 0; i < _playerGhosts.Count; ++i)
            {
                BaseCharacterEntity g = _playerGhosts[i];
                if (g == null || g.IsDead())
                    continue;
                if ((g.EntityTransform.position - position).sqrMagnitude <= sqr)
                    return true;
            }
            return false;
        }

        private readonly Dictionary<BaseMonsterCharacterEntity, CharacterEntityKilledDelegate> _killHooks = new Dictionary<BaseMonsterCharacterEntity, CharacterEntityKilledDelegate>();
        private readonly List<BaseMonsterCharacterEntity> _tmpMonsters = new List<BaseMonsterCharacterEntity>();

        private void UnhookKill(BaseMonsterCharacterEntity monster)
        {
            if (monster == null) return;
            if (_killHooks.TryGetValue(monster, out CharacterEntityKilledDelegate hook))
            {
                monster.onKilled -= hook;
                _killHooks.Remove(monster);
            }
        }

        private void PruneKillHooks()
        {
            _tmpMonsters.Clear();
            foreach (BaseMonsterCharacterEntity m in _killHooks.Keys)
                if (m == null) _tmpMonsters.Add(m);
            for (int i = 0; i < _tmpMonsters.Count; ++i)
                _killHooks.Remove(_tmpMonsters[i]);
        }

        /// <summary>An owned monster died. If the last attacker is a ghost, its owner cell counts the kill.</summary>
        private void OnOwnedMonsterKilled(BaseMonsterCharacterEntity monster, EntityInfo instigator)
        {
            UnhookKill(monster);
            if (monster == null || !instigator.TryGetEntity(out BaseCharacterEntity killer) || !(killer is BasePlayerCharacterEntity))
                return;
            MeshIdentity killerId = MeshIdentity.Get(killer);
            if (killerId == null || !killerId.IsGhost || string.IsNullOrEmpty(killerId.OwnerZoneId))
                return;
            if (!_linkClients.TryGetValue(killerId.OwnerZoneId, out MeshLinkClient link) || !link.IsConnected)
                return;
            MeshIdentity mid = MeshIdentity.Get(monster);
            link.SendGhostKill(new GhostKillMessage
            {
                attackerMeshId = killer.Id, monsterMeshId = mid != null ? mid.MeshEntityId : string.Empty,
                monsterDataId = monster.DataId, monsterLevel = monster.Level,
            });
            if (traceHandoffTiming)
                Logging.Log("Mesh", $"Kill by ghost {killer.CharacterName} of monster {monster.DataId} forwarded to zone {killerId.OwnerZoneId}");
        }

        internal void OnGhostKill(GhostKillMessage msg)
        {
            if (!IsServer)
                return;
            if (!ServerUserHandlers.TryGetPlayerCharacterById(msg.attackerMeshId, out IPlayerCharacterData data) || !(data is BasePlayerCharacterEntity player))
                return;
            // Quest kill counters for the killer and (as the kit does) every party member online here.
            int counted = CountQuestKill(player, msg.monsterDataId) ? 1 : 0;
            int forwarded = 0;
            if (GameInstance.ServerPartyHandlers.TryGetParty(player.PartyId, out PartyData party) && party != null)
                counted += CreditKillToPartyMembers(player, party, player.Level, msg.monsterLevel, msg.monsterDataId, member => CountQuestKill(member, msg.monsterDataId), out forwarded);
            if (traceHandoffTiming)
                Logging.Log("Mesh", $"Kill credit for {player.CharacterName}: monster {msg.monsterDataId} lv{msg.monsterLevel}, quest counters advanced on {counted} character(s) here, forwarded to {forwarded} member(s) on other cells");
        }

        private static bool CountQuestKill(BasePlayerCharacterEntity character, int monsterDataId)
        {
            bool any = false;
            for (int i = 0; i < character.Quests.Count; ++i)
            {
                CharacterQuest quest = character.Quests[i];
                if (quest.AddKillMonster(monsterDataId, 1))
                {
                    character.Quests[i] = quest;
                    any = true;
                }
            }
            return any;
        }

        internal void OnGhostReward(GhostRewardMessage msg)
        {
            if (!IsServer)
                return;
            if (!ServerUserHandlers.TryGetPlayerCharacterById(msg.attackerMeshId, out IPlayerCharacterData data) || !(data is BasePlayerCharacterEntity player))
            {
                Logging.LogWarning("Mesh", $"Forwarded reward for {msg.attackerMeshId} dropped: character not on this cell");
                return;
            }
            RewardGivenType givenType = (RewardGivenType)msg.givenType;
            // The killing cell skipped guild and party sharing (its ghost carries neither); do it here with the real
            // character, mirroring BaseMonsterCharacterEntity.GivingRewardToGuild / GivingRewardToParty.
            float shareGuildExpRate = 0f;
            bool sharedExp = false, sharedGold = false;
            if (givenType == RewardGivenType.KillMonster)
            {
                if (GameInstance.ServerGuildHandlers.TryGetGuild(player.GuildId, out GuildData guild) && guild != null)
                {
                    shareGuildExpRate = guild.ShareExpPercentage(player.Id) * 0.01f;
                    if (shareGuildExpRate > 0f && msg.exp > 0)
                        GameInstance.ServerGuildHandlers.IncreaseGuildExp(player, Mathf.CeilToInt(msg.exp * shareGuildExpRate * msg.multiplier)).Forget();
                }
                if (GameInstance.ServerPartyHandlers.TryGetParty(player.PartyId, out PartyData party) && party != null)
                {
                    PartyShareSets sets = CollectPartyMembers(player, party);
                    sharedExp = party.shareExp && msg.exp > 0 && SharePartyReward(player, sets.expMembers, sets.expGhosts, sets.expRemote, msg.exp, (1f - shareGuildExpRate) * msg.multiplier, 0, 0f, null, msg.giverLevel, msg.sourceLevel);
                    sharedGold = party.shareItem && msg.gold > 0 && SharePartyReward(player, sets.itemMembers, sets.itemGhosts, sets.itemRemote, 0, 0f, msg.gold, msg.multiplier, null, msg.giverLevel, msg.sourceLevel);
                    if (traceHandoffTiming && (sharedExp || sharedGold))
                        Logging.Log("Mesh", $"Party {party.id}: shared exp with {sets.expMembers.Count} here + {sets.expGhosts.Count} on other cells + {sets.expRemote.Count} remote, gold with {sets.itemMembers.Count} + {sets.itemGhosts.Count} + {sets.itemRemote.Count}");
                }
            }
            if (msg.exp > 0 && !sharedExp)
                player.RewardExp(msg.exp, (1f - shareGuildExpRate) * msg.multiplier, givenType, msg.giverLevel, msg.sourceLevel);
            if (msg.gold > 0 && !sharedGold)
                player.RewardGold(msg.gold, msg.multiplier, givenType, msg.giverLevel, msg.sourceLevel);
            List<CurrencyAmount> currencies = UnpackCurrencies(msg.currencyDataIds, msg.currencyAmounts);
            if (currencies != null)
                player.RewardCurrencies(currencies, msg.multiplier, givenType, msg.giverLevel, msg.sourceLevel);
            if (traceHandoffTiming)
                Logging.Log("Mesh", $"Forwarded reward applied to {player.CharacterName}: exp {msg.exp} gold {msg.gold} x{msg.multiplier} -> level={player.Level} exp={player.Exp} gold={player.Gold}");
        }

        // ---- Party share across cells (DESIGN §12) ----
        // The kit shares a kill with party members online on the same map server, by distance. On the mesh a member can
        // stand on another cell; if it is near enough it is a ghost here (the band is wider than any sane share
        // distance), so the sharing cell counts it in the split and forwards its share to the cell that owns it, which
        // applies it to the real character (and its pet, as the kit does) with no further sharing. A member beyond the
        // band is treated as out of range even when the share distance is "unlimited" (0) — open, see DESIGN.

        /// <summary>
        /// A party member's ghost standing here (alive, not a reserved arrival, not our own demoted handoff still in
        /// flight — that character is registered nowhere yet and a share sent to it would be dropped).
        /// </summary>
        internal bool TryGetPlayerGhost(string characterId, out BasePlayerCharacterEntity ghost)
        {
            ghost = null;
            if (!_ghosts.TryGetValue(characterId, out MeshGhost g) || g.kind != GhostKind.Player || g.reserved || _pendingHandoffAcks.ContainsKey(characterId) || !(g.entity is BasePlayerCharacterEntity pg) || pg.IsDead())
                return false;
            ghost = pg;
            return true;
        }

        /// <summary>Who shares a kill: members real here, members whose ghost stands here, members reachable only by broadcast.</summary>
        internal sealed class PartyShareSets
        {
            public readonly List<BasePlayerCharacterEntity> expMembers = new List<BasePlayerCharacterEntity>(), itemMembers = new List<BasePlayerCharacterEntity>();
            public readonly List<BasePlayerCharacterEntity> expGhosts = new List<BasePlayerCharacterEntity>(), itemGhosts = new List<BasePlayerCharacterEntity>();
            public readonly List<string> expRemote = new List<string>(), itemRemote = new List<string>();
            public bool AnyAcrossTheLine => expGhosts.Count + itemGhosts.Count + expRemote.Count + itemRemote.Count > 0;
        }

        /// <summary>
        /// The kit's member selection (online, alive, by distance), plus the members whose ghost stands here, plus — when
        /// the share distance is unlimited (the kit then shares with every online member of the map server) — the members
        /// that are neither here nor a ghost here but online somewhere on the cluster: they stand on some other cell.
        /// A member in the middle of a handoff we sent is registered nowhere and is left out, as the kit leaves out an
        /// unregistered member.
        /// </summary>
        internal PartyShareSets CollectPartyMembers(BasePlayerCharacterEntity killer, PartyData party)
        {
            PartyShareSets sets = new PartyShareSets();
            float expDist = GameInstance.Singleton.partyShareExpDistance, itemDist = GameInstance.Singleton.partyShareItemDistance;
            Vector3 killerPos = killer.EntityTransform.position;
            foreach (string memberId in party.GetMemberIds())
            {
                BasePlayerCharacterEntity member;
                bool here;
                if (ServerUserHandlers.TryGetPlayerCharacterById(memberId, out member) && member != null)
                    here = true;
                else if (TryGetPlayerGhost(memberId, out member))
                    here = false;
                else
                {
                    if (memberId != killer.Id && IsRemotePartyMember(party, memberId))
                    {
                        if (party.shareExp && expDist <= 0f) sets.expRemote.Add(memberId);
                        if (party.shareItem && itemDist <= 0f) sets.itemRemote.Add(memberId);
                    }
                    continue; // offline, mid-handoff, or on a cell too far for a ghost here
                }
                if (member.IsDead())
                    continue;
                float dist = Vector3.Distance(killerPos, member.EntityTransform.position);
                if (party.shareExp && (expDist <= 0f || dist <= expDist))
                    (here ? sets.expMembers : sets.expGhosts).Add(member);
                if (party.shareItem && (itemDist <= 0f || dist <= itemDist))
                    (here ? sets.itemMembers : sets.itemGhosts).Add(member);
            }
            return sets;
        }

        /// <summary>A member neither here nor a ghost here that a broadcast can still reach: linked, online, not mid-handoff.</summary>
        internal bool IsRemotePartyMember(PartyData party, string memberId)
        {
            return linkAllZones && !_pendingHandoffAcks.ContainsKey(memberId) && IsPartyMemberOnline(party, memberId);
        }

        /// <summary>
        /// The split: every member in the three sets counts once, members here are paid directly (the kit's pet split
        /// included), ghosts through their owner cell, remote members by broadcast. False when nobody shares.
        /// </summary>
        internal bool SharePartyReward(BasePlayerCharacterEntity killer, List<BasePlayerCharacterEntity> members, List<BasePlayerCharacterEntity> ghosts, List<string> remote, int exp, float expRate, int gold, float goldRate, IEnumerable<CurrencyAmount> currencies, int giverLevel, int sourceLevel)
        {
            int count = members.Count + ghosts.Count + remote.Count;
            if (count == 0)
                return false;
            float expMultiplier = expRate / count, goldMultiplier = goldRate / count;
            List<CurrencyAmount> currencyList = currencies as List<CurrencyAmount> ?? (currencies != null ? new List<CurrencyAmount>(currencies) : null);
            foreach (BasePlayerCharacterEntity member in members)
                GivePartyShareHere(member, exp, expMultiplier, gold, goldMultiplier, currencyList, ReferenceEquals(member, killer) ? RewardGivenType.KillMonster : RewardGivenType.PartyShare, giverLevel, sourceLevel);
            foreach (BasePlayerCharacterEntity ghost in ghosts)
                ForwardPartyShare(ghost, exp, expMultiplier, gold, goldMultiplier, currencyList, giverLevel, sourceLevel, 0, false);
            foreach (string memberId in remote)
                BroadcastPartyShare(memberId, exp, expMultiplier, gold, goldMultiplier, currencyList, giverLevel, sourceLevel, 0, false);
            return true;
        }

        /// <summary>
        /// Kill progress for every other online member, as the kit does regardless of the share flags: members here
        /// through <paramref name="creditHere"/>, members on another cell through their own cell. Returns how many were
        /// credited here; <paramref name="forwarded"/> counts the ones sent away.
        /// </summary>
        internal int CreditKillToPartyMembers(BasePlayerCharacterEntity killer, PartyData party, int giverLevel, int monsterLevel, int monsterDataId, System.Func<BasePlayerCharacterEntity, bool> creditHere, out int forwarded)
        {
            int counted = 0;
            forwarded = 0;
            foreach (string memberId in party.GetMemberIds())
            {
                if (memberId == killer.Id)
                    continue;
                if (ServerUserHandlers.TryGetPlayerCharacterById(memberId, out BasePlayerCharacterEntity member) && member != null)
                {
                    if (!member.IsDead() && creditHere(member))
                        counted++;
                }
                else if (TryGetPlayerGhost(memberId, out BasePlayerCharacterEntity memberGhost))
                {
                    ForwardPartyShare(memberGhost, 0, 0f, 0, 0f, null, giverLevel, monsterLevel, monsterDataId, true);
                    forwarded++;
                }
                else if (IsRemotePartyMember(party, memberId))
                {
                    BroadcastPartyShare(memberId, 0, 0f, 0, 0f, null, giverLevel, monsterLevel, monsterDataId, true);
                    forwarded++;
                }
            }
            return counted;
        }

        [Tooltip("A party member on another cell counts as online when the kit's online table saw it within this many seconds (the cluster relays every online player once a second).")]
        public float partyMemberOnlineWindowSeconds = 5f;

        /// <summary>
        /// Online somewhere on the cluster, as the kit knows it: DefaultServerCharacterHandlers marks local players by
        /// character id and, through the cluster's online relay, players on other map servers by user id.
        /// </summary>
        internal bool IsPartyMemberOnline(PartyData party, string memberId)
        {
            float now = Time.unscaledTime;
            if (DefaultServerCharacterHandlers.OnlineCharacterIds.TryGetValue(memberId, out float seen) && now - seen <= partyMemberOnlineWindowSeconds)
                return true;
            if (party != null && party.TryGetMember(memberId, out SocialCharacterData member) && !string.IsNullOrEmpty(member.userId)
                && DefaultServerCharacterHandlers.OnlineCharacterIds.TryGetValue(member.userId, out seen) && now - seen <= partyMemberOnlineWindowSeconds)
                return true;
            return false;
        }

        /// <summary>Apply a share to a member that is real here, splitting exp with its pet as the kit does.</summary>
        internal void GivePartyShareHere(BasePlayerCharacterEntity member, int exp, float expMultiplier, int gold, float goldMultiplier, List<CurrencyAmount> currencies, RewardGivenType givenType, int giverLevel, int sourceLevel)
        {
            if (member == null)
                return;
            if (exp > 0 && expMultiplier > 0f)
            {
                int petIndex = member.IndexOfSummon(SummonType.PetItem);
                BaseMonsterCharacterEntity pet = petIndex >= 0 ? member.Summons[petIndex].CacheEntity : null;
                if (pet != null)
                {
                    pet.RewardExp(exp, expMultiplier * 0.5f, RewardGivenType.PartyShare, giverLevel, sourceLevel);
                    member.RewardExp(exp, expMultiplier * 0.5f, givenType, giverLevel, sourceLevel);
                }
                else
                    member.RewardExp(exp, expMultiplier, givenType, giverLevel, sourceLevel);
            }
            if (gold > 0 && goldMultiplier > 0f)
                member.RewardGold(gold, goldMultiplier, givenType, giverLevel, sourceLevel);
            if (currencies != null && currencies.Count > 0 && goldMultiplier > 0f)
                member.RewardCurrencies(currencies, goldMultiplier, givenType, giverLevel, sourceLevel);
        }

        /// <summary>Currencies on the wire: parallel data-id and amount arrays (empty arrays for none).</summary>
        private static void PackCurrencies(IEnumerable<CurrencyAmount> currencies, out int[] ids, out int[] amounts)
        {
            if (currencies == null)
            {
                ids = System.Array.Empty<int>();
                amounts = System.Array.Empty<int>();
                return;
            }
            List<int> idList = new List<int>(), amountList = new List<int>();
            foreach (CurrencyAmount c in currencies)
            {
                if (c.currency == null || c.amount <= 0) continue;
                idList.Add(c.currency.DataId); amountList.Add(c.amount);
            }
            ids = idList.ToArray();
            amounts = amountList.ToArray();
        }

        /// <summary>The inverse of PackCurrencies; null when there is nothing to give.</summary>
        private static List<CurrencyAmount> UnpackCurrencies(int[] ids, int[] amounts)
        {
            if (ids == null || amounts == null || ids.Length == 0)
                return null;
            List<CurrencyAmount> currencies = new List<CurrencyAmount>();
            for (int i = 0; i < ids.Length && i < amounts.Length; ++i)
            {
                if (GameInstance.Currencies.TryGetValue(ids[i], out Currency currency))
                    currencies.Add(new CurrencyAmount { currency = currency, amount = amounts[i] });
            }
            return currencies.Count > 0 ? currencies : null;
        }

        private static GhostPartyShareMessage BuildPartyShare(string memberId, int exp, float expMultiplier, int gold, float goldMultiplier, IEnumerable<CurrencyAmount> currencies, int giverLevel, int sourceLevel, int monsterDataId, bool countKill, bool broadcast)
        {
            PackCurrencies(currencies, out int[] currencyIds, out int[] currencyAmounts);
            return new GhostPartyShareMessage
            {
                memberId = memberId, exp = exp, expMultiplier = expMultiplier, gold = gold, goldMultiplier = goldMultiplier,
                currencyDataIds = currencyIds, currencyAmounts = currencyAmounts, giverLevel = giverLevel, sourceLevel = sourceLevel,
                monsterDataId = monsterDataId, countKill = countKill, broadcast = broadcast,
            };
        }

        /// <summary>Send a member's share to the cell that owns it (the member is a ghost here).</summary>
        internal void ForwardPartyShare(BasePlayerCharacterEntity memberGhost, int exp, float expMultiplier, int gold, float goldMultiplier, IEnumerable<CurrencyAmount> currencies, int giverLevel, int sourceLevel, int monsterDataId, bool countKill)
        {
            MeshIdentity id = MeshIdentity.Get(memberGhost);
            if (id == null || !id.IsGhost || string.IsNullOrEmpty(id.OwnerZoneId))
                return;
            if (!_linkClients.TryGetValue(id.OwnerZoneId, out MeshLinkClient link) || !link.IsConnected)
            {
                WarnThrottled($"Party share for {memberGhost.CharacterName} dropped: no link to owner zone {id.OwnerZoneId}");
                return;
            }
            link.SendGhostPartyShare(BuildPartyShare(memberGhost.Id, exp, expMultiplier, gold, goldMultiplier, currencies, giverLevel, sourceLevel, monsterDataId, countKill, false));
            if (traceHandoffTiming)
                Logging.Log("Mesh", $"Party share for {memberGhost.CharacterName} forwarded to zone {id.OwnerZoneId}: exp {exp} x{expMultiplier:0.###} gold {gold} x{goldMultiplier:0.###} countKill={countKill}");
        }

        /// <summary>A member whose cell is unknown: every linked cell gets the share; the one holding the character applies it.</summary>
        internal void BroadcastPartyShare(string memberId, int exp, float expMultiplier, int gold, float goldMultiplier, IEnumerable<CurrencyAmount> currencies, int giverLevel, int sourceLevel, int monsterDataId, bool countKill)
        {
            GhostPartyShareMessage msg = BuildPartyShare(memberId, exp, expMultiplier, gold, goldMultiplier, currencies, giverLevel, sourceLevel, monsterDataId, countKill, true);
            int sent = 0;
            foreach (MeshLinkClient link in _linkClients.Values)
            {
                if (!link.IsConnected) continue;
                link.SendGhostPartyShare(msg);
                sent++;
            }
            if (traceHandoffTiming)
                Logging.Log("Mesh", $"Party share for remote member {memberId} broadcast to {sent} cell(s): exp {exp} x{expMultiplier:0.###} gold {gold} x{goldMultiplier:0.###} countKill={countKill}");
        }

        internal void OnGhostPartyShare(long connectionId, GhostPartyShareMessage msg)
        {
            if (!IsServer)
                return;
            if (!ServerUserHandlers.TryGetPlayerCharacterById(msg.memberId, out IPlayerCharacterData data) || !(data is BasePlayerCharacterEntity member) || member.IsDead())
            {
                if (!msg.broadcast)
                    WarnThrottled($"Party share for {msg.memberId} dropped: character not on this cell");
                return;
            }
            GivePartyShareHere(member, msg.exp, msg.expMultiplier, msg.gold, msg.goldMultiplier, UnpackCurrencies(msg.currencyDataIds, msg.currencyAmounts), RewardGivenType.PartyShare, msg.giverLevel, msg.sourceLevel);
            bool counted = msg.countKill && CountQuestKill(member, msg.monsterDataId);
            if (traceHandoffTiming)
                Logging.Log("Mesh", $"Party share applied to {member.CharacterName}: exp {msg.exp} x{msg.expMultiplier:0.###} gold {msg.gold} x{msg.goldMultiplier:0.###} questKill={counted} -> level={member.Level} exp={member.Exp} gold={member.Gold}");
        }

        internal void OnGhostHitResult(GhostHitResultMessage msg)
        {
            if (!IsServer || !_ghosts.TryGetValue(msg.targetMeshId, out MeshGhost ghost) || ghost.entity == null)
                return;
            ghost.entity.CurrentHp = msg.currentHp;
            if (msg.currentHp <= 0)
                UnlinkSummonPuppets(msg.targetMeshId); // see SteerGhost
            ghost.entity.CallRpcAppendCombatText((CombatAmountType)msg.combatAmountType, HitEffectsSourceType.None, 0, msg.totalDamage);
        }

        // ---- Incoming handoffs: the neighbour tells us a character is about to reconnect here ----

        [Tooltip("For a character arriving by mesh handoff, do not block its spawn on re-loading relates data (gold, cash, user level, party, guild, storage) from the DB; load them in the background instead. Cuts ~150-300 ms off the swap.")]
        public bool fastSpawnOnHandoff = true;

        // ---- Handoff acknowledgement (DESIGN §11) ----
        // The origin learns that its handoff completed (the character registered on the destination) or failed (the
        // client never claimed it), instead of inferring either from silence. Until the ack (or its timeout) the
        // demoted puppet and its pets are exempt from stale expiry, so a slow arrival never blinks them on the cell the
        // player just left; a failed handoff releases them at once.

        private sealed class IncomingHandoff { public string fromZone; public float time; public bool fastSpawned; }
        private static readonly string s_faultDieOnNoticeZone = System.Environment.GetEnvironmentVariable("MESH_FAULT_DIE_ON_NOTICE"); // a zone id: that cell dies on its next handoff notice
        private sealed class PendingHandoffAck { public string zone; public string name; public float startTime; public float sentTime; }
        private readonly Dictionary<string, IncomingHandoff> _incomingHandoffFrom = new Dictionary<string, IncomingHandoff>(); // characterId -> who announced it
        private readonly Dictionary<string, PendingHandoffAck> _pendingHandoffAcks = new Dictionary<string, PendingHandoffAck>(); // characterId -> our handoff awaiting the destination's ack

        [Tooltip("Seconds after 'Handoff SENT' with the destination linked but silent (no ack either way; it acks a failed arrival itself after its reservation timeout) before the handoff is rolled back.")]
        public float handoffAckGuardSeconds = 20f;

        [Tooltip("After a rollback, how long the demoted puppet is kept for its client to come back to this cell (the client falls back to the cell it left when the destination refuses or times out).")]
        public float handoffRollbackHoldSeconds = 20f;

        // Handoffs rolled back and still holding their puppet for the client's return (characterId).
        private readonly HashSet<string> _rolledBackHandoffs = new HashSet<string>();

        /// <summary>
        /// The commit point of a handoff is the destination's ack (the character registered there). Until then the
        /// character is still ours to take back: the destination died, its link did, or it never answered. The row in
        /// the DB is untouched (the handoff save carries OUR channel; only the destination's own saves move it), so a
        /// client that comes back here, by the migration fallback or by a fresh login, promotes the puppet it left.
        /// </summary>
        private void RollBackHandoff(string characterId, PendingHandoffAck pending, string reason)
        {
            _pendingHandoffAcks.Remove(characterId);
            float until = Time.unscaledTime + handoffRollbackHoldSeconds;
            bool held = false;
            if (_ghosts.TryGetValue(characterId, out MeshGhost puppet) && puppet.entity != null && !puppet.reserved)
            {
                puppet.holdUntil = until;
                held = true;
            }
            foreach (string petId in PetGhostsOf(characterId))
            {
                if (_ghosts.TryGetValue(petId, out MeshGhost pet))
                    pet.holdUntil = until;
            }
            if (held)
                _rolledBackHandoffs.Add(characterId);
            Logging.LogWarning("Mesh", $"Handoff ROLLBACK {pending.name} -> zone {pending.zone}: {reason}; {(held ? $"the puppet is held {handoffRollbackHoldSeconds:0}s for the client's return" : "no puppet left to hold")}");
        }

        private void RollBackHandoffsToward(string zoneId, string reason)
        {
            if (_pendingHandoffAcks.Count == 0)
                return;
            List<string> toward = null;
            foreach (KeyValuePair<string, PendingHandoffAck> kv in _pendingHandoffAcks)
                if (string.Equals(kv.Value.zone, zoneId)) (toward ??= new List<string>()).Add(kv.Key);
            if (toward == null)
                return;
            foreach (string characterId in toward)
                if (_pendingHandoffAcks.TryGetValue(characterId, out PendingHandoffAck pending))
                    RollBackHandoff(characterId, pending, reason);
        }

        internal void OnHandoffNotice(long connectionId, HandoffNoticeMessage msg)
        {
            if (!string.IsNullOrEmpty(s_faultDieOnNoticeZone) && string.Equals(s_faultDieOnNoticeZone, OwnZoneId))
            {
                // Test switch (MESH_FAULT_DIE_ON_NOTICE=<zoneId>): that destination dies in the window between the
                // origin's notice and the client's arrival, the one moment a crash cannot be produced by hand. Once
                // per test run: the kit's MapSpawn restarts the dead server (with this environment) within seconds,
                // and the marker file keeps the restarted incarnation alive.
                string marker = System.IO.Path.Combine("Temp", $"ServerMesh.FaultFired.{OwnZoneId}");
                if (!System.IO.File.Exists(marker))
                {
                    try { System.IO.Directory.CreateDirectory("Temp"); System.IO.File.WriteAllText(marker, System.DateTime.Now.ToString("HH:mm:ss.fff")); } catch { }
                    Logging.LogError("Mesh", $"FAULT: dying on handoff notice for {msg.characterId} (MESH_FAULT_DIE_ON_NOTICE)");
                    System.Diagnostics.Process.GetCurrentProcess().Kill();
                }
            }
            _incomingHandoffFrom[msg.characterId] = new IncomingHandoff
            {
                fromZone = _neighbourZoneByConnection.TryGetValue(connectionId, out string z) ? z : null,
                time = Time.unscaledTime,
            };
        }

        /// <summary>Origin side: remember a handoff we sent so the destination's ack can be matched and timed.</summary>
        internal void RegisterPendingHandoffAck(string characterId, string name, string zone, float startTime)
        {
            _pendingHandoffAcks[characterId] = new PendingHandoffAck { zone = zone, name = name, startTime = startTime, sentTime = Time.unscaledTime };
        }

        internal bool IsNeighbourLinked(string zoneId)
        {
            return _linkClients.TryGetValue(zoneId, out MeshLinkClient link) && link.IsConnected;
        }

        internal void OnHandoffAck(long connectionId, HandoffAckMessage msg)
        {
            if (!IsServer || !_pendingHandoffAcks.TryGetValue(msg.characterId, out PendingHandoffAck pending))
                return;
            _pendingHandoffAcks.Remove(msg.characterId);
            string fromZone = _neighbourZoneByConnection.TryGetValue(connectionId, out string z) ? z : "?";
            if (msg.ok)
            {
                float totalMs = (Time.unscaledTime - pending.startTime) * 1000f;
                NoteHandoffCompleted(totalMs);
                Logging.Log("Mesh", $"Handoff ACK {pending.name} by zone {fromZone}: registered {msg.arrivalMs} ms after the notice, {totalMs:0} ms after START (promoted={msg.promoted})");
                // The exemption from stale expiry ends with the ack; the destination's first snapshot is still a tick
                // away, so the demoted puppets get a fresh stale window from now.
                if (_ghosts.TryGetValue(msg.characterId, out MeshGhost demoted))
                    demoted.lastSeen = Time.unscaledTime;
                foreach (string petId in PetGhostsOf(msg.characterId))
                {
                    if (_ghosts.TryGetValue(petId, out MeshGhost pet))
                        pet.lastSeen = Time.unscaledTime;
                }
                if (!msg.promoted)
                {
                    // The destination had no puppet to promote and re-summoned the pets from the saved data: the pets we
                    // demoted with the player will never be steered (new mesh ids over there) — drop them now instead of
                    // standing beside the new ones until they expire stale.
                    ReleaseDemotedPets(msg.characterId, "destination re-summoned");
                }
                return;
            }
            Logging.LogWarning("Mesh", $"Handoff FAILED {pending.name} -> zone {fromZone}: {msg.reason}; releasing the demoted puppet here");
            ReleaseDemotedHandoff(msg.characterId, "handoff failed: " + msg.reason);
        }

        /// <summary>Destination side: tell the origin its character registered here.</summary>
        private void AckHandoffArrival(string characterId, string characterName, bool promoted)
        {
            if (!_incomingHandoffFrom.TryGetValue(characterId, out IncomingHandoff incoming))
                return;
            _incomingHandoffFrom.Remove(characterId);
            SendHandoffAck(incoming.fromZone, new HandoffAckMessage
            {
                characterId = characterId,
                ok = true,
                reason = string.Empty,
                arrivalMs = Mathf.RoundToInt((Time.unscaledTime - incoming.time) * 1000f),
                promoted = promoted,
            });
        }

        /// <summary>Destination side: the announced character will not register here.</summary>
        private void AckHandoffFailed(string characterId, string reason)
        {
            if (!_incomingHandoffFrom.TryGetValue(characterId, out IncomingHandoff incoming))
                return;
            _incomingHandoffFrom.Remove(characterId);
            SendHandoffAck(incoming.fromZone, new HandoffAckMessage { characterId = characterId, ok = false, reason = reason, arrivalMs = 0 });
        }

        private void SendHandoffAck(string zoneId, HandoffAckMessage msg)
        {
            if (!string.IsNullOrEmpty(zoneId) && _linkClients.TryGetValue(zoneId, out MeshLinkClient client) && client.IsConnected)
                client.SendHandoffAck(msg);
        }

        /// <summary>A ghost that is our own demoted handoff (or a pet demoted with it) still waiting for the ack.</summary>
        private bool IsAwaitingHandoffAck(string meshId, MeshGhost ghost)
        {
            if (_pendingHandoffAcks.Count == 0)
                return false;
            if (_pendingHandoffAcks.ContainsKey(meshId))
                return true;
            return ghost.kind == GhostKind.Monster && !string.IsNullOrEmpty(ghost.summonerMeshId) && _pendingHandoffAcks.ContainsKey(ghost.summonerMeshId);
        }

        /// <summary>The handoff did not complete: the demoted puppet and the pets demoted with it go now.</summary>
        private void ReleaseDemotedHandoff(string characterId, string reason)
        {
            if (_ghosts.TryGetValue(characterId, out MeshGhost g) && !g.reserved)
                DestroyGhost(characterId, reason);
            ReleaseDemotedPets(characterId, reason);
        }

        /// <summary>The pets demoted here with a player: born on this cell, not waiting with an arrival.</summary>
        private void ReleaseDemotedPets(string characterId, string reason)
        {
            DestroySummonPuppets(characterId, null, reason, onlyBornHere: true, skipReserved: true);
        }

        private void TickHandoffAcks()
        {
            // Own lists: the release path below reuses the shared scratch lists while these are being iterated.
            List<string> expired = null;
            if (_pendingHandoffAcks.Count > 0)
            {
                foreach (KeyValuePair<string, PendingHandoffAck> kv in _pendingHandoffAcks)
                {
                    // A linked destination always answers: promoted, or failed once its reservation timed out. Silence
                    // past that is a wedged destination; a dead link is a dead destination. Both roll back.
                    if (!IsNeighbourLinked(kv.Value.zone) || Time.unscaledTime - kv.Value.sentTime > handoffAckGuardSeconds)
                        (expired ??= new List<string>()).Add(kv.Key);
                }
                if (expired != null)
                {
                    foreach (string characterId in expired)
                    {
                        if (!_pendingHandoffAcks.TryGetValue(characterId, out PendingHandoffAck pending))
                            continue;
                        RollBackHandoff(characterId, pending, IsNeighbourLinked(pending.zone) ? $"no ack from the destination in {handoffAckGuardSeconds:0} s" : "link to the destination is down");
                    }
                }
            }
            if (_incomingHandoffFrom.Count > 0)
            {
                List<string> unclaimed = null;
                foreach (KeyValuePair<string, IncomingHandoff> kv in _incomingHandoffFrom)
                {
                    // A notice whose client never connected (a reservation that was claimed then abandoned is acked by
                    // ExpireReservedArrivals; one that registered was acked at registration).
                    if (Time.unscaledTime - kv.Value.time > reservedArrivalTimeout && !_reservedArrivals.ContainsKey(kv.Key) && !ServerUserHandlers.TryGetPlayerCharacterById(kv.Key, out _))
                        (unclaimed ??= new List<string>()).Add(kv.Key);
                }
                if (unclaimed != null)
                {
                    foreach (string characterId in unclaimed)
                        AckHandoffFailed(characterId, "client never arrived");
                }
            }
        }

        internal void SendHandoffNotice(string zoneId, string characterId, string userId)
        {
            if (_linkClients.TryGetValue(zoneId, out MeshLinkClient client))
                client.SendHandoffNotice(characterId, userId);
        }

        /// <summary>An arrival announced by a neighbour within the reservation window whose relates data was not yet fast-spawned.</summary>
        private bool TryClaimIncomingHandoff(string characterId)
        {
            if (!_incomingHandoffFrom.TryGetValue(characterId, out IncomingHandoff incoming) || incoming.fastSpawned || Time.unscaledTime - incoming.time >= reservedArrivalTimeout)
                return false;
            incoming.fastSpawned = true;
            return true;
        }

        /// <summary>
        /// The kit awaits ~5 sequential DB round trips here before it spawns the player. A character that was
        /// handed off a moment ago was fully loaded on the neighbour and saved by it, so for those we let the
        /// spawn proceed and fill the relates data in the background (extension seam: protected virtual).
        /// </summary>
        protected override UniTask LoadPlayerCharacterEntityRelatesData(long connectionId, BasePlayerCharacterEntity playerCharacterEntity, string userId, string accessToken)
        {
            if (fastSpawnOnHandoff && playerCharacterEntity != null && TryClaimIncomingHandoff(playerCharacterEntity.Id))
            {
                Stamp($"handoff arrival {playerCharacterEntity.CharacterName}: relates data loading in background");
                base.LoadPlayerCharacterEntityRelatesData(connectionId, playerCharacterEntity, userId, accessToken).Forget();
                return UniTask.CompletedTask;
            }
            return base.LoadPlayerCharacterEntityRelatesData(connectionId, playerCharacterEntity, userId, accessToken);
        }
#else
        private void GhostingOnStartServer() { }
        private void GhostingOnStopServer() { }
        private void GhostingUpdate() { }
        private void GhostingOnRealPlayerRegistered(BasePlayerCharacterEntity playerCharacter, long connectionId) { }
        private void GhostingOnRealPlayerUnregistered(long connectionId) { }
        private void GhostingOnPlayerHandoffStart(string characterId, string toZone) { }
        private bool TryPromoteGhostForArrival(long connectionId, string userId, PlayerCharacterData data, out BasePlayerCharacterEntity entity) { entity = null; return false; }
        private void DemotePlayerToGhost(BasePlayerCharacterEntity entity, string newOwnerZone) { }
        internal void OnMeshNeighbourHello(long connectionId, string zoneId) { }
        internal void OnMeshNeighbourDisconnected(long connectionId) { }
        internal void OnGhostSnapshot(long connectionId, GhostSnapshotMessage msg) { }
        internal void OnGhostRemove(string characterId) { }
        internal void OnHandoffNotice(long connectionId, HandoffNoticeMessage msg) { }
        internal void OnHandoffAck(long connectionId, HandoffAckMessage msg) { }
        internal void OnGhostPartyShare(long connectionId, GhostPartyShareMessage msg) { }
        internal bool TryGetPlayerGhost(string characterId, out BasePlayerCharacterEntity ghost) { ghost = null; return false; }
        internal bool IsPartyMemberOnline(PartyData party, string memberId) { return false; }
        internal sealed class PartyShareSets { public readonly List<BasePlayerCharacterEntity> expMembers = new List<BasePlayerCharacterEntity>(), itemMembers = new List<BasePlayerCharacterEntity>(), expGhosts = new List<BasePlayerCharacterEntity>(), itemGhosts = new List<BasePlayerCharacterEntity>(); public readonly List<string> expRemote = new List<string>(), itemRemote = new List<string>(); public bool AnyAcrossTheLine => false; }
        internal PartyShareSets CollectPartyMembers(BasePlayerCharacterEntity killer, PartyData party) { return new PartyShareSets(); }
        internal bool IsRemotePartyMember(PartyData party, string memberId) { return false; }
        internal bool SharePartyReward(BasePlayerCharacterEntity killer, List<BasePlayerCharacterEntity> members, List<BasePlayerCharacterEntity> ghosts, List<string> remote, int exp, float expRate, int gold, float goldRate, IEnumerable<CurrencyAmount> currencies, int giverLevel, int sourceLevel) { return false; }
        internal int CreditKillToPartyMembers(BasePlayerCharacterEntity killer, PartyData party, int giverLevel, int monsterLevel, int monsterDataId, System.Func<BasePlayerCharacterEntity, bool> creditHere, out int forwarded) { forwarded = 0; return 0; }
        internal void BroadcastPartyShare(string memberId, int exp, float expMultiplier, int gold, float goldMultiplier, IEnumerable<CurrencyAmount> currencies, int giverLevel, int sourceLevel, int monsterDataId, bool countKill) { }
        internal void GivePartyShareHere(BasePlayerCharacterEntity member, int exp, float expMultiplier, int gold, float goldMultiplier, List<CurrencyAmount> currencies, RewardGivenType givenType, int giverLevel, int sourceLevel) { }
        internal void ForwardPartyShare(BasePlayerCharacterEntity memberGhost, int exp, float expMultiplier, int gold, float goldMultiplier, IEnumerable<CurrencyAmount> currencies, int giverLevel, int sourceLevel, int monsterDataId, bool countKill) { }
        internal void RegisterPendingHandoffAck(string characterId, string name, string zone, float startTime) { }
        internal bool IsNeighbourLinked(string zoneId) { return true; }
        internal void OnGhostHit(long connectionId, GhostHitMessage msg) { }
        internal void OnGhostHitResult(GhostHitResultMessage msg) { }
        internal void OnGhostReward(GhostRewardMessage msg) { }
        internal void OnMonsterHandoff(long connectionId, MonsterHandoffMessage msg) { }
        internal void OnMonsterGone(string meshId) { }
        internal void OnGhostPickup(long connectionId, GhostPickupMessage msg) { }
        internal void OnGhostLoot(GhostLootMessage msg) { }
        internal void OnGhostKill(GhostKillMessage msg) { }
        internal void RequestGhostPickup(BasePlayerCharacterEntity player, ItemDropEntity ghostDrop) { }
        internal bool IsPlayerGhostNear(Vector3 position, float range) { return false; }
        internal void ForwardGhostReward(BasePlayerCharacterEntity ghost, int exp, int gold, IEnumerable<CurrencyAmount> currencies, float multiplier, RewardGivenType givenType, int giverLevel, int sourceLevel) { }
        internal void SendHandoffNotice(string zoneId, string characterId, string userId) { }
#endif
    }
}
