#!/usr/bin/env python3
"""Server Meshing regression harness (DESIGN.md §16 / changelog 0.43).

Drives the ten-cell rig (a 3x3 grid plus C2 across a seam) end to end and grades each scenario from the logs:

    python Tools/ServerMeshing/regress.py                 # the default set: pet party crash harvest build seam seamcold sleep
    python Tools/ServerMeshing/regress.py crash build     # a subset; also pvp wake dynidle
    python Tools/ServerMeshing/regress.py --list
    python Tools/ServerMeshing/regress.py --remote-cells E [--remote-persist] [--impair loss2] ...
                                                          # E served by the second machine (Docs/ServerMeshing/MULTIHOST.md)

Requirements (all already true on the rig machine):
  * the Unity editor is open on this project with BaseInit-CE (any scene that is not dirty) and the
    ServerMeshPlayRunner watching Temp/ServerMesh.PlayRequest.txt (it does whenever the editor is up);
  * a fresh player build at Build/Server/kit-ce-project.exe (the zone servers and the headless bots);
  * the MySQL database named in Config/mySqlConfig.json (the kit's schema; provision.py creates the two test accounts
    and characters, MeshTester2 and meshbotChar, and every scenario resets them to that state).

Each scenario: resets the test characters in the database, asks the editor to enter Play with the auto
client in a given mode (plus environment variables the zone servers inherit), launches headless bots from
the build, waits for the auto client's RESULT (and the bots'), stops Play, and grades: zero exceptions and no
player-puppet teleports on any cell's logs since the scenario started, the expected RESULT lines, and
scenario-specific log lines.
Exit code 1 when any scenario fails. Bot logs land in Logs/regress_<scenario>_<user>.log.
"""
import argparse
import datetime
import hashlib
import os
import re
import subprocess
import sys
import time
import uuid

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import provision
import regress_merge
import regress_split
import regress_splitcrash
import regress_auto
import regress_building
import regress_central
import regress_procs
import rigauto
import rigprocs
import rigtools
from provision import ITEM_BOW, ITEM_YIELD, db_connect

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
EXE = os.path.join(ROOT, "Build", "Server", "kit-ce-project.exe")
TEMP = os.path.join(ROOT, "Temp")
REQUEST = os.path.join(TEMP, "ServerMesh.PlayRequest.txt")
STOP = os.path.join(TEMP, "ServerMesh.StopRequest.txt")
STATUS = os.path.join(TEMP, "ServerMesh.PlayStatus.txt")
HOLD = os.path.join(TEMP, "ServerMesh.HoldRequest.txt")
REFRESH_REQUEST = os.path.join(TEMP, "ServerMesh.RefreshRequest.txt")
BUILD_REQUEST = os.path.join(TEMP, "ServerMesh.BuildRequest.txt")
BUILD_STATUS = os.path.join(TEMP, "ServerMesh.BuildStatus.txt")
TEST_REQUEST = os.path.join(TEMP, "ServerMesh.TestRequest.txt")
TEST_STATUS = os.path.join(TEMP, "ServerMesh.TestStatus.txt")
PACKAGE_REQUEST = os.path.join(TEMP, "ServerMesh.PackageRequest.txt")
PACKAGE_STATUS = os.path.join(TEMP, "ServerMesh.PackageStatus.txt")
BUILD_DLL = os.path.join(ROOT, "Build", "Server", "kit-ce-project_Data", "Managed", "Assembly-CSharp.dll")
BUILD_MANIFEST = os.path.join(ROOT, "Build", "Server", "rigbuild-manifest.txt")
LOGS = os.path.join(ROOT, "Logs")
ASSETS = os.path.join(ROOT, "Assets")
RIG_REGISTRY = os.path.join(ASSETS, "ServerMeshing", "TestMap", "ServerMeshWorld.asset")
# MeshCoordinatorStore.DefaultRelativePath, relative to the editor's working directory (the central runs there).
COORDINATOR_STORE = os.path.join(TEMP, "ServerMesh.Coordinator.json")
# Slice 2b's layout operations (regress_split): the command the play runner forwards to the central, and its op lines.
LAYOUT_COMMAND = os.path.join(TEMP, "ServerMesh.LayoutCommand.txt")
LAYOUT_COMMAND_STATUS = os.path.join(TEMP, "ServerMesh.LayoutCommandStatus.txt")
MAP = "ServerMeshTestMap"
STORAGE_TYPE_BUILDING = 3   # the kit's StorageType.Building (storageitem.storageType)
MAP2 = "ServerMeshTestMap2"   # placed 400 m east of MAP in the world registry; its only cell C2 touches E
# The test map is a 3x3 grid; C/E/N/NE keep the world positions every scenario was written against, and the
# five added cells surround them, so a crossing now happens with eight neighbours rather than three.
ZONES = [("SW", MAP), ("S", MAP), ("SE", MAP), ("W", MAP), ("C", MAP), ("E", MAP),
         ("NW", MAP), ("N", MAP), ("NE", MAP), ("C2", MAP2)]
TESTER, BOT = provision.TESTER.character_name, provision.BOT.character_name   # the editor client, the headless bot
ENV_KEYS = ["MESH_NO_MONSTER_HANDOFF", "MESH_FAULT_DIE_ON_NOTICE", "MESH_GHOST_LEAD", "MESH_LOAD_PLAYERS", "MESH_LOAD_CELL", "MESH_LOAD_UNCAPPED", "MESH_SLEEP", "MESH_SLEEP_RINGS", "MESH_SLEEP_AFTER", "MESH_SPAWN_CELLS",
            # 0.71 measurement toggles (DESIGN 0.71): re-cut maps, per-cell synthetic load, a crowd square, the probe's burn test
            "MESH_LAYOUT_OVERRIDE", "MESH_LOAD_PLAN", "MESH_LOAD_AREA", "MESH_PROBE_BURN_MS", "MESH_NO_MONSTERS", "MESH_BAND",
            "MESH_LOAD_SEED", "MESH_MONSTER_AREAS",
            # 0.72 dynamic cells: per-map cell mode, and where the central's layout coordinator keeps its store
            "MESH_CELL_MODE", "MESH_COORDINATOR_STORE",
            # Phase 0 step 2 instrument toggles (p0tools.ENV_REGISTRY): tick-frame bins, frame and physics rates,
            # per-cell uncapping, the timed AOI, per-thread CPU, a burn inside every kit tick
            "MESH_WORK_BINS", "MESH_RATES", "MESH_UNCAPPED_CELLS", "MESH_AOI_TIMING", "MESH_THREAD_CPU",
            "MESH_PROBE_BURN_TICK_MS",
            # Phase 0 step 3 (p0tools.ENV_REGISTRY): kinematic puppets, server-side animation, the load generator's
            # motion (steered, kinematic, inert) and whether its steered players fight
            "MESH_PUPPET_MOTION", "MESH_RIG_SERVER_ANIMATION", "MESH_LOAD_MOTION", "MESH_LOAD_FIGHT",
            # Phase 0 step 5 (p0tools.ENV_REGISTRY): the send meter, and phantom clients (virtual or served by
            # the sink; characters or eyes) with their view, pacing, upload rate and payload, steps, area, and the
            # shares that stand and fight
            "MESH_METER_SENDS", "MESH_PHANTOM", "MESH_PHANTOM_VIEW", "MESH_PHANTOM_ARRIVAL_PER_S",
            "MESH_PHANTOM_UPLOAD_HZ", "MESH_PHANTOM_PAYLOAD", "MESH_PHANTOM_STEPS", "MESH_PHANTOM_AREA",
            "MESH_PHANTOM_STAND", "MESH_PHANTOM_FIGHT",
            # Phase 0 step 6 (p0tools.ENV_REGISTRY): the state sender (measure or on) with its sources, tiers, tier
            # radius, compact far records, spreading, X/Z distances, re-classification period and packet cap, and the
            # CE-12 check
            "MESH_SENDER", "MESH_SENDER_SOURCES", "MESH_SENDER_TIERS", "MESH_SENDER_R0", "MESH_SENDER_COMPACT",
            "MESH_SENDER_SPREAD", "MESH_SENDER_XZ", "MESH_SENDER_CLASSIFY_BUILDS", "MESH_SENDER_PACKET_BYTES",
            "MESH_CE12_CHECK",
            # Phase 0 step 7 (p0tools.ENV_REGISTRY via p0ships): load ships and their pilots, the ship wire, scale,
            # projectiles and mirror velocities, ship-aware bots, the puppet feeder, sync load and record sizes;
            # step 7b's teleport self-ack and AI ship class
            "MESH_LOAD_SHIPS", "MESH_LOAD_SHIP_PILOTS", "MESH_SHIP_WIRE", "MESH_SHIP_SCALE", "MESH_SHIP_PROJECTILES",
            "MESH_SHIP_MIRROR_VELOCITY", "MESH_RIG_SHIPS", "MESH_PUPPET_FEEDER", "MESH_SYNC_LOAD",
            "MESH_METER_RECORD_SIZES", "MESH_SHIP_SELF_ACK", "MESH_SHIP_AI_PREFAB",
            # Phase 0 step 8a (p0tools.ENV_REGISTRY via p0stage0b): pre-warm bursts, the parked-ship pin and placed
            # monsters
            "MESH_PREWARM", "MESH_SHIP_PARK_SLEEP", "MESH_MONSTER_PLACE",
            # Phase 0 step 9 (p0tools.ENV_REGISTRY via p0cells0c): the database burst and the editor's save probe, the
            # monster cell, fighters hitting ghosts, forwarded-hit timing, isolated spaces and LiteNetLib's statistics
            "MESH_DB_BURST", "MESH_DB_SAVES_PROBE", "MESH_MONSTER_CELL", "MESH_FIGHT_GHOSTS", "MESH_FORWARD_TIMING",
            "MESH_SPACES", "MESH_NET_STATS", "MESH_MONSTER_AWAKE",
            # Slice 2d's fault toggles (regress_splitcrash): a child dies on the e1 broadcast, or on its drain's first arrival
            "MESH_FAULT_DIE_ON_PREWARM", "MESH_FAULT_DIE_ON_DRAIN",
            # Slice 3's automatic policy (regress_auto): which Dynamic maps split and merge by themselves, the triggers,
            # the cooldown, the benefit gate, the cost model's weights, and the spawner's core count
            "MESH_AUTO_OPS", "MESH_SPLIT_UTIL", "MESH_SPLIT_DWELL", "MESH_MERGE_UTIL", "MESH_MERGE_AFTER", "MESH_OP_COOLDOWN",
            "MESH_SPLIT_BENEFIT", "MESH_MONSTER_WEIGHT", "MESH_PLANNER_WEIGHTS", "MESH_HOST_CORES",
            # Slice 5 (regress_central, regress_procs): the editor as a client only (the split-process topology sets it), and
            # the base-crash fault: a cell dies right after its n-th LayoutHello
            "MESH_EDITOR_SERVERS", "MESH_FAULT_DIE_ON_HELLO"]
