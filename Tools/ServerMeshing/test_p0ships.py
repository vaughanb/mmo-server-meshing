"""Unit tests for p0ships (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py")."""
import datetime
import unittest

import p0ships

STAMP = "2026-10-03 12:00:05"
END = datetime.datetime(2026, 10, 3, 12, 0, 5)


def stamped(message):
    return f" INFO Mesh [{STAMP}] - {message}"


SHIPS_SAMPLE = (
    "Ships window 5.00 s: cell C ships 50 ai 50 mirror 0 parked 0 phantom 0 pilots 0 seated 0 outside 0 notInSlot 0 "
    "| speed p50 1.50 max 3.76 patrol p50 1.50 m/s | scans 500 casts 7500 shots 300 bolts 600 hits 40 respawns 0 "
    "| shield writes 1500 | rpcSends 2400 | uploads 0 stale 0 bad 0 "
    "| ms/s pilot 0.40 engine 0.30 combat 0.20 bolts 0.90 mirror 0.00 | gc0 1")
SHIPS_FIELDS = {
    "seconds": 5.0, "cell": "C", "ships": 50, "ai": 50, "mirror": 0, "parked": 0, "phantom": 0, "pilots": 0,
    "seated": 0, "outside": 0, "not_in_slot": 0, "speed_p50": 1.5, "speed_max": 3.76, "patrol_p50": 1.5,
    "scans": 500, "casts": 7500, "shots": 300, "bolts": 600, "hits": 40, "respawns": 0, "shield_writes": 1500,
    "rpc_sends": 2400, "uploads": 0, "stale": 0, "bad": 0, "pilot_ms_per_s": 0.4, "engine_ms_per_s": 0.3,
    "combat_ms_per_s": 0.2, "bolts_ms_per_s": 0.9, "mirror_ms_per_s": 0.0, "gc0": 1, "pending": None, "patrol_n": None,
    "mirror_age_n": None, "mirror_age_ms": None, "mirror_age_frames": None, "parked_sleeping": None,
    "parked_total": None}
# Step 7b's form of the same window: pending, a patrol count and the mirror feed age.
SHIPS_7B_SAMPLE = (
    "Ships window 5.00 s: cell C ships 50 ai 50 mirror 0 parked 0 phantom 0 pilots 0 seated 0 outside 0 notInSlot 0 "
    "pending 3 | speed p50 1.50 max 2.82 patrol n 120 p50 1.05 m/s | scans 500 casts 7500 shots 300 bolts 600 hits 40 "
    "respawns 3 | shield writes 1500 | rpcSends 2400 | uploads 0 stale 0 bad 0 "
    "| ms/s pilot 0.40 engine 0.30 combat 0.20 bolts 0.90 mirror 0.00 | mirror age n 40 p50 16.7 ms frames 1 | gc0 1")
SHIPS_7B_FIELDS = dict(SHIPS_FIELDS, pending=3, speed_max=2.82, patrol_n=120, patrol_p50=1.05, respawns=3,
                       mirror_age_n=40, mirror_age_ms=16.7, mirror_age_frames=1)
SHIPS_7B_EMPTY = (SHIPS_7B_SAMPLE.replace("patrol n 120 p50 1.05", "patrol n 0 p50 none")
                  .replace("mirror age n 40 p50 16.7 ms frames 1", "mirror age n 0 p50 none ms frames none"))
SHIPS_7B_EMPTY_FIELDS = dict(SHIPS_7B_FIELDS, patrol_n=0, patrol_p50=None, mirror_age_n=0, mirror_age_ms=None,
                             mirror_age_frames=None)
# Step 8a's parked sleeping group (MESH_SHIP_PARK_SLEEP set) before gc0, on both forms of the window.
SHIPS_PARK_SAMPLE = SHIPS_7B_SAMPLE.replace(" | gc0 1", " | parked sleeping 12 of 200 | gc0 1")
SETTINGS_SAMPLE = ("Ships: cell C ai 50 mirror 0 parked 0 pilots 0 | wire header56 scale 0.03 projectiles instantiate "
                   "mirrorVelocity real selfAck 0 aiPrefab enemy | area none | prefab hash -12345678")
