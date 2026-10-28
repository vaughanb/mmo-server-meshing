// Server Meshing add-on — commands a crossing client sends inside the gap (DESIGN.md 1.07).
//
// From the warp until the client adopts the destination's character, the client still owns its kept character (CE-14
// keeps its connection id) and the kit sends its commands as usual: an attack plays at once on the client and its
// CmdAttack goes to the destination. The destination dropped every one (7 of 7 in the measuring run), twice over: the
// call names the object id the character had on the origin, which is not the id the destination gave it, and until
// the client's ready the arriving entity has no owner, while the kit only accepts an owner's call. The swing played on
// the client and never happened; its trigger found no hit validation either.
//
// The origin sends the character's object id with the handoff state. The destination parks every client call from a
// connection with no character registered (the kit would drop it; it can come before the origin's notice, so the
// target cannot be told yet), bounded per connection and dropped with the connection, and replays the connection's
// parked calls in order once a character registers for it: a call naming the character's id on the origin is
// retargeted to its id here. Until the client has adopted the new id (arrivalIdRemapSeconds), a registered
// connection's call that still names the origin's id is retargeted the same way. Every call still goes through the
// kit's handler, whose owner check decides: parking and retargeting grant nothing.
//
// A call that reaches the origin for a character it has just demoted (sent before the client read its warp, one trip
// from the client to the origin long) is forwarded over the mesh link and replayed at the destination ahead of the
// parked ones. Not covered: a call still queued on the client's old connection when the warp closes it (LiteNetLib's
// Stop shuts its peers before their logic thread's last send, up to one 15 ms update of calls).
using System.Collections.Generic;
using LiteNetLib.Utils;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class MeshMapNetworkManager
    {
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        private struct ParkedRpc { public ushort messageType; public TransportHandler transportHandler; public byte[] data; public float time; }
        private struct ArrivalIdRemap { public uint from; public float until; }
        private readonly Dictionary<long, List<ParkedRpc>> _parkedRpcs = new Dictionary<long, List<ParkedRpc>>();
        private readonly Dictionary<long, ArrivalIdRemap> _arrivalIdRemaps = new Dictionary<long, ArrivalIdRemap>();
        private static readonly NetDataWriter s_retargetWriter = new NetDataWriter();
        [Tooltip("Destination: client RPCs parked for one arriving connection at most (older ones are dropped).")]
        public int maxParkedRpcsPerConnection = 64;
        [Tooltip("Destination: how long after an arrival registers a client RPC naming the character's object id on the origin is still retargeted to its id here (the client adopts the new id one trip after the spawn).")]
        public float arrivalIdRemapSeconds = 5f;
        private readonly Dictionary<string, List<ParkedRpc>> _lateRpcs = new Dictionary<string, List<ParkedRpc>>(); // characterId -> calls the origin forwarded
        private int _parkedTotal, _retargetedTotal, _lateForwardedTotal;

        protected override void HandleClientCallFunction(MessageHandlerData messageHandler)
        {
            if (IsServer && PeekRpcTarget(messageHandler.Reader, out uint objectId))
            {
                long connectionId = messageHandler.ConnectionId;
                if (ServerUserHandlers.TryGetPlayerCharacter(connectionId, out IPlayerCharacterData registered))
                {
                    if (registered is BasePlayerCharacterEntity player && _arrivalIdRemaps.TryGetValue(connectionId, out ArrivalIdRemap remap)
                        && remap.from == objectId && objectId != player.ObjectId)
                    {
                        if (Time.unscaledTime < remap.until)
                        {
                            _retargetedTotal++;
                            base.HandleClientCallFunction(Retarget(messageHandler.MessageType, messageHandler.TransportHandler, connectionId, CopyRest(messageHandler.Reader), player.ObjectId));
                            return;
                        }
                        _arrivalIdRemaps.Remove(connectionId);
                    }
                }
                else if (Assets.TryGetSpawnedObject(objectId, out LiteNetLibIdentity identity) && identity.TryGetComponent(out BasePlayerCharacterEntity demoted)
                    && MeshIdentity.IsGhostEntity(demoted) && _pendingHandoffAcks.TryGetValue(demoted.Id, out PendingHandoffAck pending))
                {
                    // Sent to us before the client read its warp: the character is the destination's now, so the call goes there.
                    _lateForwardedTotal++;
                    bool sent = _linkClients.TryGetValue(pending.zone, out MeshLinkClient link) && link.IsConnected;
                    if (sent)
                        link.SendHandoffLateRpc(new HandoffLateRpcMessage { characterId = demoted.Id, messageType = messageHandler.MessageType, data = CopyRest(messageHandler.Reader) });
                    Stamp($"RPC from {connectionId} for {demoted.CharacterName} arrived after its demotion; {(sent ? $"forwarded to {pending.zone}" : "dropped (no link)")} ({_lateForwardedTotal} so far)");
                    return;
                }
                else
                {
                    // No character registered for the caller: the kit would drop the call (nothing here is its to call).
                    // The arriving client's calls can come before the origin's notice does (the client's path is one
                    // frame quicker: 2 of 7 in the run that found it), so whatever the call names is parked, and the
                    // replay sorts it out when, and if, a character registers for the connection.
                    ParkRpc(messageHandler, TryFindArrival(objectId, out string arrivingName) ? arrivingName : $"object {objectId}");
                    return;
                }
            }
            base.HandleClientCallFunction(messageHandler);
        }

        /// <summary>The object id a client RPC is for, read from a copy (the kit's handler reads the original).</summary>
        private static bool PeekRpcTarget(NetDataReader reader, out uint objectId)
        {
            objectId = 0;
            if (reader == null || reader.AvailableBytes < 2)
                return false;
            NetDataReader peek = new NetDataReader(reader.RawData, reader.Position, reader.Position + reader.AvailableBytes); // the last argument is the end index
            try
            {
                RPCReceivers receivers = (RPCReceivers)peek.GetByte();
                if (receivers == RPCReceivers.Target)
                    peek.GetPackedLong();
                objectId = LiteNetLibElementInfo.DeserializeInfo(peek).objectId;
                return true;
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// A character announced to arrive here (a handoff notice within the reservation window) that the call names, by
        /// the id it had on the origin or by its id here. Who the caller is cannot be told before its enter-game (the call
        /// can overtake it on another channel), and need not be: the replay checks ownership.
        /// </summary>
        private bool TryFindArrival(uint objectId, out string characterName)
        {
            characterName = null;
            foreach (KeyValuePair<string, IncomingHandoff> kv in _incomingHandoffFrom)
            {
                if (Time.unscaledTime - kv.Value.time >= reservedArrivalTimeout)
                    continue;
                if (kv.Value.originObjectId != 0 && kv.Value.originObjectId == objectId)
                {
                    characterName = kv.Value.state?.CharacterName ?? kv.Key;
                    return true;
                }
            }
            if (Assets.TryGetSpawnedObject(objectId, out LiteNetLibIdentity identity) && identity.TryGetComponent(out BasePlayerCharacterEntity player)
                && _incomingHandoffFrom.TryGetValue(player.Id, out IncomingHandoff incoming) && Time.unscaledTime - incoming.time < reservedArrivalTimeout)
            {
                characterName = player.CharacterName;
                return true;
            }
            return false;
        }

        private void ParkRpc(MessageHandlerData messageHandler, string characterName)
        {
            if (!_parkedRpcs.TryGetValue(messageHandler.ConnectionId, out List<ParkedRpc> parked))
                _parkedRpcs[messageHandler.ConnectionId] = parked = new List<ParkedRpc>();
            while (parked.Count > 0 && (parked.Count >= Mathf.Max(1, maxParkedRpcsPerConnection) || Time.unscaledTime - parked[0].time >= reservedArrivalTimeout))
                parked.RemoveAt(0);
            parked.Add(new ParkedRpc { messageType = messageHandler.MessageType, transportHandler = messageHandler.TransportHandler, data = CopyRest(messageHandler.Reader), time = Time.unscaledTime });
            _parkedTotal++;
            Stamp($"RPC from {messageHandler.ConnectionId} for arriving {characterName} parked until it registers ({parked.Count} waiting)");
        }

        /// <summary>Arrival side, as the character registers: the id its client still names it by, retargeted for a while.</summary>
        private void NoteArrivalObjectId(BasePlayerCharacterEntity playerCharacter, long connectionId)
        {
            if (_incomingHandoffFrom.TryGetValue(playerCharacter.Id, out IncomingHandoff incoming) && incoming.originObjectId != 0)
                _arrivalIdRemaps[connectionId] = new ArrivalIdRemap { from = incoming.originObjectId, until = Time.unscaledTime + Mathf.Max(0f, arrivalIdRemapSeconds) };
        }

        /// <summary>
        /// After a character registered for the connection (owner applied): its parked calls, in the order they came. A
        /// call naming the character's id on the origin is retargeted to its id here; any other goes to the kit as it is,
        /// whose owner check drops a call that was never this character's.
        /// </summary>
        private void ReplayParkedRpcs(long connectionId, BasePlayerCharacterEntity player)
        {
            if (player == null)
                return;
            // The origin's forwarded calls first: the client sent them before anything it sent here.
            if (_lateRpcs.TryGetValue(player.Id, out List<ParkedRpc> late))
            {
                _lateRpcs.Remove(player.Id);
                ReplayLateRpcs(connectionId, player, late);
            }
            if (!_parkedRpcs.TryGetValue(connectionId, out List<ParkedRpc> parked))
                return;
            _parkedRpcs.Remove(connectionId);
            uint originId = _arrivalIdRemaps.TryGetValue(connectionId, out ArrivalIdRemap remap) ? remap.from : 0;
            float oldest = parked.Count > 0 ? Time.unscaledTime - parked[0].time : 0f;
            int retargeted = 0;
            for (int i = 0; i < parked.Count; ++i)
            {
                ParkedRpc held = parked[i];
                if (originId != 0 && originId != player.ObjectId && PeekRpcTarget(new NetDataReader(held.data), out uint target) && target == originId)
                {
                    retargeted++;
                    base.HandleClientCallFunction(Retarget(held.messageType, held.transportHandler, connectionId, held.data, player.ObjectId));
                }
                else
                    base.HandleClientCallFunction(new MessageHandlerData(held.messageType, held.transportHandler, connectionId, new NetDataReader(held.data)));
            }
            Stamp($"arrival {player.CharacterName}: replayed {parked.Count} RPC(s) parked for up to {oldest * 1000f:0} ms ({retargeted} from object {originId} to {player.ObjectId})");
        }

        /// <summary>
        /// Destination side: a call the origin received for the character after demoting it. Replayed when the character
        /// registers here (ahead of its connection's parked calls), or now when it already has.
        /// </summary>
        internal void OnHandoffLateRpc(HandoffLateRpcMessage msg)
        {
            if (!IsServer || string.IsNullOrEmpty(msg.characterId) || msg.data == null)
                return;
            ParkedRpc call = new ParkedRpc { messageType = msg.messageType, transportHandler = Server, data = msg.data, time = Time.unscaledTime };
            if (ServerUserHandlers.TryGetConnectionIdById(msg.characterId, out long connectionId)
                && ServerUserHandlers.TryGetPlayerCharacter(connectionId, out IPlayerCharacterData registered) && registered is BasePlayerCharacterEntity player)
            {
                ReplayLateRpcs(connectionId, player, new List<ParkedRpc> { call });
                return;
            }
            if (!_lateRpcs.TryGetValue(msg.characterId, out List<ParkedRpc> late))
                _lateRpcs[msg.characterId] = late = new List<ParkedRpc>();
            while (late.Count > 0 && (late.Count >= Mathf.Max(1, maxParkedRpcsPerConnection) || Time.unscaledTime - late[0].time >= reservedArrivalTimeout))
                late.RemoveAt(0);
            late.Add(call);
            Stamp($"RPC forwarded by the origin for arriving {msg.characterId} parked until it registers ({late.Count} waiting)");
        }

        /// <summary>Calls the origin forwarded: they name the character's id there, so all go to its id here.</summary>
        private void ReplayLateRpcs(long connectionId, BasePlayerCharacterEntity player, List<ParkedRpc> late)
        {
            for (int i = 0; i < late.Count; ++i)
                base.HandleClientCallFunction(Retarget(late[i].messageType, late[i].transportHandler, connectionId, late[i].data, player.ObjectId));
            Stamp($"arrival {player.CharacterName}: replayed {late.Count} RPC(s) the origin forwarded, as object {player.ObjectId}");
        }

        private static byte[] CopyRest(NetDataReader reader)
        {
            byte[] data = new byte[reader.AvailableBytes];
            System.Buffer.BlockCopy(reader.RawData, reader.Position, data, 0, data.Length);
            return data;
        }

        /// <summary>The call in <paramref name="data"/> (an RPC message body) addressed to <paramref name="objectId"/>, the rest unchanged.</summary>
        private static MessageHandlerData Retarget(ushort messageType, TransportHandler transportHandler, long connectionId, byte[] data, uint objectId)
        {
            NetDataReader reader = new NetDataReader(data);
            byte receivers = reader.GetByte();
            long target = (RPCReceivers)receivers == RPCReceivers.Target ? reader.GetPackedLong() : 0;
            LiteNetLibElementInfo info = LiteNetLibElementInfo.DeserializeInfo(reader);
            s_retargetWriter.Reset();
            s_retargetWriter.Put(receivers);
            if ((RPCReceivers)receivers == RPCReceivers.Target)
                s_retargetWriter.PutPackedLong(target);
            info.objectId = objectId;
            LiteNetLibElementInfo.SerializeInfo(info, s_retargetWriter);
            s_retargetWriter.Put(data, reader.Position, reader.AvailableBytes);
            return new MessageHandlerData(messageType, transportHandler, connectionId, new NetDataReader(s_retargetWriter.CopyData()));
        }

        private void DropParkedRpcs(long connectionId)
        {
            _parkedRpcs.Remove(connectionId);
            _arrivalIdRemaps.Remove(connectionId);
        }
#else
        private void ReplayParkedRpcs(long connectionId, BasePlayerCharacterEntity player) { }
        private void DropParkedRpcs(long connectionId) { }
        internal void OnHandoffLateRpc(HandoffLateRpcMessage msg) { }
#endif
    }
}
