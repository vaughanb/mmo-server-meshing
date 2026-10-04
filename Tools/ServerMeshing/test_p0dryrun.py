"""Unit tests for p0dryrun (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py")."""
import unittest

import p0dryrun
import p0plans
import p0plans0b
import regress
import test_p0plans
import test_p0plans0b

find, COARSE = test_p0plans.find, test_p0plans.COARSE


class PredictLoadTests(unittest.TestCase):
    def test_predict_load(self):
        with self.assertRaisesRegex(ValueError, "MESH_LOAD_MOTION 'flying' has no predicted cost"):
            p0dryrun.predict_load(dict(p0plans.canary_plan("check")[0], env={"MESH_LOAD_MOTION": "flying"}))
        heavy = p0plans.presence_plan(points=(400,), steered_points=(250, 400))
        cases = {
            "250 steered owners": (find(heavy, "solo", "owners-steered", players=250), "C", 11.4, False),
            "400 steered owners": (find(heavy, "solo", "owners-steered", players=400), "C", 17.88, True),
            "400 kinematic owners": (find(heavy, "solo", "owners-kinematic", players=400), "C", 4.72, False),
            "250 steered puppets": (find(heavy, "main", "puppets-steered", players=250), "E", 11.6, False),
            "400 + 400 kinematic": (find(heavy, "additivity", "puppets-kinematic", players=400), "C", 6.6, False),
            "the idle floor": (find(heavy, "baseline", "idle"), "E", 0.6, False),
        }
        for name, (spec, cell, ms, saturated) in cases.items():
            with self.subTest(name):
                load = p0dryrun.predict_load(spec)[cell]
                self.assertAlmostEqual(load["ms_per_frame"], ms)
                self.assertAlmostEqual(load["util"], ms * 60 / 1000)
                self.assertEqual((load["saturated"], load["label"]), (saturated, "E"))

    def test_monsters_abgeo_and_uncapped(self):
        density = p0plans.density_plan()
        monsters = find(density, "attribution", "monsters-on")
        self.assertAlmostEqual(p0dryrun.predict_load(monsters)["C"]["ms_per_frame"], 0.6 + 200 * 0.0432 + 294 * 0.0037)
        counts = {"C": {"O_monster": 100, "U_monster": 10}}
        self.assertAlmostEqual(p0dryrun.predict_load(monsters, counts)["C"]["ms_per_frame"],
                               0.6 + 200 * 0.0432 + 100 * 0.0037 + 10 * 0.0087)
        bridge = p0dryrun.predict_load(find(density, "bridge-0.53", "uncapped"))
        self.assertEqual((bridge["C"]["uncapped"], bridge["C"]["saturated"], len(bridge)), (True, False, 9))
        abgeo = {"family": "abgeo", "layout": "halves", "cells_started": ["C", "E"], "plan": {"C": 125, "E": 125},
                 "env": {"MESH_NO_MONSTERS": "1", "MESH_PUPPET_MOTION": "kinematic"}, "band": 80.0, "expect": {},
                 "uncapped": []}
        puppets = 250 / (600 * 600) * 80 * 600
        self.assertAlmostEqual(p0dryrun.predict_load(abgeo)["E"]["ms_per_frame"], 0.6 + 125 * 0.0432 + puppets * 0.0047)
        rig = p0dryrun.predict_load(p0plans.accept_plan("A")[0])
        self.assertEqual(list(rig), list(p0plans.TEN_CELLS))
        self.assertAlmostEqual(rig["C2"]["ms_per_frame"], 0.6)


class DryRunTests(unittest.TestCase):
    def test_stage_0b_is_not_modelled(self):
        with test_p0plans0b.new_keys():
            fanout = p0plans0b.fanout_plan()
            prewarm = p0plans0b.prewarm_plan()
        for spec in (fanout[0], prewarm[0], test_p0plans0b.plans()["observed M"][0]):
            with self.subTest(spec["family"]):
                self.assertIsNone(p0dryrun.predict_load(spec))
        lines = p0dryrun.dry_run_lines(fanout[:2] + prewarm[:1])
        self.assertEqual(lines[1], f"[1/3] fanout main char-kit V=100 rates=30/0.02 r1 seed 1 key {fanout[0]['key']} | "
                                   f"layout coarse | start C | read C | 10+388 s | steps 50,100,200,400 at 90 s each")
        self.assertEqual([lines[3], lines[9]], ["    predicted (E) not modelled for phantoms, ships or monsters"] * 2)
        self.assertTrue(lines[7].endswith("| read C | 45+60 s"))
        self.assertEqual(lines[-1], "3 runs, about 16 min at 126 s per 45+60 run and 90 s per 25+40 run")

    def test_dry_run_lines(self):
        specs = p0plans.floor_plan()
        lines = p0dryrun.dry_run_lines(specs)
        used = {key for spec in specs for key in spec["env"]}
        self.assertEqual(lines[0], f"every other regress.ENV_KEYS key is cleared "
                                   f"({len(set(regress.ENV_KEYS) - used)} keys)")
        self.assertEqual(lines[-1], "12 runs, about 25 min at 126 s per 45+60 run and 90 s per 25+40 run")
        self.assertEqual(len(lines), 2 + 3 * 12)
        self.assertEqual(lines[1], f"[1/12] floor cell idle players=0 rates=30/0.02 r1 seed 1 key {specs[0]['key']} | "
                                   f"layout coarse | start C | read C | 45+60 s")
        self.assertEqual(lines[2], f"    env: MESH_AOI_TIMING=1 MESH_LAYOUT_OVERRIDE={COARSE} MESH_NO_MONSTERS=1 "
                                   f"MESH_RATES=30/0.02 MESH_SPAWN_CELLS=C MESH_THREAD_CPU=1 MESH_WORK_BINS=1")
        self.assertEqual(lines[3], "    predicted (E): C 0.6 ms/frame util 0.04")

    def test_flags_and_short_runs(self):
        heavy = p0dryrun.dry_run_lines(p0plans.presence_plan(steered_points=(400,)))
        self.assertTrue(any(line.endswith("C 17.9 ms/frame util 1.07 [predicted saturated]") for line in heavy))
        accept = p0dryrun.dry_run_lines(p0plans.accept_plan("E"), env_keys=["MESH_RATES", "MESH_BAND", "MESH_SLEEP"])
        self.assertEqual(accept[0], "every other regress.ENV_KEYS key is cleared (1 keys)")
        self.assertEqual(accept[-1], "6 runs, about 9 min at 126 s per 45+60 run and 90 s per 25+40 run")
        self.assertIn("| checks_family density", accept[1 + 3 * 2])
        self.assertEqual(p0dryrun.dry_run_lines(p0plans.canary_plan("check"))[-1],
                         "1 run, about 2 min at 126 s per 45+60 run and 90 s per 25+40 run")
        self.assertEqual(p0plans.run_seconds(dict(p0plans.canary_plan("check")[0], warmup=10, measure=20)), 51)


if __name__ == "__main__":
    unittest.main()
