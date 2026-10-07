"""Phase 0 accept-a checks (step 4): parts A and B's acceptance checks over named rig runs, and the instruments'
overhead over interleaved pairs. Pure: every input is an argument. Split out of p0checks (the ~500-line rule), whose
tolerances and window helpers these share.

    p0accept.accept_instrument_checks(runs)   # accept-a part A: [(ok, text)] over plain, r60, r60burn, r30, r30burn
    p0accept.accept_puppet_checks(runs)       # accept-a part B: [(ok, text)] over steered, kin, kin_kwalk, kin_inert
    p0accept.overhead(pairs)                  # the instruments' cost over interleaved pairs, as a CI verdict

runs = {name: run}, each run as p0checks takes it: {"since", "start", "end", "cells": {cell: {"structural":
rigrun.cell_window(...), "p0": p0cells.cell_record(...)}}}.
Unit tests: python -m unittest discover -s Tools/ServerMeshing -p "test_*.py"
"""
import math
import statistics

import p0base
import p0cells
import p0checks
import p0tools

MIN_WINDOWS = 6                 # accept-a: Work ticks windows per 40 s measurement
OVERHEAD_GATE_MS = 0.02         # the instruments may cost at most this much per frame (Decision 20)
BURN_MS, BURN_SLACK_MS = 2.0, 0.1
OTHER_SPREAD_MS = 0.15          # step 2's measured between-Play spread of other frames, replacing the sheet's 5 %
P95_SLACK_MS = 0.055            # one 0.05 ms bin, plus half the Work ticks line's 0.01 ms print resolution
THREAD_SHARE, TICK_SHARE_SLACK = 0.05, 0.02   # thread classes against the process; share of frames with a tick
INSTRUMENT_RUNS = ("plain", "r60", "r60burn", "r30", "r30burn")
RATE_RUNS = (("r60", 60, 0.03, 1 / 3), ("r30", 30, 0.02, 2 / 3))   # (run, fps, step, share of frames with a tick)
BURN_RUNS = (("r60", "r60burn"), ("r30", "r30burn"))
BINS_RUNS = ("r60", "r60burn", "r30", "r30burn")
PUPPET_RUNS = ("steered", "kin", "kin_kwalk", "kin_inert")


def _collector():
    """(checks, add): the accept-a check list and the function that appends (ok, text) to it."""
    checks = []
    return checks, lambda ok, text: checks.append((bool(ok), text))


def _cell(runs, name, cell):
    """(structural, record) of one accept-a run's cell, or None when the run or the cell is missing."""
    entry = ((runs.get(name) or {}).get("cells") or {}).get(cell) or {}
    if entry.get("structural") is None or entry.get("p0") is None:
        return None
    return entry["structural"], entry["p0"]


def _p95_misses(triples):
    """Each window whose own Work bins p95 differs from its Work ticks line's by more than P95_SLACK_MS; an overflow
    rank of a histogram with no logged maximum cannot be compared and is skipped."""
    misses = []
    for t in triples:
        if t["bins"] is None:
            continue
        for hist, fields, max_field in p0cells.PERCENTILES:
            max_us = round(t["ticks"][max_field] * 1000) if max_field else None
            for field, ppm in fields:
                ours = p0tools.percentile_us(t["bins"][hist], ppm, max_us) if ppm == p0cells.P95 else None
                if ours is not None and not p0base.at_most(abs(ours / 1000 - t["ticks"][field]), P95_SLACK_MS):
                    misses.append(f"{p0checks.clock(t['end'])} {hist} {ours / 1000:.2f} vs {t['ticks'][field]:.2f} ms")
    return misses


def _instrument_run(add, name, structural, record):
    triples = record["triples"]
    probes = structural["stats"]["probe"] if structural["stats"] else []
    add(len(triples) >= MIN_WINDOWS, f"{name}: at least {MIN_WINDOWS} Work ticks windows in the measurement "
                                     f"({len(triples)})")
    add(not structural["exceptions"], f"{name}: no exception lines ({structural['exceptions']})")
    add(probes and "Invalid" not in probes, f"{name}: the Work window line parses and the probe is not Invalid "
                                            f"({'/'.join(sorted(set(probes))) or 'no windows'})")
    misses = p0checks.tick_misses(triples)
    add(triples and not misses, f"{name}: 100 ± 1 ticks per 5 s window, no tick cap ({'; '.join(misses) or 'all'})")


