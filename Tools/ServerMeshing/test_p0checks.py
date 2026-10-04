"""Unit tests for p0checks (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py")."""
import datetime
import unittest
from unittest import mock

import p0cells
import p0checks
import p0results
import rigrun

SINCE = datetime.datetime(2026, 9, 26, 9, 59, 0)
START = datetime.datetime(2026, 9, 26, 10, 0, 0)
END = datetime.datetime(2026, 9, 26, 10, 1, 0)
WORLD = {"C": "x [-300.00, 0.00) z [-300.00, 300.00)", "E": "x [0.00, 300.00) z [-300.00, 300.00)"}
THREADS = ("Threads window 5.00 s: main 800.0 litenetlib 20.1 jobs 150.0 unnamed 0.0 other 12.5 total 982.6 "
           "process 1000.0 ms/s | threads 57 sampler 0.9 ms/s clock cycles 2.994 GHz names pre-start=absent")
PUPPETS = ("Puppets window 10.00 s: kinematic {kinematic} steered {steered} | fallback mounted 0 summoned 0 handler 0 "
           "grid {steered} install 0 | snaps {snaps} | observed ticks 0/s unobserved poses {poses}/s | steer kinematic "
           "{samples} mean 0.12 m p95 {p95:.2f} m max 0.80 m | steer steered 0 mean 0.00 m p95 0.00 m max 0.00 m")
KINEMATIC_PUPPETS = {"kinematic": 60, "steered": 0, "snaps": 0, "poses": 540, "samples": 240, "p95": 0.35}
IDLE_PUPPETS = dict(KINEMATIC_PUPPETS, kinematic=0, poses=0, samples=0, p95=0.0)
ENV = {"MESH_LAYOUT_OVERRIDE": rigrun.layout_override("halves"), "MESH_SPAWN_CELLS": "C,E", "MESH_LOAD_PLAN": "C:60",
       "MESH_LOAD_SEED": "1", "MESH_NO_MONSTERS": "1", "MESH_RATES": "60/0.03", "MESH_BAND": "300",
       "MESH_AOI_TIMING": "1", "MESH_WORK_BINS": "1", "MESH_THREAD_CPU": "1", "MESH_LOAD_MOTION": "kinematic",
       "MESH_PUPPET_MOTION": "kinematic", "MESH_RIG_SERVER_ANIMATION": "0"}


def at(seconds):
    return (START + datetime.timedelta(seconds=seconds)).strftime("%H:%M:%S")


def stamped(clock, message):
    return f" INFO Mesh [2026-09-26 {clock}] - {message}"


def window(clock, owned=0, puppets=0, fps=60, physics=33.3, ticks=100, dropped=0, passes=5, sat=0.0, tick_mean=5.0,
           other_mean=1.0, bins=True, send=None, frames=None):
    frames = fps * 5 if frames is None else frames
    budget, t0 = 1000 / fps, frames - ticks
    lines = [stamped(clock, f"Work window 5.00 s: frames {frames} work mean 5.000 ms p95 6.00 ms max 8.0 ms wait "
                            f"10.000 ms period {budget:.3f} ms budget {budget:.2f} ms sat {sat:.1f}% probe=Valid | "
                            f"owned players {owned} monsters 0 | puppets players {puppets} monsters 0 | clients 0 cpu 30%"),
             stamped(clock, f"Work ticks 5.00 s: ticks {ticks} expected 100 dropped {dropped} cap 0 unclosed 0 | frames "
                            f"{frames} t0 {t0} t1 {ticks} t2 0 | tick frames mean {tick_mean:.3f} ms p95 2.05 ms p99 3.05 "
                            f"ms max 3.2 ms | nonaoi p95 2.05 ms p99 2.05 ms | aoi passes {passes} frames 5 mean 4.000 ms "
                            f"max 8.2 ms | other mean {other_mean:.3f} ms p95 0.55 ms | kit mean 0.812 ms p95 0.85 ms p99 "
                            f"0.85 ms max 2.0 ms | parts {ticks} sync 0.300 ms aoi 0.050 ms move 0.462 ms | late 0 mean "
                            f"0.000 ms max 0.0 ms 0.0 ms/s | send {ticks} mean 0.462 ms p95 0.65 ms p99 0.65 ms | physics "
                            f"{physics:.1f}/s mean 0.56 max 1 f2 0 | work {frames:.1f} ms/s busy 30.0% | budget "
                            f"{budget:.2f} ms | gc0 0 | outside 0")]
    if bins:
        lines.append(stamped(clock, f"Work bins 5.00 s: tick 40:{ticks - 5} 60:5 over:0 | nonaoi 40:{ticks - 5} over:0 | "
                                    f"other 10:{t0} over:0 | kit 16:{ticks} over:0 | send 12:{send or ticks} over:0"))
    return lines


