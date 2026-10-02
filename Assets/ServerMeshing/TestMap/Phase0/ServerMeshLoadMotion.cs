// Server Meshing add-on — rig load motion (MESH_LOAD_MOTION, MESH_LOAD_FIGHT): how the load generator's synthetic players move, and whether steered ones fight.
using LiteNetLibManager;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>ServerMeshLoadMotionKind is how the load generator's synthetic players move (MESH_LOAD_MOTION).</summary>
    public enum ServerMeshLoadMotionKind
    {
        /// <summary>Steered players get the wander-and-fight brain and walk through the kit's movement component, as the load always has.</summary>
        Steered = 0,
        /// <summary>Kinematic players have the kit's movement component switched off once it has started, and their transforms moved directly.</summary>
        Kinematic = 1,
        /// <summary>Inert players get no brain: spawned and standing, with the kit's movement component left enabled.</summary>
        Inert = 2,
    }

    /// <summary>
    /// ServerMeshLoadMotion reads MESH_LOAD_MOTION (steered, kinematic or inert; default steered) and MESH_LOAD_FIGHT (0 or 1;
    /// default 1). An unset variable means the default; a value that does not parse logs a warning and keeps the default.
    /// </summary>
    public static class ServerMeshLoadMotion
    {
        /// <summary>ENV_MOTION is "steered", "kinematic" or "inert" (ignoring case and surrounding whitespace).</summary>
        public const string ENV_MOTION = "MESH_LOAD_MOTION";
        /// <summary>ENV_FIGHT is "1" (steered players attack monsters in reach) or "0" (they only wander and heal).</summary>
        public const string ENV_FIGHT = "MESH_LOAD_FIGHT";
        /// <summary>DefaultMotion is the motion of an unset or unreadable MESH_LOAD_MOTION.</summary>
        public const ServerMeshLoadMotionKind DefaultMotion = ServerMeshLoadMotionKind.Steered;
        /// <summary>DefaultFight is the fight flag of an unset or unreadable MESH_LOAD_FIGHT.</summary>
        public const bool DefaultFight = true;

        /// <summary>TryParseMotion reads a MESH_LOAD_MOTION value; on failure motion is <see cref="DefaultMotion"/>.</summary>
        public static bool TryParseMotion(string text, out ServerMeshLoadMotionKind motion)
        {
            motion = DefaultMotion;
            if (text == null)
                return false;
            string value = text.Trim();
            if (string.Equals(value, "steered", System.StringComparison.OrdinalIgnoreCase))
                motion = ServerMeshLoadMotionKind.Steered;
            else if (string.Equals(value, "kinematic", System.StringComparison.OrdinalIgnoreCase))
                motion = ServerMeshLoadMotionKind.Kinematic;
            else if (string.Equals(value, "inert", System.StringComparison.OrdinalIgnoreCase))
                motion = ServerMeshLoadMotionKind.Inert;
            else
                return false;
            return true;
        }

        /// <summary>TryParseFight reads a MESH_LOAD_FIGHT value, "0" or "1" with surrounding whitespace allowed; on failure fight is <see cref="DefaultFight"/>.</summary>
        public static bool TryParseFight(string text, out bool fight)
        {
            fight = DefaultFight;
            string value = text?.Trim();
            if (value == "0")
                fight = false;
            else if (value != "1")
                return false;
            return true;
        }

        /// <summary>
        /// ResolveMotion is the motion a MESH_LOAD_MOTION value selects: the default when it is null or empty (no warning),
        /// the parsed motion when it reads, else the default with a warning naming the variable and the value.
        /// </summary>
        public static ServerMeshLoadMotionKind ResolveMotion(string text, out string warning)
        {
            warning = null;
            if (string.IsNullOrEmpty(text))
                return DefaultMotion;
            if (TryParseMotion(text, out ServerMeshLoadMotionKind motion))
                return motion;
            warning = Printable($"Load generator: {ENV_MOTION}={text} is not steered, kinematic or inert; steered kept");
            return DefaultMotion;
        }

        /// <summary>
        /// ResolveFight is the fight flag a MESH_LOAD_FIGHT value selects: the default when it is null or empty (no
        /// warning), the parsed flag when it reads, else the default with a warning naming the variable and the value.
        /// </summary>
        public static bool ResolveFight(string text, out string warning)
        {
            warning = null;
            if (string.IsNullOrEmpty(text))
                return DefaultFight;
            if (TryParseFight(text, out bool fight))
                return fight;
            warning = Printable($"Load generator: {ENV_FIGHT}={text} is not 0 or 1; fighting kept on");
            return DefaultFight;
        }

        /// <summary>ReadMotion is <see cref="ResolveMotion"/> of the environment, logging its warning.</summary>
        public static ServerMeshLoadMotionKind ReadMotion()
        {
            ServerMeshLoadMotionKind motion = ResolveMotion(System.Environment.GetEnvironmentVariable(ENV_MOTION), out string warning);
            if (warning != null)
                Logging.LogWarning("Mesh", warning);
            return motion;
        }

        /// <summary>ReadFight is <see cref="ResolveFight"/> of the environment, logging its warning.</summary>
        public static bool ReadFight()
        {
            bool fight = ResolveFight(System.Environment.GetEnvironmentVariable(ENV_FIGHT), out string warning);
            if (warning != null)
                Logging.LogWarning("Mesh", warning);
            return fight;
        }

        /// <summary>SettingLine is the line the load generator logs when the motion or the fight flag is not the default.</summary>
        public static string SettingLine(ServerMeshLoadMotionKind motion, bool fight)
        {
            return $"Load generator: motion {MotionName(motion)} ({ENV_MOTION}), fight {(fight ? "on" : "off")} ({ENV_FIGHT})";
        }

        private static string MotionName(ServerMeshLoadMotionKind motion)
        {
            switch (motion)
            {
                case ServerMeshLoadMotionKind.Steered: return "steered";
                case ServerMeshLoadMotionKind.Kinematic: return "kinematic";
                case ServerMeshLoadMotionKind.Inert: return "inert";
                default: return ((int)motion).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        private static string Printable(string text) => text.Replace('{', '(').Replace('}', ')');
    }
}
