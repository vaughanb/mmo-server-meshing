"""Unit tests for p0checks8b and p0accept8b (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py"). Rows
and runs are built by hand: only the fields the checks read."""
import datetime
import unittest

import p0accept8b
import p0base
import p0checks8b
import p0plans8b
import p0results

START = datetime.datetime(2026, 10, 7, 10, 0, 0)


def metric(name, value, stat="mean", cell="C"):
    return {"name": name, "stat": stat, "value": value, "unit": "", "label": "M", "cell": cell}


def row(family="calibrate", case="J-lite", arm="kit", point=None, repeat=1, key=None, accepted=True, metrics=(),
        **fields):
    base = {"schema": p0results.SCHEMA, "row": "run", "team": "rig", "game": "fantasy", "family": family,
            "stage": "0b", "build": "b", "cpu": "cpu", "host": "A", "case": case, "arm": arm, "point": point or {},
            "repeat": repeat, "plan_key": key or f"{family}-{case}-{arm}-{point}-{repeat}", "accepted": accepted,
            "metrics": list(metrics)}
    return dict(base, **fields)


def j_rows(work=(100.0, 110.0), rig=5.0, wire=(500.0, 520.0), connected=(50.0, 50.0), v=(49.0, 48.0)):
    bots = row(point={"P": 50, "load": "bots"}, peer={"sink": None, "bots": {"n": 50}}, clients={"connected": connected[0]},
               metrics=[metric("work_ms_per_s", work[0]), metric("link_kBps_out", wire[0]),
                        metric("bot_players_seen", v[0], cell="B"), metric("bot_upload_pkt_per_s", 21.0, "median", "B")])
    phantoms = row(point={"P": 50, "load": "phantoms"}, peer={"sink": {"count": 50}, "bots": None},
                   clients={"connected": connected[1]}, v_move_mean=v[1],
                   metrics=[metric("work_ms_per_s", work[1]), metric("phantom_rig_ms_per_s", rig),
                            metric("link_kBps_out", wire[1]), metric("phantom_uploads_per_s", 1050.0)])
    return bots, phantoms


def run_of(clients=(), handoffs=(), cells=("C",)):
    triples = [{"window": {"clients": n}} for n in clients]
    return {"cells": {cid: {"p0": {"triples": triples}, "p0b": {"handoffs": list(handoffs)}} for cid in cells}}


class CheckTests(unittest.TestCase):
    def test_bots_on_cell(self):
        crowd = {"peer": {"sink": None, "bots": {"n": 50}}}
        cases = {
            "two windows short": (crowd, [50, 47, 49, 50], ["C: 47 of 50 bots on the cell in 2 of 4 windows"]),
            "one window short": (crowd, [50, 47, 50], []),
            "all there (a link peer more)": (crowd, [51, 51, 51], []),
            "no bots": ({"peer": None}, [0, 0], []),
            "crossings counts handoffs": (dict(crowd, family="crossings"), [1, 2, 3], []),
        }
        for name, (extra, clients, expected) in cases.items():
            with self.subTest(name):
                spec = dict({"family": "calibrate", "cells_read": ["C"]}, **extra)
                self.assertEqual(p0checks8b.check_bots_on_cell(spec, run_of(clients)), expected)

    def test_handoffs(self):
        spec = {"family": "crossings", "cells_read": ["C", "E"], "stop_after_outcomes": 40}
        cases = {"no ACK": ([{"kind": "rollback"}], ["no Handoff ACK on C, E in the walk (40 outcomes expected)"]),
                 "an ACK": ([{"kind": "rollback"}, {"kind": "ack"}], []),
                 "not a walk": (None, [])}
        for name, (handoffs, expected) in cases.items():
            with self.subTest(name):
                got_spec = spec if handoffs is not None else dict(spec, stop_after_outcomes=None)
                run = run_of(handoffs=handoffs or [], cells=("C", "E"))
                self.assertEqual(p0checks8b.check_handoffs(got_spec, run), expected)