EXTRA_ENV = {}   # --env KEY=VALUE from the command line, applied to every Play request of the run
# Slice 5: the per-scenario topology. `editor`: the editor hosts central, login, database manager and spawner (every
# scenario so far); `split`: the central (+ login + database manager) and the spawner are build processes started by
# regress_procs with cwd = the project root, and the editor's Play is a client only. --topology forces one for the run.
SPLIT_TOPOLOGY = set(regress_central.SCENARIOS)
TOPOLOGY_OVERRIDE = None
_current_scenario = None


def topology_of(name):
    """'split' or 'editor' for a scenario (TOPOLOGY_OVERRIDE wins)."""
    if TOPOLOGY_OVERRIDE in ("split", "editor"):
        return TOPOLOGY_OVERRIDE
    return "split" if name in SPLIT_TOPOLOGY else "editor"

# Two-machine runs (--remote-cells): the named cells are served by the other machine, started and read over SSH by
# Tools/ServerMeshing/remoteb.py. Their logs live on that machine, so they are copied into Logs/remote before grading.
REMOTE_CELLS = []
REMOTE_TOOL = os.path.join(ROOT, "Tools", "ServerMeshing", "remoteb.py")
REMOTE_LOGS = os.path.join(LOGS, "remote")
REMOTE_PORT = 8001
REMOTE_PERSIST = False
_last_fetch = 0.0


def remote(*argv, timeout=300):
    """Run remoteb.py for one remote cell and return its output."""
    r = subprocess.run([sys.executable, REMOTE_TOOL] + list(argv), capture_output=True, text=True, timeout=timeout)
    return (r.stdout or "") + (r.stderr or "")


def fetch_remote(force=False):
    """Copy the other machine's zone logs here. Throttled: grading calls this through zone_lines many times."""
    global _last_fetch
    if not REMOTE_CELLS:
        return
    if not force and time.time() - _last_fetch < 5:
        return
    _last_fetch = time.time()
    out = remote("fetch")
    if "fetched" not in out:
        raise RuntimeError("could not copy the remote machine's logs, so nothing about its cells can be graded: "
                           + " ".join(out.split())[:200])


# ------------------------------------------------------------------ rig plumbing
def zone_log(zone, suffix, day, map_id=MAP):
    name = f"Log_Map({map_id})-Ch({zone})-Alloc(False)-Instance()_{day}.{suffix}.log"
    return os.path.join(REMOTE_LOGS if zone in REMOTE_CELLS else LOGS, name)


def read_status():
    """The play runner's status (every key; a torn read while the editor rewrites it is read again)."""
    return rigtools.read_status_file(STATUS)


# The play runner rewrites its status every second while the editor ticks. A domain reload (every Play entry) stops the
# ticks, so a status older than this means the editor is busy rather than idle.
STATUS_STALE_S = 5
# How much longer a Play wait may run while the editor is busy, on top of its own timeout. Domain reloads slow down over
# a long editor session (5-10 s fresh, 30-46 s after a day of scenarios), which once tipped a 60 s wait over.
PLAY_BUSY_CAP_S = 120
# A Play start slower than this is reported: the editor has slowed down and wants a restart.
SLOW_PLAY_START_S = 45


def status_age():
    """Seconds since the play runner last wrote its status; None when it never has (no editor running the runner)."""
    try:
        return time.time() - os.path.getmtime(STATUS)
    except FileNotFoundError:
        return None


def wait_status(pred, timeout, what, busy_cap=0):
    """Wait for a status the predicate accepts. While the editor is busy (its status gone stale) the deadline moves
    with the clock, by at most busy_cap seconds in all, so a slow reload is not mistaken for a runner that ignored us."""
    end = time.time() + timeout
    hard = end + busy_cap
    last = time.time()
    while True:
        st = read_status()
        if pred(st):
            return st
        now = time.time()
        age = status_age() if busy_cap else None
        if age is not None and age > STATUS_STALE_S:
            end = min(hard, end + (now - last))
        last = now
        if now >= end:
            raise TimeoutError(f"timed out waiting for {what}")
        time.sleep(2)


def ps(script):
    """Run a PowerShell snippet and return its output; a failing snippet raises rather than reading as empty."""
    r = subprocess.run(["powershell", "-NoProfile", "-Command", script], capture_output=True, text=True)
    if r.returncode != 0:
        raise RuntimeError(f"PowerShell failed ({r.returncode}): {(r.stderr or r.stdout).strip()[:300]}")
    return r.stdout.strip()


# Only processes of the rig's own build: a player started from anywhere else on this machine is not the rig's to kill.
# "No such process" must not fail the snippet (ps raises on a failure). Get-Process -Name marks the pipeline failed when
# nothing matches, whatever its -ErrorAction; collecting it in @() first does not. A process that exits before
# Stop-Process reaches it is ignored too: every kill is followed by kit_process_count, which catches a real survivor.
RIG_PROCESSES = "@(Get-Process kit-ce-project -ErrorAction Ignore) | Where-Object { $_.Path -eq '" + EXE.replace("'", "''") + "' }"


# The ports the editor's servers and the cells bind (Config/serverConfig.json, MMOServerInstance-CE.prefab): map spawner,
# cluster, database manager, central, login, then the cells from spawnStartPort and their links meshLinkPortOffset above.
RIG_PORTS = [6001, 6010, 6100, 7000, 7500] + list(range(8000, 8016)) + list(range(18000, 18016))


def foreign_port_holders(ports=RIG_PORTS):
    """(port, pid, process name) for each rig UDP port held by anything but the rig's own build (by path).

    Called only while this editor is out of Play, when it binds none of these ports: any editor holding one, this one
    included after a stop that did not take, is named. A process whose path cannot be read (a service running as
    another user) counts as foreign."""
    out = ps("$ports = @(" + ",".join(str(p) for p in ports) + "); "
             "@(Get-NetUDPEndpoint -ErrorAction Ignore) | Where-Object { $ports -contains $_.LocalPort } | ForEach-Object { "
             "$p = Get-Process -Id $_.OwningProcess -ErrorAction Ignore; "
             "\"$($_.LocalPort)`t$($_.OwningProcess)`t$($p.ProcessName)`t$($p.Path)\" }")
    holders = set()
    for line in out.splitlines():
        port, pid, name, path = (line.split("\t") + ["", "", ""])[:4]
        if path and os.path.normcase(path) == os.path.normcase(EXE):
            continue
        holders.add((int(port), int(pid), name or "?"))
    return sorted(holders)


def preflight():
    """What would make every scenario fail before it starts: another server holding the rig's ports. The editor's servers
    then fail to bind, its login server joins the other cluster, and no cell ever logs."""
    by_process = {}
    for port, pid, name in foreign_port_holders():
        by_process.setdefault((pid, name), []).append(port)
    return [f"{name} (pid {pid}) holds rig port(s) {', '.join(map(str, ports))}; stop it for the run"
            for (pid, name), ports in sorted(by_process.items())]


def kill_bots():
    ps(RIG_PROCESSES + " | Where-Object { (Get-CimInstance Win32_Process -Filter \"ProcessId=$($_.Id)\").CommandLine -like '*-meshAutoClient*' } | Stop-Process -Force -ErrorAction Ignore")


def kit_process_count():
    out = ps("(" + RIG_PROCESSES + " | Measure-Object).Count")
    try:
        return int(out)
    except ValueError as ex:
        raise RuntimeError(f"could not count the rig's processes: {out[:200]!r}") from ex


def launch_bot(scenario, user, args, channel="C", observe_seconds=None, line_x=None, harvest_after_migration=False):
    """Start a headless bot on `channel`; the split scenarios' stand window, cut line and harvest variant are optional."""
    log = os.path.join(LOGS, f"regress_{scenario}_{user}.log")
    if os.path.exists(log):
        os.remove(log)
    extra = []
    if observe_seconds is not None:
        extra += ["-meshObserveSeconds", f"{observe_seconds:g}"]
    if line_x is not None:
        extra += ["-meshLineX", f"{line_x:g}"]
    if harvest_after_migration:
        extra += ["-meshHarvestAfterMigration", "1"]
    cmd = [EXE, "-batchmode", "-nographics", "-meshAutoClient", "-meshUser", user, "-meshChannel", channel] + args + extra + ["-logFile", log]
    subprocess.Popen(cmd, creationflags=getattr(subprocess, "DETACHED_PROCESS", 0) | getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0), stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    return log


_play_seq = None   # the id of the last Play requested, echoed by the runner as playSeq= while that Play runs
_play_env = {}     # the environment that Play was asked for (the scenario's own plus --env), for grading


