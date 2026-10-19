"""Unit tests for regress's status waits (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py")."""
import os
import tempfile
import unittest
from unittest import mock

import regress


class FakeClock:
    """time.time/time.sleep for regress: sleeping advances the clock, and the status age follows a script."""

    def __init__(self, ages, ready_at=None):
        self.now = 1000.0
        self.start = self.now
        self.ages = ages            # elapsed seconds -> status age, the last entry at or before now applies
        self.ready_at = ready_at    # elapsed seconds after which the predicate holds; None never

    def time(self):
        return self.now

    def sleep(self, seconds):
        self.now += seconds

    def elapsed(self):
        return self.now - self.start

    def age(self):
        current = None
        for at, age in sorted(self.ages.items()):
            if self.elapsed() >= at:
                current = age
        return current

    def ready(self, _status):
        return self.ready_at is not None and self.elapsed() >= self.ready_at


class WaitStatusTests(unittest.TestCase):
    def run_wait(self, clock, timeout, busy_cap):
        with mock.patch.object(regress.time, "time", clock.time), \
                mock.patch.object(regress.time, "sleep", clock.sleep), \
                mock.patch.object(regress, "status_age", clock.age), \
                mock.patch.object(regress, "read_status", lambda: {}):
            return regress.wait_status(clock.ready, timeout, "Play", busy_cap=busy_cap)

    def test_times_out(self):
        cases = {
            "idle editor, never ready": (FakeClock({0: 1.0}), 60, 120, 60),
            "no status file at all": (FakeClock({0: None}), 60, 120, 60),
            "busy but no cap": (FakeClock({0: 30.0}), 60, 0, 60),
            "busy past the cap": (FakeClock({0: 30.0}), 60, 120, 180),
            "busy for 40 s, then idle": (FakeClock({0: 1.0, 10: 20.0, 50: 1.0}), 60, 120, 100),
        }
        for name, (clock, timeout, cap, gave_up_at) in cases.items():
            with self.subTest(name):
                with self.assertRaises(TimeoutError):
                    self.run_wait(clock, timeout, cap)
                self.assertAlmostEqual(clock.elapsed(), gave_up_at, delta=2)

    def test_returns(self):
        cases = {
            "ready at once": (FakeClock({0: 1.0}, ready_at=0), 60, 120),
            "ready inside the timeout": (FakeClock({0: 1.0}, ready_at=40), 60, 120),
            "slow reload, ready after the timeout": (FakeClock({0: 1.0, 4: 40.0, 44: 1.0}, ready_at=90), 60, 120),
        }
        for name, (clock, timeout, cap) in cases.items():
            with self.subTest(name):
                self.assertEqual(self.run_wait(clock, timeout, cap), {})
                self.assertAlmostEqual(clock.elapsed(), clock.ready_at, delta=2)


class SplitWiringTests(unittest.TestCase):
    """Slice 2b: the split scenarios' registration, the LayoutCommand files cleared at every start, launch_bot's new
    arguments. Popen is replaced, so no bot starts."""

    def test_launch_bot(self):
        cases = {
            "the defaults": ({}, ["-meshChannel", "C", "-meshAutoMode", "stand"]),
            "another channel": ({"channel": "E"}, ["-meshChannel", "E", "-meshAutoMode", "stand"]),
            "the split's stand bot": ({"observe_seconds": 300, "line_x": 0.0},
                                      ["-meshChannel", "C", "-meshAutoMode", "stand", "-meshObserveSeconds", "300",
                                       "-meshLineX", "0"]),
            "the harvest bot": ({"line_x": 200.0, "harvest_after_migration": True},
                                ["-meshChannel", "C", "-meshAutoMode", "stand", "-meshLineX", "200",
                                 "-meshHarvestAfterMigration", "1"]),
        }
        for name, (kwargs, expected) in cases.items():
            with self.subTest(name), tempfile.TemporaryDirectory() as d, \
                    mock.patch.object(regress, "LOGS", d), mock.patch.object(regress.subprocess, "Popen") as popen:
                log = regress.launch_bot("split", "meshbot2", ["-meshAutoMode", "stand"], **kwargs)
                cmd = popen.call_args[0][0]
                self.assertEqual(cmd[:6], [regress.EXE, "-batchmode", "-nographics", "-meshAutoClient", "-meshUser",
                                           "meshbot2"])
                self.assertEqual(cmd[6:-2], expected)
                self.assertEqual(cmd[-2:], ["-logFile", log])
                self.assertEqual(log, os.path.join(d, "regress_split_meshbot2.log"))

    def test_scenario_start_clears_the_layout_command_files(self):
        with tempfile.TemporaryDirectory() as d:
            paths = {name: os.path.join(d, f"{name}.txt") for name in
                     ("COORDINATOR_STORE", "LAYOUT_COMMAND", "LAYOUT_COMMAND_STATUS")}
            for path in paths.values():
                open(path, "w").close()
            with mock.patch.multiple(regress, TEMP=d, **paths), self.assertRaises(ValueError):
                regress.run_scenario("no such scenario", regress.Report("x"))
            self.assertEqual(os.listdir(d), [])

    def test_registration(self):
        for name in ("split", "splitc2", "merge", "mergec2", "splitcrash_prewarm", "splitcrash_drain", "splitcrash_base",
                     "autosplit", "autobenefit", "autorefuse", "splitbuild"):
            with self.subTest(name):
                self.assertIn(name, regress.ALL)
                self.assertNotIn(name, regress.DEFAULT)
                self.assertIn("spawner", regress.TWO_HOST_SKIP[name])
                self.assertIn(name, regress.PERSIST_SKIP)
        for key in ("MESH_FAULT_DIE_ON_PREWARM", "MESH_FAULT_DIE_ON_DRAIN", "MESH_FAULT_DIE_ON_NOTICE", "MESH_AUTO_OPS",
                    "MESH_SPLIT_UTIL", "MESH_SPLIT_DWELL", "MESH_MERGE_UTIL", "MESH_MERGE_AFTER", "MESH_OP_COOLDOWN",
                    "MESH_SPLIT_BENEFIT", "MESH_MONSTER_WEIGHT", "MESH_PLANNER_WEIGHTS", "MESH_HOST_CORES", "MESH_PROBE_BURN_MS"):
            self.assertIn(key, regress.ENV_KEYS)
        self.assertEqual(len(regress.ENV_KEYS), len(set(regress.ENV_KEYS)))
        # Slice 3: an expected id outside ZONES (a dynamic child) is graded on the test map; the Coarse set names three.
        self.assertEqual(regress.expected_zones({"C", "C.1", "C2"}), [("C", regress.MAP), ("C2", regress.MAP2), ("C.1", regress.MAP)])
        self.assertEqual(regress.expected_zones({"E"}), [("E", regress.MAP)])
        self.assertEqual(regress.LAYOUT_COMMAND, os.path.join(regress.TEMP, "ServerMesh.LayoutCommand.txt"))
        self.assertEqual(regress.LAYOUT_COMMAND_STATUS, os.path.join(regress.TEMP, "ServerMesh.LayoutCommandStatus.txt"))


if __name__ == "__main__":
    unittest.main()
