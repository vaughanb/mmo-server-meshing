"""abload's pure half (Phase 0 step 4): the ab plan as RunSpecs, its run order, and every line the A/B prints from its
runs (run lines, the summary table, the verdicts). Nothing here drives the rig or reads a file: abload runs the plan
and prints these lines, so a saved report still replays through the unchanged abload.ab_command (abregrade).

    abgrade.ab_plan(args)                                  # an `abload ab` Namespace's RunSpecs, in run order
    abgrade.run_order(points, layouts, repeats, order)     # point-major (0.71) or repeat-major (the §2 rule)
    abgrade.summary_lines(points, layouts, repeats)        # the table: one row per point and layout
    abgrade.grade_points(points, layouts, area, repeats)   # the verdict section and its verdict groups
    abgrade.exit_code(points, groups)                      # abload ab's exit code

points maps each (players, monster setting, band) to {layout: {attempt: run_record}}; band is the --bands value, else
--band (0 = as configured). Pairs graded (cut/base): halves/coarse (the Slice 1 go/no-go), grid/coarse,
strips4/coarse and strips4/halves (its own verdict). A pair of runs is left out when either side's busiest cell is
saturated; the ratio is the cut's busiest cell over the base's, per repeat, judged by rigtools.point_verdict at 0.75.
Unit tests: python -m unittest discover -s Tools/ServerMeshing -p "test_*.py"
"""
import p0plans
import p0tools
import rigrun
import rigtools

GO_RATIO = 0.75          # a cut's busiest cell over its base's, spread load: the go/no-go threshold
PUPPETS = ("steered", "kinematic")
ORDERS = ("point-major", "repeat-major")
PAIRS = (("halves", "coarse"), ("grid", "coarse"), ("strips4", "coarse"), ("strips4", "halves"))
VERDICT_PAIRS = (("halves", "coarse"), ("strips4", "halves"))   # the pairs judged, with spread load only
DECIDED = ("GO", "NO-GO", "MARGINAL")
FAMILY, GAME = "abgeo", "fantasy"
NO_GO_NO_GO = "no go/no-go: it needs both coarse and halves, with spread load"


def listed(text):
    """The comma-separated items of an argument, stripped, empty items left out."""
    return [item.strip() for item in text.split(",") if item.strip()]


def load_area(args):
    """None for spread load, else --load's crowd square (x, z, r). Refuses anything but spread or three numbers."""
    if args.load == "spread":
        return None
    try:
        x, z, radius = (float(value) for value in args.load.split(","))
    except ValueError as err:
        raise ValueError(f"--load {args.load} is neither spread nor a crowd square x,z,r") from err
    return x, z, radius


def players_of(args):
    """--players as whole numbers. Refuses anything else."""
    try:
        return [int(n) for n in listed(args.players)]
    except ValueError as err:
        raise ValueError(f"--players {args.players} is not a list of whole numbers") from err


def bands_of(args):
    """--bands as metres ([] without it). Refuses a value that is not a positive number, a value given twice, and
    --band beside --bands."""
    if not args.bands:
        return []
    if args.band:
        raise ValueError(f"--band {args.band:g} and --bands {args.bands} both set the band; give --bands alone (it "
                         f"sets MESH_BAND on every run)")
    bands = []
    for text in listed(args.bands):
        try:
            band = float(text)
        except ValueError as err:
            raise ValueError(f"--bands {args.bands}: {text} is not a number of metres") from err
        if band <= 0:
            raise ValueError(f"--bands {args.bands}: {text} is not above 0 m (every band is set explicitly)")
        if band in bands:
            raise ValueError(f"--bands {args.bands} lists {band:g} twice")
        bands.append(band)
    if not bands:
        raise ValueError(f"--bands {args.bands!r} lists no band")
    return bands


