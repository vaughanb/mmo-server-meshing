"""Unit tests for rigmerge, Slice 2c's merge graders (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py")."""
import unittest

import rigmerge
import rigsplit

MAP = "ServerMeshTestMap"
C = (-100.0, -100.0, 200.0, 200.0)
KEPT, GIVEN = (-100.0, -100.0, 100.0, 200.0), (0.0, -100.0, 100.0, 200.0)
SPLIT = rigsplit.Split(MAP, "C", C, "x", 0.0, "+", "C.1")
SPLIT_PAIRS = {"e1": "1a2b/2", "e2": "1a2b/3", "e3": "1a2b/4"}
MERGE_PAIRS = {"e1": "1a2b/5", "e2": "1a2b/6", "e3": "1a2b/7", "tidy": "1a2b/8"}
STAMP = "[2026-10-17 10:00:00]"


def info(text):
    return f" INFO Mesh {STAMP} - {text}"


def warn(text):
    return f" WARN Mesh {STAMP} - {text}"


def applied(version, owned, interest, cell="C"):
    return info(f"Layout 1a2b/{version} applied: {cell} owns {rigsplit.format_rect(owned)} "
                f"interest {rigsplit.format_rect(interest)}, 8 targets")


class MergeStatusTests(unittest.TestCase):
    DONE = ["requested C C.1", "e1 broadcast 1a2b/5", "e1 gate open", "e2 staged", "e2 acked", "e2 broadcast 1a2b/6",
            "drain gate open", "retire sent", "C.1 exited", "channel C.1 removed", "e3 broadcast 1a2b/7",
            "tidy broadcast 1a2b/8", "done"]

    def lines(self, states, seq="9", op="merge-2"):
        return [f"{seq} {s}" if s.startswith(("Layout ", "Split ")) else f"{seq} Merge {op}: {s}" for s in states]

    def test_parse_merge_status(self):
        cases = {
            # failures first
            "refused": (["Layout op refused: merge of two children (Slice 3)"], "refused",
                        {"refused": "merge of two children (Slice 3)", "op": None}),
            "aborted and reverted": (self.DONE[:3] + ["aborted e1 gate shut", "reverted broadcast 1a2b/6"], "aborted",
                                     {"aborted": "e1 gate shut", "reverted": True, "pairs": {"e1": "1a2b/5",
                                                                                             "reverted": "1a2b/6"}}),
            "frozen": (self.DONE[:11] + ["frozen e3 ack timeout"], "frozen", {"frozen": "e3 ack timeout"}),
            "exited before drained": (self.DONE[:7] + ["C.1 exited before drained"] + self.DONE[9:], "done",
                                      {"exited_before": "C.1", "exited": None, "channel_removed": "C.1"}),
            "straggling": (self.DONE[:7] + ["drain straggling 92 s: characters=1 pendingAcks=1", "retire sent",
                                            "retire straggling 120 s"], None,
                           {"straggling": ["drain straggling 92 s: characters=1 pendingAcks=1",
                                           "retire straggling 120 s"], "retire_sent": 1}),
            "still running": (self.DONE[:4], None, {"pairs": {"e1": "1a2b/5"}, "done": False}),
            # passing
            "done": (self.DONE, "done", {"op": "merge-2", "requested": ("C", "C.1"), "pairs": MERGE_PAIRS,
                                         "retire_sent": 1, "exited": "C.1", "channel_removed": "C.1",
                                         "exited_before": None, "straggling": [], "other": []}),
        }
        for name, (states, outcome, fields) in cases.items():
            with self.subTest(name):
                status = rigmerge.parse_merge_status(self.lines(states), "9")
                self.assertEqual(rigmerge.merge_outcome(status), outcome)
                for key, value in fields.items():
                    self.assertEqual(status[key], value, key)

    def test_kind_filter(self):
        lines = (self.lines(self.DONE) + ["9 Split split-1: stragglers gone, settle broadcast 1a2b/9",
                                          "9 Merge retire: C.1 drained, quitting", "9 Layout command submitted: x",
                                          "8 Merge merge-1: aborted old seq"])
        status = rigmerge.parse_merge_status(lines, "9")
        self.assertEqual(rigmerge.merge_outcome(status), "done")
        self.assertEqual(status["other"], ["Split split-1: stragglers gone, settle broadcast 1a2b/9",
                                           "Merge retire: C.1 drained, quitting", "Layout command submitted: x"])
        # the split's parser records its kind and leaves a merge's lines alone
        split = rigsplit.parse_op_status(lines, "9")
        self.assertEqual((split["kind"], split["op"], split["done"]), ("Split", "split-1", False))
        self.assertIn("Merge merge-2: done", split["other"])

    def test_state_problems(self):
        ok = rigmerge.parse_merge_status(self.lines(self.DONE), "9")
        self.assertEqual(rigmerge.state_problems(ok, "C.1"), [])
        self.assertEqual(rigmerge.bad_states(ok), [])
        missing = rigmerge.parse_merge_status(self.lines([s for s in self.DONE if s != "e2 acked"]), "9")
        self.assertEqual(rigmerge.state_problems(missing, "C.1"), ["missing e2 acked"])
        swapped = self.DONE[:8] + [self.DONE[9], self.DONE[8]] + self.DONE[10:]
        order = rigmerge.state_problems(rigmerge.parse_merge_status(self.lines(swapped), "9"), "C.1")
        self.assertTrue(order and order[0].startswith("states out of order"))
        bad = rigmerge.parse_merge_status(self.lines(self.DONE[:7] + ["C.1 exited before drained",
                                                                       "drain straggling 30 s: monsters=2"]), "9")
        self.assertEqual(rigmerge.bad_states(bad), ["C.1 exited before drained", "drain straggling 30 s: monsters=2"])

    def test_command_line(self):
        self.assertEqual(rigmerge.command_line("7", "C", "C.1"), "7 merge C C.1")
        with self.assertRaises(ValueError):
            rigmerge.command_line("7", "C", "C.1 x")


