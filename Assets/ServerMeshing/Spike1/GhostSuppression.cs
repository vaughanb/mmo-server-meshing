// Server Meshing add-on — Spike 1 (ghost simulation-suppression).
// See Docs/ServerMeshing/SPIKE1.md and Docs/ServerMeshing/DESIGN.md (§9 risk #2, Q9).
//
// EXTENSION-ONLY. The gating risk for P2/P3: a kit entity spawned as a ghost on a (server) cell will
// run its server logic (AI, movement, regen, buff ticking) — double-simulation. This is the mechanism
// that neuters it without core edits. It seeds the production MeshGhostController (DESIGN.md §10).
//
// Verified mechanism (BaseGameEntity.cs, CharacterRecoveryComponent.cs, MonsterActivityComponent.cs):
//   * Server logic ticks via a centralized UpdateManager (IManagedUpdate.ManagedUpdate()), gated on
//     Entity.IsServer — so component.enabled=false does NOT stop it. We must UpdateManager.Unregister.
//   * Movement has a built-in seam: BaseGameEntity.MovementDisableState (StateFlag; was MovementDisablers) ->
//     UpdateMovementEnabling -> Movement.enabled=false + StopMove(). No core edit.

using System.Collections.Generic;
using Insthync.ManagedUpdating;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public static class GhostSuppression
    {
        // Server-driver IManagedUpdate components to silence on a ghost (by type name, to avoid hard refs).
        public static readonly HashSet<string> DefaultSuppressTypeNames = new HashSet<string>
        {
            "MonsterActivityComponent",
            "MonsterActivityComponent2D",
            "BaseMonsterActivityComponent",
            "CharacterRecoveryComponent",
            "CharacterSkillAndBuffComponent",
            // Action components respond to commands a ghost never receives, but strip them too to be safe:
            "DefaultCharacterAttackComponent",
            "DefaultCharacterUseSkillComponent",
            "DefaultCharacterChargeComponent",
            "DefaultCharacterReloadComponent",
        };

        /// <summary>
        /// Turn a normally-spawned entity into a passive ghost: stop self-movement and silence the
        /// server-driver managed-update components. Returns how many components were silenced.
        /// </summary>
        /// <param name="entity">The spawned entity to ghostify.</param>
        /// <param name="disablerKey">Any object used as the movement-disabler key (e.g. the controller).</param>
        /// <param name="stripAllManagedUpdates">
        /// If true, silence EVERY IManagedUpdate on the entity except the entity itself (most thorough,
        /// useful for the spike's discovery; may also silence benign things like footstep sounds).
        /// </param>
        public static int Ghostify(BaseGameEntity entity, object disablerKey,
            IEnumerable<string> extraTypeNames = null, bool stripAllManagedUpdates = false, bool logDetails = true,
            bool disableMovement = true)
        {
            if (entity == null)
                return 0;

            // 1) Stop self-movement via the built-in seam (takes effect next EntityUpdate).
            //    A "puppet" ghost (P2) keeps its movement component and is steered toward snapshots
            //    with the kit's own movement API instead, so its model animates and syncs normally.
            if (disableMovement)
                entity.MovementDisableState.Add(disablerKey);

            // 2) Silence server-driver managed-update components.
            HashSet<string> deny = new HashSet<string>(DefaultSuppressTypeNames);
            if (extraTypeNames != null)
            {
                foreach (string n in extraTypeNames)
                    deny.Add(n);
            }

            int silenced = 0;
            IManagedUpdate[] managed = entity.GetComponentsInChildren<IManagedUpdate>(true);
            foreach (IManagedUpdate mu in managed)
            {
                // Keep the entity's own update: with movement disabled, EntityUpdate is benign
                // (mostly animation) and is desirable for the ghost's visual.
                if (mu is BaseGameEntity)
                    continue;

                string typeName = mu.GetType().Name;
                if (stripAllManagedUpdates || deny.Contains(typeName))
                {
                    UpdateManager.Unregister(mu);
                    silenced++;
                    if (logDetails)
                        Debug.Log($"[MeshGhost] silenced {typeName} on '{entity.name}'");
                }
            }
            return silenced;
        }

        /// <summary>
        /// Drive a ghost's transform from an external snapshot. SPIKE-level: sets the transform directly.
        /// Production ghosts feed the movement-sync path so neighbours/clients see it via normal AOI.
        /// </summary>
        public static void ApplyGhostSnapshot(BaseGameEntity entity, Vector3 position, Quaternion rotation)
        {
            if (entity == null)
                return;
            entity.transform.SetPositionAndRotation(position, rotation);
        }
    }
}
