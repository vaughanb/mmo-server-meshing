"""Unit tests for p0report and p0gates (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py")."""
import datetime
import glob
import math
import os
import unittest

import p0base
import p0dryrun
import p0gates
import p0geo
import p0model
import p0plans
import p0report
import p0results
import rigrun
import test_p0geo
import test_p0model

STAMP = "20260928_120000"
HERE = os.path.dirname(os.path.abspath(__file__))
B9 = {"players": 250, "monsters": "off", "load": "spread", "puppets": "kinematic", "rates": "60/0.03"}
BANDS = (20.0, 40.0, 80.0, 150.0)
CREATED = datetime.datetime(2026, 9, 28, 9, 0, tzinfo=datetime.timezone.utc)


def with_game(rows, game):
    for row in rows:
        row.setdefault("game", game)
    return rows


def base_rows(q=0.004):
    """Presence, density and floor rows (test_p0model's builders) with their games."""
    return (with_game(test_p0model.presence_rows() + test_p0model.density_rows(q=q), "fantasy")
            + with_game(test_p0model.floor_rows(), "neutral"))


def rates_rows(per_frame=0.35, per_step=0.20, per_second=30.0, entity=(0.001, 0.002, 0.05)):
    """Block K rows at the four rate points, 3 repeats: idle and kinematic (200 owners adding entity's F, Ph, C)."""
    rows = []
    for repeat in (1, 2, 3):
        for arm, n in (("idle", 0), ("kinematic", 200)):
            for fps, step in ((30, 0.03), (30, 0.02), (60, 0.03), (60, 0.02)):
                work = ((per_frame + n * entity[0]) * fps + (per_step + n * entity[1]) / step + per_second
                        + n * entity[2])
                metrics = [{"name": name, "stat": "mean", "value": value, "unit": "", "label": "M", "cell": "C"}
                           for name, value in (("work_ms_per_s", work), ("frames_per_s", fps),
                                               ("physics_steps_per_s", 1 / step), ("kit_sync_ms", 0.4),
                                               ("kit_aoi_ms", 0.3), ("kit_move_ms", 0.5))]
                rows.append(test_p0geo.run_row("rates", "K", arm, repeat, {"C": (work, n, 0)},
                                               point={"players": n, "rates": f"{fps}/{step}"}, metrics=metrics,
                                               rates={"fps": fps, "fixed_step_s": step, "tick_hz": 20}, game="neutral"))
    return rows


def abgeo_row(layout, repeat, band, peak):
    """An accepted abgeo row at 30/0.02 (so the refit at 60/0.03 never reads it) whose busiest cell is C at peak."""
    cells = [cid for cid, _ in rigrun.LAYOUTS[layout]]
    loads = {cid: (peak if cid == "C" else 0.9 * peak, 0.0, 0.0) for cid in cells}
    return test_p0geo.run_row("abgeo", "b9-bands", layout, repeat, loads, point=dict(B9, band=band), game="fantasy",
                              rates={"fps": 30, "fixed_step_s": 0.02, "tick_hz": 20})


def abgeo_rows(slope, strips4=(0.70, 0.72, 0.74), intercept=0.5):
    """b9-bands: halves/coarse = intercept + slope·B/600 + a per-repeat shift (so the measured slope is exact);
    strips4/halves at B 80 per repeat as given (no strips4 row for a repeat past its list)."""
    rows = []
    for repeat in (1, 2, 3):
        for band in BANDS:
            halves = 100.0 * (intercept + slope * band / p0geo.GROUND_M + 0.01 * repeat)
            rows += [abgeo_row("coarse", repeat, band, 100.0), abgeo_row("halves", repeat, band, halves)]
            if band == 80.0 and repeat <= len(strips4):
                rows.append(abgeo_row("strips4", repeat, band, halves * strips4[repeat - 1]))
    return rows


