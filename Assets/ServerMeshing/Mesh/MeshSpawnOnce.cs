// Server Meshing add-on — a map spawner starts its cells once per lifetime (DESIGN.md 0.72, plan D7).
namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// MeshSpawnOnce lets a map spawner start its cells on its first cluster registration only: it registers again after
    /// every central restart, and its cells are still running then (the spawner outlives the central).
    /// </summary>
    public sealed class MeshSpawnOnce
    {
        public bool Started { get; private set; }

        /// <summary>TryStart is true the first time it is called, and false ever after.</summary>
        public bool TryStart()
        {
            if (Started)
                return false;
            Started = true;
            return true;
        }
    }
}
