"""Phase 0 cell records (step 4): one read cell of one rig run, from its log lines to the figures p0 grades and the §5
metrics its JSONL row carries. Pure: every input is an argument.

    p0cells.select_windows(items, start, end)    # the windows wholly inside the measurement, as abload selects them
    p0cells.window_saturated(ww, wt)              # §3's rule for one Work window line (and its Work ticks line)
    p0cells.span_saturated(windows)               # abload's run-level rule over the selected Work window lines
    p0cells.aggregate_work_ticks(triples)         # one figure set over Work window/ticks/bins triples
    p0cells.cell_record(cell, lines, start, end)  # everything p0 reads for one cell of one run
    p0cells.cell_metrics(record, fps, cell)       # the record's §5 metric dicts
    p0cells.window_rows(record)                   # its per-window data for the run row
    p0cells.spawn_area(record)                    # (placement m², players spawned) from its spawn line
    p0cells.stated_rates("60/0.03")               # {"fps": 60, "fixed_step_s": 0.03, "capped": True}

Saturation (the step 4 contract's Decision 18): a single saturated window on a cell is dropped from its aggregate and
named in record["dropped"]; two or more, or a saturated span, exclude the whole run (p0checks.exclusion).
Unit tests: python -m unittest discover -s Tools/ServerMeshing -p "test_*.py"
"""
import datetime
import math
import statistics

import p0base
import p0tools
import rigtools

SLACK = datetime.timedelta(seconds=1)   # a window stamped at whole seconds may have begun up to 1 s before the start
FANOUT_MULTI_TICK_SHARE = 0.02          # fan-out runs only: frames holding two or more ticks, as a share of frames
P95, P99 = 950000, 990000

SUMMED = ("ticks", "expected", "dropped", "cap", "unclosed", "frames", "t0", "t1", "t2", "aoi_passes", "aoi_frames",
          "parts", "late_calls", "send_samples", "physics_f2", "gc0", "outside")
# Each mean on the Work ticks line, weighted by the count it averages over.
WEIGHTED = (("tick_frame_mean", lambda wt: wt["t1"] + wt["t2"]),
            ("other_mean", lambda wt: wt["t0"]),
            ("aoi_frame_mean", lambda wt: wt["aoi_frames"]),
            ("kit_mean", lambda wt: wt["ticks"] - wt["unclosed"]),
            ("sync_mean", lambda wt: wt["parts"]),
            ("aoi_schedule_mean", lambda wt: wt["parts"]),
            ("move_mean", lambda wt: wt["parts"]),
            ("late_mean", lambda wt: wt["late_calls"]),
            ("send_mean", lambda wt: wt["send_samples"]),
            ("physics_mean", lambda wt: wt["frames"]))
TIME_WEIGHTED = ("work_ms_per_s", "physics_per_s", "late_ms_per_s", "busy_percent")
MAXIMA = ("tick_frame_max", "aoi_frame_max", "kit_max", "late_max", "physics_max")
# (histogram, the Work ticks fields it replaces as (field, ppm), the field holding its logged maximum or None)
PERCENTILES = (("tick", (("tick_frame_p95", P95), ("tick_frame_p99", P99)), "tick_frame_max"),
               ("nonaoi", (("nonaoi_p95", P95), ("nonaoi_p99", P99)), None),
               ("other", (("other_p95", P95),), None),
               ("kit", (("kit_p95", P95), ("kit_p99", P99)), "kit_max"),
               ("send", (("send_p95", P95), ("send_p99", P99)), None))
WINDOW_MEANS = ("owned_players", "owned_monsters", "puppet_players", "puppet_monsters", "clients")
MARKERS = {"band": "parse_band", "load_motion": "parse_load_motion", "kinematic_walk": "parse_kinematic_walk",
           "server_animation": "parse_server_animation", "aoi_timing": "parse_aoi_timing",
           "tick_burn": "parse_tick_burn", "spawned": "parse_spawned"}
FALLBACKS = ("mounted", "summoned", "handler", "grid", "install")
THREAD_CLASSES = (("main", "main"), ("litenetlib", "litenetlib"), ("jobs", "jobs"), ("unnamed", "unnamed"),
                  ("other", "other"), ("sampler", "sampler_ms_per_s"))
