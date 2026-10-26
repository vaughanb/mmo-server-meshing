"""The dynamic-cell soak for regress.py (DESIGN 0.91): hours of automatic splits and merges under kills and restarts.

    python Tools/ServerMeshing/regress.py soak [--soak-minutes 180] [--soak-kill-every 10] [--soak-central-every 60]
    python Tools/ServerMeshing/regress.py soak --remote-cells C     # C's family on machine B (DESIGN 0.92)

On the split-process topology (regress_procs: the central, login and database manager one build process, the spawner
another, the editor's Play a client only and no tester), the test map runs Dynamic with automatic split and merge on
(autosplit's triggers: dwell 20 s, cooldown 30 s, merge after 30 s cool). Load: rigsoakload.BurnPlan moves the burn
file from cell to cell, 40 synthetic players wander C's inner 200 m square, four bots stand (two in C, one in E, one in
N) and two patrol across C's middle and the C/E line. Every --soak-kill-every minutes one random live child is killed
(the supervisor restarts it), half an interval out of step with the central restarts: every --soak-central-every
minutes the central is killed and started again (not in the last 10 minutes). The harness is the operator too: a family
the central freezes (a child that died around a restart gives no hello, DESIGN 0.85) or a frozen operation gets
`unfreeze <base>` OPERATOR_DELAY_S after its line, as an operator answering the alert would. At the end the burn is
turned off and the families are given DRAIN_S to merge back whole.

Graded over the whole run (regress_soakgrade): every operation ended (done, aborted, ended) and none took longer than
STUCK_S; no frozen operation or family left unresolved; at every layout pair no rectangle with two owners, and the
done operations' pairs owned as riglayout derives them; the families whole and no operation stored at the end; the
fenced set within MeshFencedSet's 16 per cell; the store, its retired entries and every long-lived process's memory
(the OS's private bytes, sampled every SAMPLE_S, and the cells' Mesh stats) flat after warm-up; bots never lost a
session except after a kill, and all still running; regress.grade_common's 0 exceptions and 0 player teleports. The
counts (splits, merges, collapses, restarts, refusals) and trends are written to Logs/regress_soak_report_<stamp>.txt.

Across two machines (--remote-cells, spawner mode, DESIGN 0.90) the remote families start, die and restart on B: the
kills go to B's processes (rig.cell_ps), B's processes are sampled too (roles tagged 'B:'), and B's logs, shifted by the
measured clock offset, are fetched before grading.

regress.py hands itself in as `rig`, as for the other scenario modules; the readers are rigsoak's and rigsoakload's.
"""
import collections
import datetime
import json
import os
import random
import time

import provision
import regress_auto
import regress_central
import regress_procs
import regress_remote
import regress_soakgrade
import regress_split
import rigmerge
import rigcentral
import rigpolicy
import rigprocs
import rigsoak
import rigsoakload
import rigsplitcrash
import remotespawner

SCENARIOS = ("soak",)
PERSIST_REASON = regress_split.PERSIST_REASON
EXPECTED_ZONES = {"soak": {"SW", "S", "SE", "W", "C", "E", "NW", "N", "NE", "C2"}}
SETTINGS = {"minutes": 180.0, "kill_every": 10.0, "central_every": 60.0}   # regress.py --soak-* set these
SEED = 91
TICK_S = 2.0
SAMPLE_S = 60.0
PROGRESS_S = 300.0
DRAIN_S = 900.0             # after the burn goes off: the families merge back whole within this
CENTRAL_QUIET_S = 600.0     # no central restart this close to the end
STAND_TIMEOUT_S = 180.0
CENTRAL_DOWN_S = regress_central.CENTRAL_DOWN_S
OPERATOR_DELAY_S = 30.0     # a frozen family's line to the operator's unfreeze
PARK = (-50.0, 82.0)        # the tester's and meshbot's rows (neither logs in)
ENV = dict(regress_auto.AUTOSPLIT_ENV, MESH_LOAD_PLAN="C:40", MESH_LOAD_AREA="0,0,100", MESH_LOAD_SEED="7")
Bot = collections.namedtuple("Bot", "user mode point return_x")
BOTS = (Bot("meshbot2", "stand", (-50.0, 70.0), None), Bot("meshbot3", "stand", (50.0, -70.0), None),
        Bot("meshbot4", "stand", (160.0, 60.0), None), Bot("meshbot5", "stand", (-60.0, 160.0), None),
        Bot("meshbot6", "patrol", (-70.0, -30.0), 70.0), Bot("meshbot7", "patrol", (40.0, 30.0), 160.0))
