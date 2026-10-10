"""Unit tests for p0checks9 and step 9's rows (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py"): the
run checks of dbburst, workerab, spaces and packets' Net stats, the Database gate, sustained capacity, workerab's
acceptance, the spaces floor and the loss figures. Runs and rows are built here; nothing touches the rig."""
import datetime
import unittest

import p0base
import p0cells0c
import p0checks
import p0checks9
import p0plans9
import p0report
import p0results
import test_p0cells0c as cells

START, END = cells.START, cells.END


def run_of(lines, saves=(), cell="C"):
    return {"cells": {cell: {"p0c": p0cells0c.cell_record_0c(cell, lines, START, END)}},
            "saves": p0cells0c.saves_record(list(saves), START, END)}


def burst_spec(setting="5/s@55+4", probe=True):
    env = {"MESH_DB_BURST": setting, **({"MESH_DB_SAVES_PROBE": "1"} if probe else {})}
    return {"family": "dbburst", "cells_read": ["C"], "env": env}


class RunCheckTests(unittest.TestCase):
    def test_check_db_burst(self):
        good = cells.burst_lines()
        cases = {   # problems first
            "no start line": (burst_spec(), run_of(good[1:], cells.SAVES), ["no 'DB burst:' start line on C"]),
            "another setting": (burst_spec("20@55"), run_of(good, cells.SAVES),
                                ["the DB burst start line says rate 5 over 4 s, the setting 20@55"]),
            "no done line": (burst_spec(), run_of([line for line in good if "done," not in line], cells.SAVES),
                             ["the DB burst logged no done line by the run's end (20 of 20 arrivals settled)"]),
            "an arrival short": (burst_spec(), run_of([line for line in good if "DB burst 20:" not in line],
                                                      cells.SAVES), ["19 DB burst arrival lines, expected 20"]),
            "no window": (burst_spec(), run_of([line for line in good if "burst window" not in line], cells.SAVES),
                          ["no DB burst window in the measurement"]),
            "no saves window": (burst_spec(), run_of(good),
                                ["no DB saves window from the editor in the measurement (MESH_DB_SAVES_PROBE=1)"]),
            "good": (burst_spec(), run_of(good, cells.SAVES), []),
            "good without the probe": (burst_spec(probe=False), run_of(good), []),
            "no burst": ({"family": "dbburst", "cells_read": ["C"], "env": {}}, run_of([]), []),
        }
        for name, (spec, run, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0checks9.check_db_burst(spec, run), expected)
        with self.assertRaisesRegex(ValueError, "MESH_DB_BURST: "):
            p0checks9.check_db_burst(burst_spec("soon"), run_of(good))

    def test_check_enter_game(self):
        spec = {"family": "dbburst", "cells_read": ["C"], "env": {}, "peer": {"bots": {"n": 2}}}
        line = cells.stamped("10:00:01", "S enter-game validated+loaded ok={} in 90ms", "MeshTrace")
        cases = {"one refused": ([line.format("True"), line.format("False")], ["1 of 2 bots logged an ok enter-game "
                                                                               "line (MeshTrace; traceHandoffTiming "
                                                                               "must be on)"]),
                 "both": ([line.format("True")] * 2, [])}
        for name, (lines, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0checks9.check_enter_game(spec, run_of(lines)), expected)
        self.assertEqual(p0checks9.check_enter_game(dict(spec, peer=None), run_of([])), [])

    def test_check_monster_cell_and_forwarded(self):
        def run(c_line, e_line, forwarded=True):
            window = cells.stamped("10:00:20", "Forwarded hits window 5.00 s: sent 3 results 3 | p50 1 p95 2 max 3 ms")
            return {"cells": {"C": {"p0c": p0cells0c.cell_record_0c("C", [c_line] + ([window] if forwarded else []),
                                                                    START, END)},
                              "E": {"p0c": p0cells0c.cell_record_0c("E", [e_line], START, END)}}}
        none = cells.stamped("10:00:01", "MESH_MONSTER_CELL=E: this cell owns no monster spawn area")
        owns = cells.stamped("10:00:01", "MESH_MONSTER_CELL=E: cell E owns every monster spawn area")
        halves = {"family": "workerab", "cells_read": ["C", "E"],
                  "env": {"MESH_MONSTER_CELL": "E", "MESH_FORWARD_TIMING": "1"}}
        cases = {
            "the lines swapped": (halves, run(owns, none), [
                "C logged no 'MESH_MONSTER_CELL=E: this cell owns no monster spawn area' line",
                "E logged no 'MESH_MONSTER_CELL=E: cell E owns every monster spawn area' line"], []),
            "no forwarded window": (halves, run(none, owns, False), [],
                                    ["no Forwarded hits window on a cell other than E in the measurement"]),
            "a line in coarse": (dict(halves, env={}), run(none, "-"), ["C logged a MESH_MONSTER_CELL line with no key "
                                                                        "set"], []),
            "halves": (halves, run(none, owns), [], []),
        }
        for name, (spec, run_, cell_problems, forwarded) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0checks9.check_monster_cell(spec, run_), cell_problems)
                self.assertEqual(p0checks9.check_forwarded(spec, run_), forwarded)

    def test_check_spaces_and_net_stats(self):
        spec = {"family": "spaces", "cells_read": ["C"],
                "env": {"MESH_SPACES": "4:stack", "MESH_PHANTOM": "virtual:4:character:C", "MESH_NET_STATS": "1"}}
        setting = cells.stamped("10:00:01", "Spaces: S 4 layout stack pitch 0 m | per space observers 1 content 5")
        window = cells.stamped("10:00:20", "Spaces window 5.00 s: observers {} | own seen mean 5 | other seen max {}")
        net = cells.stamped("10:00:20", "Net stats window 5.00 s: sent 9 pkt received 9 pkt resent 0 | loss share 0")
        cases = {
            "nothing logged": ([], ["C logged no 'Spaces: S 4 layout stack' line",
                                    "C logged no Spaces window in the measurement"],
                               ["C logged no Net stats window in the measurement (MESH_NET_STATS=1)"]),
            "a leak": ([setting, window.format(4, 2), net], ["C: other seen max 2 in 1 of 1 Spaces windows (must be "
                                                             "0)"], []),
            "an observer short": ([setting, window.format(3, 0), net], ["C: 1 Spaces windows held other than 4 "
                                                                        "observers"], []),
            "isolated": ([setting, window.format(4, 0), net], [], []),
        }
        for name, (lines, spaces, stats) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0checks9.check_spaces(spec, run_of(lines)), spaces)
                self.assertEqual(p0checks9.check_net_stats(spec, run_of(lines)), stats)

    def test_family_checks_join_step_9(self):
        for family, extra in p0checks9.CHECKS_9.items():
            with self.subTest(family):
                self.assertEqual(p0checks.FAMILY_CHECKS[family][-len(extra):], extra)
        self.assertIn(p0checks9.check_net_stats, p0checks.FAMILY_CHECKS["packets"])


