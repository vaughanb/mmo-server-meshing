"""Unit tests for regress_split's scenario flows against a fake rig: no editor, database, process or SSH is touched
(python -m unittest discover -s Tools/ServerMeshing -p "test_*.py")."""
import os
import re
import tempfile
import unittest
from unittest import mock

import regress_split
import rigsplit

MAP, MAP2 = "ServerMeshTestMap", "ServerMeshTestMap2"
ZONES = [("SW", MAP), ("S", MAP), ("SE", MAP), ("W", MAP), ("C", MAP), ("E", MAP), ("NW", MAP), ("N", MAP),
         ("NE", MAP), ("C2", MAP2)]
WORLD = {"SW": (-300, -300), "S": (-100, -300), "SE": (100, -300), "W": (-300, -100), "C": (-100, -100),
         "E": (100, -100), "NW": (-300, 100), "N": (-100, 100), "NE": (100, 100)}
STAMP = "[2026-10-16 10:00:00]"


def line(text):
    return f" INFO Mesh {STAMP} - {text}"


def own(cell, map_id=MAP):
    x, z = WORLD.get(cell, (-100, -100))
    return line(f"Own cell from channel id: {cell} of map {map_id}, grid (0,0), world x [{x}, {x + 200}) "
                f"z [{z}, {z + 200}), 8 neighbours")


def apply(cell, version, owned, interest):
    return line(f"Layout 1a2b/{version} applied: {cell} owns {rigsplit.format_rect(owned)} interest "
                f"{rigsplit.format_rect(interest)}, 8 targets")


def passing_lines(name):
    """{zone: info lines} of a split that went to plan."""
    spec = regress_split.SPECS[name]
    split = regress_split.split_of(name, MAP)
    kept, given = rigsplit.split_rects(split.base_rect, "x", spec.at, "+")
    lines = {}
    for zone, _ in ZONES:
        if zone == "C2":
            lines[zone] = [own("C2", MAP2), line("Layout 1a2b/4 neighbours applied: C2 band E.1"),
                           line(f"Neighbour zone {spec.child} linked (conn 3), frame offset (-400.00, 0.00, 0.00)")]
            for b in regress_split.plan_bots(name):
                if b.side == "+":
                    lines[zone].append(line(f"Ghost spawned Player {b.character} (id) from zone {spec.child} at "
                                            f"({b.point[0] - 400:.2f}, 0.08, {b.point[1]:.2f}) (silenced 3)"))
            continue
        x, z = WORLD[zone]
        rect = (float(x), float(z), 200.0, 200.0)
        lines[zone] = [own(zone)] + [apply(zone, v, rect, rect) for v in (1, 2, 3, 4)]
    p = split.base_rect
    lines[spec.base] = [own(spec.base), apply(spec.base, 1, p, p), apply(spec.base, 2, p, p),
                        apply(spec.base, 3, kept, p), apply(spec.base, 4, kept, kept),
                        line("Puppet motion: Kinematic (Dynamic map default)"),
                        line(f"Static handoff s1 zone {spec.base}->{spec.child}: sent")]
    for b in regress_split.plan_bots(name):
        if b.side == "+":
            lines[spec.base].append(line(f"Handoff START {b.character} (id) zone {spec.base}->{spec.child} pos=(1, 0, 1)"))
    lines[spec.child] = [line(f"Own cell from layout: {spec.child} of map {MAP}, grid (0,0), world x [0, 100) z [-100, 100), 5 neighbours"),
                         apply(spec.child, 2, None, given), apply(spec.child, 3, given, given),
                         line(f"Monster promoted by handoff m1 {spec.base}->{spec.child} at (40, 0, 10) hp=5/5"),
                         apply(spec.child, 4, given, given),
                         line("Puppet motion: Kinematic (Dynamic map default)"),
                         line(f"Static handoff s1 zone {spec.base}->{spec.child}: promoted"),
                         line(f"Synthetic adopted Load_3 zone {spec.base}->{spec.child}"),
                         line("Prewarm window 5.00 s: queue 0 spawned 12 | max frame 18.5 ms")]
    return lines


STATUS_DONE = ["requested C x 0 + child C.1", "e1 broadcast 1a2b/2", "e2 broadcast 1a2b/3", "drain gate open",
               "e3 broadcast 1a2b/4", "done"]


