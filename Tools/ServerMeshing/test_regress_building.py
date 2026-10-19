"""Unit tests for regress_building.py (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py"): the splitbuild
flow against a fake rig built on test_regress_merge's (a split, a merge, the kill of C and its second lifetime)."""
import tempfile
import unittest
from unittest import mock

import regress_building
import regress_merge
import regress_split
import regress_splitcrash
import rigbuilding
import test_regress_merge as tm
import test_regress_split as ts
from test_regress_split import MAP, apply, line, own

W, P = "wall-1", (-100.0, -100.0, 200.0, 200.0)
RESULT = ("RESULT buildtree: placed=3 root=wall-1 storageBefore=C storageAfter=C.1 doorOpened=True movesOk=12 "
          "movesRefused=3 movesFailed=0 scrollsBag=19 scrollsStorage=1 total=20 buildingsAfterRelogin=3 storageAfterRelogin=1 "
          "migrations=2 allReady=True maxGap=310 maxJump=0.2 relogins=1 -> PASS")
LOADED = line("Buildings loaded on C: 3 placed in 1 tree(s) and 0 scene, 0 tree(s) owned elsewhere, 0 scene row(s) of other cells skipped, 0 already here (channel C, 3 row(s))")
ROWS = [("wall-1", "", "C"), ("chest-1", "wall-1", "C"), ("door-1", "wall-1", "C")]


def transfer(root, a, b, what):
    return line(f"Building transfer {root} zone {a}->{b}: {what}")


def building_lines(sent_nodes=3, back=True):
    """test_regress_merge's passing lines for `merge`, plus the tree's two hand-overs and C's peer lines."""
    lines = tm.passing_lines("merge")
    lines["C"] = lines["C"] + [transfer(W, "C", "C.1", f"sent ({sent_nodes} node(s))"), transfer(W, "C", "C.1", "done")]
    lines["C.1"] = lines["C.1"] + [transfer(W, "C", "C.1", "promoted 0, spawned 3, owned already 0")]
    if back:
        lines["C.1"] += [transfer(W, "C.1", "C", "sent (3 node(s))"), transfer(W, "C.1", "C", "done")]
        lines["C"] += [transfer(W, "C.1", "C", "promoted 1, spawned 2, owned already 0")]
    return lines


class FakeBuildRig(tm.FakeMergeRig):
    """The merge rig plus: the tester's status (phase, then finished with its RESULT), the database rows, the kill of
    C (ps with Stop-Process) after which C's lines gain a second lifetime with its load line."""

    def __init__(self, folder, lines=None, rows=None, result=RESULT, loaded=LOADED, second_life=True, results=None, **kw):
        bots = {f"meshbot{n}": "migrations=2 allReady=True maxGap=300ms maxJump=0.2m relogins=1" for n in range(2, 6)}
        bots.update({f"meshbot{n}": "migrations=0 allReady=True maxGap=0ms maxJump=0m relogins=1" for n in range(6, 10)})
        bots.update(results or {})
        super().__init__(folder, "merge", lines=building_lines() if lines is None else lines, results=bots, **kw)
        self.rows, self.result, self.loaded, self.second_life = list(ROWS) if rows is None else rows, result, loaded, second_life
        self.killed, self.plays, self.resets, self.queries, self.clock = False, [], [], [], None

    def db_reset(self, tester, bot, clear_buildings=False):
        self.resets.append((tester, bot, clear_buildings))

    def request_play(self, mode, x, z, env=None, enabled=True, observe_seconds=None, line_x=None):
        self._play_env = dict(env or {})
        self.plays.append((mode, x, z, self._play_env, enabled, observe_seconds, line_x))
        self.playing_since = 0.0

    def status_now(self):
        if self.killed:
            return {"isPlaying": "True", "phase": "relogin-buildtree-checked", "finished": "True", "result": self.result}
        if len(self.commands) >= 1:
            return {"isPlaying": "True", "phase": "buildtree-checked", "finished": "False", "result": ""}
        return {"isPlaying": "True", "phase": "buildtree-ready", "finished": "False", "result": ""}

    def wait_status(self, pred, timeout, what, busy_cap=0):
        for _ in range(60):
            st = self.status_now()
            if pred(st):
                return st
            self.clock.sleep(2)
        raise TimeoutError(f"timed out waiting for {what}")

    def db_rows(self, sql, params=()):
        self.queries.append((sql, params))
        if "from buildings" in sql:
            return list(self.rows)
        return [(1,)]

    def ps(self, script):
        if "Stop-Process" in script:
            self.killed = True
            self.scripts.append(script)
            return "1"
        return super().ps(script)

    def zone_lines(self, zone, start, day, map_id=MAP, suffixes=("info", "warn", "error")):
        lines = super().zone_lines(zone, start, day, map_id, suffixes)
        if zone == "C" and self.killed and self.second_life:
            lines = lines + [own("C"), apply("C", 9, P, P)] + ([self.loaded] if self.loaded else [])
        return lines


