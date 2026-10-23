"""Drive the second machine's cells over SSH, so one session controls both halves of the test.

Machine B runs map-server processes only. This talks to it with plain OpenSSH (the client already ships with Windows),
so B needs nothing but the OpenSSH server, the build unpacked somewhere, and this machine's public key installed.

    python Tools/ServerMeshing/remoteb.py start [--cell E] [--port 8001]
    python Tools/ServerMeshing/remoteb.py stop
    python Tools/ServerMeshing/remoteb.py status
    python Tools/ServerMeshing/remoteb.py log [--lines 40] [--grep "Neighbour zone"] [--suffix error]
    python Tools/ServerMeshing/remoteb.py fetch      copy B's zone logs into Logs/remote/ for grading here
    python Tools/ServerMeshing/remoteb.py deploy     copy this machine's server build to B (after rebuilding it)
    python Tools/ServerMeshing/remoteb.py clearmarkers   delete B's one-shot fault markers before a crash test
    python Tools/ServerMeshing/remoteb.py impair --profile loss2   degrade B's UDP link to A (remoteimpair.PROFILES)
    python Tools/ServerMeshing/remoteb.py impair --profile off      stop degrading it (clumsy outlives SSH: always do)
    python Tools/ServerMeshing/remoteb.py sink-start --sink-port 7770 [--sink-count 50] [--sink-base 9000] [--sink-sizes]
                                         start the phantom sink (Phase 0 step 5) against A's cell on that port
                                         (--sink-sizes: its Sink window adds record sizes, Phase 0 step 7)
    python Tools/ServerMeshing/remoteb.py sink-stop  stop it (start/stop only ever count and stop cells)
    python Tools/ServerMeshing/remoteb.py bots-start --first 2 --n 3 [--mode stand] [--points FILE] [--bot-net 5]
                                         [--rig-ships WIRE:SEED:SCALE] [--stagger 1]
                                         start meshbot<first>.. on B against A's login (Phase 0 step 8b; remotebots)
    python Tools/ServerMeshing/remoteb.py bots-stop | bots-count | bots-fetch   (bots-fetch: into Logs/remote/bots/)
    python Tools/ServerMeshing/remoteb.py spawner-start --cells C [--env KEY=VALUE]   B's map-spawn server (remotespawner)
    python Tools/ServerMeshing/remoteb.py fetch --from-rig | marker --name N --kind dir | tempfile --name N --text T

status prints B's cells, bots and sinks, B's clock, and whether RelicTestServer or a Relics process runs.
Defaults match Docs/ServerMeshing/MULTIHOST.md; override with --host/--user/--key/--dir or the MESH_B_* environment
variables. Nothing here changes B's configuration: it only starts, stops and reads. The SSH plumbing lives in
remotessh, the bots in remotebots, B's spawner in remotespawner (the ~500-line rule); their names stay importable here.

    remoteb.state(args)                # one SSH query: counts, clock, host, CPU, free memory, Relic (parse_state)
    remoteb.state_problems(state)      # why no step 8b run may use B as it is ([] when it may)
    remoteb.deployed_matches(args)     # (matches, sha256 here, sha256 on B) of Assembly-CSharp.dll
    remoteb.start_sink(args, port, count, base, sizes) / stop_sink(args) / fetch(args)
    remoteb.start_impair(args, profile) / clumsy_count(args) / stop_impair(args)   # clumsy (remoteimpair)
"""
import argparse
import datetime
import hashlib
import os
import re
import subprocess
import sys
import time

import remotebots
import remoteimpair
import remotespawner
import remotessh
# Re-exported for remoteb's callers and tests; calls go through remotessh.<name>, so one patch reaches them all.
from remotessh import (CRLF, DEFAULTS, IS_BOT, IS_CELL, IS_SINK, IS_SPAWNER, NOT_THIS_SESSION, ROOT,  # noqa: F401
                       SINK_FLAG, SINK_SIZES_FLAG, STOP_BOTS, STOP_CELLS, STOP_SINK, STOP_SPAWNER, Unreachable, clean,
                       count_command, stop_command)
