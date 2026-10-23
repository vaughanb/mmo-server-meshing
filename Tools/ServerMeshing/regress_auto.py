"""Slice 3's automatic-policy scenarios for regress.py (Docs/ServerMeshing/slices/slice3_design.md decisions 17-19):

    python Tools/ServerMeshing/regress.py autosplit autobenefit autorefuse autosplit_links

autosplit: the test map Dynamic with automatic split and merge on; eight bots stand 88 m either side of C's centre
  line; a switchable burn makes C hot, the central splits it by itself (x 0, child C.1), measures the relief once the
  burn is eased, and merges C.1 back once the bots are gone and the burn is off.
autobenefit: the 600 m Coarse cut of the test map, 200 spread synthetic players and no client; the central splits C
  by itself and its measured relief is held to its prediction (the plan's +-15 %).
autorefuse: the same with a 60 m crowd on the centre, steered puppets and the steered fit: the planner refuses the
  cut and says so, once a minute, and nothing splits.
autosplit_links (DESIGN 0.88): autosplit with every map's links on demand (MESH_LINKS=all:OnDemand, Slice 7).

regress.py hands itself in as `rig` (request_play, launch_bot, zone_lines, kill_bots, TEMP, ...), so this module never
imports it; the unit tests hand in a fake. The readers are rigauto's.
"""
import collections
import os
import time

import provision
import regress_merge
import regress_split
import rigauto
import rigmerge
import rigsplit
import rigtools

SCENARIOS = ("autosplit", "autobenefit", "autorefuse", "autosplit_links")
PERSIST_REASON = regress_split.PERSIST_REASON

BURN_FILE_NAME = "ServerMesh.ProbeBurn.txt"
BURN_HOT = "C:14"        # util about 0.9 at the 60 fps budget (C idle with eight bots is about 1 ms)
BURN_WARM = "C:7"        # util about 0.46: not hot at 0.6, a measured relief near 50 %, the pair not cool (decision 17)
STAND_X = 88.0           # both groups beyond the band (80 m) from the cut at x = 0
ACROSS = regress_split.ACROSS
OBSERVE_S = regress_split.OBSERVE_S
STAND_TIMEOUT_S = regress_split.STAND_TIMEOUT_S
SPLIT_TIMEOUT_S = 240    # the burn write to the split's done (dwell 20 s, then spawn, pre-warm, drain, three broadcasts)
MEASURED_TIMEOUT_S = 60
MERGE_TIMEOUT_S = 240    # the burn off to the merge's done (cooldown, dwell 30 s, the merge)
RESULT_SLACK_S = regress_split.RESULT_SLACK_S
BENEFIT_TIMEOUT_S = 420  # the Coarse Play to the split's done (the synthetics spawn, the probe proves itself, dwell 20 s)
REFUSAL_TIMEOUT_S = 240  # the Coarse Play to the first refusal
REFUSE_WINDOW_S = 90     # after the first refusal: no split, refusals once a minute
REFUSE_GAP_S = 55
RELIEF_MIN_PCT = 30
BENEFIT_PLAYERS = 200
# The spread load stays inside C's inner 200 m square (MESH_LOAD_AREA=0,0,100: a square of half-size 100), which 2b's
# split proved walkable: a steered synthetic placed where the scene has no navmesh is snapped by the kit, and a
# snapped one can land back across the cut on every hand-over (2026-10-18: four of 200 over the whole ground did).
BENEFIT_AREA = "0,0,100"
REFUSE_AREA = "0,0,30"
BENEFIT_TOLERANCE = 0.15
BENEFIT_RELIEF = 0.75
ADOPTED_MIN = 80
UTIL_NOTE_TOLERANCE = 0.05
MEASURE_AFTER_S = 10
COARSE_RECT = (-300.0, -300.0, 600.0, 600.0)
PARK = (-290.0, 290.0)

COMMON_ENV = {"MESH_AUTO_OPS": "split,merge", "MESH_SPLIT_DWELL": "20", "MESH_MONSTER_WEIGHT": "0", "MESH_NO_MONSTERS": "1"}
AUTOSPLIT_ENV = dict(COMMON_ENV, MESH_SPLIT_UTIL="0.6", MESH_MERGE_UTIL="0.5", MESH_MERGE_AFTER="30",
                     MESH_OP_COOLDOWN="30", MESH_SPLIT_BENEFIT="1")
