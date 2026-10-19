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
        public const ushort GhostReplay = 115;   // 116-118 were the per-verb attack/skill messages, folded into GhostReplay
        public const ushort BuildingPlace = 119; // a building placed from next door lands on the cell that owns its position
        public const ushort LentMonsterOwner = 120; // a borrowed monster changed hands again: its home learns the new owner
        // 121-123 are reserved. Slice 2b (dynamic cells, slices/slice2b_design.md decisions 10, 11 and 13):
        public const ushort StaticHandoff = 124;          // a harvestable or item drop now stands in the receiver's owned rectangle: take it
        public const ushort StaticHandoffAck = 125;       // receiver -> sender: the static is the receiver's now (promoted or spawned)
        public const ushort StaticHandoffFailed = 126;    // receiver -> sender: it could not take it; the sender re-promotes its copy
        public const ushort LentStatic = 127;             // to a static's home cell: who holds it now, or that it is gone (respawn it)
        public const ushort MonsterHandoffFailed = 128;   // receiver -> sender: it could not spawn the monster; the sender re-promotes it
        public const ushort ServerCharacterHandoff = 129; // a server-owned character (no client) moves to the cell that owns its position
        // Slice 2d (supervision and fencing, slices/slice2d_design.md decision 8):
        public const ushort RegistrationState = 130;      // a Dynamic map's cell -> each linked neighbour, every 2 s and on change: whether it is registered with the cluster
        // Slice 4 (buildings across a cut, slices/slice4_design.md decisions 9 and 10):
        public const ushort BuildingTransfer = 131;       // a building tree whose root stands in the receiver's owned rectangle: take it (rows already saved)
        public const ushort BuildingTransferReply = 132;  // receiver -> sender: taken (promoted, spawned, owned already), or refused with a reason
    }

    /// <summary>
    /// A building tree (Slice 4 decision 9): the root first, then its descendants parent before child, each node the
    /// save data the sender force-saved before sending. Positions are in the sender's frame.
    /// </summary>
    public struct BuildingTransferMessage : INetSerializable
    {
        public string rootId;
        public BuildingSaveData[] nodes;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(rootId ?? string.Empty);
            int count = nodes != null ? nodes.Length : 0;
            writer.Put(count);
            for (int i = 0; i < count; ++i)
                (nodes[i] ?? new BuildingSaveData()).Serialize(writer);
        }

        public void Deserialize(NetDataReader reader)
        {
            rootId = reader.GetString();
            int count = reader.GetInt();
            if (count < 0 || count > 4096)
                throw new System.IO.InvalidDataException($"building transfer node count {count}");
            nodes = new BuildingSaveData[count];
            for (int i = 0; i < count; ++i)
            {
                nodes[i] = new BuildingSaveData();
                nodes[i].Deserialize(reader);
            }
        }
    }

    /// <summary>The receiver's answer to a BuildingTransfer (Slice 4 decision 10).</summary>
    public struct BuildingTransferReplyMessage : INetSerializable
    {
        public string rootId;
        public bool ok;
        public string reason;
        public int promoted;
        public int spawned;
        public int ownedAlready;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(rootId ?? string.Empty); writer.Put(ok); writer.Put(reason ?? string.Empty);
            writer.Put(promoted); writer.Put(spawned); writer.Put(ownedAlready);
        }

        public void Deserialize(NetDataReader reader)
        {
            rootId = reader.GetString(); ok = reader.GetBool(); reason = reader.GetString();
            promoted = reader.GetInt(); spawned = reader.GetInt(); ownedAlready = reader.GetInt();
        }
    }

    /// <summary>
    /// A harvestable or an item drop the sender owned that stands in the receiver's owned rectangle (Slice 2b decision 10).
    /// The sender has already demoted its copy to a puppet; the receiver promotes its own puppet in place or spawns the
    /// entity from the registered prefab, and answers StaticHandoffAck or StaticHandoffFailed. Positions are in the
    /// sender's frame.
    /// </summary>
    public struct StaticHandoffMessage : INetSerializable
    {
        public string meshId;
        public byte kind;             // GhostKind.Harvestable or GhostKind.ItemDrop
        public int entityId;          // the prefab's entity id
        public int level;             // the spawn level
        public Vector3 position;
        public float yaw;
        public Vector3 spawnPosition;
        public string homeZoneId;     // the cell whose spawn area respawns it (empty: nobody does)
        public int currentHp;         // harvestables
        public float respawnDelay;    // the spawn area's respawn delay for it
        public byte givenType;        // item drops: RewardGivenType
        public CharacterItem[] items; // item drops: the contents
        public string[] looters;      // item drops: who may loot it while the loot lock lasts
        public float appearSeconds;   // item drops: seconds left before it disappears; negative = it stays until picked up

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(meshId ?? string.Empty); writer.Put(kind); writer.Put(entityId); writer.Put(level);
            writer.Put(position.x); writer.Put(position.y); writer.Put(position.z); writer.Put(yaw);
            writer.Put(spawnPosition.x); writer.Put(spawnPosition.y); writer.Put(spawnPosition.z);
            writer.Put(homeZoneId ?? string.Empty); writer.Put(currentHp); writer.Put(respawnDelay); writer.Put(givenType);
            int n = items != null ? items.Length : 0;
            writer.Put(n);
            for (int i = 0; i < n; ++i) writer.Put(items[i]);
            int m = looters != null ? looters.Length : 0;
            writer.Put(m);
            for (int i = 0; i < m; ++i) writer.Put(looters[i] ?? string.Empty);
            writer.Put(appearSeconds);
        }

        public void Deserialize(NetDataReader reader)
        {
            meshId = reader.GetString(); kind = reader.GetByte(); entityId = reader.GetInt(); level = reader.GetInt();
            position = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat()); yaw = reader.GetFloat();
            spawnPosition = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
            homeZoneId = reader.GetString(); currentHp = reader.GetInt(); respawnDelay = reader.GetFloat(); givenType = reader.GetByte();
            int n = reader.GetInt();
            if (n < 0 || n > 256) throw new System.IO.InvalidDataException($"static handoff with {n} items");
            items = new CharacterItem[n];
            for (int i = 0; i < n; ++i) items[i] = reader.Get<CharacterItem>();
            int m = reader.GetInt();
            if (m < 0 || m > 256) throw new System.IO.InvalidDataException($"static handoff with {m} looters");
            looters = new string[m];
            for (int i = 0; i < m; ++i) looters[i] = reader.GetString();
            appearSeconds = reader.GetFloat();
        }
    }

    /// <summary>The answer to a StaticHandoff, sent as StaticHandoffAck (no reason) or StaticHandoffFailed (why not).</summary>
    public struct StaticHandoffReplyMessage : INetSerializable
    {
        public string meshId;
        public string reason;
        public void Serialize(NetDataWriter writer) { writer.Put(meshId ?? string.Empty); writer.Put(reason ?? string.Empty); }
        public void Deserialize(NetDataReader reader) { meshId = reader.GetString(); reason = reader.GetString(); }
    }

    /// <summary>
    /// To a static's home cell (whose spawn area made it): <see cref="ownerZone"/> holds it now or, with <see cref="gone"/>,
    /// it was depleted, picked up or expired there and the home's spawn area respawns it.
    /// </summary>
    public struct LentStaticMessage : INetSerializable
    {
        public string meshId;
        public string ownerZone;
        public bool gone;
        public void Serialize(NetDataWriter writer) { writer.Put(meshId ?? string.Empty); writer.Put(ownerZone ?? string.Empty); writer.Put(gone); }
        public void Deserialize(NetDataReader reader) { meshId = reader.GetString(); ownerZone = reader.GetString(); gone = reader.GetBool(); }
    }

    /// <summary>The receiver of a MonsterHandoff could not spawn the monster: the sender re-promotes its demoted copy.</summary>
    public struct MonsterHandoffFailedMessage : INetSerializable
    {
        public string meshId;
        public string reason;
        public void Serialize(NetDataWriter writer) { writer.Put(meshId ?? string.Empty); writer.Put(reason ?? string.Empty); }
        public void Deserialize(NetDataReader reader) { meshId = reader.GetString(); reason = reader.GetString(); }
    }

    /// <summary>
    /// A server-owned character (no client connection) captured by an IMeshServerCharacterAdopter of <see cref="kind"/>:
    /// the sender despawned it and the receiver's adopter of the same kind spawns it at the pose (sender's frame). The
    /// add-on only carries the bytes; <see cref="meshId"/> lets the receiver drop its puppet of the character first.
    /// </summary>
    public struct ServerCharacterHandoffMessage : INetSerializable
    {
        public string kind;
        public string meshId;
        public byte[] state;
        public Vector3 position;
        public float yaw;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(kind ?? string.Empty); writer.Put(meshId ?? string.Empty);
            writer.PutBytesWithLength(state ?? System.Array.Empty<byte>());
            writer.Put(position.x); writer.Put(position.y); writer.Put(position.z); writer.Put(yaw);
        }

        public void Deserialize(NetDataReader reader)
        {
            kind = reader.GetString(); meshId = reader.GetString(); state = reader.GetBytesWithLength();
            position = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat()); yaw = reader.GetFloat();
        }
    }

    /// <summary>
    /// Whether the sender is registered with the cluster (Slice 2d decision 8). A neighbour that reports itself not
    /// registered is chosen as no handoff target; one that reports itself registered witnesses that the cluster is up,
    /// which a cell cut off from it needs before it fences itself. Sent and read on Dynamic maps only.
    /// </summary>
    public struct RegistrationStateMessage : INetSerializable
    {
        public bool registered;
        public void Serialize(NetDataWriter writer) { writer.Put(registered); }
        public void Deserialize(NetDataReader reader) { registered = reader.GetBool(); }
    }

    /// <summary>
    /// A building a player placed while standing on another cell at a position this cell owns: the full save data,
    /// created here as if the player had placed it here (DB row under this channel, streamed back as a ghost).
    /// </summary>
    public struct BuildingPlaceMessage : INetSerializable
    {
        public BuildingSaveData building;
        public void Serialize(NetDataWriter writer) { (building ?? new BuildingSaveData()).Serialize(writer); }
        public void Deserialize(NetDataReader reader) { building = new BuildingSaveData(); building.Deserialize(reader); }
    }

    /// <summary>
    /// Something an owned character showed its own observers (an attack or skill start, a trigger) that the puppet on
    /// the neighbour must show too. Dispatched by <see cref="kind"/> (see MeshMapNetworkManager_Actions); the target,
    /// an object id local to the owner, travels as a mesh id and is re-resolved on the puppet's cell.
    /// </summary>
    public struct GhostReplayMessage : INetSerializable
    {
        public string actorMeshId;
        public ushort kind;
        public string targetMeshId;
        public byte[] payload;
        public void Serialize(NetDataWriter writer)
        {
            writer.Put(actorMeshId); writer.Put(kind); writer.Put(targetMeshId ?? string.Empty);
            writer.PutBytesWithLength(payload ?? System.Array.Empty<byte>());
        }
        public void Deserialize(NetDataReader reader)
        {
            actorMeshId = reader.GetString(); kind = reader.GetUShort(); targetMeshId = reader.GetString();
            payload = reader.GetBytesWithLength();
        }
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

    /// <summary>A monster this cell lent out was handed on to a third cell: the respawn accounting must know who holds it now.</summary>
    public struct LentMonsterOwnerMessage : INetSerializable
    {
        public string meshId;
        public string ownerZone;
        public void Serialize(NetDataWriter writer) { writer.Put(meshId); writer.Put(ownerZone); }
        public void Deserialize(NetDataReader reader) { meshId = reader.GetString(); ownerZone = reader.GetString(); }
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

    /// <summary>
    /// Sent by a MeshLinkClient right after connecting so the server knows which zone is talking, and which process
    /// of it: a different incarnation from the same zone means the neighbour restarted and forgot everything it held.
    /// </summary>
    public struct MeshHelloMessage : INetSerializable
    {
        public string zoneId;
        public string incarnation;
        public void Serialize(NetDataWriter writer) { writer.Put(zoneId); writer.Put(incarnation ?? string.Empty); }
        public void Deserialize(NetDataReader reader) { zoneId = reader.GetString(); incarnation = reader.GetString(); }
    }

    /// <summary>One authoritative snapshot of an entity that lives in the sender's border band.</summary>
    public enum GhostKind : byte { Player = 0, Monster = 1, ItemDrop = 2, Harvestable = 3, Building = 4 }

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
        public Vector3 velocity;     // m/s, the owner's estimate over the last snapshot interval (zero when standing or just teleported)
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
        public Vector3 fullRotation;  // buildings only (written after the common fields when kind == Building): a wall leans, a character does not
        public int partyId;           // players: what the kit's ally/enemy rules read off the target (party, guild, faction, PK flag)
        public int guildId;
        public int factionId;
        public bool isPkOn;

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
            writer.Put(velocity.x); writer.Put(velocity.y); writer.Put(velocity.z);
            writer.Put(movementState);
            writer.Put(extraMovementState);
            writer.Put(currentHp);
            writer.Put(maxHp);
            writer.Put(mountEntityId);
            writer.Put(mountLevel);
            writer.Put(summonerMeshId ?? string.Empty);
            writer.Put(summonId ?? string.Empty);
            writer.Put(summonType);
            writer.Put(rightWeaponDataId); writer.Put(rightWeaponLevel); writer.Put(leftWeaponDataId); writer.Put(leftWeaponLevel); writer.Put(partyId); writer.Put(guildId); writer.Put(factionId); writer.Put(isPkOn); if (kind == GhostKind.Building) { writer.Put(fullRotation.x); writer.Put(fullRotation.y); writer.Put(fullRotation.z); }
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
            velocity = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
            movementState = reader.GetByte();
            extraMovementState = reader.GetByte();
            currentHp = reader.GetInt();
            maxHp = reader.GetInt();
            mountEntityId = reader.GetInt();
            mountLevel = reader.GetInt();
            summonerMeshId = reader.GetString();
            summonId = reader.GetString();
            summonType = reader.GetByte();
            rightWeaponDataId = reader.GetInt(); rightWeaponLevel = reader.GetInt(); leftWeaponDataId = reader.GetInt(); leftWeaponLevel = reader.GetInt(); partyId = reader.GetInt(); guildId = reader.GetInt(); factionId = reader.GetInt(); isPkOn = reader.GetBool(); if (kind == GhostKind.Building) fullRotation = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
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

        /// <summary>Bytes and packets on the wire, both directions, every neighbour (LiteNetLib's own counters).</summary>
        public NetStatistics Statistics => (Transport as LiteNetLibTransport)?.Server?.Statistics;

        public MeshLinkServer(MeshMapNetworkManager manager) : base(new LiteNetLibTransport("MESH", 16, 16))
        {
            _manager = manager;
            RegisterMessageHandler(MeshLinkMessageTypes.Hello, HandleHello);
            RegisterMessageHandler(MeshLinkMessageTypes.GhostSnapshot, HandleGhostSnapshot);
            RegisterMessageHandler(MeshLinkMessageTypes.GhostRemove, HandleGhostRemove);
            RegisterMessageHandler(MeshLinkMessageTypes.HandoffNotice, HandleHandoffNotice);
            RegisterMessageHandler(MeshLinkMessageTypes.HandoffAck, HandleHandoffAck);
            RegisterMessageHandler(MeshLinkMessageTypes.GhostPartyShare, HandleGhostPartyShare);
            RegisterMessageHandler(MeshLinkMessageTypes.GhostReplay, HandleGhostReplay);
            RegisterMessageHandler(MeshLinkMessageTypes.BuildingPlace, HandleBuildingPlace);
            RegisterMessageHandler(MeshLinkMessageTypes.GhostHit, HandleGhostHit);
            RegisterMessageHandler(MeshLinkMessageTypes.GhostHitResult, HandleGhostHitResult);
            RegisterMessageHandler(MeshLinkMessageTypes.GhostReward, HandleGhostReward);
            RegisterMessageHandler(MeshLinkMessageTypes.MonsterHandoff, HandleMonsterHandoff);
            RegisterMessageHandler(MeshLinkMessageTypes.MonsterGone, HandleMonsterGone);
            RegisterMessageHandler(MeshLinkMessageTypes.LentMonsterOwner, HandleLentMonsterOwner);
            RegisterMessageHandler(MeshLinkMessageTypes.GhostPickup, HandleGhostPickup);
            RegisterMessageHandler(MeshLinkMessageTypes.GhostLoot, HandleGhostLoot);
            RegisterMessageHandler(MeshLinkMessageTypes.GhostKill, HandleGhostKill);
            RegisterMessageHandler(MeshLinkMessageTypes.GhostAction, HandleGhostAction);
            RegisterMessageHandler(MeshLinkMessageTypes.StaticHandoff, HandleStaticHandoff);
            RegisterMessageHandler(MeshLinkMessageTypes.StaticHandoffAck, HandleStaticHandoffAck);
            RegisterMessageHandler(MeshLinkMessageTypes.StaticHandoffFailed, HandleStaticHandoffFailed);
            RegisterMessageHandler(MeshLinkMessageTypes.LentStatic, HandleLentStatic);
            RegisterMessageHandler(MeshLinkMessageTypes.MonsterHandoffFailed, HandleMonsterHandoffFailed);
            RegisterMessageHandler(MeshLinkMessageTypes.ServerCharacterHandoff, HandleServerCharacterHandoff);
            RegisterMessageHandler(MeshLinkMessageTypes.RegistrationState, HandleRegistrationState);
            RegisterMessageHandler(MeshLinkMessageTypes.BuildingTransfer, HandleBuildingTransfer);
            RegisterMessageHandler(MeshLinkMessageTypes.BuildingTransferReply, HandleBuildingTransferReply);
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
            _manager.OnMeshNeighbourHello(messageHandler.ConnectionId, msg.zoneId, msg.incarnation);
        }

        private void HandleGhostSnapshot(MessageHandlerData messageHandler)
        {
            GhostSnapshotMessage msg = messageHandler.ReadMessage<GhostSnapshotMessage>();
            _manager.OnGhostSnapshot(messageHandler.ConnectionId, msg);
        }

        private void HandleGhostRemove(MessageHandlerData messageHandler)
        {
            GhostRemoveMessage msg = messageHandler.ReadMessage<GhostRemoveMessage>();
            _manager.OnGhostRemove(messageHandler.ConnectionId, msg.characterId);
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

        private void HandleGhostReplay(MessageHandlerData messageHandler)
        {
            GhostReplayMessage msg = messageHandler.ReadMessage<GhostReplayMessage>();
            _manager.OnGhostReplay(messageHandler.ConnectionId, msg);
        }

        private void HandleBuildingPlace(MessageHandlerData messageHandler)
        {
            BuildingPlaceMessage msg = messageHandler.ReadMessage<BuildingPlaceMessage>();
            _manager.OnBuildingPlace(messageHandler.ConnectionId, msg);
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

        private void HandleLentMonsterOwner(MessageHandlerData messageHandler)
        {
            LentMonsterOwnerMessage msg = messageHandler.ReadMessage<LentMonsterOwnerMessage>();
            _manager.OnLentMonsterOwner(msg.meshId, msg.ownerZone);
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

        private void HandleStaticHandoff(MessageHandlerData messageHandler)
        {
            StaticHandoffMessage msg = messageHandler.ReadMessage<StaticHandoffMessage>();
            _manager.OnStaticHandoff(messageHandler.ConnectionId, msg);
        }

        private void HandleStaticHandoffAck(MessageHandlerData messageHandler)
        {
            StaticHandoffReplyMessage msg = messageHandler.ReadMessage<StaticHandoffReplyMessage>();
            _manager.OnStaticHandoffReply(messageHandler.ConnectionId, msg, true);
        }

        private void HandleStaticHandoffFailed(MessageHandlerData messageHandler)
        {
            StaticHandoffReplyMessage msg = messageHandler.ReadMessage<StaticHandoffReplyMessage>();
            _manager.OnStaticHandoffReply(messageHandler.ConnectionId, msg, false);
        }

        private void HandleLentStatic(MessageHandlerData messageHandler)
        {
            LentStaticMessage msg = messageHandler.ReadMessage<LentStaticMessage>();
            _manager.OnLentStatic(messageHandler.ConnectionId, msg);
        }

        private void HandleBuildingTransfer(MessageHandlerData messageHandler)
        {
            BuildingTransferMessage msg = messageHandler.ReadMessage<BuildingTransferMessage>();
            _manager.OnBuildingTransfer(messageHandler.ConnectionId, msg);
        }

        private void HandleBuildingTransferReply(MessageHandlerData messageHandler)
        {
            BuildingTransferReplyMessage msg = messageHandler.ReadMessage<BuildingTransferReplyMessage>();
            _manager.OnBuildingTransferReply(messageHandler.ConnectionId, msg);
        }

        private void HandleMonsterHandoffFailed(MessageHandlerData messageHandler)
        {
            MonsterHandoffFailedMessage msg = messageHandler.ReadMessage<MonsterHandoffFailedMessage>();
            _manager.OnMonsterHandoffFailed(messageHandler.ConnectionId, msg);
        }

        private void HandleServerCharacterHandoff(MessageHandlerData messageHandler)
        {
            ServerCharacterHandoffMessage msg = messageHandler.ReadMessage<ServerCharacterHandoffMessage>();
            _manager.OnServerCharacterHandoff(messageHandler.ConnectionId, msg);
        }

        private void HandleRegistrationState(MessageHandlerData messageHandler)
        {
            RegistrationStateMessage msg = messageHandler.ReadMessage<RegistrationStateMessage>();
            _manager.OnRegistrationState(messageHandler.ConnectionId, msg);
        }
    }

    /// <summary>Outgoing link to one neighbour cell. Owned and pumped (Update) by the MeshMapNetworkManager.</summary>
    public class MeshLinkClient : LiteNetLibClient
    {
        public override string LogTag => $"MeshLinkClient:{TargetZoneId}";

        public string TargetZoneId { get; }
        public bool IsConnected { get; private set; }
        /// <summary>The link has been up at least once since it was created: the neighbour was reachable from here.</summary>
        public bool EverConnected { get; private set; }
        public float LastConnectAttempt { get; private set; } = -999f;
        /// <summary>Seconds this link has been up (0 while down).</summary>
        public float ConnectedFor => IsConnected ? Time.unscaledTime - _connectedSince : 0f;
        private float _connectedSince;

        // A handoff sent over this link came back unclaimed (its client returned to us), so the neighbour may be gone
        // with the connection not yet timed out here. Doubt the link until the neighbour is heard from again -- any
        // datagram counts, and LiteNetLib pings every second -- and let the manager reconnect one that stays silent.
        private float _suspectSince = -1f;
        private long _packetsWhenSuspected;

        /// <summary>True from Suspect() until the next datagram from the neighbour (or a reconnect).</summary>
        public bool IsSuspect
        {
            get
            {
                if (_suspectSince < 0f)
                    return false;
                NetStatistics stats = Statistics;
                if (stats != null && stats.PacketsReceived > _packetsWhenSuspected)
                {
                    _suspectSince = -1f;
                    return false;
                }
                return true;
            }
        }

        /// <summary>Seconds this link has been doubted without an answer (0 when not doubted).</summary>
        public float SuspectFor => _suspectSince < 0f ? 0f : Time.unscaledTime - _suspectSince;

        public void Suspect()
        {
            if (_suspectSince >= 0f)
                return;
            _suspectSince = Time.unscaledTime;
            _packetsWhenSuspected = Statistics?.PacketsReceived ?? 0;
        }

        private readonly MeshMapNetworkManager _manager;

        /// <summary>Bytes and packets on the wire toward (and back from) this neighbour.</summary>
        public NetStatistics Statistics => (Transport as LiteNetLibTransport)?.Client?.Statistics;

        public MeshLinkClient(MeshMapNetworkManager manager, string targetZoneId) : base(new LiteNetLibTransport("MESH", 16, 16))
        {
            _manager = manager;
            TargetZoneId = targetZoneId;
        }

        public void Connect(string address, int port)
        {
            LastConnectAttempt = Time.unscaledTime;
            IsConnected = false;
            _suspectSince = -1f;
            StopClient();
            Logging.Log(LogTag, $"Connecting to neighbour {TargetZoneId} at {address}:{port}");
            StartClient(address, port);
            if ((Transport as LiteNetLibTransport)?.Client != null)
                ((LiteNetLibTransport)Transport).Client.EnableStatistics = true; // the transport builds its NetManager in StartClient
        }

        public override void OnClientReceive(TransportEventData eventData)
        {
            switch (eventData.type)
            {
                case ENetworkEvent.ConnectEvent:
                    IsConnected = true;
                    EverConnected = true;
                    _connectedSince = Time.unscaledTime;
                    Logging.Log(LogTag, "Connected to neighbour");
                    SendPacket(0, DeliveryMethod.ReliableOrdered, MeshLinkMessageTypes.Hello, writer => new MeshHelloMessage { zoneId = _manager.OwnZoneId, incarnation = _manager.CellIncarnation }.Serialize(writer));
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

        /// <summary>Any message on the state channel; the typed senders below are the documented vocabulary.</summary>
        internal void Send<T>(ushort msgType, T msg, byte channel = STATE_CHANNEL, DeliveryMethod delivery = DeliveryMethod.ReliableOrdered) where T : INetSerializable
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

        public void SendBuildingPlace(BuildingSaveData building)
        {
            Send(MeshLinkMessageTypes.BuildingPlace, new BuildingPlaceMessage { building = building });
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

        public void SendLentMonsterOwner(string meshId, string ownerZone)
        {
            Send(MeshLinkMessageTypes.LentMonsterOwner, new LentMonsterOwnerMessage { meshId = meshId, ownerZone = ownerZone });
        }

        public void SendGhostRemove(string characterId)
        {
            Send(MeshLinkMessageTypes.GhostRemove, new GhostRemoveMessage { characterId = characterId });
        }

        public void SendStaticHandoff(StaticHandoffMessage msg)
        {
            Send(MeshLinkMessageTypes.StaticHandoff, msg);
        }

        public void SendStaticHandoffReply(string meshId, bool ok, string reason)
        {
            Send(ok ? MeshLinkMessageTypes.StaticHandoffAck : MeshLinkMessageTypes.StaticHandoffFailed, new StaticHandoffReplyMessage { meshId = meshId, reason = reason });
        }

        public void SendLentStatic(string meshId, string ownerZone, bool gone)
        {
            Send(MeshLinkMessageTypes.LentStatic, new LentStaticMessage { meshId = meshId, ownerZone = ownerZone, gone = gone });
        }

        public void SendMonsterHandoffFailed(string meshId, string reason)
        {
            Send(MeshLinkMessageTypes.MonsterHandoffFailed, new MonsterHandoffFailedMessage { meshId = meshId, reason = reason });
        }

        public void SendServerCharacterHandoff(ServerCharacterHandoffMessage msg)
        {
            Send(MeshLinkMessageTypes.ServerCharacterHandoff, msg);
        }

        public void SendRegistrationState(bool registered)
        {
            Send(MeshLinkMessageTypes.RegistrationState, new RegistrationStateMessage { registered = registered });
        }

        public void SendBuildingTransfer(BuildingTransferMessage msg)
        {
            Send(MeshLinkMessageTypes.BuildingTransfer, msg);
        }

        public void SendBuildingTransferReply(BuildingTransferReplyMessage msg)
        {
            Send(MeshLinkMessageTypes.BuildingTransferReply, msg);
        }
    }
}
