"""Phase 0 stage 0b cell records (step 8a decisions 2-4): the Send, Sender, Phantom, Ships, Feeder, Sync load, Prewarm
and Monsters lines of one read cell of one rig run, the stage 0b metrics and load fields its JSONL row carries, and the
step windows of a stepped run. Pure: every input is an argument; the parsers are p0phantom's, p0sender's, p0ships' and
p0stage0b's.

    p0cells0b.cell_record_0b(cell, lines, start, end)   # the 0b windows of one cell inside the measurement
    p0cells0b.cell_metrics_0b(record)                   # the record's stage 0b metric dicts
    p0cells0b.load_fields_0b(spec, records)             # kind, clients, V move, payload, scale, awake monsters
    p0cells0b.phantom_steps(lines)                      # every "Phantom step i/n" line a cell logged since Play
    p0cells0b.step_windows(planned, steps, start, end)  # one window per planned step of a stepped run
    p0cells0b.spawn_rise(sends, burst_end)              # Send spawn-type calls/s before and after a Prewarm burst
    p0cells0b.net_saturated(points)                     # the packets saturation arm's per-step flag
    p0cells0b.handoff_outcomes(lines)                   # step 8b: the origin cell's Handoff ACK/ROLLBACK/FAILED lines
    p0cells0b.peer_record(sink_lines, bot_logs, offset_s, start, end)   # B's Sink and Bot windows on A's clock

Step 8b (decisions 9, 14, 19) adds handoff outcomes, wake stamps and CE-12 lines per cell, and run_metrics_0b/_fields_0b.
Units: the Send window's kB is 1024 B (MeshSendMeter.KiloRate), and every kB/s figure here follows it.
Unit tests: python -m unittest discover -s Tools/ServerMeshing -p "test_*.py"
"""
import datetime
import math
import re
import statistics

import p0cells
import p0phantom
import p0sender
import p0ships
import p0stage0b
import rigtools

KB = 1024.0                      # the Send window's kilobyte
SPAWN_TYPE = 3                   # GameMsgTypes.SyncBaseLine: a spawn travels as a GameStateSyncType.Spawn state in it
STEP_SETTLE = datetime.timedelta(seconds=10)   # a step's window starts this long after its ramp ends (decision 4)
BIND_PER_S = 50.0                              # the binder's default pace (ServerMeshPhantomSettings.DefaultArrivalPerSecond)
SPAWN_RISE = datetime.timedelta(seconds=2)     # a Send spawn rise must come within this of the burst (decision 11)
BURST_TAIL = datetime.timedelta(seconds=2)     # a Prewarm burst line is logged 2 s after its last spawn
NET_SATURATED_MS = 900.0         # the LiteNetLib thread at or above this ms/s is saturated (decision 10)
NET_RISE = 0.10                  # datagrams/s rising less than this for a doubling of P is saturated
SHIP_COUNTS = ("ai", "mirror", "parked", "phantom")
SHIP_TIMES = ("pilot", "engine", "combat", "bolts", "mirror")
FEEDER_TIMES = ("encode", "decode", "accept", "pose", "total")
SYNC_TIMES = ("write", "rpc")
MONSTER_COUNTS = ("placed", "alive", "awake")
# The names cell_metrics_0b adds to §5's vocabulary (p0results.METRIC_NAMES joins them): step 7's ship, feeder and sync
# load figures, decision 13's burst figures and decision 12's monster counts.
METRIC_NAMES_0B = frozenset((
    "datagrams_per_s", "ship_count", "ship_ms_per_s", "ship_uploads_per_s", "ship_speed_m_per_s", "feeder_ms_per_s",
    "feeder_us_per_snapshot", "feeder_snapshots_per_s", "sync_load_ms_per_s", "prewarm_frames", "prewarm_spawn_ms",
    "prewarm_per_puppet_ms", "prewarm_after_frames", "prewarm_over_budget_frames", "prewarm_worst_frame_ms",
    "monster_count", "phantom_rig_ms_per_s", "phantom_uploads_per_s", "bot_players_seen", "bot_upload_pkt_per_s",
    "bot_in_kBps", "bots_in_map", "sink_in_kBps", "sink_in_pkt_per_s", "handoff_outcomes", "crossings_per_s"))
