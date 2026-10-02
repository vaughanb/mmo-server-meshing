"""Unit tests for abload (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py").

The rig is never driven: runs go through a stand-in for rigrun.run_window / cell_window / cell_lines / host_context,
and the dry run and the refusals fail the test if anything reads the editor, PowerShell or the logs.
"""
import contextlib
import datetime
import io
import os
import shutil
import tempfile
import unittest
from unittest import mock

import abgrade
import abload
import p0checks
import p0plans
import p0results
import regress
import rigrun
import rigtools

SINCE = datetime.datetime(2026, 9, 28, 10, 0, 0)
START, END = SINCE + datetime.timedelta(seconds=47), SINCE + datetime.timedelta(seconds=107)
CONTEXT = {"host": "HOST", "cpu": "CPU", "cores": 12, "logical": 24, "os": "Windows", "power_plan": "Balanced",
           "build": "abc", "kit_patches": {"CE-6": "present"}}
SMALL_AB = ["ab", "--players", "100", "--monsters", "off", "--layouts", "coarse,halves", "--repeats", "2"]
RIG_READS = (rigrun.start_problems, rigrun.cpu_name, rigrun.host_context, rigrun.run_window, rigrun.cell_window,
             rigrun.cell_lines)


def refuse(*args, **kwargs):
    raise AssertionError("the rig, PowerShell or the logs were read")


@contextlib.contextmanager
def no_rig():
    """Every rigrun and regress call that reads the rig, PowerShell or the logs fails the test."""
    with contextlib.ExitStack() as stack:
        for function in RIG_READS:
            stack.enter_context(mock.patch.object(rigrun, function.__name__, refuse))
        for name in ("ps", "zone_lines", "read_status", "request_play"):
            stack.enter_context(mock.patch.object(regress, name, refuse))
        yield


class FakeRig:
    """rigrun's run and read calls for a rig whose runs all succeed (except the numbered failing ones): each cell
    starts once with its layout's rectangle, links with its touching cells, spawns its plan and works 10 ms (coarse)
    or 6 ms (any other layout) per frame."""

    def __init__(self, failing=(), lines=()):
        self.env, self.runs, self.failing, self.lines = None, 0, failing, list(lines)

    def run_window(self, env, warmup, measure, rep):
        self.env, self.runs = env, self.runs + 1
        if self.runs in self.failing:
            raise RuntimeError("boom")
        return SINCE, START, END

    def cell_window(self, cid, map_id, since, start, end):
        override = self.env.get("MESH_LAYOUT_OVERRIDE", "")
        layout = next(name for name in rigrun.LAYOUTS if rigrun.layout_override(name) == override)
        cells = dict(rigrun.LAYOUTS[layout])
        plan = {c: int(n) for c, n in (item.split(":") for item in self.env["MESH_LOAD_PLAN"].split(","))}
        work = 10.0 if layout == "coarse" else 6.0
        stats = {"windows": 12, "frames": 3600, "seconds": 60.0, "work_mean": work, "wait_mean": 16.67 - work,
                 "period_mean": 16.67, "work_p95": work + 1, "work_max": work + 2, "sat": 0.0, "budget": 16.67,
                 "util": work / 16.67, "probe": ["Valid"] * 12, "owned_players": plan[cid], "owned_monsters": 0,
                 "puppet_players": 0, "puppet_monsters": 0, "clients": 0, "cpu": 50}
        return {"stats": stats, "own": [(cid, regress.MAP, tuple(float(v) for v in cells[cid]))],
                "spawned": [plan[cid]], "exceptions": 0,
                "linked": {other for other, rect in cells.items() if other != cid and rigtools.rects_touch(cells[cid], rect)}}

    def cell_lines(self, cid, map_id, since):
        return self.lines

    @contextlib.contextmanager
    def installed(self):
        with contextlib.ExitStack() as stack:
            for name in ("run_window", "cell_window", "cell_lines"):
                stack.enter_context(mock.patch.object(rigrun, name, getattr(self, name)))
            stack.enter_context(mock.patch.object(rigrun, "host_context", lambda: dict(CONTEXT)))
            stack.enter_context(contextlib.redirect_stdout(io.StringIO()))
            yield


