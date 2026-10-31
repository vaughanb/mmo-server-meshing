// Server Meshing add-on — how a character puppet moves on the cell that mirrors it (steered by the kit's own movement,
// or placed by a kinematic body), the MESH_PUPPET_MOTION setting, and why a puppet fell back to steering.
using System;
using System.Collections.Generic;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>MeshPuppetMotion is how a character puppet follows its owner's snapshots.</summary>
    public enum MeshPuppetMotion : byte
    {
        /// <summary>Steered walks the puppet with the kit's movement component toward each snapshot.</summary>
        Steered = 0,

        /// <summary>Kinematic disables the kit's movement component and places the puppet from a track of the snapshots (the default of a new map manager, DESIGN 1.34).</summary>
        Kinematic = 1,
    }

    /// <summary>MeshPuppetFallback is why a puppet stays steered while the setting asks for kinematic motion.</summary>
    public enum MeshPuppetFallback : byte
    {
        /// <summary>None means nothing refused kinematic motion.</summary>
        None = 0,

        /// <summary>Mounted means the puppet rides a vehicle, whose movement places it.</summary>
        Mounted,

        /// <summary>Summoned means the puppet is a pet, which the kit teleports to its summoner.</summary>
        Summoned,

        /// <summary>Handler means the kit movement handler is not one whose bytes the kinematic body reproduces.</summary>
        Handler,

        /// <summary>
        /// Grid is retired: nothing returns it since the add-on writes the kit's quantized layout (package 0.97.0). It keeps
        /// its value so Install keeps 5 and the puppets window line keeps its grid count (always 0) for the rig's parsers.
        /// </summary>
        Grid,

        /// <summary>Install means the handler slot refused the kinematic body.</summary>
        Install,
    }

    /// <summary>
    /// MeshPuppetMotionSetting reads the MESH_PUPPET_MOTION environment value and names the extra managed-update components a
    /// puppet silences for each motion.
    /// </summary>
    public static class MeshPuppetMotionSetting
    {
        /// <summary>EnvironmentKey is the environment variable that overrides the inspector's puppet motion.</summary>
        public const string EnvironmentKey = "MESH_PUPPET_MOTION";

        private static readonly string[] s_steeredExtras = Array.Empty<string>();
        private static readonly string[] s_kinematicExtras = { "PlayerCharacterItemLockAndExpireComponent" };

        /// <summary>
        /// TryParse reads "steered" or "kinematic", ignoring case and surrounding whitespace. Anything else (empty, a number,
        /// another word) fails and leaves <paramref name="motion"/> Steered.
        /// </summary>
        public static bool TryParse(string text, out MeshPuppetMotion motion)
        {
            motion = MeshPuppetMotion.Steered;
            if (text == null)
                return false;
            string trimmed = text.Trim();
            if (string.Equals(trimmed, "steered", StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(trimmed, "kinematic", StringComparison.OrdinalIgnoreCase))
            {
                motion = MeshPuppetMotion.Kinematic;
                return true;
            }
            return false;
        }

        /// <summary>
        /// PuppetExtraTypeNames lists the managed-update type names a player puppet silences beyond the default deny list:
        /// none for Steered, so a steered puppet keeps today's list. The same instance is returned on every call.
        /// </summary>
        public static IReadOnlyList<string> PuppetExtraTypeNames(MeshPuppetMotion motion)
        {
            switch (motion)
            {
                case MeshPuppetMotion.Steered:
                    return s_steeredExtras;
                case MeshPuppetMotion.Kinematic:
                    return s_kinematicExtras;
                default:
                    throw new ArgumentOutOfRangeException(nameof(motion), motion, "unknown puppet motion");
            }
        }
    }
}
