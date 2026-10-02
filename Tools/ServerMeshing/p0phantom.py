"""Phase 0 step 5 instrument helpers: parsers for the Send, Phantom, Sink and Bot net window lines, and mirrors of the
MESH_PHANTOM, MESH_PHANTOM_AREA and MESH_METER_SENDS grammars. Pure: nothing here reads files or the environment.
Split out of p0tools (the ~500-line rule), whose registry lists the step 5 keys.

    p0phantom.parse_send_window(line)     # the "Send window" line a cell logs with MESH_METER_SENDS on, else None
    p0phantom.send_window_problems(f)     # where a Send window's totals or group order break the contract
    p0phantom.parse_phantom_window(line)  # the "Phantom window" line the phantom binder logs every 5 s
    p0phantom.parse_sink_window(line)     # the "Sink window" line the sink process logs every 5 s
    p0phantom.parse_bot_net_window(line)  # the "Bot net window" line a bot logs with -meshBotNetSeconds
    p0phantom.phantom_setting("virtual:200:character:C")   # ("virtual", 200, "character", ("C",), None)
    p0phantom.phantom_area_setting("disc:0,0,50")          # ("disc", (0.0, 0.0, 50.0), None)
    p0phantom.meter_sends_setting("count")                 # (True, False, None)

Every parser works on a bare message or a whole log line; "end" is the line's stamp, or None when it has none. The
grammar mirrors decide what the C# settings accept and refuse; their error texts are their own.
Unit tests: python -m unittest discover -s Tools/ServerMeshing -p "test_*.py"
"""
import datetime
import re

import rigtools

NUM = r"\d+(?:\.\d+)?"
MAX_TYPE = 65535        # a packed ushort message type ends here
CHANNELS = 16           # the channels a Send window lists by index; higher ones count in "other"
MAX_TYPE_GROUPS = 6     # the per-type groups a Send or Sink window lists before "other"
DELIVERIES = ("ru", "seq", "ro", "rs", "u", "other")   # the delivery groups, in LiteNetLib's enum order, then other

_PAIRS = " ".join(rf"{name} (?P<d_{name}_calls>\d+) (?P<d_{name}_bytes>\d+)" for name in DELIVERIES)
_PAIR_GROUPS = {f"d_{name}_{part}" for name in DELIVERIES for part in ("calls", "bytes")}
_SEND_TYPE = re.compile(rf"t(?P<type>\d+) calls (?P<calls>\d+) bytes (?P<bytes>\d+) (?P<calls_per_s>{NUM})/s "
                        rf"(?P<kb_per_s>{NUM}) kB/s (?P<us>{NUM}) us")
SEND_WINDOW = re.compile(
    rf"Send window (?P<seconds>{NUM}) s: calls (?P<calls>\d+) (?P<calls_per_s>{NUM})/s bytes (?P<bytes>\d+) "
    rf"(?P<kb_per_s>{NUM}) kB/s "
    rf"(?P<types>(?:\| t\d+ calls \d+ bytes \d+ {NUM}/s {NUM} kB/s {NUM} us )*)"
    rf"\| other calls (?P<other_calls>\d+) bytes (?P<other_bytes>\d+) (?P<other_calls_per_s>{NUM})/s "
    rf"(?P<other_kb_per_s>{NUM}) kB/s (?P<other_us>{NUM}) us "
    rf"\| refused (?P<refused>\d+) tooBig (?P<too_big>\d+) bytes (?P<too_big_bytes>\d+) "
    rf"\| delivery {_PAIRS} "
    rf"\| channels (?P<channels>(?:\d+:\d+ )*)other:(?P<channels_other>\d+) "
    rf"\| wire (?P<wire_bytes>\d+) B (?P<wire_packets>\d+) pkt \| offThread (?P<off_thread>\d+)$")
SEND_WINDOW_INTS = {"calls", "bytes", "other_calls", "other_bytes", "refused", "too_big", "too_big_bytes",
                    "channels_other", "wire_bytes", "wire_packets", "off_thread"}

