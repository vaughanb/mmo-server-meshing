"""Phase 0 stage 0c cell records (step 9, step9_design.md "Log lines"): parsers for every line step 9's C# writes, the
step 9 keys' meanings, the records of one read cell and of the editor's save probe, and the metrics and fields a run
row carries from them. Pure, except editor_lines (it reads the tail of Unity's Editor.log, where the editor's servers,
the database manager among them, log). Imports only rigtools, so p0tools can merge STEP9_REGISTRY without a cycle.

    p0cells0c.parse_db_arrival(line)          # "DB burst <i>: save .. ready <d> ms ok" / "DB burst <i>: failed .."
    p0cells0c.parse_db_burst_window(line)     # also parse_db_burst_start, parse_db_burst_done, parse_db_saves_window,
                                              # parse_monster_cell, parse_forwarded_window, parse_spaces_setting,
                                              # parse_spaces_window, parse_net_stats_window, parse_enter_game
    p0cells0c.cell_record_0c(cell, lines, start, end)   # one read cell's step 9 lines
    p0cells0c.saves_record(lines, start, end)           # the editor's DB saves windows
    p0cells0c.run_metrics_0c(spec, records, saves)      # and cell_metrics_0c, run_fields_0c

Every parser takes a bare message or a whole log line; "end" is the line's stamp (rigtools.stamp_datetime) or None.
Window lines carry their seconds first and end on their stamp. "n/a" percentiles (a window without a result) read None.
Unit tests: python -m unittest discover -s Tools/ServerMeshing -p "test_*.py"
"""
import datetime
import math
import os
import re

import rigtools

STEP9_REGISTRY = {
    "MESH_DB_BURST": "<N>@<t> or <R>/s@<t>+<d>: from t s, N distinct burst characters arrive at once (or R per second "
                     "for d s), each a real handoff's 7 database requests through the cell's DatabaseClient, with DB "
                     "burst lines",
    "MESH_DB_SAVES_PROBE": "1: the editor's database manager logs a DB saves window every 5 s (pending, enqueued, "
                           "written, failed saves, its frame rate, the 16/s ceiling)",
    "MESH_MONSTER_CELL": "<cell>: that cell owns every monster spawn area and the others none, monster handoff off, "
                         "and every other cell streams every owned character to it",
    "MESH_FIGHT_GHOSTS": "1: steered synthetic players may target monster ghosts, so their hits are forwarded to the "
                         "owning cell",
    "MESH_FORWARD_TIMING": "1: the sending cell times forwarded ghost hits to their results in a Forwarded hits window "
                           "(and writes no per-hit trace lines)",
    "MESH_SPACES": "S[:stack|grid]: S isolated spaces on one cell, each its own SubChannelId with observers and 5 "
                   "station stand-ins, stacked at one centre or on a 150 m grid, with Spaces lines",
    "MESH_NET_STATS": "1: a Net stats window every 5 s with LiteNetLib's sent, received and resent packets",
    "MESH_MONSTER_AWAKE": "1: a Monsters window every 5 s (placed 0) with the cell's live scene monsters and how many "
                          "are awake (a subscriber or a player ghost near); off with MESH_MONSTER_PLACE",
}
REQUESTS_PER_ARRIVAL = 7          # decision 2: save, token, get and four background reads (the sheet's §4.5 estimate)
CEILING_WRITES_PER_S = 16         # decision 3: one lane per 500 ms tick, at most 8 writes
SLOPE_SPAN_S = 24.0               # decision 5: the pending slope over the last 24 s of a sustained burst
SLACK = datetime.timedelta(seconds=1)
# The metric names step 9 adds to §5's vocabulary (p0results.METRIC_NAMES joins them; latency_ms is §5's).
METRIC_NAMES_0C = frozenset((
    "db_errors", "db_arrivals_per_s", "db_requests_per_s", "db_saves_written_per_s", "db_saves_pending",
    "db_drain_s", "db_pending_slope_per_s", "forwarded_hits_per_s", "spaces_own_seen", "spaces_other_seen",
    "net_sent_pkt_per_s", "net_received_pkt_per_s", "net_resent_per_s", "net_loss_share"))

NUM = r"-?\d+(?:\.\d+)?"
MS = rf"(?:{NUM}|n/a)"
DB_ARRIVAL_OK = re.compile(r"DB burst (?P<i>\d+): save (?P<save>\d+) token (?P<token>\d+) get (?P<get>\d+) "
                           r"bg (?P<bg>\d+) ready (?P<ready>\d+) ms ok$")