def check_args(args):
    """None when an `abload ab` Namespace can be planned; raises ValueError naming the argument at fault: an unknown
    layout or monster setting (0.71's texts), players, load, bands, --puppets other than steered or kinematic, --rates
    that ServerMeshRatesSetting would refuse (its message) or that runs uncapped, and an unknown --order."""
    for name in listed(args.layouts):
        if name not in rigrun.LAYOUTS:
            raise ValueError(f"unknown layout {name}; known: {', '.join(rigrun.LAYOUTS)}")
    for m in listed(args.monsters):
        if m not in rigrun.MONSTER_ENV:
            raise ValueError(f"unknown monster setting {m}; known: {', '.join(rigrun.MONSTER_ENV)}")
    players_of(args)
    load_area(args)
    bands_of(args)
    if args.puppets and args.puppets not in PUPPETS:
        raise ValueError(f"--puppets {args.puppets} is not one of {', '.join(PUPPETS)}")
    if args.rates:
        fps, _, _, error = p0tools.rates_setting(args.rates)
        if error:
            raise ValueError(f"--rates {args.rates}: MESH_RATES {error}")
        if fps == p0tools.UNCAPPED:
            raise ValueError(f"--rates {args.rates} runs uncapped: every run would fail the uncapped guard (its Work "
                             f"window budget is 0); uncapped measurement is p0load's (density bridge-0.53)")
    if args.order not in ORDERS:
        raise ValueError(f"--order {args.order} is not one of {', '.join(ORDERS)}")


def ab_points(args):
    """The A/B's points (players, monster setting, band) in the given order: for each --players count, for each
    --monsters setting, for each --bands value (band innermost), else --band alone (0 = as configured). Refuses what
    players_of and bands_of refuse."""
    bands = bands_of(args) or [args.band]
    return [(n, mon, band) for n in players_of(args) for mon in listed(args.monsters) for band in bands]


def run_order(points, layouts, repeats, order="point-major"):
    """[(attempt, point, position, layout)] in run order, attempt and position counting from 0, layouts rotated left
    by the attempt (rigtools.rotate). point-major (0.71): each point's repeats back to back. repeat-major (the §2
    rule): repeat by repeat, the points in order on odd repeats and reversed on even ones. Refuses another order."""
    if order not in ORDERS:
        raise ValueError(f"run order {order!r} is not one of {', '.join(ORDERS)}")
    runs = []
    if order == "point-major":
        for point in points:
            for attempt in range(repeats):
                runs += [(attempt, point, position, layout)
                         for position, layout in enumerate(rigtools.rotate(layouts, attempt))]
        return runs
    for attempt in range(repeats):
        for point in (list(points) if attempt % 2 == 0 else list(reversed(points))):
            runs += [(attempt, point, position, layout)
                     for position, layout in enumerate(rigtools.rotate(layouts, attempt))]
    return runs


def ab_plan(args):
    """The RunSpecs (p0plans' shape) of an `abload ab` Namespace, in run order: family abgeo, game fantasy, case
    --case, arm = the layout, point {players, monsters, band (0 = as configured), load, puppets, rates}, repeat,
    seed (--seed + attempt), order (1-based), cells started and read = the layout's, expect {}, key (p0plans.plan_key),
    and abload's position (1-based, in the repeat's rotation). env is 0.71's (override, plan, crowd, seed, spawn cells,
    band, monsters) plus MESH_BAND for every --bands value, MESH_PUPPET_MOTION, MESH_RATES and p0plans.INSTR when
    given, empty values left out (request_play sends an unset key empty). Raises ValueError for what check_args,
    rigtools.load_plan and rigrun.check_env refuse."""
    check_args(args)
    layouts, area = listed(args.layouts), load_area(args)
    specs = []
    for order, (attempt, point, position, layout) in enumerate(
            run_order(ab_points(args), layouts, args.repeats, args.order), 1):
        n, mon, band = point
        cells = rigrun.LAYOUTS[layout]
        plan = rigtools.load_plan(cells, n, area)
        seed = args.seed + attempt
        env = {"MESH_LAYOUT_OVERRIDE": rigrun.layout_override(layout), "MESH_LOAD_PLAN": rigtools.plan_env(plan),
               "MESH_LOAD_AREA": args.load if area else "", "MESH_LOAD_SEED": str(seed),
               "MESH_SPAWN_CELLS": ",".join(cid for cid, _ in cells), "MESH_BAND": f"{band:g}" if band else ""}
        env.update(rigrun.MONSTER_ENV[mon])
        env.update(MESH_PUPPET_MOTION=args.puppets, MESH_RATES=args.rates)
        if args.instrument:
            env.update(p0plans.INSTR)
        env = {key: value for key, value in env.items() if value}
        rigrun.check_env(env)
        ids = [cid for cid, _ in cells]
        spec = {"family": FAMILY, "game": GAME, "case": args.case, "arm": layout,
                "point": {"players": n, "monsters": mon, "band": band, "load": args.load,
                          "puppets": args.puppets or None, "rates": args.rates or None},
                "repeat": attempt + 1, "seed": seed, "order": order, "layout": layout, "cells_started": ids,
                "cells_read": list(ids), "plan": dict(plan), "env": env, "warmup": args.warmup,
                "measure": args.measure, "rates": args.rates or None, "uncapped": [],
                "band": float(band) if band else None, "expect": {}, "position": position + 1}
        spec["key"] = p0plans.plan_key(spec)
        specs.append(spec)
    return specs


