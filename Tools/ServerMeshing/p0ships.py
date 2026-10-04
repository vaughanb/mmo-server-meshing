"""Phase 0 step 7 instrument helpers: the twelve step 7 keys' meanings, parsers for the Ships, Feeder, Sync load and
Bot ships lines, mirrors of the ship grammars, the bot argument and the runner's scale. Pure: nothing here reads files
or the environment, and it imports nothing from this folder (p0tools merges STEP7_REGISTRY into its registry).

    p0ships.parse_ships_window(line)        # the "Ships window" line a map server logs under any ship key, else None
    p0ships.parse_ships_settings(line)      # the "Ships:" settings line a map server logs once per load cell
    p0ships.parse_feeder_window(line)       # the "Feeder window" line (MESH_PUPPET_FEEDER)
    p0ships.parse_sync_load_window(line)    # the "Sync load window" line (MESH_SYNC_LOAD)
    p0ships.parse_bot_ships(line)           # the "Bot ships" line a bot logs with -meshRigShips
    p0ships.ship_load_plan("C:50:ai")       # ((("C", 50, "ai"),), None)
    p0ships.rig_ship_bot_args(env)          # ["-meshRigShips", "header56:12345:0.03"] when MESH_RIG_SHIPS=1, else []
    p0ships.scaled_env(0.03)                # {"MESH_SHIP_SCALE": "0.03", "MESH_SENDER_R0": "60.0"}
    p0ships.framed_bytes(54, 241)           # 60: a record's packed id, int32 length and payload

Every parser works on a bare message or a whole log line; "end" is the line's stamp, or None when it has none (bot
lines carry none). The grammar mirrors decide what the C# settings accept and refuse; their error texts are their own.
Unit tests: python -m unittest discover -s Tools/ServerMeshing -p "test_*.py"
"""
import datetime
import math
import re

NUM = r"\d+(?:\.\d+)?"
STAMP = re.compile(r"\[(\d{4}-\d\d-\d\d) (\d\d:\d\d:\d\d)\]")   # rigtools.STAMP: kept here so nothing is imported
WIRES = ("legacy54", "header56", "compact28")
UPLOAD_BYTES = {"legacy54": 54, "header56": 56, "compact28": 56}   # compact records are never uploaded (decision 23)
AUTHORITIES = ("ai", "mirror", "parked")
FEEDER_KINDS = ("ship", "character")
DEFAULT_WIRE, DEFAULT_SEED, DEFAULT_SCALE = "legacy54", "12345", "1"
MIN_SCALE, MAX_SCALE = 0.001, 10.0
MAX_LOAD_SHIPS, MAX_DIRTY, MAX_RPC = 2000, 10000, 100000
MAX_FEEDER, MIN_FEEDER_HZ, MAX_FEEDER_HZ = 2000, 1, 60
BOT_ARGUMENT = "-meshRigShips"
VISIBLE_RANGE_M = 2000.0   # a ship's visible range at scale 1, and the R0 scaled_env scales by default
PACKED_UINT_LIMITS = (240, 2287, 67823, 16777215)   # the last value a packed uint of 1, 2, 3 and 4 bytes holds

