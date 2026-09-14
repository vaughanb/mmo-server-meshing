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
        public const ushort GhostReward = 106;
        public const ushort MonsterHandoff = 107;
        public const ushort MonsterGone = 108;
        public const ushort GhostPickup = 109;
        public const ushort GhostLoot = 110;
        public const ushort GhostKill = 111;
        public const ushort GhostAction = 112;
        public const ushort HandoffAck = 113;
        public const ushort GhostPartyShare = 114;
        public const ushort GhostAttack = 115;
        public const ushort GhostAttackTrigger = 116;
        public const ushort GhostSkill = 117;
        public const ushort GhostSkillTrigger = 118;
    }

    /// <summary>An owned character started a skill: the neighbour replays RpcUseSkill on its puppet (target by mesh id).</summary>
    public struct GhostSkillMessage : INetSerializable
    {
        public string attackerMeshId;
        public long peerTimestamp;
        public bool isItem;      // a skill item: dataId is the item's data id (the kit resolves the skill from it)
        public int dataId;
        public int skillLevel;
        public byte weaponHandlingState;
        public string targetMeshId;
        public AimPosition aimPosition;
        public void Serialize(NetDataWriter writer) { writer.Put(attackerMeshId); writer.Put(peerTimestamp); writer.Put(isItem); writer.Put(dataId); writer.Put(skillLevel); writer.Put(weaponHandlingState); writer.Put(targetMeshId ?? string.Empty); writer.Put(aimPosition); }
        public void Deserialize(NetDataReader reader) { attackerMeshId = reader.GetString(); peerTimestamp = reader.GetLong(); isItem = reader.GetBool(); dataId = reader.GetInt(); skillLevel = reader.GetInt(); weaponHandlingState = reader.GetByte(); targetMeshId = reader.GetString(); aimPosition = reader.Get<AimPosition>(); }
    }

    /// <summary>The skill reached a trigger: the neighbour replays RpcSimulateActionTrigger (target by mesh id).</summary>
    public struct GhostSkillTriggerMessage : INetSerializable
    {
        public string attackerMeshId;
        public string targetMeshId;
        public SimulateActionTriggerData data;
        public void Serialize(NetDataWriter writer) { writer.Put(attackerMeshId); writer.Put(targetMeshId ?? string.Empty); writer.Put(data); }
        public void Deserialize(NetDataReader reader) { attackerMeshId = reader.GetString(); targetMeshId = reader.GetString(); data = reader.Get<SimulateActionTriggerData>(); }
    }

    /// <summary>An owned character started an attack: the neighbour replays the kit's RpcAttack on its puppet.</summary>
    public struct GhostAttackMessage : INetSerializable
    {
        public string attackerMeshId;
        public long peerTimestamp;
        public byte weaponHandlingState;
        public void Serialize(NetDataWriter writer) { writer.Put(attackerMeshId); writer.Put(peerTimestamp); writer.Put(weaponHandlingState); }
        public void Deserialize(NetDataReader reader) { attackerMeshId = reader.GetString(); peerTimestamp = reader.GetLong(); weaponHandlingState = reader.GetByte(); }
    }

    /// <summary>The attack reached a trigger: the neighbour replays RpcSimulateActionTrigger (the visual missile/swing).</summary>
    public struct GhostAttackTriggerMessage : INetSerializable
    {
        public string attackerMeshId;
        public SimulateActionTriggerData data;
        public void Serialize(NetDataWriter writer) { writer.Put(attackerMeshId); writer.Put(data); }
        public void Deserialize(NetDataReader reader) { attackerMeshId = reader.GetString(); data = reader.Get<SimulateActionTriggerData>(); }
    }

    /// <summary>
    /// A party member's share of a kill, computed by the killer's cell for a member that stands on THIS cell (it was a
    /// ghost over there). Amounts are raw with the share multiplier already folded in; the owner applies them to the
    /// real character (and its pet) as a PartyShare, with no further party or guild processing.
    /// </summary>
    public struct GhostPartyShareMessage : INetSerializable
    {
        public string memberId;
        public int exp;
        public float expMultiplier;
        public int gold;
        public float goldMultiplier;
        public int[] currencyDataIds;
        public int[] currencyAmounts;
        public int giverLevel;
        public int sourceLevel;
        public int monsterDataId;
        public bool countKill;   // advance the member's quest kill counters too (the kit does for nearby members)
        public bool broadcast;   // sent to every linked cell because the member's cell is unknown: only the cell holding it applies, the rest stay silent
        public void Serialize(NetDataWriter writer)
        {
            writer.Put(memberId); writer.Put(exp); writer.Put(expMultiplier); writer.Put(gold); writer.Put(goldMultiplier);
            writer.PutArray(currencyDataIds ?? System.Array.Empty<int>()); writer.PutArray(currencyAmounts ?? System.Array.Empty<int>());
            writer.Put(giverLevel); writer.Put(sourceLevel); writer.Put(monsterDataId); writer.Put(countKill); writer.Put(broadcast);
        }
        public void Deserialize(NetDataReader reader)
        {
            memberId = reader.GetString(); exp = reader.GetInt(); expMultiplier = reader.GetFloat(); gold = reader.GetInt(); goldMultiplier = reader.GetFloat();
            currencyDataIds = reader.GetIntArray(); currencyAmounts = reader.GetIntArray();
            giverLevel = reader.GetInt(); sourceLevel = reader.GetInt(); monsterDataId = reader.GetInt(); countKill = reader.GetBool(); broadcast = reader.GetBool();
        }
    }

    /// <summary>
    /// Destination -> origin: the character named in a HandoffNotice registered here (ok) or never will (a reservation
    /// or notice that expired unclaimed). The origin logs the end-to-end time and releases the demoted puppet at once
    /// on failure instead of waiting for stale expiry.
    /// </summary>
    public struct HandoffAckMessage : INetSerializable
    {
        public string characterId;
        public bool ok;
        public string reason;
        public int arrivalMs;   // ms between the notice and the registration on the destination (ok only)
        public bool promoted;   // the destination promoted its puppet (pets included); false = the kit spawned fresh (pets re-summoned)
        public void Serialize(NetDataWriter writer) { writer.Put(characterId); writer.Put(ok); writer.Put(reason ?? string.Empty); writer.Put(arrivalMs); writer.Put(promoted); }
        public void Deserialize(NetDataReader reader) { characterId = reader.GetString(); ok = reader.GetBool(); reason = reader.GetString(); arrivalMs = reader.GetInt(); promoted = reader.GetBool(); }
    }

    /// <summary>
    /// Generic "do this to the entity you own" envelope: an action landed on a ghost here; the owner applies it to the
    /// real entity. New cross-edge verbs (buffs, heals, resurrection, ...) are payloads of this message, dispatched by
    /// <see cref="action"/> on the owner (see MeshMapNetworkManager_Actions).
    /// </summary>
    public struct GhostActionMessage : INetSerializable
    {
        public string targetMeshId;
        public string instigatorMeshId;  // who did it (empty when unknown); resolved on the owner as its ghost or real entity
        public ushort action;
        public byte[] payload;
        public void Serialize(NetDataWriter writer)
        {
            writer.Put(targetMeshId); writer.Put(instigatorMeshId); writer.Put(action);
            writer.PutBytesWithLength(payload ?? System.Array.Empty<byte>());
        }
        public void Deserialize(NetDataReader reader)
        {
            targetMeshId = reader.GetString(); instigatorMeshId = reader.GetString(); action = reader.GetUShort();
            payload = reader.GetBytesWithLength();
        }
    }

    /// <summary>A monster died here with a ghost as its last attacker: the killer's owner counts the kill (quests, party).</summary>
    public struct GhostKillMessage : INetSerializable
    {
        public string attackerMeshId;
        public string monsterMeshId;
        public int monsterDataId;
        public int monsterLevel;
        public void Serialize(NetDataWriter writer) { writer.Put(attackerMeshId); writer.Put(monsterMeshId); writer.Put(monsterDataId); writer.Put(monsterLevel); }
        public void Deserialize(NetDataReader reader) { attackerMeshId = reader.GetString(); monsterMeshId = reader.GetString(); monsterDataId = reader.GetInt(); monsterLevel = reader.GetInt(); }
    }

    /// <summary>A player on this cell wants to loot a ghost of a drop the neighbour owns.</summary>
    public struct GhostPickupMessage : INetSerializable
    {
        public string characterId;   // the requester (its ghost stands on the owner cell)
        public string dropMeshId;
        public void Serialize(NetDataWriter writer) { writer.Put(characterId); writer.Put(dropMeshId); }
        public void Deserialize(NetDataReader reader) { characterId = reader.GetString(); dropMeshId = reader.GetString(); }
    }

    /// <summary>The owner resolved a ghost pickup: the items go to the requester's real character (or a message why not).</summary>
    public struct GhostLootMessage : INetSerializable
    {
        public string characterId;
        public byte givenType;
        public ushort messageKey;    // UITextKeys; NONE when items follow
        public CharacterItem[] items;
        public void Serialize(NetDataWriter writer)
        {
            writer.Put(characterId); writer.Put(givenType); writer.Put(messageKey);
            writer.Put(items != null ? items.Length : 0);
            if (items != null) for (int i = 0; i < items.Length; ++i) writer.Put(items[i]);
        }
        public void Deserialize(NetDataReader reader)
        {
            characterId = reader.GetString(); givenType = reader.GetByte(); messageKey = reader.GetUShort();
            int n = reader.GetInt(); items = new CharacterItem[n];
            for (int i = 0; i < n; ++i) items[i] = reader.Get<CharacterItem>();
        }
    }

    /// <summary>
    /// Monster handoff (P3/P4): a server-owned monster walked clearly into the neighbour's cell (a chase across the
    /// edge). The owner demotes its copy to a ghost in place and the neighbour promotes its puppet to the real
    /// monster, so neither cell's clients see a despawn/spawn.
    /// </summary>
    public struct MonsterHandoffMessage : INetSerializable
    {
        public string meshId;
        public int entityId;
        public int dataId;
        public int level;
        public int currentHp;
        public Vector3 position;
        public float yaw;
        public Vector3 spawnPosition;
        public string homeZoneId;
        public string targetMeshId;
        public void Serialize(NetDataWriter writer)
        {
            writer.Put(meshId); writer.Put(entityId); writer.Put(dataId); writer.Put(level); writer.Put(currentHp);
            writer.Put(position.x); writer.Put(position.y); writer.Put(position.z); writer.Put(yaw);
            writer.Put(spawnPosition.x); writer.Put(spawnPosition.y); writer.Put(spawnPosition.z);
            writer.Put(homeZoneId); writer.Put(targetMeshId);
        }
        public void Deserialize(NetDataReader reader)
        {
            meshId = reader.GetString(); entityId = reader.GetInt(); dataId = reader.GetInt(); level = reader.GetInt(); currentHp = reader.GetInt();
            position = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat()); yaw = reader.GetFloat();
            spawnPosition = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
            homeZoneId = reader.GetString(); targetMeshId = reader.GetString();
        }
    }

    /// <summary>A borrowed monster died (or was destroyed) on the borrowing cell: its home spawn area may respawn it.</summary>
    public struct MonsterGoneMessage : INetSerializable
    {
        public string meshId;
        public void Serialize(NetDataWriter writer) { writer.Put(meshId); }
        public void Deserialize(NetDataReader reader) { meshId = reader.GetString(); }
    }

    /// <summary>
    /// Kill credit (P3): a monster killed on this cell rewarded the attacker's GHOST here. MeshGameplayRule intercepts
    /// that and sends the RAW reward (before rates/bonuses) to the attacker's owner cell, which applies it to the real
    /// character through the same gameplay rule.
    /// </summary>
    public struct GhostRewardMessage : INetSerializable
    {
        public string attackerMeshId;
        public int exp;
        public int gold;
        public float multiplier;
        public byte givenType;
        public int giverLevel;
        public int sourceLevel;
        public int[] currencyDataIds;
        public int[] currencyAmounts;
        public void Serialize(NetDataWriter writer)
        {
            writer.Put(attackerMeshId); writer.Put(exp); writer.Put(gold); writer.Put(multiplier); writer.Put(givenType); writer.Put(giverLevel); writer.Put(sourceLevel);
            writer.PutArray(currencyDataIds ?? System.Array.Empty<int>()); writer.PutArray(currencyAmounts ?? System.Array.Empty<int>());
        }
        public void Deserialize(NetDataReader reader)
        {
            attackerMeshId = reader.GetString(); exp = reader.GetInt(); gold = reader.GetInt(); multiplier = reader.GetFloat(); givenType = reader.GetByte(); giverLevel = reader.GetInt(); sourceLevel = reader.GetInt();
            currencyDataIds = reader.GetIntArray(); currencyAmounts = reader.GetIntArray();
        }
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
    public enum GhostKind : byte { Player = 0, Monster = 1, ItemDrop = 2 }

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
        public int mountEntityId;   // vehicle prefab entity id the owner is riding (0 = on foot)
        public int mountLevel;
        public string summonerMeshId; // a summoned monster: mesh id of its summoner (empty for anything else)
        public string summonId;       // ... and which CharacterSummon entry it is on that summoner (re-linked on arrival)
        public byte summonType;       // SummonType of the summon
        public int rightWeaponDataId; // equipped weapons (players): the puppet shows and animates the same weapon
        public int rightWeaponLevel;
        public int leftWeaponDataId;
        public int leftWeaponLevel;

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
            writer.Put(mountEntityId);
            writer.Put(mountLevel);
            writer.Put(summonerMeshId ?? string.Empty);
            writer.Put(summonId ?? string.Empty);
            writer.Put(summonType);
            writer.Put(rightWeaponDataId); writer.Put(rightWeaponLevel); writer.Put(leftWeaponDataId); writer.Put(leftWeaponLevel);
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
            mountEntityId = reader.GetInt();
            mountLevel = reader.GetInt();
            summonerMeshId = reader.GetString();
            summonId = reader.GetString();
            summonType = reader.GetByte();
            rightWeaponDataId = reader.GetInt(); rightWeaponLevel = reader.GetInt(); leftWeaponDataId = reader.GetInt(); leftWeaponLevel = reader.GetInt();
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
            RegisterMessageHandler(MeshLinkMessageTypes.HandoffAck, HandleHandoffAck);
            RegisterMessageHandler(MeshLinkMessageTypes.GhostPartyShare, HandleGhostPartyShare);
            RegisterMessageHandler(MeshLinkMessageTypes.GhostAttack, HandleGhostAttack);
            RegisterMessageHandler(MeshLinkMessageTypes.GhostAttackTrigger, HandleGhostAttackTrigger);
            RegisterMessageHandler(MeshLinkMessageTypes.GhostSkill, HandleGhostSkill);
            RegisterMessageHandler(MeshLinkMessageTypes.GhostSkillTrigger, HandleGhostSkillTrigger);
            RegisterMessageHandler(MeshLinkMessageTypes.GhostHit, HandleGhostHit);
            RegisterMessageHandler(MeshLinkMessageTypes.GhostHitResult, HandleGhostHitResult);
            RegisterMessageHandler(MeshLinkMessageTypes.GhostReward, HandleGhostReward);
            RegisterMessageHandler(MeshLinkMessageTypes.MonsterHandoff, HandleMonsterHandoff);
            RegisterMessageHandler(MeshLinkMessageTypes.MonsterGone, HandleMonsterGone);
            RegisterMessageHandler(MeshLinkMessageTypes.GhostPickup, HandleGhostPickup);
            RegisterMessageHandler(MeshLinkMessageTypes.GhostLoot, HandleGhostLoot);
            RegisterMessageHandler(MeshLinkMessageTypes.GhostKill, HandleGhostKill);
            RegisterMessageHandler(MeshLinkMessageTypes.GhostAction, HandleGhostAction);
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
            _manager.OnHandoffNotice(messageHandler.ConnectionId, msg);
        }

        private void HandleHandoffAck(MessageHandlerData messageHandler)
        {
            HandoffAckMessage msg = messageHandler.ReadMessage<HandoffAckMessage>();
            _manager.OnHandoffAck(messageHandler.ConnectionId, msg);
        }

        private void HandleGhostPartyShare(MessageHandlerData messageHandler)
        {
            GhostPartyShareMessage msg = messageHandler.ReadMessage<GhostPartyShareMessage>();
            _manager.OnGhostPartyShare(messageHandler.ConnectionId, msg);
        }

        private void HandleGhostAttack(MessageHandlerData messageHandler)
        {
            GhostAttackMessage msg = messageHandler.ReadMessage<GhostAttackMessage>();
            _manager.OnGhostAttack(msg);
        }

        private void HandleGhostAttackTrigger(MessageHandlerData messageHandler)
        {
            GhostAttackTriggerMessage msg = messageHandler.ReadMessage<GhostAttackTriggerMessage>();
            _manager.OnGhostAttackTrigger(msg);
        }

        private void HandleGhostSkill(MessageHandlerData messageHandler)
        {
            GhostSkillMessage msg = messageHandler.ReadMessage<GhostSkillMessage>();
            _manager.OnGhostSkill(msg);
        }

        private void HandleGhostSkillTrigger(MessageHandlerData messageHandler)
        {
            GhostSkillTriggerMessage msg = messageHandler.ReadMessage<GhostSkillTriggerMessage>();
            _manager.OnGhostSkillTrigger(msg);
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

        private void HandleGhostReward(MessageHandlerData messageHandler)
        {
            GhostRewardMessage msg = messageHandler.ReadMessage<GhostRewardMessage>();
            _manager.OnGhostReward(msg);
        }

        private void HandleMonsterHandoff(MessageHandlerData messageHandler)
        {
            MonsterHandoffMessage msg = messageHandler.ReadMessage<MonsterHandoffMessage>();
            _manager.OnMonsterHandoff(messageHandler.ConnectionId, msg);
        }

        private void HandleMonsterGone(MessageHandlerData messageHandler)
        {
            MonsterGoneMessage msg = messageHandler.ReadMessage<MonsterGoneMessage>();
            _manager.OnMonsterGone(msg.meshId);
        }

        private void HandleGhostPickup(MessageHandlerData messageHandler)
        {
            GhostPickupMessage msg = messageHandler.ReadMessage<GhostPickupMessage>();
            _manager.OnGhostPickup(messageHandler.ConnectionId, msg);
        }

        private void HandleGhostLoot(MessageHandlerData messageHandler)
        {
            GhostLootMessage msg = messageHandler.ReadMessage<GhostLootMessage>();
            _manager.OnGhostLoot(msg);
        }

        private void HandleGhostKill(MessageHandlerData messageHandler)
        {
            GhostKillMessage msg = messageHandler.ReadMessage<GhostKillMessage>();
            _manager.OnGhostKill(msg);
        }

        private void HandleGhostAction(MessageHandlerData messageHandler)
        {
            GhostActionMessage msg = messageHandler.ReadMessage<GhostActionMessage>();
            _manager.OnGhostAction(messageHandler.ConnectionId, msg);
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

        /// <summary>Channel 0: state-changing messages, reliable and ordered.</summary>
        private const byte STATE_CHANNEL = 0;
        /// <summary>Channel 1: 10 Hz position snapshots, sequenced (a stale one is dropped) so a lost snapshot never
        /// head-of-line blocks a handoff, a hit or a loot message behind it.</summary>
        private const byte SNAPSHOT_CHANNEL = 1;

        private void Send<T>(ushort msgType, T msg, byte channel = STATE_CHANNEL, DeliveryMethod delivery = DeliveryMethod.ReliableOrdered) where T : INetSerializable
        {
            if (!IsConnected) return;
            SendPacket(channel, delivery, msgType, writer => msg.Serialize(writer));
        }

        public void SendGhostSnapshot(GhostSnapshotMessage msg)
        {
            Send(MeshLinkMessageTypes.GhostSnapshot, msg, SNAPSHOT_CHANNEL, DeliveryMethod.Sequenced);
        }

        public void SendHandoffAck(HandoffAckMessage msg)
        {
            Send(MeshLinkMessageTypes.HandoffAck, msg);
        }

        public void SendGhostPartyShare(GhostPartyShareMessage msg)
        {
            Send(MeshLinkMessageTypes.GhostPartyShare, msg);
        }

        public void SendGhostAttack(GhostAttackMessage msg)
        {
            Send(MeshLinkMessageTypes.GhostAttack, msg);
        }

        public void SendGhostAttackTrigger(GhostAttackTriggerMessage msg)
        {
            Send(MeshLinkMessageTypes.GhostAttackTrigger, msg);
        }

        public void SendGhostSkill(GhostSkillMessage msg)
        {
            Send(MeshLinkMessageTypes.GhostSkill, msg);
        }

        public void SendGhostSkillTrigger(GhostSkillTriggerMessage msg)
        {
            Send(MeshLinkMessageTypes.GhostSkillTrigger, msg);
        }

        public void SendHandoffNotice(string characterId, string userId)
        {
            Send(MeshLinkMessageTypes.HandoffNotice, new HandoffNoticeMessage { characterId = characterId, userId = userId });
        }

        public void SendGhostHit(GhostHitMessage msg)
        {
            Send(MeshLinkMessageTypes.GhostHit, msg);
        }

        public void SendGhostHitResult(GhostHitResultMessage msg)
        {
            Send(MeshLinkMessageTypes.GhostHitResult, msg);
        }

        public void SendGhostReward(GhostRewardMessage msg)
        {
            Send(MeshLinkMessageTypes.GhostReward, msg);
        }

        public void SendMonsterHandoff(MonsterHandoffMessage msg)
        {
            Send(MeshLinkMessageTypes.MonsterHandoff, msg);
        }

        public void SendGhostPickup(GhostPickupMessage msg)
        {
            Send(MeshLinkMessageTypes.GhostPickup, msg);
        }

        public void SendGhostLoot(GhostLootMessage msg)
        {
            Send(MeshLinkMessageTypes.GhostLoot, msg);
        }

        public void SendGhostAction(GhostActionMessage msg)
        {
            Send(MeshLinkMessageTypes.GhostAction, msg);
        }

        public void SendGhostKill(GhostKillMessage msg)
        {
            Send(MeshLinkMessageTypes.GhostKill, msg);
        }

        public void SendMonsterGone(string meshId)
        {
            Send(MeshLinkMessageTypes.MonsterGone, new MonsterGoneMessage { meshId = meshId });
        }

        public void SendGhostRemove(string characterId)
        {
            Send(MeshLinkMessageTypes.GhostRemove, new GhostRemoveMessage { characterId = characterId });
        }
    }
}
