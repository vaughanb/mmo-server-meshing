"""Unit tests for rigpolicy's readers (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py")."""
import unittest

import rigpolicy as P

LINES = [
    "auto Policy: split of C planned: x 0 + child C.1, util 0.91 -> peak 0.91 (C 0.91, C.1 0.91), 2 cut(s) tried",
    "auto Split split-1: requested C x 0 + child C.1", "auto Split split-1: e1 broadcast 1a2b/2",
    "auto Split split-1: e2 broadcast 1a2b/3", "auto Split split-1: drain gate open",
    "auto Split split-1: e3 broadcast 1a2b/4", "auto Split split-1: done",
    "auto Policy: split of E planned: x 200 + child E.1, util 0.9 -> peak 0.9 (E 0.9, E.1 0.9), 2 cut(s) tried",
    "auto Split split-2: requested E x 200 + child E.1", "auto Split split-2: e1 broadcast 1a2b/5",
    "auto Policy: split split-1 measured: predicted peak 0.91, measured 0.47 (C 0.47, C.1 0.06), relief 48 %",
    "auto Split split-2: done", "7 Split split-9: done",
]


class PolicyReaderTests(unittest.TestCase):
    def test_ops_and_statuses(self):
        self.assertEqual(P.split_ops(LINES), ["split-1", "split-2"])
        one, two = P.op_status(LINES, "split-1"), P.op_status(LINES, "split-2")
        self.assertTrue(one["done"] and one["drain"] == "open")
        self.assertEqual(one["pairs"], {"e1": "1a2b/2", "e2": "1a2b/3", "e3": "1a2b/4"})
        self.assertEqual((two["requested"], two["pairs"], two["done"]), ("E x 200 + child E.1", {"e1": "1a2b/5"}, True))
        self.assertEqual([p["cell"] for p in P.planned(LINES)], ["C", "E"])
        self.assertEqual(list(P.measured(LINES)), ["split-1"])
        done, planned = P.index_of(LINES, P.is_done("split-1")), P.index_of(LINES, P.is_planned("E"))
        self.assertLess(done, planned)
        self.assertIsNone(P.index_of(LINES, P.is_planned("N")))

    def test_deferrals(self):
        lines = ["auto Policy: split of C deferred: host BOX at 13.7/12 cores", "auto Policy: split of E deferred: host BOX at 14/12 cores"]
        self.assertEqual(P.deferrals(lines), [{"cell": "C", "host": "BOX", "projected": 13.7, "cores": 12},
                                              {"cell": "E", "host": "BOX", "projected": 14.0, "cores": 12}])

    def test_burn_text(self):
        self.assertEqual(P.burn_text({"C": 14, "E": 7.5, "W": 0}), "C:14,E:7.5")
        self.assertEqual(P.burn_text({}), "")

    def test_headroom_cores(self):
        self.assertEqual(P.headroom_cores(6.1, 13.7), 12)
        cores = P.headroom_cores(5.0, 7.0)
        self.assertTrue(5.0 < 0.8 * cores < 7.0)
        self.assertIsNone(P.headroom_cores(5.0, 5.5))

    def test_split_of(self):
        plan = P.planned(LINES)[1]
        split = P.split_of("M", plan, (100.0, -100.0, 200.0, 200.0))
        self.assertEqual((split.base, split.axis, split.at, split.side, split.child), ("E", "x", 200.0, "+", "E.1"))


if __name__ == "__main__":
    unittest.main()
