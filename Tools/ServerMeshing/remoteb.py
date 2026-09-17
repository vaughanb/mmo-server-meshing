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

Defaults match Docs/ServerMeshing/MULTIHOST.md; override with --host/--user/--key/--dir or the MESH_B_* environment
variables. Nothing here changes B's configuration: it only starts, stops and reads.
"""
import argparse, hashlib, os, subprocess, sys, time

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


def clean(text):
    return "\n".join(l for l in text.splitlines() if not any(n in l for n in NOISE))


def ssh(args, command, timeout=120):
    base = ["ssh", "-i", args.key, "-o", "BatchMode=yes", "-o", "ConnectTimeout=15", f"{args.user}@{args.host}"]
    r = subprocess.run(base + ["powershell", "-NoProfile", "-Command", '"' + command + '"'],
                       capture_output=True, text=True, timeout=timeout)
    return clean(r.stdout).strip(), clean(r.stderr).strip(), r.returncode


def running(args):
    out, _, _ = ssh(args, "(Get-Process kit-ce-project -ErrorAction SilentlyContinue).Count")
    return out.strip() not in ("", "0")


def log_path(args, cell, suffix="info"):
    day = time.strftime("%Y-%m-%d")
    return f"{args.dir}\\Logs\\Log_Map({args.map})-Ch({cell})-Alloc(False)-Instance()_{day}.{suffix}.log"


def cmd_start(args):
    """Launch the cell on B, detached from this SSH session.

    A process started with Start-Process over SSH dies when the session closes: OpenSSH puts the session in a job
    object and kills the tree with it. Writing a small .cmd and launching that through Win32_Process.Create escapes
    the job object, so the cell keeps running after we disconnect.
    """
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
    script = CRLF.join(body + [""])
    local = os.path.join(ROOT, "Logs", "start-cell-" + args.cell + ".cmd")
    os.makedirs(os.path.dirname(local), exist_ok=True)
    with open(local, "w", newline="") as f:
        f.write(script)
    remote = args.dir + "\\start-cell-" + args.cell + ".cmd"
    r = subprocess.run(["scp", "-i", args.key, "-o", "BatchMode=yes", local, f"{args.user}@{args.host}:{remote}"],
                       capture_output=True, text=True, timeout=120)
    if r.returncode != 0:
        print("could not copy the launch script:", clean(r.stderr)[:200])
        return 1
    command = ("$r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments "
               "@{ CommandLine = '" + remote + "'; CurrentDirectory = '" + args.dir + "' }; "
               "'rc=' + $r.ReturnValue + ' pid=' + $r.ProcessId")
    out, err, _ = ssh(args, command)
    print("launch:", out.strip() or err[:160])
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


def cmd_stop(args):
    # Also kill the launcher script: with --restart it would otherwise start the cell straight back up.
    out, _, _ = ssh(args, "Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'kit-ce-project.exe' -or "
                          "$_.CommandLine -like '*start-cell-*' } | ForEach-Object { Stop-Process -Id $_.ProcessId "
                          "-Force -ErrorAction SilentlyContinue }; Start-Sleep 1; "
                          "(Get-Process kit-ce-project -ErrorAction SilentlyContinue).Count")
    print("B server processes remaining:", out.strip() or "0")
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
    ap.add_argument("command", choices=["start", "stop", "status", "log", "fetch", "deploy", "clearmarkers"])
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
    return {"start": cmd_start, "stop": cmd_stop, "status": cmd_status, "log": cmd_log, "fetch": cmd_fetch,
            "deploy": cmd_deploy, "clearmarkers": cmd_clearmarkers}[args.command](args)


if __name__ == "__main__":
    sys.exit(main())