from remoteimpair import PROFILES, NoAdmin, clumsy_count, start_impair, stop_impair  # noqa: F401 (p0peer's names)


def running(args):
    """Whether B runs a cell (a phantom sink or a bot alone does not count)."""
    return remotessh.count(args, IS_CELL) > 0


def sink_running(args):
    """Whether B runs a phantom sink."""
    return remotessh.count(args, IS_SINK) > 0


RELIC_SERVICE = "RelicTestServer"   # B's other game's test server: a service only the user stops (admin)
STATE_INTS = ("cells", "bots", "sinks", "relics", "free_mb")
# One query for everything a step 8b run asks of B: no double quotes (ssh() wraps the command in them).
STATE_COMMAND = ("$p = @(Get-CimInstance Win32_Process); "
                 "'cells=' + @($p | Where-Object { " + IS_CELL + " }).Count; "
                 "'bots=' + @($p | Where-Object { " + IS_BOT + " }).Count; "
                 "'sinks=' + @($p | Where-Object { " + IS_SINK + " }).Count; "
                 "'relics=' + @(Get-Process -ErrorAction Ignore | Where-Object { $_.ProcessName -like '*Relic*' }).Count; "
                 "'relic_service=' + (Get-Service -Name " + RELIC_SERVICE + " -ErrorAction Ignore).Status; "
                 "'clock=' + (Get-Date -Format o); "
                 "'host=' + $env:COMPUTERNAME; "
                 "'cpu=' + (Get-CimInstance Win32_Processor | Select-Object -First 1).Name; "
                 "'free_mb=' + [math]::Round((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory / 1024)")
CLOCK = re.compile(r"(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d)(?:\.(\d+))?(Z|[+-]\d\d:\d\d)$")


def parse_clock(text):
    """B's `Get-Date -Format o` text as an aware datetime (fractions past microseconds dropped), else None."""
    m = CLOCK.fullmatch((text or "").strip())
    if not m:
        return None
    zone = "+00:00" if m.group(3) == "Z" else m.group(3)
    try:
        return datetime.datetime.fromisoformat(f"{m.group(1)}.{(m.group(2) or '0')[:6].ljust(6, '0')}{zone}")
    except ValueError:
        return None


def parse_state(text):
    """STATE_COMMAND's output as {"cells", "bots", "sinks", "relics", "free_mb": int or None; "relic_service" ('' when
    the service is absent), "host", "cpu": str; "clock": aware datetime or None}. Lines it does not know are ignored."""
    found = dict(line.split("=", 1) for line in text.splitlines() if "=" in line)
    state = {key: (int(found[key]) if found.get(key, "").strip().isdigit() else None) for key in STATE_INTS}
    state.update({key: found.get(key, "").strip() for key in ("relic_service", "host", "cpu")})
    state["clock"] = parse_clock(found.get("clock"))
    return state


def state(args):
    """parse_state of one SSH query to B (Unreachable when ssh fails)."""
    out, _, _ = remotessh.ssh(args, STATE_COMMAND)
    return parse_state(out)


def state_problems(state):
    """Why B cannot serve a step 8b run as it is: one of our cells running, or counts it did not print. Leftover bots
    and sinks are not refused (every peer run stops them first), and since 2026-10-16 neither is the user's own work on
    B (RelicTestServer, Relics, other programs: our processes bind no fixed port there, and every peer run records how
    busy B's other programs kept it, cpu_sample / load_shares); state_notes names it."""
    problems = [f"B printed no {key} count" for key in ("cells", "relics") if state.get(key) is None]
    if state.get("cells"):
        problems.append(f"B runs {state['cells']} cell(s); stop them: remoteb.py stop")
    return problems


def state_notes(state):
    """What else runs on B that a run should mention (not refuse): RelicTestServer, Relics processes."""
    notes = []
    if state.get("relic_service") == "Running":
        notes.append(f"B runs {RELIC_SERVICE} (the user's; not ours to stop)")
    if state.get("relics"):
        notes.append(f"B runs {state['relics']} Relics process(es) (the user's)")
    return notes


