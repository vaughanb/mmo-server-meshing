"""Unit tests for p0cells0c (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py"): step 9's log-line
parsers (the contract's "Log lines", exact), its key meanings, the cell and saves records and the metrics and fields a
row carries. Nothing here touches the editor: editor_lines reads a temporary file."""
import datetime
import os
import tempfile
import unittest

import p0cells0c
import regress

START = datetime.datetime(2026, 10, 9, 10, 0, 0)
END = datetime.datetime(2026, 10, 9, 10, 2, 0)


def at(clock):
    return datetime.datetime.fromisoformat(f"2026-10-09 {clock}")


def stamped(clock, message, tag="Mesh"):
    return f" INFO {tag} [2026-10-09 {clock}] - {message}"


class ParserTests(unittest.TestCase):
    """Each table lists the refused lines first, then the lines read."""

    def check(self, parse, cases):
        for name, (line, expected) in cases.items():
            with self.subTest(parse.__name__, case=name):
                self.assertEqual(parse(line), expected)

    def test_db_arrival(self):
        self.check(p0cells0c.parse_db_arrival, {
            "no ok": ("DB burst 3: save 1 token 2 get 3 bg 4 ready 5 ms", None),
            "a fraction": ("DB burst 3: save 1.5 token 2 get 3 bg 4 ready 5 ms ok", None),
            "text after ok": ("DB burst 3: save 1 token 2 get 3 bg 4 ready 5 ms ok!", None),
            "failed without after": ("DB burst 3: failed token", None),
            "the window line": ("DB burst window 5.00 s: started 1 ready 1 failed 0 | ready p50 1 p95 1 max 1 ms", None),
            "ok": (stamped("10:00:05", "DB burst 3: save 12 token 20 get 31 bg 44 ready 45 ms ok"),
                   {"end": at("10:00:05"), "i": 3, "ok": True, "save_ms": 12, "token_ms": 20, "get_ms": 31,
                    "bg_ms": 44, "ready_ms": 45}),
            "failed": ("DB burst 7: failed token after 900 ms",
                       {"end": None, "i": 7, "ok": False, "step": "token", "after_ms": 900, "ready_ms": None}),
        })

    def test_db_burst_window(self):
        self.check(p0cells0c.parse_db_burst_window, {
            "no seconds": ("DB burst window s: started 1 ready 1 failed 0 | ready p50 1 p95 2 max 3 ms", None),
            "no bar": ("DB burst window 5.00 s: started 1 ready 1 failed 0 ready p50 1 p95 2 max 3 ms", None),
            "read": (stamped("10:00:10", "DB burst window 5.00 s: started 50 ready 48 failed 2 | ready p50 120.5 "
                             "p95 400 max 812 ms"),
                     {"end": at("10:00:10"), "seconds": 5.0, "started": 50, "ready": 48, "failed": 2, "p50": 120.5,
                      "p95": 400.0, "max": 812.0}),
            "nothing ready": ("DB burst window 5.00 s: started 0 ready 0 failed 0 | ready p50 n/a p95 n/a max n/a ms",
                              {"end": None, "seconds": 5.0, "started": 0, "ready": 0, "failed": 0, "p50": None,
                               "p95": None, "max": None}),
        })

    def test_db_burst_start_and_done(self):
        self.check(p0cells0c.parse_db_burst_start, {
            "no s": ("DB burst: 50 arrivals at once at 55", None),
            "no from": ("DB burst: 5/s for 40 s at 55 s", None),
            "once": ("DB burst: 50 arrivals at once at 55 s",
                     {"end": None, "mode": "once", "n": 50, "rate": None, "seconds": 0.0, "at_s": 55.0}),
            "rate": ("DB burst: 7.5/s for 40 s from 55 s",
                     {"end": None, "mode": "rate", "n": None, "rate": 7.5, "seconds": 40.0, "at_s": 55.0}),
        })
        self.check(p0cells0c.parse_db_burst_done, {
            "no comma": ("DB burst: done 49 ready 1 failed", None),
            "read": (stamped("10:01:00", "DB burst: done, 49 ready 1 failed"),
                     {"end": at("10:01:00"), "ready": 49, "failed": 1}),
        })

    def test_db_saves_window(self):
        self.check(p0cells0c.parse_db_saves_window, {
            "no ceiling": ("DB saves window 5.00 s: pending 3 enqueued 10 written 7 failed 0 | db fps 60.0", None),
            "a wrong ceiling unit": ("DB saves window 5.00 s: pending 3 enqueued 10 written 7 failed 0 | db fps 60 "
                                     "| ceiling 16 /s", None),
            "read": (stamped("10:00:15", "DB saves window 5.00 s: pending 30 enqueued 50 written 20 failed 1 | db fps "
                                         "58.2 | ceiling 16/s"),
                     {"end": at("10:00:15"), "seconds": 5.0, "pending": 30, "enqueued": 50, "written": 20, "failed": 1,
                      "db_fps": 58.2, "ceiling": 16}),
            "written below 0": ("DB saves window 5.00 s: pending 9 enqueued 0 written -4 failed 0 | db fps 60 | "
                                "ceiling 16/s",
                                {"end": None, "seconds": 5.0, "pending": 9, "enqueued": 0, "written": -4, "failed": 0,
                                 "db_fps": 60.0, "ceiling": 16}),
        })

    def test_monster_cell(self):
        self.check(p0cells0c.parse_monster_cell, {
            "no key": ("MESH_MONSTER_CELL: cell E owns every monster spawn area", None),
            "some areas": ("MESH_MONSTER_CELL=E: cell E owns some monster spawn areas", None),
            "owner": ("MESH_MONSTER_CELL=E: cell E owns every monster spawn area",
                      {"end": None, "key": "E", "owner": "E"}),
            "none": (stamped("10:00:01", "MESH_MONSTER_CELL=E: this cell owns no monster spawn area"),
                     {"end": at("10:00:01"), "key": "E", "owner": None}),
        })

    def test_forwarded_window(self):
        self.check(p0cells0c.parse_forwarded_window, {
            "no bar": ("Forwarded hits window 5.00 s: sent 4 results 4 p50 1 p95 2 max 3 ms", None),
            "read": ("Forwarded hits window 5.00 s: sent 40 results 38 | p50 3.5 p95 9 max 14.25 ms",
                     {"end": None, "seconds": 5.0, "sent": 40, "results": 38, "p50": 3.5, "p95": 9.0, "max": 14.25}),
            "no result": ("Forwarded hits window 5.00 s: sent 0 results 0 | p50 n/a p95 n/a max n/a ms",
                          {"end": None, "seconds": 5.0, "sent": 0, "results": 0, "p50": None, "p95": None,
                           "max": None}),
        })

    def test_spaces_lines(self):
        self.check(p0cells0c.parse_spaces_setting, {
            "another layout": ("Spaces: S 4 layout ring pitch 0 m | per space observers 1 content 5", None),
            "stack": ("Spaces: S 64 layout stack pitch 0 m | per space observers 1.56 content 5",
                      {"end": None, "s": 64, "layout": "stack", "pitch_m": 0.0, "observers": 1.56, "content": 5}),
            "grid": ("Spaces: S 16 layout grid pitch 150 m | per space observers 6.25 content 5",
                     {"end": None, "s": 16, "layout": "grid", "pitch_m": 150.0, "observers": 6.25, "content": 5}),
        })
        self.check(p0cells0c.parse_spaces_window, {
            "a fractional other": ("Spaces window 5.00 s: observers 4 | own seen mean 5 | other seen max 0.5", None),
            "read": ("Spaces window 5.00 s: observers 64 | own seen mean 5.0 | other seen max 0",
                     {"end": None, "seconds": 5.0, "observers": 64, "own_seen": 5.0, "other_seen": 0}),
        })

    def test_net_stats_and_enter_game(self):
        self.check(p0cells0c.parse_net_stats_window, {
            "no pkt": ("Net stats window 5.00 s: sent 10 received 9 resent 1 | loss share 0.1", None),
            "read": ("Net stats window 5.00 s: sent 1000 pkt received 990 pkt resent 12 | loss share 0.012",
                     {"end": None, "seconds": 5.0, "sent": 1000, "received": 990, "resent": 12,
                      "loss_share": 0.012}),
        })
        self.check(p0cells0c.parse_enter_game, {
            "the request": ("10:00:01.000 S enter-game request from 5", None),
            "ok": (stamped("10:00:02", "10:00:02.120 S enter-game validated+loaded ok=True in 187ms", "MeshTrace"),
                   {"end": at("10:00:02"), "ok": True, "ms": 187}),
            "refused": ("enter-game validated+loaded ok=False in 9ms", {"end": None, "ok": False, "ms": 9}),
        })


