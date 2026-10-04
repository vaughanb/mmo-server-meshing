"""Unit tests for p0phantom (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py")."""
import datetime
import unittest

import p0phantom

STAMP = "2026-10-01 12:00:05"
END = datetime.datetime(2026, 10, 1, 12, 0, 5)


def stamped(message):
    return f" INFO Mesh [{STAMP}] - {message}"


SEND_SAMPLE = (
    "Send window 5.00 s: calls 1250 250.0/s bytes 512000 100.0 kB/s "
    "| t119 calls 1000 bytes 501760 200.0/s 98.0 kB/s 0.00 us | t2 calls 250 bytes 10240 50.0/s 2.0 kB/s 0.00 us "
    "| other calls 0 bytes 0 0.0/s 0.0 kB/s 0.00 us | refused 0 tooBig 0 bytes 0 "
    "| delivery ru 0 0 seq 0 0 ro 250 10240 rs 0 0 u 1000 501760 other 0 0 | channels 0:250 2:1000 other:0 "
    "| wire 530000 B 1300 pkt | offThread 0")
SEND_FIELDS = {
    "seconds": 5.0, "calls": 1250, "calls_per_s": 250.0, "bytes": 512000, "kb_per_s": 100.0,
    "types": [{"type": 119, "calls": 1000, "bytes": 501760, "calls_per_s": 200.0, "kb_per_s": 98.0, "us": 0.0},
              {"type": 2, "calls": 250, "bytes": 10240, "calls_per_s": 50.0, "kb_per_s": 2.0, "us": 0.0}],
    "other": {"calls": 0, "bytes": 0, "calls_per_s": 0.0, "kb_per_s": 0.0, "us": 0.0},
    "refused": 0, "too_big": 0, "too_big_bytes": 0,
    "delivery": {"ru": (0, 0), "seq": (0, 0), "ro": (250, 10240), "rs": (0, 0), "u": (1000, 501760), "other": (0, 0)},
    "channels": {0: 250, 2: 1000}, "channels_other": 0, "wire_bytes": 530000, "wire_packets": 1300, "off_thread": 0,
    "records": None, "sizes": None}
SEND_EMPTY = ("Send window 5.00 s: calls 0 0.0/s bytes 0 0.0 kB/s | other calls 0 bytes 0 0.0/s 0.0 kB/s 0.00 us "
              "| refused 0 tooBig 0 bytes 0 | delivery ru 0 0 seq 0 0 ro 0 0 rs 0 0 u 0 0 other 0 0 "
              "| channels other:0 | wire 0 B 0 pkt | offThread 0")
SEND_TYPE_GROUP = "| t{t} calls 1 bytes 1 0.2/s 0.0 kB/s 0.00 us "
RECORDS_GROUP = "| records ro 300 u 49700 other 0 unread 0 "
SEND_RECORDS = SEND_SAMPLE.replace("| channels", RECORDS_GROUP + "| channels")
SIZES_GROUP = "| sizes n 5000 min 54 max 54 top 54:5000 0:0 0:0 payload 54.0 framed 60.0 B over 0 unread 0 "
SIZES_FIELDS = {"n": 5000, "min": 54, "max": 54, "top": [(54, 5000), (0, 0), (0, 0)], "payload": 54.0, "framed": 60.0,
                "over": 0, "unread": 0}
SEND_SIZES = SEND_RECORDS.replace("| channels", SIZES_GROUP + "| channels")

PHANTOM_SAMPLE = (
    "Phantom window 5.00 s: bound virtual 200 sink 0 ready 200 filler 2 eyeSeen 0 | step 0/0 target 200 "
    "| vmove n 20 mean 100.0 p95 100 min 99 | uploads 4000.0/s | posErr n 3000 p50 0.12 p95 0.24 max 0.41 m "
    "| down n 200 mean 55.3 p95 56.0 max 57.2 kB/s "
    "| swallowed ru 0 0 seq 0 0 ro 1000 56000 rs 0 0 u 60000 56000000 other 0 0 "
    "| frames 150 rig feeder 1.20 binder 0.10 sampler 0.05 ms/s 0.045 ms/frame")