def row(family="dbburst", case="burst", arm="at-once", point=None, repeat=1, accepted=True, **fields):
    point = point or {"N": 200}
    key = f"{family}|{case}|{arm}|{sorted(point.items())}|{repeat}"
    base = {"schema": "mesh-p0/1", "row": "run", "team": "rig", "game": "neutral", "family": family, "stage": "0c",
            "build": "b1", "cpu": "cpu", "host": "A", "case": case, "arm": arm, "point": point, "repeat": repeat,
            "plan_key": key, "accepted": accepted, "metrics": []}
    return dict(base, **fields)


def burst(ready_ms, failed=0, slope=None, empty=5.0, rate=None):
    return {"ready_ms": sorted(ready_ms), "failed": failed, "save_failed": 0, "arrivals_per_s": rate or 10.0,
            "requests_per_s": 7 * (rate or 10.0), "written_per_s": 8.0, "peak_pending": 40, "empty_s": empty,
            "slope_per_s": slope, "token_get_ms": [20, 30, 40]}


def metric(name, stat, value, cell="C"):
    return {"name": name, "stat": stat, "value": value, "unit": "", "label": "M", "cell": cell}


class GateTests(unittest.TestCase):
    def test_database_gate(self):
        sustained = [row(case="sustained", arm="rate", point={"R": r, "seconds": 40}, repeat=k,
                         db_burst=burst([200] * 18 + [p95] * 2, slope=slope, rate=r))
                     for r, p95, slope in ((5, 300, 0.1), (10, 600, 0.2), (15, 900, 0.9), (20, 1500, 0.1))
                     for k in (1, 2)]
        cases = {   # failures first
            "an error": ([row(db_burst=burst([1000] * 20, failed=1))], p0base.FAIL),
            "slow": ([row(db_burst=burst([1000] * 18 + [6000, 7000]))], p0base.FAIL),
            "no 200 point": ([row(point={"N": 50}, db_burst=burst([100]))], p0base.NO_VERDICT),
            "fast": ([row(db_burst=burst([1000] * 19 + [7000]))], p0base.PASS),
        }
        for name, (rows, verdict) in cases.items():
            with self.subTest(name):
                gates = p0checks9.gates_9(rows)
                self.assertEqual([(g["name"], g["verdict"]) for g in gates], [("Database", verdict)])
        gates = p0checks9.gates_9(cases["fast"][0] + sustained)
        self.assertEqual([g["name"] for g in gates], ["Database", "Crossings (B10) half B"])
        self.assertEqual(gates[1]["verdict"], p0base.NO_VERDICT)
        self.assertIn("sustained capacity R 10/s = 10.0 arrivals/s, 70.0 requests/s", "; ".join(gates[0]["notes"]))
        self.assertEqual(p0checks9.gates_9([row(accepted=False, db_burst=burst([1]))]), [])

    def test_sustained_capacity(self):
        def at(r, p95, slopes):
            return [row(case="sustained", arm="rate", point={"R": r}, repeat=k, db_burst=burst([100] * 18 + [p95] * 2,
                                                                                               slope=s, rate=r))
                    for k, s in enumerate(slopes, 1)]
        cases = {"none passes": (at(5, 1000, (0.1, 0.1)) + at(10, 400, (0.1, None)), None),
                 "the highest passing R": (at(5, 300, (0.1, 0.2)) + at(10, 999, (0.5, 0.4)) + at(15, 500, (0.6, 0.1)),
                                           10),
                 "no sustained rows": ([row(db_burst=burst([1]))], "none")}
        for name, (rows, expected) in cases.items():
            with self.subTest(name):
                capacity = p0checks9.sustained_capacity(rows)
                self.assertEqual(None if capacity is None else capacity["R"], None if expected == "none" else expected)

    def test_db_points_and_calibration(self):
        rows = [row(repeat=k, db_burst=burst([100 * k] * 10, empty=e)) for k, e in ((1, 3.0), (2, None))]
        point = p0checks9.db_points(rows)[0]
        self.assertEqual((point["n_runs"], point["p50"], point["max"], point["empty_s"]), (2, 100, 200, None))
        calibration = [row(case="calibration", arm="bots", point={"N": 25}, enter_game_ms=[100, 120, 140]),
                       row(case="calibration", arm="burst", point={"N": 25}, db_burst=burst([1]))]
        self.assertEqual(p0checks9.calibration_pairs(calibration), [{"pair": 1, "bots": (120, 140),
                                                                      "burst": (30, 40)}])

    def test_report_names_step_9_gates(self):
        self.assertNotIn("Database", [name for name, _ in p0report.PENDING_GATES])
        self.assertIn(("Database", "c1 dbburst rows (p0load dbburst)"), p0report.STEP_8B_GATES)


