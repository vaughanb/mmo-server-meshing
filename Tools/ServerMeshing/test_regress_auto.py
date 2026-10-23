"""Unit tests for regress_auto.py (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py"): the three
flows against a fake rig whose runner mirrors the policy's lines under `auto` as the burn file and the bots change."""
import json
import os
import re
import tempfile
import unittest
from unittest import mock

import regress_remote
import regress_auto
import regress_merge
import regress_split
from test_regress_split import MAP, MAP2, ZONES, apply, own

PLANNED = "Policy: split of C planned: x 0 + child C.1, util 0.92 -> peak 0.71 (C 0.71, C.1 0.71), 2 cut(s) tried"
SPLIT = ["Split split-1: requested C x 0 + child C.1", "Split split-1: e1 broadcast 1a2b/2", "Split split-1: e2 broadcast 1a2b/3",
         "Split split-1: drain gate open", "Split split-1: e3 broadcast 1a2b/4", "Split split-1: done"]
MEASURED = "Policy: split split-1 measured: predicted peak 0.71, measured 0.46 (C 0.46, C.1 0.03), relief 50 %"
MERGE = ["Policy: merge of C C.1 planned: predicted 0.07 (C 0.06, C.1 0.03)", "Merge merge-1: requested C C.1",
         "Merge merge-1: e1 broadcast 1a2b/5", "Merge merge-1: e2 broadcast 1a2b/6", "Merge merge-1: retire sent",
         "Merge merge-1: C.1 exited", "Merge merge-1: channel C.1 removed", "Merge merge-1: e3 broadcast 1a2b/7",
         "Merge merge-1: tidy broadcast 1a2b/8", "Merge merge-1: done",
         "Policy: merge merge-1 measured: predicted 0.07, measured 0.06 (C)"]
COARSE_PLANNED = "Policy: split of C planned: z 20 - child C.1, util 0.7 -> peak 0.4 (C 0.4, C.1 0.38), 42 cut(s) tried"
COARSE_MEASURED = "Policy: split split-1 measured: predicted peak 0.4, measured 0.42 (C 0.42, C.1 0.4), relief 40 %"
REFUSED = "Policy: split of C refused: best peak 0.93 over 0.75 x util 0.9"
STAMP = "2026-10-16 10:00:"


def at(second, text):
    return f" INFO Mesh [{STAMP}{second:02d}] - {text}"


def window(second, owned):
    return at(second, f"Work window 5.00 s: frames 300 work mean 7.000 ms p95 8.00 ms max 9.0 ms wait 9.000 ms period 16.670 ms "
                      f"budget 16.67 ms sat 0.0% probe=Valid | owned players {owned} monsters 0 | puppets players 0 monsters 0 "
                      f"| clients 0 cpu 50%")


def autosplit_lines():
    """C and C.1 after the automatic split: C's Handoff START of each R-bot (meshbot2..5 stand on the + side)."""
    c = [own("C")] + [at(1, f"Handoff START meshbot{n}Char (id) zone C->C.1 pos=(88, 0, 1)") for n in range(2, 6)]
    return {"C": c, "C.1": [own("C.1")]}


def coarse_lines(adopted=100, totals=((100, 100), (100, 100), (100, 100))):
    """C and C.1 after a Coarse split at z = 20 (the child takes the south): applies, adoptions and paired windows."""
    whole, south, north = (-300, -300, 600, 600), (-300, -300, 600, 320), (-300, 20, 600, 280)
    c = [at(0, f"Own cell from channel id: C of map {MAP}, grid (0,0), world x [-300, 300) z [-300, 300), 1 neighbours"),
         apply("C", 1, whole, whole), apply("C", 2, whole, whole), apply("C", 3, north, whole), apply("C", 4, north, north)]
    c1 = [at(0, f"Own cell from layout: C.1 of map {MAP}, grid (0,0), world x [-300, 300) z [-300, 20), 1 neighbours"),
          apply("C.1", 2, None, south), apply("C.1", 3, south, south), apply("C.1", 4, south, south)]
    c1 += [at(1, f"Synthetic adopted load-{k} zone C->C.1") for k in range(adopted)]
    for i, (a, b) in enumerate(totals):
        c.append(window(10 + 5 * i, a))
        c1.append(window(11 + 5 * i, b))
    return {"C": c, "C.1": c1, "C2": [own("C2", MAP2)]}


