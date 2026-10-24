"""Unit tests for rigsoak's readers (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py")."""
import unittest

import riglayout
import rigsoak as S
import rigsplit

MAP = "ServerMeshTestMap"


def entry(cell, base, state, owned=(0.0, 0.0, 1.0, 1.0)):
    return {"id": cell, "mapId": MAP, "baseId": base, "state": state,
            "owned": {"minX": owned[0], "minZ": owned[1], "maxX": owned[0] + owned[2], "maxZ": owned[1] + owned[3]}}


def split_lines(op, parent, child, at, v, end="done", axis="x"):
    return [f"auto Split {op}: requested {parent} {axis} {at:g} + child {child}", f"auto Split {op}: e1 broadcast 1a2b/{v}",
            f"auto Split {op}: e2 broadcast 1a2b/{v + 1}", f"auto Split {op}: drain gate open",
            f"auto Split {op}: e3 broadcast 1a2b/{v + 2}", f"auto Split {op}: {end}"]


def merge_lines(op, survivor, retiring, v, collapse=False, tidy=True):
    out = [f"auto Merge {op}: requested {survivor} {retiring}" + (" collapse" if collapse else ""),
           f"auto Merge {op}: e1 broadcast 1a2b/{v}", f"auto Merge {op}: e2 broadcast 1a2b/{v + 1}",
           f"auto Merge {op}: e3 broadcast 1a2b/{v + 2}"]
    return out + ([f"auto Merge {op}: tidy broadcast 1a2b/{v + 3}"] if tidy else []) + [f"auto Merge {op}: done"]


def stamp(lines, t0=100.0):
    return [(t0 + i, line) for i, line in enumerate(lines)]


def apply_line(pair, cell, owned):
    return f" INFO Mesh [2026-10-23 10:00:00] - Layout {pair} applied: {cell} owns {rigsplit.format_rect(owned)} interest {rigsplit.format_rect(owned)}, 8 targets"


class StoreTests(unittest.TestCase):
    STORE = {"broadcast": {"entries": [entry("C", "C", 2), entry("C.1", "C", 2), entry("C.2", "C", 4), entry("E.1", "E", 1),
                                       entry("E", "E", 2), {"id": "C2", "mapId": "Other", "baseId": "C2", "state": 0}]},
             "fencedIncarnations": [{"cellId": "C.1", "incarnation": "a"}, {"cellId": "c.1", "incarnation": "b"},
                                    {"cellId": "E.1", "incarnation": "c"}],
             "abortedIds": ["E.2"], "hasOperation": True}

    def test_families_and_children(self):
        fams = S.families(self.STORE, MAP)
        self.assertEqual(fams["C"], ["C", "C.1"])
        self.assertEqual(fams["E"], ["E"])            # a Pending child's split may yet abort
        self.assertEqual(S.families(self.STORE, MAP, pending=True)["E"], ["E", "E.1"])
        self.assertEqual(fams["N"], ["N"])
        self.assertEqual(set(fams), set(riglayout.TEST_MAP_RECTS))
        self.assertEqual(S.children(self.STORE, MAP), ["C.1"])   # Active only: the Pending E.1 is not killed
        self.assertEqual(S.families({}, MAP)["C"], ["C"])

    def test_store_stats(self):
        st = S.store_stats(self.STORE, 1234)
        self.assertEqual(st, {"bytes": 1234, "entries": 6, "retired": 1, "fenced": 3, "fenced_max": 2, "aborted": 1, "operation": True})
        self.assertEqual(S.store_stats({}, 0)["fenced_max"], 0)


