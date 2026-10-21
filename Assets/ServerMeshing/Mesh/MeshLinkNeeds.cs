// Server Meshing add-on — on-demand links, the pure parts (Slice 7, slices/slice7_design.md decisions 1-4 and 9).
// A cell of a map with onDemandLinks keeps a mesh link only to the zones it needs: its band neighbours, the zones its
// records name, a zone it heard from a moment ago (it may owe a reply) and a zone a path demanded. A link no longer
// needed closes once it has been unneeded and silent for the idle time. MeshLinkBook decides; the manager's
// MeshMapNetworkManager_Links gathers the wants, opens and closes. Also here: the MESH_LINKS toggle's parsing and the
// central's party-share relay (its targets and its message).
using System;
using System.Collections.Generic;
using LiteNetLib.Utils;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>Why a zone is wanted, strongest first: a band neighbour, a record, a demand, a message heard a moment ago.</summary>
    public enum MeshLinkNeedReason
    {
        None = 0,
        Band = 1,
        Record = 2,
        Demand = 3,
        Recent = 4,
    }

    /// <summary>
    /// MeshLinkBook is one cell's view of the links it needs. Each tick the caller calls BeginTick, Want for every zone
    /// a band, record or demand names, NoteSent for the traffic its links carried, then Settle; NoteHeard comes in with each
    /// counted message. A zone is wanted for its strongest reason of the tick (a zone heard within the idle time is
    /// wanted as Recent); a linked zone not wanted is closed once its last want or traffic is the idle time old. Pure:
    /// the caller passes the clock.
    /// </summary>
    public sealed class MeshLinkBook
    {
        private sealed class Entry
        {
            public MeshLinkNeedReason reason;   // this tick's strongest want (None: not wanted this tick)
            public MeshLinkNeedReason lastReason;
            public float lastActive = float.NegativeInfinity; // the last tick it was wanted, or the last counted message either way
            public float lastHeard = float.NegativeInfinity;
            public float wantedSince = -1f;     // since when it has been wanted without a break (-1: not wanted)
            public float closingSince = -1f;    // the close notice went out (-1: not closing)
        }

        private readonly Dictionary<string, Entry> _zones = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly List<string> _scratch = new List<string>();

        public float IdleSeconds { get; set; } = 30f;
        public float GraceSeconds { get; set; } = 10f;

        /// <summary>Opened and Closed count the links the caller reported opening and closing (NoteOpened, Forget after a close).</summary>
        public int Opened { get; private set; }
        public int Closed { get; private set; }

        public int Count => _zones.Count;

        /// <summary>BeginTick forgets the previous tick's wants (a zone wanted no longer keeps its last-active time).</summary>
        public void BeginTick()
        {
            foreach (Entry entry in _zones.Values)
            {
                entry.lastReason = entry.reason;
                entry.reason = MeshLinkNeedReason.None;
            }
        }

        /// <summary>Want marks a zone wanted this tick for a reason; the strongest reason of the tick is kept.</summary>
        public void Want(string zone, MeshLinkNeedReason reason, float now)
        {
            if (string.IsNullOrEmpty(zone) || reason == MeshLinkNeedReason.None)
                return;
            Entry entry = Get(zone);
            if (entry.reason == MeshLinkNeedReason.None || reason < entry.reason)
                entry.reason = reason;
            entry.lastActive = Math.Max(entry.lastActive, now);
        }

        /// <summary>NoteHeard: a counted message arrived from the zone (it may be owed a reply).</summary>
        public void NoteHeard(string zone, float now)
        {
            if (string.IsNullOrEmpty(zone))
                return;
            Entry entry = Get(zone);
            entry.lastHeard = Math.Max(entry.lastHeard, now);
            entry.lastActive = Math.Max(entry.lastActive, now);
        }

        /// <summary>NoteSent: the link to the zone carried a counted message at <paramref name="at"/>.</summary>
        public void NoteSent(string zone, float at)
        {
            if (string.IsNullOrEmpty(zone) || !_zones.TryGetValue(zone, out Entry entry))
                return;
            entry.lastActive = Math.Max(entry.lastActive, at);
        }

        /// <summary>
        /// Settle finishes the tick: every wanted zone with its reason goes to <paramref name="wanted"/> (a zone heard within
        /// the idle time is wanted as Recent), and every linked zone that is neither wanted nor closing already, and whose
        /// last want or traffic is the idle time old, goes to <paramref name="close"/>. Zones neither wanted nor linked
        /// are forgotten once idle.
        /// </summary>
        public void Settle(float now, ICollection<string> linked, List<KeyValuePair<string, MeshLinkNeedReason>> wanted, List<string> close)
        {
            wanted.Clear();
            close.Clear();
            _scratch.Clear();
            foreach (KeyValuePair<string, Entry> kv in _zones)
            {
                Entry entry = kv.Value;
                if (entry.reason == MeshLinkNeedReason.None && now - entry.lastHeard < IdleSeconds)
                    entry.reason = MeshLinkNeedReason.Recent;
                if (entry.reason != MeshLinkNeedReason.None)
                {
                    if (entry.wantedSince < 0f)
                        entry.wantedSince = now;
                    entry.lastActive = Math.Max(entry.lastActive, now);
                    wanted.Add(new KeyValuePair<string, MeshLinkNeedReason>(kv.Key, entry.reason));
                    continue;
                }
                entry.wantedSince = -1f;
                bool isLinked = linked != null && linked.Contains(kv.Key);
                if (isLinked && entry.closingSince < 0f && now - entry.lastActive >= IdleSeconds)
                    close.Add(kv.Key);
                else if (!isLinked && now - entry.lastActive >= IdleSeconds)
                    _scratch.Add(kv.Key);
            }
            if (linked != null)
            {
                foreach (string zone in linked)
                {
                    // A link the book never heard of (opened before the mode, or by another path) is unneeded from now.
                    if (!_zones.ContainsKey(zone))
                        Get(zone).lastActive = now;
                }
            }
            for (int i = 0; i < _scratch.Count; ++i)
                _zones.Remove(_scratch[i]);
            _scratch.Clear();
        }

        /// <summary>ReasonOf is the zone's reason in the last planned tick (None when it was not wanted).</summary>
        public MeshLinkNeedReason ReasonOf(string zone)
        {
            return !string.IsNullOrEmpty(zone) && _zones.TryGetValue(zone, out Entry entry) ? entry.reason : MeshLinkNeedReason.None;
        }

        /// <summary>WasWanted: the zone was wanted in the tick before this one (a new want is an opening).</summary>
        public bool WasWanted(string zone)
        {
            return !string.IsNullOrEmpty(zone) && _zones.TryGetValue(zone, out Entry entry) && entry.lastReason != MeshLinkNeedReason.None;
        }

        public void NoteOpened() => Opened++;

        /// <summary>MarkClosing: the close notice went out; the link is stopped on a later tick (IsClosing until Forget).</summary>
        public void MarkClosing(string zone, float now)
        {
            if (!string.IsNullOrEmpty(zone))
                Get(zone).closingSince = now;
        }

        public bool IsClosing(string zone) => !string.IsNullOrEmpty(zone) && _zones.TryGetValue(zone, out Entry entry) && entry.closingSince >= 0f;

        /// <summary>ClosingFor is how long ago the close notice went out (negative when the zone is not closing).</summary>
        public float ClosingFor(string zone, float now) => IsClosing(zone) ? now - _zones[zone].closingSince : -1f;

        /// <summary>
        /// Stopped: the caller stopped the closing link. The zone keeps its entry (a want on a later tick reopens it), with
        /// its closing mark cleared, and the close is counted.
        /// </summary>
        public void Stopped(string zone)
        {
            if (string.IsNullOrEmpty(zone) || !_zones.TryGetValue(zone, out Entry entry))
                return;
            entry.closingSince = -1f;
            Closed++;
        }

        /// <summary>Forget drops a zone altogether (a retired zone, the server stopping).</summary>
        public void Forget(string zone)
        {
            if (!string.IsNullOrEmpty(zone))
                _zones.Remove(zone);
        }

        public void Clear()
        {
            _zones.Clear();
            Opened = 0;
            Closed = 0;
        }

        /// <summary>
        /// IsSettling (decision 7): the zone is wanted, our link to it is not up, and this want began less than the grace
        /// ago (the want of a band neighbour is as old as the cell, so a crashed neighbour is not settling). The release
        /// paths do not take such a zone for gone. False while connected, and for a zone not wanted.
        /// </summary>
        public bool IsSettling(string zone, float now, bool connected)
        {
            if (connected || string.IsNullOrEmpty(zone) || !_zones.TryGetValue(zone, out Entry entry) || entry.wantedSince < 0f)
                return false;
            return now - entry.wantedSince < GraceSeconds;
        }

        private Entry Get(string zone)
        {
            if (!_zones.TryGetValue(zone, out Entry entry))
                _zones[zone] = entry = new Entry();
            return entry;
        }
    }

    /// <summary>MeshLinkEnv holds the on-demand links' rig toggles and their parsing (MESH_LINKS, MESH_LINK_CAP, MESH_LINK_IDLE).</summary>
    public static class MeshLinkEnv
    {
        public const string ENV_LINKS = "MESH_LINKS";
        public const string ENV_CAP = "MESH_LINK_CAP";
        public const string ENV_IDLE = "MESH_LINK_IDLE";
        /// <summary>A link server's capacity when nothing sets it (every cell before Slice 7).</summary>
        public const int DefaultCapacity = 64;

        /// <summary>
        /// ParseLinkModes reads a MESH_LINKS value (<c>&lt;mapId|all&gt;:&lt;OnDemand|Off&gt;[,...]</c>) into on-demand per map
        /// ("all" for every map). It throws FormatException, naming the part it could not read, on anything malformed;
        /// OnDemand and Off are read ignoring case.
        /// </summary>
        public static Dictionary<string, bool> ParseLinkModes(string value)
        {
            Dictionary<string, bool> modes = new Dictionary<string, bool>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(value))
                return modes;
            foreach (string rawPart in value.Split(','))
            {
                string part = rawPart.Trim();
                int colon = part.IndexOf(':');
                if (colon < 0)
                    throw new FormatException($"{ENV_LINKS}: '{part}' is not <mapId|all>:<OnDemand|Off>");
                string mapId = part.Substring(0, colon).Trim();
                if (mapId.Length == 0)
                    throw new FormatException($"{ENV_LINKS}: '{part}' names no map");
                if (string.Equals(mapId, MeshLayoutOverride.AllMaps, StringComparison.OrdinalIgnoreCase))
                    mapId = MeshLayoutOverride.AllMaps;
                string modeName = part.Substring(colon + 1).Trim();
                bool onDemand;
                if (string.Equals(modeName, "OnDemand", StringComparison.OrdinalIgnoreCase))
                    onDemand = true;
                else if (string.Equals(modeName, "Off", StringComparison.OrdinalIgnoreCase))
                    onDemand = false;
                else
                    throw new FormatException($"{ENV_LINKS}: '{modeName}' in '{part}' is neither OnDemand nor Off");
                if (modes.ContainsKey(mapId))
                    throw new FormatException($"{ENV_LINKS}: map '{mapId}' is given twice");
                modes[mapId] = onDemand;
            }
            return modes;
        }

        /// <summary>Capacity is a link server's connection cap: MESH_LINK_CAP when it is a whole number of at least 1, else 64.</summary>
        public static int Capacity(string value)
        {
            return int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int cap) && cap >= 1 ? cap : DefaultCapacity;
        }

        /// <summary>
        /// MinIdleSeconds is the floor of an on-demand link's idle time (decision 2): longer than the ack guard and the
        /// arrival reservation, the longest a mirrored record waits on its other side, plus 5 s.
        /// </summary>
        public static float MinIdleSeconds(float ackGuardSeconds, float reservationSeconds) => Math.Max(ackGuardSeconds, reservationSeconds) + 5f;

        /// <summary>IdleSeconds is MESH_LINK_IDLE when it is a number above 0, else the given default.</summary>
        public static float IdleSeconds(string value, float fallback)
        {
            return float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float seconds) && seconds > 0f ? seconds : fallback;
        }
    }

    /// <summary>A party share for a member no link can address, sent up to the central and on to every cell of the map (decision 9).</summary>
    public struct MeshPartyShareRelayMessage : INetSerializable
    {
        public string mapId;
        public string fromCellId;
        public GhostPartyShareMessage share;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(mapId ?? string.Empty);
            writer.Put(fromCellId ?? string.Empty);
            share.Serialize(writer);
        }

        public void Deserialize(NetDataReader reader)
        {
            mapId = reader.GetString();
            fromCellId = reader.GetString();
            share = new GhostPartyShareMessage();
            share.Deserialize(reader);
        }
    }

    /// <summary>MeshPartyRelay is the central's choice of where a relayed party share goes. Pure.</summary>
    public static class MeshPartyRelay
    {
        /// <summary>Targets: the cells' map servers (MeshClusterCoordinatorTransport.ToPeer: no instance server) of the share's map, the sender left out.</summary>
        public static List<long> Targets(IEnumerable<MeshMapServerPeer> peers, string mapId, long fromConnectionId)
        {
            List<long> targets = new List<long>();
            if (peers == null || string.IsNullOrEmpty(mapId))
                return targets;
            foreach (MeshMapServerPeer peer in peers)
            {
                if (peer.connectionId == fromConnectionId || !string.Equals(peer.mapId, mapId, StringComparison.Ordinal))
                    continue;
                targets.Add(peer.connectionId);
            }
            return targets;
        }
    }
}
