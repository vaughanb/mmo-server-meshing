"""Unit tests for remotespawner's pure parts and its use of remotessh (python -m unittest discover -s Tools/ServerMeshing
-p "test_*.py"). No test reaches machine B: ssh, scp and PowerShell refuse unless a test stands in for them."""
import base64
import subprocess
import unittest
from unittest import mock

import remoteb
import remotespawner
import remotessh

EXE = r"C:\MeshTest\Server\kit-ce-project.exe"
RIG = r"C:\MeshTest\Rig"
A, B = "192.168.50.83", "192.168.50.211"
_GUARDS = []


def setUpModule():
    for name in ("run", "Popen"):
        patcher = mock.patch.object(subprocess, name, side_effect=AssertionError(f"a test ran subprocess.{name}"))
        patcher.start()
        _GUARDS.append(patcher)


def tearDownModule():
    while _GUARDS:
        _GUARDS.pop().stop()


class ConfigTests(unittest.TestCase):
    def test_rig_config_points_the_cells_at_a(self):
        base = {"centralAddress": "127.0.0.1", "databaseManagerAddress": "127.0.0.1", "publicAddress": "127.0.0.1",
                "clusterPort": 7000, "channels": [{"id": "1"}], "machineAddress": "10.0.0.1"}
        config = remotespawner.rig_config(base, A, B)
        self.assertEqual((config["centralAddress"], config["LoginAddress"], config["databaseManagerAddress"]), (A, A, A))
        self.assertEqual((config["publicAddress"], config["machineAddress"]), (B, None))
        self.assertEqual((config["clusterPort"], config["databaseManagerPort"], config["spawnStartPort"]), (6010, 6100, 8000))
        self.assertEqual(config["channels"], [{"id": "1"}], "the rest of A's config is kept")
        self.assertEqual(base["publicAddress"], "127.0.0.1", "A's config is not changed")

    def test_spawner_arguments(self):
        line = " ".join(remotespawner.spawner_arguments(EXE, A, B, RIG))
        self.assertEqual(line, "-batchmode -nographics -startMapSpawnServer -centralAddress 192.168.50.83 -clusterPort 6010 "
                               "-mapSpawnPort 6001 -spawnExePath \"C:\\MeshTest\\Server\\kit-ce-project.exe\" "
                               "-spawnStartPort 8000 -publicAddress 192.168.50.211 "
                               "-spawnMaps \"ServerMeshTestMap|ServerMeshTestMap2\" -logFile \"C:\\MeshTest\\Rig\\Logs\\spawner.log\"")

    def test_launch_body(self):
        body = remotespawner.launch_body(RIG, EXE, ["-x"], ["C", "E"], {"MESH_CELL_MODE": "M:Dynamic", "MESH_SPAWN_CELLS": "N",
                                                                         "MESH_FAULT_DIE_ON_HELLO": "C.2:1", "EMPTY": ""})
        self.assertEqual(body, ["@echo off", 'cd /d "C:\\MeshTest\\Rig"', 'set "MESH_CELL_MODE=M:Dynamic"',
                                'set "MESH_FAULT_DIE_ON_HELLO=C.2:1"', 'set "MESH_SPAWN_CELLS=C,E"',
                                '"C:\\MeshTest\\Server\\kit-ce-project.exe" -x'])

    def test_translate_env(self):
        local = r"D:\Game Development\mmo-server-meshing\Temp"
        env = {"MESH_PROBE_BURN_MS": "@" + local + r"\ServerMesh.ProbeBurn.txt", "MESH_SPAWN_CELLS": "SW,S",
               "MESH_COORDINATOR_STORE": "x", "MESH_EDITOR_SERVERS": "0", "MESH_CELL_MODE": "M:Dynamic", "MESH_LINKS": ""}
        self.assertEqual(remotespawner.translate_env(env, local, RIG + r"\Temp"),
                         {"MESH_PROBE_BURN_MS": r"@C:\MeshTest\Rig\Temp\ServerMesh.ProbeBurn.txt", "MESH_CELL_MODE": "M:Dynamic"})


