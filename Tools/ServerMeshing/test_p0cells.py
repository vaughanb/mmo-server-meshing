"""Unit tests for p0cells (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py")."""
import datetime
import math
import unittest

import p0cells
import p0tools

START = datetime.datetime(2026, 9, 26, 10, 0, 0)
END = datetime.datetime(2026, 9, 26, 10, 1, 0)


def at(clock):
    return datetime.datetime.fromisoformat(f"2026-09-26 {clock}")


def stamped_at(clock, message):
    return f" INFO Mesh [2026-09-26 {clock}] - {message}"


def window_line(clock, frames=150, work_mean=3.0, sat=0.0, budget=33.33, owned=60, monsters=0, puppets=0,
                seconds="5.00"):
    return stamped_at(clock, f"Work window {seconds} s: frames {frames} work mean {work_mean:.3f} ms p95 4.00 ms "
                             f"max 6.0 ms wait 30.000 ms period 33.333 ms budget {budget:.2f} ms sat {sat:.1f}% "
                             f"probe=Valid | owned players {owned} monsters {monsters} | puppets players {puppets} "
                             f"monsters 0 | clients 0 cpu 12%")


def ticks_line(clock, work=300.0, frames=150, t0=50, t1=100, t2=0, mean=3.0, p95=4.1, budget=33.33, seconds="5.00"):
    return stamped_at(clock, (
        f"Work ticks {seconds} s: ticks 100 expected 100 dropped 0 cap 0 unclosed 0 | frames {frames} t0 {t0} t1 {t1} "
        f"t2 {t2} | tick frames mean {mean:.3f} ms p95 {p95:.2f} ms p99 5.20 ms max 6.1 ms | nonaoi p95 4.00 ms "
        f"p99 4.50 ms | aoi passes 5 frames 5 mean 4.000 ms max 8.2 ms | other mean 1.203 ms p95 1.50 ms "
        f"| kit mean 0.812 ms p95 1.00 ms p99 1.20 ms max 2.0 ms | parts 100 sync 0.300 ms aoi 0.050 ms "
        f"move 0.462 ms | late 150 mean 0.020 ms max 0.9 ms 0.6 ms/s | send 100 mean 0.462 ms p95 0.60 ms "
        f"p99 0.70 ms | physics 50.0/s mean 1.67 max 2 f2 100 | work {work:.1f} ms/s busy {work / 10:.1f}% "
        f"| budget {budget:.2f} ms | gc0 0 | outside 0"))


def bins_line(clock, tick="49:96 60:4"):
    return stamped_at(clock, f"Work bins 5.00 s: tick {tick} over:0 | nonaoi 49:95 over:0 | other 10:50 over:0 "
                             f"| kit 16:100 over:0 | send 12:100 over:0")


THREADS = ("Threads window 5.00 s: main {main} litenetlib 20.1 jobs 150.0 unnamed 0.0 other 12.5 total 994.9 "
           "process 1010.2 ms/s | threads 57 sampler 0.9 ms/s clock cycles 2.994 GHz names pre-start=absent")
PUPPETS = ("Puppets window 10.00 s: kinematic {kinematic} steered 0 | fallback mounted 0 summoned 0 handler 0 "
           "grid {grid} install 0 | snaps 2 | observed ticks 600/s unobserved poses {poses}/s | steer kinematic 240 "
           "mean 0.12 m p95 {p95} m max 1.20 m | steer steered 0 mean 0.00 m p95 0.00 m max 0.00 m")


def ww(**fields):
    base = {"end": at("10:00:05"), "seconds": 5.0, "frames": 150, "work_mean": 3.0, "budget": 33.33, "sat": 0.0,
            "owned_players": 60, "owned_monsters": 0, "puppet_players": 0, "puppet_monsters": 0, "clients": 0}
    return dict(base, **fields)


def wt(**fields):
    return dict(p0tools.parse_work_ticks(ticks_line("10:00:05")), **fields)