DB_ARRIVAL_FAILED = re.compile(r"DB burst (?P<i>\d+): failed (?P<step>\S+) after (?P<ms>\d+) ms$")
DB_BURST_WINDOW = re.compile(
    rf"DB burst window (?P<seconds>{NUM}) s: started (?P<started>\d+) ready (?P<ready>\d+) failed (?P<failed>\d+) "
    rf"\| ready p50 (?P<p50>{MS}) p95 (?P<p95>{MS}) max (?P<max>{MS}) ms$")
DB_BURST_ONCE = re.compile(rf"DB burst: (?P<n>\d+) arrivals at once at (?P<t>{NUM}) s$")
DB_BURST_RATE = re.compile(rf"DB burst: (?P<r>{NUM})/s for (?P<d>{NUM}) s from (?P<t>{NUM}) s$")
DB_BURST_DONE = re.compile(r"DB burst: done, (?P<ready>\d+) ready (?P<failed>\d+) failed$")
DB_SAVES_WINDOW = re.compile(
    rf"DB saves window (?P<seconds>{NUM}) s: pending (?P<pending>\d+) enqueued (?P<enqueued>\d+) "
    rf"written (?P<written>-?\d+) failed (?P<failed>\d+) \| db fps (?P<fps>{NUM}) \| ceiling (?P<ceiling>\d+)/s$")
MONSTER_CELL_OWNS = re.compile(r"MESH_MONSTER_CELL=(?P<key>\S+): cell (?P<cell>\S+) owns every monster spawn area$")
MONSTER_CELL_NONE = re.compile(r"MESH_MONSTER_CELL=(?P<key>\S+): this cell owns no monster spawn area$")
FORWARDED_WINDOW = re.compile(
    rf"Forwarded hits window (?P<seconds>{NUM}) s: sent (?P<sent>\d+) results (?P<results>\d+) "
    rf"\| p50 (?P<p50>{MS}) p95 (?P<p95>{MS}) max (?P<max>{MS}) ms$")
SPACES_SETTING = re.compile(
    rf"Spaces: S (?P<s>\d+) layout (?P<layout>stack|grid) pitch (?P<pitch>{NUM}) m "
    rf"\| per space observers (?P<observers>{NUM}) content (?P<content>\d+)$")
SPACES_WINDOW = re.compile(rf"Spaces window (?P<seconds>{NUM}) s: observers (?P<observers>\d+) "
                           rf"\| own seen mean (?P<own>{NUM}) \| other seen max (?P<other>\d+)$")
NET_STATS_WINDOW = re.compile(
    rf"Net stats window (?P<seconds>{NUM}) s: sent (?P<sent>\d+) pkt received (?P<received>\d+) pkt "
    rf"resent (?P<resent>\d+) \| loss share (?P<share>{NUM})$")
ENTER_GAME = re.compile(r"enter-game validated\+loaded ok=(?P<ok>True|False) in (?P<ms>\d+) ?ms")
EDITOR_LOG = os.path.join(os.environ.get("LOCALAPPDATA", ""), "Unity", "Editor", "Editor.log")
EDITOR_TAIL_BYTES = 64 * 1024 * 1024   # Editor.log grows past 1 GB: only its tail is read


def _search(pattern, line):
    return pattern.search(line.rstrip())


def _ms(text):
    return None if text == "n/a" else float(text)


def _stamp(line):
    return rigtools.stamp_datetime(line)


def parse_db_arrival(line):
    """One arrival's line, else None: "end", "i" (1-based), "ok"; an ok arrival adds "save_ms", "token_ms", "get_ms",
    "bg_ms", "ready_ms" (the stamped ms of each step since the arrival began), a failed one "step" (the request that
    failed) and "after_ms" ("ready_ms" None)."""
    m = _search(DB_ARRIVAL_OK, line)
    if m:
        return {"end": _stamp(line), "i": int(m["i"]), "ok": True, "save_ms": int(m["save"]),
                "token_ms": int(m["token"]), "get_ms": int(m["get"]), "bg_ms": int(m["bg"]), "ready_ms": int(m["ready"])}
    m = _search(DB_ARRIVAL_FAILED, line)
    if m:
        return {"end": _stamp(line), "i": int(m["i"]), "ok": False, "step": m["step"], "after_ms": int(m["ms"]),
                "ready_ms": None}
    return None


