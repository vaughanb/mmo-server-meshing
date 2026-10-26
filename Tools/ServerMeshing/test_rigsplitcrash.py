"""Unit tests for rigsplitcrash (Slice 2d's pure graders) and the Slice 2d additions to rigsplit and rigmerge
(python -m unittest discover -s Tools/ServerMeshing -p "test_*.py")."""
import datetime
import unittest

import rigmerge
import rigsplit
import rigsplitcrash

MAP = "ServerMeshTestMap"
T0 = datetime.datetime(2026, 10, 17, 10, 0, 0)
SPLIT = rigsplit.Split(MAP, "C", (-100.0, -100.0, 200.0, 200.0), "x", 0.0, "+", "C.1")
P, K, R = SPLIT.base_rect, (-100.0, -100.0, 100.0, 200.0), (0.0, -100.0, 100.0, 200.0)
N_RECT = (-100.0, 100.0, 200.0, 200.0)


def at(t, text, level="INFO", tag="Mesh"):
    return f" {level} {tag} [{(T0 + datetime.timedelta(seconds=t)):%Y-%m-%d %H:%M:%S}] - {text}"


def own(cell, x, z, t=0):
    return at(t, f"Own cell from channel id: {cell} of map {MAP}, grid (0,0), world x [{x}, {x + 200}) z [{z}, {z + 200}), "
                 "8 neighbours")


def own_child(t=0):
    return at(t, f"Own cell from layout: C.1 of map {MAP}, grid (0,0), world x [0, 100) z [-100, 100), 5 neighbours")


def apply(cell, version, owned, interest, t=0):
    return at(t, f"Layout 1a2b/{version} applied: {cell} owns {rigsplit.format_rect(owned)} interest "
                 f"{rigsplit.format_rect(interest)}, 8 targets")


def window(t, owned_monsters):
    return at(t, "Work window 5.00 s: frames 300 work mean 1.000 ms p95 2.00 ms max 3.0 ms wait 10.000 ms period 16.667 "
                 f"ms budget 16.67 ms sat 0.0% probe=Ok | owned players 0 monsters {owned_monsters} | puppets players 0 "
                 "monsters 3 | clients 0 cpu 5%")


