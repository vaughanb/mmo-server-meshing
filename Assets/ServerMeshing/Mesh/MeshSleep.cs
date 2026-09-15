// Server Meshing add-on — sleeping cells: shared configuration and the cluster messages (DESIGN.md Q13, 0.55).
//
// A cell process runs only while a player is in it or within a few cells of it. The central server (MeshCentralNetworkManager)
// keeps the awake set from the cluster's per-channel user counts, asks a map-spawn server to start a wanted cell through the
// kit's own spawn request, and tells an unwanted cell to exit after a grace period. Cells and the login server ask the
// central to wake a cell they need (a login or a warp into a sleeping cell; a handoff toward one, as a safety net).
using LiteNetLib.Utils;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>Rig toggles for sleeping cells, read from the environment once (production sets the prefab fields instead).</summary>
    public static class MeshSleepConfig
    {
        public const string ENV_ENABLED = "MESH_SLEEP";        // 1: cells start on demand and sleep when nobody is near
        public const string ENV_RINGS = "MESH_SLEEP_RINGS";    // rings of neighbours kept awake around a cell with players (default 2)
        public const string ENV_AFTER = "MESH_SLEEP_AFTER";    // seconds a cell stays up after it stopped being wanted

        public static bool EnvEnabled => System.Environment.GetEnvironmentVariable(ENV_ENABLED) == "1";

        public static int EnvRings(int fallback)
        {
            return int.TryParse(System.Environment.GetEnvironmentVariable(ENV_RINGS), out int rings) && rings >= 0 ? rings : fallback;
        }

        public static float EnvAfterSeconds(float fallback)
        {
            return float.TryParse(System.Environment.GetEnvironmentVariable(ENV_AFTER), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float seconds) && seconds >= 0f ? seconds : fallback;
        }

        /// <summary>The kit's cluster key of a cell's map server: channel id + map id.</summary>
        public static string PeerKey(MeshCell cell) => cell.id + "_" + cell.mapId;
    }

    public static class MeshClusterMsg
    {
        public const ushort Sleep = 30010;   // central -> map server: exit when no player is on you
        public const ushort Wake = 30011;    // map server -> central (request): a cell I need is asleep
    }

    /// <summary>Central to a map server: nobody needs this cell; save and exit (ignored while a player is on it).</summary>
    public struct MeshSleepMessage : INetSerializable
    {
        public string cellId;
        public void Serialize(NetDataWriter writer) { writer.Put(cellId ?? string.Empty); }
        public void Deserialize(NetDataReader reader) { cellId = reader.GetString(); }
    }

    /// <summary>A map server (or the login server) asks the central to wake a cell.</summary>
    public struct MeshWakeRequest : INetSerializable
    {
        public string cellId;
        public string mapId;
        public void Serialize(NetDataWriter writer) { writer.Put(cellId ?? string.Empty); writer.Put(mapId ?? string.Empty); }
        public void Deserialize(NetDataReader reader) { cellId = reader.GetString(); mapId = reader.GetString(); }
    }

    public struct MeshWakeResponse : INetSerializable
    {
        public bool accepted;
        public void Serialize(NetDataWriter writer) { writer.Put(accepted); }
        public void Deserialize(NetDataReader reader) { accepted = reader.GetBool(); }
    }
}
