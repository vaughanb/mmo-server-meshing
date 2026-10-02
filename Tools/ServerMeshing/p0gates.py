"""Phase 0 gates (step 4): stage 0a's gate rows, the frozen abgeo predictions and the map-size table, from the run rows
and the fits p0report made. Pure: everything comes in as arguments. Split out of p0report (the ~500-line rule): the
contract's evaluate_gates, which left p0model for the same reason.

    p0gates.evaluate_gates(runs, fits, predictions)   # gate rows: G3-F (U), kill, N², G-GEO (a, b, c), floor, drift
    p0gates.predict_abgeo(runs, refit, points, layouts, counts, created_utc)   # frozen prediction rows
    p0gates.map_size(refit, presence, floors)         # the map-size table's Markdown lines

fits = {"presence": {rates: p0model.fit result}, "refit": result or None, "floors": {rates: result}}. Each gate row
states its thresholds in ms/s and ms/frame at the stated rate where it has one (p0base.threshold_text); a gate the rows
cannot decide gives NO VERDICT and names what it needs. Monster counts for predictions are measured in abgeo rows (M),
else from the 0.71 chain3 reports (M, older build), else estimated from the grid's owned monsters by area (E).
Unit tests: python -m unittest discover -s Tools/ServerMeshing -p "test_*.py" (test_p0report)
"""
import datetime
import hashlib
import json
import math

import p0base
import p0geo
import p0markdown
import p0model
import p0results

REFIT_RATES = "60/0.03"
GATE_RATES = ("60/0.03", "30/0.02")          # G3-F and the kill rule are named at both fantasy rate sets
FLOOR_RATES = ("30/0.02", "60/0.03")
ATTRIBUTION_POINT, SPARSE_POINT = (200, 141.42), (200, 600)   # 100 m² per character; the sparse controls
REMOVALS = ("inert", "no-fight", "kinematic")
G3F_K, KILL_MS_S, KILL_PUPPETS, FLOOR_MS_S, N2_SHARE = 0.5, 600.0, 500, 600.0, 0.80
GEO_RATIO, GEO_TOLERANCE, GEO_MIN_RUNS, GEO_BAND = 0.75, 0.05, 2, 80.0
DRIFT_SHARE, DEFAULT_BAND = 0.05, 80.0
# DESIGN 0.71's monster costs, ms per frame (label E): a prediction's monster coefficients when the refit has none.
MONSTER_MS_071, MONSTER_PUPPET_MS_071 = 0.0037, 0.0087
MAP_SIDES, MAP_BANDS = (300, 350, 500, 1000, 2000, 4000, 8000), (50, 80, 120)
O_HALF = "the O half (observed puppets) needs 0b (step 5)"


def mean(values):
    """The mean of the values, or None for none."""
    return math.fsum(values) / len(values) if values else None


def fps_of(rates):
    """The frame rate of a rate set label ("60/0.03" -> 60.0)."""
    return float(rates.split("/")[0])


def ci_text(ci):
    """An interval as "value [lo, hi]", or "unbounded" for a ratio whose denominator's interval includes 0."""
    if ci is None or ci.get("value") is None:
        return "unbounded"
    return f"{ci['value']:.5g} [{ci['lo']:.5g}, {ci['hi']:.5g}]"


def chosen(runs, family, cases=None, arms=None, rates=None):
    """The accepted run rows of a family, limited to the cases, arms and rate set given."""
    return [r for r in runs if r.get("family") == family and r.get("accepted")
            and (cases is None or r.get("case") in cases) and (arms is None or r.get("arm") in arms)
            and (rates is None or p0model.rates_label(r) == rates)]


def rate_sets(rows):
    """The rows' rate sets ("60/0.03", or "as configured"), sorted."""
    return sorted({p0model.rates_label(r) for r in rows})


def at_point(row, players, side=None):
    """Whether a run row's point has these players (and this crowd side, to 0.01 m, when given)."""
    point = row.get("point") or {}
    return point.get("players") == players and (side is None or abs(float(point.get("side", -1)) - side) < 0.01)