class OperationTests(unittest.TestCase):
    def test_a_split_and_its_merge(self):
        ops = S.operations(stamp(split_lines("split-1", "C", "C.1", 0, 2) + merge_lines("merge-1", "C", "C.1", 5)))
        self.assertEqual(len(ops), 2)
        s, m = ops
        self.assertEqual((s["a"], s["b"], s["cut"], s["outcome"]), ("C", "C.1", ("x", 0.0, "+"), "done"))
        self.assertEqual(s["pairs"], {"e1": "1a2b/2", "e2": "1a2b/3", "e3": "1a2b/4"})
        self.assertEqual((m["a"], m["b"], m["collapse"], m["outcome"]), ("C", "C.1", False, "done"))
        self.assertEqual(m["pairs"]["tidy"], "1a2b/8")
        self.assertEqual((s["seen_at"], s["ended_at"]), (100.0, 105.0))
        self.assertEqual(S.op_counts(ops)["splits"], 1)
        self.assertEqual(S.op_counts(ops)["merges"], 1)

    def test_a_restarted_centrals_numbering_starts_new_ops(self):
        lines = split_lines("split-1", "C", "C.1", 0, 2) + merge_lines("merge-1", "C", "C.1", 5) + split_lines("split-1", "E", "E.1", 200, 9)
        ops = S.operations(stamp(lines))
        self.assertEqual([(o["kind"], o["a"]) for o in ops], [("Split", "C"), ("Merge", "C"), ("Split", "E")])

    def test_outcomes_resumes_and_freezes(self):
        lines = ["auto Split split-1: requested C x 0 + child C.1", "auto Split split-1: aborted C.1 lost",
                 "auto Merge merge-1: requested C C.1", "auto Merge merge-1: e1 broadcast 1a2b/3",
                 "auto Merge merge-1: resumed at DrainGate after a central restart", "auto Merge merge-1: done",
                 "auto Split split-2: requested E x 200 + child E.1", "auto Split split-2: aborted the central restarted before e2",
                 "auto Split split-3: requested N x 0 + child N.1", "auto Split split-3: frozen N.1 unreachable",
                 "auto Merge merge-2: requested C C.2 collapse", "auto Merge merge-2: done",
                 "auto Split split-4: requested C z 0 + child C.3", "auto Split split-4: ended, C.3 collapsing",
                 "auto Split split-5: requested NE x 0 + child NE.1", "auto Split split-5: dropped after the restart: no base",
                 "auto Split split-6: requested SW x -200 + child SW.1", "auto Policy: split of C planned: nonsense",
                 "7 Split split-9: done"]
        ops = S.operations(stamp(lines))
        self.assertEqual([o["outcome"] for o in ops], ["aborted", "done", "aborted", None, "done", "ended", "dropped", None])
        self.assertEqual(ops[1]["resumed"], ["DrainGate"])
        self.assertTrue(ops[3]["frozen"] and ops[4]["collapse"])
        self.assertEqual(S.op_counts(ops), {"splits": 0, "merges": 1, "collapses": 1, "aborted": 2, "ended": 1, "dropped": 1,
                                            "open": 1, "frozen_open": 1})
        late = S.stuck(ops, now=1000.0, limit_s=600.0)
        self.assertEqual([name for name, _, _ in late], ["Split split-3", "Split split-6"])
        self.assertEqual(S.stuck(ops[:3], now=1000.0, limit_s=1.0), [("Merge merge-1", 3, "done")])

    def test_policy_and_supervisor_counts(self):
        lines = ["auto Policy: split of C planned: x 0 + child C.1, util 0.9 -> peak 0.9 (C 0.9, C.1 0.9), 2 cut(s) tried",
                 "auto Policy: merge of C C.1 planned: predicted 0.2 (C 0.1, C.1 0.1)",
                 "auto Policy: split of C refused: best peak 0.9 over 0.85 x util 0.9", "auto Policy: split of E deferred: host A at 13.2/12 cores",
                 "auto Policy: flap on C: x", "auto Policy: split split-1 measured: predicted peak 0.9, measured 0.47 (C 0.47, C.1 0.06), relief 48 %",
                 "auto Policy: merge merge-1 not measured (gone)", "auto Layout op refused: cooldown",
                 "auto Supervisor: C.1 down 3.1 s, restart 1 of 3 requested", "auto Supervisor: C.1 back, incarnation 8df",
                 "auto Supervisor: C.2 restart 1 failed (timeout)", "auto Supervisor: C.2 collapse queued after 3 failed restart(s)"]
        self.assertEqual(S.policy_counts(lines), {"planned_splits": 1, "planned_merges": 1, "refused": 1, "deferred": 1, "flaps": 1,
                                                  "measured": 1, "not_measured": 1, "coordinator_refused": 1})
        sup = S.supervisor_counts(lines)
        self.assertEqual((sup["down"], sup["back"], sup["failed"], sup["collapse"]), (1, 1, 1, 1))


class CentralLogTests(unittest.TestCase):
    def test_central_stamped_keeps_the_mirrored_lines_in_stamp_order(self):
        lines = [" INFO Mesh [2026-10-23 13:54:17] - Split split-4: retired broadcast 8df/24",
                 " INFO Mesh [2026-10-23 13:54:02] - Split split-4: e1 broadcast 8df/23",
                 " INFO Mesh [2026-10-23 13:54:17] - Coordinator 8df: Dynamic maps M AdoptStored; store s (loaded); broadcast 8df/24",
                 " WARN Mesh [2026-10-23 13:54:17] - Split split-4: aborted the central restarted before e2",
                 " WARN Mesh [2026-10-23 13:53:58] - Policy: flap on C: x", "no stamp - Split split-9: done"]
        got = S.central_stamped(lines)
        self.assertEqual([t for _, t in got], ["auto Policy: flap on C: x", "auto Split split-4: e1 broadcast 8df/23",
                                               "auto Split split-4: retired broadcast 8df/24",
                                               "auto Split split-4: aborted the central restarted before e2"])
        self.assertEqual(got[2][0] - got[1][0], 15.0)
        self.assertEqual(S.operations(got)[0]["outcome"], "aborted")


