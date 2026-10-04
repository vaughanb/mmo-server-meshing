"""Phase 0 step 8a instrument helpers (stage 0b): the three step 8a keys' meanings, parsers for the Prewarm and
Monsters lines and for the phantom lines stepped runs and the sink arms read, and mirrors of the MESH_PREWARM,
MESH_MONSTER_PLACE and MESH_SHIP_PARK_SLEEP grammars. Pure: nothing here reads files or the environment; p0tools
merges STEP8_REGISTRY into its registry (step8a_design.md decision 15).

    p0stage0b.parse_prewarm_setting(line)   # "Prewarm: kind K n N at T s perFrame F on cell C", once per cell
    p0stage0b.parse_prewarm_burst(line)     # "Prewarm burst: cell C kind K n N ...", once per burst
    p0stage0b.parse_monsters_setting(line)  # "Monsters: placed N in <discs> on cell C", once per cell
    p0stage0b.parse_monsters_window(line)   # "Monsters window 5.00 s: cell C placed N alive A ...", per work window
    p0stage0b.parse_phantoms_port(line)     # the cell's "Phantoms: cell C port N mode M kind K P p V v groups g" line
    p0stage0b.parse_phantom_step(line)      # the stamped "Phantom step i/n: P a -> b" line (MESH_PHANTOM_STEPS)
    p0stage0b.prewarm_setting("kinematic:650@40/50")         # (("kinematic", 650, 40.0, 50), None)
    p0stage0b.monster_place_setting("100@0,0,50:passive")    # (((100, 0.0, 0.0, 50.0, True),), None)
    p0stage0b.park_sleep_setting("1")                         # (1, None)

Every parser works on a bare message or a whole log line; "end" is the line's stamp (rigtools.stamp_datetime), or None
when it has none. The grammar mirrors decide what the C# settings accept and refuse; their error texts are their own.
Unit tests: python -m unittest discover -s Tools/ServerMeshing -p "test_*.py"
"""
import math
import re

import rigtools

# The three keys step 8a moves out of PENDING_KEYS (MESH_PREWARM, MESH_SHIP_PARK_SLEEP) or adds (MESH_MONSTER_PLACE).
STEP8_REGISTRY = {
    "MESH_PREWARM": "<steered|kinematic|ship|range|group>:<N>@<t>[/<perFrame>]: at t s after the server can "
                    "instantiate, N puppets (steered, kinematic), stand-in ships or owned characters moved into view "
                    "(group) arrive in a burst, perFrame per frame (default all in one frame), or the visible range "
                    "goes to N metres (range); a Prewarm burst line measures it",
    "MESH_SHIP_PARK_SLEEP": "0 or 1: 1 pins every parked ship to its parked pose each FixedUpdate so it never sleeps; "
                            "either value adds 'parked sleeping S of P' to the Ships window and parkSleep to its "
                            "settings line (unset: neither)",
    "MESH_MONSTER_PLACE": "<n>@<x>,<z>,<r>[:passive][;...]: n monsters placed in each disc on the cell that owns its "
                          "centre, healed below half health and re-placed 10 s after a death (passive: they never "
                          "target anyone); total at most 2000, with MESH_NO_MONSTER_HANDOFF=1",
}

PREWARM_KINDS = ("steered", "kinematic", "ship", "range", "group")
SPAWN_KINDS = ("steered", "kinematic", "ship", "group")   # N is a count; range's N is metres
MAX_PREWARM = 10000           # the most puppets, ships or characters, and the widest range in metres
MAX_PLACED = 2000             # the most monsters MESH_MONSTER_PLACE places over all its discs
GROUP_LEAD_S = 10             # a group's characters spawn this long before its burst, so its t is at least this
PREWARM_SHAPE = "<kind>:<N>@<t>[/<perFrame>]"
PLACE_SHAPE = "<n>@<x>,<z>,<r>[:passive]"

