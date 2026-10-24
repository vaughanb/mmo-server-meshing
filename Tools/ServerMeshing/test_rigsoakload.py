"""Unit tests for rigsoakload's burn plan and readers (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py")."""
import unittest

import riglayout
import rigsoakload as L

WHOLE = {b: [b] for b in riglayout.TEST_MAP_RECTS}


def fams(**split):
    out = dict(WHOLE)
    for base, members in split.items():
        out[base] = members
    return out


class BurnPlanTests(unittest.TestCase):
    def plan(self, episodes):
        return L.BurnPlan(episodes, hold_s=100, heat_s=300, cool_s=200)

    def test_single_episode_heats_warms_cools_and_picks_the_next(self):
        p = self.plan([L.Episode("single", ("C",)), L.Episode("single", ("E",))])
        self.assertEqual(p.step(0, WHOLE), {"C": 14})
        self.assertEqual(p.step(10, WHOLE), {"C": 14})
        self.assertEqual(p.step(20, fams(C=["C", "C.1"])), {"C": 7, "C.1": 7})
        self.assertEqual(p.step(119, fams(C=["C", "C.1"])), {"C": 7, "C.1": 7})
        self.assertEqual(p.step(120, fams(C=["C", "C.1"])), {})
        self.assertEqual(p.step(150, fams(C=["C", "C.1"])), {})
        self.assertEqual(p.step(160, WHOLE), {})          # whole again: the next tick picks
        self.assertEqual(p.step(162, WHOLE), {"E": 14})
        self.assertEqual((p.started, p.completed, p.timed_out), (2, 1, 0))
        self.assertTrue(any("episode 1 single C: heat" in t for _, t in p.events))

    def test_deep_heats_the_child_with_its_parent_warm(self):
        p = self.plan([L.Episode("deep", ("C",))])
        self.assertEqual(p.step(0, WHOLE), {"C": 14})
        self.assertEqual(p.step(5, fams(C=["C", "C.1"])), {"C": 7, "C.1": 14})
        self.assertEqual(p.step(6, fams(C=["C", "C.1"])), {"C": 7, "C.1": 14})
        self.assertEqual(p.step(9, fams(C=["C", "C.1", "C.2"])), {"C": 7, "C.1": 7, "C.2": 7})

    def test_pair_waits_for_both_and_skips_split_families(self):
        p = self.plan([L.Episode("single", ("C",)), L.Episode("pair", ("E", "N"))])
        self.assertEqual(p.step(0, fams(C=["C", "C.1"])), {"E": 14, "N": 14})   # C is not whole: skipped
        self.assertEqual(p.step(5, fams(C=["C", "C.1"], E=["E", "E.1"])), {"E": 7, "E.1": 7, "N": 14})   # E split: warm
        self.assertEqual(p.step(9, fams(E=["E", "E.1"], N=["N", "N.1"])), {"E": 7, "E.1": 7, "N": 7, "N.1": 7})

    def test_timeouts_and_nothing_to_heat(self):
        p = self.plan([L.Episode("single", ("C",))])
        p.step(0, WHOLE)
        self.assertEqual(p.step(301, WHOLE), {})          # no split in 300 s: cut short, cooling
        self.assertEqual(p.timed_out, 1)
        p.step(302, WHOLE)                                # whole: back to pick (not counted completed)
        self.assertEqual(p.completed, 0)
        q = self.plan([L.Episode("single", ("C",))])
        self.assertEqual(q.step(0, fams(C=["C", "C.1"])), {})
        self.assertEqual(q.phase, "cool")
        self.assertEqual(q.step(201, fams(C=["C", "C.1"])), {})   # never whole: cut short, picks again
        self.assertEqual((q.timed_out, q.phase), (1, "pick"))

    def test_drain_turns_the_burn_off_for_good(self):
        p = self.plan([L.Episode("single", ("C",))])
        p.step(0, WHOLE)
        p.drain(5)
        self.assertEqual(p.step(6, fams(C=["C", "C.1"])), {})
        self.assertEqual(p.step(7, WHOLE), {})
        self.assertEqual(p.step(8, WHOLE), {})
        self.assertEqual(p.phase, "cool")


