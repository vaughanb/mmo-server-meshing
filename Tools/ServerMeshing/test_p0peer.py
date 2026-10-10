"""Unit tests for p0peer (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py").

Machine B is a stand-in (FakeB), the editor's regress calls and provision's database writes are patched, and every
subprocess refuses (setUpModule), so no test reaches B, the editor or the database.
"""
import contextlib
import datetime
import os
import subprocess
import tempfile
import unittest
from unittest import mock

import p0peer
import provision
import remotessh

UTC = datetime.timezone.utc
PLAY = datetime.datetime(2026, 10, 7, 10, 0, 0)
BOTS = {"first": 2, "n": 3, "mode": "pace", "placement": "pace-cut", "bot_net": 5, "rig_ships": False}
SINK = {"count": 50, "kind": "character", "sizes": True}
_GUARDS = []


def setUpModule():
    """No test here reaches a machine: ssh, scp and PowerShell refuse unless a test stands in for them."""
    for name in ("run", "Popen"):
        patcher = mock.patch.object(subprocess, name, side_effect=AssertionError(f"a test ran subprocess.{name}"))
        patcher.start()
        _GUARDS.append(patcher)


def tearDownModule():
    while _GUARDS:
        _GUARDS.pop().stop()


class Rep:
    def __init__(self):
        self.lines = []

    def note(self, text=""):
        self.lines.append(text)


class FakeB:
    """Machine B: records each call; left = (bots, sinks) after a stop; clumsy = (running, left after a stop);
    raising = {method: exception}."""

    def __init__(self, left=(0, 0), raising=None, state=None, matches=(True, "aa", "aa"), logs=(), clumsy=(0, 0)):
        self.calls, self.left, self.raising, self.clumsy = [], left, raising or {}, clumsy
        self.state_value = state if state is not None else {"cells": 0, "relics": 0, "relic_service": "Stopped",
                                                            "bots": 0, "host": "MESHB", "cpu": "B CPU",
                                                            "clock": datetime.datetime(2026, 10, 7, 9, 0, 7, tzinfo=UTC),
                                                            "free_mb": 40000}
        self.matches, self.logs = matches, list(logs)

    def _call(self, name, *args):
        self.calls.append((name,) + args)
        if name in self.raising:
            raise self.raising[name]

    def state(self):
        self._call("state")
        return self.state_value

    def deployed_matches(self):
        self._call("deployed_matches")
        return self.matches

    def stop_bots(self):
        self._call("stop_bots")
        return self.left[0]

    def stop_sink(self):
        self._call("stop_sink")
        return self.left[1]

    def start_sink(self, port, peers, base=None, sizes=False):
        self._call("start_sink", port, peers, base, sizes)

    def start_bots(self, first, n, mode, points, bot_net=None, rig_ships=None, stagger=1):
        self._call("start_bots", first, n, mode, points, bot_net, rig_ships, stagger)

    def fetch_bots(self):
        self._call("fetch_bots")
        return list(self.logs)

    def fetch(self):
        self._call("fetch")
        return []

    def clumsy_count(self):
        self._call("clumsy_count")
        return self.clumsy[0]

    def stop_impair(self):
        self._call("stop_impair")
        return self.clumsy[1]

    def start_impair(self, profile):
        self._call("start_impair", profile)


def spec(peer=None, env=None, **extra):
    return dict({"family": "crossings", "env": env or {}, "cells_read": ["C", "E"], "peer": peer}, **extra)


def names(b):
    return [call[0] for call in b.calls]