# (metric name, stat, aggregate field, unit), in the metric order of the step 4 contract's cell_metrics.
TICK_METRICS = (("tick_frame_ms", "mean", "tick_frame_mean", "ms"), ("tick_frame_ms", "p95", "tick_frame_p95", "ms"),
                ("tick_frame_ms", "p99", "tick_frame_p99", "ms"), ("tick_frame_ms", "max", "tick_frame_max", "ms"),
                ("nonaoi_tick_frame_ms", "p95", "nonaoi_p95", "ms"), ("nonaoi_tick_frame_ms", "p99", "nonaoi_p99", "ms"),
                ("aoi_tick_ms", "mean", "aoi_frame_mean", "ms"), ("aoi_tick_ms", "max", "aoi_frame_max", "ms"),
                ("other_frame_ms", "mean", "other_mean", "ms"), ("other_frame_ms", "p95", "other_p95", "ms"),
                ("tick_send_ms", "mean", "send_mean", "ms"), ("tick_send_ms", "p95", "send_p95", "ms"),
                ("tick_send_ms", "p99", "send_p99", "ms"),
                ("kit_sync_ms", "mean", "sync_mean", "ms"), ("kit_aoi_ms", "mean", "aoi_schedule_mean", "ms"),
                ("kit_move_ms", "mean", "move_mean", "ms"),
                ("multi_tick_per_1000", "mean", "multi_tick_per_1000", "per 1000 frames"),
                ("sat_pct", "mean", "sat_pct", "%"))
RATE_METRICS = (("frames_per_s", "mean", "frames_per_s", "1/s"),
                ("physics_steps_per_s", "mean", "physics_per_s", "1/s"),
                ("ticks_per_s", "mean", "ticks_per_s", "1/s"))


def _inside(item, start, end):
    return item["end"] <= end and item["end"] - datetime.timedelta(seconds=item["seconds"]) >= start - SLACK


def select_windows(items, start, end):
    """The items (dicts with "end" and "seconds") lying wholly inside [start - 1 s, end], sorted by end:
    rigtools.aggregate_windows' rule, used for every line family (Work ticks, Threads, Puppets). Raises ValueError for
    an item without a stamp, which cannot be placed."""
    for item in items:
        if item["end"] is None:
            raise ValueError(f"a {item['seconds']:g} s window has no stamp, so it cannot be placed in the measurement")
    return sorted((item for item in items if _inside(item, start, end)), key=lambda item: item["end"])


def window_saturated(ww, wt, fanout=False):
    """Whether one window is saturated (§3): False for an uncapped window (budget 0: it never waits, so its sat share is
    about 100 %); else sat above 20 %, work mean over budget at least 0.95, or (fan-out runs only) more than 2 % of its
    frames holding two or more ticks. ww is a Work window line's fields, wt its Work ticks line's."""
    if ww["budget"] <= 0:
        return False
    if p0base.above(ww["sat"], rigtools.SATURATED_SHARE):
        return True
    if p0base.at_least(ww["work_mean"] / ww["budget"], rigtools.SATURATED_UTIL):
        return True
    return bool(fanout and wt["frames"] > 0 and p0base.above(wt["t2"] / wt["frames"], FANOUT_MULTI_TICK_SHARE))


def span_saturated(windows):
    """Whether the selected Work window lines are saturated as one span: abload's run-level rule (frame-weighted sat
    above 20 %, or frame-weighted work mean over the last window's budget at least 0.95) with the EPS comparisons.
    False with no frames, and for an uncapped span (budget 0), as for a single window."""
    frames = sum(w["frames"] for w in windows)
    if frames == 0:
        return False
    budget = max(windows, key=lambda w: w["end"])["budget"]
    if budget <= 0:
        return False
    sat = math.fsum(w["sat"] * w["frames"] for w in windows) / frames
    work = math.fsum(w["work_mean"] * w["frames"] for w in windows) / frames
    return p0base.above(sat, rigtools.SATURATED_SHARE) or p0base.at_least(work / budget, rigtools.SATURATED_UTIL)


def _weighted_mean(pairs):
    """The weighted mean of (value, weight) pairs, 0.0 when the weights sum to zero (never NaN)."""
    total = math.fsum(weight for _, weight in pairs)
    return math.fsum(value * weight for value, weight in pairs) / total if total > 0 else 0.0