def header(runs, rows, family, game=None):
    """The header subset for a row made from rows (all runs when rows is empty), with family and game set."""
    head = p0results.header_of(rows or runs, family=family)
    return dict(head, game=game) if game is not None else head


def _gate(gates, runs, rows, name, family, verdict, inputs, rule, notes, case=None, key_metric=None):
    head = header(runs, rows, family, p0results.FAMILY_GAME.get(family))
    gates.append(p0results.gate_row(name, verdict, inputs, rule, list(notes), head, case, key_metric))


def _presence_gates(gates, runs, presence):
    k_rule = f"k_hold_kinematic = e_kinematic / a_steered ≤ {G3F_K:g} (a ratio: no ms/s or ms/frame form)"
    for rates in sorted(set(presence) | set(GATE_RATES)):
        rows, result = p0model.select_rows(runs, "presence", rates), presence.get(rates)
        kill_rule = f"c0 + λ + {KILL_PUPPETS}·e_kinematic ≥ {p0base.threshold_text(KILL_MS_S, fps_of(rates))} kills"
        g3f, kill = f"G3-F (U) @ {rates}", f"Kill, characters (U) @ {rates}"
        if result is None:
            need = "needs p0load presence" + (f" --rates {rates}" if rates != REFIT_RATES else "")
            _gate(gates, runs, rows, g3f, "presence", p0base.NO_VERDICT, {}, k_rule, [need, O_HALF], "main")
            _gate(gates, runs, rows, kill, "presence", p0base.NO_VERDICT, {}, kill_rule, [need], "main")
            continue
        k = result["ci"].get("k_hold_kinematic")
        _gate(gates, runs, rows, g3f, "presence", p0base.ci_gate(k, G3F_K), {"k_hold_kinematic": k}, k_rule,
              [O_HALF] + result["notes"], "main", f"k_hold_kinematic {ci_text(k)}")
        stat = p0model.predict(result, {"c0": 1.0, "links": 1.0, "U_kinematic": float(KILL_PUPPETS)})
        verdict, notes = p0base.kill_verdict(stat["lo"], stat["hi"], KILL_MS_S), []
        every = [r for r in runs if r.get("family") == "presence" and p0model.rates_label(r) == rates]
        saturated = [f"{p0geo.row_label(r)}: {r['excluded_reason']}" for r in every
                     if (r.get("env") or {}).get("MESH_PUPPET_MOTION") == "kinematic"
                     and "saturated" in (r.get("excluded_reason") or "")
                     and any(f" on {cell}" in r["excluded_reason"]
                             for cell in (("E", "C") if r.get("case") == "additivity" else ("E",)))]
        if saturated:
            verdict, notes = p0base.KILL, ["a saturated puppet cell triggers the kill rule: " + "; ".join(saturated)]
        _gate(gates, runs, rows, kill, "presence", verdict, {"statistic": stat}, kill_rule, notes, "main",
              f"c0 + λ + {KILL_PUPPETS}·e_kin {ci_text(stat)} ms/s")


def _n2_shares(runs, rates):
    def work(case, arm, point):
        values = [p0geo.metric(r, "C", "work_ms_per_s")
                  for r in chosen(runs, "density", (case,), (arm,), rates) if at_point(r, *point)]
        return mean(values)
    base_dense, base_sparse = work("scale", "base", ATTRIBUTION_POINT), work("grid", "base", SPARSE_POINT)
    shares, missing = {}, []
    for arm in REMOVALS:
        dense, sparse = work("attribution", arm, ATTRIBUTION_POINT), work("sparse", arm, SPARSE_POINT)
        if None in (base_dense, base_sparse, dense, sparse):
            missing.append(f"{arm}: needs the dense and sparse base and arm runs")
        else:
            shares[arm] = p0model.n2_removed(base_dense, dense, base_sparse, sparse)
    return shares, missing