# The twelve keys step 7 moves out of PENDING_KEYS or adds (decisions 3, 9, 12 and section Y; step 7b decisions 3, 4).
STEP7_REGISTRY = {
    "MESH_LOAD_SHIPS": "<cell>:<n>:<ai|mirror|parked>[,...]: load ships per cell, n 1 to 2000, one group per (cell, "
                       "authority); placed in MESH_LOAD_AREA within the cell when it is set",
    "MESH_LOAD_SHIP_PILOTS": "0 or 1: seat a server pilot character in every load ship (default 0)",
    "MESH_SHIP_WIRE": "legacy54, header56 or compact28: the ship record (compact28 sends 28 B to far pairs with "
                      "MESH_SENDER_COMPACT=far, header56 otherwise; default legacy54)",
    "MESH_SHIP_SCALE": "0.001 to 10: the factor on every ship distance, speed and size (default 1)",
    "MESH_SHIP_PROJECTILES": "instantiate or pooled: how server bolts are made and cast (default instantiate, as the "
                             "space game does)",
    "MESH_SHIP_MIRROR_VELOCITY": "real or zero: what a mirror ship's server record says about velocity (default real, "
                                 "the last upload's velocities, as the space game's mirror does since 9b1d69a1)",
    "MESH_SHIP_SELF_ACK": "0 (default: an unowned ship stays reliable after a server teleport until an owner acks, as "
                          "the space game) | 1 (the rig acks its own teleport after two logic ticks)",
    "MESH_SHIP_AI_PREFAB": "enemy (default: Ship_Enemy numbers) | wave (Ship_AI numbers)",
    "MESH_RIG_SHIPS": "1: bots get -meshRigShips <wire>:<seed>:<scale> and check ship records against their paths",
    "MESH_PUPPET_FEEDER": "<ship|character>:<N>@<Hz>: N in-process puppets fed snapshots at Hz (N 1 to 2000, Hz 1 to 60)",
    "MESH_SYNC_LOAD": "dirty:<D>,rpc:<R>: D load-ship sync fields written per tick and R RpcFired calls per second",
    "MESH_METER_RECORD_SIZES": "1: the Send and Sender windows add record payload sizes (needs MESH_METER_SENDS or "
                               "MESH_SENDER; default off)",
}

SHIPS_WINDOW = re.compile(
    rf"Ships window (?P<seconds>{NUM}) s: cell (?P<cell>\S+) ships (?P<ships>\d+) ai (?P<ai>\d+) "
    rf"mirror (?P<mirror>\d+) parked (?P<parked>\d+) phantom (?P<phantom>\d+) pilots (?P<pilots>\d+) "
    rf"seated (?P<seated>\d+) outside (?P<outside>\d+) notInSlot (?P<not_in_slot>\d+)(?: pending (?P<pending>\d+))? "
    rf"\| speed p50 (?P<speed_p50>{NUM}) max (?P<speed_max>{NUM}) "
    rf"patrol(?: n (?P<patrol_n>\d+))? p50 (?P<patrol_p50>{NUM}|none) m/s "
    rf"\| scans (?P<scans>\d+) casts (?P<casts>\d+) shots (?P<shots>\d+) bolts (?P<bolts>\d+) hits (?P<hits>\d+) "
    rf"respawns (?P<respawns>\d+) \| shield writes (?P<shield_writes>\d+) \| rpcSends (?P<rpc_sends>\d+) "
    rf"\| uploads (?P<uploads>\d+) stale (?P<stale>\d+) bad (?P<bad>\d+) "
    rf"\| ms/s pilot (?P<pilot_ms_per_s>{NUM}) engine (?P<engine_ms_per_s>{NUM}) combat (?P<combat_ms_per_s>{NUM}) "
    rf"bolts (?P<bolts_ms_per_s>{NUM}) mirror (?P<mirror_ms_per_s>{NUM}) "
    rf"(?:\| mirror age n (?P<mirror_age_n>\d+) p50 (?P<mirror_age_ms>{NUM}|none) ms "
    rf"frames (?P<mirror_age_frames>\d+(?:\.\d+)?|none) )?\| gc0 (?P<gc0>\d+)$")
SHIPS_WINDOW_FLOATS = {"seconds", "speed_p50", "speed_max", "patrol_p50", "pilot_ms_per_s", "engine_ms_per_s",
                       "combat_ms_per_s", "bolts_ms_per_s", "mirror_ms_per_s", "mirror_age_ms"}
# Step 7b's fields; a step 7 line has none of them and parses with each None.
SHIPS_WINDOW_OPTIONAL = {"pending", "patrol_n", "mirror_age_n", "mirror_age_ms", "mirror_age_frames"}

SHIPS_SETTINGS = re.compile(
    r"Ships: cell (?P<cell>\S+) ai (?P<ai>\d+) mirror (?P<mirror>\d+) parked (?P<parked>\d+) pilots (?P<pilots>\d+) "
    r"\| wire (?P<wire>legacy54|header56|compact28) scale (?P<scale>\S+) "
    r"projectiles (?P<projectiles>instantiate|pooled) mirrorVelocity (?P<mirror_velocity>real|zero)"
    r"(?: selfAck (?P<self_ack>[01]) aiPrefab (?P<ai_prefab>enemy|wave))? "
    r"\| area (?P<area>\S+) \| prefab hash (?P<prefab_hash>-?\d+)$")