def request_play(mode, walk_x, walk_z, pet=False, mount=False, env=None, enabled=True, observe_seconds=None, line_x=None):
    """Ask the editor to enter Play with the auto client in a mode (enabled=False: no client logs in at all). Slice 4:
    `observe_seconds` and `line_x` set the tester's stand window and cut line (the runner forwards them; None clears)."""
    global _play_seq, _play_env
    os.makedirs(TEMP, exist_ok=True)
    _play_seq = uuid.uuid4().hex[:8]
    lines = [f"seq={_play_seq}", f"mode={mode}", "channel=C", "user=meshtest", f"walkX={walk_x}", f"walkZ={walk_z}",
             f"pet={1 if pet else 0}", f"mount={1 if mount else 0}", f"enabled={1 if enabled else 0}"]
    if observe_seconds is not None:
        lines.append(f"observeSeconds={observe_seconds:g}")
    if line_x is not None:
        lines.append(f"lineX={line_x:g}")
    env = dict(EXTRA_ENV, **(env or {}))
    if topology_of(_current_scenario) == "split":
        # Slice 5: the servers are build processes (started now, after the database rows were written and before the
        # Play); the editor's Play hosts none of them.
        env["MESH_EDITOR_SERVERS"] = "0"
        regress_procs.start_cluster(sys.modules[__name__], _current_scenario, env)
    _play_env = env
    for key in ENV_KEYS:
        lines.append(f"env.{key}={env.get(key, '')}")   # every key set explicitly: an empty value clears it
    mark = {}
    if REMOTE_CELLS:
        # The remote cells can only join once the cluster exists, and the cluster is this editor's: so hold the
        # tester before its login, start them as soon as Play is up, and let it go when they are serving. The mark
        # is taken now, before Play, so the wait cannot be satisfied by the previous scenario's log lines.
        mark = remote_log_mark()
        remote("clearmarkers")      # the one-shot fault marker lives in B's own build folder, not in this Temp
        open(HOLD, "w").close()
    with open(REQUEST, "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")
    if REMOTE_CELLS:
        try:
            start_remote_cells(mark, env)
        finally:
            if os.path.exists(HOLD):
                os.remove(HOLD)


def wait_play(timeout):
    """Wait until the Play of the last request_play is running: not merely any Play, which could be one that an earlier,
    abandoned request started late, with that request's environment."""
    t0 = time.time()
    st = wait_status(lambda st: st.get("isPlaying") == "True" and st.get("playSeq") == _play_seq, timeout, "Play",
                     busy_cap=PLAY_BUSY_CAP_S)
    took = time.time() - t0
    if took > SLOW_PLAY_START_S:
        print(f"  ! Play took {took:.0f} s to start: the editor's domain reloads have slowed, restart the editor")
    return st


def play_still_running():
    """Whether the Play of the last request_play is still the one running (a run that lost it measured nothing)."""
    st = read_status()
    return st.get("isPlaying") == "True" and st.get("playSeq") == _play_seq


def remote_running():
    return "process: running" in remote("status")


def start_remote_cells(mark, env=None):
    """Get the other machine's cells serving this scenario, and wait until they are.

    Restarted for each scenario by default, because each Play recreates this machine's cells. With --remote-persist
    the cell is left running instead and has to recover on its own: the kit reconnects its cluster client after a
    disconnect, and the add-on's databaseReconnectSeconds re-opens the database connection, which the kit never
    does. That is the two-machine test of those two paths, so the wait below looks for all of it to come back --
    registration first, because a cell that has relinked is still not somewhere a player can be sent.
    """
    wait_play(60)
    t0 = time.time()
    cell = REMOTE_CELLS[0]
    if REMOTE_PERSIST and remote_running():
        # Nothing is restarted: the cell must notice the cluster and the database coming back by itself.
        wanted = [(r"App Registered successfully", "registered with the cluster again"),
                  (r"Neighbour zone \w+ linked", "relinked to this machine's cells"),
                  (r"Database connection to \S+ is open", "re-opened its database connection")]
    else:
        remote("stop")
        # The cell inherits this machine's environment through the spawner; the other machine's cannot, so the
        # scenario's variables are handed to it explicitly. MESH_SPAWN_CELLS is this spawner's business only.
        forwarded = []
        for key, value in sorted((env or {}).items()):
            if value and key != "MESH_SPAWN_CELLS":
                forwarded += ["--env", f"{key}={value}"]
        out = remote("start", "--cell", cell, "--port", str(REMOTE_PORT), "--restart", *forwarded)
        if "is running" not in out:
            raise RuntimeError("the remote cell did not start: " + " ".join(out.split())[:200])
        wanted = [(r"Own cell from channel id: " + cell + r"\b", "is serving")]
    deadline = t0 + 150
    pending = list(wanted)
    while time.time() < deadline:
        time.sleep(3)
        lines = remote_lines_after(mark)
        for want in list(pending):
            if count(lines, want[0]) >= 1:
                print(f"   remote cell {cell} {want[1]} after {time.time() - t0:.0f}s")
                pending.remove(want)
        if not pending:
            return
    raise RuntimeError(f"remote cell {cell} never {pending[0][1]}; "
                       "try: python Tools/ServerMeshing/remoteb.py status")


def kill_all_kit_processes():
    """Kill every process of the rig's build; returns how many are still alive afterwards (0 when it worked)."""
    ps(RIG_PROCESSES + " | Stop-Process -Force -ErrorAction Ignore")
    end = time.time() + 10
    while time.time() < end:
        alive = kit_process_count()
        if alive == 0:
            return 0
        time.sleep(1)
    return kit_process_count()


def stop_play():
    """Stop Play and wait for every rig process to exit. Returns how many had to be killed.

    Cells started by the editor's spawner exit with Play. One that outlives it (a dynamic cell whose parent forgot it,
    a hung process) would leak into the next scenario and serve its players, so it is killed and reported.
    """
    # A request the runner has not read yet would start a Play after this one stops, with this run's environment. If
    # the runner is taking it right now, the stop below is held until that Play has started, and then stops it.
    try:
        os.remove(REQUEST)
    except (FileNotFoundError, PermissionError):
        pass
    open(STOP, "w").close()
    try:
        # The stop file first, then the status: a status read after the runner consumed the stop was written by that
        # tick or a later one, so an isPlaying=False read then is current.
        end = time.time() + 90
        while os.path.exists(STOP):
            if time.time() > end:
                raise TimeoutError("timed out waiting for the play runner to take the stop request")
            time.sleep(0.5)
        wait_status(lambda st: st.get("isPlaying") == "False", max(1, end - time.time()), "Play to stop")
    except TimeoutError:
        print("  ! Play did not stop in time")
    kill_bots()
    if topology_of(_current_scenario) == "split":
        # Slice 5: the central, spawner and cells are build processes that outlive Play by design; the teardown ends
        # them in an order that lets nothing respawn, and whatever is left afterwards is a survivor.
        regress_procs.teardown(sys.modules[__name__])
        return kit_process_count()
    end = time.time() + 15
    while time.time() < end and kit_process_count() > 0:
        time.sleep(1)
    survivors = kit_process_count()
    if survivors > 0:
        still = kill_all_kit_processes()
        if still > 0:
            print(f"  ! {still} rig process(es) survived being killed")
    return survivors


def request_and_wait(request_path, seq, status_path, done, timeout, what):
    """Write a sequenced request for the editor's play runner and wait until done(status) holds for that sequence."""
    os.makedirs(TEMP, exist_ok=True)
    with open(request_path, "w", encoding="utf-8") as f:
        f.write(seq + "\n")
    end = time.time() + timeout
    while time.time() < end:
        st = rigtools.read_kv(status_path)
        if st.get("seq") == seq and done(st):
            return st
        time.sleep(2)
    raise TimeoutError(f"timed out waiting for {what}")


def refresh_editor(timeout=300):
    """Make the editor import changed scripts (it only auto-refreshes on focus) and wait for the compile to settle."""
    seq = uuid.uuid4().hex[:8]
    os.makedirs(TEMP, exist_ok=True)
    with open(REFRESH_REQUEST, "w", encoding="utf-8") as f:
        f.write(seq + "\n")
    # A refresh that recompiles reloads the domain, which resets the runner's echo, so the sequence cannot be relied
    # on. Instead: the request file is consumed, then the runner reports an idle editor on three consecutive status
    # writes (the compile starts a moment after the refresh returns, so one idle reading proves nothing).
    end = time.time() + timeout
    idle, last_time = 0, None
    while time.time() < end:
        time.sleep(2)
        st = read_status()
        if os.path.exists(REFRESH_REQUEST) or st.get("time") == last_time:
            continue
        last_time = st.get("time")
        idle = idle + 1 if st.get("isCompiling") == "False" and st.get("isUpdating") == "False" else 0
        if idle >= 3:
            return st
    raise TimeoutError("timed out waiting for the editor to refresh and compile")


def run_unit_tests(timeout=600):
    """Run the add-on's EditMode tests in the open editor. Returns (state, selected, passed, failed, failures)."""
    seq = uuid.uuid4().hex[:8]
    st = request_and_wait(TEST_REQUEST, seq, TEST_STATUS, lambda s: s.get("state") in ("done", "error"), timeout, "the EditMode tests")
    return (st.get("state"), int(st.get("selected", 0)), int(st.get("passed", 0)), int(st.get("failed", 0)),
            rigtools.as_list(st.get("failure")))


def build_player(timeout=1800):
    """Build the rig player in the open editor (ServerMeshRigBuild). Returns the build's result text."""
    seq = uuid.uuid4().hex[:8]
    st = request_and_wait(BUILD_REQUEST, seq, BUILD_STATUS, lambda s: s.get("result") not in (None, "running"), timeout, "the player build")
    return st.get("result", "")


def build_packages(timeout=900):
    """Build both .unitypackages in the open editor (ServerMeshPackageBuilder). Returns the build's result text."""
    seq = uuid.uuid4().hex[:8]
    st = request_and_wait(PACKAGE_REQUEST, seq, PACKAGE_STATUS, lambda s: s.get("result") not in (None, "running"), timeout, "the package build")
    return st.get("result", "")


