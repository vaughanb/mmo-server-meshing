"""Phase 0 block K (step 4): cell work split into per-frame, per-physics-step and per-second parts over measured rates,
the per-entity split of a load arm over idle, and work moved between rate sets. Pure: everything comes in as arguments.
Split out of p0model (the ~500-line rule), whose least squares and jackknife these use.

    p0rates.rate_points(run_rows)                  # (points, notes): block K's inputs from accepted run rows
    p0rates.rate_decomposition(points)             # block K: F ms/frame, Ph ms/step, C ms/s and S = C - 20·T
    p0rates.rate_split(kind_points, idle_points, n)   # the per-entity split of a kind arm over the idle arm
    p0rates.convert_rates(57.6667, 60, 100 / 3, 30, 50, 0.35, 0.20)   # 50.5 ms/s at 30/0.02

Results use the column names (per_frame, per_step, per_second, other_per_second, interaction, kind_per_*,
entity_per_*); p0report turns them into mesh-p0/1 fit rows through p0results.FIT_NAMES. Errors are p0base.ModelError
naming the repeat or rate at fault.
Unit tests: python -m unittest discover -s Tools/ServerMeshing -p "test_*.py"
"""
import math

import p0base
import p0geo
import p0model

TICK_HZ = 20
RATE_NAMES = ("per_frame", "per_step", "per_second")


def rate_points(run_rows, cell="C"):
    """(points, notes): block K's inputs, one per accepted run row: measured fps and steps_per_s, work_ms_per_s,
    kit_mean_ms (T, the sum of the kit sync, aoi and move means), rate = (nominal fps, step s), repeat, capped."""
    points = []
    for row in run_rows:
        if row.get("accepted"):
            rates = row.get("rates") or {}
            points.append({"fps": p0geo.metric(row, cell, "frames_per_s"),
                           "steps_per_s": p0geo.metric(row, cell, "physics_steps_per_s"),
                           "work_ms_per_s": p0geo.metric(row, cell, "work_ms_per_s"),
                           "kit_mean_ms": math.fsum(p0geo.metric(row, cell, n) for n in
                                                    ("kit_sync_ms", "kit_aoi_ms", "kit_move_ms")),
                           "rate": (rates.get("fps"), rates.get("fixed_step_s")),
                           "repeat": p0model.effective_repeat(row), "capped": row.get("capped", True)})
    skipped = len(run_rows) - len(points)
    return points, [f"{skipped} rows not accepted, left out"] if skipped else []


def _rate_design(points):
    for p in points:
        fps, step = p["rate"]
        if fps is None or not step:
            raise p0base.ModelError(f"the rate decomposition needs rows with MESH_RATES; repeat {p['repeat']} has none")
        problem = "uncapped" if not p.get("capped", True) else ("saturated" if p.get("saturated") else None)
        if problem:
            raise p0base.ModelError(f"the rate decomposition refuses {problem} rows (repeat {p['repeat']} at "
                                    f"{fps:g}/{step:g})")
    cells = [(round(p["rate"][0]), round(1.0 / p["rate"][1], 1)) for p in points]
    fps, steps = sorted({c[0] for c in cells}), sorted({c[1] for c in cells})
    if len(fps) < 2 or len(steps) < 2:
        raise p0base.ModelError(f"the rate decomposition needs two frame rates and two physics rates; got fps {fps} "
                                f"and physics steps/s {steps}")
    rows = [{"per_frame": p["fps"], "per_step": p["steps_per_s"], "per_second": 1.0} for p in points]
    return rows, cells, fps, steps


def _interaction(rows, y, cells, fps, steps):
    """The derived function of the interaction contrast over the cell means of the additive fit's residuals; it
    refuses a fit that lacks one of the four (fps, step) cells."""
    def interaction(coef, where, keep):
        groups = {}
        for i in keep:
            fitted = math.fsum(coef[name] * rows[i][name] for name in RATE_NAMES)
            groups.setdefault(cells[i], []).append(y[i] - fitted)
        if len(groups) < 4:
            raise p0base.ModelError(f"the interaction contrast needs all four rate points; {where} has "
                                    f"{sorted(groups)}")
        m = {c: math.fsum(v) / len(v) for c, v in groups.items()}
        return m[(fps[1], steps[1])] - m[(fps[1], steps[0])] - m[(fps[0], steps[1])] + m[(fps[0], steps[0])]
    return interaction


