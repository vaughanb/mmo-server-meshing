"""Slice 2c's merge scenarios for regress.py (Docs/ServerMeshing/slices/slice2c_design.md decisions 15-18):

    python Tools/ServerMeshing/regress.py merge mergec2

merge: regress_split's `split` (base C, child C.1 at x = 0) with 480 s stand windows; once the split is done, the
harvest bot has written its RESULT and the family's cooldown has passed, the harness writes '<seq> merge C C.1' and
waits for the op's done, C's apply of the tidy pair and C.1's process to be gone; then grades the merge.
mergec2: `splitc2` (base E, child E.1 touching C2) with 300 s windows, then 'merge E E.1'; C2's view of it graded.

regress.py hands itself in as `rig` (as for regress_split: db_reset, request_play, launch_bot, zone_lines, ps, EXE,
COORDINATOR_STORE, ...), so this module never imports it; the unit tests hand in a fake. The graders are rigmerge's.
"""
import collections
import json
import time

import regress_split
import rigmerge
import rigsplit
import rigtools

SCENARIOS = ("merge", "mergec2")
PERSIST_REASON = regress_split.PERSIST_REASON

MERGE_OBSERVE_S = 480      # merge's stand window: the split, the harvest wait, the cooldown and the merge fit in it
MERGEC2_OBSERVE_S = 300    # mergec2 waits for no harvest bot
HARVEST_CAP_S = 200        # after the split's e3 apply, for the harvest bot's RESULT
COOLDOWN_S = 30.0          # MeshDynamicSettings.splitCooldownSeconds, from the split's done
COOLDOWN_SLACK_S = 5.0
RETRY_WAIT_S = 10.0        # before the one retry of a merge refused for the cooldown
WINDOW_GUARD_S = 150.0     # the earliest R-bot's stand window must have this much left when the merge is sent
MERGE_TIMEOUT_S = 240      # the merge's status says done (pre-warm, drain, retire, exit, e3, tidy)
TIDY_APPLY_TIMEOUT_S = 30  # after done: the survivor applied the tidy pair owning and interested in its base rect
SAMPLE_EVERY_S = 2.0
EXIT_WITHIN_S = 30.0       # the retiring cell's process gone this long after its 'drained, quitting' stamp
STAY_GONE_S = 20.0         # and still gone for this long after
RESULT_SLACK_S = 90
MAX_GAP_MS = regress_split.MAX_GAP_MS

MergeSpec = collections.namedtuple("MergeSpec", "split observe_s harvest_wait")
MERGES = {"merge": MergeSpec("split", MERGE_OBSERVE_S, True), "mergec2": MergeSpec("splitc2", MERGEC2_OBSERVE_S, False)}
# What run() hands grade_layout: the split phase's SplitRun and the merge command's seq and parsed status (None when
# it was never sent).
MergeRun = collections.namedtuple("MergeRun", "name r seq status")


def new_seq(previous=None):
    """A command seq: the wall second, past `previous` when that one is not older."""
    seq = int(time.time())
    return str(max(seq, int(previous) + 1) if previous is not None else seq)


def wait_merge(status_path, seq, timeout):
    """The merge command's parsed status once it has ended (done, refused, aborted or frozen), or at the timeout."""
    end = time.time() + timeout
    while True:
        status = rigmerge.parse_merge_status(regress_split.read_lines(status_path), seq)
        if rigmerge.merge_outcome(status) or time.time() >= end:
            return status
        time.sleep(2)


def has_result(log):
    return any("RESULT stand:" in line or "RESULT harvest:" in line for line in regress_split.read_lines(log))


def r_bots(r):
    """The stand bots in the child's half (meshbot2..5)."""
    return [b for b in r.plan if b.side == regress_split.SIDE and not b.harvest]


def count_processes(rig, cell):
    """How many of the build's processes serve `cell` (one CIM query), or None when the count failed."""
    try:
        return rigmerge.parse_count(rig.cell_ps(cell, rigmerge.process_count_script))
    except (RuntimeError, ValueError):
        return None


def window_left(r, observe_s):
    """Seconds left of the earliest R-bot's stand window (counted from when its 'Standing at' was first seen)."""
    starts = [r.stood[r.logs[b.user]] for b in r_bots(r) if r.logs[b.user] in r.stood]
    return (min(starts) + observe_s - time.time()) if starts else 0.0


