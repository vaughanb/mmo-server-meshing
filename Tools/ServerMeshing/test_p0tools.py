"""Unit tests for p0tools (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py")."""
import datetime
import unittest

import p0tools
import regress
import rigtools

WORK_TICKS_SAMPLE = (
    "Work ticks 5.00 s: ticks 100 expected 100 dropped 0 cap 0 unclosed 0 | frames 150 t0 50 t1 100 t2 0 "
    "| tick frames mean 3.412 ms p95 4.10 ms p99 5.20 ms max 6.1 ms | nonaoi p95 4.00 ms p99 4.50 ms "
    "| aoi passes 5 frames 5 mean 4.000 ms max 8.2 ms | other mean 1.203 ms p95 1.50 ms "
    "| kit mean 0.812 ms p95 1.00 ms p99 1.20 ms max 2.0 ms | parts 100 sync 0.300 ms aoi 0.050 ms move 0.462 ms "
    "| late 150 mean 0.020 ms max 0.9 ms 0.6 ms/s | send 100 mean 0.462 ms p95 0.60 ms p99 0.70 ms "
    "| physics 50.0/s mean 1.67 max 2 f2 100 | work 312.4 ms/s busy 31.2% | budget 33.33 ms | gc0 0 | outside 0")
WORK_TICKS_FIELDS = {
    "seconds": 5.0, "ticks": 100, "expected": 100, "dropped": 0, "cap": 0, "unclosed": 0,
    "frames": 150, "t0": 50, "t1": 100, "t2": 0,
    "tick_frame_mean": 3.412, "tick_frame_p95": 4.1, "tick_frame_p99": 5.2, "tick_frame_max": 6.1,
    "nonaoi_p95": 4.0, "nonaoi_p99": 4.5,
    "aoi_passes": 5, "aoi_frames": 5, "aoi_frame_mean": 4.0, "aoi_frame_max": 8.2,
    "other_mean": 1.203, "other_p95": 1.5,
    "kit_mean": 0.812, "kit_p95": 1.0, "kit_p99": 1.2, "kit_max": 2.0,
    "parts": 100, "sync_mean": 0.3, "aoi_schedule_mean": 0.05, "move_mean": 0.462,
    "late_calls": 150, "late_mean": 0.02, "late_max": 0.9, "late_ms_per_s": 0.6,
    "send_samples": 100, "send_mean": 0.462, "send_p95": 0.6, "send_p99": 0.7,
    "physics_per_s": 50.0, "physics_mean": 1.67, "physics_max": 2, "physics_f2": 100,
    "work_ms_per_s": 312.4, "busy_percent": 31.2, "budget": 33.33, "gc0": 0, "outside": 0}

RATES_SAMPLE = ("Rates: fps 30 (MESH_RATES; kit 60) vSync 0 | tick 20 Hz (updateFps unchanged) | physics 0.02 s "
                "(project 0.03) | maxDeltaTime 0.333 | probe budget 33.33 ms | uncapped cells -")
RATES_FIELDS = {"fps": 30, "nominal": 0, "from_env": True, "kit": 60, "vsync": 0, "tick_hz": 20, "step": 0.02,
                "project_step": 0.03, "max_delta": 0.333, "budget": 33.33, "uncapped_cells": []}

THREADS_SAMPLE = ("Threads window 5.00 s: main 812.3 litenetlib 20.1 jobs 150.0 unnamed 0.0 other 12.5 total 994.9 "
                  "process 1010.2 ms/s | threads 57 sampler 0.9 ms/s clock cycles 2.994 GHz names pre-start=absent")
THREADS_FIELDS = {"seconds": 5.0, "main": 812.3, "litenetlib": 20.1, "jobs": 150.0, "unnamed": 0.0, "other": 12.5,
                  "total": 994.9, "process": 1010.2, "threads": 57, "sampler_ms_per_s": 0.9, "clock": "cycles",
                  "ghz": 2.994, "names_pre_start": "absent"}
THREADS_TICKS_SAMPLE = THREADS_SAMPLE.replace("clock cycles 2.994 GHz", "clock ticks")

# The step 3 contract's line, N standing for each count and rate.
PUPPETS_TEMPLATE = ("Puppets window 10.00 s: kinematic N steered N | fallback mounted N summoned N handler N grid N "
                    "install N | snaps N | observed ticks N/s unobserved poses N/s | steer kinematic N mean 0.00 m "
                    "p95 0.00 m max 0.00 m | steer steered N mean 0.00 m p95 0.00 m max 0.00 m")
