"""Slice 4's building scenario for regress.py (Docs/ServerMeshing/slices/slice4_design.md decision 16):

    python Tools/ServerMeshing/regress.py splitbuild

splitbuild: `split`'s setup (C at x = 0, eight standing bots, no harvest bot, no synthetics, no monsters) with the
editor's tester in the child's half in mode `buildtree`: it places a wall with a chest and a door snapped to it, puts a
scroll in the chest and keeps moving scrolls in and out. The split is sent once the tester is ready; C hands the tree
to C.1 (graded on both cells), the tester opens the chest and the door on C.1; then `merge C C.1` hands it back, the
database holds three rows under C, C is killed and comes back loading exactly that tree, and the bots and the tester
re-log in. regress.py hands itself in as `rig` (db_reset, request_play, wait_status, db_rows, zone_lines, ps, EXE,
...), so this module never imports it; the unit tests hand in a fake. The readers are rigbuilding's.
"""
import collections
import time

import regress_merge
import regress_split
import regress_splitcrash
import rigbuilding
import rigmerge
import rigsplit
import rigsplitcrash

SCENARIOS = ("splitbuild",)
TWO_HOST_REASON = regress_split.TWO_HOST_REASON
PERSIST_REASON = regress_split.PERSIST_REASON
EXPECTED_ZONES = {}

OBSERVE_S = 720            # the tester's and the bots' window: the tree, the split, the cooldown, the merge, the restart, the re-logins
READY_TIMEOUT_S = 120      # after every bot stands: the tester's phase buildtree-ready
CHECKED_TIMEOUT_S = 120    # after the split's e3 apply: the tester's phase buildtree-checked
KILL_AFTER_DONE_S = 10.0   # after the merge's done
KILL_GUARD_S = 240.0       # the earliest bot's window must have this much left when C is killed
ANSWER_TIMEOUT_S = 120     # C's second lifetime and its first apply
LOADED_TIMEOUT_S = 60      # C's load line in its second lifetime
RESULT_SLACK_S = regress_split.RESULT_SLACK_S
MAX_GAP_MS = regress_split.MAX_GAP_MS
TESTER = ("buildtree", 60.0, 70.0)   # in R: the tree stands at x = 63, 94 m from Spawn_Interior_C's centre
ENV = {"MESH_NO_MONSTERS": "1"}
TREE_NODES = 3
ROWS_SQL = "select id, parentId, channel from buildings where mapName=%s"
STORAGE_SQL = "select amount from storageitem where storageType=%s and storageOwnerId=%s"
STORAGE_TYPE_BUILDING = 3

# What run() leaves for grade_layout: the split phase's SplitRun and the merge's seq and status (None when not sent).
BuildRun = collections.namedtuple("BuildRun", "name r merge_seq merge_status")


def phase_is(st, phase):
    """The status file's phase is `phase`, or a re-login's prefix of it."""
    p = st.get("phase", "")
    return p == phase or p == "relogin-" + phase


def wait_phase(rig, phase, timeout):
    """The tester's phase, or None at the timeout (rig.wait_status raises TimeoutError)."""
    try:
        return rig.wait_status(lambda st: phase_is(st, phase), timeout, f"the tester's phase {phase}")
    except TimeoutError:
        return None


def ready_or_fail(rep, rig):
    """start_split's before_command: the tester placed the tree and put a scroll in (phase buildtree-ready)."""
    st = wait_phase(rig, "buildtree-ready", READY_TIMEOUT_S)
    ok = st is not None
    rep.check(ok, f"the tester reached buildtree-ready before the split ({READY_TIMEOUT_S} s)")
    return ok


