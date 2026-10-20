"""Unit tests for rigprocs (Slice 5's split-process topology, pure parts): python -m unittest Tools/ServerMeshing/test_rigprocs.py"""
import unittest

import rigprocs

EXE = r"D:\rig\Build\Server\kit-ce-project.exe"
LOGS = r"D:\rig\Logs"


class ArgumentTests(unittest.TestCase):
    def test_central_args(self):
        args = rigprocs.central_args(LOGS, "splitcentral", 2)
        self.assertEqual(args[:5], ["-batchmode", "-nographics", "-startDatabaseServer", "-startCentralServer", "-startLoginServer"])
        for flag, value in (("-centralPort", "7000"), ("-clusterPort", "6010"), ("-loginPort", "7500"), ("-databaseManagerPort", "6100"),
                            ("-databaseManagerAddress", "127.0.0.1"), ("-publicAddress", "127.0.0.1")):
            self.assertEqual(args[args.index(flag) + 1], value, flag)
        self.assertEqual(args[args.index("-logFile") + 1], f"{LOGS}/regress_splitcentral_central2.log")
        self.assertNotIn("-startMapSpawnServer", args)

    def test_spawner_args_pass_the_maps_as_one_pipe_separated_token(self):
        args = rigprocs.spawner_args(EXE, LOGS, "splitcentral")
        self.assertIn("-startMapSpawnServer", args)
        self.assertEqual(args[args.index("-spawnMaps") + 1], "ServerMeshTestMap|ServerMeshTestMap2")
        self.assertEqual(args[args.index("-spawnExePath") + 1], EXE)
        self.assertEqual(args[args.index("-spawnStartPort") + 1], "8000")
        self.assertEqual(args[args.index("-mapSpawnPort") + 1], "6001")
        self.assertEqual(args[args.index("-centralAddress") + 1], "127.0.0.1")
        self.assertNotIn("-startCentralServer", args)
        self.assertEqual(args.count("ServerMeshTestMap"), 0, "two tokens would start the test map only")


class SnippetTests(unittest.TestCase):
    def test_kill_and_count_snippets_filter_on_the_exe_and_the_flag(self):
        kill = rigprocs.kill_by_argument_script(EXE, "-startCentralServer")
        count = rigprocs.count_by_argument_script(EXE, "-startCentralServer")
        for script in (kill, count):
            self.assertIn("Name='kit-ce-project.exe'", script)
            self.assertIn("$_.ExecutablePath -eq '" + EXE + "'", script)
            self.assertIn("-like '* -startCentralServer *'", script)
        self.assertIn("Stop-Process", kill)
        self.assertNotIn("Stop-Process", count)
        self.assertTrue(count.endswith(".Count"))
        self.assertTrue(kill.endswith("$p.Count"))

    def test_port_snippet_and_counts(self):
        self.assertEqual(rigprocs.port_bound_script(7500), "@(Get-NetUDPEndpoint -LocalPort 7500 -ErrorAction Ignore).Count")
        self.assertEqual(rigprocs.parse_count(" 3\n"), 3)
        with self.assertRaises(ValueError):
            rigprocs.parse_count("Get-CimInstance : error")

    def test_command_line_flag_match(self):
        line = "kit-ce-project.exe -batchmode -nographics -startDatabaseServer -startCentralServer -startLoginServer -centralPort 7000"
        self.assertTrue(rigprocs.command_line_has_flag(line, "-startCentralServer"))
        self.assertTrue(rigprocs.command_line_has_flag(line, "-startcentralserver"))
        self.assertFalse(rigprocs.command_line_has_flag(line, "-startMapSpawnServer"))
        self.assertFalse(rigprocs.command_line_has_flag("x -startCentralServerX y", "-startCentralServer"))


class LogTests(unittest.TestCase):
    def test_kit_log_path(self):
        self.assertEqual(rigprocs.kit_log_path(LOGS, rigprocs.CENTRAL_LOG_PREFIX, "2026-10-19", "info"),
                         f"{LOGS}/Log_Database_Central_Login_2026-10-19.info.log")
        self.assertEqual(rigprocs.SPAWNER_LOG_PREFIX, "Log_MapSpawn")

    def test_summary_of(self):
        lines = [" INFO Mesh [2026-10-19 10:05:00] - Coordinator 1a2c: Dynamic maps ServerMeshTestMap AdoptStored; store D:/x/Temp/ServerMesh.Coordinator.json (loaded); broadcast 1a2b/4"]
        self.assertEqual(rigprocs.summary_of(lines), ("1a2c", "ServerMeshTestMap AdoptStored", "loaded", "1a2b/4"))
        self.assertIsNone(rigprocs.summary_of(["noise"]))


if __name__ == "__main__":
    unittest.main()
