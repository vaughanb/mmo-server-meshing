"""Phase 0 blocks on the rig (step 4): p0load's execution. A Session runs a block's RunSpecs (rigrun's protocol, read
with p0cells, graded by p0checks, one mesh-p0/1 row per run appended whatever happened) with the canaries of Decision
17 and the third repeats of 2-repeat blocks; baseline_block, accept_block and abgeo_block run canary --baseline,
accept-a (rig parts, checks, overhead, the part C/D commands) and abgeo (abload per sub-block). Split out of p0load
(the ~500-line rule); p0load keeps the command line, the dry runs and the report.

    session = p0blocks.Session(args, stamp, rep, rigrun.host_context(), history)
    code = session.block(specs, plan)      # 0 every planned run accepted, 1 not; raises Stopped (exit 3)
    p0blocks.run_spec(spec, rep, context)  # (row, run) of one rig run; a run that raises is a failed run
    p0blocks.run_rows(spec, rep, context)  # [(row, run)]: one, or one per step of a stepped run (decision 4)

Canaries: a check canary drifted more than 5 % from its build's baseline, or not accepted, is repeated at once; a
repeat that passes after a drift queues the runs since the last good canary again once, at the end (under --resume
the earlier invocation's runs on this build since that canary too; third repeats are decided again instead); a second
drift, two unaccepted canaries in a row or no pass in 3 tries stop the invocation (Stopped, exit 3). A block starts
with a canary unless the newest canary on its build passed under 2 h ago. Third repeats are decided over the whole
block plan and this build's rows only, so a resumed block still gets them. "This build" is always this machine,
(build, host) (step 8a decision 1); a stepped run is done only when every one of its steps has an accepted row.

Step 8b (decision 8): a spec with a peer runs with p0peer's PeerSession hooks (B's sink and bots started after Play and
stopped before it ends; their problems make the row not accepted), and a spec with stop_after_outcomes ends once A's
read cells logged that many handoff outcomes. A row whose peer may have left processes on B is written, then the
invocation stops (Stopped, exit 3). When the block has any peer spec, B's bots and sink are stopped before each canary.
"""
import datetime
import glob
import os
import subprocess
import sys

import abgrade
import abload
import p0accept
import p0base
import p0peer
import p0plans
import p0results
import p0stepped
import regress as r
import rigrun

P0_LOGS = os.path.join(r.LOGS, "p0")
ABLOAD = os.path.join(os.path.dirname(os.path.abspath(__file__)), "abload.py")
CANARY_FAMILIES = ("rates", "presence", "density", "floor", "abgeo") + p0results.FAMILIES_8A + p0results.FAMILIES_8B
CANARY_EVERY = datetime.timedelta(hours=2)
CANARY_TRIES = 3
PER_FRAME = "work_ms_per_measured_frame"   # derived for density's uncapped bridge, whose ms/s says nothing
# (key metric, thresholds) of the blocks with 2-repeat arms. The bridge is uncapped, so saturation thresholds skip it;
# block K has 2 repeats only when run with --repeats 2 (third_repeat acts on {1, 2} points alone).
THIRD = {"floor": ("work_ms_per_s", p0plans.THIRD_REPEAT_THRESHOLDS["floor"]), "density": (PER_FRAME, ()),
         "rates": ("work_ms_per_s", p0plans.THIRD_REPEAT_THRESHOLDS["rates"])}
THIRD_RUNS = 2   # a third repeat runs at most twice in one invocation (again only after a drift excluded it)


# Stopped ends an invocation early: its exit code (2: abload refused, B unreachable; 3: the canary, B kept processes)
# and the reason printed. One class with p0peer's, which raises it from inside a run (p0peer cannot import this module).
Stopped = p0peer.Stopped


def listed_ints(text, flag):
    """A comma-separated argument as whole numbers. Refuses anything else and an empty list."""
    try:
        values = tuple(int(item) for item in text.split(",") if item.strip())
    except ValueError as err:
        raise ValueError(f"{flag} {text} is not a list of whole numbers") from err
    if not values:
        raise ValueError(f"{flag} lists nothing")
    return values


