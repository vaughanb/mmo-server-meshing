"""Machine B's map-spawn server (DESIGN 0.90, Docs/ServerMeshing/MULTIHOST.md §6b), split out of remoteb (the ~500-line
rule): B runs the kit's spawner as a build process registered with A's central, serving B's base cells through
MESH_SPAWN_CELLS, so a Dynamic family based on B has its children, restarts and kills on B.

The spawner runs in its own folder on B (`rig`, default C:\\MeshTest\\Rig) with the executable left in the build folder
(`dir`): the kit reads ./Config/serverConfig.json from the working directory and the spawner starts its cells with
none of its own, so they inherit it. Its config is written here from A's project Config/serverConfig.json with A's
address for the cluster and the database manager and B's as the public one; B's own Config and Logs stay untouched.
Logs land in <rig>\\Logs (the kit's per cell, spawner.log the engine's), fault markers and the burn file in <rig>\\Temp.

    python Tools/ServerMeshing/remoteb.py spawner-start --cells C [--env KEY=VALUE ...]
    python Tools/ServerMeshing/remoteb.py stop                      # the spawner first, then every cell
    python Tools/ServerMeshing/remoteb.py fetch --rig               # <rig>\\Logs into Logs/remote/
    python Tools/ServerMeshing/remoteb.py marker --name ServerMesh.FaultFired.Hello.C.2 --kind dir   # file | dir | clear
    python Tools/ServerMeshing/remoteb.py tempfile --name ServerMesh.ProbeBurn.txt --text "C:14"

    remotespawner.rig_config(base, a_ip, b_ip)            # the spawner folder's serverConfig.json (pure)
    remotespawner.spawner_arguments(exe, a_ip, b_ip, rig) # its command line (pure)
    remotespawner.launch_body(rig, exe, arguments, cells, env)   # the .cmd that starts it (pure)
    remotespawner.translate_env(env, local_temp, rig_temp)       # a Play's environment as B's cells need it (pure)
    remotespawner.cell_script(args, script_of, cell)      # run rigmerge/rigsplitcrash's process snippets on B
"""
import json
import os
import subprocess
import time

import remotessh

SPAWN_MAPS = ("ServerMeshTestMap", "ServerMeshTestMap2")   # rigprocs.MAPS: both test maps, cells by servedCells
PORTS = {"cluster": 6010, "spawner": 6001, "cells": 8000, "database": 6100}
EXE_NAME = "kit-ce-project.exe"
SERVED_KEY = "MESH_SPAWN_CELLS"
# Keys a Play sets that are A's business only: the central's store, the editor's role, A's own served cells.
LOCAL_ONLY = {"MESH_SPAWN_CELLS", "MESH_COORDINATOR_STORE", "MESH_EDITOR_SERVERS", "MESH_RIG_COMMAND_DIR"}
MARKER_KINDS = ("file", "dir", "clear")


def exe_path(args):
    """B's executable, in the build folder."""
    return args.dir.rstrip("\\") + "\\" + EXE_NAME


def rig_config(base, a_ip, b_ip):
    """The spawner folder's serverConfig.json, as a dict: `base` (A's project Config/serverConfig.json) with A's address
    for the cluster, login and database manager, B's as the public one, and the rig's cluster and database ports. The
    spawner passes its cells the central's address and their port on the command line; the database manager's address
    and the cluster port they read from here."""
    config = dict(base or {})
    config.update({"centralAddress": a_ip, "LoginAddress": a_ip, "databaseManagerAddress": a_ip,
                   "databaseManagerPort": PORTS["database"], "clusterPort": PORTS["cluster"],
                   "publicAddress": b_ip, "machineAddress": None, "mapSpawnPort": PORTS["spawner"],
                   "spawnStartPort": PORTS["cells"]})
    return config