class SettingTests(unittest.TestCase):
    def test_db_burst_setting(self):
        cases = {   # refusals first
            "both forms": ("50@55+40", "is not"), "a rate without a span": ("5/s@55", "is not"),
            "a negative time": ("50@-1", "is not"), "zero arrivals": ("0@55", "below 1"),
            "a zero rate": ("0/s@55+40", "above 0"), "words": ("fifty@55", "is not"),
            "unset": (None, None), "blank": ("  ", None),
            "at once": ("50@55", ("once", 50, 55.0, 0.0)), "spaced": (" 200@55.5 ", ("once", 200, 55.5, 0.0)),
            "sustained": ("15/s@55+40", ("rate", 15.0, 55.0, 40.0)),
        }
        for name, (text, expected) in cases.items():
            with self.subTest(name):
                setting, error = p0cells0c.db_burst_setting(text)
                if isinstance(expected, str):
                    self.assertIsNone(setting)
                    self.assertIn(expected, error)
                else:
                    self.assertEqual((setting, error), (expected, None))

    def test_expected_arrivals(self):
        self.assertEqual(p0cells0c.expected_arrivals(("once", 200, 55.0, 0.0)), 200)
        self.assertEqual(p0cells0c.expected_arrivals(("rate", 15.0, 55.0, 40.0)), 600)

    def test_registry_keys_are_regress_keys(self):
        self.assertEqual(set(p0cells0c.STEP9_REGISTRY), {
            "MESH_DB_BURST", "MESH_DB_SAVES_PROBE", "MESH_MONSTER_CELL", "MESH_FIGHT_GHOSTS", "MESH_FORWARD_TIMING",
            "MESH_SPACES", "MESH_NET_STATS", "MESH_MONSTER_AWAKE"})
        for key, meaning in p0cells0c.STEP9_REGISTRY.items():
            with self.subTest(key):
                self.assertIn(key, regress.ENV_KEYS)
                self.assertNotIn("\n", meaning)

    def test_percentile_and_slope(self):
        self.assertIsNone(p0cells0c.percentile([], 0.95))
        self.assertIsNone(p0cells0c.slope([(1.0, 2.0)]))
        self.assertIsNone(p0cells0c.slope([(1.0, 2.0), (1.0, 3.0)]))
        self.assertEqual(p0cells0c.percentile(list(range(1, 101)), 0.95), 95)
        self.assertEqual(p0cells0c.percentile([3, 1, 2], 1.0), 3)
        self.assertAlmostEqual(p0cells0c.slope([(0.0, 1.0), (5.0, 2.0), (10.0, 3.0)]), 0.2)