PUPPETS_ZERO_FIELDS = {"seconds": 10.0, "kinematic": 0, "steered": 0, "fallback_mounted": 0, "fallback_summoned": 0,
                       "fallback_handler": 0, "fallback_grid": 0, "fallback_install": 0, "snaps": 0,
                       "observed_ticks_per_s": 0.0, "unobserved_poses_per_s": 0.0,
                       "steer_kinematic_samples": 0, "steer_kinematic_mean": 0.0, "steer_kinematic_p95": 0.0,
                       "steer_kinematic_max": 0.0, "steer_steered_samples": 0, "steer_steered_mean": 0.0,
                       "steer_steered_p95": 0.0, "steer_steered_max": 0.0}
PUPPETS_SAMPLE = ("Puppets window 10.00 s: kinematic 12 steered 3 | fallback mounted 1 summoned 2 handler 0 grid 0 "
                  "install 0 | snaps 4 | observed ticks 600/s unobserved poses 85/s | steer kinematic 240 mean 0.12 m "
                  "p95 0.35 m max 1.20 m | steer steered 60 mean 0.40 m p95 1.10 m max 2.75 m")
PUPPETS_FIELDS = dict(PUPPETS_ZERO_FIELDS, kinematic=12, steered=3, fallback_mounted=1, fallback_summoned=2, snaps=4,
                      observed_ticks_per_s=600.0, unobserved_poses_per_s=85.0,
                      steer_kinematic_samples=240, steer_kinematic_mean=0.12, steer_kinematic_p95=0.35,
                      steer_kinematic_max=1.2, steer_steered_samples=60, steer_steered_mean=0.4, steer_steered_p95=1.1,
                      steer_steered_max=2.75)

STAMP = "2026-09-26 10:00:05"
END = datetime.datetime(2026, 9, 26, 10, 0, 5)


def stamped(message):
    return f" INFO Mesh [{STAMP}] - {message}"


class WorkTicksTests(unittest.TestCase):
    def test_parse_work_ticks(self):
        cases = {
            "a work window line": (stamped("Work window 5.00 s: frames 150 work mean 3.000 ms p95 4.00 ms max 6.0 ms"), None),
            "a bins line": (stamped("Work bins 5.00 s: tick 50:3 51:7 over:0 | nonaoi over:0 | other over:0 | kit over:0 "
                                    "| send over:0"), None),
            "a truncated line": (WORK_TICKS_SAMPLE[:150], None),
            "a negative count": (WORK_TICKS_SAMPLE.replace("dropped 0", "dropped -1"), None),
            "NaN": (WORK_TICKS_SAMPLE.replace("mean 3.412 ms", "mean NaN ms"), None),
            "the contract sample": (WORK_TICKS_SAMPLE, dict(WORK_TICKS_FIELDS, end=None)),
            "a log line": (stamped(WORK_TICKS_SAMPLE), dict(WORK_TICKS_FIELDS, end=END)),
            "all zeros": (stamped(WORK_TICKS_SAMPLE.replace("ticks 100 expected 100", "ticks 0 expected 0")),
                          dict(WORK_TICKS_FIELDS, ticks=0, expected=0, end=END)),
        }
        for name, (line, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0tools.parse_work_ticks(line), expected)

    def test_counts_are_ints_and_the_rest_floats(self):
        fields = p0tools.parse_work_ticks(WORK_TICKS_SAMPLE)
        for name, value in fields.items():
            if name == "end":
                continue
            with self.subTest(name):
                self.assertIs(type(value), int if name in p0tools.WORK_TICKS_INTS else float)

    def test_work_window_parser_ignores_work_ticks(self):
        self.assertIsNone(rigtools.parse_work_window(stamped(WORK_TICKS_SAMPLE)))


class RatesTests(unittest.TestCase):
    def test_parse_rates(self):
        cases = {
            "another line": (stamped("Load generator: frame rate uncapped on this cell (frame time = work per frame)"), None),
            "the cell line": (stamped("Rates: cell C uncapped (nominal 30 fps)"), None),
            "an unknown source": (RATES_SAMPLE.replace("(MESH_RATES;", "(ENV;"), None),
            "a negative budget": (RATES_SAMPLE.replace("budget 33.33", "budget -33.33"), None),
            "the contract sample": (RATES_SAMPLE, dict(RATES_FIELDS, end=None)),
            "a log line": (stamped(RATES_SAMPLE), dict(RATES_FIELDS, end=END)),
            "uncapped at a nominal rate with cells": (
                stamped(RATES_SAMPLE.replace("fps 30", "fps u30").replace("cells -", "cells C,E")),
                dict(RATES_FIELDS, fps=-1, nominal=30, uncapped_cells=["C", "E"], end=END)),
            "rates unset": (
                RATES_SAMPLE.replace("fps 30 (MESH_RATES;", "fps 60 (MESH_RATES unset;").replace("0.02 s", "0.03 s")
                .replace("33.33", "16.67").replace("cells -", "cells C"),
                dict(RATES_FIELDS, fps=60, from_env=False, step=0.03, budget=16.67, uncapped_cells=["C"], end=None)),
            "bare u with an uncapped kit": (
                RATES_SAMPLE.replace("fps 30", "fps u").replace("kit 60", "kit u").replace("33.33", "0.00"),
                dict(RATES_FIELDS, fps=-1, nominal=0, kit=-1, budget=0.0, end=None)),
        }
        for name, (line, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0tools.parse_rates(line), expected)

    def test_parse_rates_cell(self):
        cases = {
            "the rates line": (stamped(RATES_SAMPLE), None),
            "no nominal": (stamped("Rates: cell C uncapped"), None),
            "bare message": ("Rates: cell C uncapped (nominal 30 fps)", {"end": None, "cell": "C", "nominal": 30}),
            "a log line": (stamped("Rates: cell C.1 uncapped (nominal 60 fps)"), {"end": END, "cell": "C.1", "nominal": 60}),
        }
        for name, (line, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0tools.parse_rates_cell(line), expected)


