#!/usr/bin/env python3
"""Phase 0 on the rig (steps 4 and 8a): every stage 0a block, stage 0b's single-machine blocks, their canaries, and the
report.

    python Tools/ServerMeshing/p0load.py canary [--baseline]
    python Tools/ServerMeshing/p0load.py accept-a [--parts A,B,C,D,E]
    python Tools/ServerMeshing/p0load.py rates [--repeats 3]
    python Tools/ServerMeshing/p0load.py presence [--players 100,200,400] [--steered-players 100,200,250]
                                                  [--rates 60/0.03]
    python Tools/ServerMeshing/p0load.py density [--players 100,200] [--rates 60/0.03]
    python Tools/ServerMeshing/p0load.py floor
    python Tools/ServerMeshing/p0load.py abgeo [--subs 1,2,3] [--without-predictions]
    python Tools/ServerMeshing/p0load.py report [--import FILE ...] [--predict abgeo] [--bridge SHA | --build SHA] [--host NAME]
    python Tools/ServerMeshing/p0load.py observed --part O|R|SA|M|S
    python Tools/ServerMeshing/p0load.py fanout|targets|syncrpc|packets|prewarm
    python Tools/ServerMeshing/p0load.py accept-b|calibrate|crossings|jfull|workerab|dbburst|spaces   (refused)
    common: --dry-run --resume --no-canary --warmup S --measure S

The plans are p0plans' (the step 4 contract's §H) and p0plans0b's (step 8a, decisions 6-11), run by p0blocks: each run
is abload's protocol (rigrun.run_window), read per cell with rigrun.cell_window and p0cells.cell_record, graded by
p0checks, and appended as a mesh-p0/1 row to Logs/p0/rig_<game>_<family>_<stamp>.jsonl whatever happened (a run that
raises is a failed run; the block goes on). Each run's line is also kept in Logs/p0/rig_<family>_<stamp>.txt. abgeo runs
abload once per sub-block (its rows go to Logs/p0/rig_fantasy_abgeo_<stamp>.jsonl) and needs frozen predictions (report
--predict abgeo) unless --without-predictions. Step 8b's and stage 0c's families are refused, naming the step that
builds them; presence --observed is refused, pointing at observed. A stage 0b spec carries its own game
(rig_<game>_<family>_<stamp>.jsonl), and a stepped run (fanout, packets saturation) has warm-up 10 s and measures its
steps' holds and bind ramps + 20 s.

Canaries (Decision 17: coarse C, 200 kinematic owners, 30/0.02) around rates, presence, density, floor, abgeo and the
stage 0b blocks (CANARY_FAMILIES), into Logs/p0/rig_neutral_canary_<stamp>.jsonl: one at the start (unless a canary on
this build is under 2 h old), one before any run once 2 h have passed, one at the end (abgeo: one after each sub-block).
A canary drifted more than 5 % from its build's baseline, or not accepted, is repeated at once; if the repeat passes
after a drift, the runs since the last good canary are queued again once at the end (abgeo: that sub-block). Two drifts,
or two unaccepted canaries in a row, stop p0load (exit 3). They need a baseline on this machine (build and host; canary
--baseline: 2 runs, a third when they differ by more than 2.5 %). floor, density's bridge and rates --repeats 2 (2
repeats) get third repeats where two runs disagree, decided over the whole block plan (so --resume still owes them)
after the block and again after a drifted canary. --resume skips planned runs with an accepted, non-drift row on this
machine (a stepped run: one per step; accept-a refuses it: it grades its runs together); the runs an earlier invocation
made after the last good canary are queued again too when a canary drifts, and the verdict counts every planned run. The
start canary runs unless the newest canary on this machine passed under 2 h ago. --no-canary skips the canaries.
--dry-run prints the plan, every run's environment and predicted load (label E); it touches no editor, database,
PowerShell or Temp file and writes nothing. report reads Logs/p0/*.jsonl (+ --import) and writes
Logs/p0/report_<stamp>.md and Logs/p0/<team>_<game>_fit_<stamp>.jsonl (p0report.build_report); only the rig's rows are
fitted (other teams' are summarised), two rig builds pool only with --bridge, and --build SHA reports one build.
Exit codes: 0 every planned run accepted (report: written); 1 some runs not accepted (listed); 2 refused
(preconditions, arguments, a later stage, a pending key, no predictions, no baseline); 3 stopped by the canary.
"""
import argparse
import datetime
import glob
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import abgrade
import abload
import p0blocks
import p0dryrun
import p0plans
import p0plans0b
import p0report
import p0results
import rigrun

