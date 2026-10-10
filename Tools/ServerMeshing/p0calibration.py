"""Phase 0 step 8b's bots before any block (step8b_design.md decisions 5 and 7): their one-off registration, the
calibration smoke and the record bots_calibration.json holds. Split out of p0peer (the ~500-line rule), which
re-exports the names here; the procedures drive B through p0remote (RemoteB, quiet_b, Stopped).

    p0calibration.register_bots(n, rep)                # meshbot2..meshbot<n+1> log in once (and register themselves)
    p0calibration.calibrate_bots(rep, n=100)           # Logs/p0/bots_calibration.json: hosted, free MB, upload pkt/s
    p0calibration.read_calibration(build, host=None)   # the record when made on this build (and B host), else None
    p0calibration.hosts(record, n)                     # whether it says B hosts n bots (b10 plans P 100 only then)
Unit tests: python -m unittest discover -s Tools/ServerMeshing -p "test_*.py" (test_p0peer)
"""
import datetime
import hashlib
import json
import os
import statistics
import time

import botpoints
import p0phantom
import p0remote
import provision
import regress as r
import remotebots

CALIBRATION = os.path.join(r.LOGS, "p0", "bots_calibration.json")
CALIBRATION_WAIT_S, CALIBRATION_MEASURE_S = 240, 60     # decision 7: all in map within 4 min, then a minute measured
SKIP_WINDOWS = 2   # each bot's first Bot net windows (login, walk-in) are not its rate
GROUP, GROUP_WAIT_S, REGISTER_X = 5, 90, -50.0          # decision 5: groups of 5, 90 s each, the west side
BOT_NET_S = 5                                           # Bot net window seconds


def build_id():
    """The sha1 hex of the player build's manifest (rigrun.build_id's value, which this module cannot import)."""
    if not os.path.exists(r.BUILD_MANIFEST):
        return ""
    with open(r.BUILD_MANIFEST, "rb") as f:
        return hashlib.sha1(f.read()).hexdigest()


def bot_upload_rate(lines_by_bot, skip=SKIP_WINDOWS):
    """(median over bots of each bot's median Bot net window out pkt/s, how many bots had a window), each bot's first
    `skip` windows left out; (None, 0) when none had one."""
    per_bot = []
    for lines in lines_by_bot.values():
        windows = [w for w in (p0phantom.parse_bot_net_window(line) for line in lines) if w][skip:]
        if windows:
            per_bot.append(statistics.median(w["out_packets_per_s"] for w in windows))
    return (round(statistics.median(per_bot), 2) if per_bot else None), len(per_bot)


def calibration_record(build, tried, arrived, in_map_s, state, lines_by_bot):
    """The bots_calibration.json record (decision 7): tried, in_map (bots that logged 'In map at' within
    CALIBRATION_WAIT_S) and in_map_s (seconds until all did, None if not all), hosted (bots running on B after the
    measurement), free_memory_MB, upload_pkt_per_s (bot_upload_rate) and upload_hz (it rounded: b2/b10's
    MESH_PHANTOM_UPLOAD_HZ), and fits: every bot tried was in the map in time and still running."""
    rate, measured = bot_upload_rate(lines_by_bot)
    hosted = state.get("bots")
    return {"build": build, "written_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds"),
            "peer_host": state.get("host") or None, "tried": tried, "in_map": arrived, "in_map_s": in_map_s,
            "wait_s": CALIBRATION_WAIT_S, "hosted": hosted, "free_memory_MB": state.get("free_mb"),
            "upload_pkt_per_s": rate, "upload_hz": None if rate is None else int(round(rate)),
            "bots_measured": measured, "fits": arrived == tried and hosted == tried}


def read_calibration(build, host=None, path=CALIBRATION):
    """The calibration record when the file exists and was written on this build with B named host (its peer_host;
    None: whichever B wrote it, as a dry run must, never asking B), else None."""
    try:
        with open(path, encoding="utf-8") as f:
            record = json.load(f)
    except FileNotFoundError:
        return None
    if not isinstance(record, dict):
        return None
    return record if record.get("build") == build and (host is None or record.get("peer_host") == host) else None


def hosts(record, n):
    """Whether a calibration record says B hosts n bots (b10 plans P 100 only then)."""
    return bool(record) and bool(record.get("fits")) and (record.get("tried") or 0) >= n