NUM = r"\d+(?:\.\d+)?"
COUNT = re.compile(r"[0-9]+")
# What C#'s float.TryParse(NumberStyles.Float, invariant culture) takes, less NaN and the infinities.
FLOAT = re.compile(r"[+-]?(?:[0-9]+\.?[0-9]*|\.[0-9]+)(?:[eE][+-]?[0-9]+)?")

PREWARM_SETTING = re.compile(
    rf"Prewarm: kind (?P<kind>{'|'.join(PREWARM_KINDS)}) n (?P<n>{NUM}) at (?P<at_s>{NUM}) s "
    rf"perFrame (?P<per_frame>\d+) on cell (?P<cell>\S+)$")
PREWARM_BURST = re.compile(
    rf"Prewarm burst: cell (?P<cell>\S+) kind (?P<kind>{'|'.join(PREWARM_KINDS)}) n (?P<n>{NUM}) "
    rf"perFrame (?P<per_frame>\d+) frames (?P<frames>\d+) "
    rf"\| spawn ms (?P<spawn_ms>{NUM}|n/a) per puppet (?P<per_puppet_ms>{NUM}|n/a) "
    rf"\| after frames (?P<after_frames>\d+) over budget (?P<over_budget>\d+) budget (?P<budget_ms>{NUM}|uncapped) ms "
    rf"worst (?P<worst_ms>{NUM}) ms \| gc0 (?P<gc0>\d+)$")
PREWARM_BURST_INTS = {"per_frame", "frames", "after_frames", "over_budget", "gc0"}
MONSTERS_SETTING = re.compile(r"Monsters: placed (?P<placed>\d+) in (?P<discs>.+?) on cell (?P<cell>\S+)$")
MONSTERS_WINDOW = re.compile(
    rf"Monsters window (?P<seconds>{NUM}) s: cell (?P<cell>\S+) placed (?P<placed>\d+) alive (?P<alive>\d+) "
    rf"awake (?P<awake>\d+) healed (?P<healed>\d+) respawned (?P<respawned>\d+) \| gc0 (?P<gc0>\d+)$")
PHANTOMS_PORT = re.compile(
    r"Phantoms: cell (?P<cell>\S+) port (?P<port>\d+) mode (?P<mode>virtual|sink) kind (?P<kind>character|eye|ship) "
    r"P (?P<p>\d+) V (?P<v>\d+) groups (?P<groups>\d+)$")
PHANTOM_STEP = re.compile(r"Phantom step (?P<i>\d+)/(?P<n>\d+): P (?P<a>\d+) -> (?P<b>\d+)$")


def _match(pattern, line):
    return pattern.search(line.rstrip())


def _number(text):
    """A printed count or measure: int when it has no fraction, else float."""
    return float(text) if "." in text else int(text)


def _optional(text, absent):
    return None if text == absent else float(text)


def parse_prewarm_setting(line):
    """The Prewarm settings line ("Prewarm: kind K n N at T s perFrame F on cell C"), else None: "end"; "kind";
    "n" (int, a float when printed with a fraction: range's metres); "at_s" (float); "per_frame" (int, 0 = all in one
    frame); "cell"."""
    m = _match(PREWARM_SETTING, line)
    if not m:
        return None
    return {"end": rigtools.stamp_datetime(line), "kind": m["kind"], "n": _number(m["n"]), "at_s": float(m["at_s"]),
            "per_frame": int(m["per_frame"]), "cell": m["cell"]}


def parse_prewarm_burst(line):
    """The Prewarm burst line, else None: "end"; "cell"; "kind"; "n" (as parse_prewarm_setting); "per_frame" (0 = all
    at once); "frames" (the burst's frames); "spawn_ms" and "per_puppet_ms" (float, both None for "n/a": range and
    group spawn nothing); "after_frames"; "over_budget" (frames whose period exceeded 1.05 x the budget); "budget_ms"
    (float, None for "uncapped"); "worst_ms" (the worst frame period); "gc0". Refused when only one of the spawn
    figures is n/a or text follows gc0."""
    m = _match(PREWARM_BURST, line)
    if not m:
        return None
    if (m["spawn_ms"] == "n/a") != (m["per_puppet_ms"] == "n/a"):
        return None
    fields = {name: int(m[name]) for name in PREWARM_BURST_INTS}
    fields.update({"end": rigtools.stamp_datetime(line), "cell": m["cell"], "kind": m["kind"], "n": _number(m["n"]),
                   "spawn_ms": _optional(m["spawn_ms"], "n/a"), "per_puppet_ms": _optional(m["per_puppet_ms"], "n/a"),
                   "budget_ms": _optional(m["budget_ms"], "uncapped"), "worst_ms": float(m["worst_ms"])})
    return fields


