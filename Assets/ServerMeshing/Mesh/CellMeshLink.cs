// Server Meshing add-on — CellMeshLink (Spike 1b / P2).
// See Docs/ServerMeshing/DESIGN.md §10 ("CellMeshLink") and §12.
//
// EXTENSION-ONLY. A minimal server-to-server channel between ADJACENT cells, built exactly like the kit's
// ClusterClient/ClusterServer (LiteNetLibServer/LiteNetLibClient over a LiteNetLibTransport), so no core
// networking is touched. Each cell runs one MeshLinkServer on (mapPort + offset) and one MeshLinkClient per
// neighbour, discovered through the cluster's map-server broadcast (see MeshMapNetworkManager._meshPeers).
//
// Payloads carry FULL-PRECISION positions plus movement state (DESIGN decisions 2026-06-13): never the
// client-facing quantized encoding.

using LiteNetLib;
using LiteNetLib.Utils;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public static class MeshLinkMessageTypes
    {
        public const ushort Hello = 100;
        public const ushort GhostSnapshot = 101;
        public const ushort GhostRemove = 102;
    }

    /// <summary>Sent by a MeshLinkClient right after connecting so the server knows which zone is talking.</summary>
    public struct MeshHelloMessage : INetSerializable
    {
        public string zoneId;
        public void Serialize(NetDataWriter writer) { writer.Put(zoneId); }
        public void Deserialize(NetDataReader reader) { zoneId = reader.GetString(); }
    }

    /// <summary>One authoritative snapshot of an entity that lives in the sender's border band.</summary>
    public enum GhostKind : byte { Player = 0, Monster = 1 }

    public struct GhostSnapshotMessage : INetSerializable
    {
        public GhostKind kind;
        public string characterId;   // MeshEntityId: character id for players, birth-provenance id for monsters (DESIGN §8)
        public int entityId;         // entity prefab id (player or monster)
        public int dataId;           // character class data id
        public string characterName;
        public int level;
        public Vector3 position;     // full precision, sender's world frame (identity translation in the shared-frame rig)
        public float yaw;
        public byte movementState;
        public byte extraMovementState;
        public int currentHp;
        public int maxHp;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put((byte)kind);
            writer.Put(characterId);
            writer.Put(entityId);
            writer.Put(dataId);
            writer.Put(characterName);
            writer.Put(level);
            writer.Put(position.x); writer.Put(position.y); writer.Put(position.z);
            writer.Put(yaw);
            writer.Put(movementState);
            writer.Put(extraMovementState);
            writer.Put(currentHp);
            writer.Put(maxHp);
        }

        public void Deserialize(NetDataReader reader)
        {
            kind = (GhostKind)reader.GetByte();
            characterId = reader.GetString();
            entityId = reader.GetInt();
            dataId = reader.GetInt();
            characterName = reader.GetString();
            level = reader.GetInt();
            position = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
            yaw = reader.GetFloat();
            movementState = reader.GetByte();
            extraMovementState = reader.GetByte();
            currentHp = reader.GetInt();
            maxHp = reader.GetInt();
        }
    }

    public struct GhostRemoveMessage : INetSerializable
    {
        public string characterId;
        public void Serialize(NetDataWriter writer) { writer.Put(characterId); }
        public void Deserialize(NetDataReader reader) { characterId = reader.GetString(); }
    }

    /// <summary>Listens for neighbour cells. Owned and pumped (Update) by the MeshMapNetworkManager.</summary>
    public class MeshLinkServer : LiteNetLibServer
    {
        public override string LogTag => "MeshLinkServer";

        private readonly MeshMapNetworkManager _manager;

        public MeshLinkServer(MeshMapNetworkManager manager) : base(new LiteNetLibTransport("MESH", 16, 16))
        {
            _manager = manager;
            RegisterMessageHandler(MeshLinkMessageTypes.Hello, HandleHello);
            RegisterMessageHandler(MeshLinkMessageTypes.GhostSnapshot, HandleGhostSnapshot);
            RegisterMessageHandler(MeshLinkMessageTypes.GhostRemove, HandleGhostRemove);
        }

        public override void OnServerReceive(TransportEventData eventData)
        {
            switch (eventData.type)
            {
                case ENetworkEvent.ConnectEvent:
                    Logging.Log(LogTag, $"Neighbour connected: {eventData.connectionId}");
                    ConnectionIds.Add(eventData.connectionId);
                    break;
                case ENetworkEvent.DataEvent:
                    ReadPacket(eventData.connectionId, eventData.reader);
                    break;
                case ENetworkEvent.DisconnectEvent:
                    Logging.Log(LogTag, $"Neighbour disconnected: {eventData.connectionId} ({eventData.disconnectInfo.Reason})");
                    ConnectionIds.Remove(eventData.connectionId);
                    _manager.OnMeshNeighbourDisconnected(eventData.connectionId);
                    break;
                case ENetworkEvent.ErrorEvent:
                    Logging.LogError(LogTag, $"Network error {eventData.endPoint} {eventData.socketError} {eventData.errorMessage}");
                    break;
            }
        }

        private void HandleHello(MessageHandlerData messageHandler)
        {
            MeshHelloMessage msg = messageHandler.ReadMessage<MeshHelloMessage>();
            _manager.OnMeshNeighbourHello(messageHandler.ConnectionId, msg.zoneId);
        }

        private void HandleGhostSnapshot(MessageHandlerData messageHandler)
        {
            GhostSnapshotMessage msg = messageHandler.ReadMessage<GhostSnapshotMessage>();
            _manager.OnGhostSnapshot(messageHandler.ConnectionId, msg);
        }

        private void HandleGhostRemove(MessageHandlerData messageHandler)
        {
            GhostRemoveMessage msg = messageHandler.ReadMessage<GhostRemoveMessage>();
            _manager.OnGhostRemove(msg.characterId);
        }
    }

    /// <summary>Outgoing link to one neighbour cell. Owned and pumped (Update) by the MeshMapNetworkManager.</summary>
    public class MeshLinkClient : LiteNetLibClient
    {
        public override string LogTag => $"MeshLinkClient:{TargetZoneId}";

        public string TargetZoneId { get; }
        public bool IsConnected { get; private set; }
        public float LastConnectAttempt { get; private set; } = -999f;

        private readonly MeshMapNetworkManager _manager;

        public MeshLinkClient(MeshMapNetworkManager manager, string targetZoneId) : base(new LiteNetLibTransport("MESH", 16, 16))
        {
            _manager = manager;
            TargetZoneId = targetZoneId;
        }

        public void Connect(string address, int port)
        {
            LastConnectAttempt = Time.unscaledTime;
            IsConnected = false;
            StopClient();
            Logging.Log(LogTag, $"Connecting to neighbour {TargetZoneId} at {address}:{port}");
            StartClient(address, port);
        }

        public override void OnClientReceive(TransportEventData eventData)
        {
            switch (eventData.type)
            {
                case ENetworkEvent.ConnectEvent:
                    IsConnected = true;
                    Logging.Log(LogTag, "Connected to neighbour");
                    SendPacket(0, DeliveryMethod.ReliableOrdered, MeshLinkMessageTypes.Hello, writer => new MeshHelloMessage { zoneId = _manager.OwnZoneId }.Serialize(writer));
                    break;
                case ENetworkEvent.DataEvent:
                    ReadPacket(-1, eventData.reader);
                    break;
                case ENetworkEvent.DisconnectEvent:
                    IsConnected = false;
                    Logging.Log(LogTag, $"Disconnected from neighbour ({eventData.disconnectInfo.Reason})");
                    StopClient();
                    break;
                case ENetworkEvent.ErrorEvent:
                    Logging.LogError(LogTag, $"Network error {eventData.endPoint} {eventData.socketError} {eventData.errorMessage}");
                    break;
            }
        }

        public void SendGhostSnapshot(GhostSnapshotMessage msg)
        {
            if (!IsConnected) return;
            SendPacket(0, DeliveryMethod.ReliableOrdered, MeshLinkMessageTypes.GhostSnapshot, writer => msg.Serialize(writer));
        }

        public void SendGhostRemove(string characterId)
        {
            if (!IsConnected) return;
            SendPacket(0, DeliveryMethod.ReliableOrdered, MeshLinkMessageTypes.GhostRemove, writer => new GhostRemoveMessage { characterId = characterId }.Serialize(writer));
        }
    }
}
