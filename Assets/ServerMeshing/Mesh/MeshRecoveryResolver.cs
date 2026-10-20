// Server Meshing add-on — what the central's coordinator does with a Dynamic map when it (re)starts (DESIGN.md 0.72,
// plan D3; Slice 5, slices/slice5_design.md decisions 1-7). Pure. 2a adopted the asset cut when nothing dynamic existed
// anywhere and froze the map otherwise. 5 adopts a runtime layout: the newest consistent pair among the stored
// broadcast, the layouts the cells hold and a stored staged step its target applied; it waits for every registered map
// server and every dynamic id those layouts name; at the ceiling a missing dynamic id freezes its family, not the map.
// The stored operation is judged apart (ResolveOperation, MeshRecoveryResolver_Operation.cs): retired, resumed at its
// gate, re-sent or dropped.
using System;
using System.Collections.Generic;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>MeshRecoveryOutcome is what a map's recovery came to.</summary>
    public enum MeshRecoveryOutcome
    {
        /// <summary>Some registered cell or named dynamic id has not said hello yet (or the store cannot be trusted): answer nobody yet.</summary>
        Waiting,
        /// <summary>Nothing dynamic anywhere: the map runs its asset cut, and hellos are answered with it.</summary>
        AdoptStatic,
        /// <summary>A layout this build cannot rebuild safely, or cells missing at the ceiling: no answers, no operations.</summary>
        Frozen,
        /// <summary>A runtime layout (stored or held) was adopted and answers every hello (Slice 5).</summary>
        AdoptStored,
    }

    /// <summary>MeshRecoveryOutcomes: the outcomes under which a map is recovered and its hellos answered.</summary>
    public static class MeshRecoveryOutcomes
    {
        public static bool Answers(this MeshRecoveryOutcome outcome) => outcome == MeshRecoveryOutcome.AdoptStatic || outcome == MeshRecoveryOutcome.AdoptStored;
    }

    /// <summary>MeshHeldLayout is the layout one cell said it holds.</summary>
    public sealed class MeshHeldLayout
    {
        public string cellId;
        public MeshLayoutSnapshot snapshot;
    }

    /// <summary>MeshRecoveryInput is what the coordinator knows about one map while it recovers.</summary>
    public sealed class MeshRecoveryInput
    {
        public string mapId;
        /// <summary>Whether the map is Dynamic in the asset this central loaded.</summary>
        public bool mapDynamic;
        /// <summary>False when the store file could not be read or trusted.</summary>
        public bool storeTrusted = true;
        /// <summary>The stored broadcast (null when none, or untrusted).</summary>
        public MeshLayoutSnapshot storedBroadcast;
        /// <summary>The stored staged step (null when none): a candidate only when a cell holds its pair.</summary>
        public MeshLayoutSnapshot storedStaged;
        /// <summary>Whether the store holds an operation (an empty broadcast with one is still a runtime layout).</summary>
        public bool storedOperation;
        /// <summary>The layouts the cells that said hello hold (any map's cell: a Static neighbour carries the Dynamic maps' entries too).</summary>
        public readonly List<MeshHeldLayout> held = new List<MeshHeldLayout>();
        /// <summary>The Dynamic maps: only their entries make a snapshot a candidate.</summary>
        public readonly HashSet<string> dynamicMaps = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>Dynamic ids whose absence the stored operation expects (decision 1): never waited for.</summary>
        public readonly HashSet<string> excludedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Dynamic ids the stored operation retires or reverts anyway (decision 3): their absence at the ceiling freezes no family.</summary>
        public readonly HashSet<string> abortableIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Map servers of this map registered with the cluster.</summary>
        public readonly List<string> registeredCells = new List<string>();
        /// <summary>Cells of this map that said hello.</summary>
        public readonly HashSet<string> helloCells = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public float elapsedSeconds;
        public float ceilingSeconds = 60f;

        /// <summary>Entries of this map in the stored broadcast and staged snapshot (the Static-map text).</summary>
        public int StoredEntries => MeshRecoveryResolver.EntriesOf(storedBroadcast, mapId) + MeshRecoveryResolver.EntriesOf(storedStaged, mapId);
    }

    /// <summary>MeshFrozenFamily is a family frozen at the ceiling: its base and the dynamic ids that never said hello.</summary>
    public sealed class MeshFrozenFamily
    {
        public string baseId;
        public readonly List<string> missingIds = new List<string>();
        public string reason;
    }

    /// <summary>MeshRecoveryDecision is the resolver's answer.</summary>
    public sealed class MeshRecoveryDecision
    {
        public MeshRecoveryOutcome outcome;
        public string reason;
        /// <summary>Whether the decision is an error the operator must see.</summary>
        public bool isError;
        public readonly List<string> waitingFor = new List<string>();
        /// <summary>
        /// The newest layout among the candidates (AdoptStored; also an empty one adopted as the asset cut, so a later hello
        /// holding an older pair re-opens nothing), and where it came from ("the store" or "cell X"). Null with no candidate.
        /// </summary>
        public MeshLayoutSnapshot chosen;
        public string chosenSource = "";
        public readonly List<MeshFrozenFamily> frozenFamilies = new List<MeshFrozenFamily>();
    }

    /// <summary>MeshRecoveryResolver decides one map's recovery and the stored operation's fate.</summary>
    public static partial class MeshRecoveryResolver
    {
        public const string StoreSource = "the store";

        /// <summary>
        /// HeldReopensRecovery is whether a hello's held layout must re-open its map's recovery (decision 4): it holds
        /// entries of a Dynamic map from another coordinator incarnation, this central has broadcast nothing of its own yet,
        /// and either no runtime layout was adopted (<paramref name="adoptedRuntimePair"/> None) or the held pair is newer than
        /// the adopted one. A layout this coordinator sent itself never does (Slice 2b decision 5b).
        /// </summary>
        public static bool HeldReopensRecovery(int heldEntries, MeshLayoutPair held, long coordinatorIncarnation, MeshLayoutPair adoptedRuntimePair, bool ownBroadcastSent)
        {
            if (heldEntries <= 0 || held.coordinatorIncarnation == coordinatorIncarnation || ownBroadcastSent)
                return false;
            return adoptedRuntimePair.IsNone || held.IsNewerThan(adoptedRuntimePair);
        }

        /// <summary>EntriesOf counts a snapshot's entries of one map (0 for null).</summary>
        public static int EntriesOf(MeshLayoutSnapshot snapshot, string mapId)
        {
            int count = 0;
            if (snapshot != null && snapshot.entries != null)
            {
                foreach (MeshLayoutEntry entry in snapshot.entries)
                {
                    if (entry != null && entry.mapId == mapId)
                        count++;
                }
            }
            return count;
        }

        /// <summary>DynamicEntries counts a snapshot's entries on any of the Dynamic maps.</summary>
        public static int DynamicEntries(MeshLayoutSnapshot snapshot, ISet<string> dynamicMaps)
        {
            int count = 0;
            if (snapshot != null && snapshot.entries != null)
            {
                foreach (MeshLayoutEntry entry in snapshot.entries)
                {
                    if (entry != null && entry.mapId != null && dynamicMaps.Contains(entry.mapId))
                        count++;
                }
            }
            return count;
        }

        /// <summary>LiveDynamicIds adds the dynamic ids (a child, not a base) with a live entry (Pending, Active, Draining) of the map.</summary>
        public static void LiveDynamicIds(MeshLayoutSnapshot snapshot, string mapId, IDictionary<string, string> into)
        {
            if (snapshot == null || snapshot.entries == null)
                return;
            foreach (MeshLayoutEntry entry in snapshot.entries)
            {
                if (entry == null || entry.mapId != mapId || string.IsNullOrEmpty(entry.id) || entry.state == MeshCellState.Retired || entry.state == MeshCellState.Static)
                    continue;
                if (string.Equals(entry.id, entry.baseId, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!into.ContainsKey(entry.id))
                    into[entry.id] = entry.baseId ?? "";
            }
        }

        /// <summary>
        /// Candidates (decision 2): the stored broadcast (when there is one), every layout a cell holds (an empty one too: it
        /// says there was no runtime layout at that pair, which outranks an older cut a zombie still holds), and the stored
        /// staged step when some cell holds its pair. Each named by its source.
        /// </summary>
        public static List<(string source, MeshLayoutSnapshot snapshot)> Candidates(MeshRecoveryInput input)
        {
            List<(string, MeshLayoutSnapshot)> candidates = new List<(string, MeshLayoutSnapshot)>();
            if (input.storedBroadcast != null)
                candidates.Add((StoreSource, input.storedBroadcast));
            bool stagedHeld = false;
            foreach (MeshHeldLayout held in input.held)
            {
                if (held == null || held.snapshot == null || held.snapshot.pair.IsNone)
                    continue;
                if (input.storedStaged != null && held.snapshot.pair == input.storedStaged.pair)
                    stagedHeld = true;
                candidates.Add(("cell " + held.cellId, held.snapshot));
            }
            if (stagedHeld)
                candidates.Add((StoreSource + " (staged)", input.storedStaged));
            return candidates;
        }

        /// <summary>
        /// Choose picks the newest pair among the candidates (the store named first on a tie); <paramref name="conflict"/>
        /// names two candidates at that pair whose entries differ, which freezes the map.
        /// </summary>
        public static bool Choose(List<(string source, MeshLayoutSnapshot snapshot)> candidates, out string source, out MeshLayoutSnapshot chosen, out string conflict)
        {
            source = null;
            chosen = null;
            conflict = null;
            foreach ((string s, MeshLayoutSnapshot snapshot) in candidates)
            {
                if (chosen == null || snapshot.pair.IsNewerThan(chosen.pair))
                {
                    chosen = snapshot;
                    source = s;
                }
            }
            if (chosen == null)
                return false;
            foreach ((string s, MeshLayoutSnapshot snapshot) in candidates)
            {
                if (snapshot.pair == chosen.pair && !MeshLayoutSnapshot.SameEntries(snapshot, chosen))
                {
                    conflict = $"{source} and {s} hold different layouts at {chosen.pair}";
                    return false;
                }
            }
            return true;
        }

        public static MeshRecoveryDecision Resolve(MeshRecoveryInput input)
        {
            MeshRecoveryDecision decision = new MeshRecoveryDecision();
            if (!input.mapDynamic)
            {
                int stored = input.StoredEntries;
                decision.outcome = MeshRecoveryOutcome.AdoptStatic;
                decision.reason = stored > 0 ? $"map {input.mapId} is Static now; {stored} stored dynamic entries ignored" : $"map {input.mapId} is Static";
                return decision;
            }
            List<(string source, MeshLayoutSnapshot snapshot)> candidates = Candidates(input);
            if (!Choose(candidates, out string source, out MeshLayoutSnapshot chosen, out string conflict) && conflict != null)
            {
                decision.outcome = MeshRecoveryOutcome.Frozen;
                decision.isError = true;
                decision.reason = $"map {input.mapId} frozen: {conflict}";
                return decision;
            }
            // The dynamic ids named for this map by any candidate, each with its base; the operation's expected absentee left out.
            Dictionary<string, string> named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach ((string _, MeshLayoutSnapshot snapshot) in candidates)
                LiveDynamicIds(snapshot, input.mapId, named);
            if (input.storedStaged != null)
                LiveDynamicIds(input.storedStaged, input.mapId, named);
            foreach (string excluded in input.excludedIds)
                named.Remove(excluded);
            List<string> missingRegistered = new List<string>(), missingNamed = new List<string>();
            foreach (string cell in input.registeredCells)
            {
                if (!input.helloCells.Contains(cell))
                    missingRegistered.Add(cell);
            }
            foreach (string id in named.Keys)
            {
                if (!input.helloCells.Contains(id) && !missingRegistered.Contains(id) && !input.registeredCells.Contains(id))
                    missingNamed.Add(id);
            }
            decision.waitingFor.AddRange(missingRegistered);
            decision.waitingFor.AddRange(missingNamed);
            bool atCeiling = input.elapsedSeconds >= input.ceilingSeconds;
            if (!atCeiling)
            {
                if (!input.storeTrusted && candidates.Count == 0)
                {
                    decision.outcome = MeshRecoveryOutcome.Waiting;
                    decision.reason = $"map {input.mapId}: the store cannot be trusted; waiting for every cell's hello ({input.ceilingSeconds - input.elapsedSeconds:0} s left)";
                    return decision;
                }
                if (decision.waitingFor.Count > 0)
                {
                    decision.outcome = MeshRecoveryOutcome.Waiting;
                    decision.reason = $"map {input.mapId}: waiting for the hello of {string.Join(", ", decision.waitingFor)}";
                    return decision;
                }
            }
            else if (missingRegistered.Count > 0)
            {
                decision.outcome = MeshRecoveryOutcome.Frozen;
                decision.isError = true;
                decision.reason = $"map {input.mapId}: no hello from {string.Join(", ", missingRegistered)} after {input.ceilingSeconds:0} s";
                return decision;
            }
            else
            {
                foreach (string id in missingNamed)
                {
                    if (input.abortableIds.Contains(id))
                        continue;   // the operation's verdict retires or reverts it: no family to freeze
                    string baseId = named[id];
                    MeshFrozenFamily family = decision.frozenFamilies.Find(f => string.Equals(f.baseId, baseId, StringComparison.OrdinalIgnoreCase));
                    if (family == null)
                    {
                        family = new MeshFrozenFamily { baseId = baseId };
                        decision.frozenFamilies.Add(family);
                    }
                    family.missingIds.Add(id);
                }
                foreach (MeshFrozenFamily family in decision.frozenFamilies)
                    family.reason = $"no hello from {string.Join(", ", family.missingIds)} after {input.ceilingSeconds:0} s";
            }
            bool runtime = chosen != null && (chosen.entries.Count > 0 || input.storedOperation);
            if (!runtime)
            {
                decision.outcome = MeshRecoveryOutcome.AdoptStatic;
                decision.chosen = chosen;
                decision.chosenSource = source ?? "";
                if (!input.storeTrusted)
                {
                    decision.isError = true;
                    decision.reason = $"map {input.mapId}: the store could not be trusted and no cell holds a runtime layout after {input.ceilingSeconds:0} s; running its asset cut";
                }
                else
                    decision.reason = $"map {input.mapId}: no runtime layout anywhere; running its asset cut";
                return decision;
            }
            decision.outcome = MeshRecoveryOutcome.AdoptStored;
            decision.chosen = chosen;
            decision.chosenSource = source;
            decision.isError = decision.frozenFamilies.Count > 0;
            decision.reason = $"map {input.mapId} adopted layout {chosen.pair} from {source} with {EntriesOf(chosen, input.mapId)} entries ({IdsOf(chosen, input.mapId)})";
            return decision;
        }

        /// <summary>IdsOf lists a snapshot's entry ids of one map, in order ("none" when there are none).</summary>
        public static string IdsOf(MeshLayoutSnapshot snapshot, string mapId)
        {
            List<string> ids = new List<string>();
            if (snapshot != null && snapshot.entries != null)
            {
                foreach (MeshLayoutEntry entry in snapshot.entries)
                {
                    if (entry != null && entry.mapId == mapId)
                        ids.Add(entry.id);
                }
            }
            return ids.Count > 0 ? string.Join(", ", ids) : "none";
        }
    }
}