SHIPS_SETTINGS_INTS = {"ai", "mirror", "parked", "pilots", "self_ack", "prefab_hash"}

FEEDER_WINDOW = re.compile(
    rf"Feeder window (?P<seconds>{NUM}) s: (?P<kind>ship|character) (?P<puppets>\d+) observed (?P<observed>\d+) "
    rf"\| snapshots (?P<snapshots>\d+) "
    rf"\| ms/s encode (?P<encode_ms_per_s>{NUM}) decode (?P<decode_ms_per_s>{NUM}) accept (?P<accept_ms_per_s>{NUM}) "
    rf"pose (?P<pose_ms_per_s>{NUM}) total (?P<total_ms_per_s>{NUM}) "
    rf"\| us/snapshot (?P<us_per_snapshot>{NUM}) \| gc0 (?P<gc0>\d+)$")
FEEDER_WINDOW_INTS = {"puppets", "observed", "snapshots", "gc0"}

SYNC_LOAD_WINDOW = re.compile(
    rf"Sync load window (?P<seconds>{NUM}) s: ships (?P<ships>\d+) "
    rf"\| writes (?P<writes>\d+) rpc (?P<rpc>\d+) rpcSends (?P<rpc_sends>\d+) "
    rf"\| ms/s write (?P<write_ms_per_s>{NUM}) rpc (?P<rpc_ms_per_s>{NUM}) "
    rf"\| alloc (?P<alloc_bytes_per_rpc>{NUM}) B/rpc gc0 (?P<gc0>\d+)$")
SYNC_LOAD_WINDOW_INTS = {"ships", "writes", "rpc", "rpc_sends", "gc0"}

BOT_SHIPS = re.compile(
    rf"Bot ships (?P<seconds>{NUM}) s: seen (?P<seen>\d+) records (?P<records>\d+) pathed (?P<pathed>\d+) "
    rf"parked (?P<parked>\d+) unpathed (?P<unpathed>\d+) "
    rf"\| path n (?P<path_n>\d+) p50 (?P<path_p50>{NUM}) p95 (?P<path_p95>{NUM}) p99 (?P<path_p99>{NUM}) "
    rf"max (?P<path_max>{NUM}) m lag p50 (?P<lag_p50_ms>-?\d+) ms "
    rf"\| parked max (?P<parked_max>{NUM}) m \| unknownPath (?P<unknown_path>\d+) badLength (?P<bad_length>\d+)$")
BOT_SHIPS_INTS = {"seen", "records", "pathed", "parked", "unpathed", "path_n", "lag_p50_ms", "unknown_path",
                  "bad_length"}

COUNT = re.compile(r"[0-9]+")
SIGNED = re.compile(r"[+-]?[0-9]+")
# What C#'s float.TryParse(NumberStyles.Float, invariant culture) takes, less NaN and the infinities.
FLOAT = re.compile(r"[+-]?(?:[0-9]+\.?[0-9]*|\.[0-9]+)(?:[eE][+-]?[0-9]+)?")
INT32 = (-2 ** 31, 2 ** 31 - 1)


def _end(line):
    m = STAMP.search(line)
    return datetime.datetime.fromisoformat(f"{m.group(1)} {m.group(2)}") if m else None


def _parse(pattern, line, convert):
    m = pattern.search(line.rstrip())
    if not m:
        return None
    fields = {name: convert(name, text) for name, text in m.groupdict().items()}
    fields["end"] = _end(line)
    return fields


def _ships_value(name, text):
    if text is None or text == "none":
        return None
    if name == "cell":
        return text
    if name in SHIPS_WINDOW_FLOATS or (name == "mirror_age_frames" and "." in text):
        return float(text)
    return int(text)


