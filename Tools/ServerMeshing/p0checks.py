"""Phase 0 run checks (step 4): whether the cells of one rig run logged what its plan asked for, as run problems.
Pure: every input is an argument. Accept-a's own acceptance checks are p0accept's.

    p0checks.run_problems(spec, run)          # every check of the spec's family (or its checks_family); [] = usable
    p0checks.exclusion(spec, run)             # None, or why a usable run is still left out of fits (saturation)
    p0checks.check_rates(spec, run)           # one check alone; FAMILY_CHECKS lists them all

Stage 0b families (step 8a) add the phantom, V move, monster and prewarm checks over each read cell's
p0cells0b.cell_record_0b (run["cells"][cell]["p0b"]); step 8b's add p0checks8b.CHECKS_8B (bots on the cell, b9's ACKs),
and a spec's coverage_exempt cells (b9's asleep neighbour, started only when woken) skip the coverage checks; step 9's
families and packets' Net stats add p0checks9.CHECKS_9 and check_net_stats.

A run is {"since", "start", "end", "cells": {cell: {"structural": rigrun.cell_window(...), "p0":
p0cells.cell_record(...)}}}; a spec is a p0plans RunSpec (layout None: the ten-cell rig as regress starts it). A run is
accepted when it has no problems and exclusion gives None: one saturated window on a cell is dropped from that cell's
figures, never a problem; a saturated span or two saturated windows exclude the run (the step 4 contract's Decision
18). Uncapped cells never count as saturated.
Unit tests: python -m unittest discover -s Tools/ServerMeshing -p "test_*.py"
"""
import math

import p0base
import p0cells
import p0cells0b
import p0checks8b
import p0checks9
import p0phantom
import p0results
import p0tools
import rigrun

TICK_HZ = 20
COVERAGE = 0.8                  # share of the measurement the windows of a line family must cover
AOI_PASSES = (4, 6)             # AOI passes per 5 s window with the timed AOI
PUPPET_SHARE = 0.03             # puppet counts within 3 % of the plan's
FRAMES_SHARE = 0.02             # frames/s within 2 % of the cap
PHYSICS_SLACK = 1.0             # physics steps/s within 1 of 1/step
PUPPETS_WINDOW_S = 10.0         # the Puppets window rides on the Mesh stats line, every 10 s
MIN_PUPPETS_WINDOWS = 4
V_SHARE = 0.95                  # a run's V move mean must reach this share of the V its spec expects (decisions 7, 8)
COVERAGE_TEXTS = ("'s work windows cover", " logged no work window")   # rigrun.run_problems' coverage problems


def _env(spec, key):
    return (spec["env"].get(key) or "").strip()


def _on(spec, key):
    return _env(spec, key) == "1"


def clock(stamp):
    """A timestamp's time of day, HH:MM:SS: how the checks name a window."""
    return stamp.strftime("%H:%M:%S")


def _records(spec, run):
    """[(cell, cell_record)] for the read cells that have one, in cells_read order (check_structure names the rest)."""
    cells = run.get("cells") or {}
    return [(cid, cells[cid]["p0"]) for cid in spec["cells_read"] if (cells.get(cid) or {}).get("p0") is not None]


def _unsaturated(record):
    return [t for t in record["triples"] if t["end"] not in record["saturated"]]


def _stated(spec):
    fps, nominal, step, error = p0tools.rates_setting(spec["rates"])
    if error:
        raise ValueError(f"spec rates {spec['rates']!r}: MESH_RATES {error}")
    return fps, nominal, step


def _rects(spec):
    """{cell: rect} of the spec's layout; the ten-cell rig (layout None) has the registry grid, and C2 no rect."""
    layout = spec["layout"]
    if layout is None:
        return dict(rigrun.LAYOUTS["grid"])
    if layout not in rigrun.LAYOUTS:
        raise ValueError(f"layout {layout!r} is not one of {', '.join(rigrun.LAYOUTS)} (or None for the ten-cell rig)")
    return dict(rigrun.LAYOUTS[layout])


