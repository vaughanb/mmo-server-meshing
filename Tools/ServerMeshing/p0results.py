"""Phase 0 results (step 4): the mesh-p0/1 JSONL rows (the build sheet's §5 plus the step 4 contract's additive fields),
their reader and writer, canary drift and summaries (p0markdown prints them). Pure, except read_rows and write_rows:
thin wrappers that read and append files around the pure parse_rows and format_rows.

    p0results.run_row(spec, run, context, problems, excluded_reason)  # one rig run as a row, accepted or not
    p0results.write_rows(path, rows) / read_rows(paths)  # append / read JSONL; SchemaError names file:line
    p0results.apply_drift(rows)                   # canary drift: rows before a drifted canary become "drift"
    p0results.summary_rows(rows)                  # one per (game, family, case, arm, point, host, cell), accepted
    p0results.machine(row)                        # (build, host): every baseline, drift, resume and pool keys on it

Row types: run (a row without "row" is one: §5 has no row field), summary, fit, gate, prediction; each must hold the
fields its type REQUIRES (null allowed), and other fields are kept. File names: see file_name.
Unit tests: python -m unittest discover -s Tools/ServerMeshing -p "test_*.py"
"""
import datetime
import json
import math
import os
import re
import statistics

import p0base
import p0cells
import p0cells0b

SCHEMA = "mesh-p0/1"
ROW_TYPES = ("run", "summary", "fit", "gate", "prediction")
FAMILIES_0A = ("canary", "accept", "rates", "presence", "density", "floor", "abgeo", "fit")
FAMILIES_8A = ("observed", "fanout", "targets", "syncrpc", "packets", "prewarm")   # stage 0b, step 8a (decision 2)
FAMILIES_8B = ("accept-b", "calibrate", "crossings", "jfull")                    # stage 0b, step 8b (decision 10)
FAMILIES_0B = frozenset(FAMILIES_8A + FAMILIES_8B)
# A family's game when its spec names none (p0plans.GAMES; a spec's own game wins: observed S and ship arms are space).
FAMILY_GAME = {"canary": "neutral", "accept": "neutral", "rates": "neutral", "floor": "neutral",
               "presence": "fantasy", "density": "fantasy", "abgeo": "fantasy", "observed": "fantasy",
               "fanout": "fantasy", "targets": "fantasy", "syncrpc": "space", "packets": "fantasy",
               "prewarm": "fantasy", **{family: "fantasy" for family in FAMILIES_8B}}
STAGE = {**{family: "0a" for family in FAMILIES_0A}, **{family: "0b" for family in sorted(FAMILIES_0B)}}
GEOGRAPHIC = ("presence", "density", "abgeo")
PREDICTION_FAMILY = "abgeo-prediction"   # rig_fantasy_abgeo-prediction_<stamp>.jsonl
TEAM, STAGE_0A, TICK_HZ, CANARY_CELL = "rig", "0a", 20, "C"
CONTEXT_FIELDS = ("host", "cpu", "cores", "logical", "os", "power_plan", "build", "kit_patches")
THIRD_REPEAT_FIELDS = ("replaces_repeat", "third_reason")   # a third repeat's spec fields its run row carries

HEADER_FIELDS = ("schema", "team", "game", "model", "family", "case", "arm", "stage", "repeat", "seed", "order",
                 "started_utc", "host", "cpu", "cores", "os", "power_plan", "build", "kit_patches", "rates", "capped",
                 "accepted", "problems", "excluded_reason")
LOAD_FIELDS = ("kind", "owned", "puppets", "awake_monsters", "clients", "v_move_mean", "v_move_p95", "density",
               "visible_range_m", "band_m", "payload_B", "budget_kBps", "scale")
HEADER_SUBSET = ("schema", "row", "team", "game", "family", "stage", "build", "cpu")
HEAD_KEYS = HEADER_SUBSET[2:] + ("host",)   # host is carried, not REQUIRED (older report rows have none)
REQUIRED = {"run": HEADER_FIELDS + LOAD_FIELDS + ("metrics",),
            "summary": HEADER_SUBSET + ("case", "arm", "point", "cell", "n"),
            "fit": HEADER_SUBSET + ("fit_id", "name", "value", "lo", "hi"),
            "gate": HEADER_SUBSET + ("name", "verdict"),
            "prediction": HEADER_SUBSET + ("point", "ratio", "inputs_sha")}

