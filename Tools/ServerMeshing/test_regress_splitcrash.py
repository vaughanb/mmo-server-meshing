"""Unit tests for regress_splitcrash's scenario flows against a fake rig: no editor, database, process or SSH is touched
(python -m unittest discover -s Tools/ServerMeshing -p "test_*.py"). The fake extends test_regress_split's: its zone
lines carry real stamps (some relative to the moment the flow killed C), its status file grows a few lines per poll,
and ps answers the process counts and the kill."""
import datetime
import os
import tempfile
import time
import unittest
from unittest import mock

import regress_split
import regress_splitcrash
import rigsplit
import test_regress_split as ts

MAP, MAP2 = ts.MAP, ts.MAP2
T0 = datetime.datetime(2026, 10, 16, 10, 0, 0)
EXE = r"D:\rig\Build\Server\kit-ce-project.exe"
P, K, R = (-100.0, -100.0, 200.0, 200.0), (-100.0, -100.0, 100.0, 200.0), (0.0, -100.0, 100.0, 200.0)
INC = "8dcf02"
R_BOTS = [f"meshbot{k}" for k in range(2, 6)]
P_BOTS = [f"meshbot{k}" for k in range(6, 10)]


def L(s, text, level="INFO", tag="Mesh", anchor="start"):
    """One zone line: `s` seconds after T0 (anchor 'start') or after the second the flow killed C (anchor 'kill')."""
    return (anchor, s, text, level, tag)


def own(zone, s=0, anchor="start"):
    map_id = MAP2 if zone == "C2" else MAP
    x, z = ts.WORLD.get(zone, (300, -100))
    return L(s, f"Own cell from channel id: {zone} of map {map_id}, grid (0,0), world x [{x}, {x + 200}) z [{z}, {z + 200}), "
                "8 neighbours", anchor=anchor)


def own_child(s=0):
    return L(s, f"Own cell from layout: C.1 of map {MAP}, grid (0,0), world x [0, 100) z [-100, 100), 5 neighbours")


def apply(cell, version, owned, interest, s=0, anchor="start"):
    return L(s, f"Layout 1a2b/{version} applied: {cell} owns {rigsplit.format_rect(owned)} interest "
                f"{rigsplit.format_rect(interest)}, 8 targets", anchor=anchor)


def window(s, monsters, anchor="kill"):
    return L(s, "Work window 5.00 s: frames 300 work mean 1.000 ms p95 2.00 ms max 3.0 ms wait 10.000 ms period 16.667 "
                f"ms budget 16.67 ms sat 0.0% probe=Ok | owned players 0 monsters {monsters} | puppets players 0 monsters "
                "3 | clients 0 cpu 5%", anchor=anchor)


def others(versions, extra=()):
    out = {"C2": [own("C2")]}
    for zone, _ in ts.ZONES:
        if zone in ("C", "C2"):
            continue
        x, z = ts.WORLD[zone]
        rect = (float(x), float(z), 200.0, 200.0)
        out[zone] = [own(zone)] + [apply(zone, v, rect, rect) for v in versions] + list(extra)
    return out


PRUNE = L(5, "Retired zone C.1: link closed, 0 record(s) released")
SPLIT_STATES = ["requested C x 0 + child C.1", "channel added", "spawning", "bound 8dcf01", "e1 staged", "e1 acked",
                "e1 broadcast 1a2b/2"]
STATUS = {
    "prewarm": SPLIT_STATES + ["aborted C.1 lost", "channel C.1 removed", "retired broadcast 1a2b/3"],
    "drain": SPLIT_STATES + ["e1 gate open", "e2 staged", "e2 acked", "e2 broadcast 1a2b/3", "C.1 lost, op paused",
                             "Supervisor: C.1 down 21 s, restart 1 of 3 requested",
                             f"Supervisor: C.1 back, incarnation {INC}", f"C.1 rebound {INC}, op resumed",
                             "drain gate open", "e3 broadcast 1a2b/4", "done"],
    "base": SPLIT_STATES + ["e1 gate open", "e2 staged", "e2 acked", "e2 broadcast 1a2b/3", "drain gate open",
                            "e3 broadcast 1a2b/4", "done"],
}
COUNTS = {"prewarm": ["0", "1", "1", "0"], "drain": ["0", "1", "1", "0", "0", "1"], "base": ["0"]}
FELL = ("Migration to 192.168.0.2:8010 | SEAMLESS connected=False playerReady=False keptController=False fellBack=True "
        "| standIns=1ms")