SETTINGS_FIELDS = {"cell": "C", "ai": 50, "mirror": 0, "parked": 0, "pilots": 0, "wire": "header56", "scale": "0.03",
                   "projectiles": "instantiate", "mirror_velocity": "real", "self_ack": 0, "ai_prefab": "enemy",
                   "area": "none", "prefab_hash": -12345678, "park_sleep": None}
SETTINGS_7_SAMPLE = SETTINGS_SAMPLE.replace(" selfAck 0 aiPrefab enemy", "").replace("Velocity real", "Velocity zero")
FEEDER_SAMPLE = ("Feeder window 5.00 s: ship 200 observed 0 | snapshots 20000 "
                 "| ms/s encode 0.30 decode 0.40 accept 0.50 pose 1.20 total 2.40 | us/snapshot 0.120 | gc0 0")
FEEDER_FIELDS = {"seconds": 5.0, "kind": "ship", "puppets": 200, "observed": 0, "snapshots": 20000,
                 "encode_ms_per_s": 0.3, "decode_ms_per_s": 0.4, "accept_ms_per_s": 0.5, "pose_ms_per_s": 1.2,
                 "total_ms_per_s": 2.4, "us_per_snapshot": 0.12, "gc0": 0}
SYNC_SAMPLE = ("Sync load window 5.00 s: ships 50 | writes 10000 rpc 1000 rpcSends 4000 | ms/s write 0.10 rpc 0.90 "
               "| alloc 640 B/rpc gc0 2")
SYNC_FIELDS = {"seconds": 5.0, "ships": 50, "writes": 10000, "rpc": 1000, "rpc_sends": 4000, "write_ms_per_s": 0.1,
               "rpc_ms_per_s": 0.9, "alloc_bytes_per_rpc": 640.0, "gc0": 2}
BOT_SAMPLE = ("Bot ships 5.00 s: seen 48 records 4800 pathed 3840 parked 960 unpathed 0 "
              "| path n 3840 p50 0.010 p95 0.050 p99 0.080 max 0.120 m lag p50 32 ms | parked max 0.0000 m "
              "| unknownPath 0 badLength 0")
BOT_FIELDS = {"seconds": 5.0, "seen": 48, "records": 4800, "pathed": 3840, "parked": 960, "unpathed": 0,
              "path_n": 3840, "path_p50": 0.01, "path_p95": 0.05, "path_p99": 0.08, "path_max": 0.12,
              "lag_p50_ms": 32, "parked_max": 0.0, "unknown_path": 0, "bad_length": 0}
MOVED = {"MESH_LOAD_SHIPS", "MESH_LOAD_SHIP_PILOTS", "MESH_SHIP_WIRE", "MESH_SHIP_SCALE", "MESH_SHIP_PROJECTILES",
         "MESH_RIG_SHIPS", "MESH_PUPPET_FEEDER", "MESH_SYNC_LOAD"}
ADDED = {"MESH_METER_RECORD_SIZES", "MESH_SHIP_MIRROR_VELOCITY", "MESH_SHIP_SELF_ACK", "MESH_SHIP_AI_PREFAB"}


