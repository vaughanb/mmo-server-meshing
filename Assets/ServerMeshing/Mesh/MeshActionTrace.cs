// Server Meshing add-on — action trace for crossings (DESIGN.md 1.06).
//
// One line per attack or skill start and per trigger, in every process (the client and each cell), when the process
// was started with MESH_TRACE_ACTIONS=1 (the cells inherit the editor's environment: set it before Play). Each line
// names the seed, so a swing can be followed across the client's Editor.log and the cells' logs: a client start with
// no server start was a command lost; a client trigger with no server trigger was damage lost. Off by default.
using LiteNetLibManager;

namespace MultiplayerARPG.MMO.Mesh
{
    public static class MeshActionTrace
    {
        /// <summary>Read once per domain: the process's environment at the first action.</summary>
        public static readonly bool Enabled = System.Environment.GetEnvironmentVariable("MESH_TRACE_ACTIONS") == "1";

        /// <summary>The seed the kit derives from an action's timestamp (CharacterAttack/UseSkill components' private GetSimulateSeed).</summary>
        public static int SeedOf(long peerTimestamp) => (int)(peerTimestamp % 16384);

        public static void Log(BaseGameEntity entity, string what)
        {
            if (!Enabled || !(entity is BasePlayerCharacterEntity player) || entity.Identity == null)
                return;
            string role = entity.IsServer ? (MeshIdentity.IsGhostEntity(entity) ? "S-ghost" : "S") : entity.IsOwnerClient ? "C" : "C-other";
            Logging.Log("MeshAction", $"{System.DateTime.Now:HH:mm:ss.fff} {role} {player.CharacterName} obj={entity.ObjectId} kept={entity.Identity.IsKept} {what}");
        }
    }
}
