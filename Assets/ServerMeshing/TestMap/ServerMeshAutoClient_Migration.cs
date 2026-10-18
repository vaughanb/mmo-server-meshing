// Server Meshing add-on — automated test client, Slice 2b additions (slices/slice2b_design.md decision 15).
//
// stand: from its "Standing at" line the bot counts the migrations a layout change gives it (a split moves the cell
// it stands in), logs each as `MIGRATION <n>: gap=<ms> ready=<True|False> jump=<m> pos=<x>,<z>` and adds
// ` migrations=<n> allReady=<bool> maxGap=<ms> maxJump=<m>` to its RESULT. -meshObserveSeconds sets the stand window;
// -meshLineX is the cut's x (stand logs which side of it the bot starts on).
// harvest with -meshHarvestAfterMigration 1: walk to the position, wait for a migration, then harvest a local node at
// x >= lineX + 50 until a yield lands in the bag and pick up an item drop there; the RESULT adds ` yield=<n> pickup=<bool>`
// and passes on both. Without the new arguments every mode behaves as before (stand only gains the RESULT fields).

using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class ServerMeshAutoClient
    {
        /// <summary>ARG_OBSERVE_SECONDS sets observeSeconds: the stand window, and how long the harvest variant waits for a migration.</summary>
        public const string ARG_OBSERVE_SECONDS = "-meshObserveSeconds";
        /// <summary>ARG_LINE_X is the x of the cut a split makes (scene frame).</summary>
        public const string ARG_LINE_X = "-meshLineX";
        /// <summary>ARG_HARVEST_AFTER_MIGRATION 1 makes harvest mode the after-migration variant.</summary>
        public const string ARG_HARVEST_AFTER_MIGRATION = "-meshHarvestAfterMigration";
        /// <summary>HarvestBeyondLine is how far past the line (x) the harvest variant's nodes and drops lie.</summary>
        public const float HarvestBeyondLine = 50f;

#if UNITY_EDITOR
        public const string PREF_OBSERVE_SECONDS = "ServerMesh.AutoClient.ObserveSeconds";
        public const string PREF_LINE_X = "ServerMesh.AutoClient.LineX";
        public const string PREF_HARVEST_AFTER_MIGRATION = "ServerMesh.AutoClient.HarvestAfterMigration";
#endif

        [Header("Split (Slice 2b)")]
        [Tooltip("x of the cut a split makes; stand logs which side it starts on, the harvest variant works beyond it.")]
        public float lineX = 0f;
        [Tooltip("Whether lineX was given.")]
        public bool hasLineX = false;
        [Tooltip("harvest mode: wait for a migration, then harvest and pick up beyond lineX + 50.")]
        public bool harvestAfterMigration = false;

        /// <summary>MigrationSettings is what the Slice 2b arguments set; a value that was absent or unreadable is not set.</summary>
        public struct MigrationSettings
        {
            public bool hasObserveSeconds;
            public float observeSeconds;
            public bool hasLineX;
            public float lineX;
            public bool harvestAfterMigration;
        }

        /// <summary>
        /// ParseMigrationSettings reads the argument values (null when absent): observe seconds a finite number above 0,
        /// line x a finite number, both invariant; harvest-after-migration "1" (anything else is off).
        /// </summary>
        public static MigrationSettings ParseMigrationSettings(string observeSeconds, string lineX, string harvestAfterMigration)
        {
            MigrationSettings settings = default;
            if (TryParseFinite(observeSeconds, out float observe) && observe > 0f)
            {
                settings.hasObserveSeconds = true;
                settings.observeSeconds = observe;
            }
            if (TryParseFinite(lineX, out float line))
            {
                settings.hasLineX = true;
                settings.lineX = line;
            }
            settings.harvestAfterMigration = harvestAfterMigration != null && harvestAfterMigration.Trim() == "1";
            return settings;
        }

        /// <summary>ParseMigrationArgs reads -meshObserveSeconds, -meshLineX and -meshHarvestAfterMigration from a command line.</summary>
        public static MigrationSettings ParseMigrationArgs(string[] args)
        {
            return ParseMigrationSettings(ReadArg(args, ARG_OBSERVE_SECONDS, null), ReadArg(args, ARG_LINE_X, null), ReadArg(args, ARG_HARVEST_AFTER_MIGRATION, null));
        }

        private static bool TryParseFinite(string text, out float value)
        {
            value = 0f;
            if (string.IsNullOrWhiteSpace(text))
                return false;
            return float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !float.IsNaN(value) && !float.IsInfinity(value);
        }

        /// <summary>The Slice 2b settings from the command line, or (editor, enabled through the menu prefs) from the prefs.</summary>
        private void ReadMigrationSettings(string[] args)
        {
            string observe = ReadArg(args, ARG_OBSERVE_SECONDS, null);
            string line = ReadArg(args, ARG_LINE_X, null);
            string harvest = ReadArg(args, ARG_HARVEST_AFTER_MIGRATION, null);
#if UNITY_EDITOR
            if (!args.Contains(ARG_ENABLE))
            {
                observe = observe ?? EditorPrefs.GetString(PREF_OBSERVE_SECONDS, "");
                line = line ?? EditorPrefs.GetString(PREF_LINE_X, "");
                harvest = harvest ?? EditorPrefs.GetString(PREF_HARVEST_AFTER_MIGRATION, "");
            }
#endif
            MigrationSettings settings = ParseMigrationSettings(observe, line, harvest);
            if (settings.hasObserveSeconds)
                observeSeconds = settings.observeSeconds;
            hasLineX = settings.hasLineX;
            if (settings.hasLineX)
                lineX = settings.lineX;
            harvestAfterMigration = settings.harvestAfterMigration;
        }

        /// <summary>MigrationLine is the line a bot logs for its n-th migration since it stood: gap ms, ready, jump m, x,z after.</summary>
        public static string MigrationLine(int n, long gapMs, bool ready, float jump, float x, float z)
        {
            return string.Format(CultureInfo.InvariantCulture, "MIGRATION {0}: gap={1} ready={2} jump={3:0.##} pos={4:0.##},{5:0.##}",
                n, gapMs, ready ? "True" : "False", Finite(jump), Finite(x), Finite(z));
        }

        /// <summary>MigrationSuffix is what a stand (or harvest-variant) RESULT gains, with a leading space.</summary>
        public static string MigrationSuffix(int migrations, bool allReady, long maxGapMs, float maxJump)
        {
            return string.Format(CultureInfo.InvariantCulture, " migrations={0} allReady={1} maxGap={2} maxJump={3:0.##}",
                System.Math.Max(0, migrations), allReady ? "True" : "False", System.Math.Max(0L, maxGapMs), Finite(maxJump));
        }

        /// <summary>HarvestSuffix is what the harvest variant's RESULT gains, with a leading space.</summary>
        public static string HarvestSuffix(int yieldAmount, bool pickup)
        {
            return string.Format(CultureInfo.InvariantCulture, " yield={0} pickup={1}", System.Math.Max(0, yieldAmount), pickup ? "True" : "False");
        }

        /// <summary>LineSideLine names the side of the line x a bot at x starts on: + from the line on (the half-open cut), - before it.</summary>
        public static string LineSideLine(float x, float lineX)
        {
            return string.Format(CultureInfo.InvariantCulture, "Line x={0:0.##}: starting on the {1} side at x={2:0.##}", Finite(lineX), x >= lineX ? "+" : "-", Finite(x));
        }

        private void LogInvariant(System.FormattableString text) => Log(System.FormattableString.Invariant(text));

        private static float Finite(float value) => float.IsNaN(value) || float.IsInfinity(value) ? 0f : value;

        // Migrations counted from the "Standing at" line.
        private bool _standTracking;
        private int _standSeen;
        private int _standMigrations;
        private bool _standAllReady = true;
        private long _standMaxGapMs;
        private float _standMaxJump;

        private void BeginStandMigrations(BasePlayerCharacterEntity self)
        {
            _standTracking = true;
            _standSeen = ClientMigrationController.CompletedCount;
            _standMigrations = 0;
            _standAllReady = true;
            _standMaxGapMs = 0;
            _standMaxJump = 0f;
            if (hasLineX && self != null)
                Log(LineSideLine(self.EntityTransform.position.x, lineX));
        }

        private void PollStandMigrations()
        {
            if (!_standTracking)
                return;
            int completed = ClientMigrationController.CompletedCount;
            if (completed <= _standSeen)
                return;
            _standMigrations += completed - _standSeen;
            _standSeen = completed;
            ClientMigrationController.Result r = ClientMigrationController.LastResult;
            _standAllReady &= r.playerReady;
            _standMaxGapMs = System.Math.Max(_standMaxGapMs, r.playerReadyMs);
            _standMaxJump = Mathf.Max(_standMaxJump, r.positionJump);
            Log(MigrationLine(_standMigrations, r.playerReadyMs, r.playerReady, r.positionJump, r.positionAfter.x, r.positionAfter.z));
        }

        private string StandMigrationsSuffix() => MigrationSuffix(_standMigrations, _standAllReady, _standMaxGapMs, _standMaxJump) + ReloginSuffix(Relogins); // Slice 2d: relogins

        /// <summary>The harvest variant: stand, wait for a migration, harvest a local node until a yield lands, pick up a drop.</summary>
        private IEnumerator RunHarvestAfterMigration(float deadline)
        {
            Phase = "harvest-stand";
            _walkZOverride = walkTargetZ;
            bool reached = false;
            yield return WalkTo(walkTargetX, deadline, ClientMigrationController.CompletedCount, r => reached = r);
            _walkZOverride = null;
            if (!reached) { Fail("never reached the harvest position"); yield break; }
            BasePlayerCharacterEntity self = GameInstance.PlayingCharacterEntity;
            float minX = lineX + HarvestBeyondLine;
            float waitEnd = Mathf.Max(deadline, Time.realtimeSinceStartup + observeSeconds);
            LogInvariant($"Standing at {(self != null ? self.EntityTransform.position : Vector3.zero)} party={(self != null ? self.PartyId : 0)}; waiting up to {waitEnd - Time.realtimeSinceStartup:0}s for a migration, then harvesting at x >= {minX:0.##}");
            BeginStandMigrations(self);
            while (_standMigrations == 0 && Time.realtimeSinceStartup < waitEnd)
            {
                PollStandMigrations();
                yield return null;
            }
            int nodesSeen = 0, attacks = 0, yieldAmount = 0;
            bool pickup = false;
            if (_standMigrations > 0)
            {
                yield return new WaitForSecondsRealtime(2f); // the new cell's entities arrive with the hop
                Phase = "harvest-after-migration";
                yield return HarvestLocal(minX, (seen, swings, gained) => { nodesSeen = seen; attacks = swings; yieldAmount = gained; });
                if (yieldAmount > 0)
                {
                    // The harvest leaves the node targeted and the swing loop's state behind; a pickup walk then sometimes
                    // never starts (two of ~13 runs on 2026-10-18: the bot stood 41 m from the drop for 60 s). Start clean.
                    BasePlayerCharacterEntity harvester = GameInstance.PlayingCharacterEntity;
                    if (harvester != null) { harvester.SetTargetEntity(null); harvester.StopMove(); }
                    yield return null;
                    Phase = "pickup";
                    yield return PickUpLocal(minX, picked => pickup = picked);
                }
            }
            else
                LogInvariant($"harvest: no migration within the wait");
            PollStandMigrations();
            Finished = true;
            Passed = yieldAmount > 0 && pickup;
            LogInvariant($"RESULT harvest: nodesSeen={nodesSeen} attacks={attacks}{HarvestSuffix(yieldAmount, pickup)}{StandMigrationsSuffix()} -> {(Passed ? "PASS" : "FAIL")}");
        }

        /// <summary>Swings at the nearest node at x >= minX until the bag gains items (the yield) or 120 s pass.</summary>
        private IEnumerator HarvestLocal(float minX, System.Action<int, int, int> onDone)
        {
            BasePlayerCharacterEntity self = GameInstance.PlayingCharacterEntity;
            int bagStart = CountBagItems(self), attacks = 0, gained = 0;
            HashSet<int> seen = new HashSet<int>();
            float end = Time.realtimeSinceStartup + 120f, nextAttack = 0f, nextLog = 0f;
            while (Time.realtimeSinceStartup < end)
            {
                PollStandMigrations();
                self = GameInstance.PlayingCharacterEntity;
                if (self == null) { yield return null; continue; }
                if (self.IsDead()) { Log("harvest: we died"); break; }
                gained = CountBagItems(self) - bagStart;
                if (gained > 0) break;
                HarvestableEntity target = null; float best = float.MaxValue;
                foreach (HarvestableEntity he in FindObjectsByType<HarvestableEntity>(FindObjectsSortMode.None))
                {
                    if (he == null || !he.gameObject.activeInHierarchy || he.IsDead() || he.EntityTransform.position.x < minX) continue;
                    if (seen.Add(he.GetInstanceID())) LogInvariant($"harvest: node seen at {he.EntityTransform.position} hp={he.CurrentHp}/{he.MaxHp}");
                    float d = Vector3.Distance(self.EntityTransform.position, he.EntityTransform.position);
                    if (d < best) { best = d; target = he; }
                }
                float now = Time.realtimeSinceStartup;
                float attackRange = Mathf.Max(1f, self.GetAttackDistance(false) - 0.3f) + 0.6f; // the node's collider is a metre wide
                if (target != null)
                {
                    if (best > attackRange)
                        self.PointClickMovement(target.EntityTransform.position);
                    else if (now >= nextAttack)
                    {
                        nextAttack = now + 0.6f;
                        self.StopMove();
                        Vector3 toTarget = target.EntityTransform.position - self.EntityTransform.position; toTarget.y = 0f;
                        if (toTarget.sqrMagnitude > 0.001f) self.SetLookRotation(Quaternion.LookRotation(toTarget.normalized), true);
                        self.SetTargetEntity(target);
                        bool isLeftHand = false;
                        self.AimPosition = self.GetAttackAimPosition(ref isLeftHand, target.EntityTransform.position + Vector3.up);
                        WeaponHandlingState whs = WeaponHandlingState.None;
                        self.Attack(ref whs);
                        attacks++;
                    }
                }
                if (now >= nextLog)
                {
                    nextLog = now + 5f;
                    LogInvariant($"harvest: node={(target != null ? target.EntityTransform.position.ToString() : "none at x >= " + minX.ToString("0.##", CultureInfo.InvariantCulture))} dist={(target != null ? best : -1f):0.#} attacks={attacks} bag+{gained} selfX={self.EntityTransform.position.x:0.#}");
                }
                yield return null;
            }
            onDone(seen.Count, attacks, System.Math.Max(0, gained));
        }

        /// <summary>Walks to the nearest item drop at x >= minX and asks to pick it up until the bag gains items or 60 s pass.</summary>
        private IEnumerator PickUpLocal(float minX, System.Action<bool> onDone)
        {
            HashSet<uint> seen = new HashSet<uint>();
            Dictionary<uint, float> retryAfter = new Dictionary<uint, float>();
            float end = Time.realtimeSinceStartup + 60f, nextLog = 0f, waitGainUntil = -1f;
            int bagBefore = 0;
            Vector3 stallFrom = Vector3.zero; float stallSince = -1f;
            while (Time.realtimeSinceStartup < end)
            {
                PollStandMigrations();
                BasePlayerCharacterEntity self = GameInstance.PlayingCharacterEntity;
                if (self == null) { yield return null; continue; }
                if (self.IsDead()) { Log("pickup: we died"); break; }
                float now = Time.realtimeSinceStartup;
                if (waitGainUntil >= 0f)
                {
                    if (CountBagItems(self) > bagBefore) { LogInvariant($"pickup: bag +{CountBagItems(self) - bagBefore}"); onDone(true); yield break; }
                    if (now < waitGainUntil) { yield return null; continue; }
                    waitGainUntil = -1f; // no gain: try the nearest drop again, or another
                }
                ItemDropEntity target = null; float best = float.MaxValue;
                foreach (ItemDropEntity drop in FindObjectsByType<ItemDropEntity>(FindObjectsSortMode.None))
                {
                    if (drop == null || !drop.gameObject.activeInHierarchy || drop.EntityTransform.position.x < minX) continue;
                    if (seen.Add(drop.ObjectId)) LogInvariant($"pickup: drop seen '{drop.EntityTitle}' at {drop.EntityTransform.position}");
                    if (retryAfter.TryGetValue(drop.ObjectId, out float after) && now < after) continue;
                    float d = Vector3.Distance(self.EntityTransform.position, drop.EntityTransform.position);
                    if (d < best) { best = d; target = drop; }
                }
                if (target != null)
                {
                    if (best > 2.2f)
                    {
                        // A walk that does not start (no progress for 3 s) is stopped and issued again.
                        Vector3 here = self.EntityTransform.position;
                        if (stallSince < 0f || (here - stallFrom).sqrMagnitude > 0.25f) { stallFrom = here; stallSince = now; }
                        else if (now - stallSince > 3f)
                        {
                            LogInvariant($"pickup: no progress for 3 s at {here}; walk issued again");
                            self.SetTargetEntity(null);
                            self.StopMove();
                            stallSince = now;
                            yield return null;
                            continue;
                        }
                        self.PointClickMovement(target.EntityTransform.position);
                    }
                    else
                    {
                        self.StopMove();
                        bagBefore = CountBagItems(self);
                        bool sent = self.CallCmdPickup(target.ObjectId);
                        LogInvariant($"pickup: requested on '{target.EntityTitle}' at {target.EntityTransform.position} sent={sent}");
                        retryAfter[target.ObjectId] = now + 5f;
                        waitGainUntil = now + 3f;
                    }
                }
                if (now >= nextLog)
                {
                    nextLog = now + 5f;
                    LogInvariant($"pickup: drop={(target != null ? target.EntityTransform.position.ToString() : "none at x >= " + minX.ToString("0.##", CultureInfo.InvariantCulture))} dist={(target != null ? best : -1f):0.#} seen={seen.Count} selfX={self.EntityTransform.position.x:0.#}");
                }
                yield return null;
            }
            onDone(false);
        }
    }
}
