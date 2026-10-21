"""Unit tests for regress_unfreeze (`splitcentral_unfreeze`, DESIGN 0.85) against test_regress_central's fake rig: the
central's two lifetimes rendered per anchor, the child's kill and the process counts answered by ps, the store file
(python -m unittest discover -s Tools/ServerMeshing -p "test_*.py")."""
import os
import tempfile
import unittest
from unittest import mock

import regress_central
import regress_merge
import regress_split
import regress_splitcrash
import regress_unfreeze
import rigcentral
import test_regress_central as tcen
import test_regress_split as ts
import test_regress_splitcrash as tc

MAP = ts.MAP
P, K, R = tc.P, tc.K, tc.R
INC1, INC2 = tcen.INC1, tcen.INC2
L, apply = tcen.L, tcen.apply
COLLAPSE = ["requested C C.1 collapse", f"e1 broadcast {INC2}/5", "e1 gate open", "e2 staged", "e2 acked", f"e2 broadcast {INC2}/6",
            "C.1 exited before drained", "channel C.1 removed", f"e3 broadcast {INC2}/7", f"tidy broadcast {INC2}/8", "done"]


def central():
    first = [tcen.summary(INC1, "AdoptStatic", "1a2b/1", 0, "start"), L(5, "Split split-1: requested C x 0 + child C.1"), L(40, "Split split-1: done")]
    second = [tcen.summary(INC2, "Waiting", "1a2b/4", 20, "kill"),
              L(80, f"Coordinator recovery: map {MAP} adopted layout 1a2b/4 from the store with 2 entries (C, C.1)", anchor="kill"),
              L(80, f"Coordinator recovery: family C of map {MAP} frozen: no hello from C.1 after 60 s", "ERROR", anchor="kill"),
              L(90, f"Unfreeze C: C.1 declared dead (no hello 70 s after the central started); incarnations started before {INC2} fenced", "WARN", anchor="kill"),
              L(90, f"Unfreeze C: family C of map {MAP} unfrozen by the operator: C.1 declared dead", anchor="kill"),
              L(90, "Unfreeze C: C.1 collapses into C", anchor="kill")]
    second += [L(91 + i, f"Merge merge-1: {s}", anchor="kill") for i, s in enumerate(COLLAPSE)]
    second.append(L(120, "Login routing: meshbotChar at (60.00, 0.08, 70.00) -> channel C (client asked for 'C', row says 'C')", anchor="kill"))
    return first + second


def lines():
    out = {"C2": [tc.own("C2")]}
    for zone, _ in ts.ZONES:
        if zone in ("C", "C2"):
            continue
        x, z = ts.WORLD[zone]
        rect = (float(x), float(z), 200.0, 200.0)
        out[zone] = ([tc.own(zone)] + [apply(zone, INC1, v, rect, rect) for v in (1, 2, 3, 4)]
                     + [apply(zone, INC2, v, rect, rect, 92 + v, "kill") for v in (5, 6, 7, 8)])
    out["C"] = ([tc.own("C"), apply("C", INC1, 1, P, P), apply("C", INC1, 2, P, P), apply("C", INC1, 3, K, P), apply("C", INC1, 4, K, K),
                 apply("C", INC2, 5, K, P, 93, "kill"), apply("C", INC2, 6, P, P, 96, "kill"), apply("C", INC2, 7, P, P, 99, "kill"),
                 apply("C", INC2, 8, P, P, 100, "kill"), L(97, "Retired zone C.1: link closed, 0 record(s) released", anchor="kill"),
                 L(125, "10:04:00.000 S player registered meshbotChar conn=0 at (60, 0, 70)", tag="MeshTrace", anchor="kill")])
    out["C.1"] = [tc.own_child(), apply("C.1", INC1, 2, None, R), apply("C.1", INC1, 3, R, R), apply("C.1", INC1, 4, R, R)]
    return out


def store(**changes):
    data = {"format": 1, "broadcast": {"pair": {"coordinatorIncarnation": 0x1a2c, "version": 8}, "entries": []}, "hasStaged": False,
            "hasOperation": False, "fencedIncarnations": [{"cellId": "C.1", "incarnation": INC2}]}
    data.update(changes)
    return data


