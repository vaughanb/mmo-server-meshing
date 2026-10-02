"""Phase 0 geography (step 4): the split model, the layout load predictor and the map-size table (build sheet §4).
Pure: everything comes in as arguments; errors are p0base.ModelError naming the argument at fault.

    p0geo.halving_ratio(400, 80, 1.0)                  # 0.70: peak child over parent when a 400 m cell halves
    p0geo.break_even_width(80, 1.0, x=0.05)            # 336.84 m: W*, the narrowest cell whose halving pays
    p0geo.smallest_cell(1000, 80, 336.84)              # 250: s_min, down the halving ladder from a base
    p0geo.split_inputs(fit_ci, "60/0.03")              # k_hold, k_band, x, link_share, V from a presence fit
    p0geo.layout_loads(cells, 80, 250)                 # per cell O, U, G, links (+ the fit's regressor values)
    p0geo.predict_ratio(coef, cut, base, 80, 250, 0, "kinematic", "kinematic")   # max w(cut) / max w(base)
    p0geo.map_size_table([1000, 8000], [80], [(0.6, 0.5, 0.7)], 0.047)          # rows of the §4.7 table
    p0geo.cell_regressors(run_row, "C")                # the same regressor values, measured, from a mesh-p0/1 run row

Cells are [(cell id, (minX, minZ, sizeX, sizeZ))], rectangles in metres in the scene frame. The measured-load readers
(row_label, metric, read_cells, window_mean, cell_regressors) live here beside layout_loads so the predicted and the
measured regressors share one set of conventions (p0model fits them; p0results cannot be imported from here).
Unit tests: python -m unittest discover -s Tools/ServerMeshing -p "test_*.py"
"""
import math

import p0base
import rigtools

# The registry's own grid (map ServerMeshTestMap, 200 m cells): the layout a run gets with no MESH_LAYOUT_OVERRIDE.
REGISTRY_GRID = [(cid, (-300.0 + 200.0 * x, -300.0 + 200.0 * z, 200.0, 200.0))
                 for cid, x, z in (("SW", 0, 0), ("S", 1, 0), ("SE", 2, 0), ("W", 0, 1), ("C", 1, 1), ("E", 2, 1),
                                   ("NW", 0, 2), ("N", 1, 2), ("NE", 2, 2))]
GROUND_M = 600.0                 # the rig's ground (the coarse cell) is 600 m on a side
BUCKET_M = 20.0                  # the occupancy bucket of the local-pair count Q
BUCKET_ORIGIN = (-300.0, -300.0)
MODES = {0: "Static", 1: "Static unless its peak exceeds one process"}


def _positive(name, value):
    if not value > 0:
        raise p0base.ModelError(f"{name} must be positive; got {value:g}")


def intersect(a, b):
    """The rectangle two rectangles share, or None when they share no area."""
    x0, z0 = max(a[0], b[0]), max(a[1], b[1])
    x1, z1 = min(a[0] + a[2], b[0] + b[2]), min(a[1] + a[3], b[1] + b[3])
    return (x0, z0, x1 - x0, z1 - z0) if x1 > x0 and z1 > z0 else None


def expand(rect, band):
    """The rectangle grown by band metres on every side."""
    return (rect[0] - band, rect[1] - band, rect[2] + 2 * band, rect[3] + 2 * band)


def layout_cells(override):
    """[(cell id, rect)] from a MESH_LAYOUT_OVERRIDE value ("Map=C@-300,-300,300,600;E@0,-300,300,600"); an empty value
    is the registry's own grid. Refuses a value that is not that grammar."""
    if not override:
        return list(REGISTRY_GRID)
    cells = []
    for entry in override.split("=", 1)[-1].split(";"):
        cid, _, numbers = entry.partition("@")
        parts = numbers.split(",")
        try:
            rect = tuple(float(v) for v in parts)
        except ValueError:
            rect = ()
        if not cid or len(rect) != 4 or rect[2] <= 0 or rect[3] <= 0:
            raise p0base.ModelError(f"MESH_LAYOUT_OVERRIDE entry '{entry}' is not cell@x,z,width,height")
        cells.append((cid, rect))
    return cells