class ThreadsWindowTests(unittest.TestCase):
    def test_parse_threads_window(self):
        cases = {
            "a work ticks line": (WORK_TICKS_SAMPLE, None),
            "an unknown probe result": (THREADS_SAMPLE.replace("pre-start=absent", "pre-start=maybe"), None),
            "a negative value": (THREADS_SAMPLE.replace("other 12.5", "other -12.5"), None),
            "the old tail with sampler wall ms": (THREADS_SAMPLE.replace("sampler 0.9 ms/s clock cycles 2.994 GHz",
                                                                         "sampler 1.2 ms"), None),
            "no clock": (THREADS_SAMPLE.replace("clock cycles 2.994 GHz ", ""), None),
            "cycles without a rate": (THREADS_SAMPLE.replace("cycles 2.994 GHz", "cycles"), None),
            "ticks with a rate": (THREADS_SAMPLE.replace("cycles 2.994 GHz", "ticks 2.994 GHz"), None),
            "an unknown clock": (THREADS_SAMPLE.replace("cycles 2.994 GHz", "wall"), None),
            "the contract sample": (THREADS_SAMPLE, dict(THREADS_FIELDS, end=None)),
            "the scheduler-tick fallback": (THREADS_TICKS_SAMPLE, dict(THREADS_FIELDS, clock="ticks", ghz=None, end=None)),
            "a log line in ticks": (stamped(THREADS_TICKS_SAMPLE),
                                    dict(THREADS_FIELDS, clock="ticks", ghz=None, end=END)),
            "a log line with names present": (stamped(THREADS_SAMPLE.replace("absent", "present")),
                                              dict(THREADS_FIELDS, names_pre_start="present", end=END)),
            "names unavailable": (THREADS_SAMPLE.replace("absent", "unavailable"),
                                  dict(THREADS_FIELDS, names_pre_start="unavailable", end=None)),
        }
        for name, (line, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0tools.parse_threads_window(line), expected)

    def test_types(self):
        for line in (THREADS_SAMPLE, THREADS_TICKS_SAMPLE):
            fields = p0tools.parse_threads_window(line)
            for name, value in fields.items():
                with self.subTest(line=fields["clock"], field=name):
                    if name == "threads":
                        self.assertIs(type(value), int)
                    elif name in p0tools.THREADS_WINDOW_FLOATS:
                        self.assertIs(type(value), float)


class PuppetsWindowTests(unittest.TestCase):
    def test_parse_puppets_window(self):
        cases = {
            "a work ticks line": (WORK_TICKS_SAMPLE, None),
            "the unfilled template": (PUPPETS_TEMPLATE, None),
            "no fallback group": (
                PUPPETS_SAMPLE.replace("| fallback mounted 1 summoned 2 handler 0 grid 0 install 0 ", ""), None),
            "a negative count": (PUPPETS_SAMPLE.replace("snaps 4", "snaps -4"), None),
            "NaN": (PUPPETS_SAMPLE.replace("p95 0.35 m", "p95 NaN m"), None),
            "no kinematic steer unit": (PUPPETS_SAMPLE.replace("max 1.20 m", "max 1.20"), None),
            "no steered steer unit": (PUPPETS_SAMPLE.replace("max 2.75 m", "max 2.75"), None),
            "a fractional sample count": (PUPPETS_SAMPLE.replace("steered 60 mean", "steered 60.5 mean"), None),
            "no kinematic sample count": (PUPPETS_SAMPLE.replace("kinematic 240 mean", "kinematic mean"), None),
            "no steered steer group": (PUPPETS_SAMPLE.split(" | steer steered")[0], None),
            "the old single steer group": (
                PUPPETS_SAMPLE.split("| steer kinematic")[0] + "| steer mean 0.12 m p95 0.35 m max 1.20 m", None),
            "the template filled with zeros": (PUPPETS_TEMPLATE.replace(" N ", " 0 ").replace(" N/s", " 0/s"),
                                               dict(PUPPETS_ZERO_FIELDS, end=None)),
            "a sample": (PUPPETS_SAMPLE, dict(PUPPETS_FIELDS, end=None)),
            "a log line": (stamped(PUPPETS_SAMPLE), dict(PUPPETS_FIELDS, end=END)),
            "fractional rates": (
                PUPPETS_SAMPLE.replace("600/s", "599.5/s").replace("85/s", "84.9/s"),
                dict(PUPPETS_FIELDS, observed_ticks_per_s=599.5, unobserved_poses_per_s=84.9, end=None)),
        }
        for name, (line, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0tools.parse_puppets_window(line), expected)

    def test_counts_are_ints_and_the_rest_floats(self):
        fields = p0tools.parse_puppets_window(PUPPETS_SAMPLE)
        for name, value in fields.items():
            if name == "end":
                continue
            with self.subTest(name):
                self.assertIs(type(value), int if name in p0tools.PUPPETS_WINDOW_INTS else float)


