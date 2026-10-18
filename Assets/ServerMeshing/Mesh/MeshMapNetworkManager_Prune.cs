// Server Meshing add-on — the neighbours of a retired cell (Slice 2c, slices/slice2c_design.md decision 14). A merged
// cell quits on Retire before the layout names it Retired, so every neighbour first sees its link drop (an Info line
// while it is Draining in the applied layout; the link-loss release runs as before). When a cell applies a layout in
// which zone Z is Retired it closes Z's link (CloseLink, moved here from MeshMapNetworkManager_Layout.cs) and prunes Z:
// lent and borrowed monsters and statics recorded with it, handoffs toward it rolled back, its deferred acks and
// announced arrivals, its inbound connection entries and frame offsets, its hello, and its puppets. Z's last incarnation
// is kept (a reused id's new incarnation releases the old one's records; the unreachable-zone sweep's wasLinked), and a
// retired-zone set makes the sweep and the link upkeep skip Z. Exactly one line per zone:
// "Retired zone <Z>: link closed, <n> record(s) released" (Info at 0, Warning above), n counting the records released at
// the prune plus those the link-loss path released since Z became Draining. Static maps see none of it unless a cell
// banding with a Dynamic map sees a Retired zone.
using System.Collections.Generic;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// MeshRetiredZoneBook is a cell's book of retired neighbour zones: which ones it has pruned (one line per zone and
    /// retirement), and how many records the link-loss path released for a zone while it was Draining. Pure.
    /// </summary>
    public sealed class MeshRetiredZoneBook
    {
        private readonly HashSet<string> _retired = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _releasedWhileDraining = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);

        /// <summary>Count is how many zones are pruned and still Retired.</summary>
        public int Count => _retired.Count;
        public IEnumerable<string> Retired => _retired;

        public bool IsRetired(string zoneId) => !string.IsNullOrEmpty(zoneId) && _retired.Contains(zoneId);

        /// <summary>ReleasedWhileDraining is what the link-loss path released for the zone since it became Draining (not yet pruned).</summary>
        public int ReleasedWhileDraining(string zoneId) => !string.IsNullOrEmpty(zoneId) && _releasedWhileDraining.TryGetValue(zoneId, out int n) ? n : 0;

        /// <summary>NoteReleased adds records released for a zone while it is Draining; ignored for any other zone.</summary>
        public void NoteReleased(string zoneId, bool zoneDraining, int count)
        {
            if (!zoneDraining || count <= 0 || string.IsNullOrEmpty(zoneId) || _retired.Contains(zoneId))
                return;
            _releasedWhileDraining[zoneId] = ReleasedWhileDraining(zoneId) + count;
        }

        /// <summary>
        /// TryRetire marks the zone pruned and gives the line's count: the records released at the prune plus those
        /// released while it was Draining. False (nothing changes) when the zone is already pruned.
        /// </summary>
        public bool TryRetire(string zoneId, int releasedAtPrune, out int total)
        {
            total = 0;
            if (string.IsNullOrEmpty(zoneId) || !_retired.Add(zoneId))
                return false;
            total = Mathf.Max(0, releasedAtPrune) + ReleasedWhileDraining(zoneId);
            _releasedWhileDraining.Remove(zoneId);
            return true;
        }

        /// <summary>Revive forgets a pruned zone whose id is live again (a split reusing it, decision 7): a later retire prunes it again.</summary>
        public bool Revive(string zoneId) => !string.IsNullOrEmpty(zoneId) && _retired.Remove(zoneId);

        public void Clear()
        {
            _retired.Clear();
            _releasedWhileDraining.Clear();
        }

        /// <summary>PruneLine is the one line a cell logs for a retired zone.</summary>
        public static string PruneLine(string zoneId, int released) => $"Retired zone {zoneId}: link closed, {released} record(s) released";

        /// <summary>PruneIsWarning: a retire that released anything was not clean.</summary>
        public static bool PruneIsWarning(int released) => released > 0;
    }

    public partial class MeshMapNetworkManager
    {
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        private readonly MeshRetiredZoneBook _retiredZones = new MeshRetiredZoneBook();
        private readonly List<string> _pruneIds = new List<string>();
        private readonly List<long> _pruneConnections = new List<long>();

        /// <summary>EvidenceOnStartServer's part: a new server knows no retired zone.</summary>
        private void PruneOnStartServer() => _retiredZones.Clear();

        /// <summary>IsRetiredZone: the zone was pruned and is still Retired (the sweep and the link upkeep skip it).</summary>
        private bool IsRetiredZone(string zoneId) => _retiredZones.Count > 0 && _retiredZones.IsRetired(zoneId);

        private bool IsZoneDraining(string zoneId)
        {
            MeshCell cell = world != null && !string.IsNullOrEmpty(zoneId) ? world.GetCellById(zoneId) : null;
            return cell != null && cell.state == MeshCellState.Draining;
        }

        /// <summary>The release paths' hook: records given up for a zone, counted toward its prune line while it is Draining.</summary>
        private void NoteZoneRecordsReleased(string zoneId, int count)
        {
            if (count > 0 && IsZoneDraining(zoneId))
                _retiredZones.NoteReleased(zoneId, true, count);
        }

        /// <summary>OnMeshNeighbourDisconnected's line: a Draining zone's link loss is expected (it quits on Retire before the layout retires it).</summary>
        private void LogNeighbourLinkLost(string zoneId, long connectionId)
        {
            if (IsZoneDraining(zoneId))
                Logging.Log("Mesh", $"Neighbour zone {zoneId} link lost (draining, retire expected)");
            else
                Logging.LogWarning("Mesh", $"Neighbour zone {zoneId} link lost (conn {connectionId})");
        }

        /// <summary>SplitOnLayoutApplied's hook (every map): a pruned zone the layout names live again is forgotten.</summary>
        private void PruneOnLayoutApplied()
        {
            if (_retiredZones.Count == 0 || world == null)
                return;
            _pruneIds.Clear();
            foreach (string zoneId in _retiredZones.Retired)
            {
                MeshCell cell = world.GetCellById(zoneId);
                if (cell != null && cell.state != MeshCellState.Retired)
                    _pruneIds.Add(zoneId);
            }
            for (int i = 0; i < _pruneIds.Count; ++i)
                _retiredZones.Revive(_pruneIds[i]);
            _pruneIds.Clear();
        }

        /// <summary>A retired cell is gone for good: close the link to it, forget what was streamed there, and prune it (once).</summary>
        private void CloseLink(MeshCell cell)
        {
            if (_linkClients.TryGetValue(cell.id, out MeshLinkClient link))
            {
                link.StopClient();
                _linkClients.Remove(cell.id);
            }
            _streamedToZone.Remove(cell.id);
            _streamedPrevToZone.Remove(cell.id);
            _statsLinkPrevOut.Remove(cell.id);
            _meshPeers.Remove(PeerInfoExtensions.GetPeerInfoKey(cell.id, cell.mapId));
            if (_retiredZones.IsRetired(cell.id))
                return;
            int released = PruneRetiredZone(cell.id);
            if (!_retiredZones.TryRetire(cell.id, released, out int total))
                return;
            if (MeshRetiredZoneBook.PruneIsWarning(total))
                Logging.LogWarning("Mesh", MeshRetiredZoneBook.PruneLine(cell.id, total));
            else
                Logging.Log("Mesh", MeshRetiredZoneBook.PruneLine(cell.id, total));
        }

        /// <summary>Releases everything recorded with a retired zone; returns how many records (not puppets) were released.</summary>
        private int PruneRetiredZone(string zoneId)
        {
            int records = 0;
            int lent = 0, borrowed = 0;
            foreach (LentMonster lentMonster in _lentMonsters.Values)
            {
                if (SameZone(lentMonster.ownerZone, zoneId))
                    lent++;
            }
            List<OwnedEntry<BaseCharacterEntity>> owned = _ownedCharacters.Entries;
            for (int i = 0; i < owned.Count; ++i)
            {
                MeshIdentity id = owned[i].entity != null ? MeshIdentity.Get(owned[i].entity) : null;
                if (id != null && owned[i].entity is BaseMonsterCharacterEntity && SameZone(id.HomeZoneId, zoneId))
                    borrowed++;
            }
            if (lent > 0)
                ReleaseLentMonsters(zoneId, "neighbour retired");
            if (borrowed > 0)
                ReleaseBorrowedMonsters(zoneId, "home zone retired");
            records += lent + borrowed;

            int statics = _staticLoans.CountToward(zoneId);
            foreach (LentStatic lentStatic in _lentStatics.Values)
            {
                if (SameZone(lentStatic.ownerZone, zoneId))
                    statics++;
            }
            foreach (string home in _borrowedStatics.Values)
            {
                if (SameZone(home, zoneId))
                    statics++;
            }
            if (statics > 0)
                ReleaseStaticRecords(zoneId, "neighbour retired", null, float.MaxValue);
            records += statics;

            int pending = 0;
            foreach (PendingHandoffAck ack in _pendingHandoffAcks.Values)
            {
                if (SameZone(ack.zone, zoneId))
                    pending++;
            }
            if (pending > 0)
                RollBackHandoffsToward(zoneId, "destination retired");
            records += pending;

            for (int i = _deferredAcks.Count - 1; i >= 0; --i)
            {
                if (!SameZone(_deferredAcks[i].zone, zoneId))
                    continue;
                _deferredAcks.RemoveAt(i);
                records++;
            }

            _pruneIds.Clear();
            foreach (KeyValuePair<string, IncomingHandoff> kv in _incomingHandoffFrom)
            {
                if (SameZone(kv.Value.fromZone, zoneId))
                    _pruneIds.Add(kv.Key);
            }
            for (int i = 0; i < _pruneIds.Count; ++i)
                _incomingHandoffFrom.Remove(_pruneIds[i]);
            records += _pruneIds.Count;
            _pruneIds.Clear();

            _pruneConnections.Clear();
            foreach (KeyValuePair<long, string> kv in _neighbourZoneByConnection)
            {
                if (SameZone(kv.Value, zoneId))
                    _pruneConnections.Add(kv.Key);
            }
            for (int i = 0; i < _pruneConnections.Count; ++i)
            {
                _neighbourZoneByConnection.Remove(_pruneConnections[i]);
                _frameOffsetByConnection.Remove(_pruneConnections[i]);
            }
            _pruneConnections.Clear();
            _neighbourHellos.Remove(zoneId);
            DestroyPuppetsOf(zoneId);
            return records;
        }

        /// <summary>The puppets a retired zone owned (characters, monsters, pets, statics), except one an arrival reserved or a rollback holds.</summary>
        private void DestroyPuppetsOf(string zoneId)
        {
            float now = Time.unscaledTime;
            _pruneIds.Clear();
            foreach (KeyValuePair<string, MeshGhost> kv in _ghosts)
            {
                MeshGhost ghost = kv.Value;
                MeshIdentity id = ghost.entity != null ? MeshIdentity.Get(ghost.entity) : null;
                if (id == null || !SameZone(id.OwnerZoneId, zoneId) || ghost.reserved || now < ghost.holdUntil || IsAwaitingHandoffAck(kv.Key, ghost))
                    continue;
                _pruneIds.Add(kv.Key);
            }
            for (int i = 0; i < _pruneIds.Count; ++i)
            {
                DestroyGhost(_pruneIds[i], "zone retired");
                if (_petGhostsBySummoner.TryGetValue(_pruneIds[i], out HashSet<string> pets) && pets.Count == 0)
                    _petGhostsBySummoner.Remove(_pruneIds[i]);
            }
            _pruneIds.Clear();
            foreach (KeyValuePair<string, GhostStatic> kv in _ghostStatics)
            {
                MeshIdentity id = kv.Value.entity != null ? MeshIdentity.Get(kv.Value.entity) : null;
                if (id != null && SameZone(id.OwnerZoneId, zoneId) && !_staticLoans.IsInFlight(kv.Key))
                    _pruneIds.Add(kv.Key);
            }
            for (int i = 0; i < _pruneIds.Count; ++i)
                DestroyGhostStatic(_pruneIds[i], "zone retired");
            _pruneIds.Clear();
        }

        private static bool SameZone(string a, string b) => string.Equals(a, b, System.StringComparison.OrdinalIgnoreCase);
#endif
    }
}
