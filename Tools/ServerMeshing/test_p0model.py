"""Unit tests for p0model (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py")."""
import itertools
import math
import re
import unittest

import p0base
import p0geo
import p0model
import test_p0geo

HALVES, COARSE = test_p0geo.HALVES, test_p0geo.COARSE
TRUE = {"c0": 36.0, "links": 7.2, "O_steered": 2.592, "O_kinematic": 0.62, "O_inert": 0.98, "U_steered": 2.64,
        "U_kinematic": 1.296, "U_inert": 0.5, "G_player": 0.2592}
PRESENCE_ARMS = [  # (case, arm, env, points, C's owner regressor, E's puppet regressor)
    ("baseline", "idle", {}, [0], None, None),
    ("main", "puppets-steered", {"MESH_LOAD_MOTION": "kinematic"}, [100, 200, 250], "O_kinematic", "U_steered"),
    ("main", "puppets-kinematic", {"MESH_LOAD_MOTION": "kinematic", "MESH_PUPPET_MOTION": "kinematic"},
     [100, 200, 400], "O_kinematic", "U_kinematic"),
    ("main", "owners-inert", {"MESH_LOAD_MOTION": "inert", "MESH_PUPPET_MOTION": "kinematic"}, [100, 200, 400],
     "O_inert", "U_inert"),
    ("solo", "owners-steered", {}, [100, 200, 250], "O_steered", None),
    ("solo", "owners-kinematic", {"MESH_LOAD_MOTION": "kinematic"}, [100, 200, 400], "O_kinematic", None),
]
NAMES7 = ["c0", "links", "O_steered", "U_steered"]
ROWS7 = [{"c0": 1, "links": links, "O_steered": o, "U_steered": u}
         for links, o, u in [(0, 100, 0), (0, 200, 0), (0, 400, 0), (1, 0, 100), (1, 0, 200), (1, 0, 400), (1, 0, 0)]]
Y7 = [147.6, 277.2, 536.4, 153.6, 285.6, 549.6, 21.6]
NOISE21 = {1: (0.6, -0.4, 1.0, -0.8, 0.5, -0.2, 0.3), 2: (-0.5, 0.7, -0.9, 0.4, -0.6, 0.8, -0.1),
           3: (0.2, -0.3, 0.4, 0.6, 0.1, -0.7, -0.2)}


def presence_rows(repeats=(1, 2, 3), true=TRUE, noise=None, arms=None, **fields):
    """Presence run rows from the true coefficients: halves C (owners, streaming to E) and E (puppets), solo C.
    noise = {(repeat, arm, players): ms/s} added to C's work."""
    rows = []
    for repeat in repeats:
        for case, arm, env, points, owned, puppets in PRESENCE_ARMS:
            if arms is not None and arm not in arms:
                continue
            for n in points:
                extra = (noise or {}).get((repeat, arm, n), 0.0)
                full_env = dict(env, MESH_LAYOUT_OVERRIDE=HALVES, MESH_BAND="300")
                point = {"players": n} if n else {}
                if case == "solo":
                    loads = {"C": (true["c0"] + true[owned] * n + extra, n, 0)}
                else:
                    streamed = n if owned in ("O_kinematic", "O_steered") else 0
                    held = true["c0"] + true["links"]
                    work_c = held + (true[owned] * n if owned else 0.0) + true["G_player"] * streamed + extra
                    work_e = held + (true[puppets] * n if puppets else 0.0)
                    loads = {"C": (work_c, n, 0), "E": (work_e, 0, n)}
                rows.append(test_p0geo.run_row("presence", case, arm, repeat, loads, full_env, point=point, **fields))
    return rows


