"""Two-machine runs for regress.py (--remote-cells; Docs/ServerMeshing/MULTIHOST.md §8b and §8g): the other machine's
cells started and read over SSH through remoteb.py, in one of two modes per scenario.

`cell` (since 2026-09-16): each remote cell is started by hand (remoteb start --restart stands in for a spawner), its
logs in B's build folder. `spawner` (DESIGN 0.90): B runs the kit's map-spawn server as a build process serving the
remote base cells (remoteb spawner-start), so a Dynamic family based on B has its children, restarts and kills there;
its cells log in the spawner's folder, fetched into Logs/remote/rig/. Every scenario of a Dynamic map runs in spawner
mode, the rest in cell mode unless --remote-spawner forces it.

regress.py hands itself in as `rig` (REMOTE_CELLS, REMOTE_MODE, REMOTE_PORT, REMOTE_PERSIST, LOGS, TEMP, ps, EXE, ...),
as the other scenario modules take it; the pure pieces are in_family, cell_env and logs_dir (test_regress_remote.py).
"""
import datetime
import os
import shutil
import subprocess
import sys
import time

import remoteb
import remotespawner
import remotessh
import rigtools

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
TOOL = os.path.join(ROOT, "Tools", "ServerMeshing", "remoteb.py")
CELL, SPAWNER = "cell", "spawner"
READY_S = 150           # a remote cell logged its own cell (or the spawner told the central what it serves)
SPAWNER_LOG_PREFIX = "Log_MapSpawn"   # rigprocs.SPAWNER_LOG_PREFIX: the kit's file log of the spawner
_last_fetch = 0.0


def in_family(zone, bases):
    """Whether a cell belongs to one of the remote base cells: the base itself or a dynamic child of it (runtime ids
    such as C.1 and C.2 carry their base's id before the first dot)."""
    return bool(zone) and (zone in bases or zone.split(".", 1)[0] in bases)


def logs_dir(logs, mode):
    """Where a mode's fetched remote logs live: Logs/remote (cell mode) or Logs/remote/rig (spawner mode), apart, so a
    run that mixes modes never grades one folder's file of a cell as the other's."""
    return os.path.join(logs, "remote", "rig") if mode == SPAWNER else os.path.join(logs, "remote")


def cell_env(env, mode, local_temp, rig_temp):
    """The --env arguments a remote start gets from a Play's environment: everything set but A's own served cells (cell
    mode), or remotespawner.translate_env's view (spawner mode: A-only keys out, A's Temp paths pointed at B's)."""
    if mode == SPAWNER:
        values = remotespawner.translate_env(env, local_temp, rig_temp)
    else:
        values = {k: v for k, v in (env or {}).items() if v and k != "MESH_SPAWN_CELLS"}
    out = []
    for key, value in sorted(values.items()):
        out += ["--env", f"{key}={value}"]
    return out


def args():
    """remoteb's connection settings (remotessh.DEFAULTS, MESH_B_* overrides)."""
    return remotessh.defaults()


def run(*argv, timeout=300):
    """Run remoteb.py and return its output."""
    r = subprocess.run([sys.executable, TOOL] + list(argv), capture_output=True, text=True, timeout=timeout)
    return (r.stdout or "") + (r.stderr or "")


def fetch(rig, force=False):
    """Copy the other machine's logs of the current mode here. Throttled: grading calls this through zone_lines often."""
    global _last_fetch
    if not rig.REMOTE_CELLS:
        return
    if not force and time.time() - _last_fetch < 5:
        return
    _last_fetch = time.time()
    out = run("fetch", *(["--from-rig"] if rig.REMOTE_MODE == SPAWNER else []))
    if "fetched" not in out:
        raise RuntimeError("could not copy the remote machine's logs, so nothing about its cells can be graded: "
                           + " ".join(out.split())[:200])


def watched_logs(rig, day):
    """The fetched files a start waits on: every remote base cell's (cell mode: the first only, as ever) and, in
    spawner mode, the spawner's own kit log."""
    cells = rig.REMOTE_CELLS if rig.REMOTE_MODE == SPAWNER else rig.REMOTE_CELLS[:1]
    paths = [rig.zone_log(c, sfx, day) for c in cells for sfx in ("info", "warn", "error")]
    if rig.REMOTE_MODE == SPAWNER:
        paths.append(os.path.join(logs_dir(rig.LOGS, SPAWNER), f"{SPAWNER_LOG_PREFIX}_{day}.info.log"))
    return paths