class FakeRig:
    """The parts of regress that regress_split uses; the bots and the play runner are simulated by the clock."""
    MAP, MAP2, ZONES, RIG_REGISTRY = MAP, MAP2, ZONES, "registry.asset"

    def __init__(self, folder, name, lines=None, status=None, results=None, standing=True):
        self.folder, self.name = folder, name
        self.STATUS = os.path.join(folder, "PlayStatus.txt")
        self.LAYOUT_COMMAND = os.path.join(folder, "LayoutCommand.txt")
        self.LAYOUT_COMMAND_STATUS = os.path.join(folder, "LayoutCommandStatus.txt")
        self.lines = passing_lines(name) if lines is None else lines
        self.status = STATUS_DONE if status is None else status
        self.results = results or {}
        self.standing = standing
        self._play_env = {}
        self.calls = []
        self.command = None

    def db_reset(self, tester, bot):
        self.calls.append(("db_reset", tester, bot))

    def request_play(self, mode, x, z, env=None, enabled=True):
        self._play_env = dict(env or {})
        self.calls.append(("request_play", mode, x, z, self._play_env, enabled))

    def wait_play(self, timeout):
        self.calls.append(("wait_play",))

    def launch_bot(self, scenario, user, args, channel="C", observe_seconds=None, line_x=None,
                   harvest_after_migration=False):
        self.calls.append(("launch_bot", user, tuple(args), channel, observe_seconds, line_x, harvest_after_migration))
        log = os.path.join(self.folder, f"regress_{scenario}_{user}.log")
        x, z = float(args[args.index("-meshWalkX") + 1]), float(args[args.index("-meshWalkZ") + 1])
        far = x >= regress_split.SPECS[scenario].at
        text = []
        if self.standing:
            text.append(f"[MeshAuto] Standing at ({x:.2f}, 0.08, {z:.2f}) party=0; watching for 300s")
        result = self.results.get(user, "migrations=1 allReady=True maxGap=300ms maxJump=0.2m" if far
                                  else "migrations=0 allReady=True maxGap=0ms maxJump=0m")
        if harvest_after_migration and user not in self.results:
            result += " yield=1 pickup=True"
        kind = args[args.index("-meshAutoMode") + 1]
        text.append(f"[MeshAuto] RESULT {kind}: reached=True {result} -> PASS")
        with open(log, "w", encoding="utf-8") as f:
            f.write("\n".join(text) + "\n")
        return log

    def zone_lines(self, zone, start, day, map_id=MAP, suffixes=("info", "warn", "error")):
        return list(self.lines.get(zone, []))

    def graded_zones(self, day):
        return list(ZONES) + [(regress_split.SPECS[self.name].child, MAP)]

    @staticmethod
    def count(lines, pattern):
        rx = re.compile(pattern)
        return sum(1 for text in lines if rx.search(text))

    def tick(self):
        """The play runner: takes the command and writes the op's status lines under its seq."""
        if self.command is None and os.path.exists(self.LAYOUT_COMMAND):
            with open(self.LAYOUT_COMMAND, encoding="utf-8") as f:
                self.command = f.read().strip()
            seq = self.command.split()[0]
            with open(self.LAYOUT_COMMAND_STATUS, "w", encoding="utf-8") as f:
                f.write("".join(f"{seq} {s if s.startswith('Layout op') else 'Split split-1: ' + s}\n"
                                for s in self.status))


class FakeClock:
    def __init__(self, rig):
        self.now, self.rig = 1000.0, rig

    def time(self):
        return self.now

    def sleep(self, seconds):
        self.now += seconds
        self.rig.tick()


class Report:
    def __init__(self):
        self.checks, self.notes = [], []

    def check(self, ok, label):
        self.checks.append((bool(ok), label))
        return ok

    def failed(self):
        return [label for ok, label in self.checks if not ok]


def run(name, **rig_args):
    with tempfile.TemporaryDirectory() as d:
        rig = FakeRig(d, name, **rig_args)
        clock = FakeClock(rig)
        rep, bots = Report(), []
        with mock.patch.object(regress_split.time, "time", clock.time), \
                mock.patch.object(regress_split.time, "sleep", clock.sleep), \
                mock.patch.object(regress_split.provision, "reset_bot_positions") as reset, \
                mock.patch.object(regress_split.rigtools, "asset_cell_modes", lambda path: {}):
            split = regress_split.run(name, rep, rig, "10:00:00", "2026-10-16", bots)
            regress_split.grade_layout(rep, rig, "10:00:00", "2026-10-16", split)
        return rep, rig, bots, reset


