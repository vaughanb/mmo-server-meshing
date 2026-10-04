"""Unit tests for p0sender (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py")."""
import datetime
import unittest

import p0sender

STAMP = "2026-10-02 12:00:05"
END = datetime.datetime(2026, 10, 2, 12, 0, 5)


def stamped(message):
    return f" INFO Mesh [{STAMP}] - {message}"


SENDER_SAMPLE = (
    "Sender window 5.00 s: on sources 100 kit 100 direct 0 pairs 5000 clients 50 "
    "| builds 100 skipped 0 rewraps 0 refusals 0 | writes 10000 compact 0 false 0 empty 0 errors 0 dropped 0 "
    "| records u 499700 r 300 deferred 0 "
    "| packets u 15000 r 300 oversize 0 tooBig 0 sendErrors 0 reliableFailed 0 | stuckReliable 0 withheldOwner 0 "
    "| hz 20:5000 10:0 5:0 2:0 1:0 0:0 withheld 0 clamped 0 "
    "| out 2048.0 kB/s 3060.0 pkt/s per client 41.0 kB/s 61.2 pkt/s "
    "| ms/s build 3.20 fanout 4.10 send 6.00 total 13.30 | us/packet 9.80 | build p95 0.20 max 0.50 ms | gc0 0")
SENDER_FIELDS = {
    "seconds": 5.0, "setting": "on", "sources": 100, "kit": 100, "direct": 0, "pairs": 5000, "clients": 50,
    "builds": 100, "skipped": 0, "rewraps": 0, "refusals": 0,
    "writes": 10000, "compact": 0, "not_written": 0, "empty": 0, "errors": 0, "dropped": 0,
    "records_u": 499700, "records_r": 300, "deferred": 0,
    "packets_u": 15000, "packets_r": 300, "oversize": 0, "too_big": 0, "send_errors": 0, "reliable_failed": 0,
    "stuck_reliable": 0, "withheld_owner": 0,
    "hz": {20: 5000, 10: 0, 5: 0, 2: 0, 1: 0, 0: 0}, "withheld": 0, "clamped": 0,
    "out_kb_per_s": 2048.0, "out_packets_per_s": 3060.0, "client_kb_per_s": 41.0, "client_packets_per_s": 61.2,
    "build_ms_per_s": 3.2, "fanout_ms_per_s": 4.1, "send_ms_per_s": 6.0, "total_ms_per_s": 13.3,
    "us_per_packet": 9.8, "build_p95_ms": 0.2, "build_max_ms": 0.5, "gc0": 0, "payload": None}
PAYLOAD_GROUP = " | payload full n 1000 min 56 max 56 compact n 4000 min 28 max 28"
PAYLOAD_FIELDS = {"full_n": 1000, "full_min": 56, "full_max": 56, "compact_n": 4000, "compact_min": 28,
                  "compact_max": 28}
SENDER_ZEROS = (
    "Sender window 5.00 s: measure sources 0 kit 0 direct 0 pairs 0 clients 0 "
    "| builds 0 skipped 0 rewraps 0 refusals 0 | writes 0 compact 0 false 0 empty 0 errors 0 dropped 0 "
    "| records u 0 r 0 deferred 0 "
    "| packets u 0 r 0 oversize 0 tooBig 0 sendErrors 0 reliableFailed 0 | stuckReliable 0 withheldOwner 0 "
    "| hz 20:0 10:0 5:0 2:0 1:0 0:0 withheld 0 clamped 0 "
    "| out 0.0 kB/s 0.0 pkt/s per client 0.0 kB/s 0.0 pkt/s "
    "| ms/s build 0.00 fanout 0.00 send 0.00 total 0.00 | us/packet 0.00 | build p95 0.00 max 0.00 ms | gc0 0")

CE12_SAMPLE = ("CE-12 check: cell C shape=pass first=1 second=1 next=1 consume=yes after=0 reliable=1/1/1/0 "
               "handler=BuiltInEntityMovementFunctions3D")
CE12_FIELDS = {"cell": "C", "shape": "pass", "first": 1, "second": 1, "next": 1, "consume": True, "after": 0,
               "reliable": (1, 1, 1, 0), "handler": "BuiltInEntityMovementFunctions3D"}