COARSE_ENV = {"MESH_LAYOUT_OVERRIDE": None, "MESH_AUTO_OPS": "split", "MESH_SPLIT_UTIL": "0.3", "MESH_SPLIT_DWELL": "20",
              "MESH_OP_COOLDOWN": "600", "MESH_NO_MONSTERS": "1", "MESH_LOAD_PLAN": f"C:{BENEFIT_PLAYERS}",
              "MESH_LOAD_SEED": "7", "MESH_LOAD_AREA": BENEFIT_AREA}
LINKS_ENV = {"MESH_LINKS": "all:OnDemand"}   # autosplit_links
REFUSE_ENV = {"MESH_LOAD_AREA": REFUSE_AREA, "MESH_PUPPET_MOTION": "steered", "MESH_PLANNER_WEIGHTS": "1,0.126,0.2,9.1"}
ALLOWED = {
    "autosplit": ("split of C planned", "split split-1 measured", "merge of C C.1 planned", "merge merge-1 measured"),
    "autobenefit": ("split of C planned", "split split-1 measured"),
    "autorefuse": ("split of C refused: best peak",),
    "autosplit_links": ("split of C planned", "split split-1 measured", "merge of C C.1 planned", "merge merge-1 measured"),
}
EXPECTED_ZONES = {"autobenefit": {"C", "C.1", "C2"}, "autorefuse": {"C", "C2"},
                  "autosplit_links": {"SW", "S", "SE", "W", "C", "E", "NW", "N", "NE", "C2", "C.1"}}
# What run() leaves for grade_layout: the rigsplit.Split the policy performed (None when it never planned one) and the
# statuses of its split and merge.
AutoRun = collections.namedtuple("AutoRun", "name split split_status merge_status")


def burn_path(rig):
    return os.path.join(rig.TEMP, BURN_FILE_NAME)


def write_burn(path, text, rig=None):
    """Writes the burn file whole (a temporary file renamed over it): a cell reading between a truncate and a write
    would see an empty file and switch its burn off for a window. With `rig`, the other machine's copy too (DESIGN
    0.90: its cells read the same name under their own Temp, rig.mirror_temp)."""
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        f.write(text)
    os.replace(tmp, path)
    if rig is not None:
        rig.mirror_temp(os.path.basename(path), text)


def status_lines(rig):
    return regress_split.read_lines(rig.LAYOUT_COMMAND_STATUS)


def wait_lines(rig, done, timeout):
    """The status file's lines once `done(lines)` holds, or as they stand at the timeout."""
    end = time.time() + timeout
    while True:
        lines = status_lines(rig)
        if done(lines) or time.time() >= end:
            return lines
        time.sleep(2)


def stand_plan():
    """meshbot2..5 at x = +88 (the child's side), meshbot6..9 at x = -88, each group spread over ACROSS."""
    far = [(STAND_X, z) for z in ACROSS]
    near = [(-STAND_X, z) for z in ACROSS]
    return [regress_split.Bot(f"meshbot{2 + i}", f"meshbot{2 + i}Char", p, "+" if i < len(far) else "-", False)
            for i, p in enumerate(far + near)]


def run(name, rep, rig, start, day, bots):
    if name in ("autosplit", "autosplit_links"):
        return flow_autosplit(rep, rig, start, day, bots, name)
    if name in ("autobenefit", "autorefuse"):
        return flow_coarse(name, rep, rig, start, day)
    raise ValueError(f"unknown scenario {name}")


def grade_policy_lines(rep, name, lines):
    """Decision 17/20: only the named policy lines, no refused request, no supervision."""
    texts = rigauto.policy_texts(lines)
    extra = rigauto.disallowed_policy(texts, ALLOWED[name])
    rep.check(not extra, f"policy lines only {', '.join(ALLOWED[name])}" + (f" ({extra[0][:100]})" if extra else ""))
    refused = rigauto.refusals(lines)
    rep.check(not refused, "no request of the policy refused by the coordinator" + (f" ({refused[0][:100]})" if refused else ""))
    sup = rigauto.supervisor_texts(lines)
    rep.check(not sup, "no supervision line" + (f" ({sup[0][:100]})" if sup else ""))


