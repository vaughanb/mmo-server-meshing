"""Unit tests for abregrade (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py").

The chain3 fixture (fixtures/abload_chain3_20260925) ships only in the repository: the add-on's tools snapshot copies
.py and .md files, so outside the repository the fixture tests skip, and inside it a missing fixture fails.
"""
import datetime
import os
import re
import shutil
import tempfile
import unittest
from unittest import mock

import abload
import abregrade
import regress

HERE = os.path.dirname(os.path.abspath(__file__))
FIXTURE = os.path.join(HERE, "fixtures", "abload_chain3_20260925")
IN_REPOSITORY = os.path.normcase(HERE) == os.path.normcase(os.path.join(regress.ROOT, "Tools", "ServerMeshing"))
NO_GO_NO_GO = "no go/no-go: it needs both coarse and halves, with spread load"
CHAIN3 = {
    "abload_ab_20260925_142623.txt": ("halves/coarse = 0.55 0.60 0.62 ", "go/no-go (halves, spread load, every point): GO"),
    "abload_ab_20260925_143808.txt": ("halves/coarse = 0.60 0.64 ", "go/no-go (halves, spread load, every point): GO"),
    "abload_ab_20260925_144600.txt": ("halves/coarse = 0.95 1.02 ", NO_GO_NO_GO),
    "abload_ab_20260925_145351.txt": ("halves/coarse: no usable coarse run", NO_GO_NO_GO),
    "abload_ab_20260925_150142.txt": ("grid (1/1): plan ", NO_GO_NO_GO),
}
HEADER = "abload ab on HOST, CPU (2026-09-25 14:14)"
AB_HEADER = "=== static A/B, capped, load {load}, band {band}, 2 repeat(s) (seeds 1..2), warmup 45s measure 60s"
AB_RUNS = [
    "--- 250 players, monsters all, repeat 1 (seed 1), coarse (1/2): plan C:250",
    "    peak C 13.74 ms | C 13.74 ms sat 12% own 250p/284m pup 0p/0m | 14:15:35",
    "--- 250 players, monsters all, repeat 1 (seed 1), halves (2/2): plan C:125,E:125",
    "    discarded: E spawned nothing, planned 125",
    "--- 250 players, monsters all, repeat 2 (seed 2), halves (1/2): plan C:125,E:125",
    "    peak C 7.75 ms | C 7.75 ms sat 25% own 125p/142m pup 13p/23m, E 7.52 ms sat 0% own 125p/143m pup 6p/8m "
    "| 14:19:29 SATURATED C",
    "--- 250 players, monsters all, repeat 2 (seed 2), coarse (2/2): plan C:250",
    "    run failed: boom",
]
PROBE_REPORT = [
    "abload probe on HOST, CPU (2026-09-25 12:47)",
    "=== probe acceptance on cell C, running alone (burn = busy-wait per frame, capped); warmup 30s measure 30s",
    "burn ms  work ms  delta   wait ms  sat %  windows  probe          verdict",
    "0        0.32     0.00    16.35    0      5        Valid          ok",
    "4        4.36     4.04    12.31    0      5        Valid          ok",
    "12.5     (not usable: C logged 1 exception line(s))",
    "probe acceptance: PASS (budget 16.67 ms)",
]
OWN_LINE = (" INFO Mesh [2026-09-25 {time}] - Own cell from channel id: C of map ServerMeshTestMap, grid (0,-1), "
            "world x [-300, 300) z [-300, 300), 1 neighbours")
LOG_NAME = re.compile(r"^(.*)_\d{4}-\d\d-\d\d\.(\w+)\.log$")
LOG_STAMP = re.compile(r"\[(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)\]")


def ab_report(load="spread", band="20.0"):
    return [HEADER, AB_HEADER.format(load=load, band=band)] + AB_RUNS


def write(path, lines):
    with open(path, "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")


def default_args(command, **changes):
    args = vars(abload.build_parser().parse_args([command]))
    args.update(changes)
    return args