def cell_lines(cid, owned=0, pup=None, pup_at=(2, 4, 6, 8, 10, 12), spawned=None, rates=("60", "0.03"), walk_steered=0,
               first=0, changes=None, drop=(), twice=(), add=(), **window_options):
    marks = {"own": f"Own cell from layout: {cid} of map ServerMeshTestMap, grid (0,0), world {WORLD[cid]}",
             "linked": f"Neighbour zone {'E' if cid == 'C' else 'C'} linked",
             "rates": f"Rates: fps {rates[0]} (MESH_RATES; kit 60) vSync 0 | tick 20 Hz (updateFps unchanged) | physics "
                      f"{rates[1]} s (project 0.03) | maxDeltaTime 0.333 | probe budget 16.67 ms | uncapped cells -",
             "aoi": f"AOI timing: installed on cell {cid} (MapNetworkManager(Clone)/TimedAoi): cellSize 20 maxObjects "
                    f"5000 updateInterval 0.1 s bufferedCells 1 1 1 visibleRange 300 | grid system ready",
             "band": "MESH_BAND=300: ghost band and visible range set to 300 m",
             "animation": "Server animation: updateAnimationAtServer 0 (MESH_RIG_SERVER_ANIMATION; kit 1)"}
    if owned:
        marks["spawned"] = (f"Load generator: spawned {spawned or owned} synthetic players on cell {cid} over x [-300, 0] "
                            f"z [-300, 300] (seed 1, planned {owned})")
        marks["motion"] = "Load generator: motion kinematic (MESH_LOAD_MOTION), fight on (MESH_LOAD_FIGHT)"
        marks["walk"] = (f"Kinematic walk: {owned} synthetic players walk by transform at 0.90 x move speed; "
                         f"{walk_steered} stay steered (no grounded kit movement within 5.0 s)")
    lines = [stamped("09:59:05", text) for name, text in marks.items() if name not in drop
             for _ in range(2 if name in twice else 1)]
    for k in range(1, 13):
        if k > first:
            lines += window(at(5 * k), owned=owned, **dict(window_options, **(changes or {}).get(k, {})))
        lines.append(stamped(at(5 * k), THREADS))
        if pup is not None and k in pup_at:
            lines.append(stamped(at(5 * k), PUPPETS.format(**pup)))
    return lines + list(add)


def make_run(lines_by_cell):
    cells = {}
    with mock.patch.object(rigrun.r, "zone_lines", side_effect=lambda cell, *_: lines_by_cell[cell]):
        for cid, lines in lines_by_cell.items():
            cells[cid] = {"structural": rigrun.cell_window(cid, rigrun.r.MAP, SINCE, START, END),
                          "p0": p0cells.cell_record(cid, lines, START, END)}
    return {"since": SINCE, "start": START, "end": END, "cells": cells}


def presence_run(c=None, e=None):
    return make_run({"C": cell_lines("C", **dict({"owned": 60, "pup": IDLE_PUPPETS}, **(c or {}))),
                     "E": cell_lines("E", **dict({"pup": KINEMATIC_PUPPETS, "puppets": 60}, **(e or {})))})


