"""Slice 2d's supervision scenarios for regress.py (Docs/ServerMeshing/slices/slice2d_design.md decisions 16-19):

    python Tools/ServerMeshing/regress.py splitcrash_prewarm splitcrash_drain splitcrash_base

Each is regress_split's `split` of C at x = 0 (child C.1) with the eight stand bots (meshbot2..5 in the child's half,
meshbot6..9 in the kept half), no harvest bot, no synthetic players and only Spawn_Interior_C's five monsters:
splitcrash_prewarm: C.1 dies on the e1 broadcast (MESH_FAULT_DIE_ON_PREWARM); the split aborts, retires C.1 and
  nothing restarts it.
splitcrash_drain: C.1 dies on its first character arrival of the drain (MESH_FAULT_DIE_ON_DRAIN); the split pauses,
  the supervisor restarts C.1, the split resumes and ends done.
splitcrash_base: no tester; 10 s after the split's done the harness kills C (the kit restarts it); C.1 releases the
  monsters it borrowed from C's old incarnation, C comes back owning its kept half, and the P-bots re-log in.

regress.py hands itself in as `rig` (as for regress_split and regress_merge: request_play, launch_bot, zone_lines, ps,
EXE, ...), so this module never imports it; the unit tests hand in a fake. The graders are rigsplitcrash's.
"""
import collections
import datetime
import time

import regress_merge
import regress_split
import rigmerge
import rigsplit
import rigsplitcrash
import rigtools

SCENARIOS = ("splitcrash_prewarm", "splitcrash_drain", "splitcrash_base")
TWO_HOST_REASON = regress_split.TWO_HOST_REASON
PERSIST_REASON = regress_split.PERSIST_REASON

MONSTER_AREAS = "only:Spawn_Interior_C"   # decision 13: every other monster area off, so the total is exact
PLANNED_MONSTERS = 5                       # Spawn_Interior_C's amount (min = max)
ABORT_TIMEOUT_S = 90       # prewarm: the split's command to its 'aborted' (about 10 s after the FAULT)
PAUSE_TIMEOUT_S = 120      # drain: to 'op paused' (graded as paused before done; the wait is the op's)
DONE_TIMEOUT_S = regress_split.SPLIT_TIMEOUT_S   # drain: to 'done', 240 s in all
POLL_SLACK_S = 3           # the status is polled every 2 s and the seq is the command's wall second
GONE_AFTER_ABORT_S = 30    # prewarm: no C.1 process for this long after the abort
KILL_AFTER_DONE_S = 10     # base: the kill of C this long after the split's done
ANSWER_TIMEOUT_S = 120     # base (D): the kill to C's second lifetime applying the central's answer
RELEASE_WITHIN_S = 30      # base: each held monster released on C.1 within this of the kill
MONSTER_WINDOW_S = 90      # base: the owned-monster sums are read over this long after C's answer
PAIR_TOLERANCE_S = 5.0
SUM_RUN = 3
SAMPLE_EVERY_S = regress_merge.SAMPLE_EVERY_S
RESULT_SLACK_S = 90

Case = collections.namedtuple("Case", "kind observe_s env enabled")
CASES = {
    "splitcrash_prewarm": Case("prewarm", 180, {"MESH_MONSTER_AREAS": MONSTER_AREAS, "MESH_FAULT_DIE_ON_PREWARM": "C.1"},
                               True),
    "splitcrash_drain": Case("drain", 300, {"MESH_MONSTER_AREAS": MONSTER_AREAS, "MESH_FAULT_DIE_ON_DRAIN": "C.1"},
                             True),
    "splitcrash_base": Case("base", 300, {"MESH_MONSTER_AREAS": MONSTER_AREAS}, False),
}
FAULT_KEYS = {"prewarm": "MESH_FAULT_DIE_ON_PREWARM", "drain": "MESH_FAULT_DIE_ON_DRAIN"}
# What run() hands grade_layout: the split phase's SplitRun and the case.
CrashRun = collections.namedtuple("CrashRun", "name case r")


def stand_plan():
    """The eight stand bots of `split` (meshbot2..5 in R, meshbot6..9 in the kept half), without the harvest bot."""
    return [b for b in regress_split.plan_bots("split") if not b.harvest]