def wait_before_merge(r, m):
    """Decision 16 step 3: the harvest bot's RESULT (cap HARVEST_CAP_S after the split's e3) and the family's cooldown
    after the split's done, whichever is later."""
    harvest = next((b for b in r.plan if b.harvest), None)
    if m.harvest_wait and harvest is not None:
        end = (r.e3_at or time.time()) + HARVEST_CAP_S
        while not has_result(r.logs[harvest.user]) and time.time() < end:
            time.sleep(3)
    cooldown_end = (r.done_at or time.time()) + COOLDOWN_S + COOLDOWN_SLACK_S
    if time.time() < cooldown_end:
        time.sleep(cooldown_end - time.time())


def guard_merge(rep, rig, r, m, attempt=1):
    """Steps 4-5 (Slice 5 factored them out for a flow that kills the central mid-merge): the window guard and, on the
    first attempt, the process positive control. True when the merge may be sent."""
    spec = r.spec
    left = window_left(r, m.observe_s)
    if left < WINDOW_GUARD_S:
        rep.check(False, f"merge: not sent, stand window too short ({left:.0f} s left on the earliest R-bot, "
                         f"needs {WINDOW_GUARD_S:g})")
        return False
    rep.notes.append(f"   merge sent with {left:.0f} s left of the earliest R-bot's stand window")
    if attempt == 1:
        n = count_processes(rig, spec.child)
        rep.check(n == 1, f"positive control: {n} process(es) with '{rigmerge.channel_filter(spec.child)}' "
                          "before the merge, expected 1")
    return True


def send_merge(rep, rig, r, m):
    """Steps 4-6: the window guard, the process positive control, the command and its wait; one retry with a new seq
    on a cooldown refusal. Returns (seq, status), (None, None) when nothing was sent."""
    spec = r.spec
    seq, status = None, None
    for attempt in (1, 2):
        if not guard_merge(rep, rig, r, m, attempt):
            return seq, status
        seq = new_seq(seq)
        regress_split.write_command(rig.LAYOUT_COMMAND, rigmerge.command_line(seq, spec.base, spec.child))
        status = wait_merge(rig.LAYOUT_COMMAND_STATUS, seq, MERGE_TIMEOUT_S)
        if attempt == 1 and status["refused"] and "cooldown" in status["refused"]:
            rep.notes.append(f"   merge {seq} refused ({status['refused'][:80]}); retrying once")
            time.sleep(RETRY_WAIT_S)
            continue
        return seq, status
    return seq, status


def wait_tidy(rig, split, pair, start, day, timeout):
    """The survivor's apply line of the tidy `pair` owning and interested in its base rect, or None at the timeout."""
    end = time.time() + timeout
    while pair:
        for line in rig.zone_lines(split.base, start, day, split.map_id, suffixes=("info",)):
            a = rigsplit.parse_apply(line)
            if a and a["cell"] == split.base and f"{a['incarnation']}/{a['version']}" == pair \
                    and rigsplit.same_rect(a["owned"], split.base_rect) \
                    and rigsplit.same_rect(a["interest"], split.base_rect):
                return line
        if time.time() >= end:
            break
        time.sleep(2)
    return None


def quitting_line(rig, cell, start, day):
    return next((line for line in rig.zone_lines(cell, start, day, rig.MAP) if rigmerge.RETIRE_QUITTING.search(line)
                 and rigmerge.RETIRE_QUITTING.search(line).group(1) == cell), None)


def sample_exit(rig, cell, quit_at):
    """Step 8: samples the cell's processes every SAMPLE_EVERY_S. {gone_at, first_at, deadline, came_back, error}:
    gone_at the first sample with none (None: never, up to the deadline, EXIT_WITHIN_S after quit_at, clamped to
    now..now + EXIT_WITHIN_S), came_back a process seen again within STAY_GONE_S of it."""
    now = time.time()
    deadline = min(max(quit_at + EXIT_WITHIN_S, now), now + EXIT_WITHIN_S)
    out = {"gone_at": None, "first_at": now, "deadline": deadline, "came_back": False, "error": False}
    while True:
        n, t = count_processes(rig, cell), time.time()
        if n is None:
            out["error"] = True
            return out
        if n == 0 and out["gone_at"] is None:
            out["gone_at"] = t
        elif n > 0 and out["gone_at"] is not None:
            out["came_back"] = True
            return out
        if (out["gone_at"] is None and t >= deadline) or (out["gone_at"] is not None and t >= out["gone_at"] + STAY_GONE_S):
            return out
        time.sleep(SAMPLE_EVERY_S)


