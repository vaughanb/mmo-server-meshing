"""Unit tests for p0plans0b and p0load's stage 0b commands (python -m unittest discover -s Tools/ServerMeshing -p
"test_*.py")."""
import argparse
import collections
import contextlib
import functools
import io
import json
import unittest
from unittest import mock

import p0load
import p0plans
import p0plans0b
import p0ships
import p0tools
import regress
import rigrun

# Step 8a's three keys join regress.ENV_KEYS (and leave p0tools.PENDING_KEYS) in another change of the same step. Until
# it lands, new_keys() patches them in so the plans build; the unpatched check_env test is skipped until then.
NEW_KEYS = ("MESH_PREWARM", "MESH_SHIP_PARK_SLEEP", "MESH_MONSTER_PLACE")
LANDED = all(key in regress.ENV_KEYS and key not in p0tools.PENDING_KEYS for key in NEW_KEYS)
COUNTS = {"observed O": 12, "observed R": 6, "observed SA": 6, "observed M": 18, "observed S": 42, "fanout": 66,
          "targets": 36, "syncrpc": 12, "packets": 24, "prewarm": 45}
SPEC_0B_FIELDS = ("game", "kind", "payload_B", "scale", "stepped", "expected_v", "eyes", "placed", "N")


@contextlib.contextmanager
def new_keys():
    """regress.ENV_KEYS and p0tools.PENDING_KEYS as they are once the step 8a keys land (unchanged when they have)."""
    if LANDED:
        yield
        return
    missing = [key for key in NEW_KEYS if key not in regress.ENV_KEYS]
    with mock.patch.object(regress, "ENV_KEYS", regress.ENV_KEYS + missing), mock.patch.dict(p0tools.PENDING_KEYS):
        for key in NEW_KEYS:
            p0tools.PENDING_KEYS.pop(key, None)
        yield


def build(name, **kwargs):
    family, _, part = name.partition(" ")
    with new_keys():
        if family == "observed":
            return p0plans0b.observed_plan(part, **kwargs)
        return getattr(p0plans0b, f"{family}_plan")(**kwargs)


@functools.lru_cache(maxsize=None)
def plans():
    return {name: build(name) for name in COUNTS}


def find(specs, case, arm, repeat=1, **point):
    return next(s for s in specs if (s["case"], s["arm"], s["repeat"]) == (case, arm, repeat)
                and all(s["point"].get(key) == value for key, value in point.items()))


def arm_points(specs):
    points = collections.defaultdict(collections.Counter)
    for spec in specs:
        points[(spec["case"], spec["arm"])][json.dumps(spec["point"], sort_keys=True)] += 1
    return points


def p0load_main(argv):
    out = io.StringIO()
    with contextlib.redirect_stdout(out), new_keys():
        code = p0load.main(argv)
    return code, out.getvalue()