HERE = os.path.dirname(os.path.abspath(__file__))
FIXTURE = os.path.join(HERE, "fixtures", "abload_chain3_20260925")
# p0blocks' canaried families and step 8a's (decision 11: every new family is canaried)
CANARY_FAMILIES = tuple(p0blocks.CANARY_FAMILIES) + tuple(family for family in p0plans0b.FAMILIES
                                                          if family not in p0blocks.CANARY_FAMILIES)


def this_host():
    """The machine a row is keyed on beside its build (decision 1): rigrun.host_context's host (with the machine epoch,
    rigrun.machine_host), read without PowerShell so --resume and a dry run can use it."""
    return rigrun.machine_host()


def block_plan(args):
    """The RunSpecs of a rig block's arguments, --warmup/--measure applied (prewarm times its bursts from the warm-up
    it runs with). Raises ValueError for what the plan builders refuse (bad counts, rates or parts, a key
    regress.ENV_KEYS lacks or a pending key) and for a command with no plan."""
    command = args.command
    if command == "canary":
        specs = p0plans.canary_plan("baseline", 2) if args.baseline else p0plans.canary_plan("check")
    elif command == "accept-a":
        specs = p0plans.accept_plan(args.parts.split(","))
    elif command == "rates":
        specs = p0plans.rates_plan(args.repeats)
    elif command == "presence":
        specs = p0plans.presence_plan(
            rates=args.rates, points=p0blocks.listed_ints(args.players, "--players"),
            steered_points=p0blocks.listed_ints(args.steered_players, "--steered-players"))
    elif command == "density":
        specs = p0plans.density_plan(args.rates, p0blocks.listed_ints(args.players, "--players"))
    elif command == "floor":
        specs = p0plans.floor_plan()
    elif command == "observed":
        specs = p0plans0b.observed_plan(args.part)
    elif command == "fanout":
        specs = p0plans0b.fanout_plan()
    elif command == "targets":
        specs = p0plans0b.targets_plan()
    elif command == "syncrpc":
        specs = p0plans0b.syncrpc_plan()
    elif command == "packets":
        specs = p0plans0b.packets_plan()
    elif command == "prewarm":
        specs = p0plans0b.prewarm_plan(p0plans.LONG[0] if args.warmup is None else args.warmup)
    else:
        raise ValueError(f"p0load has no plan for {command!r}")
    if (args.warmup is not None or args.measure is not None) and any(s.get("stepped") for s in specs):
        raise ValueError(f"{command} has stepped runs, whose warmup (10 s) and measure (the step holds, the bind ramps "
                         "and 20 s) are set by their plan: --warmup and --measure are refused for it")
    for spec in specs:
        spec["warmup"] = spec["warmup"] if args.warmup is None else args.warmup
        spec["measure"] = spec["measure"] if args.measure is None else args.measure
        rigrun.check_env(spec["env"])
    return specs


def predict_points():
    """Every b9 point and pair (abgrade.PAIRS present in its sub-block) as p0report's predict_points."""
    points = {}
    for label, argv in p0plans.abgeo_invocations("-"):
        layouts = abgrade.listed(abload.build_parser().parse_args(argv).layouts)
        pairs = [f"{cut}/{base}" for cut, base in abgrade.PAIRS if cut in layouts and base in layouts]
        for spec in p0blocks.ab_specs(argv):
            point = spec["point"]
            for pair in pairs:
                entry = {"case": label, "players": point["players"], "monsters": point["monsters"],
                         "band": point["band"], "puppets": point["puppets"], "load": point["load"], "pair": pair}
                points[tuple(entry.values())] = entry
    return list(points.values())


