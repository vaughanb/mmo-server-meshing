"""Phase 0 step 9 checks and verdicts (step9_design.md decisions 4, 5, 9, 11, 13): the run checks of the stage 0c
families and of packets' Net stats (p0checks.FAMILY_CHECKS joins them to the common ones), the Database gate and
Crossings half B's B5 term, sustained capacity, the calibration pairs, workerab's acceptance, the spaces floor and the
loss arms' figures. Pure: rows and runs come in as arguments.

    p0checks9.check_db_burst(spec, run)      # the burst's start, done and arrival lines, its windows, the saves probe
    p0checks9.db_points(rows)                # db_ready p50/p95/p99/max, errors, rates and drain per dbburst point
    p0checks9.sustained_capacity(rows)       # decision 5: the highest R with p95 < 1 s and slope <= 0.5/s
    p0checks9.workerab_acceptance(rows)      # decision 9: [(passed, text)]
    p0checks9.gates_9(runs)                  # gate rows Database and Crossings (B10) half B for p0report

A run's step 9 records are run["cells"][cell]["p0c"] (p0cells0c.cell_record_0c) and run["saves"] (the editor's DB saves
windows); a row's step 9 fields are p0cells0c.run_fields_0c's.
Unit tests: python -m unittest discover -s Tools/ServerMeshing -p "test_*.py"
"""
import json
import math
import statistics

import p0base
import p0cells0c
import p0checks8b
import p0results

GATE_P95_MS, GATE_N = 5000.0, 200          # Database (sheet :1041): 200 at once ready within 5 s at p95, 0 errors
SUSTAINED_P95_MS, SLOPE_LIMIT = 1000.0, 0.5  # decision 5's sustained rule
DATABASE_RULE = (f"{GATE_N} arrivals at once: db_ready p95 <= {GATE_P95_MS / 1000:g} s with 0 errors (failed arrivals "
                 f"and failed saves); sustained capacity = the highest R with ready p95 < {SUSTAINED_P95_MS / 1000:g} s "
                 f"and a pending slope over the last {p0cells0c.SLOPE_SPAN_S:g} s <= {SLOPE_LIMIT:g}/s, beside the "
                 f"configured {p0cells0c.CEILING_WRITES_PER_S} writes/s; '>= 2x the crossing load' is compared in the "
                 "stage report")
HALF_B_RULE = ("the recommended geography's crossing rate <= 50 % of B5's sustained handoff rate (one arrival per "
               "handoff); the geography is chosen in the stage report, so this row states B5's term")


def _records(spec, run, key="p0c"):
    cells = (run or {}).get("cells") or {}
    return [(cid, cells[cid][key]) for cid in spec["cells_read"] if (cells.get(cid) or {}).get(key) is not None]


def check_db_burst(spec, run):
    """With MESH_DB_BURST: a start line on a read cell stating the setting, a done line before the run's end, as many
    arrival lines as the setting makes, DB burst windows in the measurement, and with MESH_DB_SAVES_PROBE=1 DB saves
    windows from the editor. Raises ValueError for a setting p0cells0c.db_burst_setting refuses."""
    setting, error = p0cells0c.db_burst_setting(spec["env"].get("MESH_DB_BURST"))
    if error:
        raise ValueError(f"MESH_DB_BURST: {error}")
    if setting is None:
        return []
    records, want = _records(spec, run), p0cells0c.expected_arrivals(setting)
    starts = [s for _, r in records for s in r["starts"]]
    arrivals = sum(len(r["arrivals"]) for _, r in records)
    problems = []
    if not starts:
        problems.append(f"no 'DB burst:' start line on {', '.join(spec['cells_read'])}")
    else:
        said = (starts[0]["mode"], starts[0]["n"] if starts[0]["mode"] == "once" else starts[0]["rate"],
                starts[0]["seconds"])
        if (said[0], float(said[1]), said[2]) != (setting[0], float(setting[1]), setting[3]):
            problems.append(f"the DB burst start line says {said[0]} {said[1]:g} over {said[2]:g} s, the setting "
                            f"{spec['env']['MESH_DB_BURST']}")
    if not any(r["done"] for _, r in records):
        problems.append(f"the DB burst logged no done line by the run's end ({arrivals} of {want} arrivals settled)")
    if arrivals != want:
        problems.append(f"{arrivals} DB burst arrival lines, expected {want}")
    if not any(r["burst_windows"] for _, r in records):
        problems.append("no DB burst window in the measurement")
    if spec["env"].get("MESH_DB_SAVES_PROBE") == "1" and not ((run or {}).get("saves") or {}).get("windows"):
        problems.append("no DB saves window from the editor in the measurement (MESH_DB_SAVES_PROBE=1)")
    return problems