def _n2_gate(gates, runs):
    rule = (f"the best removal arm takes away ≥ {N2_SHARE:.0%} of the excess over linear at {ATTRIBUTION_POINT[0]} @ "
            f"{ATTRIBUTION_POINT[1]:g} m (100 m² per character; a share: no ms/s form); the profiler half is pending")
    labels = rate_sets(chosen(runs, "density", ("attribution", "sparse")))
    if not labels:
        _gate(gates, runs, [], "N²", "density", p0base.NO_VERDICT, {}, rule,
              ["needs p0load density", "profiler pending"], "attribution")
    for rates in labels:
        try:
            shares, notes = _n2_shares(runs, rates)
            verdict = f"{p0base.n2_verdict(shares, N2_SHARE)} (profiler pending)" if shares else p0base.NO_VERDICT
        except p0base.ModelError as ex:
            shares, notes, verdict = {}, [str(ex)], p0base.NO_VERDICT
        best = max(shares, key=shares.get) if shares else None
        _gate(gates, runs, chosen(runs, "density", rates=rates), f"N² @ {rates}", "density", verdict,
              {"shares": shares}, rule, notes + ["profiler pending"], "attribution",
              f"best removal {best} {shares[best]:.2f}" if best else None)


def point_id(case, point):
    """An abgeo point's identity (case, players, monsters, band, puppets, load): the same for a run row's point and
    a prediction row's point (band 0 = as configured)."""
    return (case, point.get("players"), point.get("monsters"), round(float(point.get("band") or 0.0), 3),
            point.get("puppets") or "", point.get("load") or "spread")


def abgeo_points(runs):
    """{point_id: (point, {layout: {repeat: the busiest read cell's work ms/s}})} over accepted abgeo rows."""
    points = {}
    for row in chosen(runs, "abgeo"):
        peak = max(p0geo.metric(row, cell, "work_ms_per_s") for cell in p0geo.read_cells(row))
        entry = points.setdefault(point_id(row.get("case"), row.get("point") or {}), (row.get("point") or {}, {}))
        entry[1].setdefault(row.get("arm"), {})[row.get("repeat")] = peak
    return points


def pair_ratios(layouts, cut, base):
    """{repeat: the cut's busiest cell over the base's} for the repeats both layouts ran (abgeo_points' values)."""
    a, b = layouts.get(cut, {}), layouts.get(base, {})
    return {rep: a[rep] / b[rep] for rep in sorted(set(a) & set(b)) if b[rep] > 0}


def _geo_a_gate(gates, runs, points):
    rule = f"strips4/halves ≤ {GEO_RATIO:g} in every repeat at B = {GEO_BAND:g}, kinematic, spread (at least 2 pairs)"
    found = False
    for key, (point, layouts) in points.items():
        ratios = pair_ratios(layouts, "strips4", "halves")
        if (key[3] or DEFAULT_BAND) != GEO_BAND or key[4] != "kinematic" or key[5] != "spread" or not ratios:
            continue
        found, values = True, list(ratios.values())
        _gate(gates, runs, chosen(runs, "abgeo", (key[0],)), "G-GEO (a)", "abgeo",
              p0base.point_verdict_eps(values, GEO_RATIO, GEO_MIN_RUNS), {"point": point, "ratios": ratios}, rule,
              [], key[0], "strips4/halves " + " ".join(f"{v:.2f}" for v in values))
    if not found:
        _gate(gates, runs, [], "G-GEO (a)", "abgeo", p0base.NO_VERDICT, {}, rule, ["needs p0load abgeo (b9-bands)"])


def frozen_set(predictions, runs):
    """(created_utc, rows) of the prediction set G-GEO (b) judges: the newest set (rows sharing a created_utc) made at
    or before the earliest started_utc of the accepted abgeo rows, so a set made later, from a refit that may already
    hold those rows, never replaces the frozen one; the newest set when no abgeo run has started; (None, []) when
    every set was made after the first abgeo run."""
    sets = {}
    for p in predictions:
        sets.setdefault(p.get("created_utc") or "", []).append(p)
    starts = [datetime.datetime.fromisoformat(r["started_utc"]) for r in chosen(runs, "abgeo") if r.get("started_utc")]
    first = min(starts, default=None)
    made = [created for created in sets
            if first is None or (created and datetime.datetime.fromisoformat(created) <= first)]
    if not made:
        return None, []
    newest = max(made)
    return newest, sets[newest]