class FakeUnfreezeRig(tcen.FakeCentralRig):
    """The central fake with the kills answered without moving its anchor (the flow sets it), and the command recorded."""
    def __init__(self, folder, **kwargs):
        kwargs.setdefault("lines", lines())
        kwargs.setdefault("central", central())
        kwargs.setdefault("store", store())
        kwargs.setdefault("counts", ["0"])
        super().__init__(folder, "splitcentral_unfreeze", **kwargs)
        self.unfreeze_command = None

    def ps(self, script):
        self.scripts.append(script)
        if "Stop-Process" in script:
            return self.kill_out
        return self.counts.pop(0) if len(self.counts) > 1 else self.counts[0]

    def tick(self):
        if os.path.exists(self.LAYOUT_COMMAND):
            with open(self.LAYOUT_COMMAND, encoding="utf-8") as f:
                command = f.read().strip()
            if " unfreeze " in command:
                os.remove(self.LAYOUT_COMMAND)
                self.unfreeze_command = command
                return
        super().tick()


def run(**rig_args):
    with tempfile.TemporaryDirectory() as d:
        rig = FakeUnfreezeRig(d, **rig_args)
        clock = ts.FakeClock(rig)
        clock.now = tc.T0.timestamp()
        rep, bots, started = ts.Report(), [], []

        def kill_central(r):
            rig.kill_at = clock.time()
            return 1
        modules = (regress_split, regress_merge, regress_splitcrash, regress_central, regress_unfreeze)
        patches = [mock.patch.object(m.time, "time", clock.time) for m in modules]
        patches += [mock.patch.object(m.time, "sleep", clock.sleep) for m in modules]
        patches += [mock.patch.object(regress_split.provision, "reset_bot_positions"),
                    mock.patch.object(regress_split.rigtools, "asset_cell_modes", lambda path: {}),
                    mock.patch.object(regress_unfreeze.regress_procs, "kill_central", kill_central),
                    mock.patch.object(regress_unfreeze.regress_procs, "start_central", lambda r, name: started.append(clock.time()) or 2),
                    mock.patch.object(regress_unfreeze.regress_procs, "wait_central", lambda r: True)]
        for p in patches:
            p.start()
        try:
            cr = regress_central.run("splitcentral_unfreeze", rep, rig, "10:00:00", "2026-10-16", bots)
            regress_central.grade_layout(rep, rig, "10:00:00", "2026-10-16", cr)
            rig.tick()   # the command file is read like the watcher reads it (the flow may not have slept since)
        finally:
            for p in patches:
                p.stop()
        return rep, rig, bots, cr, started


