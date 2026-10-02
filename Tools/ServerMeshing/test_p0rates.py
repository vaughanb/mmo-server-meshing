"""Unit tests for p0rates (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py")."""
import unittest

import p0base
import p0model
import p0rates
import test_p0geo
import test_p0model

assert_ci = test_p0model.assert_ci
RATE_NOISE = [0.4, -0.3, 0.2, -0.5, -0.2, 0.5, -0.1, 0.3]


def rate_points(repeats=(1, 2), fps_steps=((30, 0.03), (30, 0.02), (60, 0.03), (60, 0.02)), measured=None,
                noise=None, extra=None, per_frame=0.35, per_step=0.20, per_second=30.0, kit=1.2):
    """Block K points w = F·fps + Ph·steps + C at the measured rates (measured = {(fps, step): (fps, steps/s)});
    extra = {(fps, step): ms/s} injects an interaction."""
    points = []
    for repeat in repeats:
        for fps, step in fps_steps:
            f, p = (measured or {}).get((fps, step), (fps, 1.0 / step))
            w = per_frame * f + per_step * p + per_second + (extra or {}).get((fps, step), 0.0)
            points.append({"fps": f, "steps_per_s": p, "work_ms_per_s": w, "kit_mean_ms": kit, "rate": (fps, step),
                           "repeat": repeat, "capped": True})
    for point, e in zip(points, noise or []):
        point["work_ms_per_s"] += e
    return points