class WindowParserTests(unittest.TestCase):
    def test_parse_ships_window(self):
        cases = {
            "a feeder window": (FEEDER_SAMPLE, None),
            "no notInSlot": (SHIPS_SAMPLE.replace(" notInSlot 0", ""), None),
            "a fractional count": (SHIPS_SAMPLE.replace("shots 300", "shots 300.5"), None),
            "a negative speed": (SHIPS_SAMPLE.replace("max 3.76", "max -3.76"), None),
            "text after gc0": (SHIPS_SAMPLE + " | extra 1", None),
            "no bad": (SHIPS_SAMPLE.replace(" bad 0", ""), None),
            "an old patrol of none": (SHIPS_SAMPLE.replace("patrol p50 1.50", "patrol p50 none"), None),
            "a patrol count with none": (SHIPS_7B_SAMPLE.replace("p50 1.05", "p50 none"), None),
            "no patrol count with a speed": (SHIPS_7B_SAMPLE.replace("patrol n 120 p50 1.05", "patrol n 0 p50 1.05"),
                                             None),
            "a fractional patrol count": (SHIPS_7B_SAMPLE.replace("patrol n 120", "patrol n 1.5"), None),
            "a negative pending": (SHIPS_7B_SAMPLE.replace("pending 3", "pending -3"), None),
            "pending after the speeds": (
                SHIPS_7B_SAMPLE.replace(" pending 3", "").replace(" m/s |", " m/s pending 3 |"), None),
            "mirror age with none and a count": (
                SHIPS_7B_SAMPLE.replace("p50 16.7 ms frames 1", "p50 none ms frames 1"), None),
            "mirror age of none frames": (SHIPS_7B_SAMPLE.replace("frames 1", "frames none"), None),
            "no mirror age and speeds": (SHIPS_7B_EMPTY.replace("frames none", "frames 1"), None),
            "mirror age without frames": (SHIPS_7B_SAMPLE.replace(" frames 1", ""), None),
            "mirror age after gc0": (SHIPS_SAMPLE + " | mirror age n 40 p50 16.7 ms frames 1", None),
            "parked sleeping after gc0": (SHIPS_7B_SAMPLE + " | parked sleeping 0 of 200", None),
            "parked sleeping before the mirror age": (
                SHIPS_PARK_SAMPLE.replace(" | parked sleeping 12 of 200", "")
                .replace(" | mirror age", " | parked sleeping 12 of 200 | mirror age"), None),
            "more sleeping than parked": (SHIPS_PARK_SAMPLE.replace("sleeping 12 of", "sleeping 201 of"), None),
            "parked sleeping without a total": (SHIPS_PARK_SAMPLE.replace(" of 200", ""), None),
            "a sample": (SHIPS_SAMPLE, dict(SHIPS_FIELDS, end=None)),
            "a step 7b sample": (SHIPS_7B_SAMPLE, dict(SHIPS_7B_FIELDS, end=None)),
            "a step 7b log line": (stamped(SHIPS_7B_SAMPLE), dict(SHIPS_7B_FIELDS, end=END)),
            "a step 7b window with no patrol or mirror": (SHIPS_7B_EMPTY, dict(SHIPS_7B_EMPTY_FIELDS, end=None)),
            "fractional frames": (SHIPS_7B_SAMPLE.replace("frames 1", "frames 1.5"),
                                  dict(SHIPS_7B_FIELDS, mirror_age_frames=1.5, end=None)),
            "only pending": (SHIPS_SAMPLE.replace("notInSlot 0", "notInSlot 0 pending 0"),
                             dict(SHIPS_FIELDS, pending=0, end=None)),
            "a log line": (stamped(SHIPS_SAMPLE), dict(SHIPS_FIELDS, end=END)),
            "trailing whitespace": (SHIPS_SAMPLE + "\r\n", dict(SHIPS_FIELDS, end=None)),
            "a named cell": (SHIPS_SAMPLE.replace("cell C ", "cell NE "), dict(SHIPS_FIELDS, cell="NE", end=None)),
            "parked sleeping": (stamped(SHIPS_PARK_SAMPLE), dict(SHIPS_7B_FIELDS, parked_sleeping=12, parked_total=200,
                                                                 end=END)),
            "parked sleeping on a step 7 line": (SHIPS_SAMPLE.replace(" | gc0 1", " | parked sleeping 0 of 0 | gc0 1"),
                                                 dict(SHIPS_FIELDS, parked_sleeping=0, parked_total=0, end=None)),
        }
        for name, (line, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0ships.parse_ships_window(line), expected)

    def test_parse_ships_settings(self):
        cases = {
            "a ships window": (SHIPS_SAMPLE, None),
            "self-ack without the prefab": (SETTINGS_SAMPLE.replace(" aiPrefab enemy", ""), None),
            "a self-ack of 2": (SETTINGS_SAMPLE.replace("selfAck 0", "selfAck 2"), None),
            "an unknown prefab": (SETTINGS_SAMPLE.replace("aiPrefab enemy", "aiPrefab boss"), None),
            "the keys before mirrorVelocity": (SETTINGS_SAMPLE.replace(" mirrorVelocity real selfAck 0 aiPrefab enemy",
                                                                       " selfAck 0 aiPrefab enemy mirrorVelocity real"),
                                               None),
            "text after the hash": (SETTINGS_SAMPLE + " x", None),
            "parkSleep without the 7b fields": (SETTINGS_7_SAMPLE.replace(" | area", " parkSleep 1 | area"), None),
            "parkSleep before aiPrefab": (SETTINGS_SAMPLE.replace("selfAck 0 aiPrefab enemy",
                                                                  "selfAck 0 parkSleep 1 aiPrefab enemy"), None),
            "a parkSleep of 2": (SETTINGS_SAMPLE.replace("aiPrefab enemy", "aiPrefab enemy parkSleep 2"), None),
            "a sample": (SETTINGS_SAMPLE, dict(SETTINGS_FIELDS, end=None)),
            "a log line with wave and self-ack": (stamped(SETTINGS_SAMPLE.replace("selfAck 0 aiPrefab enemy",
                                                                                  "selfAck 1 aiPrefab wave")),
                                                  dict(SETTINGS_FIELDS, self_ack=1, ai_prefab="wave", end=END)),
            "a step 7 line": (SETTINGS_7_SAMPLE, dict(SETTINGS_FIELDS, mirror_velocity="zero", self_ack=None,
                                                      ai_prefab=None, end=None)),
            "parkSleep 0": (SETTINGS_SAMPLE.replace("aiPrefab enemy", "aiPrefab enemy parkSleep 0"),
                            dict(SETTINGS_FIELDS, park_sleep=0, end=None)),
            "parkSleep 1 on a log line": (
                stamped(SETTINGS_SAMPLE.replace("aiPrefab enemy", "aiPrefab enemy parkSleep 1")),
                dict(SETTINGS_FIELDS, park_sleep=1, end=END)),
            "an area": (SETTINGS_SAMPLE.replace("area none", "area -30,-30,30.5,30"),
                        dict(SETTINGS_FIELDS, area="-30,-30,30.5,30", end=None)),
        }
        for name, (line, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0ships.parse_ships_settings(line), expected)

    def test_parse_feeder_window(self):
        cases = {
            "a ships window": (SHIPS_SAMPLE, None),
            "an unknown kind": (FEEDER_SAMPLE.replace(": ship ", ": monster "), None),
            "no total": (FEEDER_SAMPLE.replace(" total 2.40", ""), None),
            "text after gc0": (FEEDER_SAMPLE + " x", None),
            "a sample": (FEEDER_SAMPLE, dict(FEEDER_FIELDS, end=None)),
            "a character feeder": (stamped(FEEDER_SAMPLE.replace(": ship ", ": character ")),
                                   dict(FEEDER_FIELDS, kind="character", end=END)),
        }
        for name, (line, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0ships.parse_feeder_window(line), expected)

    def test_parse_sync_load_window(self):
        cases = {
            "a feeder window": (FEEDER_SAMPLE, None),
            "no rpcSends": (SYNC_SAMPLE.replace(" rpcSends 4000", ""), None),
            "a negative alloc": (SYNC_SAMPLE.replace("alloc 640", "alloc -640"), None),
            "a sample": (SYNC_SAMPLE, dict(SYNC_FIELDS, end=None)),
            "a fractional alloc": (stamped(SYNC_SAMPLE.replace("alloc 640", "alloc 640.5")),
                                   dict(SYNC_FIELDS, alloc_bytes_per_rpc=640.5, end=END)),
        }
        for name, (line, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0ships.parse_sync_load_window(line), expected)

    def test_parse_bot_ships(self):
        cases = {
            "a ships window": (SHIPS_SAMPLE, None),
            "a fractional lag": (BOT_SAMPLE.replace("lag p50 32 ms", "lag p50 32.5 ms"), None),
            "no badLength": (BOT_SAMPLE.replace(" badLength 0", ""), None),
            "no parked unit": (BOT_SAMPLE.replace("0.0000 m", "0.0000"), None),
            "a sample": (BOT_SAMPLE, dict(BOT_FIELDS, end=None)),
            "a bot log line": (f"[Mesh] {BOT_SAMPLE}", dict(BOT_FIELDS, end=None)),
            "a leading record": (BOT_SAMPLE.replace("lag p50 32 ms", "lag p50 -4 ms"),
                                 dict(BOT_FIELDS, lag_p50_ms=-4, end=None)),
        }
        for name, (line, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0ships.parse_bot_ships(line), expected)

    def test_counts_are_int_and_the_rest_float(self):
        for parse, sample, ints in ((p0ships.parse_ships_window, SHIPS_SAMPLE, None),
                                    (p0ships.parse_ships_window, SHIPS_7B_SAMPLE, None),
                                    (p0ships.parse_ships_window, SHIPS_PARK_SAMPLE, None),
                                    (p0ships.parse_feeder_window, FEEDER_SAMPLE, p0ships.FEEDER_WINDOW_INTS),
                                    (p0ships.parse_sync_load_window, SYNC_SAMPLE, p0ships.SYNC_LOAD_WINDOW_INTS),
                                    (p0ships.parse_bot_ships, BOT_SAMPLE, p0ships.BOT_SHIPS_INTS)):
            for name, value in parse(sample).items():
                if name in ("end", "cell", "kind") or (value is None and name in p0ships.SHIPS_WINDOW_OPTIONAL):
                    continue
                with self.subTest(f"{parse.__name__} {name}"):
                    is_int = name not in p0ships.SHIPS_WINDOW_FLOATS if ints is None else name in ints
                    self.assertIs(type(value), int if is_int else float)