def predicted_slope(rows):
    """The G-GEO (c) slope computed independently: the refit's halves/coarse predictions against B/600."""
    coef, _ = p0gates.coefficients(p0model.fit(rows, "refit", "qQ", "60/0.03"))
    ys = [p0gates.predicted_ratio(coef, dict(B9, band=band, pair="halves/coarse"), rigrun.LAYOUTS, [], None)[0]
          for band in BANDS]
    xs = [band / p0geo.GROUND_M for band in BANDS]
    mx, my = sum(xs) / 4, sum(ys) / 4
    return math.fsum((x - mx) * (y - my) for x, y in zip(xs, ys)) / math.fsum((x - mx) ** 2 for x in xs)


def canary(arm, work, end, build="b1"):
    return test_p0geo.run_row("canary", "canary", arm, 1, {"C": (work, 200, 0)}, game="neutral", build=build,
                              window_local={"since": end, "start": end, "end": end})


def gates(result, name):
    return [g for g in result["gates"] if g["name"] == name]


def report(rows, **kwargs):
    return p0report.build_report(rows, STAMP, layouts=rigrun.LAYOUTS, **kwargs)


class RefusalTests(unittest.TestCase):
    def test_refusals(self):
        floor = with_game(test_p0model.floor_rows(), "neutral")
        two_builds = floor + [canary("baseline", 100.0, "2026-09-28T09:00:00"),
                              canary("check", 104.0, "2026-09-28T12:00:00", build="b2")]
        drifted = floor + [canary("baseline", 100.0, "2026-09-28T09:00:00"),
                           canary("check", 106.0, "2026-09-28T12:00:00", build="b2")]
        cases = {
            "no run rows": ([{"row": "fit"}], {}, "no run rows to report"),
            "unknown predict": (floor, {"predict": "presence"}, "only abgeo has frozen predictions"),
            "two builds without a bridge": (two_builds, {}, r"2 builds \(b1 \(5 rows\), b2 \(1 rows\)\) are never "
                                                             r"pooled: pass --bridge OLD_SHA .* or --build SHA"),
            "a bridge naming neither build": (two_builds, {"bridge": "zz"}, "must name one of exactly two builds"),
            "a bridge whose canary drifted": (drifted, {"bridge": "b1"}, "lies more than 5 % from build b1"),
            "a build naming no build": (two_builds, {"build": "zz"}, "--build zz names none of the rig builds"),
            "a build naming two builds": (two_builds, {"build": "b"}, "--build b names 2 rig builds"),
            "a build and a bridge": (two_builds, {"build": "b2", "bridge": "b1"}, "pass --bridge or --build, not both"),
        }
        for name, (rows, kwargs, message) in cases.items():
            with self.subTest(name), self.assertRaisesRegex(p0report.ReportError, message):
                report(rows, **kwargs)

    def test_a_passing_bridge_pools_the_builds(self):
        rows = with_game(test_p0model.floor_rows(), "neutral") + [
            canary("baseline", 100.0, "2026-09-28T09:00:00"), canary("check", 104.0, "2026-09-28T12:00:00", "b2")]
        result = report(rows, bridge="b1")
        self.assertEqual(gates(result, "Floor @ 60/0.03")[0]["verdict"], p0base.PASS)

    def test_a_bridged_report_compares_additivity(self):
        rows = with_game(test_p0model.presence_rows(repeats=(1, 2)) + test_p0model.presence_rows(repeats=(3,),
                                                                                                  build="b2"), "fantasy")
        env = {"MESH_LAYOUT_OVERRIDE": test_p0model.HALVES, "MESH_LOAD_MOTION": "kinematic",
               "MESH_PUPPET_MOTION": "kinematic"}
        work = 36 + 7.2 + (0.62 + 1.296 + 0.2592) * 200
        rows += [test_p0geo.run_row("presence", "additivity", "puppets-kinematic", repeat,
                                    {"C": (work, 200, 200), "E": (work, 200, 200)}, env, point={"players": 200},
                                    game="fantasy", build="b1" if repeat < 3 else "b2") for repeat in (1, 2, 3)]
        rows += [canary("baseline", 100.0, "2026-09-28T09:00:00"), canary("check", 104.0, "2026-09-28T12:00:00", "b2")]
        markdown = report(rows, bridge="b1")["markdown"]
        self.assertIn("additivity @ 60/0.03 puppets-kinematic {'players': 200} C: measured", markdown)
        self.assertNotIn("additivity @ 60/0.03: not compared", markdown)

    def test_other_teams_rows_are_summarised_not_pooled(self):
        space = test_p0geo.run_row("S1", "hot", "ships", 1, {"C": (50.0, 10, 0)}, team="space", game="space",
                                   build="spacebuild01")
        result, plain = report(base_rows() + [space]), report(base_rows())
        self.assertIn(("space", "space", "S1"), {(s["team"], s["game"], s["family"]) for s in result["summary"]})
        self.assertEqual(result["fits"], plain["fits"])
        self.assertIn(f"space_space_fit_{STAMP}.jsonl", result["files"])

    def test_build_keeps_one_build(self):
        old = with_game(test_p0model.floor_rows(noise=((5.0, 5.0), (5.0, 5.0))), "neutral")
        new = [dict(r, build="b2") for r in with_game(test_p0model.floor_rows(), "neutral")]
        result = report(old + new, build="b2")
        c0 = [f for f in result["fits"] if f["fit_id"] == "floor @ 60/0.03" and f["name"] == "c0"][0]
        self.assertAlmostEqual(c0["value"], 31.4)
        self.assertIn("4 rig run rows of other builds or hosts left out (--build b2, host None)", result["markdown"])

    def test_a_rerun_plan_key_counts_once(self):
        rows = base_rows()
        rerun = [dict(r, metrics=[dict(m, value=m["value"] + 3.0) for m in r["metrics"]]) for r in rows[:3]]
        result = report(rows + rerun)
        summary = next(s for s in result["summary"]
                       if (s["arm"], s["point"], s["cell"]) == ("puppets-steered", {"players": 100}, "C"))
        work = rows[1]["metrics"][0]["value"]
        self.assertEqual((summary["n"], summary["work_ms_per_s_max"]), (3, work + 3.0))
        presence = {f["n"] for f in result["fits"] if f["fit_id"] == "presence @ 60/0.03"}
        self.assertEqual(presence, {f["n"] for f in report(rows)["fits"] if f["fit_id"] == "presence @ 60/0.03"})
        self.assertIn("3 accepted run rows repeat a plan key on their build: only the latest row of each plan key "
                      "is used", result["markdown"])


