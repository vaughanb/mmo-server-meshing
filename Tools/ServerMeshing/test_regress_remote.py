"""Unit tests for regress_remote (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py"): the remote family,
the per-mode log folders, the environment a remote start gets, and where a process snippet or a fault marker goes. No
test reaches machine B."""
import os
import tempfile
import unittest
from unittest import mock

import regress
import regress_remote
import remotespawner
import rigtools


class FakeRig:
    EXE = "kit.exe"

    def __init__(self, temp, cells=("C",), mode="spawner"):
        self.TEMP, self.REMOTE_CELLS, self.REMOTE_MODE = temp, list(cells), mode
        self.scripts = []

    def ps(self, script):
        self.scripts.append(script)
        return "1"


class PureTests(unittest.TestCase):
    def test_in_family(self):
        for zone, expected in (("C", True), ("C.1", True), ("C.2", True), ("C2", False), ("E", False), ("", False)):
            with self.subTest(zone):
                self.assertEqual(regress_remote.in_family(zone, ["C"]), expected)

    def test_logs_dir(self):
        self.assertEqual(regress_remote.logs_dir("L", "cell"), os.path.join("L", "remote"))
        self.assertEqual(regress_remote.logs_dir("L", "spawner"), os.path.join("L", "remote", "rig"))

    def test_cell_env(self):
        env = {"MESH_SPAWN_CELLS": "SW", "MESH_FAULT_DIE_ON_NOTICE": "E", "MESH_EDITOR_SERVERS": "0", "MESH_SLEEP": ""}
        self.assertEqual(regress_remote.cell_env(env, "cell", None, None),
                         ["--env", "MESH_EDITOR_SERVERS=0", "--env", "MESH_FAULT_DIE_ON_NOTICE=E"])
        self.assertEqual(regress_remote.cell_env(env, "spawner", "T", r"R\Temp"), ["--env", "MESH_FAULT_DIE_ON_NOTICE=E"])


class RoutingTests(unittest.TestCase):
    def test_a_remote_familys_snippet_runs_on_b(self):
        with tempfile.TemporaryDirectory() as d:
            rig = FakeRig(d)
            with mock.patch.object(remotespawner, "cell_script", return_value="1") as remote:
                self.assertEqual(regress_remote.cell_ps(rig, "C.2", lambda exe, cell: f"{exe} {cell}"), "1")
            remote.assert_called_once()
            self.assertEqual(rig.scripts, [])
            self.assertEqual(regress_remote.cell_ps(rig, "E", lambda exe, cell: f"{exe} {cell}"), "1")
            self.assertEqual(rig.scripts, ["kit.exe E"])
            rig.REMOTE_MODE = "cell"
            regress_remote.cell_ps(rig, "C", lambda exe, cell: f"{exe} {cell}")
            self.assertEqual(rig.scripts[-1], "kit.exe C", "cell mode has no remote family")

    def test_local_markers(self):
        with tempfile.TemporaryDirectory() as d:
            rig = FakeRig(d, cells=())
            path = os.path.join(d, "ServerMesh.FaultFired.X.C.2")
            regress_remote.set_marker(rig, "C.2", "ServerMesh.FaultFired.X.C.2", "file")
            self.assertTrue(os.path.isfile(path))
            regress_remote.set_marker(rig, "C.2", "ServerMesh.FaultFired.X.C.2", "dir")
            self.assertTrue(os.path.isdir(path))
            regress_remote.set_marker(rig, "C.2", "ServerMesh.FaultFired.X.C.2", "clear")
            self.assertFalse(os.path.exists(path))

    def test_remote_markers_and_temp_files_go_to_b(self):
        with tempfile.TemporaryDirectory() as d:
            rig = FakeRig(d)
            with mock.patch.object(remotespawner, "run_script", return_value="ok") as remote:
                regress_remote.set_marker(rig, "C.2", "ServerMesh.FaultFired.X.C.2", "dir")
                regress_remote.mirror_temp(rig, "ServerMesh.ProbeBurn.txt", "C:14")
            self.assertEqual(os.listdir(d), [])
            self.assertIn("New-Item -ItemType Directory", remote.call_args_list[0].args[1])
            self.assertIn("'C:14'", remote.call_args_list[1].args[1])
            rig.REMOTE_MODE = "cell"
            with mock.patch.object(remotespawner, "run_script") as remote:
                regress_remote.mirror_temp(rig, "ServerMesh.ProbeBurn.txt", "C:14")
            remote.assert_not_called()