def cpu_command(directory):
    """The PowerShell printing B's CPU seconds so far for our processes (those whose executable sits under directory)
    and for every other process, and its logical processor count: "ours=<s> other=<s> logical=<n>"."""
    folder = directory.rstrip("\\") + "\\*"
    return ("$all = Get-Process; $ours = ($all | Where-Object { $_.Path -like '" + folder + "' } | Measure-Object CPU -Sum).Sum; "
            "$tot = ($all | Measure-Object CPU -Sum).Sum; "
            "$n = (Get-CimInstance Win32_ComputerSystem).NumberOfLogicalProcessors; "
            "'ours={0} other={1} logical={2}' -f [double]$ours, ([double]$tot - [double]$ours), $n")


def parse_cpu(text):
    """{"ours", "other" (CPU seconds), "logical"} from cpu_command's line, else None."""
    m = re.search(r"ours=([\d.]+) other=(-?[\d.]+) logical=(\d+)", text.replace(",", "."))
    return {"ours": float(m[1]), "other": max(0.0, float(m[2])), "logical": int(m[3])} if m else None


def cpu_sample(args):
    """parse_cpu of one SSH query (None when B printed nothing usable)."""
    out, _, _ = remotessh.ssh(args, cpu_command(args.dir))
    return parse_cpu(out)


def load_shares(before, after, seconds):
    """{"ours", "other"}: the shares of B's whole CPU (logical processors x seconds) our processes and the rest used
    between two cpu_sample readings; None when either reading is missing or no time passed. A process that started
    or ended between them counts only its own seconds, so the shares are a lower bound for short lives."""
    if before is None or after is None or seconds <= 0 or not after.get("logical"):
        return None
    capacity = after["logical"] * seconds
    return {key: max(0.0, after[key] - before[key]) / capacity for key in ("ours", "other")}


def sink_arguments(a_ip, port, count, base, directory, sizes=False):
    """The sink process's command-line arguments: it connects `count` peers to A's cell at a_ip:port, from local ports
    starting at `base` when given, walks received EntityState records into a sizes group when `sizes`
    (-meshPhantomSinkSizes 1), and logs to <directory>\\Logs\\sink.log (a sink gets no kit file log of its own).
    Raises ValueError for a port or base outside 1-65535 or a count below 1."""
    if not 1 <= port <= 65535:
        raise ValueError(f"sink port {port} is not from 1 to 65535")
    if count < 1:
        raise ValueError(f"sink count {count} is not at least 1")
    target = f"{a_ip}:{port}:{count}"
    if base is not None:
        if not 1 <= base <= 65535:
            raise ValueError(f"sink port base {base} is not from 1 to 65535")
        target += f":{base}"
    sizes_flag = [SINK_SIZES_FLAG, "1"] if sizes else []
    return " ".join(["-batchmode", "-nographics", SINK_FLAG, target] + sizes_flag
                    + ["-logFile", directory + "\\Logs\\sink.log"])


def log_path(args, cell, suffix="info"):
    day = time.strftime("%Y-%m-%d")
    return f"{args.dir}\\Logs\\Log_Map({args.map})-Ch({cell})-Alloc(False)-Instance()_{day}.{suffix}.log"


def cmd_start(args):
    """Launch the cell on B, detached from this SSH session (launch_detached)."""
    if running(args):
        print("B already has a server process running; stop it first.")
        return 1
    arguments = " ".join([
        "-batchmode", "-nographics", "-startMapServer",
        "-mapName", args.map, "-channelId", args.cell,
        "-mapPort", str(args.port), "-publicAddress", args.host,
        "-centralAddress", args.a_ip, "-centralPort", "6010",
        "-databaseManagerAddress", args.a_ip, "-databaseManagerPort", "6100",
    ])
    body = ["@echo off", 'cd /d "' + args.dir + '"']
    for kv in getattr(args, "env", []) or []:
        key, _, value = kv.partition("=")
        body.append("set " + key.strip() + "=" + value.strip())
    if getattr(args, "restart", False):
        body += [":restart", "kit-ce-project.exe " + arguments, "timeout /t 2 /nobreak >nul", "goto restart"]
    else:
        body.append("kit-ce-project.exe " + arguments)
    launched = remotessh.launch_detached(args, "start-cell-" + args.cell + ".cmd", body)
    if launched is None:
        return 1
    print("launch:", launched)
    for _ in range(12):
        time.sleep(2)
        if running(args):
            print(f"cell {args.cell} is running on {args.host}:{args.port}")
            return 0
    print("the process did not stay up; try: remoteb.py log --suffix error")
    return 1


