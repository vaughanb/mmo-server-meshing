"""Phase 0 step 8b's second machine (step8b_design.md decisions 5, 7, 8, 9): the peer session that starts and stops B's
bots and phantom sink around one rig run, B's context for the rows, the bots' one-off registration and calibration,
and the crossings block's stop_when. Drives B through remoteb and remotebots (one SSH command at a time) and A's
editor through regress; it never imports rigrun (rigrun.host_context imports this module). Step 9 (step9_design.md
decisions 4 and 12) adds clumsy on B (peer["impair"]; quiet_b stops a leftover one) and composes p0burst's A-only
burst session (spec["burst_rows"]) into the same hooks. B's driver (RemoteB, quiet_b, Stopped) is p0remote, and the
bots' registration and calibration are p0calibration; both are re-exported here.

    peer = p0peer.PeerSession(spec, rep)       # spec["peer"] = {"sink": {...} | None, "bots": {...} | None, "impair"}
    peer.before_play() / on_play(t) / after_play()   # rigrun.run_window's hooks; peer.problems, peer.stop_reason
    peer, burst, hooks = p0peer.window_session(spec, rep, on_play, stop_when)   # run_window's arguments, composed
    peer, hooks = p0peer.window_hooks(spec, rep, on_play, stop_when)            # the same without the burst session
    p0peer.mark_rows(rows, peer, burst)        # problems into the rows (not accepted), PEER_STOP when B kept some
    p0peer.peer_context()                      # {"peer_host", "peer_cpu", "clock_offset_s"}: one SSH query, cached
    p0peer.start_problems()                    # why no step 8b run may start with B as it is ([] when one may)
    p0peer.quiet_b(rep)                        # B's bots, sink, clumsy stopped (before a canary); Stopped if any live
    p0peer.register_bots(n, rep)               # meshbot2..meshbot<n+1> log in once (and register themselves)
    p0peer.calibrate_bots(rep, n=100)          # Logs/p0/bots_calibration.json: hosted, free memory, upload pkt/s
    p0peer.read_calibration(build, host=None)  # that record when made on this build (and B host), else None
    p0peer.outcomes_stop(spec, n)              # stop_when: True once A's read cells logged n handoff outcomes

A spec's bots: {"first": K >= 2, "n": N, "mode": stand|pace|walk|..., "placement": botpoints.PLACEMENTS, "bot_net":
seconds or None, "rig_ships": bool, and optionally "stagger" (default 1 s) and "walk_x" (walk mode's target; default
the start x mirrored across the cut, x = 0)}; its sink: {"count": P, "kind": k, "sizes": bool, optionally "base"};
its impair: a remoteb.PROFILES name other than "off" (clumsy started after the sink, stopped first in after_play).
The bots start at botpoints.bot_points(N, placement) (provision.reset_bot_positions before Play) and walk to the same
points (stand, pace), or to (walk_x, z) and back to x (walk). clock_offset_s is B's clock minus A's: a B-stamped line
happened at its stamp minus the offset on A's clock.
Unit tests: python -m unittest discover -s Tools/ServerMeshing -p "test_*.py"
"""
import datetime
import os
import re
import time

import botpoints
import p0burst
import p0cells0b
import p0phantom
import p0ships
import p0stage0b
import provision
import regress as r
import remoteb
import remotebots
import remotessh
# B as the session drives it, and the bots' registration and calibration (step 8b decisions 5 and 7): split out (the
# ~500-line rule), every name importable here.
from p0remote import POLL_S, RemoteB, Stopped, plural, quiet_b  # noqa: F401
from p0calibration import (CALIBRATION, bot_upload_rate, build_id, calibrate_bots, calibration_record,  # noqa: F401
                           hosts, read_calibration, register_bots, wait_in_map)