class FakeRig:
    """The parts of regress that regress_auto uses. tick() plays the runner: it mirrors lines under `auto` as the burn
    file changes (autosplit) or time passes (the Coarse flows)."""
    MAP, MAP2, ZONES, RIG_REGISTRY, EXE = MAP, MAP2, ZONES, "registry.asset", "kit.exe"

    def __init__(self, folder, name, lines=None, results=None, standing=True, measured=MEASURED, merge=MERGE,
                 coarse=(COARSE_PLANNED, COARSE_MEASURED), refusal_every=60, split_lines=SPLIT, planned=PLANNED):
        self.TEMP, self.name = folder, name
        self.STATUS = os.path.join(folder, "PlayStatus.txt")
        self.LAYOUT_COMMAND_STATUS = os.path.join(folder, "LayoutCommandStatus.txt")
        self.COORDINATOR_STORE = os.path.join(folder, "store.json")
        with open(self.COORDINATOR_STORE, "w", encoding="utf-8") as f:
            json.dump({"format": 1, "broadcast": {"entries": []}, "hasStaged": False}, f)
        self.lines = lines if lines is not None else (autosplit_lines() if name == "autosplit" else coarse_lines())
        self.results, self.standing = results or {}, standing
        self.measured, self.merge, self.coarse, self.refusal_every = measured, merge, coarse, refusal_every
        self.split_lines, self.planned = split_lines, planned
        self.calls, self.mirrored, self.killed, self.playing_since, self.now = [], set(), False, None, 0.0
        self.refusals, self.burns = 0, []   # burns: the burn file's text each time a tick saw it change

    # ---- regress's API
    def db_reset(self, tester, bot):
        self.calls.append(("db_reset", tester, bot))

    def request_play(self, mode, x, z, env=None, enabled=True):
        self._play_env = dict(env or {})
        self.calls.append(("request_play", mode, x, z, self._play_env, enabled))
        self.playing_since = self.now

    def wait_play(self, timeout):
        self.calls.append(("wait_play",))

    def launch_bot(self, scenario, user, args, channel="C", observe_seconds=None, line_x=None, harvest_after_migration=False):
        self.calls.append(("launch_bot", user, tuple(args), channel, observe_seconds, line_x))
        log = os.path.join(self.TEMP, f"regress_{scenario}_{user}.log")
        x, z = float(args[args.index("-meshWalkX") + 1]), float(args[args.index("-meshWalkZ") + 1])
        text = [f"[MeshAuto] Standing at ({x:.2f}, 0.08, {z:.2f}) party=0; watching for 300s"] if self.standing else []
        result = self.results.get(user, "migrations=1 allReady=True maxGap=300ms maxJump=0.2m" if x > 0
                                  else "migrations=0 allReady=True maxGap=0ms maxJump=0m")
        text.append(f"[MeshAuto] RESULT stand: reached=True {result} -> PASS")
        with open(log, "w", encoding="utf-8") as f:
            f.write("\n".join(text) + "\n")
        return log

    def kill_bots(self):
        self.killed = True
        self.calls.append(("kill_bots",))

    def ps(self, script):
        return "0"

    def cell_ps(self, cell, script_of):
        """regress.cell_ps on one machine: the snippet for this rig's executable."""
        return self.ps(script_of(self.EXE, cell))

    def mirror_temp(self, name, text):
        self.calls.append(("mirror_temp", name, text))

    def set_marker(self, cell, name, kind):
        regress_remote.set_marker(self, cell, name, kind)

    def zone_lines(self, zone, start, day, map_id=MAP, suffixes=("info", "warn", "error")):
        return list(self.lines.get(zone, []))

    def graded_zones(self, day):
        return list(ZONES) + [("C.1", MAP)]

    @staticmethod
    def count(lines, pattern):
        rx = re.compile(pattern)
        return sum(1 for text in lines if rx.search(text))

    # ---- the runner
    def mirror(self, key, texts):
        if key in self.mirrored:
            return
        self.mirrored.add(key)
        with open(self.LAYOUT_COMMAND_STATUS, "a", encoding="utf-8") as f:
            f.write("".join(f"auto {t}\n" for t in texts))

    def burn(self):
        try:
            with open(os.path.join(self.TEMP, regress_auto.BURN_FILE_NAME), encoding="utf-8") as f:
                return f.read().strip()
        except FileNotFoundError:
            return None

    def tick(self):
        if self.name == "autosplit":
            burn = self.burn()
            if burn != (self.burns[-1] if self.burns else None):
                self.burns.append(burn)
            if burn == regress_auto.BURN_HOT:
                self.mirror("split", [self.planned] + self.split_lines)
            elif burn == regress_auto.BURN_WARM:
                self.mirror("measured", [self.measured])
            elif burn == "" and self.killed and "split" in self.mirrored:
                self.mirror("merge", self.merge)
            return
        if self.playing_since is None:
            return
        elapsed = self.now - self.playing_since
        if self.name == "autobenefit":
            if elapsed >= 30:
                self.mirror("split", [self.coarse[0]] + self.split_lines)
            if elapsed >= 45:
                self.mirror("measured", [self.coarse[1]])
        elif self.name == "autorefuse" and elapsed >= 30 and elapsed - 30 >= self.refusals * self.refusal_every:
            self.refusals += 1
            self.mirror(f"refusal{self.refusals}", [REFUSED])


