"""Unit tests for p0cells0b (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py").

p0stage0b's Prewarm burst, Monsters window and Phantom step parsers are replaced by small fakes with their pinned
returns (step8a_design.md decision 15), so these tests do not depend on that module's grammar.
"""
import contextlib
import datetime
import re
import unittest
from unittest import mock

import p0cells0b
import p0phantom
import p0ships
import rigtools
import test_p0phantom
import test_p0sender
import test_p0ships

START = datetime.datetime(2026, 9, 26, 10, 0, 0)
END = datetime.datetime(2026, 9, 26, 10, 1, 0)


def at(clock):
    return datetime.datetime.fromisoformat(f"2026-09-26 {clock}")


def stamped(clock, message):
    return f" INFO Mesh [2026-09-26 {clock}] - {message}"


def fake_burst(line):
    m = re.search(r"FAKE burst kind (\S+) n (\d+)$", line)
    if not m:
        return None
    spawned = m[1] not in ("range", "group")
    return {"end": rigtools.stamp_datetime(line), "cell": "C", "kind": m[1], "n": int(m[2]), "per_frame": 0,
            "frames": 3, "spawn_ms": 12.5 if spawned else None, "per_puppet_ms": 0.125 if spawned else None,
            "after_frames": 120, "over_budget": 2, "budget_ms": 33.33, "worst_ms": 80.0, "gc0": 1}


def fake_monsters(line):
    m = re.search(r"FAKE monsters placed (\d+) alive (\d+) awake (\d+)$", line)
    if not m:
        return None
    return {"end": rigtools.stamp_datetime(line), "seconds": 5.0, "cell": "C", "placed": int(m[1]),
            "alive": int(m[2]), "awake": int(m[3]), "healed": 0, "respawned": 0, "gc0": 0}


def fake_step(line):
    m = re.search(r"FAKE step (\d+)/(\d+): P (\d+) -> (\d+)$", line)
    if not m:
        return None
    i, n, a, b = (int(part) for part in m.groups())
    return {"end": rigtools.stamp_datetime(line), "i": i, "n": n, "p": b, "a": a, "b": b}


@contextlib.contextmanager
def fake_stage0b():
    with mock.patch.object(p0cells0b.p0stage0b, "parse_prewarm_burst", fake_burst), \
            mock.patch.object(p0cells0b.p0stage0b, "parse_monsters_window", fake_monsters), \
            mock.patch.object(p0cells0b.p0stage0b, "parse_phantom_step", fake_step):
        yield


def step_line(clock, i, n, a, b):
    return stamped(clock, f"FAKE step {i}/{n}: P {a} -> {b}")


def cell_lines():
    lines = [step_line("09:59:50", 1, 2, 0, 50), step_line("10:00:20", 2, 2, 50, 100)]
    for clock in ("10:00:05", "10:00:10"):
        lines += [stamped(clock, test_p0phantom.SEND_RECORDS), stamped(clock, test_p0phantom.PHANTOM_SAMPLE)]
    lines += [stamped("10:00:05", test_p0sender.SENDER_SAMPLE), stamped("10:00:05", test_p0ships.SHIPS_SAMPLE),
              stamped("10:00:05", test_p0ships.FEEDER_SAMPLE), stamped("10:00:05", test_p0ships.SYNC_SAMPLE),
              stamped("10:00:05", "FAKE monsters placed 200 alive 200 awake 190"),
              stamped("10:00:32", "FAKE burst kind kinematic n 100"),
              stamped("10:01:30", "FAKE burst kind kinematic n 300"),
              stamped("10:01:30", test_p0phantom.SEND_RECORDS), stamped("09:59:58", test_p0phantom.PHANTOM_SAMPLE)]
    return lines


def record(lines=None):
    with fake_stage0b():
        return p0cells0b.cell_record_0b("C", cell_lines() if lines is None else lines, START, END)


def by_name(metrics):
    return {(m["name"], m["stat"]): m["value"] for m in metrics}