class QuietTests(unittest.TestCase):
    def test_quiet_b(self):
        cases = {  # failure first: (FakeB, exit code, reason) or (FakeB, None, None)
            "B does not answer": (FakeB(raising={"stop_bots": remotessh.Unreachable("b did not answer")}), 2,
                                  "B is unreachable: b did not answer"),
            "bots survive": (FakeB(left=(2, 0)), 3, "B: 2 bots still running after being stopped"),
            "a sink survives": (FakeB(left=(0, 1)), 3, "B: 1 sink still running after being stopped"),
            "both survive": (FakeB(left=(1, 2)), 3, "B: 1 bot and 2 sinks still running after being stopped"),
            "clumsy survives": (FakeB(clumsy=(1, 1)), 3, "B: 1 clumsy process(es) still running after being stopped"),
            "a bot and clumsy survive": (FakeB(left=(1, 0), clumsy=(2, 2)), 3,
                                         "B: 1 bot and 2 clumsy process(es) still running after being stopped"),
            "B does not answer the clumsy stop": (
                FakeB(clumsy=(1, 0), raising={"stop_impair": remotessh.Unreachable("gone")}), 2,
                "B is unreachable: gone"),
            "a leftover clumsy is stopped": (FakeB(clumsy=(1, 0)), None, None),
            "B is quiet": (FakeB(), None, None),
        }
        for name, (b, code, reason) in cases.items():
            with self.subTest(name):
                if code is None:
                    rep = Rep()
                    p0peer.quiet_b(rep, b)
                    found = b.clumsy[0]
                    self.assertEqual(names(b), ["stop_bots", "stop_sink", "clumsy_count"] + ["stop_impair"] * bool(found))
                    self.assertEqual(any("clumsy" in line for line in rep.lines), bool(found))
                    continue
                with self.assertRaises(p0peer.Stopped) as caught:
                    p0peer.quiet_b(Rep(), b)
                self.assertEqual((caught.exception.code, caught.exception.reason), (code, reason))


class PointTests(unittest.TestCase):
    def test_walk_points(self):
        points = [(-30.0, -40.0), (-30.0, 40.0)]
        cases = {
            "stand stays": ((points, "stand", None), points),
            "pace stays": ((points, "pace", 99), points),
            "walk mirrors across the cut and back": ((points, "walk", None), [(30.0, -40.0, -30.0), (30.0, 40.0, -30.0)]),
            "walk to a given x": ((points, "walk", 50.0), [(50.0, -40.0, -30.0), (50.0, 40.0, -30.0)]),
        }
        for name, (call, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0peer.walk_points(*call), expected)

    def test_rig_ships_and_the_sink_cell(self):
        cases = {
            "no ships": (spec({"bots": BOTS}), None),
            "ships from the run's wire": (spec({"bots": dict(BOTS, rig_ships=True)},
                                               {"MESH_SHIP_WIRE": "compact28", "MESH_SHIP_SCALE": "0.5"}),
                                          "compact28:12345:0.5"),
        }
        for name, (s, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0peer.rig_ships_value(s), expected)
        self.assertEqual(p0peer.sink_cell(spec(env={"MESH_PHANTOM": "sink:50:character:E"})), "E")
        self.assertEqual(p0peer.sink_cell(spec(env={"MESH_PHANTOM": "nonsense"})), "C")
        self.assertEqual(p0peer.sink_cell(spec()), "C")


class Clock:
    """A fake monotonic clock that sleep advances."""

    def __init__(self):
        self.t = 0.0

    def monotonic(self):
        return self.t

    def sleep(self, seconds):
        self.t += seconds


@contextlib.contextmanager
def fake_time(lines=None):
    clock = Clock()
    with mock.patch.object(p0peer.time, "monotonic", clock.monotonic), \
            mock.patch.object(p0peer.time, "sleep", clock.sleep), \
            mock.patch.object(p0peer.r, "zone_lines", lambda *a: list((lines or {}).get(a[0], []))):
        yield clock


PORT_LINE = "INFO Mesh [2026-10-07 10:00:05] Phantoms: cell C port 8000 mode sink kind character P 50 V 100 groups 1"