def spec(**changes):
    base = {"family": "presence", "case": "main", "arm": "puppets-kinematic", "point": {"players": 60}, "repeat": 1,
            "seed": 1, "order": 1, "layout": "halves", "cells_started": ["C", "E"], "cells_read": ["C", "E"],
            "plan": {"C": 60}, "env": dict(ENV), "warmup": 45, "measure": 60, "rates": "60/0.03", "uncapped": [],
            "band": 300.0, "expect": {"puppets": {"E": 60}}, "key": "k"}
    base.update(changes)
    return base


class RunProblemsTests(unittest.TestCase):
    def test_refuses_a_family_without_checks(self):
        with self.assertRaisesRegex(ValueError, "no checks for family 'workerab'"):
            p0checks.run_problems(spec(family="workerab"), presence_run())

    def test_step_8b_families_are_refused(self):
        for family in p0results.FAMILIES_8B:
            with self.subTest(family), self.assertRaisesRegex(
                    ValueError, f"no checks for family '{family}' yet: it needs stage 0b, step 8b"):
                p0checks.run_problems(spec(family=family), presence_run())

    def test_refuses_an_unknown_layout(self):
        with self.assertRaisesRegex(ValueError, "layout 'thirds' is not one of"):
            p0checks.check_structure(spec(layout="thirds"), presence_run())

    def test_each_failure_alone(self):
        no_motion = {key: value for key, value in ENV.items() if key != "MESH_LOAD_MOTION"}
        cases = {
            "unlinked cell": ({}, {"drop": ("linked",)}, {}, ["C never linked with E"]),
            "second Own line": ({}, {"twice": ("own",)}, {}, ["C started 2 times"]),
            "wrong spawn": ({}, {"spawned": 59}, {}, ["C spawned [59], planned 60"]),
            "exception line": ({}, {"add": (stamped("10:00:30", "NullReferenceException: boom"),)}, {},
                               ["C logged 1 exception line(s)"]),
            "45 s coverage": ({}, {"first": 3}, {}, ["C's work windows cover 45 s of the 60 s measurement, need 48",
                                                     "C's Work ticks windows cover 45 s of the 60 s measurement, need 48"]),
            "missing bins line": ({}, {"changes": {5: {"bins": False}}}, {},
                                  ["C: Work ticks at 10:00:25 has no Work bins line"]),
            "bins total mismatch": ({}, {"changes": {5: {"send": 99}}}, {},
                                    ["C: Work bins at 10:00:25: send total 99 is not send samples = 100"]),
            "two Rates lines": ({}, {"twice": ("rates",)}, {}, ["C logged 2 Rates lines, expected one"]),
            "wrong fps": ({}, {"rates": ("30", "0.03")}, {}, ["C's Rates line states 30/0.03, not 60/0.03"]),
            "stray uncapped line": ({}, {"add": (stamped("09:59:05", "Rates: cell C uncapped (nominal 60 fps)"),)}, {},
                                    ["C logged 'Rates: cell C uncapped' but is not in MESH_UNCAPPED_CELLS"]),
            "no AOI install": ({}, {"drop": ("aoi",)}, {},
                               ["C logged no 'AOI timing: installed' line with MESH_AOI_TIMING=1"]),
            "3 AOI passes": ({}, {"changes": {4: {"passes": 3}}}, {},
                             ["C: AOI passes outside 4 to 6 in 1 of 12 windows (10:00:20 3)"]),
            "99 ticks with 1 dropped": ({}, {"changes": {6: {"ticks": 99, "dropped": 1}}}, {},
                                        ["C: 1 of 12 unsaturated windows missed 20 ticks/s ± 1 tick with none dropped "
                                         "(10:00:30: 99 of 100 ticks, 1 dropped)"]),
            "missing band line": ({}, {"drop": ("band",)}, {}, ["C logged no MESH_BAND line, expected 300 m"]),
            "motion line on a default arm": ({"env": no_motion}, {}, {},
                                             ["C logged a load motion line (motion kinematic, fight on) on a default arm"]),
            "2 stay steered": ({}, {"walk_steered": 2}, {},
                               ["C: kinematic owners fell back to the brain (2 stay steered)"]),
            "missing animation line": ({}, {"drop": ("animation",)}, {},
                                       ["C logged no Server animation line with MESH_RIG_SERVER_ANIMATION=0"]),
            "a puppet fallback": ({}, {}, {"pup": dict(KINEMATIC_PUPPETS, kinematic=59, steered=1)},
                                  ["E: puppets fell back to steering (grid 6)",
                                   "E: 1.0 steered puppets on average, expected 0"]),
            "kinematic count 3.3 % low": ({}, {}, {"pup": dict(KINEMATIC_PUPPETS, kinematic=58)},
                                          ["E: 58.0 kinematic puppets on average, expected 60 ± 3 %"]),
            "puppet players 3.3 % low": ({}, {}, {"puppets": 58},
                                         ["E: 58.0 puppet players on average, expected 60 ± 3 %"]),
            "frames/s 3 % low": ({"checks_family": "rates"}, {"frames": 291}, {},
                                 ["C: 58.20 frames/s, expected 60 ± 2 %"]),
            "two Puppets windows in 40 s": ({"measure": 40}, {}, {"pup_at": (6, 12)},
                                            ["E logged 2 Puppets window(s) in the measurement, need 3"]),
        }
        for name, (changes, c, e, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0checks.run_problems(spec(**changes), presence_run(c, e)), expected)

    def test_a_good_run_passes_every_family(self):
        run = presence_run()
        for family in set(p0checks.FAMILY_CHECKS) - set(p0results.FAMILIES_8B):
            with self.subTest(family):
                self.assertEqual(p0checks.run_problems(spec(checks_family=family, N=100), run),
                                 ["no Prewarm burst with n 100 inside the measurement (bursts: none)"]
                                 if family == "prewarm" else [])
        for family in p0results.FAMILIES_8A:
            with self.subTest(f"{family} checks"):
                self.assertEqual(p0checks.FAMILY_CHECKS[family][:len(p0checks.COMMON)], p0checks.COMMON)
                self.assertIn(p0checks.check_phantoms, p0checks.FAMILY_CHECKS[family])
                self.assertIn(p0checks.check_v_move, p0checks.FAMILY_CHECKS[family])

    def test_checks_family_selects_the_target_tuple(self):
        slow = presence_run({"frames": 291})
        self.assertEqual(p0checks.run_problems(spec(family="accept"), slow), [])
        self.assertEqual(p0checks.run_problems(spec(family="accept", checks_family="rates"), slow),
                         ["C: 58.20 frames/s, expected 60 ± 2 %"])
        self.assertNotIn(p0checks.check_motion, p0checks.FAMILY_CHECKS["abgeo"])
        self.assertIn(p0checks.check_puppets, p0checks.FAMILY_CHECKS["abgeo"])

    def test_check_rates_with_no_rates(self):
        plain = spec(rates=None, env={key: value for key, value in ENV.items() if key != "MESH_RATES"})
        self.assertEqual(p0checks.check_rates(plain, presence_run({"drop": ("rates",)}, {"drop": ("rates",)})), [])
        self.assertEqual(p0checks.check_rates(plain, presence_run({"drop": ("rates",)})),
                         ["E logged 1 Rates line(s) with no MESH_RATES set"])
        self.assertEqual(p0checks.check_frames(plain, presence_run({"frames": 100})), [])

    def test_check_rates_for_uncapped_cells(self):
        uncapped = stamped("09:59:05", "Rates: cell C uncapped (nominal 60 fps)")
        self.assertEqual(p0checks.check_rates(spec(uncapped=["C"]), presence_run({"add": (uncapped,)})), [])
        self.assertEqual(p0checks.check_rates(spec(uncapped=["C"]), presence_run()),
                         ["C is in MESH_UNCAPPED_CELLS but logged no 'Rates: cell C uncapped' line"])

    def test_check_structure(self):
        self.assertEqual(p0checks.check_structure(spec(cells_read=["C"], expect={"links": 8}), presence_run()),
                         ["C linked with 1 neighbour(s) (E), expected 8"])
        run = presence_run()
        del run["cells"]["E"]
        self.assertEqual(p0checks.check_structure(spec(), run), ["E was not read"])
        self.assertEqual(p0checks.check_structure(spec(layout=None, cells_read=["C"], plan={}), run),
                         ["C ran x/z/size (-300.0, -300.0, 300.0, 600.0), not the layout's (-100, -100, 200, 200)"])

    def test_check_animation_refuses_a_bad_value(self):
        with self.assertRaisesRegex(ValueError, "MESH_RIG_SERVER_ANIMATION '2' is not 0 or 1"):
            p0checks.check_animation(spec(env=dict(ENV, MESH_RIG_SERVER_ANIMATION="2")), presence_run())

    def test_no_puppet_checks_without_kinematic_puppets(self):
        steered = spec(env={key: value for key, value in ENV.items() if key != "MESH_PUPPET_MOTION"})
        self.assertEqual(p0checks.check_puppets(steered, presence_run(e={"pup": None})), [])
        self.assertEqual(p0checks.check_coverage(steered, presence_run(e={"pup": None})), [])


