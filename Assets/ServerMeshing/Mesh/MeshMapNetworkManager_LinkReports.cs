// Server Meshing add-on — on-demand links, what replaces the reverse link (Slice 7, slices/slice7_design.md decisions 6a,
// 7a and 7b). With every cell linked to every other, a cell could count on the other side's own link for three things
// that on-demand links no longer guarantee:
//  * readiness: a handoff waits for the target's RegistrationState, which it sends over ITS links. An on-demand cell
//    also sends it back over every inbound connection, and reads one that arrives on its own link as the target's live
//    report while that connection lasts (a far handoff no longer waits for a link the target has no reason to open);
//  * one-shot notices to a monster's or a static's home (LentMonsterOwner, MonsterGone, LentStatic): sent at once when
//    the link is up, else kept for the link (which the waiting notice makes wanted) and sent when it comes up;
//  * restart detection: a restarted zone is noticed only when a link to it comes up again, which may be long after the
//    restart, so the records released at its hello are those made before the new incarnation STARTED (its id is its UTC
//    start time), not before our link came up.
// A cell that is not on demand runs none of this: every entry point returns at once or answers as before.
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class MeshMapNetworkManager
    {
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        /// <summary>How long a home notice waits for its link before it is dropped (and the home's record is left as it was).</summary>
        private const float HomeNoticeWaitSeconds = 60f;
        /// <summary>Clock skew allowed between two hosts when a restart's start time is compared with a record's.</summary>
        private const float RestartSkewSeconds = 2f;

        private sealed class HomeNotice { public string zone; public System.Action<MeshLinkClient> send; public string what; public float since; }
        private struct ReverseReport { public bool registered; public float connectedAt; }

        private readonly List<HomeNotice> _homeNotices = new List<HomeNotice>();
        private readonly Dictionary<string, ReverseReport> _reverseReports = new Dictionary<string, ReverseReport>(System.StringComparer.Ordinal);
        private readonly Dictionary<long, RegistrationStateSent> _reverseStateSent = new Dictionary<long, RegistrationStateSent>();
        private readonly List<long> _reverseScratch = new List<long>();

        private void LinkReportsOnStartServer()
        {
            _homeNotices.Clear();
            _reverseReports.Clear();
            _reverseStateSent.Clear();
        }

        // ---- readiness over the inbound connections (decision 6a) ----

        /// <summary>
        /// SendRegistrationStates' hook: on an on-demand cell, the same report goes back over every inbound connection from
        /// a Dynamic map's cell, on change, on a new connection and every 2 s.
        /// </summary>
        private void SendReverseRegistrationStates(float now, bool registered, bool changed)
        {
            if (!_linksOnDemand || _linkServer == null)
                return;
            _reverseScratch.Clear();
            foreach (long connectionId in _reverseStateSent.Keys)
            {
                if (!_neighbourZoneByConnection.ContainsKey(connectionId))
                    _reverseScratch.Add(connectionId);
            }
            for (int i = 0; i < _reverseScratch.Count; ++i)
                _reverseStateSent.Remove(_reverseScratch[i]);
            foreach (KeyValuePair<long, string> kv in _neighbourZoneByConnection)
            {
                MeshCell sender = world != null ? world.GetCellById(kv.Value) : null;
                if (sender == null || !sender.OnDynamicMap)
                    continue; // a Static map reads nothing
                if (!changed && _reverseStateSent.TryGetValue(kv.Key, out RegistrationStateSent sent) && now - sent.at < RegistrationStateSeconds)
                    continue;
                RegistrationStateMessage msg = new RegistrationStateMessage { registered = registered };
                _linkServer.SendPacket(kv.Key, 0, DeliveryMethod.ReliableOrdered, MeshLinkMessageTypes.RegistrationState, writer => msg.Serialize(writer));
                _reverseStateSent[kv.Key] = new RegistrationStateSent { at = now, connectedSince = now };
            }
        }

        /// <summary>MeshLinkClient's handler: a target's report that came back over our own link to it.</summary>
        internal void OnReverseRegistrationState(string zone, bool registered, float connectedAt)
        {
            if (!IsServer || string.IsNullOrEmpty(zone))
                return;
            bool known = _reverseReports.TryGetValue(zone, out ReverseReport previous) && previous.connectedAt == connectedAt;
            _reverseReports[zone] = new ReverseReport { registered = registered, connectedAt = connectedAt };
            if (!known || previous.registered != registered)
                Logging.Log("Mesh", $"Neighbour zone {zone} reports itself {(registered ? "registered" : "not registered")} over our link to it");
        }

        /// <summary>
        /// NeighbourBlocksHandoff's hook: a report that came back over our link to the zone counts while that same
        /// connection is up. False when there is none (the zone's own report decides, as before).
        /// </summary>
        private bool TryReverseReport(string zone, out bool registered)
        {
            registered = false;
            if (!_linksOnDemand || string.IsNullOrEmpty(zone) || !_reverseReports.TryGetValue(zone, out ReverseReport report))
                return false;
            if (!_linkClients.TryGetValue(zone, out MeshLinkClient client) || !client.IsConnected || client.ConnectedAt != report.connectedAt)
                return false;
            registered = report.registered;
            return true;
        }

        // ---- notices to a home (decision 7a) ----

        /// <summary>
        /// The home-notice sites' hook: on an on-demand cell whose link to the zone is not up, the notice is kept (the
        /// zone is wanted while it waits) and sent once the link is up; true then. False otherwise: the site sends it as before.
        /// </summary>
        private bool DeferWhileUnlinked(string zone, System.Action<MeshLinkClient> send, string what)
        {
            if (!_linksOnDemand || string.IsNullOrEmpty(zone) || string.Equals(zone, OwnZoneId) || IsRetiredZone(zone))
                return false;
            if (_linkClients.TryGetValue(zone, out MeshLinkClient client) && client.IsConnected)
                return false;
            _homeNotices.Add(new HomeNotice { zone = zone, send = send, what = what, since = Time.unscaledTime });
            DemandLink(zone, what);
            Logging.Log("Mesh", $"Notice {what} to zone {zone} waits for the link to it");
            return true;
        }

        /// <summary>The link tick's part: notices whose link is up go out; one that waited too long is dropped with a warning.</summary>
        private void FlushHomeNotices(float now)
        {
            for (int i = 0; i < _homeNotices.Count; ++i)
            {
                HomeNotice notice = _homeNotices[i];
                if (_linkClients.TryGetValue(notice.zone, out MeshLinkClient client) && client.IsConnected)
                {
                    notice.send(client);
                    Logging.Log("Mesh", System.FormattableString.Invariant($"Notice {notice.what} to zone {notice.zone} sent {now - notice.since:0.0}s late, once the link came up"));
                }
                else if (now - notice.since > HomeNoticeWaitSeconds)
                    Logging.LogWarning("Mesh", System.FormattableString.Invariant($"Notice {notice.what} to zone {notice.zone} dropped: no link to it for {HomeNoticeWaitSeconds:0}s"));
                else
                    continue;
                _homeNotices.RemoveAt(i--);
            }
        }

        /// <summary>WantLinks' part: a zone a notice waits for is wanted.</summary>
        private void WantNoticeLinks(float now)
        {
            for (int i = 0; i < _homeNotices.Count; ++i)
                WantLink(_homeNotices[i].zone, MeshLinkNeedReason.Record, now);
        }

        // ---- a restart seen late (decision 7b) ----

        /// <summary>
        /// OnMeshNeighbourHello's hook for a new incarnation: on an on-demand cell, the records to release are those made
        /// before the incarnation started (its id is its UTC start, in hex ticks), less a skew allowance; otherwise, or
        /// when the id is not a time, the given cutoff (our link's up time) as before.
        /// </summary>
        private float RestartCutoff(string incarnation, float linkUpAt)
        {
            if (!_linksOnDemand || string.IsNullOrEmpty(incarnation)
                || !long.TryParse(incarnation, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out long ticks)
                || ticks <= 0 || ticks > System.DateTime.MaxValue.Ticks)
                return linkUpAt;
            double ago = (System.DateTime.UtcNow - new System.DateTime(ticks, System.DateTimeKind.Utc)).TotalSeconds;
            return Time.unscaledTime - (float)ago - RestartSkewSeconds;
        }
#else
        internal void OnReverseRegistrationState(string zone, bool registered, float connectedAt) { }
#endif
    }
}