def cmd_clearmarkers(args):
    """Delete the fault markers (files, and directories that arm a fault for every incarnation) in the Temp folders of
    the remote build and of B's spawner: a marker is written relative to the WORKING directory, which on the other
    machine is one of those two. Clearing this machine's Temp does nothing for it."""
    out = remotespawner.run_script(args, remotespawner.clearmarkers_script([args.dir, args.rig]))
    print(out.strip())
    return 0


def cmd_impair(args):
    """Start, change or stop clumsy on B so its UDP traffic to and from A is degraded (remoteimpair).

    clumsy is started outside this SSH session's job object, so it keeps running after the session ends, and after a
    crashed harness too: the link stays degraded until `impair --profile off` (or p0peer.quiet_b, which every peer
    run and every canary of a peer block calls first) stops it. clumsy holds the impairment only while it runs, so
    stopping it restores the link and a profile change is stop-then-start.
    """
    if args.profile not in PROFILES:
        print("profiles: " + ", ".join(sorted(PROFILES)))
        return 2
    if args.profile == "off":
        left = remoteimpair.stop_impair(args)
        print("impairment off" if not left else f"impairment NOT off: {left} clumsy process(es) still running")
        return 0 if not left else 1
    try:
        remoteimpair.start_impair(args, args.profile)
    except remoteimpair.NoAdmin as ex:
        print(f"{ex}; run the SSH user as an administrator on B")
        return 1
    except RuntimeError as ex:
        print(ex)
        return 1
    print(f"impairment '{args.profile}' on: {' '.join(PROFILES[args.profile])} over "
          f"[{remoteimpair.udp_filter(args.a_ip)}]")
    return 0


def cmd_stop(args):
    # Also kill the launcher script: with --restart it would otherwise start the cell straight back up. The spawner
    # goes first (DESIGN 0.90): a live one restarts the cells it started. A phantom sink and bots are left running
    # (sink-stop and bots-stop stop them).
    remotessh.stop(args, STOP_SPAWNER, IS_SPAWNER)
    print("B server processes remaining:", remotessh.stop(args, STOP_CELLS, IS_CELL))
    return 0


def start_sink(args, port, peers, base=None, sizes=False):
    """Launches the phantom sink on B, detached like a cell: `peers` peers connect to A's cell on `port` (its
    'Phantoms: cell C port N' line), and waits up to 24 s for it to run; returns the launch output. ValueError for
    the arguments sink_arguments refuses; RuntimeError when B already runs a sink, the script could not be copied or
    the sink did not stay up."""
    arguments = sink_arguments(args.a_ip, port, peers, base, args.dir, sizes)
    if sink_running(args):
        raise RuntimeError("B already runs a phantom sink; sink-stop it first")
    launched = remotessh.launch_detached(args, "start-sink.cmd", ["@echo off", 'cd /d "' + args.dir + '"',
                                                        "kit-ce-project.exe " + arguments])
    if launched is None:
        raise RuntimeError("could not copy start-sink.cmd to B")
    for _ in range(12):
        time.sleep(2)
        if sink_running(args):
            return launched
    raise RuntimeError(f"the sink did not stay up ({launched}); read {args.dir}\\Logs\\sink.log")