def record_0b(**windows):
    """A p0cells0b.cell_record_0b of cell C with the given window lists (empty otherwise)."""
    base = {name: [] for name in ("send", "sender", "phantom", "ships", "feeder", "sync_load", "monsters", "prewarm",
                                  "steps")}
    return dict(base, cell="C", **windows)


def phantom(clock, bound=200, sink=0, vmove_n=10, vmove_mean=100.0):
    return {"end": START + datetime.timedelta(seconds=clock), "seconds": 5.0, "bound_virtual": bound,
            "bound_sink": sink, "vmove_n": vmove_n, "vmove_mean": vmove_mean, "vmove_p95": int(vmove_mean)}


def monsters(clock, placed=200, alive=200):
    return {"end": START + datetime.timedelta(seconds=clock), "seconds": 5.0, "cell": "C", "placed": placed,
            "alive": alive, "awake": 190, "healed": 0, "respawned": 0, "gc0": 0}


def send(clock, spawns_per_s):
    return {"end": START + datetime.timedelta(seconds=clock), "seconds": 5.0,
            "types": [{"type": 3, "calls": 0, "bytes": 0, "calls_per_s": spawns_per_s, "kb_per_s": 0.0, "us": 0.0}]}


def burst(clock, n=100):
    return {"end": START + datetime.timedelta(seconds=clock), "cell": "C", "kind": "kinematic", "n": n,
            "per_frame": 0, "frames": 3, "spawn_ms": 12.5, "per_puppet_ms": 0.125, "after_frames": 120,
            "over_budget": 0, "budget_ms": 33.33, "worst_ms": 40.0, "gc0": 0}