class RecordTests(unittest.TestCase):
    def test_refuses_an_unstamped_window(self):
        with self.assertRaisesRegex(ValueError, "a 5 s window has no stamp"):
            record([test_p0phantom.SEND_SAMPLE])

    def test_cell_record_0b(self):
        rec = record()
        self.assertEqual(rec["cell"], "C")
        cases = {"send": 2, "phantom": 2, "sender": 1, "ships": 1, "feeder": 1, "sync_load": 1, "monsters": 1,
                 "prewarm": 1, "steps": 2}
        for name, count in cases.items():
            with self.subTest(name):
                self.assertEqual(len(rec[name]), count)
        self.assertEqual([w["end"] for w in rec["send"]], [at("10:00:05"), at("10:00:10")])
        self.assertEqual(rec["prewarm"][0]["n"], 100)
        self.assertEqual([step["end"] for step in rec["steps"]], [at("09:59:50"), at("10:00:20")])

    def test_an_empty_record(self):
        rec = record([])
        self.assertEqual(p0cells0b.cell_metrics_0b(rec), [])
        self.assertEqual((p0cells0b.clients(rec), p0cells0b.v_move(rec)), (None, (None, None)))


class MetricTests(unittest.TestCase):
    def test_cell_metrics_0b(self):
        metrics = p0cells0b.cell_metrics_0b(record())
        self.assertEqual({m["cell"] for m in metrics}, {"C"})
        self.assertEqual({m["label"] for m in metrics}, {"M"})
        got = by_name(metrics)
        cases = {
            ("datagrams_per_s", "mean"): 260.0,                       # 2 x 1300 pkt over 10 s
            ("link_kBps_out", "mean"): 2 * 530000 / 1024 / 10,
            ("datagrams_per_s_per_client", "mean"): 1.3,              # over the 200 phantoms bound
            ("entity_state_kBps_per_client", "mean"): 0.49,           # 2 x 501760 B of t119 / 1024 / 10 s / 200
            ("reliable_records_per_s", "mean"): 60.0,                 # records ro 2 x 300 over 10 s
            ("downlink_wire_kBps_per_client", "mean"): 55.3, ("downlink_wire_kBps_per_client", "p95"): 56.0,
            ("rate_hz_by_tier{20}", "mean"): 5000.0, ("rate_hz_by_tier{10}", "mean"): 0.0,
            ("ship_count{ai}", "mean"): 50.0, ("ship_count{phantom}", "mean"): 0.0,
            ("ship_ms_per_s{pilot}", "mean"): 0.4, ("ship_ms_per_s{bolts}", "mean"): 0.9,
            ("ship_uploads_per_s", "mean"): 0.0, ("ship_speed_m_per_s", "max"): 3.76,
            ("feeder_ms_per_s{total}", "mean"): 2.4, ("feeder_us_per_snapshot", "mean"): 0.12,
            ("feeder_snapshots_per_s", "mean"): 4000.0,
            ("sync_load_ms_per_s{rpc}", "mean"): 0.9, ("rpc_send_ns", "mean"): 4500.0,
            ("monster_count{awake}", "mean"): 190.0, ("monster_count{placed}", "mean"): 200.0,
            ("prewarm_frames", "total"): 3, ("prewarm_spawn_ms", "total"): 12.5,
            ("prewarm_per_puppet_ms", "mean"): 0.125, ("prewarm_over_budget_frames", "total"): 2,
            ("prewarm_worst_frame_ms", "max"): 80.0,
        }
        for key, value in cases.items():
            with self.subTest(key):
                self.assertAlmostEqual(got[key], value)
        self.assertNotIn(("upload_in_us", "mean"), got)

    def test_a_range_burst_has_no_spawn_figures(self):
        with fake_stage0b():
            rec = p0cells0b.cell_record_0b("C", [stamped("10:00:32", "FAKE burst kind range n 160")], START, END)
        got = by_name(p0cells0b.cell_metrics_0b(rec))
        self.assertNotIn(("prewarm_spawn_ms", "total"), got)
        self.assertEqual(got[("prewarm_frames", "total")], 3)

    def test_upload_in_us_needs_mirror_ships_only(self):
        mirror = test_p0ships.SHIPS_SAMPLE.replace("ai 50 mirror 0", "ai 0 mirror 100").replace(
            "uploads 0", "uploads 1990").replace("stale 0 bad 0", "stale 10 bad 0").replace("mirror 0.00", "mirror 1.00")
        cases = {"phantom ships too": (mirror.replace("phantom 0", "phantom 20"), None),
                 "no mirror ships": (test_p0ships.SHIPS_SAMPLE, None),
                 "mirror ships only": (mirror, 2.5)}   # 1.00 ms/s x 5 s over 2000 uploads
        for name, (line, expected) in cases.items():
            with self.subTest(name):
                self.assertIsNotNone(p0ships.parse_ships_window(line))
                metrics = p0cells0b.cell_metrics_0b(record([stamped("10:00:05", line)]))
                found = [m for m in metrics if m["name"] == "upload_in_us"]
                self.assertEqual([m["value"] for m in found], [] if expected is None else [expected])
                self.assertEqual({m["label"] for m in found}, set() if expected is None else {"E"})

    def test_a_window_without_t119_counts_no_entity_state(self):
        send = p0phantom.parse_send_window(stamped("10:00:05", test_p0phantom.SEND_SAMPLE))
        self.assertEqual((p0cells0b.type_bytes(send, 119), p0cells0b.type_bytes(send, 7)), (501760, None))
        self.assertEqual((p0cells0b.type_calls_per_s(send, 2), p0cells0b.type_calls_per_s(send, 3)), (50.0, None))