class ProcessTests(unittest.TestCase):
    def test_channel_filter(self):
        self.assertEqual(rigmerge.channel_filter("C.1"), "*-channelId C.1 *")
        with self.assertRaises(ValueError):
            rigmerge.channel_filter("C*")
        spawned = r"D:\rig\kit-ce-project.exe -batchmode -nographics  -channelId C.1  -mapName ServerMeshTestMap -x"
        self.assertTrue(rigmerge.command_line_matches(spawned, "C.1"))
        self.assertTrue(rigmerge.command_line_matches(spawned.replace("channelId", "CHANNELID"), "C.1"))
        for other in ("C", "C.10", "C.2"):
            with self.subTest(other):
                self.assertFalse(rigmerge.command_line_matches(spawned.replace("C.1 ", other + " "), "C.1"))
        bot = r"D:\rig\kit-ce-project.exe -batchmode -meshAutoClient -meshChannel C.1 -logFile x"
        self.assertFalse(rigmerge.command_line_matches(bot, "C.1"))

    def test_process_count_script(self):
        exe = r"D:\Game Development\it's\Build\Server\kit-ce-project.exe"
        script = rigmerge.process_count_script(exe, "E.1")
        self.assertEqual(script.count("Get-CimInstance"), 1)
        self.assertIn("-Filter \"Name='kit-ce-project.exe'\"", script)
        self.assertIn("$_.ExecutablePath -eq 'D:\\Game Development\\it''s\\Build\\Server\\kit-ce-project.exe'", script)
        self.assertIn("$_.CommandLine -like '*-channelId E.1 *'", script)
        self.assertTrue(script.endswith(").Count"))

    def test_parse_count(self):
        self.assertEqual(rigmerge.parse_count(" 1\r\n"), 1)
        self.assertEqual(rigmerge.parse_count("0"), 0)
        for bad in ("", "x", "-1", None):
            with self.subTest(bad), self.assertRaises(ValueError):
                rigmerge.parse_count(bad)