def crowd_rect(value):
    """The crowd square of a MESH_LOAD_AREA value "x,z,r" as a rectangle, or None when the value is empty. Refuses a
    value that is not three numbers with r > 0."""
    if not value:
        return None
    try:
        x, z, r = (float(v) for v in value.split(","))
    except ValueError as ex:
        raise p0base.ModelError(f"MESH_LOAD_AREA '{value}' is not x,z,r") from ex
    _positive("the MESH_LOAD_AREA half-size", r)
    return (x - r, z - r, 2 * r, 2 * r)


def expected_local_pairs(n, rect, bucket_m=BUCKET_M, origin=BUCKET_ORIGIN):
    """E[Σ n_b²] over the occupancy buckets for n characters placed uniformly over rect: n + n(n-1)·Σ_b (a_b/A)²
    (400 in (-100,-100,200,200) -> 1996.0; 250 on the 600 m ground -> 319.17). Refuses n < 0 or an empty rect."""
    if n < 0:
        raise p0base.ModelError(f"the local-pair count needs n >= 0; got {n:g}")
    if rect is None or rect[2] <= 0 or rect[3] <= 0:
        raise p0base.ModelError(f"the local-pair count needs a rectangle with area; got {rect}")

    def share_squares(start, size, bucket_origin):
        lo = math.floor((start - bucket_origin) / bucket_m)
        hi = math.ceil((start + size - bucket_origin) / bucket_m)
        widths = [min(start + size, bucket_origin + (i + 1) * bucket_m) - max(start, bucket_origin + i * bucket_m)
                  for i in range(lo, hi)]
        return math.fsum((w / size) ** 2 for w in widths if w > 0)

    return n + n * (n - 1) * share_squares(rect[0], rect[2], origin[0]) * share_squares(rect[1], rect[3], origin[1])


def owned_name(owner_motion):
    """The owned-character regressor of a MESH_LOAD_MOTION value ('' is steered)."""
    return f"O_{owner_motion or 'steered'}"


def puppet_name(owner_motion, puppet_motion):
    """The puppet-character regressor: U_inert when the owners stand, else by MESH_PUPPET_MOTION ('' is steered)."""
    return "U_inert" if owner_motion == "inert" else f"U_{puppet_motion or 'steered'}"


def monster_puppet_name(puppet_motion):
    """The puppet-monster regressor by MESH_PUPPET_MOTION ('' is steered)."""
    return f"U_monster_{puppet_motion or 'steered'}"


def row_label(row):
    """A run row named for error messages: family, case, arm, repeat and plan key."""
    return (f"{row.get('family')} {row.get('case')} {row.get('arm')} repeat {row.get('repeat')} "
            f"(key {row.get('plan_key')})")


def metric(row, cell, name):
    """The value of a run row's `name` mean metric on cell. Refuses a row that lacks it."""
    for m in row.get("metrics") or []:
        if m.get("name") == name and m.get("stat") == "mean" and m.get("cell") == cell:
            return m["value"]
    raise p0base.ModelError(f"{row_label(row)}: no {name} mean for cell {cell}")


def read_cells(row):
    """The cells a run row carries work for (those with a work_ms_per_s mean), in row order."""
    return list(dict.fromkeys(m["cell"] for m in row.get("metrics") or []
                              if m.get("name") == "work_ms_per_s" and m.get("stat") == "mean"))


def window_mean(row, cell, key):
    """The time-weighted mean of a Work window count over a cell's unsaturated windows. Refuses a cell with none."""
    windows = [w for w in (row.get("windows") or {}).get(cell) or [] if not w.get("saturated")]
    seconds = math.fsum(w["seconds"] for w in windows)
    if not seconds > 0:
        raise p0base.ModelError(f"{row_label(row)}: no unsaturated Work windows for cell {cell}")
    return math.fsum(w[key] * w["seconds"] for w in windows) / seconds


def _plan_total(row):
    value = (row.get("env") or {}).get("MESH_LOAD_PLAN", "")
    try:
        return sum(int(entry.split(":")[1]) for entry in value.split(",") if entry)
    except (IndexError, ValueError) as ex:
        raise p0base.ModelError(f"{row_label(row)}: MESH_LOAD_PLAN '{value}' is not cell:count,...") from ex


