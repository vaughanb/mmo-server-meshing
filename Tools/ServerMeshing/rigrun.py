"""One rig run, as abload has always run it, shared by abload, p0load and abregrade.

A run: kill leftover rig processes, reset the test characters and clear every building, ask the editor for a Play with
no client and the run's environment, wait the warm-up, measure, stop Play (always, even when the run fails); then read
each cell's logs since Play began and decide whether the run can be used.

    rigrun.run_window(env, warmup, measure, rep)          # (play start, window start, window end); Play always stopped
    rigrun.run_window(env, warmup, measure, rep, on_play) # the same, calling on_play(play start) once Play is up
    rigrun.cell_window(cell, map_id, since, start, end)   # a cell's work figures over the window, and what it logged
    rigrun.run_problems(cells, plan, results, measure)    # why a run cannot be used ([] when it can)
    rigrun.cell_lines(cell, map_id, since)                # every line a cell logged since Play began
    rigrun.start_problems()                               # why no run may start now ([] when one may)
    rigrun.check_env(env)                                 # refuses keys a Play request would drop or no code reads yet
    rigrun.host_context()                                 # host, CPU, cores, OS, power plan, build and kit patches

Layouts of the 600 x 600 m test ground (LAYOUTS, CUT_LENGTH): coarse (one cell), halves (two 300 x 600 m cells),
grid (the registry's 3 x 3 of 200 m, no override) and strips4 (four 150 x 600 m strips W, C, E, NE, west to east).

Every call to the rig goes through a module attribute (r.zone_lines, rigrun.run_window), never a from-import:
abregrade replays saved reports by patching those two, and a from-import would bypass the patch.
"""
import datetime
import glob
import hashlib
import os
import platform
import re
import subprocess
import time

import p0results
import p0tools
import regress as r
import rigtools

GRID_IDS = [("SW", 0, 0), ("S", 1, 0), ("SE", 2, 0), ("W", 0, 1), ("C", 1, 1), ("E", 2, 1), ("NW", 0, 2), ("N", 1, 2), ("NE", 2, 2)]
LAYOUTS = {
    "coarse": [("C", (-300, -300, 600, 600))],
    "halves": [("C", (-300, -300, 300, 600)), ("E", (0, -300, 300, 600))],
    "grid": [(cid, (-300 + 200 * x, -300 + 200 * y, 200, 200)) for cid, x, y in GRID_IDS],
}
CUT_LENGTH = {"coarse": 0, "halves": 600, "grid": 4 * 600}   # metres of internal cut over the 600 m ground
GROUND = 600.0
PARK = (-290, 290)       # where db_reset leaves the test characters; no client logs in during these runs
MONSTER_ENV = {"off": {"MESH_NO_MONSTERS": "1"}, "edge": {}, "all": {"MESH_MONSTER_AREAS": "all"}}
STOP_POLL_S = 20           # run_window: seconds between stop_when calls during the measurement
LINKED = re.compile(r"Neighbour zone (\S+) linked")   # MeshMapNetworkManager_Ghosting: a neighbour's link said hello

# Four 150 m strips on registry ids, west to east; C and E keep the halves' cut at x = 0, and C is in every layout.
LAYOUTS["strips4"] = [("W", (-300, -300, 150, 600)), ("C", (-150, -300, 150, 600)), ("E", (0, -300, 150, 600)),
                      ("NE", (150, -300, 150, 600))]
CUT_LENGTH["strips4"] = 3 * 600

CORES_SCRIPT = ("$cores = 0; $logical = 0; Get-CimInstance Win32_Processor | ForEach-Object { "
                "$cores += $_.NumberOfCores; $logical += $_.NumberOfLogicalProcessors }; '{0} {1}' -f $cores, $logical")
KIT_PATCH_MARK = "MESH-EDIT: CE-"


class Report:
    """Report is the run's text: each line is printed when it happens and kept for the file in Logs/."""

    def __init__(self, header):
        self.lines = [header]

    def note(self, text=""):
        self.lines.append(text)
        print(text, flush=True)

    def text(self):
        return "\n".join(self.lines)


def layout_override(name):
    """The MESH_LAYOUT_OVERRIDE value for a layout ('' for the registry's own grid)."""
    if name == "grid":
        return ""
    cells = ";".join(f"{cid}@{x:g},{z:g},{w:g},{h:g}" for cid, (x, z, w, h) in LAYOUTS[name])
    return f"{r.MAP}={cells}"