def _point_text(point):
    return (f"{point.get('case')} {point.get('pair')} at {point.get('players')} players, monsters "
            f"{point.get('monsters')}, B {point.get('band') or 'as configured'}")


def _geo_b_gate(gates, runs, points, predictions):
    rule = f"|predicted - measured mean| ≤ {GEO_TOLERANCE:g} at every predicted point (a ratio: no ms/s form)"
    if not predictions:
        _gate(gates, runs, [], "G-GEO (b)", "abgeo", p0base.NO_VERDICT, {}, rule,
              ["no frozen predictions: p0load report --predict abgeo before p0load abgeo"])
        return
    newest, frozen = frozen_set(predictions, runs)
    if newest is None:
        latest = max(p.get("created_utc") or "" for p in predictions)
        _gate(gates, runs, chosen(runs, "abgeo"), "G-GEO (b)", "abgeo", p0base.NO_VERDICT, {}, rule,
              ["predictions made after the first abgeo run: not frozen",
               f"{len(predictions)} prediction rows, the newest made {latest}"])
        return
    pairs, inputs, unmatched = [], [], []
    for p in frozen:
        point = p["point"] or {}
        cut, _, base = (point.get("pair") or "").partition("/")
        entry = points.get(point_id(point.get("case"), point))
        ratios = pair_ratios(entry[1], cut, base) if entry else {}
        if ratios:
            pairs.append((p["ratio"], mean(list(ratios.values()))))
            inputs.append({"point": point, "predicted": pairs[-1][0], "measured": pairs[-1][1]})
        else:
            unmatched.append(_point_text(point))
    notes = [f"{len(frozen)} predictions made {newest}"]
    if unmatched:
        notes.append(f"{len(unmatched)} of {len(frozen)} predicted points had no accepted pair: {'; '.join(unmatched)}")
    verdict = p0base.NO_VERDICT
    if not pairs:
        notes.append("no abgeo runs match a predicted point")
    else:
        verdict = p0base.geo_prediction_verdict(pairs, GEO_TOLERANCE)
        if unmatched and verdict == p0base.PASS:
            verdict = p0base.INSUFFICIENT   # a PASS needs every predicted point judged; a miss already FAILs
    worst = max((abs(a - b) for a, b in pairs), default=None)
    _gate(gates, runs, chosen(runs, "abgeo"), "G-GEO (b)", "abgeo", verdict, {"points": inputs}, rule, notes, None,
          f"worst |predicted - measured| {worst:.3f}" if worst is not None else None)


def coefficients(result):
    """(coef, notes): a fit's coefficients plus DESIGN 0.71's monster costs (label E) for the ones it lacks, in ms/s
    at the fit's frame rate."""
    coef, notes = {name: result["ci"][name]["value"] for name in result["names"]}, []
    for name, ms in (("O_monster", MONSTER_MS_071), ("U_monster_steered", MONSTER_PUPPET_MS_071),
                     ("U_monster_kinematic", MONSTER_PUPPET_MS_071)):
        if name not in coef:
            coef[name] = ms * fps_of(result["rates"])
            notes.append(f"{name} {ms:g} ms/frame from DESIGN 0.71 (E)")
    return coef, notes


