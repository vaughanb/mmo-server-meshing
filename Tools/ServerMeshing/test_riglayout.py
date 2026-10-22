"""Unit tests for riglayout's timelines and apply grade (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py")."""
import unittest

import riglayout as L
import rigmerge
import rigsplit

CELLS = L.TEST_MAP_RECTS
SP1 = {"e1": "a/2", "e2": "a/3", "e3": "a/4"}
SP2 = {"e1": "a/5", "e2": "a/6", "e3": "a/7"}
MP = {"e1": "a/8", "e2": "a/9", "e3": "a/10"}
C, K1, G1 = CELLS["C"], (-100.0, -100.0, 100.0, 200.0), (0.0, -100.0, 100.0, 200.0)
K2, G2 = (0.0, -100.0, 100.0, 100.0), (0.0, 0.0, 100.0, 100.0)


def apply(cell, pair, owned, interest):
    return (f" INFO Mesh [2026-10-21 10:00:00] - Layout {pair} applied: {cell} owns {rigsplit.format_rect(owned)} "
            f"interest {rigsplit.format_rect(interest)}, 8 targets")


class TimelineTests(unittest.TestCase):
    def test_one_split_and_its_merge_back_match_the_single_op_graders(self):
        split = rigsplit.Split("M", "C", C, "x", 0.0, "+", "C.1")
        t = L.timeline(CELLS, [L.split_op("C", "C.1", "x", 0.0, "+", SP1)])
        for cell in ("C", "C.1", "E", "NW"):
            self.assertEqual([(o, i) for o, i, _ in t[cell]], rigsplit.expected_applies(split, cell, CELLS.get(cell, L.ANY)))
        merge = dict(MP, tidy="a/11")
        t = L.timeline(CELLS, [L.split_op("C", "C.1", "x", 0.0, "+", SP1), L.merge_op("C", "C.1", merge)])
        for cell in ("C", "C.1", "E"):
            self.assertEqual(t[cell], rigmerge.merge_expected_applies(split, cell, CELLS.get(cell, L.ANY), SP1, merge))

    def test_a_child_split_then_the_two_children_merged(self):
        ops = [L.split_op("C", "C.1", "x", 0.0, "+", SP1), L.split_op("C.1", "C.2", "z", 0.0, "+", SP2),
               L.merge_op("C.2", "C.1", MP)]
        t = L.timeline(CELLS, ops)
        n = L.NOTHING
        self.assertEqual(t["C.1"], [(n, G1, "a/2"), (G1, G1, "a/3"), (G1, G1, "a/4"),
                                    (G1, G1, "a/5"), (K2, G1, "a/6"), (K2, K2, "a/7"),
                                    (K2, K2, "a/8"), (n, K2, "a/9")])
        self.assertEqual(t["C.2"], [(n, G2, "a/5"), (G2, G2, "a/6"), (G2, G2, "a/7"),
                                    (G2, G1, "a/8"), (G1, G1, "a/9"), (G1, G1, "a/10")])
        self.assertEqual(t["C"], [(C, C, None), (C, C, "a/2"), (K1, C, "a/3"), (K1, K1, "a/4")]
                         + [(K1, L.ANY, p) for p in ("a/5", "a/6", "a/7", "a/8", "a/9", "a/10")])
        self.assertEqual(len(t["E"]), 10)
        self.assertEqual(L.final_owned(CELLS, ops)["C.2"], G1)
        self.assertNotIn("C.1", L.final_owned(CELLS, ops))

    def test_a_collapse_of_a_dead_child_applies_nothing_on_it(self):
        ops = [L.split_op("C", "C.1", "x", 0.0, "+", SP1), L.split_op("C.1", "C.2", "z", 0.0, "+", SP2),
               L.merge_op("C.1", "C.2", MP, dead=True)]
        t = L.timeline(CELLS, ops)
        self.assertEqual(len(t["C.2"]), 3)
        self.assertEqual(t["C.1"][-3:], [(K2, G1, "a/8"), (G1, G1, "a/9"), (G1, G1, "a/10")])

    def test_two_families_one_after_the_other(self):
        t = L.timeline(CELLS, [L.split_op("C", "C.1", "x", 0.0, "+", SP1), L.split_op("E", "E.1", "x", 200.0, "+", SP2)])
        self.assertEqual(t["E"][:2], [(CELLS["E"], CELLS["E"], None), (CELLS["E"], L.ANY, "a/2")])
        self.assertEqual(t["E"][-1], ((100.0, -100.0, 100.0, 200.0),) * 2 + ("a/7",))
        self.assertEqual(t["C.1"][-1], (G1, L.ANY, "a/7"))
        self.assertEqual(t["E.1"][0], (L.NOTHING, (200.0, -100.0, 100.0, 200.0), "a/5"))

    def test_impossible_ops(self):
        with self.assertRaises(ValueError):
            L.timeline(CELLS, [L.split_op("C", "C.1", "x", 0.0, "+"), L.merge_op("C", "E")])   # C and E: no full edge now
        with self.assertRaises(ValueError):
            L.timeline(CELLS, [L.merge_op("C.1", "C.2")])
        with self.assertRaises(ValueError):
            L.timeline(CELLS, [L.split_op("C", "C.1", "x", 300.0, "+")])
        self.assertIsNone(L.union(K1, K2))
        self.assertEqual(L.union(K2, G2), G1)
        self.assertEqual(L.union(K1, G1), C)


class ApplyViolationsTests(unittest.TestCase):
    OWN = " INFO Mesh [2026-10-21 10:00:00] - Own cell from layout: C.2 of map M, grid (0,0), world x [0, 100) z [0, 100), 3 neighbours"
    EXPECTED = [(L.NOTHING, G2, "a/5"), (G2, G2, "a/6"), (G2, G2, "a/7")]

    def lines(self):
        return [self.OWN, apply("C.2", "a/5", None, G2), apply("C.2", "a/6", G2, G2), apply("C.2", "a/7", G2, G2)]

    def test_passes(self):
        self.assertEqual(L.apply_violations("C.2", self.lines(), self.EXPECTED, child=True), [])

    def test_failures(self):
        base = self.lines()
        cases = {
            "a missing apply": (base[:3], "2 layout(s) applied"),
            "a wrong rect": (base[:3] + [apply("C.2", "a/7", G1, G1)], "apply 3"),
            "another pair": (base[:3] + [apply("C.2", "a/8", G2, G2)], "expects a/7"),
            "two incarnations": (base[:3] + [apply("C.2", "b/7", G2, G2)], "incarnations"),
            "a second lifetime": (base + [self.OWN], "2 process lifetimes"),
            "a player first": (base[:1] + [" INFO Mesh [x] - S player registered X "] + base[1:], "player registered"),
            "no lifetime": ([], "no process lifetime"),
        }
        for name, (lines, fragment) in cases.items():
            with self.subTest(name):
                problems = L.apply_violations("C.2", lines, self.EXPECTED, child=True)
                self.assertTrue(any(fragment in p for p in problems), problems)

    def test_a_static_cell_checks_its_own_rect(self):
        own = " INFO Mesh [x] - Own cell from channel id: E of map M, grid (2,1), world x [100, 300) z [-100, 100), 8 neighbours"
        lines = [own, apply("E", "a/1", CELLS["E"], CELLS["E"])]
        self.assertEqual(L.apply_violations("E", lines, [(CELLS["E"], L.ANY, None)], own_rect=CELLS["E"]), [])
        self.assertTrue(L.apply_violations("E", lines, [(CELLS["E"], L.ANY, None)], own_rect=C))


if __name__ == "__main__":
    unittest.main()
