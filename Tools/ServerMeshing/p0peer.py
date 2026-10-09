"""Phase 0 step 8b's second machine (step8b_design.md decisions 5, 7, 8, 9): the peer session that starts and stops B's
bots and phantom sink around one rig run, B's context for the rows, the bots' one-off registration and calibration,
and the crossings block's stop_when. Drives B through remoteb and remotebots (one SSH command at a time) and A's
editor through regress; it never imports rigrun (rigrun.host_context imports this module).

    peer = p0peer.PeerSession(spec, rep)       # spec["peer"] = {"sink": {...} | None, "bots": {...} | None}
    peer.before_play() / on_play(t) / after_play()   # rigrun.run_window's hooks; peer.problems, peer.stop_reason
    peer, hooks = p0peer.window_hooks(spec, rep, on_play, stop_when)   # run_window's keyword arguments, composed
    p0peer.mark_rows(rows, peer)               # peer problems into the rows (not accepted), PEER_STOP when B kept some
    p0peer.peer_context()                      # {"peer_host", "peer_cpu", "clock_offset_s"}: one SSH query, cached
    p0peer.start_problems()                    # why no step 8b run may start with B as it is ([] when one may)
    p0peer.quiet_b(rep)                        # B's bots and sink stopped (before a canary); Stopped when any survive
    p0peer.register_bots(n, rep)               # meshbot2..meshbot<n+1> log in once (and register themselves)
    p0peer.calibrate_bots(rep, n=100)          # Logs/p0/bots_calibration.json: hosted, free memory, upload pkt/s
    p0peer.read_calibration(build, host=None)  # that record when made on this build (and B host), else None
    p0peer.outcomes_stop(spec, n)              # stop_when: True once A's read cells logged n handoff outcomes

A spec's bots: {"first": K >= 2, "n": N, "mode": stand|pace|walk|..., "placement": botpoints.PLACEMENTS, "bot_net":
seconds or None, "rig_ships": bool, and optionally "stagger" (default 1 s) and "walk_x" (walk mode's target; default
the start x mirrored across the cut, x = 0)}; its sink: {"count": P, "kind": k, "sizes": bool, optionally "base"}.
The bots start at botpoints.bot_points(N, placement) (provision.reset_bot_positions before Play) and walk to the same
points (stand, pace), or to (walk_x, z) and back to x (walk). clock_offset_s is B's clock minus A's: a B-stamped line
happened at its stamp minus the offset on A's clock.
Unit tests: python -m unittest discover -s Tools/ServerMeshing -p "test_*.py"
"""
import datetime
import hashlib
import json
import os
import re
import statistics
import time

import botpoints
import p0cells0b
import p0phantom
import p0ships
import p0stage0b
import provision
import regress as r
import remoteb
import remotebots
import remotessh

CALIBRATION = os.path.join(r.LOGS, "p0", "bots_calibration.json")
PEER_STOP = "peer_stop"     # a row key run_spec sets and Session.run pops: B kept processes, so the invocation stops
PORT_WAIT_S = 120           # on_play: how long the sink waits for its cell's "Phantoms: cell C port N" line
POLL_S = 5                  # how often a wait looks again
GROUP, GROUP_WAIT_S, REGISTER_X = 5, 90, -50.0          # decision 5: groups of 5, 90 s each, the west side
CALIBRATION_WAIT_S, CALIBRATION_MEASURE_S = 240, 60     # decision 7: all in map within 4 min, then a minute measured
BOT_NET_S, SKIP_WINDOWS = 5, 2   # Bot net window seconds; each bot's first windows (login, walk-in) are not its rate


class Stopped(Exception):
    """Stopped ends an invocation early: its exit code (2: refused, B unreachable; 3: B kept processes, a canary) and
    the reason printed. p0blocks.Stopped is this class."""

    def __init__(self, code, reason):
        super().__init__(reason)
        self.code, self.reason = code, reason


class RemoteB:
    """Machine B as the peer session drives it: remoteb's and remotebots' functions over one set of connection
    settings (remotessh.defaults() unless given)."""

    def __init__(self, args=None):
        self.args = args or remotessh.defaults()

    def state(self):
        return remoteb.state(self.args)

    def deployed_matches(self):
        return remoteb.deployed_matches(self.args)

    def stop_bots(self):
        return remotebots.stop_bots(self.args)

    def stop_sink(self):
        return remoteb.stop_sink(self.args)

    def start_sink(self, port, peers, base=None, sizes=False):
        return remoteb.start_sink(self.args, port, peers, base, sizes)

    def start_bots(self, first, n, mode, points, bot_net=None, rig_ships=None, stagger=1):
        return remotebots.start_bots(self.args, first, n, mode, points, bot_net, rig_ships, stagger)

    def fetch_bots(self):
        return remotebots.fetch_bots(self.args)

    def fetch(self):
        return remoteb.fetch(self.args)


