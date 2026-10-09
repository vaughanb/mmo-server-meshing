"""Headless bots on machine B (Phase 0 step 8b decision 4): their command lines, the start-bots.cmd that launches them
detached and staggered, and their count, stop and log fetch over SSH. remoteb's bots-start, bots-stop, bots-count and
bots-fetch are the command line (cmd_bots_*); p0peer calls the functions.

    remotebots.bot_arguments("meshbot2", "stand", -30, 0, None, "192.168.50.83", r"C:\\MeshTest\\Server")
    remotebots.bots_script(directory, [arguments, ...], stagger=1)   # start-bots.cmd's lines
    remotebots.start_bots(args, first, n, mode, points, ...)          # launch meshbot<first>.. on B
    remotebots.bot_count(args) / stop_bots(args) / fetch_bots(args)   # count; kill (returns survivors); copy logs

Bots are meshbot<K>..meshbot<K+N-1>, K >= 2 (meshbot is the regress fixture); each gets its own -meshWalkX/-meshWalkZ
(stand and pace walk to that point first) and -meshLoginAddress <A>:7500, since the prefab's login address is
localhost. Their logs are B's Logs\\bots\\<user>.log, copied to Logs/remote/bots/ here.
"""
import glob
import math
import os
import re
import subprocess
import time

import botpoints
import remotessh as s

MODES = ("none", "walk", "self", "stand", "pace", "observe", "fight", "pvp", "harvest", "build")   # AutoClient.Mode
LOGIN_PORT = 7500           # A's login server (Config/serverConfig.json); the bots reach it from B
LOGIN_FLAG = "-meshLoginAddress"   # ServerMeshLoginAddress.ArgLoginAddress
SCRIPT = "start-bots.cmd"
LOCAL_LOGS = os.path.join(s.ROOT, "Logs", "remote", "bots")
BOT_USER = re.compile(r"meshbot(\d+)")
IN_MAP = "In map at"        # ServerMeshAutoClient logs it once the character is in the map


def bot_user(k):
    """The k-th bot's account (meshbot<k>); ValueError below 2: meshbot itself is the regress fixture."""
    if k < 2:
        raise ValueError(f"meshbot{k}: step 8b's bots are meshbot2 and up (meshbot is the regress fixture)")
    return f"meshbot{k}"


def _number(value, what):
    if not isinstance(value, (int, float)) or not math.isfinite(value):
        raise ValueError(f"{what} {value!r} is not a finite number")
    text = f"{round(value, 3) + 0.0:.3f}".rstrip("0").rstrip(".")
    return text


def bot_arguments(user, mode, walk_x, walk_z, return_x, a_ip, directory, bot_net=None, rig_ships=None):
    """One bot's command line, as regress.launch_bot builds it (-batchmode -nographics -meshAutoClient -meshUser
    -meshChannel C, then the mode and its walk point) plus -meshReturnX when return_x is given, -meshBotNetSeconds
    when bot_net is, -meshRigShips <rig_ships> (p0ships.rig_ship_bot_args' value) when given, -meshLoginAddress
    <a_ip>:7500, and -logFile <directory>\\Logs\\bots\\<user>.log. ValueError for a user that is not meshbot<K>
    (K >= 2), an unknown mode, a walk point that is not a finite number or a bot_net that is not above 0."""
    m = BOT_USER.fullmatch(user or "")
    if not m:
        raise ValueError(f"user {user!r} is not meshbot<K>")
    bot_user(int(m.group(1)))
    if mode not in MODES:
        raise ValueError(f"mode {mode!r} is not one of {', '.join(MODES)}")
    words = ["-batchmode", "-nographics", s.BOT_FLAG, "-meshUser", user, "-meshChannel", "C", "-meshAutoMode", mode,
             "-meshWalkX", _number(walk_x, "walk x"), "-meshWalkZ", _number(walk_z, "walk z")]
    if return_x is not None:
        words += ["-meshReturnX", _number(return_x, "return x")]
    if bot_net is not None:
        if not isinstance(bot_net, (int, float)) or not bot_net > 0:
            raise ValueError(f"bot net seconds {bot_net!r} is not above 0")
        words += ["-meshBotNetSeconds", _number(bot_net, "bot net seconds")]
    if rig_ships:
        words += ["-meshRigShips", rig_ships]
    return " ".join(words + [LOGIN_FLAG, f"{a_ip}:{LOGIN_PORT}", "-logFile", directory + "\\Logs\\bots\\" + user + ".log"])


def bots_script(directory, arguments, stagger=1):
    """start-bots.cmd's lines: into the build folder, Logs\\bots created and emptied, then one `start "" /b` per bot
    with `timeout /t <stagger>` between two (none when stagger is 0). ValueError for a stagger that is not a whole
    number of seconds from 0, or no bots."""
    if not isinstance(stagger, int) or stagger < 0:
        raise ValueError(f"stagger {stagger!r} is not a whole number of seconds from 0")
    if not arguments:
        raise ValueError("no bots to start")
    body = ["@echo off", 'cd /d "' + directory + '"', "if not exist Logs\\bots mkdir Logs\\bots",
            "del /q Logs\\bots\\*.log 2>nul"]
    for index, words in enumerate(arguments):
        if index and stagger:
            body.append(f"timeout /t {stagger} /nobreak >nul")
        body.append('start "" /b kit-ce-project.exe ' + words)
    return body


