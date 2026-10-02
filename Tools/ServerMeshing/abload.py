#!/usr/bin/env python3
"""Slice 1 measurements for dynamic cells (DESIGN.md 0.71): is the work probe right, and does cutting a cell help?

    python Tools/ServerMeshing/abload.py probe [--burns 0,4,8,12,24] [--cell C]
    python Tools/ServerMeshing/abload.py ab [--players 150,250] [--load spread | --load X,Z,R] [--monsters off,all]
                                            [--layouts coarse,halves,grid] [--repeats 3] [--seed 1] [--band M]
                                            [--bands 20,40,80,150] [--puppets steered|kinematic] [--rates 60/0.03]
                                            [--instrument] [--order point-major|repeat-major] [--jsonl PATH]
                                            [--case NAME] [--dry-run]

probe   Burns N ms of main-thread work per frame on one cell (MESH_PROBE_BURN_MS=cell:N), running alone and idle, and
        checks that the probe's work mean rises by N (within 5% + 0.3 ms, every window Valid) while the frame fits the
        budget, and that frames stop waiting (saturation >= 90%) once it cannot. A step within 10% of the budget either
        way is reported but not graded: neither check can place the bracket there.

ab      The same synthetic load on the rig's 600 x 600 m ground cut several ways, with no dynamic code involved:
          coarse   one 600 m cell                           (MESH_LAYOUT_OVERRIDE)
          halves   two 300 x 600 m cells                     (MESH_LAYOUT_OVERRIDE)
          grid     the registry's 3 x 3 of 200 m             (no override)
          strips4  four 150 x 600 m strips W, C, E, NE        (MESH_LAYOUT_OVERRIDE)
        The go/no-go (Slice 1) is the busiest cell's mean work per frame, halves over coarse, for spread load. Each
        (players, monsters) point is GO when every paired repeat is at most 0.75, NO-GO when every one is above it,
        MARGINAL when they straddle it, NO VERDICT when it has too few usable pairs. The go/no-go is GO only when every
        point is GO, NO-GO when any point is NO-GO, MARGINAL when the rest are GO or MARGINAL, else NO VERDICT.
        strips4 over halves gets its own verdict per point and overall by the same rule (Phase 0 G-GEO (a)).
        --band sets the ghost band and the visible range together (MESH_BAND), to model a game whose visible range is
        small next to its cells: a split's benefit follows band over cell width (peak child ~ 1/2 + k*band/width).

Phase 0 flags (step 4); without them an ab report is 0.71's text, line for line:
  --bands B,B,...   the bands to run inside every (players, monsters) point, band innermost; MESH_BAND is set on
                    every run, 80 included, so each cell's MESH_BAND line asserts it; not with --band
  --puppets P       MESH_PUPPET_MOTION on every run: steered or kinematic
  --rates F/S       MESH_RATES on every run; an uncapped u... value is refused (every run would fail the uncapped
                    guard: uncapped measurement is p0load's)
  --instrument      MESH_AOI_TIMING=1, MESH_WORK_BINS=1 and MESH_THREAD_CPU=1 on every run
  --order O         point-major: each point's repeats back to back (0.71). repeat-major: repeat by repeat, the points
                    reversed on even repeats (the Phase 0 run order; what p0load abgeo passes)
  --jsonl PATH      append one mesh-p0/1 run row per run to PATH, usable, discarded or failed, graded by p0checks
                    (p0load report reads them); a row that is not accepted is noted under its run
  --case NAME       the rows' case (default ab)
  --dry-run         print each run's plan and environment, with predicted loads (label E); no editor, database,
                    PowerShell or report file
  Any of the first six (other than its default) adds a '=== phase 0: ...' line under the header; abregrade skips
  such reports, whose rows carry their windows.

What every ab run holds constant, and checks before it is used:
  - Load: every cell draws the same stream of world points from MESH_LOAD_SEED (seed + repeat) and keeps the first
    ones in its own rectangle up to its planned count (MESH_LOAD_PLAN, spread in proportion to area or crowded into a
    square with --load X,Z,R). A player id, where a layout has it, stands at the same place in every layout of a
    repeat; which ids a layout has differs by a few percent, because each cell fills its own quota.
  - Monsters (--monsters): off (none), edge (the scene's one active area, 4 monsters), all (every spawn area, about
    294 monsters, each on the cell that owns it: MESH_MONSTER_AREAS=all).
  - Only the layout's cells run (MESH_SPAWN_CELLS): map 2's cell C2 would otherwise neighbour the east cell. No
    client logs in, and every building of the test map is cleared first.
  - A run counts only if each cell started once with the layout's rectangle, linked with every layout cell it
    touches (a cut whose cells never linked carries no puppets, so it would look cheap), spawned its planned players,
    logged no exception, and logged work windows covering at least 80% of the measurement span, none Invalid.
    Figures are frame-weighted over those 'Work window' lines. Leftover rig processes are killed before every run.
  - Order: repeats run the layouts in rotated order (a Latin square), so drift over the matrix does not line up
    with a layout, and each repeat's halves/coarse ratio compares runs made minutes apart.
  - A cell past 20% frames without a wait, or 95% of its budget, is saturated: its work per frame no longer measures
    its load at the tick rate. A pair whose busiest cell is saturated on either side is left out of the verdict and
    says so.
Not measured: synthetic players never cross a cut (they are never handed off), so the handoff cost a cut adds is
excluded; the report prints the crossing rate a cut would see.

Everything printed is also written to Logs/abload_<command>_<time>.txt, with the CPU it ran on (not in a dry run).
"""
import argparse
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import abgrade
import p0cells
import p0checks
import p0dryrun
import p0results
import regress as r
import rigrun
from rigrun import Report, run_window  # noqa: F401 -- re-exported: accept_step2.py / accept_step3.py use abload.<name>