class GateJTests(unittest.TestCase):
    def test_gate_j(self):
        cases = {   # j_rows arguments -> verdict
            "work 16 % off": ({"work": (100.0, 121.0)}, p0base.FAIL),
            "bytes 6 % off": ({"wire": (500.0, 530.0)}, p0base.FAIL),
            "V 25 % off": ({"v": (40.0, 50.0)}, p0checks8b.NOT_DECIDED),
            "no V": ({"v": (None, 48.0)}, p0base.NO_VERDICT),
            "no clients": ({"connected": (0.0, 50.0)}, p0base.NO_VERDICT),
            "work at 15 % after the rig ms": ({"work": (100.0, 120.0)}, p0base.PASS),
            "pass": ({}, p0base.PASS),
        }
        for name, (arguments, verdict) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0checks8b.gate_j(*j_rows(**arguments))["verdict"], verdict)
        j = p0checks8b.gate_j(*j_rows())
        self.assertEqual(j["work"], (100.0, 105.0, 0.05))
        self.assertEqual((j["bytes"][0], j["bytes"][1]), (10.0, 10.4))
        self.assertEqual(j["uploads"], (21.0, 1050.0))

    def test_pairs_and_gate_rows(self):
        bots, phantoms = j_rows()
        late = dict(phantoms, metrics=[metric("work_ms_per_s", 300.0)])   # a later row of the same key wins
        lone = row(arm="char-sender", point={"P": 50, "load": "bots"})
        self.assertEqual(p0checks8b.j_pairs([bots, phantoms, lone], "calibrate"), [("kit", 50, bots, phantoms)])
        self.assertEqual(p0checks8b.j_pairs([bots, phantoms, late], "calibrate")[0][3], late)
        gates = p0checks8b.gates_8b([bots, phantoms])
        self.assertEqual([(g["name"], g["verdict"], g["case"]) for g in gates], [("Phantom validity (J)", "PASS",
                                                                                 "J-lite")])
        self.assertEqual(p0checks8b.gates_8b([row(family="observed")]), [])


def walk(arm="awake", bots=50, ack_ms=(), repeat=1, **counts):
    handoffs = dict({"ack": len(ack_ms), "rollback": 0, "fail": 0, "ack_ms": list(ack_ms), "wake_ms": None}, **counts)
    return row("crossings", "walk", arm, {"bots": bots}, repeat, handoffs=handoffs)


def pace(arm, repeat, c, e, acks=None):
    window = {"start": "2026-10-07T10:00:00", "end": "2026-10-07T10:01:00"}
    return row("crossings", "pace", arm, {"bots": 20}, repeat, window_local=window,
               handoffs={"window_acks": acks} if acks is not None else None,
               metrics=[metric("work_ms_per_s", c), metric("work_ms_per_s", e, cell="E")])


class CrossingTests(unittest.TestCase):
    def test_crossing_figures_and_gate(self):
        cases = {   # walk rows -> (verdict, p95)
            "p95 over 1 s": ([walk(ack_ms=[200.0] * 18 + [1200.0, 1300.0])], "FAIL", 1200.0),
            "p95 at 1 s": ([walk(ack_ms=[200.0] * 18 + [1000.0, 5000.0])], "PASS", 1000.0),
            "pooled over runs": ([walk(ack_ms=[100.0] * 10), walk(ack_ms=[900.0] * 10, repeat=2)], "PASS", 900.0),
            "only 20-bot and asleep runs": ([walk(bots=20, ack_ms=[100.0]), walk("asleep", ack_ms=[100.0])],
                                            p0base.NO_VERDICT, None),
        }
        for name, (rows, verdict, p95) in cases.items():
            with self.subTest(name):
                gate = p0checks8b.gates_8b(rows)[0]
                self.assertEqual((gate["name"], gate["verdict"], gate["inputs"]["p95_ms"]),
                                 ("Crossings (B10) half A", verdict, p95))
        figures = p0checks8b.crossing_figures([walk(ack_ms=[1, 2, 3], rollback=2, fail=1, wake_ms=1500.0)])
        self.assertEqual(figures, {"n_runs": 1, "ack": 3, "rollback": 2, "fail": 1, "wake_ms": 1500.0, "p50": 2,
                                   "p95": 3, "p99": 3})
        gate = p0checks8b.gates_8b([walk(ack_ms=[100.0], rollback=1)])[0]
        self.assertIn("1 ACK, 1 rollback, 0 failed over 1 run(s)", gate["notes"])

    def test_pace_cost(self):
        rows = [pace("pace-cut", 1, 60.0, 50.0, acks=30), pace("pace-west", 1, 55.0, 45.0),
                pace("pace-cut", 2, 62.0, 52.0, acks=60), pace("pace-west", 2, 55.0, 45.0),
                pace("pace-cut", 3, 70.0, 50.0, acks=0), pace("pace-west", 3, 55.0, 45.0),   # no crossing: skipped
                pace("pace-cut", 4, 70.0, 50.0, acks=30)]                                   # no partner
        cost = p0checks8b.pace_cost(rows)
        self.assertEqual(cost["pairs"], [20.0, 14.0])   # 10 ms/s over 0.5/s, 14 over 1/s
        self.assertEqual((cost["mean"], cost["n"]), (17.0, 2))
        self.assertAlmostEqual(cost["hi"] - cost["mean"], 12.706 * 3.0)
        self.assertIsNone(p0checks8b.pace_cost(rows[:1]))


