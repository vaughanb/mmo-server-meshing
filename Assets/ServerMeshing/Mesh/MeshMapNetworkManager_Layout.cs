// Server Meshing add-on — a map server's side of dynamic cells (DESIGN.md 0.72, plan D5). On a Dynamic map the cell says
// hello to the central's layout coordinator each time it is registered with the cluster, and stays gated -- no player or
// monster handoff out, no building routing -- until the answer to that hello is applied. Every later layout is applied
// and acknowledged here. Static maps run none of it: they never say hello, are never gated, and only register handlers.
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
        [Header("Server Meshing — dynamic cells")]
        [Tooltip("Dynamic maps: a building placed while this cell waits for the central's layout answer is held at most this many seconds, then created here. It is never dropped: the player's item is already spent.")]
        public float layoutGatePlacementSeconds = 30f;

        private readonly MeshLayoutGate _layoutGate = new MeshLayoutGate();

        /// <summary>IsDynamicMap is whether this server's map runs dynamic cells; fixed when the server starts.</summary>
        public bool IsDynamicMap => _layoutGate.IsDynamic;

        /// <summary>IsLayoutGateOpen is false while a cell of a Dynamic map waits for the central to answer its LayoutHello.</summary>
        public bool IsLayoutGateOpen => !_layoutGate.IsGated;

        private bool IsLayoutGated => _layoutGate.IsGated;

