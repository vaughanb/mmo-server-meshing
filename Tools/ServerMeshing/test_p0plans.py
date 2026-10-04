"""Unit tests for p0plans (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py")."""
import collections
import hashlib
import json
import unittest

import p0plans
import p0results
import regress
import rigrun

HALVES = rigrun.layout_override("halves")
COARSE = rigrun.layout_override("coarse")


def arm_points(specs):
    points = collections.defaultdict(collections.Counter)
    for spec in specs:
        points[(spec["case"], spec["arm"])][json.dumps(spec["point"], sort_keys=True)] += 1
    return points


def find(specs, case, arm, repeat=1, **point):
    return next(s for s in specs if (s["case"], s["arm"], s["repeat"]) == (case, arm, repeat)
                and all(s["point"].get(key) == value for key, value in point.items()))


def row(spec, work, sat=0.0, accepted=True):
    return {"row": "run", "plan_key": spec["key"], "accepted": accepted,
            "metrics": [{"name": "work_ms_per_s", "stat": "mean", "value": work, "cell": "C"},
                        {"name": "sat_pct", "stat": "mean", "value": sat, "cell": "C"}]}


class RefusalTests(unittest.TestCase):
    def test_refusals(self):
        cases = {
            "a canary arm": (lambda: p0plans.canary_plan("drift", 1), "arm baseline or check"),
            "no canary runs": (lambda: p0plans.canary_plan("check", 0), "at least 1 run"),
            "an accept part": (lambda: p0plans.accept_plan(("A", "F")), "parts A, B, C, D and E; got F"),
            "an abgeo sub-block": (lambda: p0plans.abgeo_invocations("x.jsonl", (4,)), r"sub-blocks 1, 2 and 3; got \[4\]"),
            "an argv without repeats": (lambda: p0plans.ab_run_count(["ab", "--players", "250", "--monsters", "all",
                                                                      "--layouts", "halves"]), "states no --repeats"),
            "bad rates": (lambda: p0plans.rates_plan(points=("30/x",)), "rates '30/x': MESH_RATES step part 'x'"),
            "no repeats": (lambda: p0plans.floor_plan(repeats=0), "at least 1 repeat"),
            "repeat 0": (lambda: p0plans.stepped([1, 2], 0), "repeats count from 1"),
            "a later family": (lambda: p0plans.later_refusal("presence"), "not a later-stage family"),
            "a baseline of three": (lambda: p0plans.baseline_extra([100.0, 101.0, 99.0]), "two positive work figures"),
        }
        for name, (call, message) in cases.items():
            with self.subTest(name):
                with self.assertRaisesRegex(ValueError, message):
                    call()

    def test_later_stages(self):
        for name, stage in p0plans.STAGE_LATER.items():
            with self.subTest(name):
                self.assertIn(stage, p0plans.later_refusal(name))
        self.assertEqual(p0plans.later_refusal("fanout"),
                         "p0load fanout needs stage 0b, steps 5-6; stage 0a builds no runs for it")
        self.assertEqual(p0plans.OBSERVED_REFUSAL, "B1-O needs phantom observers (stage 0b, step 5)")