PEER_STOP = "peer_stop"     # a row key run_spec sets and Session.run pops: B kept processes, so the invocation stops
PORT_WAIT_S = 120           # on_play: how long the sink waits for its cell's "Phantoms: cell C port N" line
B_BUSY_SHARE = 0.20        # after_play: B's other programs above this share of its CPU make the run's B figures suspect


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
    """B's part of one run (decision 8). before_play: B's bots, sink and clumsy stopped and none left (quiet_b), then
    the bots' saved positions reset. on_play: the sink started on its cell's port, then clumsy when the peer names an
    impair profile (step 9 decision 12: impair_on is A's local time once it runs, noted "impairment on"), then the
    bots, staggered; a failure raises (run_window stops Play), and B's SSH session without administrator rights raises
    Stopped(2): c4 is skipped. run_window sleeps the warm-up after on_play returns, so the measurement always opens at
    least the warm-up after "impairment on". after_play: clumsy stopped first, then (its own try/finally) bots and sink
    stopped and their logs fetched (Logs/remote/bots/, Logs/remote/); it never raises: what failed is in problems (the
    rows are not accepted), and stop_reason is set when a process may have survived (the invocation stops after the
    row is written)."""

    def __init__(self, spec, rep, b=None):
        peer = spec.get("peer") or {}
        self.spec, self.rep, self.b = spec, rep, b or RemoteB()
        self.sink, self.bots, self.impair = peer.get("sink"), peer.get("bots"), peer.get("impair")
        if self.impair is not None and (self.impair not in remoteb.PROFILES or self.impair == "off"):
            raise ValueError(f"peer impair {self.impair!r} is not one of "
                             + ", ".join(p for p in remoteb.PROFILES if p != "off"))
        self.problems, self.stop_reason, self.started = [], None, False
        self.impair_started, self.impair_on = False, None
        self.load, self._cpu_start = None, None   # B's CPU shares over the run (load_shares), its first reading
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
        self._cpu_start = (time.monotonic(), self._cpu_sample())
        if self.sink:
            cell = sink_cell(self.spec)
            port = wait_port(play_start, cell)
            self.b.start_sink(port, self.sink["count"], self.sink.get("base"), bool(self.sink.get("sizes")))
            self.rep.note(f"    B: sink of {self.sink['count']} peers on cell {cell} port {port}")
        if self.impair:
            self._start_impair()
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

    def _cpu_sample(self):
        try:
            return self.b.cpu_sample()
        except Exception:  # a missed reading only leaves the run's B load unknown
            return None

    def _judge_load(self):
        """load: B's CPU shares (ours, other) between Play's start and now (remoteb.load_shares); a run where B's
        other programs (the user works on B) used more than B_BUSY_SHARE of its CPU gets a problem, so its bot and
        sink figures are not taken for the rig's alone. Unknown load is noted, never a problem."""
        start, before = self._cpu_start or (None, None)
        after = self._cpu_sample()
        self.load = remoteb.load_shares(before, after, time.monotonic() - start) if start is not None else None
        if self.load is None:
            self.rep.note("    B: CPU load not read")
            return
        self.rep.note(f"    B: CPU ours {self.load['ours']:.0%} other {self.load['other']:.0%}")
        if self.load["other"] > B_BUSY_SHARE:
            self.problems.append(f"B was busy with other work: {self.load['other']:.0%} of its CPU outside the rig "
                                 f"(over {B_BUSY_SHARE:.0%})")

    def _start_impair(self):
        self.impair_started = True   # after_play stops whatever this leaves behind
        try:
            self.b.start_impair(self.impair)
        except remoteb.NoAdmin as ex:
            raise Stopped(2, f"B: clumsy cannot start ({ex}); c4 (packet loss) must be skipped and the report says "
                             "why (step 9 decision 12)") from ex
        self.impair_on = datetime.datetime.now()
        self.rep.note(f"    B: impairment on ({self.impair}) at {self.impair_on:%H:%M:%S}")

    def _stopped(self, reason):
        """A process B may have kept: a problem, and the invocation's stop reason (joined to an earlier one)."""
        self.problems.append(reason)
        self.stop_reason = reason if not self.stop_reason else f"{self.stop_reason}; {reason}"

    def after_play(self):
        if not self.started:
            return
        self._judge_load()
        try:
            if self.impair_started:
                try:
                    left = self.b.stop_impair()
                except Exception as ex:  # B's link state is unknown: the invocation stops after this row
                    self._stopped(f"B: impair-stop failed: {ex}")
                else:
                    if left:
                        self._stopped(f"B: {left} clumsy process(es) survived")
                    else:
                        self.rep.note("    B: impairment off")
        finally:
            self._stop_peers()

    def _stop_peers(self):
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
            self._stopped("B: " + "; ".join(failed + ([" and ".join(alive) + " survived"] if alive else [])))
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


