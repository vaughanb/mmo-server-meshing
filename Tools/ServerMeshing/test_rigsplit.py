"""Unit tests for rigsplit, Slice 2b's split graders (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py")."""
import unittest

import rigsplit

MAP = "ServerMeshTestMap"
C = (-100.0, -100.0, 200.0, 200.0)
KEPT, GIVEN = (-100.0, -100.0, 100.0, 200.0), (0.0, -100.0, 100.0, 200.0)
SPLIT = rigsplit.Split(MAP, "C", C, "x", 0.0, "+", "C.1")


def stamp(n=0):
    return f"[2026-10-16 10:00:{n:02d}]"


def own(cell="C", world="x [-100, 100) z [-100, 100)"):
    return f" INFO Mesh {stamp()} - Own cell from channel id: {cell} of map {MAP}, grid (1,1), world {world}, 8 neighbours"


def own_from_layout(cell="C.1"):
    return f" INFO Mesh {stamp(1)} - Own cell from layout: {cell} of map {MAP}, grid (-1,-1), world x [0, 100) z [-100, 100), 5 neighbours"


def span(rect):
    return rigsplit.format_rect(rect)


def applied(version, owned, interest, cell="C", incarnation="1a2b"):
    return (f" INFO Mesh {stamp(2)} - Layout {incarnation}/{version} applied: {cell} owns {span(owned)} "
            f"interest {span(interest)}, 8 targets")


PLAYER = f" INFO MeshTrace {stamp(5)} - 10:00:05.123 S player registered meshbot2Char conn=0 at (20, 0, 75)"
NEIGHBOURS = f" INFO Mesh {stamp(3)} - Layout 1a2b/3 neighbours applied: C2 band E, E.1"


def base_lines(*extra):
    return [own(), applied(1, C, C), applied(2, C, C), applied(3, KEPT, C), applied(4, KEPT, KEPT), PLAYER, *extra]


def child_lines(*extra):
    return [own_from_layout(), applied(2, None, GIVEN, "C.1"), applied(3, GIVEN, GIVEN, "C.1"),
            applied(4, GIVEN, GIVEN, "C.1"), PLAYER, *extra]


E_WORLD = "x [100, 300) z [-100, 100)"
E_RECT = (100.0, -100.0, 200.0, 200.0)


def other_lines(owned=E_RECT):
    return [own("E", E_WORLD)] + [applied(v, owned, E_RECT, "E") for v in (1, 2, 3, 4)]


