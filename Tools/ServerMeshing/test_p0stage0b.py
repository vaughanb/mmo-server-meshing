"""Unit tests for p0stage0b (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py")."""
import datetime
import unittest

import p0stage0b

STAMP = "2026-10-04 12:00:05"
END = datetime.datetime(2026, 10, 4, 12, 0, 5)


def stamped(message):
    return f" INFO Mesh [{STAMP}] - {message}"


SETTING_SAMPLE = "Prewarm: kind kinematic n 650 at 40.0 s perFrame 50 on cell C"
SETTING_FIELDS = {"kind": "kinematic", "n": 650, "at_s": 40.0, "per_frame": 50, "cell": "C"}
BURST_SAMPLE = ("Prewarm burst: cell C kind kinematic n 650 perFrame 50 frames 13 | spawn ms 41.250 per puppet 0.063 "
                "| after frames 60 over budget 2 budget 34.999 ms worst 48.120 ms | gc0 1")
BURST_FIELDS = {"cell": "C", "kind": "kinematic", "n": 650, "per_frame": 50, "frames": 13, "spawn_ms": 41.25,
                "per_puppet_ms": 0.063, "after_frames": 60, "over_budget": 2, "budget_ms": 34.999, "worst_ms": 48.12,
                "gc0": 1}
RANGE_BURST = ("Prewarm burst: cell E kind range n 160 perFrame 0 frames 1 | spawn ms n/a per puppet n/a "
               "| after frames 61 over budget 0 budget uncapped ms worst 16.700 ms | gc0 0")
RANGE_FIELDS = dict(BURST_FIELDS, cell="E", kind="range", n=160, per_frame=0, frames=1, spawn_ms=None,
                    per_puppet_ms=None, after_frames=61, over_budget=0, budget_ms=None, worst_ms=16.7, gc0=0)
MONSTERS_SAMPLE = "Monsters window 5.00 s: cell C placed 200 alive 200 awake 194 healed 3 respawned 1 | gc0 0"
MONSTERS_FIELDS = {"seconds": 5.0, "cell": "C", "placed": 200, "alive": 200, "awake": 194, "healed": 3,
                   "respawned": 1, "gc0": 0}
PORT_SAMPLE = "Phantoms: cell C port 7781 mode sink kind ship P 20 V 0 groups 1"
PORT_FIELDS = {"cell": "C", "port": 7781, "mode": "sink", "kind": "ship", "p": 20, "v": 0, "groups": 1}
STEP_SAMPLE = "Phantom step 2/4: P 50 -> 100"
STEP_FIELDS = {"i": 2, "n": 4, "p": 100, "a": 50, "b": 100}
INSTALLED = ("Phantoms: MESH_PHANTOM=virtual:50:eye:C installed: mode virtual kind eye P 50 view 0 arrival 50.0/s "
             "upload 20 Hz payload transform steps none area none stand 0.00 fight 0.00 seed 12345")