def density_rows(q_form="qQ", repeats=(1, 2, 3), c0=31.4, owner=2.592, q=0.004):
    """Density base rows (grid and scale) on the coarse cell, work = c0 + a·N + q·Q (or q·N²), plus rows every
    density fit must ignore (attribution, sparse, bridge)."""
    points = [("grid", n, side) for n in (100, 200) for side in (100, 200, 400, 600)]
    points += [("scale", 50, 70.71), ("scale", 200, 141.42)]
    rows = []
    for repeat in repeats:
        for case, n, side in points:
            area = "" if side == 600 else f"0,0,{side / 2:g}"
            place = p0geo.crowd_rect(area) or (-300.0, -300.0, 600.0, 600.0)
            pairs = p0geo.expected_local_pairs(n, place) if q_form == "qQ" else n * n
            env = {"MESH_LAYOUT_OVERRIDE": COARSE, "MESH_LOAD_AREA": area}
            rows.append(test_p0geo.run_row("density", case, "base", repeat, {"C": (c0 + owner * n + q * pairs, n, 0)},
                                           env, point={"players": n, "side": side}))
        for case, arm in (("attribution", "inert"), ("sparse", "inert"), ("bridge-0.53", "bridge")):
            rows.append(test_p0geo.run_row("density", case, arm, repeat, {"C": (999.0, 200, 0)},
                                           {"MESH_LAYOUT_OVERRIDE": COARSE}, point={"players": 200}))
    return rows


def floor_rows(noise=((0.2, -0.4), (-0.2, 0.4))):
    """a7's idle rows: coarse C alone (no links) and C of the registry grid (8 links), 2 repeats."""
    grid = [cid for cid, _ in p0geo.REGISTRY_GRID]
    rows = []
    for repeat, (cell_noise, linked_noise) in enumerate(noise, start=1):
        rows.append(test_p0geo.run_row("floor", "cell", "idle", repeat, {"C": (31.4 + cell_noise, 0, 0)},
                                       {"MESH_LAYOUT_OVERRIDE": COARSE}))
        rows.append(test_p0geo.run_row("floor", "linked", "idle", repeat, {"C": (31.4 + 8 * 7.2 + linked_noise, 0, 0)},
                                       {}, started=grid))
    return rows


def assert_ci(case, ci, value, lo, hi, places=4):
    case.assertAlmostEqual(ci["value"], value, places=places)
    case.assertAlmostEqual(ci["lo"], lo, places=places)
    case.assertAlmostEqual(ci["hi"], hi, places=places)


class LeastSquaresTests(unittest.TestCase):
    def test_refusals(self):
        dependent = [{"c0": 1, "O_steered": s, "O_kinematic": k, "O_total": s + k}
                     for s, k in [(100, 0), (200, 0), (0, 100), (0, 200), (100, 100), (50, 150)]]
        linked = [{"c0": 1, "links": 1, "U_steered": u} for u in (100, 200, 400)] + [{"c0": 1, "links": 1}]
        cases = {
            "no rows": (([], [], NAMES7), "0 rows for 4 regressors (c0, links, O_steered, U_steered): need at least 5"),
            "rows = regressors": ((ROWS7[:4], Y7[:4], NAMES7),
                                  "4 rows for 4 regressors (c0, links, O_steered, U_steered): need at least 5"),
            "mismatched y": ((ROWS7, Y7[:6], NAMES7), "7 rows but 6 responses"),
            "zero column": ((ROWS7, Y7, NAMES7 + ["U_kinematic"]),
                            "singular design: regressor 'U_kinematic' is zero in every row"),
            "links = c0": ((linked, [1.0] * 4, ["c0", "links", "U_steered"]),
                           "singular design: regressor 'links' equals 1 x 'c0' in every row (drop one of them, or add "
                           "runs where they differ)"),
            "L = 10·U": (([dict(r, L=10 * r["U_steered"]) for r in ROWS7], Y7, NAMES7 + ["L"]),
                         "singular design: regressor 'L' equals 10 x 'U_steered' in every row (drop one of them, or "
                         "add runs where they differ)"),
            "O_total = O_steered + O_kinematic": ((dependent, [1.0] * 6, ["c0", "O_steered", "O_kinematic", "O_total"]),
                                                  "singular design: regressor 'O_total' equals 1 x 'O_steered' + 1 x "
                                                  "'O_kinematic' in every row (drop one of them, or add runs where "
                                                  "they differ)"),
        }
        for name, (args, message) in cases.items():
            with self.subTest(name), self.assertRaises(p0base.ModelError) as caught:
                p0model.least_squares(*args)
            self.assertEqual(str(caught.exception), message)

    def test_exact_recovery(self):
        fit = p0model.least_squares(ROWS7, Y7, NAMES7)
        for name, value in {"c0": 18.0, "links": 3.6, "O_steered": 1.296, "U_steered": 1.32}.items():
            self.assertAlmostEqual(fit["coef"][name], value, places=9)
        self.assertAlmostEqual(fit["coef"]["U_steered"] / fit["coef"]["O_steered"], 1.0185185, places=7)
        self.assertAlmostEqual(fit["min_sine"], 0.308607, places=6)
        self.assertEqual((fit["n"], fit["p"], fit["df"], fit["warnings"]), (7, 4, 3, []))
        self.assertAlmostEqual(fit["rms"], 0.0, places=9)
        self.assertAlmostEqual(fit["r2"], 1.0, places=12)

    def test_order_and_scale_invariance(self):
        base = p0model.least_squares(ROWS7, Y7, NAMES7)
        for order in itertools.permutations(NAMES7):
            with self.subTest(order):
                fit = p0model.least_squares(ROWS7, Y7, list(order))
                self.assertAlmostEqual(fit["min_sine"], 0.308607, places=6)
                for name in NAMES7:
                    self.assertAlmostEqual(fit["coef"][name], base["coef"][name], places=9)
        scaled = p0model.least_squares([dict(r, O_steered=1000 * r["O_steered"]) for r in ROWS7], Y7, NAMES7)
        self.assertAlmostEqual(scaled["coef"]["O_steered"], 0.001296, places=12)
        self.assertAlmostEqual(scaled["min_sine"], 0.308607, places=6)

    def test_near_singular_design_warns(self):
        rows = [{"c0": 1, "a": n, "b": n + (1e-4 if i % 2 else 0)} for i, n in enumerate((100, 200, 300, 400, 500, 600))]
        fit = p0model.least_squares(rows, [1.0 + 2 * r["a"] for r in rows], ["c0", "a", "b"])
        self.assertLess(fit["min_sine"], 1e-3)
        self.assertEqual(len(fit["warnings"]), 1)
        self.assertIn("near-singular design", fit["warnings"][0])