def check_structure(spec, run):
    """rigrun.run_problems over the read cells (links among them, started once with the layout's rectangle, spawned
    the plan, no exceptions, 80 % Work window coverage, probe not Invalid; uncapped allowed when the spec has uncapped
    cells), a read cell with no record, and, with expect["links"], a read cell linked with another number of
    neighbours."""
    rects, cells = _rects(spec), run.get("cells") or {}
    missing = [cid for cid in spec["cells_read"] if (cells.get(cid) or {}).get("structural") is None]
    problems = [f"{cid} was not read" for cid in missing]
    read = [(cid, rects.get(cid)) for cid in spec["cells_read"] if cid not in missing]
    results = {cid: cells[cid]["structural"] for cid, _ in read}
    exempt = tuple(f"{cid}{text}" for cid in spec.get("coverage_exempt") or () for text in COVERAGE_TEXTS)
    problems += [text for text in rigrun.run_problems(read, spec["plan"], results, spec["measure"],
                                                      allow_uncapped=bool(spec["uncapped"])) if not text.startswith(exempt)]
    want = spec["expect"].get("links")
    if want is not None:
        for cid, _ in read:
            linked = sorted(results[cid]["linked"])
            if len(linked) != want:
                problems.append(f"{cid} linked with {len(linked)} neighbour(s) ({', '.join(linked) or 'none'}), "
                                f"expected {want}")
    return problems


def check_pairing(spec, run):
    """Each read cell's unpaired Work window/ticks/bins lines inside the measurement, and with MESH_WORK_BINS=1 every
    Work ticks line without its Work bins line or contradicted by it (p0tools.bins_problems)."""
    problems = []
    for cid, record in _records(spec, run):
        problems += [f"{cid}: {text}" for text in record["pairing"]]
        if _on(spec, "MESH_WORK_BINS"):
            problems += [f"{cid}: {text}" for t in record["triples"] for text in p0tools.bins_problems(t)]
    return problems


def _puppet_cells(spec):
    """The cells holding puppets: expect["puppets"]' cells when given, else every read cell of a multi-cell run."""
    if "puppets" in spec["expect"]:
        return set(spec["expect"]["puppets"])
    return set(spec["cells_read"]) if len(spec["cells_started"]) > 1 else set()