class StatusTests(unittest.TestCase):
    DRAIN = ["Split split-1: requested C x 0 + child C.1", "Split split-1: e1 broadcast 1a2b/2",
             "Split split-1: e2 broadcast 1a2b/3", "Split split-1: C.1 lost, op paused",
             "Supervisor: C.1 down 21 s, restart 1 of 3 requested", "Supervisor: C.1 back, incarnation 8dcf02",
             "Split split-1: C.1 rebound 8dcf02, op resumed", "Split split-1: drain gate open",
             "Split split-1: e3 broadcast 1a2b/4", "Split split-1: done"]

    def lines(self, texts, seq="7"):
        return [f"{seq} {t}" for t in texts]

    def test_parse_supervisor(self):
        texts = ["Supervisor: C.1 hung 31.5 s, restart 2 of 3 requested", "Supervisor: C.1 restart 2 failed (no hello in 60 s)",
                 "Supervisor: C.1 collapse queued after 3 failed restart(s)",
                 "Supervisor: C.1 cannot collapse (no live cell of its family shares a full edge); left down", "Supervisor: C.1 killed by its spawner (hung)",
                 "Supervisor: C.1 something new", "Split split-1: done"]
        events = rigsplitcrash.parse_supervisor(self.lines(texts) + ["6 Supervisor: C.1 back, incarnation 01"], "7")
        self.assertEqual([e["event"] for e in events], ["hung", "failed", "collapse", "cannot", "killed", "other"])
        self.assertEqual((events[0]["seconds"], events[0]["n"], events[0]["max"]), (31.5, 2, 3))
        self.assertEqual((events[1]["n"], events[1]["why"]), (2, "no hello in 60 s"))
        self.assertEqual((events[2]["n"], events[4]["why"], events[5]["cell"]), (3, "hung", "C.1"))
        self.assertEqual([e["at"] for e in events], [0, 1, 2, 3, 4, 5])

    def test_crash_status_drain(self):
        status = rigsplitcrash.crash_status(self.lines(self.DRAIN), "7")
        self.assertEqual(rigsplit.op_outcome(status), "done")
        self.assertEqual((status["paused"], status["rebound"], status["blips"]), (["C.1"], [("C.1", "8dcf02")], []))
        self.assertEqual([e["event"] for e in status["supervisor"]], ["down", "back"])
        self.assertEqual(status["supervisor"][1]["incarnation"], "8dcf02")
        self.assertIn("Supervisor: C.1 down 21 s, restart 1 of 3 requested", status["other"])
        self.assertEqual(len(status["texts"]), len(self.DRAIN))

    def test_crash_status_abort_and_ended(self):
        texts = ["Split split-1: e1 staged", "Split split-1: e1 acked", "Split split-1: e1 broadcast 1a2b/2",
                 "Split split-1: aborted C.1 lost", "Split split-1: channel C.1 removed",
                 "Split split-1: retired broadcast 1a2b/3"]
        status = rigsplitcrash.crash_status(self.lines(texts), "7")
        self.assertEqual(rigsplit.op_outcome(status), "aborted")
        self.assertEqual((status["lost_aborted"], status["channel_removed"], status["retired"]), ("C.1", "C.1", "1a2b/3"))
        ended = rigsplitcrash.crash_status(self.lines(texts[:3] + ["Split split-1: ended, C.1 collapsing",
                                                                   "Split split-1: stragglers gone, settle broadcast 1a2b/5"]),
                                           "7")
        self.assertEqual((rigsplit.op_outcome(ended), ended["ended"], ended["settle"]), ("ended", "C.1", "1a2b/5"))
        self.assertEqual(rigmerge.merge_outcome(rigmerge.parse_merge_status(self.lines(texts), "7")), None)

    def test_order_problems(self):
        pats = [rigsplitcrash.op_pattern("C.1 lost, op paused"), r"^Supervisor: C\.1 back", rigsplitcrash.op_pattern("done")]
        self.assertEqual(rigsplitcrash.order_problems(self.DRAIN, pats), [])
        swapped = self.DRAIN[:3] + [self.DRAIN[5], self.DRAIN[4], self.DRAIN[3]] + self.DRAIN[6:]
        self.assertEqual(len(rigsplitcrash.order_problems(swapped, pats)), 1)
        self.assertIn("out of order", rigsplitcrash.order_problems(swapped, pats)[0])
        self.assertIn("missing", rigsplitcrash.order_problems(self.DRAIN[:-1], pats)[0])
        self.assertRegex("Split split-1: C.1 lost, op paused", pats[0])
        self.assertNotRegex("Split split-1: C11 lost, op paused", pats[0])


class RigAdditionTests(unittest.TestCase):
    def test_relogins_in_result(self):
        r = rigsplit.parse_result("[MeshAuto] RESULT stand: reached=True migrations=1 allReady=True maxGap=40 maxJump=0 "
                                  "relogins=2 -> PASS")
        self.assertEqual((r["relogins"], r["migrations"]), (2, 1))
        self.assertNotIn("relogins", rigsplit.parse_result("RESULT stand: reached=True -> PASS"))

    def test_merge_requested_collapse(self):
        lines = ["9 Merge merge-2: requested C C.1 collapse", "9 Merge merge-2: done"]
        status = rigmerge.parse_merge_status(lines, "9")
        self.assertEqual((status["requested"], status["collapse"], rigmerge.merge_outcome(status)), (("C", "C.1"), True, "done"))
        plain = rigmerge.parse_merge_status(["9 Merge merge-2: requested C C.1"], "9")
        self.assertEqual((plain["requested"], plain["collapse"]), (("C", "C.1"), False))