def cell_regressors(row, cell):
    """(regressors, note): the measured counterpart of layout_loads for one read cell of a mesh-p0/1 run row. c0;
    links = started cells touching it; O_kind by MESH_LOAD_MOTION (O_steered_nofight with MESH_LOAD_FIGHT=0) and
    O_monster from its Work windows; U by MESH_PUPPET_MOTION (U_inert for inert owners) and U_monster_<motion>;
    G_player = the other cell's puppet players in a two-cell layout, else layout_loads' G (E, noted), moving owners
    only; Q over MESH_LOAD_AREA ∩ cell and N2 = owned². Refuses read or started cells outside the layout."""
    env = row.get("env") or {}
    owner = env.get("MESH_LOAD_MOTION") or "steered"
    puppet = env.get("MESH_PUPPET_MOTION") or "steered"
    cells = dict(layout_cells(env.get("MESH_LAYOUT_OVERRIDE", "")))
    started = row.get("cells_started") or []
    if cell not in cells or any(cid not in cells for cid in started):
        raise p0base.ModelError(f"{row_label(row)}: cells {started} and read cell {cell} are not all in the layout "
                                f"{sorted(cells)}")
    rect = cells[cell]
    owned = window_mean(row, cell, "owned_players")
    others = [cid for cid in started if cid != cell]
    streamed, note = 0.0, None
    if owner != "inert" and len(others) == 1 and others[0] in (row.get("windows") or {}):
        streamed = window_mean(row, others[0], "puppet_players")
    elif owner != "inert" and others:
        band = float(env.get("MESH_BAND") or 80)
        streamed = layout_loads([(cid, cells[cid]) for cid in started], band, _plan_total(row))[cell]["G"]
        note = "G_player from p0geo.layout_loads (E) where the neighbour's puppets were not read"
    crowd = crowd_rect(env.get("MESH_LOAD_AREA", ""))
    place = intersect(rect, crowd) if crowd else rect
    if owned > 0 and place is None:
        raise p0base.ModelError(f"{row_label(row)}: cell {cell} owns {owned:g} players outside MESH_LOAD_AREA")
    name = "O_steered_nofight" if owner == "steered" and env.get("MESH_LOAD_FIGHT") == "0" else owned_name(owner)
    return {"c0": 1.0, "links": float(sum(rigtools.rects_touch(rect, cells[cid]) for cid in others)),
            name: owned, "O_monster": window_mean(row, cell, "owned_monsters"),
            puppet_name(owner, puppet): window_mean(row, cell, "puppet_players"),
            monster_puppet_name(puppet): window_mean(row, cell, "puppet_monsters"), "G_player": streamed,
            "Q": expected_local_pairs(owned, place) if owned > 0 else 0.0, "N2": owned * owned}, note


def halving_ratio(width, band, k, x=0.0, sigma=1.0, link_share=0.0):
    """Peak child over parent when a cell of this width halves: (x + ½ + link_share + σ·k·B/W)/(1 + x)."""
    _positive("width", width)
    return (x + 0.5 + link_share + sigma * k * band / width) / (1.0 + x)


def break_even_width(band, k, x=0.0, sigma=1.0, gate=0.75, link_share=0.0):
    """W*, the width at which halving_ratio equals the gate: σkB/(gate·(1+x) - x - ½ - link_share) (336.84 m at k 1,
    B 80, x 0.05). Refuses B <= 0, k < 0, σ outside (0, 1], x < 0, and a floor that alone fails the gate."""
    _positive("band", band)
    if k < 0:
        raise p0base.ModelError(f"k must not be negative; got {k:g}")
    if not 0 < sigma <= 1:
        raise p0base.ModelError(f"sigma must be in (0, 1]; got {sigma:g}")
    if x < 0:
        raise p0base.ModelError(f"x must not be negative; got {x:g}")
    denominator = gate * (1.0 + x) - x - 0.5 - link_share
    if not denominator > 0:
        floor_ratio = (x + 0.5 + link_share) / (1.0 + x)
        raise p0base.ModelError(f"no width pays: the floor alone gives a ratio of {floor_ratio:.3f}, above {gate:g}")
    return sigma * k * band / denominator


def smallest_cell(base, band, w_star):
    """s_min: halve from the base while the cell is at least W* wide and each child at least B wide (the validator's
    band rule). Refuses a base or band that is not positive."""
    _positive("base", base)
    _positive("band", band)
    width = base
    while width >= w_star * (1 - p0base.EPS) and width / 2 >= band * (1 - p0base.EPS):
        width /= 2
    return width


def _value(entry):
    return entry["value"] if isinstance(entry, dict) else float(entry)


