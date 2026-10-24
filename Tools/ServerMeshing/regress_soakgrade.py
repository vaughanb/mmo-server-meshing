"""The graders of the dynamic-cell soak (regress_soak.py, DESIGN 0.91), split out of it under the ~500-line rule.

grade() takes the SoakRun the flow left and the central's log: the operations and the policy's and supervisor's lines
(rigsoak.central_stamped), the central's lifetimes (one adoption per restart, nothing frozen left), the store's samples, the processes'
memory, the bots' stamped lines; it records the checks on the report, a summary in its notes and the whole account in
the report file. grade_layout() holds every cell of the test map, pair by pair, to one owner per rectangle and to the
rectangles riglayout derives for the done operations. regress.py hands itself in as `rig`; the readers are rigsoak's,
rigsoakload's and rigcentral's.
"""
import re
import time

import rigcentral
import riglayout
import rigsoak
import rigsoakload
import rigsplit
import rigtools

STUCK_S = 600.0             # an operation's first line to its end
RELOGIN_WINDOW_S = 90.0     # a session lost this long after a child kill is the kill's
WARMUP_S = 900.0            # memory and store series: from this long after their first sample
MIN_SPAN_S = 1800.0         # graded only when the warmed-up series spans this long
MEM_ABS_MB, MEM_REL = 25.0, 0.05
STORE_ABS_BYTES, STORE_REL = 2048.0, 0.10
COUNT_ABS = 2.0             # retired entries, aborted ids
FENCED_PER_CELL = 16        # MeshFencedSet.KeptPerCell
ASSET_CUT = re.compile(r"Coordinator recovery: map (\S+): no runtime layout anywhere; running its asset cut")


def _lines_of(stamped):
    return [line for _, line in stamped]


def op_lines(rig, start, day):
    """The central's operation, policy and supervisor lines with their stamps (rigsoak.central_stamped)."""
    return rigsoak.central_stamped(rig.central_lines(start, day))


def grade(rep, rig, start, day, sr):
    """Every whole-run check but the layout's (grade_layout); writes sr.report."""
    out = [f"soak {sr.name}: {(sr.drained or time.time()) - sr.began:.0f} s from the bots' start to the drain's end"]
    stamped = op_lines(rig, start, day)
    ops = rigsoak.operations(stamped)
    counts = rigsoak.op_counts(ops)
    lines = _lines_of(stamped)
    grade_ops(rep, ops, counts, out)
    policy, sup = rigsoak.policy_counts(lines), rigsoak.supervisor_counts(lines)
    rep.notes.append(f"   operations: {counts['splits']} splits, {counts['merges']} merges, {counts['collapses']} collapses, "
                     f"{counts['aborted']} aborted, {counts['ended']} ended collapsing, {counts['dropped']} dropped after a restart")
    rep.notes.append(f"   policy: {policy['planned_splits']} splits and {policy['planned_merges']} merges planned, "
                     f"{policy['refused']} refused, {policy['deferred']} deferred, {policy['flaps']} flaps, "
                     f"{policy['coordinator_refused']} refused by the coordinator; supervisor: {sup['down'] + sup['hung']} "
                     f"restarts requested, {sup['back']} back, {sup['failed']} failed, {sup['collapse']} collapses queued")
    out += [f"operations {counts}", f"policy {policy}", f"supervisor {sup}"]
    grade_faults(rep, rig, start, day, sr, out)
    grade_store(rep, sr, out)
    grade_memory(rep, rig, start, day, sr, out)
    grade_bots(rep, sr, out)
    plan = sr.plan
    rep.notes.append(f"   load: {plan.started} episode(s), {plan.completed} completed, {plan.timed_out} phase(s) cut short")
    out.append(f"episodes started {plan.started}, completed {plan.completed}, phases cut short {plan.timed_out}")
    with open(sr.report, "w", encoding="utf-8") as f:
        f.write("\n".join(out) + "\n")
    rep.notes.append(f"   report: {sr.report}")