class SenderWindowTests(unittest.TestCase):
    def test_parse_sender_window(self):
        cases = {
            "a send window": ("Send window 5.00 s: calls 0 0.0/s bytes 0 0.0 kB/s", None),
            "an unknown setting": (SENDER_SAMPLE.replace(": on ", ": kit "), None),
            "an upper-case setting": (SENDER_SAMPLE.replace(": on ", ": On "), None),
            "no setting": (SENDER_SAMPLE.replace(": on ", ": "), None),
            "hz out of order": (SENDER_SAMPLE.replace("20:5000 10:0", "10:0 20:5000"), None),
            "an hz missing": (SENDER_SAMPLE.replace(" 2:0", ""), None),
            "an hz 3 tier": (SENDER_SAMPLE.replace(" 2:0", " 3:0"), None),
            "no withheld": (SENDER_SAMPLE.replace(" withheld 0", ""), None),
            "residual ms/tick": (SENDER_SAMPLE.replace("| us/packet", "| residual 0.10 ms/tick | us/packet"), None),
            "a fractional count": (SENDER_SAMPLE.replace("builds 100", "builds 100.5"), None),
            "a negative count": (SENDER_SAMPLE.replace("errors 0", "errors -1"), None),
            "a NaN rate": (SENDER_SAMPLE.replace("2048.0 kB/s", "NaN kB/s"), None),
            "a negative rate": (SENDER_SAMPLE.replace("3.20", "-3.20"), None),
            "Unicode us": (SENDER_SAMPLE.replace("us/packet", "µs/packet"), None),
            "text after gc0": (SENDER_SAMPLE + " | extra 1", None),
            "cut before gc0": (SENDER_SAMPLE.split(" | gc0")[0], None),
            "cut before the build distribution": (SENDER_SAMPLE.split(" | build p95")[0], None),
            "a sample": (SENDER_SAMPLE, dict(SENDER_FIELDS, end=None)),
            "a log line": (stamped(SENDER_SAMPLE), dict(SENDER_FIELDS, end=END)),
            "trailing whitespace": (SENDER_SAMPLE + "\r\n", dict(SENDER_FIELDS, end=None)),
            "measure": (SENDER_SAMPLE.replace(": on ", ": measure "), dict(SENDER_FIELDS, setting="measure", end=None)),
            "off": (SENDER_SAMPLE.replace(": on ", ": off "), dict(SENDER_FIELDS, setting="off", end=None)),
            "tiers": (SENDER_SAMPLE.replace("20:5000 10:0 5:0 2:0 1:0 0:0 withheld 0",
                                            "20:1000 10:2000 5:1500 2:0 1:0 0:450 withheld 50"),
                      dict(SENDER_FIELDS, hz={20: 1000, 10: 2000, 5: 1500, 2: 0, 1: 0, 0: 450}, withheld=50,
                           end=None)),
        }
        for name, (line, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0sender.parse_sender_window(line), expected)

    def test_the_zeros_line_parses(self):
        fields = p0sender.parse_sender_window(SENDER_ZEROS)
        self.assertEqual(fields["setting"], "measure")
        self.assertEqual(fields["hz"], {hz: 0 for hz in p0sender.HZ})
        self.assertIsNone(fields["payload"])
        self.assertTrue(all(value == 0 for name, value in fields.items()
                            if name not in ("setting", "hz", "end", "seconds", "payload")))

    def test_counts_are_int_and_rates_float(self):
        fields = p0sender.parse_sender_window(SENDER_SAMPLE)
        for name, value in fields.items():
            if name in ("setting", "hz", "end", "payload"):
                continue
            with self.subTest(name):
                self.assertIs(type(value), float if name in p0sender.SENDER_WINDOW_FLOATS else int)


class SenderPayloadTests(unittest.TestCase):
    def test_the_payload_group(self):
        cases = {
            "payload before gc0": (SENDER_SAMPLE.replace(" | gc0 0", PAYLOAD_GROUP + " | gc0 0"), None),
            "payload twice": (SENDER_SAMPLE + PAYLOAD_GROUP * 2, None),
            "no compact part": (SENDER_SAMPLE + PAYLOAD_GROUP.split(" compact")[0], None),
            "no full part": (SENDER_SAMPLE + " | payload compact n 4000 min 28 max 28", None),
            "a fractional min": (SENDER_SAMPLE + PAYLOAD_GROUP.replace("min 56", "min 56.5"), None),
            "a negative count": (SENDER_SAMPLE + PAYLOAD_GROUP.replace("n 4000", "n -4000"), None),
            "text after the group": (SENDER_SAMPLE + PAYLOAD_GROUP + " | extra 1", None),
            "a step 6 line": (SENDER_SAMPLE, dict(SENDER_FIELDS, end=None)),
            "a step 7 line": (SENDER_SAMPLE + PAYLOAD_GROUP, dict(SENDER_FIELDS, end=None, payload=PAYLOAD_FIELDS)),
            "a step 7 log line": (stamped(SENDER_SAMPLE + PAYLOAD_GROUP),
                                  dict(SENDER_FIELDS, end=END, payload=PAYLOAD_FIELDS)),
            "nothing written": (SENDER_ZEROS + " | payload full n 0 min 0 max 0 compact n 0 min 0 max 0", "zeros"),
        }
        for name, (line, expected) in cases.items():
            with self.subTest(name):
                fields = p0sender.parse_sender_window(line)
                if expected == "zeros":
                    self.assertEqual(fields["payload"], {key: 0 for key in p0sender.PAYLOAD})
                else:
                    self.assertEqual(fields, expected)