def probe_command(args, rep):
    burns = [float(b) for b in args.burns.split(",") if b.strip()]
    if not burns or burns[0] != 0:
        raise SystemExit("--burns must start with 0: the unburnt run is the baseline every other step is measured from")
    zones = {z.lower(): (z, m) for z, m in r.ZONES}
    if args.cell.lower() not in zones:
        raise SystemExit(f"unknown cell {args.cell}; known: {', '.join(z for z, _ in r.ZONES)}")
    cell, map_id = zones[args.cell.lower()]
    rep.note(f"=== probe acceptance on cell {cell}, running alone (burn = busy-wait per frame, capped); "
             f"warmup {args.warmup}s measure {args.measure}s")
    rep.note(f"{'burn ms':<8} {'work ms':<8} {'delta':<7} {'wait ms':<8} {'sat %':<6} {'windows':<8} {'probe':<14} verdict")
    baseline, budget, all_ok, linear, saturating = None, 0.0, True, 0, 0
    for burn in burns:
        env = {"MESH_PROBE_BURN_MS": f"{cell}:{burn:g}" if burn > 0 else "", "MESH_SPAWN_CELLS": cell,
               "MESH_NO_MONSTERS": "1"}
        try:
            since, start, end = rigrun.run_window(env, args.warmup, args.measure, rep)
            res = rigrun.cell_window(cell, map_id, since, start, end)
        except Exception as ex:  # a failed run is a failed step; the table still gets written
            rep.note(f"{burn:<8g} (run failed: {ex})")
            all_ok = False
            if burn == 0:
                break   # no baseline: nothing else can be graded
            continue
        problems = rigrun.run_problems([(cell, None)], {}, {cell: res}, args.measure)
        if problems:
            rep.note(f"{burn:<8g} (not usable: {'; '.join(problems)})")
            all_ok = False
            if burn == 0:
                break
            continue
        s = res["stats"]
        work, budget, states = s["work_mean"], s["budget"], s["probe"]
        if baseline is None:
            baseline = work
        delta, expected = work - baseline, baseline + burn
        if expected > 1.1 * budget:
            ok = s["sat"] >= 90 and "Invalid" not in states
            saturating += 1
            verdict = "ok" if ok else "FAIL"
        elif expected <= 0.9 * budget:
            ok = abs(delta - burn) <= 0.05 * burn + 0.3 and set(states) == {"Valid"}
            linear += 1 if burn > 0 else 0
            verdict = "ok" if ok else "FAIL"
        else:
            ok, verdict = True, "not graded (within 10% of the budget)"
        all_ok &= ok
        rep.note(f"{burn:<8g} {work:<8.2f} {delta:<7.2f} {s['wait_mean']:<8.2f} {s['sat']:<6.0f} {s['windows']:<8} "
                 f"{'/'.join(sorted(set(states))):<14} {verdict}")
    if all_ok and (linear == 0 or saturating == 0):
        rep.note(f"probe acceptance: INCOMPLETE ({linear} step(s) under the budget and {saturating} over it were graded; "
                 f"it needs at least one of each)")
        return 1
    rep.note(f"probe acceptance: {'PASS' if all_ok else 'FAIL'} "
             + (f"(budget {budget:.2f} ms)" if baseline is not None else "(no baseline: the unburnt run was not usable)"))
    return 0 if all_ok else 1