def wait_in_map(b, users, timeout):
    """The users whose fetched bot log says 'In map at', once all do or after timeout seconds (polled every
    p0remote.POLL_S)."""
    deadline = time.monotonic() + timeout
    while True:
        time.sleep(p0remote.POLL_S)
        try:
            paths = {os.path.splitext(os.path.basename(path))[0]: path for path in b.fetch_bots()}
        except RuntimeError:   # no log yet: scp matched nothing
            paths = {}
        arrived = [user for user in users if user in paths and remotebots.in_map(paths[user])]
        if len(arrived) == len(users) or time.monotonic() >= deadline:
            return arrived


def _play_up():
    """A Play with no client and no load, after A's leftover rig processes are killed (run_window's first steps)."""
    r.kill_bots()
    if r.kit_process_count() and r.kill_all_kit_processes():
        raise RuntimeError("leftover rig processes survived being killed")
    r.request_play("none", 0, 0, env={}, enabled=False)
    r.wait_play(120)


def _stop_all(b, rep):
    """B's bots stopped (a failure noted, never raised), then A's Play."""
    try:
        left = b.stop_bots()
        if left:
            rep.note(f"  ! B: {p0remote.plural(left, 'bot')} survived bots-stop")
    except Exception as ex:  # cleanup: Play must still stop
        rep.note(f"  ! B: bots-stop failed: {ex}")
    r.stop_play()


def register_bots(n, rep, b=None):
    """Decision 5 (one-off, outside measurement): Play up with no load, then meshbot2..meshbot<n+1> in groups of 5,
    standing on the west side (x = -50), each group stopped once every bot logged 'In map at' (or after 90 s); a bot
    whose login fails registers itself and creates <user>Char. Returns the users that reached the map; notes
    'registered k of N'. Idempotent. Stopped when B is unreachable or keeps bots."""
    if n < 1:
        raise ValueError(f"register {n} bots: at least 1")
    b = b or p0remote.RemoteB()
    p0remote.quiet_b(rep, b)
    registered = []
    _play_up()
    try:
        for first in range(2, n + 2, GROUP):
            k = min(GROUP, n + 2 - first)
            users = [remotebots.bot_user(i) for i in range(first, first + k)]
            b.start_bots(first, k, "stand", botpoints.column_points(k, REGISTER_X), stagger=1)
            arrived = wait_in_map(b, users, GROUP_WAIT_S)
            left = b.stop_bots()
            registered += arrived
            missing = [user for user in users if user not in arrived]
            rep.note(f"{users[0]}..{users[-1]}: {len(arrived)} of {k} in map"
                     + (f" (not: {', '.join(missing)})" if missing else ""))
            if left:
                raise p0remote.Stopped(3, f"B: {p0remote.plural(left, 'bot')} survived bots-stop")
    finally:
        _stop_all(b, rep)
    rep.note(f"registered {len(registered)} of {n}")
    return registered


def calibrate_bots(rep, n=100, b=None, path=CALIBRATION):
    """Decision 7's bot smoke: meshbot2..meshbot<n+1> pace at the spread points (Bot net windows every 5 s) with Play
    up and no load; waits until all are in the map (4 min at most), measures a minute, reads B's state and the bots'
    logs, stops them and Play, and writes the calibration_record to path. Returns the record."""
    b = b or p0remote.RemoteB()
    users = [remotebots.bot_user(k) for k in range(2, n + 2)]
    points = botpoints.bot_points(n, "spread")
    p0remote.quiet_b(rep, b)
    provision.reset_bot_positions(2, n, points)
    _play_up()
    try:
        started = time.monotonic()
        b.start_bots(2, n, "pace", points, bot_net=BOT_NET_S, stagger=1)
        arrived = wait_in_map(b, users, CALIBRATION_WAIT_S)
        in_map_s = round(time.monotonic() - started) if len(arrived) == n else None
        rep.note(f"{len(arrived)} of {n} bots in the map" + (f" after {in_map_s} s" if in_map_s is not None else ""))
        time.sleep(CALIBRATION_MEASURE_S)
        state = b.state()
        lines = {}
        for log in b.fetch_bots():
            with open(log, encoding="utf-8", errors="replace") as f:
                lines[os.path.splitext(os.path.basename(log))[0]] = f.read().splitlines()
    finally:
        _stop_all(b, rep)
    record = calibration_record(build_id(), n, len(arrived), in_map_s, state, lines)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(record, f, indent=2)
        f.write("\n")
    rep.note(f"calibration: {record['hosted']} bots hosted, {record['free_memory_MB']} MB free, "
             f"{record['upload_pkt_per_s']} pkt/s per bot up; fits {n}: {record['fits']}")
    return record