def triple(clock="10:00:05", window=None, ticks=None, bins=None):
    return {"end": at(clock), "seconds": 5.0, "window": window or ww(end=at(clock)), "ticks": ticks or wt(end=at(clock)),
            "bins": bins}


def hists(tick):
    empty = {"bins": {}, "over": 0}
    return {"tick": tick, "nonaoi": empty, "other": empty, "kit": empty, "send": empty}


class SelectWindowsTests(unittest.TestCase):
    def test_refuses_an_unstamped_window(self):
        with self.assertRaisesRegex(ValueError, "a 5 s window has no stamp"):
            p0cells.select_windows([{"end": None, "seconds": 5.0}], START, END)

    def test_select_windows(self):
        cases = {
            "began 2 s before the start": ("10:00:03", False),
            "began 1 s before the start": ("10:00:04", True),
            "began at the start": ("10:00:05", True),
            "ends at the end": ("10:01:00", True),
            "ends after the end": ("10:01:01", False),
        }
        for name, (clock, inside) in cases.items():
            with self.subTest(name):
                chosen = p0cells.select_windows([{"end": at(clock), "seconds": 5.0}], START, END)
                self.assertEqual(len(chosen), 1 if inside else 0)

    def test_sorted_by_end(self):
        items = [{"end": at(clock), "seconds": 5.0} for clock in ("10:00:20", "10:00:10", "10:00:15")]
        self.assertEqual([i["end"] for i in p0cells.select_windows(items, START, END)],
                         [at("10:00:10"), at("10:00:15"), at("10:00:20")])


class SaturationTests(unittest.TestCase):
    def test_window_saturated(self):
        cases = {
            "uncapped: sat 100 %": ((ww(budget=0.0, sat=100.0), wt(), False), False),
            "sat 20.1 %": ((ww(sat=20.1), wt(), False), True),
            "util exactly 0.95": ((ww(work_mean=15.8365, budget=16.67), wt(), False), True),
            "fan-out 4 of 150 frames with two ticks": ((ww(), wt(t2=4, frames=150), True), True),
            "sat 20.0 %": ((ww(sat=20.0), wt(), False), False),
            "util 0.9496": ((ww(work_mean=15.83, budget=16.67), wt(), False), False),
            "fan-out 3 of 150 frames with two ticks": ((ww(), wt(t2=3, frames=150), True), False),
            "4 of 150 without fan-out": ((ww(), wt(t2=4, frames=150), False), False),
            "fan-out with no frames": ((ww(), wt(t2=0, frames=0), True), False),
        }
        for name, ((window, ticks, fanout), expected) in cases.items():
            with self.subTest(name):
                self.assertIs(p0cells.window_saturated(window, ticks, fanout), expected)

    def test_span_saturated(self):
        cases = {
            "frame-weighted sat 21 %": ([ww(sat=30.0), ww(sat=12.0, end=at("10:00:10"))], True),
            "frame-weighted util 0.95": ([ww(work_mean=19.0, budget=16.67, frames=100),
                                          ww(work_mean=12.673, budget=16.67, frames=100, end=at("10:00:10"))], True),
            "the last window's budget": ([ww(work_mean=16.0, budget=33.33, end=at("10:00:10")),
                                          ww(work_mean=16.0, budget=16.67)], False),
            "no windows": ([], False),
            "no frames": ([ww(frames=0, sat=90.0)], False),
            "frame-weighted sat 20 %": ([ww(sat=30.0), ww(sat=10.0, end=at("10:00:10"))], False),
            "one saturated window of three": ([ww(sat=40.0), ww(end=at("10:00:10")), ww(end=at("10:00:15"))], False),
            "uncapped": ([ww(budget=0.0, sat=100.0)], False),
        }
        for name, (windows, expected) in cases.items():
            with self.subTest(name):
                self.assertIs(p0cells.span_saturated(windows), expected)


