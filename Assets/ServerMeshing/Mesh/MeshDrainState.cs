// Server Meshing add-on — what a Draining cell still holds (Slice 2c, slices/slice2c_design.md decision 9). A cell
// merged away owns nothing, so everything it still has must reach the survivor before it may exit: counted anywhere,
// not by rectangle. The same predicate decides the evidence's `drained` (the central's drain gate) and the cell's own
// exit on Retire. Since Slice 4 (slices/slice4_design.md decision 6) buildings hold the drain too. Pure and game-neutral.
using System.Text;
using LiteNetLib.Utils;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>MeshDrainState counts what a Draining cell still holds; IsDrained when nothing is left.</summary>
    public struct MeshDrainState
    {
        /// <summary>characters: every non-puppet player character present (the kit's despawn delay included) plus pending spawns.</summary>
        public int characters;
        /// <summary>summons: pets, mounts and other summons owned here.</summary>
        public int summons;
        /// <summary>monsters: owned monsters, summoned ones included.</summary>
        public int monsters;
        public int itemDrops;
        /// <summary>rewardDrops: kill and reward drops (they stay with the cell of the kill and expire after their appear time).</summary>
        public int rewardDrops;
        public int harvestables;
        /// <summary>buildings: owned non-scene buildings plus building transfers in flight (Slice 4: part of IsDrained).</summary>
        public int buildings;
        /// <summary>connections: game-transport peers only (not neighbour mesh links, not the cluster client).</summary>
        public int connections;
        /// <summary>inProgress: character handoffs started and not yet sent.</summary>
        public int inProgress;
        public int pendingAcks;
        public int rolledBack;
        public int deferredAcks;
        public int reserved;
        /// <summary>incoming: characters a neighbour announced (HandoffNotice) that have not arrived.</summary>
        public int incoming;
        public int staticLoans;
        /// <summary>serverCharacters: server-owned character handoffs in flight.</summary>
        public int serverCharacters;

        /// <summary>IsDrained is true when every count is zero.</summary>
        public bool IsDrained => characters == 0 && summons == 0 && monsters == 0 && itemDrops == 0 && rewardDrops == 0
            && harvestables == 0 && buildings == 0 && connections == 0 && inProgress == 0 && pendingAcks == 0 && rolledBack == 0
            && deferredAcks == 0 && reserved == 0 && incoming == 0 && staticLoans == 0 && serverCharacters == 0;

        /// <summary>
        /// Describe lists the non-zero counts as name=value pairs separated by single spaces, in field order (buildings
        /// included when non-zero); "nothing" when every count is zero. Used in the straggling log lines.
        /// </summary>
        public string Describe()
        {
            StringBuilder sb = new StringBuilder();
            void Add(string name, int value)
            {
                if (value == 0)
                    return;
                if (sb.Length > 0)
                    sb.Append(' ');
                sb.Append(name).Append('=').Append(value);
            }
            Add("characters", characters);
            Add("summons", summons);
            Add("monsters", monsters);
            Add("itemDrops", itemDrops);
            Add("rewardDrops", rewardDrops);
            Add("harvestables", harvestables);
            Add("buildings", buildings);
            Add("connections", connections);
            Add("inProgress", inProgress);
            Add("pendingAcks", pendingAcks);
            Add("rolledBack", rolledBack);
            Add("deferredAcks", deferredAcks);
            Add("reserved", reserved);
            Add("incoming", incoming);
            Add("staticLoans", staticLoans);
            Add("serverCharacters", serverCharacters);
            return sb.Length == 0 ? "nothing" : sb.ToString();
        }

        /// <summary>Max is the most of each count of the two (a window's samples folded together).</summary>
        public static MeshDrainState Max(MeshDrainState a, MeshDrainState b)
        {
            return new MeshDrainState
            {
                characters = System.Math.Max(a.characters, b.characters),
                summons = System.Math.Max(a.summons, b.summons),
                monsters = System.Math.Max(a.monsters, b.monsters),
                itemDrops = System.Math.Max(a.itemDrops, b.itemDrops),
                rewardDrops = System.Math.Max(a.rewardDrops, b.rewardDrops),
                harvestables = System.Math.Max(a.harvestables, b.harvestables),
                buildings = System.Math.Max(a.buildings, b.buildings),
                connections = System.Math.Max(a.connections, b.connections),
                inProgress = System.Math.Max(a.inProgress, b.inProgress),
                pendingAcks = System.Math.Max(a.pendingAcks, b.pendingAcks),
                rolledBack = System.Math.Max(a.rolledBack, b.rolledBack),
                deferredAcks = System.Math.Max(a.deferredAcks, b.deferredAcks),
                reserved = System.Math.Max(a.reserved, b.reserved),
                incoming = System.Math.Max(a.incoming, b.incoming),
                staticLoans = System.Math.Max(a.staticLoans, b.staticLoans),
                serverCharacters = System.Math.Max(a.serverCharacters, b.serverCharacters),
            };
        }

        /// <summary>Put writes the counts in field order (the evidence wire, version 2).</summary>
        public void Put(NetDataWriter writer)
        {
            writer.Put(characters);
            writer.Put(summons);
            writer.Put(monsters);
            writer.Put(itemDrops);
            writer.Put(rewardDrops);
            writer.Put(harvestables);
            writer.Put(buildings);
            writer.Put(connections);
            writer.Put(inProgress);
            writer.Put(pendingAcks);
            writer.Put(rolledBack);
            writer.Put(deferredAcks);
            writer.Put(reserved);
            writer.Put(incoming);
            writer.Put(staticLoans);
            writer.Put(serverCharacters);
        }

        /// <summary>Get reads what Put wrote.</summary>
        public static MeshDrainState Get(NetDataReader reader)
        {
            return new MeshDrainState
            {
                characters = reader.GetInt(),
                summons = reader.GetInt(),
                monsters = reader.GetInt(),
                itemDrops = reader.GetInt(),
                rewardDrops = reader.GetInt(),
                harvestables = reader.GetInt(),
                buildings = reader.GetInt(),
                connections = reader.GetInt(),
                inProgress = reader.GetInt(),
                pendingAcks = reader.GetInt(),
                rolledBack = reader.GetInt(),
                deferredAcks = reader.GetInt(),
                reserved = reader.GetInt(),
                incoming = reader.GetInt(),
                staticLoans = reader.GetInt(),
                serverCharacters = reader.GetInt(),
            };
        }
    }

    /// <summary>MeshDrainRules holds the pure decisions of a Draining cell (slice2c_design.md decisions 9 and 12).</summary>
    public static class MeshDrainRules
    {
        /// <summary>IdleConnectionSeconds: a game connection idle this long on a Draining cell is closed.</summary>
        public const float IdleConnectionSeconds = 10f;

        /// <summary>
        /// AcceptsEnterGame: a cell that is not Draining takes every enter-game (the kit decides); a Draining one only a
        /// character it is handing on or taking in: a handoff of its own awaiting the ack or rolled back (the client
        /// comes back), one a neighbour announced, or one it holds a puppet of (a real arrival always has one, and a
        /// HandoffNotice can land after the enter-game).
        /// </summary>
        public static bool AcceptsEnterGame(bool draining, bool pendingAck, bool rolledBack, bool incoming, bool puppetHeld)
        {
            return !draining || pendingAck || rolledBack || incoming || puppetHeld;
        }

        /// <summary>
        /// TryPeekEnterGameIds reads the kit's enter-game data (user id, access token, character id) from a copy of
        /// <paramref name="reader"/>, so the reader itself is left where it was for the kit; false on short data.
        /// </summary>
        public static bool TryPeekEnterGameIds(NetDataReader reader, out string userId, out string characterId)
        {
            return TryPeekEnterGameIds(reader, out userId, out _, out characterId);
        }

        /// <summary>
        /// TryPeekEnterGameIds with the access token too: the handoff state's enter-game (DESIGN 1.04) compares it with the
        /// token the origin vouched for, which is what stands in for the kit's token validation on that path.
        /// </summary>
        public static bool TryPeekEnterGameIds(NetDataReader reader, out string userId, out string accessToken, out string characterId)
        {
            userId = null;
            accessToken = null;
            characterId = null;
            if (reader == null || reader.RawData == null)
                return false;
            NetDataReader copy = new NetDataReader(reader.RawData, reader.Position, reader.Position + reader.AvailableBytes);
            try
            {
                userId = copy.GetString();
                accessToken = copy.GetString();
                characterId = copy.GetString();
                return true;
            }
            catch (System.Exception e) when (e is System.InvalidOperationException || e is System.ArgumentException)
            {
                userId = null;
                accessToken = null;
                characterId = null;
                return false;
            }
        }

        /// <summary>IsIdleConnection: a game connection with no registered character and no pending spawn for its user.</summary>
        public static bool IsIdleConnection(bool hasCharacter, bool hasPendingSpawn) => !hasCharacter && !hasPendingSpawn;

        /// <summary>IdleExpired: idle since <paramref name="idleSince"/> for at least IdleConnectionSeconds.</summary>
        public static bool IdleExpired(float idleSince, float now) => now - idleSince >= IdleConnectionSeconds;

        /// <summary>
        /// StragglingDue: past <paramref name="maxSeconds"/> since the retire arrived, a straggling line is due once every
        /// <paramref name="everySeconds"/> (the first at maxSeconds); <paramref name="nextAt"/> is advanced when it is.
        /// </summary>
        public static bool StragglingDue(float retireAt, float now, float maxSeconds, float everySeconds, ref float nextAt)
        {
            if (now - retireAt < maxSeconds)
                return false;
            if (nextAt <= 0f)
                nextAt = retireAt + maxSeconds;
            if (now < nextAt)
                return false;
            nextAt = now + everySeconds;
            return true;
        }
    }
}