def plural(count, word):
    return f"{count} {word}{'' if count == 1 else 's'}"


def quiet_b(rep, b=None):
    """Stops B's bots and sink and checks none is left: Stopped(3) when one survives, Stopped(2) when B does not
    answer. Before every peer run and every canary of a block with peers (B idle while A's canary measures)."""
    b = b or RemoteB()
    try:
        bots, sinks = b.stop_bots(), b.stop_sink()
    except remotessh.Unreachable as ex:
        raise Stopped(2, f"B is unreachable: {ex}") from ex
    if bots or sinks:
        left = " and ".join(plural(c, w) for c, w in ((bots, "bot"), (sinks, "sink")) if c)
        raise Stopped(3, f"B: {left} still running after being stopped")


def walk_points(points, mode, walk_x=None):
    """Each bot's (walk x, walk z[, return x]) from its start point: the start point itself (stand and pace walk there
    first, then stay or pace), or for walk mode (walk_x, z, x): across and back (walk_x default -x)."""
    if mode != "walk":
        return [(x, z) for x, z, *_ in points]
    return [(-x if walk_x is None else walk_x, z, x) for x, z, *_ in points]


def rig_ships_value(spec):
    """The bots' -meshRigShips value when spec's bots set rig_ships (the run's wire, seed and scale), else None."""
    if not ((spec.get("peer") or {}).get("bots") or {}).get("rig_ships"):
        return None
    return p0ships.rig_ship_bot_args(dict(spec.get("env") or {}, MESH_RIG_SHIPS="1"))[1]


def sink_cell(spec):
    """The cell a sink connects to: MESH_PHANTOM's first cell, else the spec's first read cell."""
    setting = p0phantom.phantom_setting((spec.get("env") or {}).get("MESH_PHANTOM") or "")
    cells = setting[3] if setting[-1] is None else None
    return cells[0] if isinstance(cells, tuple) and cells else spec["cells_read"][0]


def wait_port(play_start, cell):
    """The port of cell's "Phantoms: cell C port N mode sink" line since play_start; RuntimeError after PORT_WAIT_S."""
    deadline, map_id = time.monotonic() + PORT_WAIT_S, dict(r.ZONES)[cell]
    while True:
        for line in r.zone_lines(cell, play_start.strftime("%H:%M:%S"), play_start.strftime("%Y-%m-%d"), map_id):
            found = p0stage0b.parse_phantoms_port(line)
            if found and found["cell"] == cell and found["mode"] == "sink":
                return found["port"]
        if time.monotonic() >= deadline:
            raise RuntimeError(f"cell {cell} logged no 'Phantoms: cell {cell} port N mode sink' line in {PORT_WAIT_S} s")
        time.sleep(POLL_S)


EYE_CENTRE = re.compile(r"Phantom group 0: centre (-?\d+(?:\.\d+)?) (-?\d+(?:\.\d+)?) ")


def eye_centre(play_start, cell):
    """(x, z) of cell's "Phantom group 0: centre X Z ..." line since play_start (the eye group a bot is placed in,
    accept-b part B); RuntimeError after PORT_WAIT_S."""
    deadline, map_id = time.monotonic() + PORT_WAIT_S, dict(r.ZONES)[cell]
    while True:
        for line in r.zone_lines(cell, play_start.strftime("%H:%M:%S"), play_start.strftime("%Y-%m-%d"), map_id):
            found = EYE_CENTRE.search(line)
            if found:
                return float(found.group(1)), float(found.group(2))
        if time.monotonic() >= deadline:
            raise RuntimeError(f"cell {cell} logged no 'Phantom group 0: centre' line in {PORT_WAIT_S} s")
        time.sleep(POLL_S)


def _lines(path):
    """A fetched log's lines, or [] when it is missing."""
    try:
        with open(path, encoding="utf-8", errors="replace") as f:
            return f.read().splitlines()
    except FileNotFoundError:
        return []