class FitTests(unittest.TestCase):
    def test_fits_in_the_stated_order(self):
        result = report(rates_rows() + base_rows())
        order = list(dict.fromkeys(f["fit_id"] for f in result["fits"]))
        self.assertEqual(order, ["rates idle", "rates kinematic", "rates kinematic per entity", "density @ 60/0.03",
                                 "floor @ 60/0.03", "presence @ 60/0.03", "refit @ 60/0.03"])
        values = {(f["fit_id"], f["name"]): f["value"] for f in result["fits"]}
        expected = {("rates idle", "rate_F"): 0.35, ("rates idle", "rate_Ph"): 0.20, ("rates idle", "rate_C"): 30.0,
                    ("rates idle", "rate_S"): 6.0, ("rates idle", "rate_I"): 0.0,
                    ("rates kinematic per entity", "rate_entity_F"): 0.001,
                    ("rates kinematic per entity", "rate_entity_Ph"): 0.002,
                    ("rates kinematic per entity", "rate_entity_C"): 0.05,
                    ("rates kinematic per entity", "rate_kind_F"): 0.2,
                    ("rates kinematic per entity", "rate_kind_Ph"): 0.4,
                    ("rates kinematic per entity", "rate_kind_C"): 10.0,
                    ("presence @ 60/0.03", "k_hold_kinematic"): 0.5, ("presence @ 60/0.03", "k_kinematic"): 0.5,
                    ("presence @ 60/0.03", "a_steered"): 2.592, ("floor @ 60/0.03", "lambda"): 7.2}
        for key, value in expected.items():
            with self.subTest(key):
                self.assertAlmostEqual(values[key], value, places=6)
        self.assertIn("rates steered: not fitted: the rate decomposition needs two frame rates", result["markdown"])
        split = [f for f in result["fits"] if f["fit_id"] == "rates kinematic per entity"]
        self.assertEqual([f["name"] for f in split], ["rate_kind_F", "rate_kind_Ph", "rate_kind_C", "rate_entity_F",
                                                      "rate_entity_Ph", "rate_entity_C"])
        self.assertEqual({(f["n"], len(f["regressors"])) for f in split}, {(24, 6)})
        idle_notes = [note for f in result["fits"] if f["fit_id"] == "rates idle" for note in f["notes"]]
        self.assertFalse([note for note in idle_notes if note.startswith("interaction")])

    def test_a_lost_rate_cell_keeps_the_rate_fits(self):
        rows = [dict(r, accepted=False) if (r["arm"], r["point"]["rates"]) == ("idle", "60/0.02") else r
                for r in rates_rows()]
        fit_ids = list(dict.fromkeys(f["fit_id"] for f in report(rows)["fits"]))
        self.assertEqual(fit_ids, ["rates idle", "rates kinematic", "rates kinematic per entity"])

    def test_presence_takes_q_only_when_density_excludes_zero(self):
        cases = {
            "q interval includes 0": (0.0, False, "q left out: density's q interval"),
            "q interval excludes 0": (0.004, True, "q included (Q): density's q interval 0.004 [0.004, 0.004] "
                                                   "excludes 0"),
        }
        for name, (q, included, note) in cases.items():
            with self.subTest(name):
                result = report(base_rows(q))
                names = [f["name"] for f in result["fits"] if f["fit_id"] == "presence @ 60/0.03"]
                self.assertEqual("q" in names, included)
                self.assertIn(note, result["markdown"])
                for fit in (f for f in result["fits"] if f["fit_id"] == "presence @ 60/0.03"):
                    self.assertTrue(any(text.startswith(note) for text in fit["notes"]), fit["name"])

    def test_two_repeat_fit_prints_both_intervals(self):
        result = report(with_game(test_p0model.floor_rows(), "neutral"))
        lam = [f for f in result["fits"] if f["fit_id"] == "floor @ 60/0.03" and f["name"] == "lambda"][0]
        self.assertAlmostEqual(lam["t"], 12.706)
        self.assertEqual(lam["runs_ci"]["label"], p0model.RUNS_LABEL)
        self.assertLess(lam["runs_ci"]["hi"] - lam["runs_ci"]["lo"], lam["hi"] - lam["lo"])
        self.assertIn("| floor @ 60/0.03 | lambda | links | 7.2 [6.247, 8.153] | 12.706 | 4 | ols + jackknife over "
                      "repeats | runs as groups (assumes no between-repeat drift): 7.2 [6.9821, 7.4179] |",
                      result["markdown"])

    def test_earlier_report_rows_are_ignored(self):
        rows = base_rows()
        earlier = [{"row": "summary", "family": "presence", "game": "fantasy"},
                   {"row": "fit", "fit_id": "presence @ 60/0.03", "name": "c0", "value": 1.0},
                   {"row": "gate", "name": "G3-F (U) @ 60/0.03", "verdict": "NO-GO"}]
        plain, mixed = report(rows), report(earlier + rows)
        self.assertEqual(mixed["fits"], plain["fits"])
        self.assertEqual(mixed["gates"], plain["gates"])
        self.assertIn("3 summary, fit and gate rows of earlier reports ignored", mixed["markdown"])

    def test_output_files(self):
        created = datetime.datetime(2026, 9, 28, 12, 0, tzinfo=datetime.timezone.utc)
        point = dict(B9, case="b9-main", band=0.0, pair="halves/coarse")
        result = report(base_rows(), predict="abgeo", predict_points=[point], created_utc=created)
        self.assertEqual(sorted(result["files"]), [f"report_{STAMP}.md", f"rig_fantasy_abgeo-prediction_{STAMP}.jsonl",
                                                   f"rig_fantasy_fit_{STAMP}.jsonl", f"rig_neutral_fit_{STAMP}.jsonl"])
        for name, content in result["files"].items():
            with self.subTest(name):
                if name.endswith(".jsonl"):
                    self.assertEqual(p0results.parse_rows(name, p0results.format_rows(content, name)), content)
                    self.assertEqual({row["game"] for row in content}, {name.split("_")[1]})
        self.assertIsNone(result["prediction_error"])
        self.assertEqual(result["files"][f"report_{STAMP}.md"], result["markdown"])
        self.assertTrue(result["markdown"].startswith(f"# Phase 0 stage 0a report {STAMP}\n"))
        for heading in ("## Summary", "## Runs not accepted", "## Fits", "## Gates", "## Map-size table",
                        "## Frozen abgeo predictions", "| Side (m) | B (m) | k |", "G1-F needs the stage 0b report",
                        "Phantom validity (J) needs b2 or b10 rows"):
            with self.subTest(heading):
                self.assertIn(heading, result["markdown"])