class AggregateWorkTicksTests(unittest.TestCase):
    def test_no_triples(self):
        self.assertIsNone(p0cells.aggregate_work_ticks([]))

    def test_the_worked_example(self):
        first = triple("10:00:05", ww(frames=300), wt(seconds=5.0, frames=300, work_ms_per_s=400.0, t1=200, t2=0,
                                                      tick_frame_mean=3.0))
        second = triple("10:00:10", ww(frames=100), wt(seconds=5.0, frames=100, work_ms_per_s=200.0, t1=50, t2=50,
                                                       tick_frame_mean=6.0))
        agg = p0cells.aggregate_work_ticks([first, second])
        self.assertAlmostEqual(agg["work_ms_per_s"], 300.0)
        self.assertAlmostEqual(agg["work_ms_per_measured_frame"], 7.5)
        self.assertAlmostEqual(agg["tick_frame_mean"], 4.0)
        self.assertEqual((agg["windows"], agg["seconds"], agg["frames"], agg["t1"], agg["t2"]), (2, 10.0, 400, 250, 50))
        self.assertAlmostEqual(agg["multi_tick_per_1000"], 125.0)
        self.assertAlmostEqual(agg["frames_per_s"], 40.0)
        self.assertAlmostEqual(agg["ticks_per_s"], 20.0)
        self.assertEqual((agg["budget"], agg["problems"], agg["p95_source"]), (33.33, [], "worst-window"))

    def test_percentiles_from_merged_bins(self):
        a = triple("10:00:05", bins=hists({"bins": {49: 96, 60: 4}, "over": 0}))
        b = triple("10:00:10", bins=hists({"bins": {40: 90, 70: 10}, "over": 0}))
        agg = p0cells.aggregate_work_ticks([a, b])
        self.assertEqual(agg["p95_source"], "bins")
        self.assertEqual(agg["tick_frame_p95"], 3.05)
        self.assertEqual(p0cells.aggregate_work_ticks([a])["tick_frame_p95"], 2.5)
        self.assertEqual(p0cells.aggregate_work_ticks([b])["tick_frame_p95"], 3.55)
        self.assertEqual(agg["nonaoi_p95"], 0.0)

    def test_an_overflow_rank(self):
        over = triple(ticks=wt(tick_frame_max=231.4), bins=hists({"bins": {10: 90}, "over": 10}))
        over["bins"]["nonaoi"] = {"bins": {10: 90}, "over": 10}
        agg = p0cells.aggregate_work_ticks([over])
        self.assertEqual(agg["tick_frame_p95"], 231.4)
        self.assertIsNone(agg["nonaoi_p95"])

    def test_worst_window_when_a_triple_lacks_its_bins(self):
        with_bins = triple("10:00:05", ticks=wt(tick_frame_p95=4.1), bins=hists({"bins": {49: 100}, "over": 0}))
        without = triple("10:00:10", ticks=wt(tick_frame_p95=5.5))
        agg = p0cells.aggregate_work_ticks([with_bins, without])
        self.assertEqual((agg["p95_source"], agg["tick_frame_p95"]), ("worst-window", 5.5))

    def test_a_window_with_another_budget(self):
        agg = p0cells.aggregate_work_ticks([triple("10:00:05"), triple("10:00:10", ticks=wt(budget=16.67))])
        self.assertEqual(agg["budget"], 33.33)
        self.assertEqual(agg["problems"], ["Work ticks at 10:00:10: budget 16.67 ms differs from 33.33 ms "
                                           "(the first window's)"])

    def test_zero_weights_give_zero(self):
        quiet = wt(aoi_frames=0, aoi_frame_mean=0.0, late_calls=0, late_mean=0.0, send_samples=0, send_mean=0.0,
                   parts=0, t0=0, other_mean=0.0)
        agg = p0cells.aggregate_work_ticks([triple(ticks=quiet), triple("10:00:10", ticks=dict(quiet))])
        for name in ("aoi_frame_mean", "late_mean", "send_mean", "sync_mean", "other_mean"):
            with self.subTest(name):
                self.assertEqual(agg[name], 0.0)
                self.assertFalse(math.isnan(agg[name]))

    def test_window_figures(self):
        agg = p0cells.aggregate_work_ticks([triple("10:00:05", ww(sat=10.0, frames=300, owned=60)),
                                            triple("10:00:10", ww(sat=40.0, frames=100, owned_players=80))])
        self.assertAlmostEqual(agg["sat_pct"], 17.5)
        self.assertAlmostEqual(agg["owned_players"], 70.0)