def grade_transfer(rep, rig, spec, start, day, from_zone, to_zone, label):
    """One hand-over of the tree between the two cells (decision 16 steps 3 and 5); returns the root id."""
    sender = rig.zone_lines(from_zone, start, day, rig.MAP)
    receiver = rig.zone_lines(to_zone, start, day, rig.MAP)
    roots = rigbuilding.roots_sent(sender, from_zone, to_zone)
    root = roots[0] if roots else None
    problems = rigbuilding.transfer_problems(sender, receiver, root, from_zone, to_zone, TREE_NODES)
    rep.check(not problems, f"{label}: {from_zone} handed the tree {root or '?'} to {to_zone} ({TREE_NODES} nodes, done, taken)"
              + (f" ({'; '.join(problems)})" if problems else ""))
    kept = sum(rig.count(lines, rigbuilding.KEPT_HERE.pattern) for lines in (sender, receiver))
    rep.check(kept == 0, f"{label}: {kept} 'kept here' placement(s) on {from_zone} and {to_zone}")
    return root


def grade_split_entities(rep, rig, spec, start, day, e2):
    """The statics `split` grades (HarvestSpawn_C lies in R), without its monster and synthetic checks."""
    base_lines = rig.zone_lines(spec.base, start, day, rig.MAP)
    child_lines = rig.zone_lines(spec.child, start, day, rig.MAP)
    sent = rigsplit.count_kind(base_lines, rigsplit.STATIC_HANDOFF, "sent", spec.base, spec.child)
    taken = sum(rigsplit.count_kind(child_lines, rigsplit.STATIC_HANDOFF, k, spec.base, spec.child) for k in ("promoted", "spawned"))
    failed = sum(rig.count(lines, r"Static handoff \S+ zone \S+: failed") for lines in (base_lines, child_lines))
    rep.check(sent >= 1 and taken >= 1 and failed == 0, f"statics: {sent} sent {spec.base}->{spec.child}, {taken} taken, {failed} failed")


def grade_rows(rep, rig, base, root, label):
    """The map's building rows: three, all under the base's channel, the root and its two children (steps 6 and 7)."""
    try:
        rows = rig.db_rows(ROWS_SQL, (rig.MAP,))
    except Exception as ex:   # noqa: BLE001 -- a database the rig cannot reach fails the check, not the run
        rep.check(False, f"{label}: building rows unreadable ({ex})")
        return []
    problems = rigbuilding.rows_problems(rows, base, root, TREE_NODES)
    rep.check(not problems, f"{label}: {len(rows)} building row(s) under channel {base}, the root {root or '?'} and its children"
              + (f" ({'; '.join(problems)})" if problems else ""))
    return rows


def grade_storage_rows(rep, rig, rows, result):
    """The chest's storage rows sum to the tester's scrollsStorage (after Play, when the RESULT is known)."""
    chest = next((r[0] for r in rows if r[1] and r[0] != result.get("root")), None)
    expected = result.get("scrollsStorage")
    if chest is None or expected is None or expected < 0:
        rep.notes.append("   storage rows: no chest row or no scrollsStorage in the RESULT; not compared")
        return
    chests = [r[0] for r in rows if r[1]]
    sums = {}
    for cid in chests:
        try:
            sums[cid] = rigbuilding.storage_sum(rig.db_rows(STORAGE_SQL, (STORAGE_TYPE_BUILDING, cid)))
        except Exception as ex:   # noqa: BLE001
            rep.check(False, f"storage rows of {cid} unreadable ({ex})")
            return
    rep.check(expected in sums.values(), f"the chest's storage rows sum to the RESULT's scrollsStorage {expected} ({sums})")