class SupervisionElsewhereTests(unittest.TestCase):
    """Decision 19: the split and merge scenarios keep failing on supervision, fence and fault lines."""

    def test_split_op_problem_takes_fence_and_fault(self):
        for line in ("Fence: C.1 incarnation 8d fenced (registration lost): saving 1 player(s)",
                     "FAULT: dying on handoff notice for x (MESH_FAULT_DIE_ON_NOTICE)"):
            with self.subTest(line):
                self.assertTrue(rigsplit.SPLIT_OP_PROBLEM.search(line))
                self.assertTrue(rigmerge.OP_PROBLEM.search(line))
        self.assertFalse(rigsplit.SPLIT_OP_PROBLEM.search("Neighbour zone C.1 linked (conn 3)"))

    def test_supervision_lines(self):
        split = rigsplit.parse_op_status(["7 Split split-1: e2 broadcast 1a2b/3", "7 Split split-1: C.1 lost, op paused",
                                          "7 Supervisor: C.1 back, incarnation 8d", "7 Split split-1: C.1 back, op resumed",
                                          "7 Split split-1: aborted e1 gate timeout", "7 Split split-1: done"], "7")
        self.assertEqual(rigsplit.supervision_lines(split), ["Split split-1: C.1 lost, op paused",
                                                             "Split split-1: C.1 back, op resumed",
                                                             "Supervisor: C.1 back, incarnation 8d"])
        merge = rigmerge.parse_merge_status(["9 Merge merge-2: aborted C.1 lost", "9 Merge merge-2: done"], "9")
        self.assertEqual(rigsplit.supervision_lines(merge), ["Merge merge-2: aborted C.1 lost"])
        clean = rigsplit.parse_op_status(["7 Split split-1: done", "7 Split split-1: stragglers gone, settle "
                                          "broadcast 1a2b/5"], "7")
        self.assertEqual(rigsplit.supervision_lines(clean), [])


class ProcessTests(unittest.TestCase):
    def test_kill_script(self):
        exe = r"D:\rig\Build\Server\kit-ce-project.exe"
        script = rigsplitcrash.kill_script(exe, "C")
        self.assertEqual(script.count("Get-CimInstance"), 1)
        self.assertIn("Name='kit-ce-project.exe'", script)
        self.assertIn(f"$_.ExecutablePath -eq '{exe}'", script)
        self.assertIn("-like '*-channelId C *'", script)
        self.assertIn("Stop-Process -Id $q.ProcessId -Force", script)
        self.assertTrue(script.endswith("$p.Count"))
        self.assertTrue(rigmerge.command_line_matches("x.exe  -channelId C  -mapName M", "C"))
        self.assertFalse(rigmerge.command_line_matches("x.exe  -channelId C.1  -mapName M", "C"))
        with self.assertRaises(ValueError):
            rigsplitcrash.kill_script(exe, "C *")

    def test_sequences(self):
        self.assertEqual(rigsplitcrash.process_sequence([0, 0, 1, 1, 0, 1, 1]), [0, 1, 0, 1])
        self.assertTrue(rigsplitcrash.contains_in_order([0, 1, 0, 1], [1, 0, 1]))
        self.assertFalse(rigsplitcrash.contains_in_order([0, 1, 0], [1, 0, 1]))
        self.assertFalse(rigsplitcrash.contains_in_order([1, 1], [1, 0, 1]))


class LineTests(unittest.TestCase):
    def test_faults_and_incarnation(self):
        lines = [at(1, "FAULT: dying on the e1 broadcast naming C.1 Pending (MESH_FAULT_DIE_ON_PREWARM)", "ERROR"),
                 at(2, "FAULT: dying on handoff notice for x (MESH_FAULT_DIE_ON_NOTICE)", "ERROR"),
                 at(0, "LayoutHello sent: cell C incarnation 8dcfaa layout 0/0")]
        self.assertEqual(rigsplitcrash.faults(lines, "MESH_FAULT_DIE_ON_PREWARM"), ["the e1 broadcast naming C.1 Pending"])
        self.assertEqual(rigsplitcrash.incarnation(lines, "C"), "8dcfaa")
        self.assertIsNone(rigsplitcrash.incarnation(lines, "E"))

    def test_disallowed_lines(self):
        prune = at(1, "Retired zone C.1: link closed, 0 record(s) released")
        fault = at(1, "FAULT: dying on x (MESH_FAULT_DIE_ON_DRAIN)", "ERROR")
        fence = at(1, "Fence: C.1 incarnation 8d fenced (registration lost): saving 1 player(s)", "WARN")
        cases = {
            "the retired zone's prune": ("C", [prune], "C.1", None, False, 0),
            "a prune of another zone": ("C", [prune.replace("C.1", "E.1")], "C.1", None, False, 1),
            "a prune when nothing retired": ("C", [prune], None, None, False, 1),
            "the faulted cell's FAULT": ("C.1", [fault], None, "C.1", False, 0),
            "a FAULT elsewhere": ("C", [fault], None, "C.1", False, 1),
            "a fence": ("C.1", [fence], None, "C.1", False, 1),
            "an abort allowed": ("C", [at(1, "Split split-1: aborted C.1 lost")], None, None, True, 0),
            "an abort not allowed": ("C", [at(1, "Split split-1: aborted C.1 lost")], None, None, False, 1),
            "a merge retire": ("C.1", [at(1, "Merge retire: C.1 drained, quitting")], None, None, True, 1),
            "own drain line": ("C", [at(1, "Drain on C: gave away 3")], None, None, False, 0),
            "a release line": ("C.1", [at(1, "Borrowed monster C:aa:5 released (home zone link lost)", "WARN")],
                               None, None, False, 0),
        }
        for name, (cell, lines, retired, faulted, aborted, n) in cases.items():
            with self.subTest(name):
                self.assertEqual(len(rigsplitcrash.disallowed_lines(cell, lines, faulted, retired, aborted)), n)