# Step 8b: handoff outcomes (Mesh/MeshMapNetworkManager_Ghosting.cs :1023, :3335, :3412, :3431; *.cs :1011) and wakes.
_MS = r"-?\d+(?:\.\d+)?"
HANDOFF_ACK = re.compile(rf"Handoff ACK (?P<name>\S+) by zone (?P<zone>\S+): registered (?P<arrival>{_MS}) ms after the "
                         rf"notice, (?P<total>{_MS}) ms after START \(promoted=(?P<promoted>\w+)\)")
HANDOFF_ENDS = (("rollback", re.compile(r"Handoff ROLLBACK (?P<name>\S+) -> zone (?P<zone>\S+): ")),
                ("fail", re.compile(r"Handoff FAILED (?P<name>\S+) -> zone (?P<zone>\S+): ")),
                ("fail", re.compile(r"Handoff ABORTED (?P<name>\S*) -> zone (?P<zone>\S+): ")))
WAKE_REQUESTED = re.compile(r"Wake requested for sleeping cell (?P<cell>\S+) \(")
OUTCOMES = ("ack", "rollback", "fail")
PEER_EDGE = datetime.timedelta(seconds=2)      # a B-stamped window within this of the measurement's edge is dropped
IN_MAP = "In map at"                           # a bot's map entry (step8b_design.md decision 5)
BOT_BAD = ("invalid state length", "error occurs while reading", "Exception", "is not registered",
           "Unable to spawn object", "Bot ships error:")


def _parsed(lines, parse):
    return [fields for fields in map(parse, lines) if fields is not None]


def _stamped(items, start, end):
    """The stamped items (no length: bursts, step lines) whose stamp lies in [start, end], by stamp."""
    return sorted((item for item in items if item["end"] is not None and start <= item["end"] <= end),
                  key=lambda item: item["end"])


def cell_record_0b(cell, lines, start, end):
    """The stage 0b windows of one cell of one run, from its log lines since Play and the measurement [start, end]:
    "send", "sender", "phantom", "ships", "feeder", "sync_load" and "monsters" (each the parsed windows wholly inside the
    measurement, p0cells.select_windows' rule), "prewarm" (the Prewarm burst lines stamped inside it), "steps" (every
    Phantom step line since Play, unclipped: step_windows clips them), and step 8b's "handoffs" (handoff_outcomes since
    Play up to end, each with in_window: stamped at or after start), "wakes" (wake_stamps up to end) and "ce12" (every
    stamped CE-12 check line since Play). Raises ValueError for an unstamped window."""
    lines = list(lines)

    def windows(parse):
        return p0cells.select_windows(_parsed(lines, parse), start, end)
    return {"cell": cell,
            "send": windows(p0phantom.parse_send_window),
            "sender": windows(p0sender.parse_sender_window),
            "phantom": windows(p0phantom.parse_phantom_window),
            "ships": windows(p0ships.parse_ships_window),
            "feeder": windows(p0ships.parse_feeder_window),
            "sync_load": windows(p0ships.parse_sync_load_window),
            "monsters": windows(p0stage0b.parse_monsters_window),
            "prewarm": _stamped(_parsed(lines, p0stage0b.parse_prewarm_burst), start, end),
            "steps": phantom_steps(lines),
            "handoffs": [dict(h, in_window=h["at"] >= start) for h in handoff_outcomes(lines) if h["at"] <= end],
            "wakes": [w for w in wake_stamps(lines) if w["at"] <= end],
            "ce12": [c for c in _parsed(lines, p0sender.parse_ce12_check) if c["end"] is not None]}


def phantom_steps(lines):
    """Every stamped Phantom step line (p0stage0b.parse_phantom_step) in log order."""
    return [step for step in _parsed(lines, p0stage0b.parse_phantom_step) if step["end"] is not None]


