// Server Meshing test rig — what an AI stand-in ship does this frame (Phase 0 step 7). Return is rig-only; the rest
// follow the space game's ShipAIPilot at commit 1a3a58d9.
namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>ServerMeshShipPilotTask is an AI pilot's task for one frame.</summary>
    public enum ServerMeshShipPilotTask
    {
        /// <summary>Patrol orbits the spawn point.</summary>
        Patrol = 0,

        /// <summary>Attack drives at the target's intercept point and fires in bursts.</summary>
        Attack = 1,

        /// <summary>Evade flies away along a rolled direction.</summary>
        Evade = 2,

        /// <summary>Avoid steers away from an obstacle ahead.</summary>
        Avoid = 3,

        /// <summary>Return steers back into the cell rectangle (rig only).</summary>
        Return = 4,
    }
}