class ParserTests(unittest.TestCase):
    def test_parse_prewarm_setting(self):
        cases = {
            "a burst line": (BURST_SAMPLE, None),
            "an unknown kind": (SETTING_SAMPLE.replace("kinematic", "monster"), None),
            "a fractional perFrame": (SETTING_SAMPLE.replace("perFrame 50", "perFrame 5.5"), None),
            "no cell": (SETTING_SAMPLE.replace(" on cell C", ""), None),
            "text after the cell": (SETTING_SAMPLE + " x", None),
            "a sample": (SETTING_SAMPLE, dict(SETTING_FIELDS, end=None)),
            "a log line": (stamped(SETTING_SAMPLE), dict(SETTING_FIELDS, end=END)),
            "all at once": (SETTING_SAMPLE.replace("perFrame 50", "perFrame 0"), dict(SETTING_FIELDS, per_frame=0,
                                                                                     end=None)),
            "a range in metres": ("Prewarm: kind range n 160.5 at 40 s perFrame 0 on cell E\r\n",
                                  {"kind": "range", "n": 160.5, "at_s": 40.0, "per_frame": 0, "cell": "E", "end": None}),
        }
        for name, (line, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0stage0b.parse_prewarm_setting(line), expected)

    def test_parse_prewarm_burst(self):
        cases = {
            "a setting line": (SETTING_SAMPLE, None),
            "spawn n/a alone": (BURST_SAMPLE.replace("spawn ms 41.250", "spawn ms n/a"), None),
            "per puppet n/a alone": (BURST_SAMPLE.replace("per puppet 0.063", "per puppet n/a"), None),
            "a negative worst": (BURST_SAMPLE.replace("worst 48.120", "worst -48.120"), None),
            "an uncapped worst": (BURST_SAMPLE.replace("worst 48.120", "worst uncapped"), None),
            "budget without ms": (BURST_SAMPLE.replace("34.999 ms", "34.999"), None),
            "no gc0": (BURST_SAMPLE.replace(" | gc0 1", ""), None),
            "text after gc0": (BURST_SAMPLE + " | x 1", None),
            "a sample": (BURST_SAMPLE, dict(BURST_FIELDS, end=None)),
            "a log line": (stamped(BURST_SAMPLE), dict(BURST_FIELDS, end=END)),
            "a range burst": (stamped(RANGE_BURST), dict(RANGE_FIELDS, end=END)),
            "a group burst": (RANGE_BURST.replace("kind range n 160", "kind group n 100"),
                              dict(RANGE_FIELDS, kind="group", n=100, end=None)),
        }
        for name, (line, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0stage0b.parse_prewarm_burst(line), expected)

    def test_parse_monsters_lines(self):
        windows = {
            "a setting line": ("Monsters: placed 200 in 1 disc on cell C", None),
            "no respawned": (MONSTERS_SAMPLE.replace(" respawned 1", ""), None),
            "a fractional count": (MONSTERS_SAMPLE.replace("alive 200", "alive 199.5"), None),
            "text after gc0": (MONSTERS_SAMPLE + " x", None),
            "a sample": (MONSTERS_SAMPLE, dict(MONSTERS_FIELDS, end=None)),
            "a log line": (stamped(MONSTERS_SAMPLE), dict(MONSTERS_FIELDS, end=END)),
        }
        for name, (line, expected) in windows.items():
            with self.subTest(f"window: {name}"):
                self.assertEqual(p0stage0b.parse_monsters_window(line), expected)
        settings = {
            "a window": (MONSTERS_SAMPLE, None),
            "no discs": ("Monsters: placed 200 in  on cell C", None),
            "no cell": ("Monsters: placed 200 in 1 disc", None),
            "one disc": ("Monsters: placed 200 in 1 disc on cell C",
                         {"placed": 200, "discs": "1 disc", "cell": "C", "end": None}),
            "the discs as text": (stamped("Monsters: placed 130 in 100@0,0,50;30@10,10,50:passive on cell E"),
                                  {"placed": 130, "discs": "100@0,0,50;30@10,10,50:passive", "cell": "E", "end": END}),
        }
        for name, (line, expected) in settings.items():
            with self.subTest(f"setting: {name}"):
                self.assertEqual(p0stage0b.parse_monsters_setting(line), expected)

    def test_parse_phantoms_port(self):
        cases = {
            "the installed line": (INSTALLED, None),
            "a warning about the cell": ("Phantoms: cell C: the server's connection limit 500 stops phantom binds at "
                                         "400 phantoms and 2 real peers", None),
            "an unknown mode": (PORT_SAMPLE.replace("mode sink", "mode remote"), None),
            "no groups": (PORT_SAMPLE.replace(" groups 1", ""), None),
            "a sample": (PORT_SAMPLE, dict(PORT_FIELDS, end=None)),
            "a virtual eye line": (stamped("Phantoms: cell E port 7790 mode virtual kind eye P 400 V 0 groups 4"),
                                   {"cell": "E", "port": 7790, "mode": "virtual", "kind": "eye", "p": 400, "v": 0,
                                    "groups": 4, "end": END}),
        }
        for name, (line, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0stage0b.parse_phantoms_port(line), expected)

    def test_parse_phantom_step(self):
        cases = {
            "step 0": (STEP_SAMPLE.replace("2/4", "0/4"), None),
            "beyond the count": (STEP_SAMPLE.replace("2/4", "5/4"), None),
            "a negative target": (STEP_SAMPLE.replace("-> 100", "-> -100"), None),
            "text after": (STEP_SAMPLE + " x", None),
            "a sample": (STEP_SAMPLE, dict(STEP_FIELDS, end=None)),
            "the first step": (stamped("Phantom step 1/4: P 0 -> 50"),
                               {"i": 1, "n": 4, "p": 50, "a": 0, "b": 50, "end": END}),
            "a step down": ("Phantom step 4/4: P 400 -> 50\r\n", {"i": 4, "n": 4, "p": 50, "a": 400, "b": 50,
                                                                    "end": None}),
        }
        for name, (line, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0stage0b.parse_phantom_step(line), expected)