class PlanTests(unittest.TestCase):
    def plans(self):
        return {"canary": p0plans.canary_plan("baseline", 2), "accept": p0plans.accept_plan(),
                "rates": p0plans.rates_plan(), "presence": p0plans.presence_plan(), "density": p0plans.density_plan(),
                "floor": p0plans.floor_plan()}

    def test_run_counts(self):
        counts = {name: len(specs) for name, specs in self.plans().items()}
        self.assertEqual(counts, {"canary": 2, "accept": 25, "rates": 48, "presence": 66, "density": 53, "floor": 12})
        self.assertEqual(len(p0plans.accept_plan("E")), 6)

    def test_every_spec_is_sendable_and_numbered(self):
        for name, specs in self.plans().items():
            with self.subTest(name):
                for spec in specs:
                    self.assertIsNone(rigrun.check_env(spec["env"]))
                    self.assertEqual(spec["key"], p0plans.plan_key(spec))
                    self.assertEqual(spec["game"], p0results.FAMILY_GAME[spec["family"]])
                self.assertEqual([s["order"] for s in specs], list(range(1, len(specs) + 1)))
                self.assertEqual(len({s["key"] for s in specs}), len(specs))

    def test_plan_key(self):
        spec = p0plans.canary_plan("check")[0]
        text = 'canary|canary|check|{"players": 200, "rates": "30/0.02"}|1'
        self.assertEqual(spec["key"], hashlib.sha1(text.encode()).hexdigest()[:12])
        self.assertEqual(p0plans.plan_key(dict(spec, seed=9, env={})), spec["key"])
        self.assertNotEqual(p0plans.plan_key(dict(spec, repeat=2)), spec["key"])

    def test_canary(self):
        specs = p0plans.canary_plan("baseline", 2)
        self.assertEqual(specs[0]["env"], dict(p0plans.INSTR, MESH_LAYOUT_OVERRIDE=COARSE, MESH_SPAWN_CELLS="C",
                                               MESH_LOAD_PLAN="C:200", MESH_LOAD_SEED="1", MESH_NO_MONSTERS="1",
                                               MESH_RATES="30/0.02", MESH_LOAD_MOTION="kinematic"))
        self.assertEqual([(s["arm"], s["repeat"], s["seed"], s["rates"]) for s in specs],
                         [("baseline", 1, 1, "30/0.02"), ("baseline", 2, 1, "30/0.02")])

    def test_accept_instrument_and_puppets(self):
        specs = p0plans.accept_plan("A,B")
        self.assertEqual(len(specs), 19)
        plain = specs[0]
        self.assertEqual((plain["arm"], plain["env"], plain["rates"], plain["layout"], plain["cells_started"]),
                         ("plain", {}, None, None, list(p0plans.TEN_CELLS)))
        self.assertEqual(find(specs, "instrument", "r60burn")["env"],
                         dict(p0plans.INSTR, MESH_RATES="60/0.03", MESH_PROBE_BURN_TICK_MS="C:2"))
        pairs = [(s["arm"][-1], s["repeat"]) for s in specs if s["arm"].startswith("overhead")]
        self.assertEqual([side for side, _ in pairs], list("ABBAABBAAB"))
        self.assertEqual(find(specs, "instrument", "overhead-A", 2)["env"], {"MESH_RATES": "60/0.03"})
        self.assertEqual({(s["warmup"], s["measure"]) for s in specs if s["case"] == "instrument"}, {(25, 40)})
        kwalk = find(specs, "puppets", "kin_kwalk")
        self.assertEqual((kwalk["cells_read"], kwalk["env"]["MESH_LOAD_AREA"], kwalk["env"]["MESH_LOAD_MOTION"],
                          kwalk["env"]["MESH_PUPPET_MOTION"], kwalk["measure"]), (["C", "E"], "80,0,30", "kinematic",
                                                                                   "kinematic", 60))

    def test_accept_first_use(self):
        specs = p0plans.accept_plan(["E"])
        self.assertEqual({s["arm"]: s["checks_family"] for s in specs},
                         {"r30s03": "rates", "r60s02": "rates", "uncap": "density", "nofight": "density",
                          "noanim": "presence", "band300": "presence"})
        uncap, band300 = find(specs, "first-use", "uncap"), find(specs, "first-use", "band300")
        self.assertEqual((uncap["uncapped"], uncap["env"]["MESH_UNCAPPED_CELLS"]), (["C"], "C"))
        self.assertEqual((band300["layout"], band300["band"], band300["env"]["MESH_BAND"], band300["expect"]),
                         ("halves", 300.0, "300", {"puppets": {"E": 60}}))
        self.assertEqual(find(specs, "first-use", "r30s03")["env"]["MESH_RATES"], "30/0.03")

    def test_accept_commands(self):
        commands = p0plans.accept_commands("C,D")
        self.assertEqual([label for _, label, _, _ in commands],
                         ["scenarios", "scenarios-dynamic", "dynidle", "kinematic", "regress-unit", "unittest", "ruff"])
        self.assertEqual(commands[3][2][-4:], ["wake", "dynidle", "--env", "MESH_PUPPET_MOTION=kinematic"])
        self.assertEqual(commands[-1], ("D", "ruff", ["-m", "ruff", "check", "Tools/ServerMeshing"], True))

    def test_rates(self):
        specs = p0plans.rates_plan()
        self.assertEqual({arm: len(points) for (_, arm), points in arm_points(specs).items()},
                         {"idle": 4, "steered": 4, "kinematic": 4, "inert": 4})
        first = [s["point"]["rates"] for s in specs[:4]]
        self.assertEqual(first, list(p0plans.RATE_POINTS))
        second = [s for s in specs if s["repeat"] == 2]
        self.assertEqual([s["point"]["rates"] for s in second[:4]], list(reversed(p0plans.RATE_POINTS)))
        self.assertNotIn("MESH_LOAD_PLAN", find(specs, "K", "idle", rates="60/0.02")["env"])
        self.assertEqual(find(specs, "K", "inert", rates="30/0.03")["env"]["MESH_LOAD_MOTION"], "inert")

    def test_presence_arms(self):
        specs = p0plans.presence_plan()
        expected = {("baseline", "idle"): [0], ("main", "puppets-steered"): [100, 200, 250],
                    ("main", "puppets-kinematic"): [100, 200, 400], ("main", "owners-inert"): [100, 200, 400],
                    ("additivity", "puppets-steered"): [100, 200], ("additivity", "puppets-kinematic"): [200, 400],
                    ("solo", "owners-steered"): [100, 200, 250], ("solo", "owners-kinematic"): [100, 200, 400],
                    ("animation-off", "puppets-steered"): [200], ("animation-off", "puppets-kinematic"): [200]}
        got = {arm: sorted(json.loads(p)["players"] for p in points) for arm, points in arm_points(specs).items()}
        self.assertEqual(got, expected)
        self.assertEqual({n for points in arm_points(specs).values() for n in points.values()}, {3})
        keys = ("MESH_SPAWN_CELLS", "MESH_LOAD_PLAN", "MESH_LOAD_MOTION", "MESH_PUPPET_MOTION", "MESH_RIG_SERVER_ANIMATION")
        cases = {
            ("baseline", "idle", 0): ({"MESH_SPAWN_CELLS": "C,E"}, {}),
            ("main", "puppets-steered", 250): ({"MESH_SPAWN_CELLS": "C,E", "MESH_LOAD_PLAN": "C:250",
                                                "MESH_LOAD_MOTION": "kinematic"}, {"puppets": {"E": 250}}),
            ("main", "owners-inert", 400): ({"MESH_SPAWN_CELLS": "C,E", "MESH_LOAD_PLAN": "C:400",
                                             "MESH_LOAD_MOTION": "inert", "MESH_PUPPET_MOTION": "kinematic"},
                                            {"puppets": {"E": 400}}),
            ("additivity", "puppets-kinematic", 400): ({"MESH_SPAWN_CELLS": "C,E", "MESH_LOAD_PLAN": "C:400,E:400",
                                                        "MESH_LOAD_MOTION": "kinematic",
                                                        "MESH_PUPPET_MOTION": "kinematic"},
                                                       {"puppets": {"C": 400, "E": 400}}),
            ("solo", "owners-steered", 250): ({"MESH_SPAWN_CELLS": "C", "MESH_LOAD_PLAN": "C:250"}, {}),
            ("animation-off", "puppets-steered", 200): ({"MESH_SPAWN_CELLS": "C,E", "MESH_LOAD_PLAN": "C:200",
                                                         "MESH_LOAD_MOTION": "kinematic",
                                                         "MESH_RIG_SERVER_ANIMATION": "0"}, {"puppets": {"E": 200}}),
        }
        for (case, arm, n), (env, expect) in cases.items():
            with self.subTest(f"{case} {arm} {n}"):
                spec = find(specs, case, arm, players=n)
                self.assertEqual({key: spec["env"][key] for key in keys if key in spec["env"]}, env)
                self.assertEqual((spec["expect"], spec["band"], spec["env"]["MESH_BAND"], spec["env"]["MESH_RATES"],
                                  spec["env"]["MESH_NO_MONSTERS"], spec["layout"]),
                                 (expect, 300.0, "300", "60/0.03", "1", "halves"))
        self.assertEqual(find(specs, "additivity", "puppets-steered", players=100)["point"],
                         {"players": 100, "players_E": 100, "rates": "60/0.03"})

    def test_density_arms(self):
        specs = p0plans.density_plan()
        points = {arm: {tuple(json.loads(p)[k] for k in ("players", "side")): n for p, n in pts.items()}
                  for arm, pts in arm_points(specs).items()}
        grid = {(n, side): 3 for n in (100, 200) for side in (100, 200, 400, 600)}
        self.assertEqual(points, {("grid", "base"): grid, ("scale", "base"): {(50, 70.71): 3, (200, 141.42): 3},
                                  **{("attribution", arm): {(200, 141.42): 3}
                                     for arm in ("inert", "no-fight", "kinematic", "monsters-on")},
                                  **{("sparse", arm): {(200, 600): 3} for arm in ("inert", "no-fight", "kinematic")},
                                  ("bridge-0.53", "uncapped"): {(500, 200): 2}})
        self.assertEqual(find(specs, "grid", "base", players=100, side=100)["env"]["MESH_LOAD_AREA"], "0,0,50")
        self.assertNotIn("MESH_LOAD_AREA", find(specs, "grid", "base", players=200, side=600)["env"])
        self.assertEqual(find(specs, "scale", "base", players=50)["env"]["MESH_LOAD_AREA"], "0,0,35.355")
        monsters = find(specs, "attribution", "monsters-on")["env"]
        self.assertEqual((monsters.get("MESH_MONSTER_AREAS"), "MESH_NO_MONSTERS" in monsters), ("all", False))
        self.assertEqual(find(specs, "sparse", "no-fight")["env"]["MESH_LOAD_FIGHT"], "0")
        bridge = find(specs, "bridge-0.53", "uncapped")
        self.assertEqual({k: v for k, v in bridge["env"].items() if k not in p0plans.INSTR},
                         {"MESH_SPAWN_CELLS": ",".join(p0plans.MAP_ONE), "MESH_LOAD_PLAN": "C:500",
                          "MESH_LOAD_SEED": "1", "MESH_RATES": "60/0.03", "MESH_UNCAPPED_CELLS": "C"})
        self.assertEqual((bridge["uncapped"], bridge["cells_read"], bridge["layout"], bridge["expect"]),
                         (["C"], ["C"], "grid", {"links": 8}))
        grid_first = [(s["point"]["players"], s["point"]["side"]) for s in specs if s["case"] == "grid"][:8]
        self.assertEqual(grid_first, sorted(grid, key=lambda p: (p[0], -p[1])))

    def test_floor(self):
        specs = p0plans.floor_plan()
        linked = find(specs, "linked", "idle", rates="60/0.03")
        self.assertEqual((linked["expect"], linked["cells_started"], linked["cells_read"], linked["layout"]),
                         ({"links": 8}, list(p0plans.MAP_ONE), ["C"], "grid"))
        self.assertNotIn("MESH_LAYOUT_OVERRIDE", linked["env"])
        self.assertEqual(find(specs, "cell", "players-150", rates="30/0.02")["env"]["MESH_LOAD_PLAN"], "C:150")
        self.assertEqual([s["arm"] for s in specs if s["repeat"] == 2][::2], ["players-150", "idle", "idle"])
        self.assertEqual([s["case"] for s in specs if s["repeat"] == 2][::2], ["cell", "linked", "cell"])

    def test_abgeo(self):
        invocations = p0plans.abgeo_invocations("x.jsonl")
        self.assertEqual([label for label, _ in invocations],
                         ["b9-main", "b9-bands", "b9-steered-b150", "b9-steered-b80"])
        self.assertEqual(invocations[0][1], ["ab", "--puppets", "kinematic", "--players", "150,250", "--monsters",
                                             "off,all", "--layouts", "coarse,halves,grid", "--repeats", "3",
                                             "--instrument", "--order", "repeat-major", "--rates", "60/0.03",
                                             "--jsonl", "x.jsonl", "--case", "b9-main"])
        self.assertEqual([p0plans.ab_run_count(argv) for _, argv in invocations], [36, 36, 6, 6])
        self.assertEqual(len(p0plans.abgeo_invocations("x.jsonl", (3,))), 2)

    def test_ten_cells_are_regress_zones(self):
        self.assertEqual(p0plans.TEN_CELLS, tuple(zone for zone, _ in regress.ZONES))