def grade_ops(rep, ops, counts, out):
    rep.check(counts["splits"] >= 1 and counts["merges"] >= 1,
              f"the policy split and merged during the soak ({counts['splits']} splits, {counts['merges']} merges)")
    rep.check(counts["open"] == 0 and counts["frozen_open"] == 0,
              f"every operation ended (done, aborted or ended): {counts['open']} open, {counts['frozen_open']} frozen")
    late = rigsoak.stuck(ops, time.time(), STUCK_S)
    rep.check(not late, f"no operation took longer than {STUCK_S:g} s" + (f" ({late[:3]})" if late else ""))
    for op in ops:
        took = (op["ended_at"] or time.time()) - op["seen_at"]
        out.append(f"op {op['kind']} {op['op']} {op['requested']}: {op['outcome']} in {took:.0f} s"
                   + (f", resumed at {op['resumed']}" if op["resumed"] else "") + (", was frozen" if op["frozen"] else ""))


def grade_faults(rep, rig, start, day, sr, out):
    """The kills and the central restarts: as many as planned, each restart adopted by the next lifetime, nothing frozen
    left in the last lifetime, no exception in the central's log."""
    soak_s, every_s = float(sr.settings["minutes"]) * 60.0, float(sr.settings["kill_every"]) * 60.0
    expected = int(min(sr.drained or time.time(), sr.began + soak_s) - sr.began) // int(every_s)
    rep.check(len(sr.kills) >= max(0, expected - 1), f"{len(sr.kills)} child kill(s), {expected} planned "
              f"({', '.join(c for _, c in sr.kills[:20])})")
    out += [f"kill {time.strftime('%H:%M:%S', time.localtime(t))} {c}" for t, c in sr.kills]
    failed = [d for _, ok, d in sr.restarts if not ok]
    rep.check(not failed, f"{len(sr.restarts)} central restart(s), every one killed one process and came back" + (f" ({failed[0]})" if failed else ""))
    out += [f"central restart {time.strftime('%H:%M:%S', time.localtime(t))}: {d}" for t, _, d in sr.restarts]
    central = rig.central_lines(start, day)
    lives = rigcentral.lifetimes(sorted(central, key=lambda line: rigtools.stamp_of(line) or ("", "")))
    # A restart while no family is cut finds no runtime layout and runs the asset cut; otherwise it adopts one.
    recovered = ["adopted" if any(a["map"] == rig.MAP for a in rigcentral.adoptions(life))
                 else "asset cut" if any(m and m.group(1) == rig.MAP for m in map(ASSET_CUT.search, life)) else "none"
                 for life in lives[1:]]
    rep.check(len(lives) == len(sr.restarts) + 1 and "none" not in recovered,
              f"{len(lives)} central lifetime(s), each restarted one recovered {rig.MAP}: adopted a layout or ran the asset cut ({recovered})")
    last = lives[-1] if lives else []
    unresolved = frozen_unresolved(last)
    rep.check(not unresolved, "nothing frozen left in the central's last lifetime" + (f" ({unresolved[:2]})" if unresolved else ""))
    frozen_any = [rigcentral.payload(line)[:100] for life in lives for line in rigcentral.frozen_lines(life)]
    if frozen_any or sr.unfreezes:
        rep.notes.append(f"   {len(frozen_any)} frozen line(s) in the central's log, {len(sr.unfreezes)} operator unfreeze(s)"
                         + (f"; first: {frozen_any[0]}" if frozen_any else ""))
    out += [f"operator unfreeze {time.strftime('%H:%M:%S', time.localtime(t))} {b}: {why}" for t, b, why in sr.unfreezes]
    ex = [line for line in central if "Exception" in line]
    rep.check(not ex, f"central: {len(ex)} exception lines" + (f" ({ex[0][:120]})" if ex else ""))


def frozen_unresolved(lines):
    """The frozen families of a central lifetime with no later 'unfrozen' line (a hello, or the operator's unfreeze),
    and its map-level frozen lines."""
    state = {}
    for line in lines:
        m = rigcentral.FROZEN_FAMILY.search(line)
        if m:
            state[(m.group(1), m.group(2))] = rigcentral.payload(line)
        m = rigcentral.UNFROZEN.search(line) or rigsoak.OPERATOR_UNFROZEN.search(line)
        if m:
            key = (m.group(1), m.group(2)) if m.re is rigcentral.UNFROZEN else (m.group(2), m.group(3))
            state.pop(key, None)
    maps = [rigcentral.payload(line) for line in lines if rigcentral.MAP_FROZEN.search(line) and not rigcentral.FROZEN_FAMILY.search(line)]
    return list(state.values()) + maps


