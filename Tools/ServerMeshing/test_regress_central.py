"""Unit tests for regress_central's scenario flows against a fake rig: no editor, database, process or SSH is touched
(python -m unittest discover -s Tools/ServerMeshing -p "test_*.py"). The fake extends test_regress_splitcrash's: it renders
the central's kit log per lifetime (the second anchored on the moment the flow restarted the central), answers the merge
command with Merge status lines, plays the tester's RESULT, and regress_procs' restart is replaced by a stamp."""
import datetime
import json
import os
import tempfile
import unittest
from unittest import mock

import regress_central
import regress_merge
import regress_split
import regress_splitcrash
import rigsplit
import test_regress_split as ts
import test_regress_splitcrash as tc

MAP, MAP2 = ts.MAP, ts.MAP2
T0 = tc.T0
P, K, R = tc.P, tc.K, tc.R
R_BOTS, P_BOTS = tc.R_BOTS, tc.P_BOTS
INC1, INC2 = "1a2b", "1a2c"
SPLIT_STATUS = tc.STATUS["base"]   # the split to done: e1 1a2b/2, e2 1a2b/3, e3 1a2b/4
MERGE_STATUS = ["requested C C.1", "e1 broadcast 1a2b/5", "e1 gate open", "e2 staged", "e2 acked", "e2 broadcast 1a2b/6"]
TESTER_RESULT = {"split": "RESULT stand: reached=True migrations=1 allReady=True maxGap=300 maxJump=0.2 relogins=0 -> PASS",
                 "merge": "RESULT stand: reached=True migrations=2 allReady=True maxGap=300 maxJump=0.2 relogins=0 -> PASS"}


def L(s, text, level="INFO", tag="Mesh", anchor="start"):
    return (anchor, s, text, level, tag)


def apply(cell, inc, version, owned, interest, s=0, anchor="start"):
    return L(s, f"Layout {inc}/{version} applied: {cell} owns {rigsplit.format_rect(owned)} interest {rigsplit.format_rect(interest)}, 8 targets", anchor=anchor)


def restart_lines(cell, pair="1a2b/4"):
    """What every cell logs around the restart: a second hello, the adopted pair answered, the database reconnect."""
    return [L(0, f"LayoutHello sent: cell {cell} incarnation aaaa layout 0/0"),
            L(3, "Database connection lost; reconnecting to 127.0.0.1:6100", "WARN", anchor="kill"),
            L(14, f"LayoutHello sent: cell {cell} incarnation aaaa layout {pair}", anchor="kill"),
            L(15, f"Layout answer {pair} on {cell}: already applied", anchor="kill"),
            L(45, "Database connection to 127.0.0.1:6100 is open", anchor="kill")]


def summary(inc, outcome, pair, s, anchor):
    return L(s, f"Coordinator {inc}: Dynamic maps {MAP} {outcome}; store D:/x/Temp/ServerMesh.Coordinator.json (loaded); broadcast {pair}", anchor=anchor)


def central_lines(kind):
    """The central's log: the first lifetime (the split), the second (the recovery and what the case expects of it)."""
    first = [summary(INC1, "AdoptStatic", "1a2b/1", 0, "start"), L(5, "Split split-1: requested C x 0 + child C.1"), L(40, "Split split-1: done")]
    adopted_pair = "1a2b/6" if kind == "merge" else "1a2b/4"
    second = [summary(INC2, "Waiting", adopted_pair, 20, "kill"),  # written at Start, before any hello
              L(21, f"Coordinator recovery: map {MAP} adopted layout {adopted_pair} from the store with 2 entries (C, C.1)", anchor="kill"),
              L(21, "Coordinator policy: cooldowns start fresh for C after the restart; flap multipliers, measurements and restart counts forgotten", anchor="kill")]
    if kind == "split":
        second.append(L(30, "Login routing: meshbotChar at (60.00, 0.08, 70.00) -> channel C.1 (client asked for 'C', row says 'C')", anchor="kill"))
    if kind == "merge":
        second += [L(22, "Merge merge-1: resumed at DrainGate after a central restart", anchor="kill"), L(50, "Merge merge-1: drain gate open", anchor="kill"),
                   L(50, "Merge merge-1: retire sent", anchor="kill"), L(60, "Merge merge-1: C.1 exited", anchor="kill"),
                   L(60, "Merge merge-1: channel C.1 removed", anchor="kill"), L(60, "Merge merge-1: e3 broadcast 1a2c/7", anchor="kill"),
                   L(62, "Merge merge-1: tidy broadcast 1a2c/8", anchor="kill"), L(63, "Merge merge-1: done", anchor="kill")]
    if kind == "base":
        second.append(L(25, f"Login routing: no server for cell C of {MAP} (meshbot6Char at (-20.0, 0.0, 75.0)); the nearest live cell N serves it", "WARN", anchor="kill"))
    return first + second