def check_enter_game(spec, run):
    """The calibration's bot arm: the read cells logged an ok 'enter-game validated+loaded' line (MeshTrace) per bot."""
    crowd = (spec.get("peer") or {}).get("bots")
    if spec["family"] != "dbburst" or not crowd:
        return []
    ok = sum(1 for _, r in _records(spec, run) for e in r["enter_game"] if e["ok"])
    return [] if ok >= crowd["n"] else [f"{ok} of {crowd['n']} bots logged an ok enter-game line (MeshTrace; "
                                        f"traceHandoffTiming must be on)"]


def check_monster_cell(spec, run):
    """With MESH_MONSTER_CELL=X, cell X logged that it owns every monster spawn area and every other read cell that it
    owns none (each naming X); without the key, no read cell logged a MESH_MONSTER_CELL line."""
    key, problems = (spec["env"].get("MESH_MONSTER_CELL") or "").strip(), []
    for cid, record in _records(spec, run):
        lines = record["monster_cell"]
        if not key:
            problems += [f"{cid} logged a MESH_MONSTER_CELL line with no key set"] if lines else []
            continue
        want = key if cid == key else None
        if not any(line["key"] == key and line["owner"] == want for line in lines):
            what = f"cell {key} owns every monster spawn area" if want else "this cell owns no monster spawn area"
            problems.append(f"{cid} logged no 'MESH_MONSTER_CELL={key}: {what}' line")
    return problems


def check_forwarded(spec, run):
    """With MESH_MONSTER_CELL and MESH_FORWARD_TIMING=1, a read cell other than the monster cell (the sending cell)
    logged Forwarded hits windows in the measurement (forwarded hits > 0 is the block's acceptance)."""
    key = (spec["env"].get("MESH_MONSTER_CELL") or "").strip()
    if not key or spec["env"].get("MESH_FORWARD_TIMING") != "1":
        return []
    senders = [cid for cid, record in _records(spec, run) if cid != key and record["forwarded"]]
    return [] if senders else [f"no Forwarded hits window on a cell other than {key} in the measurement"]


def check_spaces(spec, run):
    """With MESH_SPACES=S[:layout], each read cell logged the Spaces line with that S and layout and Spaces windows in
    the measurement, each with the phantoms bound as observers and other seen 0 (decision 10: an observer sees only
    its own space)."""
    text = (spec["env"].get("MESH_SPACES") or "").strip()
    if not text:
        return []
    count, _, layout = text.partition(":")
    parts = (spec["env"].get("MESH_PHANTOM") or "").split(":")
    layout, observers = layout or "stack", int(parts[1]) if len(parts) > 1 and parts[1].isdigit() else None
    problems = []
    for cid, record in _records(spec, run):
        lines, windows = record["spaces_setting"], record["spaces"]
        if not any(line["s"] == int(count) and line["layout"] == layout for line in lines):
            problems.append(f"{cid} logged no 'Spaces: S {count} layout {layout}' line")
        if not windows:
            problems.append(f"{cid} logged no Spaces window in the measurement")
        leaks = [w for w in windows if w["other_seen"]]
        if leaks:
            problems.append(f"{cid}: other seen max {max(w['other_seen'] for w in leaks)} in {len(leaks)} of "
                            f"{len(windows)} Spaces windows (must be 0)")
        short = [w for w in windows if observers is not None and w["observers"] != observers]
        if short:
            problems.append(f"{cid}: {len(short)} Spaces windows held other than {observers} observers")
    return problems


def check_net_stats(spec, run):
    """With MESH_NET_STATS=1, each read cell logged Net stats windows in the measurement."""
    if spec["env"].get("MESH_NET_STATS") != "1":
        return []
    return [f"{cid} logged no Net stats window in the measurement (MESH_NET_STATS=1)"
            for cid, record in _records(spec, run) if not record["net"]]


CHECKS_9 = {"dbburst": (check_db_burst, check_enter_game, p0checks8b.check_bots_on_cell),
            "workerab": (check_monster_cell, check_forwarded), "spaces": (check_spaces,)}


def _accepted(rows, family, case=None):
    return [r for r in p0checks8b._latest(rows) if r.get("family") == family and r.get("accepted")
            and (case is None or r.get("case") == case)]