def point_of(spec):
    """The (players, monster setting, band) point an ab_plan spec belongs to (a key of ab_command's points)."""
    point = spec["point"]
    return point["players"], point["monsters"], point["band"]


def phase0(args):
    """Whether any Phase 0 flag that changes the runs or the report is given (--case alone only names rows)."""
    return bool(args.puppets or args.rates or args.bands or args.instrument or args.jsonl
                or args.order != "point-major")


def header_lines(args):
    """The A/B's header: 0.71's '=== static A/B ...' line (band 'per point (--bands)' with --bands) and, only when
    phase0(args), the '=== phase 0: ...' line naming puppets, rates, bands, instruments, order and the rows file."""
    band = "per point (--bands)" if args.bands else (args.band or "as configured (80 m)")
    lines = [f"=== static A/B, capped, load {args.load}, band {band}, {args.repeats} repeat(s) "
             f"(seeds {args.seed}..{args.seed + args.repeats - 1}), warmup {args.warmup}s measure {args.measure}s"]
    if phase0(args):
        bands = ",".join(f"{band:g}" for band in bands_of(args)) or "-"
        lines.append(f"=== phase 0: puppets {args.puppets or 'as configured'}, rates {args.rates or 'as configured'}, "
                     f"bands {bands}, instruments {'on' if args.instrument else 'off'}, order {args.order}, "
                     f"rows {args.jsonl or '-'}")
    return lines


def point_label(point, bands_given=False):
    """'250 players, monsters all', with ', band 20' only when --bands is given."""
    n, mon, band = point
    return f"{n} players, monsters {mon}" + (f", band {band:g}" if bands_given else "")


def run_heading(spec, positions, bands_given=False):
    """The '--- <point>, repeat R (seed S), <layout> (P/N): plan ...' line before a run."""
    return (f"--- {point_label(point_of(spec), bands_given)}, repeat {spec['repeat']} (seed {spec['seed']}), "
            f"{spec['layout']} ({spec['position']}/{positions}): plan {rigtools.plan_env(spec['plan'])}")


def run_record(start, per_cell):
    """A usable run's figures from its cells' window stats (rigrun.cell_window's 'stats'): start, cells, the peak cell
    and its work, the sums of work, cores, owned players and monsters, the worst sat, the saturated cells and the probe
    states."""
    peak_cell = max(per_cell, key=lambda c: per_cell[c]["work_mean"])
    return {
        "start": start, "cells": per_cell, "peak_cell": peak_cell,
        "peak_work": per_cell[peak_cell]["work_mean"],
        "sum_work": sum(s["work_mean"] for s in per_cell.values()),
        "cores": sum(s["cpu"] for s in per_cell.values()) / 100.0,
        "players": sum(s["owned_players"] for s in per_cell.values()),
        "monsters": sum(s["owned_monsters"] for s in per_cell.values()),
        "max_sat": max(s["sat"] for s in per_cell.values()),
        "saturated": [cid for cid, s in per_cell.items() if rigtools.is_saturated(s)],
        "probe": sorted({p for s in per_cell.values() for p in s["probe"]}),
    }


def run_line(record):
    """The line printed under a usable run: its peak cell, every cell's figures and the window start."""
    cells = ", ".join(f"{cid} {s['work_mean']:.2f} ms sat {s['sat']:.0f}% own {s['owned_players']:.0f}p/{s['owned_monsters']:.0f}m "
                      f"pup {s['puppet_players']:.0f}p/{s['puppet_monsters']:.0f}m"
                      for cid, s in record["cells"].items())
    flag = f" SATURATED {','.join(record['saturated'])}" if record["saturated"] else ""
    return f"    peak {record['peak_cell']} {record['peak_work']:.2f} ms | {cells} | {record['start']:%H:%M:%S}{flag}"


