"""Unit tests for p0plans8b and p0load's step 8b commands (python -m unittest discover -s Tools/ServerMeshing -p
"test_*.py"). Nothing here touches the editor, the database or machine B: p0load8b's calibration reader and B's
refusals are patched."""
import contextlib
import io
import unittest
from unittest import mock

import p0dryrun
import p0load
import p0load8b
import p0plans
import p0plans0b
import p0plans8b
import rigrun

COUNTS = {"accept-b": 19, "calibrate": 4, "jfull": 4, "packets --sink": 22, "crossings": 18}
FITS = {"build": "x", "tried": 100, "in_map": 100, "hosted": 100, "fits": True, "upload_pkt_per_s": 10.4,
        "upload_hz": 10}
PEER_BOTS = ("first", "n", "mode", "placement", "bot_net", "rig_ships")


def p0load_main(argv, found=None, refusals=()):
    out = io.StringIO()
    with contextlib.redirect_stdout(out), mock.patch.object(p0load8b, "calibration", return_value=found), \
            mock.patch.object(p0load8b, "start_problems", return_value=list(refusals)):
        code = p0load.main(argv)
    return code, out.getvalue()


def plans(found=FITS):
    return {"accept-b": p0plans8b.accept_b_plan(), "calibrate": p0plans8b.calibrate_plan(found),
            "jfull": p0plans8b.jfull_plan(found), "packets --sink": p0plans8b.packets_sink_plan(),
            "crossings": p0plans8b.crossings_plan()}


class RefusalTests(unittest.TestCase):
    def test_refusals(self):
        cases = {
            "an unknown accept-b part": (lambda: p0plans8b.accept_b_plan("A,G"), "parts A, B, C, D, E, F; got G"),
            "no accept-b part": (lambda: p0plans8b.accept_b_plan(""), "got none"),
            "no bots": (lambda: p0plans8b.bots(0, "pace", "spread"), "at least one"),
            "meshbot itself": (lambda: p0plans8b.bots(5, "pace", "spread", first=1), "meshbot2 and up"),
            "an upload rate the key refuses": (lambda: p0plans8b.upload_hz({"upload_hz": 61.0}), "outside"),
            "jfull without a calibration": (lambda: p0plans8b.jfull_plan(None), "run p0load calibrate-bots first"),
            "jfull when 100 do not fit": (lambda: p0plans8b.jfull_plan(dict(FITS, fits=False, hosted=81)),
                                          "B cannot host 100 bots"),
            "a peer run past the pace stop": (
                lambda: p0plans8b._spec8b("crossings", "x", "y", {}, 1, "fantasy", "character", {}, "30/0.02",
                                          p0plans8b._peer(bot_part=p0plans8b.bots(500, "pace", "spread"))),
                "outlasts a bot's 600 s of pacing"),
        }
        for name, (call, message) in cases.items():
            with self.subTest(name):
                with self.assertRaisesRegex(ValueError, message):
                    call()

    def test_jfull_refusal(self):
        cases = {"no file": (None, "calibrate-bots"), "too few tried": (dict(FITS, tried=50), "cannot host"),
                 "not all in the map": (dict(FITS, fits=False), "cannot host"), "fits": (FITS, None)}
        for name, (found, text) in cases.items():
            with self.subTest(name):
                refusal = p0plans8b.jfull_refusal(found)
                self.assertIsNone(refusal) if text is None else self.assertIn(text, refusal)


