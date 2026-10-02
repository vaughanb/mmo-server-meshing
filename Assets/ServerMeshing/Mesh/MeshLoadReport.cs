// Server Meshing add-on — what a cell tells the central about its load (DESIGN.md 0.71): measured main-thread work
// (MeshWorkProbe), what it holds and where (MeshOccupancyGrid), and what it still owns outside its rectangle.
using LiteNetLib.Utils;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>MeshLoadReportMessage is a cell's load report, sent to the central every few seconds.</summary>
    public sealed class MeshLoadReportMessage : INetSerializable
    {
        public string cellId;
        public string mapId;
        public string incarnation;
        /// <summary>With layoutVersion, the layout pair the cell had applied when it measured (0/0: none).</summary>
        public long layoutIncarnation;
        public long layoutVersion;
        public float windowSeconds;
        public MeshWorkWindow work;
        public int registeredPlayers, ownedPlayers, ownedMonsters;
        public int puppetPlayers, puppetMonsters, puppetDrops, puppetStatics;
        public int clients;
        public float clientOutKBps, linkOutKBps, cpuPercent;
        /// <summary>Owned characters more than the handoff hysteresis past the cell's edge: what a drain still has to move.</summary>
        public int ownedOutside;
        public int ownedDrops, ownedStatics, pendingAcks, reserved;
        public MeshOccupancyGrid grid;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(cellId ?? string.Empty);
            writer.Put(mapId ?? string.Empty);
            writer.Put(incarnation ?? string.Empty);
            writer.Put(layoutIncarnation);
            writer.Put(layoutVersion);
            writer.Put(windowSeconds);
            writer.Put(work.frames);
            writer.Put(work.workMeanMs);
            writer.Put(work.workP95Ms);
            writer.Put(work.workMaxMs);
            writer.Put(work.waitMeanMs);
            writer.Put(work.periodMeanMs);
            writer.Put(work.saturatedShare);
            writer.Put(work.budgetMs);
            writer.Put((byte)work.state);
            writer.Put(registeredPlayers);
            writer.Put(ownedPlayers);
            writer.Put(ownedMonsters);
            writer.Put(puppetPlayers);
            writer.Put(puppetMonsters);
            writer.Put(puppetDrops);
            writer.Put(puppetStatics);
            writer.Put(clients);
            writer.Put(clientOutKBps);
            writer.Put(linkOutKBps);
            writer.Put(cpuPercent);
            writer.Put(ownedOutside);
            writer.Put(ownedDrops);
            writer.Put(ownedStatics);
            writer.Put(pendingAcks);
            writer.Put(reserved);
            writer.Put(grid != null);
            grid?.Serialize(writer);
        }

        public void Deserialize(NetDataReader reader)
        {
            cellId = reader.GetString();
            mapId = reader.GetString();
            incarnation = reader.GetString();
            layoutIncarnation = reader.GetLong();
            layoutVersion = reader.GetLong();
            windowSeconds = reader.GetFloat();
            work.frames = reader.GetInt();
            work.workMeanMs = reader.GetFloat();
            work.workP95Ms = reader.GetFloat();
            work.workMaxMs = reader.GetFloat();
            work.waitMeanMs = reader.GetFloat();
            work.periodMeanMs = reader.GetFloat();
            work.saturatedShare = reader.GetFloat();
            work.budgetMs = reader.GetFloat();
            work.state = (MeshProbeState)reader.GetByte();
            registeredPlayers = reader.GetInt();
            ownedPlayers = reader.GetInt();
            ownedMonsters = reader.GetInt();
            puppetPlayers = reader.GetInt();
            puppetMonsters = reader.GetInt();
            puppetDrops = reader.GetInt();
            puppetStatics = reader.GetInt();
            clients = reader.GetInt();
            clientOutKBps = reader.GetFloat();
            linkOutKBps = reader.GetFloat();
            cpuPercent = reader.GetFloat();
            ownedOutside = reader.GetInt();
            ownedDrops = reader.GetInt();
            ownedStatics = reader.GetInt();
            pendingAcks = reader.GetInt();
            reserved = reader.GetInt();
            if (reader.GetBool())
            {
                grid = new MeshOccupancyGrid();
                grid.Deserialize(reader);
            }
            else
                grid = null;
        }
    }
}
