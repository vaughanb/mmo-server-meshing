using System;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>
    /// MeshTickScheduleCopy is a copy of the kit LogicUpdater's accumulator, so a test can say on which frames the kit's
    /// ticks fire: the period is (long)(1 / tickHz * frequency) ticks, each Update adds the frame's elapsed ticks and fires
    /// while a whole period has accumulated, and a sixth tick due in one Update zeroes the backlog instead.
    /// </summary>
    public sealed class MeshTickScheduleCopy
    {
        /// <summary>MaxTicksPerUpdate is the kit's cap on ticks per Update.</summary>
        public const int MaxTicksPerUpdate = 5;

        private long _accumulator;

        /// <summary>MeshTickScheduleCopy creates a schedule of <paramref name="tickHz"/> ticks per second on a clock of <paramref name="frequency"/> ticks per second.</summary>
        public MeshTickScheduleCopy(int tickHz, long frequency)
        {
            if (tickHz <= 0)
                throw new ArgumentOutOfRangeException(nameof(tickHz), tickHz, "tick rate must be positive");
            if (frequency <= 0)
                throw new ArgumentOutOfRangeException(nameof(frequency), frequency, "clock frequency must be positive");
            PeriodTicks = (long)(1.0 / tickHz * frequency);
        }

        /// <summary>PeriodTicks is one tick's length in clock ticks.</summary>
        public long PeriodTicks { get; }

        /// <summary>Update adds a frame's elapsed clock ticks and returns how many kit ticks fire in that frame.</summary>
        public int Update(long elapsedTicks)
        {
            _accumulator += elapsedTicks;
            int updates = 0;
            while (_accumulator >= PeriodTicks)
            {
                if (updates >= MaxTicksPerUpdate)
                {
                    _accumulator = 0;
                    return updates;
                }
                updates++;
                _accumulator -= PeriodTicks;
            }
            return updates;
        }
    }
}