def rate_decomposition(points):
    """(full, ci) for w = F·frames/s + Ph·steps/s + C over measured rates (points as rate_points), jackknifed over
    repeat, with derived other_per_second S = C - mean(20·T) over each fit's rows and, on a 2 x 2 design, the
    interaction I = m(hi,hi) - m(hi,lo) - m(lo,hi) + m(lo,lo) over the cell means of the additive fit's residuals (the
    same as over w at nominal rates, and blind to off-nominal measured rates); |I| > max(t·SE_I, 5 % of mean w) adds a
    "non-additive rates" warning. The interaction is its own jackknife: when the full fit or a deletion lacks one of
    the four cells it is left out of ci and full["notes"] says why, while F, Ph, C and S are still fitted (three cells
    identify them). Refuses one frame rate or one step, an uncapped row and a saturated row."""
    rows, cells, fps, steps = _rate_design(points)
    y = [p["work_ms_per_s"] for p in points]
    ticks = [TICK_HZ * p["kit_mean_ms"] for p in points]
    derived = {"other_per_second": lambda coef, where, keep: coef["per_second"] - math.fsum(ticks[i] for i in keep)
               / len(keep)}
    groups = [p["repeat"] for p in points]
    full, ci = p0model.jackknife(rows, y, list(RATE_NAMES), groups, derived)
    full["notes"] = []
    if len(fps) == 2 and len(steps) == 2:
        present = sorted(set(cells))
        try:
            if len(present) < 4:
                raise p0base.ModelError(f"the full fit has only {present}")
            ci["interaction"] = p0model.jackknife(rows, y, list(RATE_NAMES), groups,
                                                  {"interaction": _interaction(rows, y, cells, fps, steps)})[1][
                "interaction"]
        except p0base.ModelError as ex:
            full["notes"].append(f"interaction not computed: {ex}")
    if "interaction" in ci:
        contrast, limit = ci["interaction"], max(ci["interaction"]["t"] * ci["interaction"]["se"],
                                                 0.05 * math.fsum(y) / len(y))
        if abs(contrast["value"]) > limit:
            full["warnings"].append(f"non-additive rates: interaction {contrast['value']:.3f} ms/s exceeds "
                                    f"{limit:.3f} ms/s (max of t·SE and 5 % of the mean work)")
    return full, ci


def rate_split(kind_points, idle_points, n):
    """(full, ci) of one joint fit over a kind arm and the idle arm: per_frame, per_step, per_second plus their
    products with the kind-arm indicator (kind_per_*); derived entity_per_* = kind_per_* / n, the per-entity split.
    Jackknifed over repeat, so each deletion drops that repeat from both arms. Refuses n <= 0 and each arm's
    rate_decomposition refusals."""
    if not n > 0:
        raise p0base.ModelError(f"the per-entity split needs a positive entity count; got {n}")
    idle_rows = _rate_design(idle_points)[0]
    kind_rows = [dict(r, **{f"kind_{name}": r[name] for name in RATE_NAMES}) for r in _rate_design(kind_points)[0]]
    names = list(RATE_NAMES) + [f"kind_{name}" for name in RATE_NAMES]
    derived = {f"entity_{name}": (lambda coef, where, keep, column=f"kind_{name}": coef[column] / n)
               for name in RATE_NAMES}
    return p0model.jackknife(idle_rows + kind_rows, [p["work_ms_per_s"] for p in idle_points + kind_points], names,
                             [p["repeat"] for p in idle_points + kind_points], derived)


def convert_rates(w, f1, p1, f2, p2, per_frame, per_step):
    """Work in ms/s moved from f1 frames/s and p1 steps/s to f2 and p2: w + F·(f2 - f1) + Ph·(p2 - p1)."""
    return w + per_frame * (f2 - f1) + per_step * (p2 - p1)
