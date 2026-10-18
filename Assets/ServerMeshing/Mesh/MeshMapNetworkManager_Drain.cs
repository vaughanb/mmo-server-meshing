// Server Meshing add-on — a cell merged away (Slice 2c, slices/slice2c_design.md decisions 9, 11, 12 and 13). At the
// merge's e2 the retiring cell is Draining: it owns nothing, so every entity it still has leaves through 2b's paths
// toward the survivor (OwnerOf). This file adds what only a Draining cell does: what it still holds, anywhere
// (MeshDrainState, sampled once a second for the evidence and its own exit); a game connection idle 10 s is closed;
// monsters a monster summoned are despawned (no handoff takes them); an enter-game is taken only for a character the cell
// is handing on or taking in; and the retire of a running cell: on Retire for its id and incarnation it quits once
// drained (never kicking anyone), else it keeps handing off and checks again each second. The 2b retire of a cell that
// owns nothing yet (Pending, or no layout) lives here too and is unchanged. Static maps and Active cells run none of it.
using System.Collections.Generic;
using LiteNetLib.Utils;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class MeshMapNetworkManager
    {
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        /// <summary>StragglingLogSeconds: how often a retire that waits past retireMaxSeconds says what is left.</summary>
        private const float StragglingLogSeconds = 30f;

        private bool _retiring;
        private bool _mergeRetirePending;
        private float _mergeRetireAt;
        private float _mergeRetireNextStraggle;
        private MeshDrainState _drainState;
        private MeshDrainState _drainStateWindow;
        private bool _drainStateWindowHasSample;
        private float _nextDrainStateSample;
        private readonly Dictionary<long, float> _drainIdleSince = new Dictionary<long, float>();
        private readonly List<long> _drainIdleClosing = new List<long>();
        private readonly List<BaseMonsterCharacterEntity> _drainSummoned = new List<BaseMonsterCharacterEntity>();

        /// <summary>IsDraining: this cell of a Dynamic map is Draining in the layout it applied (a merge retires it).</summary>
        private bool IsDraining => IsDynamicMap && world != null && HasOwnZone && OwnCell.state == MeshCellState.Draining && !world.AppliedPair.IsNone;

        private float RetireMaxSeconds => world != null ? world.dynamicSettings.retireMaxSeconds : 90f;

        /// <summary>EvidenceOnStartServer's part: a new server holds nothing of a drain.</summary>
        private void DrainOnStartServer()
        {
            _mergeRetirePending = false;
            _mergeRetireAt = 0f;
            _mergeRetireNextStraggle = 0f;
            _drainState = default;
            ResetDrainStateWindow();
            _nextDrainStateSample = 0f;
            _drainIdleSince.Clear();
            _drainSummoned.Clear();
        }

        // ---- what a Draining cell still holds ----

        /// <summary>SplitEntityTick's hook, every snapshot tick: once a second on a Draining cell, the drain's own work and the retire check.</summary>
        private void DrainTick(float now)
        {
            if (!IsDraining)
            {
                if (_drainIdleSince.Count > 0)
                    _drainIdleSince.Clear();
                _mergeRetirePending = false; // a newer layout made it something else: the retire no longer applies
                return;
            }
            if (now < _nextDrainStateSample)
                return;
            _nextDrainStateSample = now + 1f;
            CloseIdleConnections(now);
            _drainState = CountDrainState(despawnMonsterSummons: true);
            _drainStateWindow = _drainStateWindowHasSample ? MeshDrainState.Max(_drainStateWindow, _drainState) : _drainState;
            _drainStateWindowHasSample = true;
            if (_mergeRetirePending)
                MergeRetireCheck(now);
        }

        /// <summary>
        /// CountDrainState counts what this cell holds anywhere (decision 9): every non-puppet player character present
        /// (one the kit keeps through its despawn delay included) and every pending spawn, the mounts they ride and the
        /// pets of players, living monsters (a monster's summons included; with <paramref name="despawnMonsterSummons"/>
        /// those are despawned after the count), item drops that still hold items, reward drops, living harvestables,
        /// buildings, the game transport's connections, and the handoff tables.
        /// </summary>
        private MeshDrainState CountDrainState(bool despawnMonsterSummons)
        {
            MeshDrainState state = default;
            _drainSummoned.Clear();
            Dictionary<uint, LiteNetLibIdentity>.Enumerator spawned = Assets.GetSpawnedObjects();
            while (spawned.MoveNext())
            {
                LiteNetLibIdentity identity = spawned.Current.Value;
                if (identity == null || identity.IsDestroyed || !identity.TryGetComponent(out BaseGameEntity entity) || MeshIdentity.IsGhostEntity(entity))
                    continue;
                switch (entity)
                {
                    case BasePlayerCharacterEntity player:
                        state.characters++;
                        if (player.PassengingVehicleEntity != null)
                            state.summons++;
                        break;
                    case BaseMonsterCharacterEntity monster:
                        if (monster.IsDead())
                            break;
                        if (monster.IsSummoned && monster.SummonerEntity is BasePlayerCharacterEntity)
                        {
                            state.summons++;
                            break;
                        }
                        state.monsters++;
                        if (monster.IsSummoned)
                            _drainSummoned.Add(monster);
                        break;
                    case ItemDropEntity drop:
                        if (drop.DropItems.Count > 0)
                            state.itemDrops++;
                        break;
                    case BaseRewardDropEntity _:
                        state.rewardDrops++;
                        break;
                    case HarvestableEntity harvestable:
                        if (harvestable.CurrentHp > 0)
                            state.harvestables++;
                        break;
                    case BuildingEntity _:
                        state.buildings++;
                        break;
                }
            }
            spawned.Dispose();
            state.characters += _pendingSpawnPlayerCharactersByUserId.Count;
            state.connections = Server != null ? GetConnectionIds().Count : 0; // the game transport only: the mesh links and the cluster client are their own transports
            state.inProgress = _handoffsInProgress.Count;
            state.pendingAcks = _pendingHandoffAcks.Count;
            state.rolledBack = _rolledBackHandoffs.Count;
            state.deferredAcks = _deferredAcks.Count;
            state.reserved = _reservedArrivals.Count;
            state.incoming = _incomingHandoffFrom.Count;
            state.staticLoans = _staticLoans.Count;
            state.serverCharacters = 0; // a server-owned character's handoff has no ack to wait for; one still here is counted in characters
            if (despawnMonsterSummons && _drainSummoned.Count > 0)
            {
                int despawned = 0;
                for (int i = 0; i < _drainSummoned.Count; ++i)
                {
                    BaseMonsterCharacterEntity summoned = _drainSummoned[i];
                    if (summoned == null || summoned.Identity == null || summoned.Identity.IsDestroyed)
                        continue;
                    UntrackOwned(summoned);
                    summoned.UnSummon();
                    despawned++;
                }
                if (despawned > 0)
                    Logging.Log("Mesh", $"Drain on {OwnZoneId}: {despawned} summoned monster(s) despawned");
            }
            _drainSummoned.Clear();
            return state;
        }

        /// <summary>A game connection with no character and no pending spawn for 10 s is closed, else a refused client that never leaves blocks the retire.</summary>
        private void CloseIdleConnections(float now)
        {
            if (Server == null)
                return;
            HashSet<long> connections = GetConnectionIds();
            _drainIdleClosing.Clear();
            foreach (long known in _drainIdleSince.Keys)
            {
                if (!connections.Contains(known))
                    _drainIdleClosing.Add(known);
            }
            for (int i = 0; i < _drainIdleClosing.Count; ++i)
                _drainIdleSince.Remove(_drainIdleClosing[i]);
            _drainIdleClosing.Clear();
            foreach (long connectionId in connections)
            {
                bool hasCharacter = ServerUserHandlers.TryGetPlayerCharacter(connectionId, out _);
                bool hasPending = ServerUserHandlers.TryGetUserId(connectionId, out string userId) && !string.IsNullOrEmpty(userId)
                    && _pendingSpawnPlayerCharactersByUserId.ContainsKey(userId);
                if (!MeshDrainRules.IsIdleConnection(hasCharacter, hasPending))
                {
                    _drainIdleSince.Remove(connectionId);
                    continue;
                }
                if (!_drainIdleSince.TryGetValue(connectionId, out float since))
                    _drainIdleSince[connectionId] = now;
                else if (MeshDrainRules.IdleExpired(since, now))
                    _drainIdleClosing.Add(connectionId);
            }
            for (int i = 0; i < _drainIdleClosing.Count; ++i)
            {
                _drainIdleSince.Remove(_drainIdleClosing[i]);
                Logging.Log("Mesh", $"Drain on {OwnZoneId}: idle connection closed");
                KickClient(_drainIdleClosing[i], UITextKeys.UI_ERROR_APP_NOT_READY);
            }
            _drainIdleClosing.Clear();
        }

        /// <summary>The evidence's drain state: the most of each count over the window (a fresh count on a change), zeros when not Draining.</summary>
        private MeshDrainState EvidenceDrainState(bool onChange)
        {
            if (!IsDraining)
                return default;
            return onChange || !_drainStateWindowHasSample ? CountDrainState(despawnMonsterSummons: false) : _drainStateWindow;
        }

        /// <summary>A new evidence window: the drain state's maxima start again.</summary>
        private void ResetDrainStateWindow()
        {
            _drainStateWindow = default;
            _drainStateWindowHasSample = false;
        }

        // ---- arrivals ----

        /// <summary>
        /// The DeserializeEnterGameData override's hook, before the kit's own (after it, a refusal would orphan the
        /// despawning entity it adopted): a Draining cell refuses an enter-game for a character it is neither handing on
        /// nor taking in. True when refused (the kit answers the client with the reason set here).
        /// </summary>
        private bool RefuseEnterGameWhileDraining(uint requestId, NetDataReader reader)
        {
            if (!IsServer || !IsDraining || !MeshDrainRules.TryPeekEnterGameIds(reader, out _, out string characterId) || string.IsNullOrEmpty(characterId))
                return false;
            bool puppet = _ghosts.TryGetValue(characterId, out MeshGhost ghost) && ghost.entity != null && ghost.kind == GhostKind.Player;
            if (MeshDrainRules.AcceptsEnterGame(true, _pendingHandoffAcks.ContainsKey(characterId), _rolledBackHandoffs.Contains(characterId), _incomingHandoffFrom.ContainsKey(characterId), puppet))
                return false;
            _enterGameRequestResponseMessages[requestId] = UITextKeys.UI_ERROR_APP_NOT_READY;
            Logging.LogWarning("Mesh", $"Enter game refused on draining cell {OwnZoneId}: {characterId}");
            return true;
        }

        // ---- retire (moved from MeshMapNetworkManager_Layout.cs; Slice 2c adds the Draining cell) ----

        private void HandleRetireMessage(MessageHandlerData messageHandler)
        {
            if (!TryReadCentralMessage(messageHandler, "Retire", out MeshRetireMessage message))
                return;
            string cellId = LayoutCellId; // a child that holds no layout yet has no own zone, only the id it was started as
            if (!string.Equals(message.cellId, cellId, System.StringComparison.OrdinalIgnoreCase)
                || !string.Equals(message.incarnation, CellIncarnation, System.StringComparison.Ordinal))
            {
                Logging.LogWarning("Mesh", $"Retire for cell {message.cellId} incarnation {message.incarnation} reached cell {cellId} incarnation {CellIncarnation}; ignored");
                return;
            }
            if (FenceOnRetire(message.reason))
                return; // Slice 2d: a fenced: reason fences this cell in any state, a quit in progress included
            if (MayRetireNow(message.cellId, message.incarnation))
            {
                RetireThisCell();
                return;
            }
            if (_retiring || _mergeRetirePending)
                return; // the central resends Retire every 10 s: one is enough
            if (IsDraining && OwnCell.isDynamic)
            {
                _mergeRetirePending = true;
                _mergeRetireAt = Time.unscaledTime;
                _mergeRetireNextStraggle = 0f;
                _drainState = CountDrainState(despawnMonsterSummons: true);
                MergeRetireCheck(Time.unscaledTime);
                return;
            }
            Logging.LogWarning("Mesh", $"Retire asked for cell {cellId} at layout {message.pair} ({message.reason}), but this version cannot retire a running cell; staying up");
        }

        /// <summary>A Draining cell asked to retire: quit once drained; until then keep handing off, and past retireMaxSeconds say what is left every 30 s. Never kicks anyone.</summary>
        private void MergeRetireCheck(float now)
        {
            if (_retiring)
                return;
            bool holdsLayout = HasOwnZone && world != null && !world.AppliedPair.IsNone;
            MeshCellState state = OwnCell != null ? OwnCell.state : MeshCellState.Pending;
            MeshRetireDecision decision = MeshRetireRule.Decide(LayoutCellId, CellIncarnation, LayoutCellId, CellIncarnation, holdsLayout, state, _drainState.IsDrained);
            if (decision == MeshRetireDecision.QuitDrained)
            {
                QuitDrainedCell();
                return;
            }
            if (decision != MeshRetireDecision.AwaitDrain)
            {
                _mergeRetirePending = false; // no longer Draining (a newer layout): the retire no longer applies
                return;
            }
            if (MeshDrainRules.StragglingDue(_mergeRetireAt, now, RetireMaxSeconds, StragglingLogSeconds, ref _mergeRetireNextStraggle))
                Logging.LogWarning("Mesh", $"Merge retire: {LayoutCellId} still holds {_drainState.Describe()}");
        }

        /// <summary>The drained cell ends itself through the kit's quit path (wantsToQuit, ProceedBeforeQuit), as Sleep does.</summary>
        private void QuitDrainedCell()
        {
            if (_retiring)
                return;
            _retiring = true;
            _mergeRetirePending = false;
            if (_drainState.buildings > 0)
                Logging.LogWarning("Mesh", $"Merge retire: {LayoutCellId} leaves {_drainState.buildings} building(s) saved under its channel");
            Logging.Log("Mesh", $"Merge retire: {LayoutCellId} drained, quitting");
            Application.Quit();
        }

        /// <summary>
        /// MayRetireNow: a dynamic cell of a Dynamic map that owns nothing yet (no layout, or Pending in the one it holds)
        /// may end itself (Slice 2b decision 5a); an asset cell, or a child that owns something, may not.
        /// </summary>
        private bool MayRetireNow(string retireCellId, string retireIncarnation)
        {
            if (!IsDynamicMap || world == null || (HasOwnZone && !OwnCell.isDynamic))
                return false;
            bool holdsLayout = HasOwnZone && !world.AppliedPair.IsNone;
            return MeshRetireRule.ShouldRetire(retireCellId, retireIncarnation, LayoutCellId, CellIncarnation, holdsLayout, OwnCell != null ? OwnCell.state : MeshCellState.Pending);
        }

        /// <summary>A layout that names this cell Retired while it may retire (an aborted split's version after its e1 broadcast).</summary>
        private bool RetiresInLayout(MeshLayoutSnapshot snapshot)
        {
            if (!IsDynamicMap)
                return false;
            string id = LayoutCellId;
            for (int i = 0; i < snapshot.entries.Count; ++i)
            {
                MeshLayoutEntry entry = snapshot.entries[i];
                if (entry != null && entry.state == MeshCellState.Retired && string.Equals(entry.id, id, System.StringComparison.OrdinalIgnoreCase))
                    return MayRetireNow(id, CellIncarnation);
            }
            return false;
        }

        /// <summary>The child ends itself through the kit's quit path: the spawner cannot stop one process.</summary>
        private void RetireThisCell()
        {
            if (_retiring)
                return;
            _retiring = true;
            Logging.Log("Mesh", $"Split retire: {LayoutCellId} retired before it owned anything");
            Application.Quit();
        }
#else
        private bool RefuseEnterGameWhileDraining(uint requestId, NetDataReader reader) { return false; }
#endif
    }
}
