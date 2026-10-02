"""Phase 0 report (step 4): stage 0a's results from mesh-p0/1 rows, as one pure function; p0load reads the files and
writes what it returns. Pure: rows, layouts and monster counts come in as arguments; nothing here reads a file or the
clock.

    result = p0report.build_report(rows, "20260928_120000")
    result["files"]      # {"report_<stamp>.md": text, "rig_<game>_fit_<stamp>.jsonl": rows, ...}
    p0report.chain3_monster_counts(texts)   # per-cell monster counts from saved 0.71 abload reports (label M)

Order (the step 4 contract's §H report): canary drift (p0results.apply_drift); the latest accepted row of each (build,
plan_key) (a planned run made twice counts once); the rig team's rows (other teams' imported rows are only summarised),
one build of them with --build; the build check (two rig builds pool only with --bridge and p0results.bridge_ok);
summaries; the fits: block K's rates, density and the N² form (fixed before any
grid is fitted), floor and λ, presence (with q only when density's q interval excludes 0), the refit at 60/0.03; the
0a gates (p0gates); the frozen abgeo predictions (--predict abgeo); the map-size table. Only run rows are fitted:
summary, fit and gate rows of earlier reports are ignored, and prediction rows feed only G-GEO (b). A fit the rows
cannot make is printed as "not fitted: <reason>" and the gates that need it give NO VERDICT with that reason.
Unit tests: python -m unittest discover -s Tools/ServerMeshing -p "test_*.py"
"""
import json
import re

import p0base
import p0gates
import p0geo
import p0markdown
import p0model
import p0rates
import p0results

REFIT_RATES = p0gates.REFIT_RATES
RATE_ARMS, RATE_KINDS = ("idle", "steered", "kinematic", "inert"), ("steered", "kinematic", "inert")
ANIMATION_OFF_POINT, PUPPET_ARMS = 200, ("puppets-steered", "puppets-kinematic")
BRIDGE_053_MS = 47.1     # 0.53's C at 500 players in 200 m, uncapped at 60 fps: ms per measured frame
PENDING_GATES = (("G1-F", "0b (steps 5-6)"), ("G2-F", "0b (steps 5-6)"), ("G3-F (O)", "0b (step 5)"),
                 ("Crossings (B10)", "0b (step 5)"), ("Database", "0c (step 9)"),
                 ("Phantom validity (J)", "0b (step 5)"),
                 ("space gates (G1-S, G2-S, G3-S, kill for ships)", "0b (step 7)"))
FIT_ORDER = ("rates: block K's rate decomposition per arm, then the per-entity split per kind",
             "density: n2_form (q·Q or q·N²), then the density fit",
             "floor: c0 and λ per rate set (2 repeats: the t(1) interval, runs as groups beside it)",
             "presence: per rate set, with q only when density's q interval excludes 0; additivity; animation-off",
             f"refit: presence + density + abgeo rows at {REFIT_RATES}",
             "gates (G3-F (U), kill, N², G-GEO (a, b, c), floor, canary drift), --predict abgeo, the map-size table")
FIT_COLUMNS = ("Fit", "§5 name", "Regressor", "Value [95 % CI]", "t", "n", "Method", "Runs as groups")
GATE_COLUMNS = ("Gate", "Game", "Case", "Verdict", "Key metric", "Rule", "Notes")
AB_BAND = re.compile(r"^=== static A/B, capped, load \S+, band (?P<band>as configured \(80 m\)|[\d.]+),")
AB_RUN = re.compile(r"^--- \d+ players, monsters (?P<monsters>\w+), repeat \d+ \(seed \d+\), (?P<layout>\w+) \(")
AB_CELL = re.compile(r"(?P<cell>\S+) [\d.]+ ms sat \d+% own \d+p/(?P<own>\d+)m pup \d+p/(?P<pup>\d+)m")


class ReportError(ValueError):
    """ReportError is a report the rows cannot make: no run rows, builds pooled without a passing --bridge, a --build
    naming no single build."""