def _seconds(windows):
    return math.fsum(w["seconds"] for w in windows)


def _time_mean(windows, value):
    """The seconds-weighted mean of value(window) over the windows (0.0 when they hold no time)."""
    seconds = _seconds(windows)
    return math.fsum(value(w) * w["seconds"] for w in windows) / seconds if seconds > 0 else 0.0


def clients(record):
    """The mean phantoms bound (virtual plus sink) over the record's Phantom windows, or None without one."""
    windows = record["phantom"]
    if not windows:
        return None
    return math.fsum(w["bound_virtual"] + w["bound_sink"] for w in windows) / len(windows)


def v_move(record):
    """(mean, p95) of V move over the record's Phantom windows: the mean weighted by each window's samples (vmove n),
    the p95 the worst window's; (None, None) without a sampled window."""
    windows = [w for w in record["phantom"] if w["vmove_n"] > 0]
    if not windows:
        return None, None
    n = sum(w["vmove_n"] for w in windows)
    return math.fsum(w["vmove_mean"] * w["vmove_n"] for w in windows) / n, max(w["vmove_p95"] for w in windows)


def type_bytes(window, kind):
    """A Send window's bytes of one message type: its listed group's, else None (an unlisted type is not among the six
    largest the window names, and its share of "other" is unknown: never read as 0)."""
    return next((g["bytes"] for g in window["types"] if g["type"] == kind), None)


def type_calls_per_s(window, kind):
    """A Send window's calls/s of one message type: its listed group's, else None (unknown, as type_bytes)."""
    return next((g["calls_per_s"] for g in window["types"] if g["type"] == kind), None)


def _traffic(record, add):
    sends, seconds, per = record["send"], _seconds(record["send"]), clients(record)
    if sends and seconds > 0:
        packets = sum(w["wire_packets"] for w in sends)
        add("datagrams_per_s", "mean", packets / seconds, "1/s")
        add("link_kBps_out", "mean", sum(w["wire_bytes"] for w in sends) / KB / seconds, "kB/s")
        if per:
            add("datagrams_per_s_per_client", "mean", packets / seconds / per, "1/s")
            states = [type_bytes(w, p0phantom.ENTITY_STATE) for w in sends]
            if all(b is not None for b in states):   # a window that does not list t119 leaves the figure unknown
                add("entity_state_kBps_per_client", "mean", sum(states) / KB / seconds / per, "kB/s")
        if any(w["records"] is not None for w in sends):
            ro = sum(w["records"]["ro"] for w in sends if w["records"] is not None)
            add("reliable_records_per_s", "mean", ro / seconds, "1/s")
    phantoms = record["phantom"]
    if phantoms:
        add("downlink_wire_kBps_per_client", "mean", _time_mean(phantoms, lambda w: w["down_mean"]), "kB/s")
        add("downlink_wire_kBps_per_client", "p95", max(w["down_p95"] for w in phantoms), "kB/s")
        add("phantom_rig_ms_per_s", "mean", _time_mean(phantoms, lambda w: w["feeder_ms_per_s"] + w["binder_ms_per_s"]
                                                       + w["sampler_ms_per_s"]), "ms/s")
        add("phantom_uploads_per_s", "mean", _time_mean(phantoms, lambda w: w["uploads_per_s"]), "1/s")
    senders = record["sender"]
    if senders:
        for hz in p0sender.HZ:
            add(f"rate_hz_by_tier{{{hz}}}", "mean", math.fsum(w["hz"][hz] for w in senders) / len(senders), "pairs")


