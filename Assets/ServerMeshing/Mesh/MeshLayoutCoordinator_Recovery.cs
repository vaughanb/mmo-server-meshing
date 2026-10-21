// Server Meshing add-on — the coordinator's recovery at a central (re)start (DESIGN.md 0.72, plan D3; Slice 5,
// slices/slice5_design.md decisions 1-5, 8). Each Dynamic map waits for every registered map server and every dynamic id
// the stored and held layouts name, then adopts the newest consistent layout among the store and the cells (or the asset
// cut when nothing dynamic exists anywhere), re-adds the dynamic cells' channels and answers every pending hello with it.
// At the ceiling a missing dynamic id freezes its family (no operation, supervision or policy on it; its cells are still
// answered) until it says hello. A later hello holding a newer layout from another coordinator re-opens the map while
// this central has broadcast nothing of its own. The pure decisions are MeshRecoveryResolver's; the stored operation's
// fate is MeshLayoutCoordinator_Resume's.
using System;
using System.Collections.Generic;

namespace MultiplayerARPG.MMO.Mesh
{
    public sealed partial class MeshLayoutCoordinator
    {
        private sealed class FrozenFamily
        {
            public string reason = "";
            public readonly HashSet<string> missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class MapRecovery
        {
            public MeshRecoveryOutcome outcome = MeshRecoveryOutcome.Waiting;
            public string reason = "";
            public readonly Dictionary<string, (long connection, string incarnation)> pending = new Dictionary<string, (long, string)>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, FrozenFamily> frozenFamilies = new Dictionary<string, FrozenFamily>(StringComparer.OrdinalIgnoreCase);
        }

        private readonly List<(long connection, MeshLayoutHelloMessage hello)> _pendingStatic = new List<(long, MeshLayoutHelloMessage)>();
        private MeshLayoutPair _adoptedPair;
        private bool _ownBroadcastSent;
        private bool _policyFreshDone;

        /// <summary>
        /// The pair recovery adopted: the newest the store or a cell held, an empty layout's included (then the asset cut
        /// runs but an older held cut re-opens nothing); None when nothing was stored or held.
        /// </summary>
        public MeshLayoutPair AdoptedPair => _adoptedPair;
        /// <summary>Whether a runtime layout (entries, or a stored operation) was adopted rather than the asset cut.</summary>
        public bool AdoptedRuntimeLayout { get; private set; }
        /// <summary>Whether this central has broadcast a layout of its own (a step, an abort's version, a settle).</summary>
        public bool OwnBroadcastSent => _ownBroadcastSent;

        /// <summary>IsFamilyFrozen: a dynamic id of the family never said hello by the recovery ceiling (decision 3).</summary>
        public bool IsFamilyFrozen(string baseId) => FrozenFamilyReason(baseId) != null;

        /// <summary>FrozenFamilyReason is why the family is frozen, or null when it is not.</summary>
        public string FrozenFamilyReason(string baseId)
        {
            if (string.IsNullOrEmpty(baseId))
                return null;
            foreach (MapRecovery map in _maps.Values)
            {
                if (map.frozenFamilies.TryGetValue(baseId, out FrozenFamily family))
                    return family.reason;
            }
            return null;
        }

