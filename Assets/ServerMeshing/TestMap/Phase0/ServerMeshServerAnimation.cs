// Server Meshing add-on — rig server animation (MESH_RIG_SERVER_ANIMATION): switches the kit's animation evaluation on a map server on or off.
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshServerAnimation sets the game instance's updateAnimationAtServer from MESH_RIG_SERVER_ANIMATION ("0" or
    /// "1") once the map server serves, and logs it once. The flag is read every entity update, so it applies at once to
    /// every character on the process: players, monsters and puppets. A value that does not parse logs a warning and
    /// leaves the kit's value. Never in the editor. With the variable unset it is not installed and nothing changes.
    /// </summary>
    public class ServerMeshServerAnimation : MonoBehaviour
    {
        /// <summary>ENV_SERVER_ANIMATION is "1" (the server evaluates character animation, the kit's default) or "0" (it does not).</summary>
        public const string ENV_SERVER_ANIMATION = "MESH_RIG_SERVER_ANIMATION";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoStart()
        {
            if (string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable(ENV_SERVER_ANIMATION)))
                return;
            if (FindFirstObjectByType<ServerMeshServerAnimation>() != null)
                return;
            GameObject go = new GameObject("[Mesh] ServerAnimation");
            DontDestroyOnLoad(go);
            go.AddComponent<ServerMeshServerAnimation>();
        }

        /// <summary>TryParseEnabled reads a MESH_RIG_SERVER_ANIMATION value, "0" or "1" with surrounding whitespace allowed; on failure enabled is false.</summary>
        public static bool TryParseEnabled(string text, out bool enabled)
        {
            string value = text?.Trim();
            enabled = value == "1";
            return enabled || value == "0";
        }

        private void Update()
        {
            if (!(BaseGameNetworkManager.Singleton is MeshMapNetworkManager mesh) || !mesh.IsServer || GameInstance.Singleton == null)
                return;
            Apply(GameInstance.Singleton);
            enabled = false;
        }

        private static void Apply(GameInstance gameInstance)
        {
            string text = System.Environment.GetEnvironmentVariable(ENV_SERVER_ANIMATION);
            if (Application.isEditor)
            {
                Logging.LogWarning("Mesh", $"Server animation: {ENV_SERVER_ANIMATION} is ignored in the editor; updateAnimationAtServer left at {Bit(gameInstance.updateAnimationAtServer)}");
                return;
            }
            if (!TryParseEnabled(text, out bool value))
            {
                Logging.LogWarning("Mesh", Printable($"Server animation: {ENV_SERVER_ANIMATION}={text} is not 0 or 1; updateAnimationAtServer left at {Bit(gameInstance.updateAnimationAtServer)}"));
                return;
            }
            bool kit = gameInstance.updateAnimationAtServer;
            gameInstance.updateAnimationAtServer = value;
            Logging.Log("Mesh", $"Server animation: updateAnimationAtServer {Bit(value)} ({ENV_SERVER_ANIMATION}; kit {Bit(kit)})");
        }

        private static string Bit(bool value) => value ? "1" : "0";

        private static string Printable(string text) => text.Replace('{', '(').Replace('}', ')');
    }
}
