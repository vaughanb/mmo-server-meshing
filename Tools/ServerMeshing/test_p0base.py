"""Unit tests for p0base (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py")."""
import unittest

import p0base


class T975Tests(unittest.TestCase):
    def test_refuses_no_degrees_of_freedom(self):
        cases = {"zero": 0, "negative": -3}
        for name, df in cases.items():
            with self.subTest(name), self.assertRaisesRegex(p0base.ModelError, "at least 1 degree of freedom"):
                p0base.t975(df)

    def test_table(self):
        cases = {
            "1": (1, 12.706), "2": (2, 4.303), "3": (3, 3.182), "4": (4, 2.776), "7": (7, 2.365), "10": (10, 2.228),
            "30": (30, 2.042), "35 takes 30": (35, 2.042), "40": (40, 2.021), "45 takes 40": (45, 2.021),
            "60": (60, 2.000), "100 takes 60": (100, 2.000), "120": (120, 1.980), "121": (121, 1.980),
            "500 stays at 1.980, never 1.960": (500, 1.980),
        }
        for name, (df, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0base.t975(df), expected)


class ComparisonTests(unittest.TestCase):
    def test_at_most(self):
        cases = {
            "above by more than EPS": (0.05 + 1e-7, 0.05, False),
            "0.8 - 0.75 is at most 0.05": (0.8 - 0.75, 0.05, True),
            "0.1 + 0.2 is at most 0.3": (0.1 + 0.2, 0.3, True),
            "equal": (600.0, 600.0, True),
            "just inside the slack at 600": (600.0 + 5e-7, 600.0, True),
            "below": (0.74, 0.75, True),
        }
        for name, (value, threshold, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0base.at_most(value, threshold), expected)
        self.assertFalse(0.8 - 0.75 <= 0.05, "the naive comparison is the reason for the slack")

    def test_at_least(self):
        cases = {
            "below by more than EPS": (0.8 - 1e-7, 0.8, False),
            "equal": (0.8, 0.8, True),
            "just inside the slack": (0.8 - 5e-10, 0.8, True),
            "above": (0.81, 0.8, True),
        }
        for name, (value, threshold, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0base.at_least(value, threshold), expected)

    def test_above(self):
        cases = {
            "equal is not above": (0.75, 0.75, False),
            "2.1/2.8 is not above 0.75": (2.1 / 2.8, 0.75, False),
            "above by more than EPS": (0.7500001, 0.75, True),
        }
        for name, (value, threshold, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0base.above(value, threshold), expected)
        self.assertGreater(2.1 / 2.8, 0.75, "the float ratio really is above 0.75")