class LayoutTests(unittest.TestCase):
    R = riglayout.TEST_MAP_RECTS

    def test_two_owners(self):
        cells = {"C": [apply_line("1a2b/3", "C", (-100, -100, 100, 200))], "C.1": [apply_line("1a2b/3", "C.1", (0, -100, 100, 200))],
                 "E": [apply_line("1a2b/3", "E", self.R["E"]), apply_line("1a2b/4", "E", self.R["E"])]}
        by_pair = S.applies_by_pair(cells)
        self.assertEqual(set(by_pair), {"1a2b/3", "1a2b/4"})
        self.assertEqual(S.two_owners(by_pair), [])     # edges touch, nothing shared
        cells["C.1"].append(apply_line("1a2b/4", "C.1", (-10, -100, 110, 200)))
        cells["C"].append(apply_line("1a2b/4", "C", (-100, -100, 100, 200)))
        found = S.two_owners(S.applies_by_pair(cells))
        self.assertEqual([(p, a, b) for p, a, b, _, _ in found], [("1a2b/4", "C", "C.1")])
        self.assertIsNone(S.intersection(rigsplit.NOTHING, (0, 0, 1, 1)))

    def test_expected_pairs_from_done_ops(self):
        lines = split_lines("split-1", "C", "C.1", 0, 2) + ["auto Split split-2: requested C z 0 + child C.2", "auto Split split-2: aborted C.2 lost"] \
            + merge_lines("merge-1", "C", "C.1", 5)
        lops, skipped = S.layout_ops(S.operations(stamp(lines)))
        self.assertEqual((len(lops), skipped), (2, []))
        expected = S.expected_by_pair(self.R, lops)
        self.assertEqual(expected["1a2b/3"]["C"], (-100.0, -100.0, 100.0, 200.0))
        self.assertEqual(expected["1a2b/3"]["C.1"], (0.0, -100.0, 100.0, 200.0))
        self.assertIs(expected["1a2b/2"]["C.1"], rigsplit.NOTHING)
        self.assertEqual(expected["1a2b/8"]["C"], self.R["C"])
        self.assertNotIn("C.1", expected["1a2b/8"])
        self.assertEqual(riglayout.final_owned(self.R, lops), self.R)
        cells = {"C": [apply_line("1a2b/3", "C", (-100, -100, 100, 200)), apply_line("1a2b/9", "C", self.R["C"])],
                 "C.1": [apply_line("1a2b/3", "C.1", (0, -100, 90, 200))], "E": [apply_line("1a2b/3", "E", self.R["E"])]}
        bad, underived = S.mismatches(S.applies_by_pair(cells), expected)
        self.assertEqual([(p, c) for p, c, _, _ in bad], [("1a2b/3", "C.1")])
        self.assertEqual(underived, 1)

    def test_a_collapse_and_a_bad_request(self):
        lines = split_lines("split-1", "C", "C.1", 0, 2) + merge_lines("merge-1", "C", "C.1", 5, collapse=True, tidy=False) \
            + ["auto Split split-2: requested something odd", "auto Split split-2: done"]
        lops, skipped = S.layout_ops(S.operations(stamp(lines)))
        self.assertEqual(skipped, ["Split split-2"])
        self.assertTrue(lops[1].dead)
        self.assertNotIn("C.1", S.expected_by_pair(self.R, lops)["1a2b/5"])


class FlatnessTests(unittest.TestCase):
    def test_a_ratchet_passes_and_a_leak_fails(self):
        ratchet = [(t * 60.0, 200.0 + min(t, 20) * 2.0) for t in range(180)]
        flat = S.flatness(ratchet, 900, 25, 0.05, 1800)
        self.assertTrue(flat.ok)
        self.assertEqual((flat.first_max, flat.second_max), (240.0, 240.0))
        leak = [(t * 60.0, 200.0 + t * 0.5) for t in range(180)]
        flat = S.flatness(leak, 900, 25, 0.05, 1800)
        self.assertFalse(flat.ok)
        self.assertAlmostEqual(flat.slope_per_h, 30.0, places=6)

    def test_too_short_or_empty(self):
        self.assertIsNone(S.flatness([], 900, 1, 0, 1800))
        self.assertIsNone(S.flatness([(t * 60.0, 1.0) for t in range(30)], 900, 1, 0, 1800))
        self.assertTrue(S.flatness([(t * 60.0, 5.0) for t in range(60)], 900, 0, 0, 1800).ok)


if __name__ == "__main__":
    unittest.main()