def _table(columns, rows):
    def line(cells):
        return "| " + " | ".join(str(cell).replace("|", "\\|") for cell in cells) + " |"
    return [line(columns), "|" + "---|" * len(columns)] + [line(row) for row in rows]


def _add_fit(out, fit_id, name, ci, regressors, n, head, method, notes, reference=None, runs_ci=None):
    row = p0results.fit_row(fit_id, name, ci, regressors, n, method, reference, head, notes)
    if runs_ci is not None:
        row["runs_ci"] = {"lo": runs_ci["lo"], "hi": runs_ci["hi"], "label": p0model.RUNS_LABEL}
    out["fits"] += [row] + [dict(row, name=alias) for alias in p0results.section5_names(name)[1:]]
    t = "–" if ci.get("t") is None else f"{ci['t']:.3f}"
    other = f"{p0model.RUNS_LABEL}: {p0gates.ci_text(runs_ci)}" if runs_ci is not None else "–"
    out["fit_lines"].append([fit_id, row["name"], name, p0gates.ci_text(ci), t, n, method, other])


def _fit_rows(out, fit_id, result, rows, notes=()):
    head = p0gates.header(out["runs"], rows, "fit")
    notes = list(notes) + result["notes"] + result["warnings"]
    reference = p0results.FIT_NAMES.get(result["reference"]) if result["reference"] else None
    for name, ci in result["ci"].items():
        _add_fit(out, fit_id, name, ci, result["names"], result["n"], head, result["method"], notes,
                 reference if name.startswith("k_") else None, (result["runs_ci"] or {}).get(name))


def _rates_fits(out, runs):
    rows = [r for r in runs if r.get("family") == "rates"]
    if not rows:
        out["fit_notes"].append("rates (block K): no rows (p0load rates)")
        return
    points = {}
    for arm in RATE_ARMS:
        arm_rows, fit_id = [r for r in rows if r.get("arm") == arm], f"rates {arm}"
        try:
            pts, notes = p0rates.rate_points(arm_rows)
            full, ci = p0rates.rate_decomposition(pts)
        except p0base.ModelError as ex:
            out["fit_notes"].append(f"{fit_id}: not fitted: {ex}")
            continue
        points[arm] = (pts, arm_rows)
        notes += full["notes"] + full["warnings"]
        head = p0gates.header(runs, arm_rows, "fit")
        for name, entry in ci.items():
            _add_fit(out, fit_id, name, entry, p0rates.RATE_NAMES, len(pts), head, p0model.METHOD_REPEATS, notes)
    for kind in RATE_KINDS:
        if kind not in points or "idle" not in points:
            continue
        fit_id, n = f"rates {kind} per entity", (points[kind][1][0].get("point") or {}).get("players") or 0
        try:
            full, ci = p0rates.rate_split(points[kind][0], points["idle"][0], n)
        except p0base.ModelError as ex:
            out["fit_notes"].append(f"{fit_id}: not fitted: {ex}")
            continue
        head = p0gates.header(runs, points[kind][1] + points["idle"][1], "fit")
        # The joint fit's per_* terms equal the idle arm's own fit, written above; only the split is written here.
        split = [name for name in ci if name.startswith(("kind_", "entity_"))]
        for name in split:
            _add_fit(out, fit_id, name, ci[name], list(full["coef"]), full["n"], head, p0model.METHOD_REPEATS,
                     [f"the {kind} arm over idle; the entity terms are divided by {n} entities"])


def _density_fits(out, runs, bridged):
    found = {}
    labels = p0gates.rate_sets(p0gates.chosen(runs, "density", ("grid", "scale"), ("base",)))
    if not labels:
        out["fit_notes"].append("density: no accepted grid or scale rows (p0load density)")
    for rates in labels:
        fit_id = f"density @ {rates}"
        try:
            form = p0model.n2_form(runs, rates, bridged)
            result = p0model.fit(runs, "density", form["form"], rates, bridged)
        except p0base.ModelError as ex:
            out["fit_notes"].append(f"{fit_id}: not fitted: {ex}")
            continue
        q = "N2" if form["form"] == "qN2" else "Q"
        q_ci = result["ci"][q]
        excludes = p0base.above(q_ci["lo"], 0.0) or p0base.above(0.0, q_ci["hi"])
        chose = (f"n2_form chose {form['form']} (AIC {form['aic']:.2f} against {form['other']['form']} "
                 f"{form['other']['aic']:.2f})")
        _fit_rows(out, fit_id, result, p0model.select_rows(runs, "density", rates), [chose])
        found[rates] = {"form": form["form"], "q": q, "q_ci": q_ci, "excludes": excludes}
        out["fit_notes"].append(f"{fit_id}: {chose}; q {p0gates.ci_text(q_ci)} "
                                f"{'excludes' if excludes else 'includes'} 0")
    return found