def abgeo_invocations(args, jsonl_path, done=frozenset()):
    """[(label, abload argv)] for the chosen sub-blocks, with --warmup/--measure passed on; a sub-block whose every
    run's plan key is in done (--resume) is left out."""
    subs = listed_ints(args.subs, "--subs")
    extra = [flag for name in ("warmup", "measure") if getattr(args, name) is not None
             for flag in (f"--{name}", str(getattr(args, name)))]
    return [(label, argv + extra) for label, argv in p0plans.abgeo_invocations(jsonl_path, subs)
            if not {spec["key"] for spec in ab_specs(argv + extra)} <= done]


def ab_specs(argv):
    """abgrade.ab_plan of an abload argv (ValueError for what it refuses)."""
    return abgrade.ab_plan(abload.build_parser().parse_args(argv))


def read_history(extra=()):
    """(paths, rows) of every Logs/p0/*.jsonl file plus extra files (SchemaError names file:line)."""
    paths = sorted(glob.glob(os.path.join(P0_LOGS, "*.jsonl"))) + list(extra)
    return paths, p0results.read_rows(paths)


def done_keys(rows, build, host):
    """Plan keys with a complete, non-drift run on this machine, (build, host) (--resume skips them)."""
    runs = p0results.apply_drift(p0results.rows_of_type(rows, "run"))
    return p0stepped.complete_keys([row for row in runs if p0results.machine(row) == (build, host)])


def canary_state(rows, build, host):
    """(last good, scheduling) window ends of this machine's canaries: the newest canary that judged the machine good
    (an accepted baseline, or an accepted check that did not drift, per p0results.apply_drift), and the same when it is
    also the newest canary of all, else None (the newest drifted or was not accepted, so the next block needs a
    canary before its first run). Both None without a good canary."""
    canaries = sorted(((datetime.datetime.fromisoformat(row["window_local"]["end"]), row)
                       for row in p0results.apply_drift(rows) if row.get("family") == "canary"
                       and p0results.machine(row) == (build, host) and (row.get("window_local") or {}).get("end")),
                      key=lambda canary: canary[0])
    good = [end for end, row in canaries
            if row.get("accepted") and (row.get("arm") == "baseline" or row.get("drift") is False)]
    last_good = max(good, default=None)
    newest_is_good = bool(good) and canaries[-1][0] == last_good
    return last_good, last_good if newest_is_good else None


def measured(spec, start, end):
    """The spec a run is graded against: a run that may end early (stop_after_outcomes, b9's walks) has its coverage
    checked against the seconds it measured, as a step's row is (p0stepped.step_spec); any other spec as planned."""
    if not spec.get("stop_after_outcomes"):
        return spec
    seconds = (end - start).total_seconds()
    return dict(spec, measure=int(seconds) if seconds == int(seconds) else seconds)


def run_spec(spec, rep, context):
    """(row, run) of one planned rig run: its mesh-p0/1 row whatever happened and the run p0checks graded (None when
    it failed). A run that raises or cannot be graded is a failed run; Stopped (a peer's refusal before Play) is
    raised. A canary's row also carries machine_state (rigrun.MachineProbe: the Windows session and the CPU's mean %
    processor performance over the measurement). A spec with a peer runs with p0peer.window_hooks (the PeerSession's
    on_play before the probe's), and its row carries the peer's problems and PEER_STOP (p0peer.mark_rows)."""
    probe = rigrun.MachineProbe(spec["warmup"], spec["measure"]) if spec["family"] == "canary" else None
    peer, hooks = p0peer.window_hooks(spec, rep, probe.start if probe else None)
    try:
        since, start, end = rigrun.run_window(spec["env"], spec["warmup"], spec["measure"], rep,
                                              **dict({"on_play": None}, **hooks))
    except Stopped:
        raise
    except Exception as ex:  # one run's boundary: its row is written as failed, and the block goes on
        row, run = p0results.run_row(spec, None, context, [str(ex)], "run failed"), None
    else:
        row, run = p0stepped.graded(measured(spec, start, end), since, start, end,
                                    lambda: p0stepped.cell_lines(spec, since), context, peer)
    if probe:
        row["machine_state"] = probe.state()
    p0peer.mark_rows([row], peer)
    return row, run


def run_rows(spec, rep, context):
    """[(row, run)] of one planned run: run_spec's one pair, or run_stepped's one per step."""
    return p0stepped.run_stepped(spec, rep, context) if spec.get("stepped") else [run_spec(spec, rep, context)]