def lines_of(kind):
    """{zone: [line tuples]} of a case that went to plan."""
    out = {"C2": [tc.own("C2")] + restart_lines("C2", "1a2b/6" if kind == "merge" else "1a2b/4")}
    versions = (1, 2, 3, 4)
    for zone, _ in ts.ZONES:
        if zone in ("C", "C2"):
            continue
        x, z = ts.WORLD[zone]
        rect = (float(x), float(z), 200.0, 200.0)
        out[zone] = [tc.own(zone)] + [apply(zone, INC1, v, rect, rect) for v in versions] + restart_lines(zone, "1a2b/6" if kind == "merge" else "1a2b/4")
        if kind == "merge":
            out[zone] += [apply(zone, INC1, 5, rect, rect), apply(zone, INC1, 6, rect, rect),
                          apply(zone, INC2, 7, rect, rect, 60, "kill"), apply(zone, INC2, 8, rect, rect, 62, "kill"),
                          L(61, "Retired zone C.1: link closed, 0 record(s) released", anchor="kill")]
    c = [tc.own("C"), apply("C", INC1, 1, P, P), apply("C", INC1, 2, P, P), apply("C", INC1, 3, K, P), apply("C", INC1, 4, K, K)]
    c1 = [tc.own_child(), apply("C.1", INC1, 2, None, R), apply("C.1", INC1, 3, R, R), apply("C.1", INC1, 4, R, R)]
    if kind == "split":
        c += restart_lines("C")
        c1 += restart_lines("C.1") + [L(35, "10:02:00.000 S player registered meshbotChar conn=0 at (60, 0, -60)", tag="MeshTrace", anchor="kill")]
    elif kind == "merge":
        c += restart_lines("C", "1a2b/6") + [apply("C", INC1, 5, K, P), apply("C", INC1, 6, P, P), apply("C", INC2, 7, P, P, 60, "kill"),
                                             apply("C", INC2, 8, P, P, 62, "kill"), L(61, "Retired zone C.1: link closed, 0 record(s) released", anchor="kill")]
        c1 += restart_lines("C.1", "1a2b/6") + [apply("C.1", INC1, 5, R, R), apply("C.1", INC1, 6, None, R),
                                                L(55, "Merge retire: C.1 drained, quitting", anchor="kill")]
        for u in R_BOTS:
            c1.append(L(52, f"Handoff START {u}Char (id) zone C.1->C pos=(1, 0, 1)", anchor="kill"))
    else:
        c += [L(0, "LayoutHello sent: cell C incarnation aaaa layout 0/0"),
              L(14, "LayoutHello sent: cell C incarnation aaaa layout 1a2b/4", anchor="kill"),
              L(14, "FAULT: dying on layout hello 2 (MESH_FAULT_DIE_ON_HELLO)", "ERROR", anchor="kill"),
              L(22, f"Own cell from channel id: C of map {MAP}, grid (0,0), world x [-100, 100) z [-100, 100), 8 neighbours", anchor="kill"),
              L(23, "LayoutHello sent: cell C incarnation bbbb layout 0/0", anchor="kill"),
              apply("C", INC1, 4, K, K, 24, "kill"), L(24, "Spawn areas of C: 5 monster(s) planned in the base rectangle", anchor="kill")]
        c += [L(30, f"10:02:00.000 S player registered {u}Char conn=0 at (1, 0, 1)", tag="MeshTrace", anchor="kill") for u in P_BOTS]
        c += [tc.window(25 + 5 * i, 2) for i in range(18)]
        c1 += restart_lines("C.1") + [L(10, "Monster promoted by handoff C:aaaa:5 C->C.1 at (40, 0, 10) hp=5/5 home=C target=none"),
                                      L(3, "Borrowed monster C:aaaa:5 released (home zone link lost)", "WARN", anchor="kill")]
        c1 += [tc.window(26 + 5 * i, 3) for i in range(18)]
    out["C"], out["C.1"] = c, c1
    return out