def parse_db_burst_window(line):
    """The cell's DB burst window, else None: "end", "seconds", "started", "ready", "failed", "p50", "p95", "max"
    (ready ms, None for n/a)."""
    m = _search(DB_BURST_WINDOW, line)
    if not m:
        return None
    return {"end": _stamp(line), "seconds": float(m["seconds"]), "started": int(m["started"]),
            "ready": int(m["ready"]), "failed": int(m["failed"]), "p50": _ms(m["p50"]), "p95": _ms(m["p95"]),
            "max": _ms(m["max"])}


def parse_db_burst_start(line):
    """The burst's start line, else None: "end", "mode" ("once": "n"; "rate": "rate" per s for "seconds"), "at_s"."""
    m = _search(DB_BURST_ONCE, line)
    if m:
        return {"end": _stamp(line), "mode": "once", "n": int(m["n"]), "rate": None, "seconds": 0.0,
                "at_s": float(m["t"])}
    m = _search(DB_BURST_RATE, line)
    if m:
        return {"end": _stamp(line), "mode": "rate", "n": None, "rate": float(m["r"]), "seconds": float(m["d"]),
                "at_s": float(m["t"])}
    return None


def parse_db_burst_done(line):
    """The burst's done line ("DB burst: done, <n> ready <m> failed"), else None: "end", "ready", "failed"."""
    m = _search(DB_BURST_DONE, line)
    return None if not m else {"end": _stamp(line), "ready": int(m["ready"]), "failed": int(m["failed"])}


def parse_db_saves_window(line):
    """The editor's DB saves window, else None: "end", "seconds", "pending", "enqueued", "written" (enqueued less the
    pending change: negative when other saves queued), "failed", "db_fps", "ceiling" (writes/s)."""
    m = _search(DB_SAVES_WINDOW, line)
    if not m:
        return None
    return {"end": _stamp(line), "seconds": float(m["seconds"]), "pending": int(m["pending"]),
            "enqueued": int(m["enqueued"]), "written": int(m["written"]), "failed": int(m["failed"]),
            "db_fps": float(m["fps"]), "ceiling": int(m["ceiling"])}


def parse_monster_cell(line):
    """A MESH_MONSTER_CELL line, else None: "end", "key" (the key's cell), "owner" (the cell that says it owns every
    monster spawn area, or None on a cell that owns none)."""
    m = _search(MONSTER_CELL_OWNS, line)
    if m:
        return {"end": _stamp(line), "key": m["key"], "owner": m["cell"]}
    m = _search(MONSTER_CELL_NONE, line)
    return None if not m else {"end": _stamp(line), "key": m["key"], "owner": None}


def parse_forwarded_window(line):
    """The sending cell's Forwarded hits window, else None: "end", "seconds", "sent", "results", "p50", "p95", "max"
    (send-to-result ms, None for n/a)."""
    m = _search(FORWARDED_WINDOW, line)
    if not m:
        return None
    return {"end": _stamp(line), "seconds": float(m["seconds"]), "sent": int(m["sent"]), "results": int(m["results"]),
            "p50": _ms(m["p50"]), "p95": _ms(m["p95"]), "max": _ms(m["max"])}


def parse_spaces_setting(line):
    """The Spaces settings line, else None: "end", "s", "layout", "pitch_m", "observers" (per space), "content"."""
    m = _search(SPACES_SETTING, line)
    if not m:
        return None
    return {"end": _stamp(line), "s": int(m["s"]), "layout": m["layout"], "pitch_m": float(m["pitch"]),
            "observers": float(m["observers"]), "content": int(m["content"])}


def parse_spaces_window(line):
    """The Spaces window, else None: "end", "seconds", "observers", "own_seen" (mean), "other_seen" (max: must be 0)."""
    m = _search(SPACES_WINDOW, line)
    if not m:
        return None
    return {"end": _stamp(line), "seconds": float(m["seconds"]), "observers": int(m["observers"]),
            "own_seen": float(m["own"]), "other_seen": int(m["other"])}


def parse_net_stats_window(line):
    """The Net stats window, else None: "end", "seconds", "sent", "received", "resent" (packets), "loss_share"."""
    m = _search(NET_STATS_WINDOW, line)
    if not m:
        return None
    return {"end": _stamp(line), "seconds": float(m["seconds"]), "sent": int(m["sent"]),
            "received": int(m["received"]), "resent": int(m["resent"]), "loss_share": float(m["share"])}