class GateTests(unittest.TestCase):
    def test_presence_and_floor_gates(self):
        result = report(base_rows())
        cases = {
            "G3-F (U) @ 60/0.03": p0base.GO,
            "Kill, characters (U) @ 60/0.03": p0base.KILL,
            "G3-F (U) @ 30/0.02": p0base.NO_VERDICT,
            "Kill, characters (U) @ 30/0.02": p0base.NO_VERDICT,
            "Floor @ 60/0.03": p0base.PASS,
            "Floor @ 30/0.02": p0base.NO_VERDICT,
            "N² @ 60/0.03": p0base.NO_VERDICT,
            "G-GEO (a)": p0base.NO_VERDICT,
            "Canary drift": p0base.NO_VERDICT,
        }
        for name, verdict in cases.items():
            with self.subTest(name):
                self.assertEqual(gates(result, name)[0]["verdict"], verdict)
        kill = gates(result, "Kill, characters (U) @ 60/0.03")[0]
        self.assertAlmostEqual(kill["inputs"]["statistic"]["value"], 36.0 + 7.2 + 500 * 1.296)
        self.assertEqual(kill["rule"], "c0 + λ + 500·e_kinematic ≥ 600 ms/s = 10.00 ms/frame at 60 fps kills")
        self.assertIn("needs p0load presence --rates 30/0.02", gates(result, "G3-F (U) @ 30/0.02")[0]["notes"])
        self.assertEqual(gates(result, "Floor @ 30/0.02")[0]["rule"],
                         "idle C work ≤ 600 ms/s = 20.00 ms/frame at 30 fps")

    def test_n2_gate(self):
        pairs = {n: p0geo.expected_local_pairs(200, p0geo.crowd_rect(area) or (-300.0, -300.0, 600.0, 600.0))
                 for n, area in (("dense", "0,0,70.71"), ("sparse", ""))}
        base_dense, base_sparse = 31.4 + 2.592 * 200 + 0.004 * pairs["dense"], 31.4 + 2.592 * 200 + 0.004 * pairs["sparse"]
        excess = base_dense - base_sparse
        cases = {"best arm below 0.80": (0.79, "FAIL (profiler pending)"),
                 "best arm at 0.80": (0.80, "PASS (profiler pending)")}
        for name, (share, verdict) in cases.items():
            with self.subTest(name):
                arms = []
                for repeat in (1, 2, 3):
                    for arm, removed in (("inert", share), ("no-fight", 0.3), ("kinematic", 0.1)):
                        for case, side, work in (("attribution", 141.42, base_dense - removed * excess - 5.0),
                                                 ("sparse", 600, base_sparse - 5.0)):
                            arms.append(test_p0geo.run_row("density", case, arm, repeat, {"C": (work, 200, 0)},
                                                           point={"players": 200, "side": side}, game="fantasy"))
                gate = gates(report(base_rows() + arms), "N² @ 60/0.03")[0]
                self.assertEqual(gate["verdict"], verdict)
                self.assertAlmostEqual(gate["inputs"]["shares"]["inert"], share)
                self.assertIn("profiler pending", gate["notes"])

    def test_geo_gates(self):
        rows = base_rows()
        slope = predicted_slope(rows)
        prediction = p0results.prediction_row(dict(B9, case="b9-bands", band=80.0, pair="strips4/halves"), 0.0, "x",
                                              CREATED)
        cases = {  # strips4/halves per repeat, measured band slope, prediction minus the measured mean, verdicts
            "strips4 straddles, slope off, prediction off": ((0.76, 0.74, 0.70), slope + 0.2, 0.0501,
                                                             (p0base.MARGINAL, p0base.FAIL, p0base.FAIL)),
            "prediction off below": ((0.70, 0.72, 0.74), slope, -0.0501, (p0base.GO, p0base.PASS, p0base.FAIL)),
            "one strips4 pair": ((0.70,), slope, 0.0, (p0base.INSUFFICIENT, p0base.PASS, p0base.PASS)),
            "all at their thresholds": ((0.75, 2.1 / 2.8, 0.70), slope, -0.05, (p0base.GO, p0base.PASS, p0base.PASS)),
            "the §E example (0.77 against 0.72)": ((0.70, 0.72, 0.74), slope, 0.05,
                                                   (p0base.GO, p0base.PASS, p0base.PASS)),
        }
        for name, (strips4, measured_slope, offset, verdicts) in cases.items():
            with self.subTest(name):
                frozen = [dict(prediction, ratio=sum(strips4) / len(strips4) + offset)]
                result = report(rows + abgeo_rows(measured_slope, strips4) + frozen)
                got = tuple(gates(result, name)[0]["verdict"] for name in ("G-GEO (a)", "G-GEO (c)", "G-GEO (b)"))
                self.assertEqual(got, verdicts)
                self.assertEqual(gates(result, "G-GEO (a)")[0]["case"], "b9-bands")

    def test_geo_b_without_predictions_or_runs(self):
        cases = {
            "no predictions": ([], "no frozen predictions: p0load report --predict abgeo before p0load abgeo"),
            "no matching runs": ([p0results.prediction_row(dict(B9, case="b9-main", band=0.0, pair="halves/coarse"),
                                                           0.6, "x", CREATED)], "no abgeo runs match a predicted point"),
        }
        for name, (predictions, note) in cases.items():
            with self.subTest(name):
                gate = gates(report(base_rows() + predictions), "G-GEO (b)")[0]
                self.assertEqual(gate["verdict"], p0base.NO_VERDICT)
                self.assertIn(note, gate["notes"])

    def test_canary_drift(self):
        cases = {"105.1 against 100 drifts": (105.1, p0base.FAIL, False), "105.0 against 100 does not": (105.0,
                                                                                                        p0base.PASS,
                                                                                                        True)}
        for name, (work, verdict, floor_kept) in cases.items():
            with self.subTest(name):
                floor = with_game(test_p0model.floor_rows(), "neutral")
                for row in floor:
                    row["window_local"] = {"since": "", "start": "", "end": "2026-09-28T10:00:00"}
                result = report(floor + [canary("baseline", 100.0, "2026-09-28T09:00:00"),
                                         canary("check", work, "2026-09-28T11:00:00")])
                self.assertEqual(gates(result, "Canary drift")[0]["verdict"], verdict)
                floor_gate = gates(result, "Floor @ 60/0.03")[0]["verdict"]
                self.assertEqual(floor_gate, p0base.PASS if floor_kept else p0base.NO_VERDICT)

    def test_canary_drift_counts_only_points_left_unmeasured(self):
        def floor_at(end):
            rows = with_game(test_p0model.floor_rows(), "neutral")
            for row in rows:
                row["window_local"] = {"since": end, "start": end, "end": end}
            return rows

        cases = {
            "a drift before any run costs nothing": (
                floor_at("2026-09-28T12:00:00") + [canary("check", 120.0, "2026-09-28T10:00:00"),
                                                   canary("check", 100.0, "2026-09-28T13:00:00")], p0base.PASS, 0),
            "excluded points re-measured after the drift": (
                floor_at("2026-09-28T10:00:00") + floor_at("2026-09-28T12:00:00")
                + [canary("check", 120.0, "2026-09-28T11:00:00"), canary("check", 100.0, "2026-09-28T13:00:00")],
                p0base.PASS, 0),
        }
        for name, (rows, verdict, missing) in cases.items():
            with self.subTest(name):
                result = report(rows + [canary("baseline", 100.0, "2026-09-28T09:00:00")])
                gate = gates(result, "Canary drift")[0]
                self.assertEqual(gate["verdict"], verdict)
                self.assertEqual(gate["inputs"]["not_remeasured"], missing)
                self.assertEqual(gates(result, "Floor @ 60/0.03")[0]["verdict"], p0base.PASS)