def now():
    return datetime.datetime.now().replace(microsecond=0)


def run_window(env, warmup, measure, rep, on_play=None, stop_when=None):
    """One Play session with no client: returns (play start, window start, window end) as datetimes. Play is always
    stopped, and a failure is raised to the caller only after that, so one broken run never leaves the editor playing.
    on_play, when given, is called as on_play(play start) once Play is up (after wait_play, before the warm-up), so
    what must start after Play (step 8b's sink and bots: leftover bots are killed first) starts there; an exception
    it raises stops Play and is re-raised (step 8a decision 5). stop_when, when given, is asked as
    stop_when(play start, window start) every STOP_POLL_S seconds of the measurement; True ends it there (a stepped
    run whose step saturated), and the window end is that moment."""
    r.kill_bots()
    leftover = r.kit_process_count()
    if leftover:
        still = r.kill_all_kit_processes()
        rep.note(f"    {leftover} leftover rig process(es) killed before the run")
        if still:
            raise RuntimeError(f"{still} leftover rig process(es) survived being killed")
    if os.path.exists(r.HOLD):
        os.remove(r.HOLD)   # left by an interrupted two-machine run
    play_start = now()
    try:
        r.db_reset(PARK, (0, 0), clear_buildings=True)
        r.request_play("none", 0, 0, env=env, enabled=False)
        r.wait_play(120)
        if on_play is not None:
            on_play(play_start)
        time.sleep(warmup)
        start = now()
        if stop_when is None:
            time.sleep(measure)
        else:
            measure_until(play_start, start, measure, stop_when)
        end = now()
        if not r.play_still_running():
            raise RuntimeError("Play ended, or another Play replaced it, during the measurement")
    finally:
        survivors = r.stop_play()
    if survivors:
        raise RuntimeError(f"{survivors} rig process(es) outlived Play")
    return play_start, start, end


def cell_window(cell, map_id, since, start, end):
    """A cell's frame-weighted work figures over [start, end], and what it logged since Play began: its own-cell lines,
    the load generator's spawn counts, and its exception lines."""
    lines = r.zone_lines(cell, since.strftime("%H:%M:%S"), since.strftime("%Y-%m-%d"), map_id)
    windows = [w for w in (rigtools.parse_work_window(line) for line in lines) if w]
    own = [o for o in (rigtools.parse_own_cell(line) for line in lines) if o and o[0] == cell]
    spawned_re = re.compile(r"Load generator: spawned (\d+) synthetic players on cell " + re.escape(cell) + " ")
    spawned = [int(m.group(1)) for m in (spawned_re.search(line) for line in lines) if m]
    linked = {m.group(1) for m in (LINKED.search(line) for line in lines) if m}
    return {"stats": rigtools.aggregate_windows(windows, start, end), "own": own, "spawned": spawned, "linked": linked,
            "exceptions": sum(1 for line in lines if "Exception" in line)}


def run_problems(cells, plan, results, measure, allow_uncapped=False):
    """Why a run cannot be used (empty when it can): see the docstring's list."""
    problems = []
    for cid, rect in cells:
        res = results[cid]
        if rect is not None:
            touching = {other for other, other_rect in cells if other != cid and rigtools.rects_touch(rect, other_rect)}
            unlinked = sorted(touching - res["linked"])
            if unlinked:
                problems.append(f"{cid} never linked with {', '.join(unlinked)}")
        if len(res["own"]) != 1:
            problems.append(f"{cid} started {len(res['own'])} times")
        elif rect is not None and any(abs(a - b) > 0.5 for a, b in zip(res["own"][0][2], rect)):
            problems.append(f"{cid} ran x/z/size {res['own'][0][2]}, not the layout's {rect}")
        want = plan.get(cid, 0)
        if want and res["spawned"] != [want]:
            problems.append(f"{cid} spawned {res['spawned'] or 'nothing'}, planned {want}")
        if res["exceptions"]:
            problems.append(f"{cid} logged {res['exceptions']} exception line(s)")
        s = res["stats"]
        if s is None:
            problems.append(f"{cid} logged no work window inside the measurement")
            continue
        if s["seconds"] < 0.8 * measure:
            problems.append(f"{cid}'s work windows cover {s['seconds']:.0f} s of the {measure} s measurement, need {0.8 * measure:.0f}")
        if "Invalid" in s["probe"]:
            problems.append(f"{cid}'s work probe went Invalid")
        if not allow_uncapped and s["budget"] <= 0:
            problems.append(f"{cid} ran uncapped, so its work per frame is not comparable")
    return problems


