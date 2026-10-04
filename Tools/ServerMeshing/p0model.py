"""Phase 0 statistics (step 4): least squares with a delete-one-repeat jackknife, the work-model fits over mesh-p0/1
run rows and the N² form. Pure: everything comes in as arguments. Block K's rate decomposition is p0rates'.

    p0model.least_squares(rows, y, names)      # {coef, n, p, df, rms, r2, min_sine, residuals, warnings}
    p0model.jackknife(rows, y, names, groups)  # (full fit, {name: {value, se, lo, hi, t, leave_one_out}})
    p0model.fit(run_rows, "presence")          # coefficients, k_hold/k_band, jackknife intervals, method, notes
    p0model.n2_form(run_rows)                  # "qQ" or "qN2", by AIC over the density base rows

Fit results use the regressor names (c0, links, O_steered, U_kinematic, G_player, Q, k_hold_kinematic, ...);
p0report turns them into mesh-p0/1 fit rows through p0results.FIT_NAMES and evaluates the gates with the p0base
verdict helpers. Errors are p0base.ModelError naming the regressor, repeat, row or cell at fault.
Unit tests: python -m unittest discover -s Tools/ServerMeshing -p "test_*.py"
"""
import json
import math

import p0base
import p0geo

RANK_TOL = 1e-8        # |R_jj| of a unit-norm column below this: the column lies in the span of the earlier ones
MIN_SINE_WARN = 1e-3
SSE_RESOLUTION = 1e-12  # residual RMS relative to the work's RMS below which a fit is exact (n2_form's AIC)
METHOD_REPEATS = "ols + jackknife over repeats"
METHOD_RUNS = "ols + jackknife over runs (unbalanced repeats)"
RUNS_LABEL = "runs as groups (assumes no between-repeat drift)"
KINDS = ("steered", "kinematic", "inert")

FIT_SPECS = {
    "presence": {"required": ("c0", "links", "O_steered", "O_kinematic", "U_steered", "U_kinematic", "G_player"),
                 "optional": ("O_inert", "U_inert")},
    "density": {"required": ("c0", "O_steered", "Q"), "optional": ("O_kinematic", "O_inert", "O_monster")},
    "refit": {"required": ("c0",),
              "optional": ("links", "O_steered", "O_kinematic", "O_inert", "O_monster", "U_steered", "U_kinematic",
                           "U_inert", "U_monster_steered", "U_monster_kinematic", "G_player", "Q")},
    "floor": {"required": ("c0", "links"), "optional": ()},
}
# (family, cases or None for any, arms or None for any) per fit: the animation-off and additivity rows never enter
# the presence fit; attribution/sparse feed only n2_removed and bridge-0.53 only its ratio.
PRESENCE_ROWS = (("presence", ("baseline", "main", "solo"), None),)
DENSITY_ROWS = (("density", ("grid", "scale"), ("base",)),)
ROW_SELECTION = {
    "presence": PRESENCE_ROWS,
    "density": DENSITY_ROWS,
    "refit": PRESENCE_ROWS + DENSITY_ROWS + (("density", ("attribution",), ("monsters-on",)), ("abgeo", None, None)),
    "floor": (("floor", ("cell", "linked"), ("idle",)),),
    "additivity": (("presence", ("additivity",), None),),
}


def _fmt(value):
    return f"{value:.6g}"


def _refuse_dependent(a, r, j, names, norms):
    # Column j (unit norm) lies in the span of columns 0..j-1: solve R[:j,:j]·c = (Qᵀx_j)[:j] and name the combination.
    c = [0.0] * j
    for i in reversed(range(j)):
        c[i] = (a[i][j] - math.fsum(r[i][k] * c[k] for k in range(i + 1, j))) / r[i][i]
    terms = [f"{_fmt(c[i] * norms[j] / norms[i])} x '{names[i]}'" for i in range(j)
             if abs(c[i] * norms[j] / norms[i]) > 1e-6]
    raise p0base.ModelError(f"singular design: regressor '{names[j]}' equals {' + '.join(terms) or '0'} in every row "
                            f"(drop one of them, or add runs where they differ)")


