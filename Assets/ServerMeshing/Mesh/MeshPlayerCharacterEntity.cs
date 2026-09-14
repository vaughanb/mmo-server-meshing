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