# §5's fixed vocabulary (labelled families as thread_ms_per_s{main}), then the step 4 contract's additions.
METRIC_NAMES = frozenset((
    "work_ms_per_s", "work_ms_per_frame", "tick_frame_ms", "nonaoi_tick_frame_ms", "aoi_tick_ms", "other_frame_ms",
    "tick_send_ms", "kit_sync_ms", "kit_aoi_ms", "kit_move_ms", "multi_tick_per_1000", "sat_pct",
    "c_pair_ns", "residual_ns", "pkt_main_us", "pkt_net_us", "sync_scan_ns", "rpc_send_ns", "upload_in_us",
    "downlink_wire_kBps_per_client", "entity_state_kBps_per_client", "datagrams_per_s_per_client", "rate_hz_by_tier",
    "reliable_records_per_s", "thread_ms_per_s", "process_cores", "mem_MB", "link_kBps_in", "link_kBps_out",
    "latency_ms", "frames_per_s", "physics_steps_per_s", "ticks_per_s", "puppet_count", "puppet_steer_m",
    "puppet_snaps", "puppet_unobserved_poses_per_s", "puppet_fallbacks")) | p0cells0b.METRIC_NAMES_0B

OWNER_KINDS = ("steered", "steered_nofight", "kinematic", "inert", "monster")
PUPPET_KINDS = ("steered", "kinematic", "inert", "monster", "monster_steered", "monster_kinematic")
# Model regressors and derived values -> §5's fit-row names (rate_*, g_*, k_hold_*, k_band_* are the contract's; of
# block K's, rate_I is the interaction, rate_kind_* a kind arm over idle and rate_entity_* that split per entity).
FIT_NAMES = {"c0": "c0", "links": "lambda", "Q": "q", "N2": "q", "G_player": "g_player",
             "per_frame": "rate_F", "per_step": "rate_Ph", "per_second": "rate_C", "other_per_second": "rate_S",
             "interaction": "rate_I",
             "kind_per_frame": "rate_kind_F", "kind_per_step": "rate_kind_Ph", "kind_per_second": "rate_kind_C",
             "entity_per_frame": "rate_entity_F", "entity_per_step": "rate_entity_Ph",
             "entity_per_second": "rate_entity_C",
             **{f"O_{kind}": f"a_{kind}" for kind in OWNER_KINDS},
             **{f"G_{kind}": f"g_{kind}" for kind in OWNER_KINDS},
             **{f"U_{kind}": f"e_{kind}" for kind in PUPPET_KINDS},
             **{f"k_hold_{kind}": f"k_hold_{kind}" for kind in PUPPET_KINDS},
             **{f"k_band_{kind}": f"k_band_{kind}" for kind in PUPPET_KINDS}}
K_ALIASES = {f"k_hold_{kind}": f"k_{kind}" for kind in PUPPET_KINDS}   # §5 calls k_hold_{kind} k_{kind}

POWER_PLAN = re.compile(r"GUID:\s*[0-9A-Fa-f-]+\s+\((?P<name>[^()]+)\)")
KIT_PATCH = re.compile(r"MESH-EDIT: (CE-\d+)\b")


class SchemaError(ValueError):
    """SchemaError is a row that is not a readable mesh-p0/1 row; its message names the file and line."""


def row_type(row):
    """A row's type: its "row" field, or "run" when it has none."""
    return row.get("row", "run")


def rows_of_type(rows, kind):
    """The rows of one type, in order (the report fits only run rows; prediction rows feed only G-GEO (b))."""
    return [row for row in rows if row_type(row) == kind]


def metric_base(name):
    """A metric name without its brace label: "thread_ms_per_s{main}" -> "thread_ms_per_s"."""
    return name.split("{", 1)[0]


def metric_value(row, name, stat="mean", cell=None):
    """A run row's metric value (full name, stat, and cell when given), or None. Raises ValueError when several
    metrics match (name the cell)."""
    found = [m for m in row.get("metrics") or []
             if m["name"] == name and m["stat"] == stat and (cell is None or m.get("cell") == cell)]
    if len(found) > 1:
        raise ValueError(f"metric {name} {stat} appears {len(found)} times (cells "
                         f"{', '.join(str(m.get('cell')) for m in found)}); name one cell")
    return found[0]["value"] if found else None


