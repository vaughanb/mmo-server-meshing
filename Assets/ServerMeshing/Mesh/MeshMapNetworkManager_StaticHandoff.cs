// Server Meshing add-on — statics and item drops follow the owned rectangle (Slice 2b, slices/slice2b_design.md decision
// 10; plan D6). On a Dynamic map, a harvestable or item drop a cell owns that stands in another cell's owned rectangle
// of the same map (a respawn from this cell's spawn area in a rectangle it gave away included) goes to that owner:
//   sender:   demotes its copy to a puppet on send (hits and pickups on it go to the receiver, so they are refused while
//             in flight), records the loan, sends StaticHandoff (124); "Static handoff <id> zone <from>-><to>: sent";
//   receiver: dedupes by mesh id (one it owns already is acked again), promotes its puppet in place or spawns the entity
//             from the registered prefab ("promoted" / "spawned"), answers StaticHandoffAck (125), or StaticHandoffFailed
//             (126) when it cannot ("failed");
//   sender:   on the ack the loan is settled, and a home cell (whose spawn area made it) keeps a lent record; on a
//             failure, or with no answer within the ack guard (a lost link), it re-promotes its copy ("released") and
//             waits before trying that one again.
// LentStatic (127) tells the home cell who holds its static, or that it is gone (depleted, picked up, expired) so its
// spawn area respawns it. A neighbour's new incarnation releases statics like monsters. Reward drops (exp, gold,
// currency) are not item drops and stay with the killing cell. Buildings move as trees through the Slice 4 hook below
// (MeshMapNetworkManager_BuildingTransfer), never through StaticHandoff.
using System.Collections.Generic;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class MeshMapNetworkManager
    {
        [Tooltip("Dynamic maps: after a static handoff is refused or released, seconds before the same static is tried again.")]
        public float staticHandoffRetrySeconds = 10f;

#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        /// <summary>What a static was and how its home respawns it: kept with a loan, then with a home's lent record.</summary>
        private sealed class StaticLoan
        {
            public BaseGameEntity entity;
            public GhostKind kind;
            public string home;
            public GameSpawnArea<HarvestableEntity> harvestArea;
            public HarvestableEntity harvestPrefab;
            public GameSpawnArea<ItemDropEntity> dropArea;
            public ItemDropEntity dropPrefab;
            public int level;
            public float respawnDelay;
            public bool HasArea => harvestArea != null || dropArea != null;
        }

        private sealed class LentStatic { public string ownerZone; public float ownerSince; public StaticLoan spawn; }
        private struct StaticCandidate { public BaseGameEntity entity; public string meshId; public GhostKind kind; public MeshCell owner; }

        private readonly MeshStaticLoanBook<StaticLoan> _staticLoans = new MeshStaticLoanBook<StaticLoan>();
        private readonly Dictionary<string, LentStatic> _lentStatics = new Dictionary<string, LentStatic>(System.StringComparer.Ordinal);
        private readonly Dictionary<string, string> _borrowedStatics = new Dictionary<string, string>(System.StringComparer.Ordinal); // meshId -> home zone
        private readonly List<StaticCandidate> _staticCandidates = new List<StaticCandidate>();
        private readonly List<MeshStaticLoanBook<StaticLoan>.Loan> _loanScratch = new List<MeshStaticLoanBook<StaticLoan>.Loan>();
        private float _nextStaticHandoffScan;

        private void StaticHandoffOnStartServer()
        {
            _staticLoans.Clear();
            _lentStatics.Clear();
            _borrowedStatics.Clear();
            _staticCandidates.Clear();
            _nextStaticHandoffScan = 0f;
        }

        private int StaticLoansToward(string zone) => _staticLoans.CountToward(zone);

        private static void LogStaticHandoff(string meshId, string from, string to, string what)
        {
            Logging.Log("Mesh", $"Static handoff {meshId} zone {from}->{to}: {what}");
        }

        /// <summary>Once a second on a Dynamic map: keep in-flight puppets alive, take back loans past the guard, hand what stands in another cell's rectangle.</summary>
        private void StaticHandoffTick(float now)
        {
            if (now < _nextStaticHandoffScan)
                return;
            _nextStaticHandoffScan = now + 1f;
            foreach (MeshStaticLoanBook<StaticLoan>.Loan loan in _staticLoans.Loans)
            {
                if (_ghostStatics.TryGetValue(loan.meshId, out GhostStatic puppet))
                    puppet.lastSeen = now; // exempt from stale expiry while in flight
            }
            _loanScratch.Clear();
            _staticLoans.TakeExpired(now, HandoffAckGuardSeconds, staticHandoffRetrySeconds, _loanScratch);
            for (int i = 0; i < _loanScratch.Count; ++i)
                RepromoteStatic(_loanScratch[i]);
            _loanScratch.Clear();
            if (!enableBorderGhosting)
                return;
            BuildingTransferTick(now); // Slice 4 decision 8: building trees follow their root's ground (MeshMapNetworkManager_BuildingTransfer)
            _staticCandidates.Clear();
            List<OwnedEntry<DamageableEntity>> statics = _ownedStatics.Entries;
            for (int i = 0; i < statics.Count; ++i)
            {
                if (statics[i].entity is HarvestableEntity harvestable && harvestable != null && harvestable.CurrentHp > 0)
                    ConsiderStatic(harvestable, statics[i].meshId, GhostKind.Harvestable, now);
            }
            List<OwnedEntry<ItemDropEntity>> drops = _ownedDrops.Entries;
            for (int i = 0; i < drops.Count; ++i)
            {
                ItemDropEntity drop = drops[i].entity;
                if (drop != null && drop.DropItems.Count > 0)
                    ConsiderStatic(drop, drops[i].meshId, GhostKind.ItemDrop, now);
            }
            for (int i = 0; i < _staticCandidates.Count; ++i)
                SendStaticHandoff(_staticCandidates[i], now);
            _staticCandidates.Clear();
        }

        private void ConsiderStatic(BaseGameEntity entity, string meshId, GhostKind kind, float now)
        {
            if (string.IsNullOrEmpty(meshId) || entity.Identity == null || entity.Identity.IsDestroyed || _staticLoans.InBackoff(meshId, now))
                return;
            Vector3 pos = entity.EntityTransform.position;
            MeshCell owner = world.OwnerOf(OwnCell, pos);
            if (owner == null || MeshCell.Same(owner, OwnCell) || !string.Equals(owner.mapId, OwnCell.mapId, System.StringComparison.Ordinal))
                return;
            if (-OwnCell.DistanceToEdge(pos) < handoffHysteresis)
                return; // the same hysteresis as characters
            if (!IsNeighbourLinked(owner.id) || !IsNeighbourSettled(owner.id) || !AdmitEntityDrain(pos))
                return;
            _staticCandidates.Add(new StaticCandidate { entity = entity, meshId = meshId, kind = kind, owner = owner });
        }

        private void SendStaticHandoff(StaticCandidate candidate, float now)
        {
            if (!_linkClients.TryGetValue(candidate.owner.id, out MeshLinkClient link) || !link.IsConnected)
                return;
            BaseGameEntity entity = candidate.entity;
            MeshIdentity id = MeshIdentity.GetOrAdd(entity);
            StaticLoan loan = new StaticLoan { entity = entity, kind = candidate.kind };
            StaticHandoffMessage msg = new StaticHandoffMessage
            {
                meshId = candidate.meshId,
                kind = (byte)candidate.kind,
                entityId = entity.EntityId,
                position = entity.EntityTransform.position,
                yaw = entity.EntityTransform.eulerAngles.y,
                appearSeconds = -1f,
            };
            if (entity is HarvestableEntity harvestable)
            {
                loan.harvestArea = harvestable.SpawnArea;
                loan.harvestPrefab = harvestable.SpawnPrefab;
                loan.level = harvestable.SpawnLevel;
                loan.respawnDelay = harvestable.DestroyRespawnDelay;
                msg.spawnPosition = harvestable.SpawnPosition;
                msg.currentHp = harvestable.CurrentHp;
            }
            else if (entity is ItemDropEntity drop)
            {
                loan.dropArea = drop.SpawnArea;
                loan.dropPrefab = drop.SpawnPrefab;
                loan.level = drop.SpawnLevel;
                loan.respawnDelay = drop.DestroyRespawnDelay;
                msg.spawnPosition = drop.SpawnPosition;
                msg.givenType = (byte)drop.GivenType;
                msg.items = drop.DropItems.ToArray();
                string[] looters = new string[drop.Looters.Count];
                drop.Looters.CopyTo(looters);
                msg.looters = looters;
                if (drop.SpawnArea == null)
                    msg.appearSeconds = Mathf.Max(0.1f, GameInstance.Singleton.itemAppearDuration - OwnedAge(drop, now));
            }
            loan.home = !string.IsNullOrEmpty(id.HomeZoneId) ? id.HomeZoneId : loan.HasArea ? OwnZoneId : string.Empty;
            msg.level = loan.level;
            msg.respawnDelay = loan.respawnDelay;
            msg.homeZoneId = loan.home;
            if (!_staticLoans.TryBegin(candidate.meshId, candidate.owner.id, now, loan))
                return;
            _borrowedStatics.Remove(candidate.meshId); // handed on: the next holder tells the home
            link.SendStaticHandoff(msg);
            DemoteStaticOnSend(entity, id, candidate.meshId, candidate.kind, candidate.owner.id);
            LogStaticHandoff(candidate.meshId, OwnZoneId, candidate.owner.id, "sent");
        }

        /// <summary>The sender's copy becomes a puppet in place: no longer owned or streamed, hits forwarded to the new owner, the spawn area's despawn sweep detached.</summary>
        private void DemoteStaticOnSend(BaseGameEntity entity, MeshIdentity id, string meshId, GhostKind kind, string toZone)
        {
            UntrackOwned(entity);
            foreach (HashSet<string> streamed in _streamedToZone.Values)
                streamed.Remove(meshId); // no GhostRemove: the receiver promotes its puppet
            if (entity.TryGetComponent(out GameSpawnAreaEntityHandler areaHandler) && areaHandler.Handler != null)
            {
                areaHandler.Handler.OnEntityHandlerDestroy(areaHandler);
                areaHandler.Handler = null;
            }
            id.IsGhost = true;
            id.OwnerZoneId = toZone;
            id.LastHandoffTime = Time.unscaledTime;
            GhostSuppression.Ghostify(entity, this, null, false, false, disableMovement: false);
            ReceiveDamageDelegate hitHook = null;
            if (entity is DamageableEntity damageable)
            {
                damageable.IsInvincible = true;
                if (kind != GhostKind.ItemDrop)
                {
                    string capturedMeshId = meshId;
                    hitHook = (target, position, fromPosition, instigator, damageAmounts, weapon, skill, skillLevel) =>
                        ForwardGhostStaticHit(capturedMeshId, position, fromPosition, instigator, damageAmounts, weapon, skill, skillLevel);
                    damageable.onReceiveDamage += hitHook;
                }
            }
            _ghostStatics[meshId] = new GhostStatic { entity = entity, kind = kind, lastSeen = Time.unscaledTime, hitHook = hitHook };
        }

        /// <summary>A loan that failed or expired: the sender's puppet is its real entity again.</summary>
        private void RepromoteStatic(MeshStaticLoanBook<StaticLoan>.Loan loan)
        {
            NoteZoneRecordsReleased(loan.toZone, 1); // Slice 2c: counted toward a Draining zone's prune line
            BaseGameEntity entity = loan.data.entity;
            if (_ghostStatics.TryGetValue(loan.meshId, out GhostStatic puppet) && ReferenceEquals(puppet.entity, entity))
            {
                _ghostStatics.Remove(loan.meshId);
                if (puppet.hitHook != null && entity is DamageableEntity hooked)
                    hooked.onReceiveDamage -= puppet.hitHook;
            }
            if (entity == null || entity.Identity == null || entity.Identity.IsDestroyed)
            {
                LogStaticHandoff(loan.meshId, OwnZoneId, loan.toZone, "released");
                return;
            }
            GhostSuppression.Unghostify(entity, this, null);
            if (entity is DamageableEntity damageable)
                damageable.IsInvincible = false;
            MeshIdentity id = MeshIdentity.GetOrAdd(entity);
            id.MeshEntityId = loan.meshId;
            id.IsGhost = false;
            id.OwnerZoneId = OwnZoneId;
            id.LastHandoffTime = Time.unscaledTime;
            if (entity is HarvestableEntity harvestable)
                TrackOwnedStatic(harvestable);
            else if (entity is ItemDropEntity drop)
                TrackOwnedDrop(drop);
            if (!string.IsNullOrEmpty(loan.data.home) && !string.Equals(loan.data.home, OwnZoneId, System.StringComparison.OrdinalIgnoreCase))
                _borrowedStatics[loan.meshId] = loan.data.home;
            LogStaticHandoff(loan.meshId, OwnZoneId, loan.toZone, "released");
        }

        internal void OnStaticHandoffReply(long connectionId, StaticHandoffReplyMessage msg, bool ok)
        {
            if (!IsServer)
                return;
            string fromZone = _neighbourZoneByConnection.TryGetValue(connectionId, out string z) ? z : "?";
            float now = Time.unscaledTime;
            if (ok)
            {
                if (!_staticLoans.TrySettle(msg.meshId, fromZone, out MeshStaticLoanBook<StaticLoan>.Loan loan))
                    return;
                if (_ghostStatics.TryGetValue(msg.meshId, out GhostStatic puppet))
                    puppet.lastSeen = now; // a normal puppet from here on: the new owner's snapshots keep it, or it expires
                if (loan.data.HasArea && string.Equals(loan.data.home, OwnZoneId, System.StringComparison.OrdinalIgnoreCase))
                    _lentStatics[msg.meshId] = new LentStatic { ownerZone = fromZone, ownerSince = now, spawn = loan.data };
                return;
            }
            if (!_staticLoans.TryFail(msg.meshId, fromZone, now, staticHandoffRetrySeconds, out MeshStaticLoanBook<StaticLoan>.Loan failed))
                return;
            Logging.LogWarning("Mesh", $"Static handoff {msg.meshId} refused by zone {fromZone}: {msg.reason}");
            RepromoteStatic(failed);
        }

        internal void OnStaticHandoff(long connectionId, StaticHandoffMessage msg)
        {
            if (!IsServer)
                return;
            Vector3 offset = OffsetFrom(connectionId);
            msg.position += offset;
            msg.spawnPosition += offset;
            string fromZone = _neighbourZoneByConnection.TryGetValue(connectionId, out string z) ? z : "?";
            _linkClients.TryGetValue(fromZone, out MeshLinkClient reply);
            GhostKind kind = (GhostKind)msg.kind;
            bool owned = _ownedStatics.TryGet(msg.meshId, out _) || _ownedDrops.TryGet(msg.meshId, out _);
            bool puppetHere = _ghostStatics.TryGetValue(msg.meshId, out GhostStatic puppet) && puppet.kind == kind
                && puppet.entity != null && puppet.entity.Identity != null && !puppet.entity.Identity.IsDestroyed;
            bool kindKnown = kind == GhostKind.Harvestable || kind == GhostKind.ItemDrop;
            MeshStaticReceipt receipt = MeshStaticHandoffRules.Receive(owned, puppetHere && kindKnown, kindKnown && HandedStaticPrefab(kind, msg.entityId) != null);
            BaseGameEntity entity = null;
            switch (receipt)
            {
                case MeshStaticReceipt.AckDuplicate:
                    reply?.SendStaticHandoffReply(msg.meshId, true, null);
                    return;
                case MeshStaticReceipt.Promote:
                    entity = PromoteStaticPuppet(msg, puppet);
                    break;
                case MeshStaticReceipt.Spawn:
                    entity = SpawnHandedStatic(msg, kind);
                    break;
            }
            if (entity == null)
            {
                reply?.SendStaticHandoffReply(msg.meshId, false, receipt == MeshStaticReceipt.Fail ? $"no prefab for {kind} entity id {msg.entityId}" : $"could not take {kind} entity id {msg.entityId}");
                LogStaticHandoff(msg.meshId, fromZone, OwnZoneId, "failed");
                return;
            }
            MeshIdentity id = MeshIdentity.GetOrAdd(entity);
            id.MeshEntityId = msg.meshId;
            id.IsGhost = false;
            id.OwnerZoneId = OwnZoneId;
            id.HomeZoneId = msg.homeZoneId;
            id.LastHandoffTime = Time.unscaledTime;
            AdoptStaticHome(entity, msg.meshId, msg.homeZoneId, fromZone);
            if (entity is HarvestableEntity harvestable)
                TrackOwnedStatic(harvestable);
            else if (entity is ItemDropEntity drop)
                TrackOwnedDrop(drop);
            reply?.SendStaticHandoffReply(msg.meshId, true, null);
            LogStaticHandoff(msg.meshId, fromZone, OwnZoneId, receipt == MeshStaticReceipt.Promote ? "promoted" : "spawned");
        }

        private BaseGameEntity HandedStaticPrefab(GhostKind kind, int entityId)
        {
            if (kind == GhostKind.Harvestable)
            {
                if ((!GameInstance.HarvestableEntities.TryGetValue(entityId, out HarvestableEntity prefab) || prefab == null) && RegisterHarvestablePrefabs())
                    GameInstance.HarvestableEntities.TryGetValue(entityId, out prefab);
                return prefab;
            }
            if (!GameInstance.ItemDropEntities.TryGetValue(entityId, out ItemDropEntity dropPrefab) || dropPrefab == null)
                dropPrefab = GameInstance.Singleton.ItemDropEntityPrefab;
            return dropPrefab;
        }

        /// <summary>The puppet that stands here becomes the real entity in place, with the sender's state.</summary>
        private BaseGameEntity PromoteStaticPuppet(StaticHandoffMessage msg, GhostStatic puppet)
        {
            BaseGameEntity entity = puppet.entity;
            _ghostStatics.Remove(msg.meshId);
            if (puppet.hitHook != null && entity is DamageableEntity hooked)
                hooked.onReceiveDamage -= puppet.hitHook;
            GhostSuppression.Unghostify(entity, this, null);
            if (entity is DamageableEntity damageable)
                damageable.IsInvincible = false;
            ApplyHandedState(entity, msg);
            return entity;
        }

        private BaseGameEntity SpawnHandedStatic(StaticHandoffMessage msg, GhostKind kind)
        {
            Quaternion rotation = Quaternion.Euler(0f, msg.yaw, 0f);
            BaseGameEntity entity = null;
            _spawningPuppet = true; // registered under the sender's mesh id below, not a new birth id
            try
            {
                if (kind == GhostKind.Harvestable)
                {
                    HarvestableEntity prefab = HandedStaticPrefab(kind, msg.entityId) as HarvestableEntity;
                    Assets.RegisterPrefab(prefab.Identity);
                    LiteNetLibManager.LiteNetLibIdentity identity = Assets.NetworkSpawn(prefab.Identity.HashAssetId, msg.position, rotation);
                    entity = identity != null ? identity.GetComponent<HarvestableEntity>() : null;
                    if (entity == null && identity != null)
                        identity.NetworkDestroy();
                }
                else if (msg.items != null && msg.items.Length > 0)
                {
                    ItemDropEntity prefab = HandedStaticPrefab(kind, msg.entityId) as ItemDropEntity;
                    entity = ItemDropEntity.Drop(prefab, msg.position, rotation, (RewardGivenType)msg.givenType, msg.items[0], msg.looters ?? System.Array.Empty<string>(), msg.appearSeconds > 0f ? msg.appearSeconds : -1f);
                }
            }
            finally
            {
                _spawningPuppet = false;
            }
            if (entity != null)
                ApplyHandedState(entity, msg);
            return entity;
        }

        /// <summary>The state that matters: a harvestable's hp and respawn timing, a drop's contents, looters and appear time.</summary>
        private void ApplyHandedState(BaseGameEntity entity, StaticHandoffMessage msg)
        {
            if (entity is HarvestableEntity harvestable)
            {
                HarvestableEntity prefab = HandedStaticPrefab(GhostKind.Harvestable, msg.entityId) as HarvestableEntity;
                harvestable.SetSpawnArea(null, prefab, msg.level, msg.spawnPosition); // the home's spawn area respawns it (LentStatic)
                harvestable.DestroyRespawnDelay = msg.respawnDelay;
                harvestable.CurrentHp = Mathf.Clamp(msg.currentHp, 1, Mathf.Max(1, harvestable.MaxHp));
            }
            else if (entity is ItemDropEntity drop)
            {
                ItemDropEntity prefab = HandedStaticPrefab(GhostKind.ItemDrop, msg.entityId) as ItemDropEntity;
                drop.SetSpawnArea(null, prefab, msg.level, msg.spawnPosition);
                drop.DestroyRespawnDelay = msg.respawnDelay;
                if (msg.items != null && msg.items.Length > 0)
                {
                    drop.DropItems.Clear();
                    drop.DropItems.AddRange(msg.items);
                }
                drop.Looters.Clear();
                if (msg.looters != null)
                {
                    for (int i = 0; i < msg.looters.Length; ++i)
                        drop.Looters.Add(msg.looters[i]);
                }
                drop.Identity.CancelInvoke(nameof(LiteNetLibManager.LiteNetLibIdentity.NetworkDestroy)); // a puppet's long expiry, or Drop's
                if (msg.appearSeconds > 0f)
                    drop.NetworkDestroy(msg.appearSeconds);
            }
        }

        /// <summary>Who respawns an adopted static: this cell when it came home, else its home, which learns who holds it.</summary>
        private void AdoptStaticHome(BaseGameEntity entity, string meshId, string home, string fromZone)
        {
            if (string.IsNullOrEmpty(home))
                return;
            if (string.Equals(home, OwnZoneId, System.StringComparison.OrdinalIgnoreCase))
            {
                if (_lentStatics.TryGetValue(meshId, out LentStatic lent))
                {
                    _lentStatics.Remove(meshId);
                    if (entity is HarvestableEntity harvestable && lent.spawn.harvestArea != null)
                        harvestable.SetSpawnArea(lent.spawn.harvestArea, lent.spawn.harvestPrefab, lent.spawn.level, harvestable.SpawnPosition);
                    else if (entity is ItemDropEntity drop && lent.spawn.dropArea != null)
                        drop.SetSpawnArea(lent.spawn.dropArea, lent.spawn.dropPrefab, lent.spawn.level, drop.SpawnPosition);
                }
                return;
            }
            _borrowedStatics[meshId] = home;
            if (!string.Equals(home, fromZone, System.StringComparison.OrdinalIgnoreCase) && !DeferWhileUnlinked(home, l => l.SendLentStatic(meshId, OwnZoneId, false), $"lent static {meshId}") && _linkClients.TryGetValue(home, out MeshLinkClient homeLink) && homeLink.IsConnected) // Slice 7
                homeLink.SendLentStatic(meshId, OwnZoneId, false);
        }

        /// <summary>OnOwnedUntracked's part for statics: a borrowed one that is gone here (depleted, picked up, expired) is respawned by its home.</summary>
        private void OnStaticUntracked(Component entity)
        {
            if (_borrowedStatics.Count == 0 || entity is BaseCharacterEntity)
                return;
            MeshIdentity id = MeshIdentity.Get(entity);
            string meshId = id != null ? id.MeshEntityId : null;
            if (meshId == null || !_borrowedStatics.TryGetValue(meshId, out string home))
                return;
            _borrowedStatics.Remove(meshId);
            if (DeferWhileUnlinked(home, l => l.SendLentStatic(meshId, OwnZoneId, true), $"static gone {meshId}"))
                return; // Slice 7: kept for the link
            if (_linkClients.TryGetValue(home, out MeshLinkClient link) && link.IsConnected)
                link.SendLentStatic(meshId, OwnZoneId, true);
            else
                Logging.LogWarning("Mesh", $"Borrowed static {meshId} is gone here but home zone {home} is not linked: its respawn is lost");
        }

        internal void OnLentStatic(long connectionId, LentStaticMessage msg)
        {
            if (!IsServer || !_lentStatics.TryGetValue(msg.meshId, out LentStatic lent))
                return;
            string fromZone = _neighbourZoneByConnection.TryGetValue(connectionId, out string z) ? z : "?";
            if (!msg.gone)
            {
                lent.ownerZone = msg.ownerZone;
                lent.ownerSince = Time.unscaledTime;
                return;
            }
            _lentStatics.Remove(msg.meshId);
            DestroyGhostStatic(msg.meshId, "gone on borrower");
            RespawnLentStatic(lent.spawn);
            Logging.Log("Mesh", $"Lent static {msg.meshId} gone on zone {fromZone}; home spawn area respawns it in {lent.spawn.respawnDelay:0.#}s");
        }

        private static void RespawnLentStatic(StaticLoan spawn)
        {
            if (spawn.harvestArea != null && spawn.harvestPrefab != null)
                spawn.harvestArea.Spawn(spawn.harvestPrefab, spawn.level, spawn.respawnDelay, spawn.respawnDelay);
            else if (spawn.dropArea != null && spawn.dropPrefab != null)
                spawn.dropArea.Spawn(spawn.dropPrefab, spawn.level, spawn.respawnDelay, spawn.respawnDelay);
        }

        /// <summary>
        /// A neighbour restarted: statics lent to its old process are respawned at home, statics borrowed from it (not born
        /// in its new incarnation) go because its spawn areas respawn everything, and loans sent to it before our link to
        /// the new process came up are taken back.
        /// </summary>
        private void ReleaseStaticRecords(string zoneId, string reason, string keepIncarnation, float linkUpAt)
        {
            if (!IsDynamicMap)
                return;
            _tmpIds.Clear();
            foreach (KeyValuePair<string, LentStatic> kv in _lentStatics)
            {
                if (string.Equals(kv.Value.ownerZone, zoneId, System.StringComparison.OrdinalIgnoreCase) && kv.Value.ownerSince < linkUpAt)
                    _tmpIds.Add(kv.Key);
            }
            for (int i = 0; i < _tmpIds.Count; ++i)
            {
                LentStatic lent = _lentStatics[_tmpIds[i]];
                _lentStatics.Remove(_tmpIds[i]);
                DestroyGhostStatic(_tmpIds[i], reason);
                RespawnLentStatic(lent.spawn);
                Logging.LogWarning("Mesh", $"Lent static {_tmpIds[i]} released ({reason}); home spawn area respawns it in {lent.spawn.respawnDelay:0.#}s");
            }
            _tmpIds.Clear();
            foreach (KeyValuePair<string, string> kv in _borrowedStatics)
            {
                if (string.Equals(kv.Value, zoneId, System.StringComparison.OrdinalIgnoreCase) && !string.Equals(MeshIdentity.BirthIncarnation(kv.Key), keepIncarnation))
                    _tmpIds.Add(kv.Key);
            }
            for (int i = 0; i < _tmpIds.Count; ++i)
            {
                string meshId = _tmpIds[i];
                _borrowedStatics.Remove(meshId);
                BaseGameEntity entity = null;
                if (_ownedStatics.TryGet(meshId, out DamageableEntity stat))
                    entity = stat;
                else if (_ownedDrops.TryGet(meshId, out ItemDropEntity drop))
                    entity = drop;
                if (entity == null)
                    continue;
                UntrackOwned(entity);
                if (entity.Identity != null && !entity.Identity.IsDestroyed)
                    entity.NetworkDestroy();
                Logging.LogWarning("Mesh", $"Borrowed static {meshId} released ({reason})");
            }
            _tmpIds.Clear();
            _loanScratch.Clear();
            _staticLoans.TakeToward(zoneId, linkUpAt, _loanScratch);
            for (int i = 0; i < _loanScratch.Count; ++i)
                RepromoteStatic(_loanScratch[i]);
            _loanScratch.Clear();
        }
#else
        internal void OnStaticHandoff(long connectionId, StaticHandoffMessage msg) { }
        internal void OnStaticHandoffReply(long connectionId, StaticHandoffReplyMessage msg, bool ok) { }
        internal void OnLentStatic(long connectionId, LentStaticMessage msg) { }
#endif
    }
}