class RefusalTests(unittest.TestCase):
    def test_refusals(self):
        def spec(env, steps=None):
            return p0plans0b._spec0b("fanout", "x", "y", {}, 1, "fantasy", "character", env, "30/0.02", steps=steps,
                                     hold=10)
        eyes100 = {"MESH_PHANTOM": "virtual:100:eye:C", "MESH_PHANTOM_VIEW": "0"}
        cases = {
            "an observed part": (lambda: p0plans0b.observed_plan("X"), "observed has parts O, R, SA, M, S; got 'X'"),
            "no steps": (lambda: p0plans0b.stepped_measure((), 90), r"steps of at least 1 and a hold above 0; got \[\]"),
            "a step of 0": (lambda: p0plans0b.stepped_measure((0, 50), 90), "steps of at least 1"),
            "a zero hold": (lambda: p0plans0b.stepped_measure((50,), 0), "a hold above 0"),
            "a key set twice": (lambda: p0plans0b._merged({"A": "1"}, {"A": "2"}), "A set twice in one spec"),
            "a bad MESH_PHANTOM": (lambda: spec({"MESH_PHANTOM": "virtual:0:eye:C"}), "count '0' is not an integer"),
            "a view beside ship phantoms": (lambda: spec({"MESH_PHANTOM": "virtual:4:ship:C", "MESH_PHANTOM_VIEW": "0"}),
                                            "MESH_PHANTOM_VIEW=0 is refused with the ship kind"),
            "steps on characters": (lambda: spec({"MESH_PHANTOM": "virtual:400:character:C", "MESH_PHANTOM_VIEW": "50"},
                                                 (50,)), r"MESH_PHANTOM_STEPS \[50\] needs eyes binding at least 50"),
            "steps above P": (lambda: spec(eyes100, (50, 200)), "needs eyes binding at least 200"),
            "steps with no phantoms": (lambda: spec({}, (50,)), "MESH_PHANTOM is None"),
            "a prewarm kind against its motion": (lambda: self.unmatched_prewarm(),
                                                  "a kinematic prewarm needs MESH_PUPPET_MOTION kinematic; the arm "
                                                  "sets steered"),
            "a command with no plan": (lambda: p0load.block_plan(argparse.Namespace(command="nothing", warmup=None,
                                                                                    measure=None)),
                                       "p0load has no plan for 'nothing'"),
        }
        for name, (call, message) in cases.items():
            with self.subTest(name):
                with self.assertRaisesRegex(ValueError, message):
                    with new_keys():
                        call()

    @staticmethod
    def unmatched_prewarm():
        with mock.patch.object(p0plans0b, "KINEMATIC_PUPPETS", {}):
            return p0plans0b.prewarm_plan()

    def test_later_stages_and_moved_observed(self):
        self.assertEqual(p0plans.STAGE_LATER, {})   # step 9 builds workerab, dbburst and spaces (p0plans9)
        for name in ("workerab", "dbburst", "spaces"):
            with self.subTest(name):
                code, out = p0load_main([name, "--dry-run"])
                self.assertEqual(code, 0)
                self.assertNotIn("refused", out)
        code, out = p0load_main(["presence", "--observed"])
        self.assertEqual((code, out), (2, f"refused: {p0plans.OBSERVED_REFUSAL}\n"))
        self.assertIn("use p0load observed", out)
        self.assertFalse(set(p0plans0b.FAMILIES) & set(p0plans.STAGE_LATER))
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            p0load.build_parser().parse_args(["observed"])

    def test_canaried(self):
        ours = p0load.p0blocks.CANARY_FAMILIES
        self.assertEqual(p0load.CANARY_FAMILIES[:len(ours)], tuple(ours))
        self.assertTrue(set(p0plans0b.FAMILIES) <= set(p0load.CANARY_FAMILIES))
        self.assertEqual(len(set(p0load.CANARY_FAMILIES)), len(p0load.CANARY_FAMILIES))