def section5_names(name):
    """The §5 fit-row names of a model coefficient or derived value: FIT_NAMES[name], then k_{kind} for
    k_hold_{kind} (that row is written twice). A §5 name maps to itself; ValueError for a name FIT_NAMES lacks."""
    if name in FIT_NAMES:
        primary = FIT_NAMES[name]
    elif name in FIT_NAMES.values() or name in K_ALIASES.values():
        primary = name
    else:
        raise ValueError(f"fit name '{name}' has no §5 name in p0results.FIT_NAMES")
    return [primary, K_ALIASES[primary]] if primary in K_ALIASES else [primary]


def _iso(stamp):
    return stamp.isoformat() if isinstance(stamp, datetime.datetime) else stamp


def machine(row):
    """A row's machine key (build, host): A and B may share a CPU model, so the machine is its host (decision 1)."""
    return row.get("build"), row.get("host")


def cpu_problems(rows):
    """'host X ran on 2 CPUs (A and B)' per host whose rows state more than one cpu (a row without a cpu is not
    counted): cpu must be constant per host, or the host key would pool two machines."""
    cpus = {}
    for row in (row for row in rows if row.get("cpu") is not None):
        cpus.setdefault(row.get("host"), set()).add(str(row["cpu"]))
    return [f"host {host} ran on {len(found)} CPUs ({' and '.join(sorted(found))}): give each machine its own host "
            f"name" for host, found in cpus.items() if len(found) > 1]


def run_row(spec, run, context, problems, excluded_reason):
    """One rig run as a run row, accepted or not (§5's fields plus the contract's additions). spec is a p0plans
    RunSpec; run is {"since", "start", "end", "cells": {cell: {"structural", "p0": p0cells.cell_record, and for a stage
    0b family "p0b": p0cells0b.cell_record_0b}}} (None or partial for a failed run; a naive start is local time);
    context is rigrun.host_context() (peer_host and peer_cpu, the second machine of step 8b, None when absent). owned
    and puppets are Work window means summed over the read cells, keyed by MESH_LOAD_MOTION / MESH_PUPPET_MOTION;
    density is m² per spawned character; stage 0b rows add p0cells0b's metrics and load fields, and step 8b's run-level
    metrics (handoffs; run["peer"], p0cells0b.peer_record) and fields (p0cells0b.run_fields_0b). A third repeat's
    replaces_repeat and third_reason, and a stepped run's stepped, are copied from the spec when it sets them.
    ValueError for a family without a game, a bad MESH_RATES or MESH_BAND, or an unknown metric."""
    family = spec["family"]
    game = spec.get("game") or FAMILY_GAME.get(family)
    if game is None:
        raise ValueError(f"family '{family}' has no game: give the spec a game or use one of {', '.join(FAMILY_GAME)}")
    rates = p0cells.stated_rates(spec.get("rates"))
    env, context, run = dict(spec.get("env") or {}), context or {}, run or {}
    cells = run.get("cells") or {}
    records = {cid: cell["p0"] for cid, cell in cells.items() if cell.get("p0") is not None}
    records_0b = {cid: cell["p0b"] for cid, cell in cells.items() if cell.get("p0b") is not None}
    metrics = [m for cid, record in records.items() for m in p0cells.cell_metrics(record, rates["fps"], cid)]
    metrics += [m for record in records_0b.values() for m in p0cells0b.cell_metrics_0b(record)]
    if family in FAMILIES_0B and run.get("start") and run.get("end"):
        metrics += p0cells0b.run_metrics_0b(spec, records_0b, run.get("peer"),
                                            (run["end"] - run["start"]).total_seconds())
    unknown = sorted({metric_base(m["name"]) for m in metrics} - METRIC_NAMES)
    if unknown:
        raise ValueError(f"metric names {', '.join(unknown)} are not in p0results.METRIC_NAMES")
    start, problems = run.get("start"), list(problems)
    row = {"schema": SCHEMA, "row": "run", "team": TEAM, "game": game,
           "model": "geographic" if family in GEOGRAPHIC else "none", "family": family, "case": spec["case"],
           "arm": spec["arm"], "stage": STAGE.get(family, STAGE_0A), "repeat": spec["repeat"], "seed": spec["seed"],
           "order": spec["order"], "plan_key": spec.get("key"), "point": spec.get("point") or {},
           "started_utc": None if start is None else start.astimezone(datetime.timezone.utc).isoformat(),
           "window_local": None if start is None else {key: _iso(run.get(key)) for key in ("since", "start", "end")}}
    row.update({key: context.get(key) for key in CONTEXT_FIELDS + ("peer_host", "peer_cpu")})
    row.update({"rates": {"fps": rates["fps"], "fixed_step_s": rates["fixed_step_s"], "tick_hz": TICK_HZ},
                "capped": rates["capped"] and not spec.get("uncapped"), "env": env, "layout": spec.get("layout"),
                "cells_started": list(spec.get("cells_started") or []),
                "accepted": not problems and excluded_reason is None, "problems": problems,
                "excluded_reason": excluded_reason,
                "dropped_windows": {cid: [_iso(end) for end in record["dropped"]]
                                    for cid, record in records.items() if record["dropped"]}})
    row.update(p0cells.load_fields(spec, env, records))
    if family in FAMILIES_0B:
        row.update(p0cells0b.load_fields_0b(spec, records_0b, records))
        row.update(p0cells0b.run_fields_0b(spec, records_0b, context))
    row["metrics"] = metrics
    row["windows"] = {cid: p0cells.window_rows(record) for cid, record in records.items()}
    row.update({field: spec[field] for field in THIRD_REPEAT_FIELDS + ("stepped",) if spec.get(field) is not None})
    return row