class MapSideTests(unittest.TestCase):
    def test_prunes_and_link_lost(self):
        lines = [info("Retired zone C.1: link closed, 0 record(s) released"),
                 warn("Retired zone C.1: link closed, 2 record(s) released"),
                 info("Retired zone E.1: link closed, 0 record(s) released"),
                 info("Neighbour zone C.1 link lost (draining, retire expected)")]
        self.assertEqual(rigmerge.prunes(lines, "C.1"), [0, 2])
        self.assertEqual(rigmerge.prunes(lines, "C.10"), [])
        self.assertEqual(rigmerge.link_losts(lines, "C.1"), 1)
        self.assertTrue(rigmerge.is_warning("\ufeff" + lines[1]))
        self.assertFalse(rigmerge.is_warning(lines[0]))

    def test_disallowed_op_lines(self):
        allowed_child = [info("Merge retire: C.1 drained, quitting"), warn("Merge retire: C.1 still holds characters=1"),
                         info("Drain on C.1: 2 summoned monster(s) despawned"),
                         info("Drain on C.1: idle connection closed"),
                         warn("Enter game refused on draining cell C.1: abc123")]
        everywhere = [info("Retired zone C.1: link closed, 0 record(s) released"),
                      info("Neighbour zone C.1 link lost (draining, retire expected)"),
                      info("Layout 1a2b/7 applied: C owns x[-100,100) z[-100,100) interest x[-100,100) z[-100,100), 8 targets")]
        self.assertEqual(rigmerge.disallowed_op_lines("C.1", allowed_child + everywhere, "C.1"), [])
        self.assertEqual(rigmerge.disallowed_op_lines("C", everywhere, "C.1"), [])
        self.assertEqual(rigmerge.disallowed_op_lines("C", allowed_child, "C.1"), allowed_child)
        bad = [info("Retire for cell C.1 incarnation 3 reached cell C incarnation 4; ignored"),
               info("Split retire: C.1 retired before it owned anything"), info("Merge merge-2: frozen x"),
               info("Neighbour zone C.1 Retired early")]
        self.assertEqual(rigmerge.disallowed_op_lines("C.1", bad, "C.1"), bad)
        self.assertEqual(rigmerge.disallowed_op_lines("C", [info("Merged nothing; Splitting hairs")], "C.1"), [])
        # 2b's split drain line on the split's parent is the parent's own; another cell's Drain line is not.
        parent = info("Drain on C: gave away x[0,100) z[-100,100) to C.1")
        self.assertEqual(rigmerge.disallowed_op_lines("C", [parent], "C.1"), [])
        self.assertEqual(rigmerge.disallowed_op_lines("E", [parent], "C.1"), [parent])

    def test_applied_pair(self):
        lines = [applied(6, C, C), info("x"), info("Layout 1a2b/7 neighbours applied: C2 band SE,E,NE"), info("y")]
        self.assertTrue(rigmerge.applied_pair(lines, "1a2b/7"))
        self.assertTrue(rigmerge.applied_pair(lines, "1a2b/6"))
        self.assertFalse(rigmerge.applied_pair(lines, "1a2b/8"))
        self.assertFalse(rigmerge.applied_pair(lines, None))
        self.assertEqual(rigmerge.after_applied(lines, "1a2b/7"), lines[2:])
        self.assertIsNone(rigmerge.after_applied(lines, "1a2b/8"))

    def test_owner_moved_at(self):
        before = info("Ghost spawned Player bot (id) from zone E at (-180.00, 0.08, 75.00) (silenced 3)")
        changed = info("Ghost owner changed Player bot (id) zone E.1->E at (-180.00, 0.08, 75.00)")
        fresh = info("Ghost spawned Player bot (id) from zone E at (-179.00, 0.08, 75.50) (silenced 3)")
        self.assertTrue(rigmerge.owner_moved_at([before, changed], [], "bot", "E.1", "E", -180.0, 75.0))
        self.assertFalse(rigmerge.owner_moved_at([before, changed], [], "bot", "E", "E.1", -180.0, 75.0))
        self.assertFalse(rigmerge.owner_moved_at([before, changed], [], "bot", "E.1", "E", -100.0, 75.0))
        self.assertFalse(rigmerge.owner_moved_at([before], None, "bot", "E.1", "E", -180.0, 75.0))
        self.assertTrue(rigmerge.owner_moved_at([before, fresh], [fresh], "bot", "E.1", "E", -180.0, 75.0))

    def test_statics_held(self):
        lines = [info("Static handoff s1 zone C->C.1: promoted"), info("Static handoff s2 zone C->C.1: spawned"),
                 info("Static handoff s3 zone C->C.1: failed"), info("Static handoff s1 zone C.1->E: sent"),
                 info("Static handoff s9 zone E->C.1: promoted")]
        self.assertEqual(rigmerge.statics_held(lines, "C", "C.1"), 1)
        self.assertEqual(rigmerge.statics_held([], "C", "C.1"), 0)