def cap(case, size, repeat, work, net, datagrams=None):
    metrics = [metric("work_ms_per_s", work), metric("thread_ms_per_s{litenetlib}", net)]
    metrics += [metric("datagrams_per_s", datagrams)] if datagrams else []
    return row("packets", case, "ship-sender-28-tiered", {"P": 100, "packet_bytes": size}, repeat, metrics=metrics)


def probe(p, thread, datagrams, work=400.0):
    return row("packets", "probe", "char-sender-tiered", {"P": p}, metrics=[
        metric("thread_ms_per_s{litenetlib}", thread), metric("datagrams_per_s", datagrams),
        metric("work_ms_per_s", work)])


class PacketTests(unittest.TestCase):
    def test_packet_costs(self):
        rows = [cap("cap-sink", 1020, 1, 220.0, 60.0, 20000.0), cap("cap", 1020, 1, 200.0, 20.0),
                cap("cap-sink", 1020, 2, 240.0, 80.0, 20000.0), cap("cap", 1020, 2, 200.0, 20.0),
                cap("cap-sink", 255, 1, 300.0, 90.0, 40000.0)]   # no virtual partner: left out
        self.assertEqual(p0checks8b.packet_costs(rows), [{"packet_bytes": 1020, "pkt_main_us": 1.5,
                                                          "pkt_net_us": 2.5, "n": 2}])

    def test_net_thread_adds_unnamed(self):
        """Where LiteNetLib's pre-start names do not reach Windows its threads file as unnamed (A, 2026-10-08)."""
        cases = {  # metrics, network-thread ms/s
            "names absent: all in unnamed": ([metric("thread_ms_per_s{litenetlib}", 0.0),
                                              metric("thread_ms_per_s{unnamed}", 279.6)], 279.6),
            "names present: litenetlib plus the unnamed rest": ([metric("thread_ms_per_s{litenetlib}", 270.0),
                                                                 metric("thread_ms_per_s{unnamed}", 8.0)], 278.0),
            "no thread figures": ([metric("work_ms_per_s", 400.0)], None),
        }
        for name, (metrics, want) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0checks8b.net_thread(row("packets", "probe", metrics=metrics)), want)
        rows = [probe(100, 0.0, 10000.0), probe(200, 0.0, 20000.0)]
        rows[1]["metrics"].append(metric("thread_ms_per_s{unnamed}", 950.0))
        self.assertEqual([p["saturated"] for p in p0checks8b.probe_saturation(rows)], [False, True])

    def test_probe_saturation(self):
        rows = [probe(800, 950.0, 70000.0, 960.0), probe(100, 100.0, 10000.0), probe(200, 200.0, 20000.0),
                probe(400, 400.0, 21500.0)]
        got = [(p["P"], p["saturated"], p["near"], p["main_saturated"]) for p in p0checks8b.probe_saturation(rows)]
        self.assertEqual(got, [(100, False, False, False), (200, False, False, False), (400, True, False, False),
                               (800, True, True, True)])
        near = p0checks8b.probe_saturation([probe(100, 1.0, 1000.0), probe(200, 1.0, 1105.0)])
        self.assertEqual([(p["saturated"], p["near"]) for p in near], [(False, False), (False, True)])   # 10.5 %


def record(**windows):
    return dict({"send": [], "phantom": [], "ce12": []}, **windows)


def accept_run(c=None, peer=None):
    return {"cells": {"C": {"p0b": c or record()}}, "peer": peer}


def accept_spec(part, arm="x", repeat=1):
    return {"accept_part": part, "arm": arm, "repeat": repeat}


def send(wire, seconds=5.0, ro=0, u=0):
    return {"seconds": seconds, "wire_bytes": wire, "records": {"ro": ro, "u": u, "other": 0}}