def run(name, rep, rig, start, day, bots):
    """Runs one merge scenario against the rig (regress) and records its checks on rep; appends every bot to `bots`.
    Returns a MergeRun for grade_layout."""
    m = MERGES[name]
    r = regress_split.start_split(name, rep, rig, start, day, bots, spec_name=m.split, observe_s=m.observe_s)
    spec = r.spec
    if not r.sent or r.applied is None:
        rep.check(False, f"merge: not sent, the split did not reach {spec.base}'s e3 apply")
        return MergeRun(name, r, None, None)
    wait_before_merge(r, m)
    seq, status = send_merge(rep, rig, r, m)
    if status is None:
        return MergeRun(name, r, seq, None)
    grade_status(rep, status, spec)
    if not status["done"]:
        return MergeRun(name, r, seq, status)   # the status checks failed it; the rest would only wait out the bots
    pairs = status["pairs"]
    tidy = wait_tidy(rig, r.split, pairs.get("tidy"), start, day, TIDY_APPLY_TIMEOUT_S)
    rep.check(tidy is not None, f"{spec.base} applied the tidy layout {pairs.get('tidy')} owning and interested in "
                                f"{rigsplit.format_rect(r.split.base_rect)}")
    grade_exit(rep, rig, spec.child, start, day)
    grade_store(rep, rig, r.split)
    stands = [r.stood[log] for log in r.logs.values() if log in r.stood] or [time.time()]
    results = regress_split.wait_results(list(r.logs.values()),
                                         max(10.0, max(stands) + m.observe_s + RESULT_SLACK_S - time.time()))
    grade_bots(rep, rig, r, results, start, day)
    grade_retiring(rep, rig, spec.child, start, day)
    grade_prunes(rep, rig, spec.child, pairs.get("e3"), start, day)
    if spec.beside_c2:
        grade_c2(rep, rig, r, pairs, start, day)
    else:
        grade_entities(rep, rig, spec, pairs, start, day)
    return MergeRun(name, r, seq, status)


def grade_status(rep, status, spec):
    """The merge's own record: kind Merge, accepted with the survivor and the retiring cell, every state in order, no
    abort, freeze, revert, early exit or straggling."""
    rep.check(status["op"] is not None and status["requested"] == (spec.base, spec.child) and not status["refused"],
              f"merge {spec.base} {spec.child} accepted ({status['op'] or 'no Merge op'}"
              + (f"; refused: {status['refused']}" if status["refused"] else "")
              + (f"; requested {' '.join(status['requested'])}" if status["requested"] else "") + ")")
    problems = rigmerge.state_problems(status, spec.child)
    rep.check(not problems, "merge states e1 broadcast .. done" + (f" ({'; '.join(problems)})" if problems else ""))
    bad = rigmerge.bad_states(status)
    rep.check(not bad, "merge neither aborted, frozen, reverted, exited before drained nor straggling"
              + (f" ({'; '.join(bad[:3])})" if bad else ""))
    supervision = rigsplit.supervision_lines(status)
    rep.check(not supervision, "no supervision (restart, pause, resume) during the merge"
              + (f" ({supervision[0][:120]})" if supervision else ""))
    for text in status["other"][:3]:
        rep.notes.append(f"   status: {text[:160]}")


def grade_exit(rep, rig, cell, start, day):
    """Step 8's check: the retiring cell's process gone within EXIT_WITHIN_S of its quitting stamp, and staying gone."""
    line = quitting_line(rig, cell, start, day)
    stamp = rigtools.stamp_datetime(line) if line else None
    quit_at = stamp.timestamp() if stamp else time.time()
    s = sample_exit(rig, cell, quit_at)
    if s["error"]:
        rep.check(False, f"{cell}: its processes could not be counted")
        return
    rep.check(s["gone_at"] is not None and not s["came_back"],
              f"{cell}: no process with '{rigmerge.channel_filter(cell)}' within {EXIT_WITHIN_S:g} s of its quitting "
              f"stamp and none for {STAY_GONE_S:g} s after" + (" (one came back)" if s["came_back"] else "")
              + ("" if s["gone_at"] is not None else " (still running)"))
    if s["gone_at"] is not None and stamp and quit_at <= s["first_at"]:
        first = " (gone at the first sample)" if s["gone_at"] == s["first_at"] else ""
        rep.notes.append(f"   {cell}: process gone {s['gone_at'] - quit_at:.0f} s after its quitting stamp{first}")
    elif stamp:
        rep.notes.append(f"   {cell}: its quitting stamp is ahead of this machine's clock; exit time not measured")


def grade_store(rep, rig, split):
    """After done, the coordinator store holds no entry for the family (the tidy's purpose)."""
    try:
        with open(rig.COORDINATOR_STORE, encoding="utf-8-sig") as f:
            store = json.load(f)
    except (OSError, ValueError) as ex:
        rep.check(False, f"coordinator store unreadable after the merge: {ex}")
        return
    left = rigmerge.family_entries(store, split.map_id, split.base, [split.base, split.child])
    rep.check(not left, f"coordinator store holds no entry for {split.base}'s family after the merge"
              + (f" ({', '.join(map(str, left))})" if left else ""))