def _ships(record, add):
    ships = record["ships"]
    if ships:
        seconds = _seconds(ships)
        for kind in SHIP_COUNTS:
            add(f"ship_count{{{kind}}}", "mean", math.fsum(w[kind] for w in ships) / len(ships), "ships")
        for part in SHIP_TIMES:
            add(f"ship_ms_per_s{{{part}}}", "mean", _time_mean(ships, lambda w: w[f"{part}_ms_per_s"]), "ms/s")
        uploads = sum(w["uploads"] + w["stale"] + w["bad"] for w in ships)
        add("ship_uploads_per_s", "mean", uploads / seconds if seconds > 0 else 0.0, "1/s")
        add("ship_speed_m_per_s", "max", max(w["speed_max"] for w in ships), "m/s")
        if uploads and all(w["phantom"] == 0 and w["mirror"] > 0 for w in ships):
            mirror_ms = math.fsum(w["mirror_ms_per_s"] * w["seconds"] for w in ships)
            add("upload_in_us", "mean", mirror_ms * 1000.0 / uploads, "us", "E")
    feeders = record["feeder"]
    if feeders:
        seconds, snapshots = _seconds(feeders), sum(w["snapshots"] for w in feeders)
        for part in FEEDER_TIMES:
            add(f"feeder_ms_per_s{{{part}}}", "mean", _time_mean(feeders, lambda w: w[f"{part}_ms_per_s"]), "ms/s")
        if snapshots:
            add("feeder_us_per_snapshot", "mean",
                math.fsum(w["us_per_snapshot"] * w["snapshots"] for w in feeders) / snapshots, "us")
        add("feeder_snapshots_per_s", "mean", snapshots / seconds if seconds > 0 else 0.0, "1/s")
    syncs = record["sync_load"]
    if syncs:
        for part in SYNC_TIMES:
            add(f"sync_load_ms_per_s{{{part}}}", "mean", _time_mean(syncs, lambda w: w[f"{part}_ms_per_s"]), "ms/s")
        rpcs = sum(w["rpc"] for w in syncs)
        if rpcs:
            add("rpc_send_ns", "mean", math.fsum(w["rpc_ms_per_s"] * w["seconds"] for w in syncs) * 1e6 / rpcs, "ns")


def _bursts(record, add):
    monsters = record["monsters"]
    if monsters:
        for name in MONSTER_COUNTS:
            add(f"monster_count{{{name}}}", "mean", math.fsum(w[name] for w in monsters) / len(monsters), "monsters")
    if record["prewarm"]:
        burst = record["prewarm"][0]
        add("prewarm_frames", "total", burst["frames"], "frames")
        if burst["spawn_ms"] is not None:
            add("prewarm_spawn_ms", "total", burst["spawn_ms"], "ms")
            add("prewarm_per_puppet_ms", "mean", burst["per_puppet_ms"], "ms")
        add("prewarm_after_frames", "total", burst["after_frames"], "frames")
        add("prewarm_over_budget_frames", "total", burst["over_budget"], "frames")
        add("prewarm_worst_frame_ms", "max", burst["worst_ms"], "ms")


def cell_metrics_0b(record):
    """The 0b record's metrics, each {name, stat, value, unit, label, cell: record["cell"]}, in this order. With Send
    windows: datagrams_per_s and link_kBps_out (wire), and per client (the Phantom windows' mean bound, when above 0)
    datagrams_per_s_per_client and entity_state_kBps_per_client (t119 bytes); reliable_records_per_s (records ro) when
    a window has the records group. With Phantom windows: downlink_wire_kBps_per_client mean (time-weighted) and p95 (the
    worst window's), phantom_rig_ms_per_s (the rig's feeder, binder and sampler ms/s, which production would not pay;
    gate J subtracts it) and phantom_uploads_per_s (time-weighted). With Sender windows: rate_hz_by_tier{hz} (the mean pairs at each Hz). With Ships windows: step 7's
    ship_count{kind}, ship_ms_per_s{part}, ship_uploads_per_s (uploads, stale and bad), ship_speed_m_per_s max, and
    upload_in_us (label E: the mirror feeder's ms per upload, write included, only when every window has mirrors and no
    phantom ships); with Feeder windows feeder_ms_per_s{part}, feeder_us_per_snapshot, feeder_snapshots_per_s; with Sync
    load windows sync_load_ms_per_s{part} and rpc_send_ns. Then monster_count{placed, alive, awake} (Monsters windows)
    and the first Prewarm burst's figures (spawn ms left out for range and group, which spawn nothing)."""
    metrics = []

    def add(name, stat, value, unit, label="M"):
        metrics.append({"name": name, "stat": stat, "value": value, "unit": unit, "label": label,
                        "cell": record["cell"]})
    _traffic(record, add)
    _ships(record, add)
    _bursts(record, add)
    return metrics