SAMPLE_SCRIPT = ("Get-CimInstance Win32_Process -Filter \"Name='kit-ce-project.exe'\" | ForEach-Object { "
                 "\"$($_.ProcessId)`t$($_.PrivatePageCount)`t$($_.WorkingSetSize)`t$($_.CommandLine)\" }")
# What run() leaves for the graders: the status lines with the wall time each was first seen, the kills [(t, cell)],
# the central restarts [(t, ok, detail)], the process samples [(t, rows)], the store samples [(t, stats)], the bots'
# logs {user: path} and stamped lines {user: [(t, line)]}, the burn plan, the soak's start, the drain's end (None when
# the families were not whole in time), the report's path, the SETTINGS it ran with and the operator's unfreezes
# [(t, base, why)].
SoakRun = collections.namedtuple("SoakRun", "name stamped kills restarts samples store_samples bot_logs bot_lines plan "
                                            "began drained report settings unfreezes")


class Tail:
    """New lines of a growing file, each stamped with the wall time it was read (bot logs and the status file carry none)."""

    def __init__(self, path):
        self.path, self.offset, self.rest, self.lines = path, 0, "", []

    def read(self, now):
        try:
            with open(self.path, "rb") as f:
                f.seek(self.offset)
                data = f.read()
        except OSError:
            return []
        self.offset += len(data)
        text = self.rest + data.decode("utf-8", errors="replace")
        parts = text.split("\n")
        self.rest = parts.pop()
        new = [(now, p.rstrip("\r")) for p in parts if p.strip()]
        self.lines.extend(new)
        return new


class Events:
    """The run's event log, appended as things happen (Logs/regress_soak_events_<stamp>.txt), so an interrupted soak
    still says what it did."""

    def __init__(self, path):
        self.path = path
        with open(path, "w", encoding="utf-8") as f:
            f.write(f"soak events, started {datetime.datetime.now():%Y-%m-%d %H:%M:%S}\n")

    def add(self, now, text):
        with open(self.path, "a", encoding="utf-8") as f:
            f.write(f"{datetime.datetime.fromtimestamp(now):%H:%M:%S} {text}\n")


class Operator:
    """The soak's operator: reads the central's kit log as it grows and answers a frozen family, or a frozen operation,
    with `<seq> unfreeze <base>` in the command file OPERATOR_DELAY_S later (once per freeze, one command at a time)."""

    def __init__(self, rig):
        self.rig, self.tails, self.due, self.done, self.seq, self.bases = rig, {}, [], [], 0, {}

    def _new_lines(self, now):
        day = datetime.date.today().isoformat()
        out = []
        for sev in ("info", "warn", "error"):
            path = rigprocs.kit_log_path(self.rig.LOGS, rigprocs.CENTRAL_LOG_PREFIX, day, sev)
            tail = self.tails.get(path)
            if tail is None:
                tail = self.tails[path] = Tail(path)
                tail.offset = os.path.getsize(path) if os.path.exists(path) else 0   # only what this soak adds
            out += [line for _, line in tail.read(now)]
        return out

    def tick(self, now, events):
        for line in self._new_lines(now):
            m = rigcentral.FROZEN_FAMILY.search(line)
            text = rigcentral.payload(line)
            op = rigsoak.FROZEN_OP.match(text)
            requested = rigsoak.OP_REQUESTED.match(text)
            if requested:
                self.bases[requested.group(2)] = requested.group(3).split(".")[0]   # a child's id is <base>.<n>
            if m and m.group(2) == self.rig.MAP:
                self.due.append((now + OPERATOR_DELAY_S, m.group(1), f"family frozen: {m.group(3)[:80]}"))
            elif op and op.group(2) in self.bases:
                self.due.append((now + OPERATOR_DELAY_S, self.bases[op.group(2)], f"{op.group(1)} frozen: {text[:80]}"))
        ready = [d for d in self.due if d[0] <= now]
        if ready and not os.path.exists(self.rig.LAYOUT_COMMAND):   # one at a time: the central takes the file away
            item = ready[0]
            self.due.remove(item)
            _, base, why = item
            self.seq += 1
            regress_split.write_command(self.rig.LAYOUT_COMMAND, f"soak{self.seq} unfreeze {base}")
            self.done.append((now, base, why))
            events.add(now, f"operator: unfreeze {base} ({why})")


def read_store(rig):
    """(parsed store, its size in bytes), or (None, 0) while it is missing or being replaced."""
    try:
        size = os.path.getsize(rig.COORDINATOR_STORE)
        with open(rig.COORDINATOR_STORE, encoding="utf-8-sig") as f:
            return json.load(f), size
    except (OSError, ValueError):
        return None, 0