def log_mark(rig):
    """How many lines each watched remote log holds right now. A line count taken before Play cannot match the previous
    scenario's tail, and does not care whether the two machines' clocks agree (2026-09-17, seam)."""
    if not rig.REMOTE_CELLS:
        return {}
    fetch(rig, force=True)
    day = time.strftime("%Y-%m-%d")
    mark = {}
    for path in watched_logs(rig, day):
        mark[path] = sum(1 for _ in open(path, encoding="utf-8", errors="replace")) if os.path.exists(path) else 0
    return mark


def lines_after(rig, mark):
    """Every line the watched remote logs gained since the mark."""
    fetch(rig, force=True)
    out = []
    for path in watched_logs(rig, time.strftime("%Y-%m-%d")):
        if not os.path.exists(path):
            continue
        with open(path, encoding="utf-8", errors="replace") as f:
            for i, line in enumerate(f):
                if i >= mark.get(path, 0):
                    out.append(line.rstrip("\n"))
    return out


def running():
    return "process: running" in run("status")


def start(rig, mark, env=None):
    """Get the other machine serving this scenario and wait until it does (regress.start_remote_cells).

    Cell mode restarts the cell for each scenario by default, because each Play recreates this machine's cells; with
    --remote-persist it is left running and must recover on its own (cluster reconnect, databaseReconnectSeconds), so
    the wait looks for all of that. Spawner mode starts B's spawner with the scenario's environment (its cells inherit
    it) and waits for every remote base cell's own-cell line, or for the spawner's word to the central when cells sleep.
    """
    rig.wait_play(60)
    t0 = time.time()
    if rig.REMOTE_MODE == SPAWNER:
        run("stop")
        rig_temp = args().rig.rstrip("\\") + "\\Temp"
        out = run("spawner-start", "--cells", ",".join(rig.REMOTE_CELLS),
                  *cell_env(env, SPAWNER, rig.TEMP, rig_temp))
        if "spawner is running" not in out:
            raise RuntimeError("the remote spawner did not start: " + " ".join(out.split())[:200])
        if (env or {}).get("MESH_SLEEP"):
            wanted = [(r"Told the central this spawner serves", "spawner registered with the central")]
        else:
            wanted = [(r"Own cell from channel id: " + c + r"\b", f"cell {c} is serving") for c in rig.REMOTE_CELLS]
        label = "remote spawner"
    else:
        cell = rig.REMOTE_CELLS[0]
        label = f"remote cell {cell}"
        if rig.REMOTE_PERSIST and running():
            wanted = [(r"App Registered successfully", "registered with the cluster again"),
                      (r"Neighbour zone \w+ linked", "relinked to this machine's cells"),
                      (r"Database connection to \S+ is open", "re-opened its database connection")]
        else:
            run("stop")
            out = run("start", "--cell", cell, "--port", str(rig.REMOTE_PORT), "--restart", *cell_env(env, CELL, None, None))
            if "is running" not in out:
                raise RuntimeError("the remote cell did not start: " + " ".join(out.split())[:200])
            wanted = [(r"Own cell from channel id: " + cell + r"\b", "is serving")]
    deadline = t0 + READY_S
    pending = list(wanted)
    while time.time() < deadline:
        time.sleep(3)
        lines = lines_after(rig, mark)
        for want in list(pending):
            if rig.count(lines, want[0]) >= 1:
                print(f"   {label}: {want[1]} after {time.time() - t0:.0f}s")
                pending.remove(want)
        if not pending:
            return
    raise RuntimeError(f"{label}: never {pending[0][1]}; try: python Tools/ServerMeshing/remoteb.py status")


def cell_ps(rig, cell, script_of):
    """A process snippet (rigmerge.process_count_script, rigsplitcrash.kill_script) for a cell, run where the cell runs:
    on B, against B's executable, for a cell of a remote family in spawner mode; here otherwise."""
    if rig.REMOTE_MODE == SPAWNER and in_family(cell, rig.REMOTE_CELLS):
        return remotespawner.cell_script(args(), script_of, cell)
    return rig.ps(script_of(rig.EXE, cell))


