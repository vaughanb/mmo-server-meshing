// Server Meshing add-on — the fixed settings of one state fan-out.
using System;

namespace MultiplayerARPG.MMO.Mesh.Sync
{
    /// <summary>MeshStateFanoutSettings holds what a MeshStateFanout is configured with when the sender starts; it never changes afterwards.</summary>
    public sealed class MeshStateFanoutSettings
    {
        /// <summary>FirstSendBuilds is how many builds a new pair receives a record on, whatever its rate.</summary>
        public const int FirstSendBuilds = 3;

        /// <summary>UrgentBuilds is how many builds, the current one included, a DiscreteVersion change makes a source due to every subscriber.</summary>
        public const int UrgentBuilds = 3;

        /// <summary>StuckReliableBuilds is how many reliable writes in a row of a wrapped kit handler raise the one-shot warning.</summary>
        public const int StuckReliableBuilds = 20;

        /// <summary>
        /// MeshStateFanoutSettings checks and keeps the settings: <paramref name="buildHz"/> 1 to 60 (the kit's tick rate),
        /// <paramref name="classifyEveryBuilds"/> 1 to 1000 and <paramref name="maxPacketBytes"/> 64 to 1020.
        /// </summary>
        public MeshStateFanoutSettings(int buildHz, int classifyEveryBuilds, int maxPacketBytes, bool spread, bool measure, bool horizontalDistance)
        {
            if (buildHz < 1 || buildHz > 60)
                throw new ArgumentOutOfRangeException(nameof(buildHz), buildHz, "the build rate must be 1 to 60 Hz");
            if (classifyEveryBuilds < 1 || classifyEveryBuilds > 1000)
                throw new ArgumentOutOfRangeException(nameof(classifyEveryBuilds), classifyEveryBuilds, "pairs must be classified every 1 to 1000 builds");
            if (maxPacketBytes < 64 || maxPacketBytes > MeshStatePacketWriter.MaxPacketBytes)
                throw new ArgumentOutOfRangeException(nameof(maxPacketBytes), maxPacketBytes, $"the packet cap must be 64 to {MeshStatePacketWriter.MaxPacketBytes} bytes");
            BuildHz = buildHz;
            ClassifyEveryBuilds = classifyEveryBuilds;
            MaxPacketBytes = maxPacketBytes;
            Spread = spread;
            Measure = measure;
            HorizontalDistance = horizontalDistance;
        }

        /// <summary>BuildHz is the kit's tick rate, the most builds per second; a pair's divisor is BuildHz over its Hz.</summary>
        public int BuildHz { get; }

        /// <summary>ClassifyEveryBuilds is how often, in builds, every pair is classified again, spread evenly over the builds.</summary>
        public int ClassifyEveryBuilds { get; }

        /// <summary>MaxPacketBytes caps every packet but the one a too-big reliable record goes out alone in.</summary>
        public int MaxPacketBytes { get; }

        /// <summary>Spread defers the routine unreliable records of every second source to the frame after the build.</summary>
        public bool Spread { get; }

        /// <summary>Measure encodes and counts everything but never calls the transport and never consumes one-shot flags.</summary>
        public bool Measure { get; }

        /// <summary>HorizontalDistance measures a pair's distance on X and Z only.</summary>
        public bool HorizontalDistance { get; }
    }
}