def check_coverage(spec, run):
    """Each read cell's Work ticks windows cover at least 80 % of the measurement, so do its Threads windows with
    MESH_THREAD_CPU=1, and with kinematic puppets each puppet cell logged 4 Puppets windows (3 when a measurement
    under 50 s cannot hold 4 whole 10 s windows)."""
    measure, problems = spec["measure"], []
    need = f"need {COVERAGE * measure:.0f}"
    puppet_cells = _puppet_cells(spec) if _env(spec, "MESH_PUPPET_MOTION") == "kinematic" else set()
    want_puppets = max(1, min(MIN_PUPPETS_WINDOWS, int((measure + 1) // PUPPETS_WINDOW_S) - 1))
    for cid, record in _records(spec, run):
        if cid in (spec.get("coverage_exempt") or ()):
            continue
        seconds = math.fsum(t["seconds"] for t in record["triples"])
        if not p0base.covered(seconds, measure, COVERAGE):
            problems.append(f"{cid}'s Work ticks windows cover {seconds:.0f} s of the {measure} s measurement, {need}")
        if _on(spec, "MESH_THREAD_CPU"):
            seconds = record["threads"]["seconds"] if record["threads"] else 0.0
            if not p0base.covered(seconds, measure, COVERAGE):
                problems.append(f"{cid}'s Threads windows cover {seconds:.0f} s of the {measure} s measurement, {need}")
        if cid in puppet_cells:
            count = len(record["puppets"]["windows"]) if record["puppets"] else 0
            if count < want_puppets:
                problems.append(f"{cid} logged {count} Puppets window(s) in the measurement, need {want_puppets}")
    return problems


def rates_text(line):
    """A Rates line's setting as MESH_RATES writes it: "60/0.03", or "u60/0.03" without a cap."""
    fps = f"u{line['nominal'] or ''}" if line["fps"] == p0tools.UNCAPPED else str(line["fps"])
    return f"{fps}/{line['step']:g}"


def check_rates(spec, run):
    """With spec rates None, no Rates line on any read cell (ServerMeshRates installs only with MESH_RATES or
    MESH_UNCAPPED_CELLS); otherwise exactly one per read cell, stating the spec's fps and step, set by MESH_RATES, tick
    20 Hz, and a 'Rates: cell X uncapped' line on exactly the spec's uncapped cells."""
    records, problems = _records(spec, run), []
    if spec["rates"] is None:
        return [f"{cid} logged {len(record['rates'])} Rates line(s) with no MESH_RATES set"
                for cid, record in records if record["rates"]]
    fps, nominal, step = _stated(spec)
    for cid, record in records:
        lines = record["rates"]
        if len(lines) != 1:
            problems.append(f"{cid} logged {len(lines)} Rates lines, expected one")
        else:
            line = lines[0]
            if line["fps"] != fps or (fps == p0tools.UNCAPPED and line["nominal"] != nominal) \
                    or abs(line["step"] - step) > 1e-9:
                problems.append(f"{cid}'s Rates line states {rates_text(line)}, not {spec['rates']}")
            if not line["from_env"]:
                problems.append(f"{cid}'s Rates line was not set by MESH_RATES")
            if line["tick_hz"] != TICK_HZ:
                problems.append(f"{cid}'s Rates line says tick {line['tick_hz']} Hz, not {TICK_HZ}")
        uncapped = any(line["cell"] == cid for line in record["rates_cells"])
        if cid in spec["uncapped"] and not uncapped:
            problems.append(f"{cid} is in MESH_UNCAPPED_CELLS but logged no 'Rates: cell {cid} uncapped' line")
        elif uncapped and cid not in spec["uncapped"]:
            problems.append(f"{cid} logged 'Rates: cell {cid} uncapped' but is not in MESH_UNCAPPED_CELLS")
    return problems


def check_instruments(spec, run):
    """With MESH_AOI_TIMING=1, each read cell logged 'AOI timing: installed' and 4 to 6 AOI passes in every window;
    no Tick burn line unless MESH_PROBE_BURN_TICK_MS is set."""
    low, high = AOI_PASSES
    problems = []
    for cid, record in _records(spec, run):
        if _on(spec, "MESH_AOI_TIMING"):
            if not record["markers"]["aoi_timing"]:
                problems.append(f"{cid} logged no 'AOI timing: installed' line with MESH_AOI_TIMING=1")
            off = [t for t in record["triples"] if not low <= t["ticks"]["aoi_passes"] <= high]
            # A saturation probe skips AOI passes by design; a prewarm burst stalls the frame loop for a pass or two in
            # the windows around it, which is what it measures (b8, 2026-10-05: 650 at once, one window at 3 passes).
            burst = spec["family"] == "prewarm" and len(off) <= PREWARM_AOI_WINDOWS
            if off and not spec.get("saturation_probe") and not burst:
                detail = ", ".join(f"{clock(t['end'])} {t['ticks']['aoi_passes']}" for t in off)
                problems.append(f"{cid}: AOI passes outside {low} to {high} in {len(off)} of {len(record['triples'])} "
                                f"windows ({detail})")
        if record["markers"]["tick_burn"] and not _env(spec, "MESH_PROBE_BURN_TICK_MS"):
            problems.append(f"{cid} logged a Tick burn line with no MESH_PROBE_BURN_TICK_MS set")
    return problems


def tick_misses(triples):
    """'10:00:05: 98 of 100 ticks, 2 dropped, cap 1' for each window off 20 ticks/s by more than 1 tick or holding a
    frame that ran the kit's tick cap (where the kit drops its backlog). The line's own "dropped" is only expected minus
    ticks, so a window one frame over 5 s (5.03 s: 100 of 101) reads "1 dropped" with nothing dropped: that is the ± 1."""
    misses = []
    for t in triples:
        want, wt = round(TICK_HZ * t["seconds"]), t["ticks"]
        cap = wt.get("cap", 0)
        if abs(wt["ticks"] - want) > 1 or cap:
            misses.append(f"{clock(t['end'])}: {wt['ticks']} of {want} ticks, {wt['dropped']} dropped, cap {cap}")
    return misses


PREWARM_AOI_WINDOWS = 2          # prewarm: windows around the burst allowed short of AOI passes
DROPPED_TICK_SHARE_0B = 0.25   # stage 0b: windows that only dropped ticks fail the run above this share (prewarm: never)


def check_ticks(spec, run):
    """Every unsaturated window of each read cell holds 100 ± 1 ticks (20 per second) and no tick-cap frame; a window
    stating another budget than the first (p0cells.aggregate_work_ticks' problems) is a problem too. Stage 0b runs
    load their cell near its limit, so there a window whose ticks run plus dropped make its count (98 + 2 of 100)
    but that dropped ticks is tolerated in up to
    DROPPED_TICK_SHARE_0B of the windows, and in a prewarm run always (the burst's dropped ticks are what it
    measures); a window off by more than one tick still fails every family."""
    problems = []
    for cid, record in _records(spec, run):
        windows = _unsaturated(record)
        misses = tick_misses(windows)
        if misses and spec["family"] in p0results.STAGED:
            counted = tick_misses([t for t in windows
                                   if abs(t["ticks"]["ticks"] + t["ticks"]["dropped"] - round(TICK_HZ * t["seconds"])) > 1])
            dropped_only = len(misses) - len(counted)
            if spec["family"] == "prewarm" or dropped_only <= DROPPED_TICK_SHARE_0B * len(windows):
                misses = counted
        if misses:
            problems.append(f"{cid}: {len(misses)} of {len(windows)} unsaturated windows missed {TICK_HZ} ticks/s "
                            f"± 1 tick or hit the kit's tick cap ({'; '.join(misses)})")
        if record["ticks"] is not None:
            problems += [f"{cid}: {text}" for text in record["ticks"]["problems"]]
    return problems


def check_band(spec, run):
    """With a spec band, each read cell logged a MESH_BAND line setting exactly that band; without one, none."""
    band, problems = spec["band"], []
    for cid, record in _records(spec, run):
        lines = record["markers"]["band"]
        if band is None:
            if lines:
                problems.append(f"{cid} logged a MESH_BAND line ({lines[-1]['band']:g} m) with no band set")
        elif not lines:
            problems.append(f"{cid} logged no MESH_BAND line, expected {band:g} m")
        elif any(abs(line["band"] - band) > 1e-9 for line in lines):
            got = ", ".join(f"{line['band']:g}" for line in lines)
            problems.append(f"{cid}'s MESH_BAND line set {got} m, expected {band:g} m")
    return problems


def check_motion(spec, run):
    """On every read cell with planned players: the load generator's motion line states the arm's MESH_LOAD_MOTION
    and MESH_LOAD_FIGHT (and is absent for the defaults, steered with fight on); kinematic owners logged a Kinematic
    walk line with none left steered."""
    motion = (_env(spec, "MESH_LOAD_MOTION") or "steered").lower()
    fight = _env(spec, "MESH_LOAD_FIGHT") != "0"
    default, want = motion == "steered" and fight, f"motion {motion}, fight {'on' if fight else 'off'}"
    problems = []
    for cid, record in _records(spec, run):
        if not spec["plan"].get(cid):
            continue
        lines = record["markers"]["load_motion"]
        said = [f"motion {line['motion']}, fight {'on' if line['fight'] else 'off'}" for line in lines]
        if default and lines:
            problems.append(f"{cid} logged a load motion line ({said[-1]}) on a default arm")
        elif not default and not lines:
            problems.append(f"{cid} logged no load motion line, expected {want}")
        elif not default and any(text != want for text in said):
            problems.append(f"{cid}'s load motion line says {'; '.join(said)}, expected {want}")
        if motion == "kinematic":
            walks = record["markers"]["kinematic_walk"]
            if not walks:
                problems.append(f"{cid} logged no Kinematic walk line with kinematic owners")
            elif any(walk["steered"] for walk in walks):
                steered = max(walk["steered"] for walk in walks)
                problems.append(f"{cid}: kinematic owners fell back to the brain ({steered} stay steered)")
    return problems


def check_animation(spec, run):
    """With MESH_RIG_SERVER_ANIMATION set, each read cell logged the Server animation line with that value. Raises
    ValueError for a value that is not 0 or 1."""
    value = _env(spec, "MESH_RIG_SERVER_ANIMATION")
    if not value:
        return []
    if value not in ("0", "1"):
        raise ValueError(f"MESH_RIG_SERVER_ANIMATION {value!r} is not 0 or 1")
    problems = []
    for cid, record in _records(spec, run):
        lines = record["markers"]["server_animation"]
        if not lines:
            problems.append(f"{cid} logged no Server animation line with MESH_RIG_SERVER_ANIMATION={value}")
        elif any(line["value"] != int(value) for line in lines):
            got = ", ".join(str(line["value"]) for line in lines)
            problems.append(f"{cid}'s Server animation line says {got}, expected {value}")
    return problems


def _near(value, want, share):
    return p0base.at_most(abs(value - want), share * want)


def check_puppets(spec, run):
    """With MESH_PUPPET_MOTION=kinematic, on each cell of expect["puppets"]: Puppets windows in the measurement, no
    fallback to steering, no steered puppet, and a mean kinematic count within 3 % of the expected puppets."""
    if _env(spec, "MESH_PUPPET_MOTION") != "kinematic":
        return []
    records, problems = dict(_records(spec, run)), []
    for cid, want in (spec["expect"].get("puppets") or {}).items():
        if cid not in records:
            continue
        puppets = records[cid]["puppets"]
        if puppets is None:
            problems.append(f"{cid} logged no Puppets window in the measurement")
            continue
        fallbacks = [f"{reason} {puppets[f'fallback_{reason}']}" for reason in p0cells.FALLBACKS
                     if puppets[f"fallback_{reason}"]]
        if fallbacks:
            problems.append(f"{cid}: puppets fell back to steering ({', '.join(fallbacks)})")
        if puppets["steered"]:
            problems.append(f"{cid}: {puppets['steered']:.1f} steered puppets on average, expected 0")
        if not _near(puppets["kinematic"], want, PUPPET_SHARE):
            problems.append(f"{cid}: {puppets['kinematic']:.1f} kinematic puppets on average, expected {want} ± 3 %")
    return problems


def check_puppet_count(spec, run):
    """On each cell of expect["puppets"], the mean Work window puppet players lies within 3 % of the expected."""
    records, problems = dict(_records(spec, run)), []
    for cid, want in (spec["expect"].get("puppets") or {}).items():
        ticks = records[cid]["ticks"] if cid in records else None
        if ticks is not None and not _near(ticks["puppet_players"], want, PUPPET_SHARE):
            problems.append(f"{cid}: {ticks['puppet_players']:.1f} puppet players on average, expected {want} ± 3 %")
    return problems


def check_frames(spec, run):
    """With spec rates, each capped read cell ran within 2 % of the frame cap, and every read cell within 1 of the
    physics steps per second the step sets."""
    if spec["rates"] is None:
        return []
    fps, _, step = _stated(spec)
    problems = []
    for cid, record in _records(spec, run):
        ticks = record["ticks"]
        if ticks is None:
            continue
        capped = fps != p0tools.UNCAPPED and cid not in spec["uncapped"]
        if capped and not _near(ticks["frames_per_s"], fps, FRAMES_SHARE):
            problems.append(f"{cid}: {ticks['frames_per_s']:.2f} frames/s, expected {fps} ± 2 %")
        if not p0base.at_most(abs(ticks["physics_per_s"] - 1.0 / step), PHYSICS_SLACK):
            problems.append(f"{cid}: {ticks['physics_per_s']:.2f} physics steps/s, expected {1.0 / step:.1f} ± 1")
    return problems


def _records_0b(spec, run):
    """[(cell, cell_record_0b)] for the read cells that have one, in cells_read order."""
    cells = run.get("cells") or {}
    return [(cid, cells[cid]["p0b"]) for cid in spec["cells_read"] if (cells.get(cid) or {}).get("p0b") is not None]


def check_phantoms(spec, run):
    """With MESH_PHANTOM set, each read cell it binds on (its cell list, or every read cell for all) logged Phantom
    windows in the measurement, every one with the planned phantoms bound: bound virtual (bound sink in sink mode)
    equal to P, or to the step's P on a stepped run's row. Raises ValueError for a MESH_PHANTOM that does not parse."""
    mode, count, _, cells, error = p0phantom.phantom_setting(spec["env"].get("MESH_PHANTOM"))
    if error:
        raise ValueError(f"MESH_PHANTOM: {error}")
    if mode is None:
        return []
    want = (spec.get("point") or {}).get("step") or count
    field, named = f"bound_{mode}", None if cells == "all" else {cell.lower() for cell in cells}
    problems = []
    for cid, record in _records_0b(spec, run):
        if named is not None and cid.lower() not in named:
            continue
        windows = record["phantom"]
        off = [w for w in windows if w[field] != want]
        if not windows:
            problems.append(f"{cid} logged no Phantom window in the measurement with MESH_PHANTOM set")
        elif off:
            detail = ", ".join(f"{clock(w['end'])} {w[field]}" for w in off)
            problems.append(f"{cid}: {len(off)} of {len(windows)} Phantom windows had other than {want} {mode} "
                            f"phantoms bound ({detail})")
    return problems


def check_v_move(spec, run):
    """With spec["expected_v"], the V move mean over the read cells' Phantom windows (p0cells0b.v_move: weighted by
    each window's samples) is at least 0.95 x expected_v (the targets and fan-out acceptance, decisions 7 and 8)."""
    want = spec.get("expected_v")
    if want is None:
        return []
    mean, _ = p0cells0b.v_move({"phantom": [w for _, record in _records_0b(spec, run) for w in record["phantom"]]})
    need = V_SHARE * want
    if mean is None:
        return [f"no Phantom window sampled V move, expected a mean of at least {need:g} ({V_SHARE:g} x {want:g})"]
    if not p0base.at_least(mean, need):
        return [f"V move mean {mean:.1f} is below {need:g} ({V_SHARE:g} x the expected {want:g})"]
    return []


def check_monsters(spec, run):
    """With spec["placed"] above 0 (MESH_MONSTER_PLACE runs), the read cells logged Monsters windows in the
    measurement whose placed (each cell's last window, summed over the cells) is spec["placed"], and every window has
    as many alive as placed."""
    want = spec.get("placed")
    if not want:
        return []
    found = [(cid, record["monsters"]) for cid, record in _records_0b(spec, run) if record["monsters"]]
    if not found:
        return [f"no Monsters window in the measurement, expected {want} placed monsters"]
    placed = sum(windows[-1]["placed"] for _, windows in found)
    per_cell = ", ".join(f"{cid} {windows[-1]['placed']}" for cid, windows in found)
    problems = [] if placed == want else [f"Monsters windows place {placed} monsters ({per_cell}), expected {want}"]
    for cid, windows in found:
        short = [w for w in windows if w["alive"] != w["placed"]]
        if short:
            detail = ", ".join(f"{clock(w['end'])} {w['alive']} of {w['placed']}" for w in short)
            problems.append(f"{cid}: {len(short)} of {len(windows)} Monsters windows had other than every placed "
                            f"monster alive ({detail})")
    return problems


def _rate(value):
    return "no window" if value is None else f"{value:.1f}/s"


def check_prewarm(spec, run):
    """A Prewarm burst line stamped inside the measurement on a read cell with n equal to spec["N"] (decision 11), and
    with spec["eyes"] a rise in that cell's Send spawn-type calls (t3, SyncBaseLine) within 2 s of the burst
    (p0cells0b.spawn_rise). Raises ValueError for a spec without N."""
    if spec.get("N") is None:
        raise ValueError("a prewarm spec needs N, the burst's size (step 8a decision 11)")
    want, records = spec["N"], _records_0b(spec, run)
    bursts = [(cid, burst) for cid, record in records for burst in record["prewarm"]]
    matched = [(cid, burst) for cid, burst in bursts if abs(burst["n"] - want) <= p0base.EPS * max(1.0, abs(want))]
    if not matched:
        said = ", ".join(f"{cid} n {burst['n']:g}" for cid, burst in bursts) or "none"
        return [f"no Prewarm burst with n {want:g} inside the measurement (bursts: {said})"]
    if not spec.get("eyes"):
        return []
    cid, burst = matched[0]
    before, after = p0cells0b.spawn_rise(dict(records)[cid]["send"], burst["end"])
    if before is None or after is None or not p0base.above(after, before):
        return [f"{cid}: Send t{p0cells0b.SPAWN_TYPE} (spawn) calls did not rise within 2 s of the Prewarm burst at "
                f"{clock(burst['end'])} ({_rate(before)} before, {_rate(after)} after)"]
    return []


COMMON = (check_structure, check_pairing, check_coverage, check_rates, check_instruments, check_ticks, check_band,
          check_motion, check_animation)
STAGE_0B = (check_phantoms, check_v_move)
FAMILY_CHECKS = {"canary": COMMON, "accept": COMMON, "density": COMMON, "floor": COMMON,
                 "rates": COMMON + (check_frames,),
                 "presence": COMMON + (check_puppets, check_puppet_count),
                 "abgeo": tuple(check for check in COMMON if check is not check_motion) + (check_puppets,),
                 "observed": COMMON + STAGE_0B + (check_puppets, check_puppet_count, check_monsters),
                 "fanout": COMMON + STAGE_0B, "targets": COMMON + STAGE_0B + (check_monsters,),
                 "syncrpc": COMMON + STAGE_0B, "packets": COMMON + STAGE_0B + (p0checks9.check_net_stats,),
                 "prewarm": COMMON + STAGE_0B + (check_prewarm,),
                 **{family: COMMON + STAGE_0B + p0checks8b.CHECKS_8B[family] for family in p0results.FAMILIES_8B},
                 **{family: COMMON + STAGE_0B + p0checks9.CHECKS_9[family] for family in p0results.FAMILIES_0C}}


def run_problems(spec, run):
    """Every problem the checks of the spec's family find, in FAMILY_CHECKS order ([] when the run is usable); a spec's
    checks_family (accept-a part E) picks another family's checks. Raises ValueError for a family without checks."""
    family = spec.get("checks_family") or spec["family"]
    if family not in FAMILY_CHECKS:
        raise ValueError(f"no checks for family {family!r}; known: {', '.join(FAMILY_CHECKS)}")
    problems = []
    for check in FAMILY_CHECKS[family]:
        problems += check(spec, run)
    return problems


def exclusion(spec, run):
    """None, or why the run is left out of fits and ratio verdicts: on some capped read cell, its selected windows
    are saturated as a span ('saturated (span) on C') or 2 or more of them are ('saturated in 3 of 12 windows on C').
    Reasons of several cells are joined with '; '. A single saturated window is not a reason: p0cells drops it. A
    saturation probe (spec["saturation_probe"], packets' saturation arm) is never excluded: saturating is its point."""
    if spec.get("saturation_probe"):
        return None
    reasons = []
    for cid, record in _records(spec, run):
        if cid in spec["uncapped"]:
            continue
        if p0cells.span_saturated([t["window"] for t in record["triples"]]):
            reasons.append(f"saturated (span) on {cid}")
        elif len(record["saturated"]) >= 2:
            reasons.append(f"saturated in {len(record['saturated'])} of {len(record['triples'])} windows on {cid}")
    return "; ".join(reasons) or None