class PairWorkLinesTests(unittest.TestCase):
    def test_pair_work_lines(self):
        cases = {
            "a Work ticks line alone": (
                [ticks_line("10:00:05")], ([], ["Work ticks at 10:00:05 has no Work window"])),
            "a second Work bins line": (
                [window_line("10:00:05"), ticks_line("10:00:05"), bins_line("10:00:05"), bins_line("10:00:05")],
                ([("10:00:05", True)], ["Work bins at 10:00:05 has no Work ticks"])),
            "a Work bins line after a Work window": (
                [window_line("10:00:05"), bins_line("10:00:05")],
                ([], ["Work window at 10:00:05 has no Work ticks", "Work bins at 10:00:05 has no Work ticks"])),
            "a Work window line followed by another": (
                [window_line("10:00:05"), window_line("10:00:10"), ticks_line("10:00:10")],
                ([("10:00:10", False)], ["Work window at 10:00:05 has no Work ticks"])),
            "a Work ticks line two seconds late": (
                [window_line("10:00:05"), ticks_line("10:00:07")],
                ([], ["Work window at 10:00:05 has no Work ticks", "Work ticks at 10:00:07 has no Work window"])),
            "a Work ticks line stamped before its window": (
                [window_line("10:00:05"), ticks_line("10:00:04")],
                ([], ["Work window at 10:00:05 has no Work ticks", "Work ticks at 10:00:04 has no Work window"])),
            "other seconds": (
                [window_line("10:00:05", seconds="4.00"), ticks_line("10:00:05")],
                ([], ["Work window at 10:00:05 has no Work ticks", "Work ticks at 10:00:05 has no Work window"])),
            "a Work bins line after an unpaired Work ticks line": (
                [ticks_line("10:00:05"), bins_line("10:00:05")], ([], ["Work ticks at 10:00:05 has no Work window"])),
            "a last Work window line": (
                [window_line("10:00:05"), ticks_line("10:00:05"), window_line("10:00:10")],
                ([("10:00:05", False)], ["Work window at 10:00:10 has no Work ticks"])),
            "paired without bins": ([window_line("10:00:05"), ticks_line("10:00:05")], ([("10:00:05", False)], [])),
            "paired across a second boundary": (
                [window_line("10:00:05"), ticks_line("10:00:06"), bins_line("10:00:07")], ([("10:00:05", True)], [])),
            "other lines in between": (
                [stamped_at("10:00:00", "Own cell from layout: C"), window_line("10:00:05"),
                 stamped_at("10:00:05", "Mesh stats: players=1 owned=2"), ticks_line("10:00:05"),
                 stamped_at("10:00:05", "hello"), bins_line("10:00:05"), window_line("10:00:10"),
                 ticks_line("10:00:11")],
                ([("10:00:05", True), ("10:00:10", False)], [])),
        }
        for name, (lines, (expected_triples, expected_problems)) in cases.items():
            with self.subTest(name):
                triples, problems = p0tools.pair_work_lines(lines)
                self.assertEqual([(t["end"].strftime("%H:%M:%S"), t["bins"] is not None) for t in triples],
                                 expected_triples)
                self.assertEqual([p["text"] for p in problems], expected_problems)

    def test_a_triple_holds_its_three_lines(self):
        triples, problems = p0tools.pair_work_lines(
            [window_line("10:00:05"), ticks_line("10:00:06"), bins_line("10:00:06")])
        self.assertEqual(problems, [])
        self.assertEqual(set(triples[0]), {"end", "seconds", "window", "ticks", "bins"})
        self.assertEqual((triples[0]["end"], triples[0]["seconds"]), (at("10:00:05"), 5.0))
        self.assertEqual(triples[0]["window"]["owned_players"], 60)
        self.assertEqual(triples[0]["ticks"]["end"], at("10:00:06"))
        self.assertEqual(triples[0]["bins"]["kit"], {"bins": {16: 100}, "over": 0})

    def test_a_problem_carries_its_window(self):
        _, problems = p0tools.pair_work_lines([ticks_line("10:00:05")])
        self.assertEqual(problems, [{"end": at("10:00:05"), "seconds": 5.0,
                                     "text": "Work ticks at 10:00:05 has no Work window"}])

    def test_bins_problems(self):
        def paired(bins=bins_line("10:00:05"), **ticks):
            return {"end": at("10:00:05"), "seconds": 5.0, "window": {}, "ticks": wt(**ticks),
                    "bins": p0tools.parse_work_bins(bins) if bins else None}

        cases = {
            "no Work bins line": (paired(None), ["Work ticks at 10:00:05 has no Work bins line"]),
            "a tick total off": (paired(t1=101), [
                "Work bins at 10:00:05: tick total 100 is not t1 + t2 = 101",
                "Work bins at 10:00:05: nonaoi total 95 is not t1 + t2 - aoi frames = 96"]),
            "a nonaoi total off": (paired(aoi_frames=4),
                                   ["Work bins at 10:00:05: nonaoi total 95 is not t1 + t2 - aoi frames = 96"]),
            "an other total off": (paired(t0=51), ["Work bins at 10:00:05: other total 50 is not t0 = 51"]),
            "a send total off": (paired(send_samples=99),
                                 ["Work bins at 10:00:05: send total 100 is not send samples = 99"]),
            "a kit total outside unclosed": (paired(ticks=103, unclosed=1), [
                "Work bins at 10:00:05: kit total 100 is not within 1 of ticks - unclosed = 102"]),
            "the overflow counts": (paired(bins_line("10:00:05").replace("other 10:50 over:0", "other 10:49 over:1")),
                                    []),
            "a kit total exactly unclosed away": (paired(ticks=102, unclosed=1), []),
            "all agree": (paired(), []),
        }
        for name, (value, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0tools.bins_problems(value), expected)