class ScenarioTests(unittest.TestCase):
    def test_failures(self):
        base = passing_lines("split")
        twice = dict(base, C=base["C"] + [base["C"][-1]])
        respawn = line("Monster spawned by handoff m2 C->C.1 at (1, 0, 1) hp=1/1")
        spawned = dict(base, **{"C.1": base["C.1"][:4] + [respawn] + base["C.1"][4:]})
        no_e3 = dict(base, C=base["C"][:4] + base["C"][5:])
        cases = {
            "refused": ({"status": ["Layout op refused: maxCellsPerBase"]}, ["accepted", "split done", "drain"]),
            "aborted": ({"status": STATUS_DONE[:2] + ["aborted X silent"]}, ["split done", "neither aborted"]),
            "stragglers": ({"status": STATUS_DONE[:3] + ["drain timeout, 2 straggler(s)", "e3 broadcast 1a2b/4", "done"]},
                           ["drain evidence"]),
            "no bot stood": ({"standing": False}, ["not sent"]),
            "a supervisor restart": ({"status": STATUS_DONE[:3] + ["C.1 lost, op paused"] + STATUS_DONE[3:]},
                                     ["no supervision"]),
            "an R-bot never migrated": ({"results": {"meshbot2": "migrations=0 allReady=True maxGap=0"}},
                                        ["meshbot2: 1 migration"]),
            "a P-bot migrated": ({"results": {"meshbot7": "migrations=1 allReady=True maxGap=5"}},
                                 ["meshbot7: 0 migration"]),
            "a slow migration": ({"results": {"meshbot3": "migrations=1 allReady=True maxGap=1200"}},
                                 ["meshbot3: 1 migration"]),
            "no harvest": ({"results": {"meshbot": "migrations=1 allReady=True maxGap=5 yield=0 pickup=False"}},
                           ["harvested"]),
            "two handoff starts": ({"lines": twice}, ["2 Handoff START meshbotChar zone C->C.1"]),
            "a monster spawned": ({"lines": spawned}, ["monster(s) promoted"]),
            "the base never shrank its interest": ({"lines": no_e3}, ["applied a layout owning", "zone C:"]),
        }
        for name, (rig_args, fragments) in cases.items():
            with self.subTest(name):
                rep = run("split", **rig_args)[0]
                failed = rep.failed()
                for fragment in fragments:
                    self.assertTrue(any(fragment in label for label in failed), f"{fragment!r} not in {failed}")

    def test_respawns_after_e3_are_a_note(self):
        base = passing_lines("split")
        respawn = line("Monster spawned by handoff m2 C->C.1 at (1, 0, 1) hp=1/1")
        rep, _, _, _ = run("split", lines=dict(base, **{"C.1": base["C.1"] + [respawn, respawn]}))
        self.assertEqual(rep.failed(), [])
        self.assertIn("   C.1: 0 monster(s) respawned in R and spawned by handoff before e3, 2 after (from C's areas)",
                      rep.notes)

    def test_a_respawn_before_e3_is_a_note_a_prewarmed_spawn_fails(self):
        base = passing_lines("split")
        ghost = line("Ghost spawned Monster  (C:ab:7) from zone C at (1.00, 0.08, 1.00) (silenced 4)")
        child = base["C.1"][:2] + [ghost] + base["C.1"][2:4]   # own cell, e1 apply, the ghost, e2 apply, a promotion
        for n, failed in ((9, []), (7, ["monster(s) promoted"])):
            with self.subTest(n=n):
                spawn = line(f"Monster spawned by handoff C:ab:{n} C->C.1 at (1, 0, 1) hp=6/6 home=C target=none")
                rep, _, _, _ = run("split", lines=dict(base, **{"C.1": child + [spawn] + base["C.1"][4:]}))
                self.assertEqual([f for f in rep.failed() if not any(k in f for k in failed)], [])
                self.assertEqual(len(rep.failed()), len(failed))

    def test_split_passes(self):
        rep, rig, bots, reset = run("split")
        self.assertEqual(rep.failed(), [])
        self.assertEqual(rig.command, rig.command.split()[0] + " split C x 0 + C.1")
        reset.assert_called_once()
        first, n, points = reset.call_args[0]
        self.assertEqual((first, n, len(points)), (2, 8, 8))
        self.assertEqual(rig.calls[0], ("db_reset", (-50.0, 82.0), (70.0, -80.0)))
        play = rig.calls[1]
        self.assertEqual(play[:4], ("request_play", "stand", -50.0, 82.0))
        self.assertEqual(play[4], {"MESH_CELL_MODE": f"{MAP}:Dynamic", "MESH_LOAD_PLAN": "C:40",
                                   "MESH_MONSTER_AREAS": "Spawn_Interior_C"})
        launches = [c for c in rig.calls if c[0] == "launch_bot"]
        self.assertEqual([c[1] for c in launches], [f"meshbot{k}" for k in range(2, 10)] + ["meshbot"])
        self.assertTrue(all(c[3] == "C" and c[4] == regress_split.OBSERVE_S and c[5] == 0.0 for c in launches))
        self.assertEqual([c[6] for c in launches], [False] * 8 + [True])
        self.assertEqual([c[2][1] for c in launches], ["stand"] * 8 + ["harvest"])
        self.assertEqual([kind for kind, _ in bots], ["stand"] * 8 + ["harvest"])
        self.assertEqual(len(bots), 9)

    def test_splitc2_offset_set_by_the_apply_passes(self):
        base = passing_lines("splitc2")
        late = [line("Neighbour zone E.1 linked (conn 3)"),
                line("Neighbour zone E.1 frame offset (-400.00, 0.00, 0.00) from layout 1a2b/2")]
        c2 = [t for t in base["C2"] if "linked" not in t] + late
        rep, _, _, _ = run("splitc2", lines=dict(base, C2=c2),
                           status=[s.replace("C x 0", "E x 200").replace("C.1", "E.1") for s in STATUS_DONE])
        self.assertEqual(rep.failed(), [])

    def test_splitc2_passes(self):
        rep, rig, bots, _ = run("splitc2", status=[s.replace("C x 0", "E x 200").replace("C.1", "E.1")
                                                   for s in STATUS_DONE])
        self.assertEqual(rep.failed(), [])
        self.assertTrue(rig.command.endswith(" split E x 200 + E.1"))
        launches = [c for c in rig.calls if c[0] == "launch_bot"]
        self.assertEqual(len(launches), 8)
        self.assertTrue(all(c[3] == "E" and c[5] == 200.0 and not c[6] for c in launches))
        self.assertNotIn("MESH_LOAD_PLAN", rig.calls[1][4])
        self.assertNotIn("MESH_MONSTER_AREAS", rig.calls[1][4])

    def test_splitc2_failures(self):
        base = passing_lines("splitc2")
        cases = {
            "E still in C2's band": ({"C2": base["C2"][:1] + [line("Layout 1a2b/4 neighbours applied: C2 band E, E.1")]
                                      + base["C2"][2:]}, ["C2's band after e3"]),
            "no frame offset": ({"C2": [t.replace("frame offset (-400", "frame offset (0") for t in base["C2"]]},
                                ["C2 linked E.1"]),
            "the puppet in the wrong place": ({"C2": [t.replace("from zone E.1 at (-1", "from zone E.1 at (1")
                                                      for t in base["C2"]]}, ["C2 sees an R-bot"]),
            "an own-cell layout on C2": ({"C2": base["C2"] + [apply("C2", 4, (300.0, -100.0, 200.0, 200.0), None)]},
                                         ["zone C2"]),
        }
        status = [s.replace("C x 0", "E x 200").replace("C.1", "E.1") for s in STATUS_DONE]
        for name, (changed, fragments) in cases.items():
            with self.subTest(name):
                rep = run("splitc2", lines=dict(base, **changed), status=status)[0]
                for fragment in fragments:
                    self.assertTrue(any(fragment in label for label in rep.failed()), rep.failed())