def parse_ships_window(line):
    """The Ships window line, else None: "end"; "seconds"; "cell"; the gauges "ships", "ai", "mirror", "parked",
    "phantom", "pilots", "seated", "outside", "not_in_slot", "pending" (ships with an unacknowledged teleport at the
    window's end); "speed_p50", "speed_max" (m/s); "patrol_n" (the patrol speeds sampled) and "patrol_p50" (m/s, None
    when n is 0); the counts "scans", "casts", "shots", "bolts", "hits", "respawns", "shield_writes", "rpc_sends",
    "uploads", "stale", "bad"; "pilot_ms_per_s", "engine_ms_per_s", "combat_ms_per_s", "bolts_ms_per_s",
    "mirror_ms_per_s"; the mirror feed age "mirror_age_n", "mirror_age_ms" (p50, ms) and "mirror_age_frames" (p50,
    int, float when printed with a fraction), the last two None when n is 0; "gc0". Counts are int, the rest float;
    nothing may follow gc0. A step 7 line (no pending, "patrol p50 X m/s", no mirror age) parses with the step 7b fields
    None; "none" goes with n 0 and only there, else the line is refused."""
    fields = _parse(SHIPS_WINDOW, line, _ships_value)
    if fields is None:
        return None
    patrol_n = fields["patrol_n"]
    if (fields["patrol_p50"] is None) != (patrol_n == 0):
        return None
    age_n = fields["mirror_age_n"]
    if age_n is not None and any((fields[k] is None) != (age_n == 0) for k in ("mirror_age_ms", "mirror_age_frames")):
        return None
    return fields


def parse_ships_settings(line):
    """The Ships settings line ("Ships: cell ..."), else None: "end"; "cell"; "ai", "mirror", "parked", "pilots";
    "wire"; "scale" (the text as printed); "projectiles"; "mirror_velocity"; "self_ack" (0 or 1) and "ai_prefab"
    ("enemy" or "wave"), both None on a step 7 line; "area" (text, "none" when unset); "prefab_hash" (int)."""
    return _parse(SHIPS_SETTINGS, line, lambda name, text: None if text is None else
                  int(text) if name in SHIPS_SETTINGS_INTS else text)


def parse_feeder_window(line):
    """The Feeder window line, else None: "end"; "seconds"; "kind" (ship or character); "puppets", "observed",
    "snapshots"; "encode_ms_per_s", "decode_ms_per_s", "accept_ms_per_s", "pose_ms_per_s", "total_ms_per_s";
    "us_per_snapshot"; "gc0"."""
    return _parse(FEEDER_WINDOW, line, lambda name, text: text if name == "kind" else
                  int(text) if name in FEEDER_WINDOW_INTS else float(text))


def parse_sync_load_window(line):
    """The Sync load window line, else None: "end"; "seconds"; "ships"; "writes", "rpc", "rpc_sends";
    "write_ms_per_s", "rpc_ms_per_s"; "alloc_bytes_per_rpc" (a window mean, reported only); "gc0"."""
    return _parse(SYNC_LOAD_WINDOW, line, lambda name, text: int(text) if name in SYNC_LOAD_WINDOW_INTS else float(text))


def parse_bot_ships(line):
    """The Bot ships line, else None: "end" (None: bot lines carry no stamp); "seconds"; "seen", "records", "pathed",
    "parked", "unpathed"; the path-curve distances "path_n", "path_p50", "path_p95", "path_p99", "path_max" (m);
    "lag_p50_ms" (int, negative when records lead their path); "parked_max" (m); "unknown_path", "bad_length"."""
    return _parse(BOT_SHIPS, line, lambda name, text: int(text) if name in BOT_SHIPS_INTS else float(text))


def _keyword(text, choices):
    folded = text.strip().lower()
    return folded if folded in choices else None