def planned(args):
    """abgrade.ab_plan(args); a refused argument ends the command as SystemExit (exit 1, like an unknown layout)."""
    try:
        return abgrade.ab_plan(args)
    except ValueError as ex:
        raise SystemExit(str(ex)) from ex


def append_row(path, spec, window, results, context, rep):
    """Appends a run's mesh-p0/1 row to path (a run that ran: usable or discarded). Its cells' logs become
    p0cells records, graded by p0checks alone (its structure check is rigrun.run_problems). A row that cannot be graded
    is still written, as a failed run naming why; either way, and for a row that is not accepted, the report says so."""
    since, start, end = window
    try:
        cells = {cid: {"structural": res,
                       "p0": p0cells.cell_record(cid, rigrun.cell_lines(cid, r.MAP, since), start, end)}
                 for cid, res in results.items()}
        run = {"since": since, "start": start, "end": end, "cells": cells}
        row = p0results.run_row(spec, run, context, p0checks.run_problems(spec, run), p0checks.exclusion(spec, run))
    except Exception as ex:  # one run's boundary: its row is written as failed, and the matrix goes on
        rep.note(f"    row not graded: {ex}")
        row = p0results.run_row(spec, None, context, [f"row not graded: {ex}"], "run failed")
    else:
        if not row["accepted"]:
            reasons = row["problems"] + ([row["excluded_reason"]] if row["excluded_reason"] else [])
            rep.note("    row not accepted: " + "; ".join(reasons))
    p0results.write_rows(path, [row])


def run_spec(spec, args, rep, context):
    """Runs one planned run and notes how it went: returns the usable run's record (abgrade.run_record), or None when
    it failed or was discarded. With --jsonl its row is appended whatever happened (a failed run's with
    excluded_reason 'run failed' and the failure as its problem)."""
    cells = rigrun.LAYOUTS[spec["layout"]]
    try:
        since, start, end = rigrun.run_window(spec["env"], spec["warmup"], spec["measure"], rep)
        results = {cid: rigrun.cell_window(cid, r.MAP, since, start, end) for cid, _ in cells}
    except Exception as ex:  # recorded and skipped; the matrix goes on
        rep.note(f"    run failed: {ex}")
        if args.jsonl:
            p0results.write_rows(args.jsonl, [p0results.run_row(spec, None, context, [str(ex)], "run failed")])
        return None
    record = None
    problems = rigrun.run_problems(cells, spec["plan"], results, spec["measure"])
    if problems:
        rep.note("    discarded: " + "; ".join(problems))
    else:
        record = abgrade.run_record(start, {cid: res["stats"] for cid, res in results.items()})
        rep.note(abgrade.run_line(record))
    if args.jsonl:
        append_row(args.jsonl, spec, (since, start, end), results, context, rep)
    return record


