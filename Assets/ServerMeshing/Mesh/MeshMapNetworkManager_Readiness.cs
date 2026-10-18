// Server Meshing add-on — a restarted cell of a Dynamic map and its neighbours (Slice 2d follow-up to splitcrash_drain).
// (1) The mesh link is not pumped until the server scene is ready: a restarted cell is linked by its neighbours within
// milliseconds of listening, and the snapshots they stream would spawn puppets before the kit's Assets.Initialize, whose
// RegisterSceneObjects takes every root identity of the loaded scenes for a scene object: the puppets were re-spawned as
// scene objects no client has, and the kit's spawn-state reader then lost the rest of the batch (the player's own
// character among it), so every client that came there timed out. Drop snapshots also met the kit's game data before it
// was set up, and handoffs met prefabs not registered yet. The held link events are read once OnServerOnlineSceneLoaded
// has run (every spawn area's prefabs registered), nothing is dropped. Since DESIGN 0.79 the hold (MeshLinkHold) is on
// every map: a Static map's cell met the same early puppets when a neighbour still held its address. (2) A player that
// comes back to this cell without a handoff notice shortly after this cell handed it out (the client fell back, or
// re-logged in) is not handed again at once: every arrival waits rehandoffCooldownSeconds, and each such return doubles
// it up to rehandoffBackoffMaxSeconds.
// (3) A neighbour's registration report is no longer live once the connection it came over is lost (an old incarnation's
// "registered" would otherwise make its restarted successor a handoff target). (2) and (3): Dynamic maps only.
using System;
using System.Collections.Generic;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>MeshRehandoffRule is the pure part of the re-handoff back-off of a Dynamic map's cell.</summary>
    public static class MeshRehandoffRule
    {
        /// <summary>An arrival this long after this cell handed the player out, without a notice, is a return.</summary>
        public const float ReturnWindowSeconds = 30f;

        /// <summary>IsReturn: handed out by this cell within the window and back without a handoff notice (a fallback or a re-login).</summary>
        public static bool IsReturn(bool handedOut, float handedOutAt, bool announced, float now)
        {
            return handedOut && !announced && now - handedOutAt >= 0f && now - handedOutAt <= ReturnWindowSeconds;
        }

        /// <summary>HoldSeconds: the cooldown after any arrival; the n-th consecutive return doubles it n-1 times, capped.</summary>
        public static float HoldSeconds(int returns, float cooldown, float max)
        {
            float hold = Mathf.Max(0f, cooldown);
            for (int i = 1; i < returns && hold < max; ++i)
                hold *= 2f;
            return Mathf.Min(hold, Mathf.Max(max, cooldown));
        }
    }

    public partial class MeshMapNetworkManager
    {
        [Tooltip("Dynamic maps: seconds a player that arrived on this cell is not handed to another (a client that fell back is not sent straight back).")]
        public float rehandoffCooldownSeconds = 2f;
        [Tooltip("Dynamic maps: the cap of the doubling hold for a player that keeps coming back to this cell without a notice after its handoffs.")]
        public float rehandoffBackoffMaxSeconds = 30f;

#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        /// <summary>Handed-out records kept at most; older ones than the return window are pruned beyond this.</summary>
        private const int MaxHandoffOutRecords = 256;

        private struct HandoffOut { public string zone; public float at; public int returns; }

        private readonly Dictionary<string, HandoffOut> _handoffsOut = new Dictionary<string, HandoffOut>();
        private readonly List<string> _handoffsOutPrune = new List<string>();
        private readonly MeshLinkHold _linkHold = new MeshLinkHold(); // every map since 0.79

        /// <summary>OnStartServer's hook, before the kit's start: the scene of this start is not ready yet.</summary>
        private void ReadinessOnStartServer()
        {
            _linkHold.Start(Time.unscaledTime);
            _handoffsOut.Clear();
        }

        /// <summary>OnServerOnlineSceneLoaded's hook, after every spawn area's prefabs are registered: the link is read from now.</summary>
        private void ReadinessOnSceneLoaded()
        {
            if (!_linkHold.SceneReady())
                return;
            if (_linkHold.HeldFrames > 0)
                Logging.Log("Mesh", FormattableString.Invariant($"Mesh link of {PrintableCell(LayoutCellId)} read from now: the server scene is ready {Time.unscaledTime - _linkHold.StartedAt:0.0} s after start ({_linkHold.HeldFrames} frame(s) held, {MapModeText} map)"));
        }

        /// <summary>GhostingUpdate's hook: true while a map server holds its mesh link until the server scene is ready (any map since 0.79).</summary>
        private bool HoldLinksUntilSceneReady()
        {
            if (!_linkHold.Tick(IsServer, Time.unscaledTime, out bool warn))
                return false;
            if (warn)
                Logging.LogWarning("Mesh", FormattableString.Invariant($"Mesh link of {PrintableCell(LayoutCellId)} still held: the server scene is not ready {Time.unscaledTime - _linkHold.StartedAt:0} s after start ({MapModeText} map)"));
            return true;
        }

        private string MapModeText => IsDynamicMap ? "Dynamic" : "Static";

        /// <summary>RegisterPendingHandoffAck's hook: a Dynamic map's cell remembers whom it handed where (the return rule reads it).</summary>
        private void NoteHandoffOut(string characterId, string zone)
        {
            if (!IsDynamicMap || string.IsNullOrEmpty(characterId))
                return;
            float now = Time.unscaledTime;
            int returns = _handoffsOut.TryGetValue(characterId, out HandoffOut previous) && string.Equals(previous.zone, zone, StringComparison.Ordinal)
                && now - previous.at <= MeshRehandoffRule.ReturnWindowSeconds + rehandoffBackoffMaxSeconds ? previous.returns : 0;
            _handoffsOut[characterId] = new HandoffOut { zone = zone, at = now, returns = returns };
            if (_handoffsOut.Count <= MaxHandoffOutRecords)
                return;
            _handoffsOutPrune.Clear();
            foreach (KeyValuePair<string, HandoffOut> kv in _handoffsOut)
                if (now - kv.Value.at > MeshRehandoffRule.ReturnWindowSeconds + rehandoffBackoffMaxSeconds)
                    _handoffsOutPrune.Add(kv.Key);
            for (int i = 0; i < _handoffsOutPrune.Count; ++i)
                _handoffsOut.Remove(_handoffsOutPrune[i]);
            _handoffsOutPrune.Clear();
        }

        /// <summary>
        /// GhostingOnRealPlayerRegistered's hook, before the notice is consumed: on a Dynamic map every arrival is held
        /// rehandoffCooldownSeconds; a return (handed out by this cell a moment ago, back without a notice) is held longer
        /// with each consecutive return, so a client that fell back is not sent straight to the cell that failed it.
        /// </summary>
        private void RehandoffBackoffOnArrival(BasePlayerCharacterEntity player)
        {
            if (!IsDynamicMap || player == null || string.IsNullOrEmpty(player.Id))
                return;
            string characterId = player.Id;
            float now = Time.unscaledTime;
            bool handedOut = _handoffsOut.TryGetValue(characterId, out HandoffOut sent);
            bool returned = MeshRehandoffRule.IsReturn(handedOut, sent.at, _incomingHandoffFrom.ContainsKey(characterId), now);
            int returns = returned ? sent.returns + 1 : 0;
            float hold = MeshRehandoffRule.HoldSeconds(returns, rehandoffCooldownSeconds, rehandoffBackoffMaxSeconds);
            if (returned)
            {
                sent.returns = returns;
                _handoffsOut[characterId] = sent;
                Logging.LogWarning("Mesh", FormattableString.Invariant($"Handoff back-off: {player.CharacterName} came back to {PrintableCell(OwnZoneId)} {now - sent.at:0.0} s after its handoff to zone {PrintableCell(sent.zone)} without a notice (return {returns}); no handoff for {hold:0.#} s"));
            }
            else if (handedOut && _incomingHandoffFrom.ContainsKey(characterId))
                _handoffsOut.Remove(characterId); // handed back properly: the next handoff out starts a fresh count
            if (hold <= 0f)
                return;
            float until = now + hold;
            if (!_handoffRetryAfter.TryGetValue(characterId, out float retryAt) || retryAt < until)
                _handoffRetryAfter[characterId] = until;
        }

        /// <summary>OnMeshNeighbourDisconnected's hook: the registration report that came over this connection is no longer live.</summary>
        private void ForgetRegistrationReportOf(long connectionId)
        {
            if (!IsDynamicMap || !_neighbourZoneByConnection.TryGetValue(connectionId, out string zone) || string.IsNullOrEmpty(zone))
                return;
            if (!_neighbourRegistration.TryGetValue(zone, out NeighbourRegistration state) || !state.live || state.connectionId != connectionId)
                return; // a dead incarnation's late timeout: its successor's report came over another connection
            state.live = false;
            _neighbourRegistration[zone] = state;
            Logging.Log("Mesh", $"Neighbour zone {PrintableCell(zone)}: its registration report went with its link (connection {connectionId}); no handoff to it until it reports again");
        }
#else
        private void ReadinessOnStartServer() { }
        private void ReadinessOnSceneLoaded() { }
#endif
    }
}
