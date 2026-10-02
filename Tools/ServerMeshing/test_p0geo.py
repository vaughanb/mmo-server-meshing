"""Unit tests for p0geo (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py")."""
import math
import unittest

import p0base
import p0geo
import rigrun

HALVES = "ServerMeshTestMap=C@-300,-300,300,600;E@0,-300,300,600"
COARSE = "ServerMeshTestMap=C@-300,-300,600,600"
STRIPS4 = [("W", (-300.0, -300.0, 150.0, 600.0)), ("C", (-150.0, -300.0, 150.0, 600.0)),
           ("E", (0.0, -300.0, 150.0, 600.0)), ("NE", (150.0, -300.0, 150.0, 600.0))]
HALVES_CELLS = p0geo.layout_cells(HALVES)
COARSE_CELLS = p0geo.layout_cells(COARSE)


def window(owned=0.0, puppets=0.0, owned_monsters=0.0, puppet_monsters=0.0, seconds=5.0, saturated=False):
    """One Work window entry of a run row's `windows`."""
    return {"end": "2026-09-28T10:00:05", "seconds": seconds, "frames": 300, "work_ms_per_s": 0.0,
            "owned_players": owned, "owned_monsters": owned_monsters, "puppet_players": puppets,
            "puppet_monsters": puppet_monsters, "saturated": saturated}


def run_row(family, case, arm, repeat, loads, env=None, started=None, point=None, **fields):
    """A minimal accepted mesh-p0/1 run row at 60/0.03. loads = {cell: (work ms/s, owned players, puppet players[,
    owned monsters, puppet monsters])}, each as one 60 s Work window; fields override anything."""
    row = {"row": "run", "family": family, "case": case, "arm": arm, "repeat": repeat, "point": point or {},
           "plan_key": f"{family}-{case}-{arm}-{repeat}-{point}", "accepted": True, "capped": True,
           "rates": {"fps": 60, "fixed_step_s": 0.03, "tick_hz": 20}, "cpu": "Test CPU", "build": "b1",
           "env": dict(env or {}), "cells_started": started or list(loads),
           "metrics": [{"name": "work_ms_per_s", "stat": "mean", "value": v[0], "unit": "ms/s", "label": "M",
                        "cell": cell} for cell, v in loads.items()],
           "windows": {cell: [window(*v[1:], seconds=60.0)] for cell, v in loads.items()}}
    row.update(fields)
    return row


class GeometryTests(unittest.TestCase):
    def test_refusals(self):
        cases = {
            "override without @": (lambda: p0geo.layout_cells("M=C-300,-300,300,600"), "is not cell@x,z,width,height"),
            "override with a word": (lambda: p0geo.layout_cells("M=C@a,0,1,1"), "is not cell@x,z,width,height"),
            "override with no width": (lambda: p0geo.layout_cells("M=C@0,0,0,1"), "is not cell@x,z,width,height"),
            "area of two numbers": (lambda: p0geo.crowd_rect("0,0"), "MESH_LOAD_AREA '0,0' is not x,z,r"),
            "area with zero half-size": (lambda: p0geo.crowd_rect("0,0,0"), "half-size must be positive"),
            "pairs of negative n": (lambda: p0geo.expected_local_pairs(-1, (0, 0, 10, 10)), "needs n >= 0"),
            "pairs over nothing": (lambda: p0geo.expected_local_pairs(10, None), "rectangle with area"),
            "pairs over a line": (lambda: p0geo.expected_local_pairs(10, (0, 0, 0, 10)), "rectangle with area"),
        }
        for name, (call, message) in cases.items():
            with self.subTest(name), self.assertRaisesRegex(p0base.ModelError, message):
                call()

    def test_layout_cells(self):
        self.assertEqual(p0geo.layout_cells(HALVES),
                         [("C", (-300.0, -300.0, 300.0, 600.0)), ("E", (0.0, -300.0, 300.0, 600.0))])
        self.assertEqual(p0geo.layout_cells(""), p0geo.REGISTRY_GRID)
        for name, cells in rigrun.LAYOUTS.items():
            with self.subTest(name):
                self.assertEqual(p0geo.layout_cells(rigrun.layout_override(name)),
                                 [(cid, tuple(float(v) for v in rect)) for cid, rect in cells])
        self.assertEqual(STRIPS4, [(cid, tuple(float(v) for v in rect)) for cid, rect in rigrun.LAYOUTS["strips4"]])

    def test_crowd_rect_and_intersect(self):
        self.assertIsNone(p0geo.crowd_rect(""))
        self.assertEqual(p0geo.crowd_rect("0,0,50"), (-50.0, -50.0, 100.0, 100.0))
        self.assertEqual(p0geo.intersect((-300, -300, 300, 600), (-50, -50, 100, 100)), (-50, -50, 50, 100))
        self.assertIsNone(p0geo.intersect((-300, -300, 300, 600), (0, -300, 300, 600)))
        self.assertEqual(p0geo.expand((0, 0, 10, 20), 5), (-5, -5, 20, 30))

    def test_expected_local_pairs(self):
        cases = {
            "nobody": (0, (-100, -100, 200, 200), 0.0),
            "400 on aligned buckets": (400, (-100, -100, 200, 200), 1996.0),
            "200 on unaligned buckets": (200, (-50, -50, 100, 100), 1489.52),
            "250 on the ground": (250, (-300, -300, 600, 600), 319.1666667),
            "400 on halves C": (400, (-300, -300, 300, 600), 754.6666667),
            "100 on halves C": (100, (-300, -300, 300, 600), 122.0),
        }
        for name, (n, rect, expected) in cases.items():
            with self.subTest(name):
                self.assertAlmostEqual(p0geo.expected_local_pairs(n, rect), expected, places=6)


