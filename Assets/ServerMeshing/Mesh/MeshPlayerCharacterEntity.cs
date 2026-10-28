// Server Meshing add-on — MeshPlayerCharacterEntity (DESIGN.md §12, loot across the edge).
//
// A drop lying just across the line is a puppet here (see MeshMapNetworkManager_Ghosting: drop ghosts). Picking it
// up must not run the kit's local pickup (the puppet holds a copy of the item): the request goes to the owner cell,
// which validates it against this character's ghost over there and sends the items back to this cell.
// Add-on only: the demo player prefab uses this class instead of PlayerCharacterEntity.
using LiteNetLibManager;

namespace MultiplayerARPG.MMO.Mesh
{
    public class MeshPlayerCharacterEntity : PlayerCharacterEntity
    {
        // The ownership the owner / non-owner objects (HUD, nameplate, controller binding) were last built for.
        private bool? _ownerObjectsBuiltFor;

        /// <summary>
        /// A character a migrating client kept and the new cell's spawn adopted (CE-14) keeps the objects the kit builds
        /// for it when its ownership did not change: rebuilding the HUD, nameplate and controller binding would show the
        /// cell swap to the player.
        /// </summary>
        protected override void EntityOnSetOwnerClient(bool isOwnerClient)
        {
            if (Identity != null && Identity.IsBeingAdopted && _ownerObjectsBuiltFor == isOwnerClient)
                return;
            _ownerObjectsBuiltFor = isOwnerClient;
            base.EntityOnSetOwnerClient(isOwnerClient);
        }

        /// <summary>A puppet must not take buffs/debuffs/heals locally: forward them to the owner of the real character.</summary>
        public override void ApplyBuff(int dataId, BuffType type, int level, EntityInfo buffApplier, CharacterItem buffApplierWeapon)
        {
            if (IsServer && MeshIdentity.IsGhostEntity(this))
            {
                MeshMapNetworkManager mesh = BaseGameNetworkManager.Singleton as MeshMapNetworkManager;
                if (mesh != null && mesh.ForwardGhostAction(this, GhostActionKind.ApplyBuff, buffApplier,
                        writer => MeshMapNetworkManager.WriteApplyBuffPayload(writer, dataId, type, level, buffApplierWeapon)))
                    return;
            }
            base.ApplyBuff(dataId, type, level, buffApplier, buffApplierWeapon);
        }

        [ServerRpc]
        protected override void CmdPickup(uint objectId)
        {
#if UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES
            if (Manager.TryGetEntityByObjectId(objectId, out ItemDropEntity drop) && MeshIdentity.IsGhostEntity(drop))
            {
                if (!CanPickup())
                    return;
                if (!IsGameEntityInDistance(drop))
                {
                    GameInstance.ServerGameMessageHandlers.SendGameMessage(ConnectionId, UITextKeys.UI_ERROR_CHARACTER_IS_TOO_FAR);
                    return;
                }
                MeshMapNetworkManager mesh = BaseGameNetworkManager.Singleton as MeshMapNetworkManager;
                if (mesh != null)
                    mesh.RequestGhostPickup(this, drop);
                else
                    GameInstance.ServerGameMessageHandlers.SendGameMessage(ConnectionId, UITextKeys.UI_ERROR_NOT_ABLE_TO_LOOT);
                return;
            }
#endif
            base.CmdPickup(objectId);
        }
    }
}
