"""Unit tests for regress_soak's flow and regress_soakgrade against a fake rig whose central splits a cell the tick its
burn reaches 14 ms and merges a child back once its family's burn is off, on a fake clock (python -m unittest discover
-s Tools/ServerMeshing -p "test_*.py"); no editor, process or database is touched."""
import datetime
import json
import os
import tempfile
import time as real_time
import unittest
from unittest import mock

import regress_procs
import regress_soak
import regress_soakgrade
import riglayout
import rigsplit

MAP, MAP2 = "ServerMeshTestMap", "ServerMeshTestMap2"
ZONES = [(z, MAP) for z in riglayout.TEST_MAP_RECTS] + [("C2", MAP2)]
T0 = datetime.datetime(2026, 10, 23, 10, 0, 0).timestamp()


def stamped(t, text):
    return f" INFO Mesh [{datetime.datetime.fromtimestamp(t):%Y-%m-%d %H:%M:%S}] - {text}"


class FakeSoakRig:
    """The parts of regress the soak uses. The 'central' reacts to the burn file on every clock tick."""
    MAP, MAP2, ZONES, EXE = MAP, MAP2, ZONES, "kit.exe"
    REMOTE_CELLS, REMOTE_MODE = [], "cell"

    def __init__(self, folder, clock):
        self.folder, self.clock = folder, clock
        self.TEMP = self.LOGS = folder
        self.STATUS = os.path.join(folder, "PlayStatus.txt")
        self.LAYOUT_COMMAND_STATUS = os.path.join(folder, "LayoutCommandStatus.txt")
        self.COORDINATOR_STORE = os.path.join(folder, "Coordinator.json")
        self.fams = {b: [b] for b in riglayout.TEST_MAP_RECTS}
        self.parent, self.ops, self.version, self.n = {}, [], 2, 0
        self.retired, self.kills, self.bots = set(), [], []
        self.central = [stamped(T0, "Coordinator 1a2b: Dynamic maps ServerMeshTestMap Waiting; store s (Fresh); broadcast 1a2b/1")]
        self.write_store()

    # ---- regress's surface
    def db_reset(self, *args):
        pass

    def mirror_temp(self, name, text):
        pass

    def request_play(self, mode, x, z, env=None, enabled=True):
        self.env = dict(env or {})

    def wait_play(self, timeout):
        pass

    def launch_bot(self, scenario, user, args, channel="C", observe_seconds=None, line_x=None):
        log = os.path.join(self.folder, f"regress_{scenario}_{user}.log")
        mode = args[args.index("-meshAutoMode") + 1]
        with open(log, "w", encoding="utf-8") as f:
            f.write("[MeshAuto] Standing at (1.00, 0.08, 2.00) party=0\n" if mode == "stand" else
                    "[MeshAuto] Patrolling between x=-70 and x=70 at z=-30 for 100s\n")
        self.bots.append(user)
        return log

    def cell_ps(self, cell, script_of):
        self.kills.append(cell)
        return "1"

    def ps(self, script):
        rows = [f"{100 + i}\t{200 * 1048576}\t{100 * 1048576}\tkit.exe -channelId {z} -mapName {m}" for i, (z, m) in enumerate(ZONES)]
        rows += [f"90\t{300 * 1048576}\t1\tkit.exe -batchmode -startCentralServer -logFile c",
                 f"91\t{150 * 1048576}\t1\tkit.exe -batchmode -startMapSpawnServer -x"]
        rows += [f"{50 + i}\t1048576\t1\tkit.exe -meshAutoClient -meshUser {u} -x" for i, u in enumerate(self.bots)]
        return "\n".join(rows)

    def central_lines(self, start, day):
        return list(self.central)

    def graded_zones(self, day, start=None):
        return list(ZONES) + [(c, MAP) for c in sorted(self.parent)]

    def zone_lines(self, zone, start, day, map_id=MAP, suffixes=("info", "warn", "error")):
        if "info" not in suffixes or map_id != MAP:
            return []
        head = (f"Own cell from layout: {zone} of map {MAP}, grid (0,0), world x [0, 0) z [0, 0), 0 neighbours" if "." in zone
                else f"Own cell from channel id: {zone} of map {MAP}, grid (0,0), world x [0, 1) z [0, 1), 8 neighbours")
        out = [stamped(T0, head)] + [stamped(T0 + 60 * i, "Mesh stats: players=0 | frame mean 1 ms cpu 1% mem 180 MB clients=0")
                                     for i in range(1, 60)]
        for owned, _, pair in riglayout.timeline(riglayout.TEST_MAP_RECTS, self.ops).get(zone, []):
            if pair:
                out.append(stamped(T0, f"Layout {pair} applied: {zone} owns {rigsplit.format_rect(owned)} interest "
                                       f"{rigsplit.format_rect(owned)}, 8 targets"))
        return out

    # ---- the fake central
    def status(self, *texts):
        with open(self.LAYOUT_COMMAND_STATUS, "a", encoding="utf-8") as f:
            f.write("".join(f"auto {t}\n" for t in texts))
        self.central += [stamped(self.clock.now, t) for t in texts]

    def write_store(self):
        entries = [{"id": c, "mapId": MAP, "baseId": b, "state": 2} for b, ms in self.fams.items() if len(ms) > 1 for c in ms]
        entries += [{"id": c, "mapId": MAP, "baseId": c.split(".")[0], "state": 4} for c in sorted(self.retired)]
        with open(self.COORDINATOR_STORE, "w", encoding="utf-8") as f:
            json.dump({"broadcast": {"entries": entries}, "fencedIncarnations": [], "abortedIds": [], "hasOperation": False}, f)

    def tick(self):
        path = os.path.join(self.TEMP, "ServerMesh.ProbeBurn.txt")
        text = ""
        if os.path.exists(path):
            with open(path, encoding="utf-8") as f:
                text = f.read()
        burns = {k: float(v) for k, v in (e.split(":") for e in text.split(",") if e)}
        owned = riglayout.final_owned(riglayout.TEST_MAP_RECTS, self.ops)
        for base, members in self.fams.items():
            hot = [m for m in members if burns.get(m, 0) >= 14]
            if hot:
                self.n += 1
                cell, child, rect, v = hot[0], f"{base}.{len(members)}", owned[hot[0]], self.version
                at = rect[0] + rect[2] / 2
                pairs = {"e1": f"1a2b/{v}", "e2": f"1a2b/{v + 1}", "e3": f"1a2b/{v + 2}"}
                self.status(f"Split split-{self.n}: requested {cell} x {at:g} + child {child}",
                            *(f"Split split-{self.n}: {s} broadcast {p}" for s, p in pairs.items()), f"Split split-{self.n}: done")
                self.ops.append(riglayout.split_op(cell, child, "x", at, "+", pairs))
                self.version += 3
                members.append(child)
                self.parent[child] = cell
                self.retired.discard(child)
                break
            if len(members) > 1 and not any(burns.get(m, 0) for m in members):
                child = members[-1]
                v, pairs = self.version, {s: f"1a2b/{self.version + i}" for i, s in enumerate(("e1", "e2", "e3", "tidy"))}
                self.n += 1
                self.status(f"Merge merge-{self.n}: requested {self.parent[child]} {child}",
                            *(f"Merge merge-{self.n}: {s} broadcast {p}" for s, p in pairs.items()), f"Merge merge-{self.n}: done")
                self.ops.append(riglayout.merge_op(self.parent[child], child, pairs))
                self.version = v + 4
                members.remove(child)
                self.retired.add(child)
                break
        self.write_store()