def _phantom_kind(env):
    mode, _, kind, _, error = p0phantom.phantom_setting(env.get("MESH_PHANTOM"))
    return None if error or mode is None else kind


def load_fields_0b(spec, records, records_0a=None):
    """The run row's load fields a stage 0b spec and its cells' 0b records give ({cell: cell_record_0b}): kind
    (spec["kind"], default "character"), clients {real, phantom_virtual and phantom_sink (the Phantom windows' mean
    bound, summed over cells), observer_kind (MESH_PHANTOM's kind), connected (the Work windows' mean clients summed
    over the cells of records_0a, {cell: p0cells.cell_record}: the transport's connected peers, sink peers and bots
    alike; None without records_0a)}, real being connected less phantom_sink (step 8b decision 19: the bots; a halves
    run's neighbour link may add its peer); v_move_mean and v_move_p95 (v_move over every cell's Phantom windows),
    payload_B and scale (spec["payload_B"], spec["scale"], default 1.0), awake_monsters (the Monsters windows' mean
    awake summed over cells, None without one)."""
    def mean_sum(name, field):
        found = [record[name] for record in records.values() if record[name]]
        return math.fsum(math.fsum(w[field] for w in ws) / len(ws) for ws in found) if found else None
    phantoms = {"cell": None, "phantom": [w for record in records.values() for w in record["phantom"]]}
    mean, p95 = v_move(phantoms)
    ticks = [record["ticks"] for record in (records_0a or {}).values() if record.get("ticks") is not None]
    connected = math.fsum(t["clients"] for t in ticks) if ticks else None
    sink = mean_sum("phantom", "bound_sink") or 0
    return {"kind": spec.get("kind") or "character",
            "clients": {"real": 0 if connected is None else max(0.0, connected - sink),
                        "phantom_virtual": mean_sum("phantom", "bound_virtual") or 0, "phantom_sink": sink,
                        "observer_kind": _phantom_kind(spec.get("env") or {}), "connected": connected},
            "v_move_mean": mean, "v_move_p95": p95, "payload_B": spec.get("payload_B"),
            "scale": 1.0 if spec.get("scale") is None else float(spec["scale"]),
            "awake_monsters": mean_sum("monsters", "awake")}


def step_windows(planned, steps, start, end, settle=STEP_SETTLE):
    """One {"index" (1-based), "step" (P), "start", "end", "problem"} per planned step of a stepped run (planned: the
    spec's stepped["steps"] in run order), from the cell's Phantom step lines (phantom_steps): step k's window is
    [its line's stamp + the ramp to its P at BIND_PER_S + settle, the next step's stamp, or end], clipped to [start,
    end] (decision 4: the step line is logged when a step starts, and the binder then binds at 50/s). start and end are
    None, with a problem, for a step whose line is missing, says another P or step count, or whose window is empty."""
    by_index = {}
    for step in steps:
        by_index.setdefault(step["i"], step)
    out = []
    for index, want in enumerate(planned, 1):
        step, following = by_index.get(index), by_index.get(index + 1)
        window = {"index": index, "step": want, "start": None, "end": None, "problem": None}
        out.append(window)
        if step is None:
            window["problem"] = f"step {index}/{len(planned)} (P {want}) logged no Phantom step line"
            continue
        if step["n"] != len(planned) or step["b"] != want:
            window["problem"] = (f"step {index} logged 'Phantom step {step['i']}/{step['n']}: P {step['a']} -> "
                                 f"{step['b']}', planned {index}/{len(planned)} P {want}")
            continue
        ramp = datetime.timedelta(seconds=max(0, step["b"] - step["a"]) / BIND_PER_S)
        low = max(step["end"] + ramp + settle, start)
        # Stamps have whole-second resolution: a Phantom window stamped in the second the next step's line is logged
        # already counts that step's binds (fanout 2026-10-05: 88 bound at the end of step 50), so the window ends a
        # second earlier.
        high = min(following["end"] - datetime.timedelta(seconds=1) if following is not None else end, end)
        if low >= high:
            window["problem"] = (f"step {index}/{len(planned)} (P {want}) has no time inside the measurement "
                                 f"(it starts {step['end']:%H:%M:%S}, ramps for {ramp.total_seconds():g} s and settles for "
                                 f"{settle.total_seconds():g} s)")
            continue
        window.update(start=low, end=high)
    return out


