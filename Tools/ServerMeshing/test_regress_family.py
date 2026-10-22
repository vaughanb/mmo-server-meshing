"""Unit tests for regress_family's flows against a fake rig: no editor, database, process or SSH is touched
(python -m unittest discover -s Tools/ServerMeshing -p "test_*.py"). The fake's cells log what riglayout.timeline says
they apply, its play runner answers each command from a script, and ps answers the process counts and the kill."""
import json
import os
import re
import tempfile
import unittest
from unittest import mock

import regress_family
import regress_split
import rigfamily
import riglayout
import rigsplit
import test_regress_split as ts

MAP, line, own = ts.MAP, ts.line, ts.own
SPLIT1 = ["requested C x 0 + child C.1", "e1 broadcast 1a2b/2", "e2 broadcast 1a2b/3", "drain gate open",
          "e3 broadcast 1a2b/4", "done"]
SPLIT2 = ["requested C.1 z 0 + child C.2", "e1 broadcast 1a2b/5", "e2 broadcast 1a2b/6", "drain gate open",
          "e3 broadcast 1a2b/7", "done"]
MERGE = ["requested C.2 C.1", "e1 broadcast 1a2b/8", "e1 gate open", "e2 staged", "e2 acked", "e2 broadcast 1a2b/9",
         "drain gate open", "retire sent", "C.1 exited", "channel C.1 removed", "e3 broadcast 1a2b/10", "done"]
SUPERVISOR = [t for n in (1, 2, 3) for t in (f"Supervisor: C.2 down 20 s, restart {n} of 3 requested",
                                             f"Supervisor: C.2 restart {n} failed (no hello from a new incarnation in 60 s)")] \
    + ["Supervisor: C.2 collapse queued after 3 failed restart(s)"]
COLLAPSE = ["requested C.1 C.2 collapse", "e1 broadcast 1a2b/8", "e1 gate open", "e2 staged", "C.2 exited before drained",
            "e2 broadcast 1a2b/9", "channel C.2 removed", "e3 broadcast 1a2b/10", "done"]
PAIRS = ({"e1": "1a2b/2", "e2": "1a2b/3", "e3": "1a2b/4"}, {"e1": "1a2b/5", "e2": "1a2b/6", "e3": "1a2b/7"},
         {"e1": "1a2b/8", "e2": "1a2b/9", "e3": "1a2b/10"})


def store_entry(cell, state, rect):
    owned = {"minX": rect[0], "minZ": rect[1], "maxX": rect[0] + rect[2], "maxZ": rect[1] + rect[3]} if rect else {}
    return {"id": cell, "mapId": MAP, "baseId": "C", "state": state, "owned": owned}


def passing_lines(name):
    """{zone: lines} of the scenario gone to plan: every cell's applies from riglayout.timeline, the bots' handoffs,
    the retiring child's quit and every other cell's prune of it."""
    collapse = name == "collapsesibling"
    survivor, retiring = ("C.1", "C.2") if collapse else ("C.2", "C.1")
    ops = rigfamily.ops(name, *PAIRS, survivor=survivor, retiring=retiring)
    expected = riglayout.timeline(riglayout.TEST_MAP_RECTS, ops)
    lines = {"C2": [own("C2", ts.MAP2)]}
    for cell, applies in expected.items():
        head = [line(f"Own cell from layout: {cell} of map {MAP}, grid (0,0), world x [0, 100) z [0, 100), 3 neighbours")] \
            if "." in cell else [own(cell)]
        body = [line(f"Layout {pair or '1a2b/1'} applied: {cell} owns {rigsplit.format_rect(o)} interest "
                     f"{rigsplit.format_rect(o if i == riglayout.ANY else i)}, 8 targets")
                for o, i, pair in applies]
        lines[cell] = head + body
    for b in rigfamily.PLANS[name]:
        for src, dst in rigfamily.expected_hops(name, b.point, survivor):
            lines[src].append(line(f"Handoff START {b.character} (id) zone {src}->{dst} pos=(1, 0, 1)"))
    if not collapse:
        lines[retiring].append(line(f"Merge retire: {retiring} drained, quitting"))
    for cell in expected:
        if cell != retiring:
            lines[cell].append(line(f"Retired zone {retiring}: link closed, 0 record(s) released"))
    return lines


