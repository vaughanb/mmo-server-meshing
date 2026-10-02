"""Unit tests for abgrade (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py").

0.71's own text on real runs is covered by test_abregrade, which replays the chain3 reports through abload.ab_command;
these tests pin each function on synthetic records.
"""
import datetime
import unittest
from unittest import mock

import abgrade
import abload
import p0plans
import p0tools
import rigrun
import rigtools

START = datetime.datetime(2026, 9, 25, 14, 15, 35)
POINT = (250, "off", 0.0)
SECTION = "busiest cell over the coarse cell, per repeat (same seed, runs minutes apart); <= 0.75 everywhere is GO:"
SECTION_STRIPS4 = ("busiest cell over the base layout's busiest cell (coarse; halves for strips4/halves), per repeat "
                   "(same seed, runs minutes apart); <= 0.75 everywhere is GO:")
CROSSING_250 = "  not measured: a halves cut would see about 0.8 crossings/s at 250 players (grid about 3.3/s), each a handoff"
STRIPS4_CROSSING_250 = "  not measured: a strips4 layout would see about 2.5 crossings/s at 250 players, each a handoff"
GO_LINE = "go/no-go (halves, spread load, every point): "
STRIPS4_LINE = "strips4/halves (spread load, every point): "


def args_of(*argv):
    return abload.build_parser().parse_args(["ab", *argv])


def stats(work, sat=0.0, owned=100, monsters=0, puppets=0, cpu=50):
    return {"work_mean": work, "sat": sat, "budget": 16.67, "util": work / 16.67, "owned_players": owned,
            "owned_monsters": monsters, "puppet_players": puppets, "puppet_monsters": 0, "cpu": cpu,
            "probe": ["Valid"]}


def rec(work, sat=None, monsters=None):
    """A usable run's record from each cell's work mean; sat and monsters are per cell, 0 unless given."""
    sat, monsters = sat or {}, monsters or {}
    return abgrade.run_record(START, {cid: stats(w, sat.get(cid, 0.0), monsters=monsters.get(cid, 0))
                                      for cid, w in work.items()})


def runs_of(**by_layout):
    """{layout: {attempt: record}} from a list per layout (None for a run that was not usable)."""
    return {layout: {i: r for i, r in enumerate(records) if r is not None} for layout, records in by_layout.items()}


def coarse(*works):
    return [rec({"C": w}) if w is not None else None for w in works]


def halves(*pairs):
    return [rec({"C": c, "E": e}) if c is not None else None for c, e in pairs]


def strips4(*works):
    return [rec({"W": w * 0.9, "C": w, "E": w * 0.95, "NE": w * 0.8}) if w is not None else None for w in works]


class ArgsTests(unittest.TestCase):
    def test_check_args_refuses(self):
        cases = {
            "unknown layout": (["--layouts", "coarse,strips5"],
                               "unknown layout strips5; known: coarse, halves, grid, strips4"),
            "unknown monster setting": (["--monsters", "some"], "unknown monster setting some; known: off, edge, all"),
            "players": (["--players", "150,x"], "--players 150,x is not a list of whole numbers"),
            "load": (["--load", "0,0"], "--load 0,0 is neither spread nor a crowd square x,z,r"),
            "band and bands": (["--band", "20", "--bands", "20,40"],
                               "--band 20 and --bands 20,40 both set the band; give --bands alone (it sets MESH_BAND "
                               "on every run)"),
            "band not a number": (["--bands", "20,x"], "--bands 20,x: x is not a number of metres"),
            "band 0": (["--bands", "0,40"], "--bands 0,40: 0 is not above 0 m (every band is set explicitly)"),
            "band twice": (["--bands", "20,20.0"], "--bands 20,20.0 lists 20 twice"),
            "no band": (["--bands", ","], "--bands ',' lists no band"),
            "puppets": (["--puppets", "inert"], "--puppets inert is not one of steered, kinematic"),
            "malformed rates": (["--rates", "30/x"], "--rates 30/x: MESH_RATES step part 'x' is not a number of seconds"),
            "uncapped rates": (["--rates", "u30/0.02"],
                               "--rates u30/0.02 runs uncapped: every run would fail the uncapped guard (its Work "
                               "window budget is 0); uncapped measurement is p0load's (density bridge-0.53)"),
            "bare u": (["--rates", "u/0.02"],
                       "--rates u/0.02 runs uncapped: every run would fail the uncapped guard (its Work window budget "
                       "is 0); uncapped measurement is p0load's (density bridge-0.53)"),
            "order": (["--order", "random"], "--order random is not one of point-major, repeat-major"),
        }
        for name, (argv, message) in cases.items():
            with self.subTest(name):
                for function in (abgrade.check_args, abgrade.ab_plan):
                    with self.assertRaises(ValueError) as caught:
                        function(args_of(*argv))
                    self.assertEqual(str(caught.exception), message)

    def test_check_args_accepts(self):
        cases = {
            "0.71 defaults": [],
            "crowd": ["--load", "-100,0,50"],
            "every Phase 0 flag": ["--layouts", "coarse,halves,strips4", "--bands", "20,40,80,150", "--puppets",
                                   "kinematic", "--rates", "60/0.03", "--instrument", "--order", "repeat-major",
                                   "--jsonl", "rows.jsonl", "--case", "b9-bands"],
        }
        for name, argv in cases.items():
            with self.subTest(name):
                self.assertIsNone(abgrade.check_args(args_of(*argv)))

    def test_listed_and_load_area(self):
        self.assertEqual(abgrade.listed(" coarse, ,halves,"), ["coarse", "halves"])
        self.assertIsNone(abgrade.load_area(args_of()))
        self.assertEqual(abgrade.load_area(args_of("--load", "-100,0,50")), (-100.0, 0.0, 50.0))