def run(name, rep, rig, start, day, bots):
    """Runs one crash scenario against the rig (regress) and records its checks on rep; appends every bot to `bots`.
    Returns a CrashRun for grade_layout."""
    case = CASES[name]
    child = regress_split.SPECS["split"].child
    samples = []

    def sample():
        samples.append(regress_merge.count_processes(rig, child))
    r = regress_split.start_split(name, rep, rig, start, day, bots, spec_name="split", observe_s=case.observe_s,
                                  plan=stand_plan(), load_plan="", enabled=case.enabled, env=dict(case.env),
                                  grade_status=regress_split.grade_status if case.kind == "base" else None,
                                  on_poll=sample if case.kind != "base" else None)
    cr = CrashRun(name, case, r)
    if not r.sent:
        return cr
    {"prewarm": flow_prewarm, "drain": flow_drain, "base": flow_base}[case.kind](rep, rig, cr, samples, start, day)
    return cr


def status_now(rig, r):
    """The split command's status as the file stands now (rigsplitcrash.crash_status)."""
    return rigsplitcrash.crash_status(regress_split.read_lines(rig.LAYOUT_COMMAND_STATUS), r.seq)


def grade_accepted(rep, status, spec):
    rep.check(status["requested"] is not None and not status["refused"],
              f"split {spec.base} at x={spec.at:g} accepted ({status['op'] or 'no op'}"
              + (f"; refused: {status['refused']}" if status["refused"] else "") + ")")
    for text in [t for t in status["other"] if not t.startswith("Supervisor:")][:3]:
        rep.notes.append(f"   status: {text[:160]}")


def grade_order(rep, texts, patterns, label):
    problems = rigsplitcrash.order_problems(texts, patterns)
    rep.check(not problems, label + (f" ({'; '.join(problems[:3])})" if problems else ""))


def grade_fault(rep, rig, cell, key, start, day):
    n = len(rigsplitcrash.faults(rig.zone_lines(cell, start, day, rig.MAP), key))
    rep.check(n == 1, f"{cell}: {n} 'FAULT: dying on ... ({key})' line(s), expected 1")


def sample_for(rig, cell, seconds):
    """The cell's process counts, sampled every SAMPLE_EVERY_S for `seconds` (None for a failed count)."""
    end, out = time.time() + seconds, []
    while True:
        out.append(regress_merge.count_processes(rig, cell))
        if time.time() >= end:
            return out
        time.sleep(SAMPLE_EVERY_S)


def wait_results(r, observe_s):
    stands = [r.stood[log] for log in r.logs.values() if log in r.stood] or [time.time()]
    return regress_split.wait_results(list(r.logs.values()),
                                      max(10.0, max(stands) + observe_s + RESULT_SLACK_S - time.time()))


def results_of(r, results):
    """{user: (bot, parsed RESULT, log lines)}."""
    return {b.user: (b, rigsplit.parse_result(results.get(r.logs[b.user], "")),
                     regress_split.read_lines(r.logs[b.user])) for b in r.plan}


def flow_prewarm(rep, rig, cr, samples, start, day):
    """Decision 16: the abort within ABORT_TIMEOUT_S, its states, no restart, no C.1 process after it, the bots."""
    r, spec = cr.r, cr.r.spec
    elapsed = time.time() - int(r.seq)
    # The abort's retired broadcast and channel removal follow its line: the samples come first, the status after them.
    after = sample_for(rig, spec.child, GONE_AFTER_ABORT_S)
    status = status_now(rig, r)
    grade_accepted(rep, status, spec)
    rep.check(status["lost_aborted"] == spec.child and elapsed <= ABORT_TIMEOUT_S + POLL_SLACK_S,
              f"split aborted '{spec.child} lost' within {ABORT_TIMEOUT_S} s ({status['aborted'] or 'no abort'}, "
              f"{elapsed:.0f} s after the command)")
    grade_order(rep, status["texts"], [rigsplitcrash.op_pattern(s) for s in ("e1 staged", "e1 acked")]
                + [r"^Split \S+: e1 broadcast \S+$", rigsplitcrash.op_pattern(f"aborted {spec.child} lost")],
                "split states e1 staged, e1 acked, e1 broadcast, aborted on the loss")
    aborted = rigsplitcrash.op_pattern(f"aborted {spec.child} lost")
    grade_order(rep, status["texts"], [aborted, r"^Split \S+: retired broadcast \S+$"], "retired broadcast after the abort")
    grade_order(rep, status["texts"], [aborted, rigsplitcrash.op_pattern(f"channel {spec.child} removed")],
                f"channel {spec.child} removed after the abort")
    rep.check(not status["done"] and not status["frozen"], f"split neither done nor frozen "
              f"({'done' if status['done'] else status['frozen'] or 'clean'})")
    restarts = [s["text"] for s in status["supervisor"] if s["cell"] == spec.child and s["event"] in ("down", "hung")]
    rep.check(not restarts, f"no restart requested for the aborted {spec.child}" + (f" ({restarts[0]})" if restarts else ""))
    rep.notes.append(f"   {spec.child}: process counts during the split {rigsplitcrash.process_sequence(samples)}")
    rep.check(after and all(n == 0 for n in after), f"no process with '{rigmerge.channel_filter(spec.child)}' for "
              f"{GONE_AFTER_ABORT_S} s after the abort ({rigsplitcrash.process_sequence(after)})")
    grade_fault(rep, rig, spec.child, FAULT_KEYS["prewarm"], start, day)
    regress_merge.grade_prunes(rep, rig, spec.child, status["retired"], start, day, step="retired broadcast")
    for user, (b, result, _) in results_of(r, wait_results(r, cr.case.observe_s)).items():
        problems = rigsplitcrash.bot_problems(result, 0, 0)
        rep.check(not problems, f"{user}: no migration, no re-login" + (f" ({'; '.join(problems)})" if problems else ""))


