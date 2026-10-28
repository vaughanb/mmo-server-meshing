// Server Meshing add-on — pre-connect to the next cell (DESIGN.md 1.03; the undetectable handoff, step 3).
//
// Until 1.02 a crossing opened its connection to the new cell at the hop: the client transport was destroyed and
// rebuilt in the frame the warp arrived (the longest frame left in a hop) and the connect handshake was a round trip
// of the gap. Now the origin cell tells a client which neighbour it is approaching — within preconnectDistance of
// that cell, and only a registered, linked one — and the client opens an idle connection to it ahead of time. The hop
// takes that connection over (LiteNetLibManager.MigrateClient with a connected transport) and enters the game at once.
//
// Server: MeshBoundaryTick calls PreconnectHint for every owned player; a MeshPreconnectMessage goes out once per
// change of target (an empty address tells the client to drop its warm connection). Client: HandlePreconnectAtClient
// hands the hint to the migration controller, which owns the warm connection (ClientMigrationController, "Pre-connect").
// The destination sees an ordinary connected peer that has not entered the game yet; it costs it a LiteNetLibPlayer.

using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>Server to client: open (or drop, when the address is empty) an idle connection to the cell named.</summary>
    public struct MeshPreconnectMessage : INetSerializable
    {
        public string cellId;
        public string networkAddress;
        public int networkPort;

        public void Deserialize(NetDataReader reader)
        {
            cellId = reader.GetString();
            networkAddress = reader.GetString();
            networkPort = reader.GetInt();
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(cellId ?? string.Empty);
            writer.Put(networkAddress ?? string.Empty);
            writer.Put(networkPort);
        }
    }

    public partial class MeshMapNetworkManager
    {
        /// <summary>The add-on's client message id for <see cref="MeshPreconnectMessage"/> (the kit's ids stay below 200).</summary>
        public const ushort MeshPreconnectMsgType = 1900;

        [Header("Pre-connect (undetectable handoff, step 3)")]
        [Tooltip("A client within this many metres of a neighbouring cell is told to open an idle connection to it, so the crossing needs no connect. 0 turns pre-connect off.")]
        public float preconnectDistance = 30f;

        // Connection id -> the peer key of the cell it was last told to pre-connect to (absent: told none).
        private readonly Dictionary<long, string> _preconnectHints = new Dictionary<long, string>();

        protected override void RegisterMessages()
        {
            base.RegisterMessages();
            RegisterClientMessage(MeshPreconnectMsgType, HandlePreconnectAtClient);
        }

        /// <summary>
        /// Server, from MeshBoundaryTick for a player this cell owns: the nearest neighbouring cell within
        /// <see cref="preconnectDistance"/> that is registered and linked is the one its client should hold a
        /// connection to. One message per change (none, or another cell).
        /// </summary>
        private void PreconnectHint(BasePlayerCharacterEntity entity, Vector3 scenePos)
        {
            if (preconnectDistance <= 0f || entity == null || OwnCell == null)
                return;
            long connectionId = entity.ConnectionId;
            if (connectionId < 0)
                return;
            MeshCell nearest = null;
            float nearestDistance = preconnectDistance;
            for (int i = 0; i < OwnCell.neighbours.Count; ++i)
            {
                MeshCell neighbour = OwnCell.neighbours[i];
                float distance = OwnCell.DistanceTo(neighbour, scenePos);
                if (distance <= nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = neighbour;
                }
            }
            string key = null;
            CentralServerPeerInfo peer = default;
            if (nearest != null)
            {
                string candidate = PeerInfoExtensions.GetPeerInfoKey(nearest.id, nearest.mapId);
                if (_meshPeers.TryGetValue(candidate, out peer) && IsNeighbourLinked(nearest.id))
                    key = candidate;
            }
            _preconnectHints.TryGetValue(connectionId, out string hinted);
            if (string.Equals(key, hinted))
                return;
            if (key == null)
                _preconnectHints.Remove(connectionId);
            else
                _preconnectHints[connectionId] = key;
            MeshPreconnectMessage message = key == null
                ? new MeshPreconnectMessage()
                : new MeshPreconnectMessage { cellId = nearest.id, networkAddress = peer.networkAddress, networkPort = peer.networkPort };
            ServerSendPacket(connectionId, 0, DeliveryMethod.ReliableOrdered, MeshPreconnectMsgType, message);
            Logging.Log("Mesh", key == null
                ? $"Pre-connect: {entity.CharacterName} (conn {connectionId}) left the edge band; told to drop its warm connection"
                : $"Pre-connect: {entity.CharacterName} (conn {connectionId}) is {nearestDistance:0.#} m from cell {nearest.id}; told to connect to {peer.networkAddress}:{peer.networkPort}");
        }

        /// <summary>The connection is gone (or this server stopped): nothing to compare the next hint against.</summary>
        private void PreconnectForget(long connectionId)
        {
            _preconnectHints.Remove(connectionId);
        }

        private void PreconnectForgetAll()
        {
            _preconnectHints.Clear();
        }

        /// <summary>Client: the cell we are on names the neighbour we are approaching (or none).</summary>
        private void HandlePreconnectAtClient(MessageHandlerData messageHandler)
        {
            MeshPreconnectMessage message = messageHandler.ReadMessage<MeshPreconnectMessage>();
            if (IsServer)
                return;
            if (string.IsNullOrEmpty(message.networkAddress))
                Migration.DropPreconnect("the cell says we left the edge band");
            else
                Migration.Preconnect(message.networkAddress, message.networkPort, message.cellId);
        }

        public override void OnStopClient()
        {
            base.OnStopClient();
            if (!IsServer && _migration != null)
                _migration.DropPreconnect("client stopped");
        }
    }
}
