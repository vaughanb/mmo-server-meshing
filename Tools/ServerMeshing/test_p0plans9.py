"""Unit tests for p0plans9 and p0load's step 9 commands (python -m unittest discover -s Tools/ServerMeshing -p
"test_*.py"). Nothing here touches the editor, the database or machine B: dry runs only, and B's refusals are
patched."""
import contextlib
import io
import unittest
from unittest import mock

import p0cells0c
import p0dryrun
import p0load
import p0load8b
import p0load9
import p0plans
import p0plans8b
import p0plans9
import rigrun

COUNTS = {"dbburst": 21, "workerab": 14, "workerab --fallback": 14, "spaces": 18, "packets --loss": 9,
          "dbburst --parts burst": 9, "dbburst --parts sustained": 8, "dbburst --parts calibration": 4}


def p0load_main(argv, refusals=()):
    out = io.StringIO()
    with contextlib.redirect_stdout(out), mock.patch.object(p0load8b, "start_problems", return_value=list(refusals)):
        code = p0load.main(argv)
    return code, out.getvalue()


def plans():
    return {"dbburst": p0plans9.dbburst_plan(), "workerab": p0plans9.workerab_plan(),
            "workerab --fallback": p0plans9.workerab_plan(True), "spaces": p0plans9.spaces_plan(),
            "packets --loss": p0plans9.loss_plan(), "dbburst --parts burst": p0plans9.dbburst_plan("burst"),
            "dbburst --parts sustained": p0plans9.dbburst_plan("sustained"),
            "dbburst --parts calibration": p0plans9.dbburst_plan(["calibration"])}


class RefusalTests(unittest.TestCase):
    def test_refusals(self):
        cases = {
            "an unknown dbburst part": (lambda: p0plans9.dbburst_plan("burst,storm"), "got storm"),
            "no dbburst part": (lambda: p0plans9.dbburst_plan(" , "), "got none"),
            "loss with sink": (lambda: p0load9.block_plan_9(mock.Mock(command="packets", loss=True, sink=True)),
                               "two blocks"),
            "a command without a step 9 plan": (lambda: p0load9.block_plan_9(mock.Mock(command="floor")),
                                                "no step 9 plan"),
        }
        for name, (call, message) in cases.items():
            with self.subTest(name):
                with self.assertRaisesRegex(ValueError, message):
                    call()

    def test_command_line_refusals(self):
        cases = {"loss and sink together": (["packets", "--loss", "--sink", "--dry-run"], "two blocks"),
                 "an unknown part": (["dbburst", "--parts", "drain", "--dry-run"], "got drain")}
        for name, (argv, text) in cases.items():
            with self.subTest(name):
                code, out = p0load_main(argv)
                self.assertEqual(code, 2)
                self.assertIn(text, out)