def remote_spawner(rig):
    """Whether the other machine's spawner serves families of this run (regress.py --remote-cells, spawner mode)."""
    return bool(rig.REMOTE_CELLS) and rig.REMOTE_MODE == regress_remote.SPAWNER


def tag_rows(rows, host):
    """parse_process_rows' rows with each role prefixed '<host>:' (the other machine's processes: their pids are its own)."""
    return [(pid, f"{host}:{role}", private, ws) for pid, role, private, ws in rows]


def sample_processes(rig, events, now):
    """The rig's processes on this machine and, in a two-machine run, the other's (tagged 'B:'); a host whose sample
    failed is noted in the events and left out of this sample."""
    rows = []
    try:
        rows += rigsoakload.parse_process_rows(rig.ps(SAMPLE_SCRIPT))
    except RuntimeError as ex:
        events.add(now, f"process sample failed: {str(ex)[:100]}")
    if remote_spawner(rig):
        try:
            rows += tag_rows(rigsoakload.parse_process_rows(remotespawner.run_script(regress_remote.args(), SAMPLE_SCRIPT)), "B")
        except RuntimeError as ex:
            events.add(now, f"B's process sample failed: {str(ex)[:100]}")
    return rows


def launch(rig, name, bots, observe_s):
    logs = {}
    for b in BOTS:
        args = ["-meshAutoMode", b.mode, "-meshWalkX", f"{b.point[0]:g}", "-meshWalkZ", f"{b.point[1]:g}"]
        if b.return_x is not None:
            args += ["-meshReturnX", f"{b.return_x:g}"]
        logs[b.user] = rig.launch_bot(name, b.user, args, channel="C", observe_seconds=observe_s)
        bots.append((b.mode, logs[b.user]))
        time.sleep(1.5)
    return logs


def wait_started(logs, timeout):
    """{user: True} once each bot logged 'Standing at' or 'Patrolling between', or as far as it got at the timeout."""
    end = time.time() + timeout
    while True:
        found = {u: any("Standing at" in line or "Patrolling between" in line for line in regress_split.read_lines(p))
                 for u, p in logs.items()}
        if all(found.values()) or time.time() >= end:
            return found
        time.sleep(3)


def run(name, rep, rig, start, day, bots):
    """Runs the soak against the rig (regress) and grades it; appends every bot to `bots`. Returns a SoakRun."""
    minutes = float(SETTINGS["minutes"])
    stamp = datetime.datetime.now().strftime("%Y%m%d_%H%M")
    events = Events(os.path.join(rig.LOGS, f"regress_soak_events_{stamp}.txt"))
    rig.db_reset(PARK, PARK)
    provision.reset_bot_positions(2, len(BOTS), [b.point for b in BOTS], status_path=rig.STATUS)
    burn = regress_auto.burn_path(rig)
    regress_auto.write_burn(burn, "", rig)
    env = dict(ENV, MESH_CELL_MODE=f"{rig.MAP}:Dynamic", MESH_PROBE_BURN_MS="@" + burn)
    rig.request_play("none", PARK[0], PARK[1], env=env, enabled=False)
    rig.wait_play(60)
    time.sleep(20)
    observe_s = minutes * 60.0 + DRAIN_S + 600.0
    logs = launch(rig, name, bots, observe_s)
    started = wait_started(logs, STAND_TIMEOUT_S)
    rep.check(all(started.values()), f"every bot standing or patrolling within {STAND_TIMEOUT_S:g} s ({started})")
    sr = soak(rig, name, burn, logs, minutes, events)._replace(report=os.path.join(rig.LOGS, f"regress_soak_report_{stamp}.txt"))
    if remote_spawner(rig):
        rig.fetch_remote(force=True)   # the other machine's cell and spawner logs as they stand at the end
        rep.notes.append(f"   two machines: {', '.join(rig.REMOTE_CELLS)} and their families on B, B's stamps shifted "
                         f"{rig.REMOTE_CLOCK_SHIFT:+d} s")
    regress_soakgrade.grade(rep, rig, start, day, sr)
    return sr