class LifetimeTests(unittest.TestCase):
    PAIRS = {"e1": "1a2b/2", "e2": "1a2b/3", "e3": "1a2b/4", "retired": "1a2b/3"}

    def grade(self, case, cell, lines, pairs=None, timeout=False):
        pairs = pairs or self.PAIRS
        return rigsplitcrash.lifetime_apply_violations(
            cell, lines, SPLIT, lambda own_rect: rigsplitcrash.crash_expected_lives(case, SPLIT, cell, own_rect, pairs,
                                                                                    timeout))

    def test_prewarm(self):
        base = [own("C", -100, -100), apply("C", 1, P, P), apply("C", 2, P, P), apply("C", 3, P, P)]
        self.assertEqual(self.grade("prewarm", "C", base), [])
        self.assertEqual(self.grade("prewarm", "C.1", [own_child(), apply("C.1", 2, None, R)]), [])
        n = [own("N", -100, 100), apply("N", 1, N_RECT, N_RECT), apply("N", 2, N_RECT, N_RECT), apply("N", 3, N_RECT, N_RECT)]
        self.assertEqual(self.grade("prewarm", "N", n), [])
        self.assertTrue(self.grade("prewarm", "C", base[:3] + [apply("C", 3, K, P)]))
        self.assertTrue(self.grade("prewarm", "C.1", [own_child(), apply("C.1", 2, None, R), apply("C.1", 3, R, R)]))

    def test_drain(self):
        child = [own_child(0), apply("C.1", 2, None, R), apply("C.1", 3, R, R),
                 own_child(50), apply("C.1", 3, R, R, 50), apply("C.1", 4, R, R, 51)]
        self.assertEqual(self.grade("drain", "C.1", child), [])
        problems = self.grade("drain", "C.1", child[:3])
        self.assertTrue(any("1 process lifetime(s), the case expects 2" in p for p in problems), problems)
        self.assertTrue(self.grade("drain", "C.1", child[:4] + [apply("C.1", 4, R, R, 50)]))   # rebound at e3, not e2
        base = [own("C", -100, -100), apply("C", 1, P, P), apply("C", 2, P, P), apply("C", 3, K, P), apply("C", 4, K, K)]
        self.assertEqual(self.grade("drain", "C", base), [])
        settle = dict(self.PAIRS, settle="1a2b/5")
        timeout = base[:4] + [apply("C", 4, K, P), apply("C", 5, K, K)]
        self.assertEqual(self.grade("drain", "C", timeout, settle, True), [])
        self.assertTrue(self.grade("drain", "C", timeout, settle, False))

    def test_base(self):
        base = [own("C", -100, -100), apply("C", 1, P, P), apply("C", 2, P, P), apply("C", 3, K, P), apply("C", 4, K, K),
                own("C", -100, -100, 100), apply("C", 4, K, K, 108)]
        self.assertEqual(self.grade("base", "C", base), [])
        problems = self.grade("base", "C", base + [apply("C", 5, K, K, 120)])
        self.assertTrue(any("lifetime 2: 2 layout(s) applied" in p for p in problems), problems)
        self.assertTrue(self.grade("base", "C", base[:5]))
        player = at(101, "10:01:41.000 S player registered meshbot6Char conn=0 at (1, 0, 1)", tag="MeshTrace")
        problems = self.grade("base", "C", base[:6] + [player, base[6]])
        self.assertTrue(any("registered before the first layout" in p for p in problems), problems)
        other = [apply("C", 4, K, K).replace("1a2b/4", "9f9f/4")]
        self.assertTrue(any("coordinator incarnations" in p for p in self.grade("base", "C", base[:4] + other + base[5:])))


