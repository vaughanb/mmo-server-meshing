// Server Meshing add-on — what the client queued on the old connection leaves before the hop closes it (DESIGN.md 1.15).
//
// A hop takes the client over to the new cell's connection in the frame its warp arrives, and the kit stops the old
// connection there (LiteNetLibManager.MigrateClient). LiteNetLib sends a connection's queued packets on its own logic
// thread, every UpdateTime (15 ms), and its Stop shuts its peers before that thread's last pass: whatever the client
// had queued since that thread's last pass was dropped, unsent. A swing pressed in the frame of the warp played on
// the client and never happened (seen once in 15 hops). The origin forwards a call that reaches it after the
// demotion (1.07), but this one never reached it.
//
// Now, just before the old connection stops (OnStartMigrateClient), its logic thread is woken and the main thread
// waits, at most hopFlushMaxMilliseconds, until the connection's reliable queues are empty and a datagram has been
// written after them: the queued calls are on the wire to the origin, which forwards them. With nothing queued it
// costs nothing.
using LiteNetLib;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class MeshMapNetworkManager
    {
        [Tooltip("Client: at a hop, send what is still queued on the old connection before it is closed (DESIGN 1.15).")]
        public bool flushOldConnectionOnHop = true;

        [Tooltip("Client: the longest the hop's frame waits for the old connection's queue to go out, in milliseconds.")]
        public float hopFlushMaxMilliseconds = 5f;

        /// <summary>Client: raised as a hop is about to close the old connection, before its queue is flushed (the test rig sends a call here).</summary>
        public event System.Action ClientConnectionClosing;

        /// <summary>Client, from OnStartMigrateClient: the old connection's reliable queues, sent.</summary>
        private void FlushOldConnection()
        {
            ClientConnectionClosing?.Invoke();
            if (!flushOldConnectionOnHop || IsServer || Client == null)
                return;
            LiteNetLibTransport transport = MeshTransportUnwrap.Innermost<LiteNetLibTransport>(Client.Transport);
            NetManager manager = transport?.Client;
            NetPeer peer = manager?.FirstPeer;
            if (peer == null)
                return;
            int queued = QueuedReliable(peer, manager.ChannelsCount);
            if (queued == 0)
                return;
            // The logic thread takes a packet off the queue into its send window and only then writes the merged
            // datagram: an empty queue alone was not enough (one call of 13 lost). Wait for the write too: the
            // manager counts every datagram it writes, when its statistics are on.
            manager.EnableStatistics = true;
            long sentBefore = manager.Statistics.PacketsSent;
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            manager.TriggerUpdate();
            int left = queued;
            bool written = false;
            while ((left > 0 || !written) && sw.Elapsed.TotalMilliseconds < hopFlushMaxMilliseconds)
            {
                System.Threading.Thread.Yield();
                left = QueuedReliable(peer, manager.ChannelsCount);
                written = manager.Statistics.PacketsSent > sentBefore;
            }
            Stamp($"old connection: {queued} queued packet(s) {(left == 0 && written ? "sent" : $"not all sent ({left} left, written={written})")} in {sw.Elapsed.TotalMilliseconds:0.00} ms before it closed");
        }

        private static int QueuedReliable(NetPeer peer, int channels)
        {
            int count = 0;
            for (int channel = 0; channel < channels; ++channel)
                count += peer.GetPacketsCountInReliableQueue((byte)channel, true) + peer.GetPacketsCountInReliableQueue((byte)channel, false);
            return count;
        }
    }
}
