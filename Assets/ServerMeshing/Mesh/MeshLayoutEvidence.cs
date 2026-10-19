// Server Meshing add-on — a map server's evidence for the split gates (Slice 2b, slices/slice2b_design.md decisions 6
// and 8): sent to the central each load window while its map has an operation in progress, and on change. The e1 gate
// reads a new child's pre-warm (its band neighbours' hellos and datagrams, its spawn queue, its saturation); the
// drain gate reads what a shrunk parent still owns in the rectangle it gave away. Id MeshClusterMsg.LayoutEvidence
// (30019). Game-neutral; the load report keeps its own frozen wire. Version 2 (Slice 2c decision 10) appends what a
// Draining cell still holds (MeshDrainState) and whether it is drained; version 3 (Slice 4 decision 12) adds the
// buildings a shrunk cell still owns in the rectangle it gave away to the drain counts.
using System.Collections.Generic;
using LiteNetLib.Utils;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>MeshNeighbourEvidence is what a cell has heard from one band neighbour since that neighbour's hello.</summary>
    public struct MeshNeighbourEvidence
    {
        /// <summary>zoneId is the neighbour's cell id.</summary>
        public string zoneId;
        /// <summary>helloAgeSeconds is the time since the neighbour's inbound MeshHello arrived; negative when none has.</summary>
        public float helloAgeSeconds;
        /// <summary>datagramsSinceHello counts the datagrams received on that link since the hello (snapshots or pings).</summary>
        public int datagramsSinceHello;
    }

    /// <summary>
    /// MeshDrainCounts is what a cell whose owned rectangle shrank still owns inside the rectangle it gave away (by
    /// rectangle with the handoff hysteresis), plus handoffs toward that rectangle's new owner not yet settled.
    /// </summary>
    public struct MeshDrainCounts
    {
        public int characters;
        public int petsAndMounts;
        public int monsters;
        public int staticsAndDrops;
        public int synthetics;
        /// <summary>pendingHandoffs counts handoffs toward the new owner awaiting an ack, rolled back, or reserved.</summary>
        public int pendingHandoffs;
        /// <summary>buildings counts building trees rooted in the given-away rectangle (no hysteresis) plus transfers in flight toward its owner (Slice 4).</summary>
        public int buildings;

        /// <summary>Total is the sum of every count; the drain gate opens at 0.</summary>
        public int Total => characters + petsAndMounts + monsters + staticsAndDrops + synthetics + pendingHandoffs + buildings;
    }

    /// <summary>MeshLayoutEvidenceMessage is one cell's split-gate evidence for one load window.</summary>
    public struct MeshLayoutEvidenceMessage : INetSerializable
    {
        /// <summary>WireVersion is the evidence wire's version, written first; a reader refuses another.</summary>
        public const byte WireVersion = 3;

        public string cellId;
        public string incarnation;
        /// <summary>pair is the layout the cell had applied when the window started.</summary>
        public MeshLayoutPair pair;
        /// <summary>windowStartedAfterApplySeconds is how long after that apply the window started (negative if before).</summary>
        public float windowStartedAfterApplySeconds;
        public List<MeshNeighbourEvidence> neighbours;
        /// <summary>prewarmQueue is the length of the puppet spawn queue at the window's end.</summary>
        public int prewarmQueue;
        /// <summary>saturatedShare is the share of the window's frames that never waited (0 to 1).</summary>
        public float saturatedShare;
        /// <summary>p95Ms and budgetMs are the window's frame-work p95 and frame budget (budget 0 when uncapped).</summary>
        public float p95Ms;
        public float budgetMs;
        /// <summary>shrunk is true when the cell's owned rectangle shrank in its current layout; then givenAway and drain count.</summary>
        public bool shrunk;
        public MeshRect givenAway;
        public MeshDrainCounts drain;
        /// <summary>drainState is what this cell holds anywhere (Slice 2c; zeros when it is not Draining).</summary>
        public MeshDrainState drainState;
        /// <summary>drained is drainState.IsDrained on a Draining cell, else false.</summary>
        public bool drained;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(WireVersion);
            writer.Put(cellId ?? string.Empty);
            writer.Put(incarnation ?? string.Empty);
            MeshLayoutWire.Put(writer, pair);
            writer.Put(windowStartedAfterApplySeconds);
            int count = neighbours != null ? neighbours.Count : 0;
            writer.Put(count);
            for (int i = 0; i < count; ++i)
            {
                writer.Put(neighbours[i].zoneId ?? string.Empty);
                writer.Put(neighbours[i].helloAgeSeconds);
                writer.Put(neighbours[i].datagramsSinceHello);
            }
            writer.Put(prewarmQueue);
            writer.Put(saturatedShare);
            writer.Put(p95Ms);
            writer.Put(budgetMs);
            writer.Put(shrunk);
            MeshLayoutWire.Put(writer, givenAway);
            writer.Put(drain.characters);
            writer.Put(drain.petsAndMounts);
            writer.Put(drain.monsters);
            writer.Put(drain.staticsAndDrops);
            writer.Put(drain.synthetics);
            writer.Put(drain.pendingHandoffs);
            writer.Put(drain.buildings);
            drainState.Put(writer);
            writer.Put(drained);
        }

        /// <summary>
        /// Deserialize reads the wire; a version other than WireVersion or a negative neighbour count throws
        /// System.IO.InvalidDataException, which the central's handler logs as a dropped evidence message.
        /// </summary>
        public void Deserialize(NetDataReader reader)
        {
            byte version = reader.GetByte();
            if (version != WireVersion)
                throw new System.IO.InvalidDataException($"layout evidence wire version {version}, expected {WireVersion}");
            cellId = reader.GetString();
            incarnation = reader.GetString();
            pair = MeshLayoutWire.GetPair(reader);
            windowStartedAfterApplySeconds = reader.GetFloat();
            int count = reader.GetInt();
            if (count < 0 || count > 1024)
                throw new System.IO.InvalidDataException($"layout evidence neighbour count {count}");
            neighbours = new List<MeshNeighbourEvidence>(count);
            for (int i = 0; i < count; ++i)
                neighbours.Add(new MeshNeighbourEvidence { zoneId = reader.GetString(), helloAgeSeconds = reader.GetFloat(), datagramsSinceHello = reader.GetInt() });
            prewarmQueue = reader.GetInt();
            saturatedShare = reader.GetFloat();
            p95Ms = reader.GetFloat();
            budgetMs = reader.GetFloat();
            shrunk = reader.GetBool();
            givenAway = MeshLayoutWire.GetRect(reader);
            drain = new MeshDrainCounts
            {
                characters = reader.GetInt(),
                petsAndMounts = reader.GetInt(),
                monsters = reader.GetInt(),
                staticsAndDrops = reader.GetInt(),
                synthetics = reader.GetInt(),
                pendingHandoffs = reader.GetInt(),
                buildings = reader.GetInt(),
            };
            drainState = MeshDrainState.Get(reader);
            drained = reader.GetBool();
        }
    }
}