def cpu_name():
    try:
        return r.ps("(Get-CimInstance Win32_Processor | Select-Object -First 1).Name")
    except RuntimeError:
        return os.environ.get("PROCESSOR_IDENTIFIER", "?")


def cell_lines(cell, map_id, since):
    """Every line a cell logged since Play began (since = the run's play start): the read cell_window makes."""
    return r.zone_lines(cell, since.strftime("%H:%M:%S"), since.strftime("%Y-%m-%d"), map_id)


def start_problems():
    """Why no rig run may start now, as the lines to print before refusing; empty when a run may start.

    Stops at the first of four single-line refusals (no player build, the editor already playing, script compile
    errors, a stale build), reading the editor's status only when the build exists; only then lists every preflight
    problem (another server holding the rig's ports), one 'cannot run: ...' line each."""
    if not os.path.exists(r.EXE):
        return [f"no player build at {r.EXE}"]
    status = r.read_status()
    if status.get("isPlaying") == "True":
        return ["the editor is already in Play mode; stop it first"]
    if status.get("compileFailed") == "True":
        return ["the editor reports script compile errors; fix them first (see the editor console)"]
    stale = rigtools.build_staleness(r.ROOT, r.BUILD_MANIFEST)
    if stale:
        return ["the player build does not match the working tree: " + "; ".join(stale)
                + ". Run: python Tools/ServerMeshing/regress.py --build"]
    return [f"cannot run: {problem}" for problem in r.preflight()]


def check_env(env):
    """Returns None when a Play environment may be sent; raises ValueError naming every key regress.ENV_KEYS does not
    hold (request_play would drop it silently), every dropped key with its reason (p0tools.DROPPED_KEYS) and every
    stage 0b/0c key no code reads yet (p0tools.PENDING_KEYS). Values are not checked."""
    pending = sorted(key for key in env if key in p0tools.PENDING_KEYS)
    dropped = sorted(key for key in env if key in p0tools.DROPPED_KEYS)
    unknown = sorted(key for key in env if key not in r.ENV_KEYS and key not in p0tools.PENDING_KEYS
                     and key not in p0tools.DROPPED_KEYS)
    problems = []
    if len(unknown) == 1:
        problems.append(f"{unknown[0]} is not in regress.ENV_KEYS, so a Play request would drop it silently")
    elif unknown:
        problems.append(f"{', '.join(unknown)} are not in regress.ENV_KEYS, so a Play request would drop them silently")
    for key in dropped:
        problems.append(f"{key} was {p0tools.DROPPED_KEYS[key]}")
    for key in pending:
        stage, _, step = p0tools.PENDING_KEYS[key].partition(", ")
        problems.append(f"{key} is a {stage} key ({step}) that no code reads yet")
    if problems:
        raise ValueError("; ".join(problems))


def build_id():
    """The sha1 hex of the player build's manifest (a JSONL row's 'build'), or '' when there is no manifest."""
    if not os.path.exists(r.BUILD_MANIFEST):
        return ""
    with open(r.BUILD_MANIFEST, "rb") as f:
        return hashlib.sha1(f.read()).hexdigest()


def kit_patch_texts():
    """The text of every Assets/**/*.cs file that carries a kit patch marker (MESH-EDIT: CE-)."""
    texts = []
    for path in sorted(glob.glob(os.path.join(r.ASSETS, "**", "*.cs"), recursive=True)):
        with open(path, encoding="utf-8", errors="replace") as f:
            text = f.read()
        if KIT_PATCH_MARK in text:
            texts.append(text)
    return texts


MACHINE_EPOCH_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "Logs", "p0", "MACHINE_EPOCH")


PERF_COUNTER = r"\Processor Information(_Total)\% Processor Performance"


