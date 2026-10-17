// Server Meshing add-on — the pure parts of the static handoff (Slice 2b, slices/slice2b_design.md decision 10): what a
// receiver does with a StaticHandoff, and a sender's book of statics in flight. MeshMapNetworkManager_StaticHandoff
// wires them to the server.
using System.Collections.Generic;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>MeshStaticReceipt is what a receiver does with a StaticHandoff.</summary>
    public enum MeshStaticReceipt
    {
        /// <summary>The receiver owns it already (a resend): ack again, change nothing.</summary>
        AckDuplicate,
        /// <summary>Its puppet stands here: make it the real one in place.</summary>
        Promote,
        /// <summary>Spawn it from the registered prefab.</summary>
        Spawn,
        /// <summary>Neither: answer StaticHandoffFailed.</summary>
        Fail,
    }

    /// <summary>MeshStaticHandoffRules holds the pure decisions of a static handoff.</summary>
    public static class MeshStaticHandoffRules
    {
        public static MeshStaticReceipt Receive(bool ownedAlready, bool puppetHere, bool prefabKnown)
        {
            if (ownedAlready)
                return MeshStaticReceipt.AckDuplicate;
            if (puppetHere)
                return MeshStaticReceipt.Promote;
            return prefabKnown ? MeshStaticReceipt.Spawn : MeshStaticReceipt.Fail;
        }
    }

    /// <summary>
    /// MeshStaticLoanBook is a sender's statics in flight, by mesh id: at most one loan per id, settled by the
    /// destination's ack, failed by its refusal (then a back-off before the same id is tried again), or taken back once
    /// older than the ack guard or when the destination restarts. Pure: the caller passes the clock.
    /// </summary>
    public sealed class MeshStaticLoanBook<T>
    {
        public sealed class Loan
        {
            public string meshId;
            public string toZone;
            public float sentAt;
            public T data;
        }

        private readonly Dictionary<string, Loan> _loans = new Dictionary<string, Loan>(System.StringComparer.Ordinal);
        private readonly Dictionary<string, float> _retryAfter = new Dictionary<string, float>(System.StringComparer.Ordinal);
        private readonly List<string> _scratch = new List<string>();

        public int Count => _loans.Count;
        public Dictionary<string, Loan>.ValueCollection Loans => _loans.Values;

        public bool IsInFlight(string meshId) => meshId != null && _loans.ContainsKey(meshId);

        /// <summary>InBackoff is true while a refused id waits before it is tried again.</summary>
        public bool InBackoff(string meshId, float now)
        {
            if (meshId == null || !_retryAfter.TryGetValue(meshId, out float until))
                return false;
            if (now < until)
                return true;
            _retryAfter.Remove(meshId);
            return false;
        }

        /// <summary>TryBegin records a loan; false (nothing recorded) when the id is in flight or backing off.</summary>
        public bool TryBegin(string meshId, string toZone, float now, T data)
        {
            if (meshId == null || IsInFlight(meshId) || InBackoff(meshId, now))
                return false;
            _loans[meshId] = new Loan { meshId = meshId, toZone = toZone, sentAt = now, data = data };
            return true;
        }

        /// <summary>TrySettle ends a loan on its destination's ack (an ack from any other cell is ignored).</summary>
        public bool TrySettle(string meshId, string fromZone, out Loan loan)
        {
            if (meshId != null && _loans.TryGetValue(meshId, out loan) && SameZone(loan.toZone, fromZone))
            {
                _loans.Remove(meshId);
                return true;
            }
            loan = null;
            return false;
        }

        /// <summary>TryFail ends a loan its destination refused and backs the id off for <paramref name="backoffSeconds"/>.</summary>
        public bool TryFail(string meshId, string fromZone, float now, float backoffSeconds, out Loan loan)
        {
            if (!TrySettle(meshId, fromZone, out loan))
                return false;
            _retryAfter[meshId] = now + Mathf.Max(0f, backoffSeconds);
            return true;
        }

        /// <summary>TakeExpired moves every loan older than <paramref name="guardSeconds"/> into <paramref name="into"/>, backing each off.</summary>
        public void TakeExpired(float now, float guardSeconds, float backoffSeconds, List<Loan> into)
        {
            _scratch.Clear();
            foreach (Loan loan in _loans.Values)
            {
                if (now - loan.sentAt > guardSeconds)
                    _scratch.Add(loan.meshId);
            }
            Take(now, backoffSeconds, into);
        }

        /// <summary>TakeToward moves every loan toward a zone sent before <paramref name="sentBefore"/> into <paramref name="into"/>.</summary>
        public void TakeToward(string zone, float sentBefore, List<Loan> into)
        {
            _scratch.Clear();
            foreach (Loan loan in _loans.Values)
            {
                if (SameZone(loan.toZone, zone) && loan.sentAt < sentBefore)
                    _scratch.Add(loan.meshId);
            }
            Take(0f, 0f, into);
        }

        /// <summary>CountToward counts the loans toward a zone (every loan when the zone is null).</summary>
        public int CountToward(string zone)
        {
            if (zone == null)
                return _loans.Count;
            int count = 0;
            foreach (Loan loan in _loans.Values)
            {
                if (SameZone(loan.toZone, zone))
                    count++;
            }
            return count;
        }

        public void Clear()
        {
            _loans.Clear();
            _retryAfter.Clear();
        }

        private void Take(float now, float backoffSeconds, List<Loan> into)
        {
            for (int i = 0; i < _scratch.Count; ++i)
            {
                Loan loan = _loans[_scratch[i]];
                _loans.Remove(_scratch[i]);
                if (backoffSeconds > 0f)
                    _retryAfter[_scratch[i]] = now + backoffSeconds;
                into.Add(loan);
            }
            _scratch.Clear();
        }

        private static bool SameZone(string a, string b) => string.Equals(a, b, System.StringComparison.OrdinalIgnoreCase);
    }
}