class PeerSession:
    """B's part of one run (decision 8). before_play: B's bots and sink stopped and none left (quiet_b), then the
    bots' saved positions reset. on_play: the sink started on its cell's port, then the bots, staggered; a failure
    raises (run_window stops Play). after_play: bots and sink stopped, their logs fetched (Logs/remote/bots/,
    Logs/remote/); it never raises: what failed is in problems (the rows are not accepted), and stop_reason is set
    when a process may have survived (the invocation stops after the row is written)."""

    def __init__(self, spec, rep, b=None):
        peer = spec.get("peer") or {}
        self.spec, self.rep, self.b = spec, rep, b or RemoteB()
        self.sink, self.bots = peer.get("sink"), peer.get("bots")
        self.problems, self.stop_reason, self.started = [], None, False
        self.points = botpoints.bot_points(self.bots["n"], self.bots["placement"]) if self.bots else []

    def before_play(self):
        quiet_b(self.rep, self.b)
        if self.bots:
            try:
                provision.reset_bot_positions(self.bots["first"], self.bots["n"], self.points)
            except provision.ProvisionError as ex:
                raise Stopped(2, f"bot positions: {ex}") from ex

    def on_play(self, play_start):
        self.started = True
        if self.sink:
            cell = sink_cell(self.spec)
            port = wait_port(play_start, cell)
            self.b.start_sink(port, self.sink["count"], self.sink.get("base"), bool(self.sink.get("sizes")))
            self.rep.note(f"    B: sink of {self.sink['count']} peers on cell {cell} port {port}")
        if self.bots:
            bots, points = self.bots, self.points
            if bots["placement"] == "eye-centre":
                cx, cz = eye_centre(play_start, sink_cell(self.spec))
                points = [(cx + x, cz + z) for x, z, *_ in points]
                self.rep.note(f"    B: the eye group's centre is ({cx:g}, {cz:g})")
            self.b.start_bots(bots["first"], bots["n"], bots["mode"],
                              walk_points(points, bots["mode"], bots.get("walk_x")), bots.get("bot_net"),
                              rig_ships_value(self.spec), bots.get("stagger", 1))
            self.rep.note(f"    B: meshbot{bots['first']}..meshbot{bots['first'] + bots['n'] - 1} {bots['mode']} at "
                          f"{bots['placement']}")

    def after_play(self):
        if not self.started:
            return
        left = {}
        for what, stop in (("bots", self.b.stop_bots), ("sink", self.b.stop_sink)):
            try:
                left[what] = stop()
            except Exception as ex:  # B's state is unknown: the invocation stops after this row
                left[what] = f"{what}-stop failed: {ex}"
        failed = [value for value in left.values() if isinstance(value, str)]
        alive = [plural(count, word) for count, word in ((left["bots"], "bot"), (left["sink"], "sink"))
                 if not isinstance(count, str) and count]
        if failed or alive:
            self.stop_reason = "B: " + "; ".join(failed + ([" and ".join(alive) + " survived"] if alive else []))
            self.problems.append(self.stop_reason)
        if self.bots:
            self._fetch("bots-fetch", self.b.fetch_bots, True)
        self._fetch("fetch", self.b.fetch, bool(self.sink))

    def users(self):
        """The bot users of this run, meshbot<first>.. (empty without bots)."""
        if not self.bots:
            return []
        return [remotebots.bot_user(k) for k in range(self.bots["first"], self.bots["first"] + self.bots["n"])]

    def record(self, start, end, logs=r.REMOTE_LOGS, bot_logs=remotebots.LOCAL_LOGS):
        """The run's peer record for its checks (p0cells0b.peer_record, on A's clock by peer_context's offset): B's
        sink.log as fetched into Logs/remote/ when the run had a sink, and this run's bots' logs from
        Logs/remote/bots/ (a missing log reads as no lines: the bots-on-cell and in-map checks then say so)."""
        sink = _lines(os.path.join(logs, "sink.log")) if self.sink else []
        bots = {user: _lines(os.path.join(bot_logs, f"{user}.log")) for user in self.users()}
        return p0cells0b.peer_record(sink, bots, peer_context(self.b)["clock_offset_s"], start, end)

    def _fetch(self, what, fetch, needed):
        try:
            fetch()
        except Exception as ex:  # a missing B log is the row's problem only when the run reads it (sink.log)
            if needed:
                self.problems.append(f"B: {what} failed: {ex}")
            else:
                self.rep.note(f"    B: {what} failed: {ex}")


def window_hooks(spec, rep, on_play=None, stop_when=None):
    """(peer, the keyword arguments for rigrun.run_window): a PeerSession when spec has a peer (else None), whose
    before_play and after_play are passed and whose on_play runs before the given on_play (one on_play calling both);
    stop_when is outcomes_stop when spec sets stop_after_outcomes, else the given one. Hooks that are None are left
    out."""
    peer = PeerSession(spec, rep) if spec.get("peer") else None
    calls = [call for call in (peer.on_play if peer else None, on_play) if call is not None]
    hooks = {}
    if len(calls) == 1:
        hooks["on_play"] = calls[0]
    elif calls:
        def both(play_start):
            for call in calls:
                call(play_start)
        hooks["on_play"] = both
    if peer:
        hooks.update(before_play=peer.before_play, after_play=peer.after_play)
    if spec.get("stop_after_outcomes"):
        hooks["stop_when"] = outcomes_stop(spec, spec["stop_after_outcomes"])
    elif stop_when is not None:
        hooks["stop_when"] = stop_when
    return peer, hooks


