namespace MultiplayerARPG.MMO
{
    /// <summary>
    /// Extends ProcessArguments with zone-specific command-line args for server meshing.
    /// </summary>
    public static partial class ProcessArguments
    {
        public const string CONFIG_ZONE_X = "zoneX";
        public const string ARG_ZONE_X = "-" + CONFIG_ZONE_X;

        public const string CONFIG_ZONE_Y = "zoneY";
        public const string ARG_ZONE_Y = "-" + CONFIG_ZONE_Y;

        public const string CONFIG_ZONE_ID = "zoneId";
        public const string ARG_ZONE_ID = "-" + CONFIG_ZONE_ID;
    }
}
