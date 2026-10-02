// Server Meshing add-on — rig phantom settings (MESH_PHANTOM and its keys, -meshPhantomSink): the pure grammar every phantom part reads.
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>ServerMeshPhantomRoute is where a phantom's client lives: inside the server process (virtual) or on a sink machine (sink).</summary>
    public enum ServerMeshPhantomRoute
    {
        /// <summary>Virtual phantoms are synthetic connections of the phantom transport; their downstream is counted and swallowed.</summary>
        Virtual = 0,
        /// <summary>Sink phantoms are real LiteNetLib peers of a sink process; each binds by its hello.</summary>
        Sink = 1,
    }

    /// <summary>ServerMeshPhantomKind is what a phantom owns: a walking character or a hidden eye.</summary>
    public enum ServerMeshPhantomKind
    {
        /// <summary>Character phantoms own a player character fed by uploads.</summary>
        Character = 0,
        /// <summary>Eye phantoms own a hidden identity that follows a host and observes what is around it.</summary>
        Eye = 1,
    }

    /// <summary>ServerMeshPhantomPayloadKind is what a character phantom uploads (MESH_PHANTOM_PAYLOAD).</summary>
    public enum ServerMeshPhantomPayloadKind
    {
        /// <summary>Transform uploads are the kit's NotSecure sync transform: state, extra state, position, yaw.</summary>
        Transform = 0,
        /// <summary>Input uploads are the kit's ServerAuthoritative key-movement input, sent only when it changes.</summary>
        Input = 1,
    }

    /// <summary>
    /// ServerMeshPhantomSettings is one parsed phantom setting: MESH_PHANTOM with every MESH_PHANTOM_* key checked against
    /// it. The TryParse members read one value each; an empty or null value is unset, gives the default and no error.
    /// <see cref="TryResolve"/> reads them all and applies the rules between keys. Error texts never contain braces.
    /// </summary>
    public sealed class ServerMeshPhantomSettings
    {
        /// <summary>ENV_PHANTOM is the phantom setting: &lt;virtual|sink&gt;:&lt;P&gt;:&lt;character|eye&gt;:&lt;cell[,cell..]|all&gt;.</summary>
        public const string ENV_PHANTOM = "MESH_PHANTOM";
        /// <summary>ENV_VIEW is V, the movers each phantom sees (required unless MESH_PHANTOM_AREA is set; 0 means host mode for eyes).</summary>
        public const string ENV_VIEW = "MESH_PHANTOM_VIEW";
        /// <summary>ENV_ARRIVAL is the bind pace in binds per second (default <see cref="DefaultArrivalPerSecond"/>).</summary>
        public const string ENV_ARRIVAL = "MESH_PHANTOM_ARRIVAL_PER_S";
        /// <summary>ENV_UPLOAD_HZ is the upload rate per phantom, 1 to 60 (default <see cref="DefaultUploadHz"/>).</summary>
        public const string ENV_UPLOAD_HZ = "MESH_PHANTOM_UPLOAD_HZ";
        /// <summary>ENV_PAYLOAD is transform or input (default transform).</summary>
        public const string ENV_PAYLOAD = "MESH_PHANTOM_PAYLOAD";
        /// <summary>ENV_STEPS is a stepped phantom count, &lt;P1,..,Pn&gt;@&lt;seconds&gt; (eye kind, virtual mode).</summary>
        public const string ENV_STEPS = "MESH_PHANTOM_STEPS";
        /// <summary>ENV_AREA is a placement area, &lt;cx&gt;,&lt;cz&gt;,&lt;w&gt;,&lt;h&gt; or disc:&lt;cx&gt;,&lt;cz&gt;,&lt;r&gt; (character kind).</summary>
        public const string ENV_AREA = "MESH_PHANTOM_AREA";
        /// <summary>ENV_STAND is the share of phantoms, 0 to 1, that stand (character kind).</summary>
        public const string ENV_STAND = "MESH_PHANTOM_STAND";
        /// <summary>ENV_FIGHT is the share of phantoms, 0 to 1, that fight through the kit's server-side handlers (character kind).</summary>
        public const string ENV_FIGHT = "MESH_PHANTOM_FIGHT";
        /// <summary>ARG_SINK is the sink process argument, host:port:count[:portBase].</summary>
        public const string ARG_SINK = "-meshPhantomSink";
        /// <summary>ENV_SEED is the load generator's placement seed, which phantom layouts, walks and shares also draw from.</summary>
        public const string ENV_SEED = ServerMeshLoadGenerator.ENV_SEED;

        /// <summary>DefaultArrivalPerSecond is the bind pace of an unset MESH_PHANTOM_ARRIVAL_PER_S.</summary>
        public const float DefaultArrivalPerSecond = 50f;
        /// <summary>DefaultUploadHz is the upload rate of an unset MESH_PHANTOM_UPLOAD_HZ.</summary>
        public const int DefaultUploadHz = 20;
        /// <summary>MinUploadHz is the lowest upload rate MESH_PHANTOM_UPLOAD_HZ accepts.</summary>
        public const int MinUploadHz = 1;
        /// <summary>MaxUploadHz is the highest upload rate MESH_PHANTOM_UPLOAD_HZ accepts.</summary>
        public const int MaxUploadHz = 60;
        /// <summary>MaxSinkPeers is the largest peer count the sink argument accepts.</summary>
        public const int MaxSinkPeers = 10000;

        private static readonly string[] s_noCells = new string[0];

        /// <summary>Route is virtual or sink.</summary>
        public ServerMeshPhantomRoute Route { get; private set; }
        /// <summary>Kind is character or eye.</summary>
        public ServerMeshPhantomKind Kind { get; private set; }
        /// <summary>Phantoms is P, bound on each listed cell.</summary>
        public int Phantoms { get; private set; }
        /// <summary>AllCells is true when the cell field is all.</summary>
        public bool AllCells { get; private set; }
        /// <summary>Cells lists the cell ids of the cell field (empty when it is all).</summary>
        public IReadOnlyList<string> Cells { get; private set; } = s_noCells;
        /// <summary>View is V, or -1 when unset (the placement-area mode).</summary>
        public int View { get; private set; } = -1;
        /// <summary>ArrivalPerSecond is the bind pace.</summary>
        public float ArrivalPerSecond { get; private set; } = DefaultArrivalPerSecond;
        /// <summary>UploadHz is the upload rate per phantom.</summary>
        public int UploadHz { get; private set; } = DefaultUploadHz;
        /// <summary>Payload is transform or input.</summary>
        public ServerMeshPhantomPayloadKind Payload { get; private set; }
        /// <summary>Steps is the stepped phantom counts, or null when MESH_PHANTOM_STEPS is unset.</summary>
        public int[] Steps { get; private set; }
        /// <summary>StepHoldSeconds is how long each step holds once reached.</summary>
        public float StepHoldSeconds { get; private set; }
        /// <summary>HasArea is true when MESH_PHANTOM_AREA is set.</summary>
        public bool HasArea { get; private set; }
        /// <summary>AreaIsDisc is true for a disc area.</summary>
        public bool AreaIsDisc { get; private set; }
        /// <summary>AreaCentre is the area's centre in the scene frame (y is z).</summary>
        public Vector2 AreaCentre { get; private set; }
        /// <summary>AreaSize is a rectangle area's width and height (zero for a disc).</summary>
        public Vector2 AreaSize { get; private set; }
        /// <summary>AreaRadius is a disc area's radius (zero for a rectangle).</summary>
        public float AreaRadius { get; private set; }
        /// <summary>StandShare is the share of phantoms that stand.</summary>
        public float StandShare { get; private set; }
        /// <summary>FightShare is the share of phantoms that fight.</summary>
        public float FightShare { get; private set; }
        /// <summary>FightSet is true when MESH_PHANTOM_FIGHT is set (the Phantom window then prints its fighting count).</summary>
        public bool FightSet { get; private set; }
        /// <summary>Seed is the seed layouts, walks and shares draw from.</summary>
        public int Seed { get; private set; } = ServerMeshLoadGenerator.DefaultSeed;
        /// <summary>Source is the MESH_PHANTOM value as given.</summary>
        public string Source { get; private set; } = string.Empty;

        /// <summary>AreaRegion is the rectangle placement-area phantoms walk in: the rectangle itself, or a disc's inscribed square.</summary>
        public Rect AreaRegion => AreaIsDisc ? ServerMeshPhantomLayout.InscribedSquare(AreaCentre, AreaRadius) : new Rect(AreaCentre - AreaSize * 0.5f, AreaSize);

        /// <summary>ServesCell is true when the cell field is all or lists cellId (case-sensitive, as cell ids are).</summary>
        public bool ServesCell(string cellId)
        {
            if (AllCells)
                return true;
            for (int i = 0; i < Cells.Count; ++i)
            {
                if (string.Equals(Cells[i], cellId, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>SettingLine is the one line the installer logs.</summary>
        public string SettingLine()
        {
            string view = View < 0 ? "unset" : View.ToString(CultureInfo.InvariantCulture);
            string steps = Steps == null ? "none" : string.Join(",", Array.ConvertAll(Steps, s => s.ToString(CultureInfo.InvariantCulture))) + FormattableString.Invariant($"@{StepHoldSeconds:0.0}");
            string area = !HasArea ? "none" : AreaIsDisc
                ? FormattableString.Invariant($"disc {AreaCentre.x:0.0} {AreaCentre.y:0.0} r {AreaRadius:0.0}")
                : FormattableString.Invariant($"rect {AreaCentre.x:0.0} {AreaCentre.y:0.0} size {AreaSize.x:0.0} {AreaSize.y:0.0}");
            return Printable(FormattableString.Invariant(
                $"Phantoms: {ENV_PHANTOM}={Source} installed: mode {RouteName(Route)} kind {KindName(Kind)} P {Phantoms} view {view} arrival {ArrivalPerSecond:0.0}/s upload {UploadHz} Hz payload {PayloadName(Payload)} steps {steps} area {area} stand {StandShare:0.00} fight {FightShare:0.00} seed {Seed}"));
        }

        /// <summary>RouteName is virtual or sink.</summary>
        public static string RouteName(ServerMeshPhantomRoute route) => route == ServerMeshPhantomRoute.Sink ? "sink" : "virtual";

        /// <summary>KindName is character or eye.</summary>
        public static string KindName(ServerMeshPhantomKind kind) => kind == ServerMeshPhantomKind.Eye ? "eye" : "character";

        /// <summary>PayloadName is transform or input.</summary>
        public static string PayloadName(ServerMeshPhantomPayloadKind payload) => payload == ServerMeshPhantomPayloadKind.Input ? "input" : "transform";

        /// <summary>
        /// TryParsePhantom reads MESH_PHANTOM, &lt;virtual|sink&gt;:&lt;P&gt;:&lt;character|eye&gt;:&lt;cell[,cell..]|all&gt;, ignoring case
        /// in the words and whitespace around each field. Null or empty is unset: false with a null error. The kind ship (step 7)
        /// and an omitted cell field are refused with an error.
        /// </summary>
        public static bool TryParsePhantom(string text, out ServerMeshPhantomRoute route, out int phantoms, out ServerMeshPhantomKind kind, out bool allCells, out string[] cells, out string error)
        {
            if (ParsePhantom(text, out route, out phantoms, out kind, out allCells, out cells, out error))
                return true;
            route = ServerMeshPhantomRoute.Virtual;
            phantoms = 0;
            kind = ServerMeshPhantomKind.Character;
            allCells = false;
            cells = s_noCells;
            return false;
        }

        private static bool ParsePhantom(string text, out ServerMeshPhantomRoute route, out int phantoms, out ServerMeshPhantomKind kind, out bool allCells, out string[] cells, out string error)
        {
            route = ServerMeshPhantomRoute.Virtual;
            phantoms = 0;
            kind = ServerMeshPhantomKind.Character;
            allCells = false;
            cells = s_noCells;
            error = null;
            if (string.IsNullOrEmpty(text))
                return false;
            string[] parts = text.Split(':');
            if (parts.Length == 3)
            {
                error = $"{ENV_PHANTOM}={text} has no cell field; add :<cell[,cell..]|all>, or every cell would bind";
                return false;
            }
            if (parts.Length != 4)
            {
                error = $"{ENV_PHANTOM}={text} is not <virtual|sink>:<P>:<character|eye>:<cell[,cell..]|all>";
                return false;
            }
            string mode = parts[0].Trim();
            if (string.Equals(mode, "virtual", StringComparison.OrdinalIgnoreCase))
                route = ServerMeshPhantomRoute.Virtual;
            else if (string.Equals(mode, "sink", StringComparison.OrdinalIgnoreCase))
                route = ServerMeshPhantomRoute.Sink;
            else
            {
                error = $"{ENV_PHANTOM}={text}: mode {mode} is not virtual or sink";
                return false;
            }
            if (!TryParseCount(parts[1], out phantoms) || phantoms < 1)
            {
                error = $"{ENV_PHANTOM}={text}: P {parts[1].Trim()} is not an integer of at least 1";
                phantoms = 0;
                return false;
            }
            string kindText = parts[2].Trim();
            if (string.Equals(kindText, "character", StringComparison.OrdinalIgnoreCase))
                kind = ServerMeshPhantomKind.Character;
            else if (string.Equals(kindText, "eye", StringComparison.OrdinalIgnoreCase))
                kind = ServerMeshPhantomKind.Eye;
            else if (string.Equals(kindText, "ship", StringComparison.OrdinalIgnoreCase))
            {
                error = $"{ENV_PHANTOM}={text}: the kind ship needs step 7";
                return false;
            }
            else
            {
                error = $"{ENV_PHANTOM}={text}: kind {kindText} is not character or eye";
                return false;
            }
            string cellField = parts[3].Trim();
            if (string.Equals(cellField, "all", StringComparison.OrdinalIgnoreCase))
            {
                allCells = true;
                return true;
            }
            string[] ids = cellField.Split(',');
            for (int i = 0; i < ids.Length; ++i)
            {
                ids[i] = ids[i].Trim();
                if (ids[i].Length == 0 || string.Equals(ids[i], "all", StringComparison.OrdinalIgnoreCase))
                {
                    error = $"{ENV_PHANTOM}={text}: the cell field {cellField} is not all or a list of cell ids";
                    return false;
                }
                for (int j = 0; j < i; ++j)
                {
                    if (string.Equals(ids[j], ids[i], StringComparison.OrdinalIgnoreCase))
                    {
                        error = $"{ENV_PHANTOM}={text}: the cell field names {ids[i]} twice";
                        return false;
                    }
                }
            }
            cells = ids;
            return true;
        }

        /// <summary>TryParseView reads MESH_PHANTOM_VIEW: an integer of at least 1 for characters, at least 0 for eyes (0 is host mode). Null or empty is unset: true, view -1.</summary>
        public static bool TryParseView(string text, ServerMeshPhantomKind kind, out int view, out string error)
        {
            view = -1;
            error = null;
            if (string.IsNullOrEmpty(text))
                return true;
            int least = kind == ServerMeshPhantomKind.Eye ? 0 : 1;
            if (!TryParseInt(text, out int value) || value < least)
            {
                error = FormattableString.Invariant($"{ENV_VIEW}={text} is not an integer of at least {least} for the {KindName(kind)} kind");
                return false;
            }
            view = value;
            return true;
        }

        /// <summary>TryParseArrival reads MESH_PHANTOM_ARRIVAL_PER_S, a finite number above 0; null or empty gives the default.</summary>
        public static bool TryParseArrival(string text, out float perSecond, out string error)
        {
            perSecond = DefaultArrivalPerSecond;
            error = null;
            if (string.IsNullOrEmpty(text))
                return true;
            if (!TryParseFloat(text, out float value) || value <= 0f)
            {
                error = $"{ENV_ARRIVAL}={text} is not a number above 0";
                return false;
            }
            perSecond = value;
            return true;
        }

        /// <summary>TryParseUploadHz reads MESH_PHANTOM_UPLOAD_HZ, an integer from 1 to 60; null or empty gives the default.</summary>
        public static bool TryParseUploadHz(string text, out int hz, out string error)
        {
            hz = DefaultUploadHz;
            error = null;
            if (string.IsNullOrEmpty(text))
                return true;
            if (!TryParseInt(text, out int value) || value < MinUploadHz || value > MaxUploadHz)
            {
                error = FormattableString.Invariant($"{ENV_UPLOAD_HZ}={text} is not an integer from {MinUploadHz} to {MaxUploadHz}");
                return false;
            }
            hz = value;
            return true;
        }

        /// <summary>TryParsePayload reads MESH_PHANTOM_PAYLOAD, transform or input; ship56 and ship28 need step 7, and input is refused with eyes. Null or empty gives transform.</summary>
        public static bool TryParsePayload(string text, ServerMeshPhantomKind kind, out ServerMeshPhantomPayloadKind payload, out string error)
        {
            payload = ServerMeshPhantomPayloadKind.Transform;
            error = null;
            if (string.IsNullOrEmpty(text))
                return true;
            string value = text.Trim();
            if (string.Equals(value, "transform", StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(value, "input", StringComparison.OrdinalIgnoreCase))
            {
                if (kind == ServerMeshPhantomKind.Eye)
                {
                    error = $"{ENV_PAYLOAD}=input needs the character kind: eyes upload nothing";
                    return false;
                }
                payload = ServerMeshPhantomPayloadKind.Input;
                return true;
            }
            if (string.Equals(value, "ship56", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "ship28", StringComparison.OrdinalIgnoreCase))
            {
                error = $"{ENV_PAYLOAD}={text} needs step 7";
                return false;
            }
            error = $"{ENV_PAYLOAD}={text} is not transform or input";
            return false;
        }

        /// <summary>
        /// TryParseSteps reads MESH_PHANTOM_STEPS, &lt;P1,..,Pn&gt;@&lt;seconds&gt;: each step an integer from 1 to phantoms, the hold a
        /// number above 0. It is refused with the character kind, where V would change with P. Null or empty is unset: true, null steps.
        /// </summary>
        public static bool TryParseSteps(string text, ServerMeshPhantomKind kind, int phantoms, out int[] steps, out float holdSeconds, out string error)
        {
            steps = null;
            holdSeconds = 0f;
            error = null;
            if (string.IsNullOrEmpty(text))
                return true;
            if (kind == ServerMeshPhantomKind.Character)
            {
                error = $"{ENV_STEPS}={text} needs the eye kind: with characters V would change with P";
                return false;
            }
            string[] halves = text.Split('@');
            if (halves.Length != 2 || !TryParseFloat(halves[1], out float hold) || hold <= 0f)
            {
                error = $"{ENV_STEPS}={text} is not <P1,..,Pn>@<seconds above 0>";
                return false;
            }
            string[] counts = halves[0].Split(',');
            int[] parsed = new int[counts.Length];
            for (int i = 0; i < counts.Length; ++i)
            {
                if (!TryParseInt(counts[i], out parsed[i]) || parsed[i] < 1 || parsed[i] > phantoms)
                {
                    error = FormattableString.Invariant($"{ENV_STEPS}={text}: step {counts[i].Trim()} is not an integer from 1 to P {phantoms}");
                    return false;
                }
            }
            steps = parsed;
            holdSeconds = hold;
            return true;
        }

        /// <summary>
        /// TryParseArea reads MESH_PHANTOM_AREA, &lt;cx&gt;,&lt;cz&gt;,&lt;w&gt;,&lt;h&gt; (centre and size, both sizes above 0) or
        /// disc:&lt;cx&gt;,&lt;cz&gt;,&lt;r&gt; (radius above 0). Null or empty is unset: true, set false.
        /// </summary>
        public static bool TryParseArea(string text, out bool set, out bool disc, out Vector2 centre, out Vector2 size, out float radius, out string error)
        {
            set = false;
            disc = false;
            centre = Vector2.zero;
            size = Vector2.zero;
            radius = 0f;
            error = null;
            if (string.IsNullOrEmpty(text))
                return true;
            string value = text.Trim();
            if (value.StartsWith("disc:", StringComparison.OrdinalIgnoreCase))
            {
                string[] d = value.Substring(5).Split(',');
                if (d.Length != 3 || !TryParseFloat(d[0], out float dx) || !TryParseFloat(d[1], out float dz) || !TryParseFloat(d[2], out float r) || r <= 0f)
                {
                    error = $"{ENV_AREA}={text} is not disc:<cx>,<cz>,<r above 0>";
                    return false;
                }
                set = true;
                disc = true;
                centre = new Vector2(dx, dz);
                radius = r;
                return true;
            }
            string[] p = value.Split(',');
            if (p.Length != 4 || !TryParseFloat(p[0], out float cx) || !TryParseFloat(p[1], out float cz) || !TryParseFloat(p[2], out float w) || !TryParseFloat(p[3], out float h) || w <= 0f || h <= 0f)
            {
                error = $"{ENV_AREA}={text} is not <cx>,<cz>,<w>,<h> with sizes above 0, or disc:<cx>,<cz>,<r>";
                return false;
            }
            set = true;
            centre = new Vector2(cx, cz);
            size = new Vector2(w, h);
            return true;
        }

        /// <summary>TryParseShare reads a share from 0 to 1 for the named key (MESH_PHANTOM_STAND, MESH_PHANTOM_FIGHT); null or empty gives 0.</summary>
        public static bool TryParseShare(string key, string text, out float share, out string error)
        {
            share = 0f;
            error = null;
            if (string.IsNullOrEmpty(text))
                return true;
            if (!TryParseFloat(text, out float value) || value < 0f || value > 1f)
            {
                error = $"{key}={text} is not a share from 0 to 1";
                return false;
            }
            share = value;
            return true;
        }

        /// <summary>
        /// TryParseSink reads the sink argument host:port:count[:portBase]: a host, a port from 1 to 65535, a count of at least 1,
        /// and an optional local port base from 1 to 65535 that leaves room for count ports (0 when omitted: any free port).
        /// </summary>
        public static bool TryParseSink(string text, out string host, out int port, out int count, out int portBase, out string error)
        {
            host = null;
            port = 0;
            count = 0;
            portBase = 0;
            error = null;
            string[] p = string.IsNullOrEmpty(text) ? s_noCells : text.Split(':');
            if (p.Length != 3 && p.Length != 4)
            {
                error = $"{ARG_SINK} {text} is not host:port:count[:portBase]";
                return false;
            }
            string h = p[0].Trim();
            if (h.Length == 0)
            {
                error = $"{ARG_SINK} {text} has no host";
                return false;
            }
            if (!TryParseInt(p[1], out int pt) || pt < 1 || pt > 65535)
            {
                error = $"{ARG_SINK} {text}: port {p[1].Trim()} is not from 1 to 65535";
                return false;
            }
            if (!TryParseInt(p[2], out int n) || n < 1 || n > MaxSinkPeers)
            {
                error = FormattableString.Invariant($"{ARG_SINK} {text}: count {p[2].Trim()} is not an integer from 1 to {MaxSinkPeers}");
                return false;
            }
            int b = 0;
            if (p.Length == 4 && (!TryParseInt(p[3], out b) || b < 1 || b + n - 1 > 65535))
            {
                error = $"{ARG_SINK} {text}: port base {p[3].Trim()} is not from 1 with room for every peer below 65536";
                return false;
            }
            host = h;
            port = pt;
            count = n;
            portBase = b;
            return true;
        }

        /// <summary>
        /// StepTarget is the step a stepped run is on: from currentIndex (below 0 means not started) it moves to the next step
        /// once boundCount equals the current step's count and secondsSinceStepReached (negative while not reached) is at least
        /// holdSeconds; the last step stays. target is that step's count.
        /// </summary>
        public static int StepTarget(int[] steps, float holdSeconds, int currentIndex, float secondsSinceStepReached, int boundCount, out int target)
        {
            if (steps == null || steps.Length == 0)
                throw new ArgumentException("a stepped run needs at least one step", nameof(steps));
            int index = Mathf.Clamp(currentIndex, 0, steps.Length - 1);
            if (currentIndex >= 0 && index < steps.Length - 1 && boundCount == steps[index] && secondsSinceStepReached >= 0f && secondsSinceStepReached >= holdSeconds)
                ++index;
            target = steps[index];
            return index;
        }

        /// <summary>
        /// TryResolve reads every phantom key through readValue (an environment lookup) and applies the rules between them:
        /// MESH_PHANTOM_VIEW is required unless MESH_PHANTOM_AREA is set, and then it must be unset; the area, stand and fight
        /// keys need the character kind; steps need the virtual mode. It returns false with a null warning when MESH_PHANTOM is
        /// unset, and false with a warning (naming the key, no braces) when anything is refused; nothing binds then.
        /// </summary>
        public static bool TryResolve(Func<string, string> readValue, out ServerMeshPhantomSettings settings, out string warning)
        {
            if (readValue == null)
                throw new ArgumentNullException(nameof(readValue));
            settings = null;
            warning = null;
            string source = readValue(ENV_PHANTOM);
            if (!TryParsePhantom(source, out ServerMeshPhantomRoute route, out int phantoms, out ServerMeshPhantomKind kind, out bool allCells, out string[] cells, out string error))
            {
                if (error != null)
                    Refuse(error, out warning);
                return false;
            }
            ServerMeshPhantomSettings s = new ServerMeshPhantomSettings
            {
                Source = source.Trim(),
                Route = route,
                Phantoms = phantoms,
                Kind = kind,
                AllCells = allCells,
                Cells = cells,
            };
            string viewText = readValue(ENV_VIEW);
            if (!TryParseArea(readValue(ENV_AREA), out bool hasArea, out bool disc, out Vector2 centre, out Vector2 size, out float radius, out error))
                return Refuse(error, out warning);
            if (hasArea)
            {
                if (kind != ServerMeshPhantomKind.Character)
                    return Refuse($"{ENV_AREA} needs the character kind", out warning);
                if (!string.IsNullOrEmpty(viewText))
                    return Refuse($"{ENV_VIEW}={viewText} must be unset with {ENV_AREA}: V is measured there, not laid out", out warning);
                s.HasArea = true;
                s.AreaIsDisc = disc;
                s.AreaCentre = centre;
                s.AreaSize = size;
                s.AreaRadius = radius;
            }
            else
            {
                if (string.IsNullOrEmpty(viewText))
                    return Refuse($"{ENV_VIEW} is required with {ENV_PHANTOM} (V, the movers each phantom sees)", out warning);
                if (!TryParseView(viewText, kind, out int view, out error))
                    return Refuse(error, out warning);
                s.View = view;
            }
            if (!TryParseArrival(readValue(ENV_ARRIVAL), out float arrival, out error))
                return Refuse(error, out warning);
            s.ArrivalPerSecond = arrival;
            if (!TryParseUploadHz(readValue(ENV_UPLOAD_HZ), out int hz, out error))
                return Refuse(error, out warning);
            s.UploadHz = hz;
            if (!TryParsePayload(readValue(ENV_PAYLOAD), kind, out ServerMeshPhantomPayloadKind payload, out error))
                return Refuse(error, out warning);
            s.Payload = payload;
            string stepsText = readValue(ENV_STEPS);
            if (!TryParseSteps(stepsText, kind, phantoms, out int[] steps, out float hold, out error))
                return Refuse(error, out warning);
            if (steps != null && route == ServerMeshPhantomRoute.Sink)
                return Refuse($"{ENV_STEPS}={stepsText} needs the virtual mode: sink peers come and go with their own process", out warning);
            s.Steps = steps;
            s.StepHoldSeconds = hold;
            if (!TryParseCharacterShare(ENV_STAND, readValue(ENV_STAND), kind, out float stand, out error))
                return Refuse(error, out warning);
            s.StandShare = stand;
            if (!TryParseCharacterShare(ENV_FIGHT, readValue(ENV_FIGHT), kind, out float fight, out error))
                return Refuse(error, out warning);
            s.FightShare = fight;
            s.FightSet = !string.IsNullOrEmpty(readValue(ENV_FIGHT));
            string seedText = readValue(ENV_SEED);
            if (!string.IsNullOrEmpty(seedText))
            {
                if (!TryParseInt(seedText, out int seed))
                    return Refuse($"{ENV_SEED}={seedText} is not an integer", out warning);
                s.Seed = seed;
            }
            settings = s;
            return true;
        }

        private static bool TryParseCharacterShare(string key, string text, ServerMeshPhantomKind kind, out float share, out string error)
        {
            if (!TryParseShare(key, text, out share, out error))
                return false;
            if (!string.IsNullOrEmpty(text) && kind != ServerMeshPhantomKind.Character)
            {
                error = $"{key}={text} needs the character kind";
                share = 0f;
                return false;
            }
            return true;
        }

        private static bool Refuse(string reason, out string warning)
        {
            warning = Printable($"Phantoms: {reason}; no phantoms installed");
            return false;
        }

        private static bool TryParseInt(string text, out int value)
        {
            value = 0;
            return text != null && int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryParseCount(string text, out int value)
        {
            value = 0;
            if (text == null)
                return false;
            string trimmed = text.Trim();
            for (int i = 0; i < trimmed.Length; ++i)
            {
                if (trimmed[i] < '0' || trimmed[i] > '9')
                    return false;
            }
            return trimmed.Length > 0 && int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryParseFloat(string text, out float value)
        {
            value = 0f;
            return text != null && float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !float.IsNaN(value) && !float.IsInfinity(value);
        }

        /// <summary>Printable turns braces into parentheses: the kit's logger formats every message.</summary>
        public static string Printable(string text) => text == null ? null : text.Replace('{', '(').Replace('}', ')');
    }
}