PHANTOM_WINDOW = re.compile(
    rf"Phantom window (?P<seconds>{NUM}) s: bound virtual (?P<bound_virtual>\d+) sink (?P<bound_sink>\d+) "
    rf"ready (?P<ready>\d+) filler (?P<filler>\d+) eyeSeen (?P<eye_seen>\d+)(?: fighting (?P<fighting>\d+))? "
    rf"\| step (?P<step>\d+)/(?P<steps>\d+) target (?P<target>\d+) "
    rf"\| vmove n (?P<vmove_n>\d+) mean (?P<vmove_mean>{NUM}) p95 (?P<vmove_p95>\d+) min (?P<vmove_min>\d+) "
    rf"\| uploads (?P<uploads_per_s>{NUM})/s "
    rf"\| posErr n (?P<pos_err_n>\d+) p50 (?P<pos_err_p50>{NUM}) p95 (?P<pos_err_p95>{NUM}) "
    rf"max (?P<pos_err_max>{NUM}) m "
    rf"\| down n (?P<down_n>\d+) mean (?P<down_mean>{NUM}) p95 (?P<down_p95>{NUM}) max (?P<down_max>{NUM}) kB/s "
    rf"\| swallowed {_PAIRS} "
    rf"\| frames (?P<frames>\d+) rig feeder (?P<feeder_ms_per_s>{NUM}) binder (?P<binder_ms_per_s>{NUM}) "
    rf"sampler (?P<sampler_ms_per_s>{NUM}) ms/s (?P<rig_ms_per_frame>{NUM}) ms/frame$")
PHANTOM_WINDOW_INTS = {"bound_virtual", "bound_sink", "ready", "filler", "eye_seen", "step", "steps", "target",
                       "vmove_n", "vmove_p95", "vmove_min", "pos_err_n", "down_n", "frames"}

_SINK_TYPE = re.compile(r"t(?P<type>\d+) calls (?P<calls>\d+) bytes (?P<bytes>\d+)")
SINK_WINDOW = re.compile(
    rf"Sink window (?P<seconds>{NUM}) s: peers (?P<peers>\d+) connected (?P<connected>\d+) bound (?P<bound>\d+) "
    rf"\| in (?P<in_bytes>\d+) B (?P<in_packets>\d+) pkt (?P<in_kb_per_s>{NUM}) kB/s "
    rf"\| down n (?P<down_n>\d+) mean (?P<down_mean>{NUM}) p95 (?P<down_p95>{NUM}) max (?P<down_max>{NUM}) kB/s "
    rf"(?P<types>(?:\| t\d+ calls \d+ bytes \d+ )*)"
    rf"\| other calls (?P<other_calls>\d+) bytes (?P<other_bytes>\d+) "
    rf"\| out (?P<out_uploads>\d+) uploads (?P<uploads_per_s>{NUM})/s (?P<out_kb_per_s>{NUM}) kB/s "
    rf"\| disconnects (?P<disconnects>\d+) \| workers (?P<workers>\d+) maxPassMs (?P<max_pass_ms>{NUM})$")
SINK_WINDOW_INTS = {"peers", "connected", "bound", "in_bytes", "in_packets", "down_n", "other_calls", "other_bytes",
                    "out_uploads", "disconnects", "workers"}

BOT_NET_WINDOW = re.compile(
    rf"Bot net window (?P<seconds>{NUM}) s: in (?P<in_kb_per_s>{NUM}) kB/s (?P<in_packets_per_s>{NUM}) pkt/s "
    rf"out (?P<out_kb_per_s>{NUM}) kB/s (?P<out_packets_per_s>{NUM}) pkt/s \| hops (?P<hops>\d+) "
    rf"\| rtt (?P<rtt_ms>\d+) ms \| players seen (?P<players_seen>\d+) "
    rf"\| at (?P<at>\d{{4}}-\d\d-\d\dT\d\d:\d\d:\d\dZ)$")
BOT_NET_WINDOW_INTS = {"hops", "rtt_ms", "players_seen"}