def row_text(spec, row):
    """A run's report line: its spec, then 'accepted' with each cell's work ms/s, or why it was not accepted."""
    point = " ".join(f"{key}={value}" for key, value in (row.get("point") or spec.get("point") or {}).items())
    head = f"{spec['family']} {spec['case']} {spec['arm']} {point} r{spec['repeat']} (key {spec['key']})"
    if row["accepted"]:
        work = ", ".join(f"{m['cell']} {m['value']:.1f}" for m in row["metrics"]
                         if m["name"] == "work_ms_per_s" and m["stat"] == "mean")
        dropped = f"; dropped window {row['dropped_windows']}" if row["dropped_windows"] else ""
        return f"{head}: accepted, work ms/s {work}{dropped}"
    return f"{head}: not accepted: " + "; ".join(row["problems"] + [row["excluded_reason"] or ""]).strip("; ")


def with_frame_metric(rows):
    """Copies of run rows with a derived work-per-measured-frame metric per cell (work ms/s over frames/s)."""
    out = []
    for row in rows:
        work = [m for m in row.get("metrics") or [] if m["name"] == "work_ms_per_s" and m["stat"] == "mean"]
        fps = {m["cell"]: p0results.metric_value(row, "frames_per_s", "mean", m["cell"]) for m in work}
        extra = [dict(m, name=PER_FRAME, value=m["value"] / fps[m["cell"]], unit="ms/frame") for m in work
                 if fps[m["cell"]]]
        out.append(dict(row, metrics=list(row.get("metrics") or []) + extra))
    return out