class PredictionTests(unittest.TestCase):
    def chain3(self):
        paths = sorted(glob.glob(os.path.join(HERE, "fixtures", "abload_chain3_20260925", "abload_ab_*.txt")))
        texts = []
        for path in paths:
            with open(path, encoding="utf-8") as f:
                texts.append(f.read())
        return texts

    def test_chain3_monster_counts(self):
        counts = p0report.chain3_monster_counts(self.chain3() + ["not a report\n    peak C 1 ms | C 1 ms"])
        self.assertEqual(sorted(counts), [("coarse", 20.0), ("coarse", 40.0), ("coarse", 80.0), ("grid", 80.0),
                                          ("halves", 20.0), ("halves", 40.0), ("halves", 80.0)])
        self.assertAlmostEqual(counts[("halves", 20.0)]["C"]["O_monster"], (140 + 142 + 140) / 3)
        self.assertAlmostEqual(counts[("halves", 20.0)]["C"]["U_monster"], (15 + 23 + 16) / 3)
        self.assertEqual(counts[("coarse", 20.0)]["C"]["U_monster"], 0.0)
        self.assertEqual(len(counts[("grid", 80.0)]), 9)

    def test_predictions(self):
        points = [dict(B9, case="b9-bands", band=band, monsters="all", pair=pair)
                  for band in (80.0, 150.0) for pair in ("halves/coarse", "strips4/halves")]
        points.append(dict(B9, case="b9-main", band=0.0, pair="halves/coarse"))
        counts = p0report.chain3_monster_counts(self.chain3())
        result = report(base_rows(), predict="abgeo", predict_points=points, monster_counts=counts, created_utc=CREATED)
        self.assertIsNone(result["prediction_error"])
        self.assertEqual([p["point"] for p in result["predictions"]], points)
        labels = {(p["point"]["band"], p["point"]["pair"]): p["notes"] for p in result["predictions"]}
        self.assertIn("halves B 80: monster counts from the 0.71 chain3 reports (M, older build)",
                      labels[(80.0, "halves/coarse")])
        self.assertIn("strips4 B 80: monster counts from the grid's owned monsters by area (E)",
                      labels[(80.0, "strips4/halves")])
        self.assertIn("halves B 150: monster counts from the grid's owned monsters by area (E)",
                      labels[(150.0, "halves/coarse")])
        for row in result["predictions"]:
            with self.subTest(row["point"]["pair"]):
                self.assertTrue(0.4 < row["ratio"] < 1.0)
                self.assertEqual(row["inputs_sha"], p0gates.inputs_sha(p0results.apply_drift(base_rows())))
                self.assertEqual(row["created_utc"], CREATED.isoformat())

    def test_prediction_failures_leave_no_file(self):
        cases = {
            "monsters without counts": ([dict(B9, case="b9-bands", band=80.0, monsters="all", pair="halves/coarse")],
                                        base_rows(), "no monster counts for halves at B 80"),
            "no refit": ([dict(B9, case="b9-main", band=0.0, pair="halves/coarse")],
                         with_game(test_p0model.floor_rows(), "neutral"), "no refit at 60/0.03 to predict from"),
        }
        for name, (points, rows, message) in cases.items():
            with self.subTest(name):
                result = report(rows, predict="abgeo", predict_points=points, created_utc=CREATED)
                self.assertIn(message, result["prediction_error"])
                self.assertFalse([name for name in result["files"] if "prediction" in name])
                self.assertIn(f"not made: {result['prediction_error']}", result["markdown"])

    def test_map_size(self):
        def fit(value, lo, hi):
            return {"fit_id": "refit", "rates": "60/0.03",
                    "ci": {"c0": {"value": 36.0}, "links": {"value": 7.2}, "O_steered": {"value": 2.592},
                           "U_kinematic": {"value": 1.296}, "G_player": {"value": 0.2592},
                           "k_band_kinematic": {"value": value, "lo": lo, "hi": hi}}}
        cases = {
            "no k": ((None, {}, {}), "not built: no refit or presence fit gives k_band_kinematic", 0),
            "k not above 0": ((fit(0.0, -0.1, 0.1), {}, {}), "not built: k_band_kinematic 0 [-0.1, 0.1] is not above 0",
                              0),
            "a lower k not above 0": ((fit(0.6, -0.1, 1.2), {}, {}),
                                      "k's lower bound -0.100 is not above 0: its rows are left out", 6),
            "the worked split inputs": ((fit(0.6, 0.5, 0.7), {}, {}), "x 0.0471, link_share 0.0094, V 764.0 ms/s", 9),
        }
        for name, (arguments, text, rows) in cases.items():
            with self.subTest(name):
                lines = p0gates.map_size(*arguments)
                self.assertIn(text, "\n".join(lines))
                self.assertEqual(len([line for line in lines if line.startswith("| 1000 |")]), rows)