PHANTOM_MODES = ("virtual", "sink")
PHANTOM_KINDS = ("character", "eye")
PHANTOM_SHAPE = "<virtual|sink>:<P>:<character|eye>:<cell[,cell..]|all>"
AREA_SHAPES = "<cx>,<cz>,<w>,<h> or disc:<cx>,<cz>,<r>"
METER_SENDS_VALUES = ("off", "count", "time")
COUNT = re.compile(r"[0-9]+")
# What C#'s float.TryParse(NumberStyles.Float, invariant culture) takes, less NaN and the infinities.
FLOAT = re.compile(r"[+-]?(?:[0-9]+\.?[0-9]*|\.[0-9]+)(?:[eE][+-]?[0-9]+)?")


def _numbers(match, ints, skip=()):
    return {name: int(text) if name in ints else float(text) for name, text in match.groupdict().items()
            if name not in skip and text is not None}


def _pairs(match):
    return {name: (int(match[f"d_{name}_calls"]), int(match[f"d_{name}_bytes"])) for name in DELIVERIES}


def _type_groups(pattern, text, fields):
    """The per-type groups of a window line in line order, else None: at most six, each type distinct and at most
    65535."""
    groups = []
    for chunk in text.split("|")[1:]:
        m = pattern.fullmatch(chunk.strip())
        group = {name: int(value) if name in ("type", "calls", "bytes") else float(value)
                 for name, value in m.groupdict().items() if name in fields}
        if group["type"] > MAX_TYPE or any(group["type"] == g["type"] for g in groups):
            return None
        groups.append(group)
    return groups if len(groups) <= MAX_TYPE_GROUPS else None


def _channels(text):
    """" 0:250 2:1000 " as {0: 250, 2: 1000}, else None: indices strictly increasing and below 16, calls at least 1."""
    channels, last = {}, -1
    for pair in text.split():
        index, calls = (int(part) for part in pair.split(":"))
        if index <= last or index >= CHANNELS or calls < 1:
            return None
        channels[index] = calls
        last = index
    return channels


def parse_send_window(line):
    """The Send window line (MESH_METER_SENDS count or time), else None: "end"; "seconds", the totals "calls" and
    "bytes" (int) and their rates "calls_per_s" and "kb_per_s"; "types", up to six groups in line order, each {"type",
    "calls", "bytes" (int), "calls_per_s", "kb_per_s", "us" (float)}, and the rest in "other" (the same keys, no type);
    "refused", "too_big", "too_big_bytes"; "delivery", {ru, seq, ro, rs, u, other: (calls, bytes)}; "channels",
    {index: calls} for the non-empty ones, and "channels_other"; "wire_bytes" and "wire_packets" (the innermost
    LiteNetLib server's window deltas); "off_thread". None when a type repeats or exceeds 65535, more than six type
    groups are listed, or a channel index is not strictly increasing, reaches 16 or lists no calls."""
    m = SEND_WINDOW.search(line.rstrip())
    if not m:
        return None
    types = _type_groups(_SEND_TYPE, m["types"], ("type", "calls", "bytes", "calls_per_s", "kb_per_s", "us"))
    channels = _channels(m["channels"])
    if types is None or channels is None:
        return None
    other_keys = ("other_calls", "other_bytes", "other_calls_per_s", "other_kb_per_s", "other_us")
    fields = _numbers(m, SEND_WINDOW_INTS, skip={"types", "channels"} | set(other_keys) | _PAIR_GROUPS)
    fields.update({"end": rigtools.stamp_datetime(line), "types": types, "channels": channels,
                   "other": {key[len("other_"):]: int(m[key]) if key in SEND_WINDOW_INTS else float(m[key])
                             for key in other_keys},
                   "delivery": _pairs(m)})
    return fields