def split_inputs(fit_ci, rates, trigger_ms_per_s=800.0, parent_links=0, links_gained=1, floor=None,
                 fit_rates="60/0.03", kind="kinematic"):
    """The split model's inputs from a presence or refit result (p0model.fit(...)["ci"]: c0, links, O_steered,
    U_<kind>, G_player as numbers or interval dicts): {k_hold, k_band, x, link_share, V, c0, lam}.
    V = trigger - c0 - λ·parent_links; x = (c0 + λ·parent_links)/V; link_share = λ·links_gained/V.
    c0 and λ at a rate set other than fit_rates come from `floor` = {c0, lam} measured at `rates` (a7's idle rows,
    or p0rates.convert_rates with block K's idle split); without it that is refused. k stays at the fit's rate set."""
    a = _value(fit_ci["O_steered"])
    if not a > 0:
        raise p0base.ModelError(f"k needs a positive owner cost; got O_steered = {a:g} ms/s")
    e = _value(fit_ci[f"U_{kind}"])
    g = _value(fit_ci["G_player"]) if "G_player" in fit_ci else 0.0
    if floor is not None:
        c0, lam = float(floor["c0"]), float(floor["lam"])
    elif rates == fit_rates:
        c0 = _value(fit_ci["c0"])
        lam = _value(fit_ci["links"]) if "links" in fit_ci else 0.0
    else:
        raise p0base.ModelError(f"c0 at {rates} needs a7's idle rows or block K's idle split")
    variable = trigger_ms_per_s - c0 - lam * parent_links
    if not variable > 0:
        raise p0base.ModelError(f"the floor {c0 + lam * parent_links:g} ms/s leaves no load under the "
                                f"{trigger_ms_per_s:g} ms/s trigger")
    return {"k_hold": e / a, "k_band": (e + g) / a, "x": (c0 + lam * parent_links) / variable,
            "link_share": lam * links_gained / variable, "V": variable, "c0": c0, "lam": lam}


def layout_loads(cells, band, players, monsters=0, owner_motion="steered", puppet_motion="steered",
                 monster_counts=None):
    """Per cell {O, U, G, links, O_monster, U_monster, Q, regressors} for players spread uniformly over the cells.
    O = ρ·area; U = ρ·Σ over linked cells j of area(rect_j ∩ expand(rect_i, B)); G = ρ·Σ over linked j of
    area(rect_i ∩ expand(rect_j, B)), moving owners only (an inert owner's streaming is in O_inert); links from
    rigtools.rects_touch (label E). Monsters are not uniform: O_monster/U_monster come from monster_counts
    {cid: {O_monster, U_monster}} (measured, or monster_counts_from_grid); refused when monsters > 0 without them.
    'regressors' holds the fit's column values (c0, links, O_*, U_*, G_player, O_monster, U_monster_*, Q, N2)."""
    _positive("band", band)
    if players < 0:
        raise p0base.ModelError(f"players must not be negative; got {players:g}")
    if monsters and monster_counts is None:
        raise p0base.ModelError("monster loads need measured per-cell monster counts (monster_counts)")
    density = players / math.fsum(rect[2] * rect[3] for _, rect in cells)
    moving = owner_motion != "inert"
    out = {}
    for cid, rect in cells:
        linked = [other for oid, other in cells if oid != cid and rigtools.rects_touch(rect, other)]
        owned = density * rect[2] * rect[3]
        puppets = density * math.fsum(rigtools.rect_overlap(other, expand(rect, band)) for other in linked)
        streamed = density * math.fsum(rigtools.rect_overlap(rect, expand(other, band)) for other in linked)
        counts = monster_counts.get(cid) if monsters else {"O_monster": 0.0, "U_monster": 0.0}
        if counts is None:
            raise p0base.ModelError(f"monster_counts has no entry for cell {cid}")
        pairs = expected_local_pairs(owned, rect) if owned > 0 else 0.0
        load = {"O": owned, "U": puppets, "G": streamed if moving else 0.0, "links": len(linked),
                "O_monster": counts["O_monster"], "U_monster": counts["U_monster"], "Q": pairs}
        load["regressors"] = {"c0": 1.0, "links": float(len(linked)), owned_name(owner_motion): owned,
                              puppet_name(owner_motion, puppet_motion): puppets, "G_player": load["G"],
                              "O_monster": load["O_monster"], monster_puppet_name(puppet_motion): load["U_monster"],
                              "Q": pairs, "N2": owned * owned}
        out[cid] = load
    return out