def worker(arm, load=100, repeat=1, owners=None, ghosts=4.0, sent=5, p95=12.0, awake=50.0, accepted=True):
    windows = {"C": [{"puppet_monsters": ghosts, "owned_monsters": 0}]}
    metrics = [metric("latency_ms{forwarded_hit}", "p95", p95)] if p95 is not None else []
    return row("workerab", "ab", arm, {"players": load}, repeat, accepted, windows=windows, metrics=metrics,
               monster_cell=owners if owners is not None else ({"C": None, "E": "E"} if arm == "halves" else {}),
               forwarded={"sent": sent, "results": sent} if arm == "halves" else None, awake_monsters=awake)


class AcceptanceTests(unittest.TestCase):
    def test_workerab_acceptance(self):
        good = [worker("coarse", awake=50.0), worker("halves", awake=52.0)]
        cases = {   # failures first: the index of the failing line
            "no halves": ([worker("coarse")], 0),
            "C owns monsters": ([worker("coarse"), worker("halves", owners={"C": "E", "E": "E"})], 0),
            "no ghosts on C": ([worker("coarse"), worker("halves", ghosts=0.0)], 1),
            "nothing forwarded": ([worker("coarse"), worker("halves", sent=0)], 2),
            "no timing": ([worker("coarse"), worker("halves", p95=None)], 2),
            "no awake figure": ([worker("coarse", awake=None), worker("halves", awake=None)], 3),
            "a run not accepted": (good + [worker("halves", repeat=2, accepted=False)], 4),
        }
        for name, (rows, failing) in cases.items():
            with self.subTest(name):
                results = p0checks9.workerab_acceptance(rows)
                self.assertFalse(results[failing][0], results[failing][1])
        self.assertTrue(all(passed for passed, _ in p0checks9.workerab_acceptance(good)))
        apart = p0checks9.workerab_acceptance([worker("coarse", awake=164.0), worker("halves", awake=85.0)])
        self.assertEqual(apart[3], (True, "load 100: awake monsters coarse 164.0, halves 85.0 (reported: the monster "
                                          "cell wakes by ghost proximity, coarse by subscription)"))

    def test_spaces_figures(self):
        def space(s, arm, work, repeat):
            return row("spaces", "stack", arm, {"S": s}, repeat, metrics=[metric("work_ms_per_s", "mean", work),
                                                                           metric("spaces_other_seen", "max", 0)])
        rows = [space(64, "per-space", 64.0, 1), space(64, "per-space", 128.0, 2), space(4, "100", 50.0, 1),
                space(16, "100", 74.0, 1)]
        figures = p0checks9.spaces_figures(rows)
        self.assertEqual([(f["S"], f["per_space_ms"], f["n"]) for f in figures["floor"]], [(64, 1.5, 2)])
        self.assertLess(figures["floor"][0]["lo"], 1.5)
        self.assertEqual((figures["slope"], figures["other_seen"]), (2.0, 0))

    def test_loss_figures(self):
        rows = [row("packets", "loss", arm, {"P": 100, "loss_pct": loss}, 1,
                    metrics=[metric("work_ms_per_s", "mean", 300.0 + loss), metric("net_resent_per_s", "mean", loss),
                             metric("sink_in_pkt_per_s", "mean", 1000.0 - loss, "B")])
                for arm, loss in (("loss5", 5), ("loss0", 0))]
        figures = p0checks9.loss_figures(rows)
        self.assertEqual([(f["arm"], f["work"], f["resent_per_s"], f["received_per_pair"]) for f in figures],
                         [("loss0", 300.0, 0.0, 10.0), ("loss5", 305.0, 5.0, 9.95)])