class Session:
    """One invocation's rig runs: the report, the host context, the JSONL files it appends to, every run row known
    (Logs/p0 and this invocation's), the block plan and the canary state. Its machine is (build, host) of the context;
    Stopped (exit 2) when the history and the context state two CPUs for one host."""

    def __init__(self, args, stamp, rep, context, history):
        self.args, self.rep, self.context, self.build = args, rep, context, context["build"]
        self.host, self.stamp = context.get("host"), stamp
        self.family = "accept" if args.command == "accept-a" else args.command
        self.rows = p0results.rows_of_type(history, "run")
        problems = p0results.cpu_problems(self.rows + [context])
        if problems:
            raise Stopped(2, "; ".join(problems))
        self.mine = []          # indices into self.rows of this invocation's block rows
        self.plan = []          # the whole block plan (block()): the verdict counts every one of its keys
        self.since_good = []    # block specs run since the last good canary, earlier invocations' included (block())
        self.redecide = False   # the last checkpoint's canary drifted, so the third repeats are decided again
        self.peer = False       # the block plan has a peer spec (block()): B is quieted before every canary
        self.canaries = args.command in CANARY_FAMILIES and not args.no_canary
        self.rows_path = self.path(p0results.FAMILY_GAME.get(self.family, "neutral"))
        self.canary_path = os.path.join(P0_LOGS, p0results.file_name("neutral", "canary", stamp))
        self.last_good, self.last_canary = canary_state(self.rows, self.build, self.host)

    def path(self, game):
        """The JSONL file of this invocation's block rows of one game (a spec's own game names its file)."""
        return os.path.join(P0_LOGS, p0results.file_name(game, self.family, self.stamp))

    def ours(self, rows):
        """The rows of this invocation's machine, (build, host)."""
        return [row for row in rows if p0results.machine(row) == (self.build, self.host)]

    def run(self, spec, label):
        """Runs one block spec (run_rows: one row, or one per step of a stepped spec), appends its rows to the file of
        their game and notes them; returns the last (row, run). Raises Stopped (exit 3) after writing them when a row
        carries p0peer.PEER_STOP (B may have kept a bot or the sink; the key is not written)."""
        pairs = run_rows(spec, self.rep, self.context)
        row = pairs[0][0]
        if spec.get("third_reason") and row["accepted"]:
            rows = with_frame_metric(p0results.apply_drift(self.ours(self.rows) + [row]))
            try:
                row["replaces_repeat"] = p0plans.replaced_repeat(spec, rows, THIRD[self.family][0])
            except ValueError as ex:
                self.rep.note(f"    replaces_repeat not set (its fit falls back to delete-one-run groups): {ex}")
        stop = None
        for row, _ in pairs:
            stop = row.pop(p0peer.PEER_STOP, None) or stop
            p0results.write_rows(self.path(row["game"]), [row])
            self.mine.append(len(self.rows))
            self.rows.append(row)
            self.rep.note(f"{label} {row_text(spec, row)}")
        if stop:
            raise Stopped(3, stop)
        return pairs[-1]

    def canary_once(self, why):
        """Runs one check canary and appends its row: "ok", "drift" (more than 5 % from the build's baseline) or
        "unaccepted". Raises Stopped (exit 2) when the build has no baseline. With a peer in the block, B's bots and
        sink are stopped first (p0peer.quiet_b: Stopped when one survives)."""
        if self.peer:
            p0peer.quiet_b(self.rep)
        spec = p0plans.canary_plan("check")[0]
        row, _ = run_spec(spec, self.rep, self.context)
        p0results.write_rows(self.canary_path, [row])
        self.rows.append(row)
        if not row["accepted"]:
            self.rep.note(f"canary ({why}) {row_text(spec, row)}")
            return "unaccepted"
        baseline = p0results.canary_baselines(self.rows).get(p0results.machine(row))
        if baseline is None:
            raise Stopped(2, f"no baseline canary on build {row['build'][:12]} host {row.get('host')}: run p0load "
                             f"canary --baseline")
        work = p0results.canary_work(row)
        result = "drift" if p0base.drifted(work, baseline) else "ok"
        self.rep.note(f"canary ({why}): C {work:.1f} ms/s against baseline {baseline:.1f} "
                      f"({abs(work - baseline) / baseline:.1%}): {'DRIFT' if result == 'drift' else 'ok'}")
        if result == "ok":
            self.last_good = self.last_canary = rigrun.now()
        return result

    def canary(self, why):
        """A check canary, repeated at once while it drifts or is not accepted; returns whether it drifted before it
        passed. Raises Stopped (exit 3) on a second drift, two unaccepted canaries in a row, or no pass in 3 tries."""
        results = []
        for _ in range(CANARY_TRIES):
            results.append(self.canary_once(why if not results else f"repeat after a {results[-1]} canary"))
            if results[-1] == "ok":
                return "drift" in results
            if results.count("drift") >= 2:
                raise Stopped(3, "machine state changed: the canary drifted twice")
            if results[-2:] == ["unaccepted", "unaccepted"]:
                raise Stopped(3, "the canary cannot run: two canaries in a row were not accepted")
        raise Stopped(3, f"the canary did not pass in {CANARY_TRIES} tries ({', '.join(results)})")

    def checkpoint(self, why):
        """A scheduled canary; returns the block specs to queue again when it drifted (the runs since the last good
        canary, each once; third repeats are left out, block() decides them again) and sets redecide to whether it
        drifted."""
        again = [dict(spec, requeued=True) for spec in self.since_good
                 if not spec.get("requeued") and not spec.get("third_reason")]
        drifted = self.canary(why)
        self.since_good, self.redecide = [], drifted
        if drifted and again:
            self.rep.note(f"    {len(again)} run(s) since the last good canary are queued again at the end")
        return again if drifted else []

    def still_decided(self, decided):
        """Whether the block's third repeats stay decided after a checkpoint: a drifted canary decides them again."""
        return self.family not in THIRD or (decided and not self.redecide)

    def earlier_runs(self, plan, queue):
        """The plan's specs an earlier invocation on this build ran after the last good canary and this one will not
        run (--resume skipped them): the next canary judges them too, so a drift queues them again."""
        queued, by_key, found = {spec["key"] for spec in queue}, {spec["key"]: spec for spec in plan}, {}
        for row in self.ours(self.rows):
            key, end = row.get("plan_key"), (row.get("window_local") or {}).get("end")
            if key in by_key and key not in queued and end and (
                    self.last_good is None or datetime.datetime.fromisoformat(end) > self.last_good):
                found[key] = by_key[key]
        return list(found.values())

    def thirds(self, plan):
        """The third repeats still owed: p0plans.third_repeat over the whole block plan and this build's drift-applied
        rows, less those with an accepted row, those this invocation ran that failed for their own reasons (not by
        drift) and those it already ran THIRD_RUNS times."""
        metric, thresholds = THIRD[self.family]
        drifted = p0results.apply_drift(self.rows)
        ours = self.ours(drifted)
        done = p0stepped.complete_keys(ours)
        ran = {}
        for index in self.mine:
            ran.setdefault(drifted[index].get("plan_key"), []).append(drifted[index])
        owed = []
        for spec in p0plans.third_repeat(plan, with_frame_metric(ours), metric, thresholds):
            runs = ran.get(spec["key"], [])
            if spec["key"] in done or len(runs) >= THIRD_RUNS or (runs and runs[-1].get("excluded_reason") != "drift"):
                continue
            owed.append(spec)
        return owed

    def block(self, specs, plan=None):
        """Runs the block's specs (plan: the whole block plan when --resume left some out; default the specs) with its
        canaries and third repeats; returns the verdict's exit code (0 every planned run accepted, 1 not). The runs an
        earlier invocation made after the last good canary are queued again with this one's after a drift; third
        repeats are decided over the whole plan once the queue is done, and again after a drifted canary."""
        self.plan = list(specs if plan is None else plan)
        self.peer = any(spec.get("peer") for spec in self.plan)
        queue, decided = list(specs), self.family not in THIRD
        self.since_good = self.earlier_runs(self.plan, queue)
        if self.canaries and (self.last_canary is None or rigrun.now() - self.last_canary >= CANARY_EVERY):
            queue += self.checkpoint("the block's start")
            decided = self.still_decided(decided)
        while True:
            while queue:
                if self.canaries and rigrun.now() - self.last_canary >= CANARY_EVERY:
                    queue += self.checkpoint("2 h since the last canary")
                    decided = self.still_decided(decided)
                spec = queue.pop(0)
                self.run(spec, f"[{len(self.mine) + 1}/{len(self.mine) + len(queue) + 1}]")
                self.since_good.append(spec)
            if not decided:
                decided = True
                queue = self.thirds(self.plan)
                self.rep.note(f"third repeats: {len(queue)}")
                if queue:
                    continue
            if not self.canaries or not self.since_good:   # nothing ran since the last canary: nothing to judge
                break
            queue = self.checkpoint("the block's end")
            decided = self.still_decided(decided)
        return self.verdict()

    def verdict(self):
        """0 when, on this machine and after canary drift, the last run of every plan key of the block (the whole plan,
        so under --resume the runs an earlier invocation made count, and this invocation's runs, third repeats
        included) is accepted, every step of a stepped run, else 1 (each missed one listed)."""
        drifted = p0results.apply_drift(self.rows)
        by_key = {spec["key"]: spec for spec in self.plan}
        keys = list(dict.fromkeys(list(by_key) + [drifted[index].get("plan_key") for index in self.mine]))
        wanted, mine = set(keys), set(self.mine)
        last = dict(p0stepped.run_groups([row for index, row in enumerate(drifted) if row.get("plan_key") in wanted
                                          and (self.ours([row]) or index in mine)]))
        missed = 0
        for key in keys:
            group = last.get(key)
            if group is None:
                spec = by_key[key]
                self.rep.note(f"not run: {spec['family']} {spec['case']} {spec['arm']} r{spec['repeat']} (key {key})")
            elif p0stepped.missing_steps(group):
                row = group[0]
                self.rep.note(f"not accepted: {row['family']} {row['case']} {row['arm']} r{row['repeat']} "
                              f"(key {key}): {p0stepped.why_not(group)}")
            else:
                continue
            missed += 1
        self.rep.note(f"{len(keys) - missed} of {len(keys)} planned runs accepted")
        return 1 if missed else 0