def run_0b(c=None, e=None):
    cells = {"C": {"p0b": c if c is not None else record_0b()}}
    if e is not None:
        cells["E"] = {"p0b": e}
    return {"since": SINCE, "start": START, "end": END, "cells": cells}


class Stage0bCheckTests(unittest.TestCase):
    def test_check_phantoms(self):
        env = dict(ENV, MESH_PHANTOM="virtual:200:eye:C")
        cases = {
            "a MESH_PHANTOM that does not parse": (dict(ENV, MESH_PHANTOM="virtual:200:eye"), record_0b(), None,
                                                    ValueError),
            "no Phantom window": (env, record_0b(), {}, ["C logged no Phantom window in the measurement with "
                                                         "MESH_PHANTOM set"]),
            "a window short of P": (env, record_0b(phantom=[phantom(5), phantom(10, bound=180)]), {},
                                    ["C: 1 of 2 Phantom windows had other than 200 virtual phantoms bound "
                                     "(10:00:10 180)"]),
            "the step's P on a stepped row": (env, record_0b(phantom=[phantom(5, bound=100)]), {"step": 100}, []),
            "sink phantoms": (dict(ENV, MESH_PHANTOM="sink:20:ship:C"), record_0b(phantom=[phantom(5, 0, 20)]), {}, []),
            "every phantom bound": (env, record_0b(phantom=[phantom(5), phantom(10)]), {}, []),
            "no phantoms": (ENV, record_0b(), {}, []),
        }
        for name, (value, rec, point, expected) in cases.items():
            with self.subTest(name):
                case = spec(family="observed", env=value, point=dict(point or {}), cells_read=["C", "E"])
                run = run_0b(rec, record_0b())   # E binds no phantoms: MESH_PHANTOM names C only
                if expected is ValueError:
                    with self.assertRaisesRegex(ValueError, "MESH_PHANTOM: 'virtual:200:eye' has no cell field"):
                        p0checks.check_phantoms(case, run)
                else:
                    self.assertEqual(p0checks.check_phantoms(case, run), expected)

    def test_check_v_move(self):
        cases = {
            "no sampled window": (record_0b(phantom=[phantom(5, vmove_n=0)]),
                                  ["no Phantom window sampled V move, expected a mean of at least 121.6 "
                                   "(0.95 x 128)"]),
            "below 0.95 x V": (record_0b(phantom=[phantom(5, vmove_mean=121.5)]),
                               ["V move mean 121.5 is below 121.6 (0.95 x the expected 128)"]),
            "at 0.95 x V, weighted by samples": (
                record_0b(phantom=[phantom(5, vmove_n=30, vmove_mean=122.0), phantom(10, vmove_n=10,
                                                                                     vmove_mean=120.4)]), []),
        }
        for name, (rec, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0checks.check_v_move(spec(family="targets", expected_v=128), run_0b(rec)), expected)
        self.assertEqual(p0checks.check_v_move(spec(family="targets"), run_0b()), [])

    def test_check_monsters(self):
        cases = {
            "no Monsters window": (record_0b(), None, ["no Monsters window in the measurement, expected 200 placed "
                                                       "monsters"]),
            "too few placed": (record_0b(monsters=[monsters(5, 150, 150)]), record_0b(monsters=[monsters(5, 40, 40)]),
                               ["Monsters windows place 190 monsters (C 150, E 40), expected 200"]),
            "one died": (record_0b(monsters=[monsters(5), monsters(10, alive=199)]), None,
                         ["C: 1 of 2 Monsters windows had other than every placed monster alive (10:00:10 199 of "
                          "200)"]),
            "placed over two cells, all alive": (record_0b(monsters=[monsters(5, 150, 150)]),
                                                 record_0b(monsters=[monsters(5, 50, 50)]), []),
        }
        for name, (c, e, expected) in cases.items():
            with self.subTest(name):
                case = spec(family="targets", placed=200, cells_read=["C", "E"])
                self.assertEqual(p0checks.check_monsters(case, run_0b(c, e)), expected)
        self.assertEqual(p0checks.check_monsters(spec(family="targets"), run_0b()), [])
        self.assertEqual(p0checks.check_monsters(spec(family="targets", placed=0), run_0b()), [])

    def test_check_prewarm(self):
        with self.assertRaisesRegex(ValueError, "a prewarm spec needs N"):
            p0checks.check_prewarm(spec(family="prewarm"), run_0b())
        rise = [send(25, 2.0), send(30, 5.0), send(35, 400.0)]
        cases = {
            "no burst": ({}, record_0b(), ["no Prewarm burst with n 100 inside the measurement (bursts: none)"]),
            "another n": ({}, record_0b(prewarm=[burst(32, 300)]),
                          ["no Prewarm burst with n 100 inside the measurement (bursts: C n 300)"]),
            "eyes without a rise": ({"eyes": True}, record_0b(prewarm=[burst(32)], send=rise[:2] + [send(35, 5.0)]),
                                    ["C: Send t3 (spawn) calls did not rise within 2 s of the Prewarm burst at "
                                     "10:00:32 (5.0/s before, 5.0/s after)"]),
            "eyes without a Send window after": ({"eyes": True}, record_0b(prewarm=[burst(32)], send=rise[:2]),
                                                 ["C: Send t3 (spawn) calls did not rise within 2 s of the Prewarm "
                                                  "burst at 10:00:32 (5.0/s before, no window after)"]),
            "the burst": ({}, record_0b(prewarm=[burst(32)]), []),
            "eyes and a rise": ({"eyes": True}, record_0b(prewarm=[burst(32)], send=rise), []),
        }
        for name, (fields, rec, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0checks.check_prewarm(spec(family="prewarm", N=100, **fields), run_0b(rec)),
                                 expected)