class BotAndProcessTests(unittest.TestCase):
    def test_bot_lines(self):
        lines = ["[MeshAuto] Patrolling between x=-70 and x=70 at z=-30 for 1000s",
                 "[MeshAuto] MIGRATION 1: gap=311 ready=True jump=0.41 pos=70,-80", "[MeshAuto] Patrol leg 1 to x=70: reached at 70,-30",
                 "[MeshAuto] Session lost (Timeout); re-login 1", "[MeshAuto] Re-login 1: Player never spawned; trying again in 2s",
                 "[MeshAuto] Re-login 1 gave up after 120s: Player never spawned", "[MeshAuto] Patrol leg 2 to x=-70: missed at 3,-30",
                 "[MeshAuto] RESULT patrol: legs=2 missed=1 migrations=1 allReady=True maxGap=311 maxJump=0.41 relogins=1 -> PASS"]
        s = L.bot_lines_summary(lines)
        self.assertEqual((s["migrations"], s["legs"], s["missed"], s["losses"], s["retries"], s["gave_up"]), (1, 2, 1, ["Timeout"], 1, 1))
        self.assertTrue(s["result"].startswith("RESULT patrol: legs=2"))

    def test_relogins_explained_by_kills(self):
        losses = [(100.0, "meshbot2", "Timeout"), (500.0, "meshbot3", "Timeout"), (95.0, "meshbot4", "x")]
        self.assertEqual(L.unexplained_relogins(losses, [90.0], 60.0), [(500.0, "meshbot3", "Timeout")])

    def test_roles_and_rows(self):
        exe = "D:/x/kit-ce-project.exe"
        self.assertEqual(L.role_of(f"{exe} -batchmode -startDatabaseServer -startCentralServer -startLoginServer -logFile a"), "central")
        self.assertEqual(L.role_of(f"{exe} -batchmode -startMapSpawnServer -centralAddress 1"), "spawner")
        self.assertEqual(L.role_of(f"{exe}  -channelId C.1  -mapName ServerMeshTestMap"), "cell:C.1")
        self.assertEqual(L.role_of(f"{exe} -batchmode -meshAutoClient -meshUser meshbot6 -meshChannel C"), "bot:meshbot6")
        self.assertIsNone(L.role_of("notepad.exe"))
        rows = L.parse_process_rows(f"12\t209715200\t104857600\t{exe} -channelId E -mapName M\n13\tx\t1\t{exe} -channelId C\n"
                                    "garbage\n14\t1\t1\tnotepad")
        self.assertEqual(rows, [(12, "cell:E", 200.0, 100.0)])
        series = L.process_series([(0.0, rows), (60.0, [(12, "cell:E", 201.0, 100.0)])])
        self.assertEqual(series, {("cell:E", 12): [(0.0, 200.0), (60.0, 201.0)]})

    def test_stats_memory_per_lifetime(self):
        def stats(t, mb):
            return f" INFO Mesh [2026-10-23 10:00:{t:02d}] - Mesh stats: players=1 owned=1 | frame mean 1 ms cpu 3% mem {mb} MB clients=0"
        own = " INFO Mesh [2026-10-23 10:00:00] - Own cell from layout: C.1 of map ServerMeshTestMap, grid (0,0), world x [0, 100) z [0, 100), 3 neighbours"
        lives = L.stats_memory("C.1", [own, stats(10, 180), stats(20, 182), own, stats(30, 170)])
        self.assertEqual([[mb for _, mb in life] for life in lives], [[180.0, 182.0], [170.0]])
        self.assertEqual(lives[0][1][0] - lives[0][0][0], 10.0)


if __name__ == "__main__":
    unittest.main()