def _head(kind, header, defaults=None):
    head = {"schema": SCHEMA, "row": kind, "team": TEAM, "game": None, "family": None, "stage": STAGE_0A,
            "build": None, "cpu": None, "host": None}
    for source in (defaults or {}, header or {}):
        head.update({key: value for key, value in source.items() if key in HEAD_KEYS})
    return head


def header_of(rows, family=None):
    """The header subset (team, game, family, stage, build, cpu) and host the rows share, for the rows made from them:
    each field's one value, or its values joined with "+" (a bridged report pools two builds); family, when given,
    replaces theirs. Raises ValueError for no rows and for rows of two hosts (two machines never pool)."""
    if not rows:
        raise ValueError("a header needs at least one row")
    hosts = sorted({str(row.get("host")) for row in rows})
    if len(hosts) > 1:
        raise ValueError(f"rows from {len(hosts)} hosts ({', '.join(hosts)}) are never pooled: one host at a time")
    head = {}
    for key in HEAD_KEYS:
        values = {row.get(key) for row in rows}
        head[key] = values.pop() if len(values) == 1 else "+".join(sorted(str(value) for value in values))
    if family is not None:
        head["family"] = family
    return head


def rates_label(row):
    """A run row's rates as "60/0.03" (with " uncapped" when not capped), or None without stated rates."""
    rates = row.get("rates") or {}
    if rates.get("fps") is None or rates.get("fixed_step_s") is None:
        return None
    label = f"{rates['fps']:g}/{rates['fixed_step_s']:g}"
    return label if row.get("capped", True) else f"{label} uncapped"


def _median(values):
    values = [value for value in values if value is not None]
    return statistics.median(values) if values else None


def _mean_load(run, cell, prefix):
    windows = (run.get("windows") or {}).get(cell) or []
    if not windows:
        return None
    return math.fsum(w[f"{prefix}_players"] + w[f"{prefix}_monsters"] for w in windows) / len(windows)


