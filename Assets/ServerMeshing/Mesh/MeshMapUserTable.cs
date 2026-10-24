// Server Meshing add-on — which map server announced each online user, so the central can say they are offline when that
// server leaves the cluster without saying so itself (DESIGN.md 0.91).
//
// Every map server mirrors every online user of the world (the kit's MapNetworkManager._socialCharactersByUserId), fed by
// the central's relays of UpdateMapUser Add/Remove/Online, and refuses -- silently -- a player that mirror lists ("prevent
// nested login"). Only a server's own disconnect handling sends the Remove, so a map server that dies (killed, crashed)
// leaves its players "online" in every other server's mirror, and the kit's central keeps no user list to correct it: a
// re-login into any other cell is kicked until that user logs in on the dead server's own restart. A dynamic child merged
// or collapsed away after it died has no restart, so its players could never log in again (the 0.91 soak: two bots
// stranded for an hour after `C.1 exited before drained`). The central's add-on records, per user, the connection that
// last announced it (Add or Online) and the peer key that connection registered under; a Remove from that connection
// forgets the user. TakeGone hands back the users of every connection that is no longer a registered map server, or is
// one registered under another key (LiteNetLib reuses connection ids), for the central to relay their Remove. Pure.
using System;
using System.Collections.Generic;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>MeshMapUserTable tracks which map server connection announced each online user.</summary>
    public sealed class MeshMapUserTable
    {
        private struct Entry
        {
            public long connectionId;
            public SocialCharacterData data;
        }

        private readonly Dictionary<string, Entry> _byUser = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly Dictionary<long, string> _keyByConnection = new Dictionary<long, string>();

        /// <summary>Count is how many users the table holds.</summary>
        public int Count => _byUser.Count;

        /// <summary>A server that left: its connection, the key it registered under, and the users it had announced.</summary>
        public sealed class Gone
        {
            public long connectionId;
            public string peerKey;
            public List<SocialCharacterData> users = new List<SocialCharacterData>();
        }

        /// <summary>
        /// Record one UpdateMapUser the central received: Add and Online make the sender the user's server (peerKey is the
        /// key the sender is registered under); a Remove forgets the user only when the sender is the server that has it
        /// (a handoff's destination may announce before its origin's Remove arrives).
        /// </summary>
        public void Record(UpdateUserCharacterMessage.UpdateType type, SocialCharacterData data, long connectionId, string peerKey)
        {
            if (string.IsNullOrEmpty(data.userId))
                return;
            if (type == UpdateUserCharacterMessage.UpdateType.Remove)
            {
                if (_byUser.TryGetValue(data.userId, out Entry known) && known.connectionId == connectionId)
                    _byUser.Remove(data.userId);
                return;
            }
            _byUser[data.userId] = new Entry { connectionId = connectionId, data = data };
            if (!string.IsNullOrEmpty(peerKey))
                _keyByConnection[connectionId] = peerKey;
        }

        /// <summary>
        /// TakeGone removes and returns, per connection, the users of every connection whose server left: keyOf gives the
        /// key a connection is registered under now, or null when it is no registered map server.
        /// </summary>
        public List<Gone> TakeGone(Func<long, string> keyOf)
        {
            Dictionary<long, Gone> gone = new Dictionary<long, Gone>();
            foreach (KeyValuePair<string, Entry> pair in _byUser)
            {
                long connection = pair.Value.connectionId;
                if (!gone.TryGetValue(connection, out Gone left))
                {
                    _keyByConnection.TryGetValue(connection, out string recorded);
                    string now = keyOf(connection);
                    if (now != null && (recorded == null || string.Equals(now, recorded, StringComparison.Ordinal)))
                        continue;
                    gone[connection] = left = new Gone { connectionId = connection, peerKey = recorded };
                }
                left.users.Add(pair.Value.data);
            }
            List<Gone> result = new List<Gone>(gone.Values);
            foreach (Gone left in result)
            {
                foreach (SocialCharacterData user in left.users)
                    _byUser.Remove(user.userId);
                _keyByConnection.Remove(left.connectionId);
            }
            return result;
        }

        /// <summary>Clear forgets everything (the central stopped).</summary>
        public void Clear()
        {
            _byUser.Clear();
            _keyByConnection.Clear();
        }

        /// <summary>GoneLine is the line the central logs when it tells the others a server's users are offline.</summary>
        public static string GoneLine(Gone left, int told)
        {
            return $"Map users: {(string.IsNullOrEmpty(left.peerKey) ? "a map server" : left.peerKey)} (connection {left.connectionId}) left the cluster with {left.users.Count} user(s) still online; told {told} map server(s) they are offline";
        }
    }
}
