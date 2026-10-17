"""Unit tests for rigtools (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py")."""
import datetime
import os
import tempfile
import time
import unittest

import rigtools


class StampTests(unittest.TestCase):
    def test_stamp_of(self):
        cases = {
            "no stamp": ("INFO Mesh - nothing here", None),
            "malformed date": ("INFO Mesh [2026-9-24 10:00:00] - short month", None),
            "short tag": (" INFO Mesh [2026-09-24 10:11:12] - hello", ("2026-09-24", "10:11:12")),
            "long tag past column 60": (" INFO M_MeshMapNetworkManager_MapNetworkManager-CE_C_LiteNetLibClient [2026-09-24 23:59:59] - x",
                                        ("2026-09-24", "23:59:59")),
            "first stamp wins": (" INFO Mesh [2026-09-24 01:02:03] - saw [2025-01-01 00:00:00] in a message", ("2026-09-24", "01:02:03")),
        }
        for name, (line, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(rigtools.stamp_of(line), expected)

    def test_stamp_datetime(self):
        cases = {
            "no stamp": ("plain", None),
            "stamp": (" INFO Mesh [2026-09-24 10:11:12] - x", datetime.datetime(2026, 9, 24, 10, 11, 12)),
        }
        for name, (line, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(rigtools.stamp_datetime(line), expected)


class LogNameTests(unittest.TestCase):
    def test_parse_log_name(self):
        cases = {
            "not a map log": ("Log_Central_2026-09-24.info.log", None),
            "allocate server": ("Log_Map(M)-Ch(C)-Alloc(True)-Instance()_2026-09-24.info.log", None),
            "unknown severity": ("Log_Map(M)-Ch(C)-Alloc(False)-Instance()_2026-09-24.debug.log", None),
            "static cell": ("Log_Map(ServerMeshTestMap)-Ch(C)-Alloc(False)-Instance()_2026-09-24.info.log",
                            ("ServerMeshTestMap", "C", "2026-09-24", "info")),
            "dynamic cell": ("Log_Map(ServerMeshTestMap)-Ch(C.1)-Alloc(False)-Instance()_2026-09-24.warn.log",
                             ("ServerMeshTestMap", "C.1", "2026-09-24", "warn")),
            "full path": (os.path.join("x", "Log_Map(M2)-Ch(C2)-Alloc(False)-Instance()_2026-09-25.error.log"),
                          ("M2", "C2", "2026-09-25", "error")),
        }
        for name, (path, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(rigtools.parse_log_name(path), expected)

    def test_discover_cells(self):
        with tempfile.TemporaryDirectory() as logs:
            for name in ("Log_Map(M)-Ch(C)-Alloc(False)-Instance()_2026-09-24.info.log",
                         "Log_Map(M)-Ch(C)-Alloc(False)-Instance()_2026-09-24.warn.log",
                         "Log_Map(M)-Ch(C.1)-Alloc(False)-Instance()_2026-09-25.info.log",
                         "Log_Map(M)-Ch(OLD)-Alloc(False)-Instance()_2026-09-01.info.log",
                         "Log_Central_2026-09-24.info.log"):
                open(os.path.join(logs, name), "w").close()
            cases = {
                "no matching day": ({"2026-01-01"}, []),
                "one day": ({"2026-09-24"}, [("C", "M")]),
                "two days": ({"2026-09-24", "2026-09-25"}, [("C", "M"), ("C.1", "M")]),
            }
            for name, (days, expected) in cases.items():
                with self.subTest(name):
                    self.assertEqual(rigtools.discover_cells(logs, days), expected)


class FreshnessTests(unittest.TestCase):
    def test_build_is_stale(self):
        with tempfile.TemporaryDirectory() as d:
            dll = os.path.join(d, "Assembly-CSharp.dll")
            old_src = os.path.join(d, "old.cs")
            new_src = os.path.join(d, "new.cs")
            for p in (old_src, dll, new_src):
                open(p, "w").close()
            now = time.time()
            os.utime(old_src, (now - 300, now - 300))
            os.utime(dll, (now - 200, now - 200))
            os.utime(new_src, (now - 100, now - 100))
            cases = {
                "missing build": (os.path.join(d, "none.dll"), [old_src], True),
                "source newer than build": (dll, [old_src, new_src], True),
                "build newer than sources": (dll, [old_src], False),
                "no sources": (dll, [], False),
            }
            for name, (build, sources, expected) in cases.items():
                with self.subTest(name):
                    self.assertEqual(rigtools.build_is_stale(build, sources), expected)

    def test_runtime_sources_skip_editor_folders(self):
        with tempfile.TemporaryDirectory() as assets:
            for rel in ("ServerMeshing/Mesh/A.cs", "ServerMeshing/Mesh/Editor/B.cs", "ServerMeshing/Tests/Editor/C.cs", "ServerMeshing/TestMap/D.cs"):
                path = os.path.join(assets, rel)
                os.makedirs(os.path.dirname(path), exist_ok=True)
                open(path, "w").close()
            got = sorted(os.path.relpath(p, assets).replace("\\", "/") for p in rigtools.runtime_sources(assets))
            self.assertEqual(got, ["ServerMeshing/Mesh/A.cs", "ServerMeshing/TestMap/D.cs"])


class StatusFileTests(unittest.TestCase):
    def test_read_kv(self):
        with tempfile.TemporaryDirectory() as d:
            missing = os.path.join(d, "missing.txt")
            path = os.path.join(d, "status.txt")
            with open(path, "w", encoding="utf-8") as f:
                f.write("state=done\nno equals sign\nfailure=a\nfailure=b\nvalue=x=y\n")
            cases = {
                "missing file": (missing, {}),
                "parsed": (path, {"state": "done", "failure": ["a", "b"], "value": "x=y"}),
            }
            for name, (p, expected) in cases.items():
                with self.subTest(name):
                    self.assertEqual(rigtools.read_kv(p), expected)

    def test_read_status_file(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "PlayStatus.txt")
            self.assertEqual(rigtools.read_status_file(path, pause=0), {})
            cases = {
                "torn: empty": ("", {}),
                "torn: cut before the key": ("time=10:00:00\n", {"time": "10:00:00"}),
                "whole": ("time=10:00:00\nisPlaying=True\n", {"time": "10:00:00", "isPlaying": "True"}),
            }
            for name, (content, expected) in cases.items():
                with self.subTest(name):
                    with open(path, "w", encoding="utf-8") as f:
                        f.write(content)
                    self.assertEqual(rigtools.read_status_file(path, tries=3, pause=0), expected)

    def test_as_list(self):
        cases = {"none": (None, []), "single": ("a", ["a"]), "list": (["a", "b"], ["a", "b"])}
        for name, (value, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(rigtools.as_list(value), expected)


class StatsLineTests(unittest.TestCase):
    LINE = (" INFO Mesh [2026-09-24 10:00:00] - Mesh stats: players=3 owned=12 drops=1 statics=2 (0+1 in a band) "
            "ghosts=4p/5m/0d/2s pendingAcks=0 reserved=0 | link out 12.5 kB/s 40 pkt/s, in 3.0 kB/s 9 pkt/s | handoffs 0 | "
            "fps 60 ghostSteer=none teleports=0p/0o | frame mean 16.8 ms max 20.1 ms cpu 34% mem 210 MB clients=3 "
            "clientOut=4.5 kB/s (60 pkt/s) | work mean 2.10 ms p95 3.25 ms max 9.0 ms wait 14.60 ms util 13% sat 0% probe=Valid"
            " | links E=1.0kB/s")

    def test_parse_stats_line(self):
        cases = {
            "not a stats line": (" INFO Mesh [2026-09-24 10:00:00] - hello", None),
            "no work suffix": (" INFO Mesh [2026-09-24 10:00:00] - Mesh stats: players=1 owned=2", {"players": 1, "owned": 2}),
        }
        for name, (line, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(rigtools.parse_stats_line(line), expected)

    def test_parse_full_line(self):
        got = rigtools.parse_stats_line(self.LINE)
        self.assertEqual(got["owned"], 12)
        self.assertEqual((got["ghost_players"], got["ghost_monsters"], got["ghost_statics"]), (4, 5, 2))
        self.assertEqual((got["frame_mean"], got["cpu"], got["mem"], got["clients"]), (16.8, 34, 210, 3))
        self.assertEqual(got["link_out"], 12.5)
        self.assertEqual((got["work_mean"], got["work_p95"], got["wait_mean"], got["util"], got["probe"]), (2.1, 3.25, 14.6, 13, "Valid"))

    def test_the_tools_own_regexes_still_match(self):
        # aggload / cellcap / loadtest parse these spans with their own compiled patterns; the work and link suffixes
        # appended in 0.71 must not break them. Imported, not re-typed, so a change in a tool is caught here.
        import aggload
        import cellcap
        import loadtest
        cases = {
            "aggload COST": (aggload.COST, "16.8"),
            "aggload PLAYERS": (aggload.PLAYERS, "3"),
            "aggload LINK": (aggload.LINK, "12.5"),
            "cellcap COST": (cellcap.COST, "16.8"),
            "cellcap OWNED": (cellcap.OWNED, "12"),
            "cellcap LINK": (cellcap.LINK, "12.5"),
            "loadtest COST": (loadtest.COST, "16.8"),
            "loadtest PLAYERS": (loadtest.PLAYERS, "3"),
            "loadtest GHOSTS": (loadtest.GHOSTS, "4"),
        }
        for name, (pattern, first_group) in cases.items():
            with self.subTest(name):
                m = pattern.search(self.LINE)
                self.assertIsNotNone(m)
                self.assertEqual(m.group(1), first_group)


class ManifestTests(unittest.TestCase):
    def test_in_manifest(self):
        cases = {
            "texture": ("Assets/A/b.png", False),
            "meta": ("Assets/A/b.cs.meta", False),
            "runtime script": ("Assets/ServerMeshing/Mesh/A.cs", True),
            "editor script": ("Assets/ServerMeshing/Mesh/Editor/A.cs", False),
            "editor asmdef": ("Assets/Kit/Editor/Kit.Editor.asmdef", False),
            "asset in an editor folder": ("Assets/Kit/Editor/Settings.asset", True),
            "scene": ("Assets/ServerMeshing/TestMap/Scenes/Map.unity", True),
            "upper-case extension": ("Assets/X/Y.PREFAB", True),
            "project settings": ("ProjectSettings/ProjectSettings.asset", True),
        }
        for name, (rel, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(rigtools.in_manifest(rel), expected)

    def test_build_staleness(self):
        with tempfile.TemporaryDirectory() as root:
            def write(rel, text):
                path = os.path.join(root, rel)
                os.makedirs(os.path.dirname(path), exist_ok=True)
                with open(path, "w", encoding="utf-8") as f:
                    f.write(text)
            write("Assets/Mesh/A.cs", "a")
            write("Assets/Mesh/Editor/E.cs", "e")
            write("ProjectSettings/P.asset", "p")
            manifest = os.path.join(root, "manifest.txt")
            self.assertTrue(rigtools.build_staleness(root, manifest)[0].startswith("the build has no manifest"))

            current = rigtools.source_manifest(root)
            self.assertEqual(sorted(current), ["Assets/Mesh/A.cs", "ProjectSettings/P.asset"])
            with open(manifest, "w", encoding="utf-8") as f:
                f.write("".join(f"{digest} {rel}\n" for rel, digest in sorted(current.items())))
            self.assertEqual(rigtools.build_staleness(root, manifest), [])

            write("Assets/Mesh/Editor/E.cs", "editor edits do not matter")
            self.assertEqual(rigtools.build_staleness(root, manifest), [])
            write("Assets/Mesh/A.cs", "changed")
            write("Assets/Mesh/B.cs", "new")
            os.remove(os.path.join(root, "ProjectSettings", "P.asset"))
            reasons = rigtools.build_staleness(root, manifest)
            self.assertEqual(len(reasons), 3)
            self.assertTrue(reasons[0].startswith("1 changed") and "Assets/Mesh/A.cs" in reasons[0])
            self.assertTrue(reasons[1].startswith("1 added") and "Assets/Mesh/B.cs" in reasons[1])
            self.assertTrue(reasons[2].startswith("1 removed") and "ProjectSettings/P.asset" in reasons[2])

    def test_manifest_matches_the_build_format(self):
        # ServerMeshRigBuild writes lower-case hex SHA-1, a space, the forward-slash path, one entry per line.
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "m.txt")
            with open(path, "w", encoding="utf-8") as f:
                f.write("0123456789abcdef0123456789abcdef01234567 Assets/A B/c.cs\n\n")
            self.assertEqual(rigtools.read_manifest(path), {"Assets/A B/c.cs": "0123456789abcdef0123456789abcdef01234567"})

    def test_normalized_bytes(self):
        cases = {
            "lone CR kept": (b"a\rb", b"a\rb"),
            "CR before CRLF kept": (b"a\r\r\nb", b"a\r\nb"),
            "empty": (b"", b""),
            "LF untouched": (b"a\nb\n", b"a\nb\n"),
            "CRLF to LF": (b"a\r\nb\r\n", b"a\nb\n"),
        }
        for name, (data, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(rigtools.normalized_bytes(data), expected)

    def test_line_end_change_is_not_stale(self):
        with tempfile.TemporaryDirectory() as root:
            path = os.path.join(root, "Assets", "T.asset")
            os.makedirs(os.path.dirname(path))
            with open(path, "wb") as f:
                f.write(b"%YAML 1.1\r\nm_Width: 256\r\n")
            manifest = os.path.join(root, "manifest.txt")
            with open(manifest, "w", encoding="utf-8") as f:
                f.write("".join(f"{digest} {rel}\n" for rel, digest in rigtools.source_manifest(root).items()))
            with open(path, "wb") as f:
                f.write(b"%YAML 1.1\nm_Width: 256\n")
            self.assertEqual(rigtools.build_staleness(root, manifest), [])
            with open(path, "wb") as f:
                f.write(b"%YAML 1.1\nm_Width: 512\n")
            self.assertEqual(len(rigtools.build_staleness(root, manifest)), 1)


class LoadPlanTests(unittest.TestCase):
    GRID = [("A", (-100, -100, 100, 200)), ("B", (0, -100, 100, 200))]

    def test_rect_overlap(self):
        cases = {
            "disjoint": ((0, 0, 10, 10), (20, 20, 5, 5), 0.0),
            "touching": ((0, 0, 10, 10), (10, 0, 5, 5), 0.0),
            "contained": ((0, 0, 10, 10), (2, 2, 3, 3), 9.0),
            "partial": ((0, 0, 10, 10), (5, 5, 10, 10), 25.0),
        }
        for name, (a, b, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(rigtools.rect_overlap(a, b), expected)

    def test_load_plan_errors(self):
        cases = {
            "negative total": (self.GRID, -1, None),
            "zero half-size": (self.GRID, 10, (0, 0, 0)),
            "crowd off the map": (self.GRID, 10, (1000, 1000, 5)),
        }
        for name, (cells, total, area) in cases.items():
            with self.subTest(name):
                with self.assertRaises(ValueError):
                    rigtools.load_plan(cells, total, area)

    def test_load_plan(self):
        cases = {
            "spread evenly": (self.GRID, 10, None, {"A": 5, "B": 5}),
            "remainder goes to the largest share": ([("A", (0, 0, 60, 10)), ("B", (60, 0, 40, 10))], 5, None, {"A": 3, "B": 2}),
            "crowd inside one cell": (self.GRID, 7, (-50, 0, 10), {"A": 7}),
            "crowd straddling the cut": (self.GRID, 8, (0, 0, 20), {"A": 4, "B": 4}),
            "zero total": (self.GRID, 0, None, {}),
        }
        for name, (cells, total, area, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(rigtools.load_plan(cells, total, area), expected)

    def test_load_plan_crowd_uses_the_drawn_ground(self):
        cells = [("C", (-300, -300, 300, 600)), ("E", (0, -300, 300, 600))]
        with self.assertRaises(ValueError):
            rigtools.load_plan(cells, 100, (0, 310, 10.3))   # meets the ground only in its outer 0.5 m
        self.assertEqual(rigtools.load_plan(cells, 100, (0, 290, 10)), {"C": 50, "E": 50})

    def test_rects_touch(self):
        cases = {
            "apart": ((0, 0, 10, 10), (11, 0, 10, 10), False),
            "diagonal gap": ((0, 0, 10, 10), (10.5, 10.5, 5, 5), False),
            "shared edge": ((0, 0, 10, 10), (10, 0, 10, 10), True),
            "shared corner": ((0, 0, 10, 10), (10, 10, 10, 10), True),
            "overlap": ((0, 0, 10, 10), (5, 5, 10, 10), True),
        }
        for name, (a, b, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(rigtools.rects_touch(a, b), expected)
                self.assertEqual(rigtools.rects_touch(b, a), expected)

    def test_plan_env(self):
        self.assertEqual(rigtools.plan_env({"E": 2, "C": 3}), "C:3,E:2")


def work_line(stamp, seconds=5.0, frames=300, work=4.2, wait=12.466, sat=0.0, probe="Valid", owned=75, monsters=40, budget=16.67):
    return (f" INFO Mesh [{stamp}] - Work window {seconds:.2f} s: frames {frames} work mean {work:.3f} ms p95 6.10 ms "
            f"max 9.3 ms wait {wait:.3f} ms period 16.667 ms budget {budget:.2f} ms sat {sat:.1f}% probe={probe} "
            f"| owned players {owned} monsters {monsters} | puppets players 20 monsters 5 | clients 0 cpu 31%")


class WorkWindowTests(unittest.TestCase):
    def test_parse_work_window(self):
        cases = {
            "a stats line": (" INFO Mesh [2026-09-25 10:00:05] - Mesh stats players=1 owned=2", None),
            "no stamp": (work_line("2026-09-25 10:00:05").replace("[2026-09-25 10:00:05]", ""), None),
            "a window": (work_line("2026-09-25 10:00:05"), {
                "end": datetime.datetime(2026, 9, 25, 10, 0, 5), "seconds": 5.0, "frames": 300, "work_mean": 4.2,
                "work_p95": 6.1, "work_max": 9.3, "wait_mean": 12.466, "period_mean": 16.667, "budget": 16.67,
                "sat": 0.0, "probe": "Valid", "owned_players": 75, "owned_monsters": 40, "puppet_players": 20,
                "puppet_monsters": 5, "clients": 0, "cpu": 31}),
        }
        for name, (line, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(rigtools.parse_work_window(line), expected)

    def test_parse_own_cell(self):
        cases = {
            "another line": (" INFO Mesh [2026-09-25 10:00:05] - Neighbour zone E linked", None),
            "grid cell": (" INFO Mesh [2026-09-25 10:00:05] - Own cell from channel id: C of map ServerMeshTestMap, grid (1,1), "
                          "world x [-100, 100) z [-100, 100), 8 neighbours", ("C", "ServerMeshTestMap", (-100.0, -100.0, 200.0, 200.0))),
            "coarse override": (" INFO Mesh [2026-09-25 10:00:05] - Own cell from channel id: C of map ServerMeshTestMap, grid (0,0), "
                                "world x [-300, 300) z [-300, 300), 0 neighbours", ("C", "ServerMeshTestMap", (-300.0, -300.0, 600.0, 600.0))),
        }
        for name, (line, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(rigtools.parse_own_cell(line), expected)

    def test_aggregate_windows(self):
        start, end = datetime.datetime(2026, 9, 25, 10, 0, 0), datetime.datetime(2026, 9, 25, 10, 1, 0)
        def w(stamp, **kw):
            return rigtools.parse_work_window(work_line(stamp, **kw))

        cases = {
            "no windows": ([], None),
            "begins before start": ([w("2026-09-25 10:00:03")], None),
            "ends after end": ([w("2026-09-25 10:01:01")], None),
            "one second of slack at start": ([w("2026-09-25 10:00:04", work=2.0)], {"windows": 1, "work_mean": 2.0}),
            "frame-weighted": ([w("2026-09-25 10:00:10", frames=100, work=2.0), w("2026-09-25 10:00:15", frames=300, work=6.0)],
                               {"windows": 2, "frames": 400, "work_mean": 5.0}),
            "states in time order": ([w("2026-09-25 10:00:15", probe="Valid"), w("2026-09-25 10:00:10", probe="Unknown")],
                                     {"probe": ["Unknown", "Valid"]}),
            "util from the budget": ([w("2026-09-25 10:00:10", work=8.335, budget=16.67)], {"util": 0.5}),
        }
        for name, (windows, expected) in cases.items():
            with self.subTest(name):
                got = rigtools.aggregate_windows(windows, start, end)
                if expected is None:
                    self.assertIsNone(got)
                    continue
                for key, value in expected.items():
                    if isinstance(value, float):
                        self.assertAlmostEqual(got[key], value, places=6)
                    else:
                        self.assertEqual(got[key], value)

    def test_is_saturated(self):
        cases = {
            "many frames without a wait": ({"sat": 25.0, "util": 0.5}, True),
            "near the budget": ({"sat": 0.0, "util": 0.95}, True),
            "at the share limit": ({"sat": 20.0, "util": 0.5}, False),
            "idle": ({"sat": 0.0, "util": 0.1}, False),
        }
        for name, (stats, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(rigtools.is_saturated(stats), expected)


class VerdictTests(unittest.TestCase):
    def test_rotate(self):
        cases = {
            "empty": ([], 2, []),
            "no turn": (["c", "h", "g"], 0, ["c", "h", "g"]),
            "one turn": (["c", "h", "g"], 1, ["h", "g", "c"]),
            "wraps": (["c", "h", "g"], 5, ["g", "c", "h"]),
        }
        for name, (items, k, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(rigtools.rotate(items, k), expected)

    def test_point_verdict(self):
        cases = {
            "too few runs": ([0.5], 2, "INSUFFICIENT"),
            "none": ([], 1, "INSUFFICIENT"),
            "straddles": ([0.7, 0.8], 2, "MARGINAL"),
            "all over": ([0.76, 0.9], 2, "NO-GO"),
            "all at or under": ([0.75, 0.6], 2, "GO"),
        }
        for name, (ratios, min_runs, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(rigtools.point_verdict(ratios, 0.75, min_runs), expected)

    def test_overall_verdict(self):
        cases = {
            "no points": ([], "NO VERDICT"),
            "a no-go decides": (["GO", "INSUFFICIENT", "NO-GO"], "NO-GO"),
            "a point without a verdict": (["GO", "INSUFFICIENT"], "NO VERDICT"),
            "a saturated point": (["GO", "SATURATED"], "NO VERDICT"),
            "marginal": (["GO", "MARGINAL"], "MARGINAL"),
            "all go": (["GO", "GO"], "GO"),
        }
        for name, (verdicts, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(rigtools.overall_verdict(verdicts), expected)

    def test_crossing_rate(self):
        with self.assertRaises(ValueError):
            rigtools.crossing_rate(100, 0, 600, 600)
        cases = {
            "no players": ((0, 600, 600, 600), 0.0),
            "250 over 600 m, a 600 m cut": ((250, 600, 600, 600), 250 / 360000 * 600 * 12.5 * 2 / 3.141592653589793 / 4),
        }
        for name, (args, expected) in cases.items():
            with self.subTest(name):
                self.assertAlmostEqual(rigtools.crossing_rate(*args), expected, places=9)


class LayoutGradeTests(unittest.TestCase):
    OWN = " INFO Mesh [2026-09-25 10:00:00] - Own cell from channel id: C of map M, grid (1,1), world x [-100, 100) z [-100, 100), 8 neighbours"
    OWN_FROM_LAYOUT = " INFO Mesh [2026-09-25 10:00:01] - Own cell from layout: C.1 of map M, grid (-1,-1), world x [0, 100) z [-100, 100), 5 neighbours"
    PLAYER = " INFO MeshTrace [2026-09-25 10:00:05] - 10:00:05.123 S player registered MeshTester2 conn=0 at (1, 0, 0) (data (1, 0, 0))"
    OTHER = " INFO Mesh [2026-09-25 10:00:03] - Mesh stats: nothing to see"
    NEIGHBOURS = " INFO Mesh [2026-09-25 10:00:04] - Layout 1a2b/3 neighbours applied: C2 band E, E.1"

    @staticmethod
    def applied(incarnation="1a2b", version=1):
        return f" INFO Mesh [2026-09-25 10:00:02] - Layout {incarnation}/{version} applied: C owns x[-100,100) z[-100,100) interest x[-100,100) z[-100,100), 8 targets"

    def test_parse_cell_modes_malformed(self):
        cases = {
            "no colon": "M",
            "unknown mode": "M:Weird",
            "number for a mode": "M:1",
            "map twice": "M:Dynamic,M:Static",
            "no map": ":Dynamic",
            "all twice": "all:Dynamic,ALL:Static",
        }
        for name, value in cases.items():
            with self.subTest(name):
                with self.assertRaises(ValueError):
                    rigtools.parse_cell_modes(value)

    def test_parse_cell_modes(self):
        cases = {
            "empty": ("", {}),
            "none": (None, {}),
            "all": ("all:Dynamic", {"all": "Dynamic"}),
            "two maps, trimmed": (" A:Dynamic , B:Static ", {"A": "Dynamic", "B": "Static"}),
            "all with a map": ("All:Dynamic,M:Static", {"all": "Dynamic", "M": "Static"}),
            "mode ignores case": ("M:dYnAmIc", {"M": "Dynamic"}),
        }
        for name, (value, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(rigtools.parse_cell_modes(value), expected)

    def test_dynamic_maps(self):
        maps = {"A", "B"}
        cases = {
            "nothing set": ("", {}, set()),
            "the asset's own mode": ("", {"A": "Dynamic"}, {"A"}),
            "all": ("all:Dynamic", {}, {"A", "B"}),
            "a map wins over all": ("all:Dynamic,B:Static", {}, {"A"}),
            "the variable wins over the asset": ("A:Static", {"A": "Dynamic"}, set()),
            "an unknown map is ignored": ("Z:Dynamic", {}, set()),
        }
        for name, (value, asset, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(rigtools.dynamic_maps(value, asset, maps), expected)
        with self.assertRaises(ValueError):
            rigtools.dynamic_maps("A", {}, maps)

    def test_layout_apply_violations(self):
        o, p, x = self.OWN, self.PLAYER, self.OTHER
        a = self.applied
        cases = {
            "dynamic, none applied": ([o, x, p], True, 1),
            "dynamic, two from one coordinator": ([o, a(), a(version=2), p], True, 1),
            "dynamic, applied after a player registered": ([o, p, a()], True, 1),
            "static, one applied": ([o, a(), p], False, 1),
            "dynamic, a Static-neighbour apply line": ([o, a(), self.NEIGHBOURS, p], True, 1),
            "dynamic, one per lifetime across a crash restart": ([o, a(), p, o, a(), p], True, 0),
            "dynamic, a central restart adds one": ([o, a("1a"), p, a("2b")], True, 0),
            "dynamic, applied first": ([o, x, a(), p], True, 0),
            "static, none applied": ([o, x, p], False, 0),
            "static, band-neighbour applies only (Slice 2b)": ([o, self.NEIGHBOURS, p, self.NEIGHBOURS], False, 0),
            "a leading segment from an earlier process is lenient": ([p, a(), a(), o, a(), p], True, 0),
            "no lifetime at all": ([x, p], True, 0),
            "a child's own-cell line does not start a lifetime": ([o, a(), self.OWN_FROM_LAYOUT, p], True, 0),
        }
        for name, (lines, dynamic, problems) in cases.items():
            with self.subTest(name):
                self.assertEqual(len(rigtools.layout_apply_violations("C", lines, dynamic)), problems)

    def test_op_line(self):
        cases = {
            "the probe's automatic splits": ("Work probe INVALID: ... it takes no part in automatic splits", False),
            "a merge": ("Merge of C.1 into C committed", True),
            "a retire": ("Retire asked for cell C.1", True),
            "retired lower case": ("Link to retired cell C.1 closed", False),
            "a split": ("Split of C refused: best peak 0.93", True),
        }
        for name, (line, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(bool(rigtools.OP_LINE.search(line)), expected)

    def test_layout_line_formats_match(self):
        self.assertTrue(rigtools.LAYOUT_APPLIED.search(self.applied("0"))
                        and rigtools.LAYOUT_GATE_WARNING.search("Layout gate: cell C has had no answer to LayoutHello for 10s; ...")
                        and rigtools.PLAYER_REGISTERED.search(self.PLAYER))
        # Slice 2b decision 14: the Static neighbour's line is distinct from a dynamic cell's apply, and neither an op
        m = rigtools.NEIGHBOURS_APPLIED.search(self.NEIGHBOURS)
        self.assertEqual(m.groups() if m else None, ("1a2b", "3", "C2", "E, E.1"))
        self.assertIsNone(rigtools.LAYOUT_APPLIED.search(self.NEIGHBOURS))
        self.assertIsNone(rigtools.NEIGHBOURS_APPLIED.search(self.applied()))
        self.assertIsNone(rigtools.OP_LINE.search(self.NEIGHBOURS))

    def test_asset_cell_modes_and_assets_with_script(self):
        guid = rigtools.REGISTRY_SCRIPT_GUID
        registry = (
            "MonoBehaviour:\n"
            f"  m_Script: {{fileID: 11500000, guid: {guid}, type: 3}}\n"
            "  maps:\n"
            "  - mapId: Old\n"
            "    cellSize: 200\n"
            "    cellRects:\n"
            "    - id: C\n"
            "  - mapId: New\n"
            "    cellMode: 1\n"
            "  - mapId: Plain\n"
            "    cellMode: 0\n")
        other = "MonoBehaviour:\n  m_Script: {fileID: 11500000, guid: 0000000000000000000000000000beef, type: 3}\n"
        with tempfile.TemporaryDirectory() as d:
            os.makedirs(os.path.join(d, "Sub"))
            path = os.path.join(d, "Sub", "World.asset")
            with open(path, "w", encoding="utf-8") as f:
                f.write(registry)
            with open(os.path.join(d, "Other.asset"), "w", encoding="utf-8") as f:
                f.write(other)
            with open(os.path.join(d, "Notes.txt"), "w", encoding="utf-8") as f:
                f.write(registry)

            self.assertEqual(rigtools.assets_with_script(d, guid), [path])
            self.assertEqual(rigtools.asset_cell_modes(path), {"Old": "Static", "New": "Dynamic", "Plain": "Static"})


if __name__ == "__main__":
    unittest.main()