class RatesSettingTests(unittest.TestCase):
    def test_rates_setting(self):
        shapes = "<fps>/<step>, u/<step> or u<fps>/<step>"
        fps_error = "is not an integer from 1 to 1000, u, or u and such an integer"
        cases = {
            "none": (None, (None, None, None, "is empty; expected " + shapes)),
            "empty": ("", (None, None, None, "is empty; expected " + shapes)),
            "whitespace": ("   ", (None, None, None, "is empty; expected " + shapes)),
            "no slash": ("abc", (None, None, None, f"'abc' is not {shapes}")),
            "fps without a step": ("30", (None, None, None, f"'30' is not {shapes}")),
            "two slashes": ("30/0.02/1", (None, None, None, f"'30/0.02/1' is not {shapes}")),
            "zero fps": ("0/0.02", (None, None, None, f"fps part '0' {fps_error}")),
            "fps above 1000": ("1001/0.02", (None, None, None, f"fps part '1001' {fps_error}")),
            "signed fps": ("-30/0.02", (None, None, None, f"fps part '-30' {fps_error}")),
            "fractional fps": ("30.5/0.02", (None, None, None, f"fps part '30.5' {fps_error}")),
            "uncapped at zero fps": ("u0/0.02", (None, None, None, f"fps part 'u0' {fps_error}")),
            "uncapped with text": ("ux/0.02", (None, None, None, f"fps part 'ux' {fps_error}")),
            "no fps": ("/0.02", (None, None, None, f"fps part '' {fps_error}")),
            "zero step": ("30/0", (None, None, None, "step part '0' is not above 0 and at most 1 second")),
            "negative step": ("30/-0.02", (None, None, None, "step part '-0.02' is not above 0 and at most 1 second")),
            "text step": ("30/x", (None, None, None, "step part 'x' is not a number of seconds")),
            "no step": ("30/", (None, None, None, "step part '' is not a number of seconds")),
            "step above a second": ("30/2", (None, None, None, "step part '2' is not above 0 and at most 1 second")),
            "NaN step": ("30/NaN", (None, None, None, "step part 'NaN' is not a number of seconds")),
            "underscored step": ("30/0.0_2", (None, None, None, "step part '0.0_2' is not a number of seconds")),
            "capped": ("30/0.02", (30, 30, 0.02, None)),
            "the kit's rates": ("60/0.03", (60, 60, 0.03, None)),
            "bare u": ("u/0.02", (-1, 0, 0.02, None)),
            "uncapped with fps": ("u30/0.02", (-1, 30, 0.02, None)),
            "whitespace around parts": (" 30 / 0.02 ", (30, 30, 0.02, None)),
            "upper-case u": ("U30/0.02", (-1, 30, 0.02, None)),
            "space after u": ("u 30/0.02", (-1, 30, 0.02, None)),
            "upper limits": ("1000/1", (1000, 1000, 1.0, None)),
            "lower limits": ("1/0.001", (1, 1, 0.001, None)),
            "exponent step": ("30/2e-2", (30, 30, 0.02, None)),
        }
        for name, (text, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0tools.rates_setting(text), expected)