class PlanTests(unittest.TestCase):
    def test_run_counts(self):
        self.assertEqual({name: len(specs) for name, specs in plans().items()}, COUNTS)

    def test_p0load_plans(self):
        commands = {name: name.replace("observed ", "observed --part ").split() for name in COUNTS}
        commands["observed SA"] = ["observed", "--part", "sa"]
        for name, argv in commands.items():
            with self.subTest(name):
                with new_keys():
                    specs = p0load.block_plan(p0load.build_parser().parse_args(argv))
                self.assertEqual(len(specs), COUNTS[name])
        code, out = p0load_main(["fanout", "--dry-run"])
        self.assertEqual(code, 0)
        self.assertIn("66 runs, about", out)
        self.assertIn("    predicted (E) not modelled for phantoms, ships or monsters\n", out)
        with new_keys():
            keys = [spec["key"] for spec in p0load.block_plan(p0load.build_parser().parse_args(["fanout"]))][4:6]
        code, out = p0load_main(["fanout", "--dry-run", "--keys", ",".join(reversed(keys))])
        self.assertEqual(code, 0)
        self.assertIn("\n2 runs, about", out)
        code, out = p0load_main(["fanout", "--dry-run", "--keys", f"{keys[0]},zzz"])
        self.assertEqual((code, out), (2, "refused: --keys names zzz, not in this block's plan\n"))
        with mock.patch.object(p0load.p0blocks, "read_history", return_value=([], [])):
            code, out = p0load_main(["observed", "--part", "S", "--dry-run", "--resume"])
        self.assertEqual(code, 0)
        self.assertIn("\n42 runs, about", out)

    def test_every_spec_is_built_whole(self):
        for name, specs in plans().items():
            with self.subTest(name):
                for spec in specs:
                    self.assertEqual([field for field in SPEC_0B_FIELDS if field not in spec], [])
                    self.assertEqual(spec["key"], p0plans.plan_key(spec))
                    self.assertIn(spec["game"], ("fantasy", "space"))
                    self.assertIn(spec["kind"], ("character", "monster", "ship"))
                    self.assertEqual(spec["cells_read"], spec["cells_started"])
                    self.assertEqual((spec["env"]["MESH_METER_SENDS"], spec["env"]["MESH_LOAD_SEED"], spec["seed"]),
                                     ("count", str(spec["repeat"]), spec["repeat"]))
                    self.assertEqual(spec["eyes"], ":eye:" in spec["env"].get("MESH_PHANTOM", ""))
                    self.assertEqual(spec["scale"], float(spec["env"].get("MESH_SHIP_SCALE", 1)))
                    with new_keys():
                        self.assertIsNone(rigrun.check_env(spec["env"]))
                self.assertEqual([s["order"] for s in specs], list(range(1, len(specs) + 1)))
                self.assertEqual(len({s["key"] for s in specs}), len(specs))
                self.assertEqual({s["repeat"] for s in specs}, {1, 2, 3})

    @unittest.skipUnless(LANDED, "the step 8a keys are not in regress.ENV_KEYS yet (another change of step 8a)")
    def test_every_spec_passes_check_env_as_it_stands(self):
        for name, specs in plans().items():
            with self.subTest(name):
                for spec in specs:
                    self.assertIsNone(rigrun.check_env(spec["env"]))

    def test_stepped_measure(self):
        cases = {"fanout up": ((50, 100, 200, 400), 90, 388), "fanout down": ((400, 200, 100, 50), 90, 388),
                 "saturation": ((100, 200, 400, 800), 60, 276), "down and up": ((200, 100, 300), 10, 58),
                 "a part-second ramp": ((25,), 10, 31)}
        for name, (steps, hold, measure) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0plans0b.stepped_measure(steps, hold), measure)

    def test_stepped_specs(self):
        stepped = [s for name in ("fanout", "packets") for s in plans()[name] if s["stepped"]]
        self.assertEqual(len(stepped), 66 + 3)
        for spec in stepped:
            steps, hold = spec["stepped"]["steps"], spec["stepped"]["hold"]
            ascending = sorted(steps)
            with self.subTest(spec["key"]):
                self.assertEqual(steps, ascending if spec["repeat"] % 2 else ascending[::-1])
                self.assertEqual(spec["env"]["MESH_PHANTOM_STEPS"], f"{','.join(map(str, steps))}@{hold}")
                self.assertEqual((spec["warmup"], spec["measure"]),
                                 (10, len(steps) * hold + max(steps) // 50 + 20))
        self.assertEqual({(tuple(s["stepped"]["steps"]), s["stepped"]["hold"], s["measure"]) for s in stepped
                          if s["repeat"] == 2},
                         {((400, 200, 100, 50), 90, 388), ((800, 400, 200, 100), 60, 276)})


class ObservedTests(unittest.TestCase):
    def test_o(self):
        specs = plans()["observed O"]
        eyes = {arm: sorted(json.loads(p)["eyes"] for p in pts) for (_, arm), pts in arm_points(specs).items()}
        self.assertEqual(eyes, {"puppets-steered": [50, 200], "puppets-kinematic": [50, 200]})
        spec = find(specs, "O", "puppets-kinematic", eyes=200)
        env = spec["env"]
        self.assertEqual((env["MESH_PHANTOM"], env["MESH_PHANTOM_VIEW"], env["MESH_LOAD_PLAN"], env["MESH_LOAD_MOTION"],
                          env["MESH_PUPPET_MOTION"], env["MESH_BAND"], env["MESH_RATES"], spec["layout"],
                          spec["cells_read"], spec["expect"]),
                         ("virtual:200:eye:E", "0", "C:200", "kinematic", "kinematic", "300", "60/0.03", "halves",
                          ["C", "E"], {"puppets": {"E": 200}}))
        self.assertNotIn("MESH_PUPPET_MOTION", find(specs, "O", "puppets-steered", eyes=50)["env"])
        self.assertEqual((spec["game"], spec["kind"], spec["eyes"], spec["expected_v"]), ("fantasy", "character", True,
                                                                                         None))

    def test_r_and_sa(self):
        for part, arm, payload in (("R", "client-driven", None), ("SA", "server-authoritative", "input")):
            specs = plans()[f"observed {part}"]
            with self.subTest(part):
                spec = find(specs, part, arm, owners=400)
                self.assertEqual((spec["env"]["MESH_PHANTOM"], spec["env"]["MESH_PHANTOM_VIEW"],
                                  spec["env"].get("MESH_PHANTOM_PAYLOAD"), spec["layout"], spec["eyes"]),
                                 ("virtual:400:character:C", "50", payload, "coarse", False))
                self.assertEqual(sorted(s["point"]["owners"] for s in specs if s["repeat"] == 1), [200, 400])

    def test_m(self):
        specs = plans()["observed M"]
        cells = {"owned": "C", "puppets-steered": "C,E", "puppets-kinematic": "C,E"}
        for arm, eye_cells in cells.items():
            for rates in ("60/0.03", "30/0.02"):
                with self.subTest(f"{arm} {rates}"):
                    spec = find(specs, "M", arm, rates=rates)
                    env = spec["env"]
                    self.assertEqual((env["MESH_MONSTER_PLACE"], env["MESH_NO_MONSTERS"], env["MESH_NO_MONSTER_HANDOFF"],
                                      env["MESH_PHANTOM"], env["MESH_PHANTOM_VIEW"], env["MESH_RATES"],
                                      env["MESH_BAND"], env.get("MESH_PUPPET_MOTION")),
                                     ("200@-60,0,50", "1", "1", f"virtual:50:eye:{eye_cells}", "0", rates, "300",
                                      "kinematic" if arm == "puppets-kinematic" else None))
                    self.assertEqual((spec["placed"], spec["kind"], spec["layout"]), (200, "monster", "halves"))
        self.assertNotIn("MESH_LOAD_PLAN", specs[0]["env"])

    def test_s(self):
        specs = plans()["observed S"]
        points = {arm: sorted(json.loads(p).get("ships", json.loads(p).get("puppets")) for p in pts)
                  for (_, arm), pts in arm_points(specs).items()}
        self.assertEqual(points, {"ai": [100, 200, 400], "mirror": [100, 200, 400, 800], "feeder-ship": [100, 200, 400],
                                  "feeder-character": [200], "dense-ai": [100], "parked": [200, 200]})
        cases = {("ai", 400): ({"MESH_LOAD_SHIPS": "C:400:ai"}, 0.03, "ship"),
                 ("mirror", 800): ({"MESH_LOAD_SHIPS": "C:800:mirror"}, 0.03, "ship"),
                 ("feeder-ship", 100): ({"MESH_PUPPET_FEEDER": "ship:100@20"}, 0.03, "ship"),
                 ("feeder-character", 200): ({"MESH_PUPPET_FEEDER": "character:200@20"}, 0.03, "character"),
                 ("dense-ai", 100): ({"MESH_LOAD_SHIPS": "C:100:ai"}, 1.0, "ship")}
        for (arm, n), (keys, scale, kind) in cases.items():
            with self.subTest(arm):
                spec = next(s for s in specs if s["arm"] == arm and n in (s["point"].get("ships"),
                                                                          s["point"].get("puppets")))
                self.assertEqual({key: spec["env"][key] for key in keys}, keys)
                self.assertEqual({key: spec["env"][key] for key in ("MESH_SHIP_SCALE", "MESH_SENDER_R0")},
                                 p0ships.scaled_env(scale))
                self.assertEqual((spec["game"], spec["kind"], spec["scale"], spec["env"]["MESH_RATES"]),
                                 ("space", kind, scale, "30/0.02"))
        for sleep in (0, 1):
            spec = find(specs, "S", "parked", park_sleep=sleep)
            self.assertEqual((spec["env"]["MESH_LOAD_SHIPS"], spec["env"]["MESH_SHIP_PARK_SLEEP"]),
                             ("C:200:parked", str(sleep)))


class FamilyTests(unittest.TestCase):
    def test_fanout(self):
        specs = plans()["fanout"]
        got = {key: sorted(json.loads(p)["V"] for p in pts) for key, pts in arm_points(specs).items()}
        full, tiered = [100, 300, 500], [300, 500]
        self.assertEqual(got, {("main", "char-kit"): full, ("main", "char-sender-parity"): full,
                               ("main", "char-sender-tiered"): tiered, ("main", "ship-kit-56"): full,
                               ("main", "ship-sender-56-parity"): full, ("main", "ship-sender-56-tiered"): tiered,
                               ("main", "ship-sender-28-tiered"): tiered, ("classify", "ship-sender-56-parity"): [300],
                               ("60fps", "char-kit"): [300], ("60fps", "char-sender-parity"): [300],
                               ("60fps", "char-sender-tiered"): [300]})
        char = find(specs, "main", "char-sender-tiered", V=500)
        self.assertEqual({key: char["env"].get(key) for key in ("MESH_LOAD_PLAN", "MESH_LOAD_AREA", "MESH_RATES",
                                                                "MESH_SENDER", "MESH_SENDER_TIERS", "MESH_PHANTOM",
                                                                "MESH_PHANTOM_VIEW", "MESH_SHIP_SCALE")},
                         {"MESH_LOAD_PLAN": "C:500", "MESH_LOAD_AREA": "0,0,25", "MESH_RATES": "30/0.02",
                          "MESH_SENDER": "on", "MESH_SENDER_TIERS": "20/10/5/20", "MESH_PHANTOM": "virtual:400:eye:C",
                          "MESH_PHANTOM_VIEW": "0", "MESH_SHIP_SCALE": None})
        self.assertEqual((char["expected_v"], char["game"], char["kind"], char["payload_B"]), (500, "fantasy",
                                                                                            "character", 28))
        ship = find(specs, "main", "ship-sender-28-tiered", V=300)
        self.assertEqual({key: ship["env"].get(key) for key in ("MESH_LOAD_SHIPS", "MESH_LOAD_AREA", "MESH_SHIP_WIRE",
                                                                "MESH_SENDER_COMPACT", "MESH_SHIP_SCALE",
                                                                "MESH_LOAD_PLAN")},
                         {"MESH_LOAD_SHIPS": "C:300:mirror", "MESH_LOAD_AREA": "0,0,15", "MESH_SHIP_WIRE": "compact28",
                          "MESH_SENDER_COMPACT": "far", "MESH_SHIP_SCALE": "0.03", "MESH_LOAD_PLAN": None})
        self.assertEqual((ship["expected_v"], ship["game"], ship["kind"], ship["payload_B"], ship["scale"]),
                         (300, "space", "ship", 28, 0.03))
        self.assertEqual(find(specs, "main", "ship-kit-56", V=100)["payload_B"], 56)
        classify = find(specs, "classify", "ship-sender-56-parity")
        self.assertEqual((classify["env"]["MESH_SENDER_CLASSIFY_BUILDS"], classify["env"]["MESH_SENDER_TIERS"]),
                         ("10", "20/20/20"))
        self.assertEqual(find(specs, "60fps", "char-kit")["env"]["MESH_RATES"], "60/0.03")

    def test_targets_and_syncrpc(self):
        specs = plans()["targets"]
        self.assertEqual(sorted(arm_points(specs)), [("F1", "kit"), ("F1", "tiered"), ("F2", "kit"), ("F2", "tiered"),
                                                     ("S-T", "kit"), ("S-T", "tiered"), ("S-stress", "kit"),
                                                     ("S-stress", "tiered")])
        cases = {("S-T", "30/0.02"): ({"MESH_PHANTOM": "virtual:64:ship:C", "MESH_LOAD_SHIPS": "C:64:ai",
                                       "MESH_SHIP_SCALE": "0.5", "MESH_SENDER_R0": "1000.0"}, 128, "space", 0),
                 ("S-stress", "30/0.02"): ({"MESH_PHANTOM": "virtual:400:ship:C", "MESH_LOAD_SHIPS": "C:100:ai"}, 500,
                                           "space", 0),
                 ("F1", "60/0.03"): ({"MESH_PHANTOM": "virtual:150:character:C", "MESH_PHANTOM_AREA": "0,0,100,100",
                                      "MESH_PHANTOM_STAND": "0.7", "MESH_MONSTER_PLACE": "30@0,0,50:passive",
                                      "MESH_NO_MONSTER_HANDOFF": "1", "MESH_NO_MONSTERS": "1"}, 150, "fantasy", 30),
                 ("F2", "30/0.02"): ({"MESH_PHANTOM": "virtual:100:character:C", "MESH_PHANTOM_AREA": "disc:0,0,50",
                                      "MESH_PHANTOM_FIGHT": "1", "MESH_MONSTER_PLACE": "100@0,0,50",
                                      "MESH_NO_MONSTER_HANDOFF": "1"}, 100, "fantasy", 100)}
        for (target, rates), (keys, v, game, placed) in cases.items():
            with self.subTest(target):
                spec = find(specs, target, "tiered", target=target, rates=rates)
                self.assertEqual({key: spec["env"][key] for key in keys}, keys)
                self.assertEqual((spec["expected_v"], spec["game"], spec["placed"], spec["env"]["MESH_RATES"],
                                  spec["env"]["MESH_SENDER_TIERS"]), (v, game, placed, rates, "20/10/5/20"))
                self.assertNotIn("MESH_PHANTOM_VIEW", spec["env"])
        self.assertNotIn("MESH_SENDER", find(specs, "S-T", "kit")["env"])
        sync = plans()["syncrpc"]
        self.assertEqual(sorted(s["env"]["MESH_SYNC_LOAD"] for s in sync if s["repeat"] == 1),
                         ["dirty:0,rpc:0", "dirty:0,rpc:2100", "dirty:250,rpc:0", "dirty:250,rpc:2100"])
        self.assertEqual({(s["env"]["MESH_PHANTOM"], s["env"]["MESH_SHIP_SCALE"], s["expected_v"], s["game"])
                          for s in sync}, {("virtual:64:ship:C", "0.5", 128, "space")})

    def test_packets(self):
        specs = plans()["packets"]
        kit = find(specs, "kit", "kit-character")
        self.assertEqual((kit["env"]["MESH_PHANTOM"], kit["env"]["MESH_PHANTOM_VIEW"], kit["game"]),
                         ("virtual:400:character:C", "500", "fantasy"))
        caps = sorted((s["point"]["P"], s["point"]["packet_bytes"], s["env"]["MESH_LOAD_SHIPS"],
                       s["env"]["MESH_SENDER_PACKET_BYTES"]) for s in specs if s["case"] == "cap" and s["repeat"] == 1)
        self.assertEqual(caps, [(p, size, f"C:{500 - p}:ai", str(size))
                                for p in (100, 400) for size in (255, 510, 1020)])
        cap = find(specs, "cap", "ship-sender-28-tiered", P=400, packet_bytes=510)
        self.assertEqual((cap["env"]["MESH_PHANTOM"], cap["env"]["MESH_SHIP_WIRE"], cap["env"]["MESH_SENDER"],
                          cap["env"]["MESH_SHIP_SCALE"], cap["game"], cap["payload_B"]),
                         ("virtual:400:ship:C", "compact28", "on", "0.5", "space", 28))
        saturation = find(specs, "saturation", "kit-character-eyes")
        self.assertEqual((saturation["env"]["MESH_PHANTOM"], saturation["env"]["MESH_PHANTOM_VIEW"],
                          saturation["env"]["MESH_PHANTOM_STEPS"], saturation["env"]["MESH_THREAD_CPU"],
                          saturation["stepped"]), ("virtual:800:eye:C", "500", "100,200,400,800@60", "1",
                                                   {"steps": [100, 200, 400, 800], "hold": 60}))

    def test_prewarm(self):
        specs = plans()["prewarm"]
        cases = {("burst", "steered", 650): ("steered:650@55", None, "fantasy", "60/0.03", None),
                 ("burst", "kinematic", 100): ("kinematic:100@55", "kinematic", "fantasy", "60/0.03", None),
                 ("burst", "ship", 300): ("ship:300@55", None, "space", "30/0.02", None),
                 ("burst", "kinematic-paced", 650): ("kinematic:650@55/50", "kinematic", "fantasy", "60/0.03", None),
                 ("burst", "kinematic-eyes", 650): ("kinematic:650@55", "kinematic", "fantasy", "60/0.03", "0,0,25"),
                 ("range", "eyes", 160): ("range:160@55", None, "fantasy", "60/0.03", None),
                 ("group", "eyes", 300): ("group:300@55", None, "fantasy", "60/0.03", "0,0,25")}
        for (case, arm, n), (burst, motion, game, rates, area) in cases.items():
            with self.subTest(f"{case} {arm} {n}"):
                spec = find(specs, case, arm, N=n)
                env = spec["env"]
                self.assertEqual((env["MESH_PREWARM"], env.get("MESH_PUPPET_MOTION"), spec["game"], env["MESH_RATES"],
                                  env.get("MESH_LOAD_AREA"), spec["N"], spec["warmup"]),
                                 (burst, motion, game, rates, area, n, 45))
                watched = arm.endswith("eyes")
                self.assertEqual((env.get("MESH_PHANTOM"), env.get("MESH_LOAD_PLAN"), spec["eyes"]),
                                 ("virtual:100:eye:C", "C:100" if case == "group" else "C:200", True) if watched
                                 else (None, None, False))
        with new_keys():
            short = p0load.block_plan(p0load.build_parser().parse_args(["prewarm", "--warmup", "30"]))
        self.assertEqual({(s["warmup"], s["env"]["MESH_PREWARM"].split("@")[1].split("/")[0]) for s in short},
                         {(30, "40")})


if __name__ == "__main__":
    unittest.main()
