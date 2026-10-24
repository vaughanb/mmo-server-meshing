// Server Meshing add-on — automated test client, the soak's walker (DESIGN.md 0.91, Tools/ServerMeshing/regress_soak.py).
//
// patrol: walk to (walkTargetX, walkTargetZ), then back and forth between x = walkTargetX and x = walkReturnX at that z
// until -meshObserveSeconds have passed, so a bot keeps crossing whatever cuts the automatic policy makes for hours.
// Every leg logs `Patrol leg <n> to x=<x>: reached|missed at <x>,<z>`; migrations are counted from the first leg as
// stand counts them (MIGRATION lines), and the RESULT reads `RESULT patrol: legs=<n> missed=<n>` plus the stand fields
// and ` relogins=<n>`. A leg missed while a re-login runs is not a failure of the walk: the bot carries on.

using System.Collections;
using System.Globalization;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class ServerMeshAutoClient
    {
        [Header("Patrol (soak)")]
        [Tooltip("How long one patrol leg may take before it is counted missed and the next one starts.")]
        public float patrolLegSeconds = 120f;

        /// <summary>PatrolLegLine is the line a patrol leg logs when it ends.</summary>
        public static string PatrolLegLine(int n, float targetX, bool reached, float x, float z)
        {
            return string.Format(CultureInfo.InvariantCulture, "Patrol leg {0} to x={1:0.##}: {2} at {3:0.##},{4:0.##}",
                n, Finite(targetX), reached ? "reached" : "missed", Finite(x), Finite(z));
        }

        /// <summary>PatrolResultHead is the start of a patrol RESULT, before the stand fields.</summary>
        public static string PatrolResultHead(int legs, int missed)
        {
            return string.Format(CultureInfo.InvariantCulture, "RESULT patrol: legs={0} missed={1}", System.Math.Max(0, legs), System.Math.Max(0, missed));
        }

        private IEnumerator RunPatrol(float deadline)
        {
            Phase = "patrol-start";
            bool reached = false;
            _walkZOverride = walkTargetZ;
            yield return WalkTo(walkTargetX, deadline, ClientMigrationController.CompletedCount, r => reached = r);
            if (!reached) { _walkZOverride = null; Fail("never reached the patrol start"); yield break; }
            BasePlayerCharacterEntity self = GameInstance.PlayingCharacterEntity;
            LogInvariant($"Patrolling between x={walkTargetX} and x={walkReturnX} at z={walkTargetZ} for {observeSeconds}s");
            BeginStandMigrations(self);
            bool patrolling = true;
            StartCoroutine(PollMigrationsWhile(() => patrolling));
            float end = Time.realtimeSinceStartup + observeSeconds;
            int legs = 0, missed = 0;
            float target = walkReturnX;
            while (Time.realtimeSinceStartup < end)
            {
                Phase = $"patrol-leg{legs + 1}";
                bool legReached = false;
                float legEnd = Mathf.Min(end, Time.realtimeSinceStartup + patrolLegSeconds);
                yield return WalkTo(target, legEnd, ClientMigrationController.CompletedCount, r => legReached = r);
                legs++;
                if (!legReached) missed++;
                self = GameInstance.PlayingCharacterEntity;
                Vector3 pos = self != null ? self.EntityTransform.position : Vector3.zero;
                Log(PatrolLegLine(legs, target, legReached, pos.x, pos.z));
                target = Mathf.Approximately(target, walkReturnX) ? walkTargetX : walkReturnX;
                yield return new WaitForSecondsRealtime(1f);
            }
            patrolling = false;
            _walkZOverride = null;
            PollStandMigrations();
            Finished = true;
            Passed = legs > missed;
            Log($"{PatrolResultHead(legs, missed)}{StandMigrationsSuffix()} -> {(Passed ? "PASS" : "FAIL")}");
        }

        /// <summary>Counts migrations every frame while a walk runs (WalkTo does not poll them).</summary>
        private IEnumerator PollMigrationsWhile(System.Func<bool> running)
        {
            while (running())
            {
                PollStandMigrations();
                yield return null;
            }
        }
    }
}