def _floor_fits(out, runs, bridged):
    found = {}
    for rates in p0gates.rate_sets(p0model.select_rows(runs, "floor")):
        fit_id = f"floor @ {rates}"
        try:
            result = p0model.fit(runs, "floor", rates=rates, bridged=bridged)
        except p0base.ModelError as ex:
            out["fit_notes"].append(f"{fit_id}: not fitted: {ex}")
            continue
        found[rates] = result
        _fit_rows(out, fit_id, result, p0model.select_rows(runs, "floor", rates))
        c0 = result["ci"]["c0"]["value"]
        out["fit_notes"].append(f"{fit_id}: c0 {c0:.1f} ms/s = {c0 / p0gates.fps_of(rates):.2f} ms/frame (0.71: 0.60 "
                                f"ms/frame, information); λ {p0gates.ci_text(result['ci']['links'])} ms/s per link")
    return found


def _presence_comparisons(out, runs, result, rates, bridged):
    try:
        for a in p0model.additivity_residual(result, runs, rates, bridged):
            out["fit_notes"].append(f"additivity @ {rates} {a['arm']} {a['point']} {a['cell']}: measured "
                                    f"{a['measured']:.1f}, predicted {a['predicted']:.1f}, residual "
                                    f"{a['residual']:.1f} [{a['lo']:.1f}, {a['hi']:.1f}] ms/s ({a['runs']} runs)")
    except p0base.ModelError as ex:
        out["fit_notes"].append(f"additivity @ {rates}: not compared: {ex}")
    for arm in PUPPET_ARMS:
        off = p0gates.chosen(runs, "presence", ("animation-off",), (arm,), rates)
        main = [r for r in p0gates.chosen(runs, "presence", ("main",), (arm,), rates)
                if p0gates.at_point(r, ANIMATION_OFF_POINT)]
        if not off or not main:
            continue
        try:
            for cell, d in p0model.arm_difference(off, main).items():
                out["fit_notes"].append(f"animation off - on @ {rates} {arm} {cell}: {p0gates.ci_text(d)} ms/s")
        except p0base.ModelError as ex:
            out["fit_notes"].append(f"animation off - on @ {rates} {arm}: not compared: {ex}")


def _presence_fits(out, runs, density, bridged):
    found = {}
    for rates in p0gates.rate_sets(p0gates.chosen(runs, "presence", p0model.PRESENCE_ROWS[0][1])):
        fit_id, dens = f"presence @ {rates}", density.get(rates)
        if dens is None:
            q_form, why = None, f"q left out: no density fit at {rates}"
        elif dens["excludes"]:
            q_form = dens["form"]
            why = f"q included ({dens['q']}): density's q interval {p0gates.ci_text(dens['q_ci'])} excludes 0"
        else:
            q_form, why = None, f"q left out: density's q interval {p0gates.ci_text(dens['q_ci'])} includes 0"
        try:
            result = p0model.fit(runs, "presence", q_form, rates, bridged)
        except p0base.ModelError as ex:
            out["fit_notes"].append(f"{fit_id}: not fitted: {ex}")
            continue
        found[rates] = result
        _fit_rows(out, fit_id, result, p0model.select_rows(runs, "presence", rates), [why])
        out["fit_notes"].append(f"{fit_id}: {why}")
        _presence_comparisons(out, runs, result, rates, bridged)
    return found


