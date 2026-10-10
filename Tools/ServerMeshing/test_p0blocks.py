"""Unit tests for p0blocks (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py").

The rig is a stand-in: p0blocks.run_spec returns synthetic rows on a fake clock, rigrun.now reads that clock, and
Logs/p0 is a temporary folder, so no editor, database or real log is touched.
"""
import argparse
import contextlib
import datetime
import io
import os
import tempfile
import unittest
from unittest import mock

import p0base
import p0blocks
import p0load
import p0plans
import p0results
import p0stepped
import test_p0cells0b
import test_p0geo
import test_p0peer
import test_p0results

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

    def test_an_early_stopping_run_is_graded_on_what_it_measured(self):
        start = datetime.datetime(2026, 10, 8, 20, 33, 13)
        cases = {  # spec extras, seconds measured, measure graded against
            "a walk that stopped after 2N outcomes": ({"stop_after_outcomes": 40}, 61, 61),
            "a fractional window": ({"stop_after_outcomes": 40}, 60.5, 60.5),
            "a burst that stopped once the drain emptied": ({"stop_after_drain": True}, 75, 75),
            "a plain run keeps its plan": ({}, 61, 140),
        }
        for name, (extra, seconds, want) in cases.items():
            with self.subTest(name):
                spec = dict({"measure": 140}, **extra)
                end = start + datetime.timedelta(seconds=seconds)
                self.assertEqual(p0blocks.measured(spec, start, end)["measure"], want)

    def test_canaries_are_judged_against_the_blocks_reference(self):
        cases = {  # canaries (start, every 2 h, end), what the rig ran, and the report's canary lines
            "a slow slide within the block passes (8 % from the baseline, 4 % from the opening canary)": (
                [96.0, 92.0, 92.0], 3, ["against baseline 100.0 (4.0%, limit 10%): ok",
                                        "against the block's opening canary", "(4.2%, limit 5%): ok"]),
            "6 % from the opening canary drifts even within 10 % of the baseline": (
                [100.0, 106.0, 101.0, 100.0], 5, ["(6.0%, limit 5%): DRIFT"]),
        }
        for name, (canaries, ran, texts) in cases.items():
            with self.subTest(name):
                rig = Rig(canaries=list(canaries), minutes=50)
                session = self.session(["floor"], rig)
                self.assertEqual(session.block(p0plans.floor_plan()[:6]), 0)
                self.assertEqual(self.kinds(rig).count("canary"), ran)
                lines = " | ".join(session.rep.lines)
                for text in texts:
                    self.assertIn(text, lines)
                checks = [row for row in session.rows if row.get("family") == "canary" and row.get("arm") == "check"]
                self.assertEqual(checks[0]["drift_reference"], {"work": 100.0, "threshold": 0.1, "from": "baseline"})
                self.assertEqual({row["drift_reference"]["threshold"] for row in checks[1:]}, {0.05})
                drifted = p0results.apply_drift(session.rows)
                self.assertEqual([row.get("drift") for row in drifted if row.get("arm") == "check"],
                                 [p0base.drifted(p0results.canary_work(row), row["drift_reference"]["work"],
                                                 row["drift_reference"]["threshold"]) for row in checks])

    def test_a_skipped_opening_canary_lends_its_reading(self):
        history = [baseline(), check(96.0, 30)]
        rig = Rig(canaries=[92.0])
        session = self.session(["floor"], rig, history)
        self.assertEqual(session.reference[0], 96.0)
        self.assertEqual(session.block(p0plans.floor_plan()[:2]), 0)
        self.assertEqual(self.kinds(rig), ["idle", "idle", "canary"])
        self.assertTrue(any("(4.2%, limit 5%): ok" in line for line in session.rep.lines))

    def test_canary_stops(self):
        cases = {
            "two drifts (the opening canary's limit is START_DRIFT)": ([112.0, 88.0],
                                                                      "machine state changed: the canary drifted twice"),
            "two unaccepted canaries": ([None, None], "the canary cannot run: two canaries in a row were not accepted"),
            "drift, unaccepted, drift": ([112.0, None, 115.0], "machine state changed: the canary drifted twice"),
            "no pass in three tries": ([None, 112.0, None], "the canary did not pass in 3 tries (unaccepted, drift, "
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
                done = p0blocks.done_keys(history, "b1", None)
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
        done = p0blocks.done_keys(history, "b1", None)
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


class MachineTests(unittest.TestCase):
    """Step 8a decision 1 and acceptance 4: rows of one build on hosts A and B never stand in for each other."""

    def setUp(self):
        folder = tempfile.TemporaryDirectory()
        self.addCleanup(folder.cleanup)
        patcher = mock.patch.object(p0blocks, "P0_LOGS", folder.name)
        patcher.start()
        self.addCleanup(patcher.stop)

    def test_resume_skips_only_this_hosts_rows(self):
        plan = p0plans.floor_plan()[:4]
        history = [dict(row, host="A") for row in earlier_rows(plan[:1], 60)]
        history += [dict(row, host="B") for row in earlier_rows(plan[1:3], 60)]
        self.assertEqual(p0blocks.done_keys(history, "b1", "A"), {plan[0]["key"]})
        self.assertEqual(p0blocks.done_keys(history, "b1", "B"), {plan[1]["key"], plan[2]["key"]})
        self.assertEqual(p0blocks.done_keys(history, "b2", "A"), set())

    def session(self, history, host="A", cpu=None, canaries=()):
        rig = Rig(canaries=canaries)
        for target, name, value in ((p0blocks, "run_spec", rig.run_spec), (p0blocks.rigrun, "now", rig.now)):
            patcher = mock.patch.object(target, name, value)
            patcher.start()
            self.addCleanup(patcher.stop)
        args = argparse.Namespace(command="floor", no_canary=False)
        return p0blocks.Session(args, "S", Rep(), {"build": "b1", "host": host, "cpu": cpu}, history), rig

    def test_a_canary_is_judged_against_this_hosts_baseline(self):
        history = [dict(baseline(), host="A"), dict(baseline(), host="B", metrics=[dict(baseline()["metrics"][0],
                                                                                        value=120.0)])]
        session, _ = self.session(history, canaries=[104.0])   # 4 % from A's 100, 5.5 % from a pooled 110
        self.assertEqual(session.canary_once("test"), "ok")
        self.assertIn("canary (test): C 104.0 ms/s against baseline 100.0 (4.0%, limit 10%): ok", session.rep.lines)
        session, _ = self.session(history[1:], canaries=[104.0])
        with self.assertRaises(p0blocks.Stopped) as caught:
            session.canary_once("test")
        self.assertEqual((caught.exception.code, caught.exception.reason),
                         (2, "no baseline canary on build b1 host A: run p0load canary --baseline"))

    def test_the_canary_state_and_the_rows_are_this_hosts(self):
        recent = dict(baseline(end=(START - datetime.timedelta(minutes=30)).isoformat()), host="B")
        session, _ = self.session([dict(baseline(), host="A"), recent])
        self.assertEqual(session.last_canary, datetime.datetime(2026, 9, 28, 8, 0))   # A's, not B's recent one
        self.assertEqual(session.ours(session.rows), [session.rows[0]])

    def test_a_host_with_two_cpus_stops(self):
        with self.assertRaises(p0blocks.Stopped) as caught:
            self.session([dict(baseline(), host="A", cpu="X")], cpu="Y")
        self.assertEqual((caught.exception.code, caught.exception.reason),
                         (2, "host A ran on 2 CPUs (X and Y): give each machine its own host name"))


def stepped_spec(steps=(50, 100), key="ks"):
    return test_p0results.spec(family="fanout", key=key, stepped={"steps": list(steps), "hold": 40}, warmup=10,
                               measure=110, cells_read=["C"])


def step_rows(spec, accepted, since="2026-09-26T09:59:00", host=None):
    """A stepped run's rows: accepted {P: bool} per planned step (a step left out has no row)."""
    rows = []
    for step, ok in accepted.items():
        row = p0results.run_row(p0stepped.step_spec(spec, {"step": step}), None, {"build": "b1", "host": host},
                                [] if ok else [f"C: step {step} failed"], None)
        rows.append(dict(row, stepped_since=since, window_local={"since": since, "start": since, "end": since}))
    return rows


class SteppedTests(unittest.TestCase):
    def patch(self, target, name, value):
        patcher = mock.patch.object(target, name, value)
        patcher.start()
        self.addCleanup(patcher.stop)

    def stepped(self, lines, window=None, steps=(50, 100), exclusion=lambda spec, run: None):
        self.stops = []   # what the run's stop_when answered, asked once at the window's start

        def played(*_, stop_when=None):
            since = test_p0cells0b.START - datetime.timedelta(seconds=60)
            if stop_when is not None:
                self.stops.append(stop_when(since, test_p0cells0b.START))
            return since, test_p0cells0b.START, test_p0cells0b.END
        self.patch(p0stepped.rigrun, "run_window", window or played)
        self.patch(p0stepped.rigrun, "cell_lines", lambda *a: lines)
        self.patch(p0stepped.rigrun, "cell_window", lambda *a: {})
        self.patch(p0stepped.p0checks, "run_problems", lambda spec, run: [])
        self.patch(p0stepped.p0checks, "exclusion", exclusion)
        with test_p0cells0b.fake_stage0b():
            return p0stepped.run_stepped(stepped_spec(steps), Rep(), {"build": "b1", "host": "A"})

    def test_a_rising_run_stops_after_a_saturated_step(self):
        def saturated_at_50(spec, run):
            return "saturated (span) on C" if spec["point"]["step"] == 50 else None
        step = test_p0cells0b.step_line
        cases = {  # steps, their lines, what stop_when answered, then (step, excluded_reason, measured) per row
            "rising: stops, the later step is not measured": (
                (50, 100), [step("09:59:50", 1, 2, 0, 50), step("10:00:20", 2, 2, 50, 100)], [True],
                [(50, "saturated (span) on C", True),
                 (100, "not measured: saturated at the earlier step P 50 on C", False)]),
            "falling: never stops": (
                (100, 50), [step("09:59:50", 1, 2, 0, 100), step("10:00:20", 2, 2, 100, 50)], [False],
                [(100, None, True), (50, "saturated (span) on C", True)]),
        }
        for name, (steps, step_lines, stops, expected) in cases.items():
            with self.subTest(name):
                pairs = self.stepped(step_lines + test_p0cells0b.cell_lines()[2:], steps=steps,
                                     exclusion=saturated_at_50)
                self.assertEqual(self.stops, stops)
                self.assertEqual([(row["point"]["step"], row["excluded_reason"], run is not None) for row, run in pairs],
                                 expected)

    def test_a_run_that_raises_fails_every_step(self):
        def fail(*_, **__):
            raise RuntimeError("Play ended during the measurement")
        pairs = self.stepped([], window=fail)
        self.assertEqual([(row["point"]["step"], row["accepted"], row["problems"], run) for row, run in pairs],
                         [(50, False, ["Play ended during the measurement"], None),
                          (100, False, ["Play ended during the measurement"], None)])

    def test_one_row_per_step(self):
        lines = [test_p0cells0b.step_line("09:59:50", 1, 2, 0, 50), test_p0cells0b.step_line("10:00:20", 2, 2, 50, 100)]
        lines += test_p0cells0b.cell_lines()[2:]
        pairs = self.stepped(lines)
        rows = [row for row, _ in pairs]
        self.assertEqual([(row["plan_key"], row["point"], row["accepted"], row["window_local"]["start"],
                           row["window_local"]["end"]) for row in rows],
                         [("ks", {"players": 60, "step": 50}, True, "2026-09-26T10:00:01", "2026-09-26T10:00:19"),
                          ("ks", {"players": 60, "step": 100}, True, "2026-09-26T10:00:31", "2026-09-26T10:01:00")])
        self.assertEqual({(row["stepped_since"], row["net_saturated"], row["stage"]) for row in rows},
                         {("2026-09-26T09:59:00", None, "0b")})
        self.assertEqual([len(run["cells"]["C"]["p0b"]["send"]) for _, run in pairs], [2, 0])
        self.assertEqual(p0stepped.complete_keys(rows), {"ks"})

    def test_a_step_without_a_window_is_a_failed_row(self):
        pairs = self.stepped([test_p0cells0b.step_line("09:59:50", 1, 2, 0, 50)])
        self.assertEqual([(row["point"]["step"], row["accepted"], row["problems"]) for row, _ in pairs],
                         [(50, True, []), (100, False, ["step 2/2 (P 100) logged no Phantom step line"])])
        self.assertEqual(p0stepped.complete_keys([row for row, _ in pairs]), set())

    def test_step_spec(self):
        window = {"step": 200, "start": test_p0cells0b.START, "end": test_p0cells0b.at("10:00:45")}
        sub = p0stepped.step_spec(stepped_spec(), window)
        self.assertEqual((sub["point"], sub["measure"], sub["key"]), ({"players": 60, "step": 200}, 45, "ks"))
        self.assertEqual(p0stepped.step_spec(stepped_spec(), {"step": 50})["measure"], 0)

    def test_a_stepped_run_is_done_only_with_every_step(self):
        spec = stepped_spec()
        cases = {
            "a failed step": (step_rows(spec, {50: True, 100: False}), set()),
            "a missing step": (step_rows(spec, {50: True}), set()),
            "steps of two runs": (step_rows(spec, {50: True}) + step_rows(spec, {100: True}, "2026-09-26T11:00:00"),
                                  set()),
            "another host's run": (step_rows(spec, {50: True, 100: True}, host="B"), set()),
            "a later complete run": (step_rows(spec, {50: True, 100: False}) + step_rows(
                spec, {50: True, 100: True}, "2026-09-26T11:00:00"), {"ks"}),
        }
        for name, (rows, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0blocks.done_keys(rows, "b1", None), expected)

    def test_the_verdict_needs_every_step(self):
        spec = stepped_spec()
        cases = {
            "a failed step": ({50: True, 100: False}, 1,
                              "not accepted: fanout main puppets-kinematic r1 (key ks): step 100: C: step 100 failed"),
            "every step": ({50: True, 100: True}, 0, "1 of 1 planned runs accepted"),
        }
        for name, (accepted, code, line) in cases.items():
            with self.subTest(name):
                self.patch(p0blocks.p0stepped, "run_stepped",
                           lambda spec, rep, context, accepted=accepted: [(row, None)
                                                                         for row in step_rows(spec, accepted)])
                folder = tempfile.TemporaryDirectory()
                self.addCleanup(folder.cleanup)
                self.patch(p0blocks, "P0_LOGS", folder.name)
                args = argparse.Namespace(command="fanout", no_canary=True)
                session = p0blocks.Session(args, "S", Rep(), {"build": "b1"}, [])
                self.assertEqual(session.block([spec]), code)
                self.assertIn(line, session.rep.lines)
                written = p0results.read_rows(os.path.join(folder.name, "rig_fantasy_fanout_S.jsonl"))
                self.assertEqual([row["point"]["step"] for row in written], [50, 100])


PEER = {"bots": {"first": 2, "n": 2, "mode": "stand", "placement": "west", "bot_net": None, "rig_ships": False},
        "sink": None}


class PeerTests(unittest.TestCase):
    """Step 8b decision 8: B's hooks around a run, the row they mark, and the stop after a row whose B kept bots.
    Machine B is test_p0peer.FakeB; run_window and the grading are stand-ins."""

    def patch(self, target, name, value):
        patcher = mock.patch.object(target, name, value)
        patcher.start()
        self.addCleanup(patcher.stop)
        return value

    def setUp(self):
        folder = tempfile.TemporaryDirectory()
        self.addCleanup(folder.cleanup)
        self.logs = folder.name
        self.patch(p0blocks, "P0_LOGS", self.logs)
        self.patch(p0blocks.p0peer.provision, "reset_bot_positions", mock.Mock())

    def window(self, calls):
        def run_window(env, warmup, measure, rep, on_play=None, stop_when=None, before_play=None, after_play=None):
            calls.append(sorted(name for name, hook in (("on_play", on_play), ("stop_when", stop_when),
                                                        ("before_play", before_play), ("after_play", after_play))
                                if hook is not None))
            try:
                before_play()
                on_play(test_p0cells0b.START)
            finally:
                after_play()
            return test_p0cells0b.START, test_p0cells0b.START, test_p0cells0b.END
        return run_window

    def test_canary_families_hold_every_8b_family(self):
        self.assertTrue(set(p0results.FAMILIES_8B) <= set(p0blocks.CANARY_FAMILIES))
        self.assertTrue({"dbburst", "workerab", "spaces", "packets"} <= set(p0blocks.CANARY_FAMILIES))
        self.assertEqual(len(set(p0blocks.CANARY_FAMILIES)), len(p0blocks.CANARY_FAMILIES))

    def test_run_spec_runs_the_burst_session(self):
        changed = "burst: 1 row(s) changed after Play stopped: meshburst0001Char"
        cases = {  # failure first: (run_window fails, after_stop's problems), (after_stop called, accepted, problems)
            "the run fails": ((True, []), (False, False, ["Play ended during the measurement"])),
            "a row changed after Play": ((False, [changed]), (True, False, [changed])),
            "clean": ((False, []), (True, True, [])),
        }
        for name, ((fails, problems), (stopped, accepted, row_problems)) in cases.items():
            with self.subTest(name):
                calls, seen = [], []

                def run_window(env, warmup, measure, rep, **hooks):
                    calls.append(sorted(hooks))
                    if fails:
                        raise RuntimeError("Play ended during the measurement")
                    return test_p0cells0b.START, test_p0cells0b.START, test_p0cells0b.END

                def after_stop(session, problems=problems):
                    seen.append(session.n)
                    session.problems += problems
                self.patch(p0blocks.rigrun, "run_window", run_window)
                self.patch(p0blocks.p0peer.p0burst.BurstSession, "after_stop", after_stop)
                self.patch(p0blocks.p0stepped, "graded", lambda s, *a: (p0results.run_row(s, None, {}, [], None), None))
                row, _ = p0blocks.run_spec(test_p0results.spec(burst_rows=50), Rep(), {})
                self.assertEqual(calls, [["after_play", "before_play", "on_play", "stop_when"]])
                self.assertEqual((seen, row["accepted"], row["problems"]), ([50] if stopped else [], accepted,
                                                                           row_problems))

    def test_run_spec_marks_the_row(self):
        cases = {  # B's bots left after the stop; the row's problems, accepted and peer stop
            "B keeps two bots": (2, ["B: 2 bots survived"], False, "B: 2 bots survived"),
            "B stops them": (0, [], True, None),
        }
        for name, (left, problems, accepted, stop) in cases.items():
            with self.subTest(name):
                b, calls = test_p0peer.FakeB(left=(0, 0)), []
                answers = iter([0, left])     # before_play's stop, after_play's
                self.patch(b, "stop_bots", lambda: next(answers))
                self.patch(p0blocks.p0peer, "RemoteB", lambda: b)
                self.patch(p0blocks.rigrun, "run_window", self.window(calls))
                self.patch(p0blocks.p0stepped, "graded", lambda s, *a: (p0results.run_row(s, None, {}, [], None),
                                                                        {"cells": {}}))
                row, _ = p0blocks.run_spec(test_p0results.spec(peer=PEER), Rep(), {})
                self.assertEqual(calls, [["after_play", "before_play", "on_play"]])
                self.assertEqual((row["problems"], row["accepted"], row.get(p0blocks.p0peer.PEER_STOP)),
                                 (problems, accepted, stop))
                self.assertEqual([c[0] for c in b.calls if c[0].startswith("start")], ["start_bots"])

    def test_run_spec_passes_a_refusal_on(self):
        def refused(*a, **k):
            raise p0blocks.Stopped(3, "B: 1 bot still running after being stopped")
        self.patch(p0blocks.p0peer, "RemoteB", test_p0peer.FakeB)
        self.patch(p0blocks.rigrun, "run_window", refused)
        with self.assertRaises(p0blocks.Stopped) as caught:
            p0blocks.run_spec(test_p0results.spec(peer=PEER), Rep(), {})
        self.assertEqual(caught.exception.code, 3)
        self.assertIs(p0blocks.Stopped, p0blocks.p0peer.Stopped)

    def test_a_crossing_run_stops_on_its_outcomes(self):
        calls = []

        def run_window(env, warmup, measure, rep, on_play=None, stop_when=None, **hooks):
            calls.append(stop_when)
            return test_p0cells0b.START, test_p0cells0b.START, test_p0cells0b.END
        self.patch(p0blocks.rigrun, "run_window", run_window)
        self.patch(p0blocks.p0peer, "outcomes_stop", lambda spec, n: ("outcomes", n))
        self.patch(p0blocks.p0stepped, "graded", lambda s, *a: (p0results.run_row(s, None, {}, [], None), None))
        p0blocks.run_spec(test_p0results.spec(stop_after_outcomes=40), Rep(), {})
        p0blocks.run_spec(test_p0results.spec(), Rep(), {})
        self.assertEqual(calls, [("outcomes", 40), None])

    def test_a_row_whose_b_kept_bots_is_written_then_stops(self):
        rig = Rig()

        def run_spec(spec, rep, context):
            row, run = rig.run_spec(spec, rep, context)
            if spec["repeat"] == 1:
                row.update({p0blocks.p0peer.PEER_STOP: "B: 2 bots survived", "accepted": False,
                            "problems": ["B: 2 bots survived"]})
            return row, run
        self.patch(p0blocks, "run_spec", run_spec)
        self.patch(p0blocks.rigrun, "now", rig.now)
        args = p0load.build_parser().parse_args(["floor", "--no-canary"])
        session = p0blocks.Session(args, "S", Rep(), {"build": "b1"}, [baseline()])
        with self.assertRaises(p0blocks.Stopped) as caught:
            session.block(p0plans.floor_plan())
        self.assertEqual((caught.exception.code, caught.exception.reason), (3, "B: 2 bots survived"))
        written = p0results.read_rows(os.path.join(self.logs, "rig_neutral_floor_S.jsonl"))
        self.assertEqual([(row["accepted"], p0blocks.p0peer.PEER_STOP in row) for row in written], [(False, False)])

    def test_b_is_quieted_before_each_canary_of_a_peer_block(self):
        cases = {"a block with a peer": (PEER, ["quiet", "canary", "idle", "quiet", "canary"]),
                 "a block without one": (None, ["canary", "idle", "canary"])}
        for name, (peer, expected) in cases.items():
            with self.subTest(name):
                rig, order = Rig(), []

                def run_spec(spec, rep, context):
                    order.append("canary" if spec["family"] == "canary" else spec["arm"])
                    return rig.run_spec(spec, rep, context)
                self.patch(p0blocks, "run_spec", run_spec)
                self.patch(p0blocks.rigrun, "now", rig.now)
                self.patch(p0blocks.p0peer, "quiet_b", lambda rep: order.append("quiet"))
                args = p0load.build_parser().parse_args(["floor"])
                session = p0blocks.Session(args, "S" + name[2], Rep(), {"build": "b1"}, [baseline()])
                specs = [dict(spec, peer=peer) for spec in p0plans.floor_plan()[:1]]
                self.assertEqual(session.block(specs), 0)
                self.assertEqual(order, expected)

    def test_a_stepped_run_gets_the_peer_hooks(self):
        calls = []

        def run_window(env, warmup, measure, rep, **hooks):
            calls.append(sorted(hooks))
            raise RuntimeError("Play ended during the measurement")
        self.patch(p0stepped.rigrun, "run_window", run_window)
        self.patch(p0blocks.p0peer, "RemoteB", test_p0peer.FakeB)
        pairs = p0stepped.run_stepped(dict(stepped_spec(), peer=PEER), Rep(), {"build": "b1"})
        self.assertEqual(calls, [["after_play", "before_play", "on_play", "stop_when"]])
        self.assertEqual([row["accepted"] for row, _ in pairs], [False, False])


if __name__ == "__main__":
    unittest.main()
