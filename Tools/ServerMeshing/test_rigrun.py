"""Unit tests for rigrun (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py").

start_problems, host_context, build_id, cell_lines, run_window and cell_window read the editor, PowerShell or the logs:
the offline re-grade (test_abregrade) and the dry runs cover them, not these tests.
"""
import copy
import itertools
import unittest
from unittest import mock

import p0tools
import regress
import rigrun
import rigtools

HALVES = rigrun.LAYOUTS["halves"]
PLAN = {"C": 125, "E": 125}
GOOD = {
    "C": {"stats": {"seconds": 60.0, "probe": ["Valid"] * 12, "budget": 16.67},
          "own": [("C", regress.MAP, (-300.0, -300.0, 300.0, 600.0))], "spawned": [125], "linked": {"E"},
          "exceptions": 0},
    "E": {"stats": {"seconds": 60.0, "probe": ["Valid"] * 12, "budget": 16.67},
          "own": [("E", regress.MAP, (0.0, -300.0, 300.0, 600.0))], "spawned": [125], "linked": {"C"},
          "exceptions": 0},
}


def mutated(cell, key, value):
    results = copy.deepcopy(GOOD)
    if key in results[cell]:
        results[cell][key] = value
    else:
        results[cell]["stats"][key] = value
    return results


class RunProblemsTests(unittest.TestCase):
    def test_run_problems(self):
        cases = {
            "never linked": (mutated("C", "linked", set()), ["C never linked with E"]),
            "started twice": (mutated("E", "own", GOOD["E"]["own"] * 2), ["E started 2 times"]),
            "never started": (mutated("E", "own", []), ["E started 0 times"]),
            "wrong rectangle": (mutated("C", "own", [("C", regress.MAP, (-300.0, -300.0, 600.0, 600.0))]),
                                ["C ran x/z/size (-300.0, -300.0, 600.0, 600.0), not the layout's (-300, -300, 300, 600)"]),
            "spawned nothing": (mutated("E", "spawned", []), ["E spawned nothing, planned 125"]),
            "spawned twice": (mutated("E", "spawned", [125, 125]), ["E spawned [125, 125], planned 125"]),
            "exceptions": (mutated("C", "exceptions", 2), ["C logged 2 exception line(s)"]),
            "no work window": (mutated("C", "stats", None), ["C logged no work window inside the measurement"]),
            "short coverage": (mutated("C", "seconds", 47.0),
                               ["C's work windows cover 47 s of the 60 s measurement, need 48"]),
            "probe invalid": (mutated("E", "probe", ["Valid", "Invalid"]), ["E's work probe went Invalid"]),
            "uncapped": (mutated("C", "budget", 0.0), ["C ran uncapped, so its work per frame is not comparable"]),
            "usable": (copy.deepcopy(GOOD), []),
        }
        for name, (results, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(rigrun.run_problems(HALVES, PLAN, results, 60), expected)

    def test_allow_uncapped(self):
        cases = {
            "capped guard kept by default": (False, ["C ran uncapped, so its work per frame is not comparable"]),
            "uncapped allowed": (True, []),
        }
        for name, (allow, expected) in cases.items():
            with self.subTest(name):
                results = mutated("C", "budget", 0.0)
                self.assertEqual(rigrun.run_problems(HALVES, PLAN, results, 60, allow_uncapped=allow), expected)

    def test_cell_without_rectangle_skips_links_and_rectangle(self):
        results = {"C": mutated("C", "linked", set())["C"]}
        results["C"]["own"] = [("C", regress.MAP, (-100.0, -100.0, 200.0, 200.0))]
        self.assertEqual(rigrun.run_problems([("C", None)], {}, results, 60), [])


class LayoutTests(unittest.TestCase):
    def test_layout_override(self):
        with self.assertRaises(KeyError):
            rigrun.layout_override("strips5")
        cases = {
            "grid": ("grid", ""),
            "coarse": ("coarse", "ServerMeshTestMap=C@-300,-300,600,600"),
            "halves": ("halves", "ServerMeshTestMap=C@-300,-300,300,600;E@0,-300,300,600"),
            "strips4": ("strips4", "ServerMeshTestMap=W@-300,-300,150,600;C@-150,-300,150,600;E@0,-300,150,600;"
                                   "NE@150,-300,150,600"),
        }
        for name, (layout, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(rigrun.layout_override(layout), expected)

    def test_strips4(self):
        cells = rigrun.LAYOUTS["strips4"]
        touching = {(a, b) for (a, ra), (b, rb) in itertools.combinations(cells, 2) if rigtools.rects_touch(ra, rb)}
        self.assertEqual(touching, {("W", "C"), ("C", "E"), ("E", "NE")})
        self.assertEqual(sum(rect[2] * rect[3] for _, rect in cells), rigrun.GROUND * rigrun.GROUND)
        self.assertEqual(rigrun.CUT_LENGTH["strips4"], 1800)
        self.assertEqual(rigtools.load_plan(cells, 250), {"W": 63, "C": 63, "E": 62, "NE": 62})
        self.assertTrue(all(cid in {z for z, _ in regress.ZONES} for cid, _ in cells))

    def test_every_layout_holds_c(self):
        for name, cells in rigrun.LAYOUTS.items():
            with self.subTest(name):
                self.assertIn("C", [cid for cid, _ in cells])


class CheckEnvTests(unittest.TestCase):
    PENDING = {"MESH_LATER_B": "stage 0b, step 7", "MESH_LATER_C": "stage 0c, step 9"}

    def test_check_env_refuses(self):
        cases = {
            "unknown key": ({"MESH_NOPE": "1"},
                            "MESH_NOPE is not in regress.ENV_KEYS, so a Play request would drop it silently"),
            "two unknown keys": ({"MESH_B": "", "MESH_A": "1"},
                                 "MESH_A, MESH_B are not in regress.ENV_KEYS, so a Play request would drop them "
                                 "silently"),
            "stage 0b key": ({"MESH_LATER_B": "1"}, "MESH_LATER_B is a stage 0b key (step 7) that no code reads yet"),
            "stage 0c key": ({"MESH_LATER_C": "2"}, "MESH_LATER_C is a stage 0c key (step 9) that no code reads yet"),
            "dropped key": ({"MESH_LOAD_COLLIDE": "1"},
                            "MESH_LOAD_COLLIDE was dropped in step 3 (decision 4): the kit already ignores every "
                            "character pair"),
            "unknown and dropped": ({"MESH_NOPE": "1", "MESH_LOAD_COLLIDE": "0"},
                                    "MESH_NOPE is not in regress.ENV_KEYS, so a Play request would drop it silently; "
                                    "MESH_LOAD_COLLIDE was dropped in step 3 (decision 4): the kit already ignores "
                                    "every character pair"),
            "unknown and pending": ({"MESH_BAND": "80", "MESH_LATER_B": "1", "MESH_NOPE": "1"},
                                    "MESH_NOPE is not in regress.ENV_KEYS, so a Play request would drop it silently; "
                                    "MESH_LATER_B is a stage 0b key (step 7) that no code reads yet"),
        }
        with mock.patch.object(p0tools, "PENDING_KEYS", self.PENDING, create=True):
            for name, (env, message) in cases.items():
                with self.subTest(name):
                    with self.assertRaises(ValueError) as caught:
                        rigrun.check_env(env)
                    self.assertEqual(str(caught.exception), message)

    def test_check_env_accepts(self):
        cases = {
            "empty": {},
            "every regress key, values unchecked": {key: "anything" for key in regress.ENV_KEYS},
            "an abload run": {"MESH_LAYOUT_OVERRIDE": rigrun.layout_override("halves"), "MESH_LOAD_PLAN": "C:125,E:125",
                              "MESH_LOAD_AREA": "", "MESH_LOAD_SEED": "1", "MESH_SPAWN_CELLS": "C,E", "MESH_BAND": "",
                              "MESH_NO_MONSTERS": "1"},
        }
        with mock.patch.object(p0tools, "PENDING_KEYS", self.PENDING, create=True):
            for name, env in cases.items():
                with self.subTest(name):
                    self.assertIsNone(rigrun.check_env(env))


if __name__ == "__main__":
    unittest.main()
