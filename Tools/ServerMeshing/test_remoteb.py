"""Unit tests for remoteb's pure parts (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py").

Everything else talks to machine B over SSH and is exercised by the two-machine runs, not here.
"""
import unittest

import remoteb

DIR = r"C:\MeshTest\Server"


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


class ProcessFilterTests(unittest.TestCase):
    def test_cells_and_the_sink_are_told_apart(self):
        cases = {
            "a cell has no sink flag": (remoteb.IS_CELL, "-notlike '*-meshPhantomSink*'"),
            "a sink has the sink flag": (remoteb.IS_SINK, "-like '*-meshPhantomSink*'"),
            "stopping cells takes their launch scripts": (remoteb.STOP_CELLS, "'*start-cell-*'"),
            "stopping the sink takes its launch script": (remoteb.STOP_SINK, "'*start-sink*'"),
        }
        for name, (test, wanted) in cases.items():
            with self.subTest(name):
                self.assertIn(wanted, test)
        self.assertNotIn("start-sink", remoteb.STOP_CELLS)
        self.assertNotIn("start-cell-", remoteb.STOP_SINK)

    def test_stop_command_spares_its_own_session_and_counts_what_remains(self):
        command = remoteb.stop_command(remoteb.STOP_SINK, remoteb.IS_SINK)
        self.assertTrue(command.startswith(remoteb.NOT_THIS_SESSION + " -and (" + remoteb.STOP_SINK + ") }"))
        self.assertTrue(command.endswith(remoteb.count_command(remoteb.IS_SINK)))


if __name__ == "__main__":
    unittest.main()
