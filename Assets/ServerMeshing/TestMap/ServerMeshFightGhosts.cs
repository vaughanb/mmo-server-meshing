// Server Meshing test rig — the ghost-target opt-in of the synthetic fighters (Phase 0 step 9 decision 7,
// MESH_FIGHT_GHOSTS=1). SyntheticPlayerBrain.FindMonsterInReach, which the load generator's brains and the phantom
// fighter both use, skips ghost monsters; under the key it does not, so a hit on a ghost reaches the mesh's forwarding
// hook (the ghost's damage event) and is resolved by the owning cell. A no-op where there are no ghosts (one cell).
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshFightGhosts decides whether a synthetic fighter may take a ghost monster as its target. The setting is read
    /// from the environment once per process; unset or "0" keeps the old rule (ghosts skipped), "1" lets fighters hit
    /// ghosts and logs one line, anything else logs one error and keeps the old rule.
    /// </summary>
    public static class ServerMeshFightGhosts
    {
        /// <summary>EnvironmentKey is the opt-in key.</summary>
        public const string EnvironmentKey = "MESH_FIGHT_GHOSTS";

        private static bool s_read;
        private static bool s_on;

        /// <summary>Enabled is true when MESH_FIGHT_GHOSTS=1 in this process (read and logged on first use).</summary>
        public static bool Enabled
        {
            get
            {
                if (!s_read)
                    Read();
                return s_on;
            }
        }

        /// <summary>
        /// TryParseSetting reads the key's value: null, blank or "0" is off (true), "1" is on (true), anything else is false
        /// with an error naming the key and off.
        /// </summary>
        public static bool TryParseSetting(string text, out bool on, out string error)
        {
            on = false;
            error = null;
            string value = text == null ? string.Empty : text.Trim();
            if (value.Length == 0 || value == "0")
                return true;
            if (value == "1")
            {
                on = true;
                return true;
            }
            error = $"{EnvironmentKey}={value.Replace('{', '(').Replace('}', ')')} is not 0 or 1";
            return false;
        }

        /// <summary>SkipsTarget is the target rule: a ghost is skipped unless fighters may hit ghosts; a real monster never is.</summary>
        public static bool SkipsTarget(bool isGhost, bool fightGhosts) => isGhost && !fightGhosts;

        /// <summary>Skips is <see cref="SkipsTarget"/> for <paramref name="monster"/> with this process's setting.</summary>
        public static bool Skips(BaseCharacterEntity monster) => SkipsTarget(MeshIdentity.IsGhostEntity(monster), Enabled);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnPlay()
        {
            s_read = false;
            s_on = false;
        }

        private static void Read()
        {
            s_read = true;
            if (!TryParseSetting(System.Environment.GetEnvironmentVariable(EnvironmentKey), out s_on, out string error))
            {
                Logging.LogError("Mesh", "Fight ghosts error: " + error + "; fighters skip ghosts");
                return;
            }
            if (s_on)
                Logging.Log("Mesh", $"{EnvironmentKey}=1: synthetic fighters may target ghost monsters");
        }
    }
}
