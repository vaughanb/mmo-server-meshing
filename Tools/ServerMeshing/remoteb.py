"""Drive the second machine's cells over SSH, so one session controls both halves of the test.

Machine B runs map-server processes only. This talks to it with plain OpenSSH (the client already ships with Windows),
so B needs nothing but the OpenSSH server, the build unpacked somewhere, and this machine's public key installed.

    python Tools/ServerMeshing/remoteb.py start [--cell E] [--port 8001]
    python Tools/ServerMeshing/remoteb.py stop
    python Tools/ServerMeshing/remoteb.py status
    python Tools/ServerMeshing/remoteb.py log [--lines 40] [--grep "Neighbour zone"] [--suffix error]
    python Tools/ServerMeshing/remoteb.py fetch      copy B's zone logs into Logs/remote/ for grading here

Defaults match Docs/ServerMeshing/MULTIHOST.md; override with --host/--user/--key/--dir or the MESH_B_* environment
variables. Nothing here changes B's configuration: it only starts, stops and reads.
"""
import argparse, os, subprocess, sys, time

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
    script = CRLF.join(["@echo off", 'cd /d "' + args.dir + '"', "kit-ce-project.exe " + arguments, ""])
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


def cmd_stop(args):
    out, _, _ = ssh(args, "Stop-Process -Name kit-ce-project -Force -ErrorAction SilentlyContinue; "
                          "Start-Sleep 1; (Get-Process kit-ce-project -ErrorAction SilentlyContinue).Count")
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
    src = f"{args.user}@{args.host}:{args.dir}\\Logs\\*.log"
    r = subprocess.run(["scp", "-i", args.key, "-o", "BatchMode=yes", src, dest],
                       capture_output=True, text=True, timeout=300)
    if r.returncode != 0:
        print("scp failed:", clean(r.stderr)[:300])
        return 1
    files = sorted(os.listdir(dest))
    print(f"fetched {len(files)} file(s) into Logs/remote/")
    for f in files[-6:]:
        print("   ", f)
    return 0


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("command", choices=["start", "stop", "status", "log", "fetch"])
    ap.add_argument("--cell", default="E")
    ap.add_argument("--port", type=int, default=8001)
    ap.add_argument("--lines", type=int, default=40)
    ap.add_argument("--grep", default="")
    ap.add_argument("--suffix", default="info", choices=["info", "warn", "error"])
    for k, v in DEFAULTS.items():
        ap.add_argument("--" + k.replace("_", "-"), default=v)
    args = ap.parse_args()
    return {"start": cmd_start, "stop": cmd_stop, "status": cmd_status, "log": cmd_log, "fetch": cmd_fetch}[args.command](args)


if __name__ == "__main__":
    sys.exit(main())
