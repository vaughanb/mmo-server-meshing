using System.Collections.Generic;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    internal sealed class MeshFakeClock : IMeshClock
    {
        public float Now { get; set; } = 100f;
        public long UtcTicks { get; set; } = 1_000_000;
    }

    internal sealed class MeshFakeLog : IMeshLog
    {
        public readonly List<string> infos = new List<string>();
        public readonly List<string> warnings = new List<string>();
        public readonly List<string> errors = new List<string>();

        public void Info(string message) => infos.Add(message);
        public void Warning(string message) => warnings.Add(message);
        public void Error(string message) => errors.Add(message);
    }

    internal sealed class MeshFakeCoordinatorTransport : IMeshCoordinatorTransport
    {
        public readonly Dictionary<long, MeshMapServerPeer> peers = new Dictionary<long, MeshMapServerPeer>();
        public readonly List<(long connection, MeshLayoutMessage message)> layouts = new List<(long, MeshLayoutMessage)>();
        public readonly List<(long connection, MeshRetireMessage message)> retires = new List<(long, MeshRetireMessage)>();
        public readonly List<(long connection, MeshCoordinatorNoteMessage message)> notes = new List<(long, MeshCoordinatorNoteMessage)>();

        public void Register(long connection, string cellId, string mapId)
        {
            peers[connection] = new MeshMapServerPeer { connectionId = connection, cellId = cellId, mapId = mapId, address = "127.0.0.1", port = 8000 + (int)connection };
        }

        public void GetMapServers(List<MeshMapServerPeer> into) => into.AddRange(peers.Values);
        public bool TryGetMapServer(long connectionId, out MeshMapServerPeer peer) => peers.TryGetValue(connectionId, out peer);
        public void Send(long connectionId, MeshLayoutMessage message) => layouts.Add((connectionId, message));
        public void Send(long connectionId, MeshRetireMessage message) => retires.Add((connectionId, message));
        public void Send(long connectionId, MeshCoordinatorNoteMessage message) => notes.Add((connectionId, message));
    }
}
