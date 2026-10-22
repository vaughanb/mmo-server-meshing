"""Unit tests for regress_policy's flows against a fake rig whose central reacts to the burn file on the fake clock
(python -m unittest discover -s Tools/ServerMeshing -p "test_*.py"); no editor, process or database is touched."""
import os
import tempfile
import unittest
from unittest import mock

import regress_policy
import regress_split
import riglayout
import rigsplit
import test_regress_split as ts

MAP, line, own = ts.MAP, ts.line, ts.own


def split_lines(op, cell, child, at, first):
    v = first
    return [f"Split {op}: requested {cell} x {at:g} + child {child}", f"Split {op}: e1 broadcast 1a2b/{v}",
            f"Split {op}: e2 broadcast 1a2b/{v + 1}", f"Split {op}: drain gate open", f"Split {op}: e3 broadcast 1a2b/{v + 2}",
            f"Split {op}: done"]


def planned(cell, child, at):
    return f"Policy: split of {cell} planned: x {at:g} + child {child}, util 0.9 -> peak 0.9 ({cell} 0.9, {child} 0.9), 2 cut(s) tried"


def measured(op, cell, child):
    return f"Policy: split {op} measured: predicted peak 0.9, measured 0.47 ({cell} 0.47, {child} 0.06), relief 48 %"


def world(splits):
    """{zone: lines} of the cells after the splits (cell, child, at) in order, pairs from 1a2b/2 up."""
    ops = [riglayout.split_op(c, k, "x", at, "+", {"e1": f"1a2b/{2 + 3 * i}", "e2": f"1a2b/{3 + 3 * i}", "e3": f"1a2b/{4 + 3 * i}"})
           for i, (c, k, at) in enumerate(splits)]
    lines = {"C2": [own("C2", ts.MAP2)]}
    for cell, applies in riglayout.timeline(riglayout.TEST_MAP_RECTS, ops).items():
        head = [line(f"Own cell from layout: {cell} of map {MAP}, grid (0,0), world x [0, 100) z [0, 100), 3 neighbours")] \
            if "." in cell else [own(cell)]
        lines[cell] = head + [line(f"Layout {p or '1a2b/1'} applied: {cell} owns {rigsplit.format_rect(o)} interest "
                                   f"{rigsplit.format_rect(o if i == riglayout.ANY else i)}, 8 targets")
                              for o, i, p in applies]
    return lines


class FakeCentral(ts.FakeRig):
    """The rig of a policy scenario: on every tick the 'central' reads the burn file and appends `auto` lines by a
    script of (predicate on the burn text and seconds since it changed, lines), each fired once."""

    def __init__(self, folder, script, lines):
        super().__init__(folder, "split", lines=lines)
        self.TEMP = folder
        self.script, self.fired, self.burn, self.changed, self.now = list(script), set(), None, 0.0, 0.0

    def request_play(self, mode, x, z, env=None, enabled=True, **kwargs):
        super().request_play(mode, x, z, env=env, enabled=enabled)

    def graded_zones(self, day):
        return list(ts.ZONES) + [(z, MAP) for z in self.lines if "." in z]

    def tick(self):
        self.now += 1
        path = os.path.join(self.TEMP, "ServerMesh.ProbeBurn.txt")
        text = ""
        if os.path.exists(path):
            with open(path, encoding="utf-8") as f:
                text = f.read()
        if text != self.burn:
            self.burn, self.changed = text, self.now
        for i, (when, out) in enumerate(self.script):
            if i not in self.fired and when(self.burn, self.now - self.changed):
                self.fired.add(i)
                with open(self.LAYOUT_COMMAND_STATUS, "a", encoding="utf-8") as f:
                    f.write("".join(f"auto {t}\n" for t in out))


class Clock(ts.FakeClock):
    def sleep(self, seconds):
        for _ in range(max(1, int(seconds))):
            self.now += 1
            self.rig.tick()


QUEUE = [(lambda b, t: b == "C:14,E:14" and t >= 25, [planned("C", "C.1", 0)] + split_lines("split-1", "C", "C.1", 0, 2)),
         (lambda b, t: b == "C:7,E:14" and t >= 22, [planned("E", "E.1", 200)] + split_lines("split-2", "E", "E.1", 200, 5)),
         (lambda b, t: b == "C:7,E:7" and t >= 10, [measured("split-1", "C", "C.1"), measured("split-2", "E", "E.1")])]