def _refit(out, runs, density, bridged):
    fit_id, dens = f"refit @ {REFIT_RATES}", density.get(REFIT_RATES)
    try:
        result = p0model.fit(runs, "refit", dens["form"] if dens else None, REFIT_RATES, bridged)
    except p0base.ModelError as ex:
        out["fit_notes"].append(f"{fit_id}: not fitted: {ex}")
        return None
    _fit_rows(out, fit_id, result, p0model.select_rows(runs, "refit", REFIT_RATES))
    return result


def latest_per_plan_key(runs):
    """(rows, dropped): the run rows keeping, among accepted non-canary rows, only the latest row (by window_local end,
    then file order) of each (build, plan_key), so a planned run made twice (abgeo --resume re-runs a partly done
    sub-block in full; a block run again without --resume) counts once in summaries, fits and gates. Unaccepted rows
    (the not-accepted table) and canaries (apply_drift needs every check) are all kept; dropped is how many went."""
    def pooled(row):
        return row.get("accepted") and row.get("family") != "canary" and row.get("plan_key")
    latest = {}
    for index, row in enumerate(runs):
        if pooled(row):
            order = ((row.get("window_local") or {}).get("end") or "", index)
            key = (row.get("build"), row["plan_key"])
            latest[key] = max(latest.get(key, order), order)
    keep = {index for _, index in latest.values()}
    rows = [row for index, row in enumerate(runs) if not pooled(row) or index in keep]
    return rows, len(runs) - len(rows)


def _one_build(rig, build, bridge):
    """(the rig rows of the build --build names, how many rig rows of other builds were left out)."""
    if build is None:
        return rig, 0
    if bridge is not None:
        raise ReportError("pass --bridge or --build, not both: --bridge pools two builds, --build reports one")
    builds = sorted({str(row.get("build")) for row in rig})
    named = [b for b in builds if b.startswith(build)]
    if len(named) != 1:
        what = "none of the rig builds" if not named else f"{len(named)} rig builds"
        raise ReportError(f"--build {build} names {what} ({', '.join(b[:12] for b in named or builds)})")
    kept = [row for row in rig if str(row.get("build")) == named[0]]
    return kept, len(rig) - len(kept)


def _check_builds(runs, bridge):
    counts = {}
    for row in runs:
        counts[str(row.get("build"))] = counts.get(str(row.get("build")), 0) + 1
    if len(counts) <= 1:
        return False
    listed = ", ".join(f"{build[:12]} ({n} rows)" for build, n in sorted(counts.items()))
    if bridge is None:
        raise ReportError(f"run rows from {len(counts)} builds ({listed}) are never pooled: pass --bridge OLD_SHA "
                          f"(a check canary on the new build within 5 % of the old build's baseline) or --build SHA "
                          f"(report one build's rows)")
    old = [build for build in counts if build.startswith(bridge)]
    if len(counts) != 2 or len(old) != 1:
        raise ReportError(f"--bridge {bridge} must name one of exactly two builds; the rows hold {listed}")
    new = next(build for build in counts if build != old[0])
    try:
        ok = p0results.bridge_ok(runs, old[0], new, p0gates.DRIFT_SHARE)
    except p0base.ModelError as ex:
        raise ReportError(f"--bridge {bridge}: {ex}") from ex
    if not ok:
        raise ReportError(f"--bridge {bridge}: a check canary on build {new[:12]} lies more than 5 % from build "
                          f"{old[0][:12]}'s baseline, so the builds may not pool")
    return True


def _rejected(runs):
    return [[r.get("family"), r.get("case"), r.get("arm"), json.dumps(r.get("point") or {}, sort_keys=True),
             r.get("repeat"), "; ".join((r.get("problems") or []) + [r.get("excluded_reason") or ""]).strip("; ")]
            for r in runs if not r.get("accepted")]