def burst_lines(failed=False):
    """A 5/s for 4 s burst on C: start at 10:00:10, 20 arrivals (one failed when failed), done at 10:00:15."""
    lines = [stamped("10:00:10", "DB burst: 5/s for 4 s from 55 s")]
    for i in range(1, 21):
        lines.append(stamped("10:00:12", f"DB burst {i}: failed token after 300 ms") if failed and i == 20 else
                     stamped("10:00:12", f"DB burst {i}: save 10 token 20 get {30 + i} bg 40 ready {50 + i} ms ok"))
    lines += [stamped("10:00:15", f"DB burst: done, {19 if failed else 20} ready {1 if failed else 0} failed"),
              stamped("10:00:15", "DB burst window 5.00 s: started 20 ready 20 failed 0 | ready p50 60 p95 69 max 70 "
                                  "ms"),
              stamped("10:00:15", "Forwarded hits window 5.00 s: sent 10 results 10 | p50 4 p95 8 max 9 ms"),
              stamped("10:00:20", "Forwarded hits window 5.00 s: sent 30 results 30 | p50 8 p95 12 max 20 ms"),
              stamped("10:00:20", "Spaces window 5.00 s: observers 4 | own seen mean 5 | other seen max 0"),
              stamped("10:00:20", "Net stats window 5.00 s: sent 100 pkt received 90 pkt resent 5 | loss share 0.05"),
              stamped("10:00:20", "MESH_MONSTER_CELL=E: this cell owns no monster spawn area"),
              stamped("09:59:00", "Net stats window 5.00 s: sent 1 pkt received 1 pkt resent 1 | loss share 1"),
              stamped("10:03:00", "DB burst 99: save 1 token 2 get 3 bg 4 ready 5 ms ok")]
    return lines


SAVES = [stamped(clock, f"DB saves window 5.00 s: pending {pending} enqueued 10 written {written} failed {failed} | "
                        f"db fps 60 | ceiling 16/s")
         for clock, pending, written, failed in (("10:00:05", 0, 0, 0), ("10:00:10", 4, 6, 0), ("10:00:15", 8, 6, 1),
                                                 ("10:00:20", 0, 8, 0), ("10:03:00", 0, 0, 0))]