def mirror_temp(rig, name, text):
    """A file the harness writes under Temp for the cells (the burn file) written on B too in spawner mode."""
    if rig.REMOTE_CELLS and rig.REMOTE_MODE == SPAWNER:
        remotespawner.run_script(args(), remotespawner.tempfile_script(args().rig, name, text))


def set_marker(rig, cell, name, kind):
    """A fault marker under the Temp of the machine that runs `cell`: 'file', 'dir' (the fault fires in every
    incarnation) or 'clear'."""
    if rig.REMOTE_MODE == SPAWNER and in_family(cell, rig.REMOTE_CELLS):
        remotespawner.run_script(args(), remotespawner.marker_script(args().rig, name, kind))
        return
    path = os.path.join(rig.TEMP, name)
    if os.path.isdir(path):
        shutil.rmtree(path, ignore_errors=True)
    elif os.path.exists(path):
        os.remove(path)
    if kind == "dir":
        os.makedirs(path)
    elif kind == "file":
        os.makedirs(rig.TEMP, exist_ok=True)
        with open(path, "w", encoding="utf-8") as f:
            f.write("armed later by the harness\n")


def clock_offset(samples):
    """The other machine's clock minus this one's, in seconds, from (sent, its UTC time, received) samples: each read
    against the midpoint of its round trip, the median kept."""
    offsets = sorted((theirs - (sent + received) / 2) for sent, theirs, received in samples)
    return offsets[len(offsets) // 2]


def clock_shift(samples=3):
    """Whole seconds to add to the other machine's log stamps (minus its clock offset, rounded): the graders compare
    its cells' lines with times taken here. Its own logs are never rewritten."""
    readings = []
    for _ in range(samples):
        sent = time.time()
        out = remotespawner.run_script(args(), "[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()")
        readings.append((sent, int(out.strip()) / 1000.0, time.time()))
    return -int(round(clock_offset(readings)))


def ensure_deployed():
    """B runs this machine's build (remoteb deploy when its Assembly-CSharp.dll differs); a message for the log."""
    matches, want, got = remoteb.deployed_matches(args())
    if matches:
        return f"B runs this build ({want[:16]})"
    out = run("deploy", timeout=1800)
    matches, want, got = remoteb.deployed_matches(args())
    if not matches:
        raise RuntimeError("B's build does not match this machine's after a deploy: " + " ".join(out.split())[-200:])
    return f"deployed this build to B ({want[:16]})"


def grade_family_host(rep, rig, start, day):
    """DESIGN 0.90, spawner mode: no cell of a remote family ran on this machine (a family stays on its base's host);
    notes the family cells the other machine ran and what its spawner did with kill requests."""
    days = {day, (datetime.date.fromisoformat(day) + datetime.timedelta(days=1)).isoformat()}
    since = f"{day} {start}"
    remote_since = (datetime.datetime.fromisoformat(since)
                    - datetime.timedelta(seconds=rig.REMOTE_CLOCK_SHIFT)).strftime("%Y-%m-%d %H:%M:%S")
    here = [c for c, _ in rigtools.discover_cells(rig.LOGS, days, since) if in_family(c, rig.REMOTE_CELLS)]
    rep.check(not here, f"no cell of {', '.join(rig.REMOTE_CELLS)}'s family ran on this machine"
              + (f" ({', '.join(here)})" if here else ""))
    rig.fetch_remote()
    there = [c for c, _ in rigtools.discover_cells(logs_dir(rig.LOGS, SPAWNER), days, remote_since)
             if in_family(c, rig.REMOTE_CELLS)]
    rep.notes.append(f"   the other machine ran {', '.join(there) or 'no family cell'}")
    spawner = []
    for d in sorted(days):
        path = os.path.join(logs_dir(rig.LOGS, SPAWNER), f"{SPAWNER_LOG_PREFIX}_{d}.info.log")
        if os.path.exists(path):
            with open(path, encoding="utf-8", errors="replace") as f:
                spawner += [line.rstrip("\n") for line in f if rigtools.stamp_of(line)
                            and " ".join(rigtools.stamp_of(line)[:2]) >= remote_since]
    kills = [line for line in spawner if "Spawner: kill" in line or "Spawner: killed" in line]
    if kills:
        rep.notes.append(f"   the other machine's spawner: {len(kills)} kill line(s), last: {kills[-1][-140:]}")