def _markdown(stamp, out, summaries, ignored, predictions, prediction_error):
    runs = out["runs"] + out["others"]
    drift = sum(1 for r in runs if r.get("excluded_reason") == "drift")
    lines = [f"# Phase 0 stage 0a report {stamp}", "",
             f"{len(runs)} run rows ({sum(1 for r in runs if r.get('accepted'))} accepted, {drift} excluded by canary "
             f"drift), {len(predictions)} prediction rows read; {ignored} summary, fit and gate rows of earlier "
             f"reports ignored. Builds {', '.join(sorted({str(r.get('build'))[:12] for r in runs}))}; CPU "
             f"{', '.join(sorted({str(r.get('cpu')) for r in runs}))}.", ""]
    lines += [f"- {note}" for note in out["notes"]] + ([""] if out["notes"] else []) + ["## Summary", ""]
    lines += p0markdown.markdown_summary(summaries, out["gates"]).splitlines()
    rejected = _rejected(runs)
    lines += ["", "## Runs not accepted", ""]
    lines += _table(("Family", "Case", "Arm", "Point", "Repeat", "Why"), rejected) if rejected else ["none"]
    lines += ["", "## Fits", ""] + (_table(FIT_COLUMNS, out["fit_lines"]) if out["fit_lines"] else ["no fits"])
    lines += [""] + [f"- {note}" for note in out["fit_notes"]]
    lines += ["", "## Gates", ""] + _table(GATE_COLUMNS, [
        [g["name"], g["game"], g.get("case") or "–", g["verdict"], g.get("key_metric") or "–", g["rule"],
         "; ".join(g["notes"]) or "–"] for g in out["gates"]])
    lines += ["", "Not evaluated in stage 0a: " + "; ".join(f"{name} needs {need}" for name, need in PENDING_GATES)]
    lines += ["", "## Map-size table", ""] + out["map"]
    if prediction_error is not None or out["predictions"]:
        lines += ["", "## Frozen abgeo predictions", ""]
        lines += [f"not made: {prediction_error}"] if prediction_error is not None else _table(
            ("Case", "Pair", "Players", "Monsters", "Band", "Puppets", "Ratio"),
            [[p["point"].get("case"), p["point"]["pair"], p["point"]["players"], p["point"]["monsters"],
              p["point"].get("band") or "as configured", p["point"].get("puppets"), f"{p['ratio']:.3f}"]
             for p in out["predictions"]])
    return "\n".join(lines + _bridge_lines(runs)) + "\n"


def _bridge_lines(runs):
    bridge = p0gates.chosen(runs, "density", ("bridge-0.53",))
    if not bridge:
        return []
    try:
        frame = p0gates.mean([p0geo.metric(r, "C", "work_ms_per_s") / p0geo.metric(r, "C", "frames_per_s")
                              for r in bridge])
    except p0base.ModelError as ex:
        return ["", f"Bridge to 0.53: not computed: {ex}"]
    return ["", f"Bridge to 0.53 (E: another CPU and build): C {frame:.1f} ms per measured frame over "
                f"{BRIDGE_053_MS:g} ms = {frame / BRIDGE_053_MS:.2f} ({len(bridge)} runs)"]


