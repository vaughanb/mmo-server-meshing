"""Pure parts of the split-process rig topology (Docs/ServerMeshing/slices/slice5_design.md decision 13): the arguments
the central and spawner build processes are started with, the PowerShell snippets that find, count and kill them and check
a port, the kit's log file paths, and the central's log split into process lifetimes. Nothing here touches a file, a
process or the database (python -m unittest Tools/ServerMeshing/test_rigprocs.py); regress_procs.py runs it.
"""
import re

# The flags the kit's MMOServerInstance reads (ProcessArguments.cs); a process is found by them in its command line.
CENTRAL_FLAG = "-startCentralServer"
LOGIN_FLAG = "-startLoginServer"
DATABASE_FLAG = "-startDatabaseServer"
SPAWNER_FLAG = "-startMapSpawnServer"
MAP_FLAG = "-startMapServer"
BATCH = ("-batchmode", "-nographics")
# The editor rig's ports (MMOServerInstance-CE.prefab, regress.RIG_PORTS): the build processes bind the same ones.
PORTS = {"central": 7000, "cluster": 6010, "login": 7500, "database": 6100, "spawner": 6001, "cells": 8000}
ADDRESS = "127.0.0.1"
MAPS = ("ServerMeshTestMap", "ServerMeshTestMap2")
# The kit's file log names (MMOServerInstance.LogFileName: the flags' order is fixed in code, whatever the arguments'):
# Logs/<prefix>_<day>.<info|warn|error>.log under the process's cwd (DefaultLogger.cs).
CENTRAL_LOG_PREFIX = "Log_Database_Central_Login"
SPAWNER_LOG_PREFIX = "Log_MapSpawn"
# The coordinator's summary line, once per central lifetime (MeshLayoutCoordinator_Recovery.RecoveryStart).
SUMMARY = re.compile(r"Coordinator ([0-9a-f]+): Dynamic maps (.*); store (.*) \((\w+)\); broadcast (\S+)")


def central_args(logs, scenario, lifetime):
    """The central process's arguments (the exe apart): database manager, central and login in one process, every port
    on the command line (the project's Config/serverConfig.json has other ports), its Unity log per lifetime."""
    return list(BATCH) + [DATABASE_FLAG, CENTRAL_FLAG, LOGIN_FLAG,
                          "-centralPort", str(PORTS["central"]), "-clusterPort", str(PORTS["cluster"]),
                          "-loginPort", str(PORTS["login"]),
                          "-databaseManagerAddress", ADDRESS, "-databaseManagerPort", str(PORTS["database"]),
                          "-publicAddress", ADDRESS,
                          "-logFile", f"{logs}/regress_{scenario}_central{lifetime}.log"]


def spawner_args(exe, logs, scenario):
    """The spawner process's arguments: the cluster to join, its own port, the cells from the build (absolute: the path
    is cwd-relative in the config), their first port, the two test maps (ONE token split on '|' by the kit's ReadArgs)."""
    return list(BATCH) + [SPAWNER_FLAG, "-centralAddress", ADDRESS, "-clusterPort", str(PORTS["cluster"]),
                          "-mapSpawnPort", str(PORTS["spawner"]), "-spawnExePath", exe,
                          "-spawnStartPort", str(PORTS["cells"]), "-publicAddress", ADDRESS,
                          "-spawnMaps", "|".join(MAPS),
                          "-logFile", f"{logs}/regress_{scenario}_spawner.log"]


def _ps_quote(text):
    return text.replace("'", "''")


def _by_argument(exe, flag):
    """The CIM filter of the build's processes whose command line holds `flag` as a whole word."""
    name = exe.replace("\\", "/").rsplit("/", 1)[-1]
    return ("@(Get-CimInstance Win32_Process -Filter \"Name='" + _ps_quote(name) + "'\" -ErrorAction Stop"
            " | Where-Object { $_.ExecutablePath -eq '" + _ps_quote(exe) + "' -and $_.CommandLine -like '* " + _ps_quote(flag) + " *' })")


def count_by_argument_script(exe, flag):
    """PowerShell: prints how many of the build's processes carry the flag."""
    return _by_argument(exe, flag) + ".Count"


def kill_by_argument_script(exe, flag):
    """PowerShell: stops every build process carrying the flag; prints how many it found."""
    return "$p = " + _by_argument(exe, flag) + "; foreach ($q in $p) { Stop-Process -Id $q.ProcessId -Force -ErrorAction Stop }; $p.Count"


def port_bound_script(port):
    """PowerShell: prints how many UDP endpoints are bound to the port (0 while nothing listens)."""
    return f"@(Get-NetUDPEndpoint -LocalPort {int(port)} -ErrorAction Ignore).Count"


def parse_count(out):
    """The integer a count snippet printed; ValueError for anything else."""
    text = (out or "").strip()
    if not text.isdigit():
        raise ValueError(f"not a count: {text[:80]!r}")
    return int(text)


def command_line_has_flag(command_line, flag):
    """Whether a process command line passes _by_argument's filter (the flag as a whole word; PowerShell's -like is case-insensitive)."""
    return f" {flag.lower()} " in f" {(command_line or '').lower()} "


def kit_log_path(logs, prefix, day, sev):
    """The kit's file log of a process: <logs>/<prefix>_<day>.<sev>.log."""
    return f"{logs}/{prefix}_{day}.{sev}.log"
def summary_of(lines):
    """(incarnation, maps text, store state, broadcast pair) of the first summary line, or None."""
    for line in lines:
        m = SUMMARY.search(line)
        if m:
            return m.group(1), m.group(2), m.group(4), m.group(5)
    return None