def _solve(rows, y, names, min_rows):
    n, p = len(rows), len(names)
    if len(y) != n:
        raise p0base.ModelError(f"{n} rows but {len(y)} responses")
    if n < min_rows:
        raise p0base.ModelError(f"{n} rows for {p} regressors ({', '.join(names)}): need at least {min_rows}")
    cols = [[float(row.get(name, 0.0)) for row in rows] for name in names]
    norms = [math.sqrt(math.fsum(v * v for v in col)) for col in cols]
    for name, norm in zip(names, norms):
        if norm == 0.0:
            raise p0base.ModelError(f"singular design: regressor '{name}' is zero in every row")
    a = [[cols[j][i] / norms[j] for j in range(p)] for i in range(n)]
    b = [float(v) for v in y]
    r = [[0.0] * p for _ in range(p)]
    for j in range(p):
        x = [a[i][j] for i in range(j, n)]
        alpha = math.sqrt(math.fsum(v * v for v in x))
        if alpha < RANK_TOL:
            _refuse_dependent(a, r, j, names, norms)
        if x[0] > 0:
            alpha = -alpha
        v = x[:]
        v[0] -= alpha
        vv = math.fsum(t * t for t in v)
        for k in range(j, p):
            f = 2.0 * math.fsum(v[i - j] * a[i][k] for i in range(j, n)) / vv
            for i in range(j, n):
                a[i][k] -= f * v[i - j]
        f = 2.0 * math.fsum(v[i - j] * b[i] for i in range(j, n)) / vv
        for i in range(j, n):
            b[i] -= f * v[i - j]
        r[j][j:] = a[j][j:]
    z = [0.0] * p
    for j in reversed(range(p)):
        z[j] = (b[j] - math.fsum(r[j][k] * z[k] for k in range(j + 1, p))) / r[j][j]
    inverse = [[0.0] * p for _ in range(p)]
    for c in range(p):
        for i in reversed(range(c + 1)):
            unit = 1.0 if i == c else 0.0
            inverse[i][c] = (unit - math.fsum(r[i][k] * inverse[k][c] for k in range(i + 1, c + 1))) / r[i][i]
    sines = [1.0 / math.sqrt(math.fsum(v * v for v in inverse[j])) for j in range(p)]
    coef = {names[j]: z[j] / norms[j] for j in range(p)}
    residuals = [float(yy) - math.fsum(coef[nm] * float(row.get(nm, 0.0)) for nm in names)
                 for row, yy in zip(rows, y)]
    sse = math.fsum(e * e for e in residuals)
    mean = math.fsum(y) / n
    sst = math.fsum((yy - mean) ** 2 for yy in y)
    worst = min(range(p), key=lambda j: sines[j])
    warnings = []
    if sines[worst] < MIN_SINE_WARN:
        warnings.append(f"near-singular design: regressor '{names[worst]}' is within sine {sines[worst]:.2g} of the "
                        f"others' span (min_sine < {MIN_SINE_WARN:g})")
    return {"coef": coef, "n": n, "p": p, "df": n - p, "rms": math.sqrt(sse / n),
            "r2": 1.0 - sse / sst if sst > 0 else (1.0 if sse == 0 else 0.0), "min_sine": sines[worst],
            "residuals": residuals, "warnings": warnings}


def least_squares(rows, y, names):
    """Ordinary least squares by Householder QR on unit-norm columns, in the declared order. rows are dicts (a missing
    name is 0). Returns {coef, n, p, df, rms, r2, min_sine, residuals, warnings}; min_sine is the smallest sine between
    a column and the span of all the others (order-invariant), and a value under 1e-3 adds a warning. Refuses a
    response count that differs, fewer than p + 1 rows, and a singular design (naming the combination)."""
    return _solve(rows, y, names, len(names) + 1)


