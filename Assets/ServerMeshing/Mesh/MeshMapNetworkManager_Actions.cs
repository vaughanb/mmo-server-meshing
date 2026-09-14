// Server Meshing add-on — MeshMapNetworkManager (generic ghost actions, DESIGN.md §12).
//
// Anything the kit does TO a target entity that is a ghost here (a buff, a debuff, a heal-over-time, a resurrection)
// must happen on the entity's owner instead. Entity subclasses intercept the kit call on a ghost and forward it as a
// GhostActionMessage; the owner resolves the target and the instigator (its ghost or real entity here) and dispatches
// by action id. Adding a verb = one payload writer on the ghost side + one handler here; no new message type, no stub.
using System.Collections.Generic;
using LiteNetLib.Utils;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public enum GhostActionKind : ushort
    {
        ApplyBuff = 1,
    }

    public partial class MeshMapNetworkManager
    {
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        private delegate void GhostActionHandler(BaseCharacterEntity target, EntityInfo instigator, NetDataReader payload);
        private readonly Dictionary<ushort, GhostActionHandler> _ghostActionHandlers = new Dictionary<ushort, GhostActionHandler>();
        private readonly NetDataWriter _actionWriter = new NetDataWriter();

        private void RegisterGhostActionHandlers()
        {
            _ghostActionHandlers.Clear();
            _ghostActionHandlers[(ushort)GhostActionKind.ApplyBuff] = HandleApplyBuffAction;
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
            if (_ownedByMeshId.TryGetValue(meshId, out BaseCharacterEntity owned) && owned != null)
                return owned;
            if (ServerUserHandlers.TryGetPlayerCharacterById(meshId, out IPlayerCharacterData data) && data is BasePlayerCharacterEntity player)
                return player;
            return null;
        }

        /// <summary>Who did it, as this cell knows them: their ghost here, their real entity here, or nobody.</summary>
        private EntityInfo ResolveInstigator(string meshId)
        {
            if (string.IsNullOrEmpty(meshId))
                return EntityInfo.Empty;
            if (_ghosts.TryGetValue(meshId, out MeshGhost ghost) && ghost.entity != null)
                return ghost.entity.GetInfo();
            BaseCharacterEntity real = ResolveOwnedCharacter(meshId);
            return real != null ? real.GetInfo() : EntityInfo.Empty;
        }

        // ---- handlers ----

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
        internal static void WriteApplyBuffPayload(NetDataWriter writer, int dataId, BuffType type, int level, CharacterItem weapon) { }
#endif
    }
}
