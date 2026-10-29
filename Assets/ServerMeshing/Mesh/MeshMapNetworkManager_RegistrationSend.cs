// Server Meshing add-on — a registered player gets its spawn as it registers, not on the cell's next tick (DESIGN.md 1.26).
//
// The kit queues what a newly ready player must receive (its own spawn and every object it now sees) and sends a
// player's queue on the next network tick; LiteNetLib then writes it on its own logic thread's next pass (15 ms). An
// arrival's client adopts its character when that spawn arrives: on the 8 km demo the registration was 29-32 ms
// before the adoption on a hop whose whole gap was 42-67 ms, most of it those two waits (0-17 ms to the tick, up to
// 15 ms to the transport).
//
// At the end of a registration the cell now sends that player's queue at once (the kit's own per-player send, reached
// by reflection: it is private) and wakes the transport's logic thread to write it. The states are the ones the next
// tick would have sent, with the current tick; the tick then sends whatever is new.
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class MeshMapNetworkManager
    {
        [Tooltip("Server: send a registering player's queued spawn at once instead of on the next network tick (DESIGN 1.26).")]
        public bool sendSpawnAtRegistration = true;

#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        private static System.Reflection.MethodInfo s_syncGameStateToClient;
        private static bool s_syncGameStateLookedUp;

        /// <summary>Sends <paramref name="connectionId"/>'s queued states now and wakes the transport to write them.</summary>
        private void SendRegistrationStatesNow(long connectionId)
        {
            if (!sendSpawnAtRegistration || !IsServer || !Players.TryGetValue(connectionId, out LiteNetLibPlayer player) || !player.IsReady)
                return;
            if (!s_syncGameStateLookedUp)
            {
                s_syncGameStateLookedUp = true;
                s_syncGameStateToClient = typeof(LiteNetLibGameManager).GetMethod("SyncGameStateToClient", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (s_syncGameStateToClient == null)
                    Logging.LogWarning("Mesh", "The kit has no SyncGameStateToClient(player): a registered player's spawn waits for the next tick");
            }
            if (s_syncGameStateToClient == null)
                return;
            s_syncGameStateToClient.Invoke(this, new object[] { player });
            MeshTransportUnwrap.Innermost<LiteNetLibTransport>(Server?.Transport)?.Server?.TriggerUpdate();
        }
#endif
    }
}
