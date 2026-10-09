"""Phase 0 step 8b's part of p0load (step8b_design.md decisions 5, 7, 10-15): the two-machine subcommands' arguments,
plans, refusals and dry-run notes, accept-b's and packets --sink's block runners, and the one-off bot procedures
(register-bots, calibrate-bots). Split out of p0load (the ~500-line rule); p0load keeps the command line's flow.

    p0load8b.add_commands(command, sub)      # accept-b, calibrate, crossings, jfull, register-bots, calibrate-bots
    p0load8b.block_plan_8b(args)             # a two-machine block's specs (calibrate and jfull read the calibration)
    p0load8b.run_refusals(args, specs)       # why a two-machine block may not start (B, the calibration); [] if it may
    p0load8b.procedure(args)                 # register-bots / calibrate-bots (decisions 5, 7)

Every step 8b command refuses to start (exit 2, before Play) when B is unreachable, runs a cell, runs RelicTestServer or
Relics, or holds another build (p0peer.start_problems); a dry run never touches B (it reads only the local calibration
file, Logs/p0/bots_calibration.json). p0peer is imported where it is used: it drives B and A's editor.
"""
import os
import time

import p0accept8b
import p0blocks
import p0checks8b
import p0plans8b
import p0results
import rigrun

COMMANDS = ("accept-b", "calibrate", "crossings", "jfull")
PROCEDURES = ("register-bots", "calibrate-bots")
CALIBRATION_NOTE = "MESH_PHANTOM_UPLOAD_HZ is the bots' measured upload rate (p0load calibrate-bots on this build)"


def add_commands(command, sub):
    """The step 8b subcommands: command(name) adds a block's common flags (p0load.build_parser); the procedures take
    --n and --dry-run only."""
    command("accept-b").add_argument("--parts", default=",".join(p0plans8b.ACCEPT_B_PARTS))
    for name in ("calibrate", "crossings", "jfull"):
        command(name)
    for name, what in (("register-bots", "meshbot2.. log in once (decision 5)"),
                       ("calibrate-bots", "the bot smoke: Logs/p0/bots_calibration.json (decision 7)")):
        p = sub.add_parser(name, help=what)
        p.add_argument("--n", type=int, default=p0plans8b.JFULL_P, help="bots (default 100)")
        p.add_argument("--dry-run", action="store_true", help="print the procedure; no editor, database or B")


def two_machine(args):
    """Whether the command runs with machine B: a step 8b block, a procedure, or packets --sink."""
    return args.command in COMMANDS + PROCEDURES or (args.command == "packets" and getattr(args, "sink", False))


def _peer():
    import p0peer   # drives B and A's editor: imported only where a two-machine command needs it
    return p0peer


def calibration():
    """This build's bot calibration (p0peer.read_calibration), or None when there is none (or no p0peer yet)."""
    try:
        peer = _peer()
    except ImportError:
        return None
    return peer.read_calibration(rigrun.build_id())


def block_plan_8b(args):
    """The specs of a two-machine block: accept-b's parts, calibrate and jfull at the calibration's upload rate, jfull
    only when the calibration says B hosts 100 bots (a dry run previews it anyway), crossings, packets --sink."""
    command = args.command
    if command == "accept-b":
        return p0plans8b.accept_b_plan(args.parts)
    if command == "calibrate":
        return p0plans8b.calibrate_plan(calibration())
    if command == "jfull":
        return p0plans8b.jfull_plan(calibration(), force=args.dry_run)
    if command == "crossings":
        return p0plans8b.crossings_plan()
    if command == "packets":
        return p0plans8b.packets_sink_plan()
    raise ValueError(f"p0load has no step 8b plan for {command!r}")


def start_problems():
    """p0peer's refusals of decision 10 (p0peer.preflight when it has one, else start_problems): lines, [] when B is
    ready."""
    peer = _peer()
    return list((getattr(peer, "preflight", None) or peer.start_problems)())


def run_refusals(args, specs):
    """Why a two-machine block may not start, as lines: a phantom arm without the bots' calibrated upload rate, and
    p0peer's refusals (B unreachable, a cell or Relics on B, another build)."""
    problems = []
    if any("upload_hz" in spec and spec["upload_hz"] is None for spec in specs):
        problems.append(f"{args.command}: no bot calibration on this build; {CALIBRATION_NOTE}")
    return problems + start_problems()


def dry_run_notes(args, specs):
    """The dry run's step 8b lines: what B must be before a real run, and the calibration's state."""
    lines = ["machine B: a real run refuses unless B answers, runs no cell, RelicTestServer or Relics, and holds this "
             "build; B's bots and sink are stopped before each run and each canary"]
    if args.command in ("calibrate", "jfull"):
        found = calibration()
        refusal = p0plans8b.jfull_refusal(found) if args.command == "jfull" else None
        if refusal:
            lines.append(f"jfull is not planned: {refusal}; the plan above is what it would run")
        if found is None:
            lines.append(f"no bot calibration on this build: {CALIBRATION_NOTE}")
        else:
            lines.append(f"calibration: tried {found.get('tried')}, hosted {found.get('hosted')}, upload "
                         f"{found.get('upload_pkt_per_s')} pkt/s per bot -> MESH_PHANTOM_UPLOAD_HZ "
                         f"{p0plans8b.upload_hz(found)}")
    if args.command == "packets":
        lines.append("probe: a run that saturates C's main thread stops the probe there (the larger P are not run)")
    return lines


