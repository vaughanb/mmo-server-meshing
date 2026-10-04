// Server Meshing add-on — the state sender's engine: writes each source once per build and fans the record out to its
// subscribers at their rates, consuming the kit handlers' one-shot flags once a build has gone out whole.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Sync
{
    /// <summary>IMeshStateTransport sends one finished EntityState packet to one connection; it may throw.</summary>
    public interface IMeshStateTransport
    {
        /// <summary>Send hands <paramref name="writer"/>'s bytes to <paramref name="connectionId"/> with <paramref name="deliveryMethod"/>; the channel is the transport's business.</summary>
        void Send(long connectionId, DeliveryMethod deliveryMethod, NetDataWriter writer);
    }

    /// <summary>IMeshStatePositions tells the fan-out where observers and sources are, for the policy's distance.</summary>
    public interface IMeshStatePositions
    {
        /// <summary>TryGetObserverAnchor is the observer's anchor (its character, or its first owned object); false when it has none.</summary>
        bool TryGetObserverAnchor(long connectionId, out Vector3 anchor);

        /// <summary>PositionOf is the source's position now.</summary>
        Vector3 PositionOf(IMeshStateSource source);
    }

    /// <summary>
    /// MeshStateFanout writes every registered source that has a subscriber once per build and sends an unchanged copy
    /// of the record to each subscriber whose pair is due: every build for a reliable record, on the pair's first
    /// FirstSendBuilds builds, on UrgentBuilds builds after a DiscreteVersion change, and otherwise at the rate the policy
    /// gives the pair. It owns the one-shot flags pass of the kit handlers it writes: Begin at the build's start, Record
    /// after a handler's one successful write, ConsumeAll once the build went out with no failed reliable send. A build
    /// that throws sends nothing more and consumes nothing, so the flags go out again on the next build. Main thread only;
    /// nothing is allocated per build once the population is steady.
    /// </summary>
    public sealed partial class MeshStateFanout
    {
        /// <summary>DropReasonIdentityGone is the drop callback's reason for a source whose identity was destroyed.</summary>
        public const string DropReasonIdentityGone = "its identity is gone";

        /// <summary>DropReasonAppendOnly is the drop callback's reason for a source whose WriteState rewound or reset the writer.</summary>
        public const string DropReasonAppendOnly = "it broke append-only";

        private const int GuardBytes = 4;
        private const int MaxDivisor = 60;

        private readonly IMeshStateTransport _transport;
        private readonly IMeshStatePositions _positions;
        private readonly IMeshSendPolicy _policy;
        private readonly MeshStateFanoutSettings _settings;
        private readonly IMeshLog _log;
        private readonly Action<IMeshStateSource, string> _dropped;
        private readonly EntityMovementServerStateFlagsPass _flags = new EntityMovementServerStateFlagsPass();

        private readonly Dictionary<uint, FanoutSource> _sourceById = new Dictionary<uint, FanoutSource>();
        private FanoutSource[] _sources = new FanoutSource[16];
        private int _sourceSlots;

        private readonly Dictionary<long, FanoutConnection> _connectionById = new Dictionary<long, FanoutConnection>();
        private readonly List<FanoutConnection> _connections = new List<FanoutConnection>();
        private readonly Stack<FanoutConnection> _freeConnections = new Stack<FanoutConnection>();

        private readonly NetDataWriter _scratch = new NetDataWriter(true, 256);
        private readonly NetDataWriter _oversize = new NetDataWriter(true, 2048);
        private byte[] _arena = new byte[4096];
        private int _arenaLength;

        private DeferredRecord[] _deferred = new DeferredRecord[64];
        private int _deferredCount;
        private int _deferredFrame;
        private long _flushStamp;

        private readonly int[] _phaseCounters = new int[MaxDivisor + 1];
        private int _classifyCounter;

        private long _buildIndex;
        private long _buildTimestamp;
        private int _lastFrame;
        private bool _hasBuilt;
        private bool _reliableFailed;
        private bool _withheldOwnerLogged;
        private bool _clampLogged;
        private long _sendErrorLoggedTake = -1;

        private long _pairsThisBuild;
        private long _withheldThisBuild;
        private readonly long[] _hzThisBuild = new long[MeshStateFanoutCounters.HzSlots];

        /// <summary>
        /// MeshStateFanout creates an engine over its ports. <paramref name="dropped"/> is optional; it runs synchronously
        /// after a source left the fan-out on its own (a gone identity, an append-only violation), with the reason.
        /// </summary>
        public MeshStateFanout(IMeshStateTransport transport, IMeshStatePositions positions, IMeshSendPolicy policy, MeshStateFanoutSettings settings, IMeshLog log, Action<IMeshStateSource, string> dropped)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _positions = positions ?? throw new ArgumentNullException(nameof(positions));
            _policy = policy ?? throw new ArgumentNullException(nameof(policy));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _dropped = dropped;
        }

        /// <summary>Counters is the engine's live counters; take them with MeshStateFanoutCounters.TakeAndReset.</summary>
        public MeshStateFanoutCounters Counters { get; } = new MeshStateFanoutCounters();

        /// <summary>SourceCount is the registered sources.</summary>
        public int SourceCount => _sourceById.Count;

        /// <summary>HasDeferred is true while records put off by the last build wait for FlushDeferred.</summary>
        public bool HasDeferred => _deferredCount > 0;

        /// <summary>
        /// FlagsHandlerOf is the handler whose one-shot flags a source's write carries: a wrapped kit handler's inner
        /// handler, the source itself when it is a movement handler, else null (the pass ignores a handler without flags).
        /// </summary>
        public static IEntityMovementDataHandler FlagsHandlerOf(IMeshStateSource source)
        {
            return source is MeshKitMovementStateSource wrapper ? wrapper.Inner : source as IEntityMovementDataHandler;
        }

        /// <summary>
        /// Register adds a source from the next build on. False, with <paramref name="error"/> naming the object id, when
        /// it has no identity or another source holds its id.
        /// </summary>
        public bool Register(IMeshStateSource source, out string error)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            uint objectId = source.ObjectId;
            if (source.Identity == null)
            {
                error = $"object {objectId} ({source.GetType().Name}) has no identity";
                return false;
            }
            if (_sourceById.TryGetValue(objectId, out FanoutSource holder))
            {
                error = $"object {objectId} is already sent for a {holder.Source.GetType().Name}, so this {source.GetType().Name} stays out";
                return false;
            }
            FanoutSource record = new FanoutSource(source, objectId);
            _sourceById.Add(objectId, record);
            if (_sourceSlots == _sources.Length)
                Array.Resize(ref _sources, _sources.Length * 2);
            _sources[_sourceSlots++] = record;
            error = null;
            return true;
        }

        /// <summary>Unregister takes a source out at once; false when that source is not registered.</summary>
        public bool Unregister(IMeshStateSource source)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (!_sourceById.TryGetValue(source.ObjectId, out FanoutSource record) || !ReferenceEquals(record.Source, source))
                return false;
            Remove(record);
            return true;
        }

        /// <summary>IsRegistered is true while some source is registered under <paramref name="objectId"/>.</summary>
        public bool IsRegistered(uint objectId) => _sourceById.ContainsKey(objectId);

        /// <summary>MarkRelevanceChanged classifies the pair of <paramref name="objectId"/> and <paramref name="observerConnectionId"/> again on the next build.</summary>
        public void MarkRelevanceChanged(uint objectId, long observerConnectionId)
        {
            if (_sourceById.TryGetValue(objectId, out FanoutSource record) && record.PairIndex.TryGetValue(observerConnectionId, out int index))
                record.Pairs[index].Marked = true;
        }

        /// <summary>MarkSourceChanged classifies every pair of <paramref name="objectId"/> again on the next build that writes it.</summary>
        public void MarkSourceChanged(uint objectId)
        {
            if (_sourceById.TryGetValue(objectId, out FanoutSource record))
                record.SourceMark++;
        }

        /// <summary>MarkObserverChanged classifies every pair of <paramref name="observerConnectionId"/> again when next visited.</summary>
        public void MarkObserverChanged(long observerConnectionId)
        {
            if (_connectionById.TryGetValue(observerConnectionId, out FanoutConnection connection))
                connection.ObserverMark++;
        }

        /// <summary>
        /// ForgetConnection drops a disconnected peer: its packets are recycled and every pair and deferred record of it
        /// is void, so the id coming back starts new pairs.
        /// </summary>
        public void ForgetConnection(long connectionId)
        {
            if (!_connectionById.TryGetValue(connectionId, out FanoutConnection connection))
                return;
            _connectionById.Remove(connectionId);
            int last = _connections.Count - 1;
            FanoutConnection moved = _connections[last];
            _connections[connection.ListIndex] = moved;
            moved.ListIndex = connection.ListIndex;
            _connections.RemoveAt(last);
            connection.Generation++;
            _freeConnections.Push(connection);
        }

        /// <summary>
        /// Build writes and sends one build stamped <paramref name="sampleTimestamp"/>. A second call in the same
        /// <paramref name="frameIndex"/> only counts a skip. Records still deferred are flushed first.
        /// <paramref name="hostConnectionId"/> is never sent to. On a throw nothing more of the build is sent, later, and
        /// its flags stay unconsumed; the exception is rethrown.
        /// </summary>
        public void Build(long sampleTimestamp, int frameIndex, long hostConnectionId)
        {
            if (_hasBuilt && frameIndex == _lastFrame)
            {
                Counters.Skipped++;
                return;
            }
            _hasBuilt = true;
            _lastFrame = frameIndex;
            long started = Stopwatch.GetTimestamp();
            long writeBefore = Counters.WriteTicks;
            long sendBefore = Counters.SendTicks;
            try
            {
                if (_deferredCount > 0)
                    FlushDeferredNow();
                Compact();
                _buildIndex++;
                Counters.Builds++;
                _buildTimestamp = sampleTimestamp;
                _arenaLength = 0;
                for (int i = 0; i < _connections.Count; ++i)
                {
                    _connections[i].Unreliable.Begin(sampleTimestamp);
                    _connections[i].Reliable.Begin(sampleTimestamp);
                }
                if (!_settings.Measure)
                    _flags.Begin();
                _reliableFailed = false;
                _pairsThisBuild = 0;
                _withheldThisBuild = 0;
                Array.Clear(_hzThisBuild, 0, _hzThisBuild.Length);

                int slots = _sourceSlots;
                for (int slot = 0; slot < slots; ++slot)
                {
                    FanoutSource record = _sources[slot];
                    if (record.Registered)
                        BuildSource(record, slot, sampleTimestamp, hostConnectionId);
                }

                for (int i = 0; i < _connections.Count; ++i)
                {
                    FanoutConnection connection = _connections[i];
                    if (!connection.Reliable.IsEmpty)
                        SendPacket(connection, connection.Reliable, DeliveryMethod.ReliableOrdered);
                    if (!connection.Unreliable.IsEmpty)
                        SendPacket(connection, connection.Unreliable, DeliveryMethod.Unreliable);
                }
                if (_deferredCount > 0)
                    _deferredFrame = frameIndex;
                Compact();
                Counters.Sources = _sourceById.Count;
                Counters.Connections = _connections.Count;
                Counters.Pairs = _pairsThisBuild;
                Counters.PairsWithheld = _withheldThisBuild;
                Array.Copy(_hzThisBuild, Counters.PairsByHz, _hzThisBuild.Length);
                if (!_settings.Measure && !_reliableFailed)
                    _flags.ConsumeAll();
            }
            catch
            {
                Abandon();
                throw;
            }
            finally
            {
                long spent = Stopwatch.GetTimestamp() - started;
                Counters.FanoutTicks += Math.Max(0L, spent - (Counters.WriteTicks - writeBefore) - (Counters.SendTicks - sendBefore));
            }
        }

        /// <summary>
        /// FlushDeferred sends the records the last build put off, to the pairs still subscribed, stamped with that build's
        /// timestamp. It does nothing in the frame of that build (the point is to send them a frame later); Build flushes
        /// them itself first. They are only ever routine unreliable records.
        /// </summary>
        public void FlushDeferred(int frameIndex)
        {
            if (_deferredCount == 0 || frameIndex == _deferredFrame)
                return;
            long started = Stopwatch.GetTimestamp();
            long sendBefore = Counters.SendTicks;
            try
            {
                FlushDeferredNow();
            }
            catch
            {
                ClearDeferred();
                throw;
            }
            finally
            {
                long spent = Stopwatch.GetTimestamp() - started;
                Counters.FanoutTicks += Math.Max(0L, spent - (Counters.SendTicks - sendBefore));
            }
        }

        private void ClearDeferred()
        {
            Array.Clear(_deferred, 0, _deferredCount);
            _deferredCount = 0;
        }

        private void Abandon()
        {
            for (int i = 0; i < _connections.Count; ++i)
            {
                _connections[i].Unreliable.Begin(_buildTimestamp);
                _connections[i].Reliable.Begin(_buildTimestamp);
            }
            ClearDeferred();
        }

        private void Drop(FanoutSource record, string reason)
        {
            Remove(record);
            _dropped?.Invoke(record.Source, reason);
        }

        private void Remove(FanoutSource record)
        {
            _sourceById.Remove(record.ObjectId);
            record.Registered = false;
            record.Generation++;
        }

        private void Compact()
        {
            int kept = 0;
            for (int slot = 0; slot < _sourceSlots; ++slot)
            {
                FanoutSource record = _sources[slot];
                if (record.Registered)
                    _sources[kept++] = record;
            }
            Array.Clear(_sources, kept, _sourceSlots - kept);
            _sourceSlots = kept;
        }
    }
}