def cmd_sink_start(args):
    """Launch the phantom sink on B (Phase 0 step 5), detached like a cell: --sink-count peers connect to A's cell on
    --sink-port, the port its 'Phantoms: cell C port N' line names."""
    if args.sink_port is None:
        print("sink-start needs --sink-port: A's cell port, from its 'Phantoms: cell C port N' line")
        return 2
    try:
        launched = start_sink(args, args.sink_port, args.sink_count, args.sink_base, args.sink_sizes)
    except ValueError as e:
        print(f"sink-start: {e}")
        return 2
    except RuntimeError as e:
        print(f"sink-start: {e}")
        return 1
    print("launch:", launched)
    print(f"phantom sink is running on {args.host}: {args.sink_count} peers to {args.a_ip}:{args.sink_port}")
    return 0


def stop_sink(args):
    """Kills B's phantom sink and its launch script; returns how many sinks still run."""
    return remotessh.stop(args, STOP_SINK, IS_SINK)


def cmd_sink_stop(args):
    print("B phantom sinks remaining:", stop_sink(args))
    return 0


def status_lines(state):
    """What status prints about B as a whole (state: parse_state)."""
    relic = state["relic_service"] or "not installed"
    return [f"process: {'running' if state['cells'] else 'not running'} ({state['cells']} cell(s))",
            f"bots: {state['bots']}", f"sinks: {state['sinks']}",
            f"B clock: {state['clock'].isoformat() if state['clock'] else '?'}",
            f"{RELIC_SERVICE}: {relic}; Relics processes: {state['relics']}"]


def cmd_status(args):
    for line in status_lines(state(args)):
        print(line)
    path = log_path(args, args.cell)
    out, _, _ = remotessh.ssh(args, "if (Test-Path '" + path + "') { Select-String -Path '" + path + "' "
                          "-Pattern 'Own cell','Zone peer registered','Neighbour zone .* linked','Handoff','Arrival' | "
                          "Select-Object -Last 10 | ForEach-Object { $_.Line } } else { 'no log for today yet' }")
    print(out or "(nothing)")
    err_path = log_path(args, args.cell, "error")
    out, _, _ = remotessh.ssh(args, "if (Test-Path '" + err_path + "') { (Get-Content '" + err_path + "' | Measure-Object -Line).Lines } else { '0' }")
    print("error log lines:", out.strip())
    return 0


def cmd_log(args):
    path = log_path(args, args.cell, args.suffix)
    if args.grep:
        command = ("Select-String -Path '" + path + "' -Pattern '" + args.grep + "' | "
                   "Select-Object -Last " + str(args.lines) + " | ForEach-Object { $_.Line }")
    else:
        command = "Get-Content -Path '" + path + "' -Tail " + str(args.lines)
    out, err, _ = remotessh.ssh(args, command)
    print(out or err or "(nothing)")
    return 0


def fetch(args):
    """Copies B's Logs/*.log (its cells' logs and sink.log) into Logs/remote/, or with args.from_rig the spawner
    folder's (its cells' logs and spawner.log) into Logs/remote/rig/; returns that folder's files, sorted. RuntimeError
    when scp fails or the folder stays empty; a spawner folder with no log yet (before its first start) is no error:
    a cell whose log is missing fails its own grading (regress.zone_lines)."""
    from_rig = getattr(args, "from_rig", False)
    dest = os.path.join(ROOT, "Logs", "remote", *(["rig"] if from_rig else []))
    os.makedirs(dest, exist_ok=True)
    # Forward slashes: scp splits user@host:path on the first colon, and a backslash path with a drive letter
    # gets mangled by the remote shell, so the copy silently matches nothing.
    folder = args.rig if from_rig else args.dir
    src = f"{args.user}@{args.host}:{folder}/Logs/*.log".replace("\\", "/")
    r = subprocess.run(["scp", "-i", args.key, "-o", "BatchMode=yes", src, dest],
                       capture_output=True, text=True, timeout=300)
    if r.returncode != 0 and not (from_rig and "No such file or directory" in r.stderr):
        raise RuntimeError("scp failed: " + clean(r.stderr)[:300])
    files = sorted(name for name in os.listdir(dest) if os.path.isfile(os.path.join(dest, name)))
    if not files and not from_rig:
        raise RuntimeError("scp reported success but copied nothing; check the remote log folder")
    return files


