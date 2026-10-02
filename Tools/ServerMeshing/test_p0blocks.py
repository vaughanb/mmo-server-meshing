"""Unit tests for p0blocks (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py").

The rig is a stand-in: p0blocks.run_spec returns synthetic rows on a fake clock, rigrun.now reads that clock, and
Logs/p0 is a temporary folder, so no editor, database or real log is touched.
"""
import contextlib
import datetime
import io
import os
import tempfile
import unittest
from unittest import mock

import p0blocks
import p0load
import p0plans
import p0results
import test_p0geo

START = datetime.datetime(2026, 9, 28, 10, 0, 0)


class Rep:
    """A quiet stand-in for rigrun.Report."""

    def __init__(self):
        self.lines = []

    def note(self, text=""):
        self.lines.append(text)


class Rig:
    """The stand-in rig: each run takes `minutes` of the fake clock; canary works come from a list (None: not
    accepted), block works from work(spec); keys in failing give unaccepted rows."""

    def __init__(self, canaries=(), work=None, failing=(), minutes=2):
        self.clock, self.canaries, self.failing = START, list(canaries), set(failing)
        self.work, self.minutes = work or (lambda spec: 300.0), minutes
        self.runs = []

    def now(self):
        return self.clock

    def run_spec(self, spec, rep, context):
        self.clock += datetime.timedelta(minutes=self.minutes)
        self.runs.append(spec)
        canary = spec["family"] == "canary" and spec["arm"] == "check"
        work = (self.canaries.pop(0) if self.canaries else 100.0) if canary else self.work(spec)
        failed = spec["key"] in self.failing or work is None
        row = p0results.run_row(spec, None, context, ["C logged 1 exception line(s)"] if failed else [], None)
        metrics = [] if work is None else [{"name": name, "stat": "mean", "value": value, "unit": "", "label": "M",
                                            "cell": "C"} for name, value in (("work_ms_per_s", work), ("sat_pct", 0.0))]
        row.update(window_local={"since": "", "start": "", "end": self.clock.isoformat()}, metrics=metrics)
        return row, {"cells": {}}


def baseline(end="2026-09-28T08:00:00"):
    return test_p0geo.run_row("canary", "canary", "baseline", 1, {"C": (100.0, 200, 0)}, build="b1", game="neutral",
                              plan_key="base", window_local={"since": "", "start": "", "end": end})


def check(work, minutes_before_start):
    end = (START - datetime.timedelta(minutes=minutes_before_start)).isoformat()
    return dict(baseline(end), arm="check", plan_key="check", metrics=[dict(baseline()["metrics"][0], value=work)])


def earlier_rows(specs, minutes_before_start, work=None, build="b1"):
    """An earlier invocation's rows for these specs, from a stand-in rig whose clock starts that long before START."""
    rig = Rig(work=work)
    rig.clock = START - datetime.timedelta(minutes=minutes_before_start)
    return [rig.run_spec(spec, None, {"build": build})[0] for spec in specs]


def point_work(values, arm="idle", case="cell", rates="30/0.02", other=100.0):
    """A block work function: values[repeat] at one floor point, other everywhere else."""
    def work(spec):
        if (spec["case"], spec["arm"], spec["point"]["rates"]) == (case, arm, rates):
            return values(spec) if callable(values) else values[spec["repeat"]]
        return other
    return work