def store_of(kind):
    if kind == "merge":
        return {"format": 1, "broadcast": {"pair": {"coordinatorIncarnation": 0x1a2c, "version": 8}, "entries": []}, "hasStaged": False, "hasOperation": False}
    entries = [{"id": "C", "mapId": MAP, "baseId": "C", "state": 2}, {"id": "C.1", "mapId": MAP, "baseId": "C", "state": 2}]
    return {"format": 1, "broadcast": {"pair": {"coordinatorIncarnation": 0x1a2b, "version": 4}, "entries": entries}, "hasStaged": False, "hasOperation": False}


class FakeCentralRig(tc.FakeCrashRig):
    """test_regress_splitcrash's fake for a central-restart case: the central's log per lifetime (the second anchored on
    the restart), Merge status lines for a merge command, the tester's RESULT, the store file."""
    def __init__(self, folder, name, lines=None, central=None, status=None, merge_status=None, results=None, counts=None,
                 tester_result=None, store=None):
        ts.FakeRig.__init__(self, folder, "split", lines={}, results=results)
        self.kind = regress_central.CASES[name].kind
        self.zone_spec = lines_of(self.kind) if lines is None else lines
        self.central = central_lines(self.kind) if central is None else central
        self.status = SPLIT_STATUS if status is None else status
        self.merge_status = MERGE_STATUS if merge_status is None else merge_status
        self.counts, self.kill_out = list(counts or (["1", "0"] if self.kind == "merge" else ["1"])), "1"
        self.bot_lines = {}
        self.kill_at, self.scripts, self.written, self.merge_written = None, [], 0, 0
        self.merge_command = None
        self.tester_result = TESTER_RESULT.get(self.kind, "") if tester_result is None else tester_result
        self.COORDINATOR_STORE = os.path.join(folder, "store.json")
        with open(self.COORDINATOR_STORE, "w", encoding="utf-8") as f:
            json.dump(store_of(self.kind) if store is None else store, f)

    def request_play(self, mode, x, z, env=None, enabled=True, observe_seconds=None, line_x=None):
        ts.FakeRig.request_play(self, mode, x, z, env=env, enabled=enabled)
        self.calls[-1] += (observe_seconds,)

    def _render(self, spec, levels):
        kill = datetime.datetime.fromtimestamp(self.kill_at).replace(microsecond=0) if self.kill_at is not None else None
        out = []
        for anchor, s, text, level, tag in spec:
            if level not in levels or (anchor == "kill" and kill is None):
                continue
            t = (T0 if anchor == "start" else kill) + datetime.timedelta(seconds=s)
            out.append(f" {level} {tag} [{t:%Y-%m-%d %H:%M:%S}] - {text}")
        return out

    def central_lines(self, start, day, suffixes=("info", "warn", "error")):
        return self._render(self.central, {s.upper() for s in suffixes})

    def wait_status(self, pred, timeout, what):
        return {"finished": "True", "result": self.tester_result}

    def launch_bot(self, scenario, user, args, channel="C", observe_seconds=None, line_x=None, harvest_after_migration=False):
        self.calls.append(("launch_bot", user, tuple(args), channel, observe_seconds, line_x, harvest_after_migration))
        log = os.path.join(self.folder, f"regress_{scenario}_{user}.log")
        x, z = float(args[args.index("-meshWalkX") + 1]), float(args[args.index("-meshWalkZ") + 1])
        far, extra = x >= line_x, []
        if user == "meshbot":
            default = "migrations=0 allReady=True maxGap=0 maxJump=0 relogins=0"
        elif far:
            default = f"migrations={2 if self.kind == 'merge' else 1} allReady=True maxGap=300 maxJump=0.2 relogins=0"
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

    def tick(self):
        """The command watcher: the split's status a few lines per poll under its seq; then the merge's under its own."""
        if os.path.exists(self.LAYOUT_COMMAND):
            with open(self.LAYOUT_COMMAND, encoding="utf-8") as f:
                command = f.read().strip()
            os.remove(self.LAYOUT_COMMAND)
            if " merge " in command:
                self.merge_command = command
            else:
                self.command = command
        if self.command is not None and self.written < len(self.status):
            seq = self.command.split()[0]
            more = self.status[self.written:self.written + 3]
            self.written += len(more)
            with open(self.LAYOUT_COMMAND_STATUS, "a", encoding="utf-8") as f:
                f.write("".join(f"{seq} {s if s.startswith(('Layout op', 'Supervisor:')) else 'Split split-1: ' + s}\n" for s in more))
        if self.merge_command is not None and self.merge_written < len(self.merge_status):
            seq = self.merge_command.split()[0]
            more = self.merge_status[self.merge_written:self.merge_written + 3]
            self.merge_written += len(more)
            with open(self.LAYOUT_COMMAND_STATUS, "a", encoding="utf-8") as f:
                f.write("".join(f"{seq} {s if s.startswith('Layout op') else 'Merge merge-1: ' + s}\n" for s in more))