class RectTests(unittest.TestCase):
    def test_parse_span_refuses(self):
        for text in ("x[0,1)", "", "x[a,1) z[0,1)", "everything"):
            with self.subTest(text), self.assertRaises(ValueError):
                rigsplit.parse_span(text)

    def test_parse_span(self):
        cases = {"nothing": ("nothing", None), "the base": ("x[-100,100) z[-100,100)", C),
                 "the child": ("x[0,100) z[-100,100)", GIVEN)}
        for name, (text, rect) in cases.items():
            with self.subTest(name):
                self.assertEqual(rigsplit.parse_span(text), rect)
                self.assertEqual(rigsplit.format_rect(rect), text)

    def test_split_rects_refuses(self):
        cases = {"cut on the edge": (C, "x", -100.0, "+"), "cut outside": (C, "x", 150.0, "+"),
                 "unknown axis": (C, "y", 0.0, "+"), "unknown side": (C, "x", 0.0, "x")}
        for name, args in cases.items():
            with self.subTest(name), self.assertRaises(ValueError):
                rigsplit.split_rects(*args)

    def test_split_rects(self):
        cases = {
            "x, high side to the child": ((C, "x", 0.0, "+"), (KEPT, GIVEN)),
            "x, low side to the child": ((C, "x", 0.0, "-"), (GIVEN, KEPT)),
            "z, high side": ((C, "z", 50.0, "+"), ((-100.0, -100.0, 200.0, 150.0), (-100.0, 50.0, 200.0, 50.0))),
            "E at 200": ((E_RECT, "x", 200.0, "+"), ((100.0, -100.0, 100.0, 200.0), (200.0, -100.0, 100.0, 200.0))),
        }
        for name, (args, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(rigsplit.split_rects(*args), expected)

    def test_same_rect(self):
        cases = {"nothing vs a rect": (None, C, False), "off by a metre": (C, (-99.0, -100.0, 200.0, 200.0), False),
                 "any": (rigsplit.ANY, None, True), "rounded": (C, (-100.4, -100.0, 200.4, 200.0), True),
                 "nothing vs nothing": (None, None, True)}
        for name, (a, b, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(rigsplit.same_rect(a, b), expected)

    def test_parse_apply(self):
        self.assertIsNone(rigsplit.parse_apply(NEIGHBOURS))
        self.assertEqual(rigsplit.parse_apply(applied(2, None, GIVEN, "C.1")),
                         {"incarnation": "1a2b", "version": 2, "cell": "C.1", "owned": None, "interest": GIVEN})


class SplitApplyTests(unittest.TestCase):
    def test_split_apply_violations(self):
        b = base_lines()
        cases = {
            # failures first
            "base: no lifetime": ("C", [applied(1, C, C)], 1),
            "base: only the first layout": ("C", [own(), applied(1, C, C)], 1),
            "base: interest shrank at e2": ("C", [own(), applied(1, C, C), applied(2, C, C), applied(3, KEPT, KEPT),
                                                  applied(4, KEPT, KEPT)], 1),
            "base: still interested in R after e3": ("C", b[:4] + [applied(4, KEPT, C)], 1),
            "base: own cell is not the split's": ("C", [own(world=E_WORLD)] + b[1:], 1),
            "base: a player before the first apply": ("C", [own(), PLAYER] + b[1:5], 1),
            "base: two coordinator incarnations": ("C", b[:4] + [applied(4, KEPT, KEPT, incarnation="ff")], 1),
            "base: versions not rising": ("C", b[:3] + [applied(2, KEPT, C), applied(4, KEPT, KEPT)], 1),
            "base: a Static-neighbour line": ("C", base_lines(NEIGHBOURS), 1),
            "base: restarted": ("C", b + [own()] + b[1:], 1),
            "child: owns R at e1": ("C.1", [own_from_layout(), applied(2, GIVEN, GIVEN, "C.1")] + child_lines()[2:], 1),
            "child: four applies": ("C.1", child_lines(applied(5, GIVEN, GIVEN, "C.1")), 1),
            "child: never applied": ("C.1", [PLAYER], 1),
            "other: owns something else": ("E", other_lines(GIVEN), 4),
            "other: one layout only": ("E", other_lines()[:2], 1),
            # passing
            "base": ("C", b, 0),
            "child": ("C.1", child_lines(), 0),
            "other": ("E", other_lines(), 0),
            "lines of an earlier process are not graded": ("C", [applied(9, C, C), PLAYER] + b, 0),
        }
        for name, (cell, lines, problems) in cases.items():
            with self.subTest(name):
                found = rigsplit.split_apply_violations(cell, lines, SPLIT)
                self.assertEqual(len(found), problems, found)

    def test_expected_applies(self):
        self.assertEqual(rigsplit.expected_applies(SPLIT, "C", C), [(C, C), (C, C), (KEPT, C), (KEPT, KEPT)])
        self.assertEqual(rigsplit.expected_applies(SPLIT, "C.1", rigsplit.ANY),
                         [(None, GIVEN), (GIVEN, GIVEN), (GIVEN, GIVEN)])
        self.assertEqual(rigsplit.expected_applies(SPLIT, "N", E_RECT), [(E_RECT, rigsplit.ANY)] * 4)

    def test_neighbour_bands(self):
        lines = [NEIGHBOURS, NEIGHBOURS.replace("1a2b/3", "1a2b/4").replace("band E, E.1", "band E.1,NE"),
                 NEIGHBOURS.replace("C2 band E, E.1", "X band nothing"), applied(1, C, C)]
        self.assertEqual(rigsplit.neighbour_bands(lines, "C2"), [("1a2b/3", "C2", ["E", "E.1"]),
                                                                 ("1a2b/4", "C2", ["E.1", "NE"])])
        self.assertEqual(rigsplit.neighbour_bands(lines)[-1], ("1a2b/3", "X", []))


class OpStatusTests(unittest.TestCase):
    LINES = ["7 Split split-1: requested C x 0 + child C.1", "7 Split split-1: channel added",
             "7 Split split-1: e1 broadcast 1a2b/2", "7 Split split-1: e2 broadcast 1a2b/3",
             "7 Split split-1: drain gate open", "7 Split split-1: e3 broadcast 1a2b/4", "7 Split split-1: done"]

    def test_parse_op_status(self):
        cases = {
            # failures first
            "refused": (["7 Layout op refused: maxCellsPerBase"], "refused", {"refused": "maxCellsPerBase"}),
            "aborted": (self.LINES[:2] + ["7 Split split-1: aborted X silent for 60 s"], "aborted",
                        {"aborted": "X silent for 60 s", "done": False}),
            "frozen": (self.LINES[:4] + ["7 Split split-1: frozen P rejected e3"], "frozen", {"frozen": "P rejected e3"}),
            "stragglers": (self.LINES[:4] + ["7 Split split-1: drain timeout, 3 straggler(s)"], None,
                           {"drain": "timeout", "stragglers": 3}),
            "another sequence only": (["6 Split split-1: done"], None, {"op": None, "done": False}),
            "still running": (self.LINES[:3], None, {"pairs": {"e1": "1a2b/2"}}),
            # passing
            "done": (self.LINES + ["7 something new"], "done",
                     {"op": "split-1", "requested": "C x 0 + child C.1", "drain": "open", "stragglers": None,
                      "pairs": {"e1": "1a2b/2", "e2": "1a2b/3", "e3": "1a2b/4"}, "other": ["something new"]}),
        }
        for name, (lines, outcome, fields) in cases.items():
            with self.subTest(name):
                status = rigsplit.parse_op_status(lines, "7")
                self.assertEqual(rigsplit.op_outcome(status), outcome)
                for key, value in fields.items():
                    self.assertEqual(status[key], value, key)

    def test_command_line(self):
        with self.assertRaises(ValueError):
            rigsplit.command_line("7", "C", "y", 0.0, "+", "C.1")
        self.assertEqual(rigsplit.command_line("7", "C", "x", 0.0, "+", "C.1"), "7 split C x 0 + C.1")
        self.assertEqual(rigsplit.command_line(12, "E", "x", 200.0, "+", "E.1"), "12 split E x 200 + E.1")


class AutoClientTests(unittest.TestCase):
    RESULT = ("[MeshAuto] RESULT stand: reached=True exp+0 gold+0 level+0 questKills+0 migrations=1 allReady=True "
              "maxGap=420ms maxJump=0.35m yield=2 pickup=True -> PASS")

    def test_parse_result(self):
        self.assertEqual(rigsplit.parse_result(""), {})
        self.assertEqual(rigsplit.parse_result(self.RESULT),
                         {"passed": True, "migrations": 1, "allReady": True, "maxGap": 420.0, "maxJump": 0.35,
                          "yield": 2, "pickup": True, "reached": True})
        self.assertEqual(rigsplit.parse_result("RESULT stand: reached=False -> FAIL"), {"passed": False, "reached": False})

    def test_migration_problems(self):
        r = rigsplit.parse_result
        cases = {
            # failures first
            "no result": ({}, 1, 1),
            "an R-bot never migrated": (r("migrations=0 allReady=True maxGap=0"), 1, 1),
            "not ready, slow": (r("migrations=1 allReady=False maxGap=1000"), 1, 2),
            "a P-bot migrated": (r("migrations=1 allReady=True maxGap=10"), 0, 1),
            # passing
            "an R-bot": (r(self.RESULT), 1, 0),
            "a P-bot (gap not graded)": (r("migrations=0 allReady=False maxGap=0"), 0, 0),
        }
        for name, (result, expected, problems) in cases.items():
            with self.subTest(name):
                self.assertEqual(len(rigsplit.migration_problems(result, expected)), problems)

    def test_migrations_and_standing(self):
        lines = ["[MeshAuto] Standing at (20.00, 0.08, 75.00) party=0; watching exp/gold/quests for 300s",
                 "[MeshAuto] MIGRATION 1: gap=312 ready=True jump=0.42 pos=20.1,75.3",
                 "[MeshAuto] MIGRATION 2: gap=95ms ready=False jump=1.5m pos=-3.5, 7"]
        self.assertEqual(rigsplit.standing_at(lines), (20.0, 75.0))
        self.assertIsNone(rigsplit.standing_at(lines[1:]))
        self.assertEqual(rigsplit.parse_migrations(lines),
                         [{"n": 1, "gap": 312.0, "ready": True, "jump": 0.42, "x": 20.1, "z": 75.3},
                          {"n": 2, "gap": 95.0, "ready": False, "jump": 1.5, "x": -3.5, "z": 7.0}])
        self.assertEqual([rigsplit.side_of(p, "x", 0.0) for p in ((-0.1, 0), (0.0, 0), (5, -5))], ["-", "+", "+"])


class HandoffLineTests(unittest.TestCase):
    def test_handoff_start(self):
        rx = rigsplit.handoff_start("meshbot2Char", "C", "C.1")
        line = "Handoff START meshbot2Char (abc-1) zone C->C.1 pos=(20.0, 0.0, 75.0) vel=(0, 0, 0)"
        for text, expected in ((line.replace("C->C.1", "C->C.10"), False), (line.replace("C->C.1", "C->C11"), False),
                               (line.replace("meshbot2Char", "meshbot22Char"), False), (line, True),
                               (line.replace(" pos=(20.0, 0.0, 75.0) vel=(0, 0, 0)", ""), True)):
            with self.subTest(text):
                self.assertEqual(bool(rx.search(text)), expected)

    def test_entity_lines(self):
        lines = ["Monster promoted by handoff m1 C->C.1 at (40, 0, 10) hp=5/5 home=C target=none",
                 "Monster spawned by handoff m2 C->C.1 at (40, 0, 10) hp=5/5 home=C target=none",
                 "Monster promoted by handoff m3 E->C at (40, 0, 10) hp=5/5 home=E target=none",
                 "Static handoff s1 zone C->C.1: sent", "Static handoff s1 zone C->C.1: promoted",
                 "Static handoff s2 zone C->C.1: failed", "Synthetic adopted Load_7 zone C->C.1",
                 "Synthetic adopted Load_8 zone C->E", "Prewarm window 5.00 s: queue 3 spawned 40 | max frame 21.5 ms",
                 "Prewarm window 5.00 s: queue 0 spawned 2 | max frame 9 ms"]
        mh, sh = rigsplit.MONSTER_HANDOFF, rigsplit.STATIC_HANDOFF
        self.assertEqual([rigsplit.count_kind(lines, mh, k, "C", "C.1") for k in ("promoted", "spawned")], [1, 1])
        self.assertEqual(rigsplit.count_kind(lines, mh, "promoted", "E", "C"), 1)
        self.assertEqual([rigsplit.count_kind(lines, sh, k, "C", "C.1") for k in ("sent", "promoted", "failed")],
                         [1, 1, 1])
        self.assertEqual(rigsplit.adoptions(lines, "C", "C.1"), ["Load_7"])
        self.assertEqual(rigsplit.prewarm_max_frame(lines), 21.5)
        self.assertIsNone(rigsplit.prewarm_max_frame(lines[:3]))

    def test_monster_spawns(self):
        prewarm = ["Ghost spawned Monster  (C:ab:77) from zone C at (4.46, 0.08, -38.59) (silenced 4)",
                   "Ghost spawned Monster Bat (C:ab:52) from zone C at (1.00, 0.08, 1.00) (silenced 4)"]
        lines = ["Monster spawned by handoff C:ab:89 C->C.1 at (1, 0, 1) hp=6/6 home=C target=none",
                 "Monster spawned by handoff C:ab:60 C->C.1 at (1, 0, 1) hp=6/6 home=C target=none",
                 "Monster spawned by handoff E:cd:3 C->C.1 at (1, 0, 1) hp=6/6 home=E target=none",
                 "Monster spawned by handoff C:ab:99 C.1->C at (1, 0, 1) hp=6/6 home=C target=none",
                 "Monster promoted by handoff C:ab:77 C->C.1 at (1, 0, 1) hp=6/6 home=C target=none"]
        self.assertEqual(rigsplit.monster_spawns(lines, prewarm, "C", "C.1"), (["C:ab:60", "E:cd:3"], ["C:ab:89"]))
        self.assertEqual(rigsplit.mesh_id_parts("m1"), ("m1", None))

    def test_until_applied(self):
        lines = ["a", "Layout 1a2b/3 applied: C.1 owns x[0,100)", "b", "Layout 1a2b/4 applied: C.1 owns x[0,100)", "c"]
        self.assertEqual(rigsplit.until_applied(lines, "1a2b/4"), lines[:3])
        self.assertEqual(rigsplit.until_applied(lines, "1a2b/9"), lines)
        self.assertEqual(rigsplit.until_applied(lines, None), lines)
        self.assertEqual(rigsplit.until_applied(["x", "Layout 1a2b/4 neighbours applied: C2 band E.1"], "1a2b/4"), ["x"])

    def test_ghost_at(self):
        line = "Ghost spawned Player meshbot3Char (id-3) from zone E.1 at (-140.12, 0.08, -75.00) (silenced 4)"
        cases = {"another owner": ("meshbot3Char", "E", -140, -75, False), "too far": ("meshbot3Char", "E.1", -150, -75, False),
                 "another bot": ("meshbot2Char", "E.1", -140, -75, False), "there": ("meshbot3Char", "E.1", -140, -74, True)}
        for name, (who, owner, x, z, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(rigsplit.ghost_at([line], who, owner, x, z), expected)
        moved = "Ghost owner changed Player meshbot3Char (id-3) zone E->E.1 at (-140.12, 0.08, -75.00)"
        cases = {"the new owner": ("E.1", True), "the old owner": ("E", False)}
        for name, (owner, expected) in cases.items():
            with self.subTest("owner changed, " + name):
                self.assertEqual(rigsplit.ghost_at([moved], "meshbot3Char", owner, -140, -75), expected)

    def test_split_op_problem(self):
        cases = {"a retired child": ("Split retire: C.1 retired before it owned anything", True),
                 "an abort in a cell's log": ("Split split-1: aborted e1 gate timeout", True),
                 "a merge": ("Merge of C.1 into C", True), "the apply line": (applied(3, KEPT, C), False),
                 "a static handoff": ("Static handoff s1 zone C->C.1: sent", False)}
        for name, (line, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(bool(rigsplit.SPLIT_OP_PROBLEM.search(line)), expected)


if __name__ == "__main__":
    unittest.main()