PHANTOM_FIELDS = {
    "seconds": 5.0, "bound_virtual": 200, "bound_sink": 0, "ready": 200, "filler": 2, "eye_seen": 0, "fighting": None,
    "step": 0, "steps": 0, "target": 200, "vmove_n": 20, "vmove_mean": 100.0, "vmove_p95": 100, "vmove_min": 99,
    "uploads_per_s": 4000.0, "pos_err_n": 3000, "pos_err_p50": 0.12, "pos_err_p95": 0.24, "pos_err_max": 0.41,
    "down_n": 200, "down_mean": 55.3, "down_p95": 56.0, "down_max": 57.2,
    "swallowed": {"ru": (0, 0), "seq": (0, 0), "ro": (1000, 56000), "rs": (0, 0), "u": (60000, 56000000),
                  "other": (0, 0)},
    "frames": 150, "feeder_ms_per_s": 1.2, "binder_ms_per_s": 0.1, "sampler_ms_per_s": 0.05, "rig_ms_per_frame": 0.045,
    "view_err_n": None, "view_err_p50": None, "view_err_p95": None, "view_err_max": None}
VIEW_ERR_GROUP = "| viewErr n 64 p50 0.08 p95 0.31 max 0.77 m "
PHANTOM_VIEW_ERR = PHANTOM_SAMPLE.replace("| down n", VIEW_ERR_GROUP + "| down n")

SINK_SAMPLE = (
    "Sink window 5.00 s: peers 50 connected 50 bound 50 | in 2621440 B 6000 pkt 512.0 kB/s "
    "| down n 50 mean 10.2 p95 10.5 max 10.9 kB/s | t119 calls 5000 bytes 2500000 | t65001 calls 0 bytes 0 "
    "| other calls 1000 bytes 30000 | out 5000 uploads 1000.0/s 30.0 kB/s | disconnects 0 | workers 1 maxPassMs 3.2")
SINK_FIELDS = {
    "seconds": 5.0, "peers": 50, "connected": 50, "bound": 50, "in_bytes": 2621440, "in_packets": 6000,
    "in_kb_per_s": 512.0, "down_n": 50, "down_mean": 10.2, "down_p95": 10.5, "down_max": 10.9,
    "types": [{"type": 119, "calls": 5000, "bytes": 2500000}, {"type": 65001, "calls": 0, "bytes": 0}],
    "other_calls": 1000, "other_bytes": 30000, "out_uploads": 5000, "uploads_per_s": 1000.0, "out_kb_per_s": 30.0,
    "disconnects": 0, "workers": 1, "max_pass_ms": 3.2, "sizes": None}

BOT_SAMPLE = ("Bot net window 5.00 s: in 52.3 kB/s 120 pkt/s out 3.1 kB/s 22 pkt/s | hops 0 | rtt 1 ms "
              "| players seen 200 | at 2026-10-01T12:00:05Z")
BOT_FIELDS = {"seconds": 5.0, "in_kb_per_s": 52.3, "in_packets_per_s": 120.0, "out_kb_per_s": 3.1,
              "out_packets_per_s": 22.0, "hops": 0, "rtt_ms": 1, "players_seen": 200,
              "at": datetime.datetime(2026, 10, 1, 12, 0, 5, tzinfo=datetime.timezone.utc)}


def with_types(sample, marker, groups):
    head, tail = sample.split(marker, 1)
    return head + "".join(groups) + marker + tail


