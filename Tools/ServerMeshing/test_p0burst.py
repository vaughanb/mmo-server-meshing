"""Unit tests for p0burst, step 9's burst session on machine A, and its composition into p0peer's hooks (python -m
unittest discover -s Tools/ServerMeshing -p "test_*.py").

No test reads the real Editor.log, opens the database or reaches the editor or B: the editor log is a temporary file,
regress.zone_lines and provision_burst's database calls are patched, B is test_p0peer.FakeB, and every subprocess
refuses (test_p0peer's setUpModule, run here too).
"""
import datetime
import os
import tempfile
import unittest
from unittest import mock

import p0burst
import p0peer
import provision
from test_p0peer import BOTS, PLAY, FakeB, Rep, setUpModule, spec, tearDownModule  # noqa: F401 (the guards)

T0 = datetime.datetime(2026, 10, 9, 12, 0, 0)
DONE = "DB burst: done, 50 ready 0 failed"


def at(seconds):
    return T0 + datetime.timedelta(seconds=seconds)


def stamped(seconds, text):
    return f" INFO Mesh [{at(seconds):%Y-%m-%d %H:%M:%S}] - {text}"


def saves(seconds, pending):
    return stamped(seconds, f"DB saves window 5.00 s: pending {pending} enqueued 40 written 30 failed 0 | db fps 60.0 "
                            "| ceiling 16/s")


class LineTests(unittest.TestCase):
    def test_editor_lines(self):
        with tempfile.TemporaryDirectory() as folder:
            path = os.path.join(folder, "Editor.log")
            self.assertEqual((p0burst.editor_size(path), p0burst.editor_lines(7, path)), (0, ([], 7)))
            with open(path, "wb") as f:
                f.write(b"before Play\n")
            mark = p0burst.editor_size(path)
            with open(path, "ab") as f:
                f.write(b"line one\nline two\npartial")
            lines, offset = p0burst.editor_lines(mark, path)
            self.assertEqual(lines, ["line one", "line two"])
            with open(path, "ab") as f:
                f.write(b" line\n")
            self.assertEqual(p0burst.editor_lines(offset, path), (["partial line"], os.path.getsize(path)))
            with open(path, "wb") as f:      # the editor restarted: a new, shorter log
                f.write(b"new log\n")
            self.assertEqual(p0burst.editor_lines(offset, path), (["new log"], 8))

    def test_parsers(self):
        lines = ["no stamp: DB saves window 5.00 s: pending 3 enqueued 1", saves(5, 12), "noise", saves(10, 0),
                 "DB burst: done, 50 ready 0 failed (no stamp)", stamped(7, DONE), stamped(9, DONE)]
        self.assertEqual(p0burst.saves_windows(lines), [(at(5), 12), (at(10), 0)])
        self.assertEqual(p0burst.done_at(lines), at(7))
        self.assertIsNone(p0burst.done_at(lines[:5]))

    def test_drain_state(self):
        windows = [(at(5), 30), (at(10), 4), (at(15), 0), (at(20), 2)]
        cases = {  # failure first: (done, windows, now), (state, stop, pending, empty_at)
            "timed out": ((at(0), [(at(5), 30), (at(125), 3)], at(120)), ("timed out", True, 3, None)),
            "no done line": ((None, windows, at(500)), ("waiting", False, None, None)),
            "draining": ((at(0), windows[:2], at(60)), ("draining", False, 4, None)),
            "a zero in the done line's own second does not count": ((at(15), windows, at(30)),
                                                                     ("draining", False, 2, None)),
            "no window after the done line yet": ((at(20), windows, at(22)), ("draining", False, None, None)),
            "drained": ((at(0), windows, at(30)), ("drained", True, 2, at(15))),
            "drained wins over the bound": ((at(0), windows, at(500)), ("drained", True, 2, at(15))),
        }
        for name, ((done, wins, now), expected) in cases.items():
            with self.subTest(name):
                state = p0burst.drain_state(done, wins, now)
                self.assertEqual((state["state"], state["stop"], state["pending"], state["empty_at"]), expected)