def monster_counts_from_grid(grid_owned, cells, band, grid_cells=None):
    """Estimated {cid: {O_monster, U_monster}} for a layout never run with monsters (strips4, label E) from the grid
    layout's measured owned monsters per cell {grid cid: n}: each grid cell's monsters are spread over it; O by area
    overlap, U from the linked cells' monsters within band B. Refuses a grid cell with no count."""
    grid_cells = grid_cells or REGISTRY_GRID
    missing = [gid for gid, _ in grid_cells if gid not in grid_owned]
    if missing:
        raise p0base.ModelError(f"grid monster counts are missing cells {', '.join(missing)}")

    def monsters_in(area):
        if area is None:
            return 0.0
        return math.fsum(grid_owned[gid] * rigtools.rect_overlap(g, area) / (g[2] * g[3]) for gid, g in grid_cells)

    out = {}
    for cid, rect in cells:
        linked = [other for oid, other in cells if oid != cid and rigtools.rects_touch(rect, other)]
        grown = expand(rect, band)
        out[cid] = {"O_monster": monsters_in(rect),
                    "U_monster": math.fsum(monsters_in(intersect(other, grown)) for other in linked)}
    return out


def predicted_work(coef, regressors):
    """Σ coef·value over the regressors the fit has (a regressor the fit lacks adds nothing)."""
    return math.fsum(coef[name] * value for name, value in regressors.items() if name in coef)


def predict_ratio(coef, cut, base, band, players, monsters, owner_motion, puppet_motion, monster_counts=None):
    """max ŵ(cut)/max ŵ(base) with the fit's coefficients over layout_loads of both layouts; monster_counts is
    (cut counts, base counts) when monsters > 0. Refuses a base whose predicted peak is not positive."""
    cut_counts, base_counts = monster_counts if monster_counts is not None else (None, None)
    peaks = []
    for cells, counts in ((cut, cut_counts), (base, base_counts)):
        loads = layout_loads(cells, band, players, monsters, owner_motion, puppet_motion, counts)
        peaks.append(max(predicted_work(coef, load["regressors"]) for load in loads.values()))
    if not peaks[1] > 0:
        raise p0base.ModelError(f"the base layout's predicted peak is {peaks[1]:g} ms/s; no ratio")
    return peaks[0] / peaks[1]


def neighbour_links(m, max_neighbours=8, edges="map"):
    """Directed neighbour links of an m x m map of cells: 'map' counts real links (corner 3, edge 5, interior 8, each
    capped; 0 for one cell), 'interior' is the sheet's §4.4 form m²·min(8, cap)."""
    if edges == "interior":
        return m * m * min(8, max_neighbours)
    if edges != "map":
        raise p0base.ModelError(f"edges must be 'map' or 'interior'; got {edges!r}")
    if m == 1:
        return 0
    cap = max_neighbours
    return 4 * min(3, cap) + 4 * (m - 2) * min(5, cap) + (m - 2) ** 2 * min(8, cap)


def total_cpu_ratio(side, width, band, k, c0=0.0, load=1.0, lam=0.0, max_neighbours=8, edges="map"):
    """T(W)/T(S), a map of side S cut into W cells against one cell: [m²·c0 + links·λ + L·(1 + k·φ)]/(c0 + L).
    'interior' is the sheet's §4.4 form (φ = 4B/W + 4B²/W²); 'map' counts real links and internal band only
    (φ = 4B(m-1)/(mW) + 4B²(m-1)²/(m²W²)). Refuses a side that is not a whole number of cells."""
    _positive("width", width)
    m = side / width
    if m < 1 - p0base.EPS or abs(m - round(m)) > 1e-9 * max(1.0, m):
        raise p0base.ModelError(f"the map side {side:g} m is not a whole number of {width:g} m cells")
    m = round(m)
    if c0 + load <= 0:
        raise p0base.ModelError(f"c0 + load must be positive; got {c0 + load:g}")
    if edges == "interior":
        phi = 4 * band / width + 4 * band ** 2 / width ** 2
    else:
        phi = 4 * band * (m - 1) / (m * width) + 4 * band ** 2 * (m - 1) ** 2 / (m * m * width ** 2)
    links = neighbour_links(m, max_neighbours, edges)
    return (m * m * c0 + links * lam + load * (1 + k * phi)) / (c0 + load)


def crossings_per_player(speed, width):
    """Edge crossings per second of one moving player in cells of this width: 4v/(πW)."""
    _positive("width", width)
    return 4 * speed / (math.pi * width)