# ------------------------------------------------------------------ autosplit (decision 17)
def flow_autosplit(rep, rig, start, day, bots, name="autosplit"):
    spec = regress_split.SPECS["split"]
    split = regress_split.split_of("split", rig.MAP)
    plan = stand_plan()
    rig.db_reset(spec.tester, spec.tester)
    provision.reset_bot_positions(2, regress_split.STAND_BOTS, [b.point for b in plan], status_path=rig.STATUS)
    burn = burn_path(rig)
    write_burn(burn, "", rig)
    env = dict(AUTOSPLIT_ENV, MESH_CELL_MODE=f"{rig.MAP}:Dynamic", MESH_PROBE_BURN_MS="@" + burn,
               **(LINKS_ENV if name == "autosplit_links" else {}))
    rig.request_play("stand", spec.tester[0], spec.tester[1], env=env, enabled=False)
    rig.wait_play(60)
    time.sleep(20)
    logs = {}
    for b in plan:
        args = ["-meshAutoMode", "stand", "-meshWalkX", f"{b.point[0]:g}", "-meshWalkZ", f"{b.point[1]:g}"]
        logs[b.user] = rig.launch_bot(name, b.user, args, channel=spec.channel, observe_seconds=OBSERVE_S, line_x=spec.at)
        bots.append(("stand", logs[b.user]))
    standing = regress_split.wait_standing(list(logs.values()), STAND_TIMEOUT_S)
    for b in plan:
        at = standing[logs[b.user]]
        rep.check(at is not None and rigsplit.side_of(at, regress_split.AXIS, spec.at) == b.side,
                  f"{b.user} stood on the {'child' if b.side == '+' else 'kept'} side (at {at}, planned {b.point})")
    if not all(standing.values()):
        rep.check(False, "autosplit: no burn, a bot never logged 'Standing at'")
        return AutoRun(name, split, None, None)
    before = status_lines(rig)
    rep.check(rigauto.split_count(before) == 0 and not any(t.startswith("split of") for t in rigauto.policy_texts(before)),
              "nothing split or planned before the burn (idle C is not hot)")

    write_burn(burn, BURN_HOT, rig)
    lines = wait_lines(rig, lambda ls: rigsplit.op_outcome(rigauto.split_status(ls)) is not None, SPLIT_TIMEOUT_S)
    split_status = rigauto.split_status(lines)
    write_burn(burn, BURN_WARM, rig)
    regress_split.grade_status(rep, split_status, spec)
    planned = rigauto.first(rigauto.policy_texts(lines), rigauto.parse_planned)
    rep.check(planned is not None and (planned["cell"], planned["axis"], planned["at"], planned["side"], planned["child"]) == ("C", "x", 0.0, "+", "C.1")
              and planned["util"] >= 0.6,
              f"the policy planned x 0 + child C.1 of a hot C ({planned or 'no planned line'})")
    lines = wait_lines(rig, lambda ls: rigauto.first(rigauto.policy_texts(ls), rigauto.parse_split_measured) is not None, MEASURED_TIMEOUT_S)
    measured = rigauto.first(rigauto.policy_texts(lines), rigauto.parse_split_measured)
    rep.check(measured is not None and measured["relief"] >= RELIEF_MIN_PCT,
              f"the split was measured with at least {RELIEF_MIN_PCT} % relief ({measured or 'no measured line'})")

    results = regress_split.wait_results(list(logs.values()), OBSERVE_S + RESULT_SLACK_S)
    rep.check(not rigauto.has_merge_line(status_lines(rig)), "no merge while the bots stood (the warm burn kept the pair from cooling)")
    rig.kill_bots()
    write_burn(burn, "", rig)
    lines = wait_lines(rig, lambda ls: rigsplit.op_outcome(rigauto.merge_status(ls)) is not None, MERGE_TIMEOUT_S)
    merge_status = rigauto.merge_status(lines)
    merge_planned = rigauto.first(rigauto.policy_texts(lines), rigauto.parse_merge_planned)
    rep.check(merge_planned is not None and {merge_planned["a"], merge_planned["b"]} == {"C", "C.1"},
              f"the policy planned the merge of C and C.1 once the bots were gone ({merge_planned or 'no merge line'})")
    rep.check(merge_status["requested"] in (("C", "C.1"), ("C.1", "C")) and not merge_status["collapse"],
              f"merge requested C C.1 ({merge_status['requested']})")
    rep.check(merge_status["done"] and not merge_status["aborted"] and not merge_status["frozen"],
              f"merge done (states: {', '.join(merge_status['states'][-4:]) or 'none'})")
    regress_split.grade_bots(rep, rig, plan, logs, results, spec, start, day)
    regress_merge.grade_exit(rep, rig, spec.child, start, day)
    regress_merge.grade_store(rep, rig, split)
    grade_policy_lines(rep, name, status_lines(rig))
    return AutoRun(name, split, split_status, merge_status)


# ------------------------------------------------------------------ autobenefit and autorefuse (decisions 18, 19)
def coarse_env(name, rig):
    env = dict(COARSE_ENV, MESH_LAYOUT_OVERRIDE=f"{rig.MAP}=C@{COARSE_RECT[0]:g},{COARSE_RECT[1]:g},{COARSE_RECT[2]:g},{COARSE_RECT[3]:g}",
               MESH_CELL_MODE=f"{rig.MAP}:Dynamic")
    if name == "autorefuse":
        env.update(REFUSE_ENV)
    return env


