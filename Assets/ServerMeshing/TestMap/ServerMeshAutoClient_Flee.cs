// Server Meshing add-on — automated test client: run from a monster (DESIGN.md 1.28).
//
// flee: walk to (walkTargetX, walkTargetZ), stand fleeWaitSeconds to be noticed, then run in a straight line to
// (walkReturnX, walkTargetZ) at a normal run or, with fleeSprint, a sprint. Every half second it logs the nearest live
// monster's distance and whether that monster targets this character (known only while the monster is on this client's
// cell: a puppet's target is not synced), and the RESULT reads the gap at the end and the largest one on the way:
// `RESULT flee: sprint=<bool> ran=<m> s=<s> nearest=<m> maxGap=<m> chasedAtEnd=<bool>`.
using System.Collections;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class ServerMeshAutoClient
    {
        [Header("Flee")]
        [Tooltip("flee: seconds to stand at the start so a monster notices the character before it runs.")]
        public float fleeWaitSeconds = 4f;
        [Tooltip("flee: run at a sprint instead of a normal run.")]
        public bool fleeSprint = false;
        [Tooltip("flee: seconds to stand at the end of the run, watching the monster.")]
        public float fleeWatchSeconds = 6f;

        private IEnumerator RunFlee(float deadline)
        {
            Phase = "flee";
            bool reached = false;
            _walkZOverride = walkTargetZ;
            yield return WalkTo(walkTargetX, deadline, ClientMigrationController.CompletedCount, r => reached = r);
            float waitEnd = Time.realtimeSinceStartup + fleeWaitSeconds;
            while (Time.realtimeSinceStartup < waitEnd)
                yield return null;
            BasePlayerCharacterEntity self = GameInstance.PlayingCharacterEntity;
            if (self == null)
            {
                _walkZOverride = null;
                Finished = true; Passed = false;
                Log("RESULT flee: no character -> FAIL");
                yield break;
            }
            Vector3 start = self.EntityTransform.position;
            Vector3 end = new Vector3(walkReturnX, start.y, walkTargetZ);
            float t0 = Time.realtimeSinceStartup, nextLog = 0f, maxGap = 0f, lastNearest = -1f;
            bool chased = false;
            Log($"flee: {(fleeSprint ? "sprinting" : "running")} from {start} to {end}; nearest monster {NearestMonster(self, out bool startTargeted):0.0} m targets me {startTargeted}");
            while (Time.realtimeSinceStartup < deadline)
            {
                self = GameInstance.PlayingCharacterEntity;
                if (self == null || self.IsDead())
                    break;
                Vector3 pos = self.EntityTransform.position;
                Vector3 left = end - pos;
                left.y = 0f;
                if (left.magnitude < 0.6f)
                    break;
                self.SetExtraMovementState(fleeSprint ? ExtraMovementState.IsSprinting : ExtraMovementState.None);
                self.PointClickMovement(end);
                if (Time.realtimeSinceStartup >= nextLog)
                {
                    nextLog = Time.realtimeSinceStartup + 0.5f;
                    lastNearest = NearestMonster(self, out chased);
                    if (lastNearest > maxGap && lastNearest < 60f)
                        maxGap = lastNearest;
                    Log($"flee: at x={pos.x:0.0} speed {self.GetMoveSpeed():0.0} m/s extra={self.ExtraMovementState} stamina={self.CurrentStamina:0} nearest monster {lastNearest:0.0} m targets me {chased}");
                }
                yield return null;
            }
            float ran = Time.realtimeSinceStartup - t0;
            float watchEnd = Time.realtimeSinceStartup + fleeWatchSeconds;
            self = GameInstance.PlayingCharacterEntity;
            if (self != null)
                self.SetExtraMovementState(ExtraMovementState.None);
            while (Time.realtimeSinceStartup < watchEnd)
            {
                self = GameInstance.PlayingCharacterEntity;
                if (self != null && Time.realtimeSinceStartup >= nextLog)
                {
                    nextLog = Time.realtimeSinceStartup + 0.5f;
                    lastNearest = NearestMonster(self, out chased);
                    Log($"flee: stopped; nearest monster {lastNearest:0.0} m targets me {chased}");
                }
                yield return null;
            }
            _walkZOverride = null;
            self = GameInstance.PlayingCharacterEntity;
            Finished = true; Passed = reached && self != null;
            Log(System.FormattableString.Invariant($"RESULT flee: sprint={fleeSprint} ran={(self != null ? Vector3.Distance(start, self.EntityTransform.position) : 0f):0.0} s={ran:0.0} nearest={lastNearest:0.0} maxGap={maxGap:0.0} chasedAtEnd={chased} hp={(self != null ? self.CurrentHp : 0)}{StandMigrationsSuffix()} -> {(Passed ? "PASS" : "FAIL")}"));
        }

        /// <summary>The nearest live monster's flat distance (or -1), and whether it targets <paramref name="self"/>.</summary>
        private float NearestMonster(BasePlayerCharacterEntity self, out bool targetsMe)
        {
            targetsMe = false;
            float best = -1f;
            BaseMonsterCharacterEntity nearest = null;
            BaseGameNetworkManager manager = BaseGameNetworkManager.Singleton;
            if (self == null || manager == null || manager.Assets == null)
                return best;
            System.Collections.Generic.Dictionary<uint, LiteNetLibIdentity>.Enumerator it = manager.Assets.GetSpawnedObjects();
            while (it.MoveNext())
            {
                LiteNetLibIdentity identity = it.Current.Value;
                if (identity == null || !identity.TryGetComponent(out BaseMonsterCharacterEntity monster) || monster.IsDead() || monster.IsSummoned)
                    continue;
                Vector3 d = monster.EntityTransform.position - self.EntityTransform.position;
                d.y = 0f;
                if (best < 0f || d.magnitude < best)
                {
                    best = d.magnitude;
                    nearest = monster;
                }
            }
            if (nearest != null)
                targetsMe = ReferenceEquals(nearest.GetTargetEntity(), self);
            return best;
        }
    }
}