def summary_row(runs, cell):
    """One summary row over the accepted run rows of one (game, family, case, arm, point) for one cell: n, the median,
    min and max of work_ms_per_s, the median non-AOI tick-frame and tick-send p95, the median owned and puppet counts
    (characters and monsters), the rates label and the plan keys. ValueError for no runs or a run without the cell's
    work_ms_per_s."""
    if not runs:
        raise ValueError(f"a summary of cell {cell} needs at least one run row")
    work = [metric_value(run, "work_ms_per_s", "mean", cell) for run in runs]
    for run, value in zip(runs, work):
        if value is None:
            raise ValueError(f"run {run.get('plan_key')} has no work_ms_per_s on cell {cell}")
    first = runs[0]
    row = _head("summary", header_of(runs))
    row.update({"case": first.get("case"), "arm": first.get("arm"), "point": first.get("point") or {}, "cell": cell,
                "n": len(runs), "kind": first.get("kind"), "rates": rates_label(first),
                "work_ms_per_s_median": statistics.median(work), "work_ms_per_s_min": min(work),
                "work_ms_per_s_max": max(work),
                "nonaoi_tick_frame_p95_ms_median": _median(metric_value(r, "nonaoi_tick_frame_ms", "p95", cell)
                                                           for r in runs),
                "tick_send_p95_ms_median": _median(metric_value(r, "tick_send_ms", "p95", cell) for r in runs),
                "owned": _median(_mean_load(r, cell, "owned") for r in runs),
                "puppets": _median(_mean_load(r, cell, "puppet") for r in runs),
                "clients": _median(sum((r.get("clients") or {}).get(k) or 0
                                       for k in ("phantom_virtual", "phantom_sink")) for r in runs) or 0,
                "plan_keys": [run.get("plan_key") for run in runs]})
    return row


def summary_rows(rows):
    """One summary row per (game, family, case, arm, point, rates, host, cell) over the accepted run rows, in first-seen
    order; a run joins the group of each cell it has work_ms_per_s for. Rates are in the key so two rate sets are
    never pooled (a point normally names its rates already), and the host so two machines never are."""
    groups = {}
    for row in rows:
        if row_type(row) != "run" or not row.get("accepted"):
            continue
        key = (row.get("game"), row.get("family"), row.get("case"), row.get("arm"),
               json.dumps(row.get("point") or {}, sort_keys=True), rates_label(row), row.get("host"))
        for cell in dict.fromkeys(m.get("cell") for m in row.get("metrics") or []
                                  if m["name"] == "work_ms_per_s" and m["stat"] == "mean"):
            groups.setdefault(key + (cell,), []).append(row)
    return [summary_row(runs, key[-1]) for key, runs in groups.items()]


def fit_row(fit_id, name, ci, regressors, n, method="ols + jackknife over repeats", reference=None, header=None,
            notes=None):
    """A fit row: name as §5 names it (section5_names(name)[0]; write a k_hold_{kind} again under k_{kind}), ci's
    value, se, lo, hi and t (a p0model jackknife entry), the regressors, n rows, method, reference (the owner cost a k
    divides by, "a_steered") and notes. header gives game, build and cpu (header_of the fitted rows); family is
    always "fit" (§5's family for fit rows; fit_id names the fit)."""
    row = _head("fit", header)
    row.update({"family": "fit", "fit_id": fit_id, "name": section5_names(name)[0], "value": ci["value"], "se": ci.get("se"),
                "lo": ci["lo"], "hi": ci["hi"], "t": ci.get("t"), "regressors": list(regressors), "n": n,
                "method": method, "reference": reference, "notes": list(notes or [])})
    return row


def gate_row(name, verdict, inputs, rule, notes, header=None, case=None, key_metric=None):
    """A gate row: name, verdict, inputs, rule (thresholds in ms/s and ms/frame at the stated rate, e.g.
    p0base.threshold_text) and notes; header gives game, family, build and cpu; case limits the summary rows the
    Markdown table shows it on (None: all of its family), key_metric is that table's Key metric text."""
    row = _head("gate", header)
    row.update({"name": name, "verdict": verdict, "inputs": inputs, "rule": rule, "notes": notes, "case": case,
                "key_metric": key_metric})
    return row


def prediction_row(point, ratio, inputs_sha, created_utc, header=None):
    """A frozen prediction row (report --predict abgeo): point, ratio, inputs_sha, created_utc (a datetime becomes
    ISO text); game fantasy and family abgeo unless header says otherwise."""
    row = _head("prediction", header, {"game": "fantasy", "family": "abgeo"})
    row.update({"point": point, "ratio": ratio, "inputs_sha": inputs_sha, "created_utc": _iso(created_utc)})
    return row