class SplitModelTests(unittest.TestCase):
    def test_refusals(self):
        cases = {
            "band zero": (lambda: p0geo.break_even_width(0, 1.0), "band must be positive; got 0"),
            "negative k": (lambda: p0geo.break_even_width(80, -0.1), "k must not be negative; got -0.1"),
            "sigma zero": (lambda: p0geo.break_even_width(80, 1.0, sigma=0), r"sigma must be in \(0, 1\]; got 0"),
            "sigma above one": (lambda: p0geo.break_even_width(80, 1.0, sigma=1.5), r"sigma must be in \(0, 1\]"),
            "negative x": (lambda: p0geo.break_even_width(80, 1.0, x=-0.01), "x must not be negative; got -0.01"),
            "floor alone fails the gate": (lambda: p0geo.break_even_width(80, 1.0, x=1.2),
                                           "no width pays: the floor alone gives a ratio of 0.773, above 0.75"),
            "halving a zero width": (lambda: p0geo.halving_ratio(0, 80, 1.0), "width must be positive"),
            "ladder with no band": (lambda: p0geo.smallest_cell(1000, 0, 300), "band must be positive"),
            "ladder with no base": (lambda: p0geo.smallest_cell(0, 80, 300), "base must be positive"),
        }
        for name, (call, message) in cases.items():
            with self.subTest(name), self.assertRaisesRegex(p0base.ModelError, message):
                call()

    def test_halving_ratio(self):
        cases = {
            "x 0, W 400": ((400, 80, 1.0), 0.70),
            "x 0.05, W 1000": ((1000, 80, 1.0, 0.05), 0.6000),
            "x 0.05, W 500": ((500, 80, 1.0, 0.05), 0.6762),
            "x 0.05, W 350": ((350, 80, 1.0, 0.05), 0.7415),
            "x 0.05, W 250": ((250, 80, 1.0, 0.05), 0.8286),
        }
        for name, (args, expected) in cases.items():
            with self.subTest(name):
                self.assertAlmostEqual(p0geo.halving_ratio(*args), expected, places=4)

    def test_break_even_width(self):
        self.assertAlmostEqual(p0geo.break_even_width(80, 1.0, 0.05), 336.8421053, places=6)
        self.assertAlmostEqual(p0geo.break_even_width(80, 1.0, 0.05), 4 * 80 / 0.95, places=9)

    def test_smallest_cell_boundaries(self):
        cases = {
            "B 125.0001 stays 250": ((250, 125.0001, 100), 250),
            "B exactly 125 splits 250": ((250, 125, 100), 125),
            "W* a hair over 500 still lets 500 split": ((500, 100, 500.00000000000006), 250),
            "W* above the base: no split": ((300, 80, 336.84), 300),
        }
        for name, (args, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0geo.smallest_cell(*args), expected)

    def test_section_4_3_table(self):
        expected = {  # (k, B): (W* to 0.1 m (the sheet rounds loosely: "210" for 210.5), s_min from a 1000 m base)
            (1.0, 50): (210.5, 125), (1.0, 80): (336.8, 250), (1.0, 120): (505.3, 500),
            (0.7, 50): (147.4, 125), (0.7, 80): (235.8, 125), (0.7, 120): (353.7, 250),
            (0.5, 50): (105.3, 62.5), (0.5, 80): (168.4, 125), (0.5, 120): (252.6, 250),
            (0.25, 50): (52.6, 62.5), (0.25, 80): (84.2, 125), (0.25, 120): (126.3, 125),
        }

        def up_the_ladder(value, base):
            width = base
            while width / 2 >= value:
                width /= 2
            return width

        for (k, band), (w_star, s_min) in expected.items():
            with self.subTest(f"k {k} B {band}"):
                computed = p0geo.break_even_width(band, k, 0.05)
                self.assertEqual(round(computed, 1), w_star)
                self.assertEqual(p0geo.smallest_cell(1000, band, computed), s_min)
                self.assertEqual(p0geo.smallest_cell(1000, band, computed), up_the_ladder(max(band, computed / 2), 1000))
        rows = p0geo.map_size_table([1000], [50, 80, 120], [1.0, 0.7, 0.5, 0.25], 0.05, base=1000)
        self.assertEqual({(r["k"], r["band"]): r["s_min"] for r in rows}, {key: v[1] for key, v in expected.items()})

    def test_split_inputs(self):
        fit_ci = {"c0": 36.0, "links": 7.2, "O_steered": 2.592, "U_kinematic": 1.296, "G_player": 0.2592}
        with self.assertRaisesRegex(p0base.ModelError, "^c0 at 30/0.02 needs a7's idle rows or block K's idle split$"):
            p0geo.split_inputs(fit_ci, "30/0.02")
        with self.assertRaisesRegex(p0base.ModelError, "positive owner cost"):
            p0geo.split_inputs(dict(fit_ci, O_steered=0.0), "60/0.03")
        with self.assertRaisesRegex(p0base.ModelError, "leaves no load under the 800 ms/s trigger"):
            p0geo.split_inputs(fit_ci, "30/0.02", floor={"c0": 800.0, "lam": 0.0})
        cases = {
            "parent_links 0": (dict(rates="60/0.03"), 764.0, (0.04712, 5), 0.009424, (131.1, 209.8, 314.7)),
            "parent_links 1": (dict(rates="60/0.03", parent_links=1), 756.8, (0.05708, 5), 0.00951,
                               (132.6, 212.2, 318.3)),
            "30/0.02 from a7's floor": (dict(rates="30/0.02", floor={"c0": 23.2, "lam": 7.2}), 776.8, (0.0299, 4),
                                        0.00927, None),
            "interval dicts": (dict(rates="60/0.03", fit_ci={k: {"value": v, "lo": 0.0, "hi": 9.0}
                                                             for k, v in fit_ci.items()}),
                               764.0, (0.04712, 5), 0.009424, None),
        }
        for name, (kwargs, variable, (x, places), link_share, widths) in cases.items():
            with self.subTest(name):
                inputs = p0geo.split_inputs(kwargs.pop("fit_ci", fit_ci), **kwargs)
                self.assertAlmostEqual(inputs["k_hold"], 0.5, places=9)
                self.assertAlmostEqual(inputs["k_band"], 0.6, places=9)
                self.assertAlmostEqual(inputs["V"], variable, places=9)
                self.assertAlmostEqual(inputs["x"], x, places=places)
                self.assertAlmostEqual(inputs["link_share"], link_share, places=5)
                if widths:
                    computed = [p0geo.break_even_width(b, inputs["k_band"], inputs["x"],
                                                       link_share=inputs["link_share"]) for b in (50, 80, 120)]
                    self.assertEqual([round(w, 1) for w in computed], list(widths))
                    self.assertEqual([p0geo.smallest_cell(1000, b, w) for b, w in zip((50, 80, 120), computed)],
                                     [125, 125, 250])