def _per(numerator, denominator):
    return numerator / denominator if denominator > 0 else 0.0


def _percentiles(triples, agg):
    """The p95/p99 fields over the triples in ms: from the merged Work bins histograms when every triple has its bins
    (a rank in the overflow gives the logged maximum for tick and kit, None for the others), else the worst window's."""
    out = {}
    if all(t["bins"] is not None for t in triples):
        out["p95_source"] = "bins"
        for hist, fields, max_field in PERCENTILES:
            merged = p0tools.merge_bins([t["bins"][hist] for t in triples])
            max_us = round(agg[max_field] * 1000) if max_field else None
            for field, ppm in fields:
                value_us = p0tools.percentile_us(merged, ppm, max_us)
                out[field] = None if value_us is None else value_us / 1000
        return out
    out["p95_source"] = "worst-window"
    for _, fields, _ in PERCENTILES:
        for field, _ in fields:
            out[field] = max(t["ticks"][field] for t in triples)
    return out


def _shared_budget(triples):
    """(the first window's Work ticks budget, a problem per window that states another budget)."""
    first = triples[0]["ticks"]["budget"]
    problems = [f"Work ticks at {t['end']:%H:%M:%S}: budget {t['ticks']['budget']:g} ms differs from {first:g} ms "
                f"(the first window's)" for t in triples if t["ticks"]["budget"] != first]
    return first, problems


def aggregate_work_ticks(triples):
    """One figure set over Work window/ticks/bins triples (p0tools.pair_work_lines), or None for none: the counts
    summed; work_ms_per_s, physics_per_s, late_ms_per_s and busy_percent time-weighted; work_ms_per_measured_frame =
    sum(work x seconds) / frames (not the metric work_ms_per_frame, which divides by the stated fps); each other mean
    weighted by what it averages; frames_per_s, ticks_per_s, multi_tick_per_1000; every max the max; the p95/p99 fields
    (ms) from the merged bins when every triple has them (p95_source "bins"), else the worst window ("worst-window");
    sat_pct frame-weighted and the owned/puppet/client counts averaged from the Work window lines; budget, the first
    window's, with a "problems" entry for each window stating another (the run is then not accepted). Zero weights
    give 0, never NaN."""
    if not triples:
        return None
    ticks = [t["ticks"] for t in triples]
    windows = [t["window"] for t in triples]
    agg = {name: sum(wt[name] for wt in ticks) for name in SUMMED}
    seconds, frames = math.fsum(wt["seconds"] for wt in ticks), agg["frames"]
    work = math.fsum(wt["work_ms_per_s"] * wt["seconds"] for wt in ticks)
    agg.update({"windows": len(triples), "seconds": seconds, "work_ms_per_measured_frame": _per(work, frames),
                "frames_per_s": _per(frames, seconds), "ticks_per_s": _per(agg["ticks"], seconds),
                "multi_tick_per_1000": _per(1000.0 * agg["t2"], frames),
                "sat_pct": _weighted_mean([(ww["sat"], ww["frames"]) for ww in windows])})
    for name in TIME_WEIGHTED:
        agg[name] = _weighted_mean([(wt[name], wt["seconds"]) for wt in ticks])
    for name, weight in WEIGHTED:
        agg[name] = _weighted_mean([(wt[name], weight(wt)) for wt in ticks])
    for name in MAXIMA:
        agg[name] = max(wt[name] for wt in ticks)
    for name in WINDOW_MEANS:
        agg[name] = math.fsum(ww[name] for ww in windows) / len(windows)
    agg.update(_percentiles(triples, agg))
    agg["budget"], agg["problems"] = _shared_budget(triples)
    return agg


def _merged_bins(triples):
    """Each histogram merged over the triples, or None when there are none or one lacks its Work bins line."""
    if not triples or any(t["bins"] is None for t in triples):
        return None
    return {name: p0tools.merge_bins([t["bins"][name] for t in triples]) for name in p0tools.HISTOGRAMS}


def _threads(windows):
    """The selected Threads windows with each class's time-weighted ms/s, or None for none."""
    if not windows:
        return None
    out = {"windows": windows, "seconds": math.fsum(w["seconds"] for w in windows),
           "clocks": sorted({w["clock"] for w in windows})}
    for name in p0tools.THREADS_WINDOW_FLOATS[1:]:
        out[name] = _weighted_mean([(w[name], w["seconds"]) for w in windows])
    return out