def _monster_counts(runs, layout, band, layouts, fallback):
    def with_monsters(arm):
        return [r for r in chosen(runs, "abgeo", arms=(arm,)) if (r.get("point") or {}).get("monsters") == "all"]

    def cell_means(rows, keys):
        return {cid: {name: mean([p0geo.window_mean(r, cid, key) for r in rows]) for name, key in keys}
                for cid in p0geo.read_cells(rows[0])}

    rows = [r for r in with_monsters(layout) if float(r["point"].get("band") or DEFAULT_BAND) == band]
    if rows:
        keys = (("O_monster", "owned_monsters"), ("U_monster", "puppet_monsters"))
        return cell_means(rows, keys), f"{layout} B {band:g}: measured monster counts (M)"
    if (layout, band) in (fallback or {}):
        return (fallback[(layout, band)],
                f"{layout} B {band:g}: monster counts from the 0.71 chain3 reports (M, older build)")
    grid = with_monsters("grid")
    if grid:
        owned = {cid: c["O_monster"] for cid, c in cell_means(grid, (("O_monster", "owned_monsters"),)).items()}
    else:
        owned = next(({cid: c["O_monster"] for cid, c in counts.items()}
                      for (name, _), counts in (fallback or {}).items() if name == "grid"), None)
    if owned is None:
        raise p0base.ModelError(f"no monster counts for {layout} at B {band:g} (none measured, no grid counts)")
    return (p0geo.monster_counts_from_grid(owned, layouts[layout], band),
            f"{layout} B {band:g}: monster counts from the grid's owned monsters by area (E)")


def predicted_ratio(coef, point, layouts, runs, fallback):
    """(ratio, labels): p0geo.predict_ratio for one abgeo point {pair "cut/base", players, monsters, band (0 = as
    configured), puppets} with the rig's steered owners; monsters all take the counts _monster_counts finds. Raises
    p0base.ModelError for a layout not given or monsters without counts."""
    cut, _, base = point["pair"].partition("/")
    for name in (cut, base):
        if name not in (layouts or {}):
            raise p0base.ModelError(f"layout {name} is not among the layouts given")
    band, labels, counts = float(point.get("band") or DEFAULT_BAND), [], None
    if point["monsters"] != "off":
        found = [_monster_counts(runs, name, band, layouts, fallback) for name in (cut, base)]
        counts, labels = (found[0][0], found[1][0]), [found[0][1], found[1][1]]
    ratio = p0geo.predict_ratio(coef, layouts[cut], layouts[base], band, point["players"], 1 if counts else 0,
                                "steered", point.get("puppets") or "steered", counts)
    return ratio, labels


def _predicted_slope(refit, bands, layouts, runs, fallback):
    coef, notes = coefficients(refit)
    xs, ys = [], []
    for band, point in sorted(bands, key=lambda entry: entry[0]):
        ratio, labels = predicted_ratio(coef, dict(point, pair="halves/coarse"), layouts, runs, fallback)
        xs.append(band / p0geo.GROUND_M)
        ys.append(ratio)
        notes += [label for label in labels if label not in notes]
    mx, my = mean(xs), mean(ys)
    return math.fsum((x - mx) * (y - my) for x, y in zip(xs, ys)) / math.fsum((x - mx) ** 2 for x in xs), notes


def _geo_c_gate(gates, runs, points, refit, layouts, fallback):
    rule = "the predicted band slope lies inside the measured slope's 95 % interval, ends included (no ms/s form)"
    groups = {}
    for key, (point, lay) in points.items():
        if key[3] and "halves" in lay and "coarse" in lay:
            groups.setdefault(key[:3] + key[4:], []).append((key[3], point, lay))
    found = False
    for key, bands in groups.items():
        if len(bands) < 2:
            continue
        found = True
        measured = [(rep, band, ratio) for band, _, lay in bands
                    for rep, ratio in pair_ratios(lay, "halves", "coarse").items()]
        try:
            if refit is None:
                raise p0base.ModelError(f"no refit at {REFIT_RATES} to predict the slope with")
            _, ci = p0model.band_slope(measured)
            slope, notes = _predicted_slope(refit, [(b, p) for b, p, _ in bands], layouts, runs, fallback)
            verdict = p0base.geo_slope_verdict(slope, ci["slope"]["lo"], ci["slope"]["hi"])
            inputs, text = {"predicted": slope, "measured": ci["slope"]}, f"slope {slope:.3f} in {ci_text(ci['slope'])}"
        except p0base.ModelError as ex:
            verdict, inputs, text, notes = p0base.NO_VERDICT, {}, None, [str(ex)]
        _gate(gates, runs, chosen(runs, "abgeo", (key[0],)), "G-GEO (c)", "abgeo", verdict, inputs, rule, notes,
              key[0], text)
    if not found:
        _gate(gates, runs, [], "G-GEO (c)", "abgeo", p0base.NO_VERDICT, {}, rule, ["needs p0load abgeo (b9-bands)"])


