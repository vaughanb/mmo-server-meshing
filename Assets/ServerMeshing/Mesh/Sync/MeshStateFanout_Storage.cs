// Server Meshing add-on — the state fan-out's tables: sources with their pairs, connections with their packets, and the
// records put off to the next frame.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Sync
{
    public sealed partial class MeshStateFanout
    {
        private sealed class FanoutSource
        {
            public readonly IMeshStateSource Source;
            public readonly IMeshCompactStateSource CompactSource;
            public readonly uint ObjectId;
            public readonly bool IsAdapter;
            public readonly Dictionary<long, int> PairIndex = new Dictionary<long, int>();
            public FanoutPair[] Pairs = new FanoutPair[4];
            public int PairCount;
            public bool Registered = true;
            public int Generation;
            public int FullOffset, FullLength, CompactOffset, CompactLength;
            public long CompactBuild = -1;
            public bool CompactValid;
            public long PositionBuild = -1;
            public Vector3 Position;
            public uint LastVersion;
            public bool HasVersion;
            public int UrgentBuilds;
            public int SourceMark;
            public int ConsecutiveReliable;
            public bool StuckLogged;
            public long ErrorLoggedTake = -1;
            public long TooBigLoggedTake = -1;

            public FanoutSource(IMeshStateSource source, uint objectId)
            {
                Source = source;
                CompactSource = source as IMeshCompactStateSource;
                ObjectId = objectId;
                IsAdapter = source is MeshKitMovementStateSource;
            }

            public void ClearPairs()
            {
                PairIndex.Clear();
                Array.Clear(Pairs, 0, PairCount);
                PairCount = 0;
            }

            public void RemovePairsNotSeen(long buildIndex)
            {
                for (int i = PairCount - 1; i >= 0; --i)
                {
                    if (Pairs[i].LastSeen == buildIndex)
                        continue;
                    PairIndex.Remove(Pairs[i].ConnectionId);
                    int last = PairCount - 1;
                    if (i != last)
                    {
                        Pairs[i] = Pairs[last];
                        PairIndex[Pairs[i].ConnectionId] = i;
                    }
                    Pairs[last] = default;
                    PairCount--;
                }
            }
        }

        private struct FanoutPair
        {
            public long ConnectionId;
            public FanoutConnection Connection;
            public int ConnectionGeneration;
            public long LastSeen;
            public byte Hz;
            public int Divisor;
            public int SendPhase;
            public int ClassifyPhase;
            public int Boost;
            public bool IsOwner;
            public bool Marked;
            public bool Withheld;
            public bool Classified;
            public MeshStateVariant Variant;
            public int SourceMarkSeen;
            public int ObserverMarkSeen;
        }

        private sealed class FanoutConnection
        {
            public readonly MeshStatePacketWriter Unreliable;
            public readonly MeshStatePacketWriter Reliable;
            public long Id;
            public int Generation;
            public int ListIndex;
            public long AnchorBuild = -1;
            public bool HasAnchor;
            public Vector3 Anchor;
            public int ObserverMark;
            public long FlushStamp;

            public FanoutConnection(int maxPacketBytes)
            {
                Unreliable = new MeshStatePacketWriter(maxPacketBytes);
                Reliable = new MeshStatePacketWriter(maxPacketBytes);
            }
        }

        private readonly struct DeferredRecord
        {
            public DeferredRecord(FanoutConnection connection, FanoutSource source, int offset, int length)
            {
                Connection = connection;
                ConnectionGeneration = connection.Generation;
                Source = source;
                SourceGeneration = source.Generation;
                Timestamp = connection.Unreliable.Timestamp;
                Offset = offset;
                Length = length;
            }

            public FanoutConnection Connection { get; }
            public int ConnectionGeneration { get; }
            public FanoutSource Source { get; }
            public int SourceGeneration { get; }
            public long Timestamp { get; }
            public int Offset { get; }
            public int Length { get; }
        }
    }
}
