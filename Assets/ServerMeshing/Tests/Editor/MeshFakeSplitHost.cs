using System.Collections.Generic;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>MeshFakeSplitHost records the channel, spawn, restart and kill calls an operation or the supervisor makes.</summary>
    internal sealed class MeshFakeSplitHost : IMeshSplitHost
    {
        public bool canSpawn = true, addOk = true, spawnOk = true, restartOk = true, killOk = true;
        public bool kitRestartsBaseCells = true;
        public readonly List<string> channels = new List<string>();
        public readonly List<string> removed = new List<string>();
        public readonly List<string> spawns = new List<string>();
        public readonly List<string> restarts = new List<string>();
        public readonly List<(MeshKillChannelMessage message, string baseId)> kills = new List<(MeshKillChannelMessage, string)>();

        public bool KitRestartsBaseCells => kitRestartsBaseCells;

        public bool CanSpawn(string baseId, out string error)
        {
            error = canSpawn ? null : "no map-spawn server is registered";
            return canSpawn;
        }

        public bool AddChannel(string cellId, out string error)
        {
            error = addOk ? null : $"channel {cellId} already exists";
            if (addOk)
                channels.Add(cellId);
            return addOk;
        }

        public void RemoveChannel(string cellId)
        {
            removed.Add(cellId);
            channels.Remove(cellId);
        }

        public bool RequestSpawn(string opId, string baseId, string childId, string mapId, out string error)
        {
            error = spawnOk ? null : "spawner gone";
            if (spawnOk)
                spawns.Add($"{opId} {baseId} {childId} {mapId}");
            return spawnOk;
        }

        public bool RequestRestart(string cellId, string baseId, string mapId, out string error)
        {
            error = restartOk ? null : "spawner gone";
            if (restartOk)
                restarts.Add($"{cellId} {baseId} {mapId}");
            return restartOk;
        }

        public bool KillChannel(MeshKillChannelMessage message, string baseId, out string error)
        {
            error = killOk ? null : "no map-spawn server is registered";
            if (killOk)
                kills.Add((message, baseId));
            return killOk;
        }
    }
}