def split_lines():
    """The split's four applies on C, three on C.1."""
    c = [own("C"), L(0, "LayoutHello sent: cell C incarnation aaaa layout 0/0"), apply("C", 1, P, P), apply("C", 2, P, P),
         apply("C", 3, K, P), apply("C", 4, K, K)]
    c1 = [own_child(), apply("C.1", 2, None, R), apply("C.1", 3, R, R), apply("C.1", 4, R, R)]
    return c, c1


def lines_of(kind):
    """{zone: [line tuples]} of a case that went to plan."""
    if kind == "prewarm":
        out = others((1, 2, 3), [PRUNE])
        out["C"] = [own("C"), apply("C", 1, P, P), apply("C", 2, P, P), apply("C", 3, P, P), PRUNE]
        out["C.1"] = [own_child(), apply("C.1", 2, None, R),
                      L(3, "FAULT: dying on the e1 broadcast naming C.1 Pending (MESH_FAULT_DIE_ON_PREWARM)", "ERROR")]
        return out
    out = others((1, 2, 3, 4))
    c, c1 = split_lines()
    if kind == "drain":
        c += [L(10, f"Handoff START {u}Char (id) zone C->C.1 pos=(1, 0, 1)") for u in R_BOTS]
        c += [L(20, "Handoff ROLLBACK meshbot2Char -> zone C.1: link lost; the puppet is held 10s", "WARN"),
              L(55, "Handoff START meshbot2Char (id) zone C->C.1 pos=(1, 0, 1)")]
        c += [L(60, f"Handoff ACK {u}Char by zone C.1: registered 50 ms after the notice, 300 ms after START "
                    "(promoted=True)") for u in R_BOTS]
        c1 = c1[:3] + [L(15, "FAULT: dying on handoff notice for meshbot3Char (MESH_FAULT_DIE_ON_DRAIN)", "ERROR"),
                       own_child(50), apply("C.1", 3, R, R, 50), apply("C.1", 4, R, R, 51)]
    else:
        c1 += [L(10, "Monster promoted by handoff C:aaaa:5 C->C.1 at (40, 0, 10) hp=5/5 home=C target=none"),
               L(11, "Monster spawned by handoff C:aaaa:6 C->C.1 at (40, 0, 10) hp=5/5 home=C target=none"),
               L(3, "Borrowed monster C:aaaa:5 released (home zone link lost)", "WARN", anchor="kill"),
               L(12, "Borrowed monster C:aaaa:6 released (home zone restarted)", "WARN", anchor="kill")]
        c1 += [window(11 + 5 * i, 3) for i in range(18)]
        c += [own("C", 8, "kill"), apply("C", 4, K, K, 9, "kill"),
              L(9, "Spawn areas of C: 5 monster(s) planned in the base rectangle", anchor="kill")]
        c += [L(15, f"10:02:00.000 S player registered {u}Char conn=0 at (1, 0, 1)", tag="MeshTrace", anchor="kill")
              for u in P_BOTS]
        c += [window(10 + 5 * i, 2) for i in range(18)]
    out["C"], out["C.1"] = c, c1
    return out