def spawner_arguments(exe, a_ip, b_ip, rig, maps=SPAWN_MAPS):
    """The spawner's arguments (rigprocs.spawner_args across two hosts): A's cluster, its own port, the cells from the
    build (absolute), their first port, B's public address, the maps as ONE '|'-split token (quoted: '|' is a pipe to
    cmd.exe), its engine log in <rig>\\Logs."""
    return ["-batchmode", "-nographics", remotessh.SPAWNER_FLAG, "-centralAddress", a_ip,
            "-clusterPort", str(PORTS["cluster"]), "-mapSpawnPort", str(PORTS["spawner"]),
            "-spawnExePath", f'"{exe}"', "-spawnStartPort", str(PORTS["cells"]), "-publicAddress", b_ip,
            "-spawnMaps", '"' + "|".join(maps) + '"', "-logFile", f'"{rig}\\Logs\\spawner.log"']


def launch_body(rig, exe, arguments, cells, env):
    """The .cmd lines that start the spawner from `rig` with the scenario's environment (set "K=V": '&' and '|' stay
    literal) and the cells it serves."""
    body = ["@echo off", 'cd /d "' + rig + '"']
    for key, value in sorted((env or {}).items()):
        if value and key != SERVED_KEY:
            body.append(f'set "{key}={value}"')
    body.append(f'set "{SERVED_KEY}={",".join(cells)}"')
    body.append(f'"{exe}" ' + " ".join(arguments))
    return body


def translate_env(env, local_temp, rig_temp):
    """A Play's environment as B's cells need it: A-only keys dropped, empty values dropped, and a value naming a file
    under A's Temp (MESH_PROBE_BURN_MS=@<Temp>\\ServerMesh.ProbeBurn.txt) pointed at the same name under B's."""
    out = {}
    for key, value in (env or {}).items():
        if key in LOCAL_ONLY or not value:
            continue
        if local_temp and local_temp.lower() in value.lower():
            at = value.lower().index(local_temp.lower())
            value = value[:at] + rig_temp + value[at + len(local_temp):].replace("/", "\\")
        out[key] = value
    return out


def _quote(text):
    return text.replace("'", "''")


def prepare_script(rig):
    """PowerShell: the spawner folder's Config, Logs and Temp exist."""
    return ("New-Item -ItemType Directory -Force -Path '" + _quote(rig) + "\\Config','" + _quote(rig) + "\\Logs','"
            + _quote(rig) + "\\Temp' | Out-Null; 'ready'")


def marker_script(rig, name, kind):
    """PowerShell: <rig>\\Temp\\<name> made a file or a directory, or removed (MeshFault: a directory marker fires the
    fault in every incarnation)."""
    if kind not in MARKER_KINDS:
        raise ValueError(f"marker kind {kind!r} is not one of {', '.join(MARKER_KINDS)}")
    path = _quote(rig + "\\Temp\\" + name)
    clear = f"if (Test-Path '{path}') {{ Remove-Item -Recurse -Force '{path}' }}; "
    if kind == "clear":
        return clear + "'cleared'"
    if kind == "dir":
        return clear + f"New-Item -ItemType Directory -Force -Path '{path}' | Out-Null; 'dir'"
    return clear + f"Set-Content -Path '{path}' -Value 'armed later by the harness'; 'file'"


def tempfile_script(rig, name, text):
    """PowerShell: <rig>\\Temp\\<name> replaced whole by `text` (written beside it, then moved over it: a cell reading
    mid-write never sees an empty file)."""
    path = _quote(rig + "\\Temp\\" + name)
    return (f"[IO.File]::WriteAllText('{path}.tmp', '{_quote(text)}'); "
            f"Move-Item -Force '{path}.tmp' '{path}'; 'written'")


def clearmarkers_script(folders):
    """PowerShell: every ServerMesh.FaultFired.* (file or directory) under each folder's Temp removed; prints the count."""
    paths = ",".join("'" + _quote(f.rstrip("\\") + "\\Temp") + "'" for f in folders)
    return ("$n = 0; foreach ($p in @(" + paths + ")) { if (Test-Path $p) { "
            "$f = @(Get-ChildItem $p -Filter 'ServerMesh.FaultFired.*' -ErrorAction SilentlyContinue); "
            "$f | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue; $n += $f.Count } }; 'cleared ' + $n")


