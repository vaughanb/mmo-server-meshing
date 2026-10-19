"""Unit tests for rigauto.py: python -m unittest discover -s Tools/ServerMeshing -p "test_*.py" """
import datetime
import unittest

import rigauto

STATUS = [
    "auto Policy: split of C planned: x 0 + child C.1, util 0.92 -> peak 0.71 (C 0.71, C.1 0.71), 2 cut(s) tried",
    "auto Split split-1: requested C x 0 + child C.1",
    "auto Split split-1: e1 broadcast 1a2b/2",
    "auto Split split-1: e2 broadcast 1a2b/3",
    "auto Split split-1: drain gate open",
    "auto Split split-1: e3 broadcast 1a2b/4",
    "auto Split split-1: done",
    "auto Policy: split split-1 measured: predicted peak 0.71, measured 0.46 (C 0.46, C.1 0.03), relief 50 %",
    "auto Policy: merge of C C.1 planned: predicted 0.07 (C 0.06, C.1 0.03)",
    "auto Merge merge-1: requested C C.1",
    "auto Merge merge-1: done",
    "auto Policy: merge merge-1 measured: predicted 0.07, measured 0.06 (C)",
    "17 Split split-2: requested C x 0 + child C.1",
]


def line(stamp, text):
    return f" INFO Mesh [2026-10-18 {stamp}] - {text}"


def window(stamp, owned, work=5.0):
    return line(stamp, f"Work window 5.00 s: frames 300 work mean {work:.3f} ms p95 6.00 ms max 9.0 ms wait 11.000 ms "
                       f"period 16.670 ms budget 16.67 ms sat 0.0% probe=Valid | owned players {owned} monsters 0 "
                       f"| puppets players 0 monsters 0 | clients 0 cpu 50%")


class StatusTests(unittest.TestCase):
    def test_texts_under_auto_only(self):
        self.assertEqual(len(rigauto.status_texts(STATUS)), 12)
        self.assertEqual(rigauto.status_texts(STATUS, "17"), ["Split split-2: requested C x 0 + child C.1"])
        self.assertEqual(len(rigauto.policy_texts(STATUS)), 4)
        self.assertEqual(rigauto.refusals(STATUS + ["auto Layout op refused: cooldown 9 s left on C"]), ["cooldown 9 s left on C"])
        self.assertEqual(rigauto.supervisor_texts(STATUS + ["auto Supervisor: C.1 down 20 s, restart 1 of 3 requested"]),
                         ["Supervisor: C.1 down 20 s, restart 1 of 3 requested"])

    def test_parsers(self):
        texts = rigauto.policy_texts(STATUS)
        planned = rigauto.first(texts, rigauto.parse_planned)
        self.assertEqual(planned, {"cell": "C", "axis": "x", "at": 0.0, "side": "+", "child": "C.1", "util": 0.92, "peak": 0.71,
                                   "parent_util": 0.71, "child_util": 0.71, "tried": 2})
        measured = rigauto.first(texts, rigauto.parse_split_measured)
        self.assertEqual(measured, {"op": "split-1", "predicted": 0.71, "measured": 0.46, "utils": {"C": 0.46, "C.1": 0.03}, "relief": 50})
        self.assertEqual(rigauto.first(texts, rigauto.parse_merge_planned),
                         {"a": "C", "b": "C.1", "predicted": 0.07, "utils": {"C": 0.06, "C.1": 0.03}})
        self.assertEqual(rigauto.first(texts, rigauto.parse_merge_measured),
                         {"op": "merge-1", "predicted": 0.07, "measured": 0.06, "survivor": "C"})
        self.assertEqual(rigauto.parse_refusal("split of C refused: best peak 0.93 over 0.75 x util 0.9"),
                         {"cell": "C", "reason": "best peak 0.93 over 0.75 x util 0.9", "peak": 0.93, "ratio": 0.75, "util": 0.9})
        self.assertEqual(rigauto.parse_refusal("split of C refused: family C has 4 cell(s), the map's most"),
                         {"cell": "C", "reason": "family C has 4 cell(s), the map's most", "peak": None, "ratio": None, "util": None})
        self.assertIsNone(rigauto.parse_refusal("split of C planned: x 0 + child C.1, util 0.9 -> peak 0.5 (C 0.5, C.1 0.4), 1 cut(s) tried"))
        self.assertIsNone(rigauto.parse_planned("merge of C C.1 planned: predicted 0.07 (C 0.06, C.1 0.03)"))

    def test_statuses_and_counts(self):
        split = rigauto.split_status(STATUS)
        self.assertTrue(split["done"])
        self.assertEqual(split["pairs"], {"e1": "1a2b/2", "e2": "1a2b/3", "e3": "1a2b/4"})
        self.assertEqual(split["drain"], "open")
        merge = rigauto.merge_status(STATUS)
        self.assertEqual((merge["requested"], merge["done"], merge["collapse"]), (("C", "C.1"), True, False))
        self.assertEqual(rigauto.split_count(STATUS), 1)
        self.assertEqual(rigauto.split_count(STATUS + ["auto Split split-2: requested C z 0 + child C.2"]), 2)
        self.assertTrue(rigauto.has_merge_line(STATUS))
        self.assertFalse(rigauto.has_merge_line(STATUS[:8]))

    def test_disallowed_policy(self):
        texts = rigauto.policy_texts(STATUS) + ["flap on C: split split-1 relief 6 % under 10 %; dwell and cooldown x2"]
        allowed = ("split of C planned", "split split-1 measured", "merge of C C.1 planned", "merge merge-1 measured")
        self.assertEqual(rigauto.disallowed_policy(texts, allowed), [texts[-1]])
        self.assertEqual(rigauto.disallowed_policy(texts[:-1], allowed), [])


class WindowTests(unittest.TestCase):
    def test_util_after_and_pairs(self):
        a = [window("10:00:05", 120, 8.0), window("10:00:10", 110, 7.0), window("10:00:15", 100, 6.0), window("10:00:20", 100, 6.0)]
        b = [window("10:00:07", 70), window("10:00:12", 80), window("10:00:17", 100), window("10:00:22", 100)]
        after = datetime.datetime(2026, 10, 18, 10, 0, 9)
        self.assertAlmostEqual(rigauto.util_after(a, after), 7.0 / 16.67, places=4)
        self.assertIsNone(rigauto.util_after(a, datetime.datetime(2026, 10, 18, 11, 0, 0)))
        pairs = rigauto.owned_pairs(a, b, after)
        self.assertEqual(pairs, [(110, 80), (100, 100), (100, 100)])
        self.assertFalse(rigauto.consecutive(pairs, 200))
        self.assertTrue(rigauto.consecutive(pairs + [(100, 100)], 200))
        self.assertTrue(rigauto.consecutive(pairs, 200, run=2))
        self.assertEqual(rigauto.owned_pairs(a, [], after), [])


if __name__ == "__main__":
    unittest.main()