def grade_store(rep, sr, out):
    """The store at the end and over the run: no operation, the fenced set within its bound, size and retired entries flat."""
    rep.check(sr.drained is not None, "every family whole again and no operation stored once the burn was off")
    samples = sr.store_samples
    if not samples:
        rep.check(False, "no store sample")
        return
    last = samples[-1][1]
    rep.check(not last["operation"], f"no operation stored at the end ({last})")
    fenced = max(s["fenced_max"] for _, s in samples)
    rep.check(fenced <= FENCED_PER_CELL, f"the fenced set kept at most {FENCED_PER_CELL} per cell (at most {fenced})")
    for key, abs_tol, rel_tol in (("bytes", STORE_ABS_BYTES, STORE_REL), ("retired", COUNT_ABS, 0.0), ("aborted", COUNT_ABS, 0.0),
                                  ("entries", COUNT_ABS, 0.0)):
        series = [(t, s[key]) for t, s in samples]
        flat = rigsoak.flatness(series, WARMUP_S, abs_tol, rel_tol, MIN_SPAN_S)
        what = f"store {key}: max {max(v for _, v in series):g}, last {series[-1][1]:g}"
        if flat is None:
            rep.notes.append(f"   {what} (too short to grade)")
        else:
            rep.check(flat.ok, f"{what}, flat after warm-up (peak {flat.first_max:g} then {flat.second_max:g}, {flat.slope_per_h:+.1f}/h)")
        out.append(f"{what} {flat}")


def grade_memory(rep, rig, start, day, sr, out):
    """Every long-lived process flat: the OS's private bytes of the central, the spawner and each cell (sampled here),
    and each cell's Mesh stats memory per process lifetime."""
    graded, bad = 0, []
    for (role, pid), series in sorted(rigsoakload.process_series(sr.samples).items()):
        if role.startswith("bot:"):
            continue
        flat = rigsoak.flatness(series, WARMUP_S, MEM_ABS_MB, MEM_REL, MIN_SPAN_S)
        out.append(f"process {role} pid {pid}: {len(series)} samples, {series[0][1]:.0f} -> {series[-1][1]:.0f} MB private, {flat}")
        if flat is not None:
            graded += 1
            if not flat.ok:
                bad.append(f"{role} pid {pid} {flat.first_max:.0f}->{flat.second_max:.0f} MB")
            if role in ("central", "spawner", "cell:C", "cell:E"):
                rep.notes.append(f"   {role} pid {pid}: private {flat.first_max:.0f} -> {flat.second_max:.0f} MB peak, {flat.slope_per_h:+.1f} MB/h")
    _flat_check(rep, graded, bad, "OS private memory flat for {} long-lived process(es)")
    graded, bad = 0, []
    for zone, map_id in rig.graded_zones(day, start):
        for i, series in enumerate(rigsoakload.stats_memory(zone, rig.zone_lines(zone, start, day, map_id, suffixes=("info",)))):
            flat = rigsoak.flatness(series, WARMUP_S, MEM_ABS_MB, MEM_REL, MIN_SPAN_S)
            if flat is None:
                continue
            graded += 1
            out.append(f"stats mem {zone} lifetime {i + 1}: {flat}")
            if not flat.ok:
                bad.append(f"{zone}#{i + 1} {flat.first_max:.0f}->{flat.second_max:.0f} MB")
    _flat_check(rep, graded, bad, "Mesh stats memory flat for {} long cell lifetime(s)")


def _flat_check(rep, graded, bad, label):
    """A memory check over the series long enough to grade; a note when none is (a short soak)."""
    if not graded:
        rep.notes.append(f"   {label.format(0)}: none ran {(WARMUP_S + MIN_SPAN_S) / 60:g} min (too short to grade)")
        return
    rep.check(not bad, label.format(graded) + (f" (grew: {'; '.join(bad[:4])})" if bad else ""))