class SessionTests(unittest.TestCase):
    def test_before_play_refuses(self):
        cases = {
            "leftover bots": (FakeB(left=(3, 0)), None, 3, "B: 3 bots still running after being stopped"),
            "bots not registered": (FakeB(), provision.ProvisionError("0 account(s) meshbot4"), 2,
                                    "bot positions: 0 account(s) meshbot4"),
        }
        for name, (b, error, code, reason) in cases.items():
            with self.subTest(name), mock.patch.object(p0peer.provision, "reset_bot_positions",
                                                       mock.Mock(side_effect=error)) as reset:
                with self.assertRaises(p0peer.Stopped) as caught:
                    p0peer.PeerSession(spec({"bots": BOTS}), Rep(), b).before_play()
                self.assertEqual((caught.exception.code, caught.exception.reason), (code, reason))
                self.assertEqual(reset.called, error is not None)

    def test_before_play_resets_the_bots(self):
        b = FakeB()
        with mock.patch.object(p0peer.provision, "reset_bot_positions") as reset:
            p0peer.PeerSession(spec({"bots": BOTS}), Rep(), b).before_play()
            p0peer.PeerSession(spec({"sink": SINK}), Rep(), b).before_play()
        reset.assert_called_once_with(2, 3, [(-4.0, -40.0), (-4.0, 0.0), (-4.0, 40.0)])

    def test_on_play_starts_the_sink_then_the_bots(self):
        b, rep = FakeB(), Rep()
        session = p0peer.PeerSession(spec({"sink": SINK, "bots": dict(BOTS, stagger=2)},
                                          {"MESH_PHANTOM": "sink:50:character:C"}), rep, b)
        with fake_time({"C": ["a line", PORT_LINE.replace("mode sink", "mode virtual"), PORT_LINE]}):
            session.on_play(PLAY)
        self.assertEqual(b.calls, [("start_sink", 8000, 50, None, True),
                                   ("start_bots", 2, 3, "pace", [(-4.0, -40.0), (-4.0, 0.0), (-4.0, 40.0)], 5, None, 2)])
        self.assertTrue(session.started)
        self.assertIn("    B: sink of 50 peers on cell C port 8000", rep.lines)

    def test_on_play_walks_eye_centre_bots_to_the_logged_centre(self):
        b, rep = FakeB(), Rep()
        bots = dict(BOTS, n=1, mode="stand", placement="eye-centre")
        session = p0peer.PeerSession(spec({"bots": bots}, {"MESH_PHANTOM": "virtual:50:eye:C"}), rep, b)
        centre = "INFO Mesh [2026-10-07 10:00:05] Phantom group 0: centre 12.5 -3.0 side 55.9 phantoms 50 filler 200"
        with fake_time({"C": ["a line", centre]}):
            session.on_play(PLAY)
        self.assertEqual(b.calls, [("start_bots", 2, 1, "stand", [(12.5, -3.0)], 5, None, 1)])
        self.assertIn("    B: the eye group's centre is (12.5, -3)", rep.lines)
        with fake_time({"C": ["no centre here"]}), self.assertRaisesRegex(RuntimeError, "no 'Phantom group 0: centre'"):
            p0peer.PeerSession(spec({"bots": bots}, {"MESH_PHANTOM": "virtual:50:eye:C"}), Rep(), FakeB()).on_play(PLAY)

    def test_record_reads_the_fetched_logs(self):
        with tempfile.TemporaryDirectory() as logs, tempfile.TemporaryDirectory() as bot_logs:
            with open(os.path.join(logs, "sink.log"), "w", encoding="utf-8") as f:
                f.write("sink line\n")
            with open(os.path.join(bot_logs, "meshbot2.log"), "w", encoding="utf-8") as f:
                f.write("bot line\n")
            cases = {  # peer, then (sink lines, bot logs) handed to peer_record
                "sink and bots (meshbot3's log is missing)": ({"sink": SINK, "bots": dict(BOTS, n=2)},
                                                              (["sink line"], {"meshbot2": ["bot line"], "meshbot3": []})),
                "bots only: sink.log is not read": ({"bots": dict(BOTS, n=1)}, ([], {"meshbot2": ["bot line"]})),
                "sink only": ({"sink": SINK}, (["sink line"], {})),
            }
            for name, (peer, (sink, bots)) in cases.items():
                with self.subTest(name), \
                        mock.patch.object(p0peer, "peer_context", lambda b=None: {"clock_offset_s": -2.5}), \
                        mock.patch.object(p0peer.p0cells0b, "peer_record", lambda *a: a) as _:
                    got = p0peer.PeerSession(spec(peer), Rep(), FakeB()).record("s", "e", logs, bot_logs)
                    self.assertEqual(got, (sink, bots, -2.5, "s", "e"))

    def test_on_play_without_a_port_line_raises(self):
        session = p0peer.PeerSession(spec({"sink": SINK}), Rep(), FakeB())
        with fake_time({"C": [PORT_LINE.replace("mode sink", "mode virtual")]}) as clock:
            with self.assertRaisesRegex(RuntimeError, "cell C logged no 'Phantoms: cell C port N mode sink' line"):
                session.on_play(PLAY)
        self.assertGreaterEqual(clock.t, p0peer.PORT_WAIT_S)

    def test_after_play(self):
        cases = {  # failure first: (peer, FakeB), problems, stop_reason, calls
            "bots survive": (({"bots": BOTS}, FakeB(left=(3, 0))), ["B: 3 bots survived"], "B: 3 bots survived",
                             ["stop_bots", "stop_sink", "fetch_bots", "fetch"]),
            "a stop that fails": (({"sink": SINK}, FakeB(raising={"stop_sink": RuntimeError("timed out")})),
                                  ["B: sink-stop failed: timed out"], "B: sink-stop failed: timed out",
                                  ["stop_bots", "stop_sink", "fetch"]),
            "a failed bot fetch": (({"bots": BOTS}, FakeB(raising={"fetch_bots": RuntimeError("no match")})),
                                   ["B: bots-fetch failed: no match"], None, ["stop_bots", "stop_sink", "fetch_bots", "fetch"]),
            "a failed fetch with a sink": (({"sink": SINK}, FakeB(raising={"fetch": RuntimeError("scp")})),
                                           ["B: fetch failed: scp"], None, ["stop_bots", "stop_sink", "fetch"]),
            "a failed fetch without a sink": (({"bots": BOTS}, FakeB(raising={"fetch": RuntimeError("scp")})), [],
                                              None, ["stop_bots", "stop_sink", "fetch_bots", "fetch"]),
            "clean": (({"sink": SINK, "bots": BOTS}, FakeB()), [], None, ["stop_bots", "stop_sink", "fetch_bots", "fetch"]),
        }
        for name, ((peer, b), problems, reason, calls) in cases.items():
            with self.subTest(name):
                session = p0peer.PeerSession(spec(peer), Rep(), b)
                session.started = True
                session.after_play()
                self.assertEqual((session.problems, session.stop_reason, names(b)), (problems, reason, calls))

    def test_impair_refuses(self):
        cases = {"an unknown profile": "loss9", "off": "off"}
        for name, profile in cases.items():
            with self.subTest(name), self.assertRaisesRegex(ValueError, f"peer impair '{profile}' is not one of"):
                p0peer.PeerSession(spec({"sink": SINK, "impair": profile}), Rep(), FakeB())

    def test_on_play_starts_clumsy_after_the_sink(self):
        cases = {  # failure first: FakeB, the exception, its code (Stopped) or message
            "no admin on B": (FakeB(raising={"start_impair": p0peer.remoteb.NoAdmin("not elevated")}), p0peer.Stopped,
                              2),
            "clumsy fails": (FakeB(raising={"start_impair": RuntimeError("did not stay up")}), RuntimeError,
                             "did not stay up"),
            "clumsy runs": (FakeB(), None, None),
        }
        for name, (b, error, detail) in cases.items():
            with self.subTest(name):
                rep = Rep()
                session = p0peer.PeerSession(spec({"sink": SINK, "bots": BOTS, "impair": "loss1"},
                                                  {"MESH_PHANTOM": "sink:50:character:C"}), rep, b)
                with fake_time({"C": [PORT_LINE]}):
                    if error is None:
                        session.on_play(PLAY)
                    else:
                        with self.assertRaises(error) as caught:
                            session.on_play(PLAY)
                calls = names(b)
                self.assertEqual(calls[:2], ["start_sink", "start_impair"])
                self.assertTrue(session.impair_started)
                if error is p0peer.Stopped:
                    self.assertEqual(caught.exception.code, detail)
                    self.assertIn("c4 (packet loss) must be skipped", caught.exception.reason)
                elif error is not None:
                    self.assertIn(detail, str(caught.exception))
                if error is None:
                    self.assertEqual(calls, ["start_sink", "start_impair", "start_bots"])
                    self.assertIsNotNone(session.impair_on)
                    self.assertTrue(any(line.startswith("    B: impairment on (loss1) at ") for line in rep.lines))
                else:
                    self.assertIsNone(session.impair_on)

    def test_after_play_stops_clumsy_first(self):
        cases = {  # failure first: FakeB, problems, stop_reason
            "clumsy survives": (FakeB(clumsy=(1, 1)), ["B: 1 clumsy process(es) survived"],
                                "B: 1 clumsy process(es) survived"),
            "the stop fails, the bots stop anyway": (
                FakeB(left=(2, 0), raising={"stop_impair": RuntimeError("timed out")}),
                ["B: impair-stop failed: timed out", "B: 2 bots survived"],
                "B: impair-stop failed: timed out; B: 2 bots survived"),
            "clean": (FakeB(), [], None),
        }
        for name, (b, problems, reason) in cases.items():
            with self.subTest(name):
                session = p0peer.PeerSession(spec({"sink": SINK, "impair": "loss5"}), Rep(), b)
                session.started = session.impair_started = True
                session.after_play()
                self.assertEqual((session.problems, session.stop_reason), (problems, reason))
                self.assertEqual(names(b)[:3], ["stop_impair", "stop_bots", "stop_sink"])
        b = FakeB()
        session = p0peer.PeerSession(spec({"sink": SINK, "impair": "loss5"}), Rep(), b)
        session.started = True     # Play up, but on_play failed before clumsy: nothing to stop on that side
        session.after_play()
        self.assertNotIn("stop_impair", names(b))

    def test_after_play_before_any_start_does_nothing(self):
        b = FakeB(left=(3, 0))
        session = p0peer.PeerSession(spec({"bots": BOTS}), Rep(), b)
        session.after_play()
        self.assertEqual((b.calls, session.problems), ([], []))


