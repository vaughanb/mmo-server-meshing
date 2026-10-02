// Server Meshing add-on — what the central's coordinator does with a Dynamic map when it (re)starts (DESIGN.md 0.72,
// plan D3). Pure. Until splits and merges exist (Slice 2b) there is nothing a restarted coordinator could safely adopt,
// so any dynamic entry anywhere freezes the map: no operations and no answers, rather than a guess.
using System;
using System.Collections.Generic;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>MeshRecoveryOutcome is what a map's recovery came to.</summary>
    public enum MeshRecoveryOutcome
    {
        /// <summary>Some registered cell has not said hello yet (or the store cannot be trusted): answer nobody yet.</summary>
        Waiting,
        /// <summary>Nothing dynamic anywhere: the map runs its asset cut, and hellos are answered with it.</summary>
        AdoptStatic,
        /// <summary>A layout this build cannot rebuild safely, or cells missing at the ceiling: no answers, no operations.</summary>
        Frozen,
    }

    /// <summary>MeshRecoveryInput is what the coordinator knows about one map while it recovers.</summary>
    public sealed class MeshRecoveryInput
    {
        public string mapId;
        /// <summary>Whether the map is Dynamic in the asset this central loaded.</summary>
        public bool mapDynamic;
        /// <summary>False when the store file could not be read or trusted.</summary>
        public bool storeTrusted = true;
        /// <summary>Entries of this map in the stored broadcast or staged layout.</summary>
        public int storedEntries;
        /// <summary>Whether any cell's hello held entries of this map.</summary>
        public bool helloHeldEntries;
        /// <summary>Map servers of this map registered with the cluster.</summary>
        public readonly List<string> registeredCells = new List<string>();
        /// <summary>Cells of this map that said hello.</summary>
        public readonly HashSet<string> helloCells = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public float elapsedSeconds;
        public float ceilingSeconds = 60f;
    }

    /// <summary>MeshRecoveryDecision is the resolver's answer.</summary>
    public sealed class MeshRecoveryDecision
    {
        public MeshRecoveryOutcome outcome;
        public string reason;
        /// <summary>Whether the decision is an error the operator must see.</summary>
        public bool isError;
        public readonly List<string> waitingFor = new List<string>();
    }

    /// <summary>MeshRecoveryResolver decides one map's recovery.</summary>
    public static class MeshRecoveryResolver
    {
        public static MeshRecoveryDecision Resolve(MeshRecoveryInput input)
        {
            MeshRecoveryDecision decision = new MeshRecoveryDecision();
            if (!input.mapDynamic)
            {
                decision.outcome = MeshRecoveryOutcome.AdoptStatic;
                decision.reason = input.storedEntries > 0 ? $"map {input.mapId} is Static now; {input.storedEntries} stored dynamic entries ignored" : $"map {input.mapId} is Static";
                return decision;
            }
            if (input.storedEntries > 0 || input.helloHeldEntries)
            {
                decision.outcome = MeshRecoveryOutcome.Frozen;
                decision.isError = true;
                decision.reason = $"map {input.mapId} has a runtime layout ({input.storedEntries} stored entries{(input.helloHeldEntries ? ", and cells hold one" : string.Empty)}); rebuilding it needs Slice 2b recovery";
                return decision;
            }
            foreach (string cell in input.registeredCells)
            {
                if (!input.helloCells.Contains(cell))
                    decision.waitingFor.Add(cell);
            }
            bool atCeiling = input.elapsedSeconds >= input.ceilingSeconds;
            if (!input.storeTrusted)
            {
                if (!atCeiling)
                {
                    decision.outcome = MeshRecoveryOutcome.Waiting;
                    decision.reason = $"map {input.mapId}: the store cannot be trusted; waiting for every cell's hello ({input.ceilingSeconds - input.elapsedSeconds:0} s left)";
                    return decision;
                }
                decision.outcome = MeshRecoveryOutcome.AdoptStatic;
                decision.isError = true;
                decision.reason = $"map {input.mapId}: the store could not be trusted and no cell holds a runtime layout after {input.ceilingSeconds:0} s; running its asset cut";
                return decision;
            }
            if (decision.waitingFor.Count > 0)
            {
                decision.outcome = atCeiling ? MeshRecoveryOutcome.Frozen : MeshRecoveryOutcome.Waiting;
                decision.isError = atCeiling;
                decision.reason = atCeiling
                    ? $"map {input.mapId}: no hello from {string.Join(", ", decision.waitingFor)} after {input.ceilingSeconds:0} s"
                    : $"map {input.mapId}: waiting for the hello of {string.Join(", ", decision.waitingFor)}";
                return decision;
            }
            decision.outcome = MeshRecoveryOutcome.AdoptStatic;
            decision.reason = $"map {input.mapId}: no runtime layout anywhere; running its asset cut";
            return decision;
        }
    }
}