class GrammarTests(unittest.TestCase):
    def test_ship_load_plan(self):
        cases = {
            "an empty part": ("C:50:ai,", (None, "'C:50:ai,' has an empty part")),
            "a missing count": ("C:ai", (None, "part 'C:ai' is not <cell>:<n>:<ai|mirror|parked>")),
            "no cell": (":5:ai", (None, "part ':5:ai' is not <cell>:<n>:<ai|mirror|parked>")),
            "a negative count": ("C:-1:ai", (None, "part 'C:-1:ai': count '-1' is not an integer from 1 to 2000")),
            "a zero count": ("C:0:ai", (None, "part 'C:0:ai': count '0' is not an integer from 1 to 2000")),
            "too many": ("C:2001:ai", (None, "part 'C:2001:ai': count '2001' is not an integer from 1 to 2000")),
            "a word count": ("C:x:ai", (None, "part 'C:x:ai': count 'x' is not an integer from 1 to 2000")),
            "an unknown authority": ("C:5:pilot", (None, "part 'C:5:pilot': authority 'pilot' is not ai, mirror or parked")),
            "a repeated group": ("C:5:ai,C:6:AI", (None, "part 'C:6:AI': cell C has a second ai group")),
            "unset": (None, (None, None)),
            "blank": (" ", (None, None)),
            "three groups": ("C:50:ai,C:20:mirror,E:10:parked",
                             ((("C", 50, "ai"), ("C", 20, "mirror"), ("E", 10, "parked")), None)),
            "case and whitespace": (" C : 2000 : Mirror ", ((("C", 2000, "mirror"),), None)),
        }
        for name, (text, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0ships.ship_load_plan(text), expected)

    def test_ship_wire_and_scale(self):
        wires = {
            "ship56": ("ship56", (None, "'ship56' is not legacy54, header56 or compact28")),
            "unset": (None, ("legacy54", None)),
            "header56": (" Header56 ", ("header56", None)),
            "compact28": ("compact28", ("compact28", None)),
        }
        for name, (text, expected) in wires.items():
            with self.subTest(f"wire {name}"):
                self.assertEqual(p0ships.ship_wire(text), expected)
        scales = {
            "zero": ("0", (None, "'0' is not a number from 0.001 to 10")),
            "negative": ("-1", (None, "'-1' is not a number from 0.001 to 10")),
            "eleven": ("11", (None, "'11' is not a number from 0.001 to 10")),
            "NaN": ("NaN", (None, "'NaN' is not a number from 0.001 to 10")),
            "unset": ("", (1.0, None)),
            "the low edge": ("0.001", (0.001, None)),
            "scale 0.03": (" 0.03 ", (0.03, None)),
            "the high edge": ("1e1", (10.0, None)),
        }
        for name, (text, expected) in scales.items():
            with self.subTest(f"scale {name}"):
                self.assertEqual(p0ships.ship_scale(text), expected)

    def test_puppet_feeder(self):
        refused = (None, None, None)
        cases = {
            "no rate": ("ship:200", refused + ("'ship:200' is not <ship|character>:<N>@<Hz>",)),
            "an unknown kind": ("eye:10@20", refused + ("kind 'eye' is not ship or character",)),
            "a zero count": ("ship:0@20", refused + ("count '0' is not an integer from 1 to 2000",)),
            "too many": ("ship:2001@20", refused + ("count '2001' is not an integer from 1 to 2000",)),
            "a zero rate": ("ship:10@0", refused + ("rate '0' is not an integer from 1 to 60 Hz",)),
            "a fast rate": ("ship:10@61", refused + ("rate '61' is not an integer from 1 to 60 Hz",)),
            "unset": (None, refused + (None,)),
            "ships": ("ship:200@20", ("ship", 200, 20, None)),
            "characters": (" Character : 2000 @ 60 ", ("character", 2000, 60, None)),
        }
        for name, (text, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0ships.puppet_feeder(text), expected)

    def test_sync_load(self):
        cases = {
            "a missing rpc": ("dirty:100", (None, None, "'dirty:100' has no rpc part")),
            "a repeated part": ("dirty:1,dirty:2", (None, None, "'dirty' is given twice")),
            "an unknown part": ("dirty:1,rpcs:2", (None, None, "part 'rpcs:2' is not dirty:<D> or rpc:<R>")),
            "too dirty": ("dirty:10001,rpc:0", (None, None, "dirty '10001' is not an integer from 0 to 10000")),
            "too many rpcs": ("dirty:0,rpc:100001", (None, None, "rpc '100001' is not an integer from 0 to 100000")),
            "a negative": ("dirty:-1,rpc:0", (None, None, "dirty '-1' is not an integer from 0 to 10000")),
            "unset": (None, (None, None, None)),
            "in order": ("dirty:100,rpc:200", (100, 200, None)),
            "reversed with spaces": (" RPC : 0 , dirty : 10000 ", (10000, 0, None)),
        }
        for name, (text, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0ships.sync_load(text), expected)

    def test_bot_argument(self):
        refused = (None, None, None)
        cases = {
            "two fields": ("header56:12345", refused + ("'header56:12345' is not <wire>:<seed>:<scale>",)),
            "none": (None, refused + ("'None' is not <wire>:<seed>:<scale>",)),
            "an empty wire": (":1:1", refused + ("the wire is empty",)),
            "a junk wire": ("ship56:1:1", refused + ("'ship56' is not legacy54, header56 or compact28",)),
            "a word seed": ("legacy54:x:1", refused + ("seed 'x' is not a 32-bit integer",)),
            "a huge seed": ("legacy54:2147483648:1", refused + ("seed '2147483648' is not a 32-bit integer",)),
            "a zero scale": ("legacy54:1:0", refused + ("'0' is not a number from 0.001 to 10",)),
            "header56": ("header56:12345:0.03", ("header56", 12345, 0.03, None)),
            "a negative seed": ("compact28:-7:0.5", ("compact28", -7, 0.5, None)),
        }
        for name, (text, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0ships.bot_argument(text), expected)