class EnvRegistryTests(unittest.TestCase):
    def test_the_step_two_three_five_six_seven_eight_a_and_nine_keys(self):
        self.assertEqual(set(p0tools.ENV_REGISTRY), {"MESH_WORK_BINS", "MESH_RATES", "MESH_UNCAPPED_CELLS",
                                                     "MESH_AOI_TIMING", "MESH_THREAD_CPU", "MESH_PROBE_BURN_TICK_MS",
                                                     "MESH_PUPPET_MOTION", "MESH_RIG_SERVER_ANIMATION",
                                                     "MESH_LOAD_MOTION", "MESH_LOAD_FIGHT", "MESH_METER_SENDS",
                                                     "MESH_PHANTOM", "MESH_PHANTOM_VIEW", "MESH_PHANTOM_ARRIVAL_PER_S",
                                                     "MESH_PHANTOM_UPLOAD_HZ", "MESH_PHANTOM_PAYLOAD",
                                                     "MESH_PHANTOM_STEPS", "MESH_PHANTOM_AREA", "MESH_PHANTOM_STAND",
                                                     "MESH_PHANTOM_FIGHT", "MESH_SENDER", "MESH_SENDER_SOURCES",
                                                     "MESH_SENDER_TIERS", "MESH_SENDER_R0", "MESH_SENDER_COMPACT",
                                                     "MESH_SENDER_SPREAD", "MESH_SENDER_XZ",
                                                     "MESH_SENDER_CLASSIFY_BUILDS", "MESH_SENDER_PACKET_BYTES",
                                                     "MESH_CE12_CHECK", "MESH_LOAD_SHIPS", "MESH_LOAD_SHIP_PILOTS",
                                                     "MESH_SHIP_WIRE", "MESH_SHIP_SCALE", "MESH_SHIP_PROJECTILES",
                                                     "MESH_SHIP_MIRROR_VELOCITY", "MESH_RIG_SHIPS",
                                                     "MESH_PUPPET_FEEDER", "MESH_SYNC_LOAD",
                                                     "MESH_METER_RECORD_SIZES", "MESH_SHIP_SELF_ACK",
                                                     "MESH_SHIP_AI_PREFAB", "MESH_PREWARM", "MESH_SHIP_PARK_SLEEP",
                                                     "MESH_MONSTER_PLACE", "MESH_DB_BURST", "MESH_DB_SAVES_PROBE",
                                                     "MESH_MONSTER_CELL", "MESH_FIGHT_GHOSTS", "MESH_FORWARD_TIMING",
                                                     "MESH_SPACES", "MESH_NET_STATS", "MESH_MONSTER_AWAKE"})

    def test_the_dropped_keys_read_after_was(self):
        cases = {
            "MESH_PHANTOM_REGISTER": "dropped in step 5 (decision 3): phantoms anchor through their owned object, and "
                                     "registering sends central traffic and makes them handoff candidates whose "
                                     "handoff writes a fake row",
            "MESH_SENDER_RESIDUAL_PROBE": "dropped in step 6 (decision 13): the Work ticks move part already times the "
                                          "kit's movement loop under MESH_AOI_TIMING=1",
        }
        for key, expected in cases.items():
            with self.subTest(key):
                self.assertEqual(p0tools.DROPPED_KEYS[key], expected)
        for key, reason in p0tools.DROPPED_KEYS.items():
            with self.subTest(key):
                self.assertRegex(reason, r"^dropped in step \d \(decision \d+\): ")

    def test_every_key_is_a_regress_key_with_a_one_line_meaning(self):
        for key, meaning in p0tools.ENV_REGISTRY.items():
            with self.subTest(key):
                self.assertIn(key, regress.ENV_KEYS)
                self.assertTrue(meaning.strip())
                self.assertNotIn("\n", meaning)

    def test_registry_pending_and_dropped_keys_are_section_0_16(self):
        registry, pending, dropped = set(p0tools.ENV_REGISTRY), set(p0tools.PENDING_KEYS), set(p0tools.DROPPED_KEYS)
        self.assertEqual((len(registry), len(pending), len(dropped)), (53, 1, 3))
        self.assertEqual(registry | pending | dropped, SECTION_0_16_KEYS | STEP_9_KEYS)
        self.assertEqual(registry & pending, set())
        self.assertEqual(registry & dropped, set())
        self.assertEqual(pending & dropped, set())

    def test_regress_keys_are_90_distinct_keys(self):
        # 73 through Phase 0, plus Slice 2d's two fault toggles (MESH_FAULT_DIE_ON_PREWARM, MESH_FAULT_DIE_ON_DRAIN), and
        # Slice 7's three link toggles (MESH_LINKS, MESH_LINK_CAP, MESH_LINK_IDLE)
        self.assertEqual((len(regress.ENV_KEYS), len(set(regress.ENV_KEYS))), (90, 90))

    def test_no_pending_or_dropped_key_is_a_regress_key(self):
        for key in list(p0tools.PENDING_KEYS) + list(p0tools.DROPPED_KEYS):
            with self.subTest(key):
                self.assertNotIn(key, regress.ENV_KEYS)

    def test_pending_keys_name_their_stage_and_step(self):
        cases = {"MESH_SHIP_JUMP_SECONDS": "stage 0c, step 9"}
        self.assertEqual(set(p0tools.PENDING_KEYS), set(cases))
        for key, expected in cases.items():
            with self.subTest(key):
                self.assertEqual(p0tools.PENDING_KEYS[key], expected)
        for key, meaning in p0tools.PENDING_KEYS.items():
            with self.subTest(key):
                self.assertRegex(meaning, r"^stage 0[bc], step \d$")