class SendWindowTests(unittest.TestCase):
    def test_parse_send_window(self):
        seven = [SEND_TYPE_GROUP.format(t=t) for t in range(1, 8)]
        cases = {
            "a phantom window": (PHANTOM_SAMPLE, None),
            "a work window": ("Work window 5.00 s: calls 0", None),
            "seven type groups": (with_types(SEND_EMPTY, "| other calls", seven), None),
            "a repeated type": (with_types(SEND_EMPTY, "| other calls", [SEND_TYPE_GROUP.format(t=7)] * 2), None),
            "a type above 65535": (with_types(SEND_EMPTY, "| other calls", [SEND_TYPE_GROUP.format(t=65536)]), None),
            "channel 16 listed": (SEND_SAMPLE.replace("channels 0:250 2:1000", "channels 0:250 16:1000"), None),
            "channels out of order": (SEND_SAMPLE.replace("channels 0:250 2:1000", "channels 2:1000 0:250"), None),
            "an empty channel listed": (SEND_SAMPLE.replace("channels 0:250 2:1000", "channels 0:250 1:0 2:1000"),
                                        None),
            "a missing delivery group": (SEND_SAMPLE.replace("rs 0 0 ", ""), None),
            "a NaN rate": (SEND_SAMPLE.replace("250.0/s", "NaN/s"), None),
            "a negative total": (SEND_SAMPLE.replace("calls 1250", "calls -1250"), None),
            "Unicode us": (SEND_SAMPLE.replace("0.00 us", "0.00 µs"), None),
            "text after offThread": (SEND_SAMPLE + " | extra 1", None),
            "cut before wire": (SEND_SAMPLE.split(" | wire")[0], None),
            "cut before offThread": (SEND_SAMPLE.split(" | offThread")[0], None),
            "no other group": (SEND_SAMPLE.replace("| other calls 0 bytes 0 0.0/s 0.0 kB/s 0.00 us ", ""), None),
            "a sample": (SEND_SAMPLE, dict(SEND_FIELDS, end=None)),
            "a log line": (stamped(SEND_SAMPLE), dict(SEND_FIELDS, end=END)),
            "an empty window": (SEND_EMPTY, {
                "seconds": 5.0, "calls": 0, "calls_per_s": 0.0, "bytes": 0, "kb_per_s": 0.0, "types": [],
                "other": {"calls": 0, "bytes": 0, "calls_per_s": 0.0, "kb_per_s": 0.0, "us": 0.0},
                "refused": 0, "too_big": 0, "too_big_bytes": 0,
                "delivery": {name: (0, 0) for name in p0phantom.DELIVERIES}, "channels": {}, "channels_other": 0,
                "wire_bytes": 0, "wire_packets": 0, "off_thread": 0, "records": None, "sizes": None, "end": None}),
        }
        for name, (line, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0phantom.parse_send_window(line), expected)

    def test_the_records_group(self):
        cases = {
            "records before delivery": (SEND_SAMPLE.replace("| delivery", RECORDS_GROUP + "| delivery"), None),
            "records after channels": (SEND_SAMPLE.replace("| wire", RECORDS_GROUP + "| wire"), None),
            "records twice": (SEND_SAMPLE.replace("| channels", RECORDS_GROUP * 2 + "| channels"), None),
            "no unread": (SEND_SAMPLE.replace("| channels", "| records ro 300 u 49700 other 0 | channels"), None),
            "rs instead of other": (SEND_SAMPLE.replace("| channels", RECORDS_GROUP.replace("other", "rs")
                                                        + "| channels"), None),
            "a fractional count": (SEND_SAMPLE.replace("| channels", RECORDS_GROUP.replace("ro 300", "ro 300.5")
                                                       + "| channels"), None),
            "a negative count": (SEND_SAMPLE.replace("| channels", RECORDS_GROUP.replace("u 49700", "u -49700")
                                                     + "| channels"), None),
            "a step 5 line": (SEND_SAMPLE, dict(SEND_FIELDS, end=None)),
            "a step 6 line": (SEND_RECORDS, dict(SEND_FIELDS, end=None,
                                                 records={"ro": 300, "u": 49700, "other": 0, "unread": 0})),
            "a step 6 log line": (stamped(SEND_RECORDS), dict(SEND_FIELDS, end=END,
                                                              records={"ro": 300, "u": 49700, "other": 0,
                                                                       "unread": 0})),
            "only unreadable": (SEND_SAMPLE.replace("| channels", "| records ro 0 u 0 other 0 unread 2 | channels"),
                                dict(SEND_FIELDS, end=None, records={"ro": 0, "u": 0, "other": 0, "unread": 2})),
        }
        for name, (line, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0phantom.parse_send_window(line), expected)

    def test_the_sizes_group(self):
        records = {"ro": 300, "u": 49700, "other": 0, "unread": 0}
        cases = {
            "sizes before records": (SEND_SAMPLE.replace("| channels", SIZES_GROUP + RECORDS_GROUP + "| channels"), None),
            "sizes after channels": (SEND_SAMPLE.replace("| wire", SIZES_GROUP + "| wire"), None),
            "sizes twice": (SEND_RECORDS.replace("| channels", SIZES_GROUP * 2 + "| channels"), None),
            "two top lengths": (SEND_SIZES.replace(" 0:0 0:0 payload", " 0:0 payload"), None),
            "no framed mean": (SEND_SIZES.replace(" framed 60.0 B", " B"), None),
            "a negative count": (SEND_SIZES.replace("sizes n 5000", "sizes n -5000"), None),
            "a step 6 line": (SEND_RECORDS, dict(SEND_FIELDS, end=None, records=records)),
            "a step 7 line": (SEND_SIZES, dict(SEND_FIELDS, end=None, records=records, sizes=SIZES_FIELDS)),
            "a step 7 log line": (stamped(SEND_SIZES), dict(SEND_FIELDS, end=END, records=records, sizes=SIZES_FIELDS)),
            "sizes without records": (SEND_SAMPLE.replace("| channels", SIZES_GROUP + "| channels"),
                                      dict(SEND_FIELDS, end=None, sizes=SIZES_FIELDS)),
            "three lengths": (SEND_SIZES.replace("min 54 max 54 top 54:5000 0:0 0:0 payload 54.0 framed 60.0",
                                                 "min 28 max 56 top 28:3900 56:1000 54:100 payload 34.0 framed 40.5"),
                              dict(SEND_FIELDS, end=None, records=records,
                                   sizes=dict(SIZES_FIELDS, min=28, max=56, payload=34.0, framed=40.5,
                                              top=[(28, 3900), (56, 1000), (54, 100)]))),
        }
        for name, (line, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0phantom.parse_send_window(line), expected)

    def test_records_need_t119_calls(self):
        no_119 = SEND_EMPTY.replace("| channels", RECORDS_GROUP + "| channels")
        hidden_119 = SEND_EMPTY.replace("| other calls 0 bytes 0", "| other calls 5 bytes 0").replace(
            "| channels", RECORDS_GROUP + "| channels")
        zero_records = SEND_EMPTY.replace("| channels", "| records ro 0 u 0 other 0 unread 0 | channels")
        cases = {
            "records and no t119": (no_119, ["records without t119 calls: ro 300 u 49700 other 0 unread 0"]),
            "a step 6 line": (SEND_RECORDS, []),
            "t119 in other": (hidden_119, None),
            "a zero records group": (zero_records, []),
            "no records group": (SEND_EMPTY, []),
        }
        for name, (line, expected) in cases.items():
            with self.subTest(name):
                problems = p0phantom.send_window_problems(p0phantom.parse_send_window(line))
                if expected is None:
                    self.assertNotIn("records without t119 calls", " ".join(problems))
                else:
                    self.assertEqual(problems, expected)

    def test_six_type_groups_parse(self):
        six = [SEND_TYPE_GROUP.format(t=t) for t in range(1, 7)]
        fields = p0phantom.parse_send_window(with_types(SEND_EMPTY, "| other calls", six))
        self.assertEqual([g["type"] for g in fields["types"]], [1, 2, 3, 4, 5, 6])

    def test_send_window_problems(self):
        def problems(**changes):
            fields = p0phantom.parse_send_window(SEND_SAMPLE)
            fields.update(changes)
            return p0phantom.send_window_problems(fields)

        swapped = list(reversed(SEND_FIELDS["types"]))
        cases = {
            "calls above the groups": (
                {"calls": 1251},
                ["type groups and other sum to 1250, not the window's 1251 calls",
                 "delivery groups sum to 1250, not the window's 1251 calls",
                 "channels sum to 1250, not the window's 1251 calls"]),
            "bytes above the groups": (
                {"bytes": 512001},
                ["type groups and other sum to 512000, not the window's 512001 bytes",
                 "delivery groups sum to 512000, not the window's 512001 bytes"]),
            "a channel short": ({"channels": {0: 250, 2: 999}},
                                ["channels sum to 1249, not the window's 1250 calls"]),
            "groups by bytes ascending": ({"types": swapped},
                                          ["type groups are not by bytes descending, ties by type ascending: "
                                           "t2:10240 t119:501760"]),
            "a tie by type descending": (
                {"types": [dict(SEND_FIELDS["types"][1], type=9, bytes=256000),
                           dict(SEND_FIELDS["types"][0], type=3, bytes=256000)]},
                ["type groups are not by bytes descending, ties by type ascending: t9:256000 t3:256000"]),
            "the sample": ({}, []),
            "a tie by type ascending": (
                {"types": [dict(SEND_FIELDS["types"][0], type=3, bytes=256000),
                           dict(SEND_FIELDS["types"][1], type=9, bytes=256000)]}, []),
        }
        for name, (changes, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(problems(**changes), expected)


class PhantomWindowTests(unittest.TestCase):
    def test_parse_phantom_window(self):
        cases = {
            "a send window": (SEND_SAMPLE, None),
            "no eyeSeen": (PHANTOM_SAMPLE.replace(" eyeSeen 0", ""), None),
            "a fractional bound count": (PHANTOM_SAMPLE.replace("virtual 200", "virtual 200.5"), None),
            "no posErr unit": (PHANTOM_SAMPLE.replace("max 0.41 m", "max 0.41"), None),
            "a missing swallowed group": (PHANTOM_SAMPLE.replace("rs 0 0 ", ""), None),
            "a NaN rate": (PHANTOM_SAMPLE.replace("4000.0/s", "NaN/s"), None),
            "fighting after the step": (PHANTOM_SAMPLE.replace("target 200", "target 200 fighting 3"), None),
            "text after ms/frame": (PHANTOM_SAMPLE + " | extra 1", None),
            "cut before the rig": (PHANTOM_SAMPLE.split(" | frames")[0], None),
            "a sample": (PHANTOM_SAMPLE, dict(PHANTOM_FIELDS, end=None)),
            "a log line": (stamped(PHANTOM_SAMPLE), dict(PHANTOM_FIELDS, end=END)),
            "fighting": (PHANTOM_SAMPLE.replace("eyeSeen 0", "eyeSeen 0 fighting 40"),
                         dict(PHANTOM_FIELDS, fighting=40, end=None)),
            "a step": (PHANTOM_SAMPLE.replace("step 0/0 target 200", "step 2/3 target 100"),
                       dict(PHANTOM_FIELDS, step=2, steps=3, target=100, end=None)),
        }
        for name, (line, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0phantom.parse_phantom_window(line), expected)

    def test_the_view_err_group(self):
        view_err = {"view_err_n": 64, "view_err_p50": 0.08, "view_err_p95": 0.31, "view_err_max": 0.77}
        cases = {
            "viewErr before posErr": (PHANTOM_SAMPLE.replace("| posErr", VIEW_ERR_GROUP + "| posErr"), None),
            "viewErr after down": (PHANTOM_SAMPLE.replace("| swallowed", VIEW_ERR_GROUP + "| swallowed"), None),
            "viewErr twice": (PHANTOM_SAMPLE.replace("| down n", VIEW_ERR_GROUP * 2 + "| down n"), None),
            "no viewErr unit": (PHANTOM_VIEW_ERR.replace("max 0.77 m", "max 0.77"), None),
            "no p95": (PHANTOM_VIEW_ERR.replace(" p95 0.31", ""), None),
            "a fractional n": (PHANTOM_VIEW_ERR.replace("viewErr n 64", "viewErr n 64.5"), None),
            "a NaN p50": (PHANTOM_VIEW_ERR.replace("p50 0.08", "p50 NaN"), None),
            "a step 5 line": (PHANTOM_SAMPLE, dict(PHANTOM_FIELDS, end=None)),
            "a step 6 line": (PHANTOM_VIEW_ERR, dict(PHANTOM_FIELDS, end=None, **view_err)),
            "a step 6 log line": (stamped(PHANTOM_VIEW_ERR), dict(PHANTOM_FIELDS, end=END, **view_err)),
            "an empty sample": (PHANTOM_SAMPLE.replace("| down n", "| viewErr n 0 p50 0.00 p95 0.00 max 0.00 m | down n"),
                                dict(PHANTOM_FIELDS, end=None, view_err_n=0, view_err_p50=0.0, view_err_p95=0.0,
                                     view_err_max=0.0)),
            "with fighting": (PHANTOM_VIEW_ERR.replace("eyeSeen 0", "eyeSeen 0 fighting 40"),
                              dict(PHANTOM_FIELDS, end=None, fighting=40, **view_err)),
        }
        for name, (line, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0phantom.parse_phantom_window(line), expected)


class SinkWindowTests(unittest.TestCase):
    def test_parse_sink_window(self):
        group = "| t{t} calls 1 bytes 1 "
        seven = [group.format(t=t) for t in range(1, 8)]
        bare = SINK_SAMPLE.replace("| t119 calls 5000 bytes 2500000 | t65001 calls 0 bytes 0 ", "")
        cases = {
            "a send window": (SEND_SAMPLE, None),
            "seven type groups": (with_types(bare, "| other calls", seven), None),
            "a repeated type": (SINK_SAMPLE.replace("t65001", "t119"), None),
            "a type above 65535": (SINK_SAMPLE.replace("t65001", "t65536"), None),
            "fractional in bytes": (SINK_SAMPLE.replace("in 2621440 B", "in 2621440.5 B"), None),
            "no out group": (SINK_SAMPLE.replace("| out 5000 uploads 1000.0/s 30.0 kB/s ", ""), None),
            "text after maxPassMs": (SINK_SAMPLE + " | extra 1", None),
            "cut before workers": (SINK_SAMPLE.split(" | workers")[0], None),
            "a sample": (SINK_SAMPLE, dict(SINK_FIELDS, end=None)),
            "a log line": (stamped(SINK_SAMPLE), dict(SINK_FIELDS, end=END)),
            "no type groups": (bare, dict(SINK_FIELDS, types=[], end=None)),
            "a sizes group": (SINK_SAMPLE + " " + SIZES_GROUP.strip(), dict(SINK_FIELDS, sizes=SIZES_FIELDS, end=None)),
            "a sizes group before workers": (SINK_SAMPLE.replace("| workers", SIZES_GROUP + "| workers"), None),
            "a cut sizes group": (SINK_SAMPLE + " | sizes n 20 min 56", None),
        }
        for name, (line, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0phantom.parse_sink_window(line), expected)


class BotNetWindowTests(unittest.TestCase):
    def test_parse_bot_net_window(self):
        cases = {
            "a sink window": (SINK_SAMPLE, None),
            "a local time": (BOT_SAMPLE.replace("12:00:05Z", "12:00:05"), None),
            "an impossible date": (BOT_SAMPLE.replace("2026-10-01", "2026-13-01"), None),
            "a fractional rtt": (BOT_SAMPLE.replace("rtt 1 ms", "rtt 1.5 ms"), None),
            "no players seen": (BOT_SAMPLE.replace("| players seen 200 ", ""), None),
            "cut before at": (BOT_SAMPLE.split(" | at")[0], None),
            "a sample": (BOT_SAMPLE, dict(BOT_FIELDS, end=None)),
            "a bot log line": (f"[Mesh] {BOT_SAMPLE}", dict(BOT_FIELDS, end=None)),
            "fractional packet rates": (BOT_SAMPLE.replace("120 pkt/s", "119.8 pkt/s"),
                                        dict(BOT_FIELDS, in_packets_per_s=119.8, end=None)),
        }
        for name, (line, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0phantom.parse_bot_net_window(line), expected)


class PhantomSettingTests(unittest.TestCase):
    def test_phantom_setting(self):
        refused = (None, None, None, None)
        shape = p0phantom.PHANTOM_SHAPE
        cases = {
            "no cell field": ("virtual:20:character", refused + (f"'virtual:20:character' has no cell field; "
                                                                f"expected {shape}",)),
            "two parts": ("virtual:20", refused + (f"'virtual:20' is not {shape}",)),
            "five parts": ("virtual:20:eye:C:x", refused + (f"'virtual:20:eye:C:x' is not {shape}",)),
            "an unknown mode": ("ship:20:eye:C", refused + ("mode 'ship' is not virtual or sink",)),
            "a fifth kind": ("virtual:20:monster:C", refused + ("kind 'monster' is not character, eye or ship",)),
            "a zero count": ("virtual:0:eye:C", refused + ("count '0' is not an integer of at least 1",)),
            "a negative count": ("virtual:-5:eye:C", refused + ("count '-5' is not an integer of at least 1",)),
            "a fractional count": ("virtual:2.5:eye:C", refused + ("count '2.5' is not an integer of at least 1",)),
            "the ship kind": ("virtual:20:ship:C", ("virtual", 20, "ship", ("C",), None)),
            "a sink ship kind": ("sink:20:SHIP:C", ("sink", 20, "ship", ("C",), None)),
            "an empty cell": ("virtual:20:eye:C,,E", refused + ("cell field 'C,,E' has an empty cell",)),
            "an empty cell field": ("virtual:20:eye:", refused + ("cell field '' has an empty cell",)),
            "a repeated cell": ("virtual:20:eye:C,c", refused + ("cell field 'C,c' names c twice",)),
            "unset": (None, refused + (None,)),
            "empty": ("", refused + (None,)),
            "blank": ("  ", refused + (None,)),
            "one cell": ("virtual:20:character:SW", ("virtual", 20, "character", ("SW",), None)),
            "several cells": ("sink:50:character:C, E", ("sink", 50, "character", ("C", "E"), None)),
            "every cell": ("virtual:400:eye:all", ("virtual", 400, "eye", "all", None)),
            "case and whitespace": (" Virtual : 200 : EYE : ALL ", ("virtual", 200, "eye", "all", None)),
        }
        for name, (text, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0phantom.phantom_setting(text), expected)

    def test_ship_phantom_refusal(self):
        ship = {"MESH_PHANTOM": "virtual:4:ship:C"}
        tail = "is refused with the ship kind: a ship phantom sees the ships in its range and uploads what " \
               "MESH_SHIP_WIRE names"
        cases = {
            "view": (dict(ship, MESH_PHANTOM_VIEW="10"), f"MESH_PHANTOM_VIEW=10 {tail}"),
            "area": (dict(ship, MESH_PHANTOM_AREA="0,0,50,50"), f"MESH_PHANTOM_AREA=0,0,50,50 {tail}"),
            "stand": (dict(ship, MESH_PHANTOM_STAND="0.5"), f"MESH_PHANTOM_STAND=0.5 {tail}"),
            "fight": (dict(ship, MESH_PHANTOM_FIGHT="1"), f"MESH_PHANTOM_FIGHT=1 {tail}"),
            "steps": (dict(ship, MESH_PHANTOM_STEPS="1,4@5"), f"MESH_PHANTOM_STEPS=1,4@5 {tail}"),
            "payload": (dict(ship, MESH_PHANTOM_PAYLOAD="ship56"), f"MESH_PHANTOM_PAYLOAD=ship56 {tail}"),
            "view before payload": (dict(ship, MESH_PHANTOM_PAYLOAD="ship28", MESH_PHANTOM_VIEW="3"),
                                    f"MESH_PHANTOM_VIEW=3 {tail}"),
            "ship alone": (ship, None),
            "an empty view": (dict(ship, MESH_PHANTOM_VIEW=""), None),
            "the ship keys": (dict(ship, MESH_SHIP_WIRE="header56", MESH_PHANTOM_UPLOAD_HZ="20"), None),
            "a character with a view": ({"MESH_PHANTOM": "virtual:4:character:C", "MESH_PHANTOM_VIEW": "3"}, None),
            "no phantom": ({"MESH_PHANTOM_VIEW": "3"}, None),
        }
        for name, (env, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0phantom.ship_phantom_refusal(env), expected)

    def test_phantom_area_setting(self):
        shapes = p0phantom.AREA_SHAPES
        cases = {
            "three rect parts": ("0,0,50", (None, None, f"'0,0,50' is not {shapes}")),
            "four disc parts": ("disc:0,0,50,50", (None, None, f"'disc:0,0,50,50' is not {shapes}")),
            "a word": ("0,0,wide,50", (None, None, "'0,0,wide,50' has a part that is not a number of metres")),
            "NaN": ("disc:0,0,NaN", (None, None, "'disc:0,0,NaN' has a part that is not a number of metres")),
            "an empty part": ("0,,50,50", (None, None, "'0,,50,50' has a part that is not a number of metres")),
            "a zero width": ("0,0,0,50", (None, None, "'0,0,0,50' has a size or radius that is not above 0")),
            "a negative radius": ("disc:0,0,-5", (None, None, "'disc:0,0,-5' has a size or radius that is not above 0")),
            "unset": (None, (None, None, None)),
            "empty": (" ", (None, None, None)),
            "a rect": ("10,-20.5,100,50", ("rect", (10.0, -20.5, 100.0, 50.0), None)),
            "a disc": ("disc:0,0,50", ("disc", (0.0, 0.0, 50.0), None)),
            "an upper-case disc with spaces": (" DISC: 1 , 2 , 3e1 ", ("disc", (1.0, 2.0, 30.0), None)),
            "negative centres": ("-100,-100,20,20", ("rect", (-100.0, -100.0, 20.0, 20.0), None)),
        }
        for name, (text, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0phantom.phantom_area_setting(text), expected)

    def test_meter_sends_setting(self):
        cases = {
            "a word": ("on", (False, False, "'on' is not off, count or time")),
            "a number": ("1", (False, False, "'1' is not off, count or time")),
            "unset": (None, (False, False, None)),
            "empty": ("", (False, False, None)),
            "off": ("off", (False, False, None)),
            "count": ("count", (True, False, None)),
            "time": ("time", (True, True, None)),
            "case and whitespace": (" TIME ", (True, True, None)),
        }
        for name, (text, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0phantom.meter_sends_setting(text), expected)


if __name__ == "__main__":
    unittest.main()
