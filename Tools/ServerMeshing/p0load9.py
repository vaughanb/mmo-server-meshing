"""Phase 0 step 9's part of p0load (step9_design.md decisions 4, 9, 11-14): the stage 0c subcommands' arguments, plans,
refusals, dry-run notes and block runners (dbburst, workerab, spaces) and packets --loss. Split out of p0load (the
~500-line rule); p0load keeps the command line's flow.

    p0load9.add_commands(command)        # dbburst [--parts burst,sustained,calibration], workerab [--fallback], spaces
    p0load9.block_plan_9(args)           # a stage 0c block's specs, or packets --loss's
    p0load9.two_machine(args)            # dbburst with its calibration part, packets --loss: B is needed
    p0load9.run_block(session, specs, plan)   # the block, then its figures and acceptance as report notes

A command that needs B refuses to start (exit 2) as step 8b's do (p0load8b.start_problems: B unreachable, a cell or
Relics on B, another build); a dry run never touches B, the editor or the database.
"""
import p0checks8b
import p0checks9
import p0load8b
import p0plans9
import p0results

COMMANDS = p0plans9.FAMILIES


def add_commands(command):
    """The step 9 subcommands: command(name) adds a block's common flags (p0load.build_parser)."""
    command("dbburst").add_argument("--parts", default=",".join(p0plans9.DB_PARTS),
                                    help="burst, sustained (machine A) and calibration (with B's bots)")
    command("workerab").add_argument("--fallback", action="store_true",
                                     help="loads 50 and 100 (when the coarse probe at 200 saturates)")
    command("spaces")


def loss(args):
    """Whether the command is packets --loss."""
    return args.command == "packets" and getattr(args, "loss", False)


def two_machine(args):
    """Whether the command runs with machine B: dbburst with its calibration part, or packets --loss."""
    if args.command == "dbburst":
        return "calibration" in [part.strip().lower() for part in args.parts.split(",")]
    return loss(args)


def block_plan_9(args):
    """The specs of a step 9 block. Refuses packets --loss with --sink (two plans in one invocation)."""
    if args.command == "dbburst":
        return p0plans9.dbburst_plan(args.parts)
    if args.command == "workerab":
        return p0plans9.workerab_plan(args.fallback)
    if args.command == "spaces":
        return p0plans9.spaces_plan()
    if loss(args):
        if getattr(args, "sink", False):
            raise ValueError("packets --loss and --sink are two blocks: run them one at a time")
        return p0plans9.loss_plan()
    raise ValueError(f"p0load has no step 9 plan for {args.command!r}")


def run_refusals(args, specs):
    """Why a step 9 block that needs B may not start: p0peer's refusals (p0load8b.start_problems); [] for one that does
    not."""
    return p0load8b.start_problems() if two_machine(args) else []


def dry_run_notes(args, specs):
    """The dry run's step 9 lines."""
    lines = []
    if args.command == "dbburst":
        lines.append(f"burst rows: provision's meshburst0001..{p0plans9.BURST_ROWS:04d} made out of Play before each "
                     f"burst run (A-only); each burst run ends once the editor's DB saves window reads pending 0 after "
                     f"the burst (at most {p0plans9.DRAIN_BOUND_S} s after the last arrival)")
    if args.command == "workerab":
        lines.append("probe: the coarse probes run first; if coarse at the highest load saturates C, the block stops "
                     "and asks for --fallback (loads 50, 100)")
    if loss(args):
        lines.append("loss: clumsy on B (remoteb impair) starts after the sink, the measurement opens after "
                     "'impairment on', and after_play stops it; a run refuses while clumsy already runs on B")
    if two_machine(args):
        lines.append("machine B: a real run refuses unless B answers, runs no cell, RelicTestServer or Relics, and holds "
                     "this build")
    return lines


def _workerab(session, specs, plan):
    probes = [spec for spec in specs if spec["case"] == "probe"]
    runs = p0load8b._run_all(session, probes) if probes else []
    top = max((spec["point"]["players"] for spec in plan if spec["case"] == "probe"), default=None)
    saturated = [row for spec, row, _ in runs if spec["point"]["players"] == top and
                 (p0checks8b.main_saturated(row) or "saturated" in (row.get("excluded_reason") or ""))]
    if saturated and top not in p0plans9.WORKER_FALLBACK:
        session.rep.note(f"workerab: coarse saturates C at {top} players: run p0load workerab --fallback")
        session.plan = list(plan)
        return 1
    rest = [spec for spec in specs if spec["case"] != "probe"]
    if rest:
        session.block(rest, [spec for spec in plan if spec["case"] != "probe"])
    for passed, text in p0checks9.workerab_acceptance(session.ours(p0results.apply_drift(session.rows))):
        session.rep.note(f"{'ok  ' if passed else 'FAIL'} {text}")
    session.plan = list(plan)
    return session.verdict()


def _notes(session, command):
    rows = session.ours(p0results.apply_drift(session.rows))
    if command == "dbburst":
        for gate in p0checks9.gates_9(rows):
            session.rep.note(f"{gate['name']}: {gate['verdict']}" + (f" ({gate['key_metric']})" if gate["key_metric"]
                                                                     else ""))
            for note in gate["notes"]:
                session.rep.note(f"    {note}")
    elif command == "spaces":
        figures = p0checks9.spaces_figures(rows)
        for entry in figures["floor"]:
            interval = "" if entry["lo"] is None else f" [{entry['lo']:.2f}, {entry['hi']:.2f}]"
            session.rep.note(f"spaces floor S {entry['S']}: {entry['per_space_ms']:.2f}{interval} ms/s per space "
                             f"({entry['n']} runs)")
        session.rep.note(f"spaces slope at 100 observers: {figures['slope']} ms/s per space; other seen max "
                         f"{figures['other_seen']}")
    else:
        for arm in p0checks9.loss_figures(rows):
            session.rep.note(f"loss {arm['loss_pct']} %: " + ", ".join(
                f"{name} {'–' if arm[name] is None else format(arm[name], '.2f')}"
                for name in ("work", "net_thread", "datagrams_per_s", "resent_per_s", "loss_share",
                             "received_per_pair")) + f" ({arm['n']} runs)")


def run_block(session, specs, plan):
    """A step 9 block: workerab's probes, then its arms and acceptance; any other through session.block; the block's
    figures (Database gate and capacity, the spaces floor, the loss arms) noted after it. The verdict over the plan."""
    command = "loss" if session.args.command == "packets" else session.args.command
    code = _workerab(session, specs, plan) if command == "workerab" else session.block(specs, plan)
    if command != "workerab":
        _notes(session, command)
    return code