def cmd_fetch(args):
    """Copy B's zone logs here so the local harness can grade them alongside this machine's."""
    try:
        files = fetch(args)
    except RuntimeError as ex:
        print(ex)
        return 1
    print(f"fetched {len(files)} file(s) into Logs/remote/" + ("rig/" if getattr(args, "from_rig", False) else ""))
    for f in files[-6:]:
        print("   ", f)
    return 0


# Everything a cell needs to run, which is the whole build except what belongs to B: its own Config (addresses and
# database credentials) and its own Logs. The Burst debug folder is build output nothing reads, so it stays here.
DEPLOY_SKIP = {"Config", "Logs", "log", "kit-ce-project_BurstDebugInformation_DoNotShip"}
BUILD_ROOT = os.path.join(ROOT, "Build", "Server")
LOCAL_DLL = os.path.join(BUILD_ROOT, "kit-ce-project_Data", "Managed", "Assembly-CSharp.dll")


def deployed_matches(args, local_dll=LOCAL_DLL):
    """(matches, sha256 here, sha256 on B) of Assembly-CSharp.dll: B's copy compared by content, not by size (the
    managed assembly is padded to a 512-byte boundary, so a build with a few methods added can come out exactly the
    same length as the one before it). B's hash reads 'missing' when it has no build."""
    with open(local_dll, "rb") as f:
        want = hashlib.sha256(f.read()).hexdigest()
    remote_dll = args.dir + "\\kit-ce-project_Data\\Managed\\Assembly-CSharp.dll"
    out, _, _ = remotessh.ssh(args, "if (Test-Path '" + remote_dll + "') { (Get-FileHash '" + remote_dll +
                          "' -Algorithm SHA256).Hash } else { 'missing' }")
    got = out.strip().lower()
    return got == want, want, got


def cmd_deploy(args):
    """Copy the server build to B, so a cell there runs the code this machine just built.

    A cell runs the BUILD, not the editor's scripts: an add-on change is invisible on B until the build is rebuilt
    here and copied over. Stops any cell, sink and bot first, because Windows will not overwrite a running
    executable's files.
    """
    src_root = BUILD_ROOT
    if not os.path.exists(os.path.join(src_root, "kit-ce-project.exe")):
        print(f"no server build at {src_root}; build it in the editor first")
        return 1
    if running(args):
        print("stopping B's cell first (a running process locks its own files)")
        cmd_stop(args)
    if sink_running(args):
        print("stopping B's phantom sink first (the same executable)")
        cmd_sink_stop(args)
    if remotebots.bot_count(args):
        print("stopping B's bots first (the same executable)")
        remotebots.cmd_bots_stop(args)
    entries = [e for e in sorted(os.listdir(src_root)) if e not in DEPLOY_SKIP]
    total = sum(os.path.getsize(os.path.join(dp, f))
                for e in entries for dp, _, fs in os.walk(os.path.join(src_root, e)) for f in fs) \
        + sum(os.path.getsize(os.path.join(src_root, e)) for e in entries if os.path.isfile(os.path.join(src_root, e)))
    print(f"copying {len(entries)} item(s), {total / 1e6:.0f} MB to {args.user}@{args.host}:{args.dir}")
    dest = f"{args.user}@{args.host}:{args.dir}/".replace("\\", "/")
    started = time.time()
    r = subprocess.run(["scp", "-r", "-q", "-i", args.key, "-o", "BatchMode=yes"]
                       + [os.path.join(src_root, e) for e in entries] + [dest],
                       capture_output=True, text=True, timeout=1800)
    if r.returncode != 0:
        print("scp failed:", clean(r.stderr)[:400])
        return 1
    # Prove it landed rather than trusting the exit code (deployed_matches).
    matches, want, got = deployed_matches(args)
    print(f"copied in {time.time() - started:.0f}s; Assembly-CSharp.dll sha256 {want[:16]} here, {got[:16]} on B")
    if not matches:
        print("the copy did not land: B's assembly does not match this machine's")
        return 1
    return 0