class StoreTests(unittest.TestCase):
    def test_family_entries(self):
        entry = lambda i, base, m=MAP: {"id": i, "mapId": m, "baseId": base, "state": 0}   # noqa: E731
        store = {"broadcast": {"entries": [entry("E", "E"), entry("C2", "C2", "ServerMeshTestMap2")]},
                 "hasStaged": False, "staged": {"entries": [entry("C", "C")]}}
        self.assertEqual(rigmerge.family_entries(store, MAP, "C", ["C", "C.1"]), [])
        store["hasStaged"] = True
        self.assertEqual(rigmerge.family_entries(store, MAP, "C", ["C", "C.1"]), ["C"])
        store = {"broadcast": {"entries": [entry("c.1", "c"), entry("C.1", "C", "OtherMap")]}}
        self.assertEqual(rigmerge.family_entries(store, MAP, "C", ["C", "C.1"]), ["c.1"])
        self.assertEqual(rigmerge.family_entries({}, MAP, "C", ["C"]), [])


class ApplyTests(unittest.TestCase):
    def test_merge_expected_applies(self):
        p, n, any_ = C, rigsplit.NOTHING, rigsplit.ANY
        self.assertEqual(rigmerge.merge_expected_applies(SPLIT, "C", C, SPLIT_PAIRS, MERGE_PAIRS),
                         [(p, p, None), (p, p, "1a2b/2"), (KEPT, p, "1a2b/3"), (KEPT, KEPT, "1a2b/4"),
                          (KEPT, p, "1a2b/5"), (p, p, "1a2b/6"), (p, p, "1a2b/7"), (p, p, "1a2b/8")])
        self.assertEqual(rigmerge.merge_expected_applies(SPLIT, "C.1", any_, SPLIT_PAIRS, MERGE_PAIRS),
                         [(n, GIVEN, "1a2b/2"), (GIVEN, GIVEN, "1a2b/3"), (GIVEN, GIVEN, "1a2b/4"),
                          (GIVEN, GIVEN, "1a2b/5"), (n, GIVEN, "1a2b/6")])
        e = (100.0, -100.0, 200.0, 200.0)
        self.assertEqual([(o, i) for o, i, _ in rigmerge.merge_expected_applies(SPLIT, "E", e, {}, {})], [(e, any_)] * 8)
        self.assertEqual({pair for *_, pair in rigmerge.merge_expected_applies(SPLIT, "E", e, {}, {})}, {None})

    def test_merge_apply_violations(self):
        own = info(f"Own cell from channel id: C of map {MAP}, grid (1,1), world x [-100, 100) z [-100, 100), 8 neighbours")
        passing = [own, applied(1, C, C), applied(2, C, C), applied(3, KEPT, C), applied(4, KEPT, KEPT),
                   applied(5, KEPT, C), applied(6, C, C), applied(7, C, C), applied(8, C, C)]
        self.assertEqual(rigmerge.merge_apply_violations("C", passing, SPLIT, SPLIT_PAIRS, MERGE_PAIRS), [])
        no_tidy = rigmerge.merge_apply_violations("C", passing[:-1], SPLIT, SPLIT_PAIRS, MERGE_PAIRS)
        self.assertIn("C: 7 layout(s) applied, the merge expects 8", no_tidy)
        shifted = passing[:5] + [applied(6, KEPT, C)] + passing[6:]
        found = rigmerge.merge_apply_violations("C", shifted, SPLIT, SPLIT_PAIRS, MERGE_PAIRS)
        self.assertTrue(any("apply 5 is layout 1a2b/6, the merge expects 1a2b/5" in f for f in found), found)
        child = [info(f"Own cell from layout: C.1 of map {MAP}, grid (0,0), world x [0, 100) z [-100, 100), 5 neighbours"),
                 applied(2, None, GIVEN, "C.1"), applied(3, GIVEN, GIVEN, "C.1"), applied(4, GIVEN, GIVEN, "C.1"),
                 applied(5, GIVEN, GIVEN, "C.1"), applied(6, None, GIVEN, "C.1")]
        self.assertEqual(rigmerge.merge_apply_violations("C.1", child, SPLIT, SPLIT_PAIRS, MERGE_PAIRS), [])
        late = rigmerge.merge_apply_violations("C.1", child + [applied(7, None, None, "C.1")], SPLIT, SPLIT_PAIRS,
                                               MERGE_PAIRS)
        self.assertIn("C.1: 6 layout(s) applied, the merge expects 5", late)
        # the split's own grade keeps its wording
        split_only = rigsplit.split_apply_violations("C", passing[:5], SPLIT)
        self.assertEqual(split_only, [])
        self.assertIn("C: 8 layout(s) applied, the split expects 4", rigsplit.split_apply_violations("C", passing, SPLIT))