def jackknife(rows, y, names, groups, derived=None, unit="repeat"):
    """(full fit, {name: {value, se, lo, hi, t, leave_one_out}}): delete-one-group refits, SE = sqrt((G-1)/G·Σ(θ₋ⱼ -
    θ̄)²), interval θ̂ ± t975(G-1)·SE. derived = {name: f(coef, where, keep)} is recomputed per deletion. A deletion
    may be just identified (n = p). Refuses fewer than 2 groups and a singular deletion (naming the group)."""
    if len(groups) != len(rows):
        raise p0base.ModelError(f"{len(rows)} rows but {len(groups)} group labels")
    derived = derived or {}
    full = least_squares(rows, y, names)
    keys = sorted(set(groups))
    if len(keys) < 2:
        raise p0base.ModelError(f"a jackknife over {unit}s needs at least 2 {unit}s; got {len(keys)}")
    estimate = dict(full["coef"])
    for name, f in derived.items():
        estimate[name] = f(full["coef"], "the full fit", list(range(len(rows))))
    left_out = {name: [] for name in estimate}
    for key in keys:
        keep = [i for i, group in enumerate(groups) if group != key]
        try:
            part = _solve([rows[i] for i in keep], [y[i] for i in keep], names, len(names))
        except p0base.ModelError as ex:
            raise p0base.ModelError(f"leaving out {unit} {key}: {ex}") from ex
        for name in names:
            left_out[name].append(part["coef"][name])
        for name, f in derived.items():
            left_out[name].append(f(part["coef"], f"{unit} {key} left out", keep))
    g = len(keys)
    t = p0base.t975(g - 1)
    out = {}
    for name, values in left_out.items():
        mean = math.fsum(values) / g
        se = math.sqrt((g - 1) / g * math.fsum((v - mean) ** 2 for v in values))
        out[name] = {"value": estimate[name], "se": se, "lo": estimate[name] - t * se,
                     "hi": estimate[name] + t * se, "t": t, "leave_one_out": values}
    return full, out


def ratio_of(num, den):
    """A derived function for jackknife: Σ coef[num] / Σ coef[den] (a name or a tuple of names). Refuses a
    denominator <= 0 in any fit, naming it and the fit ("... in repeat 2 left out")."""
    nums = (num,) if isinstance(num, str) else tuple(num)
    dens = (den,) if isinstance(den, str) else tuple(den)

    def ratio(coef, where, keep=None):
        d = math.fsum(coef[name] for name in dens)
        if not d > 0:
            raise p0base.ModelError(f"ratio undefined: denominator {' + '.join(dens)} = {_fmt(d)} ms/s in {where}")
        return math.fsum(coef[name] for name in nums) / d
    return ratio


def effective_repeat(row):
    """A run's jackknife group: the repeat a third-repeat run replaces (replaces_repeat), else its own repeat."""
    return row.get("replaces_repeat") or row["repeat"]


def _design_point(row):
    return f"{row.get('family')} {row.get('case')} {row.get('arm')} {json.dumps(row.get('point'), sort_keys=True)}"


def run_groups(run_rows):
    """(labels, unit, notes) over run rows: one label per run = its effective repeat (unit "repeat") when every design
    point (family, case, arm, point) has the most common set of effective repeats or a subset of it (runs lost, none
    extra: the lost run's group is left out without it, and a note names the points); otherwise, when some point has a
    repeat outside that set (planned repeat counts differ), one group per run (unit "run", the unbalanced method) with
    a note naming the points whose repeats differ."""
    present = {}
    for row in run_rows:
        present.setdefault(_design_point(row), set()).add(effective_repeat(row))
    sets = [frozenset(s) for s in present.values()]
    if len(set(sets)) <= 1:
        return [effective_repeat(row) for row in run_rows], "repeat", []
    common = max(sets, key=sets.count)
    short = "; ".join(f"{point} has {sorted(s)}" for point, s in present.items() if s != common)
    if all(s <= common for s in sets):
        return ([effective_repeat(row) for row in run_rows], "repeat",
                [f"runs missing (delete-one-repeat groups kept; a missing run's group is left out without it): "
                 f"{short}"])
    return (list(range(len(run_rows))), "run",
            [f"unbalanced repeats (most points have {sorted(common)}): {short}; jackknife over runs"])