def run(name, **rig_args):
    with tempfile.TemporaryDirectory() as d:
        rig = FakeCentralRig(d, name, **rig_args)
        clock = ts.FakeClock(rig)
        clock.now = T0.timestamp()
        rep, bots = ts.Report(), []
        restarts = []

        def fake_restart(r, scenario, down):
            rig.kill_at = clock.time()
            restarts.append((scenario, dict(rig._play_env), down))
            clock.sleep(down)
            return 1, 2, rig.kill_at
        patches = [mock.patch.object(m.time, "time", clock.time) for m in (regress_split, regress_merge, regress_splitcrash, regress_central)]
        patches += [mock.patch.object(m.time, "sleep", clock.sleep) for m in (regress_split, regress_merge, regress_splitcrash, regress_central)]
        patches += [mock.patch.object(regress_split.provision, "reset_bot_positions"),
                    mock.patch.object(regress_split.rigtools, "asset_cell_modes", lambda path: {}),
                    mock.patch.object(regress_central.regress_procs, "restart_central", fake_restart)]
        for p in patches:
            p.start()
        try:
            cr = regress_central.run(name, rep, rig, "10:00:00", "2026-10-16", bots)
            regress_central.grade_layout(rep, rig, "10:00:00", "2026-10-16", cr)
        finally:
            for p in patches:
                p.stop()
        return rep, rig, bots, cr, restarts


def without(lines, zone, needle):
    return dict(lines, **{zone: [t for t in lines[zone] if needle not in t[2]]})