class DroppedTickTests(unittest.TestCase):
    """check_ticks on stage 0b families: drop-only windows tolerated up to a share (prewarm always); count misses fail."""

    @staticmethod
    def run_with(windows):
        triples = [{"end": datetime.datetime(2026, 9, 26, 10, 0, 5 * k), "seconds": 5.0,
                    "ticks": {"ticks": ticks, "dropped": dropped}} for k, (ticks, dropped) in enumerate(windows)]
        return {"cells": {"C": {"p0": {"triples": triples, "saturated": set(), "ticks": None}}}}

    def test_check_ticks_stage_0b(self):
        clean, dropped, short = (100, 0), (98, 2), (97, 0)   # (ticks run, dropped): 98 + 2 make the 100
        cases = {
            "a window off by more than one tick fails a 0b family": ("targets", [clean] * 7 + [short], 1),
            "drops in over a quarter of the windows fail a 0b family": ("targets", [clean] * 5 + [dropped] * 3, 1),
            "a drop fails a stage 0a family": ("rates", [clean] * 7 + [dropped], 1),
            "drops in a quarter of the windows pass a 0b family": ("targets", [clean] * 6 + [dropped] * 2, 0),
            "drops never fail a prewarm run": ("prewarm", [dropped] * 8, 0),
        }
        for name, (family, windows, problems) in cases.items():
            with self.subTest(name):
                case = spec(family=family, cells_read=["C"])
                self.assertEqual(len(p0checks.check_ticks(case, self.run_with(windows))), problems)


class ExclusionTests(unittest.TestCase):
    def test_exclusion(self):
        two = {"changes": {5: {"sat": 25.0}, 8: {"sat": 25.0}}}
        cases = {
            "two saturated windows": (spec(), two, "saturated in 2 of 12 windows on C"),
            "a saturated span": (spec(), {"sat": 19.9, "changes": {5: {"sat": 100.0}}}, "saturated (span) on C"),
            "one saturated window": (spec(), {"changes": {5: {"sat": 25.0}}}, None),
            "an uncapped arm": (spec(uncapped=["C"]), two, None),
            "none saturated": (spec(), {}, None),
        }
        for name, (value, c, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(p0checks.exclusion(value, presence_run(c)), expected)

    def test_one_saturated_window_is_dropped_not_a_problem(self):
        run = presence_run({"changes": {5: {"sat": 25.0}}})
        self.assertEqual(p0checks.run_problems(spec(), run), [])
        self.assertEqual(run["cells"]["C"]["p0"]["dropped"], [datetime.datetime(2026, 9, 26, 10, 0, 25)])


if __name__ == "__main__":
    unittest.main()
