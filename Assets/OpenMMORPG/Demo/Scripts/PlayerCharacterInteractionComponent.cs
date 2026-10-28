using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// The local player's requests to the server about things in the world that the kit has no
    /// request for: open or shut a <see cref="SceneryDoor"/>, and say whether they have a
    /// <see cref="TreasureChestEntity"/>'s loot window open.
    ///
    /// **Why on the player.** A client may only call a server function on an object it owns, and
    /// nobody owns a door. The kit's own built doors work the same way
    /// (<c>PlayerCharacterBuildingComponent.CallCmdOpenDoor</c>): the request travels on the
    /// player's own entity with the target's object id, and the server checks it there - that the
    /// player is alive and near enough - before it changes anything everyone will see.
    ///
    /// Belongs on the player entity prefabs; the demo's entity builder puts it there.
    /// </summary>
    [DisallowMultipleComponent]
    public class PlayerCharacterInteractionComponent : BaseNetworkedGameEntityComponent<BasePlayerCharacterEntity>
    {
        [Tooltip("How much farther than a door's own reach the server accepts a request from, in metres: the player " +
                 "has moved on their own client since they pressed.")]
        public float reachTolerance = 1.5f;

        /// <summary>The playing character's, or null.</summary>
        public static PlayerCharacterInteractionComponent Local
        {
            get
            {
                BasePlayerCharacterEntity player = GameInstance.PlayingCharacterEntity;
                return player != null ? player.GetComponent<PlayerCharacterInteractionComponent>() : null;
            }
        }

        /// <summary>Asks the server to open the door whose network half has this object id, or shut it.</summary>
        public void CallCmdToggleDoor(uint objectId)
        {
            RPC(CmdToggleDoor, objectId);
        }

        [ServerRpc]
        protected void CmdToggleDoor(uint objectId)
        {
#if UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES
            if (Entity.IsDead())
                return;
            if (!Manager.TryGetEntityByObjectId(objectId, out SceneryDoorSync sync) || sync.Door == null)
                return;
            if (!sync.Door.InReach(Entity.EntityTransform.position, reachTolerance))
            {
                GameInstance.ServerGameMessageHandlers.SendGameMessage(ConnectionId, UITextKeys.UI_ERROR_CHARACTER_IS_TOO_FAR);
                return;
            }
            sync.Door.ServerToggle();
#endif
        }

        /// <summary>Tells the server whether this player has the chest with this object id open to loot.</summary>
        public void CallCmdSetLooting(uint objectId, bool looting)
        {
            RPC(CmdSetLooting, objectId, looting);
        }

        [ServerRpc]
        protected void CmdSetLooting(uint objectId, bool looting)
        {
#if UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES
            if (!Manager.TryGetEntityByObjectId(objectId, out TreasureChestEntity chest))
                return;
            if (looting)
                chest.ServerAddLooter(Entity);
            else
                chest.ServerRemoveLooter(Entity);
#endif
        }
    }
}