# ------------------------------------------------------------------ database
def db_reset(tester_pos, bot_pos, tester_party=1, bot_party=1, clear_buildings=False):
    """Put both test characters back to their provisioned state (provision.py) at the given (x, z), in the given parties.

    Every scenario starts from the same bag, skills, quest and party, so none depends on what the previous one left
    (a consumed skill stack, a harvested yield, a saved summon). Refuses while the editor is in Play: its database
    manager caches characters and would not see these rows.
    """
    if provision.editor_is_playing(STATUS):
        raise RuntimeError("the editor is still in Play; the test characters can only be reset while it is stopped")
    if topology_of(_current_scenario) == "split" and regress_procs.database_running(sys.modules[__name__]):
        raise RuntimeError("a build process still hosts the database manager; the test characters can only be reset once it is gone")
    conn = db_connect()
    try:
        provision.provision(conn, {TESTER: provision.Placement(tester_pos[0], tester_pos[1], tester_party),
                                   BOT: provision.Placement(bot_pos[0], bot_pos[1], bot_party)})
        if clear_buildings:
            with conn.cursor() as cur:
                # Every channel of the test map, and nothing else: the database may be shared with another game. A
                # building loads by channel id, not by position, so one left on any cell lands differently per layout.
                # Slice 4: a building's storage rows go first (they are keyed by the building's id, not its channel).
                cur.execute("delete from storageitem where storageType=%s and storageOwnerId in "
                            "(select id from buildings where mapName=%s)", (STORAGE_TYPE_BUILDING, MAP))
                cur.execute("delete from buildings where mapName=%s", (MAP,))
    finally:
        conn.close()


def db_rows(sql, params=()):
    """The rows of one read-only query against the rig's database (Slice 4's scenario reads building and storage rows;
    the unit tests fake this)."""
    conn = db_connect()
    try:
        with conn.cursor() as cur:
            cur.execute(sql, params)
            return cur.fetchall()
    finally:
        conn.close()


def character_id(name):
    """The id of the one character with this name (names are unique in the kit, but not by index)."""
    conn = db_connect()
    try:
        with conn.cursor() as cur:
            cur.execute("select id from characters where characterName=%s", (name,))
            rows = cur.fetchall()
    finally:
        conn.close()
    if len(rows) != 1:
        raise RuntimeError(f"{len(rows)} characters named {name}; run Tools/ServerMeshing/provision.py")
    return rows[0][0]


# ------------------------------------------------------------------ grading
STAMP = rigtools.STAMP


def remote_log_mark():
    """How many lines the remote cell's logs hold right now, per severity.

    Timestamps were the wrong tool for this: the wait used a window starting five seconds before Play was noticed,
    which could match the PREVIOUS scenario's tail and declare the cell ready while it was still registering for
    this one (seen 2026-09-17, seam). A line count taken before Play cannot do that, and does not care whether the
    two machines' clocks agree.
    """
    if not REMOTE_CELLS:
        return {}
    fetch_remote(force=True)
    day = time.strftime("%Y-%m-%d")
    mark = {}
    for suffix in ("info", "warn", "error"):
        path = zone_log(REMOTE_CELLS[0], suffix, day)
        mark[suffix] = sum(1 for _ in open(path, encoding="utf-8", errors="replace")) if os.path.exists(path) else 0
    return mark


def remote_lines_after(mark):
    """Every line the remote cell has logged since the mark was taken."""
    fetch_remote(force=True)
    day = time.strftime("%Y-%m-%d")
    out = []
    for suffix in ("info", "warn", "error"):
        path = zone_log(REMOTE_CELLS[0], suffix, day)
        if not os.path.exists(path):
            continue
        with open(path, encoding="utf-8", errors="replace") as f:
            for i, line in enumerate(f):
                if i >= mark.get(suffix, 0):
                    out.append(line.rstrip("\n"))
    return out


def zone_lines(zone, start, day, map_id=MAP, suffixes=("info", "warn", "error")):
    """Every line of the zone's logs (info, warn and error by default) stamped at or after `start` (HH:MM:SS) on `day`.

    The kit writes one file per day, so a scenario that crosses midnight carries on in the next day's file: both are
    read, and stamps are compared as "date time" rather than time alone, so a line from 00:01 is not thrown away
    for sorting below 23:59. (Before this, a run over midnight silently lost every line after it.)
    """
    if zone in REMOTE_CELLS:
        fetch_remote()
        # A missing remote log must fail loudly: read as "no lines", it turns every check on that cell into a
        # silent pass, which is worse than no coverage at all.
        if not any(os.path.exists(zone_log(zone, sfx, day, map_id)) for sfx in ("info", "warn", "error")):
            raise RuntimeError(f"no log for remote cell {zone} in {REMOTE_LOGS}; it may not have started today")
    since = f"{day} {start}"
    next_day = (datetime.date.fromisoformat(day) + datetime.timedelta(days=1)).isoformat()
    out = []
    for d in (day, next_day):
        for suffix in suffixes:
            path = zone_log(zone, suffix, d, map_id)
            if not os.path.exists(path):
                continue
            for line in open(path, encoding="utf-8", errors="replace"):
                stamp = rigtools.stamp_of(line)
                if stamp and f"{stamp[0]} {stamp[1]}" >= since:
                    out.append(line.rstrip("\n"))
    return out


def central_lines(start, day, suffixes=("info", "warn", "error")):
    """Slice 5: every line of the build-hosted central's kit log (Logs/Log_Database_Central_Login_<day>.<sev>.log: one
    file per day for every lifetime of the process) stamped at or after `start` on `day`, as zone_lines reads a cell's."""
    since = f"{day} {start}"
    next_day = (datetime.date.fromisoformat(day) + datetime.timedelta(days=1)).isoformat()
    out = []
    for d in (day, next_day):
        for suffix in suffixes:
            path = rigprocs.kit_log_path(LOGS, rigprocs.CENTRAL_LOG_PREFIX, d, suffix)
            if not os.path.exists(path):
                continue
            for line in open(path, encoding="utf-8", errors="replace"):
                stamp = rigtools.stamp_of(line)
                if stamp and f"{stamp[0]} {stamp[1]}" >= since:
                    out.append(line.rstrip("\n"))
    return out


def wait_log(zone, map_id, pattern, start, day, timeout):
    """Wait until the zone logs a line matching pattern after the scenario start; returns it, or None on timeout."""
    rx = re.compile(pattern)
    end = time.time() + timeout
    while time.time() < end:
        for line in zone_lines(zone, start, day, map_id):
            if rx.search(line):
                return line
        time.sleep(2)
    return None


def expected_zones(expect):
    """The cells a scenario expects to have run: every local static cell by default, or the ids it names (an id
    outside ZONES, a dynamic child such as C.1, is on the test map)."""
    if expect is None:
        return [(z, m) for z, m in ZONES if z not in REMOTE_CELLS]
    known = {z for z, _ in ZONES}
    return ([(z, m) for z, m in ZONES if z in expect and z not in REMOTE_CELLS]
            + [(z, MAP) for z in sorted(expect) if z not in known and z not in REMOTE_CELLS])


def graded_zones(day):
    """The static rig cells plus every cell that logged today or tomorrow: dynamic cells have runtime ids."""
    next_day = (datetime.date.fromisoformat(day) + datetime.timedelta(days=1)).isoformat()
    zones = list(ZONES)
    for cell, map_id in rigtools.discover_cells(LOGS, {day, next_day}):
        if (cell, map_id) not in zones and cell not in REMOTE_CELLS:
            zones.append((cell, map_id))
    return zones


def count(lines, pattern):
    rx = re.compile(pattern)
    return sum(1 for line in lines if rx.search(line))


class Report:
    def __init__(self, name):
        self.name, self.checks, self.notes = name, [], []

    def check(self, ok, label):
        self.checks.append((bool(ok), label))
        return ok

    @property
    def passed(self):
        return all(ok for ok, _ in self.checks)


def grade_common(rep, start, day, expect=None):
    # A teleported puppet is a visible pop for everyone in that cell, and nothing used to report one: the observer
    # watches for entities appearing and vanishing, not for jumping. A player popping is the one that matters, so
    # the two are counted apart. Do NOT anchor this on a character name: a monster puppet has none, and the first
    # version of this check ("Ghost \S+ teleported") passed while the stats line beside it said teleports=2.
    zones = graded_zones(day)
    # A cell that should have run and logged nothing (every cell logs its Mesh stats every 10 s) was never started, or
    # died before logging: its zero teleports and zero exceptions below would otherwise pass for it.
    for zone, map_id in expected_zones(expect):
        rep.check(len(zone_lines(zone, start, day, map_id)) > 0, f"zone {zone}: logged since the scenario started")
    for zone, map_id in zones:
        lines = zone_lines(zone, start, day, map_id)
        players = count(lines, r"Ghost teleported: Player ")
        others = count(lines, r"Ghost teleported: (?!Player )")
        rep.check(players == 0, f"zone {zone}: {players} PLAYER puppet teleport(s), {others} non-player")

    for z, map_id in zones:
        lines = zone_lines(z, start, day, map_id)
        ex = [line for line in lines if "Exception" in line]
        rep.check(len(ex) == 0, f"zone {z}: {len(ex)} exception lines")
        for line in ex[:3]:
            rep.notes.append(f"   {z}: {line[:160]}")


def grade_layout(rep, start, day):
    """Dynamic cells as far as 2a goes (DESIGN.md 0.72): a Dynamic map's cells apply the central's layout once per
    process, before a player registers; a Static map's cells apply none; no cell splits, merges, retires or waits at
    the layout gate. Which maps are Dynamic comes from the rig's registry asset and the Play's MESH_CELL_MODE."""
    try:
        dynamic = rigtools.dynamic_maps(_play_env.get("MESH_CELL_MODE", ""), rigtools.asset_cell_modes(RIG_REGISTRY),
                                        {m for _, m in ZONES})
    except (OSError, ValueError) as ex:
        rep.check(False, f"layout: cannot tell which maps are Dynamic: {ex}")
        return
    for zone, map_id in graded_zones(day):
        if zone in REMOTE_CELLS and REMOTE_PERSIST:
            continue    # a persisted remote cell got no environment from this Play: its mode is the asset's
        info = zone_lines(zone, start, day, map_id, suffixes=("info",))
        problems = rigtools.layout_apply_violations(zone, info, map_id in dynamic)
        everything = zone_lines(zone, start, day, map_id)
        ops = sum(1 for line in everything if rigtools.OP_LINE.search(line))
        gate = sum(1 for line in everything if rigtools.LAYOUT_GATE_WARNING.search(line))
        if ops:
            problems.append(f"{ops} split/merge/retire line(s)")
        if gate:
            problems.append(f"{gate} layout gate warning(s)")
        how = "Dynamic: layout applied before any player" if map_id in dynamic else "Static: no layout applied"
        rep.check(not problems, f"zone {zone}: {how}" + (f" ({'; '.join(problems[:2])})" if problems else ""))
    grade_no_policy(rep)


