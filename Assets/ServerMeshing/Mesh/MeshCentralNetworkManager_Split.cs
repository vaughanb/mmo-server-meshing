// Server Meshing add-on — the central's share of a manual split (Slice 2b decisions 2, 3 and 6) and merge (Slice 2c
// decision 2): the layout command entry point, the cells' gate evidence, and the split host the coordinator uses for what
// only the kit can do: a channel added to the live channel list at runtime (and removed once a merged-away cell exited),
// and the child's process asked from its base cell's map-spawn server.
using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using LiteNetLibManager;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class MeshCentralNetworkManager
    {
        /// <summary>LayoutOpLine is raised with every layout operation line ("Split ...", "Merge ..." or "Layout op refused: ..."), for a runner to mirror.</summary>
        public event Action<string> LayoutOpLine;

        /// <summary>
        /// SubmitLayoutCommand runs one command line, `&lt;seq&gt; split &lt;cell&gt; &lt;x|z&gt; &lt;at&gt; &lt;+|-&gt; &lt;childId&gt;`
        /// (world frame) or `&lt;seq&gt; merge &lt;a&gt; &lt;b&gt;` (either order). True when the operation started; false with
        /// the reason, also logged "Layout op refused: reason".
        /// </summary>
        public bool SubmitLayoutCommand(string line, out string error)
        {
            if (!MeshLayoutCommand.TryParse(line, out MeshLayoutCommand command, out error))
            {
                RefuseCommand(error);
                return false;
            }
            if (_coordinator == null)
            {
                error = "no layout coordinator runs on this central (no Dynamic map, or the server is not started)";
                RefuseCommand(error);
                return false;
            }
            if (command.kind == MeshLayoutCommandKind.Merge)
                return _coordinator.RequestMerge(command.mergeA, command.mergeB, out error);
            return _coordinator.RequestSplit(command.cellId, command.axis, command.at, command.childOnPositiveSide, command.childId, out error);
        }

        private void RefuseCommand(string rule)
        {
            string text = "Layout op refused: " + rule;
            Logging.LogWarning("Mesh", text);
            RaiseLayoutOpLine(text);
        }

        private void RaiseLayoutOpLine(string text)
        {
            Action<string> handlers = LayoutOpLine;
            if (handlers == null)
                return;
            foreach (Delegate handler in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<string>)handler)(text);
                }
                catch (Exception ex)
                {
                    Logging.LogWarning("Mesh", "Layout op line subscriber failed: " + ex.Message);
                }
            }
        }

        private void AttachSplit(MeshLayoutCoordinator coordinator)
        {
            coordinator.SplitHost = new SplitHost(this);
            coordinator.OpLine += RaiseLayoutOpLine;
        }

        private void HandleLayoutEvidence(MessageHandlerData messageHandler)
        {
            if (!ReadWhole(messageHandler, "Layout evidence", out MeshLayoutEvidenceMessage evidence))
                return;
            _coordinator?.OnLayoutEvidence(messageHandler.ConnectionId, evidence);
        }

        /// <summary>A cell the awake set leaves alone (and the wake refusal refuses): a dynamic cell, or a base cell whose family is cut or in an operation.</summary>
        private bool IsSplitFamilyCell(MeshCell cell)
        {
            return cell.isDynamic || (_coordinator != null && cell.OnDynamicMap && _coordinator.IsSplitFamily(cell.baseId));
        }

        /// <summary>SpawnerFor by the cell id a spawner advertised (a child asks its base cell's): see SpawnerFor(MeshCell, ...).</summary>
        public static long SpawnerForKey(string key, IReadOnlyDictionary<string, long> spawnerByCell, ICollection<long> registered, out bool advertised)
        {
            advertised = false;
            if (!string.IsNullOrEmpty(key) && spawnerByCell.TryGetValue(key, out long owner) && registered.Contains(owner))
            {
                advertised = true;
                return owner;
            }
            foreach (long connectionId in registered)
                return connectionId;
            return -1;
        }

        private async UniTaskVoid SpawnChild(long spawnerConnectionId, string opId, string childId, string mapId)
        {
            string key = PeerInfoExtensions.GetPeerInfoKey(childId, mapId);
            bool answered = false;
            RequestSpawnMapMessage request = new RequestSpawnMapMessage { channelId = childId, mapName = mapId, instanceId = string.Empty };
            try
            {
                await ClusterServer.RequestSpawnMap(spawnerConnectionId, request, key, (responseCode, response, extraSerializer) =>
                {
                    // The kit keeps this callback and fires it on every later registration of the key: only the first counts.
                    if (answered)
                        return;
                    answered = true;
                    ClusterServer.MapSpawnResultActions.Remove(key);
                    bool success = responseCode == AckResponseCode.Success;
                    _coordinator?.OnSpawnResult(opId, success, success ? "Success" : $"{responseCode} {response.message}");
                });
            }
            catch (Exception ex)
            {
                Logging.LogWarning("Mesh", $"Spawn request for child {childId} failed: {ex.Message}");
                if (!answered)
                {
                    answered = true;
                    _coordinator?.OnSpawnResult(opId, false, "request failed: " + ex.Message);
                }
            }
            // On an error the kit calls back first and stores the callback after: take it out again.
            if (answered && ClusterServer != null)
                ClusterServer.MapSpawnResultActions.Remove(key);
        }

        /// <summary>SplitHost gives the coordinator the kit's channel list and map-spawn servers.</summary>
        private sealed class SplitHost : IMeshSplitHost
        {
            private readonly MeshCentralNetworkManager _central;

            public SplitHost(MeshCentralNetworkManager central)
            {
                _central = central;
            }

            private ClusterServer Cluster => _central.ClusterServer;

            public bool CanSpawn(string baseId, out string error)
            {
                error = null;
                if (Cluster == null || !Cluster.IsNetworkActive)
                {
                    error = "the cluster server is not running";
                    return false;
                }
                if (Cluster.MapSpawnServerPeers.Count == 0)
                {
                    error = "no map-spawn server is registered";
                    return false;
                }
                return true;
            }

            public bool AddChannel(string cellId, out string error)
            {
                error = null;
                Dictionary<string, ChannelData> channels = _central.Channels;
                if (channels.ContainsKey(cellId))
                {
                    error = $"channel {cellId} already exists";
                    return false;
                }
                int max = _central.defaultChannelMaxConnections > 0 ? _central.defaultChannelMaxConnections : 500;
                channels[cellId] = new ChannelData { id = cellId, title = cellId, maxConnections = max };
                return true;
            }

            public void RemoveChannel(string cellId)
            {
                if (_central.Channels.Remove(cellId))
                    Logging.Log("Mesh", $"Channel {cellId} removed");
            }

            public bool RequestSpawn(string opId, string baseId, string childId, string mapId, out string error)
            {
                error = null;
                if (!CanSpawn(baseId, out error))
                    return false;
                long spawner = SpawnerForKey(baseId, _central._spawnerByCell, Cluster.MapSpawnServerPeers.Keys, out bool advertised);
                if (spawner < 0)
                {
                    error = "no map-spawn server is registered";
                    return false;
                }
                Logging.Log("Mesh", $"Child {childId} of {baseId} ({mapId}): start requested from map-spawn server {spawner}{(advertised ? string.Empty : $", which did not advertise {baseId}")}");
                _central.SpawnChild(spawner, opId, childId, mapId).Forget();
                return true;
            }
        }
    }
}