class ScriptTests(unittest.TestCase):
    def test_marker_scripts(self):
        path = RIG + r"\Temp\ServerMesh.FaultFired.X.C.2"
        self.assertIn(f"New-Item -ItemType Directory -Force -Path '{path}'", remotespawner.marker_script(RIG, "ServerMesh.FaultFired.X.C.2", "dir"))
        self.assertIn(f"Set-Content -Path '{path}'", remotespawner.marker_script(RIG, "ServerMesh.FaultFired.X.C.2", "file"))
        clear = remotespawner.marker_script(RIG, "ServerMesh.FaultFired.X.C.2", "clear")
        self.assertIn(f"Remove-Item -Recurse -Force '{path}'", clear)
        self.assertNotIn("New-Item", clear)
        with self.assertRaises(ValueError):
            remotespawner.marker_script(RIG, "x", "link")

    def test_tempfile_and_clearmarkers_scripts(self):
        script = remotespawner.tempfile_script(RIG, "ServerMesh.ProbeBurn.txt", "C:14,E:7")
        self.assertIn("WriteAllText('C:\\MeshTest\\Rig\\Temp\\ServerMesh.ProbeBurn.txt.tmp', 'C:14,E:7')", script)
        self.assertIn("Move-Item -Force", script)
        clear = remotespawner.clearmarkers_script([r"C:\MeshTest\Server", RIG])
        self.assertIn("@('C:\\MeshTest\\Server\\Temp','C:\\MeshTest\\Rig\\Temp')", clear)
        self.assertIn("Remove-Item -Recurse -Force", clear)

    def test_cell_script_runs_the_snippet_encoded_against_bs_executable(self):
        seen = []

        def fake_run(argv, **kwargs):
            seen.append(argv)
            return subprocess.CompletedProcess(argv, 0, "1\n", "")

        args = remotessh.defaults()
        with mock.patch.object(subprocess, "run", side_effect=fake_run):
            out = remotespawner.cell_script(args, lambda exe, cell: f"\"{exe}\" | {cell}", "C.1")
        self.assertEqual(out, "1")
        script = base64.b64decode(seen[0][seen[0].index("-EncodedCommand") + 1]).decode("utf-16-le")
        self.assertEqual(script, f"\"{args.dir}\\kit-ce-project.exe\" | C.1")

    def test_a_failing_script_raises(self):
        with mock.patch.object(subprocess, "run", return_value=subprocess.CompletedProcess([], 1, "", "boom")):
            with self.assertRaises(RuntimeError):
                remotespawner.run_script(remotessh.defaults(), "x")
        with mock.patch.object(subprocess, "run", return_value=subprocess.CompletedProcess([], 255, "", "timed out")):
            with self.assertRaises(remotessh.Unreachable):
                remotespawner.run_script(remotessh.defaults(), "x")


class RemotebWiringTests(unittest.TestCase):
    def test_commands_and_options(self):
        for name in ("spawner-start", "marker", "tempfile"):
            self.assertIn(name, remoteb.COMMANDS)
        args = remoteb.build_parser().parse_args(["fetch", "--from-rig", "--rig", r"D:\R"])
        self.assertTrue(args.from_rig)
        self.assertEqual(args.rig, r"D:\R")

    def test_stop_kills_the_spawner_first(self):
        calls = []
        with mock.patch.object(remotessh, "stop", side_effect=lambda args, test, remaining: calls.append(test) or 0):
            remoteb.cmd_stop(remotessh.defaults())
        self.assertEqual(calls, [remotessh.STOP_SPAWNER, remotessh.STOP_CELLS])
        self.assertIn("-startMapSpawnServer", remotessh.STOP_SPAWNER)

    def test_spawner_start_refuses_without_cells(self):
        args = remoteb.build_parser().parse_args(["spawner-start"])
        self.assertEqual(remotespawner.cmd_spawner_start(args), 2)


if __name__ == "__main__":
    unittest.main()