def grade_no_policy(rep):
    """Slice 3 decision 16: a scenario that does not turn the automatic policy on sees no `Policy:` line in the
    status file (the central writes no log the harness reads; the runner mirrors its lines under `auto`)."""
    policy = rigauto.policy_texts(regress_split.read_lines(LAYOUT_COMMAND_STATUS))
    rep.check(not policy, "no automatic policy line in the status file" + (f" ({policy[0][:100]})" if policy else ""))


def registry_state(paths):
    """(git status of the world registry assets, {path: sha1 of their line-end-normalized bytes}). Play must never
    write a runtime layout into an asset, and git cannot see an asset the editor has changed but not yet saved, so
    both are compared."""
    status = subprocess.run(["git", "-C", ROOT, "status", "--porcelain", "--", *paths], capture_output=True, text=True)
    if status.returncode != 0:
        raise RuntimeError(f"git status of the registry assets failed: {status.stderr.strip()}")
    digests = {}
    for path in paths:
        with open(path, "rb") as f:
            digests[path] = hashlib.sha1(rigtools.normalized_bytes(f.read())).hexdigest()
    return status.stdout.strip(), digests


def hms(t):
    h, m, s2 = t.split(":")
    return int(h) * 3600 + int(m) * 60 + int(s2)


def bot_result(log, kind):
    try:
        for line in open(log, encoding="utf-8", errors="replace"):
            if f"RESULT {kind}:" in line:
                return line.strip()[line.find("RESULT"):]
    except FileNotFoundError:
        pass
    return ""