class LoadFieldTests(unittest.TestCase):
    def test_load_fields_0b(self):
        cases = {
            "no 0b lines and no spec fields": (
                {"env": {}}, {"C": record([])},
                {"kind": "character", "clients": {"real": 0, "phantom_virtual": 0, "phantom_sink": 0,
                                                  "observer_kind": None, "connected": None},
                 "v_move_mean": None, "v_move_p95": None, "payload_B": None, "scale": 1.0, "awake_monsters": None}),
            "eyes, ships and monsters": (
                {"kind": "ship", "payload_B": 56, "scale": 0.5, "env": {"MESH_PHANTOM": "virtual:200:eye:C"}},
                {"C": record(), "E": record([])},
                {"kind": "ship", "clients": {"real": 0, "phantom_virtual": 200.0, "phantom_sink": 0,
                                             "observer_kind": "eye", "connected": None},
                 "v_move_mean": 100.0, "v_move_p95": 100, "payload_B": 56, "scale": 0.5, "awake_monsters": 190.0}),
        }
        for name, (spec, records, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0cells0b.load_fields_0b(spec, records), expected)

    def test_real_clients_come_from_the_work_windows(self):
        sink = {"phantom": [dict(p0phantom.parse_phantom_window(test_p0phantom.PHANTOM_SAMPLE), bound_virtual=0,
                                 bound_sink=20)]}
        cases = {   # (0b records, 0a records' Work window clients) -> (real, connected)
            "no Work windows": ({"C": record([])}, {"C": {"ticks": None}}, (0, None)),
            "50 bots on C": ({"C": record([])}, {"C": {"ticks": {"clients": 50.0}}}, (50.0, 50.0)),
            "bots over two cells": ({"C": record([])}, {"C": {"ticks": {"clients": 30.0}},
                                                        "E": {"ticks": {"clients": 20.0}}}, (50.0, 50.0)),
            "sink peers are not real": ({"C": dict(record([]), **sink)}, {"C": {"ticks": {"clients": 20.0}}},
                                        (0.0, 20.0)),
        }
        for name, (records, records_0a, (real, connected)) in cases.items():
            with self.subTest(name):
                clients = p0cells0b.load_fields_0b({"env": {}}, records, records_0a)["clients"]
                self.assertEqual((clients["real"], clients["connected"]), (real, connected))


