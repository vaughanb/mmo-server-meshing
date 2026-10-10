"""Unit tests for p0calibration, step 8b's bot registration and calibration (python -m unittest discover -s
Tools/ServerMeshing -p "test_*.py"). Split out of test_p0peer with the module; its names are reached through p0peer's
re-exports, so both import paths stay pinned. Machine B is test_p0peer.FakeB, the editor's regress calls are patched,
and every subprocess refuses (test_p0peer's setUpModule, run here too).
"""
import contextlib
import json
import os
import tempfile
import unittest
from unittest import mock

import p0calibration
import p0peer
from test_p0peer import FakeB, Rep, fake_time, setUpModule, tearDownModule  # noqa: F401 (the subprocess guards)


BOT_NET = ("[MeshAuto] Bot net window 5.00 s: in 52.3 kB/s 120 pkt/s out 3.1 kB/s {} pkt/s | hops 0 | rtt 1 ms "
           "| players seen 49 | at 2026-10-07T16:00:{:02d}Z")


def bot_log(rates):
    return ["[MeshAuto] In map at (-4.0, 0.1, 0.0)"] + [BOT_NET.format(rate, 5 * i) for i, rate in enumerate(rates)]


class CalibrationTests(unittest.TestCase):
    def test_bot_upload_rate(self):
        cases = {
            "no window": ({"meshbot2": ["In map at"]}, (None, 0)),
            "only the skipped windows": ({"meshbot2": bot_log([90, 90])}, (None, 0)),
            "medians of medians": ({"meshbot2": bot_log([90, 90, 20, 22, 21]), "meshbot3": bot_log([0, 0, 30, 30]),
                                    "meshbot4": bot_log([0, 0, 10])}, (21.0, 3)),
        }
        for name, (lines, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0peer.bot_upload_rate(lines), expected)

    def test_calibration_record(self):
        state = dict(FakeB().state_value, bots=100)
        cases = {  # (in map, hosted), fits
            "not all in the map": ((98, 100), False),
            "some exited": ((100, 97), False),
            "all fit": ((100, 100), True),
        }
        for name, ((arrived, hosted), fits) in cases.items():
            with self.subTest(name):
                record = p0peer.calibration_record("b1", 100, arrived, 180, dict(state, bots=hosted),
                                                   {"meshbot2": bot_log([0, 0, 20.6])})
                self.assertEqual((record["fits"], record["upload_pkt_per_s"], record["upload_hz"], record["hosted"],
                                  record["free_memory_MB"], record["peer_host"]),
                                 (fits, 20.6, 21, hosted, 40000, "MESHB"))
                self.assertEqual(p0peer.hosts(record, 100), fits)
        self.assertFalse(p0peer.hosts(None, 100))

    def test_read_calibration(self):
        with tempfile.TemporaryDirectory() as folder:
            path = os.path.join(folder, "bots_calibration.json")
            self.assertIsNone(p0peer.read_calibration("b1", path=path))
            with open(path, "w", encoding="utf-8") as f:
                json.dump({"build": "b1", "peer_host": "MESHB", "fits": True, "tried": 100}, f)
            cases = {"another build": (("b2", None), False), "another B": (("b1", "OTHER"), False),
                     "this build, any B": (("b1", None), True), "this build and B": (("b1", "MESHB"), True)}
            for name, ((build, host), found) in cases.items():
                with self.subTest(name):
                    self.assertEqual(p0peer.read_calibration(build, host, path) is not None, found)

    @contextlib.contextmanager
    def editor(self):
        calls = []

        def note(name, result=None):
            return lambda *a, **k: (calls.append(name), result)[1]
        with contextlib.ExitStack() as stack:
            for name, result in (("kill_bots", None), ("kit_process_count", 0), ("request_play", None),
                                 ("wait_play", None), ("stop_play", 0)):
                stack.enter_context(mock.patch.object(p0peer.r, name, note(name, result)))
            stack.enter_context(fake_time())
            yield calls

    def test_register_bots(self):
        with tempfile.TemporaryDirectory() as folder:
            logs = []
            for user in ("meshbot2", "meshbot3", "meshbot4", "meshbot5", "meshbot6", "meshbot7"):
                logs.append(os.path.join(folder, user + ".log"))
                with open(logs[-1], "w", encoding="utf-8") as f:
                    f.write("In map at (-50, 0, 0)\n" if user != "meshbot4" else "Logged in\n")
            b, rep = FakeB(logs=logs), Rep()
            with self.editor() as calls:
                registered = p0peer.register_bots(6, rep, b)
        self.assertEqual(registered, ["meshbot2", "meshbot3", "meshbot5", "meshbot6", "meshbot7"])
        self.assertEqual(calls, ["kill_bots", "kit_process_count", "request_play", "wait_play", "stop_play"])
        starts = [call[1:4] for call in b.calls if call[0] == "start_bots"]
        self.assertEqual(starts, [(2, 5, "stand"), (7, 1, "stand")])
        self.assertIn("meshbot2..meshbot6: 4 of 5 in map (not: meshbot4)", rep.lines)
        self.assertEqual(rep.lines[-1], "registered 5 of 6")

    def test_register_bots_stops_when_b_keeps_bots(self):
        b = FakeB()
        answers = iter([0, 2, 2])     # quiet_b's stop, the group's stop, the cleanup's stop
        with self.editor() as calls, mock.patch.object(b, "stop_bots", lambda: next(answers)):
            with self.assertRaises(p0peer.Stopped) as caught:
                p0peer.register_bots(2, Rep(), b)
        self.assertEqual((caught.exception.code, caught.exception.reason), (3, "B: 2 bots survived bots-stop"))
        self.assertEqual(calls[-1], "stop_play")

    def test_calibrate_bots(self):
        with tempfile.TemporaryDirectory() as folder:
            logs = []
            for k in range(2, 6):
                logs.append(os.path.join(folder, f"meshbot{k}.log"))
                with open(logs[-1], "w", encoding="utf-8") as f:
                    f.write("\n".join(bot_log([0, 0, 20])) + "\n")
            b = FakeB(state=dict(FakeB().state_value, bots=4), logs=logs)
            path = os.path.join(folder, "p0", "bots_calibration.json")
            with self.editor() as calls, mock.patch.object(p0peer.provision, "reset_bot_positions") as reset, \
                    mock.patch.object(p0calibration, "build_id", lambda: "b1"):
                record = p0peer.calibrate_bots(Rep(), 4, b, path)
            with open(path, encoding="utf-8") as f:
                self.assertEqual(json.load(f), record)
        reset.assert_called_once_with(2, 4, [(-25.0, -25.0), (25.0, -25.0), (-25.0, 25.0), (25.0, 25.0)])
        self.assertEqual((record["build"], record["tried"], record["in_map"], record["fits"], record["upload_hz"]),
                         ("b1", 4, 4, True, 20))
        self.assertEqual(calls[-1], "stop_play")
        self.assertEqual([c[1:] for c in b.calls if c[0] == "start_bots"][0][2:], ("pace", reset.call_args[0][2], 5,
                                                                                   None, 1))


if __name__ == "__main__":
    unittest.main()