class LayoutTests(unittest.TestCase):
    def test_refusals(self):
        coef = {"c0": 0.0, "O_steered": 1.0}
        cases = {
            "monsters without counts": (lambda: p0geo.layout_loads(HALVES_CELLS, 80, 250, monsters=284),
                                        "measured per-cell monster counts"),
            "counts missing a cell": (lambda: p0geo.layout_loads(HALVES_CELLS, 80, 250, 284, monster_counts={
                "C": {"O_monster": 140, "U_monster": 20}}), "no entry for cell E"),
            "negative players": (lambda: p0geo.layout_loads(HALVES_CELLS, 80, -1), "players must not be negative"),
            "grid counts missing cells": (lambda: p0geo.monster_counts_from_grid({"C": 56}, STRIPS4, 80),
                                          "missing cells SW, S, SE, W, E, NW, N, NE"),
            "empty base prediction": (lambda: p0geo.predict_ratio(coef, STRIPS4, COARSE_CELLS, 80, 0, 0, "steered",
                                                                  "steered"), "predicted peak is 0"),
        }
        for name, (call, message) in cases.items():
            with self.subTest(name), self.assertRaisesRegex(p0base.ModelError, message):
                call()

    def test_layout_loads_halves_at_band_80(self):
        loads = p0geo.layout_loads(HALVES_CELLS, 80, 250, owner_motion="kinematic", puppet_motion="kinematic")
        for cell in ("C", "E"):
            with self.subTest(cell):
                load = loads[cell]
                self.assertAlmostEqual(load["O"], 125.0)
                self.assertAlmostEqual(load["U"], 250 * 80 * 600 / 360000)
                self.assertAlmostEqual(load["G"], 250 * 80 * 600 / 360000)
                self.assertEqual(load["links"], 1)
                self.assertAlmostEqual(load["Q"], p0geo.expected_local_pairs(125.0, HALVES_CELLS[0][1]))
                self.assertEqual(set(load["regressors"]), {"c0", "links", "O_kinematic", "U_kinematic", "G_player",
                                                           "O_monster", "U_monster_kinematic", "Q", "N2"})
        inert = p0geo.layout_loads(HALVES_CELLS, 80, 250, owner_motion="inert", puppet_motion="kinematic")["C"]
        self.assertEqual((inert["G"], inert["regressors"]["G_player"]), (0.0, 0.0))
        self.assertIn("U_inert", inert["regressors"])
        coarse = p0geo.layout_loads(COARSE_CELLS, 80, 250)["C"]
        self.assertEqual((coarse["U"], coarse["G"], coarse["links"]), (0.0, 0.0, 0))

    def test_layout_loads_strips4(self):
        loads = p0geo.layout_loads(STRIPS4, 80, 250)
        band_area = 80 * 600 / 360000 * 250
        self.assertEqual({cid: load["links"] for cid, load in loads.items()}, {"W": 1, "C": 2, "E": 2, "NE": 1})
        self.assertAlmostEqual(loads["W"]["U"], band_area)
        self.assertAlmostEqual(loads["C"]["U"], 2 * band_area)
        self.assertAlmostEqual(loads["C"]["G"], 2 * band_area)

    def test_monster_counts(self):
        grid = {"SW": 30, "S": 20, "SE": 30, "W": 40, "C": 56, "E": 40, "NW": 30, "N": 20, "NE": 30}
        counts = p0geo.monster_counts_from_grid(grid, STRIPS4, 80)
        expected = {"W": (75.0, 39.4), "C": (73.0, 78.4), "E": (73.0, 78.4), "NE": (75.0, 39.4)}
        for cid, (owned, puppets) in expected.items():
            with self.subTest(cid):
                self.assertAlmostEqual(counts[cid]["O_monster"], owned)
                self.assertAlmostEqual(counts[cid]["U_monster"], puppets)
        self.assertAlmostEqual(sum(c["O_monster"] for c in counts.values()), sum(grid.values()))
        loads = p0geo.layout_loads(STRIPS4, 80, 250, monsters=296, monster_counts=counts)
        self.assertEqual(loads["C"]["regressors"]["U_monster_steered"], counts["C"]["U_monster"])

    def test_predict_ratio_strips4_over_halves(self):
        players, owner = 250, 1.0
        cases = {"k 1": (1.0, 0.829), "k 0.5": (0.5, 0.703)}
        for name, (k, expected) in cases.items():
            with self.subTest(name):
                coef = {"c0": 0.05 * owner * players, "O_kinematic": owner, "U_kinematic": k * owner / 2,
                        "G_player": k * owner / 2}
                ratio = p0geo.predict_ratio(coef, STRIPS4, HALVES_CELLS, 80, players, 0, "kinematic", "kinematic")
                self.assertAlmostEqual(ratio, expected, places=3)

    def test_predict_ratio_with_monsters(self):
        coef = {"c0": 10.0, "O_steered": 1.0, "O_monster": 0.5}
        counts = ({"C": {"O_monster": 140, "U_monster": 0}, "E": {"O_monster": 143, "U_monster": 0}},
                  {"C": {"O_monster": 283, "U_monster": 0}})
        ratio = p0geo.predict_ratio(coef, HALVES_CELLS, COARSE_CELLS, 80, 200, 283, "steered", "steered", counts)
        self.assertAlmostEqual(ratio, (10 + 100 + 0.5 * 143) / (10 + 200 + 0.5 * 283))