def cell_mean(runs, cid):
    """A cell's work mean averaged over a layout's runs."""
    return sum(x["cells"][cid]["work_mean"] for x in runs.values()) / len(runs)


def summarize_layout(point, layout, runs, repeats, bands_given=False):
    """One table row for a layout at a point: the mean of the per-run peaks, and every cell's mean across runs beside
    it (the busiest cell's alone above 4 cells); a band column only when --bands is given."""
    n, mon, band = point
    lead = f"{n:<8} {mon:<6} " + (f"{band:<6g} " if bands_given else "") + f"{layout:<7}"
    got = list(runs.values())
    if not got:
        return f"{lead} 0/{repeats} (no data)"
    peaks = [x["peak_work"] for x in got]
    cell_means = {cid: cell_mean(runs, cid) for cid in got[0]["cells"]}
    busiest = max(cell_means, key=cell_means.get)

    def mean(key):
        return sum(x[key] for x in got) / len(got)

    cells_text = " ".join(f"{cid}={v:.2f}" for cid, v in cell_means.items()) if len(cell_means) <= 4 else f"{busiest}={cell_means[busiest]:.2f}"
    runs_text = f"{len(got)}/{repeats}"
    spread = f"{min(peaks):.2f}-{max(peaks):.2f}"
    return (f"{lead} {runs_text:<6} {sum(peaks) / len(peaks):<9.2f} "
            f"{spread:<14} {cells_text:<22} {mean('sum_work'):<8.2f} {mean('cores'):<6.1f} "
            f"{mean('players'):<7.0f} {mean('monsters'):<8.0f} {max(x['max_sat'] for x in got):<6.0f} "
            f"{'/'.join(sorted({p for x in got for p in x['probe']}))}")


def summary_lines(points, layouts, repeats, bands_given=False):
    """The summary table: a blank line, the column header (a band column only with --bands), and summarize_layout's
    row for every point and layout."""
    header = (f"{'players':<8} {'mon':<6} " + (f"{'band':<6} " if bands_given else "")
              + f"{'layout':<7} {'runs':<6} {'peak ms':<9} {'range':<14} {'cell means ms':<22} "
              f"{'sum ms':<8} {'cores':<6} {'owned p':<7} {'monsters':<8} {'sat %':<6} probe")
    return ["", header] + [summarize_layout(point, layout, runs[layout], repeats, bands_given)
                           for point, runs in points.items() for layout in layouts]


def _busiest_mean(runs):
    return max(cell_mean(runs, cid) for cid in next(iter(runs.values()))["cells"])


def _base_label(base):
    return base if len(rigrun.LAYOUTS[base]) == 1 else f"{base} peak cell"


def _pair(label, cut, base, runs):
    """(text, ratios, left_out) of one cut/base pair at a point: 0.71's text with coarse generalised to any base."""
    cut_runs, base_runs = runs[cut], runs[base]
    ratios, left_out = [], []
    for attempt, cut_run in sorted(cut_runs.items()):
        base_run = base_runs.get(attempt)
        if base_run is None:
            continue
        base_saturated = base_run["peak_cell"] in base_run["saturated"]
        if base_saturated or cut_run["peak_cell"] in cut_run["saturated"]:
            side = _base_label(base) if base_saturated else f"{cut} peak cell"
            left_out.append(f"repeat {attempt + 1} ({side} saturated)")
            continue
        ratios.append(cut_run["peak_work"] / base_run["peak_work"])
    if ratios:
        text = (f"  {label}: {cut}/{base} = {' '.join(f'{x:.2f}' for x in ratios)} "
                f"(mean {sum(ratios) / len(ratios):.2f}; busiest cell's mean over {base}'s mean "
                f"{_busiest_mean(cut_runs) / _busiest_mean(base_runs):.2f})")
    else:
        text = f"  {label}: {cut}/{base}: " + (f"no usable {base} run" if not base_runs else "no usable pair")
    if left_out:
        text += "; left out: " + ", ".join(left_out)
    return text, ratios, left_out


