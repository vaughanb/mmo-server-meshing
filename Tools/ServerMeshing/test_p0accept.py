"""Unit tests for p0accept (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py")."""
import unittest

import p0accept
import p0base
import test_p0checks

make_run, cell_lines, stamped = test_p0checks.make_run, test_p0checks.cell_lines, test_p0checks.stamped
KINEMATIC_PUPPETS = test_p0checks.KINEMATIC_PUPPETS


def instrument_runs(drop=(), **changes):
    options = {"plain": {"drop": ("rates", "aoi"), "bins": False}, "r60": {},
               "r60burn": {"tick_mean": 7.0, "other_mean": 1.1},
               "r30": {"fps": 30, "physics": 50.0, "rates": ("30", "0.02")},
               "r30burn": {"fps": 30, "physics": 50.0, "rates": ("30", "0.02"), "tick_mean": 7.0, "other_mean": 1.05}}
    return {name: make_run({"C": cell_lines("C", **dict(opts, **changes.get(name, {})))})
            for name, opts in options.items() if name not in drop}


def puppet_runs(**changes):
    options = {"steered": {}, "kin": {"pup": KINEMATIC_PUPPETS}, "kin_kwalk": {"pup": KINEMATIC_PUPPETS},
               "kin_inert": {"pup": dict(KINEMATIC_PUPPETS, poses=0, samples=0, p95=0.0)}}
    return {name: make_run({"C": cell_lines("C", **changes.get(f"{name}_C", {})),
                            "E": cell_lines("E", **dict(opts, **changes.get(name, {})))})
            for name, opts in options.items()}


def failures(checks):
    return [text for ok, text in checks if not ok]


class AcceptChecksTests(unittest.TestCase):
    def test_instrument_failures(self):
        cases = {
            "a missing run": ({"drop": ("r30burn",)}, ["r30burn: no run to check on C",
                                                       "r30burn: no Work ticks figures to compare with r30"]),
            "a burn of 1.8 ms": ({"r60burn": {"tick_mean": 6.8}},
                                 ["r60burn: tick-frame work rose 1.800 ms (want 2.000 ± 0.100): 5.000 -> 6.800"]),
            "other frames moved 0.2 ms": ({"r30burn": {"other_mean": 1.2}},
                                          ["r30burn: other frames 1.000 -> 1.200 ms (within the 0.15 ms between-Play "
                                           "spread)"]),
            "physics 48 steps/s": ({"r30": {"physics": 48.0}}, ["r30: physics steps/s 48.00 (want 50.0 ± 1)"]),
            "a missing bins line": ({"r60": {"changes": {3: {"bins": False}}}},
                                    ["r60: every Work ticks line has its Work bins line (1 missing, 0 unpaired)"]),
            "a bin p95 off the line's": ({"r60": {"changes": {3: {"ticks": 99}}}},
                                         ["r60: each window's Work bins p95 matches its Work ticks p95 within 0.05 ms "
                                          "(10:00:15 tick 3.05 vs 2.05 ms)"]),
            "a dropped tick": ({"plain": {"changes": {2: {"dropped": 1}}}},
                               ["plain: 100 ± 1 ticks per 5 s window, none dropped (10:00:10: 100 of 100 ticks, "
                                "1 dropped)"]),
        }
        for name, (changes, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(failures(p0accept.accept_instrument_checks(instrument_runs(**changes))), expected)

    def test_instrument_checks_pass(self):
        checks = p0accept.accept_instrument_checks(instrument_runs())
        self.assertEqual(failures(checks), [])
        texts = [text for _, text in checks]
        self.assertIn("r30: share of frames with a tick 0.667 (want 0.667 ± 0.02)", texts)
        self.assertIn("r60burn: tick-frame work rose 2.000 ms (want 2.000 ± 0.100): 5.000 -> 7.000", texts)
        self.assertIn("r60: thread classes within ± 5 % of process CPU in 12 of 12 windows", texts)

    def test_puppet_failures(self):
        cases = {
            "steered logs Puppets lines": ({"steered": {"pup": KINEMATIC_PUPPETS}},
                                           ["steered: no Puppets window line on E (default output unchanged)"]),
            "a steer p95 of 1.2 m": ({"kin": {"pup": dict(KINEMATIC_PUPPETS, p95=1.2)}},
                                     ["kin: steer kinematic p95 under 1.0 m (worst window 1.20 m)"]),
            "12 poses per puppet": ({"kin_kwalk": {"pup": dict(KINEMATIC_PUPPETS, poses=720)}},
                                    ["kin_kwalk: unwatched kinematic puppets posed at the snapshot rate (12.0 per puppet "
                                     "per s, want 7 to 11)"]),
            "frequent snaps": ({"kin": {"pup": dict(KINEMATIC_PUPPETS, snaps=5)}},
                               ["kin: snaps are rare (30 for 1440 moving samples)"]),
            "inert owners moving": ({"kin_inert": {"pup": KINEMATIC_PUPPETS}},
                                    ["kin_inert: standing owners give no moving samples (1440)"]),
            "an exception on C": ({"kin_C": {"add": (stamped("10:00:30", "NullReferenceException"),)}},
                                  ["kin: no exception lines on C or E (1)"]),
            "too few kinematic puppets": ({"kin": {"pup": dict(KINEMATIC_PUPPETS, kinematic=9, poses=81)}},
                                          ["kin: E holds kinematic puppets (9.0 on average, need 10)"]),
        }
        for name, (changes, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(failures(p0accept.accept_puppet_checks(puppet_runs(**changes))), expected)

    def test_puppet_checks_pass(self):
        self.assertEqual(failures(p0accept.accept_puppet_checks(puppet_runs())), [])
        self.assertEqual(failures(p0accept.accept_puppet_checks({})),
                         [f"{name}: no run to check on E" for name in p0accept.PUPPET_RUNS])


class OverheadTests(unittest.TestCase):
    def test_refuses_one_pair(self):
        with self.assertRaisesRegex(p0base.ModelError, "at least 2 pairs; got 1"):
            p0accept.overhead([(5.0, 5.01)])

    def test_overhead(self):
        cases = {
            "resolved above the gate": ([0.05, 0.06, 0.05, 0.04, 0.05], (0.05, p0base.NO_GO)),
            "straddling the gate": ([0.0, 0.04, 0.02, 0.01, 0.03], (0.02, p0base.MARGINAL)),
            "resolved under the gate": ([0.01, 0.0, 0.02, 0.01, 0.01], (0.01, p0base.GO)),
        }
        for name, (diffs, (mean, verdict)) in cases.items():
            with self.subTest(name):
                got = p0accept.overhead([(1.0, 1.0 + d) for d in diffs])
                self.assertAlmostEqual(got["mean"], mean)
                self.assertEqual((got["verdict"], got["n"], got["t"]), (verdict, 5, 2.776))

    def test_the_interval(self):
        got = p0accept.overhead([(1.0, 1.01), (1.0, 1.0), (1.0, 1.02), (1.0, 1.01), (1.0, 1.01)])
        self.assertAlmostEqual(got["se"], 0.0031623, places=6)
        self.assertAlmostEqual(got["hi"], 0.01 + 2.776 * 0.0031623, places=6)


if __name__ == "__main__":
    unittest.main()