class MachineProbe:
    """What the machine was doing over one run's measurement, for a canary row's machine_state (A's canary moves with
    states no setting names: idle, in use over RDP, night): start(play_start), an on_play, notes this Windows session
    (qwinsta's current line, e.g. "rdp-tcp#0 brend 1 Active") and starts one PowerShell that waits out the warm-up, then
    samples the CPU's % processor performance every 5 s over the measurement; state() reaps it and returns {"session",
    "perf_pct"} (the samples' mean). It never fails a run: what it cannot read is None."""

    def __init__(self, warmup, measure):
        self.warmup, self.measure, self.proc, self.session = warmup, measure, None, None

    def start(self, play_start):
        try:
            sessions = subprocess.run(["qwinsta"], capture_output=True, text=True).stdout.splitlines()
            self.session = next((" ".join(line[1:].split()) for line in sessions if line.startswith(">")), None)
            script = (f"Start-Sleep -Seconds {self.warmup}; (Get-Counter '{PERF_COUNTER}' -SampleInterval 5 "
                      f"-MaxSamples {max(1, int(self.measure) // 5)} | ForEach-Object "
                      "{ $_.CounterSamples[0].CookedValue }) -join ' '")
            self.proc = subprocess.Popen(["powershell", "-NoProfile", "-Command", script], stdout=subprocess.PIPE,
                                         stderr=subprocess.DEVNULL, text=True)
        except OSError:
            self.proc = None

    def state(self):
        perf = None
        if self.proc is not None:
            try:
                values = [float(value) for value in self.proc.communicate(timeout=30)[0].split()]
                perf = round(sum(values) / len(values), 1) if values else None
            except (subprocess.TimeoutExpired, ValueError):
                self.proc.kill()
        return {"session": self.session, "perf_pct": perf}


def measure_until(play_start, start, measure, stop_when):
    """Sleeps out measure seconds in STOP_POLL_S naps, returning early once stop_when(play_start, start) is True (it is
    not asked after the last nap). The naps count, not the clock, so the time stop_when takes only lengthens the run."""
    left = measure
    while left > 0:
        nap = min(STOP_POLL_S, left)
        time.sleep(nap)
        left -= nap
        if left > 0 and stop_when(play_start, start):
            return


def machine_host(epoch_file=MACHINE_EPOCH_FILE):
    """The host a row is keyed on: COMPUTERNAME, plus "#<epoch>" when Logs/p0/MACHINE_EPOCH names one. An epoch marks
    a change of the machine's state that the canary shows but no setting names (2026-10-06 on A: the same build and
    power plan read 68.6 ms/s for a day, then 80-83 after an editor restart and a reboot), so rows of two epochs are
    keyed apart like two machines: never pooled, never resumed past, each with its own baseline."""
    host = os.environ.get("COMPUTERNAME", "?")
    try:
        with open(epoch_file, encoding="utf-8") as f:
            epoch = f.read().strip()
    except FileNotFoundError:
        return host
    if epoch and any(ch.isspace() or ch == "#" for ch in epoch):
        raise RuntimeError(f"{epoch_file}: an epoch is one word without '#', got {epoch!r}")
    return f"{host}#{epoch}" if epoch else host


def host_context():
    """Where the runs happen, for every JSONL run row: host, cpu, cores, logical, os, power_plan, build (build_id) and
    kit_patches ({"CE-6": "present", ...}). Runs PowerShell and powercfg and reads every Assets/**/*.cs, so it is called
    once per invocation and never in a dry run; a command that fails raises RuntimeError."""
    counts =r.ps(CORES_SCRIPT).split()
    if len(counts) != 2 or not all(count.isdigit() for count in counts):
        raise RuntimeError(f"Win32_Processor gave no core counts: {' '.join(counts)!r}")
    power = subprocess.run(["powercfg", "/getactivescheme"], capture_output=True, text=True)
    if power.returncode != 0:
        raise RuntimeError(f"powercfg /getactivescheme failed ({power.returncode}): "
                           f"{(power.stderr or power.stdout).strip()[:300]}")
    return {"host": machine_host(), "cpu": cpu_name(), "cores": int(counts[0]),
            "logical": int(counts[1]), "os": platform.platform(),
            "power_plan": p0results.parse_power_plan(power.stdout), "build": build_id(),
            "kit_patches": p0results.kit_patches(kit_patch_texts())}
