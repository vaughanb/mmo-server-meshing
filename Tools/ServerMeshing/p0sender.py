"""Phase 0 step 6 instrument helpers: parsers for the Sender window line the state sender logs with Mesh stats and the
CE-12 check line the rig logs once per cell, and mirrors of the MESH_SENDER and MESH_SENDER_TIERS grammars. Pure:
nothing here reads files or the environment. Split out of p0tools (the ~500-line rule), whose registry lists the step 6
keys. Step 7 adds the Sender window's optional payload group (MESH_METER_RECORD_SIZES=1), which never changes an old line.

    p0sender.parse_sender_window(line)   # the "Sender window" line a cell logs with MESH_SENDER=measure|on, else None
    p0sender.parse_ce12_check(line)      # the "CE-12 check:" line a cell logs with MESH_CE12_CHECK=1, else None
    p0sender.sender_setting("measure")   # ("measure", None)
    p0sender.sender_tiers("20/10/5")     # (20, 10, 5, 20, None)

Every parser works on a bare message or a whole log line; "end" is the line's stamp, or None when it has none. The
grammar mirrors decide what the C# settings accept and refuse; their error texts are their own.
Unit tests: python -m unittest discover -s Tools/ServerMeshing -p "test_*.py"
"""
import re

import rigtools

NUM = r"\d+(?:\.\d+)?"
HZ = (20, 10, 5, 2, 1, 0)                     # the Hz a pair can be classified at, in the Sender window's order
SETTINGS = ("off", "measure", "on")           # MeshStateSenderSetting, as MESH_SENDER spells it
CE12_SHAPES = ("stamp", "perCall", "pass", "absent", "stuck")
OWNER_HZ = 20                                 # the owner tier when MESH_SENDER_TIERS names three tiers
TIERS_SHAPE = "<near>/<mid>/<far>[/<owner>]"

_SETTING_CHOICES = "|".join(SETTINGS)
_SHAPE_CHOICES = "|".join(CE12_SHAPES)
_HZ_GROUP = " ".join(rf"{hz}:(?P<hz_{hz}>\d+)" for hz in HZ)
SENDER_WINDOW = re.compile(
    rf"Sender window (?P<seconds>{NUM}) s: (?P<setting>{_SETTING_CHOICES}) sources (?P<sources>\d+) "
    rf"kit (?P<kit>\d+) direct (?P<direct>\d+) pairs (?P<pairs>\d+) clients (?P<clients>\d+) "
    rf"\| builds (?P<builds>\d+) skipped (?P<skipped>\d+) rewraps (?P<rewraps>\d+) refusals (?P<refusals>\d+) "
    rf"\| writes (?P<writes>\d+) compact (?P<compact>\d+) false (?P<not_written>\d+) empty (?P<empty>\d+) "
    rf"errors (?P<errors>\d+) dropped (?P<dropped>\d+) "
    rf"\| records u (?P<records_u>\d+) r (?P<records_r>\d+) deferred (?P<deferred>\d+) "
    rf"\| packets u (?P<packets_u>\d+) r (?P<packets_r>\d+) oversize (?P<oversize>\d+) tooBig (?P<too_big>\d+) "
    rf"sendErrors (?P<send_errors>\d+) reliableFailed (?P<reliable_failed>\d+) "
    rf"\| stuckReliable (?P<stuck_reliable>\d+) withheldOwner (?P<withheld_owner>\d+) "
    rf"\| hz {_HZ_GROUP} withheld (?P<withheld>\d+) clamped (?P<clamped>\d+) "
    rf"\| out (?P<out_kb_per_s>{NUM}) kB/s (?P<out_packets_per_s>{NUM}) pkt/s "
    rf"per client (?P<client_kb_per_s>{NUM}) kB/s (?P<client_packets_per_s>{NUM}) pkt/s "
    rf"\| ms/s build (?P<build_ms_per_s>{NUM}) fanout (?P<fanout_ms_per_s>{NUM}) send (?P<send_ms_per_s>{NUM}) "
    rf"total (?P<total_ms_per_s>{NUM}) "
    rf"\| us/packet (?P<us_per_packet>{NUM}) "
    rf"\| build p95 (?P<build_p95_ms>{NUM}) max (?P<build_max_ms>{NUM}) ms "
    rf"\| gc0 (?P<gc0>\d+)"
    rf"(?: \| payload full n (?P<payload_full_n>\d+) min (?P<payload_full_min>\d+) max (?P<payload_full_max>\d+) "
    rf"compact n (?P<payload_compact_n>\d+) min (?P<payload_compact_min>\d+) max (?P<payload_compact_max>\d+))?$")
SENDER_WINDOW_FLOATS = {"seconds", "out_kb_per_s", "out_packets_per_s", "client_kb_per_s", "client_packets_per_s",
                        "build_ms_per_s", "fanout_ms_per_s", "send_ms_per_s", "total_ms_per_s", "us_per_packet",
                        "build_p95_ms", "build_max_ms"}
_HZ_NAMES = {f"hz_{hz}" for hz in HZ}
PAYLOAD = ("full_n", "full_min", "full_max", "compact_n", "compact_min", "compact_max")   # the payload group's fields
_PAYLOAD_NAMES = {f"payload_{name}" for name in PAYLOAD}