def validate_row(row, where):
    """Refuses with SchemaError, prefixed with where ("rows.jsonl:12"), a row that is not a JSON object, whose schema
    is not mesh-p0/1, whose row type is unknown, or that lacks a field its type REQUIRES (null is allowed)."""
    if not isinstance(row, dict):
        raise SchemaError(f"{where}: a row is a JSON object, not {type(row).__name__}")
    if "schema" not in row:
        raise SchemaError(f"{where}: no schema field (expected {SCHEMA})")
    if row["schema"] != SCHEMA:
        raise SchemaError(f"{where}: schema {row['schema']!r} is not {SCHEMA}")
    kind = row_type(row)
    if kind not in ROW_TYPES:
        raise SchemaError(f"{where}: unknown row type {kind!r} (expected one of {', '.join(ROW_TYPES)})")
    missing = [field for field in REQUIRED[kind] if field not in row]
    if missing:
        raise SchemaError(f"{where}: {kind} row is missing {', '.join(missing)}")


def _refuse_constant(name):
    raise ValueError(f"{name} is not JSON")


def parse_rows(name, text):
    """The rows of one JSONL text (name: its file name, for messages); blank lines are skipped. SchemaError naming
    name:line for invalid JSON (NaN and Infinity included) and every validate_row refusal."""
    rows = []
    for number, line in enumerate(text.split("\n"), 1):
        if not line.strip():
            continue
        where = f"{name}:{number}"
        try:
            row = json.loads(line, parse_constant=_refuse_constant)
        except ValueError as err:
            raise SchemaError(f"{where}: invalid JSON ({err})") from err
        validate_row(row, where)
        rows.append(row)
    return rows


def format_rows(rows, name="rows"):
    """The rows as JSONL text, one compact object per line. Each row is validated first (named name:index); NaN,
    infinities and values JSON cannot hold raise SchemaError, so nothing unreadable is ever written."""
    lines = []
    for number, row in enumerate(rows, 1):
        where = f"{name}:{number}"
        validate_row(row, where)
        try:
            lines.append(json.dumps(row, separators=(",", ":"), ensure_ascii=False, allow_nan=False))
        except (TypeError, ValueError) as err:
            raise SchemaError(f"{where}: cannot be written as JSON ({err})") from err
    return "".join(f"{line}\n" for line in lines)


def write_rows(path, rows):
    """Appends the rows to the JSONL file at path (format_rows first, so a bad row writes nothing; creates the
    folder). One of the two functions here that touch files."""
    text = format_rows(rows, os.path.basename(path))
    if os.path.dirname(path):
        os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "a", encoding="utf-8", newline="\n") as f:
        f.write(text)


def read_rows(paths):
    """The rows of every JSONL file in order (parse_rows, messages naming each file's base name and line); a single
    path string is one file. One of the two functions here that touch files."""
    rows = []
    for path in [paths] if isinstance(paths, str) else paths:
        with open(path, encoding="utf-8-sig") as f:
            rows.extend(parse_rows(os.path.basename(path), f.read()))
    return rows


def file_name(game, family, stamp, team=TEAM):
    """§5's JSONL file name <team>_<game>_<family>_<stamp>.jsonl: run rows rig_<game>_<family>_<stamp>.jsonl
    (FAMILY_GAME gives the game: rig_neutral_canary_...), the report's rig_<game>_fit_<stamp>.jsonl, the frozen
    predictions rig_fantasy_abgeo-prediction_<stamp>.jsonl (family PREDICTION_FAMILY)."""
    return f"{team}_{game}_{family}_{stamp}.jsonl"


def canary_work(row):
    """A canary row's drift figure: C's Work ticks work ms/s (metric work_ms_per_s, mean), or None."""
    return metric_value(row, "work_ms_per_s", "mean", CANARY_CELL)


def _canaries(rows, arm, key=None):
    return [row for row in rows if row_type(row) == "run" and row.get("family") == "canary" and row.get("arm") == arm
            and row.get("accepted") and (key is None or machine(row) == key)]


def canary_baselines(rows):
    """{(build, host): the mean C work ms/s of its accepted baseline canary rows}; a machine without one is left
    out."""
    values = {}
    for row in _canaries(rows, "baseline"):
        work = canary_work(row)
        if work is not None:
            values.setdefault(machine(row), []).append(work)
    return {key: math.fsum(works) / len(works) for key, works in values.items()}


def _share(work, baseline):
    if baseline <= 0:
        raise p0base.ModelError(f"a canary baseline must be positive; got {baseline:g} ms/s")
    return abs(work - baseline) / baseline


