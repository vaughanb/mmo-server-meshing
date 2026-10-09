"""Unit tests for p0results (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py")."""
import datetime
import json
import os
import tempfile
import unittest

import p0base
import p0cells
import p0results
import test_p0cells
import test_p0cells0b

CONTEXT = {"host": "RIG", "cpu": "Test CPU", "cores": 8, "logical": 16, "os": "Windows-11", "power_plan": "Balanced",
           "build": "abc123", "kit_patches": {"CE-6": "present", "CE-12": "absent"}}


def spec(**fields):
    base = {"family": "presence", "case": "main", "arm": "puppets-kinematic", "point": {"players": 60}, "repeat": 1,
            "seed": 1, "order": 3, "layout": "halves", "cells_started": ["C", "E"], "cells_read": ["C"],
            "plan": {"C": 60}, "env": {"MESH_LOAD_MOTION": "kinematic", "MESH_PUPPET_MOTION": "kinematic",
                                       "MESH_BAND": "300", "MESH_RATES": "60/0.03"},
            "warmup": 45, "measure": 60, "rates": "60/0.03", "uncapped": [], "band": None, "expect": {}, "key": "k1"}
    return dict(base, **fields)


def a_run(lines=None):
    record = p0cells.cell_record("C", test_p0cells.run_lines() if lines is None else lines, test_p0cells.START,
                                 test_p0cells.END)
    return {"since": test_p0cells.START - datetime.timedelta(seconds=60), "start": test_p0cells.START,
            "end": test_p0cells.END, "cells": {"C": {"structural": {}, "p0": record}}}


def other_rows():
    header = {"game": "fantasy", "family": "presence", "build": "abc123", "cpu": "Test CPU"}
    return [p0results.fit_row("presence", "O_steered", {"value": 2.5, "se": 0.1, "lo": 2.2, "hi": 2.8, "t": 4.303},
                              ["c0", "O_steered"], 12, reference="a_steered", header=header),
            p0results.gate_row("G3-F (U)", "GO", {"k": 0.4}, "k_hold_kinematic <= 0.5", ["O half needs 0b (step 5)"],
                               header=header),
            p0results.prediction_row({"players": 250}, 0.61, "sha", datetime.datetime(2026, 9, 27, 12, 0))]