COMMANDS = {"start": cmd_start, "stop": cmd_stop, "status": cmd_status, "log": cmd_log, "fetch": cmd_fetch,
            "deploy": cmd_deploy, "clearmarkers": cmd_clearmarkers, "impair": cmd_impair,
            "sink-start": cmd_sink_start, "sink-stop": cmd_sink_stop, "bots-start": remotebots.cmd_bots_start,
            "bots-stop": remotebots.cmd_bots_stop, "bots-count": remotebots.cmd_bots_count,
            "bots-fetch": remotebots.cmd_bots_fetch, "spawner-start": remotespawner.cmd_spawner_start,
            "marker": remotespawner.cmd_marker, "tempfile": remotespawner.cmd_tempfile}


def build_parser():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("command", choices=list(COMMANDS))
    ap.add_argument("--sink-port", type=int, default=None,
                    help="sink-start: A's cell port, from its 'Phantoms: cell C port N' line (required)")
    ap.add_argument("--sink-count", type=int, default=50, help="sink-start: peers to connect (default 50)")
    ap.add_argument("--sink-base", type=int, default=None, help="sink-start: the peers' first local port")
    ap.add_argument("--sink-sizes", action="store_true",
                    help="sink-start: add record sizes to the Sink window (-meshPhantomSinkSizes 1, Phase 0 step 7)")
    ap.add_argument("--first", type=int, default=2, help="bots-start: the first bot, meshbot<first> (from 2)")
    ap.add_argument("--n", type=int, default=1, help="bots-start: how many bots (default 1)")
    ap.add_argument("--mode", default="stand", choices=remotebots.MODES, help="bots-start: the bots' auto mode")
    ap.add_argument("--points", default=None,
                    help="bots-start: a file of one 'x,z[,return_x]' line per bot (default: the west column)")
    ap.add_argument("--bot-net", type=float, default=None, help="bots-start: -meshBotNetSeconds for every bot")
    ap.add_argument("--rig-ships", default=None, metavar="WIRE:SEED:SCALE",
                    help="bots-start: -meshRigShips for every bot (p0ships.rig_ship_bot_args' value)")
    ap.add_argument("--stagger", type=int, default=1, help="bots-start: whole seconds between two bots (default 1)")
    ap.add_argument("--profile", default="off", help="impairment profile: " + ", ".join(sorted(PROFILES)))
    ap.add_argument("--clumsy", default=os.environ.get("MESH_B_CLUMSY", remoteimpair.CLUMSY_DIR),
                    help="where clumsy lives on B")
    ap.add_argument("--cell", default="E")
    ap.add_argument("--port", type=int, default=8001)
    ap.add_argument("--lines", type=int, default=40)
    ap.add_argument("--grep", default="")
    ap.add_argument("--env", action="append", default=[], metavar="KEY=VALUE",
                    help="environment variable for the remote cell, repeatable (e.g. MESH_FAULT_DIE_ON_NOTICE=E)")
    ap.add_argument("--restart", action="store_true",
                    help="relaunch the cell if it exits, the way A's spawner does (needed by the crash scenario)")
    ap.add_argument("--suffix", default="info", choices=["info", "warn", "error"])
    ap.add_argument("--cells", default="", help="spawner-start: the base cells B's spawner serves, comma-separated")
    ap.add_argument("--from-rig", action="store_true", help="fetch: the spawner folder's logs (--rig), not the build's")
    ap.add_argument("--name", default="", help="marker, tempfile: the file's name under the spawner folder's Temp")
    ap.add_argument("--kind", default="file", choices=remotespawner.MARKER_KINDS, help="marker: file, dir or clear")
    ap.add_argument("--text", default="", help="tempfile: the file's whole text")
    for k, v in DEFAULTS.items():
        ap.add_argument("--" + k.replace("_", "-"), default=v)
    return ap


def main():
    args = build_parser().parse_args()
    try:
        return COMMANDS[args.command](args)
    except Unreachable as e:
        print(f"UNREACHABLE: {e}")
        return 3


if __name__ == "__main__":
    sys.exit(main())
