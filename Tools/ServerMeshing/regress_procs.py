"""The split-process rig topology for regress.py (Docs/ServerMeshing/slices/slice5_design.md decision 13): the central,
login and database manager as one build process and the map spawner as another, both started from Build/Server with
cwd = the project root (so their logs land in Logs/, their configuration is the project's Config/ and the coordinator's
store is the editor's Temp/ServerMesh.Coordinator.json), the editor's Play being a client only. The harness can then kill
and restart the central alone.

regress.py hands itself in as `rig` (EXE, ROOT, LOGS, TEMP, ps, zone_lines, kill_all_kit_processes, ...), as the other
scenario modules take it; the pure pieces (arguments, snippets, log paths) are rigprocs'. The unit tests hand in a fake.
"""
import datetime
import os
import subprocess
import time

import rigprocs

LOGIN_READY_S = 60      # the central process binds its login port within this (a build takes 10-20 s to reach StartServers here)
CELLS_READY_S = 120     # every cell expected at boot logged its own cell
POLL_S = 2.0
_lifetime = 0           # the central's lifetimes this run (its Unity log is named per lifetime)
_cluster_env = {}       # the environment the cluster was started with: a restarted central gets the same one


def _popen(rig, args, env):
    """Start a build process detached, cwd = the project root, with the scenario's environment on top of the harness's."""
    merged = dict(os.environ)
    for key, value in (env or {}).items():
        if value:
            merged[key] = value
        else:
            merged.pop(key, None)
    merged[_command_dir_key()] = rig.TEMP
    log = args[args.index("-logFile") + 1] if "-logFile" in args else None
    if log and os.path.exists(log):
        os.remove(log)
    return subprocess.Popen([rig.EXE] + args, cwd=rig.ROOT, env=merged,
                            creationflags=getattr(subprocess, "DETACHED_PROCESS", 0) | getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0),
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def _command_dir_key():
    return "MESH_RIG_COMMAND_DIR"


def count(rig, flag):
    return rigprocs.parse_count(rig.ps(rigprocs.count_by_argument_script(rig.EXE, flag)))


def database_running(rig):
    """Whether a build process hosts the database manager (the rows may not be rewritten then: its cache would not see them)."""
    try:
        return count(rig, rigprocs.DATABASE_FLAG) > 0
    except (RuntimeError, ValueError):
        return False


def start_central(rig, scenario, env=None):
    """Start the central (+ login + database manager) process with the cluster's environment (the first start's, or
    `env` when given); returns its lifetime number (1, 2, ...)."""
    global _lifetime
    _lifetime += 1
    _popen(rig, rigprocs.central_args(rig.LOGS, scenario, _lifetime), _cluster_env if env is None else env)
    return _lifetime


def start_spawner(rig, scenario, env):
    _popen(rig, rigprocs.spawner_args(rig.EXE, rig.LOGS, scenario), env)


def kill_central(rig):
    """Stop the central process (exactly one is expected); returns how many were found."""
    return rigprocs.parse_count(rig.ps(rigprocs.kill_by_argument_script(rig.EXE, rigprocs.CENTRAL_FLAG)))


def wait_port(rig, port, timeout):
    """True once the UDP port is bound (polled), False at the timeout."""
    end = time.time() + timeout
    while True:
        try:
            if rigprocs.parse_count(rig.ps(rigprocs.port_bound_script(port))) > 0:
                return True
        except (RuntimeError, ValueError):
            pass
        if time.time() >= end:
            return False
        time.sleep(POLL_S)


def wait_central(rig, timeout=LOGIN_READY_S):
    """The central process is up when its login port is bound: the login, central and cluster servers start in one frame
    and the coordinator with them, so a bound login port means the coordinator exists."""
    return wait_port(rig, rigprocs.PORTS["login"], timeout)


def expected_at_boot(rig, env):
    """The cells the spawner starts at boot: every local cell, or none under MESH_SLEEP (the central wakes them)."""
    if (env or {}).get("MESH_SLEEP"):
        return []
    return [(z, m) for z, m in rig.ZONES if z not in getattr(rig, "REMOTE_CELLS", [])]


def wait_cells(rig, expected, mark, timeout=CELLS_READY_S):
    """True once every expected (zone, map) logged 'Own cell from channel id' since `mark` (a datetime), else False."""
    start, day = mark.strftime("%H:%M:%S"), mark.date().isoformat()
    end = time.time() + timeout
    pending = list(expected)
    while True:
        for zone, map_id in list(pending):
            if any(f"Own cell from channel id: {zone} " in line for line in rig.zone_lines(zone, start, day, map_id, suffixes=("info",))):
                pending.remove((zone, map_id))
        if not pending:
            return True
        if time.time() >= end:
            return False
        time.sleep(POLL_S)


def start_cluster(rig, scenario, env):
    """The topology's start: the central process, its login port, the spawner process, every cell at boot. Raises on a
    step that never came, naming it."""
    global _cluster_env, _lifetime
    _cluster_env = dict(env or {})
    _lifetime = 0
    mark = datetime.datetime.now()
    start_central(rig, scenario, env)
    if not wait_central(rig):
        raise RuntimeError(f"the central process did not bind its login port within {LOGIN_READY_S} s")
    start_spawner(rig, scenario, env)
    expected = expected_at_boot(rig, env)
    if expected and not wait_cells(rig, expected, mark):
        raise RuntimeError(f"not every cell logged its own cell within {CELLS_READY_S} s of the spawner's start")


def restart_central(rig, scenario, down_seconds):
    """Kill the central, wait `down_seconds` (past the cells' disconnect timeout), start it again with the cluster's
    environment (the first live run started it bare: no MESH_CELL_MODE, so no coordinator); returns (killed, lifetime, kill_time)."""
    killed = kill_central(rig)
    kill_at = time.time()
    time.sleep(down_seconds)
    lifetime = start_central(rig, scenario)
    if not wait_central(rig):
        raise RuntimeError(f"the restarted central did not bind its login port within {LOGIN_READY_S} s")
    return killed, lifetime, kill_at


def teardown(rig):
    """End the topology: the spawner first (so nothing re-queues a cell), then the central, then every rig process."""
    for flag in (rigprocs.SPAWNER_FLAG, rigprocs.CENTRAL_FLAG):
        try:
            rig.ps(rigprocs.kill_by_argument_script(rig.EXE, flag))
        except RuntimeError:
            pass
    time.sleep(1)
    rig.kill_all_kit_processes()