class ConstantTests(unittest.TestCase):
    def test_constants_match_the_plans(self):
        cases = {
            "attribution point": (p0gates.ATTRIBUTION_POINT, p0plans.ATTRIBUTION_POINT),
            "sparse point": (p0gates.SPARSE_POINT, p0plans.SPARSE_POINT),
            "removal arms": (p0gates.REMOVALS, tuple(arm for arm, _ in p0plans.REMOVALS)),
            "floor rates": (p0gates.FLOOR_RATES, p0plans.FLOOR_RATES),
            "animation-off point": (p0report.ANIMATION_OFF_POINT, p0plans.ANIMATION_OFF_POINT),
            "the floor limit": (p0gates.FLOOR_MS_S, p0plans.THIRD_REPEAT_THRESHOLDS["floor"][0][1]),
            "monster costs": ((p0gates.MONSTER_MS_071, p0gates.MONSTER_PUPPET_MS_071),
                              (p0dryrun.MONSTER_MS, p0dryrun.MONSTER_PUPPET_MS)),
            "fit rates": (p0gates.REFIT_RATES, p0plans.ABGEO_COMMON[-1]),
        }
        for name, (ours, theirs) in cases.items():
            with self.subTest(name):
                self.assertEqual(ours, theirs)
        for name, need in p0report.PENDING_GATES:   # step 9 evaluates Database and Crossings half B (gates_9)
            with self.subTest(name):
                self.assertEqual(need, p0report.STAGE_0B_REPORT)
        self.assertEqual([name for name, _ in p0report.STEP_8B_GATES][2:], ["Database", "Crossings (B10) half B"])


if __name__ == "__main__":
    unittest.main()