class SessionTests(unittest.TestCase):
    def setUp(self):
        folder = tempfile.TemporaryDirectory()
        self.addCleanup(folder.cleanup)
        self.log = os.path.join(folder.name, "Editor.log")
        with open(self.log, "w", encoding="utf-8") as f:   # an earlier run's lines, before this run's mark
            f.write(saves(0, 0) + "\n" + stamped(0, DONE) + "\n")

    def session(self, now=at(30)):
        return p0burst.BurstSession(spec(burst_rows=50, cells_read=["C"]), Rep(), connect="db",
                                    editor_log=self.log, now=lambda: now)

    def append(self, *lines):
        with open(self.log, "a", encoding="utf-8") as f:
            f.write("".join(line + "\n" for line in lines))

    def test_before_play(self):
        session = self.session()
        with mock.patch.object(p0burst.provision_burst, "burst") as burst:
            session.before_play()
        burst.assert_called_once_with(50, log=session.rep.note)
        self.assertEqual(session.offset, os.path.getsize(self.log))
        refused = mock.Mock(side_effect=provision.ProvisionError("in Play"))
        with mock.patch.object(p0burst.provision_burst, "burst", refused), self.assertRaises(provision.ProvisionError):
            self.session().before_play()

    def test_stop_when(self):
        cases = {  # failure first: cells' lines (None: the read fails), editor lines added, now, (answer, state)
            "a cell log that cannot be read": (None, [saves(10, 0)], at(30), (False, None)),
            "the earlier run's lines are not read": ({"C": []}, [], at(30), (False, "waiting")),
            "draining": ({"C": [stamped(5, DONE)]}, [saves(10, 3)], at(30), (False, "draining")),
            "timed out": ({"C": [stamped(5, DONE)]}, [saves(10, 3)], at(200), (True, "timed out")),
            "drained (done in the cell's log)": ({"C": [stamped(5, DONE)]}, [saves(10, 3), saves(15, 0)], at(30),
                                                 (True, "drained")),
            "drained (done in the editor's log)": ({"C": []}, [stamped(5, DONE), saves(10, 0)], at(30),
                                                   (True, "drained")),
        }
        for name, (cells, editor, now, (answer, state)) in cases.items():
            with self.subTest(name):
                self.setUp()
                session = self.session(now)
                session.offset = os.path.getsize(self.log)
                self.append(*editor)
                if cells is None:
                    zone_lines = mock.Mock(side_effect=OSError("locked"))
                else:
                    def zone_lines(cell, hms, day, map_id, cells=cells):
                        return list(cells[cell])
                with mock.patch.object(p0burst.r, "zone_lines", zone_lines):
                    self.assertEqual(session.stop_when(PLAY, PLAY), answer)
                self.assertEqual(session.drain and session.drain["state"], state)

    def test_the_editor_log_is_read_once(self):
        session = self.session()
        session.offset = os.path.getsize(self.log)
        self.append(stamped(5, DONE), saves(10, 4))
        with mock.patch.object(p0burst.r, "zone_lines", lambda *a: []):
            self.assertFalse(session.stop_when(PLAY, PLAY))
            self.append(saves(15, 0))
            self.assertTrue(session.stop_when(PLAY, PLAY))
        self.assertEqual(len(session._editor), 3)

    def test_after_play_and_after_stop(self):
        first = {"meshburst0001Char": "2026-10-09 12:00:01", "meshburst0002Char": "2026-10-09 12:00:02"}
        changed = dict(first, meshburst0002Char="2026-10-09 12:01:00")
        cases = {  # failure first: (drain state, snapshots or errors), problems
            "the drain timed out": (("timed out", [first, first]),
                                    ["burst: pending 3 saves 120 s after the burst was done (the drain never emptied)"]),
            "no read before the stop": (("drained", [OSError("no pymysql")]),
                                        ["burst: updateAt not read before the stop: no pymysql"]),
            "no read after the stop": (("drained", [first, OSError("refused")]),
                                       ["burst: updateAt not read after the stop: refused"]),
            "a row changed after Play": (("drained", [first, changed]),
                                         ["burst: 1 row(s) changed after Play stopped: meshburst0002Char"]),
            "nothing changed": (("drained", [first, dict(first)]), []),
        }
        for name, ((state, answers), problems) in cases.items():
            with self.subTest(name):
                session = self.session()
                session.drain = {"state": state, "pending": 3}
                reads, left = [], list(answers)

                def snapshot(n, connect):
                    reads.append((n, connect))
                    answer = left.pop(0)
                    if isinstance(answer, Exception):
                        raise answer
                    return answer
                with mock.patch.object(p0burst.provision_burst, "snapshot", snapshot), \
                        mock.patch.object(p0burst.time, "sleep") as sleep:
                    session.after_play()
                    session.after_stop()
                self.assertEqual(session.problems, problems)
                self.assertEqual(reads, [(50, "db")] * len(answers))
                self.assertEqual(sleep.called, len(answers) > 1)
                self.assertIsNone(session.stop_reason)