def _floor_gates(gates, runs):
    for rates in sorted(set(rate_sets(chosen(runs, "floor"))) | set(FLOOR_RATES)):
        rows = chosen(runs, "floor", ("cell",), ("idle",), rates)
        rule = f"idle C work ≤ {p0base.threshold_text(FLOOR_MS_S, fps_of(rates))}"
        if not rows:
            _gate(gates, runs, [], f"Floor @ {rates}", "floor", p0base.NO_VERDICT, {}, rule, ["needs p0load floor"])
            continue
        work = [p0geo.metric(r, "C", "work_ms_per_s") for r in rows]
        _gate(gates, runs, rows, f"Floor @ {rates}", "floor", p0base.floor_verdict(mean(work), FLOOR_MS_S),
              {"work": work}, rule, [], None, f"idle C {mean(work):.1f} ms/s")


def _plan_point(row):
    """A run row's planned point: its plan_key, or its family, case, arm and point when it has none."""
    return row.get("plan_key") or (row.get("family"), row.get("case"), row.get("arm"),
                                   json.dumps(row.get("point") or {}, sort_keys=True))


def _drift_gate(gates, runs):
    """A drifted canary already excludes the runs before it (p0results.apply_drift), so the gate fails only while a
    point that lost runs to a drift has no accepted run since; drifts that cost no kept run are notes."""
    rule = (f"|w - baseline|/baseline ≤ {DRIFT_SHARE:.0%} on C's Work ticks work ms/s (30/0.02 canary); a drifted "
            "canary excludes the runs before it, and the gate fails while an excluded point has no accepted re-run")
    checks = [r for r in runs if r.get("family") == "canary" and r.get("arm") == "check" and "drift" in r]
    judged = [r for r in checks if r["drift"] is not None]
    drifted = [(r.get("window_local") or {}).get("end") for r in judged if r["drift"]]
    excluded = [r for r in runs if r.get("excluded_reason") == "drift"]
    measured = {_plan_point(r) for r in runs if p0results.row_type(r) == "run" and r.get("accepted")
                and r.get("family") != "canary"}
    missing = {_plan_point(r): r for r in excluded if _plan_point(r) not in measured}
    notes = [f"{len(judged)} of {len(checks)} check canaries judged, {len(drifted)} drifted; {len(excluded)} rows "
             f"excluded, {len(missing)} of their points not re-measured"]
    notes += [f"not re-measured: {r.get('family')} {r.get('case')} {r.get('arm')} {json.dumps(r.get('point') or {})}"
              for r in missing.values()]
    verdict = p0base.pass_fail(not missing) if judged else p0base.NO_VERDICT
    _gate(gates, runs, checks, "Canary drift", "canary", verdict, {"drifted": drifted, "not_remeasured": len(missing)},
          rule, notes, None, f"{len(drifted)} drifted, {len(missing)} points not re-measured")


def evaluate_gates(runs, fits, predictions, layouts=None, monster_counts=None):
    """Stage 0a's gate rows, in this order: G3-F (U) and the kill rule per fantasy rate set (from fits["presence"]),
    N² per density rate set, G-GEO (a) per strips4/halves point at B 80, G-GEO (b) against frozen_set's prediction
    rows (PASS only when every predicted point has an accepted pair; INSUFFICIENT when some have none and the judged
    ones pass), G-GEO (c) per band series (the refit's predicted slope; needs layouts), the floor per rate set, canary
    drift. runs are drift-applied run rows."""
    gates, points = [], abgeo_points(runs)
    _presence_gates(gates, runs, fits["presence"])
    _n2_gate(gates, runs)
    _geo_a_gate(gates, runs, points)
    _geo_b_gate(gates, runs, points, predictions)
    _geo_c_gate(gates, runs, points, fits["refit"], layouts, monster_counts)
    _floor_gates(gates, runs)
    _drift_gate(gates, runs)
    return gates


