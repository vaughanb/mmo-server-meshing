// Server Meshing add-on — a slow handoff save is made again at the player's position (DESIGN.md 0.91).
//
// A handoff saves the character at the crossing (MeshMapNetworkManager.MeshHandoff) and the destination spawns it from
// that row, but the client keeps moving the entity here until the save returns. A save is a few milliseconds; when the
// database is slow or reconnecting (the central restarted: it hosts the database manager) it took 8.8 s in the soak,
// the patrolling bot walked 25 m into the neighbour meanwhile, and the destination spawned it 25 m back (the client's
// migration `jump=25.54m`, and the departing cell's demoted puppet teleported 25 m to its owner). A save that took over
// SlowSaveMs is now made again with the position the player has reached, while it moved handoffResaveMeters or more
// since the last saved one, up to handoffResaveAttempts times: the database answered the slow save, so the next is fast.
// A failed re-save keeps the earlier row (a consistent one); a client gone during it ends the handoff as the abort does.
using Cysharp.Threading.Tasks;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class MeshMapNetworkManager
    {
        /// <summary>A handoff save slower than this (ms) may have let the player move on: it is checked for a re-save.</summary>
        public const long SlowSaveMs = 500;

        [Tooltip("A handoff save slower than 0.5 s (a slow or reconnecting database) is made again at the player's current position when it moved at least this far (m) meanwhile, so the destination spawns it where it is rather than where it crossed. 0 = never.")]
        public float handoffResaveMeters = 2f;
        [Tooltip("How many times one handoff's save is made again at most (each after a save slower than 0.5 s).")]
        public int handoffResaveAttempts = 3;

        /// <summary>ShouldResave: the last save was slow, re-saving is on, the player moved far enough and attempts are left.</summary>
        public static bool ShouldResave(long lastSaveMs, float movedMeters, float resaveMeters, int resaves, int maxResaves)
        {
            return lastSaveMs > SlowSaveMs && resaveMeters > 0f && movedMeters >= resaveMeters && resaves < maxResaves;
        }

        /// <summary>SlowSaveLine is the warning a slow handoff save logs, with what the re-saves did.</summary>
        public static string SlowSaveLine(string name, long firstMs, int resaves, float movedMeters, long lastMs, bool failed)
        {
            if (resaves <= 0)
                return $"Handoff save for {name} took {firstMs} ms; the saved position may lag the client";
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "Handoff save for {0} took {1} ms; saved again {2} time(s) at the player's position, {3:0.0} m on, the last in {4} ms{5}",
                name, firstMs, resaves, movedMeters, lastMs, failed ? " (the last re-save failed: the row keeps the one before)" : string.Empty);
        }

#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        /// <summary>
        /// MeshHandoff's hook after its save succeeded: when that save was slow, saves again at the player's position (led
        /// by the handoff's velocity, in the destination's frame) while the player keeps moving away from the saved one.
        /// False when the handoff must end here: the client left or the entity was destroyed during a re-save; the entity
        /// is then released as the abort path does and a retry is scheduled.
        /// </summary>
        private async UniTask<bool> ResaveAfterSlowSave(BasePlayerCharacterEntity entity, long connectionId, string characterId, string mapName,
            Vector3 savePosition, Vector3 frameOffset, Vector3 velocity, System.Diagnostics.Stopwatch sw)
        {
            long firstMs = sw.ElapsedMilliseconds, lastMs = firstMs;
            if (firstMs <= SlowSaveMs)
                return true;
            int resaves = 0;
            float moved = 0f;
            bool failed = false;
            while (true)
            {
                Vector3 target = entity.EntityTransform.position + velocity * Mathf.Max(0f, handoffLeadSeconds) + frameOffset;
                float distance = Vector3.Distance(target, savePosition);
                if (!ShouldResave(lastMs, distance, handoffResaveMeters, resaves, handoffResaveAttempts))
                    break;
                long t0 = sw.ElapsedMilliseconds;
                bool saved = false;
                try
                {
                    saved = await WaitAndSaveCharacter(TransactionUpdateCharacterState.All, entity.CloneTo(new PlayerCharacterData()), true, mapName, target, true, entity.CurrentRotation);
                }
                catch (System.Exception ex)
                {
                    Logging.LogException("Mesh", ex);
                }
                resaves++;
                lastMs = sw.ElapsedMilliseconds - t0;
                if (entity == null || entity.Identity == null || entity.Identity.IsDestroyed || !ContainsConnectionId(connectionId))
                {
                    Logging.LogWarning("Mesh", $"Handoff ABORTED {entity?.CharacterName} during a re-save of its slow save: its client is gone");
                    ReleaseAbandonedHandoff(entity, true, null).Forget();
                    _handoffRetryAfter[characterId] = Time.unscaledTime + handoffRetrySeconds;
                    return false;
                }
                if (!saved)
                {
                    failed = true;
                    break;
                }
                moved += distance;
                savePosition = target;
            }
            Logging.LogWarning("Mesh", SlowSaveLine(entity.CharacterName, firstMs, resaves, moved, lastMs, failed));
            return true;
        }
#endif
    }
}