def ab(argv, rig):
    """(exit code, report lines) of abload.ab_command on the stand-in rig."""
    rep = rigrun.Report("abload ab on HOST, CPU (2026-09-28 10:00)")
    with rig.installed():
        code = abload.ab_command(abload.build_parser().parse_args(argv), rep)
    return code, rep.lines


class ParserTests(unittest.TestCase):
    def test_new_flags_default_to_071(self):
        args = abload.build_parser().parse_args(["ab"])
        self.assertEqual({key: getattr(args, key) for key in ("bands", "puppets", "rates", "instrument", "order",
                                                              "jsonl", "case", "dry_run")},
                         {"bands": "", "puppets": "", "rates": "", "instrument": False, "order": "point-major",
                          "jsonl": "", "case": "ab", "dry_run": False})
        self.assertFalse(abgrade.phase0(args))
        self.assertEqual(len(abgrade.header_lines(args)), 1)
        new_keys = {"MESH_PUPPET_MOTION", "MESH_RATES", "MESH_BAND", *p0plans.INSTR}
        self.assertFalse(any(new_keys & set(spec["env"]) for spec in abgrade.ab_plan(args)))


class RefusalTests(unittest.TestCase):
    CASES = {
        "malformed rates": (["--rates", "30/x"], "--rates 30/x: MESH_RATES step part 'x' is not a number of seconds"),
        "uncapped rates": (["--rates", "u30/0.02"],
                           "--rates u30/0.02 runs uncapped: every run would fail the uncapped guard (its Work window "
                           "budget is 0); uncapped measurement is p0load's (density bridge-0.53)"),
        "band with bands": (["--band", "20", "--bands", "20,40"],
                            "--band 20 and --bands 20,40 both set the band; give --bands alone (it sets MESH_BAND on "
                            "every run)"),
        "unknown layout": (["--layouts", "halves,strips5"],
                           "unknown layout strips5; known: coarse, halves, grid, strips4"),
    }

    def test_main_refuses_before_any_precondition(self):
        for name, (argv, message) in self.CASES.items():
            for extra in ([], ["--dry-run"]):
                with self.subTest(name, extra=extra), no_rig():
                    with self.assertRaises(SystemExit) as caught:
                        abload.main(["ab", *argv, *extra])
                    self.assertEqual(caught.exception.code, message)

    def test_ab_command_refuses_before_printing(self):
        for name, (argv, message) in self.CASES.items():
            with self.subTest(name), no_rig():
                rep = rigrun.Report("header")
                with self.assertRaises(SystemExit) as caught:
                    abload.ab_command(abload.build_parser().parse_args(["ab", *argv]), rep)
                self.assertEqual((caught.exception.code, rep.lines), (message, ["header"]))

    def test_planned(self):
        with self.assertRaises(SystemExit) as caught:
            abload.planned(abload.build_parser().parse_args(["ab", "--puppets", "inert"]))
        self.assertEqual(caught.exception.code, "--puppets inert is not one of steered, kinematic")
        self.assertEqual(len(abload.planned(abload.build_parser().parse_args(SMALL_AB))), 4)


class DryRunTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.dir)

    def test_dry_run_prints_the_plan_and_touches_nothing(self):
        rows = os.path.join(self.dir, "p0", "rows.jsonl")
        label, argv = p0plans.abgeo_invocations(rows, subs=(2,))[0]
        out = io.StringIO()
        with no_rig(), mock.patch.object(regress, "LOGS", self.dir), contextlib.redirect_stdout(out):
            code = abload.main([*argv, "--dry-run"])
        lines = out.getvalue().splitlines()
        self.assertEqual((label, code), ("b9-bands", 0))
        self.assertEqual(lines[:2], [
            "=== static A/B, capped, load spread, band per point (--bands), 3 repeat(s) (seeds 1..3), warmup 45s "
            "measure 60s",
            f"=== phase 0: puppets kinematic, rates 60/0.03, bands 20,40,80,150, instruments on, order repeat-major, "
            f"rows {rows}"])
        self.assertTrue(lines[2].startswith("every other regress.ENV_KEYS key is cleared ("))
        self.assertEqual(sum(1 for line in lines if line.startswith("[") and "/36] abgeo b9-bands " in line), 36)
        self.assertTrue(lines[-1].startswith("36 runs, about 76 min at 126 s per 45+60 run"))
        self.assertIn("MESH_BAND=80 ", "\n".join(lines))
        self.assertEqual(os.listdir(self.dir), [])


class RowsTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.dir)
        self.rows = os.path.join(self.dir, "rows.jsonl")
        self.argv = SMALL_AB + ["--jsonl", self.rows, "--case", "t"]

    def test_row_for_every_run(self):
        keys = [spec["key"] for spec in abgrade.ab_plan(abload.build_parser().parse_args(self.argv))]
        code, lines = ab(self.argv, FakeRig(failing=(2,)))
        rows = p0results.read_rows(self.rows)
        self.assertEqual(code, 1)   # one usable halves run: too few pairs for a verdict
        self.assertEqual([(row["family"], row["case"], row["arm"], row["repeat"], row["accepted"]) for row in rows],
                         [("abgeo", "t", "coarse", 1, False), ("abgeo", "t", "halves", 1, False),
                          ("abgeo", "t", "halves", 2, False), ("abgeo", "t", "coarse", 2, False)])
        failed = rows[1]
        self.assertEqual((failed["excluded_reason"], failed["problems"], failed["window_local"]),
                         ("run failed", ["boom"], None))
        self.assertEqual(rows[0]["window_local"], {"since": SINCE.isoformat(), "start": START.isoformat(),
                                                   "end": END.isoformat()})
        self.assertEqual(rows[0]["problems"], ["C's Work ticks windows cover 0 s of the 60 s measurement, need 48"])
        self.assertEqual([row["plan_key"] for row in rows], keys)
        self.assertEqual((rows[0]["host"], rows[0]["build"]), ("HOST", "abc"))
        self.assertIn("    run failed: boom", lines)
        self.assertIn("    row not accepted: C's Work ticks windows cover 0 s of the 60 s measurement, need 48", lines)

    def test_text_is_071_plus_the_phase0_line_and_row_notes(self):
        plain_code, plain = ab(SMALL_AB, FakeRig(failing=(3,)))
        code, with_rows = ab(self.argv, FakeRig(failing=(3,)))
        added = [line for line in with_rows if line.startswith(("=== phase 0:", "    row "))]
        self.assertEqual([line for line in with_rows if line not in added], plain)
        self.assertEqual((code, plain_code), (1, 1))
        self.assertEqual(len(added), 4)   # the phase 0 line and a note under each of the three runs that ran
        self.assertEqual(plain[-1], "go/no-go (halves, spread load, every point): NO VERDICT")

    def test_accepted_row_has_no_note(self):
        with mock.patch.object(p0checks, "run_problems", lambda spec, run: []), \
                mock.patch.object(p0checks, "exclusion", lambda spec, run: None):
            code, lines = ab(self.argv, FakeRig())
        rows = p0results.read_rows(self.rows)
        self.assertEqual(code, 0)
        self.assertEqual([row["accepted"] for row in rows], [True] * 4)
        self.assertFalse(any(line.startswith("    row ") for line in lines))
        self.assertEqual(lines[-1], "go/no-go (halves, spread load, every point): GO")

    def test_row_that_cannot_be_graded_is_written_as_failed(self):
        rig = FakeRig()
        rig.cell_lines = mock.Mock(side_effect=OSError("log locked"))
        code, lines = ab(self.argv, rig)
        rows = p0results.read_rows(self.rows)
        self.assertEqual(code, 0)
        self.assertEqual(lines.count("    row not graded: log locked"), 4)
        self.assertEqual({(row["excluded_reason"], tuple(row["problems"]), row["window_local"]) for row in rows},
                         {("run failed", ("row not graded: log locked",), None)})


if __name__ == "__main__":
    unittest.main()