def run_lines(saturated=("10:00:25",)):
    lines = [stamped_at("09:59:40", "MESH_BAND=80: ghost band and visible range set to 80 m"),
             stamped_at("09:59:41", "Load generator: spawned 60 synthetic players on cell C over x [-300, 0] "
                                    "z [-300, 300] (seed 1, planned 60)"),
             stamped_at("09:59:42", "Rates: fps 60 (MESH_RATES; kit 60) vSync 0 | tick 20 Hz (updateFps unchanged) "
                                    "| physics 0.03 s (project 0.03) | maxDeltaTime 0.333 | probe budget 16.67 ms "
                                    "| uncapped cells -"),
             ticks_line("09:59:50")]
    for second in range(-5, 70, 5):
        clock = (START + datetime.timedelta(seconds=second)).strftime("%H:%M:%S")
        sat = 25.0 if clock in saturated else 0.0
        lines += [window_line(clock, sat=sat, owned=60 if clock not in saturated else 90),
                  ticks_line(clock, work=900.0 if clock in saturated else 300.0), bins_line(clock)]
        if clock == "10:00:30":
            lines.append(ticks_line("10:00:32"))
    for clock, main in (("10:00:07", 800.0), ("10:00:12", 820.0), ("10:01:30", 999.0)):
        lines.append(stamped_at(clock, THREADS.format(main=main)))
    for clock, kinematic, grid in (("10:00:05", 58, 0), ("10:00:30", 62, 1)):
        lines.append(stamped_at(clock, PUPPETS.format(kinematic=kinematic, grid=grid, poses=8.5, p95=0.35)))
    for clock, mem in (("09:59:55", 999), ("10:00:10", 210), ("10:00:20", 230), ("10:00:30", 220)):
        lines.append(stamped_at(clock, f"Mesh stats: players=60 owned=60 | frame mean 16.8 ms max 20.1 ms cpu 34% "
                                       f"mem {mem} MB clients=0 clientOut=0.0 kB/s (0 pkt/s)"))
    return lines


