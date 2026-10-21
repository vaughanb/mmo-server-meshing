// Server Meshing add-on — on-demand links (Slice 7, slices/slice7_design.md decisions 1-11). A cell of a map whose
// layout has onDemandLinks links only to the zones it needs (MeshLinkBook decides): its band and touching neighbours, the
// zones its records name (handoffs and acks in flight, ghosts, lent and borrowed monsters and statics, static loans,
// building releases, the owner of the position of every character it owns), a zone it heard from a moment ago and a
// zone a path demanded. EnsureNeighbourLinks hands over to EnsureLinksOnDemand every second and after every applied
// layout. A link unneeded and silent for linkIdleSeconds is closed: a LinkClose notice first, the stop one tick later.
// The receiver of the notice takes the drop that follows as a close, not a loss (nothing released, an Info line); the
// unreachable-zone sweep does not take a zone for gone while its link is still coming up. A party share for a member no
// link can address goes through the central (MeshClusterMsg.PartyShareRelay) instead of to every link. A cell that is
// not on demand runs none of this: every entry point returns at once.
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class MeshMapNetworkManager
    {
        [Header("Server Meshing — on-demand links (maps with onDemandLinks)")]
        [Tooltip("On-demand links: a link no longer needed (not a band neighbour, named by no record) is closed after this many seconds without a message either way; never less than the handoff ack guard or the arrival reservation plus 5 s. Rig: MESH_LINK_IDLE.")]
        public float linkIdleSeconds = 30f;

        [Tooltip("On-demand links: a zone whose link has been tried for less than this is not taken for gone by the unreachable-zone sweep (a lent record re-pointed to a zone whose link was closed must not respawn the monster while the link comes back up).")]
        public float linkOpenGraceSeconds = 10f;

        /// <summary>Seconds between a close notice and the stop of the link: the reliable notice goes out first (nothing else does: a closing link reads as down).</summary>
        private const float LinkCloseStopSeconds = 0.5f;

#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        private readonly MeshLinkBook _linkBook = new MeshLinkBook();
        private readonly HashSet<long> _idleClosedConnections = new HashSet<long>();
        private readonly Dictionary<string, float> _linkDemands = new Dictionary<string, float>(System.StringComparer.Ordinal);
        private readonly List<KeyValuePair<string, MeshLinkNeedReason>> _linkWanted = new List<KeyValuePair<string, MeshLinkNeedReason>>();
        private readonly List<string> _linkClose = new List<string>();
        private readonly List<string> _linkScratch = new List<string>();
        private readonly HashSet<string> _linkedZones = new HashSet<string>(System.StringComparer.Ordinal);
        private bool _linksOnDemand;
        private int _linkPeakOut, _linkPeakIn;

        /// <summary>This cell's map links on demand (its layout's onDemandLinks, or MESH_LINKS), decided at start.</summary>
        internal bool LinksOnDemand => _linksOnDemand;

        /// <summary>The link server's connection cap: 64, or MESH_LINK_CAP (the rig proves "more cells than one link server takes" at a small cap).</summary>
        private int LinkServerCapacity => MeshLinkEnv.Capacity(System.Environment.GetEnvironmentVariable(MeshLinkEnv.ENV_CAP));

        /// <summary>GhostingOnStartServer's hook, before the link server starts: the mode, the toggles, the relay's handler.</summary>
        private void LinksOnStartServer()
        {
            _linkBook.Clear();
            _idleClosedConnections.Clear();
            _linkDemands.Clear();
            _linkPeakOut = 0;
            _linkPeakIn = 0;
            LinkReportsOnStartServer();
            MeshMapLayout layout = world != null && !string.IsNullOrEmpty(OwnMapId) ? world.GetLayout(OwnMapId) : null;
            _linksOnDemand = enableBorderGhosting && layout != null && layout.onDemandLinks;
            if (ClusterClient != null)
                ClusterClient.RegisterMessageHandler(MeshClusterMsg.PartyShareRelay, HandlePartyShareRelay);
            string capEnv = System.Environment.GetEnvironmentVariable(MeshLinkEnv.ENV_CAP);
            if (!string.IsNullOrWhiteSpace(capEnv))
                Logging.Log("Mesh", $"{MeshLinkEnv.ENV_CAP}={capEnv}: the mesh link server takes {LinkServerCapacity} connection(s)");
            if (!_linksOnDemand)
                return;
            string idleEnv = System.Environment.GetEnvironmentVariable(MeshLinkEnv.ENV_IDLE);
            linkIdleSeconds = MeshLinkEnv.IdleSeconds(idleEnv, linkIdleSeconds);
            _linkBook.IdleSeconds = Mathf.Max(MeshLinkEnv.MinIdleSeconds(HandoffAckGuardSeconds, reservedArrivalTimeout), linkIdleSeconds);
            _linkBook.GraceSeconds = Mathf.Max(1f, linkOpenGraceSeconds);
            Logging.Log("Mesh", System.FormattableString.Invariant($"Mesh links on demand: band neighbours and the cells our records name; an unneeded link closes after {_linkBook.IdleSeconds:0.#} s idle (linkAllZones ignored)"));
        }

        /// <summary>GhostingOnStopServer's hook.</summary>
        private void LinksOnStopServer()
        {
            _linkBook.Clear();
            _idleClosedConnections.Clear();
            _linkDemands.Clear();
        }

        /// <summary>
        /// EnsureNeighbourLinks' hook (decision 5): on an on-demand cell, want, open and close here instead, and return true.
        /// </summary>
        private bool EnsureLinksOnDemand()
        {
            if (!_linksOnDemand || world == null || OwnCell == null)
                return false;
            float now = Time.unscaledTime;
            _linkBook.BeginTick();
            WantLinks(now);
            _linkedZones.Clear();
            foreach (KeyValuePair<string, MeshLinkClient> kv in _linkClients)
            {
                _linkedZones.Add(kv.Key);
                _linkBook.NoteSent(kv.Key, kv.Value.LastSentAt);
            }
            _linkBook.Settle(now, _linkedZones, _linkWanted, _linkClose);
            StopClosedLinks(now);
            FlushHomeNotices(now);
            for (int i = 0; i < _linkWanted.Count; ++i)
            {
                string zone = _linkWanted[i].Key;
                if (_linkBook.IsClosing(zone))
                    continue; // stopped first, reopened on a later tick
                MeshCell cell = world.GetCellById(zone);
                if (cell == null)
                    continue;
                bool had = _linkClients.ContainsKey(zone);
                EnsureLink(cell, force: _linkWanted[i].Value == MeshLinkNeedReason.Demand);
                if (had || !_linkClients.ContainsKey(zone))
                    continue;
                _linkBook.NoteOpened();
                if (_linkWanted[i].Value != MeshLinkNeedReason.Band)
                    Logging.Log("Mesh", $"Mesh link to zone {zone} opened on demand ({ReasonName(_linkWanted[i].Value)})");
            }
            for (int i = 0; i < _linkClose.Count; ++i)
            {
                string zone = _linkClose[i];
                if (!_linkClients.TryGetValue(zone, out MeshLinkClient client))
                    continue;
                _linkBook.MarkClosing(zone, now);
                if (client.IsConnected)
                    client.BeginClose(); // the notice, then the link reads as down until the stop
                else
                    StopLink(zone, client); // never up: nothing to tell
            }
            _linkPeakOut = Mathf.Max(_linkPeakOut, _linkClients.Count);
            _linkPeakIn = Mathf.Max(_linkPeakIn, _neighbourZoneByConnection.Count);
            return true;
        }

        /// <summary>Every zone this cell needs, with its reason (decision 1): band, records, demands. Recent comes from the book.</summary>
        private void WantLinks(float now)
        {
            List<MeshCell> band = OwnCell.bandNeighbours;
            for (int i = 0; i < band.Count; ++i)
                WantLink(band[i].id, MeshLinkNeedReason.Band, now);
            List<MeshCell> touching = OwnCell.neighbours;
            for (int i = 0; i < touching.Count; ++i)
                WantLink(touching[i].id, MeshLinkNeedReason.Band, now);
            foreach (PendingHandoffAck pending in _pendingHandoffAcks.Values)
                WantLink(pending.zone, MeshLinkNeedReason.Record, now);
            foreach (IncomingHandoff incoming in _incomingHandoffFrom.Values)
                WantLink(incoming.fromZone, MeshLinkNeedReason.Record, now);
            for (int i = 0; i < _deferredAcks.Count; ++i)
                WantLink(_deferredAcks[i].zone, MeshLinkNeedReason.Record, now);
            foreach (MeshGhost ghost in _ghosts.Values)
                WantOwnerOf(ghost.entity, now);
            foreach (GhostStatic ghost in _ghostStatics.Values)
                WantOwnerOf(ghost.entity, now);
            foreach (LentMonster lent in _lentMonsters.Values)
                WantLink(lent.ownerZone, MeshLinkNeedReason.Record, now);
            foreach (LentStatic lent in _lentStatics.Values)
                WantLink(lent.ownerZone, MeshLinkNeedReason.Record, now);
            foreach (string home in _borrowedStatics.Values)
                WantLink(home, MeshLinkNeedReason.Record, now);
            foreach (MeshStaticLoanBook<StaticLoan>.Loan loan in _staticLoans.Loans)
                WantLink(loan.toZone, MeshLinkNeedReason.Record, now);
            foreach (BuildingRelease release in _buildingReleases.Values)
                WantLink(release.toZone, MeshLinkNeedReason.Record, now);
            List<OwnedEntry<BaseCharacterEntity>> owned = _ownedCharacters.Entries;
            for (int i = 0; i < owned.Count; ++i)
            {
                BaseCharacterEntity entity = owned[i].entity;
                if (entity == null)
                    continue;
                MeshIdentity id = MeshIdentity.Get(entity);
                if (id != null && !string.IsNullOrEmpty(id.HomeZoneId))
                    WantLink(id.HomeZoneId, MeshLinkNeedReason.Record, now); // a borrowed monster's home
                MeshCell owner = world.OwnerOf(OwnCell, entity.EntityTransform.position);
                if (owner != null)
                    WantLink(owner.id, MeshLinkNeedReason.Record, now); // standing on another cell: its handoff goes there
            }
            _linkScratch.Clear();
            foreach (KeyValuePair<string, float> kv in _linkDemands)
            {
                if (now - kv.Value < _linkBook.IdleSeconds)
                    WantLink(kv.Key, MeshLinkNeedReason.Demand, now);
                else
                    _linkScratch.Add(kv.Key);
            }
            for (int i = 0; i < _linkScratch.Count; ++i)
                _linkDemands.Remove(_linkScratch[i]);
            WantNoticeLinks(now);
        }

        private void WantOwnerOf(BaseGameEntity entity, float now)
        {
            MeshIdentity id = entity != null ? MeshIdentity.Get(entity) : null;
            if (id != null && id.OwnerZoneId != "?")
                WantLink(id.OwnerZoneId, MeshLinkNeedReason.Record, now);
        }

        /// <summary>A zone is wanted unless it is ours, retired or empty.</summary>
        private void WantLink(string zone, MeshLinkNeedReason reason, float now)
        {
            if (string.IsNullOrEmpty(zone) || string.Equals(zone, OwnZoneId) || IsRetiredZone(zone))
                return;
            _linkBook.Want(zone, reason, now);
        }

        /// <summary>Stops every closing link whose notice went out LinkCloseStopSeconds ago (decision 6).</summary>
        private void StopClosedLinks(float now)
        {
            _linkScratch.Clear();
            foreach (string zone in _linkClients.Keys)
            {
                if (_linkBook.ClosingFor(zone, now) >= LinkCloseStopSeconds)
                    _linkScratch.Add(zone);
            }
            for (int i = 0; i < _linkScratch.Count; ++i)
            {
                if (_linkClients.TryGetValue(_linkScratch[i], out MeshLinkClient client))
                    StopLink(_linkScratch[i], client);
            }
            _linkScratch.Clear();
        }

        /// <summary>Stops and forgets one link; the zone's peer and incarnation are kept (a want reopens it).</summary>
        private void StopLink(string zone, MeshLinkClient client)
        {
            client.StopClient();
            _linkClients.Remove(zone);
            _streamedToZone.Remove(zone);
            _streamedPrevToZone.Remove(zone);
            _statsLinkPrevOut.Remove(zone);
            _linkBook.Stopped(zone);
            Logging.Log("Mesh", System.FormattableString.Invariant($"Mesh link to zone {zone} closed on demand: unneeded and idle {_linkBook.IdleSeconds:0.#} s"));
        }

        /// <summary>
        /// DemandLink (decision 4): a path that needs a zone now (a handoff whose link is down) has its link opened at once
        /// and kept for the idle time. A cell that is not on demand ignores it.
        /// </summary>
        internal void DemandLink(string zone, string reason)
        {
            if (!_linksOnDemand || string.IsNullOrEmpty(zone) || world == null || string.Equals(zone, OwnZoneId) || IsRetiredZone(zone))
                return;
            bool known = _linkDemands.ContainsKey(zone);
            _linkDemands[zone] = Time.unscaledTime;
            MeshCell cell = world.GetCellById(zone);
            if (cell == null || _linkBook.IsClosing(zone) || _linkClients.ContainsKey(zone))
                return;
            EnsureLink(cell, force: true);
            if (!known && _linkClients.ContainsKey(zone))
            {
                _linkBook.NoteOpened();
                Logging.Log("Mesh", $"Mesh link to zone {zone} opened on demand (demand: {reason})");
            }
        }

        /// <summary>MeshLinkServer's hook (decision 8): a counted message arrived on an inbound connection.</summary>
        internal void NoteLinkHeard(long connectionId)
        {
            if (_linksOnDemand && _neighbourZoneByConnection.TryGetValue(connectionId, out string zone))
                _linkBook.NoteHeard(zone, Time.unscaledTime);
        }

        /// <summary>
        /// OnMeshNeighbourHello's hook: a zone that opened a link to us is heard (wanted as Recent for the idle time), so the
        /// link back comes up with it and its first requests have a way to be answered.
        /// </summary>
        private void NoteLinkPeerHello(string zone)
        {
            if (_linksOnDemand)
                _linkBook.NoteHeard(zone, Time.unscaledTime);
        }

        /// <summary>A neighbour's LinkClose: the drop that follows on this connection is a close, not a loss.</summary>
        internal void OnLinkClose(long connectionId)
        {
            _idleClosedConnections.Add(connectionId);
        }

        /// <summary>
        /// OnMeshNeighbourDisconnected's hook (decision 6): the drop of a connection whose sender announced its close is
        /// handled here (true): the connection is forgotten, nothing is released or rolled back, one Info line.
        /// </summary>
        private bool OnIdleClosedDisconnect(long connectionId)
        {
            if (!_idleClosedConnections.Remove(connectionId))
                return false;
            ForgetRegistrationReportOf(connectionId);
            if (_neighbourZoneByConnection.TryGetValue(connectionId, out string zone))
            {
                _neighbourZoneByConnection.Remove(connectionId);
                _frameOffsetByConnection.Remove(connectionId);
                Logging.Log("Mesh", $"Neighbour zone {zone} unlinked (idle close)");
            }
            return true;
        }

        /// <summary>
        /// The release paths' hook (decision 7: the unreachable-zone sweep and an inbound link's loss): on an on-demand cell,
        /// a zone whose link we have tried for less than linkOpenGraceSeconds, or that refused us because its link server is
        /// full, is not gone.
        /// </summary>
        private bool LinkStillOpening(string zone)
        {
            if (!_linksOnDemand || string.IsNullOrEmpty(zone))
                return false;
            bool hasClient = _linkClients.TryGetValue(zone, out MeshLinkClient client);
            if (hasClient && client.Refused)
                return true;
            return _linkBook.IsSettling(zone, Time.unscaledTime, hasClient && client.IsConnected);
        }

        /// <summary>The stats tick's hook (decision 11): one line about the links, on an on-demand cell only.</summary>
        private void LogLinkSummary()
        {
            if (!_linksOnDemand)
                return;
            int band = 0, record = 0, recent = 0, demand = 0, closing = 0;
            foreach (string zone in _linkClients.Keys)
            {
                if (_linkBook.IsClosing(zone)) { closing++; continue; }
                switch (_linkBook.ReasonOf(zone))
                {
                    case MeshLinkNeedReason.Band: band++; break;
                    case MeshLinkNeedReason.Record: record++; break;
                    case MeshLinkNeedReason.Demand: demand++; break;
                    case MeshLinkNeedReason.Recent: recent++; break;
                }
            }
            Logging.Log("Mesh", $"Mesh links: {_linkClients.Count} out ({band} band, {record} record, {recent} recent, {demand} demand, {closing} closing), {_neighbourZoneByConnection.Count} in; opened {_linkBook.Opened}, closed {_linkBook.Closed}; peak out {_linkPeakOut}, peak in {_linkPeakIn}");
        }

        private static string ReasonName(MeshLinkNeedReason reason)
        {
            switch (reason)
            {
                case MeshLinkNeedReason.Band: return "band";
                case MeshLinkNeedReason.Record: return "record";
                case MeshLinkNeedReason.Demand: return "demand";
                case MeshLinkNeedReason.Recent: return "recent";
                default: return "none";
            }
        }

        // ---- party shares beyond the band (decision 9) ----

        /// <summary>
        /// BroadcastPartyShare's hook: on an on-demand cell registered with the cluster, the share goes to the central, which
        /// sends it on to every other cell of this map (true); otherwise the broadcast over the links runs (false).
        /// </summary>
        private bool TryRelayPartyShare(GhostPartyShareMessage msg)
        {
            if (!_linksOnDemand || ClusterClient == null || !ClusterClient.IsAppRegistered || OwnCell == null)
                return false;
            MeshPartyShareRelayMessage relay = new MeshPartyShareRelayMessage { mapId = OwnCell.mapId, fromCellId = OwnZoneId, share = msg };
            ClusterClient.SendPacket(0, DeliveryMethod.ReliableOrdered, MeshClusterMsg.PartyShareRelay, writer => relay.Serialize(writer));
            if (traceHandoffTiming)
                Logging.Log("Mesh", System.FormattableString.Invariant($"Party share for remote member {msg.memberId} relayed through the central to map {relay.mapId}: exp {msg.exp} x{msg.expMultiplier:0.###} gold {msg.gold} x{msg.goldMultiplier:0.###} countKill={msg.countKill}"));
            return true;
        }

        /// <summary>A relayed share from the central: applied only if the member is on this cell, as a broadcast is.</summary>
        private void HandlePartyShareRelay(MessageHandlerData messageHandler)
        {
            MeshPartyShareRelayMessage relay = messageHandler.ReadMessage<MeshPartyShareRelayMessage>();
            relay.share.broadcast = true;
            OnGhostPartyShare(-1, relay.share);
        }
#else
        internal bool LinksOnDemand => false;
        internal void NoteLinkHeard(long connectionId) { }
        internal void OnLinkClose(long connectionId) { }
        internal void DemandLink(string zone, string reason) { }
#endif
    }
}