def send_window_problems(fields):
    """What a parsed Send window contradicts in the contract, as messages (empty when it agrees): the type groups plus
    other, the delivery groups and the channels plus channel other must each sum to the calls (and the first two to
    the bytes), and the type groups must run by bytes descending, ties by type ascending."""
    calls, total_bytes = fields["calls"], fields["bytes"]
    type_calls = sum(g["calls"] for g in fields["types"]) + fields["other"]["calls"]
    type_bytes = sum(g["bytes"] for g in fields["types"]) + fields["other"]["bytes"]
    delivery_calls = sum(c for c, _ in fields["delivery"].values())
    delivery_bytes = sum(b for _, b in fields["delivery"].values())
    channel_calls = sum(fields["channels"].values()) + fields["channels_other"]
    problems = [f"{what} sum to {got}, not the window's {want} {unit}"
                for what, got, want, unit in (("type groups and other", type_calls, calls, "calls"),
                                              ("type groups and other", type_bytes, total_bytes, "bytes"),
                                              ("delivery groups", delivery_calls, calls, "calls"),
                                              ("delivery groups", delivery_bytes, total_bytes, "bytes"),
                                              ("channels", channel_calls, calls, "calls"))
                if got != want]
    order = [(-g["bytes"], g["type"]) for g in fields["types"]]
    if order != sorted(order):
        problems.append("type groups are not by bytes descending, ties by type ascending: "
                        + " ".join(f"t{g['type']}:{g['bytes']}" for g in fields["types"]))
    return problems


def parse_phantom_window(line):
    """The Phantom window line, else None: "end"; "seconds"; the counts "bound_virtual", "bound_sink", "ready",
    "filler", "eye_seen" and "fighting" (None when the line has no fighting field); "step", "steps" and "target";
    V_move "vmove_n", "vmove_mean", "vmove_p95", "vmove_min"; "uploads_per_s"; posErr "pos_err_n", "pos_err_p50",
    "pos_err_p95", "pos_err_max" (metres); the per-phantom down distribution "down_n", "down_mean", "down_p95",
    "down_max" (kB/s); "swallowed", {ru, seq, ro, rs, u, other: (calls, bytes)}; "frames" and the rig's time,
    "feeder_ms_per_s", "binder_ms_per_s", "sampler_ms_per_s" and "rig_ms_per_frame". Counts are int, the rest float."""
    m = PHANTOM_WINDOW.search(line.rstrip())
    if not m:
        return None
    fields = _numbers(m, PHANTOM_WINDOW_INTS, skip=_PAIR_GROUPS)
    fields.update({"end": rigtools.stamp_datetime(line), "fighting": int(m["fighting"]) if m["fighting"] else None,
                   "swallowed": _pairs(m)})
    return fields


def parse_sink_window(line):
    """The Sink window line, else None: "end"; "seconds"; "peers", "connected", "bound"; "in_bytes" and "in_packets"
    (the summed window deltas of every sink manager) and "in_kb_per_s"; the per-peer down distribution "down_n",
    "down_mean", "down_p95", "down_max" (kB/s, wire); "types", up to six received types in line order, each {"type",
    "calls", "bytes"}, and the rest as "other_calls" and "other_bytes"; "out_uploads", "uploads_per_s",
    "out_kb_per_s"; "disconnects"; "workers" and "max_pass_ms". None when a type repeats or exceeds 65535, or more than
    six type groups are listed."""
    m = SINK_WINDOW.search(line.rstrip())
    if not m:
        return None
    types = _type_groups(_SINK_TYPE, m["types"], ("type", "calls", "bytes"))
    if types is None:
        return None
    fields = _numbers(m, SINK_WINDOW_INTS, skip={"types"})
    fields.update({"end": rigtools.stamp_datetime(line), "types": types})
    return fields