class CellRecordTests(unittest.TestCase):
    def test_one_saturated_window_is_dropped(self):
        record = p0cells.cell_record("C", run_lines(), START, END)
        self.assertEqual(len(record["triples"]), 12)
        self.assertEqual(record["saturated"], [at("10:00:25")])
        self.assertEqual(record["dropped"], [at("10:00:25")])
        self.assertEqual(record["ticks"]["windows"], 11)
        self.assertAlmostEqual(record["ticks"]["work_ms_per_s"], 300.0)
        self.assertEqual(record["bins"]["tick"], {"bins": {49: 11 * 96, 60: 11 * 4}, "over": 0})

    def test_two_saturated_windows_drop_nothing(self):
        record = p0cells.cell_record("C", run_lines(("10:00:25", "10:00:45")), START, END)
        self.assertEqual(record["saturated"], [at("10:00:25"), at("10:00:45")])
        self.assertEqual(record["dropped"], [])
        self.assertEqual(record["ticks"]["windows"], 12)
        self.assertAlmostEqual(record["ticks"]["work_ms_per_s"], 400.0)

    def test_the_fan_out_rule_applies_only_to_fan_out_runs(self):
        lines = [ticks_line("10:00:40", t2=10) if line == ticks_line("10:00:40") else line for line in run_lines(())]
        cases = {"another family": (False, []), "family fanout": (True, [at("10:00:40")])}
        for name, (fanout, saturated) in cases.items():
            with self.subTest(name):
                record = p0cells.cell_record("C", lines, START, END, fanout)
                self.assertEqual((record["saturated"], record["dropped"]), (saturated, saturated))

    def test_load_fields(self):
        record = p0cells.cell_record("C", run_lines(), START, END)
        with self.assertRaisesRegex(ValueError, "MESH_BAND 'wide' is not a number of metres"):
            p0cells.load_fields({}, {"MESH_BAND": "wide"}, {"C": record})
        fields = p0cells.load_fields({"band": 150.0}, {"MESH_LOAD_MOTION": "Inert"}, {"C": record})
        self.assertEqual({key: fields[key] for key in ("kind", "owned", "density", "band_m", "visible_range_m",
                                                       "scale")},
                         {"kind": "character", "owned": {"character_inert": 60.0, "monster": 0.0}, "density": 3000.0,
                          "band_m": 150.0, "visible_range_m": 150.0, "scale": 1.0})
        self.assertEqual(p0cells.load_fields({}, {}, {})["band_m"], p0cells.DEFAULT_BAND)

    def test_only_problems_inside_the_measurement(self):
        record = p0cells.cell_record("C", run_lines(), START, END)
        self.assertEqual(record["pairing"], ["Work ticks at 10:00:32 has no Work window"])

    def test_threads_puppets_and_memory(self):
        record = p0cells.cell_record("C", run_lines(), START, END)
        self.assertEqual(len(record["threads"]["windows"]), 2)
        self.assertAlmostEqual(record["threads"]["main"], 810.0)
        self.assertEqual(record["threads"]["clocks"], ["cycles"])
        self.assertEqual(len(record["puppets"]["windows"]), 1)
        self.assertEqual((record["puppets"]["kinematic"], record["puppets"]["fallback_grid"]), (62.0, 1))
        self.assertEqual(record["mem_mb"], 220)

    def test_everything_since_play(self):
        record = p0cells.cell_record("C", run_lines(), START, END)
        self.assertEqual([r["fps"] for r in record["rates"]], [60])
        self.assertEqual(record["rates_cells"], [])
        self.assertEqual([m["band"] for m in record["markers"]["band"]], [80.0])
        self.assertEqual([m["spawned"] for m in record["markers"]["spawned"]], [60])
        self.assertEqual(record["markers"]["tick_burn"], [])
        self.assertEqual(set(record["markers"]), set(p0cells.MARKERS))

    def test_no_lines(self):
        record = p0cells.cell_record("C", [], START, END)
        self.assertEqual((record["triples"], record["ticks"], record["bins"], record["threads"], record["puppets"],
                          record["mem_mb"]), ([], None, None, None, None, None))