def procedure(args):
    """register-bots (decision 5) or calibrate-bots (decision 7): a dry run prints the procedure; a real run refuses
    (exit 2) unless the rig and B may start, then runs p0peer's procedure with its report written to
    Logs/p0/rig_<command>_<stamp>.txt. 0 when every bot registered / the calibration fits N bots, else 1."""
    if args.n < 1:
        print(f"refused: --n {args.n}: at least 1 bot")
        return 2
    last = args.n + p0plans8b.FIRST_BOT - 1
    if args.dry_run:
        what = (f"Play up with no load; meshbot2..meshbot{last} log in from B in groups of 5, standing at x = -50, each "
                "group stopped once every bot logged 'In map at' (or 90 s); a bot whose login fails registers itself"
                if args.command == "register-bots" else
                f"Play up with no load; meshbot2..meshbot{last} pace at the spread points; all in the map within 4 min?; "
                "a minute measured; B's free memory and the bots' Bot net upload pkt/s -> Logs/p0/bots_calibration.json")
        print(f"{args.command}: {what}")
        return 0
    problems = rigrun.start_problems() + start_problems()
    for problem in problems:
        print(problem)
    if problems:
        return 2
    peer, stamp = _peer(), time.strftime("%Y%m%d_%H%M%S")
    rep = rigrun.Report(f"p0load {args.command} --n {args.n} ({time.strftime('%Y-%m-%d %H:%M')})")
    code = 1
    try:
        if args.command == "register-bots":
            code = 0 if len(peer.register_bots(args.n, rep)) == args.n else 1
        else:
            code = 0 if peer.calibrate_bots(rep, n=args.n).get("fits") else 1
    except peer.Stopped as stop:
        rep.note(f"stopped: {stop.reason}")
        code = stop.code
    finally:
        os.makedirs(p0blocks.P0_LOGS, exist_ok=True)
        path = os.path.join(p0blocks.P0_LOGS, f"rig_{args.command}_{stamp}.txt")
        with open(path, "w", encoding="utf-8") as f:
            f.write(rep.text() + "\n")
        print("\nwritten to", path)
    return code


def _canary_due(session):
    return session.canaries and (session.last_canary is None
                                 or rigrun.now() - session.last_canary >= p0blocks.CANARY_EVERY)


def _run_all(session, specs, keep=None):
    """Runs the specs in order with the 2 h canaries (a drift is noted: these blocks are graded together, so nothing is
    queued again); returns [(spec, row, run)]. keep(row) False stops before the next spec (its rest are noted)."""
    done = []
    for index, spec in enumerate(specs, 1):
        if done and keep is not None and not keep(done[-1][1]):
            session.rep.note(f"not run: {spec['family']} {spec['case']} {spec['arm']} {spec['point']} (key "
                             f"{spec['key']}): the probe stopped at the previous P")
            continue
        if _canary_due(session) and session.checkpoint("2 h since the last canary"):
            session.rep.note("    a drifted canary: the runs since the last good one are not re-run (graded together)")
        row, run = session.run(spec, f"[{index}/{len(specs)}]")
        done.append((spec, row, run))
    return done


def accept_b_block(session, specs):
    """accept-b (decision 11): every part's runs (canaries at the start, every 2 h and at the end), then
    p0accept8b's checks; 0 when every check passes and every planned run is accepted."""
    session.plan = list(specs)
    if _canary_due(session):
        session.checkpoint("the block's start")
    runs = _run_all(session, specs)
    if session.canaries and runs:
        session.checkpoint("the block's end")
    ok = True
    for passed, text in p0accept8b.accept_b_checks(runs):
        session.rep.note(f"{'ok  ' if passed else 'FAIL'} {text}")
        ok &= passed
    ok &= not specs or session.verdict() == 0
    session.rep.note(f"accept-b: {'PASS' if ok else 'FAIL'}")
    return 0 if ok else 1


def packets_sink_block(session, specs, plan):
    """packets --sink (decision 13): the cap arms as a block (Session.block: canaries, re-queue after a drift), then
    the probe's P in ascending order, stopping once a run saturates C's main thread, and the closing canary; notes the
    per-packet costs and the probe's saturation verdicts; the verdict over the whole plan."""
    probes = [spec for spec in specs if spec["case"] == "probe"]
    others = [spec for spec in specs if spec["case"] != "probe"]
    if others:
        session.block(others, [spec for spec in plan if spec["case"] != "probe"])
    if probes:
        runs = _run_all(session, probes, keep=lambda row: not p0checks8b.main_saturated(row))
        if session.canaries and runs:
            session.checkpoint("the probe's end")
    rows = session.ours(p0results.apply_drift(session.rows))
    for cost in p0checks8b.packet_costs(rows):
        session.rep.note(f"per packet at cap {cost['packet_bytes']} B: main {_us(cost['pkt_main_us'])}, LiteNetLib "
                         f"{_us(cost['pkt_net_us'])} (sink - virtual over the sink's datagrams; {cost['n']} repeats)")
    for point in p0checks8b.probe_saturation(rows):
        session.rep.note(f"probe P {point['P']}: LiteNetLib {point['thread_ms_per_s']} ms/s, {point['datagrams_per_s']} "
                         f"datagrams/s, saturated {point['saturated']}" + (" (near its threshold: a second repeat is "
                                                                           "owed)" if point["near"] else "")
                         + (" [main thread saturated]" if point["main_saturated"] else ""))
    session.plan = list(plan)
    return session.verdict()


def _us(value):
    return "–" if value is None else f"{value:.2f} us"
