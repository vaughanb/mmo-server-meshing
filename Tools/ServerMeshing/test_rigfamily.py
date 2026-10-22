"""Unit tests for rigfamily's readers (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py")."""
import unittest

import rigfamily as F
import rigsplitcrash

G1, K2, G2, K1 = (0.0, -100.0, 100.0, 200.0), (0.0, -100.0, 100.0, 100.0), (0.0, 0.0, 100.0, 100.0), (-100.0, -100.0, 100.0, 200.0)


def entry(cell, state, rect=None):
    owned = {"minX": rect[0], "minZ": rect[1], "maxX": rect[0] + rect[2], "maxZ": rect[1] + rect[3]} if rect else \
        {"minX": 0, "minZ": 0, "maxX": 0, "maxZ": 0}
    return {"id": cell, "mapId": "M", "baseId": "C", "state": state, "owned": owned}


class PlanTests(unittest.TestCase):
    def test_the_plans(self):
        merge, collapse = F.PLANS["mergechildren"], F.PLANS["collapsesibling"]
        self.assertEqual([b.user for b in merge], [f"meshbot{i}" for i in range(2, 10)])
        self.assertEqual(F.owned_players(merge), {"C.1": 2, "C.2": 3, "C": 3})
        self.assertEqual(F.expected_survivor(merge), "C.2")
        self.assertEqual(F.owned_players(collapse), {"C.1": 4, "C": 4})
        self.assertEqual(F.owned_players(merge, "C.2"), {"C.2": 5, "C": 3})
        self.assertTrue(all(-100 < x < 100 and -100 < z < 100 for b in merge + collapse for x, z in [b.point]))
        self.assertEqual({b.side for b in merge if b.point[0] > 0}, {"+"})

    def test_the_survivor_rule(self):
        tie = F.bots([(40.0, -50.0), (40.0, 50.0)])
        self.assertEqual(F.expected_survivor(tie), "C.1", "a tie goes to the lower id")
        self.assertEqual(F.expected_survivor(F.bots([(40.0, -50.0), (40.0, -60.0), (40.0, 50.0)])), "C.1")

    def test_hops(self):
        self.assertEqual(F.expected_hops("mergechildren", (40.0, -75.0)), [("C", "C.1"), ("C.1", "C.2")])
        self.assertEqual(F.expected_hops("mergechildren", (20.0, 75.0)), [("C", "C.1"), ("C.1", "C.2")])
        self.assertEqual(F.expected_hops("mergechildren", (40.0, -75.0), survivor="C.1"), [("C", "C.1")])
        self.assertEqual(F.expected_hops("mergechildren", (40.0, 75.0), survivor="C.1"), [("C", "C.1"), ("C.1", "C.2"), ("C.2", "C.1")])
        self.assertEqual(F.expected_hops("collapsesibling", (40.0, -75.0)), [("C", "C.1")])
        self.assertEqual(F.expected_hops("collapsesibling", (-40.0, -75.0)), [])

    def test_ops(self):
        ops = F.ops("mergechildren", merge_pairs={"e1": "a/8"})
        self.assertEqual([(o.kind, o.a, o.b, o.dead) for o in ops], [("split", "C", "C.1", False), ("split", "C.1", "C.2", False),
                                                                     ("merge", "C.2", "C.1", False)])
        ops = F.ops("collapsesibling", merge_pairs={"e1": "a/8"})
        self.assertEqual((ops[-1].a, ops[-1].b, ops[-1].dead), ("C.1", "C.2", True))
        self.assertEqual(len(F.ops("collapsesibling")), 2)