def _mean(values):
    values = [v for v in values if v is not None]
    return math.fsum(values) / len(values) if values else None


def db_points(rows):
    """Per accepted dbburst point (case, arm, point; the latest row of each plan key) with a db_burst field: {"case",
    "arm", "point", "n_runs", "p50", "p95", "p99", "max" (nearest rank over every ready ms), "errors" (failed arrivals
    and saves), "arrivals_per_s", "requests_per_s", "written_per_s" (means), "peak_pending" (max), "empty_s" (the
    slowest drain, None when a run never emptied), "slope_per_s" (the steepest)}, in first-seen order."""
    groups = {}
    for row in _accepted(rows, "dbburst"):
        if row.get("db_burst"):
            groups.setdefault((row["case"], row["arm"], json.dumps(row.get("point") or {}, sort_keys=True)),
                              []).append(row)
    out = []
    for (case, arm, point), runs in groups.items():
        bursts = [r["db_burst"] for r in runs]
        ready = [ms for b in bursts for ms in b["ready_ms"]]
        empties, slopes = [b.get("empty_s") for b in bursts], [b.get("slope_per_s") for b in bursts]
        entry = {"case": case, "arm": arm, "point": json.loads(point), "n_runs": len(runs),
                 "errors": sum(b["failed"] + (b.get("save_failed") or 0) for b in bursts),
                 "arrivals_per_s": _mean(b["arrivals_per_s"] for b in bursts),
                 "requests_per_s": _mean(b["requests_per_s"] for b in bursts),
                 "written_per_s": _mean(b.get("written_per_s") for b in bursts),
                 "peak_pending": max((b.get("peak_pending") or 0 for b in bursts), default=None),
                 "empty_s": None if None in empties else max(empties),
                 "slope_per_s": None if None in slopes else max(slopes)}
        entry.update({name: p0cells0c.percentile(ready, share)
                      for name, share in (("p50", 0.5), ("p95", 0.95), ("p99", 0.99), ("max", 1.0))})
        out.append(entry)
    return out


def sustained_capacity(rows):
    """Decision 5: {"R" (the highest sustained R whose pooled ready p95 < 1 s and every run's pending slope <= 0.5/s;
    None when none passes), "arrivals_per_s", "requests_per_s" (that point's measured means), "points": [{"R", "p95",
    "slope_per_s", "passed"}]}, or None without sustained rows."""
    points = sorted((p for p in db_points(rows) if p["case"] == "sustained"), key=lambda p: p["point"].get("R", 0))
    if not points:
        return None
    marked = [dict(p, passed=p["p95"] is not None and p0base.above(SUSTAINED_P95_MS, p["p95"])
                   and p["slope_per_s"] is not None and p0base.at_most(p["slope_per_s"], SLOPE_LIMIT)) for p in points]
    best = next((p for p in reversed(marked) if p["passed"]), None)
    return {"R": best["point"]["R"] if best else None, "arrivals_per_s": best["arrivals_per_s"] if best else None,
            "requests_per_s": best["requests_per_s"] if best else None,
            "points": [{"R": p["point"]["R"], "p95": p["p95"], "slope_per_s": p["slope_per_s"], "passed": p["passed"]}
                       for p in marked]}


def calibration_pairs(rows):
    """Decision 4's calibration per pair (repeat): {"pair", "bots" (median, p90 of the cell's enter-game ms),
    "burst" (median, p90 of token + get ms)} for the pairs with both arms accepted."""
    found = {}
    for row in _accepted(rows, "dbburst", "calibration"):
        found.setdefault(row["repeat"], {})[row["arm"]] = row
    out = []
    for pair, arms in sorted(found.items()):
        if "bots" in arms and "burst" in arms:
            bots, burst = arms["bots"].get("enter_game_ms") or [], (arms["burst"].get("db_burst") or {}).get(
                "token_get_ms") or []
            out.append({"pair": pair, "bots": (p0cells0c.percentile(bots, 0.5), p0cells0c.percentile(bots, 0.9)),
                        "burst": (p0cells0c.percentile(burst, 0.5), p0cells0c.percentile(burst, 0.9))})
    return out


def _cell_mean(row, cell, field):
    windows = (row.get("windows") or {}).get(cell) or []
    return _mean(w[field] for w in windows)