class CellMetricsTests(unittest.TestCase):
    def test_an_empty_record(self):
        record = p0cells.cell_record("C", [], START, END)
        self.assertEqual(p0cells.cell_metrics(record, 60, "C"), [])

    def test_metric_order_and_values(self):
        metrics = p0cells.cell_metrics(p0cells.cell_record("C", run_lines(), START, END), 60, "C")
        names = [(m["name"], m["stat"]) for m in metrics]
        self.assertEqual(names[:3], [("work_ms_per_s", "mean"), ("work_ms_per_frame", "mean"), ("tick_frame_ms", "mean")])
        self.assertEqual(names[names.index(("sat_pct", "mean")) + 1], ("thread_ms_per_s{main}", "mean"))
        self.assertLess(names.index(("mem_MB", "median")), names.index(("frames_per_s", "mean")))
        self.assertEqual(names[-5:], [(f"puppet_fallbacks{{{reason}}}", "total") for reason in p0cells.FALLBACKS])
        values = {(m["name"], m["stat"]): m["value"] for m in metrics}
        self.assertAlmostEqual(values[("work_ms_per_frame", "mean")], 5.0)
        self.assertAlmostEqual(values[("process_cores", "mean")], (1010.2 - 0.9) / 1000)
        self.assertEqual(values[("thread_ms_per_s{sampler}", "mean")], 0.9)
        self.assertEqual(values[("mem_MB", "median")], 220)
        self.assertEqual(values[("tick_frame_ms", "p95")], 2.5)
        self.assertEqual(values[("puppet_count{kinematic}", "mean")], 62.0)
        self.assertEqual(values[("puppet_steer_m{kinematic}", "p95")], 0.35)
        for metric in metrics:
            with self.subTest(metric["name"]):
                self.assertEqual((metric["label"], metric["cell"]), ("M", "C"))

    def test_no_stated_fps(self):
        metrics = p0cells.cell_metrics(p0cells.cell_record("C", run_lines(), START, END), None, "E")
        self.assertNotIn("work_ms_per_frame", [m["name"] for m in metrics])
        self.assertEqual({m["cell"] for m in metrics}, {"E"})


class RowHelpersTests(unittest.TestCase):
    def test_window_rows(self):
        record = p0cells.cell_record("C", run_lines(), START, END)
        rows = p0cells.window_rows(record)
        self.assertEqual(len(rows), 12)
        self.assertEqual(rows[0], {"end": "2026-09-26T10:00:05", "seconds": 5.0, "frames": 150, "work_ms_per_s": 300.0,
                                   "owned_players": 60, "owned_monsters": 0, "puppet_players": 0, "puppet_monsters": 0,
                                   "saturated": False})
        self.assertEqual([r["end"] for r in rows if r["saturated"]], ["2026-09-26T10:00:25"])

    def test_spawn_area(self):
        self.assertEqual(p0cells.spawn_area(p0cells.cell_record("C", run_lines(), START, END)), (180000, 60))
        self.assertEqual(p0cells.spawn_area(p0cells.cell_record("C", [], START, END)), (0, 0))

    def test_stated_rates_refuses_a_malformed_value(self):
        with self.assertRaisesRegex(ValueError, "MESH_RATES step part 'x' is not a number of seconds"):
            p0cells.stated_rates("30/x")

    def test_stated_rates(self):
        cases = {
            "none set": (None, {"fps": None, "fixed_step_s": None, "capped": True}),
            "capped": ("60/0.03", {"fps": 60, "fixed_step_s": 0.03, "capped": True}),
            "uncapped at a nominal rate": ("u30/0.02", {"fps": 30, "fixed_step_s": 0.02, "capped": False}),
            "bare u": ("u/0.02", {"fps": None, "fixed_step_s": 0.02, "capped": False}),
        }
        for name, (text, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0cells.stated_rates(text), expected)


if __name__ == "__main__":
    unittest.main()
