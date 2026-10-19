// Server Meshing add-on — what a map-spawn server tells the central about its host (Slice 3, slices/slice3_design.md
// decision 8): the machine it runs on and its cores, so the central can defer a split while the host has no headroom.
// Sent on every successful registration (cluster message 30021).
using System;
using LiteNetLib.Utils;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>MeshHostReportMessage (30021) is a spawner's host: its name and how many cores it has.</summary>
    public sealed class MeshHostReportMessage : INetSerializable
    {
        /// <summary>Rig override of the core count a spawner reports.</summary>
        public const string ENV_HOST_CORES = "MESH_HOST_CORES";

        public string hostName;
        public int cores;
        /// <summary>Cores the whole host used over the spawner's last sampling interval (every process it can read; 0 before the first interval).</summary>
        public float coresInUse;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(hostName ?? string.Empty);
            writer.Put(cores);
            writer.Put(coresInUse);
        }

        public void Deserialize(NetDataReader reader)
        {
            hostName = reader.GetString();
            cores = reader.GetInt();
            coresInUse = reader.GetFloat();
        }

        /// <summary>Cores reads the override, else the machine's processor count.</summary>
        public static int Cores(string overrideValue, int processorCount)
        {
            if (!string.IsNullOrEmpty(overrideValue) && int.TryParse(overrideValue.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int cores) && cores > 0)
                return cores;
            return Math.Max(1, processorCount);
        }
    }

    /// <summary>MeshHostInfo is a host as the coordinator sees it: the spawner connection that reported it, its name, cores, and cores in use.</summary>
    public struct MeshHostInfo
    {
        public long spawnerConnection;
        public string hostName;
        public int cores;
        public float coresInUse;
    }

    /// <summary>
    /// MeshHostCpuSampler turns two sums of every process's processor time into the host's cores in use over the
    /// interval between them. Pure: the spawner feeds it the sums it can read.
    /// </summary>
    public sealed class MeshHostCpuSampler
    {
        private double _lastTotalSeconds = -1d;
        private double _lastAtSeconds;

        /// <summary>Sample takes the host-wide processor seconds now and returns the cores in use since the previous sample (0 on the first).</summary>
        public float Sample(double totalProcessorSeconds, double nowSeconds)
        {
            float cores = 0f;
            if (_lastTotalSeconds >= 0d && nowSeconds > _lastAtSeconds)
                cores = (float)Math.Max(0d, (totalProcessorSeconds - _lastTotalSeconds) / (nowSeconds - _lastAtSeconds));
            _lastTotalSeconds = totalProcessorSeconds;
            _lastAtSeconds = nowSeconds;
            return cores;
        }
    }
}