class Ce12CheckTests(unittest.TestCase):
    def test_parse_ce12_check(self):
        cases = {
            "a sender window": (SENDER_SAMPLE, None),
            "an unknown shape": (CE12_SAMPLE.replace("shape=pass", "shape=flag"), None),
            "a lower-case shape": (CE12_SAMPLE.replace("shape=pass", "shape=percall"), None),
            "a bit of 2": (CE12_SAMPLE.replace("second=1", "second=2"), None),
            "consume as a bit": (CE12_SAMPLE.replace("consume=yes", "consume=1"), None),
            "three reliable bits": (CE12_SAMPLE.replace("reliable=1/1/1/0", "reliable=1/1/1"), None),
            "no handler": (CE12_SAMPLE.split(" handler=")[0], None),
            "an empty handler": (CE12_SAMPLE.replace("=BuiltInEntityMovementFunctions3D", "="), None),
            "no cell": (CE12_SAMPLE.replace("cell C ", ""), None),
            "fields out of order": (CE12_SAMPLE.replace("first=1 second=1", "second=1 first=1"), None),
            "text after the handler": (CE12_SAMPLE + " extra", None),
            "a sample": (CE12_SAMPLE, dict(CE12_FIELDS, end=None)),
            "a log line": (stamped(CE12_SAMPLE), dict(CE12_FIELDS, end=END)),
            "stuck without a consume": (
                CE12_SAMPLE.replace("shape=pass", "shape=stuck").replace("consume=yes", "consume=no")
                .replace("after=0", "after=1").replace("1/1/1/0", "1/1/1/1"),
                dict(CE12_FIELDS, shape="stuck", consume=False, after=1, reliable=(1, 1, 1, 1), end=None)),
            "stamp on NavMesh": (
                "CE-12 check: cell NE shape=stamp first=1 second=1 next=0 consume=no after=0 reliable=1/1/0/0 "
                "handler=NavMeshEntityMovement",
                {"cell": "NE", "shape": "stamp", "first": 1, "second": 1, "next": 0, "consume": False, "after": 0,
                 "reliable": (1, 1, 0, 0), "handler": "NavMeshEntityMovement", "end": None}),
            "absent": (
                "CE-12 check: cell C2 shape=absent first=0 second=0 next=0 consume=no after=0 reliable=0/0/0/0 "
                "handler=none",
                {"cell": "C2", "shape": "absent", "first": 0, "second": 0, "next": 0, "consume": False, "after": 0,
                 "reliable": (0, 0, 0, 0), "handler": "none", "end": None}),
            "perCall": (CE12_SAMPLE.replace("shape=pass", "shape=perCall").replace("second=1", "second=0")
                        .replace("next=1", "next=0"),
                        dict(CE12_FIELDS, shape="perCall", second=0, next=0, end=None)),
        }
        for name, (line, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0sender.parse_ce12_check(line), expected)


class SenderSettingTests(unittest.TestCase):
    def test_sender_setting(self):
        cases = {
            "a word": ("kit", ("off", "MESH_SENDER 'kit' is not off, measure or on")),
            "a number": ("1", ("off", "MESH_SENDER '1' is not off, measure or on")),
            "a list": ("on,measure", ("off", "MESH_SENDER 'on,measure' is not off, measure or on")),
            "unset": (None, ("off", None)),
            "empty": ("", ("off", None)),
            "blank": ("  ", ("off", None)),
            "off": ("off", ("off", None)),
            "measure": ("measure", ("measure", None)),
            "on": ("on", ("on", None)),
            "case and whitespace": (" MEASURE ", ("measure", None)),
        }
        for name, (text, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0sender.sender_setting(text), expected)

    def test_sender_tiers(self):
        refused = (None, None, None, None)
        shape = p0sender.TIERS_SHAPE
        cases = {
            "two parts": ("20/10", refused + (f"'20/10' is not {shape}",)),
            "five parts": ("20/10/5/20/1", refused + (f"'20/10/5/20/1' is not {shape}",)),
            "a 3 Hz tier": ("20/3/1", refused + ("mid tier '3' is not 0, 1, 2, 5, 10 or 20 Hz",)),
            "a 21 Hz owner": ("20/10/5/21", refused + ("owner tier '21' is not 0, 1, 2, 5, 10 or 20 Hz",)),
            "a negative tier": ("-5/10/5", refused + ("near tier '-5' is not 0, 1, 2, 5, 10 or 20 Hz",)),
            "a fractional tier": ("20/10/2.5", refused + ("far tier '2.5' is not 0, 1, 2, 5, 10 or 20 Hz",)),
            "an empty tier": ("20//5", refused + ("mid tier '' is not 0, 1, 2, 5, 10 or 20 Hz",)),
            "junk": ("fast", refused + (f"'fast' is not {shape}",)),
            "unset": (None, (20, 20, 20, 20, None)),
            "empty": ("", (20, 20, 20, 20, None)),
            "blank": (" ", (20, 20, 20, 20, None)),
            "three tiers": ("20/10/5", (20, 10, 5, 20, None)),
            "four tiers": ("20/10/5/1", (20, 10, 5, 1, None)),
            "zero far": ("20/5/0", (20, 5, 0, 20, None)),
            "whitespace": (" 10 / 5 / 2 / 20 ", (10, 5, 2, 20, None)),
        }
        for name, (text, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0sender.sender_tiers(text), expected)


if __name__ == "__main__":
    unittest.main()