def parse_enter_game(line):
    """The cell's MeshTrace 'enter-game validated+loaded ok=<b> in <n>ms' line (MeshMapNetworkManager.cs :380), else
    None: "end", "ok", "ms"."""
    m = ENTER_GAME.search(line)
    return None if not m else {"end": _stamp(line), "ok": m["ok"] == "True", "ms": int(m["ms"])}


def db_burst_setting(text):
    """MESH_DB_BURST as the plans write it: (("once", n, t, 0.0) or ("rate", r, t, d), None), or (None, error);
    unset or blank is (None, None). n is an integer of at least 1, r and d numbers above 0, t at least 0."""
    if text is None or not text.strip():
        return None, None
    m = re.fullmatch(rf"\s*(?:(?P<n>\d+)|(?P<r>{NUM})/s)@(?P<t>{NUM})(?:\+(?P<d>{NUM}))?\s*", text)
    if not m or (m["n"] is None) == (m["d"] is None) or float(m["t"]) < 0:
        return None, f"'{text.strip()}' is not <N>@<t> or <R>/s@<t>+<d>"
    if m["n"] is not None:
        return (None, f"count '{m['n']}' is below 1") if int(m["n"]) < 1 else (("once", int(m["n"]), float(m["t"]),
                                                                                0.0), None)
    if float(m["r"]) <= 0 or float(m["d"]) <= 0:
        return None, f"rate '{m['r']}' and span '{m['d']}' must be above 0"
    return ("rate", float(m["r"]), float(m["t"]), float(m["d"])), None


def expected_arrivals(setting):
    """The arrivals a db_burst_setting tuple makes: n at once, or round(r x d)."""
    mode, size, _, seconds = setting
    return size if mode == "once" else round(size * seconds)


def _parsed(lines, parse):
    return [fields for fields in map(parse, lines) if fields is not None]


def select_windows(windows, start, end):
    """The windows wholly inside [start - 1 s, end] by their stamp and seconds, sorted by end (p0cells' rule). Raises
    ValueError for an unstamped window."""
    for window in windows:
        if window["end"] is None:
            raise ValueError(f"a {window['seconds']:g} s window has no stamp, so it cannot be placed in the measurement")
    return sorted((w for w in windows if w["end"] <= end and w["end"] - datetime.timedelta(seconds=w["seconds"])
                   >= start - SLACK), key=lambda w: w["end"])


def _until(items, end):
    return [item for item in items if item["end"] is not None and item["end"] <= end]


def cell_record_0c(cell, lines, start, end):
    """One read cell's step 9 lines for a run measured over [start, end]: "arrivals", "starts", "done" and
    "enter_game" (stamped since Play up to end: a burst or a login may begin before the measurement), "monster_cell"
    (every stamped MESH_MONSTER_CELL line), "spaces_setting" (the stamped Spaces settings lines), and the windows
    inside the measurement: "burst_windows", "forwarded", "spaces", "net"."""
    lines = list(lines)

    def windows(parse):
        return select_windows(_parsed(lines, parse), start, end)
    return {"cell": cell, "arrivals": _until(_parsed(lines, parse_db_arrival), end),
            "starts": _until(_parsed(lines, parse_db_burst_start), end),
            "done": _until(_parsed(lines, parse_db_burst_done), end),
            "enter_game": _until(_parsed(lines, parse_enter_game), end),
            "monster_cell": _until(_parsed(lines, parse_monster_cell), end),
            "spaces_setting": _until(_parsed(lines, parse_spaces_setting), end),
            "burst_windows": windows(parse_db_burst_window), "forwarded": windows(parse_forwarded_window),
            "spaces": windows(parse_spaces_window), "net": windows(parse_net_stats_window)}


def saves_record(lines, start, end):
    """The editor's DB saves windows of one run: "windows" (inside [start - 1 s, end])."""
    return {"windows": select_windows(_parsed(list(lines), parse_db_saves_window), start, end)}


def editor_lines(since, path=None, tail_bytes=EDITOR_TAIL_BYTES):
    """The stamped lines of Editor.log's last tail_bytes stamped at or after since: what the editor's servers (the
    database manager's DB saves window) logged during a run. [] when there is no log. Reads only that file."""
    path = path or EDITOR_LOG
    if not os.path.exists(path):
        return []
    with open(path, "rb") as f:
        f.seek(max(0, os.path.getsize(path) - tail_bytes))
        text = f.read().decode("utf-8", errors="replace")
    return [line for line in text.splitlines() if (_stamp(line) or since - SLACK) >= since]