def parse_bot_net_window(line):
    """The Bot net window line a bot logs every -meshBotNetSeconds, else None: "end" (None: bot lines carry no stamp);
    "at", the line's UTC time as an aware datetime; "seconds"; "in_kb_per_s", "in_packets_per_s", "out_kb_per_s",
    "out_packets_per_s" (float); "hops", "rtt_ms" and "players_seen" (int). None when "at" is not a real time."""
    m = BOT_NET_WINDOW.search(line.rstrip())
    if not m:
        return None
    try:
        at = datetime.datetime.strptime(m["at"], "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=datetime.timezone.utc)
    except ValueError:
        return None
    fields = _numbers(m, BOT_NET_WINDOW_INTS, skip={"at"})
    fields.update({"end": rigtools.stamp_datetime(line), "at": at})
    return fields


def _keyword(text, choices):
    """The choice text names, ignoring case (C#'s OrdinalIgnoreCase), else None."""
    folded = text.lower()
    return folded if folded in choices else None


def _cells(text):
    """A cell field as "all" or a tuple of cell ids in the order written, else an error message."""
    if text.lower() == "all":
        return "all"
    cells, seen = [], set()
    for entry in text.split(","):
        cell = entry.strip()
        if not cell:
            return f"cell field '{text}' has an empty cell"
        if cell.lower() in seen:
            return f"cell field '{text}' names {cell} twice"
        seen.add(cell.lower())
        cells.append(cell)
    return tuple(cells)


def phantom_setting(text):
    """MESH_PHANTOM read as ServerMeshPhantomSettings reads it: (mode, count, kind, cells, None), or
    (None, None, None, None, error). An empty or unset value is (None, None, None, None, None): unset, no warning.

    mode is virtual or sink, count the phantoms bound on each listed cell (at least 1), kind character or eye, and
    cells "all" or a tuple of cell ids. Parts are trimmed and keywords ignore case. The ship kind (step 7) and a
    value without the cell field are refused: without it every cell would bind.
    """
    if text is None or not text.strip():
        return None, None, None, None, None
    parts = [part.strip() for part in text.split(":")]
    if len(parts) == 3:
        return None, None, None, None, f"'{text.strip()}' has no cell field; expected {PHANTOM_SHAPE}"
    if len(parts) != 4:
        return None, None, None, None, f"'{text.strip()}' is not {PHANTOM_SHAPE}"
    mode = _keyword(parts[0], PHANTOM_MODES)
    if mode is None:
        return None, None, None, None, f"mode '{parts[0]}' is not virtual or sink"
    if not COUNT.fullmatch(parts[1]) or int(parts[1]) < 1:
        return None, None, None, None, f"count '{parts[1]}' is not an integer of at least 1"
    if parts[2].lower() == "ship":
        return None, None, None, None, "kind 'ship' is refused until step 7"
    kind = _keyword(parts[2], PHANTOM_KINDS)
    if kind is None:
        return None, None, None, None, f"kind '{parts[2]}' is not character or eye"
    cells = _cells(parts[3])
    if isinstance(cells, str) and cells != "all":
        return None, None, None, None, cells
    return mode, int(parts[1]), kind, cells, None


def _floats(parts):
    return tuple(float(part) for part in parts) if all(FLOAT.fullmatch(part) for part in parts) else None


def phantom_area_setting(text):
    """MESH_PHANTOM_AREA read as ServerMeshPhantomSettings reads it: ("rect", (cx, cz, w, h), None) for a centre and
    size, ("disc", (cx, cz, r), None) for disc:<cx>,<cz>,<r>, or (None, None, error). Sizes and the radius must be
    above 0. An empty or unset value is (None, None, None): unset, no warning."""
    if text is None or not text.strip():
        return None, None, None
    value = text.strip()
    shape, body = "rect", value
    if value[:5].lower() == "disc:":
        shape, body = "disc", value[5:]
    parts = [part.strip() for part in body.split(",")]
    if len(parts) != (3 if shape == "disc" else 4):
        return None, None, f"'{value}' is not {AREA_SHAPES}"
    numbers = _floats(parts)
    if numbers is None:
        return None, None, f"'{value}' has a part that is not a number of metres"
    if any(size <= 0 for size in numbers[2:]):
        return None, None, f"'{value}' has a size or radius that is not above 0"
    return shape, numbers, None


def meter_sends_setting(text):
    """MESH_METER_SENDS read as MeshMeteredTransportFactory.TryParseSetting reads it: (meter, timed, warning). Unset,
    empty and off give (False, False, None); count (True, False, None); time (True, True, None); anything else
    (False, False, warning), and nothing is installed."""
    if text is None or not text.strip():
        return False, False, None
    value = _keyword(text.strip(), METER_SENDS_VALUES)
    if value is None:
        return False, False, f"'{text.strip()}' is not off, count or time"
    return value != "off", value == "time", None