class VerdictTests(unittest.TestCase):
    def test_ci_verdict_g3f_boundaries(self):
        cases = {
            "lo just above 0.5": (0.5000001, 0.6, p0base.NO_GO),
            "lo at 0.5": (0.5, 0.6, p0base.MARGINAL),
            "straddles": (0.4, 0.6, p0base.MARGINAL),
            "hi at 0.5": (0.3, 0.5, p0base.GO),
            "below": (0.3, 0.45, p0base.GO),
        }
        for name, (lo, hi, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0base.ci_verdict(lo, hi, 0.5), expected)

    def test_kill_verdict_boundaries(self):
        cases = {
            "hi 599.9": (500.0, 599.9, p0base.NOT_KILLED),
            "lo 599.9, hi 600": (599.9, 600.0, p0base.MARGINAL),
            "straddles": (590.0, 610.0, p0base.MARGINAL),
            "lo 600": (600.0, 650.0, p0base.KILL),
        }
        for name, (lo, hi, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0base.kill_verdict(lo, hi, 600.0), expected)

    def test_ci_gate(self):
        cases = {
            "missing": (None, p0base.NO_VERDICT),
            "unbounded ratio": ({"lo": 0.1, "hi": 0.4, "unbounded": True}, p0base.NO_VERDICT),
            "no interval": ({"lo": None, "hi": None, "unbounded": True}, p0base.NO_VERDICT),
            "bounded": ({"lo": 0.1, "hi": 0.4, "unbounded": False}, p0base.GO),
        }
        for name, (ci, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0base.ci_gate(ci, 0.5), expected)
        self.assertEqual(p0base.ci_gate({"lo": 600.0, "hi": 700.0}, 600.0, p0base.kill_verdict), p0base.KILL)

    def test_point_verdict_eps_g_geo_a(self):
        cases = {
            "one pair": ([0.70], p0base.INSUFFICIENT),
            "none": ([], p0base.INSUFFICIENT),
            "straddles": ([0.76, 0.74], p0base.MARGINAL),
            "all above": ([0.76, 0.80], p0base.NO_GO),
            "at and under": ([0.75, 0.74, 0.70], p0base.GO),
            "2.1/2.8 counts as 0.75": ([2.1 / 2.8, 0.74], p0base.GO),
        }
        for name, (ratios, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0base.point_verdict_eps(ratios, 0.75, 2), expected)

    def test_point_verdict_eps_counts_exact_quarters_as_go(self):
        pairs = [(b * 3 // 4 / 100, b / 100) for b in range(4, 2000, 4)]
        above = [(a, b) for a, b in pairs if a / b > 0.75]
        self.assertEqual(len(above), 61, "61 two-decimal pairs with a/b = 0.75 compute above 0.75")
        for a, b in above:
            with self.subTest(f"{a}/{b}"):
                self.assertEqual(p0base.point_verdict_eps([a / b, 0.70], 0.75, 2), p0base.GO)


class GateHelperTests(unittest.TestCase):
    def test_refusals(self):
        cases = {
            "N² without arms": (lambda: p0base.n2_verdict({}), "at least one removal arm"),
            "G-GEO (b) without points": (lambda: p0base.geo_prediction_verdict([]), "at least one predicted"),
            "drift against a zero baseline": (lambda: p0base.drifted(100.0, 0.0), "baseline must be positive"),
            "coverage of no measure": (lambda: p0base.covered(48.0, 0.0), "measured seconds must be positive"),
            "threshold text at 0 fps": (lambda: p0base.threshold_text(600.0, 0), "positive frame rates"),
            "threshold text with no rate": (lambda: p0base.threshold_text(600.0, ()), "positive frame rates"),
        }
        for name, (call, message) in cases.items():
            with self.subTest(name), self.assertRaisesRegex(p0base.ModelError, message):
                call()

    def test_gate_table_boundaries(self):
        cases = {
            "N² best arm 0.7999 fails": (p0base.n2_verdict({"inert": 0.7999, "kinematic": 0.5}), p0base.FAIL),
            "N² best arm 0.80 passes": (p0base.n2_verdict({"inert": 0.80, "kinematic": 0.5}), p0base.PASS),
            "G-GEO (b) 0.8001 vs 0.75 fails": (p0base.geo_prediction_verdict([(0.8001, 0.75)]), p0base.FAIL),
            "G-GEO (b) one point out fails": (p0base.geo_prediction_verdict([(0.70, 0.70), (0.9, 0.8)]),
                                              p0base.FAIL),
            "G-GEO (b) 0.80 vs 0.75 passes": (p0base.geo_prediction_verdict([(0.80, 0.75)]), p0base.PASS),
            "G-GEO (c) 0.8999 fails": (p0base.geo_slope_verdict(0.8999, 0.9, 1.1), p0base.FAIL),
            "G-GEO (c) 1.1001 fails": (p0base.geo_slope_verdict(1.1001, 0.9, 1.1), p0base.FAIL),
            "G-GEO (c) 0.9 passes": (p0base.geo_slope_verdict(0.9, 0.9, 1.1), p0base.PASS),
            "G-GEO (c) 1.1 passes": (p0base.geo_slope_verdict(1.1, 0.9, 1.1), p0base.PASS),
            "floor 600.1 fails": (p0base.floor_verdict(600.1), p0base.FAIL),
            "floor 600 passes": (p0base.floor_verdict(600.0), p0base.PASS),
            "drift 105.1 vs 100": (p0base.drifted(105.1, 100.0), True),
            "no drift 105.0 vs 100": (p0base.drifted(105.0, 100.0), False),
            "no drift 95.0 vs 100": (p0base.drifted(95.0, 100.0), False),
            "coverage 47.99 of 60 fails": (p0base.covered(47.99, 60.0), False),
            "coverage 48.0 of 60 passes": (p0base.covered(48.0, 60.0), True),
        }
        for name, (actual, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(actual, expected)

    def test_threshold_bin_edge_us(self):
        cases = {"2.5 ms": (2.5, 2500), "1000/60 ms": (1000 / 60, 16650), "0.15·1000/60 ms": (0.15 * 1000 / 60, 2500),
                 "5.0 ms": (5.0, 5000)}
        for name, (ms, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0base.threshold_bin_edge_us(ms), expected)

    def test_bin_gate_passes(self):
        cases = {
            "above the histogram's range": (None, 2.5, False),
            "p95 in bin 50 (edge 2550) fails 2.5 ms": (2550, 2.5, False),
            "p95 in bin 49 (edge 2500) passes 2.5 ms": (2500, 2.5, True),
            "well under": (1200, 2.5, True),
        }
        for name, (p95_us, threshold_ms, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0base.bin_gate_passes(p95_us, threshold_ms), expected)

    def test_threshold_text(self):
        cases = {
            "one rate": ((600.0, 60), "600 ms/s = 10.00 ms/frame at 60 fps"),
            "both rate sets": ((600.0, (60, 30)), "600 ms/s = 10.00 ms/frame at 60 fps, 20.00 ms/frame at 30 fps"),
            "G1-F share": ((150.0, [60]), "150 ms/s = 2.50 ms/frame at 60 fps"),
        }
        for name, (args, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0base.threshold_text(*args), expected)


if __name__ == "__main__":
    unittest.main()