def build_report(rows, stamp, bridge=None, predict=None, predict_points=(), layouts=None, monster_counts=None,
                 created_utc=None, build=None):
    """The stage 0a report from mesh-p0/1 rows: {"markdown", "files", "summary", "fits", "gates", "predictions",
    "prediction_error"}. files maps report_<stamp>.md to the Markdown and <team>_<game>_fit_<stamp>.jsonl (one per team
    and game) to its summary, fit and gate rows, plus rig_fantasy_abgeo-prediction_<stamp>.jsonl with predict "abgeo".
    Only the rig team's run rows feed the fits and gates (--build keeps one build's); other teams' imported rows are
    summarised per team and game. Each (build, plan_key) counts once (latest_per_plan_key). The predictions and G-GEO
    (c) take predict_points [{case, players, monsters, band (0 = as configured), puppets, load, pair "cut/base"}],
    layouts {name: [(cell, rect)]}, monster_counts {(layout, band): {cell: {O_monster, U_monster}}}
    (chain3_monster_counts) and created_utc. A prediction that cannot be made leaves no prediction file and names why
    in prediction_error. Raises ReportError for no (rig) run rows, rig builds pooled without a passing bridge (a build
    sha or a unique prefix), a --build naming no single rig build or given with --bridge, and a predict other than
    abgeo."""
    if predict not in (None, "abgeo"):
        raise ReportError(f"--predict {predict!r}: only abgeo has frozen predictions")
    every = p0results.apply_drift(p0results.rows_of_type(rows, "run"))
    if not every:
        raise ReportError("no run rows to report: run a block first (p0load canary --baseline, rates, ...)")
    kept, repeated = latest_per_plan_key(every)
    rig = [r for r in kept if (r.get("team") or p0results.TEAM) == p0results.TEAM]
    others = [r for r in kept if (r.get("team") or p0results.TEAM) != p0results.TEAM]
    if not rig:
        raise ReportError("no rig run rows to report (only other teams' rows): run a block first")
    runs, left_out = _one_build(rig, build, bridge)   # the rig rows every fit and gate reads
    predictions = p0results.rows_of_type(rows, "prediction")
    bridged = _check_builds(runs, bridge)
    summaries = p0results.summary_rows(runs + others)
    notes = [f"{repeated} accepted run rows repeat a plan key on their build: only the latest row of each plan key is "
             f"used"] if repeated else []
    notes += [f"{left_out} rig run rows of other builds left out (--build {build})"] if left_out else []
    out = {"runs": runs, "others": others, "notes": notes, "fits": [], "fit_lines": [], "fit_notes": [],
           "predictions": []}
    _rates_fits(out, runs)
    density = _density_fits(out, runs, bridged)
    floors = _floor_fits(out, runs, bridged)
    presence = _presence_fits(out, runs, density, bridged)
    refit = _refit(out, runs, density, bridged)
    out["gates"] = p0gates.evaluate_gates(runs, {"presence": presence, "refit": refit}, predictions, layouts,
                                          monster_counts)
    prediction_error = None
    if predict == "abgeo":
        try:
            out["predictions"] = p0gates.predict_abgeo(runs, refit, predict_points, layouts, monster_counts,
                                                       created_utc)
        except p0base.ModelError as ex:
            prediction_error = str(ex)
    out["map"] = p0gates.map_size(refit, presence, floors)
    markdown = _markdown(stamp, out, summaries, len(rows) - len(every) - len(predictions), predictions,
                         prediction_error)
    files = {f"report_{stamp}.md": markdown}
    for row in summaries + out["fits"] + out["gates"]:
        team = row.get("team") or p0results.TEAM
        files.setdefault(p0results.file_name(row["game"], "fit", stamp, team), []).append(row)
    if out["predictions"]:
        files[p0results.file_name("fantasy", p0results.PREDICTION_FAMILY, stamp)] = out["predictions"]
    return {"markdown": markdown, "files": files, "summary": summaries, "fits": out["fits"], "gates": out["gates"],
            "predictions": out["predictions"], "prediction_error": prediction_error}


def chain3_monster_counts(texts):
    """{(layout, band): {cell: {"O_monster", "U_monster"}}}: the mean owned and puppet monsters per cell over the
    usable runs with monsters all in saved 0.71 abload ab reports (their 'peak' lines; band 'as configured' is 80 m).
    Label M, from an older build. A text that is not an ab report adds nothing."""
    sums = {}
    for text in texts:
        band = layout = monsters = None
        for line in text.splitlines():
            head, run = AB_BAND.match(line), AB_RUN.match(line)
            if head:
                band = p0gates.DEFAULT_BAND if head["band"].startswith("as") else float(head["band"])
            elif run:
                layout, monsters = run["layout"], run["monsters"]
            elif line.startswith("    peak ") and band is not None and layout and monsters == "all":
                for m in AB_CELL.finditer(line.split(" | ")[1]):
                    entry = sums.setdefault((layout, band), {}).setdefault(m["cell"], [0, 0, 0])
                    entry[0], entry[1], entry[2] = entry[0] + int(m["own"]), entry[1] + int(m["pup"]), entry[2] + 1
                layout = None
    return {key: {cid: {"O_monster": o / n, "U_monster": p / n} for cid, (o, p, n) in cells.items()}
            for key, cells in sums.items()}