class StatusTests(unittest.TestCase):
    def test_states(self):
        states = ["requested C.2 C.1", "e1 broadcast a/8", "e1 gate open", "e2 staged", "e2 acked", "e2 broadcast a/9",
                  "drain gate open", "retire sent", "C.1 exited", "channel C.1 removed", "e3 broadcast a/10", "done"]
        self.assertEqual(F.state_problems(states, F.MERGE_STATES, "C.2", "C.1"), [])
        self.assertTrue(F.state_problems(states[:-1] + ["tidy broadcast a/11", "done"], F.MERGE_STATES, "C.2", "C.1"))
        self.assertTrue(F.state_problems(states, F.MERGE_STATES, "C.1", "C.2"))
        collapse = ["requested C.1 C.2 collapse", "e1 broadcast a/8", "e1 gate open", "e2 staged", "C.2 exited before drained",
                    "e2 broadcast a/9", "channel C.2 removed", "e3 broadcast a/10", "done"]
        self.assertEqual(F.state_problems(collapse, F.COLLAPSE_STATES, "C.1", "C.2"), [])
        self.assertTrue(F.state_problems(collapse[1:], F.COLLAPSE_STATES, "C.1", "C.2"))

    def supervisor(self, texts):
        return rigsplitcrash.parse_supervisor([f"7 {t}" for t in texts], 7)

    def test_supervisor(self):
        good = []
        for n in (1, 2, 3):
            good += [f"Supervisor: C.2 down 20 s, restart {n} of 3 requested",
                     f"Supervisor: C.2 restart {n} failed (no hello from a new incarnation in 60 s; no known process id, left to the kit)"]
        good.append("Supervisor: C.2 collapse queued after 3 failed restart(s)")
        self.assertEqual(F.supervisor_problems(self.supervisor(good), "C.2"), [])
        self.assertEqual(len(F.failure_reasons(self.supervisor(good), "C.2")), 3)
        self.assertTrue(F.supervisor_problems(self.supervisor(good[:-1]), "C.2"))
        self.assertTrue(F.supervisor_problems(self.supervisor(good[2:]), "C.2"))
        cannot = good + ["Supervisor: C.2 cannot collapse (no live cell of its family shares a full edge); left down"]
        self.assertTrue(any("unexpected" in p for p in F.supervisor_problems(self.supervisor(cannot), "C.2")))
        back = good[:1] + ["Supervisor: C.2 back, incarnation 9f"] + good[1:]
        self.assertEqual(F.supervisor_problems(self.supervisor(back), "C.2"), [], "a hello that got out is allowed")

    def test_store(self):
        store = {"broadcast": {"entries": [entry("C", 2, K1), entry("C.2", 2, G1), entry("C.1", 4)]}, "hasOperation": False}
        self.assertEqual(F.store_problems(store, "M", {"C": K1, "C.2": G1}, ["C.1"]), [])
        self.assertEqual(F.store_problems({"broadcast": {"entries": [entry("C", 2, K1), entry("C.2", 2, G1)]}}, "M",
                                          {"C": K1, "C.2": G1}, ["C.1"]), [])
        self.assertTrue(F.store_problems(store, "M", {"C": K1, "C.2": G2}, ["C.1"]))
        self.assertTrue(F.store_problems(store, "M", {"C": K1, "C.1": G1}, ["C.2"]))
        live = {"broadcast": {"entries": [entry("C", 2, K1), entry("C.2", 2, G1), entry("C.1", 2, K2)]}}
        self.assertTrue(F.store_problems(live, "M", {"C": K1, "C.2": G1}, ["C.1"]))
        self.assertTrue(F.store_problems(dict(store, hasOperation=True), "M", {"C": K1, "C.2": G1}, ["C.1"]))
        self.assertEqual(F.fenced({"fencedIncarnations": [{"cellId": "c.2", "incarnation": "ab"}]}, "C.2"), ["ab"])

    def test_the_fault(self):
        self.assertEqual(F.hello_fault("C.2"), "C.2:1")
        self.assertEqual(F.marker_name("C.2"), "ServerMesh.FaultFired.MESH_FAULT_DIE_ON_HELLO.C.2")


if __name__ == "__main__":
    unittest.main()
