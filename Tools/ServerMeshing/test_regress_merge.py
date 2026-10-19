"""Unit tests for regress_merge's scenario flows against a fake rig: no editor, database, process or SSH is touched
(python -m unittest discover -s Tools/ServerMeshing -p "test_*.py"). The fake extends test_regress_split's."""
import json
import os
import tempfile
import unittest
from unittest import mock

import regress_merge
import regress_split
import rigsplit
import test_regress_split as ts

MAP, MAP2, line, apply = ts.MAP, ts.MAP2, ts.line, ts.apply
SPLIT_OF = {"merge": "split", "mergec2": "splitc2"}
MERGE_DONE = ["requested {b} {c}", "e1 broadcast 1a2b/5", "e1 gate open", "e2 staged", "e2 acked",
              "e2 broadcast 1a2b/6", "drain gate open", "retire sent", "{c} exited", "channel {c} removed",
              "e3 broadcast 1a2b/7", "tidy broadcast 1a2b/8", "done"]
EXE = r"D:\rig\Build\Server\kit-ce-project.exe"


def warn(text):
    return f" WARN Mesh {ts.STAMP} - {text}"


def merge_status(name, states=None):
    spec = regress_split.SPECS[SPLIT_OF[name]]
    return [s.format(b=spec.base, c=spec.child) for s in (states or MERGE_DONE)]


def seamless(fell_back=False):
    return (f" INFO Spike2 {ts.STAMP} - Migration to 192.168.0.2:8010 | SEAMLESS connected=True playerReady=True "
            f"keptController=True fellBack={fell_back} | standIns=1ms")


def passing_lines(name):
    """{zone: lines} of a split and a merge back that went to plan."""
    split_name = SPLIT_OF[name]
    spec = regress_split.SPECS[split_name]
    base, child = spec.base, spec.child
    kept, given = rigsplit.split_rects(spec.base_rect, "x", spec.at, "+")
    p = spec.base_rect
    lines = ts.passing_lines(split_name)
    prune = line(f"Retired zone {child}: link closed, 0 record(s) released")
    for zone, map_id in ts.ZONES:
        if zone in (base, "C2"):
            continue
        x, z = ts.WORLD[zone]
        rect = (float(x), float(z), 200.0, 200.0)
        lost = [line(f"Neighbour zone {child} link lost (draining, retire expected)")] if zone == "N" else []
        lines[zone] = lines[zone] + [apply(zone, v, rect, rect) for v in (5, 6)] + lost + [apply(zone, 7, rect, rect),
                                                                                           prune, apply(zone, 8, rect, rect)]
    lines[base] = lines[base] + [
        apply(base, 5, kept, p), apply(base, 6, p, p),
        line(f"Monster promoted by handoff m1 {child}->{base} at (40, 0, 10) hp=5/5"),
        line(f"Static handoff s1 zone {child}->{base}: promoted"), line(f"Synthetic adopted Load_3 zone {child}->{base}"),
        apply(base, 7, p, p), prune, apply(base, 8, p, p)]
    lines[child] = lines[child] + [apply(child, 5, given, given), apply(child, 6, None, given),
                                   line(f"Static handoff s1 zone {child}->{base}: sent")]
    for b in regress_split.plan_bots(split_name):
        if b.side == "+":
            lines[child].append(line(f"Handoff START {b.character} (id) zone {child}->{base} pos=(1, 0, 1)"))
    lines[child].append(line(f"Merge retire: {child} drained, quitting"))
    if spec.beside_c2:
        lines["C2"] = lines["C2"] + [line("Layout 1a2b/5 neighbours applied: C2 band E, E.1"),
                                     line("Layout 1a2b/6 neighbours applied: C2 band E, E.1"),
                                     line("Layout 1a2b/7 neighbours applied: C2 band E"), prune,
                                     line("Layout 1a2b/8 neighbours applied: C2 band E")]
        for b in regress_split.plan_bots(split_name):
            if b.side == "+":
                lines["C2"].append(line(f"Ghost owner changed Player {b.character} (id) zone {child}->{base} at "
                                        f"({b.point[0] - 400:.2f}, 0.08, {b.point[1]:.2f})"))
    return lines