def grade_bots(rep, rig, r, results, start, day):
    """R-bots: two migrations (split and merge), ready, under a second, one Handoff START back each; P-bots none; the
    harvest bot's rule (rigmerge.harvest_problems); no fallback hop in any bot log."""
    spec = r.spec
    child_lines = rig.zone_lines(spec.child, start, day, rig.MAP)
    for b in r.plan:
        log = r.logs[b.user]
        result = rigsplit.parse_result(results.get(log, ""))
        starts = rig.count(child_lines, rigsplit.handoff_start(b.character, spec.child, spec.base).pattern)
        bot_lines = regress_split.read_lines(log)
        if b.harvest:
            problems = rigmerge.harvest_problems(result, bot_lines, starts)
            rep.check(not problems, f"{b.user} (harvest): RESULT before the merge, handed back once"
                      + (f" ({'; '.join(problems)})" if problems else ""))
        else:
            expected = 2 if b.side == regress_split.SIDE else 0
            problems = rigsplit.migration_problems(result, expected, MAX_GAP_MS)
            rep.check(not problems, f"{b.user}: {expected} migration(s) with the split and the merge"
                      + (f" ({'; '.join(problems)})" if problems else ""))
            if expected:
                rep.check(starts == 1, f"{spec.child} logged {starts} Handoff START {b.character} zone "
                                       f"{spec.child}->{spec.base}, expected 1")
        hops = rigmerge.fell_back(bot_lines).count(True)
        rep.check(hops == 0, f"{b.user}: {hops} migration(s) with fellBack=True")


def grade_retiring(rep, rig, cell, start, day):
    """The retiring cell drained and quit in its one lifetime, with no warning-level 'Merge retire' line."""
    lines = rig.zone_lines(cell, start, day, rig.MAP)
    info = rig.zone_lines(cell, start, day, rig.MAP, suffixes=("info",))
    quits = sum(1 for line in lines if rigmerge.RETIRE_QUITTING.search(line)
                and rigmerge.RETIRE_QUITTING.search(line).group(1) == cell)
    rep.check(quits >= 1, f"{cell}: 'Merge retire: {cell} drained, quitting' logged ({quits})")
    lives = len(rigsplit.lifetimes(info, cell))
    rep.check(lives == 1, f"{cell}: {lives} process lifetime(s), expected 1")
    warned = [line for line in lines if rigmerge.is_warning(line) and "Merge retire:" in line]
    rep.check(not warned, f"{cell}: {len(warned)} warning-level 'Merge retire' line(s)"
              + (f" ({warned[0].strip()[:120]})" if warned else ""))


def grade_prunes(rep, rig, retiring, e3, start, day, step="e3"):
    """Every cell that applied the e3 pair logs 'Retired zone <retiring>: link closed, 0 record(s) released' once; no
    cell logs a warning-level or non-zero prune line, or a lent/borrowed monster release. `step` names the pair in the
    labels (Slice 2d's aborted split retires its child in a 'retired' broadcast)."""
    applied = 0
    for zone, map_id in rig.graded_zones(day):
        if zone == retiring:
            continue
        lines = rig.zone_lines(zone, start, day, map_id)
        if rigmerge.applied_pair(lines, e3):
            applied += 1
            ns = rigmerge.prunes(lines, retiring)
            rep.check(ns == [0], f"zone {zone}: applied {step} {e3} and logged 'Retired zone {retiring}: link closed, "
                                 f"0 record(s) released' once ({ns or 'none'})")
        bad = [line for line in lines if rigmerge.PRUNE.search(line)
               and (rigmerge.is_warning(line) or rigmerge.PRUNE.search(line).group(2) != "0")]
        loans = [line for line in lines if rigmerge.LOAN_RELEASED.search(line)]
        if bad or loans:
            rep.check(False, f"zone {zone}: {len(bad)} warning prune line(s), {len(loans)} lent/borrowed monster "
                             f"release(s)" + f" ({(bad + loans)[0].strip()[:120]})")
        lost = rigmerge.link_losts(lines, retiring)
        if lost:
            rep.notes.append(f"   zone {zone}: {lost} 'link lost (draining, retire expected)' line(s) for {retiring}")
    rep.check(applied > 0, f"{applied} cell(s) applied the {step} {e3}" if step != "e3" else
              f"{applied} cell(s) applied the merge's e3 {e3}")


