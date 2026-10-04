using System;
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using MultiplayerARPG.MMO.Mesh.Sync;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>
    /// MeshFakeStateTransport keeps a copy of every packet it accepts (connection, delivery, bytes), or only counts them
    /// in counting mode; fault decides per call (connection, delivery, 1-based call number) whether it throws instead.
    /// </summary>
    internal sealed class MeshFakeStateTransport : IMeshStateTransport
    {
        public readonly List<(long connection, DeliveryMethod method, byte[] data)> sends = new List<(long, DeliveryMethod, byte[])>();
        public bool countOnly;
        public int calls;
        public int accepted;
        public long acceptedBytes;
        public Func<long, DeliveryMethod, int, Exception> fault;

        public void Send(long connectionId, DeliveryMethod deliveryMethod, NetDataWriter writer)
        {
            calls++;
            Exception failure = fault?.Invoke(connectionId, deliveryMethod, calls);
            if (failure != null)
                throw failure;
            accepted++;
            acceptedBytes += writer.Length;
            if (!countOnly)
                sends.Add((connectionId, deliveryMethod, writer.CopyData()));
        }

        public List<(long connection, DeliveryMethod method, byte[] data)> To(long connectionId)
        {
            List<(long, DeliveryMethod, byte[])> result = new List<(long, DeliveryMethod, byte[])>();
            foreach ((long connection, DeliveryMethod method, byte[] data) send in sends)
                if (send.connection == connectionId)
                    result.Add(send);
            return result;
        }
    }
}