def flow_coarse(name, rep, rig, start, day):
    rig.db_reset(PARK, PARK)
    rig.request_play("none", 0, 0, env=coarse_env(name, rig), enabled=False)
    rig.wait_play(60)
    play_at = time.time()
    if name == "autorefuse":
        grade_refusal(rep, rig)
        return AutoRun(name, None, None, None)
    lines = wait_lines(rig, lambda ls: rigsplit.op_outcome(rigauto.split_status(ls)) is not None, BENEFIT_TIMEOUT_S)
    done_at = time.time()
    split_status = rigauto.split_status(lines)
    planned = rigauto.first(rigauto.policy_texts(lines), rigauto.parse_planned)
    rep.check(planned is not None and planned["cell"] == "C", f"the policy planned a split of C ({planned or 'no planned line'})")
    split = None
    if planned is not None:
        split = rigsplit.Split(rig.MAP, "C", COARSE_RECT, planned["axis"], planned["at"], planned["side"], planned["child"])
    rep.check(split_status["requested"] is not None and split_status["done"] and not split_status["aborted"] and not split_status["frozen"],
              f"split done (states: {', '.join(split_status['states'][-4:]) or 'none'})")
    lines = wait_lines(rig, lambda ls: rigauto.first(rigauto.policy_texts(ls), rigauto.parse_split_measured) is not None, MEASURED_TIMEOUT_S)
    measured = rigauto.first(rigauto.policy_texts(lines), rigauto.parse_split_measured)
    if planned is not None and measured is not None:
        p, m, u = measured["predicted"], measured["measured"], planned["util"]
        rep.check(abs(m - p) <= BENEFIT_TOLERANCE * p,
                  f"measured peak {m:g} within {BENEFIT_TOLERANCE:.0%} of the predicted {p:g} (util before {u:g})")
        rep.check(m <= BENEFIT_RELIEF * u, f"measured peak {m:g} at most {BENEFIT_RELIEF:g} x the util before ({u:g})")
        rep.notes.append(f"   benefit: util before {u:g}, predicted peak {p:g}, measured {m:g}, relief {measured['relief']} % "
                         f"(cut {planned['axis']} {planned['at']:g} {planned['side']}, {planned['tried']} cuts tried)")
    else:
        rep.check(False, f"the split was measured ({measured or 'no measured line'})")
    rep.check(rigauto.split_count(lines) == 1, f"exactly one split operation ({rigauto.split_count(lines)})")
    if split is not None:
        grade_synthetics(rep, rig, split, split_status, start, day)
        if measured is not None:
            note_utils(rep, rig, split, measured, done_at, start, day)
    grade_policy_lines(rep, name, status_lines(rig))
    rep.notes.append(f"   Coarse Play to the split's done: {done_at - play_at:.0f} s")
    return AutoRun(name, split, split_status, None)


def grade_synthetics(rep, rig, split, split_status, start, day):
    """Decision 18: every synthetic accounted for after e3 (the two cells' owned players sum to the plan for three
    consecutive paired windows) and at least ADOPTED_MIN adopted by the child."""
    base = rig.zone_lines(split.base, start, day, split.map_id, suffixes=("info",))
    child = rig.zone_lines(split.child, start, day, split.map_id, suffixes=("info",))
    e3 = rigmerge.after_applied(base, split_status["pairs"].get("e3"))
    after = rigtools.stamp_datetime(e3[0]) if e3 else None
    if after is None:
        rep.notes.append(f"   {split.base}: no e3 apply found; the synthetic count is read from the whole run")
        after = rigauto.local_datetime(0)
    pairs = rigauto.owned_pairs(base, child, after)
    rep.check(rigauto.consecutive(pairs, BENEFIT_PLAYERS),
              f"{split.base} and {split.child} own {BENEFIT_PLAYERS} synthetic players together for three paired windows "
              f"after e3 ({len(pairs)} pairs, last {pairs[-3:] if pairs else 'none'})")
    adopted = rigsplit.adoptions(child, split.base, split.child)
    rep.check(len(adopted) >= ADOPTED_MIN, f"{split.child} adopted {len(adopted)} synthetic player(s), at least {ADOPTED_MIN}")