def fit_columns(fit_id, q_form=None):
    """(required, optional) regressors of a fit; q_form "qQ" or "qN2" (n2_form's choice) picks Q or N2 (density
    and refit default to Q; presence gains it only when given). Refuses an unknown fit or form."""
    if fit_id not in FIT_SPECS:
        raise p0base.ModelError(f"unknown fit '{fit_id}'; known: {', '.join(FIT_SPECS)}")
    if q_form not in (None, "qQ", "qN2"):
        raise p0base.ModelError(f"q_form must be 'qQ' or 'qN2'; got {q_form!r}")
    q = "N2" if q_form == "qN2" else "Q"
    required = tuple(q if name == "Q" else name for name in FIT_SPECS[fit_id]["required"])
    optional = tuple(q if name == "Q" else name for name in FIT_SPECS[fit_id]["optional"])
    if fit_id == "presence" and q_form:
        optional += (q,)
    return required, optional


def rates_label(row):
    """A run row's rate set as "60/0.03", or "as configured" when it set no MESH_RATES."""
    rates = row.get("rates") or {}
    if rates.get("fps") is None:
        return "as configured"
    return f"{rates['fps']:g}/{rates['fixed_step_s']:g}"


def select_rows(run_rows, fit_id, rates=None):
    """The run rows a fit reads (ROW_SELECTION), at `rates` ("60/0.03") when given; summary, fit and gate rows are
    ignored."""
    chosen = []
    for row in run_rows:
        if row.get("row", "run") != "run" or (rates is not None and rates_label(row) != rates):
            continue
        if any(row.get("family") == family and (cases is None or row.get("case") in cases)
               and (arms is None or row.get("arm") in arms) for family, cases, arms in ROW_SELECTION[fit_id]):
            chosen.append(row)
    return chosen


def _check_pool(rows, columns, bridged):
    fields = [("rates", "rate sets", "fit one rate set at a time and convert with block K"),
              ("host", "hosts", "fit one machine at a time (step 8a decision 1)"),
              ("cpu", "CPUs", "fit one CPU at a time")]
    if not bridged:
        fields.append(("build", "builds", "pool builds only when report --bridge passes"))
    for field, what, advice in fields:
        values = list(dict.fromkeys(rates_label(r) if field == "rates" else str(r.get(field)) for r in rows))
        if len(values) > 1:
            count = "two" if len(values) == 2 else str(len(values))
            raise p0base.ModelError(f"rows from {count} {what} ({' and '.join(values)}): {advice}")
    for row in rows:
        env = row.get("env") or {}
        if row.get("capped") is False:
            raise p0base.ModelError(f"{p0geo.row_label(row)}: uncapped rows are not work at a rate set")
        if env.get("MESH_LOAD_FIGHT") == "0" and "O_steered_nofight" not in columns:
            raise p0base.ModelError(f"{p0geo.row_label(row)}: MESH_LOAD_FIGHT=0 rows need an O_steered_nofight "
                                    f"regressor, which this fit does not list")