class CompositionTests(unittest.TestCase):
    """p0peer.window_session with a burst session (step 9 decision 4): the A-only hooks beside B's."""

    def test_window_session_with_a_burst(self):
        order = []

        def hook(name, error=None):
            def call(self, *args):
                order.append(name)
                if error is not None:
                    raise error
            return call
        cases = {  # failure first: the burst's before_play error, then what the composed before_play raises
            "provision refuses the burst rows": (provision.ProvisionError("account meshburst0002 has email 'x'"),
                                                 (2, "burst rows: account meshburst0002 has email 'x'")),
            "the rows are reset": (None, None),
        }
        for name, (error, refusal) in cases.items():
            with self.subTest(name), mock.patch.object(p0peer, "RemoteB", FakeB), \
                    mock.patch.object(p0peer.PeerSession, "before_play", hook("peer before")), \
                    mock.patch.object(p0peer.PeerSession, "after_play", hook("peer after")), \
                    mock.patch.object(p0burst.BurstSession, "before_play", hook("burst before", error)), \
                    mock.patch.object(p0burst.BurstSession, "after_play",
                                      hook("burst after", RuntimeError("no database"))):
                order.clear()
                peer, burst, hooks = p0peer.window_session(spec({"bots": BOTS}, burst_rows=50), Rep())
                self.assertEqual((type(peer), type(burst), burst.n), (p0peer.PeerSession, p0burst.BurstSession, 50))
                self.assertEqual(sorted(hooks), ["after_play", "before_play", "on_play", "stop_when"])
                if refusal:
                    with self.assertRaises(p0peer.Stopped) as caught:
                        hooks["before_play"]()
                    self.assertEqual((caught.exception.code, caught.exception.reason), refusal)
                else:
                    hooks["before_play"]()
                with self.assertRaisesRegex(RuntimeError, "no database"):
                    hooks["after_play"]()
                self.assertEqual(order, ["peer before", "burst before", "burst after", "peer after"])
        _, burst, hooks = p0peer.window_session(spec(burst_rows=10), Rep())
        self.assertEqual(sorted(hooks), ["after_play", "before_play", "stop_when"])
        self.assertEqual((hooks["after_play"], hooks["stop_when"]), (burst.after_play, burst.stop_when))
        self.assertIsNone(p0peer.window_hooks(spec(burst_rows=10), Rep())[0])
        self.assertEqual(p0peer.window_session(spec(), Rep()), (None, None, {}))

    def test_window_session_stops_when_either_does(self):
        asked = []

        def stop(answer):
            return lambda play_start, start: (asked.append(answer), answer)[1]
        cases = {"neither": ((False, False), False), "the burst's drain": ((False, True), True),
                 "the given one": ((True, False), True)}
        for name, ((given, drained), expected) in cases.items():
            with self.subTest(name), mock.patch.object(p0burst.BurstSession, "stop_when",
                                                       lambda self, a, b, drained=drained: stop(drained)(a, b)):
                asked.clear()
                _, _, hooks = p0peer.window_session(spec(burst_rows=10), Rep(), stop_when=stop(given))
                self.assertEqual(hooks["stop_when"](PLAY, PLAY), expected)
                self.assertEqual(asked, [given, drained])

    def test_mark_rows_with_a_burst(self):
        burst = p0burst.BurstSession(spec(burst_rows=10), Rep())
        burst.problems = ["burst: 2 row(s) changed after Play stopped: meshburst0001Char, meshburst0002Char"]
        rows = p0peer.mark_rows([{"accepted": True, "problems": []}], None, burst)
        self.assertEqual(rows, [{"accepted": False, "problems": burst.problems}])
        self.assertEqual(p0peer.mark_rows([{"accepted": True}], None, None), [{"accepted": True}])


if __name__ == "__main__":
    unittest.main()