# ------------------------------------------------------------------ scenarios
def run_scenario(name, rep):
    global _current_scenario
    _current_scenario = name
    day = datetime.date.today().isoformat()
    start = datetime.datetime.now().strftime("%H:%M:%S")
    for marker in os.listdir(TEMP) if os.path.isdir(TEMP) else []:
        if marker.startswith("ServerMesh.FaultFired."):
            os.remove(os.path.join(TEMP, marker))
    # The central's layout coordinator keeps its store across Plays; one left by an earlier run would be recovered
    # from, so every scenario starts without one. A layout command or its status left by an earlier run would be
    # carried out again or read as this run's (Slice 2b decision 17).
    for leftover in (COORDINATOR_STORE, COORDINATOR_STORE + ".tmp", LAYOUT_COMMAND, LAYOUT_COMMAND_STATUS):
        if os.path.exists(leftover):
            os.remove(leftover)
    bots = []
    split = None

    if name == "pet":
        # The pet round trip: an observer 12 m from the line watches the bot cross with its pet and come back.
        db_reset((86.5, 0), (0, 0))
        request_play("observe", 88, 0)
        wait_play(60)
        time.sleep(20)
        bots.append(("walk", launch_bot(name, "meshbot", ["-meshAutoMode", "walk", "-meshWalkX", "130", "-meshWalkZ", "0", "-meshPet"])))
        st = wait_status(lambda st: st.get("finished") == "True", 240, "the observer")
        walk = wait_for_bot(bots[0][1], "walk", 240)
        rep.check("-> PASS" in st.get("result", ""), f"observer: {st.get('result', '')[:120]}")
        rep.check("playerSwaps=0 absences=0" in st.get("result", ""), "observer saw no swap and no absence")
        rep.check("-> PASS" in walk and "pet=seen" in walk, f"bot: {walk[:120]}")
        c_lines, e_lines = zone_lines("C", start, day), zone_lines("E", start, day)
        rep.check(count(e_lines, r"Summon .* promoted in place") >= 1, "E promoted the pet in place")
        rep.check(count(c_lines, r"Summon .* promoted in place") >= 1, "C promoted the pet in place on the way back")
        rep.check(count(c_lines, r"Handoff ACK .*promoted=True") >= 1 and count(e_lines, r"Handoff ACK .*promoted=True") >= 1, "both handoffs acked as promoted")

    elif name == "party":
        # Fight at the line with a party member standing inside E: shares cross the line both ways. The member
        # starts a few metres from the line so it is inside E before the first kill (from the map origin its walk
        # took longer than the fighter's first kills and the shares stayed on C).
        # Known flake (~1 in 10, seen 2026-09-16): a run where E's monsters have all wandered further past the line
        # than the bow reaches ends hurtTarget=True kills=0 and fails the three share checks. The fighter DOES walk
        # toward its target (PointClickMovement, x clamped to the line) but must stay on C for its hits to be
        # forwarded, so it cannot follow a monster deeper into E. Inherent to using live monsters as targets; the
        # standing party member at (104,58) is the bait that keeps them near the line, and usually does.
        # A second stall, fixed 2026-09-26 (3 of 4 Dynamic runs that day): the kit starts an attack 0.5 m out, so a monster
        # standing 1-1.5 m past the line, hitting the fighter's puppet over there, was neither reachable nor dropped, and
        # the fighter stood at the line for 150 s without a shot (hurtTarget=False). It now shoots from the line.
        db_reset((86.5, 0), (94, 58))
        request_play("fight", 99, 60, env={"MESH_NO_MONSTER_HANDOFF": "1"})
        wait_play(60)
        time.sleep(20)
        bots.append(("stand", launch_bot(name, "meshbot", ["-meshAutoMode", "stand", "-meshWalkX", "104", "-meshWalkZ", "58"])))
        st = wait_status(lambda st: st.get("finished") == "True", 300, "the fighter")
        rep.check("-> PASS" in st.get("result", ""), f"fighter: {st.get('result', '')[:140]}")
        m = re.search(r"questKills\+(\d+)", st.get("result", ""))
        rep.check(m is not None and int(m.group(1)) >= 1, "the fighter's quest kill counter advanced for kills on the neighbour's monsters (kill credit forwarded)")
        # Projectiles: the tester fights with the test bow (right hand), so its hits on E's monsters are arrows that struck
        # puppets on C and were forwarded; E replays the shots on the tester's puppet so its players see them.
        conn = db_connect()
        try:
            with conn.cursor() as cur:
                cur.execute("select dataId from characteritem where characterId=%s and inventoryType=2", (character_id(TESTER),))
                weapon = cur.fetchone()
        finally:
            conn.close()
        rep.check(weapon is not None and weapon[0] == ITEM_BOW, "the fighter's right hand holds the test bow (projectile weapon)")
        c_lines = zone_lines("C", start, day)
        rep.check(count(c_lines, r"Hit on ghost .* forwarded to zone E") >= 1, "arrow hits on the neighbour's monster puppets were forwarded to E")
        rep.check(count(zone_lines("E", start, day), r"Ghost attack replayed on MeshTester2") >= 1, "E replayed the fighter's shots on its puppet")
        c_lines, e_lines = zone_lines("C", start, day), zone_lines("E", start, day)
        # The split is logged by whichever cell holds the member whose reward arrived from elsewhere: C for the
        # fighter's kills, E for the member's. Pinning it to C made the check depend on which side happened to make
        # a kill in the window, and it failed on a run where every split landed on E (2026-09-17). Ask instead for
        # what the check is named after -- a split that included a member who is a ghost on the splitting cell --
        # which the old regex never actually verified.
        rep.check(count(c_lines + e_lines, r"Party \d+: shared exp with \d+ here \+ [1-9]\d* on other cells") >= 1,
                  "a kill was split with a party member that is a ghost on the splitting cell")
        # Either direction counts: the fighter's kills on C forward to the member on E, and monsters the member kills
        # back on E forward to the fighter on C (the grading used to read only the E-to-C direction).
        both = c_lines + e_lines
        rep.check(count(both, r"Party share for .* forwarded to zone") >= 1 and count(both, r"Party share applied to") >= 1, "a share crossed the line")

    elif name == "seam":
        # Across the seam between two maps: the bot walks from E into C2 (map ServerMeshTestMap2, whose frame sits 400 m
        # east, so x=301 on E is x=-99 there) and back; the tester stands on E 8 m from the seam, so C2 must see it as a
        # ghost at the translated position (-108) and the bot's row is saved in C2's frame on the way over.
        db_reset((292, 0), (280, 0))
        request_play("stand", 292, 0)
        wait_play(60)
        time.sleep(20)
        bots.append(("walk", launch_bot(name, "meshbot", ["-meshAutoMode", "walk", "-meshWalkX", "310", "-meshReturnX", "-110", "-meshStopOnMigration"])))
        walk = wait_for_bot(bots[0][1], "walk", 240)
        rep.check("-> PASS" in walk and "migrations=2" in walk, f"bot: {walk[:120]}")
        e_lines, c2_lines = zone_lines("E", start, day), zone_lines("C2", start, day, MAP2)
        rep.check(count(e_lines, r"Handoff SENT meshbotChar -> zone C2") >= 1, "E handed the bot to C2 (another map)")
        rep.check(count(e_lines, r"frame offset \(-400") >= 1, "the save went into C2's frame (offset -400)")
        rep.check(count(e_lines, r"Handoff ACK .*promoted=True") >= 1, "C2 acked the arrival on its promoted ghost")
        rep.check(count(c2_lines, r"Handoff SENT meshbotChar -> zone E") >= 1, "C2 handed the bot back to E")
        rep.check(count(c2_lines, r"Ghost spawned Player MeshTester2 .* at \(-108") >= 1, "C2 sees the standing tester at the translated position (-108)")
        rep.check(count(c2_lines, r"Neighbour zone E linked .*frame offset \(-400") >= 1, "C2's link from E carries the frame offset")
        bot_lines = open(bots[0][1], encoding="utf-8", errors="replace").read().splitlines()
        rep.check(count(bot_lines, r"Seam: pre-loaded scene ServerMeshTestMap2 ") >= 1, "the bot pre-loaded map 2's scene in the band")
        rep.check(count(bot_lines, r"Seam: swapped to scene ServerMeshTestMap2 ") >= 1, "the bot swapped frames into map 2's scene at the hop")
        rep.check(count(bot_lines, r"Seam: swapped to scene ServerMeshTestMap ") >= 1, "the bot swapped back into map 1's scene on the return")

    elif name == "seamcold":
        # The same crossing without the client pre-load: the kit reloads the scene at the seam (loading screen). The
        # hop must still complete both ways without exceptions; no frame swap may be logged.
        db_reset((292, 0), (280, 0))
        request_play("stand", 292, 0)
        wait_play(60)
        time.sleep(20)
        bots.append(("walk", launch_bot(name, "meshbot", ["-meshAutoMode", "walk", "-meshWalkX", "310", "-meshReturnX", "-110", "-meshStopOnMigration", "-meshSeamPreload", "0"])))
        walk = wait_for_bot(bots[0][1], "walk", 240)
        rep.check("migrations=2" in walk, f"bot: {walk[:120]}")
        e_lines, c2_lines = zone_lines("E", start, day), zone_lines("C2", start, day, MAP2)
        rep.check(count(e_lines, r"Handoff SENT meshbotChar -> zone C2") >= 1, "E handed the bot to C2 (another map)")
        rep.check(count(c2_lines, r"Handoff SENT meshbotChar -> zone E") >= 1, "C2 handed the bot back to E")
        bot_lines = open(bots[0][1], encoding="utf-8", errors="replace").read().splitlines()
        rep.check(count(bot_lines, r"Seam pre-load distance set to 0") >= 1, "the bot ran without the seam pre-load")
        rep.check(count(bot_lines, r"Seam: (pre-loaded|swapped)") == 0, "no pre-load or frame swap happened")
        # The kit's scene load ends with Resources.UnloadUnusedAssets(), which Unity logs; one per hop after the walk began.
        walk_start = next((i for i, line in enumerate(bot_lines) if "Walking from" in line), 0)
        rep.check(count(bot_lines[walk_start:], r"Unloading \d+ unused Assets") >= 2, "the kit reloaded the scene at each crossing")

    elif name == "sleep":
        # Sleeping cells: nothing starts at boot. The tester's login into C wakes C, whose ring (1 on the rig) wakes E, N
        # and NE; C2 stays asleep. The bot logs in on E's west half (which wakes C2, 100 m ahead of the seam), walks across the
        # seam into C2 and back — seamless. Then the bot leaves; with only the tester on C, C2 is unwanted and exits after the
        # grace period.
        # The bot logs in on E's west half: its login wakes C2 (E's ring), 90 m ahead of the seam. Not at (200, 0): the test
        # map's WarpPortal_E_to_W stands there and warps whoever spawns inside it to (-200, 1, 0) (DESIGN 0.55).
        db_reset((20, 0), (210, 0))
        request_play("stand", 20, 0, env={"MESH_SLEEP": "1", "MESH_SLEEP_RINGS": "1", "MESH_SLEEP_AFTER": "30"})
        wait_play(60)
        time.sleep(25)
        c2_before = zone_lines("C2", start, day, MAP2)
        rep.check(count(c2_before, r"Own cell from channel id: C2") == 0, "C2 was not started at boot (asleep)")
        rep.check(count(zone_lines("C", start, day), r"Own cell from channel id: C") >= 1, "C woke for the tester's login")
        bots.append(("walk", launch_bot(name, "meshbot", ["-meshAutoMode", "walk", "-meshWalkX", "310", "-meshReturnX", "-110", "-meshStopOnMigration"])))
        walk = wait_for_bot(bots[0][1], "walk", 300)
        rep.check("-> PASS" in walk and "migrations=2" in walk, f"bot: {walk[:120]}")
        e_lines, c2_lines = zone_lines("E", start, day), zone_lines("C2", start, day, MAP2)
        rep.check(count(c2_lines, r"Own cell from channel id: C2") >= 1, "C2 woke once the bot was on E")
        woke = next((rigtools.stamp_of(line)[1] for line in c2_lines if "Own cell from channel id: C2" in line), None)
        entered_e = next((rigtools.stamp_of(line)[1] for line in e_lines if "player registered meshbotChar" in line), None)
        hop = next((rigtools.stamp_of(line)[1] for line in e_lines if "Handoff START meshbotChar" in line), None)
        rep.check(woke is not None and entered_e is not None and entered_e <= woke, f"C2 woke after the bot arrived on E (arrived {entered_e}, serving {woke})")
        rep.check(woke is not None and hop is not None and hms(hop) - hms(woke) >= 5, f"C2 was serving at least 5 s before the bot's crossing (serving {woke}, crossing {hop})")
        rep.check(count(e_lines, r"Handoff ACK meshbotChar by zone C2:.*promoted=") >= 1, "the crossing into the woken cell was acked")
        kill_bots()
        time.sleep(75)
        c2_lines = zone_lines("C2", start, day, MAP2)
        rep.check(count(c2_lines, r"Sleeping: cell C2 has nobody near it") >= 1, "C2 went to sleep once nobody was near")
        rep.check(count(zone_lines("E", start, day), r"Sleeping: cell E") == 0, "E stayed up (in the tester's ring)")

    elif name == "wake":
        # The boundary tick's wake request (the safety net behind the awake rings): with no rings, C2 stays asleep while
        # the bot is on E, so the first step past the seam finds no server for C2 and E asks the central to wake it.
        # The bot walks 60 m into C2's ground so the leg outlasts C2's start-up (2-4 s) and the hop happens mid-walk.
        # Before 0.71 the request threw inside the transport (no response handler registered) and was never sent.
        db_reset((20, 0), (210, 0))
        request_play("stand", 20, 0, env={"MESH_SLEEP": "1", "MESH_SLEEP_RINGS": "0", "MESH_SLEEP_AFTER": "300"})
        wait_play(60)
        time.sleep(25)
        rep.check(count(zone_lines("C2", start, day, MAP2), r"Own cell from channel id: C2") == 0, "C2 was asleep before the bot came (no rings)")
        bots.append(("walk", launch_bot(name, "meshbot", ["-meshAutoMode", "walk", "-meshWalkX", "360", "-meshReturnX", "-110", "-meshStopOnMigration"])))
        walk = wait_for_bot(bots[0][1], "walk", 360)
        rep.check("-> PASS" in walk and "migrations=2" in walk, f"bot: {walk[:120]}")
        e_lines, c2_lines = zone_lines("E", start, day), zone_lines("C2", start, day, MAP2)
        asked = next((line for line in e_lines if "Wake requested for sleeping cell C2" in line), None)
        woke = next((line for line in c2_lines if "Own cell from channel id: C2" in line), None)
        rep.check(asked is not None, "E asked the central to wake C2 when the bot crossed toward it")
        rep.check(count(e_lines, r"Wake request for cell C2 (answered|refused|could not)") == 0, "the wake request was sent and accepted")
        rep.check(asked is not None and woke is not None and rigtools.stamp_datetime(woke) >= rigtools.stamp_datetime(asked),
                  "C2 started after the wake request")
        rep.check(count(e_lines, r"Handoff ACK meshbotChar by zone C2:") >= 1, "the crossing into the woken cell was acked")

    elif name == "crash":
        # The destination dies on the handoff notice: rollback, client fallback, MapSpawn restart, re-registration.
        db_reset((86.5, 0), (0, 0))
        request_play("observe", 88, 0, env={"MESH_FAULT_DIE_ON_NOTICE": "E"})
        wait_play(60)
        time.sleep(20)
        bots.append(("walk", launch_bot(name, "meshbot", ["-meshAutoMode", "walk", "-meshWalkX", "130", "-meshWalkZ", "0", "-meshPet"])))
        st = wait_status(lambda st: st.get("finished") == "True", 240, "the observer")
        walk = wait_for_bot(bots[0][1], "walk", 300)
        rep.check("-> PASS" in walk, f"bot: {walk[:120]}")
        rep.check("fellBack=True" in open(bots[0][1], encoding="utf-8", errors="replace").read(), "the client fell back to the origin")
        rep.check("playerSwaps=0" in st.get("result", ""), f"observer: {st.get('result', '')[:120]}")
        c_lines, e_lines = zone_lines("C", start, day), zone_lines("E", start, day)
        rep.check(count(e_lines, r"FAULT: dying on handoff notice") == 1, "E died once on the notice")
        rep.check(count(c_lines, r"Handoff ROLLBACK ") >= 1, "C rolled the handoff back")
        rep.check(count(c_lines, r"ROLLBACK complete") >= 1, "the client's return promoted the held puppet")
        rep.check(count(e_lines, r"Cluster registration accepted") >= 1, "the restarted E re-registered with the cluster")
        # DESIGN 0.79: every map's cell reads its mesh link only once its server scene is ready (Static maps too), so
        # both incarnations log the release; a neighbour that still held E's address could otherwise link it early.
        rep.check(count(e_lines, r"Mesh link of E read from now: .* (Static|Dynamic) map\)") >= 2,
                  "both of E's incarnations read the mesh link only once the server scene was ready")
        rep.check(count(c_lines, r"Handoff ACK .*promoted=") >= 1, "a later handoff to the restarted E was acked")

    elif name == "harvest":
        db_reset((86.5, 62), (0, 0))
        request_play("harvest", 99, 62, env={"MESH_NO_MONSTER_HANDOFF": "1"})
        wait_play(60)
        st = wait_status(lambda st: st.get("finished") == "True", 240, "the harvester")
        rep.check("-> PASS" in st.get("result", "") and f"itemsGained=[{ITEM_YIELD}x" in st.get("result", ""), f"harvester: {st.get('result', '')[:140]}")
        c_lines, e_lines = zone_lines("C", start, day), zone_lines("E", start, day)
        rep.check(count(e_lines, r"Forwarded hit applied to static") >= 1, "E applied forwarded swings to its node")
        rep.check(count(c_lines, r"GiveItems applied") >= 1, "the yield reached the real character on C")

    elif name == "build":
        db_reset((86.5, 66), (0, 0), clear_buildings=True)
        request_play("build", 99, 66, env={"MESH_NO_MONSTER_HANDOFF": "1"})
        wait_play(60)
        st = wait_status(lambda st: st.get("finished") == "True", 240, "the builder")
        rep.check("-> PASS" in st.get("result", "") and "appeared=True" in st.get("result", ""), f"builder: {st.get('result', '')[:140]}")
        c_lines, e_lines = zone_lines("C", start, day), zone_lines("E", start, day)
        rep.check(count(c_lines, r"belongs to zone E") >= 1, "C handed the placement to E")
        rep.check(count(e_lines, r"creating here") >= 1, "E created the building")
        rep.check(count(c_lines, r"Building ghost spawned") >= 1, "the building's ghost appeared on C")
        rep.check(count(e_lines, r"Forwarded hit applied to static") >= 1, "E applied forwarded swings to the wall")

    elif name == "pvp":
        # A party member's ghost must not be attackable; the map's PvP mode must be Pvp for the stranger half,
        # so this scenario only checks the ally refusal under the map's current rules. No monsters: the check is that
        # the target was not hurt, and the bot stands in E's spawn area, where they would hurt it whatever the attacker
        # does (a run on 2026-09-25 read targetHp 500->276 with every hostile cast refused and no hit forwarded).
        db_reset((86.5, 60), (0, 0))
        request_play("pvp", 99, 60, env={"MESH_NO_MONSTER_HANDOFF": "1", "MESH_NO_MONSTERS": "1"})
        wait_play(60)
        time.sleep(20)
        bots.append(("stand", launch_bot(name, "meshbot", ["-meshAutoMode", "stand", "-meshWalkX", "103", "-meshWalkZ", "60"])))
        st = wait_status(lambda st: st.get("finished") == "True", 300, "the attacker")
        rep.check("targetHurt=False" in st.get("result", ""), f"attacker: {st.get('result', '')[:140]}")
        c_lines = zone_lines("C", start, day)
        rep.check(count(c_lines, r"Hit on ghost meshbotChar") == 0, "no hit on the party member's ghost was forwarded")

    elif name == "dynidle":
        # Dynamic cells, idle (DESIGN.md 0.72): the test map runs Dynamic with nothing to split or merge. Each of its
        # cells says hello and applies the central's (empty) layout before a player registers, and a walk from C to E
        # still hands off in place, which needs both cells' gates open. C2's map stays Static and applies nothing.
        db_reset((86.5, 0), (0, 0))
        request_play("observe", 88, 0, env={"MESH_CELL_MODE": f"{MAP}:Dynamic"})
        wait_play(60)
        time.sleep(20)
        bots.append(("walk", launch_bot(name, "meshbot", ["-meshAutoMode", "walk", "-meshWalkX", "130", "-meshWalkZ", "0"])))
        st = wait_status(lambda st: st.get("finished") == "True", 240, "the observer")
        walk = wait_for_bot(bots[0][1], "walk", 240)
        rep.check("-> PASS" in st.get("result", ""), f"observer: {st.get('result', '')[:120]}")
        rep.check("-> PASS" in walk, f"bot: {walk[:120]}")
        c_lines, e_lines = zone_lines("C", start, day), zone_lines("E", start, day)
        rep.check(count(c_lines, r"Handoff ACK \S+ by zone E: .*promoted=True") >= 1, "E took the bot over in place (both gates open)")
        for zone, lines in (("C", c_lines), ("E", e_lines)):
            rep.check(count(lines, r"Layout gate open on ") == 1, f"{zone}: layout gate opened once")
        c2_lines = zone_lines("C2", start, day, MAP2)
        rep.check(count(c2_lines, rigtools.LAYOUT_APPLIED.pattern) == 0,
                  f"C2 (Static map) applied no own-cell layout ({count(c2_lines, rigtools.NEIGHBOURS_APPLIED.pattern)} Static-neighbour applies)")

    elif name in regress_split.SCENARIOS:
        split = regress_split.run(name, rep, sys.modules[__name__], start, day, bots)

    elif name in regress_merge.SCENARIOS:
        # Slice 2c: the split's setup, then the merge back (regress_merge.run returns its MergeRun for the layout grade).
        split = regress_merge.run(name, rep, sys.modules[__name__], start, day, bots)

    elif name in regress_splitcrash.SCENARIOS:
        split = regress_splitcrash.run(name, rep, sys.modules[__name__], start, day, bots)   # Slice 2d: a CrashRun

    elif name in regress_auto.SCENARIOS:
        split = regress_auto.run(name, rep, sys.modules[__name__], start, day, bots)   # Slice 3: an AutoRun

    elif name in regress_building.SCENARIOS:
        split = regress_building.run(name, rep, sys.modules[__name__], start, day, bots)   # Slice 4: a BuildRun

    elif name in regress_central.SCENARIOS:
        split = regress_central.run(name, rep, sys.modules[__name__], start, day, bots)   # Slice 5: a CentralRun

    else:
        raise ValueError(f"unknown scenario {name}")

    # The sleeping-cell scenarios leave cells asleep on purpose; the Coarse ones run one cell; every other scenario
    # runs them all (autosplit: plus the child the policy makes).
    expect = {"wake": {"C", "E", "C2"}, "autosplit": {z for z, _ in ZONES} | {"C.1"}, **regress_auto.EXPECTED_ZONES,
              **regress_building.EXPECTED_ZONES, **regress_central.EXPECTED_ZONES}
    grade_common(rep, start, day, expect=expect.get(name))
    if name in regress_split.SCENARIOS:
        regress_split.grade_layout(rep, sys.modules[__name__], start, day, split)
    elif name in regress_merge.SCENARIOS:
        regress_merge.grade_layout(rep, sys.modules[__name__], start, day, split)
    elif name in regress_splitcrash.SCENARIOS:
        regress_splitcrash.grade_layout(rep, sys.modules[__name__], start, day, split)
    elif name in regress_auto.SCENARIOS:
        regress_auto.grade_layout(rep, sys.modules[__name__], start, day, split)
    elif name in regress_building.SCENARIOS:
        regress_building.grade_layout(rep, sys.modules[__name__], start, day, split)
    elif name in regress_central.SCENARIOS:
        regress_central.grade_layout(rep, sys.modules[__name__], start, day, split)
    else:
        grade_layout(rep, start, day)
    for kind, log in bots:
        try:
            txt = open(log, encoding="utf-8", errors="replace").read()
            n = txt.count("Exception")
            rep.check(n == 0, f"bot log {os.path.basename(log)}: {n} exception lines")
            # Nothing in the rig sets one; a puppet's steering speed that survived a promotion would (fixed in 0.72).
            n = txt.count("Move speed override on the playing character")
            rep.check(n == 0, f"bot log {os.path.basename(log)}: {n} move-speed overrides on the playing character")
            # The kit's client read path (CE-13): a state whose length does not fit the message, and a state its
            # reader failed on, each mean the client mis-read an entity's movement.
            for text, what in (("invalid state length", "invalid state lengths"),
                               ("error occurs while reading", "movement state read errors")):
                n = txt.count(text)
                rep.check(n == 0, f"bot log {os.path.basename(log)}: {n} {what}")
        except FileNotFoundError:
            rep.check(False, f"bot log {os.path.basename(log)} missing")


