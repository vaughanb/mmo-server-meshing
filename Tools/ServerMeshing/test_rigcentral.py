"""Unit tests for rigcentral (Slice 5's central-restart graders, pure): python -m unittest Tools/ServerMeshing/test_rigcentral.py"""
import unittest

import rigcentral
import rigsplit

MAP = "ServerMeshTestMap"
SPLIT = rigsplit.Split(MAP, "C", (-100.0, -100.0, 200.0, 200.0), "x", 0.0, "+", "C.1")
P, K, R = (-100.0, -100.0, 200.0, 200.0), (-100.0, -100.0, 100.0, 200.0), (0.0, -100.0, 100.0, 200.0)


def L(stamp, text, level="INFO"):
    return f" {level} Mesh [2026-10-19 {stamp}] - {text}"


SUMMARY1 = L("10:00:00", "Coordinator 1a2b: Dynamic maps ServerMeshTestMap AdoptStatic; store D:/x/Temp/ServerMesh.Coordinator.json (fresh); broadcast 1a2b/1")
SUMMARY2 = L("10:05:00", "Coordinator 1a2c: Dynamic maps ServerMeshTestMap AdoptStored; store D:/x/Temp/ServerMesh.Coordinator.json (loaded); broadcast 1a2b/4")
ADOPTED = L("10:05:01", "Coordinator recovery: map ServerMeshTestMap adopted layout 1a2b/4 from the store with 2 entries (C, C.1)")


class CentralLogTests(unittest.TestCase):
    def test_lifetimes_by_stamp_across_severities(self):
        lines = [SUMMARY1, L("10:00:05", "Split split-1: requested C x 0 + child C.1"),
                 L("10:04:50", "Cell C.1 unregistered from the cluster (connection 3, incarnation x1)", "WARN"),
                 L("10:04:59", "Central channels from the world registry: 12 (10 added)"),
                 SUMMARY2, ADOPTED, L("10:05:02", "Coordinator recovery: family C of map ServerMeshTestMap frozen: no hello from C.1 after 60 s", "ERROR")]
        lives = rigcentral.lifetimes(lines)
        self.assertEqual([len(life) for life in lives], [3, 4])
        self.assertEqual(rigcentral.summary(lives[1])[1], "ServerMeshTestMap AdoptStored")
        # The start-up a second before the summary opens its lifetime (the first live run's 17:38 split saw it).
        early = [L("10:00:00", "Central channels from the world registry: 12 (10 added)"), L("10:00:01", SUMMARY1.split(" - ", 1)[1]),
                 L("10:00:05", "Split split-1: done"), L("10:00:20", "Central channels from the world registry: 12 (10 added)"),
                 L("10:00:21", SUMMARY2.split(" - ", 1)[1]), L("10:00:21", ADOPTED.split(" - ", 1)[1])]
        self.assertEqual([len(life) for life in rigcentral.lifetimes(early)], [3, 3])
        self.assertEqual(rigcentral.lifetimes([L("09:00:00", "early"), SUMMARY1])[0], [L("09:00:00", "early")])
        self.assertEqual(rigcentral.lifetimes(["no stamp"]), [["no stamp"]])

    def test_adoptions_resumed_and_states(self):
        lines = [SUMMARY2, ADOPTED, L("10:05:01", "Coordinator recovery: map ServerMeshTestMap adopted layout 1a2b/6 from cell C.1 with 2 entries (C, C.1)"),
                 L("10:05:02", "Merge merge-1: resumed at DrainGate after a central restart"),
                 L("10:05:30", "Merge merge-1: drain gate open"), L("10:05:30", "Merge merge-1: retire sent"),
                 L("10:06:00", "Merge merge-1: e3 broadcast 1a2c/7"), L("10:06:01", "Layout op refused: cooldown 20 s left on C", "WARN")]
        adoptions = rigcentral.adoptions(lines)
        self.assertEqual(adoptions[0], {"map": MAP, "pair": "1a2b/4", "source": "the store", "entries": 2, "ids": ["C", "C.1"]})
        self.assertEqual(adoptions[1]["source"], "cell C.1")
        self.assertEqual(rigcentral.resumed(lines), [("Merge", "merge-1", "DrainGate")])
        self.assertEqual(rigcentral.op_states(lines, "Merge"), [("merge-1", "resumed at DrainGate after a central restart"), ("merge-1", "drain gate open"),
                                                              ("merge-1", "retire sent"), ("merge-1", "e3 broadcast 1a2c/7")])
        self.assertEqual(rigcentral.op_states(lines, "Split"), [])
        self.assertEqual(rigcentral.refusals(lines), ["cooldown 20 s left on C"])

    def test_frozen_login_and_cell_lines(self):
        central = [L("10:05:02", "Coordinator recovery: family C of map ServerMeshTestMap frozen: no hello from C.1 after 60 s", "ERROR"),
                   L("10:05:03", "Coordinator note to C (recovery-frozen): map ServerMeshTestMap is frozen", "ERROR"),
                   L("10:05:10", "Login routing: meshbotChar at (60.00, 0.08, 70.00) -> channel C.1 (client asked for 'C', row says 'C')"),
                   L("10:05:11", "Login routing: no server for cell C of ServerMeshTestMap (meshbot6Char at (-20.0, 0.0, 75.0)); the nearest live cell N serves it", "WARN")]
        self.assertEqual(len(rigcentral.frozen_lines(central)), 2)
        self.assertEqual(rigcentral.login_routes(central), [("meshbotChar", "C.1")])
        self.assertEqual(rigcentral.login_routes(central, "nobody"), [])
        self.assertTrue(rigcentral.LOGIN_NO_SERVER.search(central[3]))
        cell = [L("10:00:01", "LayoutHello sent: cell C incarnation aaaa layout 0/0"), L("10:05:05", "LayoutHello sent: cell C incarnation aaaa layout 1a2b/4"),
                L("10:05:06", "Layout answer 1a2b/4 on C: already applied"),
                L("10:04:56", "Database connection lost; reconnecting to 127.0.0.1:6100", "WARN"), L("10:05:40", "Database connection to 127.0.0.1:6100 is open"),
                L("10:05:05", "FAULT: dying on layout hello 2 (MESH_FAULT_DIE_ON_HELLO)", "ERROR")]
        self.assertEqual(rigcentral.hellos_sent(cell, "C"), ["aaaa", "aaaa"])
        self.assertEqual(rigcentral.answers(cell, "C"), ["1a2b/4"])
        self.assertEqual(rigcentral.database_reconnect(cell), (1, 1))
        self.assertEqual(rigcentral.database_reconnect(cell, after=__import__("datetime").datetime(2026, 10, 19, 10, 5, 0)), (0, 1))
        self.assertEqual(rigcentral.hello_faults(cell), [2])