#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        private readonly MeshHeldPlacementQueue<BuildingSaveData> _heldPlacements = new MeshHeldPlacementQueue<BuildingSaveData>();
        private readonly List<string> _streamTargetIds = new List<string>();
        private readonly List<string> _currentTargetIds = new List<string>();
        private readonly List<string> _targetsLeft = new List<string>();
        private readonly List<string> _targetsAdded = new List<string>();

        private float LayoutGateWarnSeconds => world != null ? world.dynamicSettings.layoutGateWarnSeconds : 10f;

        /// <summary>The cell this server answers for: its own cell once resolved, else the one it was started as.</summary>
        private string LayoutCellId
        {
            get
            {
                if (HasOwnZone)
                    return OwnZoneId;
                ServerMeshZoneBootstrap bootstrap = ServerMeshZoneBootstrap.Instance;
                if (bootstrap != null && !string.IsNullOrEmpty(bootstrap.AssignedZoneId))
                    return bootstrap.AssignedZoneId;
                return ChannelId ?? string.Empty;
            }
        }

        private void LayoutOnStartServer()
        {
            string mapId = OwnMapId;
            bool dynamicMap = world != null && !string.IsNullOrEmpty(mapId) && world.IsDynamicMap(mapId);
            _layoutGate.Reset(dynamicMap);
            _layoutAppliedHere = false;
            _retiring = false;
            PacingOnStartServer();
            PrewarmOnStartServer();
            EvidenceOnStartServer();
            StaticHandoffOnStartServer();
            BuildingsOnStartServer(); // Slice 4: the building channel, load and transfer state (MeshMapNetworkManager_Buildings)
            _heldPlacements.TakeAll();
            SetStreamTargets(_streamTargetIds, OwnCell);
            if (ClusterClient != null)
            {
                // Registered on every map, and inert until the central sends one: a Static cell hears nothing in 2a.
                ClusterClient.RegisterMessageHandler(MeshClusterMsg.Layout, HandleLayoutMessage);
                ClusterClient.RegisterMessageHandler(MeshClusterMsg.Retire, HandleRetireMessage);
                ClusterClient.RegisterMessageHandler(MeshClusterMsg.CoordinatorNote, HandleCoordinatorNote);
            }
            if (world == null)
                return;
            if (string.IsNullOrEmpty(mapId) && world.HasDynamicMap)
                Logging.LogWarning("Mesh", "Dynamic cells: this server's map is not known at start, so its cell runs Static");
            if (dynamicMap)
                Logging.Log("Mesh", $"Dynamic cells: {LayoutCellId} gated until the central answers LayoutHello");
            if (OwnCell != null && ghostBandWidth > world.BandWidth + 0.01f && BandsWithDynamicMap(OwnCell))
                Logging.LogError("Mesh", $"Ghost band {ghostBandWidth:0.#} m is wider than the registry's band of {world.BandWidth:0.#} m, which dynamic cells are banded and cut with: a cell between the two distances gets none of this cell's puppets. Set dynamicSettings.bandWidth (or {ENV_BAND}) to at least the ghost band");
        }

        private static bool BandsWithDynamicMap(MeshCell cell)
        {
            if (cell.OnDynamicMap)
                return true;
            for (int i = 0; i < cell.bandNeighbours.Count; ++i)
            {
                if (cell.bandNeighbours[i].OnDynamicMap)
                    return true;
            }
            return false;
        }

        private void LayoutOnStopServer()
        {
            List<MeshHeldPlacementQueue<BuildingSaveData>.HeldItem> held = _heldPlacements.TakeAll();
            if (held.Count > 0)
            {
                List<string> ids = new List<string>(held.Count);
                for (int i = 0; i < held.Count; ++i)
                    ids.Add(held[i].item.Id);
                Logging.LogError("Mesh", $"Server stopping with {held.Count} building(s) still held by the layout gate, their items already spent: {string.Join(", ", ids)}");
            }
            _streamTargetIds.Clear();
            PrewarmOnStopServer();
        }

        /// <summary>
        /// Says hello to the central's layout coordinator on every accepted cluster registration: a Dynamic map's cell, and a
        /// Static map's cell that bands with a Dynamic map (Slice 2b decision 14: it receives the broadcasts, applies only
        /// the band-neighbour part, acks and never gates). A Static cell with no Dynamic neighbour says nothing, as before.
        /// </summary>
        private void SendLayoutHello()
        {
            if (world == null || ClusterClient == null || !ClusterClient.IsAppRegistered)
                return;
            if (!IsDynamicMap && !(OwnCell != null && BandsWithDynamicMap(OwnCell)))
                return;
            MeshLayoutSnapshot held = world.AppliedSnapshot ?? new MeshLayoutSnapshot();
            MeshLayoutHelloMessage hello = new MeshLayoutHelloMessage
            {
                cellId = LayoutCellId,
                mapId = OwnMapId,
                incarnation = CellIncarnation,
                dynamicMap = IsDynamicMap,
                held = held,
                processId = System.Diagnostics.Process.GetCurrentProcess().Id, // Slice 2d: the spawner kills by it
            };
            ClusterClient.SendPacket(0, DeliveryMethod.ReliableOrdered, MeshClusterMsg.LayoutHello, writer => hello.Serialize(writer));
            _layoutGate.OnHelloSent(Time.unscaledTime, LayoutGateWarnSeconds);
            Logging.Log("Mesh", $"LayoutHello sent: cell {hello.cellId} incarnation {hello.incarnation} layout {held.pair}");
            MeshFaultOnHelloSent(hello.cellId); // Slice 5 decision 12: MESH_FAULT_DIE_ON_HELLO=<cell>:<n> (MeshMapNetworkManager_Fence)
        }

        /// <summary>Server tick: warn while the gate waits, and create what it held once it opens (or once an item's time is up).</summary>
        private void LayoutTick()
        {
            if (!IsDynamicMap)
                return;
            float now = Time.unscaledTime;
            if (IsLayoutGated)
            {
                if (_layoutGate.WarningDue(now, LayoutGateWarnSeconds))
                    Logging.LogWarning("Mesh", $"Layout gate: cell {LayoutCellId} has had no answer to LayoutHello for {now - _layoutGate.FirstHelloAt:0}s; no handoff out or building routing until the central answers ({_heldPlacements.Count} building(s) held)");
                while (_heldPlacements.TryTakeExpired(now, out MeshHeldPlacementQueue<BuildingSaveData>.HeldItem expired))
                {
                    Logging.LogWarning("Mesh", $"Building {expired.item.Id} held {layoutGatePlacementSeconds:0}s by the layout gate: creating it here without routing");
                    CreateBuildingHere(expired.item);
                }
                return;
            }
            BuildingsOnGateOpen(); // Slice 4 decision 4: the buildings load on the first open tick (MeshMapNetworkManager_BuildingLoad)
            if (!BuildingsLoaded)
            {
                // Slice 4 decision 5: a placement waits for the load too (its row would be read back and created twice).
                while (_heldPlacements.TryTakeExpired(now, out MeshHeldPlacementQueue<BuildingSaveData>.HeldItem expired))
                {
                    Logging.LogWarning("Mesh", $"Building {expired.item.Id} held {layoutGatePlacementSeconds:0}s by the layout gate: creating it here without routing");
                    CreateBuildingHere(expired.item);
                }
                return;
            }
            while (_heldPlacements.TryTakeNext(out MeshHeldPlacementQueue<BuildingSaveData>.HeldItem released))
            {
                Logging.Log("Mesh", $"Building {released.item.Id} released by the layout gate");
                if (released.received)
                    CreateBuildingHere(released.item);
                else
                    CreateBuildingEntity(released.item, false).Forget(); // routed now that the layout is known
            }
        }

        /// <summary>Holds a building while the gate is closed; false when it is open (the caller goes on as usual).</summary>
        private bool TryHoldGatedPlacement(BuildingSaveData data, bool received)
        {
            float deadline = Time.unscaledTime + Mathf.Max(0f, layoutGatePlacementSeconds);
            if (!_heldPlacements.TryHold(HoldsPlacements, data, received, deadline)) // Slice 4 decision 5: gated, or the buildings still loading
                return false;
            Logging.Log("Mesh", $"Building {data.Id} {(received ? "sent by a neighbour" : "placed")} by {data.CreatorName} at {(Vector3)data.Position} is held until the layout gate opens ({_heldPlacements.Count} held)");
            return true;
        }

        /// <summary>Creates a building on this cell without routing it anywhere else (never forwards a forwarded one).</summary>
        private void CreateBuildingHere(BuildingSaveData data) => base.CreateBuildingEntity(data, false).Forget();

        private void HandleLayoutMessage(MessageHandlerData messageHandler)
        {
            if (!TryReadCentralMessage(messageHandler, "Layout", out MeshLayoutMessage message))
                return;
            string cellId = LayoutCellId;
            MeshLayoutSnapshot snapshot = message.snapshot ?? new MeshLayoutSnapshot();
            MeshLayoutPair pair = snapshot.pair;
            bool answer = message.kind == MeshLayoutKind.HelloAnswer;
            if (!MeshLayoutTarget.Accepts(message.kind, message.targetCellId, message.targetIncarnation, cellId, CellIncarnation))
            {
                Logging.Log("Mesh", $"Layout {(answer ? "answer" : "step")} for cell {message.targetCellId} incarnation {message.targetIncarnation} reached cell {cellId} incarnation {CellIncarnation}; ignored");
                return;
            }
            if (world == null)
            {
                RejectLayout(pair, "this server has no world registry");
                return;
            }
            MeshLayoutPair applied = world.AppliedPair;
            if (applied.IsNewerThan(pair))
            {
                Logging.LogWarning("Mesh", $"Layout {pair} is older than the applied {applied}; rejected as stale");
                SendLayoutAck(pair, MeshLayoutAckResult.Rejected, "stale");
                return;
            }
            if (pair == applied)
            {
                if (!MeshLayoutSnapshot.SameEntries(snapshot, world.AppliedSnapshot))
                {
                    Logging.LogError("Mesh", $"Layout {pair} differs from the layout applied under the same pair; rejected");
                    SendLayoutAck(pair, MeshLayoutAckResult.Rejected, "same pair, different entries");
                    return;
                }
                SendLayoutAck(pair, MeshLayoutAckResult.Applied, "already at pair");
                if (answer && _layoutGate.OnAnswered())
                    Logging.Log("Mesh", $"Layout gate open on {cellId} (already at {pair})");
                else if (answer)
                    Logging.Log("Mesh", $"Layout answer {pair} on {cellId}: already applied"); // Slice 5 decision 12: a restarted central's answer is visible
                return;
            }
            if (RetiresInLayout(snapshot))
            {
                SendLayoutAck(pair, MeshLayoutAckResult.Applied, "retiring");
                RetireThisCell();
                return;
            }
            // Every check that can refuse runs before anything changes: an ack is never Rejected after a commit.
            if (!CanHoldLayout(snapshot, out string refusal))
            {
                RejectLayout(pair, refusal);
                return;
            }
            SplitBeforeLayoutApply();
            if (!world.ApplyLayout(snapshot, out MeshLayoutChange change, out string reason))
            {
                RejectLayout(pair, reason);
                return;
            }
            if (change != null && (IsDynamicMap || change.Concerns(cellId)))
                OnLayoutChanged(change);
            if (answer && _layoutGate.OnAnswered())
                Logging.Log("Mesh", System.FormattableString.Invariant($"Layout gate open on {cellId} {Time.unscaledTime - _layoutGate.FirstHelloAt:0.0}s after LayoutHello"));
            SendLayoutAck(pair, MeshLayoutAckResult.Applied, null);
            SplitOnLayoutApplied(pair); // after the ack: the evidence of the change follows it
        }

        /// <summary>Whether this cell can hold the layout: it must be in it (as an asset cell or an entry) and not retired there.</summary>
        private bool CanHoldLayout(MeshLayoutSnapshot snapshot, out string refusal)
        {
            refusal = null;
            if (!HasOwnZone && !IsDynamicMap)
                return true; // a server of a map the mesh does not cut holds the layout for no cell of its own
            string id = LayoutCellId;
            string mapId = OwnMapId;
            for (int i = 0; i < snapshot.entries.Count; ++i)
            {
                MeshLayoutEntry entry = snapshot.entries[i];
                if (entry == null || !string.Equals(entry.id, id, System.StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.Equals(entry.mapId, mapId, System.StringComparison.Ordinal))
                {
                    refusal = $"cell {id} is on map {entry.mapId} in it, but this server serves map {mapId}";
                    return false;
                }
                if (entry.state == MeshCellState.Retired)
                {
                    refusal = $"cell {id} is Retired in it, and this version cannot retire a running cell";
                    return false;
                }
                return true;
            }
            MeshCell cell = world.GetCell(mapId, id);
            if (cell != null && !cell.isDynamic)
                return true;
            refusal = cell != null
                ? $"cell {id} is a dynamic cell the layout no longer names, and this version cannot retire a running cell"
                : $"cell {id} is neither a cell of map {mapId} nor in the layout";
            return false;
        }

        private void RejectLayout(MeshLayoutPair pair, string reason)
        {
            Logging.LogError("Mesh", $"Layout {pair} rejected: {reason}");
            SendLayoutAck(pair, MeshLayoutAckResult.Rejected, reason);
        }

        private void SendLayoutAck(MeshLayoutPair pair, MeshLayoutAckResult result, string reason)
        {
            if (ClusterClient == null || !ClusterClient.IsAppRegistered)
                return;
            MeshLayoutAckMessage ack = new MeshLayoutAckMessage { cellId = LayoutCellId, incarnation = CellIncarnation, pair = pair, result = result, reason = reason };
            ClusterClient.SendPacket(0, DeliveryMethod.ReliableOrdered, MeshClusterMsg.LayoutAck, writer => ack.Serialize(writer));
        }

        /// <summary>
        /// OnLayoutChanged puts an applied layout into effect on this cell: its own cell by id, the cells it streams to, the
        /// band masks of what never moves, the puppets on cells that left the band, links, and the frame offsets of links.
        /// </summary>
        private void OnLayoutChanged(MeshLayoutChange change)
        {
            string id = LayoutCellId;
            MeshCell cell = world.GetCell(OwnMapId, id) ?? world.GetCellById(id);
            if (cell == null)
            {
                Logging.LogWarning("Mesh", $"Layout {change.pair} applied, but cell {id} is not in the registry; nothing here changes");
                return;
            }
            bool first = !HasOwnZone;
            OwnCell = cell;
            OwnZoneId = cell.id;
            if (first)
                Logging.Log("Mesh", $"Own cell from layout: {cell.id} of map {cell.mapId}, grid ({cell.gridX},{cell.gridY}), world x [{cell.worldMinX:0}, {cell.worldMaxX:0}) z [{cell.worldMinZ:0}, {cell.worldMaxZ:0}), {cell.neighbours.Count} neighbours");

            SetStreamTargets(_currentTargetIds, cell);
            MeshStreamTargetDiff.Compute(_streamTargetIds, _currentTargetIds, _targetsLeft, _targetsAdded);
            if (change.BandChanged(cell.id))
            {
                int drops = RebandOwned(_ownedDrops), statics = RebandOwned(_ownedStatics);
                Logging.Log("Mesh", $"Band masks re-measured on {cell.id}: {drops} drop(s) and {statics} static(s) in a band");
            }
            for (int i = 0; i < _targetsLeft.Count; ++i)
                PurgeStreamTarget(_targetsLeft[i]);
            if (enableBorderGhosting)
            {
                for (int i = 0; i < change.changedIds.Count; ++i)
                {
                    MeshCell changed = world.GetCellById(change.changedIds[i]);
                    if (changed != null && changed.state == MeshCellState.Retired && !MeshCell.Same(changed, cell))
                        CloseLink(changed);
                }
                for (int i = 0; i < _targetsAdded.Count; ++i)
                    EnsureLink(world.GetCellById(_targetsAdded[i]), force: true);
                EnsureNeighbourLinks();
                _nextLinkCheck = Time.unscaledTime + 1f;
            }
            // A neighbour of a new cell can say hello before this cell applies the layout naming it, so its link came up
            // with no frame offset; the apply sets it, and says so when it is not zero.
            _tmpOffsetZones.Clear();
            MeshFrameOffsets.Recompute(world, cell, _neighbourZoneByConnection, _frameOffsetByConnection, _tmpOffsetZones);
            for (int i = 0; i < _tmpOffsetZones.Count; ++i)
            {
                MeshCell sender = world.GetCellById(_tmpOffsetZones[i]);
                Vector3 offset = sender.OffsetTo(cell);
                if (offset != Vector3.zero)
                    Logging.Log("Mesh", $"Neighbour zone {sender.id} frame offset {offset} from layout {change.pair}");
            }
            if (IsDynamicMap)
                Logging.Log("Mesh", System.FormattableString.Invariant($"Layout {change.pair} applied: {cell.id} owns {Span(cell.owned)} interest {Span(cell.interest)}, {_currentTargetIds.Count} targets"));
            else
                Logging.Log("Mesh", System.FormattableString.Invariant($"Layout {change.pair} neighbours applied: {cell.id} band {string.Join(",", _currentTargetIds)}"));
            _layoutAppliedHere = true; // from now on a puppet's change of owner is logged
            _streamTargetIds.Clear();
            _streamTargetIds.AddRange(_currentTargetIds);
        }

        private static void SetStreamTargets(List<string> into, MeshCell cell)
        {
            into.Clear();
            if (cell == null)
                return;
            for (int i = 0; i < cell.bandNeighbours.Count; ++i)
                into.Add(cell.bandNeighbours[i].id);
        }

        private static string Span(MeshRect rect)
        {
            return rect.IsEmpty ? "nothing" : System.FormattableString.Invariant($"x[{rect.minX:0},{rect.maxX:0}) z[{rect.minZ:0},{rect.maxZ:0})");
        }

        /// <summary>Re-measures the band mask of everything in a table that never moves; returns how many stand in a band.</summary>
        private int RebandOwned<T>(OwnedTable<T> table) where T : BaseGameEntity
        {
            int banded = 0;
            List<OwnedEntry<T>> entries = table.Entries;
            for (int i = 0; i < entries.Count; ++i)
            {
                OwnedEntry<T> entry = entries[i];
                BaseGameEntity entity = entry.entity;
                if (entity == null || OwnedEntityGone(entity))
                    continue;
                table.Add(entry.entity, entry.meshId, BandMaskOf(entity.EntityTransform.position)); // an existing entry only moves in or out of Banded
                if (entry.bandMask != 0)
                    banded++;
            }
            return banded;
        }

        /// <summary>A cell left this cell's band: remove its puppets of our entities now instead of letting them expire.</summary>
        private void PurgeStreamTarget(string zoneId)
        {
            int removed = 0;
            if (_linkClients.TryGetValue(zoneId, out MeshLinkClient link) && link.IsConnected
                && _streamedToZone.TryGetValue(zoneId, out HashSet<string> streamed) && streamed != null)
            {
                foreach (string meshId in streamed)
                {
                    link.SendGhostRemove(meshId);
                    removed++;
                }
            }
            _streamedToZone.Remove(zoneId);
            _streamedPrevToZone.Remove(zoneId);
            Logging.Log("Mesh", $"Cell {zoneId} left the band of {OwnZoneId}: {removed} puppet(s) there removed");
        }

        private readonly List<string> _tmpOffsetZones = new List<string>();

        private void HandleCoordinatorNote(MessageHandlerData messageHandler)
        {
            if (!TryReadCentralMessage(messageHandler, "Coordinator note", out MeshCoordinatorNoteMessage note))
                return;
            string line = $"Coordinator: {note.code}: {note.text}";
            switch (note.level)
            {
                case MeshNoteLevel.Error:
                    Logging.LogError("Mesh", line);
                    break;
                case MeshNoteLevel.Warning:
                    Logging.LogWarning("Mesh", line);
                    break;
                default:
                    Logging.Log("Mesh", line);
                    break;
            }
        }

        private static bool TryReadCentralMessage<T>(MessageHandlerData messageHandler, string what, out T message) where T : INetSerializable, new()
        {
            if (MeshWholeMessage.TryRead(messageHandler.Reader, out message, out string error))
                return true;
            Logging.LogWarning("Mesh", $"{what} message from the central dropped: {error}");
            return false;
        }
#else
        private void LayoutOnStartServer() { }
        private void LayoutOnStopServer() { }
        private void SendLayoutHello() { }
        private void LayoutTick() { }
#endif
    }
}