def canary_note(args):
    """The dry run's canary lines: when the block's canaries run, and the canary's environment."""
    if args.no_canary:
        return ["canaries: none (--no-canary; rows will carry 'no canary after this run')"]
    spec = p0plans.canary_plan("check")[0]
    env = " ".join(f"{key}={value}" for key, value in sorted(spec["env"].items()))
    return [f"canaries: a check canary at the start (unless one on this build is under 2 h old), before any run once "
            f"2 h have passed and at the end{', and after each sub-block' if args.command == 'abgeo' else ''}; "
            f"{p0plans.run_seconds(spec)} s each", f"    canary env: {env}"]


def dry_run(args, specs):
    """Prints a rig block's plan (p0dryrun.dry_run_lines), its canaries, third repeats and accept-a commands; 0."""
    lines = p0dryrun.dry_run_lines(specs) if specs else ["no rig runs"]
    if args.command in CANARY_FAMILIES:
        lines += canary_note(args)
    if args.command in p0blocks.THIRD:
        metric, thresholds = p0blocks.THIRD[args.command]
        straddle = ", ".join(f"{name} {value:g}" for name, value in thresholds)
        clause = f" (or straddle {straddle})" if straddle else ""
        lines.append(f"third repeats: each 2-repeat point whose runs differ by more than 10 % on C {metric}{clause} "
                     "gets a third run after the block")
    if args.command == "accept-a":
        lines += [f"accept-a {part} {label}: {'python -u ' if python else ''}{' '.join(argv)}"
                  + (" (rerun once if it fails)" if part == "C" else "")
                  for part, label, argv, python in p0plans.accept_commands(args.parts.split(","))]
    print("\n".join(lines))
    return 0


def abgeo_dry_run(args, done):
    """Prints each abgeo sub-block's abload argv and the plan abgrade.ab_plan makes of it; 0."""
    path = os.path.join(p0blocks.P0_LOGS, p0results.file_name("fantasy", "abgeo", "<stamp>"))
    total = 0
    for label, argv in p0blocks.abgeo_invocations(args, path, done):
        specs = p0blocks.ab_specs(argv)
        total += len(specs)
        print(f"=== {label}: abload.py {' '.join(argv)}")
        print("\n".join(abgrade.header_lines(abload.build_parser().parse_args(argv)) + p0dryrun.dry_run_lines(specs)))
    print("\n".join(canary_note(args) + [f"abgeo: {total} runs in all"]))
    return 0


def report_dry_run(args):
    """Prints the files report would read, their run rows per family and the fits it would make; 0."""
    paths, rows = p0blocks.read_history(args.imports)
    families = {}
    for row in p0results.rows_of_type(rows, "run"):
        families[row.get("family")] = families.get(row.get("family"), 0) + 1
    lines = [f"would read {len(paths)} file(s): {', '.join(paths) or 'none yet'}",
             f"run rows per family: {', '.join(f'{k} {n}' for k, n in sorted(families.items())) or 'none'}",
             "fits and outputs, in order:"] + [f"  {step}" for step in p0report.FIT_ORDER]
    lines.append(f"would write {os.path.join(p0blocks.P0_LOGS, 'report_<stamp>.md')} and "
                 f"rig_<game>_fit_<stamp>.jsonl" + (" and rig_fantasy_abgeo-prediction_<stamp>.jsonl" if args.predict
                                                    else ""))
    print("\n".join(lines))
    return 0


def read_text(path):
    """A text file's contents (UTF-8)."""
    with open(path, encoding="utf-8") as f:
        return f.read()