class ParseReportTests(unittest.TestCase):
    def test_refuses_other_text(self):
        cases = {
            "empty": [],
            "first line only": [HEADER],
            "regress report": ["regress on HOST", "=== pet: PASS"],
            "ab header changed": [HEADER, AB_HEADER.format(load="spread", band="20.0").replace("capped", "uncapped")]
                                 + AB_RUNS,
            "ab report with no run line": [HEADER, AB_HEADER.format(load="spread", band="20.0")],
        }
        for name, lines in cases.items():
            with self.subTest(name):
                with self.assertRaises(abregrade.NotAReport) as caught:
                    abregrade.parse_report(lines)
                self.assertIsInstance(caught.exception, ValueError)

    def test_phase0_report_is_skipped(self):
        lines = [HEADER, AB_HEADER.format(load="spread", band="per point (--bands)"),
                 "=== phase 0: puppets kinematic, rates 60/0.03, bands 20,40, instruments on, order repeat-major, rows -"]
        with self.assertRaises(abregrade.SkipReport):
            abregrade.parse_report(lines + AB_RUNS)

    def test_ab_report(self):
        cases = {
            "band given": (ab_report(), {"band": 20.0, "load": "spread"}),
            "band as configured": (ab_report(band="as configured (80 m)"), {"band": 0.0, "load": "spread"}),
            "crowd": (ab_report(load="0,0,60"), {"band": 20.0, "load": "0,0,60"}),
            "crowd west of the origin": (ab_report(load="-100,0,50"), {"band": 20.0, "load": "-100,0,50"}),
        }
        for name, (lines, changes) in cases.items():
            with self.subTest(name):
                command, args, starts = abregrade.parse_report(lines)
                self.assertEqual(command, "ab")
                self.assertEqual(vars(args), default_args("ab", players="250", monsters="all", layouts="coarse,halves",
                                                          repeats=2, seed=1, warmup=45, measure=60, **changes))
                self.assertEqual(starts, ["14:15:35", None, "14:19:29", None])

    def test_probe_report(self):
        command, args, starts = abregrade.parse_report(PROBE_REPORT)
        self.assertEqual(command, "probe")
        self.assertEqual(vars(args), default_args("probe", burns="0,4,12.5", cell="C", warmup=30, measure=30))
        self.assertEqual(starts, [None, None, None])


class LogsTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.dir)
        self.logs = abregrade.CellLogs(self.dir)

    def log(self, cell, suffix, lines):
        write(self.logs.path(cell, suffix, "2026-09-25", regress.MAP), lines)

    def test_cell_logs(self):
        self.log("C", "info", [" INFO Mesh [2026-09-25 14:00:05] - b", "   at a stack frame with no stamp",
                               " INFO Mesh [2026-09-25 14:00:09] - d"])
        self.log("C", "error", ["ERROR Mesh [2026-09-25 14:00:01] - a", "ERROR Mesh [2026-09-25 14:00:07] - c"])
        self.assertEqual([line[-1] for _, line in self.logs.lines("C", regress.MAP, "2026-09-25")], ["a", "b", "c", "d"])
        self.assertEqual(self.logs.lines("E", regress.MAP, "2026-09-25"), [])
        self.assertEqual(self.logs.touched, [("C", regress.MAP, "2026-09-25"), ("E", regress.MAP, "2026-09-25")])
        self.assertEqual(abregrade.read_stamped(os.path.join(self.dir, "missing.log")), [])

    def test_sessions_refuse(self):
        self.log("C", "info", [OWN_LINE.format(time="14:10:00"), OWN_LINE.format(time="14:14:52"),
                               OWN_LINE.format(time="14:16:49")])
        args = abload.build_parser().parse_args(["ab"])
        cases = {
            "first line without a start time": (["abload ab on HOST", "=== static"], [None], "has no start time"),
            "fewer sessions than runs": ([HEADER], [None, None, None],
                                         "cell C has 2 Play session(s) after 2026-09-25 14:14:00"),
        }
        for name, (lines, starts, message) in cases.items():
            with self.subTest(name):
                with self.assertRaisesRegex(ValueError, re.escape(message)):
                    abregrade.sessions(self.logs, lines, args, starts)

    def test_sessions(self):
        self.log("C", "info", [OWN_LINE.format(time="14:10:00"), OWN_LINE.format(time="14:14:52"),
                               OWN_LINE.format(time="14:16:49")])
        args = abload.build_parser().parse_args(["ab", "--warmup=45", "--measure=60"])
        day = datetime.date(2026, 9, 25)

        def at(hms):
            return datetime.datetime.combine(day, datetime.time.fromisoformat(hms))

        self.assertEqual(abregrade.sessions(self.logs, [HEADER], args, ["14:15:35", None]), [
            (at("14:14:49"), at("14:16:46"), at("14:15:35"), at("14:16:35")),
            (at("14:16:46"), at("14:26:49"), at("14:17:32"), at("14:18:32")),
        ])

    def test_sessions_across_midnight(self):
        self.log("C", "info", [OWN_LINE.format(time="23:58:00"), OWN_LINE.format(time="23:59:50")])
        write(self.logs.path("C", "info", "2026-09-26", regress.MAP),
              [OWN_LINE.replace("2026-09-25", "2026-09-26").format(time="00:01:40")])
        args = abload.build_parser().parse_args(["ab", "--warmup=45", "--measure=60"])

        def at(day, hms):
            return datetime.datetime.combine(datetime.date(2026, 9, day), datetime.time.fromisoformat(hms))

        header = HEADER.replace("14:14", "23:57")
        self.assertEqual(abregrade.sessions(self.logs, [header], args, ["23:58:46", "00:00:33", None]), [
            (at(25, "23:57:57"), at(25, "23:59:47"), at(25, "23:58:46"), at(25, "23:59:46")),
            (at(25, "23:59:47"), at(26, "00:01:37"), at(26, "00:00:33"), at(26, "00:01:33")),
            (at(26, "00:01:37"), at(26, "00:11:40"), at(26, "00:02:23"), at(26, "00:03:23")),
        ])

    def test_session_cell(self):
        parser = abload.build_parser()
        with self.assertRaisesRegex(ValueError, "names cell Q"):
            abregrade.session_cell(parser.parse_args(["probe", "--cell=Q"]))
        cases = {
            "ab": (["ab"], ("C", regress.MAP)),
            "probe on C": (["probe"], ("C", regress.MAP)),
            "probe on e": (["probe", "--cell=e"], ("E", regress.MAP)),
            "probe on C2": (["probe", "--cell=C2"], ("C2", regress.MAP2)),
        }
        for name, (argv, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(abregrade.session_cell(parser.parse_args(argv)), expected)


class TextTests(unittest.TestCase):
    def test_diff_lines(self):
        cases = {
            "one line differs": (["a", "b", "c"], ["a", "x", "c"], [(1, "b", "x")]),
            "replay shorter": (["a", "b", "c"], ["a", "b"], [(2, "<3 lines>", "<2 lines>")]),
            "identical": (["a", "b"], ["a", "b"], []),
        }
        for name, (saved, replayed, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(abregrade.diff_lines(saved, replayed), expected)

    def test_verdict_line(self):
        cases = {
            "none": (["a", "b"], "?"),
            "go/no-go": (["go/no-go (halves, spread load, every point): GO", "", "written"],
                         "go/no-go (halves, spread load, every point): GO"),
            "no go/no-go": ([NO_GO_NO_GO], NO_GO_NO_GO),
            "probe": (PROBE_REPORT, "probe acceptance: PASS (budget 16.67 ms)"),
        }
        for name, (lines, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(abregrade.verdict_line(lines), expected)


class FixtureTests(unittest.TestCase):
    def setUp(self):
        if not IN_REPOSITORY:
            self.skipTest("the chain3 fixture ships only in the repository")
        if not os.path.isdir(FIXTURE):
            self.fail(f"the chain3 fixture is missing: {FIXTURE}")
        self.tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.tmp)
        self.reports = [os.path.join(FIXTURE, name) for name in CHAIN3]

    def changed_report(self):
        saved = abregrade.read_report(self.reports[0])
        path = os.path.join(self.tmp, os.path.basename(self.reports[0]))
        write(path, [line.replace("halves/coarse = 0.55", "halves/coarse = 0.56") for line in saved])
        return path

    def test_a_changed_report_differs(self):
        lines, code = abregrade.replay_reports(FIXTURE, [self.changed_report()])
        self.assertEqual(code, 1)
        self.assertEqual(lines[0], "abload_ab_20260925_142623.txt: 1 line(s) differ, exit 0")
        self.assertTrue(lines[2].startswith("      saved:    ") and "0.56" in lines[2])
        self.assertTrue(lines[3].startswith("      replayed: ") and "0.55" in lines[3])

    def test_explicit_text_that_is_not_a_report_fails(self):
        path = os.path.join(self.tmp, "abload_notes.txt")
        write(path, ["notes", "nothing to replay"])
        lines, code = abregrade.replay_reports(FIXTURE, [path])
        self.assertEqual((lines, code), (["abload_notes.txt: cannot replay: not an abload 0.71 ab or probe report: "
                                          "'nothing to replay'"], 1))

    def test_rig_calls_are_refused_during_a_replay(self):
        def driving_command(args, rep):
            regress.request_play("none", 0, 0, env={}, enabled=False)

        with mock.patch.object(abload, "ab_command", driving_command):
            with self.assertRaisesRegex(RuntimeError, "never drives the rig"):
                abregrade.replay(abregrade.read_report(self.reports[0]), abregrade.CellLogs(FIXTURE))

    def test_fixture_refuses(self):
        cases = {
            "logs folder as output": (FIXTURE, self.reports, "is the logs folder"),
            "report that does not replay identically": (os.path.join(self.tmp, "out"), [self.changed_report()],
                                                        "does not replay IDENTICAL"),
        }
        for name, (out_dir, reports, message) in cases.items():
            with self.subTest(name):
                with self.assertRaisesRegex(ValueError, message):
                    abregrade.fixture(out_dir, reports, FIXTURE)
        self.assertFalse(os.path.exists(os.path.join(self.tmp, "out")))

    def test_chain3_reports_replay_identical(self):
        logs = abregrade.CellLogs(FIXTURE)
        for name, (text, verdict) in CHAIN3.items():
            with self.subTest(name):
                saved = abregrade.read_report(os.path.join(FIXTURE, name))
                code, replayed, _ = abregrade.replay(saved, logs)
                self.assertEqual(abregrade.diff_lines(saved, replayed), [])
                self.assertEqual(code, 0)
                self.assertTrue(any(text in line for line in replayed), text)
                self.assertEqual(abregrade.verdict_line(replayed), verdict)

    def test_replay_reports_over_the_fixture(self):
        lines, code = abregrade.replay_reports(FIXTURE)
        self.assertEqual(lines, [f"{name}: IDENTICAL, exit 0; {verdict}" for name, (_, verdict) in CHAIN3.items()])
        self.assertEqual(code, 0)

    def test_skipped_reports_do_not_count(self):
        logs_dir = os.path.join(self.tmp, "logs")
        shutil.copytree(FIXTURE, logs_dir)
        write(os.path.join(logs_dir, "abload_ab_29990101_000000.txt"),
              [HEADER, AB_HEADER.format(load="spread", band="per point (--bands)"), "=== phase 0: puppets kinematic"]
              + AB_RUNS)
        write(os.path.join(logs_dir, "abload_notes.txt"), ["notes", "nothing to replay"])
        lines, code = abregrade.replay_reports(logs_dir)
        self.assertEqual(code, 0)
        self.assertEqual(lines[5:], [
            "abload_ab_29990101_000000.txt: skipped (written with phase 0 flags; its JSONL rows carry window_local)",
            "abload_notes.txt: skipped (not an abload 0.71 ab or probe report: 'nothing to replay')",
        ])

    def shifted_fixture(self, delta):
        """The first chain3 report and the fixture's cell logs moved by delta, each log line into its new day's file."""
        out, files = os.path.join(self.tmp, "shifted"), {}
        os.makedirs(out)
        for name in os.listdir(FIXTURE):
            head = LOG_NAME.match(name)
            with open(os.path.join(FIXTURE, name), encoding="utf-8") as f:
                for line in f if head else ():
                    stamp = LOG_STAMP.search(line)
                    t = datetime.datetime.fromisoformat(stamp.group(1)) + delta
                    files.setdefault(f"{head.group(1)}_{t:%Y-%m-%d}.{head.group(2)}.log", []).append(
                        f"{line[:stamp.start(1)]}{t:%Y-%m-%d %H:%M:%S}{line[stamp.end(1):]}")
        for name, lines in files.items():
            with open(os.path.join(out, name), "w", encoding="utf-8") as f:
                f.writelines(lines)
        saved = abregrade.read_report(self.reports[0])
        header = abregrade.HEADER_TIME.search(saved[0])
        start = datetime.datetime.strptime(header.group(1), "%Y-%m-%d %H:%M") + delta
        saved[0] = f"{saved[0][:header.start(1)]}{start:%Y-%m-%d %H:%M}{saved[0][header.end(1):]}"
        for i, line in enumerate(saved):
            peak = abregrade.PEAK_START.match(line)
            if peak:
                t = datetime.datetime.combine(datetime.date(2026, 9, 25), datetime.time.fromisoformat(peak.group(1)))
                saved[i] = f"{line[:peak.start(1)]}{t + delta:%H:%M:%S}{line[peak.end(1):]}"
        path = os.path.join(out, "abload_ab_shifted.txt")
        write(path, saved)
        return out, path, sorted(files)

    def test_a_report_across_midnight_replays_identical(self):
        verdict = CHAIN3["abload_ab_20260925_142623.txt"][1]
        for name, delta, days in (("same day", datetime.timedelta(hours=1), 1),
                                  ("across midnight", datetime.timedelta(hours=9, minutes=41), 2)):
            with self.subTest(name):
                out, path, files = self.shifted_fixture(delta)
                self.assertEqual(len({re.search(r"_(\d{4}-\d\d-\d\d)\.", f).group(1) for f in files}), days)
                self.assertEqual(abregrade.replay_reports(out, [path]),
                                 ([f"abload_ab_shifted.txt: IDENTICAL, exit 0; {verdict}"], 0))
                shutil.rmtree(out)

    def test_fixture_of_the_fixture_is_the_fixture(self):
        out_dir = os.path.join(self.tmp, "again")
        written = abregrade.fixture(out_dir, self.reports, FIXTURE)
        self.assertEqual(sorted(os.path.basename(path) for path in written), sorted(os.listdir(FIXTURE)))
        for path in written:
            with self.subTest(os.path.basename(path)):
                self.assertEqual(abregrade.read_report(path),
                                 abregrade.read_report(os.path.join(FIXTURE, os.path.basename(path))))
        self.assertEqual(abregrade.replay_reports(out_dir)[1], 0)


if __name__ == "__main__":
    unittest.main()