class RunRowTests(unittest.TestCase):
    def test_refusals(self):
        cases = {
            "a family without a game": (spec(family="workerab"), "family 'workerab' has no game"),
            "a malformed MESH_RATES": (spec(rates="30/x"), "MESH_RATES step part 'x' is not a number of seconds"),
            "a malformed MESH_BAND": (spec(env={"MESH_BAND": "wide"}), "MESH_BAND 'wide' is not a number of metres"),
        }
        for name, (value, message) in cases.items():
            with self.subTest(name):
                with self.assertRaisesRegex(ValueError, message):
                    p0results.run_row(value, a_run(), CONTEXT, [], None)

    def test_an_accepted_run(self):
        row = p0results.run_row(spec(), a_run(), CONTEXT, [], None)
        p0results.validate_row(row, "row")
        self.assertEqual({key: row[key] for key in ("schema", "row", "team", "game", "model", "family", "stage",
                                                    "plan_key", "order", "accepted", "excluded_reason", "capped")},
                         {"schema": "mesh-p0/1", "row": "run", "team": "rig", "game": "fantasy", "model": "geographic",
                          "family": "presence", "stage": "0a", "plan_key": "k1", "order": 3, "accepted": True,
                          "excluded_reason": None, "capped": True})
        self.assertEqual(row["rates"], {"fps": 60, "fixed_step_s": 0.03, "tick_hz": 20})
        self.assertEqual(row["window_local"], {"since": "2026-09-26T09:59:00", "start": "2026-09-26T10:00:00",
                                               "end": "2026-09-26T10:01:00"})
        self.assertEqual({key: row[key] for key in p0results.CONTEXT_FIELDS}, CONTEXT)
        self.assertEqual(row["dropped_windows"], {"C": ["2026-09-26T10:00:25"]})
        self.assertEqual(row["owned"], {"character_kinematic": 60.0, "monster": 0.0})
        self.assertEqual(row["puppets"], {"character_kinematic": 0.0, "monster": 0.0})
        self.assertEqual((row["density"], row["band_m"], row["visible_range_m"]), (3000.0, 300.0, 300.0))
        self.assertEqual(len(row["windows"]["C"]), 12)
        self.assertEqual({m["cell"] for m in row["metrics"]}, {"C"})
        self.assertIn({"name": "work_ms_per_frame", "stat": "mean", "value": 5.0, "unit": "ms/frame", "label": "M",
                       "cell": "C"}, row["metrics"])
        self.assertEqual(json.loads(json.dumps(row)), row)

    def test_problems_and_exclusions_are_not_accepted(self):
        cases = {"a problem": (["C started 2 times"], None), "an exclusion": ([], "saturated (span) on C")}
        for name, (problems, reason) in cases.items():
            with self.subTest(name):
                row = p0results.run_row(spec(), a_run(), CONTEXT, problems, reason)
                self.assertEqual((row["accepted"], row["problems"], row["excluded_reason"]), (False, problems, reason))

    def test_a_failed_run(self):
        row = p0results.run_row(spec(), None, CONTEXT, ["Play ended during the measurement"], "run failed")
        p0results.validate_row(row, "row")
        self.assertEqual((row["window_local"], row["started_utc"], row["metrics"], row["windows"], row["density"]),
                         (None, None, [], {}, None))
        self.assertEqual(row["accepted"], False)

    def test_started_utc(self):
        run = a_run()
        run["start"] = datetime.datetime(2026, 9, 26, 12, 0, tzinfo=datetime.timezone(datetime.timedelta(hours=2)))
        row = p0results.run_row(spec(), run, CONTEXT, [], None)
        self.assertEqual(row["started_utc"], "2026-09-26T10:00:00+00:00")

    def test_rates_band_and_game_by_spec(self):
        cases = {
            "no MESH_RATES": (spec(rates=None, env={}), ({"fps": None, "fixed_step_s": None, "tick_hz": 20}, True,
                                                           80.0, "fantasy", "character_steered")),
            "an uncapped cell": (spec(uncapped=["C"], band=150.0),
                                 ({"fps": 60, "fixed_step_s": 0.03, "tick_hz": 20}, False, 150.0, "fantasy",
                                  "character_kinematic")),
            "a neutral family": (spec(family="canary", env={"MESH_LOAD_MOTION": "Inert"}),
                                 ({"fps": 60, "fixed_step_s": 0.03, "tick_hz": 20}, True, 80.0, "neutral",
                                  "character_inert")),
        }
        for name, (value, (rates, capped, band, game, owned_key)) in cases.items():
            with self.subTest(name):
                row = p0results.run_row(value, a_run(), CONTEXT, [], None)
                self.assertEqual((row["rates"], row["capped"], row["band_m"], row["game"]), (rates, capped, band, game))
                self.assertIn(owned_key, row["owned"])
        row = p0results.run_row(spec(rates=None, env={}), a_run(), CONTEXT, [], None)
        self.assertNotIn("work_ms_per_frame", [m["name"] for m in row["metrics"]])
        self.assertEqual(p0results.run_row(spec(family="rates"), a_run(), CONTEXT, [], None)["model"], "none")

    def test_third_repeat_fields_come_from_the_spec(self):
        cases = {
            "an ordinary run": (spec(), {}),
            "a third repeat before its replaced repeat is known": (
                spec(repeat=3, replaces_repeat=None, third_reason="repeats 1 and 2 differ by 11.0%"),
                {"third_reason": "repeats 1 and 2 differ by 11.0%"}),
            "a third repeat replacing repeat 2": (
                spec(repeat=3, replaces_repeat=2, third_reason="repeats 1 and 2 differ by 11.0%"),
                {"replaces_repeat": 2, "third_reason": "repeats 1 and 2 differ by 11.0%"}),
        }
        for name, (value, expected) in cases.items():
            with self.subTest(name):
                row = p0results.run_row(value, a_run(), CONTEXT, [], None)
                self.assertEqual({key: row[key] for key in p0results.THIRD_REPEAT_FIELDS if key in row}, expected)

    def test_every_metric_is_in_the_vocabulary(self):
        row = p0results.run_row(spec(), a_run(), CONTEXT, [], None)
        stage_0b = p0results.run_row(spec(family="fanout"), stage_0b_run(), CONTEXT, [], None)
        self.assertTrue(stage_0b["accepted"])
        for metric in row["metrics"] + stage_0b["metrics"]:
            with self.subTest(metric["name"]):
                self.assertIn(p0results.metric_base(metric["name"]), p0results.METRIC_NAMES)

    def test_a_stage_0b_row(self):
        stepped = {"steps": [50, 100], "hold": 90}
        value = spec(family="targets", game="fantasy", kind="character", payload_B=None, scale=0.5, stepped=stepped,
                     point={"players": 60, "step": 50},
                     env=dict(spec()["env"], MESH_PHANTOM="virtual:200:character:C"))
        row = p0results.run_row(value, stage_0b_run(), dict(CONTEXT, peer_host="B"), [], None)
        p0results.validate_row(row, "row")
        self.assertEqual({key: row[key] for key in ("stage", "game", "kind", "clients", "v_move_mean", "v_move_p95",
                                                    "payload_B", "scale", "awake_monsters", "peer_host", "peer_cpu",
                                                    "stepped", "point")},
                         {"stage": "0b", "game": "fantasy", "kind": "character",
                          "clients": {"real": 0.0, "phantom_virtual": 200.0, "phantom_sink": 0,
                                      "observer_kind": "character", "connected": 0.0},
                          "v_move_mean": 100.0, "v_move_p95": 100, "payload_B": None, "scale": 0.5,
                          "awake_monsters": 190.0, "peer_host": "B", "peer_cpu": None, "stepped": stepped,
                          "point": {"players": 60, "step": 50}})
        self.assertEqual(p0results.metric_value(row, "datagrams_per_s", cell="C"), 260.0)
        self.assertEqual(p0results.run_row(spec(), a_run(), CONTEXT, [], None)["peer_host"], None)

    def test_stages_and_games_of_the_families(self):
        self.assertEqual(p0results.FAMILIES_0B, {"observed", "fanout", "targets", "syncrpc", "packets", "prewarm",
                                                 "accept-b", "calibrate", "crossings", "jfull"})
        cases = {"presence": ("0a", "fantasy"), "canary": ("0a", "neutral"), "observed": ("0b", "fantasy"),
                 "fanout": ("0b", "fantasy"), "targets": ("0b", "fantasy"), "syncrpc": ("0b", "space"),
                 "packets": ("0b", "fantasy"), "prewarm": ("0b", "fantasy")}
        for family, (stage, game) in cases.items():
            with self.subTest(family):
                row = p0results.run_row(spec(family=family), None, CONTEXT, ["Play failed"], "run failed")
                self.assertEqual((row["stage"], row["game"]), (stage, game))
        self.assertEqual(p0results.run_row(spec(family="observed", game="space"), None, CONTEXT, [], None)["game"],
                         "space")
        self.assertTrue(all(p0results.STAGE[family] == "0b" for family in p0results.FAMILIES_8B))