def wait_for_bot(log, kind, timeout):
    end = time.time() + timeout
    while time.time() < end:
        r = bot_result(log, kind)
        if r:
            return r
        time.sleep(3)
    return ""


DEFAULT = ["pet", "party", "crash", "harvest", "build", "seam", "seamcold", "sleep"]
TWO_HOST_SKIP = {
    "sleep": "needs cells started on demand by the central, which asks the spawner that serves the cell; the other "
             "machine runs no spawner, so nothing can start its cell there",
    "wake": "needs cells started on demand by the central (as sleep does); the other machine runs no spawner",
    **{n: regress_split.TWO_HOST_REASON for n in regress_split.SCENARIOS},
    **{n: regress_merge.TWO_HOST_REASON for n in regress_merge.SCENARIOS},
    **{n: regress_splitcrash.TWO_HOST_REASON for n in regress_splitcrash.SCENARIOS},
    **{n: regress_auto.TWO_HOST_REASON for n in regress_auto.SCENARIOS},
    **{n: regress_building.TWO_HOST_REASON for n in regress_building.SCENARIOS},
    **{n: regress_central.TWO_HOST_REASON for n in regress_central.SCENARIOS},
}
ALL = (DEFAULT + ["pvp", "wake", "dynidle"] + list(regress_split.SCENARIOS) + list(regress_merge.SCENARIOS)
       + list(regress_splitcrash.SCENARIOS) + list(regress_auto.SCENARIOS) + list(regress_building.SCENARIOS)
       + list(regress_central.SCENARIOS))