class FakeClock:
    def __init__(self, rig):
        self.rig = rig
        rig.now = 1000.0

    def time(self):
        return self.rig.now

    def sleep(self, seconds):
        self.rig.now += seconds
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
        patches = [mock.patch.object(m.time, "time", clock.time) for m in (regress_auto, regress_split, regress_merge)]
        patches += [mock.patch.object(m.time, "sleep", clock.sleep) for m in (regress_auto, regress_split, regress_merge)]
        patches.append(mock.patch.object(regress_auto.provision, "reset_bot_positions"))
        patches.append(mock.patch.object(regress_split.rigtools, "asset_cell_modes", lambda path: {}))
        for p in patches:
            p.start()
        try:
            ar = regress_auto.run(name, rep, rig, "10:00:00", "2026-10-16", bots)
            if name != "autosplit":
                regress_auto.grade_layout(rep, rig, "10:00:00", "2026-10-16", ar)
        finally:
            for p in patches:
                p.stop()
        return rep, rig, ar


class AutosplitTests(unittest.TestCase):
    def test_passes_and_drives_the_burn_in_three_stages(self):
        rep, rig, ar = run("autosplit")
        self.assertEqual(rep.failed(), [])
        play = next(c for c in rig.calls if c[0] == "request_play")
        self.assertEqual((play[1], play[5]), ("stand", False))
        env = play[4]
        self.assertEqual(env["MESH_AUTO_OPS"], "split,merge")
        self.assertEqual(env["MESH_CELL_MODE"], f"{MAP}:Dynamic")
        self.assertEqual(env["MESH_NO_MONSTERS"], "1")
        self.assertTrue(env["MESH_PROBE_BURN_MS"].startswith("@") and os.path.isabs(env["MESH_PROBE_BURN_MS"][1:]))
        self.assertEqual(rig.burns, ["", regress_auto.BURN_HOT, regress_auto.BURN_WARM, ""])
        self.assertIn(("kill_bots",), rig.calls)
        self.assertEqual(ar.split.child, "C.1")
        self.assertTrue(ar.split_status["done"] and ar.merge_status["done"])
        bots = [c for c in rig.calls if c[0] == "launch_bot"]
        self.assertEqual(len(bots), 8)
        self.assertEqual({float(c[2][c[2].index("-meshWalkX") + 1]) for c in bots}, {88.0, -88.0})

    def test_failures(self):
        cases = {
            "no standing": ({"standing": False}, ["no burn"]),
            "a poor relief": ({"measured": MEASURED.replace("relief 50 %", "relief 12 %")}, ["at least 30 % relief"]),
            "a flap line": ({"measured": MEASURED + "\nauto Policy: flap on C: x; dwell and cooldown x2"}, ["policy lines only"]),
            "no merge": ({"merge": []}, ["planned the merge", "merge requested", "merge done"]),
            "a merge before the kill": ({"measured": MEASURED + "\nauto Merge merge-1: requested C C.1"}, ["no merge while the bots stood"]),
            "an R-bot never migrated": ({"results": {"meshbot2": "migrations=0 allReady=True maxGap=0"}}, ["meshbot2: 1 migration"]),
            "a refused request": ({"split_lines": ["Layout op refused: cooldown 9 s left on C"]}, ["accepted", "no request of the policy refused"]),
            "the wrong cut": ({"planned": PLANNED.replace("x 0 +", "z 0 +")}, ["planned x 0 + child C.1"]),
        }
        for name, (rig_args, fragments) in cases.items():
            with self.subTest(name):
                rep = run("autosplit", **rig_args)[0]
                failed = rep.failed()
                for fragment in fragments:
                    self.assertTrue(any(fragment in label for label in failed), f"{fragment!r} not in {failed}")


