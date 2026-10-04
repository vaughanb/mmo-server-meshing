// Server Meshing test rig — the stand-in ships' window counters (Phase 0 step 7): what every ship component adds to and
// the Ships window line prints. The space game (commit 1a3a58d9) counts none of this; it is rig instrumentation.
using System.Collections.Generic;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshShipCounters is one window of ship figures. Ship components add to the process's Current instance on the
    /// main thread; ServerMeshShipLoad sets the census fields and the speed samples, prints the line and calls Reset.
    /// Plain fields, so a component adds without allocating; the sample lists keep their capacity across windows.
    /// </summary>
    public sealed class ServerMeshShipCounters
    {
        /// <summary>Current is the process's counters (main thread only).</summary>
        public static readonly ServerMeshShipCounters Current = new ServerMeshShipCounters();

        /// <summary>Speeds is the AI ships' sampled speeds in m/s.</summary>
        public readonly List<float> Speeds = new List<float>(1024);
        /// <summary>PatrolSpeeds is the sampled speeds of AI ships that were patrolling, in m/s.</summary>
        public readonly List<float> PatrolSpeeds = new List<float>(1024);
        /// <summary>MirrorAgeCapacity is the most mirror age samples a window keeps (the lists never grow past it).</summary>
        public const int MirrorAgeCapacity = 8192;
        /// <summary>
        /// MirrorAges is, per frame in which a ship with an in-process feed had a record written, that record's server
        /// write timestamp minus the ship's last feed timestamp, in ms.
        /// </summary>
        public readonly List<long> MirrorAges = new List<long>(MirrorAgeCapacity);
        /// <summary>MirrorFrames is, per sample of MirrorAges, the frames since that ship's last feed.</summary>
        public readonly List<int> MirrorFrames = new List<int>(MirrorAgeCapacity);

        /// <summary>Ships is every live stand-in ship on this server at the window's end.</summary>
        public int Ships;
        /// <summary>Ai is the live ServerAI ships.</summary>
        public int Ai;
        /// <summary>Mirror is the live OwnerMirror ships fed in-process (load mirror ships).</summary>
        public int Mirror;
        /// <summary>Parked is the live Parked ships.</summary>
        public int Parked;
        /// <summary>Phantom is the live ships owned by a client connection (phantom ships).</summary>
        public int Phantom;
        /// <summary>Pilots is the live seated-pilot characters this server spawned.</summary>
        public int Pilots;
        /// <summary>Seated is the ships whose driver seat holds their pilot.</summary>
        public int Seated;
        /// <summary>Outside is the AI ships outside (or about to leave) the cell rectangle inset by 10 %.</summary>
        public int Outside;
        /// <summary>NotInSlot is the ships whose kit movement handler is not their ServerMeshShipMovement.</summary>
        public int NotInSlot;
        /// <summary>Pending is the ships whose teleport is unacknowledged at the window's end.</summary>
        public int Pending;
        /// <summary>Scans is the pilots' target scans.</summary>
        public long Scans;
        /// <summary>Casts is the pilots' avoidance sphere casts.</summary>
        public long Casts;
        /// <summary>Shots is the server shots fired.</summary>
        public long Shots;
        /// <summary>Bolts is the bolts launched.</summary>
        public long Bolts;
        /// <summary>Hits is the bolts that damaged a ship of the other team.</summary>
        public long Hits;
        /// <summary>Respawns is the ships respawned by teleport after their hull reached 0.</summary>
        public long Respawns;
        /// <summary>ShieldWrites is the shield sync-field writes that changed its value.</summary>
        public long ShieldWrites;
        /// <summary>RpcSends is the RPC messages ship combat expected to send (subscribers plus an unsubscribed owner per call).</summary>
        public long RpcSends;
        /// <summary>Uploads is the client states applied to a ship's transform.</summary>
        public long Uploads;
        /// <summary>Stale is the client states dropped while their acknowledged teleport lagged the ship's.</summary>
        public long Stale;
        /// <summary>Bad is the client states dropped as short or with a wire mode other than 0.</summary>
        public long Bad;
        /// <summary>PilotMs is the pilots' time in milliseconds.</summary>
        public double PilotMs;
        /// <summary>EngineMs is the engines' FixedUpdate time in milliseconds.</summary>
        public double EngineMs;
        /// <summary>CombatMs is ship combat's time (server updates, shots) in milliseconds.</summary>
        public double CombatMs;
        /// <summary>BoltMs is the bolts' time in milliseconds.</summary>
        public double BoltMs;
        /// <summary>MirrorMs is the in-process mirror feeder's time in milliseconds.</summary>
        public double MirrorMs;

        /// <summary>Reset empties the window; the lists keep their capacity.</summary>
        public void Reset()
        {
            Speeds.Clear();
            PatrolSpeeds.Clear();
            MirrorAges.Clear();
            MirrorFrames.Clear();
            Ships = Ai = Mirror = Parked = Phantom = Pilots = Seated = Outside = NotInSlot = Pending = 0;

            Scans = Casts = Shots = Bolts = Hits = Respawns = ShieldWrites = RpcSends = Uploads = Stale = Bad = 0;
            PilotMs = EngineMs = CombatMs = BoltMs = MirrorMs = 0;
        }
    }
}