class RowTests(unittest.TestCase):
    def test_a_step_9_row(self):
        spec = p0plans9.spaces_plan()[0]
        lines = [cells.stamped("10:00:01", "Spaces: S 1 layout stack pitch 0 m | per space observers 1 content 5"),
                 cells.stamped("10:00:20", "Spaces window 5.00 s: observers 1 | own seen mean 5 | other seen max 0")]
        run = {"since": START, "start": START, "end": END,
               "cells": {"C": {"p0c": p0cells0c.cell_record_0c("C", lines, START, END)}}}
        out = p0results.run_row(spec, run, {"build": "b1", "host": "A"}, [], None)
        self.assertEqual((out["stage"], out["model"], out["game"]), ("0c", "spaces", "neutral"))
        self.assertEqual(out["spaces"]["C"]["s"], 1)
        self.assertEqual(p0results.metric_value(out, "spaces_other_seen", "max", "C"), 0)
        self.assertEqual(p0results.run_row(dict(spec, model=None, family="workerab"), None, {}, [], None)["model"],
                         "none")
        self.assertEqual((p0results.STAGE["dbburst"], p0results.FAMILY_GAME["workerab"]), ("0c", "fantasy"))
        self.assertTrue(p0cells0c.METRIC_NAMES_0C <= p0results.METRIC_NAMES)
        self.assertIsInstance(datetime.datetime.fromisoformat(out["window_local"]["end"]), datetime.datetime)


if __name__ == "__main__":
    unittest.main()