class HookTests(unittest.TestCase):
    def test_window_hooks(self):
        calls = []
        probe = calls.append
        stop = object()
        peer, hooks = p0peer.window_hooks(spec(), Rep(), probe, stop)
        self.assertEqual((peer, hooks), (None, {"on_play": probe, "stop_when": stop}))
        self.assertEqual(p0peer.window_hooks(spec(), Rep()), (None, {}))
        with mock.patch.object(p0peer, "RemoteB", FakeB):
            peer, hooks = p0peer.window_hooks(spec({"bots": BOTS}, stop_after_outcomes=6), Rep(), probe, stop)
        self.assertEqual(sorted(hooks), ["after_play", "before_play", "on_play", "stop_when"])
        self.assertEqual((hooks["before_play"], hooks["after_play"]), (peer.before_play, peer.after_play))
        self.assertIsNot(hooks["stop_when"], stop)
        with mock.patch.object(p0peer, "RemoteB", FakeB), \
                mock.patch.object(p0peer.PeerSession, "on_play", lambda self, t: calls.append("peer")):
            _, hooks = p0peer.window_hooks(spec({"bots": BOTS}), Rep(), probe)
        calls.clear()
        hooks["on_play"](PLAY)
        self.assertEqual(calls, ["peer", PLAY])

    def test_mark_rows(self):
        peer = p0peer.PeerSession(spec({"bots": BOTS}), Rep(), FakeB())
        rows = [{"accepted": True, "problems": []}, {"accepted": False, "problems": ["C logged 1 exception line(s)"]}]
        self.assertEqual(p0peer.mark_rows([dict(row) for row in rows], peer), rows)
        self.assertEqual(p0peer.mark_rows(rows, None), rows)
        peer.problems, peer.stop_reason = ["B: 2 bots survived"], "B: 2 bots survived"
        marked = p0peer.mark_rows([dict(row) for row in rows], peer)
        self.assertEqual([(row["accepted"], row["problems"], row[p0peer.PEER_STOP]) for row in marked],
                         [(False, ["B: 2 bots survived"], "B: 2 bots survived"),
                          (False, ["C logged 1 exception line(s)", "B: 2 bots survived"], "B: 2 bots survived")])

    def test_outcomes_stop(self):
        outcomes = {"C": ["ack"] * 3, "E": ["ack"] * 2}
        cases = {  # (wanted outcomes, counter), answer
            "a counter that fails": ((5, lambda lines: 1 / 0), False),
            "fewer than wanted": ((6, len), False),
            "exactly as many": ((5, len), True),
        }
        for name, ((n, count), expected) in cases.items():
            with self.subTest(name), fake_time(outcomes):
                self.assertEqual(p0peer.outcomes_stop(spec(), n, count)(PLAY, PLAY), expected)
        parsed = [{"kind": "ack", "ms": 10, "at": None}] * 2
        with fake_time(outcomes), mock.patch.object(p0peer.p0cells0b, "handoff_outcomes", lambda lines: parsed,
                                                    create=True):
            self.assertTrue(p0peer.outcomes_stop(spec(), 4)(PLAY, PLAY))
            self.assertFalse(p0peer.outcomes_stop(spec(), 5)(PLAY, PLAY))