def _pool(run_rows, fit_id, columns, rates, bridged):
    selected = select_rows(run_rows, fit_id, rates)
    accepted = [row for row in selected if row.get("accepted")]
    skipped = len(selected) - len(accepted)
    notes = [f"{skipped} selected rows not accepted, left out"] if skipped else []
    if not accepted:
        raise p0base.ModelError(f"the {fit_id} rows have no accepted run" + (f" at {rates}" if rates else ""))
    _check_pool(accepted, columns, bridged)
    cells = []
    for index, row in enumerate(accepted):
        for cell in p0geo.read_cells(row):
            x, note = p0geo.cell_regressors(row, cell)
            if note and note not in notes:
                notes.append(note)
            cells.append((index, row, cell, x, p0geo.metric(row, cell, "work_ms_per_s")))
    return accepted, cells, notes


def _work_rows(run_rows, fit_id, q_form, rates, bridged):
    required, optional = fit_columns(fit_id, q_form)
    columns = required + optional
    accepted, cells, notes = _pool(run_rows, fit_id, columns, rates, bridged)
    for _, row, cell, x, _ in cells:
        stray = [name for name, value in x.items() if value and name not in columns and name not in ("Q", "N2")]
        if stray:
            raise p0base.ModelError(f"{p0geo.row_label(row)}: cell {cell} has {', '.join(stray)}, which the "
                                    f"{fit_id} fit does not model")
    kept = []
    for name in columns:
        if any(x.get(name) for *_, x, _ in cells):
            kept.append(name)
        elif name in required:
            raise p0base.ModelError(f"the {fit_id} fit needs regressor '{name}', which is zero in every selected row")
        else:
            notes.append(f"regressor '{name}' is zero in every row: dropped")
    labels, unit, group_notes = run_groups(accepted)
    rows = [{name: x.get(name, 0.0) for name in kept} for *_, x, _ in cells]
    return (rows, [w for *_, w in cells], [index for index, *_ in cells], accepted, labels, unit,
            notes + group_notes)


def work_rows(run_rows, fit_id, q_form=None, rates=None, bridged=False):
    """(rows, y, groups, notes) for a fit: one row per accepted (run, read cell) of the fit's selection, y = that
    cell's work_ms_per_s, groups = (unit, label) per row ("repeat" and the effective repeat when balanced, else "run"
    and the run's index), notes (rows left out, dropped all-zero optional columns, E-labelled values, unbalanced
    repeats). Refuses mixed rate sets, hosts, CPUs or builds (unless bridged), uncapped rows, MESH_LOAD_FIGHT=0 rows
    without an O_steered_nofight regressor, a load the fit does not model and an all-zero required column."""
    rows, y, run_index, _, labels, unit, notes = _work_rows(run_rows, fit_id, q_form, rates, bridged)
    return rows, y, [(unit, labels[i]) for i in run_index], notes


def _add_ks(rows, y, names, groups, unit, ci):
    # k_hold/k_band intervals into ci; a note instead when O_steered is not positive in every fit (ratio_of would
    # refuse the whole fit, and the coefficients are still worth reporting).
    ks = {}
    for kind in KINDS if "O_steered" in names else ():
        if f"U_{kind}" in names:
            ks[f"k_hold_{kind}"] = ratio_of(f"U_{kind}", "O_steered")
            if "G_player" in names and kind != "inert":
                ks[f"k_band_{kind}"] = ratio_of((f"U_{kind}", "G_player"), "O_steered")
    if not ks:
        return None
    den = ci["O_steered"]
    fits = [("the full fit", den["value"])] + [(f"{unit} {key} left out", v)
                                               for key, v in zip(sorted(set(groups)), den["leave_one_out"])]
    bad = [(where, v) for where, v in fits if not v > 0]
    if bad:
        for name in ks:
            ci[name] = {"value": None, "se": None, "lo": None, "hi": None, "t": None, "leave_one_out": [],
                        "unbounded": True}
        return f"ratio undefined: denominator O_steered = {_fmt(bad[0][1])} ms/s in {bad[0][0]}"
    _, derived = jackknife(rows, y, names, groups, ks, unit)
    for name in ks:
        ci[name] = dict(derived[name], unbounded=den["lo"] <= 0)
    return None


