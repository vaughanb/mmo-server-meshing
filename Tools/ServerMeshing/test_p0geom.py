"""Unit tests for p0geom (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py"): exact counts on hand-built
geometry (step8a_design.md decision G)."""
import contextlib
import io
import json
import math
import os
import random
import tempfile
import unittest

import p0geom

FULL, BOOST, PATROL = 125.33, 250.67, 50.13   # player ship speeds
AI_PATROL = 37.6


def player(x, y=0.0, z=0.0, speed=FULL, heading=(1.0, 0.0, 0.0)):
    return p0geom.Ship((float(x), float(y), float(z)), heading, speed, True, "")


def enemy(x, y=0.0, z=0.0, speed=AI_PATROL, heading=(-1.0, 0.0, 0.0)):
    return p0geom.Ship((float(x), float(y), float(z)), heading, speed, False, "")


class RefusalTests(unittest.TestCase):
    def test_bad_inputs_are_refused(self):
        cases = {
            "a fight without a plus": (p0geom.parse_fight, ("64",), "is not <players>\\+<ai>"),
            "a word fight": (p0geom.parse_fight, ("a+b",), "is not <players>\\+<ai>"),
            "a fight without players": (p0geom.parse_fight, ("0+64",), "has no player ship"),
            "a zero A0": (p0geom.visible_range, (FULL, FULL, 0.0), "must be above 0"),
            "a zero cap": (p0geom.visible_range, (FULL, FULL, 2000.0, 0.0), "must be above 0"),
            "an unknown metric": (p0geom.distance, ((0, 0, 0), (1, 1, 1), "xy"), "is not 3d or xz"),
            "an unknown shape": (p0geom.generate, ("ring", 1000.0, 4, 4, random.Random(0)), "is not furball"),
            "a zero size": (p0geom.generate, ("furball", 0.0, 4, 4, random.Random(0)), "is not above 0"),
            "an empty summary": (p0geom.summary, ([],), "no values"),
            "a radius without a shape": (p0geom.grid, (None, None, 1000.0), "needs --shape"),
            "a grid with a bad fight": (p0geom.grid, ("64-64",), "is not <players>"),
            "a grid with a bad shape": (p0geom.grid, (None, "ring"), "is not furball"),
        }
        for name, (function, args, message) in cases.items():
            with self.subTest(name), self.assertRaisesRegex(ValueError, message):
                function(*args)

    def test_the_cli_refuses_with_exit_2(self):
        for name, argv in {"no seeds": ["--seeds", "0"], "a radius alone": ["--radius", "500"]}.items():
            with self.subTest(name), contextlib.redirect_stderr(io.StringIO()) as err:
                self.assertEqual(p0geom.main(argv + ["--out", "unused"]), 2)
                self.assertIn("p0geom: ", err.getvalue())

    def test_parse_fight(self):
        self.assertEqual(p0geom.parse_fight("64+64"), (64, 64))
        self.assertEqual(p0geom.parse_fight("400+100"), (400, 100))
        self.assertEqual(p0geom.parse_fight("10+0"), (10, 0))


