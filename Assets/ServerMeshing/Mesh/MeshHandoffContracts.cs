// Server Meshing add-on — the game-facing handoff contract (DESIGN.md §11, trigger 2 "explicit handoff"; agreed with
// the space game 2026-10-05, Docs/ServerMeshing/phase0/space_reply_2026-10-05.md A.4).
//
// Contract only: nothing in the add-on implements it yet. The service is built in the slice after Phase 0. Games compile
// against it now and stub it (the space game's HandoffGate raises exactly these events), so their drop and arrival code
// is written against the real shape. Members change only by agreement; doc comments may change.

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>How a game-requested handoff ended, as the origin process sees it.</summary>
    public enum MeshHandoffOutcome : byte
    {
        /// <summary>The destination registered the entity and acknowledged; the origin has released it.</summary>
        Committed,
        /// <summary>The destination refused or gave up (its reservation went unclaimed, or the client dropped before it
        /// was ready). The origin releases its copy.</summary>
        Failed,
        /// <summary>No acknowledgement within the ack guard, or the link to the destination was down past its grace.
        /// The origin's copy is restored and held so the client can return. A late ok-ack still commits, and a second
        /// result with Committed follows.</summary>
        RolledBack,
        /// <summary>The handoff never left the origin (the save failed, or the entity or its client was gone). The
        /// entity stays registered on the origin.</summary>
        Aborted,
    }

    /// <summary>Raised on the ORIGIN process when a ticket ends.</summary>
    public readonly struct MeshHandoffResult
    {
        public readonly ulong TicketId;
        public readonly string EntityId;
        public readonly string DestinationId;
        public readonly MeshHandoffOutcome Outcome;
        /// <summary>Why it failed, rolled back or aborted; empty when committed.</summary>
        public readonly string Reason;
        /// <summary>Milliseconds from RequestHandoff to this result.</summary>
        public readonly int ElapsedMs;

        public MeshHandoffResult(ulong ticketId, string entityId, string destinationId, MeshHandoffOutcome outcome, string reason, int elapsedMs)
        {
            TicketId = ticketId;
            EntityId = entityId;
            DestinationId = destinationId;
            Outcome = outcome;
            Reason = reason ?? string.Empty;
            ElapsedMs = elapsedMs;
        }
    }

    /// <summary>Raised on the DESTINATION process when the arriving entity is registered and its client is ready, in the
    /// same frame as the ok-acknowledgement to the origin. This is what a game's arrival (a drop) waits on.</summary>
    public readonly struct MeshArrivalInfo
    {
        public readonly ulong TicketId;
        public readonly string EntityId;
        public readonly string OriginId;
        /// <summary>Milliseconds from the origin's RequestHandoff to this arrival.</summary>
        public readonly int ElapsedMs;

        public MeshArrivalInfo(ulong ticketId, string entityId, string originId, int elapsedMs)
        {
            TicketId = ticketId;
            EntityId = entityId;
            OriginId = originId;
            ElapsedMs = elapsedMs;
        }
    }

    /// <summary>
    /// A game asks the mesh to move one entity (a player character, with whatever the game moves along with it) to
    /// another process. Today's timings, which the service keeps unless agreed otherwise: ack guard 20 s, link-down grace
    /// 3 s, rollback hold 20 s, destination reservation 15 s, retry after an aborted save 2 s.
    /// </summary>
    public interface IMeshHandoffService
    {
        /// <summary>Origin: starts a handoff of <paramref name="entityId"/> to <paramref name="destinationId"/>. Returns a
        /// ticket, never reused; 0 means refused at once (unknown entity or destination, or a handoff already running for
        /// it), and then no event follows.</summary>
        ulong RequestHandoff(string entityId, string destinationId);

        /// <summary>Origin: once per ticket, plus one Committed after a RolledBack whose late ok-ack arrived.</summary>
        event System.Action<MeshHandoffResult> HandoffCompleted;

        /// <summary>Destination: once per ticket that arrives.</summary>
        event System.Action<MeshArrivalInfo> ArrivalCommitted;
    }
}