class StepWindowTests(unittest.TestCase):
    def test_step_windows_refuse(self):
        first = fake_step(step_line("09:59:50", 1, 2, 0, 50))
        cases = {
            "a step without its line": ([first], "step 2/2 (P 100) logged no Phantom step line"),
            "another P": ([first, fake_step(step_line("10:00:30", 2, 2, 50, 200))],
                          "step 2 logged 'Phantom step 2/2: P 50 -> 200', planned 2/2 P 100"),
            "another step count": ([first, fake_step(step_line("10:00:30", 2, 3, 50, 100))],
                                   "step 2 logged 'Phantom step 2/3: P 50 -> 100', planned 2/2 P 100"),
            "no time inside the measurement": ([first, fake_step(step_line("10:00:55", 2, 2, 50, 100))],
                                               "step 2/2 (P 100) has no time inside the measurement (it starts "
                                               "10:00:55, ramps for 1 s and settles for 10 s)"),
        }
        for name, (steps, problem) in cases.items():
            with self.subTest(name):
                windows = p0cells0b.step_windows([50, 100], steps, START, END)
                self.assertEqual(windows[1], {"index": 2, "step": 100, "start": None, "end": None, "problem": problem})
                self.assertIsNone(windows[0]["problem"])

    def test_step_windows(self):
        steps = [fake_step(step_line(clock, i, 3, a, b))
                 for clock, i, a, b in (("09:59:40", 1, 0, 400), ("10:00:15", 2, 400, 200), ("10:00:35", 3, 200, 100))]
        self.assertEqual([(w["step"], w["start"], w["end"], w["problem"])
                          for w in p0cells0b.step_windows([400, 200, 100], steps, START, END)],
                         [(400, START, at("10:00:14"), None), (200, at("10:00:25"), at("10:00:34"), None),
                          (100, at("10:00:45"), END, None)])

    def test_step_windows_wait_for_the_ramp(self):
        # A reversed saturation repeat: the first step binds 0 -> 800 at 50/s (16 s), so its window opens 26 s after the
        # line; a step that only unbinds has no ramp.
        steps = [fake_step(step_line(clock, i, 2, a, b))
                 for clock, i, a, b in (("10:00:00", 1, 0, 800), ("10:00:40", 2, 800, 400))]
        self.assertEqual([(w["step"], w["start"], w["end"], w["problem"])
                          for w in p0cells0b.step_windows([800, 400], steps, START, END)],
                         [(800, at("10:00:26"), at("10:00:39"), None), (400, at("10:00:50"), END, None)])

    def test_phantom_steps_need_a_stamp(self):
        with fake_stage0b():
            self.assertEqual(p0cells0b.phantom_steps(["FAKE step 1/2: P 0 -> 50", step_line("10:00:00", 1, 2, 0, 50)]),
                             [fake_step(step_line("10:00:00", 1, 2, 0, 50))])


class SpawnRiseTests(unittest.TestCase):
    def test_spawn_rise(self):
        def send(clock, calls_per_s):
            types = [] if calls_per_s is None else [{"type": 3, "calls_per_s": calls_per_s}]
            return {"end": at(clock), "seconds": 5.0, "types": types}
        windows = [send("10:00:25", 2.0), send("10:00:30", 5.0), send("10:00:35", 400.0), send("10:00:40", 900.0)]
        cases = {
            "no window before": (windows[2:], (None, 400.0)),
            "no window after": (windows[:2], (5.0, None)),
            "spawns unlisted after": (windows[:2] + [send("10:00:35", None)], (5.0, None)),
            "a rise": (windows, (5.0, 400.0)),
        }
        for name, (sends, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0cells0b.spawn_rise(sends, at("10:00:32")), expected)


class NetSaturationTests(unittest.TestCase):
    def test_net_saturated(self):
        cases = {
            "nothing to judge": ([(100, None, 1000.0)], [None]),
            "a datagram count missing": ([(100, None, 1000.0), (200, None, None)], [None, None]),
            "the thread at 900 ms/s": ([(100, 900.0, 1000.0)], [True]),
            "under 10 % for a doubling": ([(100, 500.0, 1000.0), (200, 500.0, 1090.0)], [False, True]),
            "10 % for a doubling": ([(100, 500.0, 1000.0), (200, 500.0, 1100.0)], [False, False]),
            "a descending run": ([(200, None, 1090.0), (100, None, 1000.0)], [True, None]),
        }
        for name, (points, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0cells0b.net_saturated(points), expected)


def ack(clock, name="meshbot2Char", total=412, zone="E"):
    return stamped(clock, f"Handoff ACK {name} by zone {zone}: registered 120 ms after the notice, {total} ms after "
                          f"START (promoted=True)")