def restart_base(rep, rig, r, split, merge_done_at, start, day):
    """Decision 16 step 7: a window guard, the kill of C, its second lifetime and its load of exactly the tree."""
    left = regress_merge.window_left(r, OBSERVE_S)
    if left < KILL_GUARD_S:
        rep.check(False, f"restart: C not killed, stand window too short ({left:.0f} s left, needs {KILL_GUARD_S:g})")
        return None
    time.sleep(max(0.0, merge_done_at + KILL_AFTER_DONE_S - time.time()))
    try:
        killed = rigmerge.parse_count(rig.ps(rigsplitcrash.kill_script(rig.EXE, split.base)))
    except RuntimeError as ex:
        rep.check(False, f"restart: the kill of {split.base} failed ({ex})")
        return None
    rep.check(killed == 1, f"restart: killed {killed} process(es) with '{rigmerge.channel_filter(split.base)}', expected 1")
    answer = regress_splitcrash.wait_answer(rig, split, start, day, ANSWER_TIMEOUT_S)
    rep.check(answer is not None, f"restart: {split.base} came back and applied a layout within {ANSWER_TIMEOUT_S} s")
    end = time.time() + LOADED_TIMEOUT_S
    load = None
    while time.time() < end:
        info = rig.zone_lines(split.base, start, day, rig.MAP, suffixes=("info",))
        lives = rigsplit.lifetimes(info)
        if len(lives) > 1:
            found = rigbuilding.loads(lives[1])
            if found:
                load = found[0]
                break
        time.sleep(2)
    return load


def grade_bots(rep, rig, r, results, start, day):
    """Step 8: after the kill every bot re-logged in onto C: R-bots 2 or 3 migrations, P-bots 0 or 1, relogins 1, ready
    under a second; one Handoff START each way per R-bot."""
    spec = r.spec
    base_lines = rig.zone_lines(spec.base, start, day, rig.MAP)
    child_lines = rig.zone_lines(spec.child, start, day, rig.MAP)
    for b in r.plan:
        log = r.logs[b.user]
        result = rigsplit.parse_result(results.get(log, ""))
        r_side = b.side == regress_split.SIDE
        problems = rigsplitcrash.bot_problems(result, {2, 3} if r_side else {0, 1}, 1)
        if result and result.get("migrations", 0) > 0:
            if result.get("allReady") is not True:
                problems.append(f"allReady={result.get('allReady')}")
            gap = result.get("maxGap")
            if gap is None or gap >= MAX_GAP_MS:
                problems.append(f"maxGap={gap} ms, expected under {MAX_GAP_MS:g}")
        rep.check(not problems, f"{b.user}: {'2 or 3' if r_side else '0 or 1'} migration(s) and one re-login"
                  + (f" ({'; '.join(problems)})" if problems else ""))
        if r_side:
            out = rig.count(base_lines, rigsplit.handoff_start(b.character, spec.base, spec.child).pattern)
            back = rig.count(child_lines, rigsplit.handoff_start(b.character, spec.child, spec.base).pattern)
            rep.check(out == 1 and back == 1, f"{b.user}: Handoff START {spec.base}->{spec.child} {out} and back {back}, expected 1 each")