def workerab_acceptance(rows):
    """Decision 9's acceptance over the accepted workerab ab rows (the latest of each plan key), [(passed, text)]: E
    owns every monster and C none (every halves row's MESH_MONSTER_CELL lines); C sees E's monsters (C's mean monster
    puppets > 0 in every halves row); forwarded hits > 0 (sent, every halves row) with timing; both arms carry an
    awake-monster figure per load (MESH_MONSTER_AWAKE's Monsters windows), reported, not judged: with every player a
    connected phantom on C in both arms the counts differ by design (coarse keeps a monster awake while a player
    subscribes to it, out to the 80 m view; the monster cell has no connected player, so the mesh's ghost-watch rule
    decides, out to the monster's visual range + 10 m; 2026-10-09 smoke: 164 against 85); every planned run accepted
    (no exception line)."""
    rows = [r for r in p0checks8b._latest(rows) if r.get("family") == "workerab" and r.get("case") == "ab"]
    ok_rows = [r for r in rows if r.get("accepted")]
    halves = [r for r in ok_rows if r["arm"] == "halves"]
    if not halves:
        return [(False, "workerab: no accepted halves row")]
    owners = [r.get("monster_cell") or {} for r in halves]
    out = [(all(o.get("E") == "E" and "C" in o and o["C"] is None for o in owners),
            f"E owns every monster and C none in {sum(o.get('E') == 'E' and o.get('C', 1) is None for o in owners)} "
            f"of {len(halves)} halves runs")]
    ghosts = [_cell_mean(r, "C", "puppet_monsters") for r in halves]
    out.append((all(g is not None and g > 0 for g in ghosts),
                f"C sees E's monsters: monster puppets on C {', '.join('–' if g is None else f'{g:.1f}' for g in ghosts)}"))
    sent = [(r.get("forwarded") or {}).get("sent", 0) for r in halves]
    p95 = [p0results.metric_value(r, "latency_ms{forwarded_hit}", "p95", "C") for r in halves]
    out.append((all(s > 0 for s in sent) and all(v is not None for v in p95),
                f"forwarded hits sent {', '.join(str(s) for s in sent)}; forwarded_hit p95 "
                f"{', '.join('–' if v is None else f'{v:.0f}' for v in p95)} ms"))
    for load in sorted({(r.get("point") or {}).get("players") for r in ok_rows}, key=str):
        awake = {arm: _mean(r.get("awake_monsters") for r in ok_rows if r["arm"] == arm
                            and (r.get("point") or {}).get("players") == load) for arm in ("coarse", "halves")}
        if None in awake.values() or not awake["coarse"]:
            out.append((False, f"load {load}: no awake-monster figure in both arms (no Monsters window line: MESH_MONSTER_AWAKE=1 logs it)"))
            continue
        out.append((True, f"load {load}: awake monsters coarse {awake['coarse']:.1f}, halves {awake['halves']:.1f} "
                          f"(reported: the monster cell wakes by ghost proximity, coarse by subscription)"))
    out.append((len(ok_rows) == len(rows), f"{len(ok_rows)} of {len(rows)} ab runs accepted (no exception line)"))
    return out


def _ci(values):
    mean = math.fsum(values) / len(values)
    if len(values) < 2:
        return mean, None, None
    half = p0base.t975(len(values) - 1) * statistics.stdev(values) / math.sqrt(len(values))
    return mean, mean - half, mean + half


def spaces_figures(rows, cell="C"):
    """Decision 11 over the accepted spaces rows: "floor" [{"S", "per_space_ms", "lo", "hi", "n"}] (C work ms/s over
    S at 1 observer per space, stack, with its t interval over the repeats), "slope" (ms/s per space, least squares of
    C work against S at 100 observers, stack) and "other_seen" (the largest spaces_other_seen over every row)."""
    runs, floor = _accepted(rows, "spaces"), []

    def work(row):
        return p0results.metric_value(row, "work_ms_per_s", "mean", cell)
    for s in sorted({(r.get("point") or {}).get("S") for r in runs if r.get("arm") == "per-space"}):
        values = [work(r) / s for r in runs if r["arm"] == "per-space" and r["case"] == "stack" and r["point"]["S"] == s
                  and work(r) is not None]
        if values:
            mean, lo, hi = _ci(values)
            floor.append({"S": s, "per_space_ms": mean, "lo": lo, "hi": hi, "n": len(values)})
    points = [(r["point"]["S"], work(r)) for r in runs if r["arm"] == "100" and r["case"] == "stack"
              and work(r) is not None]
    seen = [p0results.metric_value(r, "spaces_other_seen", "max", cell) for r in runs]
    return {"floor": floor, "slope": p0cells0c.slope(points),
            "other_seen": max((v for v in seen if v is not None), default=None)}