        /// <summary>RecoveryStart: the broadcast starts as the stored one (Slice 5), the maps wait, and the first resolution runs.</summary>
        private void RecoveryStart(MeshCoordinatorStoreData data, string storeState)
        {
            _pendingStatic.Clear();
            _adoptedPair = MeshLayoutPair.None;
            AdoptedRuntimeLayout = false;
            _ownBroadcastSent = false;
            _policyFreshDone = false;
            _storedOpResolved = false;
            bool storedLayout = _storeTrusted && data.broadcast != null && !data.broadcast.pair.IsNone
                && (data.broadcast.entries.Count > 0 || data.hasStaged || data.hasOperation);
            _broadcast = storedLayout ? data.broadcast.Clone() : new MeshLayoutSnapshot { pair = new MeshLayoutPair(Incarnation, 1) };
            Phase = MeshCoordinatorPhase.Recovering;
            foreach (MeshMapLayout layout in _world.maps)
            {
                if (layout == null || string.IsNullOrEmpty(layout.mapId) || _maps.ContainsKey(layout.mapId))
                    continue;
                _maps[layout.mapId] = new MapRecovery();
                int stored = MeshRecoveryResolver.EntriesOf(data.broadcast, layout.mapId) + (data.hasStaged ? MeshRecoveryResolver.EntriesOf(data.staged, layout.mapId) : 0);
                if (!layout.IsDynamic && stored > 0)
                    _log.Warning($"Coordinator: map {layout.mapId} is Static now; {stored} stored dynamic entries ignored");
            }
            ResolveAll();
            List<string> summary = new List<string>();
            foreach (KeyValuePair<string, MapRecovery> map in _maps)
            {
                if (_world.IsDynamicMap(map.Key))
                    summary.Add($"{map.Key} {map.Value.outcome}");
            }
            _log.Info($"Coordinator {Incarnation:x}: Dynamic maps {(summary.Count > 0 ? string.Join(", ", summary) : "none")}; store {_store.Path} ({storeState}); broadcast {_broadcast.pair}");
        }

        private void RecoveryStop()
        {
            _pendingStatic.Clear();
            _retireQueued.Clear();
            _adoptedPair = MeshLayoutPair.None;
            AdoptedRuntimeLayout = false;
            _ownBroadcastSent = false;
            _policyFreshDone = false;
            _storedOpResolved = false;
        }

        private void PendStaticHello(long connectionId, MeshLayoutHelloMessage hello)
        {
            _pendingStatic.RemoveAll(p => p.connection == connectionId);
            _pendingStatic.Add((connectionId, hello));
        }

        private HashSet<string> DynamicMapIds()
        {
            HashSet<string> maps = new HashSet<string>(StringComparer.Ordinal);
            foreach (MeshMapLayout layout in _world.maps)
            {
                if (layout != null && layout.IsDynamic && !string.IsNullOrEmpty(layout.mapId))
                    maps.Add(layout.mapId);
            }
            return maps;
        }

        /// <summary>HeldEntriesOf counts a hello's held entries on any Dynamic map (a Static neighbour carries them too).</summary>
        private int HeldEntriesOf(MeshLayoutHelloMessage hello) => MeshRecoveryResolver.DynamicEntries(hello.held, DynamicMapIds());

        private static MeshLayoutPair HeldPair(MeshLayoutHelloMessage hello) => hello.held != null ? hello.held.pair : MeshLayoutPair.None;

        /// <summary>HeldOwnsSomething (decision 5): the hello's own entry is live and owns something (Active with a rectangle, or Draining).</summary>
        private static bool HeldOwnsSomething(MeshLayoutHelloMessage hello)
        {
            if (hello.held == null || hello.held.entries == null)
                return false;
            foreach (MeshLayoutEntry entry in hello.held.entries)
            {
                if (entry == null || !string.Equals(entry.id, hello.cellId, StringComparison.OrdinalIgnoreCase))
                    continue;
                return entry.state == MeshCellState.Draining || (entry.state == MeshCellState.Active && !entry.owned.IsEmpty);
            }
            return false;
        }

        /// <summary>
        /// RecoveryNames: while recovering, a dynamic id named by any candidate layout or by the stored operation is never
        /// answered with Retire (decision 5); its hello is what the map waits for.
        /// </summary>
        private bool RecoveryNames(string id)
        {
            if (Phase != MeshCoordinatorPhase.Recovering || string.IsNullOrEmpty(id))
                return false;
            if (_storeData != null && _storeData.hasOperation && !_storedOpResolved && SameId(_storeData.operation.DynamicId, id))
                return true;
            Dictionary<string, string> named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string mapId in DynamicMapIds())
            {
                foreach ((string _, MeshLayoutSnapshot snapshot) in Candidates(mapId))
                    MeshRecoveryResolver.LiveDynamicIds(snapshot, mapId, named);
                if (_storeTrusted && _storeData != null && _storeData.hasStaged)
                    MeshRecoveryResolver.LiveDynamicIds(_storeData.staged, mapId, named);
            }
            return named.ContainsKey(id);
        }