class FakeFamilyRig(ts.FakeRig):
    """The rig for one family scenario: commands answered in turn from `answers` (a list of status lists), the
    collapse's supervisor and merge lines written when C.2 is killed, the store written with the merge."""
    EXE = r"D:\rig\Build\Server\kit-ce-project.exe"

    def __init__(self, folder, name, lines=None, answers=None, results=None, kill="1", after_kill=None, store=None):
        super().__init__(folder, "split", lines=passing_lines(name) if lines is None else lines, results=results)
        self.scenario = name
        self.TEMP = folder
        self.COORDINATOR_STORE = os.path.join(folder, "Coordinator.json")
        merge = name == "mergechildren"
        self.answers = list(answers) if answers is not None else [SPLIT1, SPLIT2] + ([MERGE] if merge else [])
        self.after_kill = (SUPERVISOR + COLLAPSE) if after_kill is None else after_kill
        self.kill, self.killed, self.merged = kill, False, False
        self.commands, self.seqs, self.marker_was_dir = [], [], None
        if store is None:
            live = riglayout.final_owned(riglayout.TEST_MAP_RECTS, rigfamily.ops(name, *PAIRS, survivor="C.1" if not merge else "C.2",
                                                                                 retiring="C.2" if not merge else "C.1"))
            gone = "C.1" if merge else "C.2"
            store = {"broadcast": {"entries": [store_entry(c, 2, live[c]) for c in ("C", "C.2" if merge else "C.1")]
                                   + [store_entry(gone, 4, None)]},
                     "fencedIncarnations": [] if merge else [{"cellId": "C.2", "incarnation": "ab"}], "hasOperation": False}
        self.store = store

    def request_play(self, mode, x, z, env=None, enabled=True, **kwargs):
        super().request_play(mode, x, z, env=env, enabled=enabled)

    def launch_bot(self, scenario, user, args, channel="C", observe_seconds=None, line_x=None, harvest_after_migration=False):
        self.calls.append(("launch_bot", user, observe_seconds))
        log = os.path.join(self.folder, f"regress_{scenario}_{user}.log")
        x, z = float(args[args.index("-meshWalkX") + 1]), float(args[args.index("-meshWalkZ") + 1])
        hops = rigfamily.expected_hops(scenario, (x, z), "C.1" if scenario == "collapsesibling" else "C.2")
        result = self.results.get(user, f"migrations={len(hops)} allReady=True maxGap=300 maxJump=0.2 relogins=0")
        with open(log, "w", encoding="utf-8") as f:
            f.write(f"[MeshAuto] Standing at ({x:.2f}, 0.08, {z:.2f}) party=0; watching\n"
                    f"[MeshAuto] RESULT stand: reached=True {result} -> PASS\n")
        return log

    def graded_zones(self, day):
        return list(ts.ZONES) + [("C.1", MAP), ("C.2", MAP)]

    def ps(self, script):
        if "Stop-Process" in script:
            marker = os.path.join(self.TEMP, rigfamily.marker_name("C.2"))
            self.marker_was_dir = os.path.isdir(marker)
            self.killed = True
            self.write(self.seqs[-1], self.after_kill)
            return self.kill
        cell = re.search(r"-channelId (\S+) \*", script).group(1)
        if cell == "C.1":
            return "0" if self.merged else "1"
        return "0" if self.killed else "1"

    def write(self, seq, states, op="Merge merge-1: "):
        with open(self.LAYOUT_COMMAND_STATUS, "a", encoding="utf-8") as f:
            for s in states:
                f.write(f"{seq} {'' if s.startswith(('Supervisor:', 'Layout op')) else op}{s}\n")
        if op.startswith("Merge") and any(s.startswith("requested ") for s in states):
            self.merged = True
            with open(self.COORDINATOR_STORE, "w", encoding="utf-8") as f:
                json.dump(self.store, f)

    def tick(self):
        if not os.path.exists(self.LAYOUT_COMMAND):
            return
        with open(self.LAYOUT_COMMAND, encoding="utf-8") as f:
            command = f.read().strip()
        os.remove(self.LAYOUT_COMMAND)
        self.commands.append(command)
        self.seqs.append(command.split()[0])
        verb = command.split()[1]
        self.write(self.seqs[-1], self.answers.pop(0) if self.answers else [],
                   f"Split split-{len(self.seqs)}: " if verb == "split" else "Merge merge-1: ")


def run(name, **rig_args):
    with tempfile.TemporaryDirectory() as d:
        rig = FakeFamilyRig(d, name, **rig_args)
        clock = ts.FakeClock(rig)
        rep, bots = ts.Report(), []
        with mock.patch.object(regress_split.time, "time", clock.time), \
                mock.patch.object(regress_split.time, "sleep", clock.sleep), \
                mock.patch.object(regress_split.provision, "reset_bot_positions"), \
                mock.patch.object(regress_split.rigtools, "asset_cell_modes", lambda path: {}):
            fr = regress_family.run(name, rep, rig, "10:00:00", "2026-10-16", bots)
            regress_family.grade_layout(rep, rig, "10:00:00", "2026-10-16", fr)
            leftover = os.path.exists(os.path.join(d, rigfamily.marker_name("C.2")))
        return rep, rig, fr, leftover