class PlanTests(unittest.TestCase):
    def test_run_counts(self):
        for name, specs in plans().items():
            with self.subTest(name):
                self.assertEqual(len(specs), COUNTS[name])
                self.assertEqual([spec["order"] for spec in specs], list(range(1, len(specs) + 1)))
                self.assertEqual(len({spec["key"] for spec in specs}), len(specs))
                for spec in specs:
                    rigrun.check_env(spec["env"])
                    self.assertEqual(spec["key"], p0plans.plan_key(spec))

    def test_character_and_eye_phantoms_have_a_view_or_an_area(self):
        """The cell refuses MESH_PHANTOM without MESH_PHANTOM_VIEW or MESH_PHANTOM_AREA (character and eye kinds) and
        installs no phantom (the 2026-10-08 smoke: accept-b part A ran with none)."""
        for name, specs in plans().items():
            for spec in specs:
                phantom = spec["env"].get("MESH_PHANTOM", "")
                if phantom.split(":")[2:3] in (["character"], ["eye"]):
                    with self.subTest(name, key=spec["key"], phantom=phantom):
                        self.assertTrue(("MESH_PHANTOM_VIEW" in spec["env"]) != ("MESH_PHANTOM_AREA" in spec["env"]))

    def test_peer_dicts_are_decision_8s(self):
        for name, specs in plans().items():
            for spec in specs:
                with self.subTest(name, key=spec["key"]):
                    peer = spec["peer"]
                    if peer is None:
                        continue
                    self.assertEqual(set(peer), {"sink", "bots"})
                    if peer["bots"]:
                        self.assertEqual(tuple(peer["bots"]), PEER_BOTS)
                        self.assertGreaterEqual(peer["bots"]["first"], 2)
                        self.assertLess(spec["warmup"] + spec["measure"], p0plans8b.PACE_STOP_S)
                    if peer["sink"]:
                        self.assertEqual(set(peer["sink"]), {"count", "kind", "sizes"})
                        self.assertTrue(spec["env"]["MESH_PHANTOM"].startswith(f"sink:{peer['sink']['count']}:"))

    def test_accept_b_parts(self):
        specs = p0plans8b.accept_b_plan()
        parts = [spec["accept_part"] for spec in specs]
        self.assertEqual({part: parts.count(part) for part in "ABCDEF"},
                         {"A": 2, "B": 1, "C": 4, "D": 1, "E": 1, "F": 10})
        self.assertEqual([spec["accept_part"] for spec in p0plans8b.accept_b_plan("f,a")], ["A"] * 2 + ["F"] * 10)
        by_part = {part: [spec for spec in specs if spec["accept_part"] == part] for part in "ABCDEF"}
        self.assertEqual([s["env"]["MESH_PHANTOM"] for s in by_part["A"]],
                         ["virtual:50:character:C", "sink:50:character:C"])
        self.assertEqual((by_part["B"][0]["env"]["MESH_PHANTOM_VIEW"], by_part["B"][0]["peer"]["bots"]["n"]), ("200", 1))
        self.assertEqual([(s["arm"], s["repeat"]) for s in by_part["C"]],
                         [("kit", 1), ("sender", 1), ("sender", 2), ("kit", 2)])
        self.assertEqual(by_part["C"][0]["env"]["MESH_LOAD_PLAN"], "C:50")
        self.assertTrue(by_part["D"][0]["peer"]["bots"]["rig_ships"])
        self.assertEqual((by_part["E"][0]["env"]["MESH_CE12_CHECK"], by_part["E"][0]["peer"]), ("1", None))
        meter = [(s["arm"], "MESH_METER_SENDS" in s["env"]) for s in by_part["F"]]
        self.assertEqual(set(meter), {("meter-off", False), ("meter-on", True)})
        self.assertEqual([s["repeat"] for s in by_part["F"]], [1, 1, 2, 2, 3, 3, 4, 4, 5, 5])

    def test_gate_j_arms(self):
        for name, specs, p in (("calibrate", p0plans8b.calibrate_plan(FITS), 50),
                               ("jfull", p0plans8b.jfull_plan(FITS), 100)):
            with self.subTest(name):
                self.assertEqual([(s["arm"], s["point"]["load"]) for s in specs],
                                 [("kit", "bots"), ("kit", "phantoms"), ("char-sender", "bots"),
                                  ("char-sender", "phantoms")])
                bots, phantoms = specs[0], specs[1]
                self.assertEqual((bots["peer"]["bots"]["n"], bots["peer"]["bots"]["placement"], bots["warmup"]),
                                 (p, "spread", p + 45))
                self.assertEqual((phantoms["env"]["MESH_PHANTOM_UPLOAD_HZ"], phantoms["env"]["MESH_PHANTOM_AREA"],
                                  phantoms["upload_hz"]), ("10", "0,0,100,100", 10))
                self.assertEqual(specs[2]["env"]["MESH_SENDER_TIERS"], "20/20/20")
        missing = p0plans8b.calibrate_plan(None)[1]
        self.assertEqual((missing["upload_hz"], "MESH_PHANTOM_UPLOAD_HZ" in missing["env"]), (None, False))
        self.assertEqual(p0plans8b.upload_hz({"upload_hz": 10.6}), 11)

    def test_packets_sink(self):
        specs = p0plans8b.packets_sink_plan()
        virtual = {s["key"] for s in p0plans0b.packets_plan() if s["case"] == "cap" and s["point"]["P"] == 100}
        self.assertEqual({s["key"] for s in specs if s["case"] == "cap"}, virtual)   # 8a's keys, re-run on 8b
        sink = [s for s in specs if s["case"] == "cap-sink"]
        self.assertEqual({s["env"]["MESH_PHANTOM"] for s in sink}, {"sink:100:ship:C"})
        probes = [s for s in specs if s["case"] == "probe"]
        self.assertEqual([s["point"]["P"] for s in probes], [100, 200, 400, 800])
        self.assertTrue(all(s["saturation_probe"] and not s["stepped"] and s["env"]["MESH_THREAD_CPU"] == "1"
                            and s["env"]["MESH_PHANTOM_VIEW"] == "100" for s in probes))
        self.assertEqual(specs[-4:], probes)

    def test_crossings(self):
        specs = p0plans8b.crossings_plan()
        walks = [s for s in specs if s["case"] == "walk"]
        self.assertEqual(len(walks), 12)
        for spec in walks:
            with self.subTest(spec["key"]):
                n = spec["point"]["bots"]
                self.assertEqual((spec["warmup"], spec["measure"], spec["stop_after_outcomes"]), (10, n + 120, 2 * n))
                self.assertEqual(spec["layout"], "halves")
                asleep = spec["arm"] == "asleep"
                self.assertEqual(spec["coverage_exempt"], ["E"] if asleep else [])
                self.assertEqual(spec["env"].get("MESH_SLEEP_RINGS"), "0" if asleep else None)
        pace = [s for s in specs if s["case"] == "pace"]
        self.assertEqual([(s["arm"], s["repeat"]) for s in pace],
                         [(arm, r) for r in (1, 2, 3) for arm in ("pace-cut", "pace-west")])
        for left, right in zip(pace[::2], pace[1::2]):
            self.assertEqual(specs.index(right), specs.index(left) + 1)   # interleaved: each pair back to back
        self.assertTrue(all((s["warmup"], s["measure"]) == (65, 60) for s in pace))