def mark_rows(rows, peer):
    """The rows with the peer's problems added (each such row not accepted) and PEER_STOP set when the peer has a
    stop_reason; rows unchanged without a peer."""
    for row in rows if peer is not None else []:
        if peer.problems:
            row["problems"] = list(row.get("problems") or []) + list(peer.problems)
            row["accepted"] = False
        if peer.stop_reason:
            row[PEER_STOP] = peer.stop_reason
    return rows


_CACHE = {}


def context_of(state, before, after):
    """{"peer_host", "peer_cpu", "clock_offset_s"} from B's state and A's UTC times around the query: the offset is
    B's clock minus the query's midpoint (half the round trip of error). Stopped(2) when B printed no clock."""
    if state.get("clock") is None:
        raise Stopped(2, "B printed no clock (Get-Date -Format o)")
    middle = before + (after - before) / 2
    return {"peer_host": state.get("host") or None, "peer_cpu": state.get("cpu") or None,
            "clock_offset_s": round((state["clock"] - middle).total_seconds(), 3)}


def peer_context(b=None):
    """B's host, CPU and clock offset (context_of) from one SSH query, made once per invocation; Stopped(2) when B
    does not answer."""
    if "peer" not in _CACHE:
        b = b or RemoteB()
        before = datetime.datetime.now(datetime.timezone.utc)
        try:
            state = b.state()
        except remotessh.Unreachable as ex:
            raise Stopped(2, f"B is unreachable: {ex}") from ex
        _CACHE["peer"] = context_of(state, before, datetime.datetime.now(datetime.timezone.utc))
    return dict(_CACHE["peer"])


def start_problems(b=None):
    """Why no step 8b run may start (decision 10), as lines; [] when one may: B unreachable, a cell, RelicTestServer
    or a Relics process running on B (remoteb.state_problems), or B's build not the one built here."""
    b = b or RemoteB()
    try:
        problems = remoteb.state_problems(b.state())
        matches, want, got = b.deployed_matches()
    except remotessh.Unreachable as ex:
        return [f"B is unreachable: {ex}"]
    except OSError as ex:
        return [f"no build here to compare with B's: {ex}"]
    if not matches:
        problems.append(f"B's build is not this one (Assembly-CSharp.dll sha256 {want[:16]} here, {got[:16]} on B): "
                        "run remoteb.py deploy")
    return problems


def outcomes_stop(spec, n, count=None):
    """A stop_when for run_window: True once A's read cells (spec cells_read) logged at least n handoff outcomes (ACK,
    ROLLBACK or failure) since Play began, counted by count(lines) (default: len(p0cells0b.handoff_outcomes(lines)));
    a read that fails answers False (the run goes on to its planned end)."""
    maps = dict(r.ZONES)

    def counted(lines):
        return count(lines) if count is not None else len(p0cells0b.handoff_outcomes(lines))

    def stop_when(play_start, start):
        try:
            hms, day = play_start.strftime("%H:%M:%S"), play_start.strftime("%Y-%m-%d")
            return sum(counted(r.zone_lines(cid, hms, day, maps[cid])) for cid in spec["cells_read"]) >= n
        except Exception:  # a mid-run look that fails only means the run is not cut short
            return False
    return stop_when


def wait_in_map(b, users, timeout):
    """The users whose fetched bot log says 'In map at', once all do or after timeout seconds (polled every POLL_S)."""
    deadline = time.monotonic() + timeout
    while True:
        time.sleep(POLL_S)
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
            rep.note(f"  ! B: {plural(left, 'bot')} survived bots-stop")
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
    b = b or RemoteB()
    quiet_b(rep, b)
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
                raise Stopped(3, f"B: {plural(left, 'bot')} survived bots-stop")
    finally:
        _stop_all(b, rep)
    rep.note(f"registered {len(registered)} of {n}")
    return registered


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


def calibrate_bots(rep, n=100, b=None, path=CALIBRATION):
    """Decision 7's bot smoke: meshbot2..meshbot<n+1> pace at the spread points (Bot net windows every 5 s) with Play
    up and no load; waits until all are in the map (4 min at most), measures a minute, reads B's state and the bots'
    logs, stops them and Play, and writes the calibration_record to path. Returns the record."""
    b = b or RemoteB()
    users = [remotebots.bot_user(k) for k in range(2, n + 2)]
    points = botpoints.bot_points(n, "spread")
    quiet_b(rep, b)
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