# Step 9's keys beyond §0.16 (step9_design.md decision 14): the save probe, ghost targets, forwarded-hit timing and
# LiteNetLib's statistics.
STEP_9_KEYS = {"MESH_DB_SAVES_PROBE", "MESH_FIGHT_GHOSTS", "MESH_FORWARD_TIMING", "MESH_NET_STATS", "MESH_MONSTER_AWAKE"}

# The build sheet's §0.16, listed literally (step 7b's MESH_SHIP_SELF_ACK and MESH_SHIP_AI_PREFAB and step 8a's
# MESH_MONSTER_PLACE included), with step 5's AREA, STAND and FIGHT and step 7's MESH_METER_RECORD_SIZES and
# MESH_SHIP_MIRROR_VELOCITY: 15 Mesh/ keys and 37 TestMap/ keys.
SECTION_0_16_KEYS = {
    "MESH_PUPPET_MOTION", "MESH_WORK_BINS", "MESH_METER_SENDS", "MESH_SENDER", "MESH_SENDER_SOURCES",
    "MESH_SENDER_TIERS", "MESH_SENDER_R0", "MESH_SENDER_COMPACT", "MESH_SENDER_SPREAD", "MESH_SENDER_XZ",
    "MESH_SENDER_CLASSIFY_BUILDS", "MESH_SENDER_PACKET_BYTES", "MESH_SENDER_RESIDUAL_PROBE", "MESH_MONSTER_CELL",
    "MESH_RATES", "MESH_UNCAPPED_CELLS", "MESH_AOI_TIMING", "MESH_THREAD_CPU", "MESH_PROBE_BURN_TICK_MS",
    "MESH_RIG_SERVER_ANIMATION", "MESH_LOAD_MOTION", "MESH_LOAD_FIGHT", "MESH_LOAD_COLLIDE", "MESH_PHANTOM",
    "MESH_PHANTOM_VIEW", "MESH_PHANTOM_ARRIVAL_PER_S", "MESH_PHANTOM_UPLOAD_HZ", "MESH_PHANTOM_PAYLOAD",
    "MESH_PHANTOM_STEPS", "MESH_PHANTOM_REGISTER", "MESH_CE12_CHECK", "MESH_LOAD_SHIPS", "MESH_LOAD_SHIP_PILOTS",
    "MESH_SHIP_WIRE", "MESH_SHIP_SCALE", "MESH_SHIP_PROJECTILES", "MESH_SHIP_PARK_SLEEP", "MESH_SHIP_JUMP_SECONDS",
    "MESH_RIG_SHIPS", "MESH_PUPPET_FEEDER", "MESH_SYNC_LOAD", "MESH_PREWARM", "MESH_DB_BURST", "MESH_SPACES",
    "MESH_PHANTOM_AREA", "MESH_PHANTOM_STAND", "MESH_PHANTOM_FIGHT", "MESH_METER_RECORD_SIZES",
    "MESH_SHIP_MIRROR_VELOCITY", "MESH_SHIP_SELF_ACK", "MESH_SHIP_AI_PREFAB", "MESH_MONSTER_PLACE"}

BINS_SAMPLE = ("Work bins 5.00 s: tick 49:96 60:4 over:0 | nonaoi 49:95 over:0 | other 10:50 over:0 "
               "| kit 16:100 over:0 | send 12:100 over:0")
BINS_FIELDS = {"seconds": 5.0, "tick": {"bins": {49: 96, 60: 4}, "over": 0}, "nonaoi": {"bins": {49: 95}, "over": 0},
               "other": {"bins": {10: 50}, "over": 0}, "kit": {"bins": {16: 100}, "over": 0},
               "send": {"bins": {12: 100}, "over": 0}}
EMPTY_BINS = "Work bins 0.00 s: tick over:0 | nonaoi over:0 | other over:0 | kit over:0 | send over:0"