HANDOFF_LINES = [
    ack("10:00:05", total=300), "Handoff ACK meshbot9Char by zone E: registered 1 ms after the notice, 2 ms after START "
    "(promoted=True)",   # no stamp: not an outcome
    stamped("10:00:06", "Handoff ROLLBACK meshbot3Char -> zone E: no ack in 3s; the puppet is held 20s for the "
                        "client's return"),
    stamped("10:00:07", "Handoff ROLLBACK complete: meshbot3Char is back on C (its puppet promoted)"),
    stamped("10:00:07", "Handoff ROLLBACK of 77 expired: its client never returned to C; a later login spawns it"),
    stamped("10:00:08", "Handoff ACK for 77 by zone E after a rollback (promoted=True): committed after all"),
    stamped("10:00:09", "Handoff FAILED meshbot4Char -> zone E: no reservation; releasing the demoted puppet here"),
    stamped("10:00:10", "Handoff ABORTED meshbot5Char -> zone E: saved=False; character stays on C"),
    stamped("10:00:11", "Wake requested for sleeping cell E (ServerMeshTestMap)"),
    ack("10:00:13", name="meshbot6Char", total=2500.5),
]


class HandoffTests(unittest.TestCase):
    def test_handoff_outcomes(self):
        got = [(h["kind"], h["ms"], h["at"], h["name"], h["zone"]) for h in p0cells0b.handoff_outcomes(HANDOFF_LINES)]
        self.assertEqual(got, [("ack", 300.0, at("10:00:05"), "meshbot2Char", "E"),
                               ("rollback", None, at("10:00:06"), "meshbot3Char", "E"),
                               ("fail", None, at("10:00:09"), "meshbot4Char", "E"),
                               ("fail", None, at("10:00:10"), "meshbot5Char", "E"),
                               ("ack", 2500.5, at("10:00:13"), "meshbot6Char", "E")])
        self.assertEqual(p0cells0b.wake_stamps(HANDOFF_LINES), [{"at": at("10:00:11"), "cell": "E"}])
        self.assertEqual(p0cells0b.handoff_outcomes(["Wake requested for sleeping cell E (x)"]), [])

    def test_percentile(self):
        cases = {"none": ([], 0.95, None), "one": ([7.0], 0.5, 7.0), "p50 of four": ([4, 1, 3, 2], 0.5, 2),
                 "p95 of twenty": (list(range(1, 21)), 0.95, 19), "max": ([3, 9, 1], 1.0, 9)}
        for name, (values, share, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0cells0b.percentile(values, share), expected)

    def test_record_and_run_figures(self):
        with fake_stage0b():
            c = p0cells0b.cell_record_0b("C", HANDOFF_LINES, at("10:00:06"), at("10:00:12"))
        self.assertEqual([(h["kind"], h["in_window"]) for h in c["handoffs"]],
                         [("ack", False), ("rollback", True), ("fail", True), ("fail", True)])   # 10:00:13 is after end
        self.assertEqual((len(c["wakes"]), c["ce12"]), (1, []))
        e = dict(c, handoffs=[dict(h, in_window=True) for h in p0cells0b.handoff_outcomes([ack("10:00:13")])],
                 wakes=[])
        spec = {"family": "crossings", "cells_read": ["C", "E"], "peer": {"sink": None, "bots": {"n": 2}}}
        metrics = {(m["name"], m["stat"]): (m["value"], m["cell"])
                   for m in p0cells0b.run_metrics_0b(spec, {"C": c, "E": e}, None, 10.0)}
        self.assertEqual(metrics[("latency_ms{handoff_ready}", "p50")], (300.0, "C+E"))
        self.assertEqual(metrics[("latency_ms{handoff_ready}", "max")][0], 412.0)
        self.assertEqual(metrics[("latency_ms{handoff_ready}", "n")][0], 2)
        self.assertEqual(metrics[("handoff_outcomes{fail}", "total")][0], 2)
        self.assertEqual(metrics[("crossings_per_s", "mean")][0], 0.1)   # one ACK inside 10 s
        self.assertEqual(metrics[("latency_ms{wake}", "first")][0], 2000.0)   # 10:00:11 -> the ACK at 10:00:13
        fields = p0cells0b.run_fields_0b(spec, {"C": c, "E": e}, {"clock_offset_s": 0.25})
        self.assertEqual(fields, {"peer": spec["peer"], "clock_offset_s": 0.25,
                                  "handoffs": {"ack": 2, "rollback": 1, "fail": 2, "ack_ms": [300.0, 412.0],
                                               "wake_ms": 2000.0, "window_acks": 1}})
        self.assertEqual(p0cells0b.run_fields_0b(dict(spec, peer=None), {"C": c}, {}), {})
        for name, _ in metrics:
            self.assertIn(name.split("{")[0], p0cells0b.METRIC_NAMES_0B | {"latency_ms"})


