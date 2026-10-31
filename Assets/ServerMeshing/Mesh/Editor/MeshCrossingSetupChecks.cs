// Server Meshing add-on — the settings a smooth crossing rests on, for the Setup window (DESIGN.md 1.34). Puppet motion,
// the server frame rate and the map manager's network tick are saved on a project's prefabs, so a new default in code
// does not reach a project that has saved them: Setup reports each one with what it costs, and offers to set it. These
// are warnings, not faults: a game may spend its cells' CPU elsewhere, and everything still works.
namespace MultiplayerARPG.MMO.Mesh.Editor
{
    /// <summary>MeshCrossingSetupChecks judges the settings behind a crossing; ServerMeshSetupWindow turns them into lines.</summary>
    public static class MeshCrossingSetupChecks
    {
        /// <summary>RecommendedRate is the server frame rate and the network tick the crossing was measured at.</summary>
        public const int RecommendedRate = 60;

        /// <summary>SteeredText is the finding for a map manager that saved Steered.</summary>
        public const string SteeredText = "puppet motion is Steered: on a Static map, a character just across a cell line moves in bursts " +
            "for anyone watching it (10 stalls near the line against 1 with Kinematic, which also costs less CPU; DESIGN 1.34).";

        private const string RateCost = "With both the frame rate and the network tick at 60, a crossing's median gap went from 55 ms to 29 ms, " +
            "for about 40% more CPU per cell (DESIGN 1.34).";

        /// <summary>SteeredShows: the setting is Steered and a Static map uses it (a Dynamic map's cells are Kinematic whatever it says).</summary>
        public static bool SteeredShows(MeshPuppetMotion motion, bool anyStaticMap)
        {
            return motion == MeshPuppetMotion.Steered && anyStaticMap;
        }

        /// <summary>RateIsLow: a positive rate under <see cref="RecommendedRate"/> (zero or less means uncapped).</summary>
        public static bool RateIsLow(int rate)
        {
            return rate > 0 && rate < RecommendedRate;
        }

        /// <summary>LowFrameRateText is the finding for a low server frame rate.</summary>
        public static string LowFrameRateText(int rate)
        {
            return $"server target frame rate {rate}: a crossing's gap is a few server frames. {RateCost}";
        }

        /// <summary>LowNetworkTickText is the finding for a low network tick.</summary>
        public static string LowNetworkTickText(int rate)
        {
            return $"network tick (updateFps) {rate}: an arrival's spawn waits for the next tick, on the cell and on the client. {RateCost}";
        }
    }
}