class BaseCrashTests(unittest.TestCase):
    KILL = T0 + datetime.timedelta(seconds=100)

    def child_lines(self):
        return [at(10, "Monster promoted by handoff C:aa:5 C->C.1 at (40, 0, 10) hp=5/5 home=C target=none"),
                at(11, "Monster spawned by handoff C:aa:6 C->C.1 at (40, 0, 10) hp=5/5 home=C target=none"),
                at(12, "Monster promoted by handoff C:aa:7 C->C.1 at (40, 0, 10) hp=5/5 home=C target=none"),
                at(13, "Monster promoted by handoff C:old:8 C->C.1 at (40, 0, 10) hp=5/5 home=C target=none"),
                at(14, "Monster promoted by handoff C:aa:9 C->C.1 at (40, 0, 10) hp=5/5 home=C target=none"),
                at(20, "Monster handoff C:aa:6 C.1->C at (1, 0, 1) hp=5/5 home=C target=none"),
                at(21, "Monster handoff C:aa:6 C.1->C failed there (unknown prefab); re-promoted here", "WARN"),
                at(22, "Monster handoff C:aa:7 C.1->C at (1, 0, 1) hp=5/5 home=C target=none"),
                at(23, "Borrowed monster C:aa:9 died here; told home zone C")]

    def test_held_monsters(self):
        lines = self.child_lines()
        self.assertEqual(rigsplitcrash.held_monsters(lines, "C", "C.1", "C:aa"), ["C:aa:5", "C:aa:6"])
        self.assertEqual(rigsplitcrash.held_monsters(lines, "C", "C.1"), ["C:aa:5", "C:aa:6", "C:old:8"])
        self.assertEqual(rigsplitcrash.before(lines + [at(100, "x"), "no stamp"], self.KILL), lines)

    def test_unreleased(self):
        held = ["C:aa:5", "C:aa:6", "C:aa:10"]
        later = [at(103, "Borrowed monster C:aa:5 released (home zone link lost)", "WARN"),
                 at(100, "Monster handoff C:aa:6 C.1->C at (1, 0, 1) hp=5/5 home=C target=none"),
                 at(131, "Borrowed monster C:aa:10 released (home zone restarted)", "WARN")]
        self.assertEqual(rigsplitcrash.unreleased(held, later, "C", "C.1", self.KILL, 30), ["C:aa:10"])
        self.assertEqual(rigsplitcrash.unreleased(held, later, "C", "C.1", self.KILL, 31), [])
        early = [at(99, "Monster handoff C:aa:6 C.1->C at (1, 0, 1) hp=5/5 home=C target=none")]
        self.assertEqual(rigsplitcrash.unreleased(["C:aa:6"], early, "C", "C.1", self.KILL), ["C:aa:6"])
        # Handed to the dead base a second after the kill (the link has not timed out yet): gone, not unreleased.
        after = [at(101, "Monster handoff C:aa:6 C.1->C at (1, 0, 1) hp=5/5 home=C target=none")]
        self.assertEqual(rigsplitcrash.unreleased(["C:aa:6"], after, "C", "C.1", self.KILL), [])
        late = [at(106, "Monster handoff C:aa:6 C.1->C at (1, 0, 1) hp=5/5 home=C target=none")]
        self.assertEqual(rigsplitcrash.unreleased(["C:aa:6"], late, "C", "C.1", self.KILL), ["C:aa:6"])
        self.assertEqual(rigsplitcrash.statics_released(
            [at(101, "Borrowed static C:aa:s3 released (home zone restarted)", "WARN"),
             at(101, "Borrowed static E:bb:s4 released (home zone restarted)", "WARN")], "C:aa"), 1)

    def test_monster_sums(self):
        base = rigsplitcrash.windows([window(110 + 5 * i, 2) for i in range(6)] + [at(111, "x")])
        child = rigsplitcrash.windows([window(111 + 5 * i, n) for i, n in enumerate([3, 3, 4, 3, 3, 3])])
        pairs = rigsplitcrash.monster_pairs(base, child, 5.0)
        self.assertEqual([s for _, s in pairs], [5, 5, 6, 5, 5, 5])
        self.assertEqual(rigsplitcrash.monster_sum_problems([s for _, s in pairs], 5), [])
        self.assertEqual(len(rigsplitcrash.monster_sum_problems([5, 6, 5, 6, 5], 5)), 1)       # never 3 at 5
        self.assertEqual(len(rigsplitcrash.monster_sum_problems([5, 5, 5, 6, 6, 6], 5)), 1)    # 3 over
        self.assertEqual(len(rigsplitcrash.monster_sum_problems([], 5)), 1)
        # Windows ending on the same second pair with each other, not with the child's next window 5 s on.
        same = rigsplitcrash.windows([window(110 + 5 * i, n) for i, n in enumerate([4, 3, 4])])
        twin = rigsplitcrash.windows([window(110 + 5 * i, n) for i, n in enumerate([1, 2, 1])])
        self.assertEqual([s for _, s in rigsplitcrash.monster_pairs(same, twin, 5.0)], [5, 5, 5])
        far = rigsplitcrash.windows([window(130, 3)])
        self.assertEqual(rigsplitcrash.monster_pairs(base[:1], far, 5.0), [])
        lo, hi = T0 + datetime.timedelta(seconds=112), T0 + datetime.timedelta(seconds=120)
        self.assertEqual(len(rigsplitcrash.windows([window(110 + 5 * i, 2) for i in range(6)], lo, hi)), 2)