class BotTests(unittest.TestCase):
    SEAMLESS = ("[Spike2] Migration to 192.168.0.2:8010 | SEAMLESS connected=True playerReady=True keptController=True "
                "fellBack={} | standIns=1ms stop=2ms")

    def test_fell_back(self):
        lines = [self.SEAMLESS.format("False"), "Migration to 1.2.3.4:8010 failed (x); falling back to 1.2.3.4:8000",
                 self.SEAMLESS.format("True"), "Migration to 1.2.3.4:8000 | MASK connected=True playerReady=True |"]
        self.assertEqual(rigmerge.fell_back(lines), [False, True])

    def test_harvest_problems(self):
        r = rigsplit.parse_result
        good = r("RESULT harvest: nodesSeen=1 attacks=3 yield=1 pickup=True migrations=1 allReady=True maxGap=300 -> PASS")
        clean = [self.SEAMLESS.format("False")] * 2
        cases = {
            # failures first
            "no result": ({}, clean, 1, ["no RESULT harvest line"]),
            "failed": (r("RESULT harvest: migrations=1 -> FAIL"), clean, 1, ["RESULT did not pass"]),
            "written after the merge": (r("migrations=2 -> PASS"), clean, 1, ["migrations=2, expected 1 (written "
                                                                             "before the merge)"]),
            "never handed back": (good, clean, 0, ["0 Handoff START back to the survivor, expected 1"]),
            "one migration in the log": (good, clean[:1], 1, ["1 'fellBack=False' migration(s) in its log, expected 2"]),
            # passing
            "passing": (good, clean, 1, []),
        }
        for name, (result, lines, starts, expected) in cases.items():
            with self.subTest(name):
                self.assertEqual(rigmerge.harvest_problems(result, lines, starts), expected)


if __name__ == "__main__":
    unittest.main()