def apply(cell, inc, version, owned, interest):
    return L("10:00:00", f"Layout {inc}/{version} applied: {cell} owns {rigsplit.format_rect(owned)} interest {rigsplit.format_rect(interest)}, 8 targets")


def own(cell):
    return L("10:00:00", f"Own cell from channel id: {cell} of map {MAP}, grid (0,0), world x [-100, 100) z [-100, 100), 8 neighbours")


class ApplyGradeTests(unittest.TestCase):
    PAIRS = {"e1": "1a2b/2", "e2": "1a2b/3", "e3": "1a2b/4"}
    MERGE = {"e1": "1a2b/5", "e2": "1a2b/6", "e3": "1a2c/7", "tidy": "1a2c/8"}

    def test_expected_lives_per_case(self):
        split = rigcentral.expected_lives("split", SPLIT, "C", P, self.PAIRS)
        self.assertEqual(split, [[(P, P, None), (P, P, "1a2b/2"), (K, P, "1a2b/3"), (K, K, "1a2b/4")]])
        merge = rigcentral.expected_lives("merge", SPLIT, "C", P, self.PAIRS, self.MERGE)
        self.assertEqual(merge[0][4:], [(K, P, "1a2b/5"), (P, P, "1a2b/6"), (P, P, "1a2c/7"), (P, P, "1a2c/8")])
        child = rigcentral.expected_lives("merge", SPLIT, "C.1", None, self.PAIRS, self.MERGE)
        self.assertEqual(child[0][3:], [(R, R, "1a2b/5"), (None, R, "1a2b/6")])
        base = rigcentral.expected_lives("base", SPLIT, "C", P, self.PAIRS, adopted="1a2b/4")
        self.assertEqual(base[1], [(K, K, "1a2b/4")])
        self.assertEqual(len(rigcentral.expected_lives("base", SPLIT, "E", (100.0, -100.0, 200.0, 200.0), self.PAIRS)), 1)

    def test_two_incarnations_in_order_pass_and_out_of_order_fail(self):
        info = [own("C"), apply("C", "1a2b", 1, P, P), apply("C", "1a2b", 2, P, P), apply("C", "1a2b", 3, K, P), apply("C", "1a2b", 4, K, K),
                apply("C", "1a2b", 5, K, P), apply("C", "1a2b", 6, P, P), apply("C", "1a2c", 7, P, P), apply("C", "1a2c", 8, P, P)]
        def lives(own_rect):
            return rigcentral.expected_lives("merge", SPLIT, "C", own_rect, self.PAIRS, self.MERGE)
        self.assertEqual(rigcentral.lifetime_apply_violations("C", info, SPLIT, lives, label="the merge case"), [])
        bad = info[:7] + [apply("C", "1a2c", 7, P, P), apply("C", "1a2b", 8, P, P), apply("C", "1a2c", 9, P, P)]
        problems = rigcentral.lifetime_apply_violations("C", bad, SPLIT, lives, label="the merge case")
        self.assertTrue(any("out of order" in p for p in problems), problems)
        plain = [own("C"), apply("C", "1a2b", 1, P, P), apply("C", "1a2b", 2, P, P), apply("C", "1a2b", 3, K, P), apply("C", "1a2b", 4, K, K)]
        self.assertEqual(rigcentral.lifetime_apply_violations("C", plain, SPLIT, lambda o: rigcentral.expected_lives("split", SPLIT, "C", o, self.PAIRS)), [])

    def test_disallowed_and_bot_problems(self):
        lines = [L("10:05:05", "FAULT: dying on layout hello 2 (MESH_FAULT_DIE_ON_HELLO)", "ERROR"), L("10:05:06", "Fence: C incarnation x fenced (test): saving 0 player(s)")]
        self.assertEqual(len(rigcentral.disallowed_lines("C", lines)), 1)
        self.assertEqual(len(rigcentral.disallowed_lines("C", lines, retiring="C.1")), 1)
        result = {"migrations": 1, "relogins": 0, "allReady": True}
        self.assertEqual(rigcentral.bot_problems(result, 1, 0, []), [])
        self.assertEqual(rigcentral.bot_problems(result, 1, 0, ["[MeshAuto] Session lost (map connection); re-login 1"]), ["1 'Session lost' line(s) (map connection)"])
        self.assertEqual(rigcentral.bot_problems(result, 0, 0, []), ["migrations=1, expected 0"])


if __name__ == "__main__":
    unittest.main()