def spawn_rise(sends, burst_end):
    """(before, after) Send spawn-type (t3) calls/s around a Prewarm burst whose line is stamped burst_end (2 s after
    its last spawn, so the burst is taken at burst_end - 2 s): before is the last window ending at or before the burst,
    after the highest of the windows ending after it and at most one window length plus 2 s later; either is None
    without such a window. A window that does not list t3 counts as 0 before the burst (spawns are then not among its
    six largest types) and is skipped after it; after is None when no window after the burst lists t3."""
    at = burst_end - BURST_TAIL
    before = [w for w in sends if w["end"] <= at]
    after = [w for w in sends if at < w["end"] <= at + datetime.timedelta(seconds=w["seconds"]) + SPAWN_RISE]
    listed = [c for c in (type_calls_per_s(w, SPAWN_TYPE) for w in after) if c is not None]
    return ((type_calls_per_s(before[-1], SPAWN_TYPE) or 0.0) if before else None,
            max(listed) if listed else None)


def net_saturated(points):
    """Per (P, LiteNetLib thread ms/s, datagrams/s) point of one stepped run, whether its network is saturated
    (decision 10, reported, never failed): the thread at or above 900 ms/s, or datagrams/s rising less than 10 % from
    the step at P/2. None when neither can be judged (no thread figure and no step at P/2, or a figure missing)."""
    by_p = {p: datagrams for p, _, datagrams in points}
    flags = []
    for p, thread, datagrams in points:
        if thread is not None and thread >= NET_SATURATED_MS:
            flags.append(True)
            continue
        half = by_p.get(p / 2)
        if half is None or datagrams is None or not half:
            flags.append(None if thread is None else False)
            continue
        flags.append(datagrams < (1.0 + NET_RISE) * half)
    return flags


def handoff_outcomes(lines):
    """Every stamped handoff outcome an origin cell logged, in log order: {"kind": "ack" | "rollback" | "fail", "ms"
    (an ACK's milliseconds after START: handoff_ready; None otherwise), "at" (the stamp), "name" (the character),
    "zone" (the destination)}. ROLLBACK is the origin taking the character back (a silent or dead destination, or the
    client's return); FAILED (the destination refused the arrival) and ABORTED (the handoff save failed, so the
    character stays) are "fail". A late "ACK for <id> ... after a rollback", "ROLLBACK complete" and "ROLLBACK of <id>
    expired" follow an outcome already counted and are not outcomes."""
    found = []
    for line in lines:
        at = rigtools.stamp_datetime(line)
        if at is None:
            continue
        m = HANDOFF_ACK.search(line)
        if m:
            found.append({"kind": "ack", "ms": float(m["total"]), "at": at, "name": m["name"], "zone": m["zone"]})
            continue
        for kind, pattern in HANDOFF_ENDS:
            m = pattern.search(line)
            if m:
                found.append({"kind": kind, "ms": None, "at": at, "name": m["name"], "zone": m["zone"]})
                break
    return found