def _monster_warning(label, mon, runs, layouts):
    monsters_by_layout = {layout: sum(x["monsters"] for x in runs[layout].values()) / len(runs[layout])
                          for layout in layouts if runs[layout]}
    if mon != "off" and len(monsters_by_layout) > 1 and min(monsters_by_layout.values()) > 0:
        if max(monsters_by_layout.values()) / min(monsters_by_layout.values()) > 1.15:
            return [f"  {label}: WARNING the layouts owned different monster totals "
                    f"({', '.join(f'{k} {v:.0f}' for k, v in monsters_by_layout.items())})"]
    return []


def _crossing_lines(n, layouts):
    rate = rigtools.crossing_rate(n, rigrun.GROUND, rigrun.GROUND, rigrun.CUT_LENGTH["halves"])
    lines = [f"  not measured: a halves cut would see about {rate:.1f} crossings/s at {n} players "
             f"(grid about {rate * rigrun.CUT_LENGTH['grid'] / rigrun.CUT_LENGTH['halves']:.1f}/s), each a handoff"]
    if "strips4" in layouts:
        strips = rigtools.crossing_rate(n, rigrun.GROUND, rigrun.GROUND, rigrun.CUT_LENGTH["strips4"])
        lines.append(f"  not measured: a strips4 layout would see about {strips:.1f} crossings/s at {n} players, "
                     f"each a handoff")
    return lines


def _usable(points):
    return any(layout_runs for runs in points.values() for layout_runs in runs.values())


def grade_points(points, layouts, area, repeats, bands_given=False):
    """(lines, groups): the verdict section, 0.71's text for coarse, halves and grid. Per point: the monster WARNING,
    then each pair of PAIRS whose two layouts ran (halves/coarse, grid/coarse, strips4/coarse, strips4/halves), then
    with spread load the crossing line(s). groups maps 'halves/coarse' and 'strips4/halves' to their per-point
    verdicts, for each pair of VERDICT_PAIRS whose layouts are both given with spread load (area None); a verdict with
    no usable pair because of saturation reads SATURATED. The section ends with 0.71's go/no-go line (or its 'no
    go/no-go' line without the halves/coarse group) and, with the strips4/halves group, that group's overall line."""
    min_runs = min(2, repeats)
    base_text = ("busiest cell over the coarse cell" if "strips4" not in layouts
                 else "busiest cell over the base layout's busiest cell (coarse; halves for strips4/halves)")
    lines = ["", f"{base_text}, per repeat (same seed, runs minutes apart); <= {GO_RATIO} everywhere is GO:"]
    groups = {f"{cut}/{base}": [] for cut, base in VERDICT_PAIRS
              if area is None and cut in layouts and base in layouts}
    for point, runs in points.items():
        n, mon, _ = point
        label = point_label(point, bands_given)
        lines += _monster_warning(label, mon, runs, layouts)
        for cut, base in PAIRS:
            # A point whose base runs all failed still gets a line and, for a judged pair, an INSUFFICIENT verdict:
            # skipping it would let the go/no-go stand on the other points alone.
            if cut not in runs or base not in runs:
                continue
            text, ratios, left_out = _pair(label, cut, base, runs)
            name = f"{cut}/{base}"
            if name in groups:
                verdict = rigtools.point_verdict(ratios, GO_RATIO, min_runs)
                if verdict == "INSUFFICIENT" and left_out:
                    verdict = "SATURATED"
                groups[name].append(verdict)
                text += f" -> {verdict}"
            lines.append(text)
        if area is None:
            lines += _crossing_lines(n, layouts)
    if "halves/coarse" in groups:
        lines.append(f"go/no-go (halves, spread load, every point): {rigtools.overall_verdict(groups['halves/coarse'])}")
    else:
        lines.append(NO_GO_NO_GO + ("" if _usable(points) else "; and no run was usable"))
    if "strips4/halves" in groups:
        lines.append(f"strips4/halves (spread load, every point): {rigtools.overall_verdict(groups['strips4/halves'])}")
    return lines, groups


def exit_code(points, groups):
    """abload ab's exit code: with no verdict group, 0 when any run was usable (0.71's rule), else 1; with groups, 0
    when every group's overall verdict is GO, NO-GO or MARGINAL, else 1."""
    if not groups:
        return 0 if _usable(points) else 1
    return 0 if all(rigtools.overall_verdict(verdicts) in DECIDED for verdicts in groups.values()) else 1