class FakeMergeRig(ts.FakeRig):
    """test_regress_split's FakeRig for a merge scenario: the status file is appended to per command (a split, then
    one or more merges, each answered from `merges`), the store file written with the merge, and ps answers the
    process count: `control` before the merge, then `after` in turn (its last value repeats)."""
    EXE = EXE

    def __init__(self, folder, name, lines=None, merges=None, control="1", after=("0",), store=None, results=None,
                 bot_lines=None, standing=True):
        super().__init__(folder, SPLIT_OF[name], lines=passing_lines(name) if lines is None else lines,
                         results=results, standing=standing)
        self.scenario = name
        self.COORDINATOR_STORE = os.path.join(folder, "Coordinator.json")
        self.merges = list(merges) if merges is not None else [merge_status(name)]
        self.control, self.after = control, list(after)
        self.store = {"broadcast": {"pair": {}, "entries": []}, "hasStaged": False} if store is None else store
        self.bot_lines = bot_lines or {}
        self.commands, self.scripts, self.merge_sent = [], [], False

    def launch_bot(self, scenario, user, args, channel="C", observe_seconds=None, line_x=None,
                   harvest_after_migration=False):
        self.calls.append(("launch_bot", user, tuple(args), channel, observe_seconds, line_x, harvest_after_migration))
        log = os.path.join(self.folder, f"regress_{scenario}_{user}.log")
        x, z = float(args[args.index("-meshWalkX") + 1]), float(args[args.index("-meshWalkZ") + 1])
        far = x >= line_x
        text = [f"[MeshAuto] Standing at ({x:.2f}, 0.08, {z:.2f}) party=0; watching for 480s"] if self.standing else []
        if harvest_after_migration:
            default, text = "migrations=1 allReady=True maxGap=300ms maxJump=0.2m yield=1 pickup=True", text + [seamless()]
        elif far:
            default = "migrations=2 allReady=True maxGap=300ms maxJump=0.2m"
        else:
            default = "migrations=0 allReady=True maxGap=0ms maxJump=0m"
        kind = args[args.index("-meshAutoMode") + 1]
        text.append(f"[MeshAuto] RESULT {kind}: reached=True {self.results.get(user, default)} -> PASS")
        if far:
            text += [seamless(), seamless()] if not harvest_after_migration else [seamless()]
        text += self.bot_lines.get(user, [])
        with open(log, "w", encoding="utf-8") as f:
            f.write("\n".join(text) + "\n")
        return log

    def graded_zones(self, day):
        return list(ts.ZONES) + [(regress_split.SPECS[SPLIT_OF[self.scenario]].child, MAP)]

    def ps(self, script):
        self.scripts.append(script)
        if not self.merge_sent:
            return self.control
        return self.after.pop(0) if len(self.after) > 1 else self.after[0]

    def tick(self):
        """The play runner: takes each command (deleting the file) and appends its status lines under its seq."""
        if not os.path.exists(self.LAYOUT_COMMAND):
            return
        with open(self.LAYOUT_COMMAND, encoding="utf-8") as f:
            command = f.read().strip()
        os.remove(self.LAYOUT_COMMAND)
        self.commands.append(command)
        seq, verb = command.split()[:2]
        if verb == "split":
            states, op = self.status, "Split split-1: "
        else:
            states, op = (self.merges.pop(0) if self.merges else []), "Merge merge-2: "
            self.merge_sent = True
            with open(self.COORDINATOR_STORE, "w", encoding="utf-8") as f:
                json.dump(self.store, f)
        with open(self.LAYOUT_COMMAND_STATUS, "a", encoding="utf-8") as f:
            f.write("".join(f"{seq} {s if s.startswith(('Layout op', 'Split ')) else op + s}\n" for s in states))


def run(name, **rig_args):
    with tempfile.TemporaryDirectory() as d:
        rig = FakeMergeRig(d, name, **rig_args)
        clock = ts.FakeClock(rig)
        rep, bots = ts.Report(), []
        with mock.patch.object(regress_split.time, "time", clock.time), \
                mock.patch.object(regress_split.time, "sleep", clock.sleep), \
                mock.patch.object(regress_split.provision, "reset_bot_positions"), \
                mock.patch.object(regress_split.rigtools, "asset_cell_modes", lambda path: {}):
            mr = regress_merge.run(name, rep, rig, "10:00:00", "2026-10-16", bots)
            regress_merge.grade_layout(rep, rig, "10:00:00", "2026-10-16", mr)
        return rep, rig, bots, mr


def assert_fails(test, rep, fragments):
    failed = rep.failed()
    for fragment in fragments:
        test.assertTrue(any(fragment in label for label in failed), f"{fragment!r} not in {failed}")


