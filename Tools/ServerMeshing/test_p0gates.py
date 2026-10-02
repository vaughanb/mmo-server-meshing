"""Unit tests for p0gates' G-GEO (b) prediction choice (python -m unittest discover -s Tools/ServerMeshing -p
"test_*.py"). The other gates are tested through the report in test_p0report."""
import datetime
import unittest

import p0base
import p0results
import test_p0report

B9, UTC = test_p0report.B9, datetime.timezone.utc
FIRST_RUN = "2026-09-28T10:00:00+00:00"


def prediction(pair, band, ratio, hour):
    point = dict(B9, case="b9-bands", band=band, pair=pair)
    return p0results.prediction_row(point, ratio, "x", datetime.datetime(2026, 9, 28, hour, 0, tzinfo=UTC))


def geo_b(predictions, strips4=(0.70, 0.72, 0.74), excluded=()):
    rows = [dict(r, started_utc=FIRST_RUN) for r in test_p0report.abgeo_rows(1.0, strips4)]
    for row in rows:
        if row["arm"] in excluded:
            row.update(accepted=False, excluded_reason="saturated span on C")
    result = test_p0report.report(test_p0report.base_rows() + rows + predictions)
    return test_p0report.gates(result, "G-GEO (b)")[0]


class GeoPredictionTests(unittest.TestCase):
    def test_predictions_made_after_the_first_abgeo_run(self):
        cases = {
            "only a later set": ([prediction("strips4/halves", 80.0, 0.72, 20)], p0base.NO_VERDICT,
                                 "predictions made after the first abgeo run: not frozen"),
            "a later set does not replace the frozen one": ([prediction("strips4/halves", 80.0, 0.92, 9),
                                                             prediction("strips4/halves", 80.0, 0.72, 20)],
                                                            p0base.FAIL, "1 predictions made 2026-09-28T09:00:00+00:00"),
        }
        for name, (predictions, verdict, note) in cases.items():
            with self.subTest(name):
                gate = geo_b(predictions)
                self.assertEqual(gate["verdict"], verdict)
                self.assertIn(note, gate["notes"])

    def test_every_predicted_point_must_be_judged(self):
        halves = prediction("halves/coarse", 10.0, 0.8, 9)
        cases = {
            "a point without an accepted pair": ([prediction("strips4/halves", 80.0, 0.72, 9), halves], (),
                                                 p0base.INSUFFICIENT),
            "a judged point misses": ([prediction("strips4/halves", 80.0, 0.92, 9), halves], (), p0base.FAIL),
            "every strips4 run excluded": ([prediction("strips4/halves", 80.0, 0.72, 9)], ("strips4",),
                                           p0base.NO_VERDICT),
        }
        for name, (predictions, excluded, verdict) in cases.items():
            with self.subTest(name):
                gate = geo_b(predictions, excluded=excluded)
                self.assertEqual(gate["verdict"], verdict)
                self.assertTrue(any(note.startswith("1 of ") and "predicted points had no accepted pair" in note
                                    for note in gate["notes"]), gate["notes"])

    def test_every_point_judged_passes(self):
        gate = geo_b([prediction("strips4/halves", 80.0, 0.72, 9)])
        self.assertEqual(gate["verdict"], p0base.PASS)
        self.assertFalse([note for note in gate["notes"] if "no accepted pair" in note])


if __name__ == "__main__":
    unittest.main()