class PlanTests(unittest.TestCase):
    def test_run_counts(self):
        for name, specs in plans().items():
            with self.subTest(name):
                self.assertEqual(len(specs), COUNTS[name])
                self.assertEqual([spec["order"] for spec in specs], list(range(1, len(specs) + 1)))
                self.assertEqual(len({spec["key"] for spec in specs}), len(specs))
                for spec in specs:
                    rigrun.check_env(spec["env"])
                    self.assertEqual(spec["key"], p0plans.plan_key(spec))

    def test_dbburst_runs(self):
        specs = p0plans9.dbburst_plan()
        bursts = [spec for spec in specs if "MESH_DB_BURST" in spec["env"]]
        self.assertEqual(len(bursts), 19)
        for spec in bursts:
            with self.subTest(key=spec["key"]):
                setting, error = p0cells0c.db_burst_setting(spec["env"]["MESH_DB_BURST"])
                self.assertIsNone(error)
                point = spec["point"]
                self.assertEqual(p0cells0c.expected_arrivals(setting), point.get("N") or point["R"] * 40)
                self.assertEqual(setting[2], spec["warmup"] + p0plans9.BURST_AFTER_S)
                self.assertEqual((spec["burst_rows"], spec["stop_after_drain"], spec["env"]["MESH_DB_SAVES_PROBE"]),
                                 (800, True, "1"))
                self.assertEqual(spec["measure"], 10 + (40 if point.get("R") else 0) + 120)
                self.assertEqual((spec["game"], spec["layout"], spec["plan"], spec["peer"]),
                                 ("neutral", "coarse", {}, None))
        self.assertEqual(sorted((s["point"]["N"], s["repeat"]) for s in specs if s["case"] == "burst"),
                         [(n, r) for n in (50, 100, 200) for r in (1, 2, 3)])
        self.assertEqual(sorted((s["point"]["R"], s["repeat"]) for s in specs if s["case"] == "sustained"),
                         [(r, k) for r in (5, 10, 15, 20) for k in (1, 2)])

    def test_dbburst_calibration_pairs(self):
        calibration = [spec for spec in p0plans9.dbburst_plan() if spec["case"] == "calibration"]
        self.assertEqual([(s["arm"], s["repeat"]) for s in calibration],
                         [("bots", 1), ("burst", 1), ("burst", 2), ("bots", 2)])
        for spec in calibration:
            with self.subTest(arm=spec["arm"], pair=spec["repeat"]):
                if spec["arm"] == "bots":
                    self.assertEqual(spec["peer"]["bots"]["stagger"], 0)
                    self.assertEqual(spec["peer"]["bots"]["n"], 25)
                    self.assertNotIn("MESH_DB_BURST", spec["env"])
                    self.assertNotIn("stop_after_drain", spec)
                else:
                    self.assertEqual(spec["env"]["MESH_DB_BURST"], "25@55")

    def test_workerab_arms_carry_the_same_load(self):
        for fallback, loads in ((False, (100, 200)), (True, (50, 100))):
            specs = p0plans9.workerab_plan(fallback)
            self.assertEqual([(s["case"], s["point"]["players"]) for s in specs[:2]], [("probe", load) for load in loads])
            for spec in specs:
                with self.subTest(fallback=fallback, key=spec["key"]):
                    env, cells = spec["env"], spec["cells_started"]
                    self.assertEqual(env["MESH_PHANTOM"], f"virtual:{spec['point']['players']}:character:C")
                    self.assertEqual(env["MESH_PHANTOM_AREA"], p0plans9.WORKER_AREA)   # inside C's half in both arms
                    self.assertFalse(spec.get("plan"))   # no load players: they wake monsters only as ghosts
                    self.assertEqual((env["MESH_FIGHT_GHOSTS"], env["MESH_FORWARD_TIMING"], env["MESH_MONSTER_AREAS"],
                                      env["MESH_PHANTOM_FIGHT"]), ("1", "1", "all", "0.5"))
                    self.assertNotIn("MESH_NO_MONSTERS", env)
                    halves = spec["arm"] == "halves"
                    self.assertEqual(env.get("MESH_MONSTER_CELL"), "E" if halves else None)
                    self.assertEqual((spec["layout"], cells), ("halves", ["C", "E"]) if halves else ("coarse", ["C"]))
            main = [s for s in specs if s["case"] == "ab"]
            self.assertEqual(sorted((s["arm"], s["point"]["players"], s["repeat"]) for s in main),
                             sorted((arm, load, r) for arm in ("coarse", "halves") for load in loads for r in (1, 2, 3)))

    def test_spaces_runs(self):
        specs = p0plans9.spaces_plan()
        for spec in specs:
            with self.subTest(key=spec["key"]):
                point, env, stack = spec["point"], spec["env"], spec["case"] == "stack"
                self.assertEqual(env["MESH_SPACES"], f"{point['S']}:{point['layout']}")
                self.assertEqual(env["MESH_PHANTOM"], f"virtual:{point['S']}:character:C")
                self.assertEqual(env["MESH_PHANTOM_AREA"], "disc:0,0,30" if stack else "0,0,600,600")
                self.assertNotIn("MESH_PHANTOM_VIEW", env)
                self.assertEqual((env["MESH_NO_MONSTERS"], env["MESH_PHANTOM_STAND"], spec["model"]),
                                 ("1", "1", "spaces"))
                players = 100 if spec["arm"] == "100" else 0
                self.assertEqual((point["players"], spec["plan"]), (players, {"C": 100} if players else {}))
                self.assertEqual(env.get("MESH_LOAD_AREA"), ("0,0,30" if stack else "0,0,300") if players else None)
                self.assertEqual(env.get("MESH_LOAD_FIGHT"), "0" if players else None)
        self.assertEqual(sorted((s["case"], s["point"]["S"]) for s in specs if s["arm"] == "100"),
                         sorted([("grid", 16)] * 2 + [("stack", s) for s in (1, 4, 16, 64)] * 2))

    def test_loss_runs_rotate_their_arms(self):
        specs = p0plans9.loss_plan()
        repeats = [[s["arm"] for s in specs if s["repeat"] == r] for r in (1, 2, 3)]
        self.assertEqual(repeats, [["loss0", "loss1", "loss5"], ["loss1", "loss5", "loss0"],
                                   ["loss5", "loss0", "loss1"]])
        reference = p0plans8b.cap_spec("sink", 1020, 1)
        for spec in specs:
            with self.subTest(key=spec["key"]):
                self.assertEqual(spec["peer"]["impair"], {"loss0": None, "loss1": "loss1", "loss5": "loss5"}[spec["arm"]])
                self.assertEqual(spec["peer"]["sink"], reference["peer"]["sink"])
                env = dict(spec["env"], MESH_LOAD_SEED="1")
                self.assertEqual(env, dict(reference["env"], MESH_NET_STATS="1"))
                self.assertEqual((spec["family"], spec["case"]), ("packets", "loss"))

    def test_packets_sink_keys_are_unchanged(self):
        """cap_spec is p0plans8b's old cap arm: b7's sink and virtual cap keys stay what they were."""
        specs = p0plans8b.packets_sink_plan()
        self.assertEqual(len(specs), 22)
        caps = [s for s in specs if s["case"] in ("cap", "cap-sink")]
        self.assertEqual([s["arm"] for s in caps], ["ship-sender-28-tiered"] * 18)
        self.assertEqual(caps[0]["key"], p0plans.plan_key({"family": "packets", "case": "cap-sink",
                                                             "arm": "ship-sender-28-tiered", "point": caps[0]["point"],
                                                             "repeat": 1}))
        self.assertEqual(set(caps[0]["point"]), {"P", "V", "packet_bytes", "rates", "scale"})