def _chain(calls):
    """One hook calling each of calls in turn; the call itself when there is one, None when there is none."""
    calls = [call for call in calls if call is not None]
    if len(calls) <= 1:
        return calls[0] if calls else None

    def chained(*args):
        for call in calls:
            call(*args)
    return chained


def _each(calls):
    """Like _chain, each call in its own try/finally (one that raises never skips the next)."""
    calls = [call for call in calls if call is not None]
    if len(calls) <= 1:
        return calls[0] if calls else None

    def each(rest=tuple(calls)):
        try:
            rest[0]()
        finally:
            if len(rest) > 1:
                each(rest[1:])
    return each


def _any(stops):
    """One stop_when, True once any of stops is (each asked every time); the stop itself when there is one."""
    stops = [stop for stop in stops if stop is not None]
    if len(stops) <= 1:
        return stops[0] if stops else None
    return lambda play_start, start: any([stop(play_start, start) for stop in stops])


def window_session(spec, rep, on_play=None, stop_when=None):
    """(peer, burst, the keyword arguments for rigrun.run_window). peer: a PeerSession when spec has a peer, else
    None; burst: a p0burst.BurstSession when spec sets burst_rows (step 9 decision 4; A only), else None, whose
    after_stop the caller calls once run_window has returned (Play stopped). on_play runs the peer's, then the given
    one; before_play the peer's, then the burst's (the burst rows reset out of Play; what provision refuses raises
    Stopped(2)); after_play the burst's (its first updateAt read), then the peer's, each in its own try/finally;
    stop_when is outcomes_stop when spec sets stop_after_outcomes, else the given one, alongside the burst's drain stop
    (True once either is). Hooks that are None are left out."""
    peer = PeerSession(spec, rep) if spec.get("peer") else None
    burst = p0burst.BurstSession(spec, rep) if spec.get("burst_rows") else None
    burst_before = None
    if burst is not None:
        def burst_before():
            try:
                burst.before_play()
            except provision.ProvisionError as ex:
                raise Stopped(2, f"burst rows: {ex}") from ex
    stop = outcomes_stop(spec, spec["stop_after_outcomes"]) if spec.get("stop_after_outcomes") else stop_when
    hooks = {"on_play": _chain([peer.on_play if peer else None, on_play]),
             "before_play": _chain([peer.before_play if peer else None, burst_before]),
             "after_play": _each([burst.after_play if burst else None, peer.after_play if peer else None]),
             "stop_when": _any([stop, burst.stop_when if burst else None])}
    return peer, burst, {name: hook for name, hook in hooks.items() if hook is not None}


def window_hooks(spec, rep, on_play=None, stop_when=None):
    """(peer, the keyword arguments for rigrun.run_window): window_session without its burst session (the burst's
    hooks are in; its after_stop and problems reach a caller only through window_session)."""
    peer, _, hooks = window_session(spec, rep, on_play, stop_when)
    return peer, hooks


def mark_rows(rows, peer, *others):
    """The rows with the problems of the peer and of any other session (a p0burst.BurstSession) added (each such row
    not accepted), and PEER_STOP set when one has a stop_reason; rows unchanged when every session is None."""
    for row in rows if peer is not None and peer.load is not None else []:
        row["peer_load"] = {key: round(value, 4) for key, value in peer.load.items()}
    for session in (peer,) + others:
        for row in rows if session is not None else []:
            if session.problems:
                row["problems"] = list(row.get("problems") or []) + list(session.problems)
                row["accepted"] = False
            if session.stop_reason:
                row[PEER_STOP] = session.stop_reason
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