def percentile(values, share):
    """The nearest-rank percentile (share 0.5 median, 1.0 maximum), or None without values."""
    ordered = sorted(values)
    return ordered[max(0, math.ceil(share * len(ordered)) - 1)] if ordered else None


def slope(points):
    """The least-squares slope of (x, y) points, or None with fewer than two distinct x."""
    if len({x for x, _ in points}) < 2:
        return None
    mx, my = (math.fsum(p[k] for p in points) / len(points) for k in (0, 1))
    return math.fsum((x - mx) * (y - my) for x, y in points) / math.fsum((x - mx) ** 2 for x, _ in points)


def _seconds(windows):
    return math.fsum(w["seconds"] for w in windows)


def burst_figures(records, saves):
    """The DB burst of a run over the read cells' records and the editor's saves record ({} without a start line):
    arrivals, ready, failed, ready_ms and token_get_ms (sorted), mode, rate, seconds (the sustained span), started
    (the start line's stamp), settled_s (start to done line, None without one), arrivals_per_s, requests_per_s, and
    with saves windows: peak_pending, written_per_s, save_failed, empty_s (the done line to the first window at
    pending 0, None if none), slope_per_s (pending over the last 24 s of a sustained burst)."""
    starts = sorted((s for r in records.values() for s in r["starts"]), key=lambda s: s["end"])
    if not starts:
        return {}
    first, arrivals = starts[0], [a for r in records.values() for a in r["arrivals"]]
    done = sorted((d for r in records.values() for d in r["done"]), key=lambda d: d["end"])
    ready = [a for a in arrivals if a["ok"]]
    settled = (done[0]["end"] - first["end"]).total_seconds() if done else None
    span = max(first["seconds"], settled or 0.0, 1.0)
    out = {"mode": first["mode"], "rate": first["rate"], "seconds": first["seconds"], "started": first["end"],
           "arrivals": len(arrivals), "ready": len(ready), "failed": len(arrivals) - len(ready),
           "ready_ms": sorted(a["ready_ms"] for a in ready),
           "token_get_ms": sorted(a["token_ms"] + a["get_ms"] for a in ready), "settled_s": settled,
           "arrivals_per_s": len(arrivals) / span, "requests_per_s": REQUESTS_PER_ARRIVAL * len(arrivals) / span}
    windows = (saves or {}).get("windows") or []
    if windows:
        last = first["end"] + datetime.timedelta(seconds=first["seconds"])
        empty = next((w for w in windows if done and w["end"] >= done[0]["end"] and w["pending"] == 0), None)
        tail = [((w["end"] - first["end"]).total_seconds(), w["pending"]) for w in windows
                if last - datetime.timedelta(seconds=SLOPE_SPAN_S) <= w["end"] <= last]
        out.update(peak_pending=max(w["pending"] for w in windows), save_failed=sum(w["failed"] for w in windows),
                   written_per_s=sum(w["written"] for w in windows) / _seconds(windows) if _seconds(windows) else None,
                   empty_s=None if empty is None else (empty["end"] - done[0]["end"]).total_seconds(),
                   slope_per_s=slope(tail) if first["mode"] == "rate" else None)
    return out


def cell_metrics_0c(record):
    """One cell's step 9 metrics ({name, stat, value, unit, label M, cell}): with Forwarded hits windows,
    forwarded_hits_per_s (sent) and latency_ms{forwarded_hit} p50 (results-weighted mean of the windows' p50), p95
    (the worst window's) and max; with Spaces windows spaces_own_seen mean and spaces_other_seen max; with Net stats
    windows net_sent_pkt_per_s, net_received_pkt_per_s, net_resent_per_s and net_loss_share (resent over sent)."""
    metrics = []

    def add(name, stat, value, unit):
        if value is not None:
            metrics.append({"name": name, "stat": stat, "value": value, "unit": unit, "label": "M",
                            "cell": record["cell"]})
    forwarded, seconds = record["forwarded"], _seconds(record["forwarded"])
    if forwarded and seconds > 0:
        add("forwarded_hits_per_s", "mean", sum(w["sent"] for w in forwarded) / seconds, "1/s")
        timed = [w for w in forwarded if w["p50"] is not None and w["results"]]
        if timed:
            add("latency_ms{forwarded_hit}", "p50", math.fsum(w["p50"] * w["results"] for w in timed)
                / sum(w["results"] for w in timed), "ms")
            add("latency_ms{forwarded_hit}", "p95", max(w["p95"] for w in timed), "ms")
            add("latency_ms{forwarded_hit}", "max", max(w["max"] for w in timed), "ms")
    if record["spaces"]:
        add("spaces_own_seen", "mean", math.fsum(w["own_seen"] for w in record["spaces"]) / len(record["spaces"]),
            "entities")
        add("spaces_other_seen", "max", max(w["other_seen"] for w in record["spaces"]), "entities")
    net, seconds = record["net"], _seconds(record["net"])
    if net and seconds > 0:
        sent = sum(w["sent"] for w in net)
        for name, field in (("net_sent_pkt_per_s", "sent"), ("net_received_pkt_per_s", "received"),
                            ("net_resent_per_s", "resent")):
            add(name, "mean", sum(w[field] for w in net) / seconds, "1/s")
        add("net_loss_share", "mean", sum(w["resent"] for w in net) / sent if sent else None, "share")
    return metrics