def bot_count(args):
    """How many bots run on B."""
    return s.count(args, s.IS_BOT)


def stop_bots(args):
    """Kills B's bots and their launch script (never this ssh session); returns how many bots still run."""
    return s.stop(args, s.STOP_BOTS, s.IS_BOT)


def start_bots(args, first, n, mode, points, bot_net=None, rig_ships=None, stagger=1):
    """Launches meshbot<first>..meshbot<first+n-1> on B through one detached start-bots.cmd, bot i walking to
    points[i] = (x, z[, return_x]); returns the launch output ("rc=0 pid=..."). ValueError for bad arguments (see
    bot_arguments, bots_script; n points for n bots); RuntimeError when B already runs bots, the script could not be
    copied or Win32_Process.Create did not return 0. It does not wait for the bots (cmd_bots_start does)."""
    if n < 1:
        raise ValueError(f"{n} bots: start at least 1")
    if len(points) != n:
        raise ValueError(f"{len(points)} points for {n} bots")
    arguments = [bot_arguments(bot_user(first + i), mode, point[0], point[1], point[2] if len(point) > 2 else None,
                               args.a_ip, args.dir, bot_net, rig_ships) for i, point in enumerate(points)]
    body = bots_script(args.dir, arguments, stagger)
    running = bot_count(args)
    if running:
        raise RuntimeError(f"B already runs {running} bot(s); bots-stop them first")
    launched = s.launch_detached(args, SCRIPT, body)
    if launched is None:
        raise RuntimeError("could not copy start-bots.cmd to B")
    if not launched.startswith("rc=0 "):
        raise RuntimeError(f"start-bots.cmd did not start: {launched}")
    return launched


def fetch_bots(args, dest=LOCAL_LOGS):
    """Empties dest of .log files, then copies B's Logs\\bots\\*.log into it; returns the copied paths, sorted.
    RuntimeError when scp fails (B has no bot log, for one)."""
    os.makedirs(dest, exist_ok=True)
    for old in glob.glob(os.path.join(dest, "*.log")):
        os.remove(old)
    src = f"{args.user}@{args.host}:{args.dir}/Logs/bots/*.log".replace("\\", "/")
    r = subprocess.run(["scp", "-i", args.key, "-o", "BatchMode=yes", src, dest],
                       capture_output=True, text=True, timeout=300)
    if r.returncode != 0:
        raise RuntimeError("scp of B's bot logs failed: " + s.clean(r.stderr)[:300])
    return sorted(glob.glob(os.path.join(dest, "*.log")))


def in_map(path):
    """Whether a bot log says the bot reached the map (its 'In map at' line)."""
    try:
        with open(path, encoding="utf-8", errors="replace") as f:
            return any(IN_MAP in line for line in f)
    except FileNotFoundError:
        return False


def cmd_bots_start(args):
    """bots-start: --first K --n N --mode M [--points FILE] [--bot-net S] [--rig-ships V] [--stagger S]; without a
    points file the bots stand in the west column (botpoints 'west'). Waits until N bots run (or stagger x N + 24 s)."""
    try:
        if args.points:
            with open(args.points, encoding="utf-8") as f:
                points = botpoints.parse_points(f.read())
        else:
            points = botpoints.bot_points(args.n, "west")
        launched = start_bots(args, args.first, args.n, args.mode, points, args.bot_net, args.rig_ships,
                              args.stagger)
    except (OSError, ValueError) as ex:
        print(f"bots-start: {ex}")
        return 2
    except RuntimeError as ex:
        print(f"bots-start: {ex}")
        return 1
    print("launch:", launched)
    deadline = time.time() + args.stagger * args.n + 24
    while time.time() < deadline:
        time.sleep(2)
        running = bot_count(args)
        if running >= args.n:
            print(f"{running} bot(s) running on {args.host}: meshbot{args.first}..meshbot{args.first + args.n - 1}")
            return 0
    print(f"only {bot_count(args)} of {args.n} bots are running; read {args.dir}\\Logs\\bots\\")
    return 1


def cmd_bots_stop(args):
    print("B bots remaining:", stop_bots(args))
    return 0


def cmd_bots_count(args):
    print("B bots running:", bot_count(args))
    return 0


def cmd_bots_fetch(args):
    """bots-fetch: Logs/remote/bots/ emptied, then B's bot logs copied in; prints how many reached the map."""
    try:
        paths = fetch_bots(args)
    except RuntimeError as ex:
        print(ex)
        return 1
    print(f"fetched {len(paths)} bot log(s) into Logs/remote/bots/; {sum(map(in_map, paths))} reached the map")
    return 0