class CommandTests(unittest.TestCase):
    def test_dry_run_counts(self):
        for name, count in COUNTS.items():
            with self.subTest(name):
                code, out = p0load_main(name.split() + ["--dry-run"])
                self.assertEqual(code, 0, out)
                self.assertIn(f"{count} runs, about", out)

    def test_two_machine(self):
        cases = {("dbburst", "burst,sustained", False): False, ("dbburst", "burst,calibration", False): True,
                 ("packets", None, True): True, ("packets", None, False): False, ("spaces", None, False): False}
        for (command, parts, loss), expected in cases.items():
            with self.subTest(command=command, parts=parts, loss=loss):
                args = mock.Mock(command=command, parts=parts, loss=loss)
                self.assertEqual(p0load9.two_machine(args), expected)
                with mock.patch.object(p0load8b, "start_problems", return_value=["B: unreachable"]):
                    self.assertEqual(p0load9.run_refusals(args, []), ["B: unreachable"] if expected else [])

    def test_canaried_and_noted(self):
        for family in p0plans9.FAMILIES:
            with self.subTest(family):
                self.assertIn(family, p0load.CANARY_FAMILIES)
        _, out = p0load_main(["packets", "--loss", "--dry-run"])
        self.assertIn("clumsy loss5 on B", out)
        self.assertIn("'impairment on'", out)
        _, out = p0load_main(["dbburst", "--dry-run"])
        self.assertIn("stagger 0 s", out)
        self.assertIn("burst_rows 800 | stop_after_drain True", out)
        self.assertIn("meshburst0001..0800", out)
        _, out = p0load_main(["workerab", "--dry-run"])
        self.assertIn("--fallback", out)

    def test_peer_text(self):
        spec = {"peer": {"sink": None, "bots": dict(p0plans8b.bots(2, "stand", "centre"), stagger=0),
                         "impair": "loss1"}}
        self.assertEqual(p0dryrun.peer_text(spec), "bots meshbot2..meshbot3 (2) stand at centre, Bot net 5 s, stagger "
                                                   "0 s; clumsy loss1 on B (remoteb impair)")


if __name__ == "__main__":
    unittest.main()