class BotTests(unittest.TestCase):
    def test_relogin_lines(self):
        lines = ["[MeshAuto] Session lost (Timeout); re-login 1", "[MeshAuto] Re-login routed to 192.168.0.2:8004",
                 "[MeshAuto] Session lost (migration failed: RemoteConnectionClose/UI_ERROR_KICKED_FROM_SERVER); re-login 2"]
        self.assertEqual(rigsplitcrash.session_losses(lines),
                         [("Timeout", 1), ("migration failed: RemoteConnectionClose/UI_ERROR_KICKED_FROM_SERVER", 2)])
        self.assertEqual(rigsplitcrash.routed(lines), ["192.168.0.2:8004"])

    def test_bot_problems(self):
        r = rigsplit.parse_result
        self.assertEqual(rigsplitcrash.bot_problems(r("migrations=1 relogins=0 -> PASS"), 1, 0), [])
        self.assertEqual(rigsplitcrash.bot_problems(r("migrations=1 relogins=1 -> PASS"), {0, 1}, 1), [])
        self.assertEqual(len(rigsplitcrash.bot_problems(r("migrations=2 -> PASS"), {0, 1}, 1)), 2)
        self.assertEqual(rigsplitcrash.bot_problems({}, 0), ["no RESULT stand line"])

    def test_drain_bot_problems(self):
        fell = ("[MeshAuto] Migration to 192.168.0.2:8010 | SEAMLESS connected=False playerReady=False "
                "keptController=False fellBack=True | standIns=1ms")
        ready = "[MeshAuto] MIGRATION 2: gap=300 ready=True jump=0.2 pos=20,75"
        r = rigsplit.parse_result("migrations=2 allReady=False maxGap=300 relogins=0 -> PASS")
        self.assertEqual(rigsplitcrash.drain_bot_problems(r, [fell, ready], 2, 1, 0.0), (1, []))
        f, problems = rigsplitcrash.drain_bot_problems(r, [ready], 2, 1, 0.0)
        self.assertEqual((f, len(problems)), (0, 2))
        f, problems = rigsplitcrash.drain_bot_problems(r, [fell, ready.replace("pos=20", "pos=-20")], 2, 0, 0.0)
        self.assertEqual((f, len(problems)), (1, 2))
        self.assertEqual(rigsplitcrash.drain_bot_problems({}, [], 0, 0, 0.0), (0, ["no RESULT stand line"]))


if __name__ == "__main__":
    unittest.main()