def run(**rig_args):
    with tempfile.TemporaryDirectory() as d:
        rig = FakeBuildRig(d, **rig_args)
        clock = ts.FakeClock(rig)
        rig.clock = clock
        rep, bots = ts.Report(), []
        patches = [mock.patch.object(m.time, "time", clock.time) for m in (regress_building, regress_split, regress_merge, regress_splitcrash)]
        patches += [mock.patch.object(m.time, "sleep", clock.sleep) for m in (regress_building, regress_split, regress_merge, regress_splitcrash)]
        patches += [mock.patch.object(regress_split.provision, "reset_bot_positions"),
                    mock.patch.object(regress_split.rigtools, "asset_cell_modes", lambda path: {})]
        for p in patches:
            p.start()
        try:
            br = regress_building.run("splitbuild", rep, rig, "10:00:00", "2026-10-16", bots)
            regress_building.grade_layout(rep, rig, "10:00:00", "2026-10-16", br)
        finally:
            for p in patches:
                p.stop()
        return rep, rig, bots, br


class SplitBuildTests(unittest.TestCase):
    def test_passes(self):
        rep, rig, bots, br = run()
        self.assertEqual(rep.failed(), [])
        self.assertEqual([c.split()[1:] for c in rig.commands], [["split", "C", "x", "0", "+", "C.1"], ["merge", "C", "C.1"]])
        self.assertEqual(len(bots), 8, "no harvest bot")
        self.assertEqual(rig.resets, [((60.0, 70.0), (60.0, 70.0), True)])
        self.assertEqual(rig.plays[0][:3], ("buildtree", 60.0, 70.0))
        self.assertEqual((rig.plays[0][5], rig.plays[0][6]), (regress_building.OBSERVE_S, 0.0))
        self.assertEqual(rig.plays[0][3].get("MESH_NO_MONSTERS"), "1")
        self.assertNotIn("MESH_LOAD_PLAN", rig.plays[0][3])
        self.assertTrue(rig.killed)
        self.assertTrue(any("Stop-Process" in s and "-channelId C *" in s for s in rig.scripts))
        self.assertTrue(any("from buildings" in q[0] for q in rig.queries))
        self.assertTrue(any("storageitem" in q[0] for q in rig.queries))
        self.assertEqual(br.merge_status["op"], "merge-2")

    def test_failures(self):
        cases = {
            "the tree never reached the child": ({"lines": building_lines(sent_nodes=2)}, "split: C handed the tree"),
            "the tree never came back": ({"lines": building_lines(back=False)}, "merge: C.1 handed the tree"),
            "a fourth row": ({"rows": ROWS + [("stray", "", "C")]}, "building row(s)"),
            "a row under the child's channel": ({"rows": [ROWS[0], ("chest-1", "wall-1", "C.1"), ROWS[2]]}, "building row(s)"),
            "the restarted base loaded nothing": ({"loaded": None}, "second lifetime loaded exactly"),
            "the base never came back": ({"second_life": False}, "came back and applied a layout"),
            "a scroll lost": ({"result": RESULT.replace("total=20", "total=19")}, "the tester:"),
            "the storage opened on the wrong cell": ({"result": RESULT.replace("storageAfter=C.1", "storageAfter=C")}, "the tester:"),
            "a bot without a re-login": ({"results": {"meshbot2": "migrations=2 allReady=True maxGap=300ms maxJump=0.2m relogins=0"}}, "meshbot2:"),
        }
        for name, (args, fragment) in cases.items():
            with self.subTest(name):
                rep = run(**args)[0]
                failed = rep.failed()
                self.assertTrue(any(fragment in label for label in failed), (fragment, failed))

    def test_a_note_when_the_relogin_missed_the_window(self):
        rep = run(result=RESULT.replace("buildingsAfterRelogin=3", "buildingsAfterRelogin=-1").replace("storageAfterRelogin=1", "storageAfterRelogin=-1"))[0]
        self.assertEqual(rep.failed(), [])
        self.assertTrue(any("did not finish" in n for n in rep.notes), rep.notes)

    def test_registration(self):
        self.assertEqual(regress_building.SCENARIOS, ("splitbuild",))
        self.assertIn("spawner", regress_building.TWO_HOST_REASON)
        self.assertEqual(rigbuilding.SCROLLS, 20)
        self.assertGreaterEqual(regress_building.OBSERVE_S, regress_merge.MERGE_OBSERVE_S)


if __name__ == "__main__":
    unittest.main()
