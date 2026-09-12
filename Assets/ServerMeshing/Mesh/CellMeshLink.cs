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
        public const ushort HandoffNotice = 103;
        public const ushort GhostHit = 104;
        public const ushort GhostHitResult = 105;
    }

    /// <summary>
    /// A hit that landed on a GHOST in the sender's cell (P3, DESIGN §12): the raw attacker damage is forwarded to
    /// the target's owner, which resolves it authoritatively against the real entity.
    /// </summary>
    public struct GhostHitMessage : INetSerializable
    {
        public string targetMeshId;
        public string instigatorMeshId;   // MeshEntityId of the attacker (may be unknown to the receiver)
        public byte hitBoxPosition;
        public Vector3 fromPosition;
        public int[] elementDataIds;      // damageAmounts keys (0 = default element)
        public float[] mins;
        public float[] maxs;
        public bool hasWeapon;
        public CharacterItem weapon;
        public int skillDataId;
        public int skillLevel;
        public int randomSeed;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(targetMeshId);
            writer.Put(instigatorMeshId);
            writer.Put(hitBoxPosition);
            writer.Put(fromPosition.x); writer.Put(fromPosition.y); writer.Put(fromPosition.z);
            int n = elementDataIds != null ? elementDataIds.Length : 0;
            writer.Put(n);
            for (int i = 0; i < n; ++i) { writer.Put(elementDataIds[i]); writer.Put(mins[i]); writer.Put(maxs[i]); }
            writer.Put(hasWeapon);
            if (hasWeapon) weapon.Serialize(writer);
            writer.Put(skillDataId);
            writer.Put(skillLevel);
            writer.Put(randomSeed);
        }

        public void Deserialize(NetDataReader reader)
        {
            targetMeshId = reader.GetString();
            instigatorMeshId = reader.GetString();
            hitBoxPosition = reader.GetByte();
            fromPosition = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
            int n = reader.GetInt();
            elementDataIds = new int[n]; mins = new float[n]; maxs = new float[n];
            for (int i = 0; i < n; ++i) { elementDataIds[i] = reader.GetInt(); mins[i] = reader.GetFloat(); maxs[i] = reader.GetFloat(); }
            hasWeapon = reader.GetBool();
            if (hasWeapon) { weapon = new CharacterItem(); weapon.Deserialize(reader); }
            skillDataId = reader.GetInt();
            skillLevel = reader.GetInt();
            randomSeed = reader.GetInt();
        }
    }

    /// <summary>The owner's authoritative outcome of a forwarded hit, so the ghost's cell can show real combat text.</summary>
    public struct GhostHitResultMessage : INetSerializable
    {
        public string targetMeshId;
        public byte combatAmountType;
        public int totalDamage;
        public int currentHp;
        public void Serialize(NetDataWriter writer) { writer.Put(targetMeshId); writer.Put(combatAmountType); writer.Put(totalDamage); writer.Put(currentHp); }
        public void Deserialize(NetDataReader reader) { targetMeshId = reader.GetString(); combatAmountType = reader.GetByte(); totalDamage = reader.GetInt(); currentHp = reader.GetInt(); }
    }

    /// <summary>Sent by the owning cell right before it tells the client to reconnect: "this character is coming to you".</summary>
    public struct HandoffNoticeMessage : INetSerializable
    {
        public string characterId;
        public string userId;
        public void Serialize(NetDataWriter writer) { writer.Put(characterId); writer.Put(userId); }
        public void Deserialize(NetDataReader reader) { characterId = reader.GetString(); userId = reader.GetString(); }
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
            RegisterMessageHandler(MeshLinkMessageTypes.HandoffNotice, HandleHandoffNotice);
            RegisterMessageHandler(MeshLinkMessageTypes.GhostHit, HandleGhostHit);
            RegisterMessageHandler(MeshLinkMessageTypes.GhostHitResult, HandleGhostHitResult);
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

        private void HandleHandoffNotice(MessageHandlerData messageHandler)
        {
            HandoffNoticeMessage msg = messageHandler.ReadMessage<HandoffNoticeMessage>();
            _manager.OnHandoffNotice(msg);
        }

        private void HandleGhostHit(MessageHandlerData messageHandler)
        {
            GhostHitMessage msg = messageHandler.ReadMessage<GhostHitMessage>();
            _manager.OnGhostHit(messageHandler.ConnectionId, msg);
        }

        private void HandleGhostHitResult(MessageHandlerData messageHandler)
        {
            GhostHitResultMessage msg = messageHandler.ReadMessage<GhostHitResultMessage>();
            _manager.OnGhostHitResult(msg);
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

        public void SendHandoffNotice(string characterId, string userId)
        {
            if (!IsConnected) return;
            SendPacket(0, DeliveryMethod.ReliableOrdered, MeshLinkMessageTypes.HandoffNotice, writer => new HandoffNoticeMessage { characterId = characterId, userId = userId }.Serialize(writer));
        }

        public void SendGhostHit(GhostHitMessage msg)
        {
            if (!IsConnected) return;
            SendPacket(0, DeliveryMethod.ReliableOrdered, MeshLinkMessageTypes.GhostHit, writer => msg.Serialize(writer));
        }

        public void SendGhostHitResult(GhostHitResultMessage msg)
        {
            if (!IsConnected) return;
            SendPacket(0, DeliveryMethod.ReliableOrdered, MeshLinkMessageTypes.GhostHitResult, writer => msg.Serialize(writer));
        }

        public void SendGhostRemove(string characterId)
        {
            if (!IsConnected) return;
            SendPacket(0, DeliveryMethod.ReliableOrdered, MeshLinkMessageTypes.GhostRemove, writer => new GhostRemoveMessage { characterId = characterId }.Serialize(writer));
        }
    }
}