def run(name, rep, rig, start, day, bots):
    """Runs splitbuild against the rig (regress) and records its checks on rep; appends every bot to `bots`. Returns a
    BuildRun for grade_layout."""
    r = regress_split.start_split(name, rep, rig, start, day, bots, spec_name="split", observe_s=OBSERVE_S,
                                  plan=regress_splitcrash.stand_plan(), load_plan="", env=ENV, tester=TESTER,
                                  clear_buildings=True, observe_seconds=OBSERVE_S, line_x=0.0,
                                  before_command=lambda: ready_or_fail(rep, rig))
    spec, split = r.spec, r.split
    if not r.sent or r.applied is None:
        rep.check(False, f"splitbuild: the split did not reach {spec.base}'s e3 apply")
        return BuildRun(name, r, None, None)
    root = grade_transfer(rep, rig, spec, start, day, spec.base, spec.child, "split")
    grade_split_entities(rep, rig, spec, start, day, r.status["pairs"].get("e2"))
    checked = wait_phase(rig, "buildtree-checked", CHECKED_TIMEOUT_S)
    rep.check(checked is not None, f"the tester reached buildtree-checked after the split ({CHECKED_TIMEOUT_S} s)")

    m = regress_merge.MergeSpec("split", OBSERVE_S, False)
    regress_merge.wait_before_merge(r, m)
    seq, status = regress_merge.send_merge(rep, rig, r, m)
    if status is None:
        return BuildRun(name, r, seq, None)
    regress_merge.grade_status(rep, status, spec)
    if not status["done"]:
        return BuildRun(name, r, seq, status)
    merge_done_at = time.time()
    pairs = status["pairs"]
    tidy = regress_merge.wait_tidy(rig, split, pairs.get("tidy"), start, day, regress_merge.TIDY_APPLY_TIMEOUT_S)
    rep.check(tidy is not None, f"{spec.base} applied the tidy layout {pairs.get('tidy')} owning and interested in "
                                f"{rigsplit.format_rect(split.base_rect)}")
    regress_merge.grade_exit(rep, rig, spec.child, start, day)
    regress_merge.grade_store(rep, rig, split)
    back = grade_transfer(rep, rig, spec, start, day, spec.child, spec.base, "merge")
    rep.check(back == root, f"the merge handed back the same tree ({back} / {root})")
    regress_merge.grade_retiring(rep, rig, spec.child, start, day)
    regress_merge.grade_prunes(rep, rig, spec.child, pairs.get("e3"), start, day)
    rows = grade_rows(rep, rig, spec.base, root, "after the merge")

    load = restart_base(rep, rig, r, split, merge_done_at, start, day)
    problems = rigbuilding.load_problems(load, spec.base, TREE_NODES, spec.base, TREE_NODES)
    rep.check(not problems, f"{spec.base}'s second lifetime loaded exactly the tree ({TREE_NODES} placed, channel {spec.base}, {TREE_NODES} rows)"
              + (f" ({'; '.join(problems)})" if problems else ""))
    rows = grade_rows(rep, rig, spec.base, root, "after the restart") or rows

    stands = [r.stood[log] for log in r.logs.values() if log in r.stood] or [time.time()]
    results = regress_split.wait_results(list(r.logs.values()), max(10.0, max(stands) + OBSERVE_S + RESULT_SLACK_S - time.time()))
    grade_bots(rep, rig, r, results, start, day)
    try:
        st = rig.wait_status(lambda s: s.get("finished") == "True", 180, "the tester's RESULT")
    except TimeoutError:
        st = {}
    result = rigbuilding.parse_result(st.get("result", ""))
    problems, notes = rigbuilding.result_problems(result, spec.base, spec.child)
    rep.check(not problems, "the tester: tree placed, storage opened on C then on C.1, door opened, scrolls add up to 20"
              + (f" ({'; '.join(problems)})" if problems else ""))
    for note in notes:
        rep.notes.append(f"   tester: {note}")
    grade_storage_rows(rep, rig, rows, result)
    return BuildRun(name, r, seq, status)


def grade_layout(rep, rig, start, day, br):
    """regress.grade_layout for splitbuild: the split's and the merge's applies as the first lifetime of every cell, the
    base's second lifetime with its base rectangle (rigbuilding.expected_lives), the merge's op lines allowed, and no
    Split, Merge or Retire line in the base's second lifetime."""
    split = br.r.split
    split_pairs = (br.r.status or {}).get("pairs", {})
    merge_pairs = (br.merge_status or {}).get("pairs", {})
    regress_split.grade_layout(
        rep, rig, start, day, split,
        applies=lambda zone, info: rigsplitcrash.lifetime_apply_violations(
            zone, info, split, lambda own: rigbuilding.expected_lives(split, zone, own, split_pairs, merge_pairs), label="splitbuild"),
        ops=lambda zone, lines: len(rigmerge.disallowed_op_lines(zone, lines, split.child)),
        how_split="splitbuild map: the split's and the merge's layouts, then the restarted base's own rectangle")
    info = rig.zone_lines(split.base, start, day, rig.MAP, suffixes=("info",))
    stray = rigbuilding.second_life_op_lines(info)
    rep.check(not stray, f"{split.base}'s second lifetime logged no Split, Merge or Retire line ({len(stray)})"
              + (f" ({stray[0].strip()[:120]})" if stray else ""))