def run_script(args, script):
    """One PowerShell script on B; its output (Unreachable when ssh fails, RuntimeError when the script did)."""
    out, err, code = remotessh.ssh_script(args, script)
    if code != 0:
        raise RuntimeError(f"B: {(err or out).strip()[:200]}")
    return out


def cell_script(args, script_of, cell):
    """A process snippet of the harness (rigmerge.process_count_script, rigsplitcrash.kill_script) run on B against B's
    executable for `cell`; its output."""
    return run_script(args, script_of(exe_path(args), cell))


def start(args, cells, env, base_config):
    """Starts B's spawner serving `cells` with `env`, detached like a cell; waits up to 24 s for it to run. Returns the
    launch output; RuntimeError when one of ours already runs, a step failed or the spawner did not stay up."""
    if remotessh.count(args, remotessh.IS_CELL) > 0:
        raise RuntimeError("B already runs a server process; stop it first")
    run_script(args, prepare_script(args.rig))
    local = os.path.join(remotessh.ROOT, "Logs", "remote-rig-serverConfig.json")
    os.makedirs(os.path.dirname(local), exist_ok=True)
    with open(local, "w", encoding="utf-8", newline="") as f:
        json.dump(rig_config(base_config, args.a_ip, args.host), f, indent=2)
    target = f"{args.user}@{args.host}:{args.rig}/Config/serverConfig.json".replace("\\", "/")
    r = subprocess.run(["scp", "-i", args.key, "-o", "BatchMode=yes", local, target],
                       capture_output=True, text=True, timeout=120)
    if r.returncode != 0:
        raise RuntimeError("could not copy the spawner's serverConfig.json: " + remotessh.clean(r.stderr)[:200])
    exe = exe_path(args)
    body = launch_body(args.rig, exe, spawner_arguments(exe, args.a_ip, args.host, args.rig), cells, env)
    launched = remotessh.launch_detached(args, "start-spawner.cmd", body)
    if launched is None:
        raise RuntimeError("could not copy start-spawner.cmd to B")
    for _ in range(12):
        time.sleep(2)
        if remotessh.count(args, remotessh.IS_SPAWNER) > 0:
            return launched
    raise RuntimeError(f"the spawner did not stay up ({launched}); read {args.rig}\\Logs\\spawner.log")


def read_base_config():
    """A's project Config/serverConfig.json (the rig's ports and channels), {} when it cannot be read."""
    try:
        with open(os.path.join(remotessh.ROOT, "Config", "serverConfig.json"), encoding="utf-8") as f:
            return json.load(f)
    except (OSError, ValueError):
        return {}


def cmd_spawner_start(args):
    """Start B's map-spawn server serving --cells (comma list) with the --env variables (its cells inherit them)."""
    cells = [c.strip() for c in (args.cells or "").split(",") if c.strip()]
    if not cells:
        print("spawner-start needs --cells: the base cells B serves, e.g. --cells C")
        return 2
    env = dict(kv.partition("=")[::2] for kv in args.env)
    try:
        launched = start(args, cells, {k.strip(): v.strip() for k, v in env.items()}, read_base_config())
    except RuntimeError as ex:
        print(f"spawner-start: {ex}")
        return 1
    print("launch:", launched)
    print(f"spawner is running on {args.host}, serving {','.join(cells)}, registered with {args.a_ip}:{PORTS['cluster']}")
    return 0


def cmd_marker(args):
    try:
        print(run_script(args, marker_script(args.rig, args.name, args.kind)))
    except (RuntimeError, ValueError) as ex:
        print(f"marker: {ex}")
        return 1
    return 0


def cmd_tempfile(args):
    try:
        print(run_script(args, tempfile_script(args.rig, args.name, args.text)))
    except RuntimeError as ex:
        print(f"tempfile: {ex}")
        return 1
    return 0
