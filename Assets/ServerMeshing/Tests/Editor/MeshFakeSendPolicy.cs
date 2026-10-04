using System;
using System.Collections.Generic;
using MultiplayerARPG.MMO.Mesh.Sync;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>
    /// MeshFakeSendPolicy returns a programmable rate: per (object, connection), else per connection, else the owner rate
    /// for an owner pair when set, else one rate for all; it can throw and counts its calls.
    /// </summary>
    internal sealed class MeshFakeSendPolicy : IMeshSendPolicy
    {
        public readonly Dictionary<(uint objectId, long connection), MeshSendRate> byPair = new Dictionary<(uint, long), MeshSendRate>();
        public readonly Dictionary<long, MeshSendRate> byConnection = new Dictionary<long, MeshSendRate>();
        public MeshSendRate all = new MeshSendRate(20, 0);
        public bool hasOwnerRate;
        public MeshSendRate ownerRate;
        public bool throws;
        public int calls;
        public MeshObserverContext last;

        public MeshSendRate Classify(IMeshStateSource source, in MeshObserverContext observer)
        {
            calls++;
            last = observer;
            if (throws)
                throw new InvalidOperationException("the fake policy failed on purpose");
            if (byPair.Count > 0 && byPair.TryGetValue((source.ObjectId, observer.ObserverConnectionId), out MeshSendRate pairRate))
                return pairRate;
            if (byConnection.Count > 0 && byConnection.TryGetValue(observer.ObserverConnectionId, out MeshSendRate connectionRate))
                return connectionRate;
            if (hasOwnerRate && observer.IsOwner)
                return ownerRate;
            return all;
        }
    }
}