def bot(players=(), ships=(), in_map=True, bad=0):
    return {"net": [{"players_seen": n} for n in players], "ships": [{"seen": n} for n in ships], "in_map": in_map,
            "bad": {"Exception": bad}}


def failures(runs):
    return [text for ok, text in p0accept8b.accept_b_checks(runs) if not ok]


class AcceptBTests(unittest.TestCase):
    def test_failures_first(self):
        sink = {"sink": [{"seconds": 5.0, "in_bytes": 100000}], "bots": {}}
        eyes = record(phantom=[{"eye_seen": 1, "vmove_n": 10, "vmove_mean": 150.0, "vmove_p95": 160}])
        cases = {
            "A: wire 5 % over the sink's in": ([(accept_spec("A", "sink"), None, accept_run(record(send=[send(105000)]),
                                                                                             sink))], 1),
            "B: eye seen, V off, a bot sees eyes": ([(accept_spec("B"), None, accept_run(eyes, {"bots": {
                "meshbot2": bot(players=[250])}}))], 3),
            "C: records 3 % apart, a bot short of players": (
                [(accept_spec("C", "kit"), None, accept_run(record(send=[send(1, ro=0, u=1000)]),
                                                            {"bots": {"meshbot2": bot([50])}})),
                 (accept_spec("C", "sender"), None, accept_run(record(send=[send(1, ro=0, u=1030)]),
                                                               {"bots": {"meshbot2": bot([49])}}))], 2),
            "D: 44 ships, a fault, no map": ([(accept_spec("D"), None, accept_run(peer={"bots": {
                "meshbot2": bot(ships=[50, 44], in_map=False, bad=1)}}))], 2),
            "E: two CE-12 lines": ([(accept_spec("E"), None, accept_run(record(ce12=[
                {"cell": "C", "shape": "pass", "handler": "h"}] * 2)))], 1),
            "F: one pair": ([(accept_spec("F", "meter-off"), row(metrics=[metric("work_ms_per_s", 100.0)]), None),
                             (accept_spec("F", "meter-on"), row(metrics=[metric("work_ms_per_s", 101.0)]), None)], 1),
            "a failed run": ([(accept_spec("B"), None, None)], 3),
        }
        for name, (runs, count) in cases.items():
            with self.subTest(name):
                self.assertEqual(len(failures(runs)), count, failures(runs))

    def test_passing_parts(self):
        sink = {"sink": [{"seconds": 5.0, "in_bytes": 100000}], "bots": {}}
        eyes = record(phantom=[{"eye_seen": 0, "vmove_n": 10, "vmove_mean": 201.0, "vmove_p95": 210}])
        ce12 = record(ce12=[{"cell": "C", "shape": "pass", "handler": "h"}])
        pairs = []
        for pair, (off, on) in enumerate(((100.0, 100.2), (101.0, 101.1), (99.0, 99.3), (100.5, 100.6), (100.0, 100.1)),
                                         1):
            pairs += [(accept_spec("F", "meter-off", pair), row(metrics=[metric("work_ms_per_s", off)]), None),
                      (accept_spec("F", "meter-on", pair), row(metrics=[metric("work_ms_per_s", on)]), None)]
        runs = [(accept_spec("A", "virtual"), None, accept_run(record(send=[send(99000)]))),
                (accept_spec("A", "sink"), None, accept_run(record(send=[send(102000)]), sink)),
                (accept_spec("B"), None, accept_run(eyes, {"bots": {"meshbot2": bot(players=[201])}})),
                (accept_spec("C", "kit"), None, accept_run(record(send=[send(1, u=1000)]), {"bots": {
                    "meshbot2": bot([50]), "meshbot3": bot([51])}})),
                (accept_spec("C", "sender"), None, accept_run(record(send=[send(1, ro=5, u=1010)]), {"bots": {
                    "meshbot2": bot([50]), "meshbot3": bot([50])}})),
                (accept_spec("D"), None, accept_run(peer={"bots": {"meshbot2": bot(ships=[p0plans8b.D_SEEN] * 3)}})),
                (accept_spec("E"), None, accept_run(ce12))] + pairs
        checks = p0accept8b.accept_b_checks(runs)
        self.assertEqual([text for ok, text in checks if not ok], [])
        self.assertEqual({text[0] for _, text in checks}, set("ABCDEF"))
        self.assertEqual(p0accept8b.accept_b_checks([]), [])


if __name__ == "__main__":
    unittest.main()