def loss_figures(rows, cell="C"):
    """Decision 13 per loss arm (accepted packets loss rows, means over the repeats): {"arm", "loss_pct", "n", "work",
    "net_thread", "datagrams_per_s", "resent_per_s", "loss_share", "received_per_pair" (the sink's received packets
    per s over its P peers)}, in loss order."""
    by_arm = {}
    for row in _accepted(rows, "packets", "loss"):
        by_arm.setdefault(row["arm"], []).append(row)
    out = []
    for arm, runs in sorted(by_arm.items(), key=lambda item: item[1][0]["point"].get("loss_pct", 0)):
        def mean(name, stat="mean", where=cell):
            return _mean(p0results.metric_value(r, name, stat, where) for r in runs)
        sink = mean("sink_in_pkt_per_s", where="B")
        out.append({"arm": arm, "loss_pct": runs[0]["point"].get("loss_pct"), "n": len(runs),
                    "work": mean("work_ms_per_s"), "net_thread": _mean(p0checks8b.net_thread(r, cell) for r in runs),
                    "datagrams_per_s": mean("datagrams_per_s"), "resent_per_s": mean("net_resent_per_s"),
                    "loss_share": mean("net_loss_share"),
                    "received_per_pair": None if sink is None else sink / runs[0]["point"].get("P", 1)})
    return out


def _fmt(value, unit=""):
    return "–" if value is None else f"{value:.1f}{unit}"


def gates_9(runs):
    """Step 9's gate rows over the report's run rows: Database (decision 5) when dbburst rows exist (its verdict from
    the 200-at-once point: NO VERDICT without one), and Crossings (B10) half B's B5 term (NO VERDICT: stated, compared
    in the stage report) when sustained rows exist."""
    rows = _accepted(runs, "dbburst")
    if not rows:
        return []
    points, capacity, gates = db_points(rows), sustained_capacity(rows), []
    gate = next((p for p in points if p["case"] == "burst" and p["point"].get("N") == GATE_N), None)
    verdict = (p0base.NO_VERDICT if gate is None or gate["p95"] is None else
               p0base.pass_fail(p0base.at_most(gate["p95"], GATE_P95_MS) and gate["errors"] == 0))
    notes = [f"{p['case']} {' '.join(f'{k}={v}' for k, v in p['point'].items() if k != 'rates')}: p50/p95/p99/max "
             f"{_fmt(p['p50'])}/{_fmt(p['p95'])}/{_fmt(p['p99'])}/{_fmt(p['max'])} ms, {p['errors']} errors, "
             f"{_fmt(p['arrivals_per_s'])} arrivals/s, {_fmt(p['requests_per_s'])} requests/s, drain "
             f"{_fmt(p['written_per_s'])} written/s, peak {p['peak_pending']}, empty after {_fmt(p['empty_s'], ' s')} "
             f"({p['n_runs']} runs)" for p in points]
    if capacity:
        notes.append(f"sustained capacity R {capacity['R']}/s = {_fmt(capacity['arrivals_per_s'])} arrivals/s, "
                     f"{_fmt(capacity['requests_per_s'])} requests/s (configured {p0cells0c.CEILING_WRITES_PER_S} "
                     f"writes/s)")
    notes += [f"calibration pair {c['pair']}: bots median/p90 {_fmt(c['bots'][0])}/{_fmt(c['bots'][1])} ms, burst "
              f"token+get {_fmt(c['burst'][0])}/{_fmt(c['burst'][1])} ms" for c in calibration_pairs(rows)]
    key = None if gate is None else f"200 at once: db_ready p95 {_fmt(gate['p95'], ' ms')}, {gate['errors']} errors"
    gates.append(p0results.gate_row("Database", verdict, {"gate_point": gate, "capacity": capacity}, DATABASE_RULE,
                                    notes, p0results.header_of(rows, "dbburst"), None, key))
    if capacity:
        gates.append(p0results.gate_row(
            "Crossings (B10) half B", p0base.NO_VERDICT, {"capacity": capacity}, HALF_B_RULE,
            [f"B5 sustained handoff rate {_fmt(capacity['arrivals_per_s'])}/s ({_fmt(capacity['requests_per_s'])} "
             f"requests/s); a crossing rate up to half of it passes"], p0results.header_of(rows, "dbburst"),
            "sustained", f"B5 term {_fmt(capacity['arrivals_per_s'])} handoffs/s"))
    return gates
