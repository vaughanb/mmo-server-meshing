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
                     "autosplit", "autobenefit", "autorefuse", "splitbuild", "splitcentral", "splitcentral_merge",
                     "splitcentral_base", "splitcentral_unfreeze"):
            with self.subTest(name):
                self.assertIn(name, regress.ALL)
                self.assertNotIn(name, regress.DEFAULT)
                self.assertIn("spawner", regress.TWO_HOST_SKIP[name])
                self.assertIn(name, regress.PERSIST_SKIP)
        for key in ("MESH_FAULT_DIE_ON_PREWARM", "MESH_FAULT_DIE_ON_DRAIN", "MESH_FAULT_DIE_ON_NOTICE", "MESH_AUTO_OPS",
                    "MESH_SPLIT_UTIL", "MESH_SPLIT_DWELL", "MESH_MERGE_UTIL", "MESH_MERGE_AFTER", "MESH_OP_COOLDOWN",
                    "MESH_SPLIT_BENEFIT", "MESH_MONSTER_WEIGHT", "MESH_PLANNER_WEIGHTS", "MESH_HOST_CORES", "MESH_PROBE_BURN_MS",
                    "MESH_EDITOR_SERVERS", "MESH_FAULT_DIE_ON_HELLO"):
            self.assertIn(key, regress.ENV_KEYS)
        self.assertEqual(len(regress.ENV_KEYS), len(set(regress.ENV_KEYS)))
        # Slice 5: the central-restart scenarios run the split-process topology, everything else the editor's.
        for name in ("splitcentral", "splitcentral_merge", "splitcentral_base", "splitcentral_unfreeze"):
            self.assertEqual(regress.topology_of(name), "split")
        for name in ("split", "merge", "pet", "splitcrash_base"):
            self.assertEqual(regress.topology_of(name), "editor")
        # Slice 3: an expected id outside ZONES (a dynamic child) is graded on the test map; the Coarse set names three.
        self.assertEqual(regress.expected_zones({"C", "C.1", "C2"}), [("C", regress.MAP), ("C2", regress.MAP2), ("C.1", regress.MAP)])
        self.assertEqual(regress.expected_zones({"E"}), [("E", regress.MAP)])
        self.assertEqual(regress.LAYOUT_COMMAND, os.path.join(regress.TEMP, "ServerMesh.LayoutCommand.txt"))
        self.assertEqual(regress.LAYOUT_COMMAND_STATUS, os.path.join(regress.TEMP, "ServerMesh.LayoutCommandStatus.txt"))


class GradedZonesTests(unittest.TestCase):
    """The graded set is the cells that logged in the scenario's window plus the ones it expects, not every cell with a
    day file: a child of an earlier split scenario kept its file all day and added three vacuous checks to each later
    scenario, so the same scenario's check count changed at midnight (2026-10-20, all:Dynamic)."""

    def write(self, logs, cell, day, *times, map_id=regress.MAP):
        with open(os.path.join(logs, f"Log_Map({map_id})-Ch({cell})-Alloc(False)-Instance()_{day}.info.log"), "w") as f:
            f.writelines(f" INFO Mesh [{day} {t}] - Mesh stats\n" for t in times)

    def graded(self, logs, start, day, expect=None):
        with mock.patch.multiple(regress, LOGS=logs, REMOTE_CELLS=[]):
            return regress.graded_zones(day, start, expect)

    def test_a_day_file_with_no_lines_in_the_window_is_not_graded(self):
        with tempfile.TemporaryDirectory() as logs:
            self.write(logs, "C.1", "2026-10-20", "08:00:00")      # an earlier split scenario's child
            self.write(logs, "E.1", "2026-10-20", "08:00:00", "10:00:01")
            self.assertEqual(self.graded(logs, "10:00:00", "2026-10-20"), regress.ZONES + [("E.1", regress.MAP)])
            # Without a start the day file still lists it (the split and merge graders skip silent cells themselves).
            self.assertIn(("C.1", regress.MAP), self.graded(logs, None, "2026-10-20"))

    def test_an_expected_zone_with_no_lines_is_still_graded(self):
        with tempfile.TemporaryDirectory() as logs:
            self.assertEqual(self.graded(logs, "10:00:00", "2026-10-20", expect={"C", "C.1"}),
                             regress.ZONES + [("C.1", regress.MAP)])
            # ...and grade_common fails it rather than passing its zero teleports and exceptions.
            rep = regress.Report("x")
            with mock.patch.multiple(regress, LOGS=logs, REMOTE_CELLS=[]):
                regress.grade_common(rep, "10:00:00", "2026-10-20", expect={"C.1"})
            self.assertIn((False, "zone C.1: logged since the scenario started"), rep.checks)
            self.assertIn((True, "zone C.1: 0 exception lines"), rep.checks)

    def test_a_scenario_across_midnight_reads_both_days(self):
        with tempfile.TemporaryDirectory() as logs:
            self.write(logs, "C.1", "2026-10-21", "00:00:30")       # split after midnight: only the next day's file
            self.write(logs, "E.1", "2026-10-20", "23:59:50")
            self.write(logs, "N.1", "2026-10-20", "12:00:00")       # earlier that day: out of the window
            self.write(logs, "S.1", "2026-10-22", "00:00:30")       # two days on: not this scenario's
            self.assertEqual(self.graded(logs, "23:59:00", "2026-10-20"),
                             regress.ZONES + [("C.1", regress.MAP), ("E.1", regress.MAP)])

    def test_the_check_count_does_not_depend_on_earlier_scenarios(self):
        with tempfile.TemporaryDirectory() as logs:
            for zone, map_id in regress.ZONES:
                self.write(logs, zone, "2026-10-20", "10:00:05", map_id=map_id)
            counts = []
            for leftovers in ((), ("C.1", "E.1")):
                for cell in leftovers:
                    self.write(logs, cell, "2026-10-20", "08:00:00")
                rep = regress.Report("x")
                with mock.patch.multiple(regress, LOGS=logs, REMOTE_CELLS=[]):
                    regress.grade_common(rep, "10:00:00", "2026-10-20")
                counts.append(len(rep.checks))
            self.assertEqual(counts[0], counts[1])


class RigPortsTests(unittest.TestCase):
    def test_cells_and_links_cover_the_multihost_ranges(self):
        ports = set(regress.RIG_PORTS)
        self.assertTrue(set(range(8000, 8064)) <= ports)
        self.assertTrue(set(range(18000, 18064)) <= ports)
        self.assertNotIn(8064, ports)
        self.assertEqual(len(regress.RIG_PORTS), len(ports))


if __name__ == "__main__":
    unittest.main()