class WorkBinsTests(unittest.TestCase):
    def test_parse_work_bins(self):
        cases = {
            "a work ticks line": (WORK_TICKS_SAMPLE, None),
            "indices not increasing": (BINS_SAMPLE.replace("60:4", "40:4"), None),
            "a repeated index": (BINS_SAMPLE.replace("60:4", "49:4"), None),
            "an index at the bin count": (BINS_SAMPLE.replace("60:4", "4000:4"), None),
            "a zero count": (BINS_SAMPLE.replace("60:4", "60:0"), None),
            "a negative count": (BINS_SAMPLE.replace("60:4", "60:-4"), None),
            "a wrong label": (BINS_SAMPLE.replace("tick 49", "ticks 49"), None),
            "labels out of order": (BINS_SAMPLE.replace("| nonaoi 49:95 over:0 | other 10:50 over:0",
                                                        "| other 10:50 over:0 | nonaoi 49:95 over:0"), None),
            "a missing group": (BINS_SAMPLE.replace(" | send 12:100 over:0", ""), None),
            "no overflow count": (BINS_SAMPLE.replace("kit 16:100 over:0", "kit 16:100"), None),
            "trailing text": (BINS_SAMPLE + " | extra over:0", None),
            "the sample": (BINS_SAMPLE, dict(BINS_FIELDS, end=None)),
            "a log line with its line end": (stamped(BINS_SAMPLE) + "\r\n", dict(BINS_FIELDS, end=END)),
            "empty groups": (EMPTY_BINS, {"end": None, "seconds": 0.0,
                                          **{name: {"bins": {}, "over": 0} for name in p0tools.HISTOGRAMS}}),
            "the last bin and an overflow": (
                BINS_SAMPLE.replace("tick 49:96 60:4 over:0", "tick 3999:2 over:7"),
                dict(BINS_FIELDS, tick={"bins": {3999: 2}, "over": 7}, end=None)),
        }
        for name, (line, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0tools.parse_work_bins(line), expected)

    def test_merge_bins(self):
        a, b = {"bins": {49: 96, 60: 4}, "over": 1}, {"bins": {40: 90, 49: 4, 70: 6}, "over": 2}
        cases = {
            "none": ([], {"bins": {}, "over": 0}),
            "one": ([a], a),
            "two": ([a, b], {"bins": {40: 90, 49: 100, 60: 4, 70: 6}, "over": 3}),
        }
        for name, (hists, expected) in cases.items():
            with self.subTest(name):
                merged = p0tools.merge_bins(hists)
                self.assertEqual(merged, expected)
                self.assertEqual(list(merged["bins"]), sorted(merged["bins"]))

    def test_histogram_total(self):
        self.assertEqual(p0tools.histogram_total({"bins": {1: 3, 7: 4}, "over": 2}), 9)
        self.assertEqual(p0tools.histogram_total({"bins": {}, "over": 0}), 0)

    def test_percentile_refuses_a_bad_rank(self):
        for ppm in (-1, 1_000_001, 0.95, True, None):
            with self.subTest(ppm):
                with self.assertRaisesRegex(ValueError, "ppm is not an integer from 0 to 1000000"):
                    p0tools.percentile_us({"bins": {1: 1}, "over": 0}, ppm)

    def test_percentile_us(self):
        a, b = {"bins": {49: 96, 60: 4}, "over": 0}, {"bins": {40: 90, 70: 10}, "over": 0}
        cases = {
            "an overflow rank with an unknown maximum": (({"bins": {10: 90}, "over": 10}, 950000, None), None),
            "an overflow rank takes the maximum": (({"bins": {10: 90}, "over": 10}, 950000, 231400), 231400),
            "no in-bin clamp to the maximum": ((a, 950000, 2400), 2500),
            "empty": (({"bins": {}, "over": 0}, 950000, None), 0),
            "rank zero is the first value": ((a, 0, None), 2500),
            "p95 of A": ((a, 950000, None), 2500),
            "p95 of B": ((b, 950000, None), 3550),
            "p95 of A and B merged": ((p0tools.merge_bins([a, b]), 950000, None), 3050),
            "p99 of 100 values in bins 0 to 99": (({"bins": {i: 1 for i in range(100)}, "over": 0}, 990000, None), 4950),
            "a frame of exactly 2.5 ms lands above the 2.5 ms edge": (({"bins": {0: 94, 50: 6}, "over": 0}, 950000,
                                                                       None), 2550),
            "just under 2.5 ms": (({"bins": {0: 94, 49: 6}, "over": 0}, 950000, None), 2500),
            "p100 is the last value": ((a, 1_000_000, None), 3050),
        }
        for name, ((hist, ppm, max_us), expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0tools.percentile_us(hist, ppm, max_us), expected)