def assert_fails(test, rep, fragments):
    failed = rep.failed()
    for fragment in fragments:
        test.assertTrue(any(fragment in label for label in failed), f"{fragment!r} not in {failed}")


class MergeChildrenTests(unittest.TestCase):
    def test_passes(self):
        rep, rig, fr, _ = run("mergechildren")
        self.assertEqual(rep.failed(), [])
        self.assertEqual([c.split()[1:] for c in rig.commands], [["split", "C", "x", "0", "+", "C.1"],
                                                                  ["split", "C.1", "z", "0", "+", "C.2"],
                                                                  ["merge", "C.1", "C.2"]])
        self.assertEqual((fr.survivor, fr.retiring), ("C.2", "C.1"))
        self.assertTrue(all(c[2] == 600 for c in rig.calls if c[0] == "launch_bot"))
        self.assertEqual(rig._play_env.get("MESH_MONSTER_AREAS"), "only:Spawn_Interior_C")

    def test_failures(self):
        base = passing_lines("mergechildren")
        wrong = [s.replace("requested C.2 C.1", "requested C.1 C.2") for s in MERGE]
        tidy = MERGE[:-1] + ["tidy broadcast 1a2b/11", "done"]
        cases = {
            "the lower id survived": ({"answers": [SPLIT1, SPLIT2, wrong]}, ["surviving"]),
            "a tidy": ({"answers": [SPLIT1, SPLIT2, tidy]}, ["no tidy"]),
            "the child split refused": ({"answers": [SPLIT1, ["Layout op refused: maxCellsPerBase"]]}, ["split C.1 at z=0 accepted"]),
            "a bot short of a migration": ({"results": {"meshbot2": "migrations=1 allReady=True maxGap=3 relogins=0"}}, ["meshbot2: 2 migration"]),
            "a bot kicked": ({"results": {"meshbot8": "migrations=0 allReady=True maxGap=0 relogins=1"}}, ["meshbot8: 0 migration"]),
            "C.1 never quit": ({"lines": dict(base, **{"C.1": [t for t in base["C.1"] if "quitting" not in t]})}, ["drained, quitting"]),
            "C.2 missed its e3": ({"lines": dict(base, **{"C.2": [t for t in base["C.2"] if "1a2b/10" not in t]})}, ["zone C.2:", "applied the merge's e3"]),
            "a second handoff": ({"lines": dict(base, C=base["C"] + [line("Handoff START meshbot2Char (id) zone C->C.1 pos=(1, 0, 1)")])},
                                 ["C logged 2 Handoff START"]),
            "the store dropped C.2": ({"store": {"broadcast": {"entries": [store_entry("C", 2, (-100.0, -100.0, 100.0, 200.0))]}}},
                                      ["the store keeps"]),
        }
        for name, (rig_args, fragments) in cases.items():
            with self.subTest(name):
                assert_fails(self, run("mergechildren", **rig_args)[0], fragments)


class CollapseSiblingTests(unittest.TestCase):
    def test_passes(self):
        rep, rig, fr, leftover = run("collapsesibling")
        self.assertEqual(rep.failed(), [])
        self.assertEqual(len(rig.commands), 2)
        self.assertTrue(rig.marker_was_dir, "the marker is a directory when C.2 is killed")
        self.assertFalse(leftover, "and gone when the scenario ends")
        self.assertEqual(rig._play_env.get("MESH_FAULT_DIE_ON_HELLO"), "C.2:1")
        self.assertEqual((fr.survivor, fr.retiring), ("C.1", "C.2"))

    def test_failures(self):
        base = passing_lines("collapsesibling")
        restart = base["C.2"] + [line("Own cell from layout: C.2 of map M, grid (0,0), world x [0, 100) z [0, 100), 3 neighbours")]
        cases = {
            "left down": ({"after_kill": SUPERVISOR[:-1] + ["Supervisor: C.2 cannot collapse (no live cell of its family shares a full edge); left down"]},
                          ["supervisor:", "reached done"]),
            "into the base": ({"after_kill": SUPERVISOR + [s.replace("requested C.1 C.2", "requested C C.2") for s in COLLAPSE]},
                              ["collapse requested C.1 C.2"]),
            "two restarts only": ({"after_kill": SUPERVISOR[2:] + COLLAPSE}, ["supervisor:"]),
            "the kill missed": ({"kill": "0"}, ["killed 0 process"]),
            "a restart served": ({"lines": dict(base, **{"C.2": restart})}, ["2 process lifetime"]),
            "no fence": ({"store": {"broadcast": {"entries": []}}}, ["fences C.2", "the store keeps"]),
        }
        for name, (rig_args, fragments) in cases.items():
            with self.subTest(name):
                assert_fails(self, run("collapsesibling", **rig_args)[0], fragments)


if __name__ == "__main__":
    unittest.main()