        /// <summary>The world's candidate layouts as the resolver builds them from this coordinator's store and bindings.</summary>
        private List<(string source, MeshLayoutSnapshot snapshot)> Candidates(string mapId)
        {
            MeshRecoveryInput input = new MeshRecoveryInput { mapId = mapId, mapDynamic = true };
            FillCandidates(input);
            return MeshRecoveryResolver.Candidates(input);
        }

        private void FillCandidates(MeshRecoveryInput input)
        {
            input.storeTrusted = _storeTrusted;
            if (_storeTrusted && _storeData != null && !_storeData.broadcast.pair.IsNone)
                input.storedBroadcast = _storeData.broadcast;
            if (_storeTrusted && _storeData != null && _storeData.hasStaged)
                input.storedStaged = _storeData.staged;
            input.storedOperation = _storeTrusted && _storeData != null && _storeData.hasOperation && !_storedOpResolved;
            input.dynamicMaps.UnionWith(DynamicMapIds());
            if (input.storedOperation)
            {
                string excluded = _storeData.operation.ExcludedId;
                if (!string.IsNullOrEmpty(excluded))
                    input.excludedIds.Add(excluded);
                string abortable = _storeData.operation.AbortableId;
                if (!string.IsNullOrEmpty(abortable))
                    input.abortableIds.Add(abortable);
            }
            foreach (MeshCellBinding binding in _bindings.Values)
            {
                if (binding.unregisteredAt < 0f && binding.held != null)
                    input.held.Add(new MeshHeldLayout { cellId = binding.cellId, snapshot = binding.held });
            }
            foreach ((long _, MeshLayoutHelloMessage hello) in _pendingStatic)
            {
                if (hello.held != null)
                    input.held.Add(new MeshHeldLayout { cellId = hello.cellId, snapshot = hello.held });
            }
        }

        /// <summary>RecoveryOnHello: a late hello may re-open a recovered map (decision 4) or unfreeze a family (decision 3).</summary>
        private void RecoveryOnHello(MeshLayoutHelloMessage hello, MapRecovery map)
        {
            if (!map.outcome.Answers())
                return;
            int heldEntries = HeldEntriesOf(hello);
            MeshLayoutPair held = HeldPair(hello);
            if (MeshRecoveryResolver.HeldReopensRecovery(heldEntries, held, Incarnation, _adoptedPair, _ownBroadcastSent))
            {
                map.outcome = MeshRecoveryOutcome.Waiting;
                map.reason = "";
                Phase = MeshCoordinatorPhase.Recovering;
                _log.Warning(!AdoptedRuntimeLayout
                    ? $"Cell {hello.cellId} holds layout {held} of map {hello.mapId} from an earlier coordinator: map {hello.mapId} re-opens recovery"
                    : $"Cell {hello.cellId} holds layout {held} of map {hello.mapId} newer than the adopted {_adoptedPair}: map {hello.mapId} re-opens recovery");
                return;
            }
            if (heldEntries > 0 && held.coordinatorIncarnation != Incarnation && _ownBroadcastSent
                && (_adoptedPair.IsNone || held.IsNewerThan(_adoptedPair)))
                _log.Error($"Cell {hello.cellId} holds layout {held} newer than the adopted {(AdoptedRuntimeLayout ? _adoptedPair.ToString() : "asset cut")} after the central moved on; answered with the current layout");
            Unfreeze(hello.mapId, map, hello.cellId);
        }

        private void Unfreeze(string mapId, MapRecovery map, string cellId)
        {
            foreach (KeyValuePair<string, FrozenFamily> family in new List<KeyValuePair<string, FrozenFamily>>(map.frozenFamilies))
            {
                if (!family.Value.missing.Remove(cellId))
                    continue;
                if (family.Value.missing.Count > 0)
                {
                    family.Value.reason = $"no hello from {string.Join(", ", family.Value.missing)} after {Settings.recoveryCeilingSeconds:0} s";
                    continue;
                }
                map.frozenFamilies.Remove(family.Key);
                _log.Info($"Coordinator recovery: family {family.Key} of map {mapId} unfrozen: {cellId} said hello");
                if (Phase == MeshCoordinatorPhase.Running)
                    ResumeStoredOperation();
            }
        }

