// Server Meshing add-on — an aborted handoff whose client is already gone releases its entity (DESIGN.md 0.89).
//
// A handoff unregisters the player before it saves (MeshMapNetworkManager.TryHandoff), so the kit's disconnect handler
// no longer finds the character when the client leaves meanwhile. When the save then fails or the client is gone, the
// handoff is aborted; a client still here is registered again, but one that left -- it fell back, or its destination
// refused it -- left a spawned player entity that nobody owned, saved or destroyed. A merge's drain counted it as a
// character for ever ("C.1 still holds characters=1 ... nobody is kicked", splitcentral_merge 2026-10-21) and the merge
// never retired its cell. Such an entity is now saved (again, while the database connection comes back, when the
// handoff's own save failed) and destroyed, which is what the kit's disconnect would have done.
using Cysharp.Threading.Tasks;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class MeshMapNetworkManager
    {
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        /// <summary>
        /// The abort path's hook for a handoff whose client is gone: saves the entity (with the handoff's own save,
        /// asked again for up to arrivalLoadRetrySeconds while the database connection is down) unless that save
        /// already succeeded, then destroys it.
        /// </summary>
        private async UniTaskVoid ReleaseAbandonedHandoff(BasePlayerCharacterEntity entity, bool saved, System.Func<UniTask<bool>> save)
        {
            if (entity == null || entity.Identity == null || entity.Identity.IsDestroyed)
                return;
            string name = entity.CharacterName;
            float deadline = Time.unscaledTime + Mathf.Max(0f, arrivalLoadRetrySeconds);
            int attempts = 0;
            while (!saved && Time.unscaledTime < deadline)
            {
                while (DatabaseClientWatched && !IsDatabaseConnected && Time.unscaledTime < deadline)
                    await UniTask.Yield();
                if (entity == null || entity.Identity == null || entity.Identity.IsDestroyed)
                    return;
                attempts++;
                try
                {
                    saved = await save();
                }
                catch (System.Exception ex)
                {
                    Logging.LogException("Mesh", ex);
                }
                if (!saved)
                    await UniTask.Delay(2000, true);
            }
            if (entity == null || entity.Identity == null || entity.Identity.IsDestroyed)
                return;
            string how = saved ? (attempts > 0 ? $"saved after {attempts} more attempt(s)" : "saved") : $"NOT saved after {attempts} more attempt(s): progress since its last save is lost";
            Logging.LogWarning("Mesh", $"Handoff ABORTED with its client gone: {name} released here ({how})");
            entity.NetworkDestroy();
        }
#endif
    }
}