def wake_stamps(lines):
    """Every stamped 'Wake requested for sleeping cell <id>' line a cell logged: {"at", "cell"} in log order."""
    found = []
    for line in lines:
        m, at = WAKE_REQUESTED.search(line), rigtools.stamp_datetime(line)
        if m and at is not None:
            found.append({"at": at, "cell": m["cell"]})
    return found


def percentile(values, share):
    """The nearest-rank percentile of the values (share 0.95 for p95, 1.0 for the maximum), or None without values."""
    ordered = sorted(values)
    if not ordered:
        return None
    return ordered[max(0, math.ceil(share * len(ordered)) - 1)]


def _local(stamp):
    """A window time as A's naive local time: an aware time (a bot's UTC "at") converted, a naive one kept."""
    return stamp.astimezone().replace(tzinfo=None) if stamp.tzinfo is not None else stamp


def shift_b_windows(windows, offset_s, start, end, field="end", edge=PEER_EDGE):
    """B-stamped windows (Sink windows by "end", B's local stamp; Bot net windows by "at", UTC from B's clock) moved
    onto A's clock and selected against A's measurement [start, end] (step8b_design.md decision 9): each copy's "end"
    is its time minus offset_s (B's clock minus A's, seconds: rigrun.host_context's clock_offset_s), kept only when it lies wholly in the
    measurement with neither edge within 2 s of the measurement's (such a window may hold time outside it on A's
    clock). Windows without a time are dropped."""
    kept = []
    for window in windows:
        stamp = window.get(field)
        if stamp is None:
            continue
        at = _local(stamp) - datetime.timedelta(seconds=offset_s)
        begin = at - datetime.timedelta(seconds=window.get("seconds") or 0.0)
        if begin >= start + edge and at <= end - edge:
            kept.append(dict(window, end=at))
    return kept


def _bot(lines, offset_s, start, end):
    """One bot's log: its Bot net and Bot ships windows (a Bot ships line timed by the Bot net window before it, both
    every 5 s) on A's clock inside the measurement, whether it reached the map, and its fault counts."""
    net, ships, at = [], [], None
    for line in lines:
        window = p0phantom.parse_bot_net_window(line)
        if window:
            at = window["at"]
            net.append(window)
            continue
        seen = p0ships.parse_bot_ships(line)
        if seen and at is not None:
            ships.append(dict(seen, at=at))
    return {"net": shift_b_windows(net, offset_s, start, end, "at"),
            "ships": shift_b_windows(ships, offset_s, start, end, "at"),
            "in_map": any(IN_MAP in line for line in lines),
            "bad": {mark: sum(mark in line for line in lines) for mark in BOT_BAD}}


def peer_record(sink_lines, bot_logs, offset_s, start, end):
    """What machine B logged during one run, on A's clock (step8b_design.md decisions 8, 9): {"sink": the Sink
    windows (p0phantom.parse_sink_window) through shift_b_windows, "bots": {user: {"net", "ships", "in_map", "bad"}}
    from each bot's log lines (bot_logs {user: lines}), "offset_s"} (offset_s: B's clock minus A's). A run's "peer" holds it for the checks and rows."""
    sink = shift_b_windows(_parsed(sink_lines, p0phantom.parse_sink_window), offset_s, start, end)
    return {"sink": sink, "bots": {user: _bot(list(lines), offset_s, start, end) for user, lines in bot_logs.items()},
            "offset_s": offset_s}


def _handoff_figures(records):
    """The read cells' handoff outcomes pooled: (outcomes up to the run's end by stamp, every ACK's ms, wake ms or
    None, the ACKs inside the measurement). wake is the first ACK at or after the first wake request minus that
    request (both stamped on A)."""
    outcomes = sorted((h for record in records.values() for h in record.get("handoffs") or []), key=lambda h: h["at"])
    acks = [h for h in outcomes if h["kind"] == "ack"]
    wakes = sorted(w["at"] for record in records.values() for w in record.get("wakes") or [])
    first = next((h["at"] for h in acks if wakes and h["at"] >= wakes[0]), None)
    wake = (first - wakes[0]).total_seconds() * 1000.0 if first is not None else None
    return outcomes, [h["ms"] for h in acks], wake, [h for h in acks if h.get("in_window")]