        private void ResolveAll()
        {
            if (Phase != MeshCoordinatorPhase.Recovering)
                return;
            bool allSettled = true;
            foreach (KeyValuePair<string, MapRecovery> pair in _maps)
            {
                MapRecovery map = pair.Value;
                if (map.outcome != MeshRecoveryOutcome.Waiting)
                    continue;
                MeshRecoveryInput input = new MeshRecoveryInput
                {
                    mapId = pair.Key,
                    mapDynamic = _world.IsDynamicMap(pair.Key),
                    elapsedSeconds = _clock.Now - _startedAt,
                    ceilingSeconds = Settings.recoveryCeilingSeconds,
                };
                FillCandidates(input);
                _peers.Clear();
                _transport.GetMapServers(_peers);
                foreach (MeshMapServerPeer peer in _peers)
                {
                    if (peer.mapId == pair.Key && !string.IsNullOrEmpty(peer.cellId))
                        input.registeredCells.Add(peer.cellId);
                }
                foreach (MeshCellBinding binding in _bindings.Values)
                {
                    if (binding.mapId == pair.Key && binding.unregisteredAt < 0f && !binding.staticNeighbour)
                        input.helloCells.Add(binding.cellId);
                }
                MeshRecoveryDecision decision = MeshRecoveryResolver.Resolve(input);
                if (decision.outcome == MeshRecoveryOutcome.Waiting)
                {
                    allSettled = false;
                    continue;
                }
                map.outcome = decision.outcome;
                map.reason = decision.reason;
                if (decision.outcome.Answers())
                    Adopt(pair.Key, map, decision);
                else
                    Freeze(pair.Key, map, decision);
            }
            if (allSettled && Phase == MeshCoordinatorPhase.Recovering)
            {
                Phase = MeshCoordinatorPhase.Running;
                OnRunning();
            }
        }

        /// <summary>Adopt puts the decision into effect: the asset cut as in 2a, or the chosen runtime layout (Slice 5), then the pending hellos are answered.</summary>
        private void Adopt(string mapId, MapRecovery map, MeshRecoveryDecision decision)
        {
            if (decision.isError)
                _log.Error("Coordinator recovery: " + decision.reason);
            else if (_world.IsDynamicMap(mapId))
                _log.Info("Coordinator recovery: " + decision.reason);
            if (decision.outcome == MeshRecoveryOutcome.AdoptStored)
            {
                MeshLayoutSnapshot chosen = decision.chosen;
                bool later = !_adoptedPair.IsNone && chosen.pair.IsNewerThan(_adoptedPair);
                if (_world.AppliedPair != chosen.pair && !_world.ApplyLayout(chosen, out string reason))
                {
                    map.outcome = MeshRecoveryOutcome.Frozen;
                    map.reason = $"the layout {chosen.pair} does not fit the asset: {reason}";
                    _log.Error($"Coordinator recovery: map {mapId} frozen: {map.reason}");
                    map.pending.Clear();
                    return;
                }
                _broadcast = chosen.Clone();
                _adoptedPair = chosen.pair;
                AdoptedRuntimeLayout = true;
                AddChannels(chosen);
                if (later)
                    SendRecoveredLayout();
                foreach (MeshFrozenFamily frozen in decision.frozenFamilies)
                {
                    FrozenFamily family = new FrozenFamily { reason = frozen.reason };
                    family.missing.UnionWith(frozen.missingIds);
                    map.frozenFamilies[frozen.baseId] = family;
                    _log.Error($"Coordinator recovery: family {frozen.baseId} of map {mapId} frozen: {frozen.reason}");
                }
            }
            else if (decision.chosen != null && decision.chosen.pair.IsNewerThan(_adoptedPair))
            {
                // The asset cut runs (the registry applies nothing: an empty layout applied now would outrank a cut a cell
                // holds and still has to tell us about), but the newest pair seen is remembered so an older held cut re-opens nothing.
                _adoptedPair = decision.chosen.pair;
            }
            foreach (KeyValuePair<string, (long connection, string incarnation)> pending in map.pending)
            {
                MeshCellBinding binding = BindingOf(pending.Key);
                if (binding == null || binding.connectionId != pending.Value.connection || binding.incarnation != pending.Value.incarnation)
                    continue;
                Answer(pending.Value.connection, pending.Key, pending.Value.incarnation);
                binding.answered = true;
            }
            map.pending.Clear();
        }