class JackknifeTests(unittest.TestCase):
    def test_refusals(self):
        singular = [{"c0": 1, "x": 1}, {"c0": 1, "x": 0}, {"c0": 1, "x": 0}, {"c0": 1, "x": 0}]
        ratio_rows = [{"O_steered": 1}] * 3 + [{"U_x": 1}] * 3
        cases = {
            "one repeat": (lambda: p0model.jackknife(ROWS7, Y7, NAMES7, [1] * 7),
                           "a jackknife over repeats needs at least 2 repeats; got 1"),
            "labels of another length": (lambda: p0model.jackknife(ROWS7, Y7, NAMES7, [1, 2]),
                                         "7 rows but 2 group labels"),
            "singular deletion": (lambda: p0model.jackknife(singular, [1, 2, 3, 4], ["c0", "x"], [1, 2, 2, 3]),
                                  "leaving out repeat 1: singular design: regressor 'x' is zero in every row"),
            "denominator <= 0 in a deletion": (lambda: p0model.jackknife(
                ratio_rows, [-0.07, 0.05, 0.05, 0.5, 0.5, 0.5], ["O_steered", "U_x"], [1, 2, 3, 1, 2, 3],
                {"k": p0model.ratio_of("U_x", "O_steered")}),
                "ratio undefined: denominator O_steered = -0.01 ms/s in repeat 2 left out"),
        }
        for name, (call, message) in cases.items():
            with self.subTest(name), self.assertRaises(p0base.ModelError) as caught:
                call()
            self.assertEqual(str(caught.exception), message)

    def test_intercept_only(self):
        _, ci = p0model.jackknife([{"c0": 1}] * 3, [10.0, 12.0, 14.0], ["c0"], [1, 2, 3])
        assert_ci(self, ci["c0"], 12.0, 7.0313, 16.9687)
        self.assertEqual(ci["c0"]["t"], 4.303)

    def test_two_groups(self):
        _, ci = p0model.jackknife([{"c0": 1}] * 2, [10.0, 12.0], ["c0"], [1, 2])
        self.assertAlmostEqual(ci["c0"]["se"], abs(12.0 - 10.0) / 2)
        self.assertEqual(ci["c0"]["t"], 12.706)

    def test_noisy_21_rows(self):
        rows, y, groups = [], [], []
        for repeat, noise in NOISE21.items():
            rows += ROWS7
            y += [v + e for v, e in zip(Y7, noise)]
            groups += [repeat] * 7
        _, ci = p0model.jackknife(rows, y, NAMES7, groups, {"k": p0model.ratio_of("U_steered", "O_steered")})
        assert_ci(self, ci["c0"], 18.016667, 17.403919, 18.629414, places=6)
        assert_ci(self, ci["k"], 1.018165, 1.010638, 1.025692, places=6)
        self.assertEqual([round(v, 6) for v in ci["k"]["leave_one_out"]], [1.018941, 1.01642, 1.019137])

    def test_ratio_of_sums(self):
        ratio = p0model.ratio_of(("U_kinematic", "G_player"), "O_steered")
        self.assertAlmostEqual(ratio({"U_kinematic": 1.296, "G_player": 0.2592, "O_steered": 2.592}, "the full fit"),
                               0.6)


