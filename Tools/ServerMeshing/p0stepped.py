"""Phase 0 stepped runs (step 8a decision 4) and the reading every p0blocks run shares: one Play whose phantoms step
through MESH_PHANTOM_STEPS gives one row per step, each read over its step window and graded against that window's
length. Split out of p0blocks (the ~500-line rule); p0blocks.run_rows chooses between run_spec and run_stepped.

    p0stepped.graded(spec, since, start, end, lines, context)  # (row, run) of one run read over [start, end]
    p0stepped.run_stepped(spec, rep, context)                  # [(row, run)], one per planned step
    p0stepped.SaturationStop(spec, context)                    # run_window's stop_when for a rising stepped run
    p0stepped.run_groups(rows) / complete_keys(rows)           # rows by run; plan keys with a complete run

A stepped spec carries stepped = {"steps": [P1..Pn], "hold": seconds} (p0plans0b sets it, with warmup 10 s and the
measure formula); its rows share the run's plan key, carry point.step = P, stepped (the spec's) and stepped_since (the
Play start), and the run is done only when every step has an accepted row. A run whose steps rise ends once a step
before the last is graded saturated: every later step only adds load, so each gets a row naming that step instead of a
measurement (the even repeats' falling runs are measured whole).
Unit tests: python -m unittest discover -s Tools/ServerMeshing -p "test_*.py"
"""
import p0cells
import p0cells0b
import p0checks
import p0peer
import p0results
import regress as r
import rigrun


def cell_lines(spec, since):
    """{cell: every line it logged since Play} of the read cells."""
    maps = dict(r.ZONES)
    return {cid: rigrun.cell_lines(cid, maps[cid], since) for cid in spec["cells_read"]}


def graded(spec, since, start, end, lines, context, peer=None):
    """(row, run) of one run read over [start, end] (lines: {cell: its lines since Play}, or a function giving them):
    per read cell rigrun.cell_window, p0cells.cell_record (the fan-out saturation rule for family fanout) and, for a
    stage 0b family, p0cells0b.cell_record_0b; with a p0peer.PeerSession, run["peer"] = its record (B's sink and bot
    logs on A's clock); graded by p0checks. A run whose logs cannot be read or graded is a failed row (and None)."""
    run, maps = {"since": since, "start": start, "end": end, "cells": {}}, dict(r.ZONES)
    fanout = spec["family"] == "fanout"
    try:
        lines = lines() if callable(lines) else lines
        if peer is not None:
            run["peer"] = peer.record(start, end)
        for cid in spec["cells_read"]:
            cell = {"structural": rigrun.cell_window(cid, maps[cid], since, start, end),
                    "p0": p0cells.cell_record(cid, lines[cid], start, end, fanout)}
            if spec["family"] in p0results.FAMILIES_0B:
                cell["p0b"] = p0cells0b.cell_record_0b(cid, lines[cid], start, end)
            run["cells"][cid] = cell
        problems, excluded = p0checks.run_problems(spec, run), p0checks.exclusion(spec, run)
        return p0results.run_row(spec, run, context, problems, excluded), run
    except Exception as ex:  # one run's boundary: a run whose logs cannot be graded is written as failed
        return p0results.run_row(spec, dict(run, cells={}), context, [f"row not graded: {ex}"], "run failed"), None


def step_spec(spec, window):
    """The spec of one step's row: point.step = P and measure = the step window's seconds (0 without a window), so
    coverage is checked against the window (decision 4); plan key, repeat and the rest are the run's."""
    seconds = (window["end"] - window["start"]).total_seconds() if window.get("start") else 0
    return dict(spec, point=dict(spec.get("point") or {}, step=window["step"]),
                measure=int(seconds) if seconds == int(seconds) else seconds)


class SaturationStop:
    """run_window's stop_when for a stepped run: True once a step whose window is complete (the next step's line is
    logged) and that has only larger steps after it grades as excluded for saturation; step is then its P. A falling
    or flat step list never stops, and a reading or grading error reads as False (the run goes on to its end)."""

    def __init__(self, spec, context):
        planned = list(spec["stepped"]["steps"])
        self.spec, self.context, self.planned, self.seen, self.step = spec, context, planned, set(), None
        self.rising = [all(later > p for later in planned[k + 1:]) for k, p in enumerate(planned)]

    def __call__(self, since, start):
        try:
            lines = cell_lines(self.spec, since)
            steps = next((found for found in (p0cells0b.phantom_steps(lines[cid]) for cid in self.spec["cells_read"])
                          if found), [])
            stamped = {step["i"] for step in steps}
            for window in p0cells0b.step_windows(self.planned, steps, start, rigrun.now()):
                k = window["index"]
                if (k >= len(self.planned) or not self.rising[k - 1] or k + 1 not in stamped or window["problem"]
                        or k in self.seen):
                    continue
                self.seen.add(k)
                row, _ = graded(step_spec(self.spec, window), since, window["start"], window["end"], lines,
                                self.context)
                if "saturated" in (row["excluded_reason"] or ""):
                    self.step = window["step"]
                    return True
        except Exception:  # a mid-run look that fails only means the run is not cut short
            return False
        return False