def ab_command(args, rep):
    specs = planned(args)
    layouts, bands_given = abgrade.listed(args.layouts), bool(args.bands)
    for line in abgrade.header_lines(args):
        rep.note(line)
    context = rigrun.host_context() if args.jsonl else None
    points = {point: {layout: {} for layout in layouts} for point in abgrade.ab_points(args)}
    for spec in specs:
        rep.note(abgrade.run_heading(spec, len(layouts), bands_given))
        record = run_spec(spec, args, rep, context)
        if record is not None:
            points[abgrade.point_of(spec)][spec["layout"]][spec["repeat"] - 1] = record
    for line in abgrade.summary_lines(points, layouts, args.repeats, bands_given):
        rep.note(line)
    lines, groups = abgrade.grade_points(points, layouts, abgrade.load_area(args), args.repeats, bands_given)
    for line in lines:
        rep.note(line)
    return abgrade.exit_code(points, groups)


def build_parser():
    """The command line's parser: main parses with it, and abregrade rebuilds a saved report's arguments with it, so a
    replay always carries every flag's default."""
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="command", required=True)
    p = sub.add_parser("probe")
    p.add_argument("--burns", default="0,4,8,12,24")
    p.add_argument("--cell", default="C")
    p.add_argument("--warmup", type=int, default=30)
    p.add_argument("--measure", type=int, default=30)
    a = sub.add_parser("ab")
    a.add_argument("--players", default="150,250")
    a.add_argument("--load", default="spread", help="spread, or a crowd square x,z,r in the scene frame")
    a.add_argument("--monsters", default="off,all", help="any of off, edge, all")
    a.add_argument("--layouts", default="coarse,halves,grid", help="any of coarse, halves, grid, strips4")
    a.add_argument("--repeats", type=int, default=3)
    a.add_argument("--seed", type=int, default=1, help="load placement seed of the first repeat; repeat k uses seed + k")
    a.add_argument("--band", type=float, default=0.0, help="ghost band and visible range in metres (0 = as configured)")
    a.add_argument("--warmup", type=int, default=45)
    a.add_argument("--measure", type=int, default=60)
    a.add_argument("--bands", default="", help="bands in metres to run in every point, MESH_BAND always set; not with --band")
    a.add_argument("--puppets", default="", help="MESH_PUPPET_MOTION: steered or kinematic (default: as configured)")
    a.add_argument("--rates", default="", help="MESH_RATES, e.g. 60/0.03 (default: as configured; u... refused)")
    a.add_argument("--instrument", action="store_true", help="MESH_AOI_TIMING, MESH_WORK_BINS and MESH_THREAD_CPU on")
    a.add_argument("--order", default="point-major", help="point-major (0.71) or repeat-major (Phase 0)")
    a.add_argument("--jsonl", default="", help="append a mesh-p0/1 run row per run to this file")
    a.add_argument("--case", default="ab", help="the rows' case")
    a.add_argument("--dry-run", action="store_true", help="print the plan; no editor, database, PowerShell or file")
    return ap


def main(argv=None):
    args = build_parser().parse_args(argv)
    if args.command == "ab":
        specs = planned(args)   # a refused argument ends here, before any precondition is read
        if args.dry_run:
            for line in abgrade.header_lines(args) + p0dryrun.dry_run_lines(specs):
                print(line)
            return 0

    problems = rigrun.start_problems()
    for problem in problems:
        print(problem)
    if problems:
        return 2
    rep = Report(f"abload {args.command} on {os.environ.get('COMPUTERNAME', '?')}, {rigrun.cpu_name()} ({time.strftime('%Y-%m-%d %H:%M')})")
    print(rep.lines[0], flush=True)
    code = 1
    try:
        code = probe_command(args, rep) if args.command == "probe" else ab_command(args, rep)
    finally:
        # Written whatever happened: a long matrix that breaks near the end still leaves every line it produced.
        path = os.path.join(r.LOGS, f"abload_{args.command}_{time.strftime('%Y%m%d_%H%M%S')}.txt")
        with open(path, "w", encoding="utf-8") as f:
            f.write(rep.text() + "\n")
        print("\nwritten to", path)
    return code


if __name__ == "__main__":
    sys.exit(main())