def baseline_block(session, specs):
    """canary --baseline: two baseline runs, and a third when their C work differs by more than 2.5 %."""
    rows = [session.run(spec, f"[{i}/2]")[0] for i, spec in enumerate(specs, 1)]
    works = [p0results.canary_work(row) for row in rows if row["accepted"]]
    if len(works) == 2 and p0plans.baseline_extra(works):
        session.rep.note(f"the two baselines differ by more than 2.5 % ({works[0]:.1f}, {works[1]:.1f}): a third run")
        session.run(p0plans.canary_plan("baseline", 3)[2], "[3/3]")
    return session.verdict()


def accept_checks(session, runs, rows):
    """Accept-a's part A and B checks and the overhead verdict over this invocation's runs; returns whether all pass."""
    ok = True
    for case, check in (("instrument", p0accept.accept_instrument_checks), ("puppets", p0accept.accept_puppet_checks)):
        if any(spec["case"] == case for spec, _ in runs):
            for passed, text in check({spec["arm"]: run for spec, run in runs if spec["case"] == case and run}):
                session.rep.note(f"{'ok  ' if passed else 'FAIL'} {text}")
                ok &= passed
    pairs = {}
    for spec, row in rows:
        if spec["arm"].startswith("overhead-") and row["accepted"]:
            value = p0results.metric_value(row, "work_ms_per_frame", "mean", "C")
            pairs.setdefault(spec["repeat"], {})[spec["arm"][-1]] = value
    if any(spec["arm"].startswith("overhead-") for spec, _ in rows):
        try:
            cost = p0accept.overhead([(p["A"], p["B"]) for p in pairs.values() if "A" in p and "B" in p])
            session.rep.note(f"overhead: {cost['mean']:.4f} [{cost['lo']:.4f}, {cost['hi']:.4f}] ms/frame over "
                             f"{cost['n']} pairs: {cost['verdict']} (only NO-GO fails)")
            ok &= cost["verdict"] != p0base.NO_GO
        except p0base.ModelError as ex:
            session.rep.note(f"FAIL overhead: {ex}")
            ok = False
    return ok