def _puppets(windows):
    """The selected Puppets windows: mean puppet counts, summed fallbacks and snaps, time-weighted rates, and per steer
    kind the samples summed, their weighted mean, the worst window's p95 and the max; None for none."""
    if not windows:
        return None
    count = len(windows)
    out = {"windows": windows, "seconds": math.fsum(w["seconds"] for w in windows),
           "kinematic": math.fsum(w["kinematic"] for w in windows) / count,
           "steered": math.fsum(w["steered"] for w in windows) / count,
           "snaps": sum(w["snaps"] for w in windows)}
    for reason in FALLBACKS:
        out[f"fallback_{reason}"] = sum(w[f"fallback_{reason}"] for w in windows)
    for name in ("observed_ticks_per_s", "unobserved_poses_per_s"):
        out[name] = _weighted_mean([(w[name], w["seconds"]) for w in windows])
    for kind in ("kinematic", "steered"):
        samples = f"steer_{kind}_samples"
        out[samples] = sum(w[samples] for w in windows)
        out[f"steer_{kind}_mean"] = _weighted_mean([(w[f"steer_{kind}_mean"], w[samples]) for w in windows])
        out[f"steer_{kind}_p95"] = max(w[f"steer_{kind}_p95"] for w in windows)
        out[f"steer_{kind}_max"] = max(w[f"steer_{kind}_max"] for w in windows)
    return out


def _parsed(lines, parse):
    return [fields for fields in map(parse, lines) if fields is not None]


def _median_mem(lines, start, end):
    """The median Unity reserved memory (MB) of the Mesh stats lines stamped inside [start, end], or None."""
    values = []
    for line in lines:
        stats = rigtools.parse_stats_line(line)
        if stats and "mem" in stats:
            stamp = rigtools.stamp_datetime(line)
            if stamp is not None and start <= stamp <= end:
                values.append(stats["mem"])
    return statistics.median(values) if values else None


def cell_record(cell, lines, start, end):
    """Everything p0 reads for one cell of one run, from its log lines since Play and the measurement [start, end]:
    "triples" (the Work window/ticks/bins triples inside the measurement), "pairing" (the unpaired-line problems inside
    it, and any unstamped), "saturated" (the ends of its saturated windows), "dropped" (that end when exactly one is
    saturated, else empty: two or more exclude the run instead), "ticks" (aggregate_work_ticks over the triples minus a
    dropped one), "bins" (each histogram merged over the same triples, or None), "threads" and "puppets" (their
    selected windows with means, or None), "rates" and "rates_cells" (every Rates and Rates cell line since Play),
    "markers" ({name: every parsed marker line since Play}) and "mem_mb" (the median Mesh stats mem in the
    measurement, or None)."""
    lines = list(lines)
    all_triples, unpaired = p0tools.pair_work_lines(lines)
    triples = select_windows(all_triples, start, end)
    flags = [window_saturated(t["window"], t["ticks"]) for t in triples]
    drop_one = sum(flags) == 1
    kept = [t for t, flag in zip(triples, flags) if not (drop_one and flag)]
    saturated = [t["end"] for t, flag in zip(triples, flags) if flag]
    return {
        "cell": cell,
        "triples": triples,
        "pairing": [p["text"] for p in unpaired if p["end"] is None or _inside(p, start, end)],
        "saturated": saturated,
        "dropped": saturated if drop_one else [],
        "ticks": aggregate_work_ticks(kept),
        "bins": _merged_bins(kept),
        "threads": _threads(select_windows(_parsed(lines, p0tools.parse_threads_window), start, end)),
        "puppets": _puppets(select_windows(_parsed(lines, p0tools.parse_puppets_window), start, end)),
        "rates": _parsed(lines, p0tools.parse_rates),
        "rates_cells": _parsed(lines, p0tools.parse_rates_cell),
        "markers": {name: _parsed(lines, getattr(p0tools, parser)) for name, parser in MARKERS.items()},
        "mem_mb": _median_mem(lines, start, end),
    }