def soak(rig, name, burn, logs, minutes, events):
    """The main loop: the burn plan, the kills, the central restarts and the samples until the soak's end; then the drain."""
    rng = random.Random(SEED)
    plan = rigsoakload.BurnPlan()
    status, tails = Tail(rig.LAYOUT_COMMAND_STATUS), {u: Tail(p) for u, p in logs.items()}
    stamped, kills, restarts, samples, store_samples = [], [], [], [], []
    began = time.time()
    end = began + minutes * 60.0
    # The kills run half an interval out of step with the central restarts, so a restart never lands on a kill.
    next_kill, next_central = began + SETTINGS["kill_every"] * 30.0, began + SETTINGS["central_every"] * 60.0
    operator = Operator(rig)
    next_sample, next_progress, burns, store = began, began + PROGRESS_S, None, None
    events.add(began, f"soak for {minutes:g} min: kill a child every {SETTINGS['kill_every']:g} min, restart the central "
                      f"every {SETTINGS['central_every']:g} min")
    drained, drain_until = None, None
    while True:
        now = time.time()
        stamped.extend(status.read(now))
        for tail in tails.values():
            tail.read(now)
        operator.tick(now, events)
        parsed, size = read_store(rig)
        if parsed is not None:
            store = (parsed, size)
        fams = rigsoak.families(store[0], rig.MAP) if store else {b: [b] for b in rigsoak.BASES}
        if drain_until is None and now >= end:
            plan.drain(now)
            drain_until = now + DRAIN_S
        wanted = plan.step(now, fams)
        if wanted != burns:
            regress_auto.write_burn(burn, rigpolicy.burn_text(wanted), rig)
            burns = wanted
            events.add(now, f"burn {rigpolicy.burn_text(wanted) or 'off'}")
        while plan.events:
            t, text = plan.events.pop(0)
            events.add(t, text)
        if drain_until is None and now >= next_kill and store:
            kids = rigsoak.children(store[0], rig.MAP)
            if kids:
                cell = rng.choice(kids)
                try:
                    killed = rigmerge.parse_count(rig.cell_ps(cell, rigsplitcrash.kill_script))
                except (RuntimeError, ValueError) as ex:
                    killed, cell = 0, f"{cell} ({str(ex)[:80]})"
                events.add(now, f"kill {cell}: {killed} process(es)")
                if killed:
                    kills.append((now, cell))
                    next_kill += SETTINGS["kill_every"] * 60.0
        if drain_until is None and now >= next_central and end - now > CENTRAL_QUIET_S:
            events.add(now, "central restart")
            try:
                killed, lifetime, kill_at = regress_procs.restart_central(rig, name, CENTRAL_DOWN_S)
                restarts.append((kill_at, killed == 1, f"killed {killed}, lifetime {lifetime}"))
            except (RuntimeError, ValueError) as ex:
                restarts.append((now, False, str(ex)[:120]))
            events.add(time.time(), f"central restart: {restarts[-1][2]}")
            next_central += SETTINGS["central_every"] * 60.0
        if now >= next_sample:
            next_sample += SAMPLE_S
            samples.append((now, sample_processes(rig, events, now)))
            if store:
                store_samples.append((now, rigsoak.store_stats(store[0], store[1])))
        if now >= next_progress:
            next_progress += PROGRESS_S
            counts = rigsoak.op_counts(rigsoak.operations(stamped))
            print(f"   soak {(now - began) / 60:.0f} min: {counts['splits']} splits, {counts['merges']} merges, "
                  f"{counts['collapses']} collapses, {counts['aborted']} aborted, {len(kills)} kills, {len(restarts)} central "
                  f"restarts; episodes {plan.started} ({plan.timed_out} cut short); families "
                  f"{ {b: len(m) for b, m in fams.items() if len(m) > 1} }", flush=True)
        if drain_until is not None:
            whole = all(len(m) == 1 for m in fams.values())
            idle = store is not None and not store[0].get("hasOperation")
            if (whole and idle) or now >= drain_until:
                drained = now if whole and idle else None
                events.add(now, "drained: every family whole, no operation" if drained else f"drain: not whole after {DRAIN_S:g} s")
                break
        time.sleep(TICK_S)
    time.sleep(SAMPLE_S / 2)
    now = time.time()
    stamped.extend(status.read(now))
    for tail in tails.values():
        tail.read(now)
    samples.append((now, sample_processes(rig, events, now)))
    parsed, size = read_store(rig)
    if parsed is not None:
        store_samples.append((now, rigsoak.store_stats(parsed, size)))
    return SoakRun(name, stamped, kills, restarts, samples, store_samples, logs,
                   {u: t.lines for u, t in tails.items()}, plan, began, drained, None, dict(SETTINGS), operator.done)


def grade_layout(rep, rig, start, day, sr):
    regress_soakgrade.grade_layout(rep, rig, start, day, sr)