class GrammarTests(unittest.TestCase):
    def test_prewarm_setting(self):
        cases = {
            "no time": ("kinematic:100", (None, "'kinematic:100' is not <kind>:<N>@<t>[/<perFrame>]")),
            "no kind": ("100@20", (None, "'100@20' is not <kind>:<N>@<t>[/<perFrame>]")),
            "an unknown kind": ("monster:100@20", (None, "kind 'monster' is not steered, kinematic, ship, range or "
                                                         "group")),
            "a zero count": ("ship:0@20", (None, "count '0' is not an integer from 1 to 10000")),
            "too many": ("steered:10001@20", (None, "count '10001' is not an integer from 1 to 10000")),
            "a fractional count": ("group:1.5@20", (None, "count '1.5' is not an integer from 1 to 10000")),
            "a zero range": ("range:0@20", (None, "range '0' is not a number of metres above 0 and at most 10000")),
            "too wide": ("range:10000.5@20", (None, "range '10000.5' is not a number of metres above 0 and at most "
                                                    "10000")),
            "a NaN range": ("range:NaN@20", (None, "range 'NaN' is not a number of metres above 0 and at most 10000")),
            "a negative time": ("ship:10@-1", (None, "time '-1' is not a number of seconds of at least 0")),
            "a word time": ("ship:10@soon", (None, "time 'soon' is not a number of seconds of at least 0")),
            "a zero perFrame": ("kinematic:650@40/0", (None, "perFrame '0' is not an integer from 1 to the count 650")),
            "a perFrame above N": ("kinematic:650@40/651",
                                   (None, "perFrame '651' is not an integer from 1 to the count 650")),
            "an empty perFrame": ("kinematic:650@40/", (None, "perFrame '' is not an integer from 1 to the count 650")),
            "a range perFrame": ("range:160@40/1", (None, "perFrame '1' is given for range, which changes the range "
                                                          "at once")),
            "a group before its lead": ("group:100@9.5", (None, "time '9.5' is under the group's 10 s lead (its "
                                                                "characters spawn 10 s before the burst)")),
            "a paced group at 0": ("group:100@0/10", (None, "time '0' is under the group's 10 s lead (its characters "
                                                            "spawn 10 s before the burst)")),
            "unset": (None, (None, None)),
            "blank": ("  ", (None, None)),
            "all at once": ("kinematic:650@40", (("kinematic", 650, 40.0, 0), None)),
            "paced": ("kinematic:650@40/50", (("kinematic", 650, 40.0, 50), None)),
            "the edges": ("ship:10000@0/10000", (("ship", 10000, 0.0, 10000), None)),
            "a range": ("range:160@40", (("range", 160.0, 40.0, 0), None)),
            "the widest range": ("range:1e4@2.5", (("range", 10000.0, 2.5, 0), None)),
            "case and whitespace": (" Group : 100 @ 30 / 10 ", (("group", 100, 30.0, 10), None)),
            "a group at its lead": ("group:300@10", (("group", 300, 10.0, 0), None)),
            "steered": ("steered:300@40", (("steered", 300, 40.0, 0), None)),
        }
        for name, (text, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0stage0b.prewarm_setting(text), expected)

    def test_monster_place_setting(self):
        cases = {
            "an empty part": ("100@0,0,50;", (None, "'100@0,0,50;' has an empty part")),
            "no geometry": ("100", (None, "part '100' is not <n>@<x>,<z>,<r>[:passive]")),
            "two numbers": ("100@0,50", (None, "part '100@0,50' is not <n>@<x>,<z>,<r>[:passive]")),
            "a zero count": ("0@0,0,50", (None, "part '0@0,0,50': count '0' is not an integer of at least 1")),
            "a negative count": ("-1@0,0,50", (None, "part '-1@0,0,50': count '-1' is not an integer of at least 1")),
            "a word centre": ("10@x,0,50", (None, "part '10@x,0,50': centre 'x,0' is not two numbers")),
            "a zero radius": ("10@0,0,0", (None, "part '10@0,0,0': radius '0' is not a number above 0")),
            "a negative radius": ("10@0,0,-5", (None, "part '10@0,0,-5': radius '-5' is not a number above 0")),
            "an unknown flag": ("10@0,0,5:angry", (None, "part '10@0,0,5:angry': 'angry' is not passive")),
            "over the total": ("1500@0,0,50;501@100,100,50",
                               (None, "part '501@100,100,50': the total 2001 is above 2000")),
            "unset": (None, (None, None)),
            "blank": (" ", (None, None)),
            "one disc": ("200@-120,40,60", (((200, -120.0, 40.0, 60.0, False),), None)),
            "passive and fighting discs": (" 30 @ 50 , 50 , 50 : Passive ; 100@0,0,50 ",
                                           (((30, 50.0, 50.0, 50.0, True), (100, 0.0, 0.0, 50.0, False)), None)),
            "the total edge": ("2000@0,0,1.5", (((2000, 0.0, 0.0, 1.5, False),), None)),
        }
        for name, (text, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0stage0b.monster_place_setting(text), expected)

    def test_park_sleep_setting(self):
        cases = {
            "two": ("2", (None, "'2' is not 0 or 1")),
            "a word": ("on", (None, "'on' is not 0 or 1")),
            "unset": (None, (None, None)),
            "blank": ("", (None, None)),
            "off": ("0", (0, None)),
            "on": (" 1 ", (1, None)),
        }
        for name, (text, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0stage0b.park_sleep_setting(text), expected)


class RegistryTests(unittest.TestCase):
    def test_the_three_step_8a_keys(self):
        self.assertEqual(set(p0stage0b.STEP8_REGISTRY), {"MESH_PREWARM", "MESH_SHIP_PARK_SLEEP", "MESH_MONSTER_PLACE"})
        for key, meaning in p0stage0b.STEP8_REGISTRY.items():
            with self.subTest(key):
                self.assertTrue(meaning.strip())
                self.assertNotIn("\n", meaning)


if __name__ == "__main__":
    unittest.main()