class CentralScenarioTests(unittest.TestCase):
    def test_splitcentral_passes(self):
        rep, rig, bots, cr, restarts = run("splitcentral")
        self.assertEqual(rep.failed(), [])
        self.assertEqual(len(restarts), 1)
        self.assertEqual(restarts[0][2], regress_central.CENTRAL_DOWN_S)
        self.assertEqual(restarts[0][1]["MESH_MONSTER_AREAS"], "only:Spawn_Interior_C")
        play = rig.calls[1]
        self.assertTrue(play[5], "the tester is in")
        self.assertEqual(play[1:4], ("stand", 60.0, 70.0))
        self.assertEqual(play[6], regress_central.OBSERVE_S, "the tester stands as long as the bots")
        launches = [c for c in rig.calls if c[0] == "launch_bot"]
        self.assertEqual([c[1] for c in launches], R_BOTS + P_BOTS + ["meshbot"])
        self.assertEqual(launches[-1][2][launches[-1][2].index("-meshWalkZ") + 1], "-60")
        self.assertEqual(len(bots), 9)
        self.assertEqual(cr.adopted, "1a2b/4")
        self.assertTrue(any("adopted 1a2b/4 from the store" in label for _, label in rep.checks))

    def test_splitcentral_failures(self):
        base = lines_of("split")
        other_source = [t if "adopted layout" not in t[2] else (t[0], t[1], t[2].replace("from the store", "from cell C.1"), t[3], t[4]) for t in central_lines("split")]
        frozen = central_lines("split") + [L(22, f"Coordinator recovery: family C of map {MAP} frozen: no hello from C.1 after 60 s", "ERROR", anchor="kill")]
        static_summary = [t if "Dynamic maps" not in t[2] or INC2 not in t[2] else (t[0], t[1], t[2].replace("Waiting", "AdoptStatic"), t[3], t[4]) for t in central_lines("split")]
        wrong_route = [t if "-> channel C.1" not in t[2] else (t[0], t[1], t[2].replace("channel C.1", "channel C"), t[3], t[4]) for t in central_lines("split")]
        cases = {
            "adopted from a cell": ({"central": other_source}, ["expected 1a2b/4 from the store"]),
            "a frozen family": ({"central": frozen}, ["nothing frozen"]),
            "the restarted central started on the asset cut": ({"central": static_summary}, ["summary says ServerMeshTestMap Waiting or AdoptStored"]),
            "routed to the base": ({"central": wrong_route}, ["routed to C.1"]),
            "a cell without the second hello": ({"lines": without(base, "E", "layout 1a2b/4")}, ["E: 1 LayoutHello sent"]),
            "a cell never re-opened its database": ({"lines": without(base, "N", "is open")}, ["N: database connection lost (1) and re-opened (0)"]),
            "a kicked bot": ({"results": {"meshbot3": "migrations=1 allReady=True maxGap=300 maxJump=0.2 relogins=1"}}, ["meshbot3: migrations 1, relogins 0, never kicked"]),
            "the tester re-logged in": ({"tester_result": "RESULT stand: reached=True migrations=1 allReady=True maxGap=300 maxJump=0.2 relogins=1 -> PASS"}, ["tester: RESULT passed"]),
            "the store kept an operation": ({"store": dict(store_of("split"), hasOperation=True)}, ["no operation"]),
            "no adoption at all": ({"central": central_lines("split")[:3]}, ["adopted ServerMeshTestMap's layout within"]),
        }
        for name, (args, fragments) in cases.items():
            with self.subTest(name):
                rep, _, _, _, _ = run("splitcentral", **args)
                tc.assert_fails(self, rep, fragments)

    def test_splitcentral_merge_passes(self):
        rep, rig, bots, cr, restarts = run("splitcentral_merge")
        self.assertEqual(rep.failed(), [])
        self.assertEqual(len(restarts), 1)
        self.assertEqual(cr.merge_pairs, {"e1": "1a2b/5", "e2": "1a2b/6", "e3": "1a2c/7", "tidy": "1a2c/8"})
        self.assertIsNotNone(rig.merge_command)
        self.assertTrue(any("resumed at its drain gate or retire" in label for _, label in rep.checks))

    def test_splitcentral_merge_failures(self):
        no_done = [t for t in central_lines("merge") if "merge-1: done" not in t[2]]
        aborted = [t if "resumed at DrainGate" not in t[2] else (t[0], t[1], "Merge merge-1: aborted the central restarted before e2", t[3], t[4]) for t in central_lines("merge")]
        cases = {
            "the merge never ended": ({"central": no_done}, ["reached done within"]),
            "the merge was aborted instead": ({"central": aborted}, ["resumed at its drain gate or retire"]),
            "e2 never broadcast": ({"merge_status": MERGE_STATUS[:4]}, ["accepted and e2 broadcast"]),
            "the child came back": ({"counts": ["1", "0", "1"]}, ["no process with"]),
        }
        for name, (args, fragments) in cases.items():
            with self.subTest(name):
                rep, _, _, _, _ = run("splitcentral_merge", **args)
                tc.assert_fails(self, rep, fragments)

    def test_splitcentral_base_passes(self):
        rep, rig, bots, cr, restarts = run("splitcentral_base")
        self.assertEqual(rep.failed(), [])
        self.assertFalse(rig.calls[1][5], "no tester")
        self.assertEqual(restarts[0][1]["MESH_FAULT_DIE_ON_HELLO"], "C:2")
        self.assertEqual(rig.calls[1][4]["MESH_FAULT_DIE_ON_HELLO"], "C:2")

    def test_splitcentral_base_failures(self):
        base = lines_of("base")
        reowned = dict(base, C=base["C"] + [apply("C", INC1, 4, P, P, 26, "kill")])
        cases = {
            "the base re-owned the whole rectangle": ({"lines": reowned}, ["no apply of the second owning the whole base"]),
            "no fault on the second hello": ({"lines": without(base, "C", "FAULT: dying")}, ["FAULT on its second layout hello"]),
            "a bot handed back": ({"lines": dict(base, **{"C.1": base["C.1"] + [L(20, "Handoff START meshbot2Char (id) zone C.1->C pos=(1, 0, 1)", anchor="kill")]})},
                                  ["handed no R-bot back"]),
        }
        for name, (args, fragments) in cases.items():
            with self.subTest(name):
                rep, _, _, _, _ = run("splitcentral_base", **args)
                tc.assert_fails(self, rep, fragments)


class RegistrationTests(unittest.TestCase):
    def test_expected_zones_and_reasons(self):
        for name in regress_central.SCENARIOS:
            self.assertIn("C.1", regress_central.EXPECTED_ZONES[name])
            self.assertIn("C2", regress_central.EXPECTED_ZONES[name])


if __name__ == "__main__":
    unittest.main()