def ship_load_plan(text):
    """MESH_LOAD_SHIPS read as ServerMeshShipLoadPlan.TryParse reads it: (groups, None), groups a tuple of (cell, n,
    authority) in the order written, or (None, error). Parts are trimmed, keywords ignore case, n is 1 to 2000, and a
    (cell, authority) may appear once. Unset or blank is (None, None)."""
    if text is None or not text.strip():
        return None, None
    groups, seen = [], set()
    for part in text.split(","):
        fields = [f.strip() for f in part.split(":")]
        if not part.strip():
            return None, f"'{text.strip()}' has an empty part"
        if len(fields) != 3 or not fields[0]:
            return None, f"part '{part.strip()}' is not <cell>:<n>:<ai|mirror|parked>"
        if not COUNT.fullmatch(fields[1]) or not 1 <= int(fields[1]) <= MAX_LOAD_SHIPS:
            return None, f"part '{part.strip()}': count '{fields[1]}' is not an integer from 1 to {MAX_LOAD_SHIPS}"
        authority = _keyword(fields[2], AUTHORITIES)
        if authority is None:
            return None, f"part '{part.strip()}': authority '{fields[2]}' is not ai, mirror or parked"
        if (fields[0], authority) in seen:
            return None, f"part '{part.strip()}': cell {fields[0]} has a second {authority} group"
        seen.add((fields[0], authority))
        groups.append((fields[0], int(fields[1]), authority))
    return tuple(groups), None


def ship_wire(text):
    """MESH_SHIP_WIRE read as ServerMeshShipSettings.TryParseWire reads it: (wire, None) or (None, error); unset or
    blank is ("legacy54", None)."""
    if text is None or not text.strip():
        return DEFAULT_WIRE, None
    wire = _keyword(text, WIRES)
    return (wire, None) if wire else (None, f"'{text.strip()}' is not legacy54, header56 or compact28")


def ship_scale(text):
    """MESH_SHIP_SCALE read as ServerMeshShipScale.TryParse reads it: (factor, None) or (None, error), a finite number
    from 0.001 to 10; unset or blank is (1.0, None)."""
    if text is None or not text.strip():
        return 1.0, None
    value = text.strip()
    if not FLOAT.fullmatch(value) or not MIN_SCALE <= float(value) <= MAX_SCALE:
        return None, f"'{value}' is not a number from 0.001 to 10"
    return float(value), None


def puppet_feeder(text):
    """MESH_PUPPET_FEEDER read as ServerMeshPuppetFeeder.TryParseSetting reads it: (kind, count, hz, None) or (None,
    None, None, error) for <ship|character>:<N>@<Hz>, N 1 to 2000 and Hz 1 to 60; unset or blank is all None."""
    refused = (None, None, None)
    if text is None or not text.strip():
        return refused + (None,)
    m = re.fullmatch(r"\s*([^:@]*):([^@]*)@(.*)", text)
    if not m:
        return refused + (f"'{text.strip()}' is not <ship|character>:<N>@<Hz>",)
    kind = _keyword(m.group(1), FEEDER_KINDS)
    count, hz = m.group(2).strip(), m.group(3).strip()
    if kind is None:
        return refused + (f"kind '{m.group(1).strip()}' is not ship or character",)
    if not COUNT.fullmatch(count) or not 1 <= int(count) <= MAX_FEEDER:
        return refused + (f"count '{count}' is not an integer from 1 to {MAX_FEEDER}",)
    if not COUNT.fullmatch(hz) or not MIN_FEEDER_HZ <= int(hz) <= MAX_FEEDER_HZ:
        return refused + (f"rate '{hz}' is not an integer from {MIN_FEEDER_HZ} to {MAX_FEEDER_HZ} Hz",)
    return kind, int(count), int(hz), None


def sync_load(text):
    """MESH_SYNC_LOAD read as ServerMeshSyncLoad.TryParseSetting reads it: (dirty, rpc, None) or (None, None, error)
    for dirty:<D>,rpc:<R> in either order, each exactly once, D 0 to 10000 and R 0 to 100000; unset or blank is (None,
    None, None)."""
    if text is None or not text.strip():
        return None, None, None
    limits = {"dirty": MAX_DIRTY, "rpc": MAX_RPC}
    found = {}
    for part in text.split(","):
        fields = [f.strip() for f in part.split(":")]
        name = fields[0].lower() if len(fields) == 2 else None
        if name not in limits:
            return None, None, f"part '{part.strip()}' is not dirty:<D> or rpc:<R>"
        if name in found:
            return None, None, f"'{name}' is given twice"
        if not COUNT.fullmatch(fields[1]) or int(fields[1]) > limits[name]:
            return None, None, f"{name} '{fields[1]}' is not an integer from 0 to {limits[name]}"
        found[name] = int(fields[1])
    missing = [name for name in limits if name not in found]
    if missing:
        return None, None, f"'{text.strip()}' has no {missing[0]} part"
    return found["dirty"], found["rpc"], None