def flow_drain(rep, rig, cr, samples, start, day):
    """Decision 17: pause, restart, rebind and done in order; C.1's process 1 -> 0 -> 1; the per-bot rule."""
    r, spec = cr.r, cr.r.spec
    status = status_now(rig, r)
    grade_accepted(rep, status, spec)
    took = (r.done_at - int(r.seq)) if r.done_at else None
    rep.check(status["done"] and took is not None and took <= DONE_TIMEOUT_S + POLL_SLACK_S,
              f"split done within {DONE_TIMEOUT_S} s ({'%.0f s' % took if took is not None else 'not done'})")
    bad = [status[k] for k in ("aborted", "frozen", "ended") if status[k]]
    rep.check(not bad, "split neither aborted, frozen nor ended" + (f" ({bad[0]})" if bad else ""))
    child = spec.child.replace(".", r"\.")
    grade_order(rep, status["texts"], [rigsplitcrash.op_pattern(f"{spec.child} lost, op paused"),
                                       rf"^Supervisor: {child} down \S+ s, restart 1 of {rigsplitcrash.CHILD_MAX_RESTARTS}"
                                       r" requested$", rf"^Supervisor: {child} back, incarnation [0-9a-f]+$",
                                       rf"^Split \S+: {child} rebound [0-9a-f]+, op resumed$",
                                       rigsplitcrash.op_pattern("done")],
                f"{spec.child} lost and paused, restart 1 requested, back, rebound and resumed, done (paused within "
                f"{PAUSE_TIMEOUT_S} s of a {DONE_TIMEOUT_S} s op)")
    back = [s["incarnation"] for s in status["supervisor"] if s["cell"] == spec.child and s["event"] == "back"]
    rebound = [inc for cell, inc in status["rebound"] if cell == spec.child]
    rep.check(bool(back) and bool(rebound) and back[-1] == rebound[-1],
              f"{spec.child} rebound to the incarnation the supervisor saw back ({back[-1:] or 'none'} vs "
              f"{rebound[-1:] or 'none'})")
    if status["drain"] == "timeout" or status["settle"]:
        rep.notes.append(f"   drain {status['drain']}, settle {status['settle'] or 'none'} (a note, decision 17)")
    seq = rigsplitcrash.process_sequence(samples + [regress_merge.count_processes(rig, spec.child)])
    rep.check(None not in seq and rigsplitcrash.contains_in_order(seq, [1, 0, 1]) and seq[-1] == 1,
              f"{spec.child}: process count 1 -> 0 -> 1 ({seq})")
    grade_fault(rep, rig, spec.child, FAULT_KEYS["drain"], start, day)
    results = results_of(r, wait_results(r, cr.case.observe_s))
    base_lines = rig.zone_lines(spec.base, start, day, rig.MAP)
    lives = rigsplit.lifetimes(rig.zone_lines(spec.child, start, day, rig.MAP, suffixes=("info",)), spec.child)
    came_back = rigtools.stamp_datetime(lives[1][0]) if len(lives) > 1 else None
    total_f = 0
    for user, (b, result, lines) in results.items():
        if b.side != regress_split.SIDE:
            problems = rigsplitcrash.bot_problems(result, 0)
            rep.check(not problems, f"{user}: no migration" + (f" ({'; '.join(problems)})" if problems else ""))
            continue
        starts = rig.count(base_lines, rigsplit.handoff_start(b.character, spec.base, spec.child).pattern)
        acks = sum(1 for line in base_lines for m in [rigsplitcrash.HANDOFF_ACK.search(line)]
                   if m and m.groups() == (b.character, spec.child) and came_back is not None
                   and (rigtools.stamp_datetime(line) or came_back) >= came_back)
        f, problems = rigsplitcrash.drain_bot_problems(result, lines, starts, acks, spec.at)
        total_f += f
        rep.check(not problems, f"{user}: migrations and START 1 + fellBack, ready on {spec.child}, acked after its "
                                f"restart" + (f" ({'; '.join(problems)})" if problems else ""))
    rollbacks = sum(1 for line in base_lines for m in [rigsplitcrash.ROLLBACK.search(line)] if m and m.group(2) == spec.child)
    rep.check(total_f >= 1 and total_f == rollbacks, f"R-bots fell back {total_f} time(s) in all, {spec.base} logged "
                                                     f"{rollbacks} Handoff ROLLBACK -> zone {spec.child} (equal, >= 1)")
    relogged = [u for u, (_, result, _) in results.items() if result.get("relogins")]
    if relogged:
        rep.notes.append(f"   re-logged in during the drain case: {', '.join(relogged)}")