def _rates_run(add, name, fps, step, share, record):
    agg, rates = record["ticks"], record["rates"]
    add(len(rates) == 1 and rates[0]["fps"] == fps and abs(rates[0]["step"] - step) < 1e-9 and rates[0]["from_env"],
        f"{name}: one Rates line with fps {fps} and physics {step:g} s "
        f"({', '.join(p0checks.rates_text(line) for line in rates) or 'none'})")
    if agg is None:
        add(False, f"{name}: no Work ticks figures")
        return
    got = agg["t1"] + agg["t2"]
    got = got / agg["frames"] if agg["frames"] else 0.0
    add(p0base.at_most(abs(got - share), TICK_SHARE_SLACK),
        f"{name}: share of frames with a tick {got:.3f} (want {share:.3f} ± {TICK_SHARE_SLACK})")
    add(p0base.at_most(abs(agg["physics_per_s"] - 1.0 / step), p0checks.PHYSICS_SLACK),
        f"{name}: physics steps/s {agg['physics_per_s']:.2f} (want {1.0 / step:.1f} ± 1)")
    passes = [t["ticks"]["aoi_passes"] for t in record["triples"]]
    add(passes and all(p0checks.AOI_PASSES[0] <= p <= p0checks.AOI_PASSES[1] for p in passes),
        f"{name}: AOI passes per window {passes} (want 5 ± 1)")
    add(agg["parts"] >= 0.95 * agg["ticks"],
        f"{name}: the tick is decomposed on at least 95 % of ticks ({agg['parts']} of {agg['ticks']})")
    windows = record["threads"]["windows"] if record["threads"] else []
    clocks = sorted({w["clock"] for w in windows})
    add(windows and clocks == ["cycles"], f"{name}: thread CPU counted in cycles ({', '.join(clocks) or 'no windows'})")
    off = [w for w in windows if w["process"] > 0 and abs(w["total"] - w["process"]) > THREAD_SHARE * w["process"]]
    add(windows and not off, f"{name}: thread classes within ± 5 % of process CPU in {len(windows) - len(off)} of "
                             f"{len(windows)} windows")


def accept_instrument_checks(runs, cell="C"):
    """Accept-a part A as [(ok, text)] over runs {name: run} named plain, r60, r60burn, r30, r30burn (a missing run
    fails): at least 6 windows, no exceptions, probe not Invalid, 100 ± 1 ticks and no tick-cap frame; for r60 and r30 one
    Rates line as asked, the share of frames with a tick (1/3, 2/3 ± 0.02), physics 33.3 / 50 ± 1 steps/s, AOI passes
    5 ± 1, the tick decomposed on 95 % of ticks, thread CPU in cycles and within ± 5 % of the process; each burn run
    +2.000 ± 0.100 ms on tick frames and other frames within 0.15 ms; every Work ticks line of the instrumented runs has
    a Work bins line that agrees with it, each window's bin p95 matching the line's within 0.05 ms."""
    checks, add = _collector()
    for name in INSTRUMENT_RUNS:
        got = _cell(runs, name, cell)
        if got is None:
            add(False, f"{name}: no run to check on {cell}")
        else:
            _instrument_run(add, name, *got)
    for name, fps, step, share in RATE_RUNS:
        got = _cell(runs, name, cell)
        if got is not None:
            _rates_run(add, name, fps, step, share, got[1])
    for base, burn in BURN_RUNS:
        b, u = _cell(runs, base, cell), _cell(runs, burn, cell)
        b, u = (b[1]["ticks"] if b else None), (u[1]["ticks"] if u else None)
        if b is None or u is None:
            add(False, f"{burn}: no Work ticks figures to compare with {base}")
            continue
        rise = u["tick_frame_mean"] - b["tick_frame_mean"]
        add(p0base.at_most(abs(rise - BURN_MS), BURN_SLACK_MS),
            f"{burn}: tick-frame work rose {rise:.3f} ms (want {BURN_MS:.3f} ± {BURN_SLACK_MS:.3f}): "
            f"{b['tick_frame_mean']:.3f} -> {u['tick_frame_mean']:.3f}")
        add(p0base.at_most(abs(u["other_mean"] - b["other_mean"]), OTHER_SPREAD_MS),
            f"{burn}: other frames {b['other_mean']:.3f} -> {u['other_mean']:.3f} ms (within the "
            f"{OTHER_SPREAD_MS} ms between-Play spread)")
    for name in BINS_RUNS:
        got = _cell(runs, name, cell)
        if got is None:
            continue
        record = got[1]
        missing = sum(1 for t in record["triples"] if t["bins"] is None)
        add(record["triples"] and not missing and not record["pairing"],
            f"{name}: every Work ticks line has its Work bins line ({missing} missing, {len(record['pairing'])} unpaired)")
        disagree = [text for t in record["triples"] if t["bins"] for text in p0tools.bins_problems(t)]
        add(not disagree, f"{name}: every Work bins line agrees with its Work ticks line"
                          + (f" ({disagree[0]}; {len(disagree)} in all)" if disagree else ""))
        misses = _p95_misses(record["triples"])
        add(not misses, f"{name}: each window's Work bins p95 matches its Work ticks p95 within 0.05 ms"
                        + (f" ({'; '.join(misses)})" if misses else ""))
    return checks