def grade_bots(rep, sr, out):
    """No session lost except after a child kill, never a kick, no re-login given up (the bot left without a character);
    every bot still running at the end."""
    losses, lost = [], []
    for user, stamped in sr.bot_lines.items():
        for t, line in stamped:
            m = rigsoakload.SESSION_LOST.search(line)
            if m:
                losses.append((t, user, m.group(1)))
        s = rigsoakload.bot_lines_summary([line for _, line in stamped])
        out.append(f"bot {user}: {s}")
        if s["gave_up"]:
            lost.append(f"{user} x{s['gave_up']}")
        rep.notes.append(f"   {user}: {s['migrations']} migrations, {len(s['losses'])} relogin(s), {s['retries']} re-login retr(ies)"
                         + (f", {s['legs']} patrol legs ({s['missed']} missed)" if s["legs"] else ""))
    kills = [t for t, _ in sr.kills]
    odd = rigsoakload.unexplained_relogins(losses, kills, RELOGIN_WINDOW_S)
    kicked = [(u, why) for _, u, why in losses if "KICK" in why.upper()]
    rep.check(not odd and not kicked, f"{len(losses)} session(s) lost, each within {RELOGIN_WINDOW_S:g} s of a child kill, none kicked"
              + (f" (not after a kill: {[(time.strftime('%H:%M:%S', time.localtime(t)), u, w) for t, u, w in odd[:3]]})" if odd else "")
              + (f" (kicked: {kicked[:2]})" if kicked else ""))
    rep.check(not lost, "no bot's re-login gave up (a bot left without a character)" + (f" ({', '.join(lost)})" if lost else ""))
    alive = {role[4:] for _, role, _, _ in (sr.samples[-1][1] if sr.samples else []) if role.startswith("bot:")}
    gone = sorted(set(sr.bot_logs) - alive)
    rep.check(not gone, "every bot still running at the end" + (f" (gone: {', '.join(gone)})" if gone else ""))


def grade_layout(rep, rig, start, day, sr):
    """Every test-map cell's apply lines, pair by pair: no rectangle with two owners, the done operations' pairs owned
    as riglayout derives them, every base cell back on its own rectangle; C2 a Static map."""
    zones = [(z, m) for z, m in rig.graded_zones(day, start) if m == rig.MAP]
    cell_lines = {z: rig.zone_lines(z, start, day, m, suffixes=("info",)) for z, m in zones}
    by_pair = rigsoak.applies_by_pair(cell_lines)
    both = rigsoak.two_owners(by_pair)
    rep.check(not both, f"no rectangle with two owners at any of {len(by_pair)} applied layout(s)"
              + (f" ({[(p, a, b, rigsplit.format_rect(ra), rigsplit.format_rect(rb)) for p, a, b, ra, rb in both[:2]]})" if both else ""))
    lops, skipped = rigsoak.layout_ops(rigsoak.operations(op_lines(rig, start, day)))
    try:
        expected = rigsoak.expected_by_pair(riglayout.TEST_MAP_RECTS, lops)
    except ValueError as ex:
        rep.check(False, f"the done operations make no layout sequence riglayout can derive ({ex})")
        return
    bad, underived = rigsoak.mismatches(by_pair, expected)
    rep.check(not bad and not skipped, f"every apply at the {len(expected)} pair(s) of {len(lops)} done operation(s) owns what "
              f"riglayout derives" + (f" ({[(p, c, rigsplit.format_rect(r), w if isinstance(w, str) else rigsplit.format_rect(w)) for p, c, r, w in bad[:2]]})" if bad else "")
              + (f" (requests that did not parse: {skipped[:3]})" if skipped else ""))
    rep.notes.append(f"   layouts: {len(by_pair)} pairs applied, {len(expected)} derived, {underived} apply line(s) at pairs "
                     "no done operation names (aborts, settles, restarts)")
    final = riglayout.final_owned(riglayout.TEST_MAP_RECTS, lops)
    rep.check(final == riglayout.TEST_MAP_RECTS, f"the done operations end on the base cut ({sorted(set(final) - set(riglayout.TEST_MAP_RECTS))} left)")
    off = []
    for cell, rect in riglayout.TEST_MAP_RECTS.items():
        applied = [a for line in cell_lines.get(cell, []) for a in [rigsplit.parse_apply(line)] if a]
        if applied and not rigsplit.same_rect(applied[-1]["owned"], rect):
            off.append(f"{cell} owns {rigsplit.format_rect(applied[-1]['owned'])}")
    rep.check(not off, "every base cell's last apply owns its own rectangle" + (f" ({off[:3]})" if off else ""))
    info = rig.zone_lines("C2", start, day, rig.MAP2, suffixes=("info",))
    problems = rigtools.layout_apply_violations("C2", info, False)
    rep.check(not problems, "zone C2: Static: no own-cell layout" + (f" ({'; '.join(problems[:2])})" if problems else ""))