class OrderTests(unittest.TestCase):
    def test_stepped(self):
        self.assertEqual([p0plans.stepped([1, 2, 3], r) for r in (1, 2, 3)], [[1, 2, 3], [3, 2, 1], [1, 2, 3]])

    def test_block_order_covers_every_position(self):
        orders = p0plans.block_order("abc", 3)
        for arm in "abc":
            with self.subTest(arm):
                self.assertEqual(sorted(order.index(arm) for order in orders), [0, 1, 2])

    def test_block_order_moves_each_arm_to_another_third(self):
        orders = p0plans.block_order(list(range(9)), 3)
        for arm in range(9):
            with self.subTest(arm):
                self.assertEqual(sorted(order.index(arm) // 3 for order in orders), [0, 1, 2])

    def test_block_order_never_repeats_a_position(self):
        cases = {"block K: 4 arms, 3 repeats": (4, 3, [0, 1, 2]), "presence and density: 10 arms, 3 repeats": (10, 3,
                                                                                                           [0, 3, 6]),
                 "floor: 3 arms, 2 repeats": (3, 2, [0, 1])}
        for name, (arms, repeats, offsets) in cases.items():
            with self.subTest(name):
                orders = p0plans.block_order(list(range(arms)), repeats)
                self.assertEqual([(arms - order.index(0)) % arms for order in orders], offsets)
                for arm in range(arms):
                    self.assertEqual(len({order.index(arm) for order in orders}), repeats, arm)
        rates = p0plans.rates_plan()
        self.assertNotEqual([s["arm"] for s in rates if s["repeat"] == 1], [s["arm"] for s in rates if s["repeat"] == 3])


class ThirdRepeatTests(unittest.TestCase):
    def test_third_repeat(self):
        specs = p0plans.floor_plan()
        one, two = find(specs, "cell", "idle", 1, rates="60/0.03"), find(specs, "cell", "idle", 2, rates="60/0.03")
        thresholds = p0plans.THIRD_REPEAT_THRESHOLDS["floor"]
        cases = {
            "repeat 2 not accepted": ([row(one, 100.0), row(two, 150.0, accepted=False)], None),
            "9 % apart": ([row(one, 100.0), row(two, 109.0)], None),
            "both above 600": ([row(one, 601.0), row(two, 605.0)], None),
            "11 % apart": ([row(one, 100.0), row(two, 111.0)],
                           "repeats 1 and 2 differ by 11.0% on C work_ms_per_s (100.0, 111.0)"),
            "599 and 601 against 600": ([row(one, 599.0), row(two, 601.0)],
                                        "repeats 1 and 2 straddle work_ms_per_s 600 on C (599.0, 601.0)"),
            "sat 19 and 21": ([row(one, 100.0, 19.0), row(two, 101.0, 21.0)],
                              "repeats 1 and 2 straddle sat_pct 20 on C (19.0, 21.0)"),
        }
        for name, (rows, reason) in cases.items():
            with self.subTest(name):
                extra = p0plans.third_repeat(specs, rows, "work_ms_per_s", thresholds)
                self.assertEqual([s["third_reason"] for s in extra], [reason] if reason else [])

    def test_two_repeat_rates_get_third_repeats(self):
        specs = p0plans.rates_plan(2)
        one, two = find(specs, "K", "steered", 1, rates="60/0.03"), find(specs, "K", "steered", 2, rates="60/0.03")
        extra = p0plans.third_repeat(specs, [row(one, 400.0), row(two, 460.0)], "work_ms_per_s",
                                     p0plans.THIRD_REPEAT_THRESHOLDS["rates"])
        self.assertEqual([s["third_reason"] for s in extra],
                         ["repeats 1 and 2 differ by 15.0% on C work_ms_per_s (400.0, 460.0)"])
        self.assertEqual(p0plans.THIRD_REPEAT_THRESHOLDS["rates"], p0plans.SATURATION)
        self.assertEqual(p0plans.third_repeat(p0plans.rates_plan(3), [row(one, 400.0), row(two, 460.0)],
                                              "work_ms_per_s"), [])

    def test_the_third_spec(self):
        specs = p0plans.floor_plan()
        one, two = find(specs, "cell", "players-150", 1, rates="30/0.02"), find(specs, "cell", "players-150", 2,
                                                                                 rates="30/0.02")
        third, = p0plans.third_repeat(specs, [row(one, 100.0), row(two, 120.0)], "work_ms_per_s")
        self.assertEqual((third["repeat"], third["seed"], third["order"], third["env"]["MESH_LOAD_SEED"],
                          third["replaces_repeat"]), (3, 3, 13, "3", None))
        self.assertEqual(third["key"], p0plans.plan_key(dict(two, repeat=3)))
        self.assertEqual(two["env"]["MESH_LOAD_SEED"], "2")
        cases = {"third near repeat 2": (118.0, 1), "third near repeat 1": (101.0, 2), "third midway": (110.0, 1)}
        for name, (work, replaced) in cases.items():
            with self.subTest(name):
                rows = [row(one, 100.0), row(two, 120.0), row(third, work)]
                self.assertEqual(p0plans.replaced_repeat(third, rows, "work_ms_per_s"), replaced)
        with self.assertRaisesRegex(ValueError, "repeat 3 .* has no accepted run row"):
            p0plans.replaced_repeat(third, [row(one, 100.0), row(two, 120.0)], "work_ms_per_s")

    def test_baseline_extra(self):
        self.assertEqual([p0plans.baseline_extra(works) for works in ([100.0, 102.5], [100.0, 102.6], [102.6, 100.0])],
                         [False, True, True])


if __name__ == "__main__":
    unittest.main()