def inputs_sha(runs):
    """sha1 of the plan keys and cell work of the accepted rows the refit reads (a prediction row's inputs_sha)."""
    items = sorted((r.get("plan_key") or "", m["cell"], m["value"])
                   for r in p0model.select_rows(runs, "refit", REFIT_RATES) if r.get("accepted")
                   for m in r.get("metrics") or [] if m["name"] == "work_ms_per_s" and m["stat"] == "mean")
    return hashlib.sha1(json.dumps(items).encode("utf-8")).hexdigest()


def predict_abgeo(runs, refit, points, layouts, monster_counts, created_utc):
    """Frozen prediction rows, one per abgeo point (predicted_ratio with the refit's coefficients), each with
    inputs_sha, created_utc and notes naming the E and M labels. Raises p0base.ModelError without a refit or when any
    point cannot be predicted (a frozen set with holes would leave G-GEO (b) points unjudged)."""
    if refit is None:
        raise p0base.ModelError(f"no refit at {REFIT_RATES} to predict from")
    coef, notes = coefficients(refit)
    head, sha = header(runs, p0model.select_rows(runs, "refit", REFIT_RATES), "abgeo", "fantasy"), inputs_sha(runs)
    rows = []
    for point in points:
        ratio, labels = predicted_ratio(coef, point, layouts, runs, monster_counts)
        rows.append(dict(p0results.prediction_row(dict(point), ratio, sha, created_utc, head), notes=notes + labels))
    return rows


def map_size(refit, presence, floors):
    """The map-size table's Markdown lines: an inputs line, then p0markdown.map_size_markdown over sides 0.3-8 km x B
    {50, 80, 120} x k_band_kinematic (k̂, lo, hi) from the refit (else presence at 60/0.03), with x, link_share and V
    from p0geo.split_inputs and c0 and λ from the floor fit at that rate set. A k that is not above 0 (a W* of 0: every
    cell would halve down to B) is left out and named; a table that cannot be built is one 'not built: <reason>'
    line."""
    source = next((f for f in (refit, presence.get(REFIT_RATES)) if f is not None
                   and (f["ci"].get("k_band_kinematic") or {}).get("value") is not None), None)
    if source is None:
        return ["not built: no refit or presence fit gives k_band_kinematic"]
    k, floor = source["ci"]["k_band_kinematic"], floors.get(source["rates"])
    if not p0base.above(k["value"], 0.0):
        return [f"not built: k_band_kinematic {ci_text(k)} is not above 0"]
    notes = [f"k_band_kinematic {ci_text(k)} from {source['fit_id']} @ {source['rates']}",
             f"c0 and λ from floor @ {source['rates']}" if floor else "c0 and λ from the fit itself (no floor fit)"]
    ks = [(k["value"], k["lo"], k["hi"])]
    if not p0base.above(k["lo"], 0.0):
        ks = [k["value"], k["hi"]]
        notes.append(f"k's lower bound {k['lo']:.3f} is not above 0: its rows are left out (W* would be 0)")
    try:
        inputs = p0geo.split_inputs(source["ci"], source["rates"], fit_rates=source["rates"],
                                    floor={"c0": floor["ci"]["c0"]["value"], "lam": floor["ci"]["links"]["value"]}
                                    if floor else None)
        table = p0geo.map_size_table(MAP_SIDES, MAP_BANDS, ks, inputs["x"], inputs["link_share"], c0=inputs["c0"],
                                     lam=inputs["lam"], load=inputs["V"])
    except p0base.ModelError as ex:
        return [f"not built: {ex}"]
    notes.append(f"x {inputs['x']:.4f}, link_share {inputs['link_share']:.4f}, V {inputs['V']:.1f} ms/s, c0 "
                 f"{inputs['c0']:.1f} ms/s, λ {inputs['lam']:.2f} ms/s")
    return ["; ".join(notes), ""] + p0markdown.map_size_markdown(table).splitlines()