class RunnerTests(unittest.TestCase):
    def test_rig_ship_bot_args_refuses_what_the_bot_would(self):
        cases = {
            "a junk wire": {"MESH_RIG_SHIPS": "1", "MESH_SHIP_WIRE": "ship56"},
            "a junk seed": {"MESH_RIG_SHIPS": "1", "MESH_LOAD_SEED": "x"},
            "a junk scale": {"MESH_RIG_SHIPS": "1", "MESH_SHIP_SCALE": "20"},
        }
        for name, env in cases.items():
            with self.subTest(name), self.assertRaisesRegex(ValueError, r"^-meshRigShips "):
                p0ships.rig_ship_bot_args(env)

    def test_rig_ship_bot_args(self):
        cases = {
            "no key": ({}, []),
            "zero": ({"MESH_RIG_SHIPS": "0", "MESH_SHIP_WIRE": "header56"}, []),
            "defaults": ({"MESH_RIG_SHIPS": "1"}, ["-meshRigShips", "legacy54:12345:1"]),
            "verbatim texts": ({"MESH_RIG_SHIPS": " 1 ", "MESH_SHIP_WIRE": "Header56", "MESH_LOAD_SEED": "7",
                                "MESH_SHIP_SCALE": "0.0315"}, ["-meshRigShips", "header56:7:0.0315"]),
            "empty values": ({"MESH_RIG_SHIPS": "1", "MESH_SHIP_WIRE": "", "MESH_LOAD_SEED": "", "MESH_SHIP_SCALE": ""},
                             ["-meshRigShips", "legacy54:12345:1"]),
        }
        for name, (env, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0ships.rig_ship_bot_args(env), expected)

    def test_scaled_env(self):
        for name, args in {"zero": (0,), "eleven": (11,), "NaN": (float("nan"),), "a text": ("0.5",),
                           "no R0": (0.5, 0.0)}.items():
            with self.subTest(name), self.assertRaises(ValueError):
                p0ships.scaled_env(*args)
        cases = {
            "scale 0.03": ((0.03,), {"MESH_SHIP_SCALE": "0.03", "MESH_SENDER_R0": "60.0"}),
            "scale 0.5": ((0.5,), {"MESH_SHIP_SCALE": "0.5", "MESH_SENDER_R0": "1000.0"}),
            "scale 1": ((1,), {"MESH_SHIP_SCALE": "1", "MESH_SENDER_R0": "2000.0"}),
            "another R0": ((0.03, 3000.0), {"MESH_SHIP_SCALE": "0.03", "MESH_SENDER_R0": "90.0"}),
        }
        for name, (args, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0ships.scaled_env(*args), expected)

    def test_framed_bytes(self):
        with self.assertRaises(ValueError):
            p0ships.framed_bytes(-1, 1)
        with self.assertRaises(ValueError):
            p0ships.packed_uint_bytes(-1)
        cases = {"id 1": (54, 1, 59), "id 240": (54, 240, 59), "id 241": (56, 241, 62), "id 2287": (28, 2287, 34),
                 "id 2288": (28, 2288, 35), "id 67823": (54, 67823, 61), "id 67824": (54, 67824, 62),
                 "id 16777215": (54, 16777215, 62), "id 16777216": (54, 16777216, 63)}
        for name, (payload, object_id, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0ships.framed_bytes(payload, object_id), expected)


class RegistryTests(unittest.TestCase):
    def test_the_twelve_step_7_keys(self):
        self.assertEqual(set(p0ships.STEP7_REGISTRY), MOVED | ADDED)
        for key, meaning in p0ships.STEP7_REGISTRY.items():
            with self.subTest(key):
                self.assertTrue(meaning.strip())
                self.assertNotIn("\n", meaning)

    def test_upload_bytes_follow_the_wire(self):
        self.assertEqual(p0ships.UPLOAD_BYTES, {"legacy54": 54, "header56": 56, "compact28": 56})
        self.assertEqual(set(p0ships.UPLOAD_BYTES), set(p0ships.WIRES))


if __name__ == "__main__":
    unittest.main()