def run_metrics_0c(spec, records, saves):
    """The run's DB metrics (cell: the read cells joined by "+", the editor's as "editor"): latency_ms{db_ready}
    p50/p95/p99/max and n, latency_ms{db_token_get} p50/p90, db_errors (failed arrivals plus failed saves),
    db_arrivals_per_s, db_requests_per_s; with saves windows db_saves_written_per_s, db_saves_pending max, db_drain_s
    and db_pending_slope_per_s; with enter-game lines latency_ms{enter_game} p50/p90 and n."""
    metrics, cell = [], "+".join(spec.get("cells_read") or sorted(records))

    def add(name, stat, value, unit, where=cell):
        if value is not None:
            metrics.append({"name": name, "stat": stat, "value": value, "unit": unit, "label": "M", "cell": where})
    burst = burst_figures(records, saves)
    if burst:
        for stat, share in (("p50", 0.5), ("p95", 0.95), ("p99", 0.99), ("max", 1.0)):
            add("latency_ms{db_ready}", stat, percentile(burst["ready_ms"], share), "ms")
        add("latency_ms{db_ready}", "n", len(burst["ready_ms"]), "arrivals")
        for stat, share in (("p50", 0.5), ("p90", 0.9)):
            add("latency_ms{db_token_get}", stat, percentile(burst["token_get_ms"], share), "ms")
        add("db_errors", "total", burst["failed"] + burst.get("save_failed", 0), "errors")
        add("db_arrivals_per_s", "mean", burst["arrivals_per_s"], "1/s")
        add("db_requests_per_s", "mean", burst["requests_per_s"], "1/s")
        add("db_saves_written_per_s", "mean", burst.get("written_per_s"), "1/s", "editor")
        add("db_saves_pending", "max", burst.get("peak_pending"), "saves", "editor")
        add("db_drain_s", "first", burst.get("empty_s"), "s", "editor")
        add("db_pending_slope_per_s", "mean", burst.get("slope_per_s"), "1/s", "editor")
    entered = sorted(e["ms"] for r in records.values() for e in r["enter_game"] if e["ok"])
    if entered:
        for stat, share in (("p50", 0.5), ("p90", 0.9)):
            add("latency_ms{enter_game}", stat, percentile(entered, share), "ms")
        add("latency_ms{enter_game}", "n", len(entered), "arrivals")
    return metrics


def run_fields_0c(records, saves):
    """The run row's step 9 fields, each only when its lines are there: db_burst (burst_figures, started as ISO text),
    enter_game_ms (sorted ok ms), monster_cell ({cell: the owner its line names, or None for 'owns no'}), spaces
    ({cell: its settings line, stamp dropped}) and forwarded ({"sent", "results"} over the read cells)."""
    fields, burst = {}, burst_figures(records, saves)
    if burst:
        fields["db_burst"] = dict(burst, started=burst["started"].isoformat())
    entered = sorted(e["ms"] for r in records.values() for e in r["enter_game"] if e["ok"])
    if entered:
        fields["enter_game_ms"] = entered
    owners = {cid: r["monster_cell"][-1]["owner"] for cid, r in records.items() if r["monster_cell"]}
    if owners:
        fields["monster_cell"] = owners
    spaces = {cid: {k: v for k, v in r["spaces_setting"][-1].items() if k != "end"}
              for cid, r in records.items() if r["spaces_setting"]}
    if spaces:
        fields["spaces"] = spaces
    windows = [w for r in records.values() for w in r["forwarded"]]
    if windows:
        fields["forwarded"] = {"sent": sum(w["sent"] for w in windows), "results": sum(w["results"] for w in windows)}
    return fields