def run_metrics_0b(spec, records, peer, seconds):
    """Step 8b's run-level metrics, cell the read cells joined by "+" (or "B" for the peer's): with handoff outcomes
    on the read cells, latency_ms{handoff_ready} p50/p95/p99/max (nearest rank over every ACK) and n,
    handoff_outcomes{ack, rollback, fail} totals, crossings_per_s (ACKs inside the measurement over its seconds), and
    latency_ms{wake} (stat first); with a peer record, bot_players_seen and bot_in_kBps (means over every bot's
    windows), bot_upload_pkt_per_s (the median over bots of each bot's mean out pkt/s), bots_in_map, and sink_in_kBps
    and sink_in_pkt_per_s (the Sink windows' in bytes and packets over their seconds). All label M."""
    metrics, cell = [], "+".join(spec.get("cells_read") or sorted(records))

    def add(name, stat, value, unit, where=cell):
        metrics.append({"name": name, "stat": stat, "value": value, "unit": unit, "label": "M", "cell": where})
    outcomes, ms, wake, inside = _handoff_figures(records)
    if outcomes:
        for stat, share in (("p50", 0.50), ("p95", 0.95), ("p99", 0.99), ("max", 1.0)):
            if ms:
                add("latency_ms{handoff_ready}", stat, percentile(ms, share), "ms")
        add("latency_ms{handoff_ready}", "n", len(ms), "handoffs")
        for kind in OUTCOMES:
            add(f"handoff_outcomes{{{kind}}}", "total", sum(1 for h in outcomes if h["kind"] == kind), "handoffs")
        if seconds:
            add("crossings_per_s", "mean", len(inside) / seconds, "1/s")
    if wake is not None:
        add("latency_ms{wake}", "first", wake, "ms")
    bots = (peer or {}).get("bots") or {}
    nets = [w for bot in bots.values() for w in bot["net"]]
    if nets:
        add("bot_players_seen", "mean", statistics.fmean(w["players_seen"] for w in nets), "players", "B")
        add("bot_in_kBps", "mean", statistics.fmean(w["in_kb_per_s"] for w in nets), "kB/s", "B")
        add("bot_upload_pkt_per_s", "median", statistics.median(
            statistics.fmean(w["out_packets_per_s"] for w in bot["net"]) for bot in bots.values() if bot["net"]),
            "1/s", "B")
    if bots:
        add("bots_in_map", "total", sum(1 for bot in bots.values() if bot["in_map"]), "bots", "B")
    sinks = (peer or {}).get("sink") or []
    if _seconds(sinks) > 0:
        add("sink_in_kBps", "mean", sum(w["in_bytes"] for w in sinks) / KB / _seconds(sinks), "kB/s", "B")
        add("sink_in_pkt_per_s", "mean", sum(w["in_packets"] for w in sinks) / _seconds(sinks), "1/s", "B")
    return metrics


def run_fields_0b(spec, records, context):
    """Step 8b's run row fields (decision 19); {} for a spec without a peer: peer (the spec's), clock_offset_s (the
    context's: B's clock minus A's, seconds) and, for a crossings run or one with handoff outcomes, handoffs {ack,
    rollback, fail (counts up to the run's end), ack_ms (every ACK's handoff_ready ms, sorted: the report pools them),
    wake_ms, window_acks (the ACKs inside the measurement)}."""
    if not spec.get("peer"):
        return {}
    fields = {"peer": spec["peer"], "clock_offset_s": (context or {}).get("clock_offset_s")}
    outcomes, ms, wake, inside = _handoff_figures(records)
    if outcomes or spec["family"] == "crossings":
        fields["handoffs"] = dict({kind: sum(1 for h in outcomes if h["kind"] == kind) for kind in OUTCOMES},
                                  ack_ms=sorted(ms), wake_ms=wake, window_acks=len(inside))
    return fields