def fit(run_rows, fit_id, q_form=None, rates=None, bridged=False):
    """One work-model fit (FIT_SPECS: presence, density, refit, floor) over mesh-p0/1 run rows: {fit_id, method,
    names, n, runs, groups, rates, cpu, build, reference, full, ci, runs_ci, runs_label, notes, warnings}. ci holds
    every coefficient and the derived k_hold_<kind> = U_kind/O_steered and k_band_<kind> = (U_kind + G_player)/O_steered
    (unbounded when O_steered's interval includes 0). With 2 repeats, runs_ci is the delete-one-run interval
    (RUNS_LABEL, information only, never a verdict). Refusals as work_rows and jackknife."""
    rows, y, run_index, accepted, labels, unit, notes = _work_rows(run_rows, fit_id, q_form, rates, bridged)
    names = list(rows[0])
    groups = [labels[i] for i in run_index]
    full, ci = jackknife(rows, y, names, groups, unit=unit)
    undefined = _add_ks(rows, y, names, groups, unit, ci)
    if undefined:
        notes.append(f"k unbounded: {undefined}")
    runs_ci = None
    if unit == "repeat" and len(set(groups)) == 2:
        _, runs_ci = jackknife(rows, y, names, run_index, unit="run")
        _add_ks(rows, y, names, run_index, "run", runs_ci)
    return {"fit_id": fit_id, "method": METHOD_REPEATS if unit == "repeat" else METHOD_RUNS, "names": names,
            "n": len(rows), "runs": len(accepted), "groups": len(set(groups)), "rates": rates_label(accepted[0]),
            "cpu": accepted[0].get("cpu"), "build": ", ".join(dict.fromkeys(str(r.get("build")) for r in accepted)),
            "reference": "O_steered" if "O_steered" in names else None, "full": full, "ci": ci,
            "runs_ci": runs_ci, "runs_label": RUNS_LABEL if runs_ci else None, "notes": notes,
            "warnings": list(full["warnings"])}


def predict(fit_result, regressors):
    """{value, se, lo, hi} of the fit's prediction for one set of regressor values, the interval from the prediction
    recomputed with each left-out group's coefficients. Refuses a load the fit does not model."""
    names, ci = fit_result["names"], fit_result["ci"]
    stray = [n for n, v in regressors.items() if v and n not in names and n not in ("Q", "N2")]
    if stray:
        raise p0base.ModelError(f"the {fit_result['fit_id']} fit does not model {', '.join(stray)}")
    value = math.fsum(ci[n]["value"] * regressors.get(n, 0.0) for n in names)
    g = len(ci[names[0]]["leave_one_out"])
    parts = [math.fsum(ci[n]["leave_one_out"][j] * regressors.get(n, 0.0) for n in names) for j in range(g)]
    mean = math.fsum(parts) / g
    se = math.sqrt((g - 1) / g * math.fsum((v - mean) ** 2 for v in parts))
    t = ci[names[0]]["t"]
    return {"value": value, "se": se, "lo": value - t * se, "hi": value + t * se}


def additivity_residual(fit_result, run_rows, rates=None, bridged=False):
    """Per (arm, point, cell) of the presence additivity rows: {arm, point, cell, runs, measured, predicted, residual,
    lo, hi}, residual = measured mean - the prediction of a fit made without them (fit_result), with that fit's
    jackknife interval. bridged pools two builds' additivity rows, as the fit it compares with did."""
    accepted, cells, _ = _pool(run_rows, "additivity", fit_result["names"], rates, bridged)
    points = {}
    for _, row, cell, x, w in cells:
        points.setdefault((row.get("arm"), json.dumps(row.get("point"), sort_keys=True), cell), []).append((x, w))
    out = []
    for (arm, point, cell), items in points.items():
        mean_x = {name: math.fsum(x.get(name, 0.0) for x, _ in items) / len(items) for name in items[0][0]}
        measured = math.fsum(w for _, w in items) / len(items)
        predicted = predict(fit_result, mean_x)
        out.append({"arm": arm, "point": json.loads(point), "cell": cell, "runs": len(items), "measured": measured,
                    "predicted": predicted["value"], "residual": measured - predicted["value"],
                    "lo": measured - predicted["hi"], "hi": measured - predicted["lo"]})
    return out