class UnfreezeScenarioTests(unittest.TestCase):
    def test_passes(self):
        rep, rig, bots, cr, started = run()
        self.assertEqual(rep.failed(), [])
        self.assertEqual(len(started), 1)
        self.assertAlmostEqual(started[0] - rig.kill_at, regress_central.CENTRAL_DOWN_S, delta=1.0)
        self.assertRegex(rig.unfreeze_command, r"^\d+ unfreeze C$")
        self.assertFalse(rig.calls[1][5], "no tester")
        self.assertEqual(rig.calls[1][2:4], (60.0, 70.0), "the fixture bot's row is put in R")
        kills = [s for s in rig.scripts if "Stop-Process" in s]
        self.assertEqual(len(kills), 1)
        self.assertIn("C.1", kills[0])
        launches = [c for c in rig.calls if c[0] == "launch_bot"]
        self.assertEqual(launches[-1][1], "meshbot")
        self.assertTrue(all(float(c[2][c[2].index("-meshWalkX") + 1]) < 0 for c in launches[:-1]), "every stand bot in the kept half")
        self.assertEqual(cr.merge_pairs, {"e1": f"{INC2}/5", "e2": f"{INC2}/6", "e3": f"{INC2}/7", "tidy": f"{INC2}/8"})

    def test_failures(self):
        base_central, base_lines = central(), lines()
        no_freeze = [t for t in base_central if "family C of map" not in t[2] or "unfrozen" in t[2]]
        no_done = [t for t in base_central if "merge-1: done" not in t[2]]
        map_frozen = base_central + [L(81, f"Coordinator recovery: map {MAP} frozen: cells C and E hold different layouts at 1a2b/4", "ERROR", anchor="kill")]
        refused = base_central + [L(95, "Layout op refused: family C is not frozen", "WARN", anchor="kill")]
        to_child = [t if "-> channel C " not in t[2] else (t[0], t[1], t[2].replace("-> channel C ", "-> channel C.1 "), t[3], t[4]) for t in base_central]
        early = dict(base_lines, C=base_lines["C"] + [apply("C", INC2, 5, P, P, 93, "kill")])
        cases = {
            "the family never froze": ({"central": no_freeze}, ["froze family C within"]),
            "the collapse never ended": ({"central": no_done}, ["reached done within"]),
            "the map froze": ({"central": map_frozen}, ["only the family froze"]),
            "a refusal": ({"central": refused}, ["no 'Layout op refused'"]),
            "routed to the dead child": ({"central": to_child}, ["routed to C ("]),
            "the exit before drained missing": ({"central": [t for t in base_central if "before drained" not in t[2]]}, ["gone before drained"]),
            "the child came back": ({"counts": ["0", "1", "0"]}, ["C.1 never came back"]),
            "C owned R before the collapse's e2": ({"lines": early}, ["owned only its kept half"]),
            "the store kept no fence": ({"store": store(fencedIncarnations=[])}, ["fences C.1"]),
            "a kicked bot": ({"results": {"meshbot7": "migrations=0 allReady=True maxGap=0 maxJump=0 relogins=1"}}, ["meshbot7: migrations 0, relogins 0"]),
        }
        for name, (args, fragments) in cases.items():
            with self.subTest(name):
                rep, _, _, _, _ = run(**args)
                tc.assert_fails(self, rep, fragments)


class UnfreezeGraderTests(unittest.TestCase):
    def test_kept_plan_puts_every_bot_in_the_kept_half(self):
        plan = regress_unfreeze.kept_plan()
        self.assertEqual([b.user for b in plan], [f"meshbot{k}" for k in range(2, 10)])
        self.assertTrue(all(-100.0 < b.point[0] < 0.0 and b.side == "-" for b in plan))
        self.assertEqual(len({b.point for b in plan}), 8)

    def test_unfreeze_lines_and_frozen_families(self):
        log = [" INFO Mesh [2026-10-16 10:01:00] - Unfreeze C: C.1 collapses into C",
               f" ERROR Mesh [2026-10-16 10:01:00] - Coordinator recovery: family C of map {MAP} frozen: no hello from C.1 after 60 s",
               " INFO Mesh [2026-10-16 10:01:00] - Unfreeze E: split-1 frozen at DrainGate (x)"]
        self.assertEqual(rigcentral.unfreeze_lines(log), [("C", "C.1 collapses into C"), ("E", "split-1 frozen at DrainGate (x)")])
        self.assertEqual(rigcentral.unfreeze_lines(log, "E"), [("E", "split-1 frozen at DrainGate (x)")])
        self.assertEqual(rigcentral.frozen_families(log), [("C", MAP, "no hello from C.1 after 60 s")])

    def test_expected_lives_unfreeze(self):
        split = regress_split.split_of("split", MAP)
        mp = {"e1": "a/5", "e2": "a/6", "e3": "a/7", "tidy": "a/8"}
        base = rigcentral.expected_lives("unfreeze", split, "C", None, {"e1": "a/2", "e2": "a/3", "e3": "a/4"}, mp)
        self.assertEqual(len(base), 1)
        self.assertEqual([p for _, _, p in base[0]][-4:], ["a/5", "a/6", "a/7", "a/8"])
        child = rigcentral.expected_lives("unfreeze", split, "C.1", None, {"e1": "a/2", "e2": "a/3", "e3": "a/4"}, mp)
        self.assertEqual([p for _, _, p in child[0]], ["a/2", "a/3", "a/4"], "the killed child applies nothing more")


if __name__ == "__main__":
    unittest.main()