class RegressWiringTests(unittest.TestCase):
    def test_zone_log_follows_the_family_in_spawner_mode(self):
        with mock.patch.multiple(regress, REMOTE_CELLS=["C"], REMOTE_MODE="spawner", LOGS="L"):
            self.assertTrue(regress.zone_log("C.1", "info", "2026-10-22").startswith(os.path.join("L", "remote", "rig")))
            self.assertTrue(regress.zone_log("E", "info", "2026-10-22").startswith("L" + os.sep + "Log_Map"))
        with mock.patch.multiple(regress, REMOTE_CELLS=["C"], REMOTE_MODE="cell", LOGS="L"):
            self.assertTrue(regress.zone_log("C", "info", "2026-10-22").startswith(os.path.join("L", "remote") + os.sep + "Log_"))
            self.assertTrue(regress.zone_log("C.1", "info", "2026-10-22").startswith("L" + os.sep + "Log_Map"))

    def test_modes(self):
        self.assertEqual(regress.remote_mode_of("pet"), "cell")
        for name in ("split", "merge", "splitcrash_drain", "splitbuild", "autosplit", "collapsesibling", "splitcentral", "sleep"):
            self.assertEqual(regress.remote_mode_of(name), "spawner")
        with mock.patch.object(regress, "REMOTE_FORCE_SPAWNER", True):
            self.assertEqual(regress.remote_mode_of("pet"), "spawner")


class FamilyHostTests(unittest.TestCase):
    @staticmethod
    def write(folder, cell, text):
        os.makedirs(folder, exist_ok=True)
        name = f"Log_Map(ServerMeshTestMap)-Ch({cell})-Alloc(False)-Instance()_2026-10-22.info.log"
        with open(os.path.join(folder, name), "w", encoding="utf-8") as f:
            f.write(text)

    def test_a_family_cell_logging_here_fails(self):
        with tempfile.TemporaryDirectory() as d:
            rig = FakeRig(d)
            rig.LOGS = d
            rig.fetch_remote = lambda force=False: None
            rig.REMOTE_CLOCK_SHIFT = 0
            line = " INFO N/A [2026-10-22 10:00:05] - [Mesh] Mesh stats\n"
            self.write(os.path.join(d, "remote", "rig"), "C.1", line)
            self.write(os.path.join(d, "remote", "rig"), "C", line)
            self.write(d, "E", line)
            rep = regress.Report("x")
            regress_remote.grade_family_host(rep, rig, "10:00:00", "2026-10-22")
            self.assertTrue(rep.passed, rep.checks)
            self.assertIn("ran C, C.1", rep.notes[0])
            self.write(d, "C.2", line)
            rep = regress.Report("x")
            regress_remote.grade_family_host(rep, rig, "10:00:00", "2026-10-22")
            self.assertFalse(rep.passed)
            self.assertIn("(C.2)", rep.checks[0][1])



class ClockTests(unittest.TestCase):
    def test_offset_is_the_median_against_each_round_trips_midpoint(self):
        samples = [(100.0, 96.9, 100.5), (200.0, 196.85, 200.4), (300.0, 290.0, 300.2)]
        self.assertAlmostEqual(regress_remote.clock_offset(samples), -3.35)

    def test_shift_stamp(self):
        line = " INFO N/A [2026-10-22 23:59:58] - [Mesh] x"
        self.assertEqual(rigtools.shift_stamp(line, 3), " INFO N/A [2026-10-23 00:00:01] - [Mesh] x")
        self.assertEqual(rigtools.shift_stamp(line, 0), line)
        self.assertEqual(rigtools.shift_stamp("no stamp", 3), "no stamp")

    def test_zone_lines_put_the_remote_familys_stamps_on_this_clock(self):
        with tempfile.TemporaryDirectory() as d:
            FamilyHostTests.write(os.path.join(d, "remote", "rig"), "C.1", " INFO N/A [2026-10-22 09:59:58] - a\n")
            FamilyHostTests.write(d, "E", " INFO N/A [2026-10-22 09:59:58] - b\n")
            with mock.patch.multiple(regress, REMOTE_CELLS=["C"], REMOTE_MODE="spawner", LOGS=d, REMOTE_CLOCK_SHIFT=3,
                                     fetch_remote=lambda force=False: None):
                self.assertEqual(regress.zone_lines("C.1", "10:00:00", "2026-10-22"), [" INFO N/A [2026-10-22 10:00:01] - a"])
                self.assertEqual(regress.zone_lines("E", "10:00:00", "2026-10-22"), [])


if __name__ == "__main__":
    unittest.main()