BUSY = "C:14," + ",".join(f"{c}:24" for c in regress_policy.BUSY_CELLS)
DEFER = [(lambda b, t: b == BUSY and t >= 25, ["Policy: split of C deferred: host BOX at 13.7/12 cores",
                                               "Policy: split of E deferred: host BOX at 14.2/12 cores"]),
         (lambda b, t: b == BUSY and t >= 85, ["Policy: split of C deferred: host BOX at 13.6/12 cores"]),
         (lambda b, t: b == "C:14" and t >= 15, [planned("C", "C.1", 0)] + split_lines("split-1", "C", "C.1", 0, 2)),
         (lambda b, t: b == "C:7" and t >= 10, [measured("split-1", "C", "C.1")])]


def run(name, script, lines):
    with tempfile.TemporaryDirectory() as d:
        rig = FakeCentral(d, script, lines)
        clock = Clock(rig)
        rep = ts.Report()
        with mock.patch.object(regress_split.time, "time", clock.time), \
                mock.patch.object(regress_split.time, "sleep", clock.sleep), \
                mock.patch.object(regress_split.rigtools, "asset_cell_modes", lambda path: {}):
            pr = regress_policy.run(name, rep, rig, "10:00:00", "2026-10-21", [])
            regress_policy.grade_layout(rep, rig, "10:00:00", "2026-10-21", pr)
        return rep, rig, pr


def assert_fails(test, rep, fragments):
    failed = rep.failed()
    for fragment in fragments:
        test.assertTrue(any(fragment in label for label in failed), f"{fragment!r} not in {failed}")


class AutoQueueTests(unittest.TestCase):
    LINES = world([("C", "C.1", 0.0), ("E", "E.1", 200.0)])

    def test_passes(self):
        rep, rig, pr = run("autoqueue", QUEUE, self.LINES)
        self.assertEqual(rep.failed(), [])
        self.assertEqual([s.child for s in pr.splits], ["C.1", "E.1"])
        self.assertEqual(rig._play_env["MESH_AUTO_OPS"], "split")
        self.assertNotIn("MESH_HOST_CORES", rig._play_env)

    def test_failures(self):
        early = [(lambda b, t: b == "C:14,E:14" and t >= 25, QUEUE[0][1][:1] + QUEUE[0][1][1:-1] + [planned("E", "E.1", 200)]
                  + QUEUE[0][1][-1:] + split_lines("split-2", "E", "E.1", 200, 5)), QUEUE[2]]
        refused = [QUEUE[0], (QUEUE[1][0], ["Layout op refused: split-1 is in progress"] + QUEUE[1][1]), QUEUE[2]]
        cases = {
            "the second planned during the first": (early, ["planned after"]),
            "a refused request": (refused, ["no request of the policy refused", "busy slot"]),
            "only one cell split": (QUEUE[:1], ["two splits"]),
        }
        for name, (script, fragments) in cases.items():
            with self.subTest(name):
                assert_fails(self, run("autoqueue", script, self.LINES)[0], fragments)


class AutoDeferTests(unittest.TestCase):
    LINES = world([("C", "C.1", 0.0)])

    def test_passes(self):
        rep, rig, pr = run("autodefer", DEFER, self.LINES)
        self.assertEqual(rep.failed(), [])
        self.assertEqual(rig._play_env["MESH_HOST_CORES"], "12")
        self.assertTrue(any("deferred at 13.6/12" in n for n in rep.notes), rep.notes)

    def test_failures(self):
        planned_busy = [(lambda b, t: b == BUSY and t >= 25, [planned("C", "C.1", 0)] + split_lines("split-1", "C", "C.1", 0, 2))]
        cases = {
            "never deferred": (planned_busy, ["deferred with the host", "stayed deferred"]),
            "deferred once only": (DEFER[:1] + DEFER[2:], ["stayed deferred"]),
            "never planned": (DEFER[:2], ["planned once the other cells' burn was removed"]),
            "an E split": (DEFER[:2] + [(DEFER[2][0], [planned("E", "E.1", 200)] + split_lines("split-1", "E", "E.1", 200, 2))], ["C was planned"]),
        }
        for name, (script, fragments) in cases.items():
            with self.subTest(name):
                assert_fails(self, run("autodefer", script, self.LINES)[0], fragments)


if __name__ == "__main__":
    unittest.main()