class MergeScenarioTests(unittest.TestCase):
    def test_merge_passes(self):
        rep, rig, bots, mr = run("merge")
        self.assertEqual(rep.failed(), [])
        self.assertEqual([c.split()[1:] for c in rig.commands], [["split", "C", "x", "0", "+", "C.1"],
                                                                  ["merge", "C", "C.1"]])
        launches = [c for c in rig.calls if c[0] == "launch_bot"]
        self.assertTrue(all(c[4] == regress_merge.MERGE_OBSERVE_S for c in launches))
        self.assertEqual(len(bots), 9)
        self.assertTrue(all("-channelId C.1 *" in s and EXE in s and "Get-CimInstance" in s for s in rig.scripts))
        self.assertGreaterEqual(len(rig.scripts), 1 + int(regress_merge.STAY_GONE_S / regress_merge.SAMPLE_EVERY_S))
        self.assertEqual(mr.status["op"], "merge-2")
        self.assertTrue(any("exit time not measured" in n for n in rep.notes), rep.notes)   # the fake clock is 1970
        self.assertTrue(any("link lost" in n for n in rep.notes), rep.notes)

    def test_mergec2_passes(self):
        rep, rig, bots, _ = run("mergec2")
        self.assertEqual(rep.failed(), [])
        self.assertTrue(rig.commands[1].endswith(" merge E E.1"))
        self.assertTrue(all(c[4] == regress_merge.MERGEC2_OBSERVE_S for c in rig.calls if c[0] == "launch_bot"))
        self.assertTrue(all("-channelId E.1 *" in s for s in rig.scripts))

    def test_a_late_split_line_under_the_merge_seq_is_a_note(self):
        late = merge_status("merge")[:3] + ["Split split-1: stragglers gone, settle broadcast 1a2b/9"] \
            + merge_status("merge")[3:]
        rep = run("merge", merges=[late])[0]
        self.assertEqual(rep.failed(), [])
        self.assertTrue(any("settle broadcast" in n for n in rep.notes))

    def test_a_cooldown_refusal_is_retried_once(self):
        refused = ["Layout op refused: family cooldown, 4 s left"]
        rep, rig, _, _ = run("merge", merges=[refused, merge_status("merge")])
        self.assertEqual(rep.failed(), [])
        self.assertEqual(len(rig.commands), 3)
        self.assertNotEqual(rig.commands[1].split()[0], rig.commands[2].split()[0])
        rep, rig, _, _ = run("merge", merges=[refused, refused])
        assert_fails(self, rep, ["accepted"])
        self.assertEqual(len(rig.commands), 3)

    def test_the_window_guard(self):
        with mock.patch.object(regress_merge, "COOLDOWN_S", 400.0):
            rep, rig, _, _ = run("merge")
        assert_fails(self, rep, ["stand window too short"])
        self.assertEqual(len(rig.commands), 1)

    def test_status_failures(self):
        s = merge_status("merge")
        cases = {
            "refused": ([["Layout op refused: C and E are not of one family (bases C and E)"]], ["accepted"]),
            "aborted": ([s[:3] + ["aborted e1 gate shut: no hello from N", "reverted broadcast 1a2b/6"]],
                        ["merge states", "neither aborted"]),
            "frozen": ([s[:11] + ["frozen e3 ack timeout"]], ["merge states", "neither aborted"]),
            "straggling": ([s[:7] + ["drain straggling 92 s: characters=1"] + s[7:]], ["neither aborted"]),
            "exited before drained": ([s[:7] + ["C.1 exited before drained"] + s[9:]], ["neither aborted"]),
            "out of order": ([s[:1] + [s[2], s[1]] + s[3:]], ["out of order"]),
            "a survivor paused": ([s[:7] + ["C lost, op paused", "C rebound 8d, op resumed"] + s[7:]],
                                  ["no supervision"]),
        }
        for name, (merges, fragments) in cases.items():
            with self.subTest(name):
                assert_fails(self, run("merge", merges=merges)[0], fragments)

    def test_process_failures(self):
        cases = {
            "still alive": ({"after": ["1"]}, ["no process with '*-channelId C.1 *'"]),
            "came back": ({"after": ["0", "0", "1"]}, ["one came back"]),
            "no positive control": ({"control": "0"}, ["positive control"]),
            "the count failed": ({"after": ["oops"]}, ["could not be counted"]),
        }
        for name, (rig_args, fragments) in cases.items():
            with self.subTest(name):
                assert_fails(self, run("merge", **rig_args)[0], fragments)

    def test_line_failures(self):
        base = passing_lines("merge")
        child_start = "Handoff START meshbot2Char (id) zone C.1->C pos=(1, 0, 1)"
        ghost = line("Ghost spawned Monster  (C:ab:7) from zone C.1 at (1.00, 0.08, 1.00) (silenced 4)")
        spawn = line("Monster spawned by handoff C:ab:5 C.1->C at (1, 0, 1) hp=6/6 home=C target=none")
        e2 = next(i for i, t in enumerate(base["C"]) if "Layout 1a2b/6 applied" in t)
        cases = {
            "a warning prune": ({"C": [t.replace("0 record(s)", "2 record(s)") for t in base["C"]]},
                                ["zone C: applied e3", "warning prune"]),
            "no prune on N": ({"N": [t for t in base["N"] if "Retired zone" not in t]}, ["zone N: applied e3"]),
            "a lent monster released": ({"N": base["N"] + [warn("Lent monster C:ab:3 released (link lost)")]},
                                        ["lent/borrowed"]),
            "C.1 still holds": ({"C.1": base["C.1"] + [warn("Merge retire: C.1 still holds characters=1")]},
                                ["warning-level"]),
            "C.1 never quit": ({"C.1": [t for t in base["C.1"] if "quitting" not in t]}, ["drained, quitting"]),
            "two handoffs back": ({"C.1": base["C.1"] + [line(child_start)]},
                                  ["2 Handoff START meshbot2Char zone C.1->C"]),
            "no tidy apply": ({"C": [t for t in base["C"] if "1a2b/8" not in t]}, ["tidy layout", "zone C:"]),
            "a pre-warmed monster spawned": ({"C": base["C"][:e2] + [ghost] + base["C"][e2:] + [spawn]},
                                             ["pre-warmed monster"]),
            "no synthetic back": ({"C": [t for t in base["C"] if "Synthetic adopted Load_3 zone C.1->C" not in t]},
                                  ["adopted 0 synthetic"]),
            "a static not taken": ({"C": [t for t in base["C"] if "zone C.1->C: promoted" not in t]}, ["statics:"]),
            "a retire line on another cell": ({"N": base["N"] + [line("Merge retire: N drained, quitting")]},
                                              ["zone N:"]),
            "C.1 applied e3": ({"C.1": base["C.1"] + [apply("C.1", 7, None, None)]}, ["zone C.1:"]),
        }
        for name, (changed, fragments) in cases.items():
            with self.subTest(name):
                assert_fails(self, run("merge", lines=dict(base, **changed))[0], fragments)

    def test_bot_failures(self):
        cases = {
            "an R-bot migrated once": ({"results": {"meshbot2": "migrations=1 allReady=True maxGap=5"}},
                                       ["meshbot2: 2 migration"]),
            "a P-bot migrated": ({"results": {"meshbot7": "migrations=1 allReady=True maxGap=5"}},
                                 ["meshbot7: 0 migration"]),
            "a fallback hop": ({"bot_lines": {"meshbot3": [seamless(True)]}}, ["meshbot3: 1 migration(s) with fellBack"]),
            "the harvest bot's RESULT after the merge": (
                {"results": {"meshbot": "migrations=2 allReady=True maxGap=5 yield=1 pickup=True"}}, ["(harvest)"]),
        }
        for name, (rig_args, fragments) in cases.items():
            with self.subTest(name):
                assert_fails(self, run("merge", **rig_args)[0], fragments)

    def test_store_failure(self):
        store = {"broadcast": {"entries": [{"id": "C", "mapId": MAP, "baseId": "C", "state": 0},
                                           {"id": "E", "mapId": MAP, "baseId": "E", "state": 0}]}}
        assert_fails(self, run("merge", store=store)[0], ["coordinator store holds no entry"])

    def test_mergec2_failures(self):
        base = passing_lines("mergec2")
        cases = {
            "E.1 still in C2's band": ({"C2": [t.replace("1a2b/7 neighbours applied: C2 band E", "1a2b/7 neighbours "
                                                         "applied: C2 band E, E.1") for t in base["C2"]]},
                                       ["C2's band at the merge's e3"]),
            "no owner change": ({"C2": [t for t in base["C2"] if "Ghost owner changed" not in t]}, ["C2: an R-bot"]),
            "no prune on C2": ({"C2": [t for t in base["C2"] if "Retired zone" not in t]},
                               ["C2 logged 'Retired zone E.1", "zone C2: applied e3"]),
        }
        for name, (changed, fragments) in cases.items():
            with self.subTest(name):
                assert_fails(self, run("mergec2", lines=dict(base, **changed))[0], fragments)

    def test_the_split_not_done_sends_no_merge(self):
        with tempfile.TemporaryDirectory() as d:
            rig = FakeMergeRig(d, "merge")
            rig.status = ["Layout op refused: maxCellsPerBase"]
            clock = ts.FakeClock(rig)
            rep2 = ts.Report()
            with mock.patch.object(regress_split.time, "time", clock.time), \
                    mock.patch.object(regress_split.time, "sleep", clock.sleep), \
                    mock.patch.object(regress_split.provision, "reset_bot_positions"):
                mr = regress_merge.run("merge", rep2, rig, "10:00:00", "2026-10-16", [])
        assert_fails(self, rep2, ["merge: not sent"])
        self.assertEqual(len(rig.commands), 1)
        self.assertIsNone(mr.status)


if __name__ == "__main__":
    unittest.main()