def run_stepped(spec, rep, context):
    """[(row, run)] of one stepped run (spec["stepped"] = {"steps": [P..], "hold": s}; decision 4): one Play, then a
    row per planned step over its window (p0cells0b.step_windows, from the first read cell that logged Phantom step
    lines). A step without a window is a failed row naming why, and a run that raises fails every step. Every row
    carries stepped_since (the Play start: its run) and net_saturated (p0cells0b.net_saturated on the steps' cell).
    A spec with a peer runs with p0peer.window_hooks like p0blocks.run_spec, and every row gets its problems
    (p0peer.mark_rows); Stopped (a peer's refusal before Play) is raised."""
    planned, stop = list(spec["stepped"]["steps"]), SaturationStop(spec, context)
    peer, hooks = p0peer.window_hooks(spec, rep, stop_when=stop)
    try:
        since, start, end = rigrun.run_window(spec["env"], spec["warmup"], spec["measure"], rep, **hooks)
        lines = cell_lines(spec, since)
        found = [(cid, p0cells0b.phantom_steps(lines[cid])) for cid in spec["cells_read"]]
        cell, steps = next(((cid, steps) for cid, steps in found if steps), (spec["cells_read"][0], []))
        windows = p0cells0b.step_windows(planned, steps, start, end)
    except p0peer.Stopped:
        raise
    except Exception as ex:  # one run's boundary: every step's row is written as failed
        pairs = [(p0results.run_row(step_spec(spec, {"step": step}), None, context, [str(ex)], "run failed"), None)
                 for step in planned]
        p0peer.mark_rows([row for row, _ in pairs], peer)
        return pairs
    if stop.step is not None:
        rep.note(f"    step P {stop.step} saturated: the run stopped there")
    pairs, cut = [], next((window["index"] for window in windows if window["step"] == stop.step), None)
    for window in windows:
        sub = step_spec(spec, window)
        if cut is not None and window["index"] > cut:
            reason = f"not measured: saturated at the earlier step P {stop.step} on {cell}"
            pairs.append((p0results.run_row(sub, None, context, [], reason), None))
        elif window["problem"]:
            pairs.append((p0results.run_row(sub, None, context, [window["problem"]], "run failed"), None))
        else:
            pairs.append(graded(sub, since, window["start"], window["end"], lines, context, peer))
    flags = p0cells0b.net_saturated([(window["step"], p0results.metric_value(row, "thread_ms_per_s{litenetlib}",
                                                                            "mean", cell),
                                      p0results.metric_value(row, "datagrams_per_s", "mean", cell))
                                     for window, (row, _) in zip(windows, pairs)])
    for (row, _), flag in zip(pairs, flags):
        row.update(stepped_since=since.isoformat(), net_saturated=flag)
    p0peer.mark_rows([row for row, _ in pairs], peer)
    return pairs


def run_groups(rows):
    """The run rows as [(plan key, [rows of one run])] in order: a stepped run's step rows (one plan key, machine and
    stepped_since) form one group, every other row its own."""
    groups, open_runs = [], {}
    for row in rows:
        key = (row.get("plan_key"), row.get("stepped_since"), p0results.machine(row)) if row.get("stepped") else None
        if key is not None and key in open_runs:
            open_runs[key].append(row)
            continue
        groups.append((row.get("plan_key"), [row]))
        if key is not None:
            open_runs[key] = groups[-1][1]
    return groups


def missing_steps(group):
    """The planned steps (P) of one run group without an accepted row (a plain run: [None] unless it is accepted)."""
    stepped = group[0].get("stepped")
    if not stepped:
        return [] if group[0].get("accepted") else [None]
    done = {(row.get("point") or {}).get("step") for row in group if row.get("accepted")}
    return [step for step in stepped["steps"] if step not in done]


def complete_keys(rows):
    """Plan keys with a complete run among the rows: an accepted row, or a stepped run with every step accepted."""
    return {key for key, group in run_groups(rows) if key and not missing_steps(group)}


def _reasons(row):
    return "; ".join((row.get("problems") or []) + [row.get("excluded_reason") or ""]).strip("; ")


def why_not(group):
    """Why one run group is not complete: a plain row's problems and exclusion; for a stepped run each unaccepted
    step's ("step 200: ...") and each planned step without a row."""
    if not group[0].get("stepped"):
        return _reasons(group[0])
    rows = {(row.get("point") or {}).get("step"): row for row in group}
    return "; ".join(f"step {step}: " + (_reasons(rows[step]) if step in rows else "no row")
                     for step in missing_steps(group))
