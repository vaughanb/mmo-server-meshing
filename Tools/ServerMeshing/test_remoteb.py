"""Unit tests for remoteb's pure parts (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py").

Everything else talks to machine B over SSH: here only through a stand-in for remotessh.ssh, launch_detached and
scp (never a real machine); the two-machine runs exercise the rest.
"""
import datetime
import hashlib
import os
import subprocess
import tempfile
import unittest
from unittest import mock

import remoteb
import remotebots
import remoteimpair
import remotessh

DIR = r"C:\MeshTest\Server"
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


class SinkArgumentsTests(unittest.TestCase):
    def test_sink_arguments_refuses(self):
        cases = {
            "port 0": (("192.168.50.83", 0, 50, None), "sink port 0 is not from 1 to 65535"),
            "port above 65535": (("192.168.50.83", 65536, 50, None), "sink port 65536 is not from 1 to 65535"),
            "count 0": (("192.168.50.83", 7770, 0, None), "sink count 0 is not at least 1"),
            "base 0": (("192.168.50.83", 7770, 50, 0), "sink port base 0 is not from 1 to 65535"),
            "base above 65535": (("192.168.50.83", 7770, 50, 70000), "sink port base 70000 is not from 1 to 65535"),
        }
        for name, ((a_ip, port, count, base), message) in cases.items():
            with self.subTest(name):
                with self.assertRaises(ValueError) as caught:
                    remoteb.sink_arguments(a_ip, port, count, base, DIR)
                self.assertEqual(str(caught.exception), message)

    def test_sink_arguments(self):
        log = r"-logFile C:\MeshTest\Server\Logs\sink.log"
        cases = {
            "50 peers": ((7770, 50, None), f"-batchmode -nographics -meshPhantomSink 192.168.50.83:7770:50 {log}"),
            "100 peers": ((7770, 100, None), f"-batchmode -nographics -meshPhantomSink 192.168.50.83:7770:100 {log}"),
            "400 peers": ((7771, 400, None), f"-batchmode -nographics -meshPhantomSink 192.168.50.83:7771:400 {log}"),
            "50 peers from a port base": (
                (7770, 50, 9000), f"-batchmode -nographics -meshPhantomSink 192.168.50.83:7770:50:9000 {log}"),
            "100 peers from a port base": (
                (7770, 100, 9000), f"-batchmode -nographics -meshPhantomSink 192.168.50.83:7770:100:9000 {log}"),
            "400 peers from a port base": (
                (7770, 400, 20000), f"-batchmode -nographics -meshPhantomSink 192.168.50.83:7770:400:20000 {log}"),
        }
        for name, ((port, count, base), expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(remoteb.sink_arguments("192.168.50.83", port, count, base, DIR), expected)


    def test_sink_arguments_with_sizes(self):
        log = r"-logFile C:\MeshTest\Server\Logs\sink.log"
        cases = {
            "sizes": ((7770, 20, None, True),
                      f"-batchmode -nographics -meshPhantomSink 192.168.50.83:7770:20 -meshPhantomSinkSizes 1 {log}"),
            "sizes from a port base": (
                (7770, 20, 9000, True),
                f"-batchmode -nographics -meshPhantomSink 192.168.50.83:7770:20:9000 -meshPhantomSinkSizes 1 {log}"),
            "no sizes": ((7770, 20, None, False), f"-batchmode -nographics -meshPhantomSink 192.168.50.83:7770:20 {log}"),
        }
        for name, ((port, count, base, sizes), expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(remoteb.sink_arguments("192.168.50.83", port, count, base, DIR, sizes), expected)

    def test_a_sink_with_sizes_still_counts_as_a_sink(self):
        self.assertIn(remoteb.SINK_FLAG, remoteb.SINK_SIZES_FLAG)


class ProcessFilterTests(unittest.TestCase):
    def test_cells_and_the_sink_are_told_apart(self):
        cases = {
            "a cell has no sink flag": (remoteb.IS_CELL, "-notlike '*-meshPhantomSink*'"),
            "a cell has no bot flag": (remoteb.IS_CELL, "-notlike '*-meshAutoClient*'"),
            "a sink has the sink flag": (remoteb.IS_SINK, "-like '*-meshPhantomSink*'"),
            "a bot has the auto client's flag": (remoteb.IS_BOT, "-like '*-meshAutoClient*'"),
            "stopping cells takes their launch scripts": (remoteb.STOP_CELLS, "'*start-cell-*'"),
            "stopping the sink takes its launch script": (remoteb.STOP_SINK, "'*start-sink*'"),
            "stopping bots takes their launch script": (remoteb.STOP_BOTS, "'*start-bots*'"),
            "stopping bots takes every bot": (remoteb.STOP_BOTS, remoteb.IS_BOT),
        }
        for name, (test, wanted) in cases.items():
            with self.subTest(name):
                self.assertIn(wanted, test)
        for stop, others in ((remoteb.STOP_CELLS, ("start-sink", "start-bots")),
                             (remoteb.STOP_SINK, ("start-cell-", "start-bots")),
                             (remoteb.STOP_BOTS, ("start-cell-", "start-sink"))):
            for other in others:
                self.assertNotIn(other, stop)

    def test_stop_command_spares_its_own_session_and_counts_what_remains(self):
        command = remoteb.stop_command(remoteb.STOP_SINK, remoteb.IS_SINK)
        self.assertTrue(command.startswith(remoteb.NOT_THIS_SESSION + " -and (" + remoteb.STOP_SINK + ") }"))
        self.assertTrue(command.endswith(remoteb.count_command(remoteb.IS_SINK)))


A_IP = "192.168.50.83"
BOT_LOG = r"-logFile C:\MeshTest\Server\Logs\bots"


def ran(outputs, commands=None):
    """A stand-in for remotessh.ssh answering each command with the next output, recording the commands."""
    answers = list(outputs)

    def fake(args, command, timeout=120):
        if commands is not None:
            commands.append(command)
        return answers.pop(0), "", 0
    return fake


class BotArgumentsTests(unittest.TestCase):
    def test_bot_arguments_refuses(self):
        cases = {
            "the regress fixture": (("meshbot", "stand", 0, 0), "user 'meshbot' is not meshbot<K>"),
            "meshbot1": (("meshbot1", "stand", 0, 0), "meshbot1: step 8b's bots are meshbot2 and up"),
            "another account": (("meshtest", "stand", 0, 0), "user 'meshtest' is not meshbot<K>"),
            "an unknown mode": (("meshbot2", "run", 0, 0), "mode 'run' is not one of none, walk"),
            "a walk x that is no number": (("meshbot2", "stand", float("nan"), 0), "walk x nan is not a finite number"),
            "a walk z that is text": (("meshbot2", "stand", 0, "0"), "walk z '0' is not a finite number"),
        }
        for name, ((user, mode, x, z), message) in cases.items():
            with self.subTest(name), self.assertRaisesRegex(ValueError, message):
                remotebots.bot_arguments(user, mode, x, z, None, A_IP, DIR)
        with self.assertRaisesRegex(ValueError, "bot net seconds 0 is not above 0"):
            remotebots.bot_arguments("meshbot2", "pace", 0, 0, None, A_IP, DIR, bot_net=0)

    def test_bot_arguments(self):
        head = "-batchmode -nographics -meshAutoClient"
        login = f"-meshLoginAddress {A_IP}:7500"
        cases = {
            "stand at a point": (("meshbot2", "stand", -30, -40, None, None, None),
                                 f"{head} -meshUser meshbot2 -meshChannel C -meshAutoMode stand -meshWalkX -30 "
                                 f"-meshWalkZ -40 {login} {BOT_LOG}\\meshbot2.log"),
            "walk across and back": (("meshbot17", "walk", 30.0, 12.5, -30.0, None, None),
                                     f"{head} -meshUser meshbot17 -meshChannel C -meshAutoMode walk -meshWalkX 30 "
                                     f"-meshWalkZ 12.5 -meshReturnX -30 {login} {BOT_LOG}\\meshbot17.log"),
            "pace with the net window and ships": (
                ("meshbot101", "pace", -4, 0.1234, None, 5, "compact28:12345:0.5"),
                f"{head} -meshUser meshbot101 -meshChannel C -meshAutoMode pace -meshWalkX -4 -meshWalkZ 0.123 "
                f"-meshBotNetSeconds 5 -meshRigShips compact28:12345:0.5 {login} {BOT_LOG}\\meshbot101.log"),
        }
        for name, ((user, mode, x, z, back, net, ships), expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(remotebots.bot_arguments(user, mode, x, z, back, A_IP, DIR, net, ships), expected)

    def test_bots_script_refuses(self):
        cases = {"a negative stagger": (["a"], -1), "a fractional stagger": (["a"], 1.5), "no bots": ([], 1)}
        for name, (arguments, stagger) in cases.items():
            with self.subTest(name), self.assertRaises(ValueError):
                remotebots.bots_script(DIR, arguments, stagger)

    def test_bots_script(self):
        head = ["@echo off", f'cd /d "{DIR}"', "if not exist Logs\\bots mkdir Logs\\bots", "del /q Logs\\bots\\*.log 2>nul"]
        cases = {
            "one bot": ((["a"], 1), head + ['start "" /b kit-ce-project.exe a']),
            "staggered by 2 s": ((["a", "b", "c"], 2), head + ['start "" /b kit-ce-project.exe a', "timeout /t 2 /nobreak >nul",
                                                            'start "" /b kit-ce-project.exe b', "timeout /t 2 /nobreak >nul",
                                                            'start "" /b kit-ce-project.exe c']),
            "no stagger": ((["a", "b"], 0), head + ['start "" /b kit-ce-project.exe a', 'start "" /b kit-ce-project.exe b']),
        }
        for name, ((arguments, stagger), expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(remotebots.bots_script(DIR, arguments, stagger), expected)


class StartBotsTests(unittest.TestCase):
    ARGS = remotessh.defaults(host="b", a_ip=A_IP, dir=DIR)
    POINTS = [(-30.0, -40.0), (-30.0, 40.0)]

    def start(self, running="0", launched="rc=0 pid=42", points=POINTS):
        scripts = []

        def launch(args, name, body):
            scripts.append((name, body))
            return launched
        with mock.patch.object(remotessh, "ssh", ran([running])), \
                mock.patch.object(remotessh, "launch_detached", launch):
            return remotebots.start_bots(self.ARGS, 2, 2, "stand", points, stagger=1), scripts

    def test_start_bots_refuses(self):
        cases = {
            "fewer points than bots": ({"points": self.POINTS[:1]}, ValueError, "1 points for 2 bots"),
            "bots already running": ({"running": "3"}, RuntimeError, "B already runs 3 bot"),
            "the script not copied": ({"launched": None}, RuntimeError, "could not copy start-bots.cmd"),
            "a failed create": ({"launched": "rc=9 pid="}, RuntimeError, "did not start: rc=9"),
            "a count that is no number": ({"running": "Access denied"}, RuntimeError, "not a count"),
        }
        for name, (kwargs, error, message) in cases.items():
            with self.subTest(name), self.assertRaisesRegex(error, message):
                self.start(**kwargs)

    def test_start_bots(self):
        launched, scripts = self.start()
        self.assertEqual(launched, "rc=0 pid=42")
        self.assertEqual([name for name, _ in scripts], ["start-bots.cmd"])
        starts = [line for line in scripts[0][1] if line.startswith("start ")]
        self.assertEqual(len(starts), 2)
        self.assertIn("-meshUser meshbot2 ", starts[0])
        self.assertIn("-meshUser meshbot3 -meshChannel C -meshAutoMode stand -meshWalkX -30 -meshWalkZ 40 ", starts[1])

    def test_stop_and_count(self):
        commands = []
        with mock.patch.object(remotessh, "ssh", ran(["0", "2"], commands)):
            self.assertEqual(remotebots.stop_bots(self.ARGS), 0)
            self.assertEqual(remotebots.bot_count(self.ARGS), 2)
        self.assertEqual(commands, [remotessh.stop_command(remotessh.STOP_BOTS, remotessh.IS_BOT),
                                    remotessh.count_command(remotessh.IS_BOT)])

    def test_fetch_bots_empties_the_folder_first(self):
        with tempfile.TemporaryDirectory() as dest:
            open(os.path.join(dest, "meshbot9.log"), "w").close()

            def copied(argv, **kwargs):
                open(os.path.join(dest, "meshbot2.log"), "w").close()
                return mock.Mock(returncode=0, stderr="")
            with mock.patch.object(remotebots.subprocess, "run", copied):
                self.assertEqual(remotebots.fetch_bots(self.ARGS, dest), [os.path.join(dest, "meshbot2.log")])
            with mock.patch.object(remotebots.subprocess, "run", lambda argv, **kw: mock.Mock(returncode=1, stderr="no match")):
                with self.assertRaisesRegex(RuntimeError, "no match"):
                    remotebots.fetch_bots(self.ARGS, dest)
            self.assertEqual(os.listdir(dest), [])

    def test_in_map(self):
        with tempfile.TemporaryDirectory() as folder:
            cases = {"no log": (None, False), "logged in only": ("[MeshAuto] Logged in as 'meshbot2'\n", False),
                     "in the map": ("[MeshAuto] Logged in\n[MeshAuto] In map at (-30.0, 0.1, 40.0)\n", True)}
            for name, (text, expected) in cases.items():
                with self.subTest(name):
                    path = os.path.join(folder, name + ".log")
                    if text is not None:
                        with open(path, "w", encoding="utf-8") as f:
                            f.write(text)
                    self.assertEqual(remotebots.in_map(path), expected)


STATE_TEXT = "\n".join(["cells=0", "bots=3", "sinks=1", "relics=0", "relic_service=Stopped",
                        "clock=2026-10-07T09:01:02.1234567-07:00", "host=MESHB", "cpu=AMD Ryzen 9 7900X", "free_mb=40960"])


class StateTests(unittest.TestCase):
    def test_parse_clock(self):
        cases = {
            "empty": ("", None),
            "not a time": ("yesterday", None),
            "no zone": ("2026-10-07T09:01:02.12", None),
            "seven digits and an offset": ("2026-10-07T09:01:02.1234567-07:00",
                                           datetime.datetime(2026, 10, 7, 16, 1, 2, 123456, datetime.timezone.utc)),
            "utc": ("2026-10-07T16:01:02Z", datetime.datetime(2026, 10, 7, 16, 1, 2, tzinfo=datetime.timezone.utc)),
        }
        for name, (text, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(remoteb.parse_clock(text), expected)

    def test_parse_state(self):
        state = remoteb.parse_state("noise\n" + STATE_TEXT)
        self.assertEqual({k: state[k] for k in ("cells", "bots", "sinks", "relics", "free_mb", "relic_service", "host",
                                                "cpu")},
                         {"cells": 0, "bots": 3, "sinks": 1, "relics": 0, "free_mb": 40960, "relic_service": "Stopped",
                          "host": "MESHB", "cpu": "AMD Ryzen 9 7900X"})
        self.assertEqual(state["clock"].utcoffset(), datetime.timedelta(hours=-7))
        empty = remoteb.parse_state("relic_service=\nbots=Access denied")
        self.assertEqual((empty["bots"], empty["cells"], empty["relic_service"], empty["clock"]), (None, None, "", None))

    def test_state_problems(self):
        base = remoteb.parse_state(STATE_TEXT)
        cases = {
            "counts not printed": (remoteb.parse_state(""), ["B printed no cells count", "B printed no relics count"]),
            "a cell": (dict(base, cells=1), ["B runs 1 cell(s); stop them: remoteb.py stop"]),
            "the user's RelicTestServer and Relics are no refusal (2026-10-16)": (
                dict(base, relic_service="Running", relics=2), []),
            "leftover bots and a sink are no refusal": (base, []),
        }
        for name, (state, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(remoteb.state_problems(state), expected)
        self.assertEqual(remoteb.state_notes(dict(base, relic_service="Running", relics=2)),
                         ["B runs RelicTestServer (the user's; not ours to stop)", "B runs 2 Relics process(es) (the user's)"])
        self.assertEqual(remoteb.state_notes(base), [])

    def test_cpu_sample_and_load_shares(self):
        command = remoteb.cpu_command(r"C:\MeshTest\Server")
        self.assertIn(r"$_.Path -like 'C:\MeshTest\Server\*'", command)
        self.assertNotIn('"', command)   # ssh wraps the command in double quotes
        cases = {  # printed line, parsed
            "dots": ("ours=12.5 other=300.25 logical=24", {"ours": 12.5, "other": 300.25, "logical": 24}),
            "a comma decimal": ("ours=12,5 other=3 logical=8", {"ours": 12.5, "other": 3.0, "logical": 8}),
            "a negative rest floors at 0": ("ours=5 other=-0.5 logical=8", {"ours": 5.0, "other": 0.0, "logical": 8}),
            "nothing usable": ("Access denied", None),
        }
        for name, (text, parsed) in cases.items():
            with self.subTest(name):
                self.assertEqual(remoteb.parse_cpu(text), parsed)
        before, after = {"ours": 10, "other": 100, "logical": 24}, {"ours": 130, "other": 340, "logical": 24}
        self.assertEqual(remoteb.load_shares(before, after, 100), {"ours": 0.05, "other": 0.1})
        self.assertIsNone(remoteb.load_shares(None, after, 100))
        self.assertIsNone(remoteb.load_shares(before, after, 0))

    def test_the_state_command_has_no_double_quote(self):
        self.assertNotIn('"', remoteb.STATE_COMMAND)
        for key in remoteb.STATE_INTS + ("relic_service", "clock", "host", "cpu"):
            self.assertIn(f"'{key}=' + ", remoteb.STATE_COMMAND)

    def test_status_lines(self):
        lines = remoteb.status_lines(remoteb.parse_state(STATE_TEXT))
        self.assertEqual(lines, ["process: not running (0 cell(s))", "bots: 3", "sinks: 1",
                                 "B clock: 2026-10-07T09:01:02.123456-07:00",
                                 "RelicTestServer: Stopped; Relics processes: 0"])
        running = remoteb.status_lines(dict(remoteb.parse_state(STATE_TEXT), cells=1, relic_service=""))
        self.assertTrue(running[0].startswith("process: running"))    # regress.remote_running reads this
        self.assertEqual(running[-1], "RelicTestServer: not installed; Relics processes: 0")

    def test_deployed_matches(self):
        with tempfile.TemporaryDirectory() as folder:
            dll = os.path.join(folder, "Assembly-CSharp.dll")
            with open(dll, "wb") as f:
                f.write(b"assembly")
            want = hashlib.sha256(b"assembly").hexdigest()
            cases = {"missing on B": ("missing", False), "another build": ("AB" * 32, False),
                     "the same build": (want.upper(), True)}
            for name, (answer, matches) in cases.items():
                with self.subTest(name), mock.patch.object(remotessh, "ssh", ran([answer])):
                    self.assertEqual(remoteb.deployed_matches(remotessh.defaults(), dll),
                                     (matches, want, answer.lower()))

    def test_the_command_line_has_the_bot_commands(self):
        args = remoteb.build_parser().parse_args(["bots-start", "--first", "2", "--n", "3", "--mode", "pace",
                                                  "--bot-net", "5", "--stagger", "2"])
        self.assertEqual((args.first, args.n, args.mode, args.bot_net, args.stagger, args.points),
                         (2, 3, "pace", 5.0, 2, None))
        for command in ("bots-start", "bots-stop", "bots-count", "bots-fetch", "status", "deploy"):
            self.assertIn(command, remoteb.COMMANDS)


FACTS = "admin=True\nclumsy=True"


class ImpairTests(unittest.TestCase):
    """Step 9 decision 12: clumsy's profiles, its launch script, and starting, counting and stopping it on B."""
    ARGS = remotessh.defaults(host="b", a_ip=A_IP, dir=DIR, clumsy=r"C:\MeshTest\clumsy")

    def test_profiles(self):
        cases = {
            "loss1 drops 1 %": ("loss1", ["--drop", "on", "--drop-chance", "1.0"]),
            "loss5 drops 5 %": ("loss5", ["--drop", "on", "--drop-chance", "5.0"]),
            "off does nothing": ("off", []),
        }
        for name, (profile, options) in cases.items():
            with self.subTest(name):
                self.assertEqual(remoteb.PROFILES[profile], options)
        self.assertIs(remoteb.PROFILES, remoteimpair.PROFILES)

    def test_impair_script(self):
        loss1, loss5 = (remoteimpair.impair_script(r"C:\MeshTest\clumsy", A_IP, p) for p in ("loss1", "loss5"))
        self.assertEqual(loss1, ["@echo off", r'cd /d "C:\MeshTest\clumsy"',
                                 f'clumsy.exe --filter "udp and (ip.DstAddr == {A_IP} or ip.SrcAddr == {A_IP})" '
                                 "--drop on --drop-chance 1.0"])
        self.assertEqual(loss1[:2] + [loss1[2].split('" ')[0]], loss5[:2] + [loss5[2].split('" ')[0]])  # one filter
        with self.assertRaises(KeyError):
            remoteimpair.impair_script(r"C:\MeshTest\clumsy", A_IP, "loss9")

    def test_facts(self):
        command = remoteimpair.facts_command(r"C:\MeshTest\clumsy")
        self.assertNotIn('"', command)
        self.assertIn(r"Test-Path 'C:\MeshTest\clumsy\clumsy.exe'", command)
        cases = {"nothing printed": ("", {"admin": False, "clumsy": False}),
                 "not elevated": ("admin=False\nclumsy=True", {"admin": False, "clumsy": True}),
                 "both": ("noise\n" + FACTS, {"admin": True, "clumsy": True})}
        for name, (text, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(remoteimpair.parse_facts(text), expected)
        self.assertEqual(remoteimpair.clumsy_dir(remotessh.defaults()), remoteimpair.CLUMSY_DIR)

    def start(self, answers, profile="loss1", launched="rc=0 pid=7"):
        commands, scripts = [], []

        def launch(args, name, body):
            scripts.append((name, body))
            return launched
        with mock.patch.object(remotessh, "ssh", ran(answers, commands)), \
                mock.patch.object(remotessh, "launch_detached", launch), \
                mock.patch.object(remoteimpair.time, "sleep", lambda s: None):
            return remoteimpair.start_impair(self.ARGS, profile), commands, scripts

    def test_start_impair_refuses(self):
        cases = {  # failure first: (answers, profile, launch output), error, message
            "an unknown profile": (([], "loss9", "x"), ValueError, "impair profile 'loss9' is not one of"),
            "off is no start": (([], "off", "x"), ValueError, "impair profile 'off'"),
            "not elevated": ((["admin=False\nclumsy=True"], "loss1", "x"), remoteimpair.NoAdmin, "not elevated"),
            "no clumsy.exe": ((["admin=True\nclumsy=False"], "loss1", "x"), RuntimeError, "no clumsy.exe in"),
            "the script not copied": (([FACTS, "0"], "loss1", None), RuntimeError, "could not copy impair.cmd"),
            "clumsy did not stay up": (([FACTS, "0", "0"], "loss1", "rc=0 pid=7"), RuntimeError,
                                       "clumsy did not stay up \\(rc=0 pid=7\\)"),
        }
        for name, ((answers, profile, launched), error, message) in cases.items():
            with self.subTest(name), self.assertRaisesRegex(error, message):
                self.start(answers, profile, launched)
        self.assertTrue(issubclass(remoteimpair.NoAdmin, RuntimeError))

    def test_start_impair(self):
        launched, commands, scripts = self.start([FACTS, "0", "1"])
        self.assertEqual(launched, "rc=0 pid=7")
        self.assertEqual(commands[1:], [remotessh.stop_command(remoteimpair.STOP_CLUMSY, remoteimpair.IS_CLUMSY),
                                        remotessh.count_command(remoteimpair.IS_CLUMSY)])
        self.assertEqual(scripts, [("impair.cmd", remoteimpair.impair_script(r"C:\MeshTest\clumsy", A_IP, "loss1"))])

    def test_stop_and_count(self):
        commands = []
        with mock.patch.object(remotessh, "ssh", ran(["1", "2"], commands)):
            self.assertEqual(remoteb.stop_impair(self.ARGS), 1)
            self.assertEqual(remoteb.clumsy_count(self.ARGS), 2)
        self.assertIn("'*impair.cmd*'", remoteimpair.STOP_CLUMSY)
        self.assertIn(remoteimpair.IS_CLUMSY, remoteimpair.STOP_CLUMSY)
        self.assertTrue(commands[0].startswith(remotessh.NOT_THIS_SESSION))

    def test_cmd_impair(self):
        cases = {  # failure first: (profile, stop_impair's count, start_impair's error), exit code
            "an unknown profile": (("loss9", 0, None), 2),
            "clumsy survives off": (("off", 1, None), 1),
            "no admin": (("loss1", 0, remoteimpair.NoAdmin("not elevated")), 1),
            "clumsy fails": (("loss1", 0, RuntimeError("did not stay up")), 1),
            "off": (("off", 0, None), 0),
            "on": (("loss1", 0, None), 0),
        }
        for name, ((profile, left, error), code) in cases.items():
            with self.subTest(name), mock.patch.object(remoteimpair, "stop_impair", lambda args: left), \
                    mock.patch.object(remoteimpair, "start_impair", mock.Mock(side_effect=error)), \
                    mock.patch("builtins.print"):
                args = remoteb.build_parser().parse_args(["impair", "--profile", profile])
                self.assertEqual(remoteb.cmd_impair(args), code)


if __name__ == "__main__":
    unittest.main()