class FakeCrashRig(ts.FakeRig):
    """test_regress_split's FakeRig for a crash case: stamped zone lines (lines_of), the status written a few lines per
    poll, ps answering process counts from `counts` (its last value repeats) and the kill with `kill_out`."""
    EXE = EXE

    def __init__(self, folder, name, lines=None, status=None, counts=None, kill_out="1", results=None, bot_lines=None):
        super().__init__(folder, "split", lines={}, results=results)
        self.kind = regress_splitcrash.CASES[name].kind
        self.zone_spec = lines_of(self.kind) if lines is None else lines
        self.status = STATUS[self.kind] if status is None else status
        self.counts, self.kill_out = list(counts or COUNTS[self.kind]), kill_out
        self.bot_lines = bot_lines or {}
        self.kill_at, self.scripts, self.written = None, [], 0

    def zone_lines(self, zone, start, day, map_id=MAP, suffixes=("info", "warn", "error")):
        levels = {s.upper() for s in suffixes} | ({"INFO"} if "info" in suffixes else set())
        kill = (datetime.datetime.fromtimestamp(self.kill_at).replace(microsecond=0) if self.kill_at is not None else None)
        out = []
        for anchor, s, text, level, tag in self.zone_spec.get(zone, []):
            if level not in levels or (anchor == "kill" and kill is None):
                continue
            t = (T0 if anchor == "start" else kill) + datetime.timedelta(seconds=s)
            out.append(f" {level} {tag} [{t:%Y-%m-%d %H:%M:%S}] - {text}")
        return out

    def launch_bot(self, scenario, user, args, channel="C", observe_seconds=None, line_x=None,
                   harvest_after_migration=False):
        self.calls.append(("launch_bot", user, tuple(args), channel, observe_seconds, line_x, harvest_after_migration))
        log = os.path.join(self.folder, f"regress_{scenario}_{user}.log")
        x, z = float(args[args.index("-meshWalkX") + 1]), float(args[args.index("-meshWalkZ") + 1])
        far, extra = x >= line_x, []
        if self.kind == "drain" and far:
            n = 2 if user == "meshbot2" else 1
            default = f"migrations={n} allReady=True maxGap=300 maxJump=0.2 relogins=0"
            extra = ([FELL] if n == 2 else []) + [f"MIGRATION {n}: gap=300 ready=True jump=0.2 pos={x:g},{z:g}"]
        elif self.kind == "base" and far:
            default = "migrations=1 allReady=True maxGap=300 maxJump=0.2 relogins=0"
        elif self.kind == "base":
            default = "migrations=0 allReady=True maxGap=0 maxJump=0 relogins=1"
            extra = ["Session lost (Timeout); re-login 1", "Re-login routed to 192.168.0.2:8004"]
        else:
            default = "migrations=0 allReady=True maxGap=0 maxJump=0 relogins=0"
        text = [f"[MeshAuto] Standing at ({x:.2f}, 0.08, {z:.2f}) party=0; watching for {observe_seconds:g}s"]
        text += [f"[MeshAuto] {t}" for t in extra + self.bot_lines.get(user, [])]
        text.append(f"[MeshAuto] RESULT stand: reached=True {self.results.get(user, default)} -> PASS")
        with open(log, "w", encoding="utf-8") as f:
            f.write("\n".join(text) + "\n")
        return log

    def ps(self, script):
        self.scripts.append(script)
        if "Stop-Process" in script:
            self.kill_at = time.time()
            return self.kill_out
        return self.counts.pop(0) if len(self.counts) > 1 else self.counts[0]

    def tick(self):
        """The play runner: takes the command and appends three more status lines per poll under its seq."""
        if self.command is None and os.path.exists(self.LAYOUT_COMMAND):
            with open(self.LAYOUT_COMMAND, encoding="utf-8") as f:
                self.command = f.read().strip()
        if self.command is None or self.written >= len(self.status):
            return
        seq = self.command.split()[0]
        more = self.status[self.written:self.written + 3]
        self.written += len(more)
        with open(self.LAYOUT_COMMAND_STATUS, "a", encoding="utf-8") as f:
            f.write("".join(f"{seq} {s if s.startswith(('Layout op', 'Supervisor:')) else 'Split split-1: ' + s}\n"
                            for s in more))


def run(name, **rig_args):
    with tempfile.TemporaryDirectory() as d:
        rig = FakeCrashRig(d, name, **rig_args)
        clock = ts.FakeClock(rig)
        clock.now = T0.timestamp()
        rep, bots = ts.Report(), []
        with mock.patch.object(regress_split.time, "time", clock.time), \
                mock.patch.object(regress_split.time, "sleep", clock.sleep), \
                mock.patch.object(regress_split.provision, "reset_bot_positions"), \
                mock.patch.object(regress_split.rigtools, "asset_cell_modes", lambda path: {}):
            cr = regress_splitcrash.run(name, rep, rig, "10:00:00", "2026-10-16", bots)
            regress_splitcrash.grade_layout(rep, rig, "10:00:00", "2026-10-16", cr)
        return rep, rig, bots, cr