class RateTests(unittest.TestCase):
    def test_refusals(self):
        exact = rate_points()
        cases = {
            "only fps 60": ([p for p in exact if p["rate"][0] == 60],
                            "the rate decomposition needs two frame rates and two physics rates; got fps [60] and "
                            "physics steps/s [33.3, 50.0]"),
            "only one step": ([p for p in exact if p["rate"][1] == 0.02],
                              "the rate decomposition needs two frame rates and two physics rates; got fps [30, 60] "
                              "and physics steps/s [50.0]"),
            "an uncapped row": (exact[:-1] + [dict(exact[-1], capped=False)],
                                "the rate decomposition refuses uncapped rows (repeat 2 at 60/0.02)"),
            "a saturated row": (exact[:-1] + [dict(exact[-1], saturated=True)],
                                "the rate decomposition refuses saturated rows (repeat 2 at 60/0.02)"),
            "no MESH_RATES": (exact[:-1] + [dict(exact[-1], rate=(None, None))],
                              "the rate decomposition needs rows with MESH_RATES; repeat 2 has none"),
        }
        for name, (points, message) in cases.items():
            with self.subTest(name), self.assertRaises(p0base.ModelError) as caught:
                p0rates.rate_decomposition(points)
            self.assertEqual(str(caught.exception), message)

    def test_exact(self):
        points = rate_points()
        self.assertEqual([round(p["work_ms_per_s"], 4) for p in points[:4]], [47.1667, 50.5, 57.6667, 61.0])
        cases = {
            "nominal rates": rate_points(),
            "off-nominal measured rates": rate_points(measured={(60, 0.03): (59.8, 33.2), (30, 0.02): (29.9, 49.9)}),
        }
        for name, points in cases.items():
            with self.subTest(name):
                full, ci = p0rates.rate_decomposition(points)
                expected = {"per_frame": 0.35, "per_step": 0.20, "per_second": 30.0, "other_per_second": 6.0,
                            "interaction": 0.0}
                for key, value in expected.items():
                    self.assertAlmostEqual(ci[key]["value"], value, places=8, msg=key)
                self.assertEqual(full["warnings"], [])

    def test_noisy_two_repeats(self):
        full, ci = p0rates.rate_decomposition(rate_points(noise=RATE_NOISE))
        assert_ci(self, ci["per_frame"], 0.345833, 0.3141, 0.3776)
        assert_ci(self, ci["per_step"], 0.1955, -0.281, 0.672)
        assert_ci(self, ci["per_second"], 30.4125, 10.2417, 50.5833)
        self.assertEqual(ci["per_step"]["t"], 12.706)
        points = rate_points(noise=RATE_NOISE)
        rows = [{"per_frame": p["fps"], "per_step": p["steps_per_s"], "per_second": 1.0} for p in points]
        _, runs = p0model.jackknife(rows, [p["work_ms_per_s"] for p in points], list(p0rates.RATE_NAMES),
                                    list(range(8)), unit="run")
        self.assertEqual((round(runs["per_step"]["lo"], 4), round(runs["per_step"]["hi"], 4), runs["per_step"]["t"]),
                         (0.1458, 0.2452, 2.365))

    def test_interaction_is_flagged(self):
        full, ci = p0rates.rate_decomposition(rate_points(repeats=(1, 2, 3), extra={(60, 0.02): 5.0}))
        self.assertAlmostEqual(ci["interaction"]["value"], 5.0, places=8)
        self.assertEqual(len(full["warnings"]), 1)
        self.assertTrue(full["warnings"][0].startswith("non-additive rates: interaction 5.000 ms/s exceeds"))

    def test_the_interaction_needs_all_four_cells_in_every_fit(self):
        exact = rate_points(repeats=(1, 2, 3))
        cases = {
            "three rate cells": ([p for p in exact if p["rate"] != (60, 0.02)],
                                 "interaction not computed: the full fit has only [(30, 33.3), (30, 50.0), (60, 33.3)]"),
            "one repeat at a cell": ([p for p in exact if p["rate"] != (60, 0.02) or p["repeat"] == 1],
                                     "interaction not computed: the interaction contrast needs all four rate points; "
                                     "repeat 1 left out has [(30, 33.3), (30, 50.0), (60, 33.3)]"),
        }
        for name, (points, note) in cases.items():
            with self.subTest(name):
                full, ci = p0rates.rate_decomposition(points)
                for key, value in {"per_frame": 0.35, "per_step": 0.20, "per_second": 30.0,
                                   "other_per_second": 6.0}.items():
                    self.assertAlmostEqual(ci[key]["value"], value, places=8, msg=key)
                self.assertNotIn("interaction", ci)
                self.assertEqual(full["notes"], [note])
        self.assertEqual(p0rates.rate_decomposition(exact)[0]["notes"], [])

    def test_convert_rates(self):
        self.assertAlmostEqual(p0rates.convert_rates(173 / 3, 60, 100 / 3, 30, 50, 0.35, 0.20), 50.5, places=9)

    def test_rate_split(self):
        with self.assertRaisesRegex(p0base.ModelError, "positive entity count; got 0"):
            p0rates.rate_split(rate_points(), rate_points(), 0)
        noise = [0.1, -0.2, 0.3, 0.0, -0.1, 0.2, 0.1, -0.3, 0.0, 0.2, -0.1, 0.1]
        idle = rate_points(repeats=(1, 2, 3), noise=noise)
        kind = rate_points(repeats=(1, 2, 3), per_frame=0.35 + 200 * 0.001, per_step=0.20 + 200 * 0.0005,
                           per_second=30.0 + 200 * 0.02)
        full, ci = p0rates.rate_split(kind, idle, 200)
        self.assertEqual(len(ci["entity_per_frame"]["leave_one_out"]), 3)
        exact = p0rates.rate_split(rate_points(repeats=(1, 2, 3), per_frame=0.55, per_step=0.30, per_second=34.0),
                                   rate_points(repeats=(1, 2, 3)), 200)[1]
        for key, value in {"entity_per_frame": 0.001, "entity_per_step": 0.0005, "entity_per_second": 0.02}.items():
            self.assertAlmostEqual(exact[key]["value"], value, places=10)
        names = list(p0rates.RATE_NAMES) + [f"kind_{n}" for n in p0rates.RATE_NAMES]
        for index, repeat in enumerate((1, 2, 3)):
            with self.subTest(f"repeat {repeat} left out of both arms"):
                kept_idle = [p for p in idle if p["repeat"] != repeat]
                kept_kind = [p for p in kind if p["repeat"] != repeat]
                rows = [{"per_frame": p["fps"], "per_step": p["steps_per_s"], "per_second": 1.0} for p in kept_idle]
                rows += [{"per_frame": p["fps"], "per_step": p["steps_per_s"], "per_second": 1.0,
                          "kind_per_frame": p["fps"], "kind_per_step": p["steps_per_s"], "kind_per_second": 1.0}
                         for p in kept_kind]
                manual = p0model.least_squares(rows, [p["work_ms_per_s"] for p in kept_idle + kept_kind], names)
                self.assertAlmostEqual(ci["entity_per_frame"]["leave_one_out"][index],
                                       manual["coef"]["kind_per_frame"] / 200, places=12)

    def test_rate_points(self):
        def row(repeat, accepted=True):
            r = test_p0geo.run_row("rates", "k", "idle", repeat, {"C": (57.6, 0, 0)},
                                   rates={"fps": 60, "fixed_step_s": 0.03, "tick_hz": 20}, accepted=accepted)
            r["metrics"] += [{"name": name, "stat": "mean", "value": value, "cell": "C"}
                             for name, value in (("frames_per_s", 59.9), ("physics_steps_per_s", 33.4),
                                                 ("kit_sync_ms", 0.3), ("kit_aoi_ms", 0.05), ("kit_move_ms", 0.85))]
            return r
        points, notes = p0rates.rate_points([row(1), row(2, accepted=False), row(3, accepted=False)])
        self.assertEqual(notes, ["2 rows not accepted, left out"])
        self.assertEqual(len(points), 1)
        point = points[0]
        self.assertEqual((point["fps"], point["steps_per_s"], point["work_ms_per_s"], point["rate"], point["repeat"]),
                         (59.9, 33.4, 57.6, (60, 0.03), 1))
        self.assertAlmostEqual(point["kit_mean_ms"], 1.2)


if __name__ == "__main__":
    unittest.main()