def wait_answer(rig, split, start, day, timeout):
    """The base's first apply line in its second lifetime (the central's answer to the restarted base), or None."""
    end = time.time() + timeout
    while True:
        lives = rigsplit.lifetimes(rig.zone_lines(split.base, start, day, split.map_id, suffixes=("info",)))
        if len(lives) > 1:
            line = next((t for t in lives[1] if rigsplit.parse_apply(t)), None)
            if line:
                return line
        if time.time() >= end:
            return None
        time.sleep(2)


def by_stamp(lines):
    """A zone's lines (info, warn and error files one after the other) in stamp order, a file's own order kept."""
    return sorted(lines, key=lambda line: rigtools.stamp_datetime(line) or datetime.datetime.min)


def flow_base(rep, rig, cr, samples, start, day):
    """Decision 18: the kill of C, its second lifetime and answer, the held monsters released, the monster sums, the
    bots (P-bots re-logged in onto C's second lifetime)."""
    r, spec, split = cr.r, cr.r.spec, cr.r.split
    if r.applied is None:
        rep.check(False, f"{spec.base}: not killed, the split did not reach its e3 apply")
        return
    time.sleep(max(0.0, r.done_at + KILL_AFTER_DONE_S - time.time()))
    try:
        killed = rigmerge.parse_count(rig.ps(rigsplitcrash.kill_script(rig.EXE, spec.base)))
    except (RuntimeError, ValueError) as ex:
        rep.check(False, f"{spec.base}: the kill failed ({str(ex)[:120]})")
        return
    kill_at = time.time()
    kill_second = datetime.datetime.fromtimestamp(kill_at).replace(microsecond=0)
    rep.check(killed == 1, f"killed {killed} process(es) with '{rigmerge.channel_filter(spec.base)}', expected 1")
    answer = wait_answer(rig, split, start, day, ANSWER_TIMEOUT_S)
    rep.check(answer is not None, f"{spec.base} came back and applied the central's answer within {ANSWER_TIMEOUT_S} s "
                                  "of the kill")
    if answer is not None:
        time.sleep(max(MONSTER_WINDOW_S + PAIR_TOLERANCE_S, kill_at + RELEASE_WITHIN_S - time.time()))
    results = results_of(r, wait_results(r, cr.case.observe_s))
    base_lines = by_stamp(rig.zone_lines(spec.base, start, day, rig.MAP))
    child_lines = by_stamp(rig.zone_lines(spec.child, start, day, rig.MAP))
    head = rigsplitcrash.incarnation(rigsplitcrash.before(base_lines, kill_second), spec.base)
    head = f"{spec.base}:{head}" if head else None
    held = rigsplitcrash.held_monsters(rigsplitcrash.before(child_lines, kill_second), spec.base, spec.child, head)
    if held:
        missing = rigsplitcrash.unreleased(held, child_lines, spec.base, spec.child, kill_second, RELEASE_WITHIN_S)
        rep.check(not missing, f"{spec.child} released the {len(held)} monster(s) it held of {spec.base}'s old "
                               f"incarnation within {RELEASE_WITHIN_S} s of the kill"
                  + (f" (not: {', '.join(missing[:4])})" if missing else ""))
    else:
        rep.notes.append(f"   {spec.child} held no monster of {spec.base} at the kill (about 1 run in 32)")
    rep.notes.append(f"   {spec.child}: {rigsplitcrash.statics_released(child_lines, head) if head else 0} borrowed "
                     f"static(s) of {spec.base}'s old incarnation released (a note)")
    if answer is None:
        return
    answered = rigtools.stamp_datetime(answer)
    second = [line for line in base_lines if (rigtools.stamp_datetime(line) or kill_second) >= answered]
    planned = [int(m.group(2)) for line in base_lines for m in [rigsplitcrash.SPAWN_AREAS.search(line)]
               if m and m.group(1) == spec.base and (rigtools.stamp_datetime(line) or kill_second) > kill_second]
    rep.check(planned and planned[-1] == PLANNED_MONSTERS,
              f"{spec.base} logged 'Spawn areas of {spec.base}: {PLANNED_MONSTERS} monster(s) planned in the base "
              f"rectangle' after its restart ({planned or 'none'})")
    window_end = answered + datetime.timedelta(seconds=MONSTER_WINDOW_S)
    tol = datetime.timedelta(seconds=PAIR_TOLERANCE_S)
    pairs = rigsplitcrash.monster_pairs(rigsplitcrash.windows(second, answered, window_end),
                                        rigsplitcrash.windows(child_lines, answered - tol, window_end + tol),
                                        PAIR_TOLERANCE_S)
    sums = [s for _, s in pairs]
    problems = rigsplitcrash.monster_sum_problems(sums, PLANNED_MONSTERS, SUM_RUN)
    rep.check(not problems, f"owned monsters {spec.base} + {spec.child} within {MONSTER_WINDOW_S} s of the answer"
              + (f" ({'; '.join(problems)})" if problems else f": {PLANNED_MONSTERS} for {SUM_RUN} pairs, never over"))
    registered = [line for line in second if rigtools.PLAYER_REGISTERED.search(line)]
    for user, (b, result, lines) in results.items():
        if b.side == regress_split.SIDE:
            problems = rigsplitcrash.bot_problems(result, 1, 0)
            rep.check(not problems, f"{user}: the split's migration, no re-login"
                      + (f" ({'; '.join(problems)})" if problems else ""))
            continue
        problems = rigsplitcrash.bot_problems(result, {0, 1}, 1)
        if not rigsplitcrash.routed(lines):
            problems.append("no 'Re-login routed to' line")
        if not any(f" S player registered {b.character} " in line for line in registered):
            problems.append(f"not registered on {spec.base}'s second lifetime")
        rep.check(not problems, f"{user}: re-logged in once and ended on {spec.base}"
                  + (f" ({'; '.join(problems)})" if problems else ""))


def grade_layout(rep, rig, start, day, cr):
    """regress.grade_layout for a crash scenario: the split's map per process lifetime against
    rigsplitcrash.crash_expected_lives (pairs from the status file as it stands now), the map-side lines the case
    allows (rigsplitcrash.disallowed_lines), everything else as regress_split.grade_layout."""
    r, kind = cr.r, cr.case.kind
    split, child = r.split, r.split.child
    status = status_now(rig, r) if r.seq else {"pairs": {}, "retired": None, "settle": None, "drain": None}
    pairs = dict(status["pairs"], retired=status["retired"], settle=status["settle"])
    timeout = status["drain"] == "timeout"
    regress_split.grade_layout(
        rep, rig, start, day, split,
        applies=lambda zone, info: rigsplitcrash.lifetime_apply_violations(
            zone, info, split, lambda own: rigsplitcrash.crash_expected_lives(kind, split, zone, own, pairs, timeout),
            label=f"the {kind} case"),
        ops=lambda zone, lines: len(rigsplitcrash.disallowed_lines(
            zone, lines, faulted=child if kind != "base" else None, retired=child if kind == "prewarm" else None,
            allow_aborted=kind == "prewarm")),
        how_split=f"split map ({kind} case): layouts by rectangle per process lifetime")