def arm_difference(a_rows, b_rows):
    """{cell: jackknife interval} of arm A's work ms/s minus arm B's, paired by effective repeat over accepted rows
    (the animation-off comparison). Refuses fewer than 2 shared repeats on a cell."""
    def by_repeat(rows):
        out = {}
        for row in rows:
            if row.get("accepted"):
                for cell in p0geo.read_cells(row):
                    out.setdefault(cell, {}).setdefault(effective_repeat(row), []).append(
                        p0geo.metric(row, cell, "work_ms_per_s"))
        return out
    a, b = by_repeat(a_rows), by_repeat(b_rows)
    result = {}
    for cell in sorted(set(a) & set(b)):
        repeats = sorted(set(a[cell]) & set(b[cell]))
        diffs = [math.fsum(a[cell][r]) / len(a[cell][r]) - math.fsum(b[cell][r]) / len(b[cell][r]) for r in repeats]
        try:
            _, ci = jackknife([{"difference": 1.0}] * len(repeats), diffs, ["difference"], repeats)
        except p0base.ModelError as ex:
            raise p0base.ModelError(f"arm difference on cell {cell}: {ex}") from ex
        result[cell] = dict(ci["difference"], repeats=repeats)
    return result


def n2_form(run_rows, rates=None, bridged=False):
    """{form, sse, aic, other}: fits c0 + a·N + q·Q and c0 + a·N + q·N² over the density base rows and keeps the lower
    AIC (n·ln(SSE/n) + 2p); ties keep qQ, and an SSE below float resolution (SSE_RESOLUTION of the work's RMS) counts
    as that resolution, so two exact fits tie. Refusals as work_rows."""
    forms = {}
    for form in ("qQ", "qN2"):
        rows, y, _, _ = work_rows(run_rows, "density", form, rates, bridged)
        result = least_squares(rows, y, list(rows[0]))
        sse = math.fsum(e * e for e in result["residuals"])
        resolved = max(sse, SSE_RESOLUTION ** 2 * math.fsum(v * v for v in y))
        forms[form] = {"form": form, "sse": sse,
                       "aic": result["n"] * math.log(resolved / result["n"]) + 2 * result["p"]}
    best, other = ("qN2", "qQ") if forms["qN2"]["aic"] < forms["qQ"]["aic"] else ("qQ", "qN2")
    return dict(forms[best], other=forms[other])


def n2_removed(base_dense, arm_dense, base_sparse, arm_sparse):
    """The share of the excess over linear a removal arm takes away: [(base_dense - arm_dense) - (base_sparse -
    arm_sparse)] / (base_dense - base_sparse). Refuses an excess <= 0."""
    excess = base_dense - base_sparse
    if not excess > 0:
        raise p0base.ModelError(f"no excess over linear to remove: dense base {base_dense:g} - sparse base "
                                f"{base_sparse:g} = {excess:g} ms/s")
    return ((base_dense - arm_dense) - (base_sparse - arm_sparse)) / excess


def band_slope(points):
    """(full, ci) of the halves/coarse ratio against B/600 over (repeat, band, ratio) points, jackknifed over repeat;
    ci["slope"] is the measured band slope G-GEO (c) compares with."""
    rows = [{"intercept": 1.0, "slope": band / p0geo.GROUND_M} for _, band, _ in points]
    return jackknife(rows, [ratio for *_, ratio in points], ["intercept", "slope"], [rep for rep, *_ in points])