# A persisted remote cell outlives the Plays, so no scenario's environment reaches it.
PERSIST_SKIP = {"dynidle": "runs the test map Dynamic through the environment, which a persisted remote cell never gets",
                **{n: regress_split.PERSIST_REASON for n in regress_split.SCENARIOS},
                **{n: regress_merge.PERSIST_REASON for n in regress_merge.SCENARIOS},
                **{n: regress_splitcrash.PERSIST_REASON for n in regress_splitcrash.SCENARIOS},
                **{n: regress_auto.PERSIST_REASON for n in regress_auto.SCENARIOS},
                **{n: regress_building.PERSIST_REASON for n in regress_building.SCENARIOS},
                **{n: regress_central.PERSIST_REASON for n in regress_central.SCENARIOS}}


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("scenarios", nargs="*", help=f"any of {', '.join(ALL)} (default: {' '.join(DEFAULT)})")
    ap.add_argument("--list", action="store_true")
    ap.add_argument("--env", action="append", default=[], metavar="KEY=VALUE", help=f"environment for the zones, any of {', '.join(ENV_KEYS)}")
    ap.add_argument("--remote-cells", default="", metavar="CELL[,CELL]",
                    help="cells served by the other machine (see Docs/ServerMeshing/MULTIHOST.md). This machine then "
                         "serves the rest, the remote cells are started and stopped over SSH, and their logs are "
                         "copied here for grading.")
    ap.add_argument("--remote-port", type=int, default=8001, help="map port for the first remote cell")
    ap.add_argument("--impair", default="", metavar="PROFILE",
                    help="degrade the other machine's UDP link to this one for the whole run (remoteb.py impair "
                         "profiles: lag5, loss05, loss2, loss5, jitter, reorder, nasty). Restored at the end, and "
                         "by clumsy exiting if this is interrupted.")
    ap.add_argument("--remote-persist", action="store_true",
                    help="leave the remote cell running between scenarios instead of restarting it, so it has to "
                         "recover from this machine's Play stopping and starting on its own (cluster reconnect and "
                         "MeshMapNetworkManager.databaseReconnectSeconds)")
    ap.add_argument("--refresh", action="store_true",
                    help="make the editor import and compile changed scripts first (it only does so on focus)")
    ap.add_argument("--package", action="store_true",
                    help="build both .unitypackages (Build/Packages) in the open editor, after any tests and player build")
    ap.add_argument("--build", action="store_true",
                    help="rebuild the player (Build/Server) in the open editor before running")
    ap.add_argument("--unit", action="store_true",
                    help="run the add-on's EditMode tests in the open editor first; a failure stops the run")
    ap.add_argument("--allow-stale-build", action="store_true",
                    help="run even though the player build is older than the add-on's runtime sources")
    ap.add_argument("--topology", choices=["editor", "split"], default=None,
                    help="force the rig topology for every scenario (Slice 5): 'editor' hosts every server in the editor, "
                         "'split' runs the central (+ login + database manager) and the spawner as build processes with the "
                         "editor as a client only; by default the splitcentral scenarios use 'split', every other 'editor'")
    args = ap.parse_args()
    if args.topology and args.remote_cells:
        print("--topology cannot be combined with --remote-cells: the two-machine run assumes the editor's cluster")
        return 2
    globals()["TOPOLOGY_OVERRIDE"] = args.topology
    for kv in args.env:
        k, _, v = kv.partition("=")
        if k not in ENV_KEYS:
            print(f"unknown env key {k}; known: {', '.join(ENV_KEYS)}")
            return 2
        EXTRA_ENV[k] = v
    if args.list:
        print("\n".join(ALL))
        return 0
    names = args.scenarios or DEFAULT
    for n in names:
        if n not in ALL:
            print(f"unknown scenario {n}; known: {', '.join(ALL)}")
            return 2
    if read_status().get("isPlaying") == "True":
        print("the editor is already in Play mode; stop it first")
        return 2
    try:
        if args.refresh or args.build or args.unit or args.package:
            st = refresh_editor()
            if st.get("compileFailed") == "True":
                print("the editor reports script compile errors; fix them first (see the editor console)")
                return 2
        if args.unit:
            state, selected, passed, failed, failures = run_unit_tests()
            print(f"=== EditMode tests: {state}, {selected} selected, {passed} passed, {failed} failed")
            for failure in failures:
                print(f"  [FAIL] {failure[:200]}")
            if state != "done" or failed > 0:
                return 1
            if passed == 0:
                print("  no test ran: the filter found none (moved to another assembly or namespace?)")
                return 1
        if args.build:
            result = build_player()
            print(f"=== player build: {result}")
            if not result.startswith("ok"):
                return 2
        if args.package:
            result = build_packages()
            print(f"=== packages: {result}")
            if not result.startswith("ok"):
                return 2
    except TimeoutError as ex:
        print(f"the editor did not answer: {ex}; is it open on this project and not showing a modal dialog?")
        return 3
    if not args.scenarios and (args.unit or args.build or args.refresh or args.package):
        return 0
    if not os.path.exists(EXE):
        print(f"no player build at {EXE}; run with --build")
        return 2
    if read_status().get("compileFailed") == "True":
        print("the editor reports script compile errors; the servers it hosts would run stale code")
        return 2
    stale = [] if args.allow_stale_build else rigtools.build_staleness(ROOT, BUILD_MANIFEST)
    if stale:
        print("the player build does not match the working tree, so the cells and bots would run other code: "
              + "; ".join(stale) + ". Run with --build (or --allow-stale-build to run anyway).")
        return 2
    problems = preflight()
    for problem in problems:
        print(f"cannot run: {problem}")
    if problems:
        return 2

    if args.remote_cells:
        REMOTE_CELLS[:] = [c.strip() for c in args.remote_cells.split(",") if c.strip()]
        known = {z for z, _ in ZONES}
        unknown = [c for c in REMOTE_CELLS if c not in known]
        if unknown:
            print(f"unknown cell(s) {', '.join(unknown)}; known: {', '.join(sorted(known))}")
            return 2
        if len(REMOTE_CELLS) > 1:
            print("only one remote cell is supported for now")
            return 2
        local = [z for z, _ in ZONES if z not in REMOTE_CELLS]
        EXTRA_ENV["MESH_SPAWN_CELLS"] = ",".join(local)
        skipped = [n for n in names if n in TWO_HOST_SKIP]
        for n in skipped:
            print(f"skipping {n}: {TWO_HOST_SKIP[n]}")
        names = [n for n in names if n not in TWO_HOST_SKIP]
        if not names:
            print("nothing left to run")
            return 2
        os.makedirs(REMOTE_LOGS, exist_ok=True)
        globals()["REMOTE_PORT"] = args.remote_port
        globals()["REMOTE_PERSIST"] = args.remote_persist
        if args.remote_persist:
            for n in [n for n in names if n in PERSIST_SKIP]:
                print(f"skipping {n}: {PERSIST_SKIP[n]}")
            names = [n for n in names if n not in PERSIST_SKIP]
            if not names:
                print("nothing left to run")
                return 2
        how = ("left running across scenarios, recovering by itself" if args.remote_persist
               else "started with each scenario while the tester is held")
        print(f"two-machine run: this machine serves {', '.join(local)}; the other serves {', '.join(REMOTE_CELLS)}, {how}")
        if os.path.exists(HOLD):
            os.remove(HOLD)     # left behind by an interrupted run; it would park every tester for ever
        remote("stop")
        if args.impair:
            out = remote("impair", "--profile", args.impair)
            print("   " + " ".join(out.split())[:170])
            if "impairment" not in out:
                print("the link could not be degraded; not running, because the result would be mislabelled")
                return 2

    kill_bots()
    reports = []
    registries = rigtools.assets_with_script(ASSETS, rigtools.REGISTRY_SCRIPT_GUID)
    try:
        registry_before = registry_state(registries)
    except (OSError, RuntimeError) as ex:
        print(f"cannot take the registry baseline: {ex}")
        return 2
    if registry_before[0]:
        print("note: world registry asset(s) already differ from git before the run; each scenario is checked against "
              "this state instead:\n  " + registry_before[0].replace("\n", "\n  "))
    try:
        for i, n in enumerate(names):
            # Checked again before every scenario: a server run as a service that restarts on exit can take the ports back
            # in the seconds between two Play sessions, and every scenario after that would fail without a cell logging.
            taken = preflight()
            if taken:
                print(f"=== stopping before {n}: another server took the rig's ports")
                for rest in names[i:]:
                    rep = Report(rest)
                    for problem in taken:
                        rep.check(False, f"not run: {problem}")
                    reports.append(rep)
                for problem in taken:
                    print(f"  {problem}")
                break
            rep = Report(n)
            t0 = time.time()
            print(f"=== {n}")
            leftover = kit_process_count()
            if leftover > 0:
                kill_all_kit_processes()
                rep.check(False, f"{leftover} rig process(es) were still running before the scenario (killed)")
            try:
                run_scenario(n, rep)
            except Exception as ex:  # a harness failure is a failed scenario, never a crash of the run
                rep.check(False, f"harness: {ex}")
            finally:
                survivors = stop_play()
                rep.check(survivors == 0, f"{survivors} rig process(es) "
                          + ("survived the teardown" if topology_of(n) == "split" else "outlived Play (killed)"))
                try:
                    rep.check(registry_state(registries) == registry_before, f"{len(registries)} registry asset(s) unchanged on disk")
                except (OSError, RuntimeError) as ex:
                    rep.check(False, f"registry assets: {ex}")
            rep.notes.append(f"   {time.time() - t0:0.0f} s")
            for ok, label in rep.checks:
                print(f"  [{'ok' if ok else 'FAIL'}] {label}")
            for note in rep.notes:
                print(note)
            reports.append(rep)
            time.sleep(5)  # the zone processes exit with Play; give the ports a moment
    except Exception as ex:  # outside a scenario's own try: the rig itself failed (PowerShell, the status file)
        print(f"=== the run stopped: {ex}")
        done = {rep.name for rep in reports}
        for rest in names:
            if rest not in done:
                rep = Report(rest)
                rep.check(False, f"not run: the run stopped ({ex})")
                reports.append(rep)
    if REMOTE_CELLS:
        fetch_remote(force=True)   # one last copy so the logs left behind match what was graded
        print("stopping the remote cell:", remote("stop").strip())
        if args.impair:
            print("restoring the link:", " ".join(remote("impair", "--profile", "off").split())[:80])
    print("\n=== summary" + (f" (link: {args.impair})" if args.impair else ""))
    for rep in reports:
        print(f"  {rep.name:8s} {'PASS' if rep.passed else 'FAIL'}  ({sum(1 for ok, _ in rep.checks if ok)}/{len(rep.checks)} checks)")
    return 0 if all(r.passed for r in reports) else 1


if __name__ == "__main__":
    sys.exit(main())
