"""Unit tests for rigbuilding.py (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py")."""
import unittest

import regress_split
import rigbuilding
import rigmerge
import rigsplit
from test_regress_split import apply, line, own

W, C, C1 = "wall-1", "C", "C.1"


def transfer(root, a, b, what):
    return line(f"Building transfer {root} zone {a}->{b}: {what}")


class LineTests(unittest.TestCase):
    def test_transfers_and_counts(self):
        sender = [transfer(W, C, C1, "sent (3 node(s))"), transfer(W, C, C1, "done"), line("Static handoff s1 zone C->C.1: sent")]
        receiver = [transfer(W, C, C1, "promoted 1, spawned 2, owned already 0")]
        self.assertEqual([t["what"] for t in rigbuilding.transfers(sender)], ["sent (3 node(s))", "done"])
        self.assertEqual(rigbuilding.sent_counts(sender, W, C, C1), [3])
        self.assertEqual(rigbuilding.roots_sent(sender, C, C1), [W])
        self.assertEqual(rigbuilding.roots_sent(sender, C1, C), [])
        self.assertEqual(rigbuilding.taken_counts(receiver, W, C, C1), [(1, 2, 0)])
        self.assertEqual(rigbuilding.transfer_problems(sender, receiver, W, C, C1), [])

    def test_transfer_problems(self):
        good_s = [transfer(W, C, C1, "sent (3 node(s))"), transfer(W, C, C1, "done")]
        good_r = [transfer(W, C, C1, "promoted 0, spawned 3, owned already 0")]
        cases = {
            "no root": (good_s, good_r, None, "no 'Building transfer"),
            "two nodes": ([transfer(W, C, C1, "sent (2 node(s))"), good_s[1]], good_r, W, "sent lines [2]"),
            "no done": (good_s[:1], good_r, W, "0 'done'"),
            "owned already": (good_s, [transfer(W, C, C1, "promoted 2, spawned 0, owned already 1")], W, "taken lines"),
            "released": (good_s + [transfer(W, C, C1, "released (no reply within the ack guard)")], good_r, W, "C: released"),
            "still in use": (good_s + [transfer(W, C, C1, "storage s still in use after 5 s")], good_r, W, "C: storage"),
            "failed on receiver": (good_s, good_r + [transfer(W, C, C1, "failed (loading)")], W, "C.1: failed"),
        }
        for name, (s, r, root, fragment) in cases.items():
            with self.subTest(name):
                problems = rigbuilding.transfer_problems(s, r, root, C, C1)
                self.assertTrue(any(fragment in p for p in problems), (fragment, problems))

    def test_loads(self):
        text = line("Buildings loaded on C: 3 placed in 1 tree(s) and 0 scene, 0 tree(s) owned elsewhere, 0 scene row(s) of other cells skipped, 0 already here (channel C, 3 row(s))")
        loads = rigbuilding.loads([line("Mesh stats"), text])
        self.assertEqual(loads, [{"cell": "C", "placed": 3, "trees": 1, "scene": 0, "elsewhere": 0, "skipped": 0, "already": 0, "channel": "C", "rows": 3}])
        self.assertEqual(rigbuilding.load_problems(loads[0], "C", 3, "C", 3), [])
        self.assertEqual(rigbuilding.load_problems(None, "C", 3, "C", 3), ["no 'Buildings loaded on C' line"])
        other = dict(loads[0], placed=2, elsewhere=1, channel="C.1")
        problems = rigbuilding.load_problems(other, "C", 3, "C", 3)
        self.assertEqual(len(problems), 3, problems)


RESULT = ("[MeshAuto] RESULT buildtree: placed=3 root=wall-1 storageBefore=C storageAfter=C.1 doorOpened=True "
          "movesOk=12 movesRefused=3 movesFailed=0 scrollsBag=19 scrollsStorage=1 total=20 buildingsAfterRelogin=3 "
          "storageAfterRelogin=1 migrations=2 allReady=True maxGap=310 maxJump=0.2 relogins=1 -> PASS")


