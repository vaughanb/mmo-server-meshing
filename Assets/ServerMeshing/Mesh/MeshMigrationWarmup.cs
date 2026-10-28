// Server Meshing add-on — warm-up of the continuity hop's adoption read (CE-14; DESIGN.md 1.02).
//
// The first crossing of a session ran the adoption's initial-state diff for the first time, and Mono compiled its
// generic instantiations (one per sync list type) in the frame the player was adopted: 6-7 ms of the first hop's
// longest frame on the 8 km demo, where every later hop's adoption took 0.2 ms. Running that read once over the
// player's own state (LiteNetLibAssets.WarmUpAdoption: the state it has, read back as an adoption, so nothing differs
// and nothing fires) compiles it while the player is still settling into the map. RuntimeHelpers.PrepareMethod does
// not compile under Unity's Mono (2,396 methods "prepared" in 6 ms changed nothing), hence a real read.
//
// EXTENSION-ONLY: calls the CE-14 seam; nothing here changes state, only when the JIT runs.

using LiteNetLibManager;

namespace MultiplayerARPG.MMO.Mesh
{
    internal static class MeshMigrationWarmup
    {
        /// <summary>
        /// True once the character's data is in (the kit's stats are cached): the lists are full, so the read
        /// exercises the serializers as a real adoption would.
        /// </summary>
        public static bool IsReady(BasePlayerCharacterEntity player)
        {
            return player != null && player.Identity != null && player.Identity.IsSpawned && player.MaxHp > 0;
        }

        /// <summary>Runs the adoption read once over <paramref name="player"/>'s state; returns the milliseconds it took.</summary>
        public static double Run(LiteNetLibAssets assets, BasePlayerCharacterEntity player)
        {
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            int elements = assets.WarmUpAdoption(player.Identity);
            double ms = sw.Elapsed.TotalMilliseconds;
            Logging.Log("Mesh", $"Migration warm-up: the adoption read of {elements} sync list(s) compiled in {ms:0.##} ms");
            return ms;
        }
    }
}