class PlacementTests(unittest.TestCase):
    def test_stand_points(self):
        for at in (0.0, 200.0):
            far, near = regress_split.stand_points(at)
            with self.subTest(at):
                self.assertTrue(all(at + 10 <= x < at + 90 and 70 <= abs(z) <= 95 for x, z in far))
                self.assertTrue(all(at - 90 <= x < at - 10 and 70 <= abs(z) <= 95 for x, z in near))
        # splitc2: at least one R-bot inside C2's 80 m band from x = 300, so C2 ghosts it
        self.assertTrue(any(x > 220 for x, _ in regress_split.stand_points(200.0)[0]))

    def test_plan_bots(self):
        split, c2 = regress_split.plan_bots("split"), regress_split.plan_bots("splitc2")
        self.assertEqual([b.side for b in split], ["+"] * 4 + ["-"] * 4 + ["+"])
        self.assertEqual((split[-1].user, split[-1].character, split[-1].harvest), ("meshbot", "meshbotChar", True))
        self.assertFalse(any(b.harvest for b in c2))
        self.assertEqual(len(c2), 8)
        for name, spec in regress_split.SPECS.items():
            with self.subTest(name):
                kept, _ = rigsplit.split_rects(spec.base_rect, "x", spec.at, "+")
                self.assertEqual(rigsplit.side_of(spec.tester, "x", spec.at), "-")   # the tester parked in P's half
                self.assertTrue(kept[0] <= spec.tester[0] < kept[0] + kept[2])

    def test_write_command_replaces_whole(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "LayoutCommand.txt")
            regress_split.write_command(path, "1 split C x 0 + C.1")
            regress_split.write_command(path, "2 split C x 0 + C.1")
            self.assertEqual(regress_split.read_lines(path), ["2 split C x 0 + C.1"])
            self.assertEqual(sorted(os.listdir(d)), ["LayoutCommand.txt"])
            self.assertEqual(regress_split.read_lines(os.path.join(d, "missing.txt")), [])


if __name__ == "__main__":
    unittest.main()