def parse_monsters_setting(line):
    """The Monsters settings line ("Monsters: placed N in <discs> on cell C"), else None: "end"; "placed" (int);
    "discs" (the text between "in" and "on cell", as printed); "cell"."""
    m = _match(MONSTERS_SETTING, line)
    if not m:
        return None
    return {"end": rigtools.stamp_datetime(line), "placed": int(m["placed"]), "discs": m["discs"], "cell": m["cell"]}


def parse_monsters_window(line):
    """The Monsters window line, else None: "end"; "seconds" (float); "cell"; "placed", "alive" (non-ghosts),
    "awake" (subscribers above 0), "healed", "respawned", "gc0" (int)."""
    m = _match(MONSTERS_WINDOW, line)
    if not m:
        return None
    fields = {name: int(m[name]) for name in ("placed", "alive", "awake", "healed", "respawned", "gc0")}
    fields.update({"end": rigtools.stamp_datetime(line), "seconds": float(m["seconds"]), "cell": m["cell"]})
    return fields


def parse_phantoms_port(line):
    """The line a phantom cell logs once its phantoms are set up ("Phantoms: cell C port N mode M kind K P p V v groups
    g", ServerMeshPhantomClients), else None: "end"; "cell"; "port" (the cell's network port, which a sink connects
    to); "mode" (virtual or sink); "kind" (character, eye or ship); "p", "v", "groups" (int). The "Phantoms:
    MESH_PHANTOM=... installed" settings line is not this line."""
    m = _match(PHANTOMS_PORT, line)
    if not m:
        return None
    return {"end": rigtools.stamp_datetime(line), "cell": m["cell"], "port": int(m["port"]), "mode": m["mode"],
            "kind": m["kind"], "p": int(m["p"]), "v": int(m["v"]), "groups": int(m["groups"])}


def parse_phantom_step(line):
    """The Info line a cell logs when MESH_PHANTOM_STEPS moves to a step ("Phantom step i/n: P a -> b"), else None:
    "end" (the stamp: the step's start, step8a_design.md decision 4); "i", "n" (1-based step and step count); "a" (the
    target before) and "b" (the step's phantom count); "p" = b, the P a stepped row is keyed by (point.step). Refused
    when i is 0 or above n."""
    m = _match(PHANTOM_STEP, line)
    if not m:
        return None
    i, n = int(m["i"]), int(m["n"])
    if not 1 <= i <= n:
        return None
    return {"end": rigtools.stamp_datetime(line), "i": i, "n": n, "p": int(m["b"]), "a": int(m["a"]), "b": int(m["b"])}


def _float(text):
    """A finite float as C#'s invariant float.TryParse reads it, else None."""
    if not FLOAT.fullmatch(text):
        return None
    value = float(text)
    return value if math.isfinite(value) else None