def cell_metrics(record, fps, cell):
    """The cell record's §5 metrics, each {name, stat, value, unit, label: "M", cell}, in this order: work_ms_per_s,
    work_ms_per_frame (work_ms_per_s over the STATED fps; left out when fps is None, as for a run without MESH_RATES),
    the tick-frame, non-AOI, AOI, other-frame, tick-send and kit-part times, multi_tick_per_1000, sat_pct, then
    thread_ms_per_s{class} and process_cores (with Threads lines), mem_MB (with Mesh stats lines), frames_per_s,
    physics_steps_per_s, ticks_per_s, and the puppet metrics (with Puppets lines). A percentile whose rank fell in a
    histogram's overflow with no logged maximum has value None (at least 200 ms)."""
    metrics = []

    def add(name, stat, value, unit):
        metrics.append({"name": name, "stat": stat, "value": value, "unit": unit, "label": "M", "cell": cell})

    ticks = record["ticks"]
    if ticks is not None:
        add("work_ms_per_s", "mean", ticks["work_ms_per_s"], "ms/s")
        if fps:
            add("work_ms_per_frame", "mean", ticks["work_ms_per_s"] / fps, "ms/frame")
        for name, stat, field, unit in TICK_METRICS:
            add(name, stat, ticks[field], unit)
    threads = record["threads"]
    if threads is not None:
        for label, field in THREAD_CLASSES:
            add(f"thread_ms_per_s{{{label}}}", "mean", threads[field], "ms/s")
        add("process_cores", "mean", (threads["process"] - threads["sampler_ms_per_s"]) / 1000, "cores")
    if record["mem_mb"] is not None:
        add("mem_MB", "median", record["mem_mb"], "MB")
    if ticks is not None:
        for name, stat, field, unit in RATE_METRICS:
            add(name, stat, ticks[field], unit)
    puppets = record["puppets"]
    if puppets is not None:
        add("puppet_count{kinematic}", "mean", puppets["kinematic"], "puppets")
        add("puppet_count{steered}", "mean", puppets["steered"], "puppets")
        add("puppet_steer_m{kinematic}", "p95", puppets["steer_kinematic_p95"], "m")
        add("puppet_steer_m{kinematic}", "max", puppets["steer_kinematic_max"], "m")
        add("puppet_snaps", "total", puppets["snaps"], "snaps")
        add("puppet_unobserved_poses_per_s", "mean", puppets["unobserved_poses_per_s"], "1/s")
        for reason in FALLBACKS:
            add(f"puppet_fallbacks{{{reason}}}", "total", puppets[f"fallback_{reason}"], "puppets")
    return metrics


def window_rows(record):
    """The run row's per-window data for one cell record: per selected triple {end (ISO text), seconds, frames,
    work_ms_per_s, owned_players, owned_monsters, puppet_players, puppet_monsters, saturated}."""
    rows = []
    for triple in record["triples"]:
        ww = triple["window"]
        rows.append({"end": triple["end"].isoformat(), "seconds": triple["seconds"], "frames": ww["frames"],
                     "work_ms_per_s": triple["ticks"]["work_ms_per_s"], "owned_players": ww["owned_players"],
                     "owned_monsters": ww["owned_monsters"], "puppet_players": ww["puppet_players"],
                     "puppet_monsters": ww["puppet_monsters"], "saturated": triple["end"] in record["saturated"]})
    return rows


def spawn_area(record):
    """(placement area in m², players spawned) from a cell record's last spawn line, or (0, 0) without one."""
    if not record["markers"]["spawned"]:
        return 0, 0
    last = record["markers"]["spawned"][-1]
    return (last["x_max"] - last["x_min"]) * (last["z_max"] - last["z_min"]), last["spawned"]


def stated_rates(text):
    """The rates a spec states with its MESH_RATES value ("60/0.03"; None when it sets none): {"fps", "fixed_step_s",
    "capped"}. fps is the frame rate the run is budgeted at (the cap, or the rate after u; None when unknown: no
    MESH_RATES, or a bare u), the one cell_metrics divides by; capped is False for u. Raises ValueError with
    p0tools.rates_setting's message for a malformed value."""
    if text is None:
        return {"fps": None, "fixed_step_s": None, "capped": True}
    fps, nominal, step, error = p0tools.rates_setting(text)
    if error:
        raise ValueError(f"MESH_RATES {error}")
    return {"fps": nominal or None, "fixed_step_s": step, "capped": fps != p0tools.UNCAPPED}
