// Server Meshing add-on — a registered player gets its spawn as it registers, not on the cell's next tick (DESIGN.md 1.26).
//
// The kit queues what a newly ready player must receive (its own spawn and every object it now sees) and sends a
// player's queue on the next network tick; LiteNetLib then writes it on its own logic thread's next pass (15 ms). An
// arrival's client adopts its character when that spawn arrives: on the 8 km demo the registration was 29-32 ms
// before the adoption on a hop whose whole gap was 42-67 ms, most of it those two waits (0-17 ms to the tick, up to
// 15 ms to the transport).
//
// At the end of a registration the cell now runs the kit's server state sync at once (the kit's own per-tick pass,
// reached by reflection: it is private) and wakes the transport's logic thread to write it. The states are the ones
// the next tick would have sent, with the current tick; the tick then sends whatever is new.
//
// It has to be the whole pass, not the one player's send. The pass queues every element's pending operations for
// the players that may see them, writes each player's states, and marks the elements synced (their operations are
// dropped: a player whose spawn of the object went out this pass has them in the spawn's initial state already,
// and the kit refuses to queue data for an object it is spawning to that player). Sending one player's spawn alone
// skipped the marking: the operations that filled the promoted puppet (CloneTo, FillEmptySlots) were still queued,
// and the next tick delivered them to the owner on top of the initial state, so its own item lists held every entry
// twice (a bag of 38 scrolls from 19) until the kit next assigned a list whole, as a storage move does. Seen on
// splitbuild's re-login after a cell restart on 2026-11-01, where no storage move followed; present on every
// promoted arrival before that, repaired within seconds by the scenario's own moves.
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class MeshMapNetworkManager
    {
        [Tooltip("Server: send a registering player's queued spawn at once instead of on the next network tick (DESIGN 1.26).")]
        public bool sendSpawnAtRegistration = true;

#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        private static System.Reflection.MethodInfo s_proceedServerGameStateSync;
        private static bool s_syncGameStateLookedUp;

        /// <summary>Runs the server state sync now, so <paramref name="connectionId"/>'s queued spawn goes out, and wakes the transport to write it.</summary>
        private void SendRegistrationStatesNow(long connectionId)
        {
            if (!sendSpawnAtRegistration || !IsServer || !Players.TryGetValue(connectionId, out LiteNetLibPlayer player) || !player.IsReady)
                return;
            if (!s_syncGameStateLookedUp)
            {
                s_syncGameStateLookedUp = true;
                s_proceedServerGameStateSync = typeof(LiteNetLibGameManager).GetMethod("ProceedServerGameStateSync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, new[] { typeof(uint) }, null);
                if (s_proceedServerGameStateSync == null)
                    Logging.LogWarning("Mesh", "The kit has no ProceedServerGameStateSync(tick): a registered player's spawn waits for the next tick");
            }
            if (s_proceedServerGameStateSync == null)
                return;
            s_proceedServerGameStateSync.Invoke(this, new object[] { Tick });
            MeshTransportUnwrap.Innermost<LiteNetLibTransport>(Server?.Transport)?.Server?.TriggerUpdate();
        }
#endif
    }
}
