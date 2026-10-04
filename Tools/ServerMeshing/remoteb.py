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
    python Tools/ServerMeshing/remoteb.py impair --profile loss2   degrade B's UDP link to A (see PROFILES)
    python Tools/ServerMeshing/remoteb.py impair --profile off      stop degrading it
    python Tools/ServerMeshing/remoteb.py sink-start --sink-port 7770 [--sink-count 50] [--sink-base 9000] [--sink-sizes]
                                         start the phantom sink (Phase 0 step 5) against A's cell on that port
                                         (--sink-sizes: its Sink window adds record sizes, Phase 0 step 7)
    python Tools/ServerMeshing/remoteb.py sink-stop  stop it (start/stop/status only ever count and stop cells)

Defaults match Docs/ServerMeshing/MULTIHOST.md; override with --host/--user/--key/--dir or the MESH_B_* environment
variables. Nothing here changes B's configuration: it only starts, stops and reads.
"""
import argparse
import hashlib
import os
import subprocess
import sys
import time

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
CRLF = "\r\n"
DEFAULTS = {
    "host": os.environ.get("MESH_B_HOST", "192.168.50.211"),
    "user": os.environ.get("MESH_B_USER", "brend"),
    "key": os.environ.get("MESH_B_KEY", os.path.expanduser("~/.ssh/meshtest_ed25519")),
    "dir": os.environ.get("MESH_B_DIR", r"C:\MeshTest\Server"),
    "a_ip": os.environ.get("MESH_A_HOST", "192.168.50.83"),
    "map": os.environ.get("MESH_MAP", "ServerMeshTestMap"),
}
NOISE = ("WARNING:", "** WARNING", "post-quantum", "openssh.com", "store now")

# Link impairment, for the faults a datacentre actually produces: brief loss, jitter, reordering. Steady tens of
# milliseconds is deliberately NOT here -- cells are co-located, so that is a case this deployment never meets.
#
# The filter is UDP ONLY and scoped to A. SSH is TCP, so the control channel this is driven over stays clean even
# at high loss; scoping to A's address leaves B's own loopback and anything else it does alone.
PROFILES = {
    "off":       [],
    "lag5":      ["--lag", "on", "--lag-time", "5"],                       # a busy switch, both directions
    "loss05":    ["--drop", "on", "--drop-chance", "0.5"],
    "loss2":     ["--drop", "on", "--drop-chance", "2.0"],
    "loss5":     ["--drop", "on", "--drop-chance", "5.0"],
    "jitter":    ["--lag", "on", "--lag-time", "20", "--drop", "on", "--drop-chance", "0.5"],
    "reorder":   ["--ood", "on", "--ood-chance", "5.0"],
    "nasty":     ["--lag", "on", "--lag-time", "20", "--drop", "on", "--drop-chance", "5.0",
                  "--ood", "on", "--ood-chance", "5.0"],
}


def clean(text):
    return "\n".join(line for line in text.splitlines() if not any(n in line for n in NOISE))


class Unreachable(RuntimeError):
    """The other machine did not answer SSH at all -- asleep, off, or sshd stopped."""


def ssh(args, command, timeout=120):
    base = ["ssh", "-i", args.key, "-o", "BatchMode=yes", "-o", "ConnectTimeout=15", f"{args.user}@{args.host}"]
    r = subprocess.run(base + ["powershell", "-NoProfile", "-Command", '"' + command + '"'],
                       capture_output=True, text=True, timeout=timeout)
    err = clean(r.stderr).strip()
    # 255 is ssh's own failure (no route, refused, timed out, key rejected), as opposed to the remote command's exit
    # code. Every caller used to read stdout only, so an unreachable machine looked exactly like an idle one:
    # "process: not running", "cleared 0", an empty log (2026-09-18).
    if r.returncode == 255:
        raise Unreachable(f"{args.host} did not answer: {err.splitlines()[-1] if err else 'ssh exit 255'}")
    return clean(r.stdout).strip(), err, r.returncode


# The phantom sink (Phase 0 step 5) is the same executable as a cell, told apart by its command line, which
# Get-Process cannot read: these filters list Win32_Process instead. A cell is any kit process without the sink flag.
SINK_FLAG = "-meshPhantomSink"
SINK_SIZES_FLAG = "-meshPhantomSinkSizes"   # ServerMeshPhantomSink.ArgSinkSizes (Phase 0 step 7)
IS_CELL = "($_.Name -eq 'kit-ce-project.exe' -and $_.CommandLine -notlike '*" + SINK_FLAG + "*')"
IS_SINK = "($_.Name -eq 'kit-ce-project.exe' -and $_.CommandLine -like '*" + SINK_FLAG + "*')"
# Stop filters also catch the launch scripts. Their own text names those scripts, so the session running one (and
# the shell sshd wrapped it in) matches too: excluded, or the command kills its own ssh session (ssh exit 255).
NOT_THIS_SESSION = ("$me = $PID; $parent = (Get-CimInstance Win32_Process -Filter \"ProcessId=$PID\").ParentProcessId; "
                    "Get-CimInstance Win32_Process | Where-Object { $_.ProcessId -ne $me -and $_.ProcessId -ne $parent")
STOP_CELLS = IS_CELL + " -or $_.CommandLine -like '*start-cell-*'"
STOP_SINK = "$_.CommandLine -like '*" + SINK_FLAG + "*' -or $_.CommandLine -like '*start-sink*'"


def count_command(test):
    """The PowerShell that prints how many processes on B pass a Where-Object test."""
    return "@(Get-CimInstance Win32_Process | Where-Object { " + test + " }).Count"


def stop_command(test, remaining):
    """The PowerShell that kills every process passing a test except this ssh session, then counts `remaining`."""
    return (NOT_THIS_SESSION + " -and (" + test + ") } | "
            "ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }; Start-Sleep 1; "
            + count_command(remaining))


def running(args):
    """Whether B runs a cell (a phantom sink alone does not count)."""
    out, _, _ = ssh(args, count_command(IS_CELL))
    return out.strip() not in ("", "0")


def sink_running(args):
    """Whether B runs a phantom sink."""
    out, _, _ = ssh(args, count_command(IS_SINK))
    return out.strip() not in ("", "0")


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


def launch_detached(args, name, body):
    """Copy a .cmd script to B's build folder and start it outside this SSH session; the launch output, or None when
    the copy failed (printed).

    A process started with Start-Process over SSH dies when the session closes: OpenSSH puts the session in a job
    object and kills the tree with it. Launching the script through Win32_Process.Create escapes the job object, so
    the process keeps running after we disconnect.
    """
    local = os.path.join(ROOT, "Logs", name)
    os.makedirs(os.path.dirname(local), exist_ok=True)
    with open(local, "w", newline="") as f:
        f.write(CRLF.join(body + [""]))
    remote = args.dir + "\\" + name
    r = subprocess.run(["scp", "-i", args.key, "-o", "BatchMode=yes", local, f"{args.user}@{args.host}:{remote}"],
                       capture_output=True, text=True, timeout=120)
    if r.returncode != 0:
        print("could not copy the launch script:", clean(r.stderr)[:200])
        return None
    command = ("$r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments "
               "@{ CommandLine = '" + remote + "'; CurrentDirectory = '" + args.dir + "' }; "
               "'rc=' + $r.ReturnValue + ' pid=' + $r.ProcessId")
    out, err, _ = ssh(args, command)
    return out.strip() or err[:160]


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
    launched = launch_detached(args, "start-cell-" + args.cell + ".cmd", body)
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
    """Delete the fault markers in the remote build's Temp folder.

    MESH_FAULT_DIE_ON_NOTICE fires once per marker file, and the marker is written relative to the WORKING
    directory, which on the other machine is its own build folder. Clearing this machine's Temp does nothing for it.
    """
    out, err, _ = ssh(args, "$p = '" + args.dir + "\\Temp'; if (Test-Path $p) { "
                            "$f = Get-ChildItem $p -Filter 'ServerMesh.FaultFired.*' -ErrorAction SilentlyContinue; "
                            "$f | Remove-Item -Force -ErrorAction SilentlyContinue; 'cleared ' + $f.Count } "
                            "else { 'cleared 0' }")
    print(out.strip() or err[:160])
    return 0


def cmd_impair(args):
    """Start, change or stop clumsy on B so its UDP traffic to A is degraded.

    clumsy holds the impairment only while it runs, so a profile change is stop-then-start, and stopping it (or the
    process dying) restores the link. That is the property worth having: a crashed harness cannot leave the network
    quietly broken for the next run.
    """
    if args.profile not in PROFILES:
        print("profiles: " + ", ".join(sorted(PROFILES)))
        return 2
    ssh(args, "Get-Process clumsy -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue")
    if args.profile == "off":
        print("impairment off")
        return 0
    if not os.path.exists(os.path.expanduser("~/.ssh/meshtest_ed25519")):
        pass    # key presence is checked by ssh itself; kept simple here
    udp_filter = f"udp and (ip.DstAddr == {args.a_ip} or ip.SrcAddr == {args.a_ip})"
    options = " ".join(PROFILES[args.profile])
    body = ["@echo off", 'cd /d "' + args.clumsy + '"',
            f'clumsy.exe --filter "{udp_filter}" {options}']
    script = CRLF.join(body + [""])
    local = os.path.join(ROOT, "Logs", "impair.cmd")
    os.makedirs(os.path.dirname(local), exist_ok=True)
    with open(local, "w", newline="") as f:
        f.write(script)
    remote_cmd = args.clumsy + "\\impair.cmd"
    r = subprocess.run(["scp", "-i", args.key, "-o", "BatchMode=yes", local, f"{args.user}@{args.host}:{remote_cmd}"],
                       capture_output=True, text=True, timeout=120)
    if r.returncode != 0:
        print("could not copy the impair script:", clean(r.stderr)[:200])
        return 1
    # Same job-object escape as a cell: clumsy must outlive this SSH session.
    command = ("$r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments "
               "@{ CommandLine = '" + remote_cmd + "'; CurrentDirectory = '" + args.clumsy + "' }; "
               "'rc=' + $r.ReturnValue + ' pid=' + $r.ProcessId")
    out, err, _ = ssh(args, command)
    time.sleep(3)
    running_out, _, _ = ssh(args, "(Get-Process clumsy -ErrorAction SilentlyContinue).Count")
    if running_out.strip() in ("", "0"):
        print("clumsy did not stay up:", (out or err)[:200])
        print("it needs administrator rights on B to load the WinDivert driver")
        return 1
    print(f"impairment '{args.profile}' on: {options} over [{udp_filter}]")
    return 0


def cmd_stop(args):
    # Also kill the launcher script: with --restart it would otherwise start the cell straight back up. A phantom
    # sink is left running (sink-stop stops it).
    out, _, _ = ssh(args, stop_command(STOP_CELLS, IS_CELL))
    print("B server processes remaining:", out.strip() or "0")
    return 0


def cmd_sink_start(args):
    """Launch the phantom sink on B (Phase 0 step 5), detached like a cell: --sink-count peers connect to A's cell on
    --sink-port, the port its 'Phantoms: cell C port N' line names."""
    if args.sink_port is None:
        print("sink-start needs --sink-port: A's cell port, from its 'Phantoms: cell C port N' line")
        return 2
    if sink_running(args):
        print("B already runs a phantom sink; sink-stop it first.")
        return 1
    try:
        arguments = sink_arguments(args.a_ip, args.sink_port, args.sink_count, args.sink_base, args.dir, args.sink_sizes)
    except ValueError as e:
        print(f"sink-start: {e}")
        return 2
    launched = launch_detached(args, "start-sink.cmd", ["@echo off", 'cd /d "' + args.dir + '"',
                                                        "kit-ce-project.exe " + arguments])
    if launched is None:
        return 1
    print("launch:", launched)
    for _ in range(12):
        time.sleep(2)
        if sink_running(args):
            print(f"phantom sink is running on {args.host}: {args.sink_count} peers to {args.a_ip}:{args.sink_port}")
            return 0
    print(f"the sink did not stay up; read {args.dir}\\Logs\\sink.log")
    return 1


def cmd_sink_stop(args):
    out, _, _ = ssh(args, stop_command(STOP_SINK, IS_SINK))
    print("B phantom sinks remaining:", out.strip() or "0")
    return 0


def cmd_status(args):
    print("process:", "running" if running(args) else "not running")
    path = log_path(args, args.cell)
    out, _, _ = ssh(args, "if (Test-Path '" + path + "') { Select-String -Path '" + path + "' "
                          "-Pattern 'Own cell','Zone peer registered','Neighbour zone .* linked','Handoff','Arrival' | "
                          "Select-Object -Last 10 | ForEach-Object { $_.Line } } else { 'no log for today yet' }")
    print(out or "(nothing)")
    err_path = log_path(args, args.cell, "error")
    out, _, _ = ssh(args, "if (Test-Path '" + err_path + "') { (Get-Content '" + err_path + "' | Measure-Object -Line).Lines } else { '0' }")
    print("error log lines:", out.strip())
    return 0


def cmd_log(args):
    path = log_path(args, args.cell, args.suffix)
    if args.grep:
        command = ("Select-String -Path '" + path + "' -Pattern '" + args.grep + "' | "
                   "Select-Object -Last " + str(args.lines) + " | ForEach-Object { $_.Line }")
    else:
        command = "Get-Content -Path '" + path + "' -Tail " + str(args.lines)
    out, err, _ = ssh(args, command)
    print(out or err or "(nothing)")
    return 0


def cmd_fetch(args):
    """Copy B's zone logs here so the local harness can grade them alongside this machine's."""
    dest = os.path.join(ROOT, "Logs", "remote")
    os.makedirs(dest, exist_ok=True)
    # Forward slashes: scp splits user@host:path on the first colon, and a backslash path with a drive letter
    # gets mangled by the remote shell, so the copy silently matches nothing.
    src = f"{args.user}@{args.host}:{args.dir}/Logs/*.log".replace("\\", "/")
    r = subprocess.run(["scp", "-i", args.key, "-o", "BatchMode=yes", src, dest],
                       capture_output=True, text=True, timeout=300)
    if r.returncode != 0:
        print("scp failed:", clean(r.stderr)[:300])
        return 1
    files = sorted(os.listdir(dest))
    if not files:
        print("scp reported success but copied nothing; check the remote log folder")
        return 1
    print(f"fetched {len(files)} file(s) into Logs/remote/")
    for f in files[-6:]:
        print("   ", f)
    return 0


# Everything a cell needs to run, which is the whole build except what belongs to B: its own Config (addresses and
# database credentials) and its own Logs. The Burst debug folder is build output nothing reads, so it stays here.
DEPLOY_SKIP = {"Config", "Logs", "log", "kit-ce-project_BurstDebugInformation_DoNotShip"}


def cmd_deploy(args):
    """Copy the server build to B, so a cell there runs the code this machine just built.

    A cell runs the BUILD, not the editor's scripts: an add-on change is invisible on B until the build is rebuilt
    here and copied over. Stops any cell first, because Windows will not overwrite a running executable's files.
    """
    src_root = os.path.join(ROOT, "Build", "Server")
    if not os.path.exists(os.path.join(src_root, "kit-ce-project.exe")):
        print(f"no server build at {src_root}; build it in the editor first")
        return 1
    if running(args):
        print("stopping B's cell first (a running process locks its own files)")
        cmd_stop(args)
    if sink_running(args):
        print("stopping B's phantom sink first (the same executable)")
        cmd_sink_stop(args)
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
    # Prove it landed rather than trusting the exit code. By content, not by size: the managed assembly is
    # padded to a 512-byte boundary, so a build with a few methods added can come out exactly the same length
    # as the one before it, and a size check then passes happily on a stale copy.
    local_dll = os.path.join(src_root, "kit-ce-project_Data", "Managed", "Assembly-CSharp.dll")
    remote_dll = args.dir + "\\kit-ce-project_Data\\Managed\\Assembly-CSharp.dll"
    want = hashlib.sha256(open(local_dll, "rb").read()).hexdigest()
    out, _, _ = ssh(args, "if (Test-Path '" + remote_dll + "') { (Get-FileHash '" + remote_dll +
                          "' -Algorithm SHA256).Hash } else { 'missing' }")
    got = out.strip().lower()
    print(f"copied in {time.time() - started:.0f}s; Assembly-CSharp.dll sha256 {want[:16]} here, {got[:16]} on B")
    if got != want:
        print("the copy did not land: B's assembly does not match this machine's")
        return 1
    return 0


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("command", choices=["start", "stop", "status", "log", "fetch", "deploy", "clearmarkers", "impair",
                                        "sink-start", "sink-stop"])
    ap.add_argument("--sink-port", type=int, default=None,
                    help="sink-start: A's cell port, from its 'Phantoms: cell C port N' line (required)")
    ap.add_argument("--sink-count", type=int, default=50, help="sink-start: peers to connect (default 50)")
    ap.add_argument("--sink-base", type=int, default=None, help="sink-start: the peers' first local port")
    ap.add_argument("--sink-sizes", action="store_true",
                    help="sink-start: add record sizes to the Sink window (-meshPhantomSinkSizes 1, Phase 0 step 7)")
    ap.add_argument("--profile", default="off", help="impairment profile: " + ", ".join(sorted(PROFILES)))
    ap.add_argument("--clumsy", default=os.environ.get("MESH_B_CLUMSY", r"C:\MeshTest\clumsy"),
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
    for k, v in DEFAULTS.items():
        ap.add_argument("--" + k.replace("_", "-"), default=v)
    args = ap.parse_args()
    try:
        return {"start": cmd_start, "stop": cmd_stop, "status": cmd_status, "log": cmd_log, "fetch": cmd_fetch,
                "deploy": cmd_deploy, "clearmarkers": cmd_clearmarkers, "impair": cmd_impair,
                "sink-start": cmd_sink_start, "sink-stop": cmd_sink_stop}[args.command](args)
    except Unreachable as e:
        print(f"UNREACHABLE: {e}")
        return 3


if __name__ == "__main__":
    sys.exit(main())