def grade_entities(rep, rig, spec, pairs, start, day):
    """merge: no pre-warmed monster spawned by handoff back to the survivor (2b's id rule, against its ghosts before
    the merge's e2), promotions and respawns as notes; the statics the child held at e2 sent back, taken, none failed
    (a note when it held none); at least one synthetic player adopted back."""
    base, child = spec.base, spec.child
    base_lines = rig.zone_lines(base, start, day, rig.MAP)
    child_lines = rig.zone_lines(child, start, day, rig.MAP)
    during = rigmerge.after_applied(base_lines, pairs.get("e1")) or []
    prewarm = rigsplit.until_applied(base_lines, pairs.get("e2"))
    existing, respawned = rigsplit.monster_spawns(during, prewarm, child, base)
    promoted = rigsplit.count_kind(during, rigsplit.MONSTER_HANDOFF, "promoted", child, base)
    rep.check(not existing, f"{base}: {len(existing)} pre-warmed monster(s) spawned by handoff {child}->{base}"
              + (f" ({', '.join(existing[:3])})" if existing else ""))
    rep.notes.append(f"   {base}: {promoted} monster(s) promoted and {len(respawned)} respawn(s) spawned by handoff "
                     f"{child}->{base} during the merge")
    held = rigmerge.statics_held(rigsplit.until_applied(child_lines, pairs.get("e2")), base, child)
    sent = rigsplit.count_kind(rigmerge.after_applied(child_lines, pairs.get("e2")) or [], rigsplit.STATIC_HANDOFF,
                               "sent", child, base)
    taken = sum(rigsplit.count_kind(base_lines, rigsplit.STATIC_HANDOFF, k, child, base) for k in ("promoted", "spawned"))
    failed = sum(rig.count(lines, r"Static handoff \S+ zone \S+: failed") for lines in (base_lines, child_lines))
    if held > 0:
        rep.check(sent >= 1 and taken >= 1 and failed == 0,
                  f"statics: {child} held {held} at e2, sent {sent} to {base}, {taken} taken, {failed} failed")
    else:
        rep.notes.append(f"   statics: {child} held none at e2 ({sent} sent, {taken} taken, {failed} failed)")
    adopted = rigsplit.adoptions(base_lines, child, base)
    rep.check(len(adopted) >= 1, f"{base} adopted {len(adopted)} synthetic player(s) back from {child}")


def grade_c2(rep, rig, r, pairs, start, day):
    """mergec2: C2 pruned the child, its e3 band holds the survivor and not the child, and an R-bot's puppet on C2
    changed owner child->survivor at its translated position."""
    spec = r.spec
    c2 = rig.zone_lines("C2", start, day, rig.MAP2)
    ns = rigmerge.prunes(c2, spec.child)
    rep.check(ns == [0], f"C2 logged 'Retired zone {spec.child}: link closed, 0 record(s) released' ({ns or 'none'})")
    e3 = pairs.get("e3")
    bands = [ids for pair, _, ids in rigsplit.neighbour_bands(c2, "C2") if pair == e3]
    rep.check(bool(bands) and spec.base in bands[-1] and spec.child not in bands[-1],
              f"C2's band at the merge's e3 ({e3}) holds {spec.base} and not {spec.child}: "
              f"{bands[-1] if bands else 'no apply'}")
    after = rigmerge.after_applied(c2, pairs.get("e1"))
    moved = []
    for b in r_bots(r):
        at = r.standing.get(r.logs[b.user]) or b.point
        if rigmerge.owner_moved_at(c2, after, b.character, spec.child, spec.base, at[0] + regress_split.C2_OFFSET_X,
                                   at[1]):
            moved.append(b.user)
    rep.check(bool(moved), f"C2: an R-bot's puppet changed owner {spec.child}->{spec.base} at its translated position "
                           f"({', '.join(moved) or 'none'})")


def grade_layout(rep, rig, start, day, mr):
    """regress.grade_layout for a merge scenario: the split's map against rigmerge.merge_expected_applies (pairs from
    both statuses), the op lines rigmerge allows, everything else as regress_split.grade_layout."""
    split = mr.r.split
    split_pairs = (mr.r.status or {}).get("pairs", {})
    merge_pairs = (mr.status or {}).get("pairs", {})
    regress_split.grade_layout(
        rep, rig, start, day, split,
        applies=lambda zone, info: rigmerge.merge_apply_violations(zone, info, split, split_pairs, merge_pairs),
        ops=lambda zone, lines: len(rigmerge.disallowed_op_lines(zone, lines, split.child)),
        how_split="merge map: the split's and the merge's layouts by rectangle")