def prewarm_setting(text):
    """MESH_PREWARM read as ServerMeshPrewarm's setting reads it: ((kind, n, at_s, per_frame), None) or (None, error)
    for <kind>:<N>@<t>[/<perFrame>]. Kinds (case ignored) steered, kinematic, ship, range, group; N an integer from 1
    to 10000 for the spawn kinds, metres above 0 and at most 10000 for range (a float); t seconds at least 0 (float),
    for group at least GROUP_LEAD_S (its characters spawn that long before the burst); perFrame an integer from 1 to N, refused for range; absent it is 0 (all in one frame). Parts are trimmed. Unset or
    blank is (None, None)."""
    if text is None or not text.strip():
        return None, None
    m = re.fullmatch(r"([^:@/]*):([^@/]*)@([^/]*)(?:/(.*))?", text.strip())
    if not m:
        return None, f"'{text.strip()}' is not {PREWARM_SHAPE}"
    kind = m.group(1).strip().lower()
    n_text, t_text = m.group(2).strip(), m.group(3).strip()
    if kind not in PREWARM_KINDS:
        return None, f"kind '{m.group(1).strip()}' is not steered, kinematic, ship, range or group"
    if kind == "range":
        n = _float(n_text)
        if n is None or not 0 < n <= MAX_PREWARM:
            return None, f"range '{n_text}' is not a number of metres above 0 and at most {MAX_PREWARM}"
    else:
        if not COUNT.fullmatch(n_text) or not 1 <= int(n_text) <= MAX_PREWARM:
            return None, f"count '{n_text}' is not an integer from 1 to {MAX_PREWARM}"
        n = int(n_text)
    at_s = _float(t_text)
    if at_s is None or at_s < 0:
        return None, f"time '{t_text}' is not a number of seconds of at least 0"
    if kind == "group" and at_s < GROUP_LEAD_S:
        return None, (f"time '{t_text}' is under the group's {GROUP_LEAD_S} s lead (its characters spawn {GROUP_LEAD_S} s "
                      "before the burst)")
    if m.group(4) is None:
        return (kind, n, at_s, 0), None
    per_text = m.group(4).strip()
    if kind == "range":
        return None, f"perFrame '{per_text}' is given for range, which changes the range at once"
    if not COUNT.fullmatch(per_text) or not 1 <= int(per_text) <= n:
        return None, f"perFrame '{per_text}' is not an integer from 1 to the count {n}"
    return (kind, n, at_s, int(per_text)), None


def monster_place_setting(text):
    """MESH_MONSTER_PLACE read as ServerMeshMonsterPlacer's setting reads it: (discs, None), discs a tuple of (n, x, z,
    r, passive) in the order written, or (None, error) naming the failing part. Parts are separated by ';' and trimmed;
    n is an integer of at least 1, x and z finite numbers, r a number above 0, ':passive' (case ignored) optional; the
    total over all parts is at most 2000. Unset or blank is (None, None)."""
    if text is None or not text.strip():
        return None, None
    discs, total = [], 0
    for part in text.split(";"):
        where = part.strip()
        if not where:
            return None, f"'{text.strip()}' has an empty part"
        body, colon, flag = where.partition(":")
        if colon and flag.strip().lower() != "passive":
            return None, f"part '{where}': '{flag.strip()}' is not passive"
        count, at, geometry = body.partition("@")
        numbers = [f.strip() for f in geometry.split(",")]
        if not at or len(numbers) != 3:
            return None, f"part '{where}' is not {PLACE_SHAPE}"
        count = count.strip()
        if not COUNT.fullmatch(count) or int(count) < 1:
            return None, f"part '{where}': count '{count}' is not an integer of at least 1"
        x, z, r = (_float(f) for f in numbers)
        if x is None or z is None:
            return None, f"part '{where}': centre '{numbers[0]},{numbers[1]}' is not two numbers"
        if r is None or r <= 0:
            return None, f"part '{where}': radius '{numbers[2]}' is not a number above 0"
        total += int(count)
        if total > MAX_PLACED:
            return None, f"part '{where}': the total {total} is above {MAX_PLACED}"
        discs.append((int(count), x, z, r, bool(colon)))
    return tuple(discs), None


def park_sleep_setting(text):
    """MESH_SHIP_PARK_SLEEP read as ServerMeshShipSettings reads it: (0 or 1, None) or (None, error); unset or blank is
    (None, None), today's behaviour with no parked sleeping group or parkSleep field."""
    if text is None or not text.strip():
        return None, None
    value = text.strip()
    if value not in ("0", "1"):
        return None, f"'{value}' is not 0 or 1"
    return int(value), None