CE12_CHECK = re.compile(
    rf"CE-12 check: cell (?P<cell>\S+) shape=(?P<shape>{_SHAPE_CHOICES}) "
    r"first=(?P<first>[01]) second=(?P<second>[01]) next=(?P<next>[01]) consume=(?P<consume>yes|no) "
    r"after=(?P<after>[01]) reliable=(?P<r_first>[01])/(?P<r_second>[01])/(?P<r_next>[01])/(?P<r_after>[01]) "
    r"handler=(?P<handler>\S+)$")

TIER_HZ = re.compile(r"[0-9]+")


def parse_sender_window(line):
    """The Sender window line (MESH_SENDER measure or on), else None: "end"; "seconds"; "setting" ("measure", "on", or
    "off" for a component whose setting is off); the gauges "sources", "kit", "direct", "pairs", "clients"; "builds",
    "skipped", "rewraps", "refusals"; "writes", "compact", "not_written" (the line's "false"), "empty", "errors",
    "dropped"; "records_u", "records_r", "deferred"; "packets_u", "packets_r", "oversize", "too_big", "send_errors",
    "reliable_failed"; "stuck_reliable", "withheld_owner"; "hz", {20, 10, 5, 2, 1, 0: pairs} at the last build, with
    "withheld" and "clamped"; "out_kb_per_s", "out_packets_per_s", "client_kb_per_s", "client_packets_per_s";
    "build_ms_per_s", "fanout_ms_per_s", "send_ms_per_s", "total_ms_per_s"; "us_per_packet"; "build_p95_ms",
    "build_max_ms"; "gc0"; "payload", {full_n, full_min, full_max, compact_n, compact_min, compact_max} (int: the
    Full and Compact payloads the fan-out wrote, min 0 with none), or None for a line without the group. Counts are
    int, the rest float; only the payload group may follow gc0."""
    m = SENDER_WINDOW.search(line.rstrip())
    if not m:
        return None
    fields = {name: float(text) if name in SENDER_WINDOW_FLOATS else int(text)
              for name, text in m.groupdict().items()
              if name != "setting" and name not in _HZ_NAMES and name not in _PAYLOAD_NAMES}
    payload = None if m["payload_full_n"] is None else {name: int(m[f"payload_{name}"]) for name in PAYLOAD}
    fields.update({"end": rigtools.stamp_datetime(line), "setting": m["setting"],
                   "hz": {hz: int(m[f"hz_{hz}"]) for hz in HZ}, "payload": payload})
    return fields


def parse_ce12_check(line):
    """The CE-12 check line, else None: "end"; "cell"; "shape" (stamp, perCall, pass, absent or stuck); the IsTeleport
    bits "first", "second", "next" and "after" (int 0 or 1) of the writes at T, T, T+50 ms and, after the consume, T+100
    ms; "consume" (bool: the handler implements IEntityMovementServerStateFlags); "reliable", the four writes'
    shouldSendReliably as a tuple of 0/1; "handler", the kit handler's type name."""
    m = CE12_CHECK.search(line.rstrip())
    if not m:
        return None
    return {"end": rigtools.stamp_datetime(line), "cell": m["cell"], "shape": m["shape"],
            "first": int(m["first"]), "second": int(m["second"]), "next": int(m["next"]), "after": int(m["after"]),
            "consume": m["consume"] == "yes",
            "reliable": tuple(int(m[name]) for name in ("r_first", "r_second", "r_next", "r_after")),
            "handler": m["handler"]}


def sender_setting(text):
    """MESH_SENDER read as MeshStateSender.TryParseSetting reads it: (setting, warning). Unset, empty and off give
    ("off", None); measure and on (trimmed, any case) give themselves and no warning; anything else ("off", warning),
    and no sender is added."""
    if text is None or not text.strip():
        return "off", None
    value = text.strip().lower()
    if value not in SETTINGS:
        return "off", f"MESH_SENDER '{text.strip()}' is not off, measure or on"
    return value, None


def _tier_hz(part):
    if not TIER_HZ.fullmatch(part):
        return None
    value = int(part)
    return value if value in HZ else None


def sender_tiers(text):
    """MESH_SENDER_TIERS read as MeshDistanceSendPolicy.TryParseTiers reads it: (near, mid, far, owner, None), or
    (None, None, None, None, error). Three or four parts split on "/", each trimmed and one of 0, 1, 2, 5, 10, 20 Hz;
    the owner tier defaults to 20. An empty or unset value is (20, 20, 20, 20, None): unset, no error."""
    if text is None or not text.strip():
        return OWNER_HZ, OWNER_HZ, OWNER_HZ, OWNER_HZ, None
    parts = [part.strip() for part in text.split("/")]
    if len(parts) not in (3, 4):
        return None, None, None, None, f"'{text.strip()}' is not {TIERS_SHAPE}"
    tiers = []
    for name, part in zip(("near", "mid", "far", "owner"), parts):
        hz = _tier_hz(part)
        if hz is None:
            return None, None, None, None, f"{name} tier '{part}' is not 0, 1, 2, 5, 10 or 20 Hz"
        tiers.append(hz)
    if len(tiers) == 3:
        tiers.append(OWNER_HZ)
    return tiers[0], tiers[1], tiers[2], tiers[3], None
