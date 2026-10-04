// Server Meshing add-on — the state sender's two ports onto the kit: its packets go out through the manager's own server
// send (so a transport wrapped around the kit's sees them), and observers are anchored at their character or, without
// one, at the first object they own.
using System;
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Sync
{
    /// <summary>MeshStateSenderTransport sends the fan-out's packets on the kit's movement channel through the manager.</summary>
    internal sealed class MeshStateSenderTransport : IMeshStateTransport
    {
        private readonly BaseGameNetworkManager _manager;

        /// <summary>MeshStateSenderTransport sends through <paramref name="manager"/>; null throws.</summary>
        public MeshStateSenderTransport(BaseGameNetworkManager manager)
        {
            if (manager == null)
                throw new ArgumentNullException(nameof(manager));
            _manager = manager;
        }

        /// <summary>Send hands one packet to the manager's ServerSendMessage on the movement data channel.</summary>
        public void Send(long connectionId, DeliveryMethod deliveryMethod, NetDataWriter writer)
        {
            _manager.ServerSendMessage(connectionId, BaseGameEntity.MOVEMENT_DATA_CHANNEL, deliveryMethod, writer);
        }
    }

    /// <summary>
    /// MeshStateSenderPositions anchors an observer at its registered player character, else at the first object its
    /// connection owns (an observer with no registered character), and places a source at its identity's transform.
    /// </summary>
    internal sealed class MeshStateSenderPositions : IMeshStatePositions
    {
        private readonly BaseGameNetworkManager _manager;

        /// <summary>MeshStateSenderPositions reads players through <paramref name="manager"/>; null throws.</summary>
        public MeshStateSenderPositions(BaseGameNetworkManager manager)
        {
            if (manager == null)
                throw new ArgumentNullException(nameof(manager));
            _manager = manager;
        }

        /// <summary>TryGetObserverAnchor is the observer's character position, else its first owned object's; false when it has neither.</summary>
        public bool TryGetObserverAnchor(long connectionId, out Vector3 anchor)
        {
            IServerUserHandlers users = GameInstance.ServerUserHandlers;
            if (users != null && users.TryGetPlayerCharacter(connectionId, out BasePlayerCharacterEntity character) && character != null)
            {
                anchor = character.EntityTransform.position;
                return true;
            }
            if (_manager.TryGetPlayer(connectionId, out LiteNetLibPlayer player) && player != null)
            {
                Dictionary<uint, LiteNetLibIdentity>.Enumerator owned = player.GetSpawnedObjects();
                while (owned.MoveNext())
                {
                    LiteNetLibIdentity identity = owned.Current.Value;
                    if (identity != null)
                    {
                        anchor = identity.transform.position;
                        return true;
                    }
                }
            }
            anchor = default;
            return false;
        }

        /// <summary>PositionOf is the source's identity transform position.</summary>
        public Vector3 PositionOf(IMeshStateSource source)
        {
            return source.Identity.transform.position;
        }
    }
}