def bot_argument(text):
    """The -meshRigShips value read as ServerMeshShipSettings.TryParseBotArgument reads it: (wire, seed, scale, None)
    or (None, None, None, error) for <wire>:<seed>:<scale>."""
    refused = (None, None, None)
    parts = [] if text is None else [p.strip() for p in text.split(":")]
    if len(parts) != 3:
        return refused + (f"'{text}' is not <wire>:<seed>:<scale>",)
    wire, error = ship_wire(parts[0]) if parts[0] else (None, "the wire is empty")
    if error:
        return refused + (error,)
    if not SIGNED.fullmatch(parts[1]) or not INT32[0] <= int(parts[1]) <= INT32[1]:
        return refused + (f"seed '{parts[1]}' is not a 32-bit integer",)
    scale, error = ship_scale(parts[2]) if parts[2] else (None, "the scale is empty")
    if error:
        return refused + (error,)
    return wire, int(parts[1]), scale, None


def rig_ship_bot_args(env):
    """The bot arguments a run's environment asks for: ["-meshRigShips", "<wire>:<seed>:<scale>"] when MESH_RIG_SHIPS
    is 1, the wire's canonical name and the MESH_LOAD_SEED and MESH_SHIP_SCALE texts verbatim (trimmed; defaults
    legacy54, 12345, 1), so server and bot parse the same float; [] otherwise. Raises ValueError for a value the C#
    side would refuse."""
    if (env.get("MESH_RIG_SHIPS") or "").strip() != "1":
        return []
    wire, error = ship_wire(env.get("MESH_SHIP_WIRE"))
    seed = (env.get("MESH_LOAD_SEED") or "").strip() or DEFAULT_SEED
    scale = (env.get("MESH_SHIP_SCALE") or "").strip() or DEFAULT_SCALE
    value = f"{wire}:{seed}:{scale}"
    if error is None:
        error = bot_argument(value)[3]
    if error:
        raise ValueError(f"{BOT_ARGUMENT} {value}: {error}")
    return [BOT_ARGUMENT, value]


def scaled_env(scale, r0_metres=VISIBLE_RANGE_M):
    """The keys a run at a ship scale sets: MESH_SHIP_SCALE (the factor as %g text) and MESH_SENDER_R0 (r0_metres x
    scale, rounded to millimetres), since the sender cannot read a rig scale (decision 6). Raises ValueError for a
    scale outside 0.001 to 10 or a non-positive R0."""
    if not (isinstance(scale, (int, float)) and math.isfinite(scale) and MIN_SCALE <= scale <= MAX_SCALE):
        raise ValueError(f"scale {scale} is not a number from 0.001 to 10")
    if not r0_metres > 0:
        raise ValueError(f"R0 {r0_metres} m is not above 0")
    return {"MESH_SHIP_SCALE": f"{scale:g}", "MESH_SENDER_R0": str(round(r0_metres * scale, 3))}


def packed_uint_bytes(value):
    """The bytes LiteNetLib's PutPackedUInt spends on value: 1 to 240, 2 to 2287, 3 to 67823, 4 to 16777215, else 5."""
    if value < 0:
        raise ValueError(f"packed uint {value} is negative")
    for size, limit in enumerate(PACKED_UINT_LIMITS, start=1):
        if value <= limit:
            return size
    return 5


def framed_bytes(payload, object_id):
    """A record's bytes on the wire: [packed uint id][int32 length][payload] (decision 1)."""
    if payload < 0:
        raise ValueError(f"payload {payload} B is negative")
    return packed_uint_bytes(object_id) + 4 + payload