class BlockTests(unittest.TestCase):
    def setUp(self):
        folder = tempfile.TemporaryDirectory()
        self.addCleanup(folder.cleanup)
        self.logs = folder.name
        patcher = mock.patch.object(p0blocks, "P0_LOGS", self.logs)
        patcher.start()
        self.addCleanup(patcher.stop)

    def session(self, argv, rig, history=None):
        for target, name, value in ((p0blocks, "run_spec", rig.run_spec), (p0blocks.rigrun, "now", rig.now)):
            patcher = mock.patch.object(target, name, value)
            patcher.start()
            self.addCleanup(patcher.stop)
        args = p0load.build_parser().parse_args(argv)
        return p0blocks.Session(args, "S", Rep(), {"build": "b1"}, [baseline()] if history is None else history)

    def kinds(self, rig):
        return ["canary" if spec["family"] == "canary" else spec["arm"] for spec in rig.runs]

    def test_canary_stops(self):
        cases = {
            "two drifts": ([106.0, 94.0], "machine state changed: the canary drifted twice"),
            "two unaccepted canaries": ([None, None], "the canary cannot run: two canaries in a row were not accepted"),
            "drift, unaccepted, drift": ([106.0, None, 110.0], "machine state changed: the canary drifted twice"),
            "no pass in three tries": ([None, 106.0, None], "the canary did not pass in 3 tries (unaccepted, drift, "
                                                            "unaccepted)"),
        }
        for name, (works, reason) in cases.items():
            with self.subTest(name):
                rig = Rig(canaries=works)
                session = self.session(["floor"], rig)
                with self.assertRaises(p0blocks.Stopped) as caught:
                    session.block(p0plans.floor_plan())
                self.assertEqual(caught.exception.code, 3)
                self.assertIn(reason, caught.exception.reason)

    def test_a_block_between_canaries(self):
        rig = Rig()
        session = self.session(["rates", "--repeats", "1"], rig)
        specs = p0plans.rates_plan(1)
        self.assertEqual(session.block(specs), 0)
        self.assertEqual(self.kinds(rig), ["canary"] + [spec["arm"] for spec in specs] + ["canary"])
        written = p0results.read_rows(os.path.join(self.logs, "rig_neutral_rates_S.jsonl"))
        self.assertEqual([row["plan_key"] for row in written], [spec["key"] for spec in specs])
        self.assertEqual(len(p0results.read_rows(os.path.join(self.logs, "rig_neutral_canary_S.jsonl"))), 2)

    def test_a_recent_canary_skips_the_start_canary(self):
        rig = Rig()
        recent = [baseline(end=(START - datetime.timedelta(minutes=30)).isoformat())]
        session = self.session(["floor"], rig, recent)
        self.assertEqual(session.block(p0plans.floor_plan()[:2]), 0)
        self.assertEqual(self.kinds(rig), ["idle", "idle", "canary"])

    def test_a_canary_every_two_hours(self):
        rig = Rig(minutes=5)
        session = self.session(["rates"], rig)
        self.assertEqual(session.block(p0plans.rates_plan(3)), 0)
        canaries = [index for index, kind in enumerate(self.kinds(rig)) if kind == "canary"]
        self.assertEqual(canaries, [0, 25, 50])
        self.assertEqual(len(rig.runs), 51)

    def test_a_drift_queues_the_runs_again_once(self):
        rig = Rig(canaries=[100.0, 106.0, 101.0, 100.0])
        session = self.session(["floor"], rig)
        specs = p0plans.floor_plan()[:3]
        self.assertEqual(session.block(specs), 0)
        self.assertEqual(self.kinds(rig), ["canary", "idle", "idle", "players-150", "canary", "canary", "idle", "idle",
                                           "players-150", "canary"])
        drifted = p0results.apply_drift(session.rows)
        first = [drifted[index] for index in session.mine[:3]]
        self.assertEqual({row["excluded_reason"] for row in first}, {"drift"})
        self.assertTrue(all(drifted[index]["accepted"] for index in session.mine[3:]))
        self.assertTrue(any("queued again at the end" in line for line in session.rep.lines))

    def test_third_repeats(self):
        def work(spec):
            if spec["case"] == "cell" and spec["arm"] == "idle" and spec["point"]["rates"] == "30/0.02":
                return {1: 100.0, 2: 115.0, 3: 102.0}[spec["repeat"]]
            return 300.0
        rig = Rig(work=work)
        session = self.session(["floor"], rig)
        self.assertEqual(session.block(p0plans.floor_plan()), 0)
        third = [session.rows[index] for index in session.mine if session.rows[index]["repeat"] == 3]
        self.assertEqual(len(third), 1)
        self.assertEqual(third[0]["replaces_repeat"], 2)
        self.assertIn("differ by 15.0%", third[0]["third_reason"])
        self.assertEqual(self.kinds(rig)[-2:], ["idle", "canary"])

    def test_resumed_blocks_still_get_their_third_repeats(self):
        plan = p0plans.floor_plan()
        cases = {"repeat 1 done earlier": 1, "both repeats done earlier": 2, "nothing done earlier": 0}
        for name, done_repeats in cases.items():
            with self.subTest(name):
                rig = Rig(work=lambda spec: {1: 100.0, 2: 130.0, 3: 110.0}[spec["repeat"]])
                history = earlier_rows([s for s in plan if s["repeat"] <= done_repeats], 180, rig.work)
                session = self.session(["floor", "--resume", "--no-canary"], rig, history)
                done = p0blocks.done_keys(history, "b1")
                self.assertEqual(session.block([s for s in plan if s["key"] not in done], plan), 0)
                self.assertEqual(len([s for s in rig.runs if s["repeat"] == 3]), 6)
                self.assertIn("third repeats: 6", session.rep.lines)
                self.assertIn("18 of 18 planned runs accepted", session.rep.lines)

    def test_third_repeats_read_this_build_only(self):
        plan = p0plans.floor_plan()
        history = [baseline()] + earlier_rows(plan[:1], 180, lambda spec: 80.0, build="b0")
        rig = Rig(work=lambda spec: 100.0, failing={plan[0]["key"]})
        session = self.session(["floor", "--no-canary"], rig, history)
        session.block(plan, plan)
        self.assertEqual([s for s in rig.runs if s["repeat"] == 3], [])

    def test_a_drift_decides_the_third_repeats_again(self):
        cases = {  # work at cell idle 30/0.02 (the others read 100), canaries after the start one, third runs
            "a pair that disagrees only after the re-runs": (
                lambda spec: 140.0 if spec["repeat"] == 2 and spec.get("requeued") else 100.0, [120.0, 100.0, 100.0],
                1),
            "a drifted third is decided again, not queued again": (
                lambda spec: {1: 100.0, 2: 115.0, 3: 102.0}[spec["repeat"]], [106.0, 100.0, 100.0], 2),
        }
        for name, (values, canaries, thirds) in cases.items():
            with self.subTest(name):
                rig = Rig(canaries=[100.0] + canaries, work=point_work(values))
                session = self.session(["floor"], rig)
                self.assertEqual(session.block(p0plans.floor_plan()), 0)
                self.assertEqual(len([s for s in rig.runs if s["repeat"] == 3]), thirds)
                self.assertFalse([s for s in rig.runs if s.get("third_reason") and s.get("requeued")])
                self.assertEqual(self.kinds(rig)[-1], "canary")

    def test_a_drifted_last_canary_forces_the_start_canary(self):
        cases = {"the newest canary drifted": ([check(100.0, 60), check(106.0, 30)], "canary"),
                 "a canary passed after the drift": ([check(106.0, 40), check(100.0, 30)], "idle")}
        for name, (canaries, first) in cases.items():
            with self.subTest(name):
                rig = Rig()
                session = self.session(["floor"], rig, [baseline()] + canaries)
                self.assertEqual(session.block(p0plans.floor_plan()[:2]), 0)
                self.assertEqual(self.kinds(rig)[0], first)

    def test_a_resumed_block_queues_again_the_earlier_runs_a_drift_excludes(self):
        plan = p0plans.floor_plan()
        history = [baseline(end=(START - datetime.timedelta(hours=2)).isoformat())]
        history += earlier_rows(p0plans.canary_plan("check") + plan[:6], 90)
        rig = Rig(canaries=[106.0, 100.0, 100.0])
        session = self.session(["floor", "--resume"], rig, history)
        done = p0blocks.done_keys(history, "b1")
        self.assertEqual(session.block([s for s in plan if s["key"] not in done], plan), 0)
        self.assertEqual(self.kinds(rig)[:6], [s["arm"] for s in plan[6:]])
        self.assertEqual(len([s for s in rig.runs if s.get("requeued")]), 12)
        self.assertIn("12 of 12 planned runs accepted", session.rep.lines)

    def test_two_repeat_rates_get_third_repeats(self):
        rig = Rig(work=point_work({1: 400.0, 2: 460.0, 3: 430.0}, "steered", "K", "60/0.03", 400.0))
        session = self.session(["rates", "--repeats", "2", "--no-canary"], rig)
        self.assertEqual(session.block(p0plans.rates_plan(2)), 0)
        self.assertEqual([(s["arm"], s["repeat"]) for s in rig.runs if s["repeat"] == 3], [("steered", 3)])
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            p0load.dry_run(session.args, [])
        self.assertIn("third repeats: each 2-repeat point whose runs differ by more than 10 %", out.getvalue())

    def test_accept_a_refuses_resume(self):
        out = io.StringIO()
        with mock.patch.object(p0load, "run_command", side_effect=AssertionError("a rig run started")), \
                mock.patch.object(p0load.rigrun, "build_id", return_value="b1"), contextlib.redirect_stdout(out):
            self.assertEqual(p0load.main(["accept-a", "--resume"]), 2)
        self.assertIn("refused: accept-a grades its runs together", out.getvalue())

    def test_verdict_lists_what_was_not_accepted(self):
        specs = p0plans.floor_plan()[:2]
        rig = Rig(failing={specs[1]["key"]})
        session = self.session(["floor", "--no-canary"], rig)
        self.assertEqual(session.block(specs), 1)
        self.assertNotIn("canary", self.kinds(rig))
        self.assertIn(f"not accepted: floor cell idle r1 (key {specs[1]['key']}): C logged 1 exception line(s)",
                      session.rep.lines)
        self.assertIn("1 of 2 planned runs accepted", session.rep.lines)

    def test_baseline_block(self):
        cases = {"within 2.5 %": ((100.0, 102.5), 2), "more than 2.5 % apart": ((100.0, 102.6), 3)}
        for name, (works, runs) in cases.items():
            with self.subTest(name):
                values = iter(works + (101.0,))
                rig = Rig(work=lambda spec: next(values))
                session = self.session(["canary", "--baseline"], rig, [])
                self.assertEqual(p0blocks.baseline_block(session, p0plans.canary_plan("baseline", 2)), 0)
                self.assertEqual([spec["repeat"] for spec in rig.runs], list(range(1, runs + 1)))

    def test_accept_commands_rerun_part_c_once(self):
        calls = []

        def call(command, cwd, stdout, stderr):
            calls.append(command)
            return 1 if len(calls) == 1 else 0
        session = self.session(["accept-a", "--parts", "C,D"], Rig())
        with mock.patch.object(p0blocks.subprocess, "call", call):
            self.assertTrue(p0blocks.accept_commands(session, ["C"], "S"))
        self.assertEqual(len(calls), 5)
        self.assertEqual(calls[0], calls[1])
        self.assertTrue(os.path.exists(os.path.join(self.logs, "accept_C_S.log")))

    def test_abgeo_reruns_a_drifted_sub_block(self):
        rig = Rig(canaries=[100.0, 106.0, 100.0, 100.0])
        session = self.session(["abgeo", "--subs", "3"], rig)
        labels = []

        def call(command, cwd):
            label = command[command.index("--case") + 1]
            labels.append(label)
            specs = p0blocks.ab_specs(command[3:])
            rows = [rig.run_spec(spec, None, None)[0] for spec in specs]
            p0results.write_rows(session.rows_path, rows)
            return 0
        with mock.patch.object(p0blocks.subprocess, "call", call):
            self.assertEqual(p0blocks.abgeo_block(session, session.args, frozenset()), 0)
        self.assertEqual(labels, ["b9-steered-b150", "b9-steered-b80", "b9-steered-b150"])
        self.assertEqual(len(session.mine), 18)


if __name__ == "__main__":
    unittest.main()