def note_utils(rep, rig, split, measured, done_at, start, day):
    """An independent read of the measured line: each cell's first work window at least MEASURE_AFTER_S after done."""
    after = rigauto.local_datetime(done_at + MEASURE_AFTER_S)
    for cell in (split.base, split.child):
        lines = rig.zone_lines(cell, start, day, split.map_id, suffixes=("info",))
        util = rigauto.util_after(lines, after)
        claimed = measured["utils"].get(cell)
        if util is None or claimed is None:
            rep.notes.append(f"   {cell}: no work window after done + {MEASURE_AFTER_S} s to check the measured line against")
        elif abs(util - claimed) > UTIL_NOTE_TOLERANCE:
            rep.notes.append(f"   {cell}: work window util {util:.2f} against the measured line's {claimed:g} (over {UTIL_NOTE_TOLERANCE:g} apart)")


def grade_refusal(rep, rig):
    """Decision 19: the gate refuses the crowd's cut, says so once a minute, and nothing splits."""
    seen = []   # (wall time, text) as each refusal first appears

    def note(lines):
        texts = [t for t in rigauto.policy_texts(lines) if rigauto.parse_refusal(t)]
        for text in texts[len(seen):]:
            seen.append((time.time(), text))
        return bool(seen)

    wait_lines(rig, note, REFUSAL_TIMEOUT_S)
    if not seen:
        rep.check(False, "the policy refused the crowd's cut (no refusal line)")
        return
    first = rigauto.parse_refusal(seen[0][1])
    rep.check(first["peak"] is not None and first["peak"] > first["ratio"] * first["util"],
              f"refused by the gate: {seen[0][1]}")
    end = time.time() + REFUSE_WINDOW_S
    while time.time() < end:
        note(status_lines(rig))
        time.sleep(2)
    lines = status_lines(rig)
    rep.check(rigauto.split_count(lines) == 0, f"no split within {REFUSE_WINDOW_S} s of the first refusal")
    gaps = [b - a for (a, _), (b, _) in zip(seen, seen[1:])]
    rep.check(len(seen) >= 2 and all(g >= REFUSE_GAP_S for g in gaps),
              f"refusals once a minute ({len(seen)} seen, gaps {[round(g) for g in gaps]})")
    grade_policy_lines(rep, "autorefuse", lines)


# ------------------------------------------------------------------ the layout grade
def grade_layout(rep, rig, start, day, ar):
    """autosplit: the split's and the merge's layouts by rectangle, as regress_merge grades them (policy lines allowed
    in the status file); the Coarse scenarios: C and the child by rectangle, C2 as a Static map, no other zone."""
    if ar.name in ("autosplit", "autosplit_links"):
        split_pairs = (ar.split_status or {}).get("pairs", {})
        merge_pairs = (ar.merge_status or {}).get("pairs", {})
        regress_split.grade_layout(
            rep, rig, start, day, ar.split,
            applies=lambda zone, info: rigmerge.merge_apply_violations(zone, info, ar.split, split_pairs, merge_pairs),
            ops=lambda zone, lines: len(rigmerge.disallowed_op_lines(zone, lines, ar.split.child)),
            how_split="automatic split then merge: the layouts by rectangle", allow_policy=True)
        return
    if ar.name == "autorefuse":
        for zone, map_id in (("C", rig.MAP), ("C2", rig.MAP2)):
            info = rig.zone_lines(zone, start, day, map_id, suffixes=("info",))
            problems = rigtools.layout_apply_violations(zone, info, map_id == rig.MAP)
            rep.check(not problems, f"zone {zone}: {'Dynamic: layout applied before any player' if map_id == rig.MAP else 'Static: no own-cell layout'}"
                      + (f" ({'; '.join(problems[:2])})" if problems else ""))
        return
    if ar.split is None:
        rep.check(False, "layout: no split to grade")
        return
    for zone in (ar.split.base, ar.split.child):
        info = rig.zone_lines(zone, start, day, rig.MAP, suffixes=("info",))
        problems = rigsplit.split_apply_violations(zone, info, ar.split)
        everything = rig.zone_lines(zone, start, day, rig.MAP)
        n_ops = sum(1 for line in everything if rigsplit.SPLIT_OP_PROBLEM.search(line))
        if n_ops:
            problems.append(f"{n_ops} retire/merge/abort/freeze line(s)")
        rep.check(not problems, f"zone {zone}: Coarse split by rectangle" + (f" ({'; '.join(problems[:2])})" if problems else ""))
    info = rig.zone_lines("C2", start, day, rig.MAP2, suffixes=("info",))
    problems = rigtools.layout_apply_violations("C2", info, False)
    rep.check(not problems, "zone C2: Static: no own-cell layout" + (f" ({'; '.join(problems[:2])})" if problems else ""))
