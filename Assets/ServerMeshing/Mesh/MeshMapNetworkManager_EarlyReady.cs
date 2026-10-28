// Server Meshing add-on — a hop's ready goes with its enter-game (DESIGN.md 1.13).
//
// A hop's handshake was a round trip and a half: the client sent enter-game on taking over its warm connection, the
// destination read it a server frame later and answered, and only on that answer did the client send ready, which the
// destination read a frame later again before it registered the player (32 ms of a 61 ms gap on one machine, and a
// round trip more with latency). A hop that keeps the scene needs nothing from the answer to be ready: the client's
// character, assets and scene are the ones it has. So on a hop over a warm connection to a cell of the same map (the
// pre-connect hint says which), the client sends ready right behind enter-game, and the destination registers the
// player in the frame it reads both.
//
// Client: the ready the answer would send (the mesh's same-scene path sends it twice) is skipped for the rest of the
// hop, and the server's state messages that come before the answer is handled wait for it: a spawn adopted before the
// answer rebound the kept character to its new connection id would be taken for someone else's. Server: requests
// travel unordered, so a ready can come before its connection's enter-game is done (or before it is read at all);
// it waits, up to earlyReadyWaitSeconds, until the enter-game has registered the connection's user and character.
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using LiteNetLib.Utils;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class MeshMapNetworkManager
    {
        [Tooltip("Client: on a hop over a warm connection to a cell of the same map, send ready right behind enter-game instead of after its answer (DESIGN 1.13).")]
        public bool pipelineReadyOnHop = true;

        [Tooltip("Server: how long a ready that came before its connection's enter-game finished waits for it, in seconds.")]
        public float earlyReadyWaitSeconds = 3f;

        /// <summary>Client: set by the migration controller just before it takes over a warm connection to a cell of the same map.</summary>
        internal bool PipelineReadyOnNextConnect { get; set; }

        private struct HeldState { public ushort messageType; public TransportHandler transportHandler; public long connectionId; public byte[] data; }
        private readonly List<HeldState> _heldStates = new List<HeldState>();
        private bool _readySentWithEnterGame;   // client: every other ready of this hop is skipped
        private bool _holdStatesForEnterGame;   // client: server states wait for the enter-game answer

        /// <summary>Client, after OnClientConnected sent enter-game: on a pipelined hop, the ready goes now.</summary>
        private void SendReadyWithEnterGame()
        {
            bool pipeline = PipelineReadyOnNextConnect;
            PipelineReadyOnNextConnect = false;
            _readySentWithEnterGame = false;
            _holdStatesForEnterGame = false;
            _heldStates.Clear();
            if (!pipeline || !pipelineReadyOnHop || IsServer || !IsMigratingClient)
                return;
            _holdStatesForEnterGame = true;
            Stamp("send client ready with the enter-game");
            base.SendClientReady();
            _readySentWithEnterGame = true;
        }

        /// <summary>Client: true when this ready is one the pipelined hop already sent.</summary>
        private bool SkipReadySentWithEnterGame()
        {
            if (!_readySentWithEnterGame)
                return false;
            Stamp("ready already sent with the enter-game");
            return true;
        }

        /// <summary>Client, the hop is over (or failed): back to the kit's handshake.</summary>
        private void EndReadyWithEnterGame()
        {
            _readySentWithEnterGame = false;
            ReleaseHeldStates(false);
        }

        protected override void HandleServerSyncBaseLine(MessageHandlerData messageHandler)
        {
            if (HoldState(messageHandler))
                return;
            base.HandleServerSyncBaseLine(messageHandler);
        }

        protected override void HandleServerSyncDelta(MessageHandlerData messageHandler)
        {
            if (HoldState(messageHandler))
                return;
            base.HandleServerSyncDelta(messageHandler);
        }

        private bool HoldState(MessageHandlerData messageHandler)
        {
            if (!_holdStatesForEnterGame || IsServer)
                return false;
            NetDataReader reader = messageHandler.Reader;
            byte[] data = new byte[reader.AvailableBytes];
            System.Buffer.BlockCopy(reader.RawData, reader.Position, data, 0, data.Length);
            _heldStates.Add(new HeldState { messageType = messageHandler.MessageType, transportHandler = messageHandler.TransportHandler, connectionId = messageHandler.ConnectionId, data = data });
            return true;
        }

        /// <summary>Client, after the enter-game answer was handled: the server states that came before it, in order (dropped when it failed).</summary>
        private void ReleaseHeldStates(bool deliver)
        {
            if (!_holdStatesForEnterGame && _heldStates.Count == 0)
                return;
            _holdStatesForEnterGame = false;
            if (deliver && _heldStates.Count > 0)
            {
                Stamp($"delivering {_heldStates.Count} server state(s) held for the enter-game answer");
                for (int i = 0; i < _heldStates.Count; ++i)
                {
                    HeldState held = _heldStates[i];
                    MessageHandlerData data = new MessageHandlerData(held.messageType, held.transportHandler, held.connectionId, new NetDataReader(held.data));
                    if (held.messageType == GameMsgTypes.SyncDelta)
                        base.HandleServerSyncDelta(data);
                    else
                        base.HandleServerSyncBaseLine(data);
                }
            }
            _heldStates.Clear();
        }

#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        /// <summary>Server: the connection's enter-game has registered its user and the character it will spawn.</summary>
        private bool ReadyCanProceed(long connectionId)
        {
            return ServerUserHandlers.TryGetUserId(connectionId, out string userId) && ServerUserHandlers.TryGetAccessToken(connectionId, out _)
                && _pendingSpawnPlayerCharactersByUserId.ContainsKey(userId);
        }

        /// <summary>Server: a ready that came before its enter-game finished, with its payload copied (the kit's reader is not ours past this frame).</summary>
        private async UniTask<bool> WaitForEnterGameThenReady(uint requestId, long connectionId, NetDataReader reader, LiteNetLibIdentity playerIdentity)
        {
            NetDataReader copy = new NetDataReader(CopyRest(reader));
            float deadline = Time.unscaledTime + Mathf.Max(0f, earlyReadyWaitSeconds);
            int frames = 0;
            while (!ReadyCanProceed(connectionId) && ContainsConnectionId(connectionId) && Time.unscaledTime < deadline)
            {
                await UniTask.Yield();
                frames++;
            }
            Stamp($"client ready from {connectionId} waited {frames} frame(s) for its enter-game: {(ReadyCanProceed(connectionId) ? "done" : "not done")}");
            return await ClientReadyAfterEnterGame(requestId, connectionId, copy, playerIdentity);
        }
#endif
    }
}
