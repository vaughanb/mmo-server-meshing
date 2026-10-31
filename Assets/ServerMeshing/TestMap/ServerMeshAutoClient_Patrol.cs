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
        [Tooltip("Swing at the air whenever the character can while it patrols, so crossings happen mid-attack (DESIGN 1.06; trace with MESH_TRACE_ACTIONS=1).")]
        public bool attackWhileWalking = false;
        [Tooltip("With attackWhileWalking: cut the pause between swings short when a hop starts, so a swing is sent inside the gap between the warp and the adoption (DESIGN 1.07).")]
        public bool swingInGap = false;
        [Tooltip("With attackWhileWalking: turn to face the nearest live monster within this many metres before each swing (0: swing straight ahead).")]
        public float swingFaceRange = 0f;
        [Tooltip("Swing in the frame of each warp, just before the old connection closes: the call is queued on a connection about to stop (DESIGN 1.15).")]
        public bool swingAtWarp = false;
        [Tooltip("With attackWhileWalking or swingAtWarp: cast this skill (data id; it must be learned) instead of a plain attack, so a skill is in flight at crossings (DESIGN 1.21).")]
        public int castSkillWhileWalking = 0;
        [Tooltip("Charge the weapon for this many seconds at a time while patrolling, with a second between, so a charge is in progress at crossings (DESIGN 1.35; 0: never).")]
        public float chargeWhileWalkingSeconds = 0f;
        private int _swings, _gapSwings, _facedSwings, _warpSwings;

        /// <summary>Draws the weapon again whenever the demo puts it away, while <paramref name="running"/>.</summary>
        private IEnumerator KeepDrawnWhile(System.Func<bool> running)
        {
            while (running())
            {
                BasePlayerCharacterEntity self = GameInstance.PlayingCharacterEntity;
                if (self != null && self.IsWeaponsSheathed && self.IsOwnerClient)
                    self.IsWeaponsSheathed = false;
                yield return null;
            }
        }

        /// <summary>The hop is about to close the old connection: swing now, so the call sits in its queue.</summary>
        private void SwingAtWarp()
        {
            BasePlayerCharacterEntity self = GameInstance.PlayingCharacterEntity;
            if (self == null || self.IsDead())
                return;
            WeaponHandlingState whs = WeaponHandlingState.None;
            if (castSkillWhileWalking != 0 ? CastWhileWalking(self, swingFaceRange > 0f && FaceNearestMonster(self, swingFaceRange)) : self.Attack(ref whs))
            {
                _swings++;
                _warpSwings++;
                Log($"swing sent at the warp ({_warpSwings})");
            }
        }

        private static bool IsHopping => BaseGameNetworkManager.Singleton is MeshMapNetworkManager mesh && mesh.IsClientHopping;

        /// <summary>Charges for chargeWhileWalkingSeconds, rests a second, and again, while <paramref name="running"/>.</summary>
        private IEnumerator ChargeWhile(System.Func<bool> running)
        {
            while (running())
            {
                BasePlayerCharacterEntity self = GameInstance.PlayingCharacterEntity;
                if (self != null && !self.IsDead())
                {
                    self.ChargeComponent.StartCharge(false);
                    yield return new WaitForSecondsRealtime(chargeWhileWalkingSeconds);
                    self = GameInstance.PlayingCharacterEntity;
                    if (self != null)
                        self.ChargeComponent.StopCharge();
                }
                yield return new WaitForSecondsRealtime(1f);
            }
        }

        /// <summary>
        /// Swings while <paramref name="running"/>, walking a random 0.6-1.4 s after each swing ends: a swing stops the walk
        /// (the demo's weapons hold the character still), so back-to-back swings would pin it short of the edge. Spaced,
        /// crossings land at every phase of a swing. Counts the swings started, those started during a hop, and those
        /// aimed at a monster.
        /// </summary>
        private IEnumerator SwingWhile(System.Func<bool> running)
        {
            System.Random random = new System.Random(1);
            while (running())
            {
                BasePlayerCharacterEntity self = GameInstance.PlayingCharacterEntity;
                if (self != null && !self.IsDead())
                {
                    // The demo starts characters sheathed and its controller draws before the first swing; the owner sets the
                    // synced flag (ClientMulticast), and the demo's sheathing component vetoes swings until the hand closes.
                    if (self.IsWeaponsSheathed && self.IsOwnerClient)
                        self.IsWeaponsSheathed = false;
                    bool faced = swingFaceRange > 0f && FaceNearestMonster(self, swingFaceRange);
                    bool hopping = IsHopping;
                    WeaponHandlingState whs = WeaponHandlingState.None;
                    if (castSkillWhileWalking != 0 ? CastWhileWalking(self, faced) : self.Attack(ref whs))
                    {
                        _swings++;
                        if (hopping) _gapSwings++;
                        if (faced) _facedSwings++;
                        if (hopping)
                            Log($"swing sent during a hop ({_gapSwings})");
                        while (running() && GameInstance.PlayingCharacterEntity != null && (GameInstance.PlayingCharacterEntity.IsAttacking || GameInstance.PlayingCharacterEntity.IsUsingSkill))
                            yield return null;
                        float resume = Time.realtimeSinceStartup + 0.6f + (float)random.NextDouble() * 0.8f;
                        bool wasHopping = IsHopping;
                        while (running() && Time.realtimeSinceStartup < resume)
                        {
                            bool nowHopping = IsHopping;
                            if (swingInGap && nowHopping && !wasHopping)
                                break; // a hop just started: swing inside its gap
                            wasHopping = nowHopping;
                            yield return null;
                        }
                        continue;
                    }
                }
                yield return null;
            }
        }

        /// <summary>Casts castSkillWhileWalking at the faced monster, or ahead of <paramref name="self"/>; false when the kit refuses it.</summary>
        private bool CastWhileWalking(BasePlayerCharacterEntity self, bool faced)
        {
            BaseGameEntity target = faced ? self.GetTargetEntity() : null;
            Vector3 aim = target != null ? target.EntityTransform.position : self.EntityTransform.position + self.EntityTransform.forward * 2f;
            bool sent = self.UseSkill(castSkillWhileWalking, WeaponHandlingState.None, target != null ? target.ObjectId : 0, AimPosition.CreatePosition(aim));
            if (sent)
                Log($"cast {castSkillWhileWalking}{(faced ? " at a monster" : string.Empty)}{(IsHopping ? " during a hop" : string.Empty)}");
            return sent;
        }

        /// <summary>Turns <paramref name="self"/> to the nearest live monster within <paramref name="range"/> metres; false when there is none.</summary>
        private static bool FaceNearestMonster(BasePlayerCharacterEntity self, float range)
        {
            Vector3 position = self.EntityTransform.position;
            BaseMonsterCharacterEntity nearest = null;
            float best = range * range;
            foreach (BaseMonsterCharacterEntity monster in FindObjectsByType<BaseMonsterCharacterEntity>(FindObjectsSortMode.None))
            {
                if (monster == null || monster.IsDead())
                    continue;
                Vector3 offset = monster.EntityTransform.position - position;
                offset.y = 0f;
                if (offset.sqrMagnitude < best)
                {
                    best = offset.sqrMagnitude;
                    nearest = monster;
                }
            }
            if (nearest == null)
                return false;
            Vector3 toTarget = nearest.EntityTransform.position - position;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude > 0.0001f)
                self.SetLookRotation(Quaternion.LookRotation(toTarget.normalized), true);
            self.SetTargetEntity(nearest);
            return true;
        }

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
            if (attackWhileWalking)
                StartCoroutine(SwingWhile(() => patrolling));
            if (chargeWhileWalkingSeconds > 0f)
                StartCoroutine(ChargeWhile(() => patrolling));
            MeshMapNetworkManager warpMesh = swingAtWarp ? BaseGameNetworkManager.Singleton as MeshMapNetworkManager : null;
            if (warpMesh != null)
            {
                warpMesh.ClientConnectionClosing += SwingAtWarp;
                StartCoroutine(KeepDrawnWhile(() => patrolling)); // a sheathed character's swing is refused, and the demo sheathes an idle one
            }
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
            if (warpMesh != null)
                warpMesh.ClientConnectionClosing -= SwingAtWarp;
            _walkZOverride = null;
            PollStandMigrations();
            Finished = true;
            Passed = legs > missed;
            Log($"{PatrolResultHead(legs, missed)}{StandMigrationsSuffix()}{(attackWhileWalking || swingAtWarp ? $" swings={_swings} gapSwings={_gapSwings} facedSwings={_facedSwings} warpSwings={_warpSwings}" : "")} -> {(Passed ? "PASS" : "FAIL")}");
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