class FakeClock:
    def __init__(self):
        self.now, self.rig = T0, None

    def time(self):
        return self.now

    def sleep(self, seconds):
        self.now += seconds
        if self.rig:
            self.rig.tick()

    strftime, localtime = staticmethod(real_time.strftime), staticmethod(real_time.localtime)


class Report:
    def __init__(self):
        self.checks, self.notes = [], []

    def check(self, ok, label):
        self.checks.append((bool(ok), label))
        return ok


class SoakFlowTests(unittest.TestCase):
    def run_soak(self, minutes=40.0):
        clock = FakeClock()
        with tempfile.TemporaryDirectory() as d, \
                mock.patch.object(regress_soak, "time", clock), mock.patch.object(regress_soakgrade, "time", clock), \
                mock.patch.dict(regress_soak.SETTINGS, minutes=minutes, kill_every=10.0, central_every=20.0), \
                mock.patch.object(regress_soak.provision, "reset_bot_positions", return_value=6), mock.patch("builtins.print"):
            rig = FakeSoakRig(d, clock)
            clock.rig = rig

            def restart(r, name, down):
                clock.now += down
                rig.central += [stamped(clock.now, "Coordinator 1a2c: Dynamic maps ServerMeshTestMap Waiting; store s (Loaded); broadcast 1a2b/9"),
                                stamped(clock.now, f"Coordinator recovery: map {MAP} adopted layout 1a2b/9 from the store with 2 entries (C, C.1)")]
                return 1, 2, clock.now - down

            with mock.patch.object(regress_procs, "restart_central", side_effect=restart):
                rep, bots = Report(), []
                sr = regress_soak.run("soak", rep, rig, "10:00:00", "2026-10-23", bots)
                regress_soak.grade_layout(rep, rig, "10:00:00", "2026-10-23", sr)
                with open(sr.report, encoding="utf-8") as f:
                    report = f.read()
        return rep, sr, rig, bots, report

    def test_a_soak_on_a_well_behaved_rig_passes(self):
        rep, sr, rig, bots, report = self.run_soak(minutes=60.0)
        failed = [label for ok, label in rep.checks if not ok]
        self.assertEqual(failed, [], "\n".join(failed))
        self.assertEqual(len(bots), len(regress_soak.BOTS))
        self.assertEqual([c for _, c in sr.kills], rig.kills)
        self.assertGreaterEqual(len(sr.kills), 3)
        self.assertEqual(len(sr.restarts), 2)           # at 20 and 40 min; 60 is the end
        self.assertEqual(sr.unfreezes, [])
        self.assertIsNotNone(sr.drained)
        self.assertGreater(sr.plan.started, 3)
        self.assertIn("op Split split-1", report)
        self.assertIn("central restart", report)

    def test_a_stuck_family_fails_the_drain_and_the_store_checks(self):
        def stuck_store(rig):
            with open(rig.COORDINATOR_STORE, "w", encoding="utf-8") as f:
                json.dump({"broadcast": {"entries": [{"id": "C.1", "mapId": MAP, "baseId": "C", "state": 2}]}, "hasOperation": True}, f)

        with mock.patch.object(FakeSoakRig, "write_store", stuck_store), mock.patch.object(FakeSoakRig, "tick", lambda rig: None):
            rep, sr, rig, _, _ = self.run_soak(minutes=12.0)
        failed = [label for ok, label in rep.checks if not ok]
        self.assertIsNone(sr.drained)
        self.assertEqual(rig.kills, ["C.1"])
        self.assertLess(sr.plan.timed_out, 6)     # the drain counts nothing
        for want in ("every family whole again", "no operation stored at the end", "the policy split and merged"):
            self.assertTrue(any(want in f for f in failed), (want, failed))