class MapTests(unittest.TestCase):
    def test_refusals(self):
        cases = {
            "side not whole cells": (lambda: p0geo.total_cpu_ratio(1000, 300, 80, 1.0),
                                     "^the map side 1000 m is not a whole number of 300 m cells$"),
            "cell wider than the map": (lambda: p0geo.total_cpu_ratio(1000, 2000, 80, 1.0), "not a whole number"),
            "unknown edge form": (lambda: p0geo.total_cpu_ratio(1000, 250, 80, 1.0, edges="torus"),
                                  "edges must be 'map' or 'interior'"),
            "no load": (lambda: p0geo.total_cpu_ratio(1000, 250, 80, 1.0, load=0.0), "c0 \\+ load must be positive"),
            "crossings of no width": (lambda: p0geo.crossings_per_player(5, 0), "width must be positive"),
            "empty sides": (lambda: p0geo.map_size_table([], [80], [1.0], 0.05), "at least one map side"),
            "side off the base ladder": (lambda: p0geo.map_size_table([3000], [80], [1.0], 0.05, base=1000),
                                         "the map side 3000 m is not a power-of-two multiple of the 1000 m base"),
            "side under the base": (lambda: p0geo.map_size_table([500], [80], [1.0], 0.05, base=1000),
                                    "not a power-of-two multiple"),
            "a base narrower than the band": (lambda: p0geo.map_size_table([1000], [80], [1.0], 0.05, base=62.5),
                                              "the 62.5 m base is narrower than the band 80 m"),
        }
        for name, (call, message) in cases.items():
            with self.subTest(name), self.assertRaisesRegex(p0base.ModelError, message):
                call()

    def test_total_cpu_ratio(self):
        cases = {
            "interior k 1": (8000, 1.0, "interior", [1.3456, 1.7424, 2.6896, 5.1984]),
            "interior k 0.5": (8000, 0.5, "interior", [1.1728, 1.3712, 1.8448, 3.0992]),
            "map form, 1 km, k 1": (1000, 1.0, "map", [1.0, 1.3456, 2.1904, 4.4944]),
        }
        for name, (side, k, edges, expected) in cases.items():
            with self.subTest(name):
                computed = [p0geo.total_cpu_ratio(side, w, 80, k, edges=edges) for w in (1000, 500, 250, 125)]
                self.assertEqual([round(v, 4) for v in computed], expected)
        with_floor = p0geo.total_cpu_ratio(1000, 250, 80, 1.0, c0=36, load=600, lam=7.2)
        self.assertAlmostEqual(with_floor, (16 * 36 + 84 * 7.2 + 600 * 2.1904) / 636)

    def test_neighbour_links(self):
        cases = {
            "one cell has none (the formula alone gives 2 at cap 2)": ((1, 2), 0),
            "one cell, interior form": ((1, 8, "interior"), 8),
            "2 x 2": ((2,), 12),
            "4 x 4": ((4,), 84),
            "4 x 4 capped at 4": ((4, 4), 4 * 3 + 8 * 4 + 4 * 4),
        }
        for name, (args, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0geo.neighbour_links(*args), expected)

    def test_crossings_per_player(self):
        self.assertEqual([round(p0geo.crossings_per_player(5, w), 5) for w in (1000, 250, 125)],
                         [0.00637, 0.02546, 0.05093])
        self.assertAlmostEqual(300 * 0.3 * p0geo.crossings_per_player(5, 125), 4.584, places=3)

    def test_choose_base_refuses_a_width_that_is_not_positive(self):
        cases = {
            "W* 0": (lambda: p0geo.choose_base(1000, 0.0, 80), "no base follows from W"),
            "W* negative": (lambda: p0geo.choose_base(1000, -1.0, 80), "no base follows from W"),
            "map table at k 0 without a base": (lambda: p0geo.map_size_table([1000], [80], [0.0], 0.05),
                                                "no base follows from W"),
            "a map narrower than the band": (lambda: p0geo.choose_base(40, 10.0, 50),
                                             "the map side 40 m is narrower than the band 50 m"),
            "a cap no base at least B wide meets": (lambda: p0geo.choose_base(8000, 15.3, 50, 40),
                                                    "no base of the 8000 m side is at most 40 m and at least the band "
                                                    "50 m wide"),
        }
        for name, (call, message) in cases.items():
            with self.subTest(name):
                with self.assertRaisesRegex(p0base.ModelError, message):
                    call()
        row = p0geo.map_size_table([1000], [80], [0.0], 0.05, base=1000)[0]
        self.assertEqual((row["w_star"], row["base"], row["s_min"]), (0.0, 1000, 125.0))

    def test_choose_base(self):
        cases = {
            "map narrower than 2·W*": ((300, 336.84, 80), (300, False)),
            "1 km at k 1": ((1000, 336.84, 80), (1000, False)),
            "8 km at k 1": ((8000, 336.84, 80), (1000.0, False)),
            "8 km at k 0.7": ((8000, 235.79, 80), (500.0, False)),
            "population caps 1 km at 500 m": ((1000, 336.84, 80, 500), (500.0, True)),
            "a cap off the ladder snaps down": ((1000, 336.84, 80, 600), (500.0, True)),
            "a cap above the base changes nothing": ((1000, 336.84, 80, 2000), (1000, False)),
            "a small W* stops at the band": ((8000, 15.3, 50), (62.5, False)),
            "the band exactly on the ladder": ((8000, 15.3, 62.5), (62.5, False)),
        }
        for name, (args, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0geo.choose_base(*args), expected)

    def test_no_cell_is_narrower_than_the_band(self):
        x, link_share = 36 / 764, 7.2 / 764
        for k in (0.05, 0.07, 1e-9, 0.6):
            with self.subTest(k=k):
                for row in p0geo.map_size_table((300, 350, 500, 1000, 2000, 4000, 8000), (50, 80, 120), [k], x,
                                                link_share):
                    self.assertGreaterEqual(min(row["base"], row["s_min"]), row["band"], row)

    def test_map_size_table_worked_rows(self):
        dynamic, busy = "Dynamic", "Dynamic if busy"
        static_unless = "Static unless its peak exceeds one process"
        expected = {  # (k, side): (base, bases, s_min, halvings, cells per base, first ratio, mode)
            (1.0, 300): (300, 1, 300, 0, 1, 0.778, "Static"),
            (1.0, 350): (350, 1, 175, 1, 4, 0.741, static_unless),
            (1.0, 500): (500, 1, 250, 1, 4, 0.676, static_unless),
            (1.0, 1000): (1000, 1, 250, 2, 16, 0.600, busy),
            (1.0, 2000): (1000, 4, 250, 2, 16, 0.600, dynamic),
            (1.0, 4000): (1000, 16, 250, 2, 16, 0.600, dynamic),
            (1.0, 8000): (1000, 64, 250, 2, 16, 0.600, dynamic),
            (0.7, 300): (300, 1, 150, 1, 4, 0.702, static_unless),
            (0.7, 500): (500, 1, 125, 2, 16, 0.630, busy),
            (0.7, 1000): (500, 4, 125, 2, 16, 0.630, dynamic),
            (0.7, 8000): (500, 256, 125, 2, 16, 0.630, dynamic),
        }
        sides = sorted({side for _, side in expected})
        rows = {(r["k"], r["side"]): r for r in p0geo.map_size_table(sides, [80], [1.0, 0.7], 0.05)}
        for (k, side), values in expected.items():
            with self.subTest(f"k {k} side {side}"):
                row = rows[(k, side)]
                actual = (row["base"], row["bases"], row["s_min"], row["halvings"], row["max_cells_per_base"],
                          round(row["first_ratio"], 3), row["mode"])
                self.assertEqual(actual, values)
                self.assertEqual((row["band"], row["k_role"], row["crossings_per_s"], row["population_limited"]),
                                 (80, None, None, False))
                self.assertAlmostEqual(row["w_star"], p0geo.break_even_width(80, k, 0.05))
                self.assertAlmostEqual(row["total_cpu"], p0geo.total_cpu_ratio(side, row["s_min"], 80, k))

    def test_map_size_table_options(self):
        x, link_share = 36 / 764, 7.2 / 764
        with_links = p0geo.map_size_table([1000], [50, 80, 120], [0.6], x, link_share=link_share)
        without = p0geo.map_size_table([1000], [50, 80, 120], [0.6], x)
        self.assertEqual([round(r["w_star"], 1) for r in with_links], [131.1, 209.8, 314.7])
        self.assertEqual([round(r["w_star"], 1) for r in without], [125.9, 201.5, 302.2])
        self.assertEqual(p0geo.map_size_table([1000], [80], [1.0], 0.05, core_budget=10)[0]["max_cells_per_base"], 10)
        limited = p0geo.map_size_table([1000], [80], [1.0], 0.05, max_base=500)[0]
        self.assertEqual((limited["base"], limited["population_limited"], limited["bases"]), (500, True, 4))
        rows = p0geo.map_size_table([1000], [80], [(0.6, 0.5, 0.7)], 0.05)
        self.assertEqual([(r["k"], r["k_role"]) for r in rows], [(0.6, "estimate"), (0.5, "lo"), (0.7, "hi")])
        crowd = p0geo.map_size_table([500], [80], [0.7], 0.05, players=300)[0]
        self.assertAlmostEqual(crowd["crossings_per_s"], 300 * 0.3 * 4 * 5 / (math.pi * 125))
        floor = p0geo.map_size_table([1000], [80], [1.0], 0.05, c0=36, lam=7.2, load=764)[0]
        self.assertAlmostEqual(floor["total_cpu"], p0geo.total_cpu_ratio(1000, 250, 80, 1.0, 36, 764, 7.2))