def report_command(args):
    """report: p0report.build_report over Logs/p0 (and --import), its files written to Logs/p0; 0, or 2 when
    --predict abgeo could not freeze its predictions (the rest is still written)."""
    _, rows = p0blocks.read_history(args.imports)
    counts = p0report.chain3_monster_counts(
        read_text(path) for path in sorted(glob.glob(os.path.join(FIXTURE, "abload_ab_*.txt"))))
    stamp = time.strftime("%Y%m%d_%H%M%S")
    result = p0report.build_report(rows, stamp, args.bridge, args.predict, predict_points() if args.predict else (),
                                   rigrun.LAYOUTS, counts, datetime.datetime.now(datetime.timezone.utc), args.build,
                                   args.host)
    os.makedirs(p0blocks.P0_LOGS, exist_ok=True)
    for name, content in result["files"].items():
        path = os.path.join(p0blocks.P0_LOGS, name)
        if name.endswith(".md"):
            with open(path, "w", encoding="utf-8", newline="\n") as f:
                f.write(content)
        else:
            p0results.write_rows(path, content)
        print("written", path)
    for gate in result["gates"]:
        print(f"{gate['name']}: {gate['verdict']}" + (f" ({gate['key_metric']})" if gate.get("key_metric") else ""))
    if result["prediction_error"] is not None:
        print(f"refused: no frozen predictions written: {result['prediction_error']}")
        return 2
    return 0


def build_parser():
    """The command line's parser: one subcommand per block, report and the refused later-stage families."""
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="command", required=True)

    def command(name, **kwargs):
        p = sub.add_parser(name, **kwargs)
        p.add_argument("--dry-run", action="store_true", help="print the plan; no editor, database or file")
        p.add_argument("--resume", action="store_true",
                       help="skip runs with an accepted, non-drift row on this build and host")
        p.add_argument("--no-canary", action="store_true", help="skip the scheduled canaries")
        p.add_argument("--warmup", type=int, default=None, help="warm-up seconds of every run (default: the plan's)")
        p.add_argument("--measure", type=int, default=None, help="measured seconds of every run (default: the plan's)")
        return p

    command("canary").add_argument("--baseline", action="store_true", help="the build's baseline (2 runs, +1)")
    command("accept-a").add_argument("--parts", default="A,B,C,D,E")
    command("rates").add_argument("--repeats", type=int, default=3)
    p = command("presence")
    p.add_argument("--players", default="100,200,400", help="kinematic and inert points")
    p.add_argument("--steered-players", default="100,200,250", help="steered points")
    p.add_argument("--rates", default="60/0.03")
    p.add_argument("--observed", action="store_true", help="refused: B1-O moved to p0load observed")
    p = command("density")
    p.add_argument("--players", default="100,200")
    p.add_argument("--rates", default="60/0.03")
    command("floor")
    command("observed").add_argument("--part", required=True, type=str.upper, choices=p0plans0b.OBSERVED_PARTS,
                                     help="b3 O, R, SA; b4 M; b5 S")
    for name in ("fanout", "targets", "syncrpc", "packets", "prewarm"):
        command(name)
    p = command("abgeo")
    p.add_argument("--subs", default="1,2,3")
    p.add_argument("--without-predictions", action="store_true", help="run without frozen predictions")
    p = command("report")
    p.add_argument("--import", dest="imports", action="append", default=[], metavar="FILE")
    p.add_argument("--predict", choices=["abgeo"], default=None)
    p.add_argument("--bridge", default=None, metavar="SHA", help="pool two builds when this build's canary allows")
    p.add_argument("--build", default=None, metavar="SHA", help="report only this build's rig rows")
    p.add_argument("--host", default=None, metavar="NAME",
                   help="report only this machine's rig rows (needed when Logs/p0 holds rows of two machines)")
    for name in p0plans.STAGE_LATER:
        command(name, help=f"refused: {p0plans.STAGE_LATER[name]}")
    return ap


