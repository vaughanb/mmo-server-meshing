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

Units: the Send window's kB is 1024 B (MeshSendMeter.KiloRate), and every kB/s figure here follows it.
Unit tests: python -m unittest discover -s Tools/ServerMeshing -p "test_*.py"
"""
import datetime
import math

import p0cells
import p0phantom
import p0sender
import p0ships
import p0stage0b

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
    "monster_count"))


def _parsed(lines, parse):
    return [fields for fields in map(parse, lines) if fields is not None]


def _stamped(items, start, end):
    """The stamped items (no length: bursts, step lines) whose stamp lies in [start, end], by stamp."""
    return sorted((item for item in items if item["end"] is not None and start <= item["end"] <= end),
                  key=lambda item: item["end"])


def cell_record_0b(cell, lines, start, end):
    """The stage 0b windows of one cell of one run, from its log lines since Play and the measurement [start, end]:
    "send", "sender", "phantom", "ships", "feeder", "sync_load" and "monsters" (each the parsed windows wholly inside the
    measurement, p0cells.select_windows' rule), "prewarm" (the Prewarm burst lines stamped inside it) and "steps" (every
    Phantom step line since Play, unclipped: step_windows clips them). Raises ValueError for an unstamped window."""
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
            "steps": phantom_steps(lines)}


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
    worst window's). With Sender windows: rate_hz_by_tier{hz} (the mean pairs at each Hz). With Ships windows: step 7's
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


def load_fields_0b(spec, records):
    """The run row's load fields a stage 0b spec and its cells' 0b records give ({cell: cell_record_0b}): kind
    (spec["kind"], default "character"), clients {real 0, phantom_virtual and phantom_sink (the Phantom windows' mean
    bound, summed over cells), observer_kind (MESH_PHANTOM's kind)}, v_move_mean and v_move_p95 (v_move over every
    cell's Phantom windows), payload_B and scale (spec["payload_B"], spec["scale"], default 1.0), awake_monsters (the
    Monsters windows' mean awake summed over cells, None without one)."""
    def mean_sum(name, field):
        found = [record[name] for record in records.values() if record[name]]
        return math.fsum(math.fsum(w[field] for w in ws) / len(ws) for ws in found) if found else None
    phantoms = {"cell": None, "phantom": [w for record in records.values() for w in record["phantom"]]}
    mean, p95 = v_move(phantoms)
    return {"kind": spec.get("kind") or "character",
            "clients": {"real": 0, "phantom_virtual": mean_sum("phantom", "bound_virtual") or 0,
                        "phantom_sink": mean_sum("phantom", "bound_sink") or 0,
                        "observer_kind": _phantom_kind(spec.get("env") or {})},
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