class RangeTests(unittest.TestCase):
    def test_visible_range(self):
        cases = {
            "A0 2 km, both at full": ((FULL, FULL, 2000.0), 2313.3),
            "A0 2 km, both at boost": ((BOOST, BOOST, 2000.0), 2626.7),
            "A0 3 km, both at full": ((FULL, FULL, 3000.0), 3313.3),
            "A0 3 km, both at boost": ((BOOST, BOOST, 3000.0), 3626.7),
            "at rest beside a boost": ((0.0, BOOST, 3000.0), 3313.3),
            "the roam cap": ((BOOST, BOOST, 3000.0, 3500.0), 3500.0),
            "the default cap": ((BOOST, BOOST, 29900.0), 30000.0),
            "below the cap": ((0.0, 0.0, 2000.0, 2000.0), 2000.0),
        }
        for name, (args, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(round(p0geom.visible_range(*args), 1), expected)

    def test_the_cap_applies_to_counts(self):
        ships = [player(0, speed=BOOST), player(2500, speed=BOOST)]   # A = 2,626.7 m uncapped
        self.assertEqual(p0geom.client_counts(ships, 2000.0)[0]["v_3d"], 1)
        self.assertEqual(p0geom.client_counts(ships, 2000.0, cap=2400.0)[0]["v_3d"], 0)


class CountTests(unittest.TestCase):
    def test_boundaries_are_inclusive(self):
        ships = [player(0), player(1000), player(0, z=80), player(0, z=-80.001), enemy(-1000.001)]
        row = p0geom.client_counts(ships, 2000.0)[0]
        self.assertEqual((row["near_3d"], row["near_xz"]), (3 / 4, 3 / 4))
        self.assertEqual(row["pilots_xz"], 1)

    def test_an_xz_near_y_far_pair(self):
        ships = [player(0), player(1000, y=20000)]
        row = p0geom.client_counts(ships, 2000.0)[0]
        self.assertEqual((row["v_3d"], row["v_xz"]), (0, 1))
        self.assertEqual((row["near_3d"], row["near_xz"]), (0.0, 1.0))
        self.assertEqual((row["carry_3d"], row["carry_xz"]), (0, 1))
        self.assertEqual(row["pilots_xz"], 0)

    def test_the_range_belongs_to_the_target(self):
        # f = 250.67: the boosting target reaches 2000 + 501.34 x 1.25 = 2626.7 m, the patrolling one 2376.0 m.
        ships = [player(0, speed=PATROL), player(2600, speed=BOOST), player(-2600, speed=PATROL)]
        rows = p0geom.client_counts(ships, 2000.0)
        self.assertEqual([row["v_3d"] for row in rows], [1, 0, 0])

    def test_v_counts_ai_ships_and_carry_only_players(self):
        ships = [player(0), enemy(500), enemy(-500), player(0, z=600)]
        row = p0geom.client_counts(ships, 2000.0)[0]
        self.assertEqual((row["v_3d"], row["carry_3d"], row["carry_xz"]), (3, 1, 1))
        self.assertEqual(len(p0geom.client_counts(ships, 2000.0)), 2)   # AI ships are not clients

    def test_a_lone_client(self):
        row = p0geom.client_counts([player(0)], 2000.0)[0]
        self.assertEqual((row["v_3d"], row["near_3d"], row["lock"]), (0, 0.0, 0))


class LockTests(unittest.TestCase):
    def test_locks(self):
        def at(angle_deg, d):
            return enemy(d * math.cos(math.radians(angle_deg)), z=d * math.sin(math.radians(angle_deg)))
        cases = {
            "outside the cone": ([player(0), at(30, 500)], None),
            "beyond 3 km": ([player(0), enemy(3000.001)], None),
            "another player ahead": ([player(0), player(500)], None),
            "an AI ship never locks": ([enemy(0, heading=(1.0, 0.0, 0.0)), player(500)], None),
            "ahead": ([player(0), enemy(500)], 1),
            "at exactly 3 km": ([player(0), enemy(3000)], 1),
            "on the cone's edge": ([player(0), at(25, 1000)], 1),
            "behind but inside 100 m": ([player(0), enemy(-50)], 1),
            "behind at exactly 100 m": ([player(0), enemy(-100)], 1),
            "the nearest of two": ([player(0), enemy(900), enemy(400), enemy(-2000)], 2),
            "the near one behind before a far one ahead": ([player(0), enemy(1000), enemy(-90)], 2),
        }
        for name, (ships, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0geom.lock_target(0, ships), expected)

    def test_a_lock_beyond_and_inside_the_range(self):
        # The target patrols at 37.6 m/s and f is 125.33: A = A0 + 162.93 x 1.25 = A0 + 203.7 m.
        ships = [player(0), enemy(2800)]
        two, three = p0geom.client_counts(ships, 2000.0)[0], p0geom.client_counts(ships, 3000.0)[0]
        self.assertEqual((two["lock"], two["extra_3d"], two["extra_xz"], two["v_3d"]), (1, 1, 1, 0))
        self.assertEqual((three["lock"], three["extra_3d"], three["extra_xz"], three["v_3d"]), (1, 0, 0, 1))

    def test_a_lock_extra_only_in_3d(self):
        # 2,100 m X/Z and 1,500 m up: 2,581 m in 3D, beyond A = 2,203.7 m; on X/Z inside it.
        ships = [player(0, heading=(2100.0, 1500.0, 0.0)), enemy(2100, y=1500)]
        row = p0geom.client_counts(ships, 2000.0)[0]
        self.assertEqual((row["lock"], row["extra_3d"], row["extra_xz"]), (1, 1, 0))


class SummaryTests(unittest.TestCase):
    def test_summary(self):
        cases = {
            "one value": ([4], {"mean": 4.0, "p95": 4, "max": 4}),
            "five values": ([0, 2, 3, 2, 3], {"mean": 2.0, "p95": 3, "max": 3}),
            "twenty values": (list(range(1, 21)), {"mean": 10.5, "p95": 19, "max": 20}),
        }
        for name, (values, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0geom.summary(values), expected)

    def test_mean_and_p95_over_five_clients(self):
        # A = 2,313.3 m for every ship: V is 2, 3, 3, 2 and 0.
        ships = [player(0), player(1000), player(2000), player(3000), player(10000)]
        values = [row["v_3d"] for row in p0geom.client_counts(ships, 2000.0)]
        self.assertEqual(values, [2, 3, 3, 2, 0])
        self.assertEqual(p0geom.summary(values), {"mean": 2.0, "p95": 3, "max": 3})


class ScenarioTests(unittest.TestCase):
    def test_shapes_place_within_their_bounds(self):
        furball = p0geom.generate("furball", 1000.0, 40, 10, random.Random(1))
        self.assertTrue(all(p0geom.distance(s.pos, (0, 0, 0)) <= 1000.0 for s in furball))
        lines = p0geom.generate("lines", 3000.0, 40, 10, random.Random(1))
        for s in lines:
            centre = (-1500.0 if s.player else 1500.0, 0.0, 0.0)
            self.assertLessEqual(p0geom.distance(s.pos, centre), 500.0)
            self.assertEqual(s.heading[0] > 0, s.player)
        patrol = p0geom.generate("patrol", 5000.0, 40, 10, random.Random(1))
        self.assertTrue(all(abs(s.pos[0]) <= 2500 and abs(s.pos[2]) <= 2500 and 0 <= s.pos[1] <= 1000
                            and s.heading[1] == 0.0 for s in patrol))

    def test_speeds_follow_the_classes(self):
        ships = p0geom.generate("furball", 1000.0, 400, 100, random.Random(3))
        self.assertEqual([s.player for s in ships], [True] * 400 + [False] * 100)
        for s in ships:
            if s.player:
                self.assertIn((s.speed_class, s.speed), {("full", FULL), ("boost", BOOST), ("patrol", PATROL)})
            elif s.speed_class == "attack":
                self.assertTrue(0.35 * 94.0 - 1e-9 <= s.speed <= 94.0)
            else:
                self.assertIn((s.speed_class, s.speed), {("patrol", AI_PATROL), ("full", 94.0)})
        self.assertEqual({s.speed_class for s in ships if s.player}, {"full", "boost", "patrol"})

    def test_seeded_determinism(self):
        first = p0geom.generate("patrol", 5000.0, 64, 64, random.Random(7))
        self.assertEqual(first, p0geom.generate("patrol", 5000.0, 64, 64, random.Random(7)))
        self.assertNotEqual(first, p0geom.generate("patrol", 5000.0, 64, 64, random.Random(8)))
        self.assertEqual(p0geom.run_point("64+64", "lines", 3000.0, seeds=(0, 1)),
                         p0geom.run_point("64+64", "lines", 3000.0, seeds=(0, 1)))

    def test_run_point_rows(self):
        rows = p0geom.run_point("64+64", "furball", 1000.0, seeds=(0, 1))
        self.assertEqual([row["a0_m"] for row in rows], [2000.0, 3000.0])
        for row in rows:
            self.assertEqual((row["label"], row["clients"], row["seeds"], row["geometry"]),
                             ("offline (no rig)", 128, [0, 1], "R 1000"))
            self.assertEqual(set(row["metrics"]), set(p0geom.METRICS))
        self.assertLessEqual(rows[0]["metrics"]["v_3d"]["mean"], rows[1]["metrics"]["v_3d"]["mean"])

    def test_the_default_grid(self):
        points = p0geom.grid()
        self.assertEqual(len(points), 14)
        self.assertIn(("400+100", "lines", 3000.0), points)
        self.assertEqual(p0geom.grid("64+64", "patrol", 2500), [("64+64", "patrol", 2500.0)])
        self.assertEqual(p0geom.grid(None, "lines"), [("64+64", "lines", 1000.0), ("64+64", "lines", 3000.0),
                                                      ("400+100", "lines", 1000.0), ("400+100", "lines", 3000.0)])

    def test_main_writes_markdown_and_jsonl(self):
        with tempfile.TemporaryDirectory() as out, contextlib.redirect_stdout(io.StringIO()) as printed:
            argv = ["--fight", "64+64", "--shape", "furball", "--radius", "500", "--seeds", "1", "--out", out]
            self.assertEqual(p0geom.main(argv), 0)
            names = sorted(os.listdir(out))
            self.assertEqual([name.rsplit(".", 1)[1] for name in names], ["jsonl", "md"])
            with open(os.path.join(out, names[0]), encoding="utf-8") as f:
                rows = [json.loads(line) for line in f]
            with open(os.path.join(out, names[1]), encoding="utf-8") as f:
                page = f.read()
        self.assertEqual([(row["schema"], row["a0_m"]) for row in rows], [("p0geom/1", 2000.0), ("p0geom/1", 3000.0)])
        self.assertIn("offline (no rig)", page)
        self.assertEqual(page.count("| 64+64 | furball | R 500 |"), 2)
        self.assertIn("written", printed.getvalue())


if __name__ == "__main__":
    unittest.main()