        /// <summary>AddChannels gives every live dynamic cell of the adopted layout its channel back (lost with the old central).</summary>
        private void AddChannels(MeshLayoutSnapshot snapshot)
        {
            if (SplitHost == null)
                return;
            foreach (MeshLayoutEntry entry in snapshot.entries)
            {
                if (entry == null || string.IsNullOrEmpty(entry.id) || SameId(entry.id, entry.baseId) || entry.state == MeshCellState.Retired)
                    continue;
                SplitHost.AddChannel(entry.id, out _);
            }
        }

        /// <summary>SendRecoveredLayout re-sends an adopted layout newer than the one already answered (a later map's candidate won): not a layout of this central's own.</summary>
        private void SendRecoveredLayout()
        {
            _peers.Clear();
            _transport.GetMapServers(_peers);
            foreach (MeshMapServerPeer peer in _peers)
                _transport.Send(peer.connectionId, new MeshLayoutMessage { kind = MeshLayoutKind.Broadcast, targetCellId = string.Empty, targetIncarnation = string.Empty, stepId = "recovery", snapshot = _broadcast.Clone() });
        }

        private void Freeze(string mapId, MapRecovery map, MeshRecoveryDecision decision)
        {
            _log.Error("Coordinator recovery: frozen: " + decision.reason);
            foreach (MeshCellBinding binding in _bindings.Values)
            {
                if (binding.mapId == mapId && binding.unregisteredAt < 0f)
                    Note(binding.connectionId, MeshNoteLevel.Error, binding.cellId, "recovery-frozen", $"map {mapId} is frozen: {decision.reason}");
            }
            map.pending.Clear();
        }

        /// <summary>OnRunning: every map recovered; the Static maps' hellos are answered, the policy's cooldowns start fresh, the stored operation is resolved.</summary>
        private void OnRunning()
        {
            List<(long connection, MeshLayoutHelloMessage hello)> statics = new List<(long, MeshLayoutHelloMessage)>(_pendingStatic);
            _pendingStatic.Clear();
            foreach ((long connection, MeshLayoutHelloMessage hello) in statics)
            {
                if (_transport.TryGetMapServer(connection, out MeshMapServerPeer peer) && SameId(peer.cellId, hello.cellId))
                    AnswerStaticHello(connection, hello);
            }
            PolicyFreshCooldowns();
            ResumeStoredOperation();
        }

        /// <summary>PolicyFreshCooldowns (decision 8): every cut family starts its cooldowns at the adoption; the multipliers and records died with the old central.</summary>
        private void PolicyFreshCooldowns()
        {
            if (_policyFreshDone || !AdoptedRuntimeLayout)
                return;
            _policyFreshDone = true;
            List<string> bases = new List<string>();
            IReadOnlyList<MeshCell> cells = _world.Cells;
            for (int i = 0; i < cells.Count; ++i)
            {
                MeshCell cell = cells[i];
                if (cell.OnDynamicMap && !cell.isDynamic && _world.FamilyOf(cell.id).Count > 1 && !bases.Contains(cell.id))
                    bases.Add(cell.id);
            }
            if (_storeData != null && _storeData.hasOperation && !string.IsNullOrEmpty(_storeData.operation.baseId) && !bases.Contains(_storeData.operation.baseId))
                bases.Add(_storeData.operation.baseId);
            if (bases.Count == 0)
                return;
            float now = _clock.Now;
            List<MeshPolicyDecision> decisions = new List<MeshPolicyDecision>();
            foreach (string baseId in bases)
            {
                _cooldownUntil[baseId] = now + Math.Max(0f, Settings.splitCooldownSeconds);
                _policy?.OpEnded("restart", baseId, false, true, "restart", now, decisions);
            }
            _log.Info($"Coordinator policy: cooldowns start fresh for {string.Join(", ", bases)} after the restart; flap multipliers, measurements and restart counts forgotten");
        }
    }
}
