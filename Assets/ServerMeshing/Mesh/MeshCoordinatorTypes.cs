// Server Meshing add-on — the types the central's layout coordinator is built from (DESIGN.md 0.72, plan D3): its clock,
// log and transport (injected, so it is tested without Unity networking), the map servers as it sees them, and one cell's
// binding. Split from MeshLayoutCoordinator.cs (Slice 2b) to keep files short.
using System;
using System.Collections.Generic;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>IMeshClock is the coordinator's time.</summary>
    public interface IMeshClock
    {
        /// <summary>Seconds, monotonic.</summary>
        float Now { get; }
        /// <summary>Wall-clock UTC ticks (a new coordinator incarnation is at least this).</summary>
        long UtcTicks { get; }
    }

    /// <summary>MeshUnityClock is Unity's unscaled time and the wall clock.</summary>
    public sealed class MeshUnityClock : IMeshClock
    {
        public float Now => Time.unscaledTime;
        public long UtcTicks => DateTime.UtcNow.Ticks;
    }

    /// <summary>IMeshLog is where the coordinator writes its lines.</summary>
    public interface IMeshLog
    {
        void Info(string message);
        void Warning(string message);
        void Error(string message);
    }

    /// <summary>MeshKitLog writes to the kit's log under the Mesh tag.</summary>
    public sealed class MeshKitLog : IMeshLog
    {
        public void Info(string message) => Logging.Log("Mesh", message);
        public void Warning(string message) => Logging.LogWarning("Mesh", message);
        public void Error(string message) => Logging.LogError("Mesh", message);
    }

    /// <summary>MeshMapServerPeer is a registered map server as the coordinator sees it.</summary>
    public struct MeshMapServerPeer
    {
        public long connectionId;
        public string cellId;
        public string mapId;
        public string address;
        public int port;
    }

    /// <summary>IMeshCoordinatorTransport is how the coordinator reaches the map servers.</summary>
    public interface IMeshCoordinatorTransport
    {
        /// <summary>Every registered map server (cells, not instances or spawners), into a reused list.</summary>
        void GetMapServers(List<MeshMapServerPeer> into);
        bool TryGetMapServer(long connectionId, out MeshMapServerPeer peer);
        void Send(long connectionId, MeshLayoutMessage message);
        void Send(long connectionId, MeshRetireMessage message);
        void Send(long connectionId, MeshCoordinatorNoteMessage message);
    }

    /// <summary>MeshCellBinding is one cell of a Dynamic map as the coordinator knows it: the map server that said hello.</summary>
    public sealed class MeshCellBinding
    {
        public string cellId;
        public string mapId;
        public long connectionId;
        public string incarnation;
        public float helloAt;
        /// <summary>Whether the cell was answered, so its gate is open.</summary>
        public bool answered;
        /// <summary>The last layout the cell acknowledged as applied.</summary>
        public MeshLayoutPair applied;
        public float lastEvidenceAt = -1f;
        /// <summary>When the cell was seen gone from the cluster (-1 while registered).</summary>
        public float unregisteredAt = -1f;
        public bool mismatchLogged;
        /// <summary>A cell of a Static map that bands with a Dynamic one: it receives broadcasts and acks them, and never gates (Slice 2b decision 14).</summary>
        public bool staticNeighbour;
        /// <summary>The cell's latest split-gate evidence (valid when hasEvidence), from this binding's connection and incarnation.</summary>
        public bool hasEvidence;
        public MeshLayoutEvidenceMessage evidence;
        /// <summary>When that evidence was received (a merge's drained window must arrive a load window after the last e2 ack).</summary>
        public float layoutEvidenceAt = -1f;
        /// <summary>The map server's own pid from its hello (0 when unknown): its spawner kills by it (Slice 2d decision 4).</summary>
        public int processId;
        /// <summary>When this incarnation's last MeshLoadReport arrived (-1 before the first): a registered cell silent too long is hung.</summary>
        public float lastReportAt = -1f;
        /// <summary>When a spawner kill of this incarnation was sent (-1 when none): its id may be restarted once its key is gone.</summary>
        public float killedAt = -1f;
        /// <summary>This incarnation's latest load report (null before the first; it arrived at lastReportAt): the policy's evidence (Slice 3).</summary>
        public MeshLoadReportMessage report;
    }

    /// <summary>
    /// IMeshSplitHost is what a split needs from the central around the coordinator: the kit's channel list and the
    /// map-spawn servers. The spawn's result comes back through MeshLayoutCoordinator.OnSpawnResult. A merge (Slice 2c)
    /// uses RemoveChannel once the merged-away cell exited. The supervisor (Slice 2d) restarts a cell through
    /// RequestRestart (its result comes back through MeshLayoutCoordinator.OnRestartSpawnResult) and has a process killed by
    /// its spawner through KillChannel.
    /// </summary>
    public interface IMeshSplitHost
    {
        /// <summary>CanSpawn: whether a map-spawn server can start a child of this base cell now.</summary>
        bool CanSpawn(string baseId, out string error);
        /// <summary>AddChannel adds the child's channel to the central's live channel list (false when it exists).</summary>
        bool AddChannel(string cellId, out string error);
        void RemoveChannel(string cellId);
        /// <summary>RequestSpawn asks the base cell's map-spawn server for the child's process; false when it could not be asked.</summary>
        bool RequestSpawn(string opId, string baseId, string childId, string mapId, out string error);
        /// <summary>RequestRestart asks the base cell's map-spawn server for a new process of an existing cell (its channel is kept); false when it could not be asked.</summary>
        bool RequestRestart(string cellId, string baseId, string mapId, out string error);
        /// <summary>KillChannel sends MeshKillChannelMessage (30020) to the base cell's map-spawn server; false when there is none.</summary>
        bool KillChannel(MeshKillChannelMessage message, string baseId, out string error);
        /// <summary>Whether the kit restarts a crashed base cell itself (autoRestart; false in sleep mode, where the supervisor does).</summary>
        bool KitRestartsBaseCells { get; }
        /// <summary>TryGetHost: the host of the spawner a base cell's family runs on, when that spawner reported one (Slice 3 decision 8).</summary>
        bool TryGetHost(string baseId, out MeshHostInfo host);
    }

    /// <summary>MeshCoordinatorPhase is where the coordinator is.</summary>
    public enum MeshCoordinatorPhase
    {
        Stopped,
        Recovering,
        Running,
    }
}
