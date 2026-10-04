// Server Meshing test rig — every stand-in ship key in one resolver (Phase 0 step 7): MESH_LOAD_SHIPS,
// MESH_LOAD_SHIP_PILOTS, MESH_SHIP_WIRE, MESH_SHIP_SCALE, MESH_SHIP_PROJECTILES, MESH_SHIP_MIRROR_VELOCITY,
// MESH_RIG_SHIPS and (step 7b) MESH_SHIP_SELF_ACK and MESH_SHIP_AI_PREFAB, plus the bot argument -meshRigShips. Default
// off: with no ship key nothing else is read. The ship they configure is modelled on the space game at commits 1a3a58d9
// and c2eb4154.
using System;
using System.Globalization;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshShipSettings is a process's resolved ship settings. AnyShipKey is false when no ship key is set (none of
    /// the nine ship keys, no MESH_PHANTOM of kind ship, no MESH_PUPPET_FEEDER of kind ship); the other properties then
    /// hold their defaults and nothing else was read.
    /// </summary>
    public sealed class ServerMeshShipSettings
    {
        /// <summary>LoadKey is the load plan: &lt;cell&gt;:&lt;n&gt;:&lt;ai|mirror|parked&gt;[,...].</summary>
        public const string LoadKey = "MESH_LOAD_SHIPS";
        /// <summary>PilotsKey seats a server pilot in each load ship: 0 (the default) or 1.</summary>
        public const string PilotsKey = "MESH_LOAD_SHIP_PILOTS";
        /// <summary>WireKey is the record layout: legacy54 (the default), header56 or compact28.</summary>
        public const string WireKey = "MESH_SHIP_WIRE";
        /// <summary>ScaleKey is the scale factor, 0.001 to 10 (default 1).</summary>
        public const string ScaleKey = "MESH_SHIP_SCALE";
        /// <summary>ProjectilesKey is how server bolts live: instantiate (the default) or pooled.</summary>
        public const string ProjectilesKey = "MESH_SHIP_PROJECTILES";
        /// <summary>MirrorVelocityKey is what an owner mirror's records carry as velocities: real (the default) or zero.</summary>
        public const string MirrorVelocityKey = "MESH_SHIP_MIRROR_VELOCITY";
        /// <summary>RigShipsKey makes the runner pass the bot argument: 1.</summary>
        public const string RigShipsKey = "MESH_RIG_SHIPS";
        /// <summary>
        /// SelfAckKey is how an unowned ship's teleport is acknowledged: 0 (the default, the space game's rule: only an
        /// owner's upload acknowledges) or 1 (step 7's two-tick self-acknowledgement).
        /// </summary>
        public const string SelfAckKey = "MESH_SHIP_SELF_ACK";
        /// <summary>AiPrefabKey is the class of ServerAI load ships: enemy (the default, Ship_Enemy) or wave (Ship_AI, the Base numbers).</summary>
        public const string AiPrefabKey = "MESH_SHIP_AI_PREFAB";
        /// <summary>BotArgument is the bot's command-line argument: &lt;wire&gt;:&lt;seed&gt;:&lt;scale&gt;.</summary>
        public const string BotArgument = "-meshRigShips";
        /// <summary>SeedKey is the load generator's seed, read once a ship key is set (default 12345).</summary>
        public const string SeedKey = "MESH_LOAD_SEED";
        /// <summary>AreaKey is the load generator's area x,z,r, read once a ship key is set; it also confines load ships.</summary>
        public const string AreaKey = "MESH_LOAD_AREA";
        /// <summary>PhantomKey is the phantom setting; only its kind field (the third) is read here.</summary>
        public const string PhantomKey = "MESH_PHANTOM";
        /// <summary>FeederKey is the puppet feeder setting; only its kind (before the first colon) is read here.</summary>
        public const string FeederKey = "MESH_PUPPET_FEEDER";
        /// <summary>DefaultSeed is the seed when MESH_LOAD_SEED is unset.</summary>
        public const int DefaultSeed = 12345;

        private static readonly string[] ShipKeys = { LoadKey, PilotsKey, WireKey, ScaleKey, ProjectilesKey, MirrorVelocityKey, RigShipsKey, SelfAckKey, AiPrefabKey };

        private ServerMeshShipSettings()
        {
            Wire = ServerMeshShipWireKind.Legacy54;
            Scale = new ServerMeshShipScale(1f);
            RealMirrorVelocity = true;
            Seed = DefaultSeed;
        }

        /// <summary>AnyShipKey is true when any ship key is set (see the class summary).</summary>
        public bool AnyShipKey { get; private set; }

        /// <summary>Load is the load plan, or null when MESH_LOAD_SHIPS is unset.</summary>
        public ServerMeshShipLoadPlan Load { get; private set; }

        /// <summary>Pilots is true when each load ship gets a seated server pilot.</summary>
        public bool Pilots { get; private set; }

        /// <summary>Wire is the record layout.</summary>
        public ServerMeshShipWireKind Wire { get; private set; }

        /// <summary>Scale is the scale factor and its scaled quantities.</summary>
        public ServerMeshShipScale Scale { get; private set; }

        /// <summary>PooledProjectiles is true for pooled bolts with RaycastNonAlloc, false for the space game's instantiated bolts.</summary>
        public bool PooledProjectiles { get; private set; }

        /// <summary>
        /// RealMirrorVelocity is true (the default) when owner mirrors send the last upload's velocities, false for
        /// MESH_SHIP_MIRROR_VELOCITY=zero.
        /// </summary>
        public bool RealMirrorVelocity { get; private set; }

        /// <summary>RigShips is true when MESH_RIG_SHIPS=1.</summary>
        public bool RigShips { get; private set; }

        /// <summary>SelfAck is true when MESH_SHIP_SELF_ACK=1: an unowned ship acknowledges its own teleport after two ticks.</summary>
        public bool SelfAck { get; private set; }

        /// <summary>WaveAi is true when MESH_SHIP_AI_PREFAB=wave: ServerAI load ships take the Base class (Ship_AI).</summary>
        public bool WaveAi { get; private set; }

        /// <summary>AiClass is the class of ServerAI load ships: Base under WaveAi, else Enemy. Every other ship is Base.</summary>
        public ServerMeshShipClass AiClass => WaveAi ? ServerMeshShipClass.Base : ServerMeshShipClass.Enemy;

        /// <summary>Seed is the path and pilot seed (MESH_LOAD_SEED, default 12345).</summary>
        public int Seed { get; private set; }

        /// <summary>HasArea is true when MESH_LOAD_AREA is set.</summary>
        public bool HasArea { get; private set; }

        /// <summary>Area is the MESH_LOAD_AREA square (x is world x, y is world z) when HasArea.</summary>
        public Rect Area { get; private set; }

        /// <summary>
        /// TryResolve reads every ship key through <paramref name="read"/> (a key to its value, null when unset). With no
        /// ship key it reads nothing else and returns the defaults. Otherwise any bad value, MESH_LOAD_SEED and
        /// MESH_LOAD_AREA included, returns null with an error naming the key. A null reader throws naming it.
        /// </summary>
        public static ServerMeshShipSettings TryResolve(Func<string, string> read, out string error)
        {
            if (read == null)
                throw new ArgumentNullException(nameof(read));
            error = null;
            ServerMeshShipSettings settings = new ServerMeshShipSettings();
            if (!HasShipKey(read))
                return settings;
            settings.AnyShipKey = true;

            string load = Trimmed(read(LoadKey));
            if (load.Length > 0)
            {
                settings.Load = ServerMeshShipLoadPlan.TryParse(load, out string planError);
                if (settings.Load == null)
                    return Fail(out error, $"{LoadKey}={Clean(load)}: {planError}");
            }
            if (!TryParseSwitch(read(PilotsKey), "0", "1", out bool pilots))
                return Fail(out error, $"{PilotsKey}={Clean(read(PilotsKey))} is not 0 or 1");
            settings.Pilots = pilots;
            if (!TryParseWire(read(WireKey), out ServerMeshShipWireKind wire, out error))
                return null;
            settings.Wire = wire;
            if (!ServerMeshShipScale.TryParse(read(ScaleKey), out float factor, out string scaleError))
                return Fail(out error, $"{ScaleKey}: {scaleError}");
            settings.Scale = new ServerMeshShipScale(factor);
            if (!TryParseSwitch(read(ProjectilesKey), "instantiate", "pooled", out bool pooled))
                return Fail(out error, $"{ProjectilesKey}={Clean(read(ProjectilesKey))} is not instantiate or pooled");
            settings.PooledProjectiles = pooled;
            if (!TryParseSwitch(read(MirrorVelocityKey), "real", "zero", out bool zero))
                return Fail(out error, $"{MirrorVelocityKey}={Clean(read(MirrorVelocityKey))} is not real or zero");
            settings.RealMirrorVelocity = !zero;
            if (!TryParseSwitch(read(RigShipsKey), "0", "1", out bool rigShips))
                return Fail(out error, $"{RigShipsKey}={Clean(read(RigShipsKey))} is not 0 or 1");
            settings.RigShips = rigShips;
            if (!TryParseSwitch(read(SelfAckKey), "0", "1", out bool selfAck))
                return Fail(out error, $"{SelfAckKey}={Clean(read(SelfAckKey))} is not 0 or 1");
            settings.SelfAck = selfAck;
            if (!TryParseSwitch(read(AiPrefabKey), "enemy", "wave", out bool wave))
                return Fail(out error, $"{AiPrefabKey}={Clean(read(AiPrefabKey))} is not enemy or wave");
            settings.WaveAi = wave;
            if (!TryParseSeed(read(SeedKey), out int seed))
                return Fail(out error, $"{SeedKey}={Clean(read(SeedKey))} is not a whole number");
            settings.Seed = seed;
            string area = Trimmed(read(AreaKey));
            if (area.Length > 0)
            {
                if (!TryParseArea(area, out Rect rect))
                    return Fail(out error, $"{AreaKey}={Clean(area)} is not x,z,r with a positive r");
                settings.HasArea = true;
                settings.Area = rect;
            }
            return settings;
        }

        /// <summary>
        /// TryParseWire reads a MESH_SHIP_WIRE value in any case: null or empty gives legacy54; legacy54, header56 and
        /// compact28 their kinds. Anything else returns false with legacy54 and an error naming the key.
        /// </summary>
        public static bool TryParseWire(string text, out ServerMeshShipWireKind wire, out string error)
        {
            wire = ServerMeshShipWireKind.Legacy54;
            error = null;
            string trimmed = Trimmed(text);
            if (trimmed.Length == 0 || Is(trimmed, "legacy54"))
                return true;
            if (Is(trimmed, "header56"))
            {
                wire = ServerMeshShipWireKind.Header56;
                return true;
            }
            if (Is(trimmed, "compact28"))
            {
                wire = ServerMeshShipWireKind.Compact28;
                return true;
            }
            error = $"{WireKey}={Clean(trimmed)} is not legacy54, header56 or compact28";
            return false;
        }

        /// <summary>WireName is the setting word of <paramref name="wire"/>: legacy54, header56 or compact28.</summary>
        public static string WireName(ServerMeshShipWireKind wire)
        {
            switch (wire)
            {
                case ServerMeshShipWireKind.Header56: return "header56";
                case ServerMeshShipWireKind.Compact28: return "compact28";
                default: return "legacy54";
            }
        }

        /// <summary>
        /// TryParseBotArgument reads a -meshRigShips value &lt;wire&gt;:&lt;seed&gt;:&lt;scale&gt;, every field required. Anything
        /// else returns false with legacy54, the default seed, scale 1 and an error naming the argument.
        /// </summary>
        public static bool TryParseBotArgument(string text, out ServerMeshShipWireKind wire, out int seed, out ServerMeshShipScale scale, out string error)
        {
            wire = ServerMeshShipWireKind.Legacy54;
            seed = DefaultSeed;
            scale = new ServerMeshShipScale(1f);
            error = null;
            string trimmed = Trimmed(text);
            string[] fields = trimmed.Split(':');
            if (fields.Length != 3 || Trimmed(fields[0]).Length == 0 || Trimmed(fields[1]).Length == 0 || Trimmed(fields[2]).Length == 0)
                return BotFail(out error, trimmed, "is not wire:seed:scale");
            if (!TryParseWire(fields[0], out ServerMeshShipWireKind parsedWire, out _))
                return BotFail(out error, trimmed, "has a wire that is not legacy54, header56 or compact28");
            if (!TryParseSeed(fields[1], out int parsedSeed))
                return BotFail(out error, trimmed, "has a seed that is not a whole number");
            if (!ServerMeshShipScale.TryParse(fields[2], out float factor, out _))
                return BotFail(out error, trimmed, "has a scale that is not a number from 0.001 to 10");
            wire = parsedWire;
            seed = parsedSeed;
            scale = new ServerMeshShipScale(factor);
            return true;
        }

        /// <summary>
        /// BotArgumentValue is the -meshRigShips value for <paramref name="wire"/>, <paramref name="seed"/> and the scale text
        /// verbatim (trimmed; empty gives 1), for example header56:12345:0.03.
        /// </summary>
        public static string BotArgumentValue(ServerMeshShipWireKind wire, int seed, string scaleText)
        {
            string scale = Trimmed(scaleText);
            return WireName(wire) + ":" + seed.ToString(CultureInfo.InvariantCulture) + ":" + (scale.Length == 0 ? "1" : scale);
        }

        private static bool HasShipKey(Func<string, string> read)
        {
            foreach (string key in ShipKeys)
                if (Trimmed(read(key)).Length > 0)
                    return true;
            string[] phantom = Trimmed(read(PhantomKey)).Split(':');
            if (phantom.Length >= 3 && Is(phantom[2].Trim(), "ship"))
                return true;
            return Trimmed(read(FeederKey)).StartsWith("ship:", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryParseSwitch(string text, string off, string on, out bool value)
        {
            string trimmed = Trimmed(text);
            value = Is(trimmed, on);
            return value || trimmed.Length == 0 || Is(trimmed, off);
        }

        private static bool TryParseSeed(string text, out int seed)
        {
            string trimmed = Trimmed(text);
            seed = DefaultSeed;
            if (trimmed.Length == 0)
                return true;
            return int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out seed);
        }

        private static bool TryParseArea(string text, out Rect area)
        {
            area = default;
            string[] parts = text.Split(',');
            if (parts.Length != 3)
                return false;
            float[] values = new float[3];
            for (int i = 0; i < 3; ++i)
            {
                if (!float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out values[i])
                    || float.IsNaN(values[i]) || float.IsInfinity(values[i]))
                    return false;
            }
            if (values[2] <= 0f)
                return false;
            area = new Rect(values[0] - values[2], values[1] - values[2], 2f * values[2], 2f * values[2]);
            return true;
        }

        private static ServerMeshShipSettings Fail(out string error, string message)
        {
            error = message;
            return null;
        }

        private static bool BotFail(out string error, string text, string problem)
        {
            error = $"{BotArgument} {Clean(text)} {problem}";
            return false;
        }

        private static string Clean(string text) => Sync.MeshStateSender.CleanForLog(Trimmed(text));

        private static string Trimmed(string text) => text == null ? string.Empty : text.Trim();

        private static bool Is(string value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
    }
}