def accept_puppet_checks(runs, holder="E", owners="C"):
    """Accept-a part B as [(ok, text)] over runs {name: run} named steered, kin, kin_kwalk, kin_inert (a missing run
    fails): no exception on the owners' or the holder's cell; steered logs no Puppets window line; the kinematic runs
    do, the holder keeping at least 10 kinematic puppets with snaps at most max(2, 1 % of the moving samples); moving
    owners give 7 to 11 unwatched poses per puppet per second, a steer kinematic p95 under 1.0 m and moving samples;
    inert owners give none."""
    checks, add = _collector()
    for name in PUPPET_RUNS:
        cells = (runs.get(name) or {}).get("cells") or {}
        if (cells.get(holder) or {}).get("p0") is None:
            add(False, f"{name}: no run to check on {holder}")
            continue
        exceptions = sum(cells[c]["structural"]["exceptions"] for c in (owners, holder)
                         if (cells.get(c) or {}).get("structural"))
        add(not exceptions, f"{name}: no exception lines on {owners} or {holder} ({exceptions})")
        puppets = cells[holder]["p0"]["puppets"]
        if name == "steered":
            add(puppets is None, f"steered: no Puppets window line on {holder} (default output unchanged)")
            continue
        add(puppets is not None, f"{name}: Puppets window lines on {holder} in the measurement "
                                 f"({len(puppets['windows']) if puppets else 0})")
        if puppets is None:
            continue
        kinematic, samples = puppets["kinematic"], puppets["steer_kinematic_samples"]
        add(kinematic >= 10, f"{name}: {holder} holds kinematic puppets ({kinematic:.1f} on average, need 10)")
        add(puppets["snaps"] <= max(2, 0.01 * samples),
            f"{name}: snaps are rare ({puppets['snaps']} for {samples} moving samples)")
        if name == "kin_inert":
            add(samples == 0, f"kin_inert: standing owners give no moving samples ({samples})")
            continue
        per = puppets["unobserved_poses_per_s"] / kinematic if kinematic else 0.0
        add(7 <= per <= 11, f"{name}: unwatched kinematic puppets posed at the snapshot rate ({per:.1f} per puppet "
                            f"per s, want 7 to 11)")
        add(puppets["steer_kinematic_p95"] < 1.0,
            f"{name}: steer kinematic p95 under 1.0 m (worst window {puppets['steer_kinematic_p95']:.2f} m)")
        add(samples > 0, f"{name}: moving kinematic puppets were sampled ({samples})")
    return checks


def overhead(pairs, gate_ms=OVERHEAD_GATE_MS):
    """The instruments' cost from interleaved pairs [(plain, instrumented)] of C's work_ms_per_frame: the paired
    differences' mean, se, n, t (n - 1 df), interval lo..hi and p0base.ci_verdict against gate_ms (Decision 20: only
    NO-GO fails accept-a). Raises p0base.ModelError with fewer than 2 pairs."""
    diffs = [instrumented - plain for plain, instrumented in pairs]
    if len(diffs) < 2:
        raise p0base.ModelError(f"the instruments' overhead needs at least 2 pairs; got {len(diffs)}")
    n = len(diffs)
    mean = math.fsum(diffs) / n
    se = statistics.stdev(diffs) / math.sqrt(n)
    t = p0base.t975(n - 1)
    lo, hi = mean - t * se, mean + t * se
    return {"mean": mean, "se": se, "n": n, "t": t, "lo": lo, "hi": hi, "verdict": p0base.ci_verdict(lo, hi, gate_ms)}