class RecordTests(unittest.TestCase):
    def record(self, failed=False):
        return {"C": p0cells0c.cell_record_0c("C", burst_lines(failed), START, END)}

    def test_cell_record_selects_the_measurement(self):
        record = self.record()["C"]
        self.assertEqual((len(record["arrivals"]), len(record["starts"]), len(record["done"])), (20, 1, 1))
        self.assertEqual((len(record["forwarded"]), len(record["spaces"]), len(record["net"])), (2, 1, 1))
        self.assertEqual(record["monster_cell"][0]["owner"], None)
        saves = p0cells0c.saves_record(SAVES, START, END)
        self.assertEqual([w["pending"] for w in saves["windows"]], [0, 4, 8, 0])   # 10:03:00 lies after the end

    def test_an_unstamped_window_is_refused(self):
        with self.assertRaisesRegex(ValueError, "no stamp"):
            p0cells0c.cell_record_0c("C", ["Net stats window 5.00 s: sent 1 pkt received 1 pkt resent 0 | loss share "
                                           "0"], START, END)

    def test_burst_figures(self):
        figures = p0cells0c.burst_figures(self.record(True), p0cells0c.saves_record(SAVES, START, END))
        self.assertEqual((figures["arrivals"], figures["ready"], figures["failed"]), (20, 19, 1))
        self.assertEqual(figures["ready_ms"][:2], [51, 52])
        self.assertEqual(figures["token_get_ms"][0], 51)   # per-step laps (ServerMeshDbBurst.Lap): token 20 + get 31
        self.assertEqual((figures["settled_s"], figures["arrivals_per_s"], figures["requests_per_s"]), (5.0, 4.0, 28.0))
        self.assertEqual((figures["peak_pending"], figures["save_failed"], figures["empty_s"]), (8, 1, 5.0))
        self.assertEqual(figures["written_per_s"], 1.0)   # 20 written over the four windows' 20 s
        self.assertAlmostEqual(figures["slope_per_s"], 0.8)
        self.assertEqual(p0cells0c.burst_figures({"C": p0cells0c.cell_record_0c("C", [], START, END)}, None), {})

    def test_metrics_and_fields(self):
        spec = {"cells_read": ["C"]}
        records, saves = self.record(True), p0cells0c.saves_record(SAVES, START, END)
        metrics = {(m["name"], m["stat"], m["cell"]): m["value"]
                   for m in p0cells0c.cell_metrics_0c(records["C"]) + p0cells0c.run_metrics_0c(spec, records, saves)}
        self.assertEqual(metrics[("latency_ms{db_ready}", "p95", "C")], 69)
        self.assertEqual(metrics[("latency_ms{db_ready}", "n", "C")], 19)
        self.assertEqual(metrics[("db_errors", "total", "C")], 2)
        self.assertEqual(metrics[("db_saves_pending", "max", "editor")], 8)
        self.assertEqual(metrics[("forwarded_hits_per_s", "mean", "C")], 4.0)
        self.assertEqual(metrics[("latency_ms{forwarded_hit}", "p50", "C")], 7.0)
        self.assertEqual(metrics[("latency_ms{forwarded_hit}", "p95", "C")], 12.0)
        self.assertEqual(metrics[("spaces_other_seen", "max", "C")], 0)
        self.assertEqual(metrics[("net_resent_per_s", "mean", "C")], 1.0)
        self.assertEqual(metrics[("net_loss_share", "mean", "C")], 0.05)
        self.assertNotIn(("latency_ms{enter_game}", "n", "C"), metrics)
        fields = p0cells0c.run_fields_0c(records, saves)
        self.assertEqual(fields["db_burst"]["started"], "2026-10-09T10:00:10")
        self.assertEqual((fields["monster_cell"], fields["forwarded"]), ({"C": None}, {"sent": 40, "results": 40}))
        self.assertEqual(p0cells0c.run_fields_0c({"C": p0cells0c.cell_record_0c("C", [], START, END)}, None), {})


class EditorLinesTests(unittest.TestCase):
    def test_editor_lines(self):
        self.assertEqual(p0cells0c.editor_lines(START, os.path.join(tempfile.gettempdir(), "no_such_editor.log")), [])
        with tempfile.TemporaryDirectory() as folder:
            path = os.path.join(folder, "Editor.log")
            with open(path, "w", encoding="utf-8") as f:
                f.write("\n".join(["unstamped noise", SAVES[0].replace("10:00:05", "09:59:59"), SAVES[1], SAVES[2]]))
            self.assertEqual(p0cells0c.editor_lines(START, path), SAVES[1:3])
            self.assertEqual(p0cells0c.editor_lines(START, path, tail_bytes=len(SAVES[2]) + 1), SAVES[2:3])


if __name__ == "__main__":
    unittest.main()