class MarkerTests(unittest.TestCase):
    def check(self, parse, cases):
        for name, (line, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(parse(line), expected)

    def test_parse_band(self):
        line = "MESH_BAND=20: ghost band and visible range set to 20 m"
        self.check(p0tools.parse_band, {
            "the ignored warning": ("MESH_BAND=x is not a positive distance; ignored", None),
            "truncated": (line[:40], None),
            "another key": (line.replace("MESH_BAND=", "MESH_BANDS="), None),
            "a longer key": (line.replace("MESH_BAND=", "XMESH_BAND="), None),
            "the line": (line, {"end": None, "band": 20.0, "setting": "20"}),
            "a log line": (stamped(line.replace("20 m", "80.5 m").replace("=20", "=80.50")),
                           {"end": END, "band": 80.5, "setting": "80.50"}),
        })

    def test_parse_load_motion(self):
        line = "Load generator: motion kinematic (MESH_LOAD_MOTION), fight on (MESH_LOAD_FIGHT)"
        self.check(p0tools.parse_load_motion, {
            "an unknown motion": (line.replace("kinematic", "flying"), None),
            "truncated": (line[:50], None),
            "another key": (line.replace("(MESH_LOAD_FIGHT)", "(MESH_FIGHT)"), None),
            "the line": (line, {"end": None, "motion": "kinematic", "fight": True}),
            "steered without fighting": (stamped(line.replace("kinematic", "steered").replace("fight on", "fight off")),
                                         {"end": END, "motion": "steered", "fight": False}),
            "inert": (line.replace("kinematic", "inert"), {"end": None, "motion": "inert", "fight": True}),
        })

    def test_parse_kinematic_walk(self):
        line = ("Kinematic walk: 60 synthetic players walk by transform at 0.90 x move speed; 0 stay steered "
                "(no grounded kit movement within 10.0 s)")
        self.check(p0tools.parse_kinematic_walk, {
            "the fallback line": ("Kinematic walk: Load0001 has no grounded kit movement after 10.0 s; it stays "
                                  "steered with the brain", None),
            "truncated": (line[:70], None),
            "another word": (line.replace("stay steered", "stay braindead"), None),
            "the line": (line, {"end": None, "walking": 60, "speed_rate": 0.9, "steered": 0, "timeout_s": 10.0}),
            "some stayed steered": (stamped(line.replace("60 synthetic", "298 synthetic").replace("; 0 ", "; 2 ")),
                                    {"end": END, "walking": 298, "speed_rate": 0.9, "steered": 2, "timeout_s": 10.0}),
        })

    def test_parse_server_animation(self):
        line = "Server animation: updateAnimationAtServer 0 (MESH_RIG_SERVER_ANIMATION; kit 1)"
        self.check(p0tools.parse_server_animation, {
            "the editor warning": ("Server animation: MESH_RIG_SERVER_ANIMATION is ignored in the editor; "
                                   "updateAnimationAtServer left at 1", None),
            "truncated": (line[:45], None),
            "another key": (line.replace("MESH_RIG_SERVER_ANIMATION", "MESH_SERVER_ANIMATION"), None),
            "not a bit": (line.replace("AtServer 0", "AtServer 2"), None),
            "the line": (line, {"end": None, "value": 0, "kit": 1}),
            "a log line": (stamped(line.replace("AtServer 0", "AtServer 1")), {"end": END, "value": 1, "kit": 1}),
        })

    def test_parse_aoi_timing(self):
        line = ("AOI timing: installed on cell C (MapNetworkManager(Clone)/TimedAoi): cellSize 20 maxObjects 5000 "
                "updateInterval 0.1 s bufferedCells 1 1 1 visibleRange 80 | grid system ready | kit "
                "JobifiedGridSpatialPartitioningAOI disabled, kept as the fallback")
        self.check(p0tools.parse_aoi_timing, {
            "not installed": ("AOI timing: no interest manager on cell C; kit AOI kept", None),
            "truncated": (line[:60], None),
            "another word": (line.replace("installed on", "removed from"), None),
            "the line": (line, {"end": None, "cell": "C", "manager": "MapNetworkManager(Clone)/TimedAoi",
                                "visible_range": 80.0}),
            "a log line": (stamped(line.replace("visibleRange 80", "visibleRange 300")),
                           {"end": END, "cell": "C", "manager": "MapNetworkManager(Clone)/TimedAoi",
                            "visible_range": 300.0}),
        })

    def test_parse_tick_burn(self):
        self.check(p0tools.parse_tick_burn, {
            "the editor warning": ("Tick burn: MESH_PROBE_BURN_TICK_MS is ignored in the editor; no tick burn", None),
            "truncated": ("Tick burn: 2 ms per tick on ", None),
            "another word": ("Tick burn: 2 ms per frame on C", None),
            "one cell": ("Tick burn: 2 ms per tick on C", {"end": None, "ms": 2.0, "cell": "C"}),
            "every cell": (stamped("Tick burn: 0.5 ms per tick on every cell") + "\n",
                           {"end": END, "ms": 0.5, "cell": None}),
        })

    def test_parse_spawned(self):
        line = ("Load generator: spawned 250 synthetic players on cell C over x [-150, 150] z [-150, 150] "
                "(seed 1, planned 250)")
        fields = {"end": None, "spawned": 250, "cell": "C", "x_min": -150, "x_max": 150, "z_min": -150, "z_max": 150,
                  "seed": 1, "planned": 250}
        self.check(p0tools.parse_spawned, {
            "the no-overlap error": ("Load generator: the load area does not overlap cell C, which the plan gave "
                                     "250 players; none spawned", None),
            "truncated": (line[:80], None),
            "another word": (line.replace("synthetic players", "synthetic monsters"), None),
            "the line": (line, fields),
            "fewer than planned, a negative zero edge": (
                stamped(line.replace("spawned 250", "spawned 248").replace("x [-150, 150]", "x [-0, 300]")),
                dict(fields, end=END, spawned=248, x_min=0, x_max=300)),
        })


if __name__ == "__main__":
    unittest.main()
