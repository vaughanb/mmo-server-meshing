"""The SSH plumbing machine B's tools share (split out of remoteb, the ~500-line rule): the connection defaults, one
PowerShell command over plain OpenSSH, the Win32_Process filters that tell B's cells, phantom sink and bots apart, and
the detached launch that outlives the SSH session. remoteb (cells, sink, status, deploy, the command line) and
remotebots (step 8b's bots) build on it; nothing here runs at import.

    remotessh.defaults(host="...")              # an argparse.Namespace with host, user, key, dir, a_ip, map
    remotessh.ssh(args, command)                # (stdout, stderr, exit code); Unreachable when ssh itself failed
    remotessh.count(args, remotessh.IS_BOT)     # how many processes on B pass a filter
    remotessh.launch_detached(args, name, body) # copy a .cmd to B and start it outside this SSH session

Defaults match Docs/ServerMeshing/MULTIHOST.md; override with --host/--user/--key/--dir or the MESH_B_* environment
variables.
"""
import argparse
import os
import subprocess

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


def defaults(**overrides):
    """The connection settings remoteb's command line would give, as a Namespace (keyword arguments override)."""
    return argparse.Namespace(**dict(DEFAULTS, **overrides))


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


# The phantom sink (Phase 0 step 5) and the bots (step 8b) are the same executable as a cell, told apart by their
# command lines, which Get-Process cannot read: these filters list Win32_Process instead. A bot carries the auto
# client's flag, a sink the sink flag; a cell is any kit process with neither.
SINK_FLAG = "-meshPhantomSink"
SINK_SIZES_FLAG = "-meshPhantomSinkSizes"   # ServerMeshPhantomSink.ArgSinkSizes (Phase 0 step 7)
BOT_FLAG = "-meshAutoClient"                # ServerMeshAutoClient's ARG_ENABLE
IS_BOT = "($_.Name -eq 'kit-ce-project.exe' -and $_.CommandLine -like '*" + BOT_FLAG + "*')"
IS_SINK = "($_.Name -eq 'kit-ce-project.exe' -and $_.CommandLine -like '*" + SINK_FLAG + "*')"
IS_CELL = ("($_.Name -eq 'kit-ce-project.exe' -and $_.CommandLine -notlike '*" + SINK_FLAG + "*'"
           " -and $_.CommandLine -notlike '*" + BOT_FLAG + "*')")
# Stop filters also catch the launch scripts. Their own text names those scripts, so the session running one (and
# the shell sshd wrapped it in) matches too: excluded, or the command kills its own ssh session (ssh exit 255).
NOT_THIS_SESSION = ("$me = $PID; $parent = (Get-CimInstance Win32_Process -Filter \"ProcessId=$PID\").ParentProcessId; "
                    "Get-CimInstance Win32_Process | Where-Object { $_.ProcessId -ne $me -and $_.ProcessId -ne $parent")
STOP_CELLS = IS_CELL + " -or $_.CommandLine -like '*start-cell-*'"
STOP_SINK = "$_.CommandLine -like '*" + SINK_FLAG + "*' -or $_.CommandLine -like '*start-sink*'"
STOP_BOTS = IS_BOT + " -or $_.CommandLine -like '*start-bots*'"


def count_command(test):
    """The PowerShell that prints how many processes on B pass a Where-Object test."""
    return "@(Get-CimInstance Win32_Process | Where-Object { " + test + " }).Count"


def stop_command(test, remaining):
    """The PowerShell that kills every process passing a test except this ssh session, then counts `remaining`."""
    return (NOT_THIS_SESSION + " -and (" + test + ") } | "
            "ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }; Start-Sleep 1; "
            + count_command(remaining))


def as_count(text):
    """A count B printed, as an int; RuntimeError for anything else (an error text must not read as 0)."""
    try:
        return int(text.strip())
    except ValueError as ex:
        raise RuntimeError(f"B printed {text.strip()[:120]!r}, not a count") from ex


def count(args, test):
    """How many processes on B pass a Where-Object test."""
    out, _, _ = ssh(args, count_command(test))
    return as_count(out or "0")


def stop(args, test, remaining):
    """Kills every process on B passing `test` (never this ssh session); returns how many pass `remaining` after."""
    out, _, _ = ssh(args, stop_command(test, remaining))
    return as_count(out or "0")


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