class PeerTests(unittest.TestCase):
    def test_shift_b_windows(self):
        start, end = at("10:00:00"), at("10:01:00")

        def window(clock):
            return {"end": at(clock), "seconds": 5.0}
        cases = {   # B's stamp, offset (B minus A) -> kept on A's clock?
            "inside": ("10:00:30", 0.0, at("10:00:30")),
            "B ahead by 3 s": ("10:00:33", 3.0, at("10:00:30")),
            "B behind by 1.5 s": ("10:00:28.5", -1.5, at("10:00:30")),
            "begins within 2 s of start": ("10:00:06", 0.0, None),
            "begins 2 s after start": ("10:00:07", 0.0, at("10:00:07")),
            "ends within 2 s of end": ("10:00:59", 0.0, None),
            "ends 2 s before end": ("10:00:58", 0.0, at("10:00:58")),
            "outside after the shift": ("10:01:05", 10.0, at("10:00:55")),
        }
        for name, (clock, offset, expected) in cases.items():
            with self.subTest(name):
                got = p0cells0b.shift_b_windows([window(clock)], offset, start, end)
                self.assertEqual([w["end"] for w in got], [] if expected is None else [expected])
        self.assertEqual(p0cells0b.shift_b_windows([{"end": None, "seconds": 5.0}], 0.0, start, end), [])

    def test_peer_record_and_metrics(self):
        utc = test_p0phantom.BOT_FIELDS["at"]
        local = utc.astimezone().replace(tzinfo=None)
        start, end = local - datetime.timedelta(seconds=30), local + datetime.timedelta(seconds=30)
        sink_line = f" INFO Sink [{local:%Y-%m-%d %H:%M:%S}] - {test_p0phantom.SINK_SAMPLE}"
        bot = ["In map at (0.0, 0.0, 0.0)", test_p0phantom.BOT_SAMPLE, "Exception in something"]
        peer = p0cells0b.peer_record([sink_line], {"meshbot2": bot, "meshbot3": [test_p0phantom.BOT_SAMPLE]}, 0.0,
                                     start, end)
        self.assertEqual([w["end"] for w in peer["sink"]], [local])
        self.assertEqual((peer["bots"]["meshbot2"]["in_map"], peer["bots"]["meshbot3"]["in_map"]), (True, False))
        self.assertEqual(peer["bots"]["meshbot2"]["bad"]["Exception"], 1)
        self.assertEqual([w["end"] for w in peer["bots"]["meshbot2"]["net"]], [local])
        metrics = {(m["name"], m["stat"]): (m["value"], m["cell"])
                   for m in p0cells0b.run_metrics_0b({"cells_read": ["C"]}, {}, peer, 60.0)}
        self.assertEqual(metrics, {("bot_players_seen", "mean"): (200.0, "B"), ("bot_in_kBps", "mean"): (52.3, "B"),
                                   ("bot_upload_pkt_per_s", "median"): (22.0, "B"), ("bots_in_map", "total"): (1, "B"),
                                   ("sink_in_kBps", "mean"): (512.0, "B"), ("sink_in_pkt_per_s", "mean"): (1200.0, "B")})
        late = p0cells0b.peer_record([sink_line], {}, 0.0, start, local + datetime.timedelta(seconds=1))
        self.assertEqual(late["sink"], [])   # within 2 s of the end


if __name__ == "__main__":
    unittest.main()