class ResultTests(unittest.TestCase):
    def test_parse_result(self):
        r = rigbuilding.parse_result(RESULT)
        self.assertEqual((r["placed"], r["root"], r["storageBefore"], r["doorOpened"], r["movesRefused"], r["total"]),
                         (3, "wall-1", "C", True, 3, 20))
        self.assertEqual((r["migrations"], r["relogins"], r["passed"]), (2, 1, True))
        self.assertEqual(rigbuilding.parse_result(""), {})
        self.assertEqual(rigbuilding.parse_result("[MeshAuto] RESULT stand: reached=True -> PASS"), {})

    def test_result_problems(self):
        problems, notes = rigbuilding.result_problems(rigbuilding.parse_result(RESULT), "C", "C.1")
        self.assertEqual(problems, [])
        self.assertTrue(any("moves ok=12" in n for n in notes))
        cases = {
            "no result": ("", "no RESULT"),
            "fail": (RESULT.replace("-> PASS", "-> FAIL"), "did not pass"),
            "two placed": (RESULT.replace("placed=3", "placed=2"), "placed=2"),
            "storage on the wrong cell": (RESULT.replace("storageAfter=C.1", "storageAfter=C"), "storageAfter="),
            "door": (RESULT.replace("doorOpened=True", "doorOpened=False"), "doorOpened"),
            "a failed move": (RESULT.replace("movesFailed=0", "movesFailed=1"), "movesFailed=1"),
            "a scroll lost": (RESULT.replace("total=20", "total=19"), "total=19"),
            "two buildings after the restart": (RESULT.replace("buildingsAfterRelogin=3", "buildingsAfterRelogin=2"), "buildingsAfterRelogin=2"),
            "storage after the restart": (RESULT.replace("storageAfterRelogin=1", "storageAfterRelogin=0"), "storageAfterRelogin=0"),
        }
        for name, (text, fragment) in cases.items():
            with self.subTest(name):
                problems, _ = rigbuilding.result_problems(rigbuilding.parse_result(text), "C", "C.1")
                self.assertTrue(any(fragment in p for p in problems), (fragment, problems))
        problems, notes = rigbuilding.result_problems(rigbuilding.parse_result(RESULT.replace("buildingsAfterRelogin=3", "buildingsAfterRelogin=-1")), "C", "C.1")
        self.assertEqual(problems, [])
        self.assertTrue(any("did not finish" in n for n in notes))


class RowTests(unittest.TestCase):
    def test_rows_problems(self):
        rows = [("wall-1", "", "C"), ("chest-1", "wall-1", "C"), ("door-1", "wall-1", "C")]
        self.assertEqual(rigbuilding.rows_problems(rows, "C", "wall-1"), [])
        self.assertTrue(rigbuilding.rows_problems(rows + [("x", "", "C")], "C", "wall-1"))
        self.assertTrue(any("channels" in p for p in rigbuilding.rows_problems([("wall-1", "", "C"), ("chest-1", "wall-1", "C.1"), ("door-1", "wall-1", "C")], "C", "wall-1")))
        self.assertTrue(any("root row" in p for p in rigbuilding.rows_problems([("wall-1", "", "C"), ("chest-1", "", "C"), ("door-1", "wall-1", "C")], "C", "wall-1")))
        self.assertTrue(any("parent is not the root" in p for p in rigbuilding.rows_problems([("wall-1", "", "C"), ("chest-1", "other", "C"), ("door-1", "wall-1", "C")], "C", "wall-1")))
        self.assertEqual(rigbuilding.storage_sum([(1,), (2,)]), 3)


class LayoutTests(unittest.TestCase):
    def test_expected_lives(self):
        split = regress_split.split_of("split", "ServerMeshTestMap")
        sp = {"e1": "1a2b/2", "e2": "1a2b/3", "e3": "1a2b/4"}
        mp = {"e1": "1a2b/5", "e2": "1a2b/6", "e3": "1a2b/7", "tidy": "1a2b/8"}
        p = split.base_rect
        base = rigbuilding.expected_lives(split, "C", p, sp, mp)
        self.assertEqual(len(base), 2)
        self.assertEqual(base[0], list(rigmerge.merge_expected_applies(split, "C", p, sp, mp)))
        self.assertEqual(base[1], [(p, p, None)])
        child = rigbuilding.expected_lives(split, "C.1", rigsplit.ANY, sp, mp)
        self.assertEqual(len(child), 1)
        other = rigbuilding.expected_lives(split, "N", (-100.0, 100.0, 200.0, 200.0), sp, mp)
        self.assertEqual(len(other), 1)
        self.assertEqual(len(other[0]), 8)

    def test_second_life_op_lines(self):
        p = (-100.0, -100.0, 200.0, 200.0)
        info = [own("C"), apply("C", 1, p, p), line("Split split-1: requested"), own("C"), apply("C", 9, p, p)]
        self.assertEqual(rigbuilding.second_life_op_lines(info), [])
        info.append(line("Merge retire: C drained, quitting"))
        self.assertEqual(len(rigbuilding.second_life_op_lines(info)), 1)
        self.assertEqual(rigbuilding.second_life_op_lines([own("C")]), [])


if __name__ == "__main__":
    unittest.main()