class PointsTests(unittest.TestCase):
    def test_ab_points(self):
        cases = {
            "0.71 defaults": ([], [(150, "off", 0.0), (150, "all", 0.0), (250, "off", 0.0), (250, "all", 0.0)]),
            "--band": (["--players", "250", "--monsters", "all", "--band", "20"], [(250, "all", 20.0)]),
            "--bands innermost": (["--players", "150,250", "--monsters", "all", "--bands", "80,20"],
                                  [(150, "all", 80.0), (150, "all", 20.0), (250, "all", 80.0), (250, "all", 20.0)]),
        }
        for name, (argv, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(abgrade.ab_points(args_of(*argv)), expected)

    def test_point_of(self):
        spec = abgrade.ab_plan(args_of("--players", "250", "--monsters", "all", "--bands", "40"))[0]
        self.assertEqual(abgrade.point_of(spec), (250, "all", 40.0))


class RunOrderTests(unittest.TestCase):
    POINTS = [(150, "off", 0.0), (250, "off", 0.0)]
    LAYOUTS = ["coarse", "halves", "grid"]

    def test_refuses_another_order(self):
        with self.assertRaisesRegex(ValueError, "run order 'random' is not one of point-major, repeat-major"):
            abgrade.run_order(self.POINTS, self.LAYOUTS, 3, "random")

    def test_point_major_is_071_nested_loops(self):
        expected = []
        for point in self.POINTS:
            for attempt in range(3):
                for position, layout in enumerate(rigtools.rotate(self.LAYOUTS, attempt)):
                    expected.append((attempt, point, position, layout))
        self.assertEqual(abgrade.run_order(self.POINTS, self.LAYOUTS, 3), expected)
        self.assertEqual(abgrade.run_order(self.POINTS, self.LAYOUTS, 3, "point-major"), expected)

    def test_repeat_major(self):
        runs = abgrade.run_order(self.POINTS, self.LAYOUTS, 3, "repeat-major")
        self.assertEqual(len(runs), 18)
        self.assertEqual([point for attempt, point, position, _ in runs if position == 0],
                         [self.POINTS[0], self.POINTS[1], self.POINTS[1], self.POINTS[0], self.POINTS[0], self.POINTS[1]])
        self.assertEqual([attempt for attempt, _, _, _ in runs], [0] * 6 + [1] * 6 + [2] * 6)
        self.assertEqual(runs[6:9], [(1, self.POINTS[1], 0, "halves"), (1, self.POINTS[1], 1, "grid"),
                                     (1, self.POINTS[1], 2, "coarse")])
        for point in self.POINTS:
            with self.subTest(point):
                seen = {(layout, position) for _, p, position, layout in runs if p == point}
                self.assertEqual(seen, {(layout, position) for layout in self.LAYOUTS for position in range(3)})


class PlanTests(unittest.TestCase):
    NEW_KEYS = {"MESH_PUPPET_MOTION", "MESH_RATES", "MESH_BAND", *p0plans.INSTR}

    def test_default_plan_is_071(self):
        specs = abgrade.ab_plan(args_of())
        self.assertEqual(len(specs), 36)
        self.assertEqual(specs[0]["env"], {"MESH_LAYOUT_OVERRIDE": "ServerMeshTestMap=C@-300,-300,600,600",
                                           "MESH_LOAD_PLAN": "C:150", "MESH_LOAD_SEED": "1", "MESH_SPAWN_CELLS": "C",
                                           "MESH_NO_MONSTERS": "1"})
        self.assertEqual(specs[3]["env"], {"MESH_LAYOUT_OVERRIDE": rigrun.layout_override("halves"),
                                           "MESH_LOAD_PLAN": "C:75,E:75", "MESH_LOAD_SEED": "2",
                                           "MESH_SPAWN_CELLS": "C,E", "MESH_NO_MONSTERS": "1"})
        self.assertEqual({spec["arm"] for spec in specs if "MESH_LAYOUT_OVERRIDE" not in spec["env"]}, {"grid"})
        self.assertFalse(any(self.NEW_KEYS & set(spec["env"]) for spec in specs))

    def test_env_per_flag(self):
        cases = {
            "--band": (["--band", "40"], {"MESH_BAND": "40"}),
            "crowd": (["--load", "0,0,60"], {"MESH_LOAD_AREA": "0,0,60"}),
            "monsters all": (["--monsters", "all"], {"MESH_MONSTER_AREAS": "all"}),
            "--puppets": (["--puppets", "kinematic"], {"MESH_PUPPET_MOTION": "kinematic"}),
            "--rates": (["--rates", "60/0.03"], {"MESH_RATES": "60/0.03"}),
            "--instrument": (["--instrument"], p0plans.INSTR),
        }
        for name, (argv, keys) in cases.items():
            with self.subTest(name):
                for spec in abgrade.ab_plan(args_of(*argv)):
                    self.assertEqual({key: spec["env"].get(key) for key in keys}, keys)

    def test_bands_set_mesh_band_on_every_run(self):
        specs = abgrade.ab_plan(args_of("--players", "250", "--monsters", "all", "--layouts", "halves",
                                        "--bands", "80,150", "--repeats", "2"))
        self.assertEqual([spec["env"]["MESH_BAND"] for spec in specs], ["80", "80", "150", "150"])
        self.assertEqual([spec["band"] for spec in specs], [80.0, 80.0, 150.0, 150.0])
        self.assertEqual([spec["seed"] for spec in specs], [1, 2, 1, 2])

    def test_spec_fields(self):
        argv = ["--puppets", "kinematic", "--players", "250", "--monsters", "all", "--layouts", "coarse,halves,strips4",
                "--bands", "20,40,80,150", "--repeats", "3", "--instrument", "--order", "repeat-major", "--rates",
                "60/0.03", "--case", "b9-bands"]
        specs = abgrade.ab_plan(args_of(*argv))
        self.assertEqual(len(specs), 36)
        first = dict(specs[0])
        key = first.pop("key")
        self.assertEqual(key, p0plans.plan_key(first))
        env = first.pop("env")
        self.assertEqual(first, {
            "family": "abgeo", "game": "fantasy", "case": "b9-bands", "arm": "coarse",
            "point": {"players": 250, "monsters": "all", "band": 20.0, "load": "spread", "puppets": "kinematic",
                      "rates": "60/0.03"},
            "repeat": 1, "seed": 1, "order": 1, "layout": "coarse", "cells_started": ["C"], "cells_read": ["C"],
            "plan": {"C": 250}, "warmup": 45, "measure": 60, "rates": "60/0.03", "uncapped": [], "band": 20.0,
            "expect": {}, "position": 1})
        self.assertEqual(env, dict({"MESH_LAYOUT_OVERRIDE": "ServerMeshTestMap=C@-300,-300,600,600",
                                    "MESH_LOAD_PLAN": "C:250", "MESH_LOAD_SEED": "1", "MESH_SPAWN_CELLS": "C",
                                    "MESH_BAND": "20", "MESH_MONSTER_AREAS": "all", "MESH_PUPPET_MOTION": "kinematic",
                                    "MESH_RATES": "60/0.03"}, **p0plans.INSTR))
        self.assertEqual([spec["order"] for spec in specs], list(range(1, 37)))
        self.assertEqual(len({spec["key"] for spec in specs}), 36)
        strips = next(spec for spec in specs if spec["arm"] == "strips4")
        self.assertEqual((strips["cells_started"], strips["plan"]),
                         (["W", "C", "E", "NE"], {"W": 63, "C": 63, "E": 62, "NE": 62}))
        self.assertEqual(specs[12]["point"]["band"], 150.0)   # repeat 2 runs the points backwards

    def test_every_env_passes_check_env(self):
        args = args_of("--puppets", "kinematic", "--layouts", "coarse,halves")
        with mock.patch.object(rigrun, "check_env", wraps=rigrun.check_env) as check:
            specs = abgrade.ab_plan(args)
        self.assertEqual([c.args[0] for c in check.call_args_list], [spec["env"] for spec in specs])
        with mock.patch.object(p0tools, "PENDING_KEYS", {"MESH_PUPPET_MOTION": "stage 0b, step 5"}):
            with self.assertRaisesRegex(ValueError, "MESH_PUPPET_MOTION is a stage 0b key"):
                abgrade.ab_plan(args)

    def test_abgeo_invocations_plan_their_run_counts(self):
        counts = {}
        for label, argv in p0plans.abgeo_invocations("rows.jsonl"):
            specs = abgrade.ab_plan(abload.build_parser().parse_args(argv))
            counts[label] = len(specs)
            with self.subTest(label):
                self.assertEqual(len(specs), p0plans.ab_run_count(argv))
                self.assertEqual({spec["case"] for spec in specs}, {label})
        self.assertEqual(counts, {"b9-main": 36, "b9-bands": 36, "b9-steered-b150": 6, "b9-steered-b80": 6})


class HeaderTests(unittest.TestCase):
    def test_header_lines(self):
        static = ("=== static A/B, capped, load {load}, band {band}, 3 repeat(s) (seeds 1..3), warmup 45s "
                  "measure 60s")
        cases = {
            "0.71 defaults": ([], [static.format(load="spread", band="as configured (80 m)")]),
            "--band": (["--band", "20"], [static.format(load="spread", band="20.0")]),
            "crowd": (["--load", "0,0,60"], [static.format(load="0,0,60", band="as configured (80 m)")]),
            "--case alone": (["--case", "b9"], [static.format(load="spread", band="as configured (80 m)")]),
            "--bands": (["--bands", "20,40", "--puppets", "kinematic", "--rates", "60/0.03", "--instrument",
                         "--order", "repeat-major"],
                        [static.format(load="spread", band="per point (--bands)"),
                         "=== phase 0: puppets kinematic, rates 60/0.03, bands 20,40, instruments on, "
                         "order repeat-major, rows -"]),
            "--jsonl alone": (["--jsonl", "Logs/p0/rows.jsonl"],
                              [static.format(load="spread", band="as configured (80 m)"),
                               "=== phase 0: puppets as configured, rates as configured, bands -, instruments off, "
                               "order point-major, rows Logs/p0/rows.jsonl"]),
        }
        for name, (argv, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(abgrade.header_lines(args_of(*argv)), expected)

    def test_run_heading(self):
        cases = {
            "0.71": ([], False, "--- 150 players, monsters off, repeat 1 (seed 1), coarse (1/3): plan C:150"),
            "--bands": (["--bands", "20"], True,
                        "--- 150 players, monsters off, band 20, repeat 1 (seed 1), coarse (1/3): plan C:150"),
        }
        for name, (argv, bands_given, expected) in cases.items():
            with self.subTest(name):
                spec = abgrade.ab_plan(args_of(*argv))[0]
                self.assertEqual(abgrade.run_heading(spec, 3, bands_given), expected)


class RecordTests(unittest.TestCase):
    def test_run_record_and_line(self):
        record = abgrade.run_record(START, {"C": stats(7.75, sat=25.0, owned=125, monsters=142, puppets=13, cpu=120),
                                            "E": stats(7.52, owned=125, monsters=143, puppets=6, cpu=130)})
        self.assertEqual({key: record[key] for key in ("peak_cell", "peak_work", "players", "monsters", "max_sat",
                                                       "saturated", "probe")},
                         {"peak_cell": "C", "peak_work": 7.75, "players": 250, "monsters": 285, "max_sat": 25.0,
                          "saturated": ["C"], "probe": ["Valid"]})
        self.assertAlmostEqual(record["sum_work"], 15.27)
        self.assertAlmostEqual(record["cores"], 2.5)
        self.assertEqual(abgrade.run_line(record),
                         "    peak C 7.75 ms | C 7.75 ms sat 25% own 125p/142m pup 13p/0m, E 7.52 ms sat 0% own "
                         "125p/143m pup 6p/0m | 14:15:35 SATURATED C")

    def test_cell_mean(self):
        self.assertAlmostEqual(abgrade.cell_mean(runs_of(halves=halves((6.0, 5.0), (7.0, 5.5)))["halves"], "E"), 5.25)


class SummaryTests(unittest.TestCase):
    def test_summarize_layout(self):
        grid = rec({cid: 3.0 + i for i, (cid, _) in enumerate(rigrun.LAYOUTS["grid"])})
        cases = {
            "no data": ((POINT, "halves", {}, 3, False), "250      off    halves  0/3 (no data)"),
            "no data, band column": (((250, "off", 20.0), "halves", {}, 3, True),
                                     "250      off    20     halves  0/3 (no data)"),
            "one cell": ((POINT, "coarse", runs_of(coarse=coarse(12.0, 13.0))["coarse"], 3, False),
                         "250      off    coarse  2/3    12.50     12.00-13.00    C=12.50                12.50    "
                         "0.5    100     0        0      Valid"),
            "four cells listed": ((POINT, "strips4", runs_of(strips4=strips4(4.0))["strips4"], 3, False),
                                  "250      off    strips4 1/3    4.00      4.00-4.00      W=3.60 C=4.00 E=3.80 "
                                  "NE=3.20 14.60    2.0    400     0        0      Valid"),
            "nine cells: the busiest": ((POINT, "grid", {0: grid}, 1, False),
                                        "250      off    grid    1/1    11.00     11.00-11.00    NE=11.00               "
                                        "63.00    4.5    900     0        0      Valid"),
        }
        for name, (call, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(abgrade.summarize_layout(*call), expected)

    def test_summary_lines(self):
        points = {POINT: runs_of(coarse=coarse(12.0), halves=[])}
        header = ("players  mon    layout  runs   peak ms   range          cell means ms          sum ms   cores  "
                  "owned p monsters sat %  probe")
        lines = abgrade.summary_lines(points, ["coarse", "halves"], 3)
        self.assertEqual(lines[:2], ["", header])
        self.assertEqual(lines[3], "250      off    halves  0/3 (no data)")
        banded = abgrade.summary_lines({(250, "off", 20.0): points[POINT]}, ["coarse", "halves"], 3, True)
        self.assertEqual(banded[1], header.replace("mon    layout", "mon    band   layout"))
        self.assertEqual(banded[3], "250      off    20     halves  0/3 (no data)")


class GradeTests(unittest.TestCase):
    def grade(self, runs, layouts, area=None, repeats=3, point=POINT, bands_given=False):
        return abgrade.grade_points({point: runs}, layouts, area, repeats, bands_given)

    def test_saturation_leaves_pairs_out(self):
        pair = "  250 players, monsters off: {} = "
        cases = {
            "base peak saturated": (
                runs_of(coarse=[rec({"C": 12.0}), rec({"C": 12.0}, sat={"C": 25.0}), rec({"C": 10.0})],
                        halves=halves((6.0, 5.0), (6.0, 5.0), (6.0, 5.0))),
                ["coarse", "halves"], "halves/coarse",
                pair.format("halves/coarse") + "0.50 0.60 (mean 0.55; busiest cell's mean over coarse's mean 0.53); "
                "left out: repeat 2 (coarse saturated) -> GO"),
            "cut peak saturated": (
                runs_of(coarse=coarse(12.0, 12.0, 12.0),
                        halves=[rec({"C": 6.0, "E": 5.0}, sat={"C": 30.0}), rec({"C": 6.0, "E": 5.0}),
                                rec({"C": 6.0, "E": 5.0})]),
                ["coarse", "halves"], "halves/coarse",
                pair.format("halves/coarse") + "0.50 0.50 (mean 0.50; busiest cell's mean over coarse's mean 0.50); "
                "left out: repeat 1 (halves peak cell saturated) -> GO"),
            "a cut cell that is not the peak": (
                runs_of(coarse=coarse(12.0, 12.0), halves=[rec({"C": 6.0, "E": 5.0}, sat={"E": 30.0}),
                                                           rec({"C": 6.0, "E": 5.0})]),
                ["coarse", "halves"], "halves/coarse",
                pair.format("halves/coarse") + "0.50 0.50 (mean 0.50; busiest cell's mean over coarse's mean 0.50) "
                "-> GO"),
            "halves as base with its peak saturated": (
                runs_of(halves=[rec({"C": 8.0, "E": 7.0}, sat={"C": 25.0}), rec({"C": 8.0, "E": 7.0}),
                                rec({"C": 8.0, "E": 7.0})],
                        strips4=strips4(4.0, 4.0, 4.0)),
                ["halves", "strips4"], "strips4/halves",
                pair.format("strips4/halves") + "0.50 0.50 (mean 0.50; busiest cell's mean over halves's mean 0.50); "
                "left out: repeat 1 (halves peak cell saturated) -> GO"),
            "every pair left out": (
                runs_of(coarse=[rec({"C": 12.0}, sat={"C": 25.0})] * 3,
                        halves=halves((6.0, 5.0), (6.0, 5.0), (6.0, 5.0))),
                ["coarse", "halves"], "halves/coarse",
                "  250 players, monsters off: halves/coarse: no usable pair; left out: repeat 1 (coarse saturated), "
                "repeat 2 (coarse saturated), repeat 3 (coarse saturated) -> SATURATED"),
        }
        for name, (runs, layouts, group, expected) in cases.items():
            with self.subTest(name):
                lines, groups = self.grade(runs, layouts)
                self.assertIn(expected, lines)
                self.assertEqual(list(groups), [group])

    def test_071_text(self):
        runs = runs_of(coarse=coarse(12.0, 10.0), halves=halves((6.0, 5.0), (6.0, 5.5)))
        lines, groups = self.grade(runs, ["coarse", "halves"], repeats=2)
        self.assertEqual(lines, [
            "", SECTION,
            "  250 players, monsters off: halves/coarse = 0.50 0.60 (mean 0.55; busiest cell's mean over coarse's mean "
            "0.55) -> GO",
            CROSSING_250,
            GO_LINE + "GO"])
        self.assertEqual(groups, {"halves/coarse": ["GO"]})

    def test_every_pair_in_order(self):
        grid = rec({cid: 2.0 for cid, _ in rigrun.LAYOUTS["grid"]})
        runs = runs_of(coarse=coarse(10.0, 10.0), halves=halves((6.0, 5.0), (6.0, 5.0)), grid=[grid, grid],
                       strips4=strips4(4.0, 5.0))
        lines, groups = self.grade(runs, ["coarse", "halves", "grid", "strips4"], repeats=2)
        self.assertEqual(lines, [
            "", SECTION_STRIPS4,
            "  250 players, monsters off: halves/coarse = 0.60 0.60 (mean 0.60; busiest cell's mean over coarse's mean "
            "0.60) -> GO",
            "  250 players, monsters off: grid/coarse = 0.20 0.20 (mean 0.20; busiest cell's mean over coarse's mean "
            "0.20)",
            "  250 players, monsters off: strips4/coarse = 0.40 0.50 (mean 0.45; busiest cell's mean over coarse's mean "
            "0.45)",
            "  250 players, monsters off: strips4/halves = 0.67 0.83 (mean 0.75; busiest cell's mean over halves's mean "
            "0.75) -> MARGINAL",
            CROSSING_250, STRIPS4_CROSSING_250,
            GO_LINE + "GO",
            STRIPS4_LINE + "MARGINAL"])
        self.assertEqual(groups, {"halves/coarse": ["GO"], "strips4/halves": ["MARGINAL"]})

    def test_strips4_halves_without_coarse(self):
        runs = runs_of(halves=halves((8.0, 7.0), (8.0, 7.0)), strips4=strips4(4.0, 4.4))
        lines, groups = self.grade(runs, ["halves", "strips4"], repeats=2, bands_given=True, point=(250, "off", 80.0))
        self.assertEqual(lines, [
            "", SECTION_STRIPS4,
            "  250 players, monsters off, band 80: strips4/halves = 0.50 0.55 (mean 0.53; busiest cell's mean over "
            "halves's mean 0.53) -> GO",
            CROSSING_250, STRIPS4_CROSSING_250,
            abgrade.NO_GO_NO_GO,
            STRIPS4_LINE + "GO"])
        self.assertEqual(groups, {"strips4/halves": ["GO"]})
        self.assertEqual(abgrade.exit_code({POINT: runs}, groups), 0)

    def test_missing_partner_is_skipped(self):
        grid = rec({cid: 2.0 for cid, _ in rigrun.LAYOUTS["grid"]})
        lines, groups = self.grade(runs_of(coarse=coarse(10.0), grid=[grid]), ["coarse", "grid"], repeats=1)
        self.assertEqual(lines, [
            "", SECTION,
            "  250 players, monsters off: grid/coarse = 0.20 (mean 0.20; busiest cell's mean over coarse's mean 0.20)",
            CROSSING_250, abgrade.NO_GO_NO_GO])
        self.assertEqual(groups, {})

    def test_no_usable_base_run(self):
        lines, groups = self.grade(runs_of(coarse=[None, None], halves=halves((6.0, 5.0), (6.0, 5.0))),
                                   ["coarse", "halves"], repeats=2)
        self.assertEqual(lines[2], "  250 players, monsters off: halves/coarse: no usable coarse run -> INSUFFICIENT")
        self.assertEqual(lines[-1], GO_LINE + "NO VERDICT")
        self.assertEqual(abgrade.exit_code({POINT: {}}, groups), 1)

    def test_monster_warning(self):
        runs = runs_of(coarse=[rec({"C": 12.0}, monsters={"C": 284})],
                       halves=[rec({"C": 6.0, "E": 5.0}, monsters={"C": 100, "E": 100})])
        lines, _ = self.grade(runs, ["coarse", "halves"], repeats=1, point=(250, "all", 0.0))
        self.assertEqual(lines[2], "  250 players, monsters all: WARNING the layouts owned different monster totals "
                                   "(coarse 284, halves 200)")
        self.assertTrue(lines[3].startswith("  250 players, monsters all: halves/coarse = 0.50 "))

    def test_crowd_has_no_verdict_and_no_crossing_line(self):
        runs = runs_of(coarse=coarse(12.0, 12.0), halves=halves((11.0, 1.0), (12.0, 1.0)))
        lines, groups = self.grade(runs, ["coarse", "halves"], area=(0.0, 0.0, 60.0), repeats=2)
        self.assertEqual(lines, [
            "", SECTION,
            "  250 players, monsters off: halves/coarse = 0.92 1.00 (mean 0.96; busiest cell's mean over coarse's mean "
            "0.96)",
            abgrade.NO_GO_NO_GO])
        self.assertEqual(groups, {})

    def test_exit_code(self):
        usable = {POINT: runs_of(coarse=coarse(12.0))}
        unusable = {POINT: runs_of(coarse=[], halves=[])}
        cases = {
            "no group, nothing usable": (unusable, {}, 1),
            "a group without a verdict": (usable, {"halves/coarse": ["GO", "INSUFFICIENT"]}, 1),
            "one of two groups without a verdict": (
                usable, {"halves/coarse": ["GO"], "strips4/halves": ["SATURATED"]}, 1),
            "no group, a usable run": (usable, {}, 0),
            "decided groups": (usable, {"halves/coarse": ["NO-GO"], "strips4/halves": ["GO", "MARGINAL"]}, 0),
        }
        for name, (points, groups, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(abgrade.exit_code(points, groups), expected)
        lines, _ = abgrade.grade_points(unusable, ["coarse", "halves"], (0.0, 0.0, 60.0), 3)
        self.assertEqual(lines[-1], abgrade.NO_GO_NO_GO + "; and no run was usable")


if __name__ == "__main__":
    unittest.main()
