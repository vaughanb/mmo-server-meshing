// Server Meshing add-on — MeshMapNetworkManager (generic ghost actions and replays, DESIGN.md §12).
//
// Two envelopes, one shape each, dispatched by a handler table; adding a verb = one payload writer on the sending side
// + one handler here, no new message type, no stub.
//
// GhostAction — puppet to owner. Anything the kit does TO a target entity that is a ghost here (a buff, a debuff, a
// heal-over-time, a resurrection) must happen on the entity's owner instead. Entity subclasses intercept the kit call
// on a ghost and forward it; the owner resolves the target and the instigator (its ghost or real entity here).
//
// GhostReplay — owner to puppets. What an owned character showed its own observers (an attack or skill start, the
// trigger that launches the missile or swing) is replayed on the puppet by the neighbours streaming it, so their
// clients run the kit's normal non-owner path. A target object id means nothing on another cell: it travels as a
// mesh id and is re-resolved on arrival. Damage, buffs and resources are untouched: they happen on the owner.
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using LiteNetLib;
using LiteNetLib.Utils;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public enum GhostActionKind : ushort
    {
        ApplyBuff = 1,
        GiveItems = 2,   // a harvest yield earned by a ghost: item dataId, amount, exp (see MeshHarvestableEntity)
        Chat = 3,        // a local chat said within range of this cell's player by someone across the line: the ChatMessage (MeshServerChatHandlers)
        PkKill = 4,      // this cell's player killed a player across the line: the PK points and consecutive kills the kit credited to its puppet there
    }

    /// <summary>What the kit writes on an attacker for a PK kill, read off a puppet before and after the killing hit (DESIGN 1.38).</summary>
    public struct PkCredit
    {
        public int pkPoint, consecutiveKills, highestPkPoint, highestConsecutiveKills;

        public static PkCredit Of(BasePlayerCharacterEntity player)
        {
            return new PkCredit { pkPoint = player.PkPoint, consecutiveKills = player.ConsecutivePkKills, highestPkPoint = player.HighestPkPoint, highestConsecutiveKills = player.HighestConsecutivePkKills };
        }

        public void Restore(BasePlayerCharacterEntity player)
        {
            player.PkPoint = pkPoint;
            player.ConsecutivePkKills = consecutiveKills;
            player.HighestPkPoint = highestPkPoint;
            player.HighestConsecutivePkKills = highestConsecutiveKills;
        }
    }

    public enum GhostReplayKind : ushort
    {
        Attack = 1,         // RpcAttack: peerTimestamp, weaponHandlingState
        AttackTrigger = 2,  // RpcSimulateActionTrigger: SimulateActionTriggerData
        Skill = 3,          // RpcUseSkill / RpcUseSkillItem: peerTimestamp, isItem, dataId, skillLevel, weaponHandlingState, aimPosition
        SkillTrigger = 4,   // RpcSimulateActionTrigger: SimulateActionTriggerData (target re-resolved)
        ChargeStart = 5,    // RpcStartCharge: isLeftHand
        ChargeStop = 6,     // RpcStopCharge: nothing
    }

    public partial class MeshMapNetworkManager
    {
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        private delegate void GhostActionHandler(BaseCharacterEntity target, EntityInfo instigator, NetDataReader payload);
        private readonly Dictionary<ushort, GhostActionHandler> _ghostActionHandlers = new Dictionary<ushort, GhostActionHandler>();
        private readonly NetDataWriter _actionWriter = new NetDataWriter();

        private delegate void GhostReplayHandler(BaseCharacterEntity puppet, uint targetObjectId, NetDataReader payload);
        private readonly Dictionary<ushort, GhostReplayHandler> _ghostReplayHandlers = new Dictionary<ushort, GhostReplayHandler>();

        private void RegisterGhostActionHandlers()
        {
            _ghostActionHandlers.Clear();
            _ghostActionHandlers[(ushort)GhostActionKind.ApplyBuff] = HandleApplyBuffAction;
            _ghostActionHandlers[(ushort)GhostActionKind.GiveItems] = HandleGiveItemsAction;
            _ghostActionHandlers[(ushort)GhostActionKind.Chat] = HandleChatAction;
            _ghostActionHandlers[(ushort)GhostActionKind.PkKill] = HandlePkKillAction;
            _ghostReplayHandlers.Clear();
            _ghostReplayHandlers[(ushort)GhostReplayKind.Attack] = HandleAttackReplay;
            _ghostReplayHandlers[(ushort)GhostReplayKind.AttackTrigger] = HandleAttackTriggerReplay;
            _ghostReplayHandlers[(ushort)GhostReplayKind.Skill] = HandleSkillReplay;
            _ghostReplayHandlers[(ushort)GhostReplayKind.SkillTrigger] = HandleSkillTriggerReplay;
            _ghostReplayHandlers[(ushort)GhostReplayKind.ChargeStart] = HandleChargeStartReplay;
            _ghostReplayHandlers[(ushort)GhostReplayKind.ChargeStop] = HandleChargeStopReplay;
        }

        /// <summary>
        /// Forward an action that landed on a ghost to its owner. Returns false when the entity is not a ghost here (the
        /// caller should then apply the action locally). A ghost whose owner cannot be reached returns true and the
        /// action is dropped: a puppet never applies anything locally.
        /// </summary>
        internal bool ForwardGhostAction(BaseCharacterEntity ghost, GhostActionKind kind, EntityInfo instigator, System.Action<NetDataWriter> writePayload)
        {
            if (!IsServer || ghost == null)
                return false;
            MeshIdentity id = MeshIdentity.Get(ghost);
            if (id == null || !id.IsGhost)
                return false;
            if (string.IsNullOrEmpty(id.OwnerZoneId) || !_linkClients.TryGetValue(id.OwnerZoneId, out MeshLinkClient link) || !link.IsConnected)
            {
                WarnThrottled($"Ghost action {kind} on {ghost.CharacterName} dropped: owner zone {id.OwnerZoneId} not linked");
                return true;
            }
            string instigatorMeshId = string.Empty;
            if (instigator != null && instigator.TryGetEntity(out BaseCharacterEntity instigatorEntity) && instigatorEntity != null)
                instigatorMeshId = MeshEntityIdOf(instigatorEntity);
            _actionWriter.Reset();
            writePayload?.Invoke(_actionWriter);
            link.SendGhostAction(new GhostActionMessage
            {
                targetMeshId = id.MeshEntityId,
                instigatorMeshId = instigatorMeshId,
                action = (ushort)kind,
                payload = _actionWriter.CopyData(),
            });
            if (traceHandoffTiming)
                Logging.Log("Mesh", $"Ghost action {kind} on {ghost.CharacterName} forwarded to zone {id.OwnerZoneId} (by {instigatorMeshId})");
            return true;
        }

        internal void OnGhostAction(long connectionId, GhostActionMessage msg)
        {
            if (!IsServer)
                return;
            BaseCharacterEntity target = ResolveOwnedCharacter(msg.targetMeshId);
            if (target == null)
            {
                WarnThrottled($"Ghost action {msg.action} for {msg.targetMeshId} dropped: not owned here");
                return;
            }
            if (!_ghostActionHandlers.TryGetValue(msg.action, out GhostActionHandler handler))
            {
                WarnThrottled($"Ghost action {msg.action} has no handler");
                return;
            }
            EntityInfo instigator = ResolveInstigator(msg.instigatorMeshId);
            handler(target, instigator, new NetDataReader(msg.payload ?? System.Array.Empty<byte>()));
        }

        /// <summary>The real entity for a mesh id when this cell owns it (players by character id, monsters by mesh id).</summary>
        private BaseCharacterEntity ResolveOwnedCharacter(string meshId)
        {
            if (string.IsNullOrEmpty(meshId))
                return null;
            if (_ownedCharacters.TryGet(meshId, out BaseCharacterEntity owned) && owned != null)
                return owned;
            if (ServerUserHandlers.TryGetPlayerCharacterById(meshId, out IPlayerCharacterData data) && data is BasePlayerCharacterEntity player)
                return player;
            return null;
        }

        /// <summary>Who did it, as this cell knows them: their ghost here, their real entity here, or nobody.</summary>
        private EntityInfo ResolveInstigator(string meshId)
        {
            BaseCharacterEntity entity = ResolveGhostOrReal(meshId);
            return entity != null ? entity.GetInfo() : EntityInfo.Empty;
        }

        // ---- replays: owner to puppets ----

        /// <summary>Owner side: replay something on every puppet of an owned character (a no-op when nobody streams it).</summary>
        internal void ForwardGhostReplay(BaseCharacterEntity actor, GhostReplayKind kind, uint targetObjectId, System.Action<NetDataWriter> writePayload)
        {
            if (!IsServer || actor == null)
                return;
            string meshId = MeshEntityIdOf(actor);
            string arrivedFrom = RecentOriginNotStreaming(meshId);
            if (_streamedToZone.Count == 0 && arrivedFrom == null)
                return;
            _actionWriter.Reset();
            writePayload?.Invoke(_actionWriter);
            GhostReplayMessage replay = new GhostReplayMessage
            {
                actorMeshId = meshId,
                kind = (ushort)kind,
                targetMeshId = MeshIdOfObject(targetObjectId),
                payload = _actionWriter.CopyData(),
            };
            SendToZonesStreaming(meshId, MeshLinkMessageTypes.GhostReplay, replay);
            if (arrivedFrom != null && _linkClients.TryGetValue(arrivedFrom, out MeshLinkClient origin) && origin.IsConnected)
                origin.Send(MeshLinkMessageTypes.GhostReplay, replay);
        }

        private const float RecentOriginSeconds = 1f;
        private readonly Dictionary<string, KeyValuePair<string, float>> _recentOrigins = new Dictionary<string, KeyValuePair<string, float>>();
        private readonly List<string> _recentOriginsGone = new List<string>();

        /// <summary>An arrival from <paramref name="zone"/>, and the ones whose second is up forgotten: most never replay anything.</summary>
        private void NoteRecentOrigin(string meshId, string zone)
        {
            float now = Time.unscaledTime;
            _recentOriginsGone.Clear();
            foreach (KeyValuePair<string, KeyValuePair<string, float>> kv in _recentOrigins)
                if (now >= kv.Value.Value)
                    _recentOriginsGone.Add(kv.Key);
            for (int i = 0; i < _recentOriginsGone.Count; ++i)
                _recentOrigins.Remove(_recentOriginsGone[i]);
            _recentOrigins[meshId] = new KeyValuePair<string, float>(zone, now + RecentOriginSeconds);
        }

        /// <summary>
        /// The cell an arrival just left, while this cell is not streaming it there yet (DESIGN 1.35). Its puppet there still
        /// shows the character, and a replay sent to nobody left a charge's stop unheard: it stayed in the charge pose.
        /// </summary>
        private string RecentOriginNotStreaming(string meshId)
        {
            if (!_recentOrigins.TryGetValue(meshId, out KeyValuePair<string, float> origin))
                return null;
            if (Time.unscaledTime >= origin.Value)
            {
                _recentOrigins.Remove(meshId);
                return null;
            }
            return _streamedToZone.TryGetValue(origin.Key, out HashSet<string> streamed) && streamed.Contains(meshId) ? null : origin.Key;
        }

        private Vector3 _replayFrameOffset; // the sender's frame relative to ours, for the replay being handled

        private AimPosition TranslateAim(AimPosition aim)
        {
            if (aim.type == AimPositionType.Position)
                aim.position += _replayFrameOffset;
            return aim;
        }

        private SimulateActionTriggerData TranslateTrigger(SimulateActionTriggerData data)
        {
            data.aimPosition = TranslateAim(data.aimPosition);
            return data;
        }

        internal void OnGhostReplay(long connectionId, GhostReplayMessage msg)
        {
            if (!IsServer)
                return;
            _replayFrameOffset = OffsetFrom(connectionId);
            // Not on a puppet that is mid-promotion or dead: the kit would reject the RPC on the client anyway.
            if (!_ghosts.TryGetValue(msg.actorMeshId, out MeshGhost ghost) || ghost.entity == null || ghost.reserved || ghost.entity.IsDead())
                return;
            if (!_ghostReplayHandlers.TryGetValue(msg.kind, out GhostReplayHandler handler))
            {
                WarnThrottled($"Ghost replay {msg.kind} has no handler");
                return;
            }
            handler(ghost.entity, ObjectIdOfMesh(msg.targetMeshId), new NetDataReader(msg.payload ?? System.Array.Empty<byte>()));
        }

        private void HandleAttackReplay(BaseCharacterEntity puppet, uint targetObjectId, NetDataReader payload)
        {
            if (!puppet.TryGetComponent(out MeshCharacterAttackComponent attack))
                return;
            attack.ReplayAttack(payload.GetLong(), (WeaponHandlingState)payload.GetByte());
            if (traceHandoffTiming)
                Logging.Log("Mesh", $"Ghost attack replayed on {puppet.CharacterName}");
        }

        private void HandleAttackTriggerReplay(BaseCharacterEntity puppet, uint targetObjectId, NetDataReader payload)
        {
            if (puppet.TryGetComponent(out MeshCharacterAttackComponent attack))
                attack.ReplayTrigger(TranslateTrigger(payload.Get<SimulateActionTriggerData>()));
        }

        private void HandleSkillReplay(BaseCharacterEntity puppet, uint targetObjectId, NetDataReader payload)
        {
            if (!puppet.TryGetComponent(out MeshCharacterUseSkillComponent skill))
                return;
            long peerTimestamp = payload.GetLong();
            bool isItem = payload.GetBool();
            int dataId = payload.GetInt();
            int skillLevel = payload.GetInt();
            WeaponHandlingState weaponHandlingState = (WeaponHandlingState)payload.GetByte();
            AimPosition aimPosition = TranslateAim(payload.Get<AimPosition>());
            if (isItem)
                skill.ReplayUseSkillItem(peerTimestamp, dataId, weaponHandlingState, targetObjectId, aimPosition);
            else
                skill.ReplayUseSkill(peerTimestamp, dataId, skillLevel, weaponHandlingState, targetObjectId, aimPosition);
            if (traceHandoffTiming)
                Logging.Log("Mesh", $"Ghost {(isItem ? "skill item" : "skill")} {dataId} replayed on {puppet.CharacterName} target={targetObjectId}");
        }

        private void HandleSkillTriggerReplay(BaseCharacterEntity puppet, uint targetObjectId, NetDataReader payload)
        {
            if (!puppet.TryGetComponent(out MeshCharacterUseSkillComponent skill))
                return;
            SimulateActionTriggerData data = TranslateTrigger(payload.Get<SimulateActionTriggerData>());
            data.targetObjectId = targetObjectId;
            skill.ReplayTrigger(data);
        }

        private void HandleChargeStartReplay(BaseCharacterEntity puppet, uint targetObjectId, NetDataReader payload)
        {
            if (puppet.TryGetComponent(out MeshCharacterChargeComponent charge))
                charge.ReplayStart(payload.GetBool());
        }

        private void HandleChargeStopReplay(BaseCharacterEntity puppet, uint targetObjectId, NetDataReader payload)
        {
            if (puppet.TryGetComponent(out MeshCharacterChargeComponent charge))
                charge.ReplayStop();
        }

        // ---- action handlers: puppet to owner ----

        private void HandleApplyBuffAction(BaseCharacterEntity target, EntityInfo instigator, NetDataReader payload)
        {
            int dataId = payload.GetInt();
            BuffType type = (BuffType)payload.GetByte();
            int level = payload.GetInt();
            CharacterItem weapon = payload.Get<CharacterItem>();
            target.ApplyBuff(dataId, type, level, instigator, weapon);
            if (traceHandoffTiming)
                Logging.Log("Mesh", $"Ghost action ApplyBuff applied on {target.CharacterName}: {type} {dataId} lv{level} (buffs={target.Buffs.Count})");
        }

        private void HandleGiveItemsAction(BaseCharacterEntity target, EntityInfo instigator, NetDataReader payload)
        {
            int dataId = payload.GetInt();
            int amount = payload.GetInt();
            int exp = payload.GetInt();
            if (amount > 0 && dataId != 0)
            {
                CharacterItem item = CharacterItem.Create(dataId, 1, amount);
                if (target.IncreasingItemsWillOverwhelming(dataId, amount))
                    ItemDropEntity.Drop(target, RewardGivenType.Harvestable, item, new[] { target.Id }).Forget(); // as the kit does when the bag is full
                else
                {
                    target.IncreaseItems(item, characterItem => target.OnRewardItem(RewardGivenType.Harvestable, characterItem));
                    target.FillEmptySlots();
                }
            }
            if (exp > 0)
                target.RewardExp(exp, 1, RewardGivenType.Harvestable, 1, 1);
            if (traceHandoffTiming)
                Logging.Log("Mesh", $"Ghost action GiveItems applied on {target.CharacterName}: item {dataId} x{amount}, exp {exp}");
        }

        /// <summary>A local chat said within range of this cell's player by someone standing across the line: delivered to the player's client as the kit would (DESIGN 1.37).</summary>
        private void HandleChatAction(BaseCharacterEntity target, EntityInfo instigator, NetDataReader payload)
        {
            ChatMessage message = payload.Get<ChatMessage>();
            if (!(target is BasePlayerCharacterEntity player) || player.ConnectionId < 0)
                return;
            ServerSendPacket(player.ConnectionId, 0, DeliveryMethod.ReliableUnordered, GameNetworkingConsts.Chat, message);
        }

        /// <summary>
        /// The victim's cell, after a killing hit from across the line: what the kit just credited to the attacker's puppet goes to
        /// the attacker's owner, and the puppet is put back as it was (the owner's state reaches it with the next snapshot anyway).
        /// </summary>
        private void ForwardPkCredit(BasePlayerCharacterEntity attackerPuppet, BaseCharacterEntity victim, PkCredit before)
        {
            PkCredit after = PkCredit.Of(attackerPuppet);
            int pkDelta = after.pkPoint - before.pkPoint, killsDelta = after.consecutiveKills - before.consecutiveKills;
            before.Restore(attackerPuppet);
            if (pkDelta == 0 && killsDelta == 0)
                return;
            if (ForwardGhostAction(attackerPuppet, GhostActionKind.PkKill, victim.GetInfo(), writer => { writer.Put(pkDelta); writer.Put(killsDelta); }))
                Logging.Log("Mesh", $"PK credit for {attackerPuppet.CharacterName}: +{pkDelta} point(s), +{killsDelta} kill(s) for killing {victim.CharacterName} across a line, sent to its owner");
        }

        /// <summary>The attacker's cell: the kit's PK credit for a kill across the line, as Killed would have written it here.</summary>
        private void HandlePkKillAction(BaseCharacterEntity target, EntityInfo instigator, NetDataReader payload)
        {
            int pkDelta = payload.GetInt(), killsDelta = payload.GetInt();
            if (!(target is BasePlayerCharacterEntity player))
                return;
            player.PkPoint += pkDelta;
            player.ConsecutivePkKills += killsDelta;
            if (player.PkPoint > player.HighestPkPoint)
                player.HighestPkPoint = player.PkPoint;
            if (player.ConsecutivePkKills > player.HighestConsecutivePkKills)
                player.HighestConsecutivePkKills = player.ConsecutivePkKills;
            Logging.Log("Mesh", $"PK credit applied on {player.CharacterName}: {player.PkPoint} point(s), {player.ConsecutivePkKills} consecutive kill(s)");
        }

        /// <summary>Entity-side helper: the payload of a GiveItems action.</summary>
        internal static void WriteGiveItemsPayload(NetDataWriter writer, int dataId, int amount, int exp)
        {
            writer.Put(dataId);
            writer.Put(amount);
            writer.Put(exp);
        }

        /// <summary>Entity-side helper: the payload of an ApplyBuff action.</summary>
        internal static void WriteApplyBuffPayload(NetDataWriter writer, int dataId, BuffType type, int level, CharacterItem weapon)
        {
            writer.Put(dataId);
            writer.Put((byte)type);
            writer.Put(level);
            writer.Put(weapon);
        }
#else
        internal bool ForwardGhostAction(BaseCharacterEntity ghost, GhostActionKind kind, EntityInfo instigator, System.Action<NetDataWriter> writePayload) { return false; }
        internal void OnGhostAction(long connectionId, GhostActionMessage msg) { }
        internal void ForwardGhostReplay(BaseCharacterEntity actor, GhostReplayKind kind, uint targetObjectId, System.Action<NetDataWriter> writePayload) { }
        internal void OnGhostReplay(long connectionId, GhostReplayMessage msg) { }
        internal static void WriteApplyBuffPayload(NetDataWriter writer, int dataId, BuffType type, int level, CharacterItem weapon) { }
        internal static void WriteGiveItemsPayload(NetDataWriter writer, int dataId, int amount, int exp) { }
#endif
    }
}
