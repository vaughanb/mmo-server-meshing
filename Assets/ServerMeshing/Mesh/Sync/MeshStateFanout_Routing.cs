// Server Meshing add-on — the state fan-out's per-build work: writing each source, classifying its pairs and routing its
// record into packets.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using LiteNetLib;
using LiteNetLib.Utils;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Sync
{
    public sealed partial class MeshStateFanout
    {
        private void BuildSource(FanoutSource record, int slot, long timestamp, long host)
        {
            IMeshStateSource source = record.Source;
            LiteNetLibIdentity identity = source.Identity;
            if (identity == null || identity.IsDestroyed)
            {
                Counters.IdentityDrops++;
                _log.Warning($"State sender: object {record.ObjectId} ({source.GetType().Name}) left the sender: {DropReasonIdentityGone}");
                Drop(record, DropReasonIdentityGone);
                return;
            }
            int subscribers = identity.CountSubscribers();
            if (subscribers == 0 || (subscribers == 1 && identity.HasSubscriber(host)))
            {
                if (record.PairCount > 0)
                    record.ClearPairs();
                return;
            }

            if (!TryWrite(record, timestamp, false, out bool reliable))
                return;
            if (!_settings.Measure)
                _flags.Record(FlagsHandlerOf(source));
            Counters.Writes++;

            if (record.IsAdapter)
                WatchReliable(record, reliable);
            uint version = source.DiscreteVersion;
            if (record.HasVersion && version != record.LastVersion)
            {
                record.UrgentBuilds = MeshStateFanoutSettings.UrgentBuilds;
                record.SourceMark++;
            }
            record.LastVersion = version;
            record.HasVersion = true;

            WalkSubscribers(record, identity, host, reliable, _settings.Spread && (slot & 1) == 1);
        }

        private bool TryWrite(FanoutSource record, long timestamp, bool compact, out bool reliable)
        {
            reliable = false;
            IMeshStateSource source = record.Source;
            _scratch.Reset();
            _scratch.Put(record.ObjectId);
            bool wrote;
            long started = Stopwatch.GetTimestamp();
            try
            {
                wrote = compact ? record.CompactSource.WriteCompactState(timestamp, _scratch) : source.WriteState(timestamp, _scratch, out reliable);
            }
            catch (Exception e)
            {
                Counters.WriteErrors++;
                if (record.ErrorLoggedTake != Counters.TakeCount)
                {
                    record.ErrorLoggedTake = Counters.TakeCount;
                    _log.Error($"State sender error: object {record.ObjectId} ({source.GetType().Name}) {(compact ? "compact write" : "write")} failed: {MeshStateSender.CleanForLog(e.Message)}");
                }
                return false;
            }
            finally
            {
                Counters.WriteTicks += Stopwatch.GetTimestamp() - started;
            }

            int length = _scratch.Length;
            if (length < GuardBytes || BitConverter.ToUInt32(_scratch.Data, 0) != record.ObjectId)
            {
                if (compact)
                {
                    Counters.WriteErrors++;
                    if (record.ErrorLoggedTake != Counters.TakeCount)
                    {
                        record.ErrorLoggedTake = Counters.TakeCount;
                        _log.Error($"State sender error: object {record.ObjectId} ({source.GetType().Name}) compact write broke append-only; its full payload goes instead");
                    }
                    return false;
                }
                Counters.AppendOnlyDrops++;
                _log.Error($"State sender error: object {record.ObjectId} ({source.GetType().Name}) left the sender: {DropReasonAppendOnly} (writer at {length} bytes after the write)");
                Drop(record, DropReasonAppendOnly);
                return false;
            }
            if (!record.Registered)
                return false;
            if (!wrote)
            {
                if (!compact)
                    Counters.NotWritten++;
                return false;
            }
            if (length == GuardBytes)
            {
                if (!compact)
                    Counters.EmptyWrites++;
                return false;
            }

            int payload = length - GuardBytes;
            if (_arenaLength + payload > _arena.Length)
                Array.Resize(ref _arena, Math.Max(_arena.Length * 2, _arenaLength + payload));
            Buffer.BlockCopy(_scratch.Data, GuardBytes, _arena, _arenaLength, payload);
            Counters.AddPayload(compact, payload);
            if (compact)
            {
                record.CompactOffset = _arenaLength;
                record.CompactLength = payload;
                Counters.CompactWrites++;
            }
            else
            {
                record.FullOffset = _arenaLength;
                record.FullLength = payload;
            }
            _arenaLength += payload;
            return true;
        }

        private void WatchReliable(FanoutSource record, bool reliable)
        {
            if (!reliable)
            {
                record.ConsecutiveReliable = 0;
                return;
            }
            record.ConsecutiveReliable++;
            if (record.ConsecutiveReliable != MeshStateFanoutSettings.StuckReliableBuilds || record.StuckLogged)
                return;
            record.StuckLogged = true;
            Counters.StuckReliable++;
            IEntityMovementDataHandler inner = ((MeshKitMovementStateSource)record.Source).Inner;
            _log.Warning($"State sender: object {record.ObjectId} ({inner.GetType().Name}) wrote {MeshStateFanoutSettings.StuckReliableBuilds} reliable builds in a row; its one-shot flags are not being consumed");
        }

        private void WalkSubscribers(FanoutSource record, LiteNetLibIdentity identity, long host, bool reliable, bool spreadSlot)
        {
            bool urgent = record.UrgentBuilds > 0;
            long owner = identity.ConnectionId;
            int classifyEvery = _settings.ClassifyEveryBuilds;
            int seen = 0;
            HashSet<long>.Enumerator subscribers = identity.GetSubscribers();
            while (subscribers.MoveNext())
            {
                long connectionId = subscribers.Current;
                if (connectionId == host)
                    continue;
                int index = FindOrAddPair(record, connectionId);
                ref FanoutPair pair = ref record.Pairs[index];
                FanoutConnection connection = pair.Connection;
                pair.LastSeen = _buildIndex;
                seen++;
                Counters.PairsVisited++;
                bool isOwner = owner == connectionId;
                if (!pair.Classified || pair.Marked || isOwner != pair.IsOwner || pair.SourceMarkSeen != record.SourceMark
                    || pair.ObserverMarkSeen != connection.ObserverMark || (_buildIndex + pair.ClassifyPhase) % classifyEvery == 0)
                    Classify(record, ref pair, isOwner);

                _pairsThisBuild++;
                if (pair.Withheld)
                {
                    _withheldThisBuild++;
                    continue;
                }
                _hzThisBuild[HzSlot(pair.Hz)]++;
                bool boosted = pair.Boost > 0;
                if (boosted)
                    pair.Boost--;
                if (reliable)
                    AppendReliable(connection, record);
                else if (urgent || boosted)
                    RouteUnreliable(connection, record, MeshStateVariant.Full, false);
                else if (pair.Divisor > 0 && (_buildIndex + pair.SendPhase) % pair.Divisor == 0)
                    RouteUnreliable(connection, record, pair.Variant, spreadSlot);
            }
            if (seen != record.PairCount)
                record.RemovePairsNotSeen(_buildIndex);
            if (urgent)
                record.UrgentBuilds--;
        }

        private int FindOrAddPair(FanoutSource record, long connectionId)
        {
            if (record.PairIndex.TryGetValue(connectionId, out int index))
            {
                ref FanoutPair existing = ref record.Pairs[index];
                if (existing.Connection.Generation == existing.ConnectionGeneration)
                    return index;
            }
            else
            {
                index = record.PairCount;
                if (index == record.Pairs.Length)
                    Array.Resize(ref record.Pairs, index * 2);
                record.PairIndex.Add(connectionId, index);
                record.PairCount++;
            }
            FanoutConnection connection = ConnectionFor(connectionId);
            _classifyCounter = (_classifyCounter + 1) % _settings.ClassifyEveryBuilds;
            record.Pairs[index] = new FanoutPair
            {
                ConnectionId = connectionId,
                Connection = connection,
                ConnectionGeneration = connection.Generation,
                ClassifyPhase = _classifyCounter,
                Boost = MeshStateFanoutSettings.FirstSendBuilds,
                SourceMarkSeen = record.SourceMark,
                ObserverMarkSeen = connection.ObserverMark,
            };
            return index;
        }

        private FanoutConnection ConnectionFor(long connectionId)
        {
            if (_connectionById.TryGetValue(connectionId, out FanoutConnection connection))
                return connection;
            connection = _freeConnections.Count > 0 ? _freeConnections.Pop() : new FanoutConnection(_settings.MaxPacketBytes);
            connection.Id = connectionId;
            connection.AnchorBuild = -1;
            connection.FlushStamp = 0;
            connection.ListIndex = _connections.Count;
            _connections.Add(connection);
            _connectionById.Add(connectionId, connection);
            connection.Unreliable.Begin(_buildTimestamp);
            connection.Reliable.Begin(_buildTimestamp);
            return connection;
        }

        private void Classify(FanoutSource record, ref FanoutPair pair, bool isOwner)
        {
            MeshObserverContext context = new MeshObserverContext(pair.ConnectionId, DistanceOf(record, pair.Connection), isOwner);
            MeshSendRate rate = _policy.Classify(record.Source, in context);
            pair.Classified = true;
            pair.Marked = false;
            pair.IsOwner = isOwner;
            pair.SourceMarkSeen = record.SourceMark;
            pair.ObserverMarkSeen = pair.Connection.ObserverMark;
            if (rate.IsWithheld)
            {
                if (!isOwner)
                {
                    pair.Withheld = true;
                    return;
                }
                Counters.WithheldOwner++;
                if (!_withheldOwnerLogged)
                {
                    _withheldOwnerLogged = true;
                    _log.Warning($"State sender: {_policy.GetType().Name} withheld object {record.ObjectId} from its owner, connection {pair.ConnectionId}; an owner pair gets Hz 0 instead, keeping its urgent and reliable records");
                }
                SetRate(ref pair, 0, MeshStateVariant.Full);
                return;
            }
            int hz = rate.Hz;
            if (!IsAllowedHz(hz))
            {
                int clamped = ClampHz(hz);
                Counters.HzClamped++;
                if (!_clampLogged)
                {
                    _clampLogged = true;
                    _log.Warning($"State sender: {_policy.GetType().Name} gave object {record.ObjectId} a rate of {hz} Hz, which is not 0, 1, 2, 5, 10 or 20; it is sent at {clamped} Hz");
                }
                hz = clamped;
            }
            SetRate(ref pair, hz, rate.Variant);
        }

        private void SetRate(ref FanoutPair pair, int hz, MeshStateVariant variant)
        {
            pair.Withheld = false;
            pair.Hz = (byte)hz;
            pair.Variant = variant;
            int divisor = hz == 0 ? 0 : Math.Max(1, Math.Min(MaxDivisor, (int)Math.Round((double)_settings.BuildHz / hz, MidpointRounding.AwayFromZero)));
            if (divisor == pair.Divisor)
                return;
            pair.Divisor = divisor;
            if (divisor > 1)
            {
                pair.SendPhase = _phaseCounters[divisor];
                _phaseCounters[divisor] = (pair.SendPhase + 1) % divisor;
            }
            else
            {
                pair.SendPhase = 0;
            }
        }

        private float DistanceOf(FanoutSource record, FanoutConnection connection)
        {
            if (connection.AnchorBuild != _buildIndex)
            {
                connection.AnchorBuild = _buildIndex;
                connection.HasAnchor = _positions.TryGetObserverAnchor(connection.Id, out connection.Anchor);
            }
            if (!connection.HasAnchor)
                return float.NaN;
            if (record.PositionBuild != _buildIndex)
            {
                record.PositionBuild = _buildIndex;
                record.Position = _positions.PositionOf(record.Source);
            }
            Vector3 delta = record.Position - connection.Anchor;
            if (_settings.HorizontalDistance)
                delta.y = 0f;
            return delta.magnitude;
        }

        private void AppendReliable(FanoutConnection connection, FanoutSource record)
        {
            MeshStatePacketWriter writer = connection.Reliable;
            if (record.FullLength > writer.MaxPayload(record.ObjectId))
            {
                MeshStatePacketWriter.WriteSingle(_oversize, _buildTimestamp, record.ObjectId, _arena, record.FullOffset, record.FullLength);
                Counters.OversizeReliable++;
                Counters.ReliableRecords++;
                Deliver(connection.Id, DeliveryMethod.ReliableOrdered, _oversize);
                return;
            }
            if (!writer.TryAppend(record.ObjectId, _arena, record.FullOffset, record.FullLength))
            {
                SendPacket(connection, writer, DeliveryMethod.ReliableOrdered);
                writer.Begin(_buildTimestamp);
                writer.TryAppend(record.ObjectId, _arena, record.FullOffset, record.FullLength);
            }
            Counters.ReliableRecords++;
        }

        private void RouteUnreliable(FanoutConnection connection, FanoutSource record, MeshStateVariant variant, bool defer)
        {
            int offset = record.FullOffset, length = record.FullLength;
            if (variant == MeshStateVariant.Compact && record.CompactSource != null && HasCompact(record))
            {
                offset = record.CompactOffset;
                length = record.CompactLength;
            }
            if (length > connection.Unreliable.MaxPayload(record.ObjectId))
            {
                Counters.TooBigSkipped++;
                if (record.TooBigLoggedTake != Counters.TakeCount)
                {
                    record.TooBigLoggedTake = Counters.TakeCount;
                    _log.Error($"State sender error: object {record.ObjectId} ({record.Source.GetType().Name}) wrote a {length}-byte unreliable record, more than a {_settings.MaxPacketBytes}-byte packet holds; it was skipped");
                }
                return;
            }
            if (defer)
            {
                if (_deferredCount == _deferred.Length)
                    Array.Resize(ref _deferred, _deferred.Length * 2);
                _deferred[_deferredCount++] = new DeferredRecord(connection, record, offset, length);
                Counters.DeferredRecords++;
                return;
            }
            AppendUnreliable(connection, record.ObjectId, offset, length);
        }

        private bool HasCompact(FanoutSource record)
        {
            if (record.CompactBuild != _buildIndex)
            {
                record.CompactBuild = _buildIndex;
                record.CompactValid = TryWrite(record, _buildTimestamp, true, out _);
            }
            return record.CompactValid;
        }

        private void AppendUnreliable(FanoutConnection connection, uint objectId, int offset, int length)
        {
            MeshStatePacketWriter writer = connection.Unreliable;
            if (!writer.TryAppend(objectId, _arena, offset, length))
            {
                SendPacket(connection, writer, DeliveryMethod.Unreliable);
                writer.Begin(writer.Timestamp);
                writer.TryAppend(objectId, _arena, offset, length);
            }
            Counters.UnreliableRecords++;
        }

        private void SendPacket(FanoutConnection connection, MeshStatePacketWriter writer, DeliveryMethod deliveryMethod)
        {
            Deliver(connection.Id, deliveryMethod, writer.Finish());
        }

        private void Deliver(long connectionId, DeliveryMethod deliveryMethod, NetDataWriter data)
        {
            bool reliable = deliveryMethod == DeliveryMethod.ReliableOrdered;
            if (!_settings.Measure)
            {
                long started = Stopwatch.GetTimestamp();
                try
                {
                    _transport.Send(connectionId, deliveryMethod, data);
                }
                catch (TooBigPacketException e)
                {
                    if (reliable)
                        FailReliable();
                    else
                        Counters.TooBigSkipped++;
                    LogSendError(connectionId, deliveryMethod, data.Length, e.Message);
                    return;
                }
                catch (Exception e)
                {
                    Counters.SendErrors++;
                    if (reliable)
                        FailReliable();
                    LogSendError(connectionId, deliveryMethod, data.Length, e.Message);
                    return;
                }
                finally
                {
                    Counters.SendTicks += Stopwatch.GetTimestamp() - started;
                }
            }
            if (reliable)
                Counters.ReliablePackets++;
            else
                Counters.UnreliablePackets++;
            Counters.BytesOut += data.Length;
        }

        private void FailReliable()
        {
            Counters.ReliableSendFailures++;
            _reliableFailed = true;
        }

        private void LogSendError(long connectionId, DeliveryMethod deliveryMethod, int bytes, string message)
        {
            if (_sendErrorLoggedTake == Counters.TakeCount)
                return;
            _sendErrorLoggedTake = Counters.TakeCount;
            _log.Error($"State sender error: a {bytes}-byte {deliveryMethod} packet to connection {connectionId} failed: {MeshStateSender.CleanForLog(message)}");
        }

        private void FlushDeferredNow()
        {
            _flushStamp++;
            for (int i = 0; i < _deferredCount; ++i)
            {
                ref DeferredRecord deferred = ref _deferred[i];
                FanoutConnection connection = deferred.Connection;
                FanoutSource record = deferred.Source;
                if (connection.Generation != deferred.ConnectionGeneration || record.Generation != deferred.SourceGeneration || !record.Registered)
                    continue;
                LiteNetLibIdentity identity = record.Source.Identity;
                if (identity == null || !identity.HasSubscriber(connection.Id))
                    continue;
                if (connection.FlushStamp != _flushStamp)
                {
                    connection.FlushStamp = _flushStamp;
                    connection.Unreliable.Begin(deferred.Timestamp);
                }
                AppendUnreliable(connection, record.ObjectId, deferred.Offset, deferred.Length);
            }
            for (int i = 0; i < _connections.Count; ++i)
            {
                FanoutConnection connection = _connections[i];
                if (connection.FlushStamp == _flushStamp && !connection.Unreliable.IsEmpty)
                    SendPacket(connection, connection.Unreliable, DeliveryMethod.Unreliable);
            }
            ClearDeferred();
        }

        /// <summary>IsAllowedHz is true for the rates a pair may have: 0, 1, 2, 5, 10 and 20 Hz.</summary>
        internal static bool IsAllowedHz(int hz) => hz == 0 || hz == 1 || hz == 2 || hz == 5 || hz == 10 || hz == 20;

        private static int ClampHz(int hz)
        {
            if (hz >= 20)
                return 20;
            if (hz >= 10)
                return 10;
            if (hz >= 5)
                return 5;
            if (hz >= 2)
                return 2;
            return hz >= 1 ? 1 : 0;
        }

        private static int HzSlot(byte hz)
        {
            switch (hz)
            {
                case 20: return 0;
                case 10: return 1;
                case 5: return 2;
                case 2: return 3;
                case 1: return 4;
                default: return 5;
            }
        }
    }
}