class ReadWriteTests(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.path = os.path.join(self.folder.name, "p0", "rows.jsonl")

    def tearDown(self):
        self.folder.cleanup()

    def write_text(self, text):
        os.makedirs(os.path.dirname(self.path), exist_ok=True)
        with open(self.path, "w", encoding="utf-8") as f:
            f.write(text)

    def test_read_refusals_name_file_and_line(self):
        good = json.dumps(other_rows()[0], separators=(",", ":"))
        cases = {
            "invalid JSON": (f"{good}\n{{not json\n", "rows.jsonl:2: invalid JSON"),
            "NaN": (good.replace('"value":2.5', '"value":NaN') + "\n", "rows.jsonl:1: invalid JSON"),
            "not an object": (f"{good}\n\n[1, 2]\n", "rows.jsonl:3: a row is a JSON object, not list"),
            "no schema": ('{"row": "fit"}\n', "rows.jsonl:1: no schema field"),
            "another schema": (good.replace("mesh-p0/1", "mesh-p0/2") + "\n",
                               "rows.jsonl:1: schema 'mesh-p0/2' is not mesh-p0/1"),
            "an unknown row type": (good.replace('"row":"fit"', '"row":"plot"') + "\n",
                                    "rows.jsonl:1: unknown row type 'plot'"),
            "a missing field": (good.replace('"lo":2.2,', "") + "\n", "rows.jsonl:1: fit row is missing lo"),
        }
        for name, (text, message) in cases.items():
            with self.subTest(name):
                self.write_text(text)
                with self.assertRaisesRegex(p0results.SchemaError, message):
                    p0results.read_rows([self.path])

    def test_every_required_field(self):
        rows = {"run": p0results.run_row(spec(), a_run(), CONTEXT, [], None),
                "summary": p0results.summary_rows([p0results.run_row(spec(), a_run(), CONTEXT, [], None)])[0],
                "fit": other_rows()[0], "gate": other_rows()[1], "prediction": other_rows()[2]}
        for kind, fields in p0results.REQUIRED.items():
            for field in fields:
                with self.subTest(kind=kind, field=field):
                    row = {key: value for key, value in rows[kind].items() if key != field}
                    with self.assertRaisesRegex(p0results.SchemaError, f"rows.jsonl:1: .*{field}"):
                        p0results.parse_rows("rows.jsonl", json.dumps(row))

    def test_write_refuses_an_unwritable_row(self):
        fit = other_rows()[0]
        cases = {"NaN": dict(fit, value=float("nan")), "a datetime": dict(fit, value=datetime.datetime(2026, 9, 26)),
                 "a missing field": {key: value for key, value in fit.items() if key != "hi"}}
        for name, row in cases.items():
            with self.subTest(name):
                with self.assertRaisesRegex(p0results.SchemaError, "rows.jsonl:2: "):
                    p0results.write_rows(self.path, [fit, row])
                self.assertFalse(os.path.exists(self.path))

    def test_write_then_read(self):
        rows = [p0results.run_row(spec(), a_run(), CONTEXT, [], None)] + other_rows()
        p0results.write_rows(self.path, rows[:2])
        p0results.write_rows(self.path, rows[2:])
        self.assertEqual(p0results.read_rows([self.path]), rows)
        self.assertEqual(p0results.read_rows(self.path), rows)

    def test_extra_fields_blank_lines_and_rows_without_a_row_field(self):
        run = p0results.run_row(spec(), a_run(), CONTEXT, [], None)
        del run["row"]
        run["space_field"] = {"x": " "}
        self.write_text("﻿" + json.dumps(run, ensure_ascii=False) + "\n\n")
        rows = p0results.read_rows([self.path])
        self.assertEqual(rows, [run])
        self.assertEqual(p0results.row_type(rows[0]), "run")

    def test_rows_of_type(self):
        rows = [p0results.run_row(spec(), a_run(), CONTEXT, [], None)] + other_rows()
        self.assertEqual([p0results.row_type(r) for r in rows], ["run", "fit", "gate", "prediction"])
        self.assertEqual(p0results.rows_of_type(rows, "gate"), [rows[2]])


class FitNamesTests(unittest.TestCase):
    def test_fit_names(self):
        cases = {"O_steered": "a_steered", "U_kinematic": "e_kinematic", "links": "lambda", "c0": "c0", "Q": "q",
                 "N2": "q", "G_player": "g_player", "per_frame": "rate_F", "per_step": "rate_Ph",
                 "per_second": "rate_C", "other_per_second": "rate_S", "k_band_kinematic": "k_band_kinematic",
                 "interaction": "rate_I", "kind_per_frame": "rate_kind_F", "kind_per_step": "rate_kind_Ph",
                 "kind_per_second": "rate_kind_C", "entity_per_frame": "rate_entity_F",
                 "entity_per_step": "rate_entity_Ph", "entity_per_second": "rate_entity_C"}
        for name, expected in cases.items():
            with self.subTest(name):
                self.assertEqual(p0results.FIT_NAMES[name], expected)

    def test_section5_names(self):
        with self.assertRaisesRegex(ValueError, "fit name 'O_ships' has no §5 name"):
            p0results.section5_names("O_ships")
        cases = {"k_hold_kinematic": ["k_hold_kinematic", "k_kinematic"], "O_steered": ["a_steered"],
                 "a_steered": ["a_steered"], "k_kinematic": ["k_kinematic"], "lambda": ["lambda"]}
        for name, expected in cases.items():
            with self.subTest(name):
                self.assertEqual(p0results.section5_names(name), expected)

    def test_fit_row(self):
        row = other_rows()[0]
        self.assertEqual({key: row[key] for key in ("row", "family", "game", "fit_id", "name", "value", "se", "lo",
                                                    "hi", "t", "regressors", "n", "method", "reference", "notes")},
                         {"row": "fit", "family": "fit", "game": "fantasy", "fit_id": "presence", "name": "a_steered",
                          "value": 2.5, "se": 0.1, "lo": 2.2, "hi": 2.8, "t": 4.303, "regressors": ["c0", "O_steered"],
                          "n": 12, "method": "ols + jackknife over repeats", "reference": "a_steered", "notes": []})


def stage_0b_run():
    """a_run with cell C's stage 0b record (test_p0cells0b's lines: two Send and Phantom windows, a Prewarm burst...)."""
    run = a_run()
    run["cells"]["C"]["p0b"] = test_p0cells0b.record()
    return run


def canary_row(arm, clock, work, build="b1", accepted=True, host=None):
    return {"schema": "mesh-p0/1", "row": "run", "family": "canary", "arm": arm, "build": build, "accepted": accepted,
            "host": host, "problems": [], "excluded_reason": None, "window_local": {"end": f"2026-09-26T{clock}"},
            "metrics": [] if work is None else [{"name": "work_ms_per_s", "stat": "mean", "value": work, "unit": "ms/s",
                                                 "label": "M", "cell": "C"}]}


def block_row(clock, build="b1", accepted=True, host=None):
    return {"schema": "mesh-p0/1", "row": "run", "family": "presence", "arm": "a", "build": build, "host": host,
            "accepted": accepted, "problems": [], "excluded_reason": None,
            "window_local": {"end": f"2026-09-26T{clock}"}, "metrics": []}


class DriftTests(unittest.TestCase):
    def test_a_drifted_canary_excludes_the_rows_before_it(self):
        rows = [canary_row("baseline", "09:00:00", 99.0), canary_row("baseline", "09:05:00", 101.0),
                block_row("10:00:00"), canary_row("check", "10:10:00", 110.0), canary_row("check", "10:12:00", 101.0),
                block_row("10:20:00"), canary_row("check", "10:30:00", 102.0), block_row("10:40:00")]
        out = p0results.apply_drift(rows)
        self.assertEqual([(r["accepted"], r["excluded_reason"]) for r in (out[2], out[5], out[7])],
                         [(False, "drift"), (True, None), (True, None)])
        self.assertRegex(out[2]["problems"][0], r"^drift: the canary at 2026-09-26T10:10:00 read 110.0 ms/s on C "
                                                r"against its build's baseline 100.0 ms/s \(10.0%, above 5%\)$")
        self.assertEqual((out[5]["problems"], out[7]["problems"]), ([], ["no canary after this run"]))
        self.assertEqual([out[i]["drift"] for i in (3, 4, 6)], [True, False, False])
        self.assertEqual(rows[2]["accepted"], True)
        self.assertNotIn("drift", rows[3])

    def test_drift_boundaries(self):
        cases = {"105.1 against 100": (105.1, True), "105.0 against 100": (105.0, False),
                 "95.0 against 100": (95.0, False)}
        for name, (work, drifted) in cases.items():
            with self.subTest(name):
                out = p0results.apply_drift([canary_row("baseline", "09:00:00", 100.0),
                                             canary_row("check", "10:00:00", work)])
                self.assertIs(out[1]["drift"], drifted)

    def test_canaries_that_cannot_judge(self):
        rows = [canary_row("baseline", "09:00:00", 100.0, build="b0"), block_row("10:00:00"),
                canary_row("check", "10:05:00", 150.0, accepted=False), canary_row("check", "10:10:00", 100.0),
                block_row("10:20:00", build="b0"), block_row("10:30:00", accepted=False)]
        out = p0results.apply_drift(rows)
        self.assertEqual(out[3]["drift"], None)
        self.assertEqual(out[3]["problems"], ["drift not judged: no accepted baseline canary on build b1 host None"])
        self.assertEqual(out[1]["problems"], ["the canary at 2026-09-26T10:10:00 after this run could not be judged: "
                                              "no accepted baseline canary on build b1 host None"])
        self.assertEqual(out[1]["accepted"], True)
        self.assertNotIn("drift", out[2])
        self.assertEqual(out[4]["problems"], ["no canary after this run"])
        self.assertEqual(out[5]["problems"], [])

    def test_canary_baselines_refuse_a_non_positive_baseline(self):
        with self.assertRaisesRegex(p0base.ModelError, "a canary baseline must be positive; got 0 ms/s"):
            p0results.apply_drift([canary_row("baseline", "09:00:00", 0.0), canary_row("check", "10:00:00", 1.0)])
        self.assertEqual(p0results.canary_baselines([canary_row("baseline", "09:00:00", 98.0),
                                                     canary_row("baseline", "09:05:00", 102.0, build="b2"),
                                                     canary_row("baseline", "09:06:00", 104.0, build="b2")]),
                         {("b1", None): 98.0, ("b2", None): 103.0})

    def test_bridge_ok(self):
        base = [canary_row("baseline", "09:00:00", 100.0, build="old", host="A")]
        cases = {"no old baseline": ([canary_row("check", "10:00:00", 100.0, build="new", host="A")],
                                     "build old has no accepted baseline canary on host A to bridge from"),
                 "the old baseline on another host": (
                     [canary_row("baseline", "09:00:00", 100.0, build="old", host="B"),
                      canary_row("check", "10:00:00", 100.0, build="new", host="A")],
                     "build old has no accepted baseline canary on host A to bridge from"),
                 "no new canary": (base + [canary_row("check", "10:00:00", 100.0, build="new", accepted=False,
                                                      host="A")],
                                   "build new has no accepted canary check on host A to bridge with"),
                 "the new canary on another host": (base + [canary_row("check", "10:00:00", 100.0, build="new",
                                                                       host="B")],
                                                    "build new has no accepted canary check on host A to bridge with")}
        for name, (rows, message) in cases.items():
            with self.subTest(name):
                with self.assertRaisesRegex(p0base.ModelError, message):
                    p0results.bridge_ok(rows, "old", "new", "A")
        self.assertFalse(p0results.bridge_ok(base + [canary_row("check", "10:00:00", 105.1, build="new", host="A")],
                                             "old", "new", "A"))
        self.assertTrue(p0results.bridge_ok(base + [canary_row("check", "10:00:00", 95.0, build="new", host="A")],
                                            "old", "new", "A"))


class SummaryTests(unittest.TestCase):
    def test_summary_row_refusals(self):
        with self.assertRaisesRegex(ValueError, "a summary of cell C needs at least one run row"):
            p0results.summary_row([], "C")
        with self.assertRaisesRegex(ValueError, "run k1 has no work_ms_per_s on cell E"):
            p0results.summary_row([p0results.run_row(spec(), a_run(), CONTEXT, [], None)], "E")

    def test_summary_rows(self):
        runs = [p0results.run_row(spec(repeat=r, key=f"k{r}"), a_run(), CONTEXT, [], None) for r in (1, 2, 3)]
        runs[1]["metrics"] = [dict(m, value=360.0) if m["name"] == "work_ms_per_s" else m for m in runs[1]["metrics"]]
        runs[2]["metrics"] = [dict(m, value=270.0) if m["name"] == "work_ms_per_s" else m for m in runs[2]["metrics"]]
        other_rates = p0results.run_row(spec(rates="30/0.02", key="k4"), a_run(), CONTEXT, [], None)
        refused = p0results.run_row(spec(key="k5"), a_run(), CONTEXT, ["C started 2 times"], None)
        summaries = p0results.summary_rows(runs + [other_rates, refused] + other_rows())
        self.assertEqual([(s["rates"], s["n"], s["plan_keys"]) for s in summaries],
                         [("60/0.03", 3, ["k1", "k2", "k3"]), ("30/0.02", 1, ["k4"])])
        first = summaries[0]
        p0results.validate_row(first, "summary")
        self.assertEqual({key: first[key] for key in ("row", "game", "family", "case", "arm", "point", "cell", "build",
                                                      "cpu", "work_ms_per_s_median", "work_ms_per_s_min",
                                                      "work_ms_per_s_max", "nonaoi_tick_frame_p95_ms_median",
                                                      "tick_send_p95_ms_median", "owned", "puppets", "clients")},
                         {"row": "summary", "game": "fantasy", "family": "presence", "case": "main",
                          "arm": "puppets-kinematic", "point": {"players": 60}, "cell": "C", "build": "abc123",
                          "cpu": "Test CPU", "work_ms_per_s_median": 300.0, "work_ms_per_s_min": 270.0,
                          "work_ms_per_s_max": 360.0, "nonaoi_tick_frame_p95_ms_median": 2.5,
                          "tick_send_p95_ms_median": 0.65, "owned": 62.5, "puppets": 0.0, "clients": 0})

    def test_header_of(self):
        with self.assertRaisesRegex(ValueError, "a header needs at least one row"):
            p0results.header_of([])
        rows = [{"team": "rig", "game": "fantasy", "family": "presence", "stage": "0a", "build": build, "cpu": "X"}
                for build in ("b2", "b1")]
        with self.assertRaisesRegex(ValueError, r"rows from 2 hosts \(A, None\) are never pooled: one host at a time"):
            p0results.header_of(rows + [dict(rows[0], host="A")])
        self.assertEqual(p0results.header_of(rows, family="fit"),
                         {"team": "rig", "game": "fantasy", "family": "fit", "stage": "0a", "build": "b1+b2", "cpu": "X",
                          "host": None})

    def test_rates_label(self):
        cases = {"none": ({"rates": {"fps": None, "fixed_step_s": None}}, None),
                 "capped": ({"rates": {"fps": 60, "fixed_step_s": 0.03}, "capped": True}, "60/0.03"),
                 "uncapped": ({"rates": {"fps": 60, "fixed_step_s": 0.03}, "capped": False}, "60/0.03 uncapped")}
        for name, (row, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0results.rates_label(row), expected)

    def test_metric_value(self):
        row = {"metrics": [{"name": "work_ms_per_s", "stat": "mean", "value": 1.0, "cell": "C"},
                           {"name": "work_ms_per_s", "stat": "mean", "value": 2.0, "cell": "E"}]}
        with self.assertRaisesRegex(ValueError, "metric work_ms_per_s mean appears 2 times .*name one cell"):
            p0results.metric_value(row, "work_ms_per_s")
        self.assertEqual(p0results.metric_value(row, "work_ms_per_s", cell="E"), 2.0)
        self.assertIsNone(p0results.metric_value(row, "work_ms_per_s", "p95", "C"))


class ContextTests(unittest.TestCase):
    def test_parse_power_plan(self):
        cases = {"none": (None, None), "other text": ("Invalid Parameters -- try \"/?\" for help", None),
                 "balanced": ("Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)\r\n", "Balanced"),
                 "localised": ("Energieschema-GUID: 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c  (Höchstleistung)",
                               "Höchstleistung")}
        for name, (text, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0results.parse_power_plan(text), expected)

    def test_kit_patches(self):
        texts = ["// MESH-EDIT: CE-6 — was a delay", "x // MESH-EDIT: CE-11", "// MESH-EDIT: CE-10 extra"]
        self.assertEqual(p0results.kit_patches(texts),
                         {"CE-6": "present", "CE-7": "absent", "CE-8": "absent", "CE-9": "absent", "CE-11": "present",
                          "CE-12": "absent", "CE-10": "present"})
        self.assertEqual(p0results.kit_patches(["MESH-EDIT: CE-1"], expected=("CE-1", "CE-11")),
                         {"CE-1": "present", "CE-11": "absent"})

    def test_file_name(self):
        cases = {"run rows": (("fantasy", "presence", "20260927_101500"), "rig_fantasy_presence_20260927_101500.jsonl"),
                 "canaries": (("neutral", "canary", "20260927_101500"), "rig_neutral_canary_20260927_101500.jsonl"),
                 "the report": (("fantasy", "fit", "20260927_101500"), "rig_fantasy_fit_20260927_101500.jsonl"),
                 "predictions": (("fantasy", p0results.PREDICTION_FAMILY, "20260927_101500"),
                                 "rig_fantasy_abgeo-prediction_20260927_101500.jsonl")}
        for name, (args, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0results.file_name(*args), expected)
        self.assertEqual(p0results.FAMILY_GAME["canary"], "neutral")

    def test_row_headers(self):
        gate, prediction = other_rows()[1], other_rows()[2]
        self.assertEqual({key: gate[key] for key in p0results.HEADER_SUBSET},
                         {"schema": "mesh-p0/1", "row": "gate", "team": "rig", "game": "fantasy", "family": "presence",
                          "stage": "0a", "build": "abc123", "cpu": "Test CPU"})
        self.assertEqual({key: prediction[key] for key in ("row", "game", "family", "build", "created_utc", "ratio")},
                         {"row": "prediction", "game": "fantasy", "family": "abgeo", "build": None,
                          "created_utc": "2026-09-27T12:00:00", "ratio": 0.61})


if __name__ == "__main__":
    unittest.main()