def assert_fails(test, rep, fragments):
    failed = rep.failed()
    for fragment in fragments:
        test.assertTrue(any(fragment in label for label in failed), f"{fragment!r} not in {failed}")


def without(lines, zone, needle):
    return dict(lines, **{zone: [t for t in lines[zone] if needle not in t[2]]})


class CrashScenarioTests(unittest.TestCase):
    def test_prewarm_passes(self):
        rep, rig, bots, cr = run("splitcrash_prewarm")
        self.assertEqual(rep.failed(), [])
        play = rig.calls[1]
        self.assertEqual(play[4], {"MESH_CELL_MODE": f"{MAP}:Dynamic", "MESH_MONSTER_AREAS": "only:Spawn_Interior_C",
                                   "MESH_FAULT_DIE_ON_PREWARM": "C.1"})
        self.assertTrue(play[5])
        launches = [c for c in rig.calls if c[0] == "launch_bot"]
        self.assertEqual([c[1] for c in launches], R_BOTS + P_BOTS)
        self.assertTrue(all(c[4] == 180 and not c[6] for c in launches))
        self.assertEqual(len(bots), 8)
        self.assertTrue(all("-channelId C.1 *" in s and "Get-CimInstance" in s for s in rig.scripts))
        self.assertGreaterEqual(len(rig.scripts), regress_splitcrash.GONE_AFTER_ABORT_S / regress_splitcrash.SAMPLE_EVERY_S)
        self.assertTrue(any("process counts during the split [0, 1, 0]" in n for n in rep.notes), rep.notes)

    def test_drain_passes(self):
        rep, rig, _, cr = run("splitcrash_drain")
        self.assertEqual(rep.failed(), [])
        self.assertEqual(rig.calls[1][4]["MESH_FAULT_DIE_ON_DRAIN"], "C.1")
        self.assertNotIn("MESH_LOAD_PLAN", rig.calls[1][4])
        self.assertTrue(all(c[4] == 300 for c in rig.calls if c[0] == "launch_bot"))
        self.assertEqual(cr.r.status["done"], True)

    def test_base_passes(self):
        rep, rig, _, _ = run("splitcrash_base")
        self.assertEqual(rep.failed(), [])
        self.assertFalse(rig.calls[1][5])                       # no tester
        kills = [s for s in rig.scripts if "Stop-Process" in s]
        self.assertEqual(len(kills), 1)
        self.assertIn("-like '*-channelId C *'", kills[0])
        self.assertTrue(any("borrowed static" in n for n in rep.notes), rep.notes)

    def test_prewarm_failures(self):
        base = lines_of("prewarm")
        done = SPLIT_STATES + ["e1 gate open", "e2 staged", "e2 acked", "e2 broadcast 1a2b/3", "drain gate open",
                               "e3 broadcast 1a2b/4", "done"]
        cases = {
            "no abort": ({"status": done}, ["aborted 'C.1 lost'", "neither done"]),
            "a restart requested": ({"status": STATUS["prewarm"] + ["Supervisor: C.1 down 20 s, restart 1 of 3 requested"]},
                                    ["no restart requested"]),
            "C.1 alive after the abort": ({"counts": ["1"]}, ["no process with"]),
            "a bot migrated": ({"results": {"meshbot2": "migrations=1 relogins=0"}}, ["meshbot2: no migration"]),
            "a bot re-logged in": ({"results": {"meshbot7": "migrations=0 relogins=1"}}, ["meshbot7: no migration"]),
            "no FAULT": ({"lines": without(base, "C.1", "FAULT")}, ["C.1: 0 'FAULT"]),
            "a FAULT on C": ({"lines": dict(base, C=base["C"] + [base["C.1"][-1]])}, ["zone C:"]),
            "no prune on N": ({"lines": without(base, "N", "Retired zone")}, ["zone N: applied retired broadcast"]),
            "C.1 owned R": ({"lines": dict(base, **{"C.1": base["C.1"] + [apply("C.1", 3, R, R)]})}, ["zone C.1:"]),
        }
        for name, (rig_args, fragments) in cases.items():
            with self.subTest(name):
                assert_fails(self, run("splitcrash_prewarm", **rig_args)[0], fragments)

    def test_drain_failures(self):
        base = lines_of("drain")
        s = STATUS["drain"]
        early_ack = dict(base, C=[t if not t[2].startswith("Handoff ACK meshbot4") else t[:1] + (40,) + t[2:]
                                  for t in base["C"]])
        cases = {
            "never paused": ({"status": [t for t in s if "paused" not in t]}, ["lost and paused"]),
            "rebound to another incarnation": ({"status": [t.replace(f"rebound {INC}", "rebound 99") for t in s]},
                                               ["rebound to the incarnation"]),
            "frozen": ({"status": s[:-1] + ["frozen e3 not acknowledged in 60 s"]}, ["neither aborted, frozen"]),
            "never came back": ({"counts": ["0", "1", "0"]}, ["process count 1 -> 0 -> 1"]),
            "no rollback": ({"lines": without(base, "C", "Handoff ROLLBACK")}, ["Handoff ROLLBACK"]),
            "an ack before the restart": ({"lines": early_ack}, ["meshbot4: migrations and START"]),
            "one START too many": ({"lines": dict(base, C=base["C"] + [base["C"][9]])}, ["meshbot5: migrations"]),
            "a P-bot migrated": ({"results": {"meshbot8": "migrations=1 relogins=0"}}, ["meshbot8: no migration"]),
            "C.1 never restarted": ({"lines": dict(base, **{"C.1": base["C.1"][:4]})}, ["zone C.1:"]),
            "no FAULT": ({"lines": without(base, "C.1", "FAULT")}, ["C.1: 0 'FAULT"]),
        }
        for name, (rig_args, fragments) in cases.items():
            with self.subTest(name):
                assert_fails(self, run("splitcrash_drain", **rig_args)[0], fragments)

    def test_base_failures(self):
        base = lines_of("base")
        c_first = [t for t in base["C"] if t[0] == "start"]
        cases = {
            "the kill found nothing": ({"kill_out": "0"}, ["killed 0 process(es)"]),
            "C never came back": ({"lines": dict(base, C=c_first)}, ["came back and applied", "zone C:"]),
            "a held monster not released": ({"lines": without(base, "C.1", "C:aaaa:6 released")}, ["released the 2"]),
            "too many monsters": ({"lines": dict(base, **{"C.1": [t for t in base["C.1"] if "Work window" not in t[2]]
                                                          + [window(11 + 5 * i, 4) for i in range(18)]})},
                                  ["owned monsters"]),
            "no spawn areas line": ({"lines": without(base, "C", "Spawn areas")}, ["Spawn areas of C"]),
            "a P-bot never re-logged in": ({"results": {"meshbot6": "migrations=0 relogins=0"}},
                                           ["meshbot6: re-logged in once"]),
            "a P-bot not back on C": ({"lines": without(base, "C", "registered meshbot7Char")}, ["meshbot7:"]),
            "an R-bot re-logged in": ({"results": {"meshbot3": "migrations=1 relogins=1"}}, ["meshbot3:"]),
        }
        for name, (rig_args, fragments) in cases.items():
            with self.subTest(name):
                assert_fails(self, run("splitcrash_base", **rig_args)[0], fragments)

    def test_base_with_nothing_held_is_a_note(self):
        base = lines_of("base")
        lines = dict(base, **{"C.1": [t for t in base["C.1"] if "by handoff" not in t[2]]})
        rep = run("splitcrash_base", lines=lines)[0]
        self.assertEqual(rep.failed(), [])
        self.assertTrue(any("held no monster" in n for n in rep.notes), rep.notes)

    def test_the_split_not_sent(self):
        with tempfile.TemporaryDirectory() as d:
            rig = FakeCrashRig(d, "splitcrash_drain")
            rig.launch_bot = lambda *a, **k: os.path.join(d, "missing.log")
            clock = ts.FakeClock(rig)
            rep = ts.Report()
            with mock.patch.object(regress_split.time, "time", clock.time), \
                    mock.patch.object(regress_split.time, "sleep", clock.sleep), \
                    mock.patch.object(regress_split.provision, "reset_bot_positions"):
                cr = regress_splitcrash.run("splitcrash_drain", rep, rig, "10:00:00", "2026-10-16", [])
        assert_fails(self, rep, ["split: not sent"])
        self.assertIsNone(cr.r.seq)
        self.assertEqual(rig.scripts, [])


if __name__ == "__main__":
    unittest.main()