def _with_problem(row, text):
    row["problems"] = list(row.get("problems") or []) + [text]


def apply_drift(rows, threshold=0.05):
    """A new list of the rows (changed ones copied) with canary drift applied. Each accepted canary check row gets
    "drift": whether |w - reference|/reference is above its threshold, w its C work ms/s; the reference and threshold
    are the row's drift_reference when it has one (p0blocks: the block's opening canary at 5 %, or the baseline at
    START_DRIFT for that opening canary), else the mean accepted baseline of its machine, (build, host), at threshold;
    None with a note when either is missing. Every other accepted run row
    whose next accepted check canary on its machine (by window_local.end) drifted becomes accepted false,
    excluded_reason "drift", with a problems entry; an unjudged next canary adds a note; no later canary adds "no
    canary after this run" (still accepted). Rows without window_local are left alone."""
    out = [dict(row) for row in rows]
    baselines = canary_baselines(out)
    checks = []
    for row in _canaries(out, "check"):
        given = row.get("drift_reference") or {}
        baseline, work = given.get("work", baselines.get(machine(row))), canary_work(row)
        limit, against = given.get("threshold", threshold), given.get("from", "its build's baseline")
        at = (row.get("window_local") or {}).get("end")
        if baseline is None or work is None:
            what = (f"no accepted baseline canary on build {row.get('build')} host {row.get('host')}"
                    if baseline is None else f"no {CANARY_CELL} work_ms_per_s metric")
            row["drift"] = None
            _with_problem(row, f"drift not judged: {what}")
            text = f"the canary at {at} after this run could not be judged: {what}"
        else:
            share = _share(work, baseline)
            row["drift"] = p0base.above(share, limit)
            text = (f"drift: the canary at {at} read {work:.1f} ms/s on {CANARY_CELL} against {against} "
                    f"{baseline:.1f} ms/s ({share:.1%}, above {limit:.0%})")
        if at is not None:
            checks.append((datetime.datetime.fromisoformat(at), row, text))
    checks.sort(key=lambda check: check[0])
    for row in out:
        if (row_type(row) != "run" or row.get("family") == "canary" or not row.get("accepted")
                or not row.get("window_local")):
            continue
        end = datetime.datetime.fromisoformat(row["window_local"]["end"])
        later = next((check for check in checks if machine(check[1]) == machine(row) and check[0] > end), None)
        if later is None:
            _with_problem(row, "no canary after this run")
        elif later[1]["drift"]:
            row.update({"accepted": False, "excluded_reason": "drift"})
            _with_problem(row, later[2])
        elif later[1]["drift"] is None:
            _with_problem(row, later[2])
    return out


def bridge_ok(rows, old_build, new_build, host, threshold=0.05):
    """Whether new_build's rows may be pooled with old_build's on one host: every accepted canary check of the new
    build there lies within the threshold of the old build's baseline there. ModelError when the old build has no
    baseline or the new build no accepted check canary with a C work metric on the host (nothing to judge)."""
    baseline = canary_baselines(rows).get((old_build, host))
    if baseline is None:
        raise p0base.ModelError(f"build {old_build} has no accepted baseline canary on host {host} to bridge from")
    works = [w for w in (canary_work(row) for row in _canaries(rows, "check", (new_build, host))) if w is not None]
    if not works:
        raise p0base.ModelError(f"build {new_build} has no accepted canary check on host {host} to bridge with")
    return not any(p0base.above(_share(work, baseline), threshold) for work in works)


def parse_power_plan(text):
    """The active power plan's name ("Balanced") from `powercfg /getactivescheme` output, else None."""
    m = POWER_PLAN.search(text or "")
    return m["name"].strip() if m else None


def kit_patches(texts, expected=("CE-6", "CE-7", "CE-8", "CE-9", "CE-11", "CE-12")):
    """{patch id: "present" | "absent"} for the expected kit patches, from the texts of the sources carrying
    "MESH-EDIT: CE-n" markers; a marked patch outside expected follows them as present."""
    found = set()
    for text in texts:
        found.update(KIT_PATCH.findall(text))
    patches = {patch: "present" if patch in found else "absent" for patch in expected}
    patches.update({patch: "present" for patch in sorted(found - set(expected))})
    return patches