class GroupTests(unittest.TestCase):
    def test_run_groups(self):
        balanced = presence_rows(repeats=(1, 2), arms=["idle", "owners-steered"])
        third = test_p0geo.run_row("presence", "baseline", "idle", 3, {"C": (43.2, 0, 0)}, replaces_repeat=2)
        three = presence_rows(arms=["idle", "owners-steered"])
        cases = {
            "one design point is always balanced": (balanced[:1] + balanced[4:5] + [dict(third, replaces_repeat=None)],
                                                    ([1, 2, 3], "repeat", False)),
            "balanced": (balanced, ([1] * 4 + [2] * 4, "repeat", False)),
            "a third repeat joins the group it replaces": (balanced + [third], ([1] * 4 + [2] * 5, "repeat", False)),
            "a third repeat without replaces_repeat is unbalanced": (balanced + [dict(third, replaces_repeat=None)],
                                                                     (list(range(9)), "run", True)),
            "a lost run keeps the repeat groups": (three[:5] + three[6:], ([1] * 4 + [2] * 3 + [3] * 4, "repeat", True)),
        }
        for name, (rows, (labels_expected, unit_expected, noted)) in cases.items():
            with self.subTest(name):
                labels, unit, notes = p0model.run_groups(rows)
                self.assertEqual((labels, unit, bool(notes)), (labels_expected, unit_expected, noted))

    def test_a_run_lost_at_one_point_keeps_repeat_groups(self):
        rows = presence_rows()
        lost = next(r for r in rows if (r["arm"], r["repeat"], r["point"].get("players")) == ("puppets-steered", 2, 250))
        lost["accepted"] = False
        fit = p0model.fit(rows, "presence")
        self.assertEqual((fit["method"], fit["groups"], fit["ci"]["O_steered"]["t"]), (p0model.METHOD_REPEATS, 3, 4.303))
        self.assertEqual(fit["notes"][-1], 'runs missing (delete-one-repeat groups kept; a missing run\'s group is '
                                           'left out without it): presence main puppets-steered {"players": 250} has '
                                           '[1, 3]')

    def test_select_rows(self):
        def row(family, case, arm, **fields):
            return test_p0geo.run_row(family, case, arm, 1, {"C": (40.0, 0, 0)}, **fields)
        rows = [row("presence", "baseline", "idle"), row("presence", "main", "puppets-steered"),
                row("presence", "solo", "owners-steered"), row("presence", "additivity", "puppets-steered"),
                row("presence", "animation-off", "puppets-steered"), row("density", "grid", "base"),
                row("density", "scale", "base"), row("density", "attribution", "monsters-on"),
                row("density", "attribution", "inert"), row("density", "sparse", "inert"),
                row("density", "bridge-0.53", "bridge"), row("abgeo", "b9-main", "halves"),
                row("floor", "cell", "idle"), row("floor", "linked", "idle"), row("floor", "cell", "players-150"),
                row("presence", "main", "puppets-kinematic", **{"row": "summary"}),
                row("presence", "main", "owners-inert", rates={"fps": 30, "fixed_step_s": 0.02, "tick_hz": 20})]

        def names(chosen):
            return [f"{r['family']} {r['case']} {r['arm']}" for r in chosen]
        cases = {
            "presence": (("presence", None), ["presence baseline idle", "presence main puppets-steered",
                                              "presence solo owners-steered", "presence main owners-inert"]),
            "presence at 60/0.03": (("presence", "60/0.03"), ["presence baseline idle", "presence main puppets-steered",
                                                              "presence solo owners-steered"]),
            "density": (("density", None), ["density grid base", "density scale base"]),
            "refit": (("refit", "60/0.03"), ["presence baseline idle", "presence main puppets-steered",
                                             "presence solo owners-steered", "density grid base", "density scale base",
                                             "density attribution monsters-on", "abgeo b9-main halves"]),
            "floor": (("floor", None), ["floor cell idle", "floor linked idle"]),
            "additivity": (("additivity", None), ["presence additivity puppets-steered"]),
        }
        for name, ((fit_id, rates), expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(names(p0model.select_rows(rows, fit_id, rates)), expected)

    def test_fit_columns(self):
        cases = {
            "presence": (("presence", None), 7, ("O_inert", "U_inert")),
            "presence with Q": (("presence", "qQ"), 7, ("O_inert", "U_inert", "Q")),
            "presence with N²": (("presence", "qN2"), 7, ("O_inert", "U_inert", "N2")),
            "density picks N²": (("density", "qN2"), 3, ("O_kinematic", "O_inert", "O_monster")),
        }
        for name, (args, required, optional) in cases.items():
            with self.subTest(name):
                columns = p0model.fit_columns(*args)
                self.assertEqual((len(columns[0]), columns[1]), (required, optional))
        self.assertEqual(p0model.fit_columns("density", "qN2")[0], ("c0", "O_steered", "N2"))
        for args, message in ((("bogus",), "unknown fit 'bogus'"), (("density", "q"), "q_form must be")):
            with self.subTest(message), self.assertRaisesRegex(p0base.ModelError, message):
                p0model.fit_columns(*args)


class FitTests(unittest.TestCase):
    def test_refusals(self):
        rows = presence_rows()
        monsters = test_p0geo.run_row("presence", "main", "puppets-steered", 1, {"C": (99.0, 100, 0, 30, 0)},
                                      {"MESH_LAYOUT_OVERRIDE": HALVES, "MESH_LOAD_MOTION": "kinematic"},
                                      started=["C", "E"], point={"players": 100})
        cases = {
            "no accepted run": ([dict(r, accepted=False) for r in rows], "the presence rows have no accepted run"),
            "two rate sets": (rows + presence_rows(arms=["idle"], rates={"fps": 30, "fixed_step_s": 0.02}),
                              "rows from two rate sets (60/0.03 and 30/0.02): fit one rate set at a time and convert "
                              "with block K"),
            "two hosts": (rows + presence_rows(arms=["idle"], host="B"),
                          "rows from two hosts (None and B): fit one machine at a time"),
            "two CPUs": (rows + presence_rows(arms=["idle"], cpu="Other CPU"),
                         "rows from two CPUs (Test CPU and Other CPU): fit one CPU at a time"),
            "two builds": (rows + presence_rows(arms=["idle"], build="b2"),
                           "rows from two builds (b1 and b2): pool builds only when report --bridge passes"),
            "an uncapped row": (rows + presence_rows(repeats=(1,), arms=["idle"], capped=False),
                                "uncapped rows are not work at a rate set"),
            "a no-fight row": (rows + [dict(rows[-1], env=dict(rows[-1]["env"], MESH_LOAD_FIGHT="0"))],
                               "MESH_LOAD_FIGHT=0 rows need an O_steered_nofight regressor"),
            "no kinematic puppets": (presence_rows(arms=["idle", "puppets-steered", "owners-inert", "owners-steered",
                                                         "owners-kinematic"]),
                                     "the presence fit needs regressor 'U_kinematic', which is zero in every selected "
                                     "row"),
            "a load the fit does not model": (rows + [monsters], "has O_monster, which the presence fit does not model"),
        }
        for name, (run_rows, message) in cases.items():
            with self.subTest(name), self.assertRaisesRegex(p0base.ModelError, re.escape(message)):
                p0model.fit(run_rows, "presence")

    def test_presence_exact(self):
        fit = p0model.fit(presence_rows(), "presence")
        ci = fit["ci"]
        for name, value in TRUE.items():
            with self.subTest(name):
                self.assertAlmostEqual(ci[name]["value"], value, places=8)
                self.assertAlmostEqual(ci[name]["se"], 0.0, places=8)
        expected_ks = {"k_hold_kinematic": 0.5, "k_band_kinematic": 0.6, "k_hold_steered": 2.64 / 2.592,
                       "k_band_steered": (2.64 + 0.2592) / 2.592, "k_hold_inert": 0.5 / 2.592}
        for name, value in expected_ks.items():
            with self.subTest(name):
                self.assertAlmostEqual(ci[name]["value"], value, places=8)
                self.assertFalse(ci[name]["unbounded"])
        self.assertNotIn("k_band_inert", ci)
        summary = {key: fit[key] for key in ("method", "n", "runs", "groups", "rates", "reference", "runs_ci", "notes")}
        self.assertEqual(summary, {"method": "ols + jackknife over repeats", "n": 78, "runs": 48, "groups": 3,
                                   "rates": "60/0.03", "reference": "O_steered", "runs_ci": None, "notes": []})

    def test_presence_ignores_rows_it_must_not_pool(self):
        rows = presence_rows()
        extra = [dict(r, case="animation-off", plan_key="anim") for r in rows[:5]]
        extra += [dict(r, case="additivity", plan_key="add") for r in rows[5:10]]
        extra += [dict(rows[3], accepted=False, plan_key="failed"), dict(rows[4], row="summary")]
        fit = p0model.fit(rows + extra, "presence")
        self.assertEqual(fit["runs"], 48)
        self.assertEqual(fit["notes"], ["1 selected rows not accepted, left out"])
        self.assertAlmostEqual(fit["ci"]["k_hold_kinematic"]["value"], 0.5, places=9)

    def test_presence_drops_all_zero_optional_columns(self):
        fit = p0model.fit(presence_rows(arms=["idle", "puppets-steered", "puppets-kinematic", "owners-steered",
                                              "owners-kinematic"]), "presence")
        self.assertEqual(fit["notes"], ["regressor 'O_inert' is zero in every row: dropped",
                                        "regressor 'U_inert' is zero in every row: dropped"])
        self.assertNotIn("O_inert", fit["names"])

    def test_presence_with_q(self):
        fit = p0model.fit(presence_rows(), "presence", q_form="qQ")
        self.assertIn("Q", fit["names"])
        self.assertAlmostEqual(fit["ci"]["Q"]["value"], 0.0, places=8)
        self.assertAlmostEqual(fit["ci"]["k_band_kinematic"]["value"], 0.6, places=8)

    def test_balanced_groups_with_a_third_repeat(self):
        noise = {(1, "owners-steered", 250): 0.3, (2, "owners-steered", 250): -0.3}
        third = presence_rows(repeats=(3,), arms=["puppets-kinematic"], replaces_repeat=2)[1]
        fit = p0model.fit(presence_rows(repeats=(1, 2), noise=noise) + [third], "presence")
        self.assertEqual((fit["groups"], fit["method"], fit["runs_label"]),
                         (2, "ols + jackknife over repeats", "runs as groups (assumes no between-repeat drift)"))
        self.assertEqual(fit["ci"]["O_steered"]["t"], 12.706)
        self.assertEqual(len(fit["runs_ci"]["O_steered"]["leave_one_out"]), 33)
        self.assertEqual(fit["runs_ci"]["O_steered"]["t"], 2.042)
        self.assertIn("k_hold_kinematic", fit["runs_ci"])

    def test_unbalanced_repeats_use_runs(self):
        rows = presence_rows(repeats=(1, 2)) + presence_rows(repeats=(3,), arms=["idle"])
        fit = p0model.fit(rows, "presence")
        self.assertEqual((fit["method"], fit["groups"], fit["runs_ci"]),
                         ("ols + jackknife over runs (unbalanced repeats)", 33, None))
        self.assertEqual(fit["notes"], ['unbalanced repeats (most points have [1, 2]): presence baseline idle {} has '
                                        '[1, 2, 3]; jackknife over runs'])

    def test_k_unbounded(self):
        weak = dict(TRUE, O_steered=0.004)
        cases = {
            "denominator interval includes 0": ({(1, "owners-steered", 250): 0.9, (2, "owners-steered", 250): -0.3,
                                                 (3, "owners-steered", 250): -0.1}, True),
            "denominator <= 0 in a deletion": ({(1, "owners-steered", 250): 3.0, (2, "owners-steered", 250): -2.0,
                                                (3, "owners-steered", 250): -2.0}, None),
        }
        for name, (noise, expected) in cases.items():
            with self.subTest(name):
                fit = p0model.fit(presence_rows(true=weak, noise=noise), "presence")
                k = fit["ci"]["k_hold_kinematic"]
                self.assertTrue(k["unbounded"])
                self.assertEqual(p0base.ci_gate(k, 0.5), p0base.NO_VERDICT)
                if expected is None:
                    self.assertIsNone(k["value"])
                    self.assertRegex(fit["notes"][-1], r"^k unbounded: ratio undefined: denominator O_steered = "
                                                       r"-[\d.e-]+ ms/s in repeat \d left out$")
                else:
                    self.assertGreater(min(fit["ci"]["O_steered"]["leave_one_out"]), 0)
                    self.assertLessEqual(fit["ci"]["O_steered"]["lo"], 0)

    def test_bridged_builds_pool(self):
        fit = p0model.fit(presence_rows(repeats=(1, 2)) + presence_rows(repeats=(3,), build="b2"), "presence",
                          bridged=True)
        self.assertEqual(fit["build"], "b1, b2")

    def test_density_and_n2_form(self):
        rows = density_rows()
        fit = p0model.fit(rows, "density")
        self.assertEqual(fit["names"], ["c0", "O_steered", "Q"])
        self.assertEqual(fit["runs"], 30)
        for name, value in {"c0": 31.4, "O_steered": 2.592, "Q": 0.004}.items():
            self.assertAlmostEqual(fit["ci"][name]["value"], value, places=8)
        cases = {"q·Q data": ("qQ", "qQ", "qN2"), "q·N² data": ("qN2", "qN2", "qQ")}
        for name, (generated, chosen, other) in cases.items():
            with self.subTest(name):
                form = p0model.n2_form(density_rows(q_form=generated))
                self.assertEqual((form["form"], form["other"]["form"]), (chosen, other))
                self.assertLess(form["aic"], form["other"]["aic"])
        n2 = p0model.fit(density_rows(q_form="qN2"), "density", q_form="qN2")
        self.assertAlmostEqual(n2["ci"]["N2"]["value"], 0.004, places=8)

    def test_n2_form_ties_keep_q(self):
        rows = [dict(r, windows={"C": [test_p0geo.window(r["point"]["players"], seconds=60.0)]})
                for r in density_rows()]
        form = p0model.n2_form([dict(r, metrics=[dict(r["metrics"][0], value=5.0)]) for r in rows])
        self.assertEqual(form["form"], "qQ")

    def test_floor_two_repeats(self):
        fit = p0model.fit(floor_rows(), "floor")
        self.assertEqual(fit["names"], ["c0", "links"])
        self.assertAlmostEqual(fit["ci"]["c0"]["value"], 31.4)
        self.assertAlmostEqual(fit["ci"]["links"]["value"], 7.2)
        self.assertAlmostEqual(fit["ci"]["links"]["se"], abs(7.275 - 7.125) / 2)
        self.assertEqual(fit["ci"]["links"]["t"], 12.706)
        self.assertEqual((fit["runs_label"], fit["runs_ci"]["links"]["t"]),
                         ("runs as groups (assumes no between-repeat drift)", 3.182))


class ComparisonTests(unittest.TestCase):
    def test_predict(self):
        fit = p0model.fit(presence_rows(), "presence")
        prediction = p0model.predict(fit, {"c0": 1.0, "links": 1.0, "O_kinematic": 100.0, "G_player": 100.0})
        self.assertAlmostEqual(prediction["value"], 36 + 7.2 + 62 + 25.92)
        self.assertAlmostEqual(prediction["hi"] - prediction["lo"], 0.0)
        with self.assertRaisesRegex(p0base.ModelError, "the presence fit does not model O_monster"):
            p0model.predict(fit, {"c0": 1.0, "O_monster": 5.0})

    def test_additivity_residual(self):
        fit = p0model.fit(presence_rows(), "presence")
        rows = []
        for repeat in (1, 2, 3):
            for arm, env, n in (("puppets-steered", {}, 100), ("puppets-kinematic", {"MESH_PUPPET_MOTION": "kinematic"},
                                                                 400)):
                puppet = TRUE["U_kinematic" if env else "U_steered"]
                work = 36 + 7.2 + 0.62 * n + puppet * n + 0.2592 * n
                rows.append(test_p0geo.run_row(
                    "presence", "additivity", arm, repeat, {"C": (work + 5.0, n, n), "E": (work, n, n)},
                    dict(env, MESH_LAYOUT_OVERRIDE=HALVES, MESH_LOAD_MOTION="kinematic"), point={"players": n}))
        residuals = {(r["arm"], r["cell"]): r for r in p0model.additivity_residual(fit, rows + presence_rows())}
        self.assertEqual(len(residuals), 4)
        for arm in ("puppets-steered", "puppets-kinematic"):
            for cell, expected in (("C", 5.0), ("E", 0.0)):
                with self.subTest(f"{arm} {cell}"):
                    r = residuals[(arm, cell)]
                    self.assertEqual(r["runs"], 3)
                    self.assertAlmostEqual(r["residual"], expected, places=8)
                    self.assertAlmostEqual(r["lo"], expected, places=8)
                    self.assertAlmostEqual(r["measured"] - r["predicted"], expected, places=8)

    def test_arm_difference(self):
        main = presence_rows(arms=["puppets-steered"])
        main = [r for r in main if r["point"]["players"] == 200]
        off = [dict(r, case="animation-off", metrics=[dict(m, value=m["value"] - d) for m in r["metrics"]])
               for r, d in zip(main, (2.0, 3.0, 4.0))]
        result = p0model.arm_difference(main, off)
        self.assertEqual(sorted(result), ["C", "E"])
        assert_ci(self, result["C"], 3.0, 3.0 - 4.303 * math.sqrt(1 / 3), 3.0 + 4.303 * math.sqrt(1 / 3))
        self.assertEqual(result["C"]["repeats"], [1, 2, 3])
        with self.assertRaisesRegex(p0base.ModelError, "arm difference on cell C: "):
            p0model.arm_difference(main[:1], off[:1])


class N2AndBandTests(unittest.TestCase):
    def test_n2_removed(self):
        with self.assertRaisesRegex(p0base.ModelError, "no excess over linear to remove"):
            p0model.n2_removed(100.0, 90.0, 100.0, 95.0)
        self.assertAlmostEqual(p0model.n2_removed(300.0, 220.0, 200.0, 190.0), 0.7)
        self.assertAlmostEqual(p0model.n2_removed(300.0, 210.0, 200.0, 190.0), 0.8)

    def test_band_slope(self):
        noise = {1: 0.01, 2: -0.01, 3: 0.0}
        points = [(repeat, band, 0.555 + 1.0 * band / 600 + noise[repeat] * band / 150)
                  for repeat in (1, 2, 3) for band in (20, 40, 80, 150)]
        full, ci = p0model.band_slope(points)
        self.assertAlmostEqual(ci["slope"]["value"], 1.0, places=9)
        self.assertAlmostEqual(ci["intercept"]["value"], 0.555, places=9)
        self.assertEqual(ci["slope"]["t"], 4.303)
        self.assertGreater(ci["slope"]["hi"] - ci["slope"]["lo"], 0)
        self.assertEqual(p0base.geo_slope_verdict(1.0, ci["slope"]["lo"], ci["slope"]["hi"]), p0base.PASS)


if __name__ == "__main__":
    unittest.main()