class MeasuredLoadTests(unittest.TestCase):
    def test_refusals(self):
        halves_env = {"MESH_LAYOUT_OVERRIDE": HALVES}
        cases = {
            "read cell outside the layout": (run_row("presence", "main", "a", 1, {"W": (40, 0, 0)}, halves_env), "W",
                                             "are not all in the layout"),
            "no windows": (run_row("presence", "main", "a", 1, {"C": (40, 0, 0)}, halves_env, windows={}), "C",
                           "no unsaturated Work windows for cell C"),
            "only saturated windows": (run_row("presence", "main", "a", 1, {"C": (40, 0, 0)}, halves_env,
                                               windows={"C": [window(10, saturated=True)]}), "C",
                                       "no unsaturated Work windows"),
            "owners outside the crowd": (run_row("density", "grid", "base", 1, {"E": (40, 10, 0)},
                                                 {"MESH_LAYOUT_OVERRIDE": HALVES, "MESH_LOAD_AREA": "-150,0,50"}),
                                         "E", "owns 10 players outside MESH_LOAD_AREA"),
            "unreadable plan": (run_row("abgeo", "b9", "grid", 1, {"C": (40, 10, 0)}, {"MESH_LOAD_PLAN": "C=10"},
                                        started=[cid for cid, _ in p0geo.REGISTRY_GRID]), "C",
                                "MESH_LOAD_PLAN 'C=10' is not cell:count"),
        }
        for name, (row, cell, message) in cases.items():
            with self.subTest(name), self.assertRaisesRegex(p0base.ModelError, message):
                p0geo.cell_regressors(row, cell)
        with self.assertRaisesRegex(p0base.ModelError, "no frames_per_s mean for cell C"):
            p0geo.metric(run_row("rates", "k", "idle", 1, {"C": (40, 0, 0)}), "C", "frames_per_s")

    def test_cell_regressors(self):
        kinematic = {"MESH_LAYOUT_OVERRIDE": HALVES, "MESH_LOAD_MOTION": "kinematic", "MESH_PUPPET_MOTION": "kinematic"}
        halves = run_row("presence", "main", "puppets-kinematic", 1, {"C": (150, 200, 0), "E": (90, 0, 198)},
                         kinematic)
        inert = run_row("presence", "main", "owners-inert", 1, {"C": (150, 200, 0), "E": (90, 0, 200)},
                        dict(kinematic, MESH_LOAD_MOTION="inert"))
        nofight = run_row("density", "attribution", "no-fight", 1, {"C": (300, 200, 0)},
                          {"MESH_LAYOUT_OVERRIDE": COARSE, "MESH_LOAD_AREA": "0,0,70.71", "MESH_LOAD_FIGHT": "0"})
        solo = run_row("presence", "solo", "owners-steered", 1, {"C": (120, 100, 0)}, {"MESH_LAYOUT_OVERRIDE": HALVES})
        cases = {
            "halves C: owners streamed to E": (halves, "C", {"links": 1.0, "O_kinematic": 200.0, "U_kinematic": 0.0,
                                                             "G_player": 198.0}),
            "halves E: puppets only": (halves, "E", {"links": 1.0, "O_kinematic": 0.0, "U_kinematic": 198.0,
                                                     "G_player": 0.0}),
            "inert owners: U_inert, no G": (inert, "E", {"U_inert": 200.0, "G_player": 0.0}),
            "inert owners on C: no G": (inert, "C", {"O_inert": 200.0, "G_player": 0.0}),
            "solo: no links, no streaming": (solo, "C", {"links": 0.0, "O_steered": 100.0, "G_player": 0.0,
                                                         "U_steered": 0.0}),
            "no fight": (nofight, "C", {"O_steered_nofight": 200.0,
                                        "Q": p0geo.expected_local_pairs(200, (-70.71, -70.71, 141.42, 141.42))}),
        }
        for name, (row, cell, expected) in cases.items():
            with self.subTest(name):
                regressors, note = p0geo.cell_regressors(row, cell)
                self.assertIsNone(note)
                self.assertEqual(regressors["c0"], 1.0)
                for key, value in expected.items():
                    self.assertAlmostEqual(regressors[key], value, msg=key)
        regressors, _ = p0geo.cell_regressors(halves, "C")
        self.assertAlmostEqual(regressors["Q"], p0geo.expected_local_pairs(200, HALVES_CELLS[0][1]))
        self.assertEqual(regressors["N2"], 40000.0)

    def test_grid_streaming_comes_from_layout_loads(self):
        grid = [cid for cid, _ in p0geo.REGISTRY_GRID]
        plan = ",".join(f"{cid}:{n}" for cid, n in zip(grid, (28, 28, 28, 28, 28, 28, 28, 27, 27)))
        row = run_row("abgeo", "b9-main", "grid", 1, {"C": (200, 28, 40, 5, 9)},
                      {"MESH_LOAD_PLAN": plan, "MESH_BAND": "80", "MESH_PUPPET_MOTION": "kinematic"}, started=grid)
        regressors, note = p0geo.cell_regressors(row, "C")
        self.assertIn("layout_loads (E)", note)
        self.assertEqual(regressors["links"], 8.0)
        self.assertAlmostEqual(regressors["G_player"], p0geo.layout_loads(p0geo.REGISTRY_GRID, 80, 250)["C"]["G"])
        self.assertEqual((regressors["O_monster"], regressors["U_monster_kinematic"]), (5.0, 9.0))

    def test_window_mean_leaves_out_saturated_windows(self):
        row = run_row("presence", "solo", "owners-steered", 1, {"C": (120, 100, 0)}, {"MESH_LAYOUT_OVERRIDE": HALVES},
                      windows={"C": [window(100, seconds=5), window(300, seconds=5, saturated=True),
                                     window(110, seconds=15)]})
        self.assertAlmostEqual(p0geo.window_mean(row, "C", "owned_players"), (500 + 1650) / 20)
        self.assertEqual(p0geo.read_cells(row), ["C"])
        self.assertEqual(p0geo.metric(row, "C", "work_ms_per_s"), 120)


if __name__ == "__main__":
    unittest.main()