def choose_base(side, w_star, band, max_base=None):
    """(base, population_limited): the smallest side/2ⁿ with base >= 2·W* and base >= B (the validator refuses a cell
    narrower than the band; the side itself when side < 2·W*); a smaller max_base wins (halved down the same ladder,
    never below B) and flags population_limited. Refuses a side or B that is not positive, a side narrower than B, a
    max_base no rung at least B wide meets, and a W* that is not positive (base >= 2·W* would hold at every size: k 0
    means every split pays, so the base is not the split model's to choose)."""
    _positive("side", side)
    _positive("band", band)
    if not w_star > 0:
        raise p0base.ModelError(f"no base follows from W* {w_star:g} m: base >= 2·W* holds at every size (k 0 means "
                                f"every split pays); pass an explicit base")
    if side < band * (1 - p0base.EPS):
        raise p0base.ModelError(f"the map side {side:g} m is narrower than the band {band:g} m")
    base = side
    while base / 2 >= max(2 * w_star, band) * (1 - p0base.EPS):
        base /= 2
    limited = False
    while max_base is not None and base > max_base * (1 + p0base.EPS):
        if base / 2 < band * (1 - p0base.EPS):
            raise p0base.ModelError(f"no base of the {side:g} m side is at most {max_base:g} m and at least the band "
                                    f"{band:g} m wide")
        base /= 2
        limited = True
    return base, limited


def _ks(ks):
    for k in ks:
        if isinstance(k, (tuple, list)):
            yield from zip(k, ("estimate", "lo", "hi"))
        else:
            yield k, None


def map_size_table(sides, bands, ks, x, link_share=0.0, sigma=1.0, gate=0.75, base=None, core_budget=None,
                   max_base=None, speed=5.0, players=None, moving=0.3, c0=0.0, lam=0.0, load=1.0):
    """The map-size table: one row per (side, band, k) with {side, band, k, k_role, w_star, base, bases, s_min,
    halvings, max_cells_per_base, first_ratio, total_cpu, crossings_per_s, mode, population_limited}. A k given as
    (k̂, lo, hi) emits three rows (k_role estimate/lo/hi). The base follows §4.6 (base >= 2·W*) unless `base` is
    given (1000 reproduces §4.3); total_cpu is the map form at s_min with c0, lam and load; crossings_per_s needs
    players. No base or s_min is narrower than B (choose_base). Refuses empty sides, a side that is not a power-of-two
    multiple of an explicit base, an explicit base narrower than B, and choose_base's refusals."""
    if not sides:
        raise p0base.ModelError("the map-size table needs at least one map side")
    rows = []
    for side in sides:
        if base is not None:
            _positive("base", base)
            ratio = side / base
            if ratio < 1 - p0base.EPS or abs(math.log2(ratio) - round(math.log2(ratio))) > 1e-9:
                raise p0base.ModelError(f"the map side {side:g} m is not a power-of-two multiple of the "
                                        f"{base:g} m base")
        for band in bands:
            for k, role in _ks(ks):
                w_star = break_even_width(band, k, x, sigma, gate, link_share)
                if base is None:
                    cell_base, limited = choose_base(side, w_star, band, max_base)
                elif base < band * (1 - p0base.EPS):
                    raise p0base.ModelError(f"the {base:g} m base is narrower than the band {band:g} m")
                else:
                    cell_base, limited = base, False
                s_min = smallest_cell(cell_base, band, w_star)
                halvings = round(math.log2(cell_base / s_min))
                bases = round((side / cell_base) ** 2)
                cells = 4 ** halvings
                if core_budget:
                    cells = min(cells, core_budget)
                mode = "Dynamic" if bases > 1 else ("Dynamic if busy" if halvings >= 2 else MODES[halvings])
                crossings = (players * moving * crossings_per_player(speed, s_min) if players is not None
                             else None)
                rows.append({"side": side, "band": band, "k": k, "k_role": role, "w_star": w_star,
                             "base": cell_base, "bases": bases, "s_min": s_min, "halvings": halvings,
                             "max_cells_per_base": cells,
                             "first_ratio": halving_ratio(cell_base, band, k, x, sigma, link_share),
                             "total_cpu": total_cpu_ratio(side, s_min, band, k, c0, load, lam),
                             "crossings_per_s": crossings, "mode": mode, "population_limited": limited})
    return rows