class CoarseTests(unittest.TestCase):
    def test_benefit_passes(self):
        rep, rig, ar = run("autobenefit")
        self.assertEqual(rep.failed(), [])
        play = next(c for c in rig.calls if c[0] == "request_play")
        self.assertEqual((play[1], play[5]), ("none", False))
        env = play[4]
        self.assertEqual(env["MESH_LAYOUT_OVERRIDE"], f"{MAP}=C@-300,-300,600,600")
        self.assertEqual((env["MESH_AUTO_OPS"], env["MESH_SPLIT_UTIL"], env["MESH_LOAD_PLAN"], env["MESH_LOAD_AREA"]), ("split", "0.3", "C:200", "0,0,100"))
        self.assertNotIn("MESH_LOAD_MOTION", env)
        self.assertEqual((ar.split.axis, ar.split.at, ar.split.side, ar.split.child), ("z", 20.0, "-", "C.1"))
        self.assertTrue(any("benefit: util before 0.7" in n for n in rep.notes))

    def test_benefit_failures(self):
        cases = {
            "off the prediction": ({"coarse": (COARSE_PLANNED, COARSE_MEASURED.replace("measured 0.42", "measured 0.55"))}, ["within 15%"]),
            "no real relief": ({"coarse": (COARSE_PLANNED.replace("util 0.7", "util 0.5"), COARSE_MEASURED)}, ["at most 0.75 x"]),
            "a second split": ({"split_lines": SPLIT + ["Split split-2: requested C x 0 + child C.2"]}, ["exactly one split"]),
            "synthetics lost": ({"lines": coarse_lines(totals=((100, 90), (100, 90), (100, 90)))}, ["200 synthetic players together"]),
            "few adopted": ({"lines": coarse_lines(adopted=10)}, ["adopted 10"]),
        }
        for name, (rig_args, fragments) in cases.items():
            with self.subTest(name):
                rep = run("autobenefit", **rig_args)[0]
                failed = rep.failed()
                for fragment in fragments:
                    self.assertTrue(any(fragment in label for label in failed), f"{fragment!r} not in {failed}")

    def test_refuse_passes_and_a_split_or_a_fast_repeat_fails(self):
        whole = (-300, -300, 600, 600)
        refuse = {"C": [at(0, f"Own cell from channel id: C of map {MAP}, grid (0,0), world x [-300, 300) z [-300, 300), 1 neighbours"),
                        apply("C", 1, whole, whole)], "C2": [own("C2", MAP2)]}
        rep, rig, ar = run("autorefuse", lines=refuse)
        self.assertEqual(rep.failed(), [])
        env = next(c for c in rig.calls if c[0] == "request_play")[4]
        self.assertEqual((env["MESH_LOAD_AREA"], env["MESH_PUPPET_MOTION"], env["MESH_PLANNER_WEIGHTS"], env["MESH_LOAD_PLAN"]), ("0,0,30", "steered", "1,0.126,0.2,9.1", "C:200"))
        self.assertIsNone(ar.split)
        rep = run("autorefuse", lines=refuse, refusal_every=20)[0]
        self.assertTrue(any("once a minute" in label for label in rep.failed()), rep.failed())


if __name__ == "__main__":
    unittest.main()