def accept_commands(session, parts, stamp):
    """Accept-a parts C and D as subprocesses from the repository root (part C reruns a failed command once), each
    part's output in Logs/p0/accept_<part>_<stamp>.log; returns whether every command exited 0."""
    ok = True
    for part, label, argv, python in p0plans.accept_commands(parts):
        command = [sys.executable, "-u", *argv] if python else argv
        for attempt in range(2 if part == "C" else 1):
            with open(os.path.join(P0_LOGS, f"accept_{part}_{stamp}.log"), "a", encoding="utf-8") as log:
                log.write(f"=== {label}: {' '.join(command)}\n")
                log.flush()
                try:
                    code = subprocess.call(command, cwd=r.ROOT, stdout=log, stderr=subprocess.STDOUT)
                except OSError as ex:
                    code = f"could not start ({ex})"
            session.rep.note(f"accept-a {part} {label}{' (rerun)' if attempt else ''}: exit {code}")
            if code == 0:
                break
        ok &= code == 0
    return ok


def accept_block(session, specs, stamp):
    """accept-a: the rig parts' runs, their checks and overhead, then parts C and D; 0 when everything passes."""
    runs, rows = [], []
    for index, spec in enumerate(specs, 1):
        row, run = session.run(spec, f"[{index}/{len(specs)}]")
        runs.append((spec, run))
        rows.append((spec, row))
    ok = accept_checks(session, runs, rows)
    ok &= not specs or session.verdict() == 0
    ok &= accept_commands(session, session.args.parts.split(","), stamp)
    session.rep.note(f"accept-a: {'PASS' if ok else 'FAIL'}")
    return 0 if ok else 1


def abgeo_block(session, args, done):
    """abgeo: abload once per sub-block, a canary after each (a drifted one re-runs that sub-block once, at the end);
    returns the exit code from the rows abload appended."""
    queue, again = abgeo_invocations(args, session.rows_path, done), set()
    if session.canaries and (session.last_canary is None or rigrun.now() - session.last_canary >= CANARY_EVERY):
        session.canary("the block's start")
    while queue:
        label, argv = queue.pop(0)
        code = subprocess.call([sys.executable, "-u", ABLOAD, *argv], cwd=r.ROOT)
        session.rep.note(f"abload {label}: exit {code}")
        if code == 2:
            raise Stopped(2, f"abload refused {label} (see its output above)")
        if session.canaries and session.canary(f"after {label}") and label not in again:
            again.add(label)
            queue.append((label, argv))
            session.rep.note(f"    {label} is run again at the end")
    rows = p0results.read_rows(session.rows_path) if os.path.exists(session.rows_path) else []
    session.mine = list(range(len(session.rows), len(session.rows) + len(rows)))
    session.rows += rows
    return session.verdict()