class ContextTests(unittest.TestCase):
    def setUp(self):
        p0peer._CACHE.clear()
        self.addCleanup(p0peer._CACHE.clear)

    def test_context_of(self):
        before = datetime.datetime(2026, 10, 7, 9, 0, 9, tzinfo=UTC)
        after = before + datetime.timedelta(seconds=2)
        state = FakeB().state_value
        self.assertEqual(p0peer.context_of(state, before, after),
                         {"peer_host": "MESHB", "peer_cpu": "B CPU", "clock_offset_s": -3.0})
        with self.assertRaises(p0peer.Stopped) as caught:
            p0peer.context_of(dict(state, clock=None), before, after)
        self.assertEqual(caught.exception.code, 2)

    def test_peer_context_asks_b_once(self):
        b = FakeB()
        first = p0peer.peer_context(b)
        self.assertEqual(p0peer.peer_context(b), first)
        self.assertEqual(names(b), ["state"])
        p0peer._CACHE.clear()
        with self.assertRaises(p0peer.Stopped) as caught:
            p0peer.peer_context(FakeB(raising={"state": remotessh.Unreachable("no route")}))
        self.assertEqual((caught.exception.code, caught.exception.reason), (2, "B is unreachable: no route"))

    def test_start_problems(self):
        cases = {
            "B does not answer": (FakeB(raising={"state": remotessh.Unreachable("no route")}),
                                  ["B is unreachable: no route"]),
            "no build here": (FakeB(raising={"deployed_matches": FileNotFoundError("Assembly-CSharp.dll")}),
                              ["no build here to compare with B's: Assembly-CSharp.dll"]),
            "another build and a cell": (FakeB(state=dict(FakeB().state_value, cells=1), matches=(False, "ab" * 8, "cd" * 8)),
                                         ["B runs 1 cell(s); stop them: remoteb.py stop",
                                          "B's build is not this one (Assembly-CSharp.dll sha256 abababababababab here, "
                                          "cdcdcdcdcdcdcdcd on B): run remoteb.py deploy"]),
            "ready": (FakeB(), []),
        }
        for name, (b, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0peer.start_problems(b), expected)


if __name__ == "__main__":
    unittest.main()
