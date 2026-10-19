// Server Meshing add-on — the automatic policy's rig toggles (Slice 3, slices/slice3_design.md decision 15), applied to
// the runtime copy of the registry that MeshLayoutOverride.Resolve makes, so the central (the only reader of these
// settings) sees them without the asset changing. Pure: the parsing and the application take the environment as a
// function and report what they did.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>MeshPolicyEnv reads MESH_AUTO_OPS and the policy's numeric overrides into a registry's dynamic settings and maps.</summary>
    public static class MeshPolicyEnv
    {
        public const string ENV_AUTO_OPS = "MESH_AUTO_OPS";
        public const string ENV_SPLIT_UTIL = "MESH_SPLIT_UTIL";
        public const string ENV_SPLIT_DWELL = "MESH_SPLIT_DWELL";
        public const string ENV_MERGE_UTIL = "MESH_MERGE_UTIL";
        public const string ENV_MERGE_AFTER = "MESH_MERGE_AFTER";
        public const string ENV_OP_COOLDOWN = "MESH_OP_COOLDOWN";
        public const string ENV_SPLIT_BENEFIT = "MESH_SPLIT_BENEFIT";
        public const string ENV_MONSTER_WEIGHT = "MESH_MONSTER_WEIGHT";
        public const string ENV_PLANNER_WEIGHTS = "MESH_PLANNER_WEIGHTS";

        /// <summary>Every key this class reads.</summary>
        public static readonly string[] Keys =
        {
            ENV_AUTO_OPS, ENV_SPLIT_UTIL, ENV_SPLIT_DWELL, ENV_MERGE_UTIL, ENV_MERGE_AFTER, ENV_OP_COOLDOWN, ENV_SPLIT_BENEFIT, ENV_MONSTER_WEIGHT, ENV_PLANNER_WEIGHTS,
        };

        /// <summary>AnySet: at least one key has a non-blank value.</summary>
        public static bool AnySet(Func<string, string> getenv)
        {
            foreach (string key in Keys)
            {
                if (!string.IsNullOrWhiteSpace(getenv(key)))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Apply sets the settings and the Dynamic maps' autoSplit/autoMerge from the environment. <paramref name="applied"/>
        /// lists `key=value` for every value taken; <paramref name="warnings"/> every value that could not be read (ignored).
        /// </summary>
        public static void Apply(MeshDynamicSettings settings, IList<MeshMapLayout> maps, Func<string, string> getenv, List<string> applied, List<string> warnings)
        {
            string autoOps = getenv(ENV_AUTO_OPS);
            if (!string.IsNullOrWhiteSpace(autoOps))
            {
                if (TryParseAutoOps(autoOps, out bool split, out bool merge))
                {
                    int touched = 0;
                    for (int i = 0; maps != null && i < maps.Count; ++i)
                    {
                        MeshMapLayout layout = maps[i];
                        if (layout == null || !layout.IsDynamic)
                            continue;
                        layout.autoSplit = split;
                        layout.autoMerge = merge;
                        touched++;
                    }
                    applied.Add($"{ENV_AUTO_OPS}={autoOps.Trim()} ({touched} Dynamic map(s))");
                }
                else
                    warnings.Add($"{ENV_AUTO_OPS}={autoOps} is not split, merge or split,merge; ignored");
            }
            if (settings == null)
                return;
            Number(getenv, ENV_SPLIT_UTIL, v => settings.splitUtil = v, applied, warnings);
            Number(getenv, ENV_SPLIT_DWELL, v => settings.splitDwellSeconds = v, applied, warnings);
            Number(getenv, ENV_MERGE_UTIL, v => settings.mergeUtil = v, applied, warnings);
            Number(getenv, ENV_MERGE_AFTER, v => settings.mergeDwellSeconds = v, applied, warnings);
            Number(getenv, ENV_OP_COOLDOWN, v => settings.opCooldownSeconds = v, applied, warnings);
            Number(getenv, ENV_SPLIT_BENEFIT, v => settings.splitBenefitRatio = v, applied, warnings);
            Number(getenv, ENV_MONSTER_WEIGHT, v => { settings.ownedMonsterWeight = v; settings.puppetMonsterWeight = v; }, applied, warnings);
            string weights = getenv(ENV_PLANNER_WEIGHTS);
            if (!string.IsNullOrWhiteSpace(weights))
            {
                string[] parts = weights.Split(',');
                if (parts.Length == 4 && TryFloat(parts[0], out float puppetPlayer) && TryFloat(parts[1], out float ownedMonster)
                    && TryFloat(parts[2], out float puppetMonster) && TryFloat(parts[3], out float floor))
                {
                    settings.puppetPlayerWeight = puppetPlayer;
                    settings.ownedMonsterWeight = ownedMonster;
                    settings.puppetMonsterWeight = puppetMonster;
                    settings.modelFloorPlayers = floor;
                    applied.Add($"{ENV_PLANNER_WEIGHTS}={weights.Trim()}");
                }
                else
                    warnings.Add($"{ENV_PLANNER_WEIGHTS}={weights} is not <puppetPlayer>,<ownedMonster>,<puppetMonster>,<floor>; ignored");
            }
        }

        /// <summary>TryParseAutoOps reads `split`, `merge` or `split,merge` (any order, case-insensitive).</summary>
        public static bool TryParseAutoOps(string value, out bool split, out bool merge)
        {
            split = merge = false;
            if (string.IsNullOrWhiteSpace(value))
                return false;
            foreach (string part in value.Split(','))
            {
                string word = part.Trim();
                if (string.Equals(word, "split", StringComparison.OrdinalIgnoreCase))
                    split = true;
                else if (string.Equals(word, "merge", StringComparison.OrdinalIgnoreCase))
                    merge = true;
                else if (word.Length > 0)
                    return false;
            }
            return split || merge;
        }

        private static void Number(Func<string, string> getenv, string key, Action<float> set, List<string> applied, List<string> warnings)
        {
            string value = getenv(key);
            if (string.IsNullOrWhiteSpace(value))
                return;
            if (TryFloat(value, out float number) && number >= 0f)
            {
                set(number);
                applied.Add($"{key}={value.Trim()}");
            }
            else
                warnings.Add($"{key}={value} is not a non-negative number; ignored");
        }

        private static bool TryFloat(string text, out float value)
        {
            return float.TryParse((text ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
