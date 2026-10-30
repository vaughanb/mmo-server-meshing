// Server Meshing add-on — a cell that fences itself (Slice 2d, slices/slice2d_design.md decisions 5, 8, 9, 10 and 12).
// Every cell of a Dynamic map tells each linked neighbour of its map whether it is registered with the cluster with its
// layout gate open (RegistrationState, link message 130, every 2 s and on change); a neighbour cell of a Dynamic map is a
// handoff target only while its latest report, on a live connection, says so (a restarted cell is not, until the central
// answers its hello). After its first successful registration, a cell fences when it has not been registered for
// childFenceSeconds (from the last tick it was) while a neighbour witnesses the cluster up: it reported itself registered
// within 5 s and has not reported "not registered" since before this cell's last registered tick (after a central
// outage every neighbour did, so nobody fences, even once the central is back), after 3 consecutive registration refusals, or on a Retire whose reason starts
// `fenced:` (any state, a quit in progress included). Fencing: the mesh links close, new game connections are kicked,
// every registered player is saved through the kit's save-and-despawn-immediately path (all at once, bounded by
// fenceSaveSeconds), every connection is kicked, and 0.5 s later the process kills itself (the kit's quit path would save
// everything again, with no bound, after the players logged in elsewhere). Neighbours ignore the hello of an older
// incarnation of a Dynamic map's zone (a zombie's hello would release the live incarnation's records). The rig's
// pre-warm and drain faults are wired here too. Static maps send, read and fence nothing.
using System;
using System.Collections.Generic;
using System.Globalization;
using Cysharp.Threading.Tasks;
using LiteNetLib.Utils;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>MeshFenceDecision is what MeshFenceRule.Decide says a registered-before cell must do.</summary>
    public enum MeshFenceDecision
    {
        Stay,
        /// <summary>Unregistered for childFenceSeconds while a neighbour witnessed the cluster up.</summary>
        Unregistered,
        /// <summary>Three consecutive registration refusals after a prior registration (another process holds the key).</summary>
        Refused,
    }

    /// <summary>MeshFenceRule is the pure part of a cell's self-fence (decisions 5 and 9).</summary>
    public static class MeshFenceRule
    {
        /// <summary>A neighbour's "registered" report witnesses the cluster for this long.</summary>
        public const float WitnessSeconds = 5f;
        /// <summary>
        /// A neighbour's "not registered" report this close before this cell's last registered tick is the same outage: cells
        /// detect a dead central up to a disconnect timeout apart, so a neighbour can lose it a moment before this cell does.
        /// </summary>
        public const float OutageSkewSeconds = 5f;
        /// <summary>Consecutive registration refusals that fence a cell registered before.</summary>
        public const int MaxRefusals = 3;
        /// <summary>The prefix of a Retire reason that fences the cell in any state.</summary>
        public const string FencedReasonPrefix = "fenced:";

        /// <summary>
        /// Decide: nothing before the cell's first successful registration (a slow start is the supervisor's); then
        /// <see cref="MaxRefusals"/> consecutive refusals fence, and so does being unregistered for
        /// <paramref name="fenceSeconds"/> since <paramref name="lastRegisteredAt"/> (the last tick it was registered, so
        /// a process resumed after a suspension fences at once) while <paramref name="witnessed"/> (some neighbour
        /// <see cref="IsWitness"/>).
        /// </summary>
        public static MeshFenceDecision Decide(bool registeredBefore, bool registeredNow, float lastRegisteredAt, bool witnessed, int consecutiveRefusals, float now, float fenceSeconds)
        {
            if (!registeredBefore)
                return MeshFenceDecision.Stay;
            if (consecutiveRefusals >= MaxRefusals)
                return MeshFenceDecision.Refused;
            if (registeredNow || now - lastRegisteredAt < fenceSeconds)
                return MeshFenceDecision.Stay;
            return witnessed ? MeshFenceDecision.Unregistered : MeshFenceDecision.Stay;
        }

        /// <summary>
        /// IsWitness: a neighbour witnesses the cluster up for this cell when its latest report, "registered" at
        /// <paramref name="registeredReportAt"/>, is within <see cref="WitnessSeconds"/>, and it has reported itself
        /// registered continuously since before this cell's last registered tick <paramref name="ownLastRegisteredAt"/>:
        /// its last "not registered" report (<paramref name="unregisteredReportAt"/>, negative when it never sent one) is
        /// older than that tick by more than <see cref="OutageSkewSeconds"/>. A lone cut-off cell has witnesses; after a
        /// central outage every neighbour reported "not registered" after (or with) this cell's loss, so none is.
        /// </summary>
        public static bool IsWitness(bool registeredNow, float registeredReportAt, float unregisteredReportAt, float ownLastRegisteredAt, float now)
        {
            if (!registeredNow || registeredReportAt < 0f || now - registeredReportAt > WitnessSeconds)
                return false;
            return unregisteredReportAt < 0f || unregisteredReportAt < ownLastRegisteredAt - OutageSkewSeconds;
        }

        /// <summary>IsFencedRetire: a Retire whose reason starts <c>fenced:</c> fences the cell in any state.</summary>
        public static bool IsFencedRetire(string reason) => reason != null && reason.StartsWith(FencedReasonPrefix, StringComparison.Ordinal);

        /// <summary>TryTicks reads an incarnation (the UTC ticks of its start, in hex).</summary>
        public static bool TryTicks(string incarnation, out long ticks)
        {
            ticks = 0;
            return !string.IsNullOrEmpty(incarnation) && long.TryParse(incarnation.Trim(), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ticks);
        }

        /// <summary>IsOlderIncarnation: both read as hex ticks and <paramref name="incarnation"/> started before <paramref name="than"/>.</summary>
        public static bool IsOlderIncarnation(string incarnation, string than)
        {
            return TryTicks(incarnation, out long a) && TryTicks(than, out long b) && a < b;
        }

        /// <summary>IgnoresHello: a MeshHello from a Dynamic map's zone whose incarnation is older than the last one seen from it.</summary>
        public static bool IgnoresHello(bool senderOnDynamicMap, string incarnation, string lastSeen)
        {
            return senderOnDynamicMap && IsOlderIncarnation(incarnation, lastSeen);
        }

        /// <summary>
        /// BlocksHandoffTo: a neighbour cell of a Dynamic map is a handoff target only once a live report of it says it is
        /// registered with its layout gate open (a restarted cell is neither until the central answers its hello, and an
        /// older incarnation's report is gone with its link or its successor's hello); one with no live report is not
        /// ready yet. A cell of a Static map never reports and stays a target.
        /// </summary>
        public static bool BlocksHandoffTo(bool targetOnDynamicMap, bool reported, bool registered) => targetOnDynamicMap && (!reported || !registered);

        /// <summary>The fence reason of a decision, for the Fence line (invariant, no braces).</summary>
        public static string Reason(MeshFenceDecision decision, float unregisteredSeconds, string witness, int refusals)
        {
            switch (decision)
            {
                case MeshFenceDecision.Unregistered:
                    return FormattableString.Invariant($"unregistered {unregisteredSeconds:0} s while zone {witness} reports itself registered");
                case MeshFenceDecision.Refused:
                    return FormattableString.Invariant($"{refusals} consecutive registration refusals after a registration");
                default:
                    return "none";
            }
        }
    }

    public partial class MeshMapNetworkManager
    {
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        /// <summary>How often a cell repeats its RegistrationState to each linked neighbour (it also sends on change).</summary>
        private const float RegistrationStateSeconds = 2f;
        /// <summary>The fencing cell kills itself this long after its last kick, so the kicks reach the clients.</summary>
        private const int FenceKillDelayMs = 500;

        private struct RegistrationStateSent { public float at; public float connectedSince; }
        /// <summary>A neighbour's latest report; <c>live</c> while the connection it came over is up (connection).</summary>
        private struct NeighbourRegistration { public bool registered; public float registeredAt; public float unregisteredAt; public bool live; public long connectionId; }

        private readonly Dictionary<string, RegistrationStateSent> _registrationStateSent = new Dictionary<string, RegistrationStateSent>();
        private readonly Dictionary<string, NeighbourRegistration> _neighbourRegistration = new Dictionary<string, NeighbourRegistration>();
        private readonly List<string> _registrationStateZones = new List<string>();
        private bool _registrationStateLast;
        private float _registrationStateNextCheck;
        private bool _fenceEverRegistered;
        private float _fenceLastRegisteredAt;
        private int _fenceRefusals;
        private bool _fenced;
        private int _fenceSaved;
        private int _fenceKickedCount;
        private readonly HashSet<long> _fenceAdmitted = new HashSet<long>();
        private readonly HashSet<long> _fenceKicked = new HashSet<long>();
        private readonly List<long> _fenceTmpConnections = new List<long>();
        private static System.Threading.Timer s_fenceKillTimer;

        private float ChildFenceSeconds => world != null ? world.dynamicSettings.childFenceSeconds : 10f;
        private float FenceSaveSeconds => world != null ? world.dynamicSettings.fenceSaveSeconds : 5f;

        /// <summary>OnStartServer's hook, after LayoutOnStartServer: a new process has never been registered; the pre-warm fault's handler.</summary>
        private void FenceOnStartServer()
        {
            _registrationStateSent.Clear();
            _neighbourRegistration.Clear();
            _registrationStateLast = false;
            _registrationStateNextCheck = 0f;
            _fenceEverRegistered = false;
            _fenceLastRegisteredAt = Time.unscaledTime;
            _fenceRefusals = 0;
            _fenced = false;
            _fenceSaved = 0;
            _fenceKickedCount = 0;
            _fenceAdmitted.Clear();
            _fenceKicked.Clear();
            _hellosSent = 0;
            // Only with the rig's pre-warm fault set: the layout handler peeks at each layout before the usual one runs.
            if (ClusterClient != null && MeshFault.Toggle(MeshFault.DieOnPrewarm) != null)
                ClusterClient.RegisterMessageHandler(MeshClusterMsg.Layout, HandleLayoutMessageWithPrewarmFault);
        }

        /// <summary>OnMeshAppServerRegister's hook: a success arms the fence rules; a refusal after one counts toward three.</summary>
        private void FenceOnRegisterResponse(AckResponseCode responseCode)
        {
            if (!IsServer || !IsDynamicMap)
                return;
            if (responseCode == AckResponseCode.Success)
            {
                _fenceEverRegistered = true;
                _fenceRefusals = 0;
                _fenceLastRegisteredAt = Time.unscaledTime;
                return;
            }
            if (responseCode == AckResponseCode.Error && _fenceEverRegistered)
                _fenceRefusals++;
        }

        /// <summary>
        /// Update's hook, after the kit's own update: the registration witness messages and the fence rules on a Dynamic
        /// map's cell. True while this cell is fencing: nothing else of the add-on runs then (no handoff, no link, no tick).
        /// </summary>
        private bool FenceTick()
        {
            if (!IsServer)
                return false;
            if (_fenced)
            {
                KickLateConnectionsWhileFencing();
                return true;
            }
            if (!IsDynamicMap)
                return false;
            float now = Time.unscaledTime;
            bool registered = ClusterClient != null && ClusterClient.IsAppRegistered;
            if (registered)
            {
                _fenceEverRegistered = true;
                _fenceLastRegisteredAt = now;
            }
            SendRegistrationStates(now, registered && IsLayoutGateOpen); // "registered" to a neighbour means ready: registered and the layout gate open
            if (_retiring)
                return false; // quitting already (a merge's drained retire, a split's unowned retire); only a fenced: Retire stops that
            string witness = registered ? null : FindRegistrationWitness(now);
            MeshFenceDecision decision = MeshFenceRule.Decide(_fenceEverRegistered, registered, _fenceLastRegisteredAt, witness != null, _fenceRefusals, now, ChildFenceSeconds);
            if (decision == MeshFenceDecision.Stay)
                return false;
            FenceThisCell(MeshFenceRule.Reason(decision, now - _fenceLastRegisteredAt, witness, _fenceRefusals));
            return true;
        }

        /// <summary>The first neighbour that witnesses the cluster up for this cell (MeshFenceRule.IsWitness), or null.</summary>
        private string FindRegistrationWitness(float now)
        {
            foreach (KeyValuePair<string, NeighbourRegistration> kv in _neighbourRegistration)
            {
                if (MeshFenceRule.IsWitness(kv.Value.registered, kv.Value.registeredAt, kv.Value.unregisteredAt, _fenceLastRegisteredAt, now))
                    return kv.Key;
            }
            return null;
        }

        // ---- registration witnesses (link message 130) ----

        /// <summary>Sends this cell's registration to every linked neighbour of a Dynamic map: on change, on a new link, and every 2 s.</summary>
        private void SendRegistrationStates(float now, bool registered)
        {
            bool changed = registered != _registrationStateLast;
            if (!changed && now < _registrationStateNextCheck)
                return;
            _registrationStateNextCheck = now + 0.25f;
            _registrationStateLast = registered;
            _registrationStateZones.Clear();
            foreach (KeyValuePair<string, MeshLinkClient> kv in _linkClients)
            {
                MeshLinkClient link = kv.Value;
                if (link == null || !link.IsConnected)
                    continue;
                MeshCell target = world != null ? world.GetCellById(kv.Key) : null;
                if (target == null || !target.OnDynamicMap)
                    continue; // a Static map reads nothing
                float connectedSince = now - link.ConnectedFor;
                if (!changed && _registrationStateSent.TryGetValue(kv.Key, out RegistrationStateSent sent)
                    && Mathf.Abs(sent.connectedSince - connectedSince) < 0.05f && now - sent.at < RegistrationStateSeconds)
                    continue;
                link.SendRegistrationState(registered);
                _registrationStateSent[kv.Key] = new RegistrationStateSent { at = now, connectedSince = connectedSince };
            }
            SendReverseRegistrationStates(now, registered, changed); // Slice 7: on-demand cells, back over the inbound links (MeshMapNetworkManager_LinkReports)
        }

        /// <summary>A neighbour's RegistrationState: per zone, its current state and when it last reported each one (the witness rule reads both).</summary>
        internal void OnRegistrationState(long connectionId, RegistrationStateMessage msg)
        {
            if (!IsServer || !IsDynamicMap || _fenced)
                return;
            if (!_neighbourZoneByConnection.TryGetValue(connectionId, out string zone) || string.IsNullOrEmpty(zone))
                return; // no hello on this connection (or an older incarnation's, ignored)
            float now = Time.unscaledTime;
            bool known = _neighbourRegistration.TryGetValue(zone, out NeighbourRegistration previous);
            NeighbourRegistration next = known ? previous : new NeighbourRegistration { registeredAt = -1f, unregisteredAt = -1f };
            next.registered = msg.registered;
            next.live = true;
            next.connectionId = connectionId;
            if (msg.registered)
                next.registeredAt = now;
            else
                next.unregisteredAt = now;
            _neighbourRegistration[zone] = next;
            if (!msg.registered && (!known || previous.registered))
                Logging.Log("Mesh", $"Neighbour zone {zone} reports itself not registered with the cluster; no handoff to it");
            else if (msg.registered && known && !previous.registered)
                Logging.Log("Mesh", $"Neighbour zone {zone} reports itself registered again");
        }

        /// <summary>
        /// The handoff paths' hook (players, monsters, statics, server characters): on a Dynamic map, a neighbour cell of a
        /// Dynamic map is no handoff target until a live report of it says it is registered with its layout gate open.
        /// </summary>
        private bool NeighbourBlocksHandoff(string zoneId)
        {
            if (!IsDynamicMap || string.IsNullOrEmpty(zoneId))
                return false;
            MeshCell target = world != null ? world.GetCellById(zoneId) : null;
            bool reported = _neighbourRegistration.TryGetValue(zoneId, out NeighbourRegistration state) && state.live;
            bool registered = state.registered;
            if (!reported && TryReverseReport(zoneId, out bool reverse)) // Slice 7: the report that came back over our own link
            {
                reported = true;
                registered = reverse;
            }
            return MeshFenceRule.BlocksHandoffTo(target != null && target.OnDynamicMap, reported, registered);
        }

        // ---- incarnations only move forward (decision 5, map part) ----

        /// <summary>
        /// OnMeshNeighbourHello's hook: a hello from a Dynamic map's zone whose incarnation is older than the last one seen
        /// from it is a zombie's: ignored (and its connection closed), so it releases nothing of the live incarnation's.
        /// A newer incarnation's hello forgets the old one's registration report.
        /// </summary>
        private bool IgnoreOlderNeighbourHello(long connectionId, string zoneId, string incarnation)
        {
            if (string.IsNullOrEmpty(zoneId) || string.IsNullOrEmpty(incarnation) || !_neighbourIncarnation.TryGetValue(zoneId, out string current))
                return false;
            MeshCell sender = world != null ? world.GetCellById(zoneId) : null;
            if (MeshFenceRule.IgnoresHello(sender != null && sender.OnDynamicMap, incarnation, current))
            {
                Logging.LogWarning("Mesh", $"Neighbour zone {zoneId}: hello from older incarnation {incarnation} (current {current}) ignored");
                _linkServer?.Transport?.ServerDisconnect(connectionId);
                return true;
            }
            if (!string.Equals(current, incarnation, StringComparison.Ordinal))
                _neighbourRegistration.Remove(zoneId);
            return false;
        }

        // ---- the fence ----

        /// <summary>HandleRetireMessage's hook, once the id and incarnation match: a fenced: reason fences this cell in any state.</summary>
        private bool FenceOnRetire(string reason)
        {
            if (!IsDynamicMap || !MeshFenceRule.IsFencedRetire(reason))
                return false;
            FenceThisCell(reason);
            return true;
        }

        /// <summary>
        /// FenceThisCell (decision 10): close the mesh links, refuse new game connections, save every registered player
        /// at once through the kit's save-and-despawn-immediately path bounded by fenceSaveSeconds, kick everyone, then kill
        /// this process 0.5 s later. A timer kills it at fenceSaveSeconds + 1 s whatever the main thread does.
        /// </summary>
        private void FenceThisCell(string reason)
        {
            if (_fenced)
                return;
            _fenced = true;
            _retiring = true; // nothing quits or retires this cell another way now
            string cell = LayoutCellId;
            List<long> players = new List<long>();
            _fenceAdmitted.Clear();
            if (Server != null)
            {
                foreach (long connectionId in GetConnectionIds())
                {
                    _fenceAdmitted.Add(connectionId);
                    if (ServerUserHandlers.TryGetPlayerCharacter(connectionId, out _))
                        players.Add(connectionId);
                }
            }
            Logging.LogWarning("Mesh", $"Fence: {cell} incarnation {CellIncarnation} fenced ({PrintableCell(reason)}): saving {players.Count} player(s)");
            foreach (MeshLinkClient client in _linkClients.Values)
                client.StopClient();
            _linkClients.Clear();
            _linkServer?.StopServer();
            _linkServer = null;
            float bound = Mathf.Max(0f, FenceSaveSeconds);
            s_fenceKillTimer?.Dispose();
            if (!Application.isEditor)
                s_fenceKillTimer = new System.Threading.Timer(_ => MeshProcessSelf.Kill(), null, (int)((bound + 1f) * 1000f), System.Threading.Timeout.Infinite);
            FenceSaveAndExit(cell, players, bound).Forget();
        }

        private async UniTaskVoid FenceSaveAndExit(string cell, List<long> players, float bound)
        {
            if (players.Count > 0)
            {
                List<UniTask> saves = new List<UniTask>(players.Count);
                for (int i = 0; i < players.Count; ++i)
                    saves.Add(FenceSaveOne(cell, players[i]));
                await UniTask.WhenAny(UniTask.WhenAll(saves), UniTask.Delay(TimeSpan.FromSeconds(bound), true));
            }
            KickConnectionsWhileFencing(admittedToo: true);
            Logging.LogWarning("Mesh", $"Fence: {cell} saved {_fenceSaved}, kicked {_fenceKickedCount}, exiting");
            await UniTask.Delay(FenceKillDelayMs, true);
            if (Application.isEditor)
                return; // never kills the editor: an editor-hosted cell stays fenced (no links, no ticks) until Play ends
            MeshProcessSelf.Kill();
        }

        private async UniTask FenceSaveOne(string cell, long connectionId)
        {
            try
            {
                await SaveAndDespawnPlayerCharacter(connectionId, true);
                _fenceSaved++;
            }
            catch (Exception)
            {
                Logging.LogWarning("Mesh", $"Fence: {cell} could not save the player of connection {connectionId}");
            }
        }

        /// <summary>While fencing, a game connection that arrived after the fence began is kicked at once.</summary>
        private void KickLateConnectionsWhileFencing() => KickConnectionsWhileFencing(admittedToo: false);

        private void KickConnectionsWhileFencing(bool admittedToo)
        {
            if (Server == null)
                return;
            _fenceTmpConnections.Clear();
            _fenceTmpConnections.AddRange(GetConnectionIds());
            for (int i = 0; i < _fenceTmpConnections.Count; ++i)
            {
                long connectionId = _fenceTmpConnections[i];
                if ((!admittedToo && _fenceAdmitted.Contains(connectionId)) || !_fenceKicked.Add(connectionId))
                    continue;
                KickClient(connectionId, UITextKeys.UI_ERROR_KICKED_FROM_SERVER);
                _fenceKickedCount++;
            }
            _fenceTmpConnections.Clear();
        }

        // ---- the rig's faults (decision 12) ----

        /// <summary>The Layout handler while MESH_FAULT_DIE_ON_PREWARM is set: the first broadcast naming this cell Pending kills it before it acks.</summary>
        private void HandleLayoutMessageWithPrewarmFault(MessageHandlerData messageHandler)
        {
            NetDataReader reader = messageHandler.Reader;
            if (reader != null && reader.RawData != null)
            {
                NetDataReader copy = new NetDataReader(reader.RawData, reader.Position, reader.Position + reader.AvailableBytes);
                string cell = LayoutCellId;
                if (MeshWholeMessage.TryRead(copy, out MeshLayoutMessage peek, out _)
                    && MeshFault.PrewarmFires(MeshFault.Toggle(MeshFault.DieOnPrewarm), cell, peek.kind, peek.snapshot))
                    MeshFault.FireOnce(MeshFault.DieOnPrewarm, cell, $"layout broadcast {peek.snapshot.pair} naming {cell} Pending");
            }
            HandleLayoutMessage(messageHandler);
        }

        private int _hellosSent;

        /// <summary>SendLayoutHello's hook (Slice 5 decision 12): MESH_FAULT_DIE_ON_HELLO=&lt;cell&gt;:&lt;n&gt; kills the cell right after its n-th hello of this process.</summary>
        private void MeshFaultOnHelloSent(string cellId)
        {
            _hellosSent++;
            if (MeshFault.HelloFires(MeshFault.Toggle(MeshFault.DieOnHello), cellId, _hellosSent))
                MeshFault.FireOnce(MeshFault.DieOnHello, cellId, $"layout hello {_hellosSent}");
        }

        /// <summary>OnHandoffNotice's hook: the notice fault (any cell it names) and the drain fault (a child that owns something).</summary>
        private void MeshFaultOnHandoffNotice(string characterId)
        {
            string zone = OwnZoneId;
            if (MeshFault.NamesCell(MeshFault.Toggle(MeshFault.DieOnNotice), zone))
                MeshFault.FireOnce(MeshFault.DieOnNotice, zone, $"handoff notice for {characterId}");
            bool child = IsDynamicMap && HasOwnZone && OwnCell.isDynamic;
            bool owns = child && world != null && !world.AppliedPair.IsNone && !OwnCell.owned.IsEmpty;
            if (MeshFault.DrainFires(MeshFault.Toggle(MeshFault.DieOnDrain), zone, child, owns))
                MeshFault.FireOnce(MeshFault.DieOnDrain, zone, $"drain handoff notice for {characterId}");
        }

        /// <summary>OnServerOnlineSceneLoaded's hook under MESH_MONSTER_AREAS=only:...: an area the list does not name is switched off.</summary>
        private static bool SwitchOffUnnamedMonsterArea(bool only, HashSet<string> named, MonsterSpawnArea area, ref int disabled)
        {
            if (area == null || !MeshMonsterAreasToggle.SwitchesOff(only, named, area.gameObject.name))
                return false;
            if (area.gameObject.activeSelf)
            {
                area.gameObject.SetActive(false);
                disabled++;
            }
            return true;
        }
#else
        internal void OnRegistrationState(long connectionId, RegistrationStateMessage msg) { }
#endif
    }
}