class TwoMachineSampleTests(unittest.TestCase):
    """DESIGN 0.92: in a spawner-mode two-machine run the other machine's processes are sampled too, tagged 'B:'."""
    class Events:
        def __init__(self):
            self.lines = []

        def add(self, t, text):
            self.lines.append(text)

    B_ROWS = (f"7\t{180 * 1048576}\t1\tkit.exe -channelId C -mapName {MAP}\n"
              f"8\t{120 * 1048576}\t1\tkit.exe -batchmode -startMapSpawnServer -x")

    def rig(self, cells, mode):
        rig = FakeSoakRig.__new__(FakeSoakRig)
        rig.bots, rig.REMOTE_CELLS, rig.REMOTE_MODE = [], cells, mode
        return rig

    def test_the_other_machines_processes_are_tagged(self):
        events = self.Events()
        with mock.patch.object(regress_soak.remotespawner, "run_script", return_value=self.B_ROWS) as run:
            rows = regress_soak.sample_processes(self.rig(["C"], "spawner"), events, 0.0)
        run.assert_called_once()
        roles = {r for _, r, _, _ in rows}
        self.assertIn("B:cell:C", roles)
        self.assertIn("B:spawner", roles)
        self.assertIn("cell:C", roles)      # this machine's own rows are untouched
        self.assertEqual(events.lines, [])

    def test_one_machine_or_cell_mode_never_asks_the_other(self):
        for cells, mode in (([], "cell"), (["E"], "cell")):
            with mock.patch.object(regress_soak.remotespawner, "run_script") as run:
                rows = regress_soak.sample_processes(self.rig(cells, mode), self.Events(), 0.0)
            run.assert_not_called()
            self.assertFalse(any(r.startswith("B:") for _, r, _, _ in rows))

    def test_a_failed_sample_on_the_other_machine_keeps_this_ones(self):
        events = self.Events()
        with mock.patch.object(regress_soak.remotespawner, "run_script", side_effect=RuntimeError("B: ssh timed out")):
            rows = regress_soak.sample_processes(self.rig(["C"], "spawner"), events, 0.0)
        self.assertTrue(any(r == "central" for _, r, _, _ in rows))
        self.assertEqual(len(events.lines), 1)
        self.assertIn("B's process sample failed", events.lines[0])