def main(argv=None):
    """The command line: refusals (a later stage, presence --observed, accept-a --resume, a refused plan) exit 2;
    --dry-run prints; report writes the report; any other block runs on the rig (run_command)."""
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    args = build_parser().parse_args(argv)
    if args.command in p0plans.STAGE_LATER:
        print(f"refused: {p0plans.later_refusal(args.command)}")
        return 2
    if args.command == "presence" and args.observed:
        print(f"refused: {p0plans.OBSERVED_REFUSAL}")
        return 2
    if args.command == "accept-a" and args.resume:
        print("refused: accept-a grades its runs together (the burn, rates, bins and overhead checks compare them): "
              "run it whole, without --resume")
        return 2
    try:
        if args.command == "report":
            return report_dry_run(args) if args.dry_run else report_command(args)
        frozen = os.path.join(p0blocks.P0_LOGS, p0results.file_name("fantasy", p0results.PREDICTION_FAMILY, "*"))
        if args.command == "abgeo" and not args.without_predictions and not glob.glob(frozen):
            raise ValueError("abgeo needs frozen predictions (p0load report --predict abgeo) or "
                             "--without-predictions")
        done = (p0blocks.done_keys(p0blocks.read_history()[1], rigrun.build_id(), this_host()) if args.resume
                else frozenset())
        if args.command == "abgeo" and args.dry_run:
            return abgeo_dry_run(args, done)
        plan = [] if args.command == "abgeo" else block_plan(args)
        specs = [spec for spec in plan if spec["key"] not in done]
        if args.dry_run:
            return dry_run(args, specs)
    except ValueError as ex:   # a refused plan, unreadable rows (SchemaError), or a report that cannot be made
        print(f"refused: {ex}")
        return 2
    return run_command(args, specs, done, plan)


def run_command(args, specs, done, plan):
    """Runs a block on the rig after its preconditions (specs: the plan less the runs --resume skips); the report
    file is written whatever happens."""
    problems = rigrun.start_problems()
    try:
        history = p0blocks.read_history()[1]
    except p0results.SchemaError as ex:
        problems.append(f"cannot read Logs/p0: {ex}")
        history = []
    build, host = rigrun.build_id(), this_host()
    canaried = args.command in CANARY_FAMILIES and not args.no_canary
    needs_baseline = canaried or (args.command == "canary" and not args.baseline)
    if needs_baseline and (build, host) not in p0results.canary_baselines(history):
        problems.append(f"no baseline canary on build {build[:12] or '(none)'} on {host}: run p0load canary --baseline "
                        "first"
                        + (" (or pass --no-canary)" if canaried else ""))
    for problem in problems:
        print(problem)
    if problems:
        return 2
    os.makedirs(p0blocks.P0_LOGS, exist_ok=True)
    stamp, family = time.strftime("%Y%m%d_%H%M%S"), "accept" if args.command == "accept-a" else args.command
    rep = rigrun.Report(f"p0load {args.command} on {os.environ.get('COMPUTERNAME', '?')} "
                        f"({time.strftime('%Y-%m-%d %H:%M')})")
    print(rep.lines[0], flush=True)
    code = 1
    try:
        session = p0blocks.Session(args, stamp, rep, rigrun.host_context(), history)
        if args.command == "canary" and args.baseline:
            code = p0blocks.baseline_block(session, specs)
        elif args.command == "canary":
            session.canary("p0load canary")   # a drift that a repeat clears is noted; a second one stops
            code = 0
        elif args.command == "accept-a":
            code = p0blocks.accept_block(session, specs, stamp)
        elif args.command == "abgeo":
            code = p0blocks.abgeo_block(session, args, done)
        else:
            code = session.block(specs, plan)
    except p0blocks.Stopped as stop:
        rep.note(f"stopped: {stop.reason}")
        code = stop.code
    finally:
        path = os.path.join(p0blocks.P0_LOGS, f"rig_{family}_{stamp}.txt")
        with open(path, "w", encoding="utf-8") as f:
            f.write(rep.text() + "\n")
        print("\nwritten to", path)
    return code


if __name__ == "__main__":
    sys.exit(main())