class CommandTests(unittest.TestCase):
    def test_dry_runs_print_the_plans(self):
        for argv, count in ((["accept-b"], 19), (["calibrate"], 4), (["jfull"], 4), (["packets", "--sink"], 22),
                            (["crossings"], 18), (["packets"], 24)):
            with self.subTest(argv):
                code, out = p0load_main(argv + ["--dry-run"], FITS)
                self.assertEqual(code, 0)
                self.assertIn(f"{count} runs, about", out)
                if argv != ["packets"]:
                    self.assertIn("machine B: a real run refuses", out)
        code, out = p0load_main(["jfull", "--dry-run"])
        self.assertEqual(code, 0)
        self.assertIn("jfull is not planned: no bot calibration", out)
        self.assertIn("MESH_PHANTOM_UPLOAD_HZ not calibrated yet", p0load_main(["calibrate", "--dry-run"])[1])
        self.assertIn("11 runs, about", p0load_main(["accept-b", "--parts", "E,F", "--dry-run"])[1])

    def test_procedures(self):
        cases = {"no bots": (["register-bots", "--n", "0", "--dry-run"], 2, "refused: --n 0"),
                 "register dry run": (["register-bots", "--n", "3", "--dry-run"], 0, "meshbot2..meshbot4"),
                 "calibrate dry run": (["calibrate-bots", "--dry-run"], 0, "bots_calibration.json")}
        for name, (argv, code, text) in cases.items():
            with self.subTest(name):
                got, out = p0load_main(argv)
                self.assertEqual(got, code)
                self.assertIn(text, out)

    def test_real_runs_refuse_without_b(self):
        with mock.patch.object(rigrun, "start_problems", side_effect=lambda: []), \
                mock.patch.object(p0load.p0blocks, "read_history", return_value=([], [])):
            code, out = p0load_main(["crossings", "--no-canary"], refusals=["B is unreachable: timed out"])
            self.assertEqual((code, out.strip()), (2, "B is unreachable: timed out"))
            code, out = p0load_main(["calibrate", "--no-canary"])
            self.assertEqual(code, 2)
            self.assertIn("calibrate: no bot calibration on this build", out)
            code, out = p0load_main(["jfull"])
            self.assertEqual((code, out.strip()), (2, "refused: jfull: no bot calibration on this build: run p0load "
                                                       "calibrate-bots first"))
        self.assertEqual(p0load_main(["accept-b", "--resume"])[0], 2)

    def test_canaried_and_dry_run_text(self):
        self.assertTrue(set(p0plans8b.FAMILIES) <= set(p0load.CANARY_FAMILIES))
        spec = p0plans8b.crossings_plan()[0]
        self.assertIsNone(p0dryrun.predict_load(spec))
        self.assertEqual(p0dryrun.peer_text(spec), "bots meshbot2..meshbot21 (20) walk at west, Bot net 5 s; walk "
                                                   "across the cut and back, stop at 40 handoff outcomes")
        self.assertEqual(p0dryrun.peer_text(p0plans8b.calibrate_plan(None)[1]),
                         "sink 50 character peers; MESH_PHANTOM_UPLOAD_HZ not calibrated yet")


if __name__ == "__main__":
    unittest.main()