class OperatorTests(unittest.TestCase):
    class Events:
        def __init__(self):
            self.lines = []

        def add(self, t, text):
            self.lines.append(text)

    def test_a_frozen_family_and_a_frozen_op_get_an_unfreeze_after_the_delay(self):
        with tempfile.TemporaryDirectory() as d:
            rig = mock.Mock(LOGS=d, MAP=MAP, LAYOUT_COMMAND=os.path.join(d, "LayoutCommand.txt"))
            day = datetime.date.today().isoformat()
            info = os.path.join(d, f"Log_Database_Central_Login_{day}.info.log")
            error = os.path.join(d, f"Log_Database_Central_Login_{day}.error.log")
            with open(info, "w", encoding="utf-8") as f:
                f.write(stamped(T0, "Coordinator recovery: family C of map ServerMeshTestMap frozen: an earlier freeze") + "\n")
            op, ev = regress_soak.Operator(rig), self.Events()
            op.tick(T0, ev)                                   # lines from before the soak are not its business
            self.assertEqual(op.due, [])
            with open(error, "w", encoding="utf-8") as f:
                f.write(stamped(T0, "Coordinator recovery: family E of map ServerMeshTestMap frozen: no hello from E.1 after 60 s") + "\n")
            with open(info, "a", encoding="utf-8") as f:
                f.write(stamped(T0, "Split split-3: requested N.1 x 50 + child N.2") + "\n")
                f.write(stamped(T0, "Split split-3: frozen N.2 unreachable") + "\n")
            op.tick(T0 + 1, ev)
            self.assertFalse(os.path.exists(rig.LAYOUT_COMMAND))
            op.tick(T0 + 1 + regress_soak.OPERATOR_DELAY_S, ev)
            op.tick(T0 + 2 + regress_soak.OPERATOR_DELAY_S, ev)    # the central has not taken the first command yet
            self.assertEqual(len(op.done), 1)
            with open(rig.LAYOUT_COMMAND, encoding="utf-8") as f:
                self.assertEqual(f.read().strip(), f"soak1 unfreeze {op.done[0][1]}")
            os.remove(rig.LAYOUT_COMMAND)
            op.tick(T0 + 3 + regress_soak.OPERATOR_DELAY_S, ev)
            self.assertEqual(sorted(b for _, b, _ in op.done), ["E", "N"])
            self.assertTrue(all(line.startswith("operator: unfreeze") for line in ev.lines))

    def test_frozen_unresolved_counts_the_operators_unfreeze(self):
        frozen = stamped(T0, "Coordinator recovery: family E of map ServerMeshTestMap frozen: no hello from E.1 after 60 s")
        unfrozen = stamped(T0 + 31, "Unfreeze E: family E of map ServerMeshTestMap unfrozen by the operator: E.1 declared dead")
        self.assertEqual(len(regress_soakgrade.frozen_unresolved([frozen])), 1)
        self.assertEqual(regress_soakgrade.frozen_unresolved([frozen, unfrozen]), [])


if __name__ == "__main__":
    unittest.main()
