"""Phase 0 step 8b checks and verdicts (step8b_design.md decisions 12-14, 19, 20): the run checks of the two-machine
families (p0checks.FAMILY_CHECKS joins them to the common ones), gate J, b9's crossing figures and gate, and b7's
per-packet cost and network saturation. Pure: rows and runs come in as arguments. accept-b's part checks are
p0accept8b's (they reuse p0accept, which imports p0checks, which imports this module: the import graph stays acyclic).

    p0checks8b.check_bots_on_cell(spec, run)   # decision 19: a bot run whose cell saw fewer than N clients
    p0checks8b.gate_j(bot_row, phantom_row)    # decision 12: work within 15 %, bytes within 5 %, V within 20 %
    p0checks8b.crossing_figures(rows)          # b9: handoff_ready p50/p95/p99 pooled, outcomes, wake
    p0checks8b.pace_cost(rows)                 # b9: ms per crossing over the pace-cut/pace-west pairs, with its CI
    p0checks8b.packet_costs(rows)              # b7: (sink - virtual) per datagram at each packet cap
    p0checks8b.probe_saturation(rows)          # b7: the network thread probe's saturation verdict per P
    p0checks8b.gates_8b(runs)                  # gate rows J (b2, b10) and Crossings half A (b9) for p0report

A run's peer record (run["peer"], p0cells0b.peer_record) holds B's Sink and Bot windows on A's clock; a row's
step 8b metrics and fields are p0cells0b.run_metrics_0b's and run_fields_0b's.
Unit tests: python -m unittest discover -s Tools/ServerMeshing -p "test_*.py"
"""
import datetime
import math
import statistics

import p0base
import p0cells0b
import p0results

WORK_SHARE, BYTES_SHARE, V_SHARE = 0.15, 0.05, 0.20   # gate J (decision 12; the sheet's ±15 % and ±5 %)
CROSSING_P95_MS, CROSSING_BOTS = 1000.0, 50          # Crossings half A (sheet :1035): handoff_ready p95 with 50 bots
SATURATED_WORK, SATURATED_PCT = 950.0, 20.0          # p0plans.SATURATION: the main thread at 0.95 utilisation
NEAR_SHARE = 0.10                                    # a probe verdict within 10 % of its threshold earns a repeat
NOT_DECIDED = "NOT DECIDED"
J_RULE = (f"phantom C work (less the Phantom window's rig ms/s) within ±{WORK_SHARE:.0%} of the bot run's, Send wire "
          f"kB/s per Work-window client within ±{BYTES_SHARE:.0%}; V (bots' players seen vs phantoms' V move) further "
          f"apart than {V_SHARE:.0%} is NOT DECIDED; reported, never failing the block")
CROSSING_RULE = (f"handoff_ready p95 <= {CROSSING_P95_MS:g} ms over the {CROSSING_BOTS}-bot walks with the neighbour "
                 f"awake (half A); half B (rate vs B5) waits for stage 0c")


def _records(spec, run, key):
    cells = (run or {}).get("cells") or {}
    return [(cid, cells[cid][key]) for cid in spec["cells_read"] if (cells.get(cid) or {}).get(key) is not None]


def check_bots_on_cell(spec, run):
    """Decision 19: with bots on B (spec["peer"]["bots"]), each read cell whose Work windows held fewer than N
    clients (the transport's connected peers) in more than one window is a problem, "C: 47 of 50 bots on the cell in 3
    of 12 windows". b9's bots migrate between C and E, so crossings runs are counted by their handoffs instead."""
    crowd = (spec.get("peer") or {}).get("bots")
    if not crowd or spec["family"] == "crossings":
        return []
    want, problems = crowd["n"], []
    for cid, record in _records(spec, run, "p0"):
        triples = record["triples"]
        short = [t["window"]["clients"] for t in triples if t["window"]["clients"] < want]
        if len(short) > 1:
            problems.append(f"{cid}: {min(short)} of {want} bots on the cell in {len(short)} of {len(triples)} windows")
    return problems


def check_handoffs(spec, run):
    """A b9 walk (spec["stop_after_outcomes"]) must see at least one Handoff ACK on its read cells; rollbacks and
    failures are counted on the row (p0cells0b.run_fields_0b), never a problem."""
    if not spec.get("stop_after_outcomes"):
        return []
    acks = sum(1 for _, record in _records(spec, run, "p0b") for h in record.get("handoffs") or []
               if h["kind"] == "ack")
    return [] if acks else [f"no Handoff ACK on {', '.join(spec['cells_read'])} in the walk "
                            f"({spec['stop_after_outcomes']} outcomes expected)"]


CHECKS_8B = {"accept-b": (check_bots_on_cell,), "calibrate": (check_bots_on_cell,), "jfull": (check_bots_on_cell,),
             "crossings": (check_handoffs,)}


def _metric(row, name, stat="mean", cell="C"):
    return p0results.metric_value(row, name, stat, cell)


def _share(value, reference):
    return abs(value - reference) / reference if reference else None


def j_figures(row, cell="C"):
    """One gate J run's figures: work (C work ms/s, less phantom_rig_ms_per_s on a phantom run), wire_per_client
    (link_kBps_out over the Work windows' connected clients), v (bot_players_seen on a bot run, else V move mean) and
    uploads (bot_upload_pkt_per_s, else phantom_uploads_per_s); None where the row lacks it."""
    work, rig = _metric(row, "work_ms_per_s", cell=cell), _metric(row, "phantom_rig_ms_per_s", cell=cell) or 0.0
    wire, connected = _metric(row, "link_kBps_out", cell=cell), (row.get("clients") or {}).get("connected")
    bots = ((row.get("peer") or {}).get("bots")) is not None
    return {"work": None if work is None else work - rig,
            "wire_per_client": wire / connected if wire is not None and connected else None,
            "v": _metric(row, "bot_players_seen", cell="B") if bots else row.get("v_move_mean"),
            "uploads": (_metric(row, "bot_upload_pkt_per_s", "median", "B") if bots
                        else _metric(row, "phantom_uploads_per_s", cell=cell))}


def gate_j(bot_row, phantom_row):
    """Gate J for one arm (decision 12): {"verdict" (PASS, FAIL, NOT DECIDED, or NO VERDICT when a figure is
    missing), "work", "bytes", "v" (each a (bot, phantom, share) triple), "uploads" (bot, phantom), "notes"}."""
    bot, phantom = j_figures(bot_row), j_figures(phantom_row)
    out = {"uploads": (bot["uploads"], phantom["uploads"]), "notes": []}
    for name, key in (("work", "work"), ("bytes", "wire_per_client"), ("v", "v")):
        a, b = bot[key], phantom[key]
        out[name] = (a, b, None if a is None or b is None else _share(b, a))
    missing = [name for name in ("work", "bytes", "v") if out[name][2] is None]
    if missing:
        out.update(verdict=p0base.NO_VERDICT, notes=[f"no {', '.join(missing)} figure"])
    elif p0base.above(out["v"][2], V_SHARE):
        out.update(verdict=NOT_DECIDED, notes=[f"V differs by {out['v'][2]:.1%} (bots {out['v'][0]:.1f}, phantoms "
                                               f"{out['v'][1]:.1f})"])
    else:
        ok = p0base.at_most(out["work"][2], WORK_SHARE) and p0base.at_most(out["bytes"][2], BYTES_SHARE)
        out["verdict"] = p0base.pass_fail(ok)
    return out


def _latest(rows):
    """The last row of each plan key (rows in order)."""
    return list({row.get("plan_key"): row for row in rows}.values())


def j_pairs(runs, family):
    """[(arm, P, bot row, phantom row)] of a gate J family's accepted rows, the latest of each, by arm and P."""
    found = {}
    for row in _latest([r for r in runs if r.get("family") == family and r.get("accepted")]):
        point = row.get("point") or {}
        found.setdefault((row["arm"], point.get("P")), {})[point.get("load")] = row
    return [(arm, p, both["bots"], both["phantoms"]) for (arm, p), both in sorted(found.items(), key=str)
            if "bots" in both and "phantoms" in both]


def crossing_figures(rows):
    """b9 walk rows pooled: {"n_runs", "ack", "rollback", "fail", "p50", "p95", "p99" (nearest rank over every ACK's
    handoff_ready ms), "wake_ms" (the median of the runs' wake)}; ms figures None without an ACK."""
    handoffs = [row.get("handoffs") or {} for row in rows]
    ms = [value for h in handoffs for value in h.get("ack_ms") or []]
    wakes = [h["wake_ms"] for h in handoffs if h.get("wake_ms") is not None]
    out = {"n_runs": len(rows), **{kind: sum(h.get(kind) or 0 for h in handoffs) for kind in p0cells0b.OUTCOMES},
           "wake_ms": statistics.median(wakes) if wakes else None}
    out.update({name: p0cells0b.percentile(ms, share) for name, share in (("p50", 0.5), ("p95", 0.95), ("p99", 0.99))})
    return out


def _window_s(row):
    window = row.get("window_local") or {}
    if not window.get("start") or not window.get("end"):
        return None
    start, end = (datetime.datetime.fromisoformat(window[key]) for key in ("start", "end"))
    return (end - start).total_seconds()


def _ci(values):
    n = len(values)
    mean = math.fsum(values) / n
    half = p0base.t975(n - 1) * statistics.stdev(values) / math.sqrt(n) if n > 1 else None
    return {"mean": mean, "n": n, "lo": None if half is None else mean - half, "hi": None if half is None else mean + half}


def pace_cost(rows):
    """b9's cost per crossing (decision 14): for each repeat with an accepted pace-cut and pace-west row, (C + E work
    ms/s, cut - west) / the cut run's crossings per second (its ACKs inside the measurement over the measurement's
    seconds); {"mean", "n", "lo", "hi" (t(n - 1), None for one pair), "pairs": [ms per crossing]}, or None without a
    pair. A pair whose cut run crossed nothing is skipped."""
    pace = {}
    for row in _latest([r for r in rows if r.get("family") == "crossings" and r.get("case") == "pace"
                        and r.get("accepted")]):
        pace.setdefault(row["repeat"], {})[row["arm"]] = row
    values = []
    for both in pace.values():
        if "pace-cut" not in both or "pace-west" not in both:
            continue
        work = {arm: sum(_metric(both[arm], "work_ms_per_s", cell=cell) or 0.0 for cell in ("C", "E")) for arm in both}
        acks, seconds = (both["pace-cut"].get("handoffs") or {}).get("window_acks"), _window_s(both["pace-cut"])
        if acks and seconds:
            values.append((work["pace-cut"] - work["pace-west"]) / (acks / seconds))
    return dict(_ci(values), pairs=values) if values else None


def packet_costs(rows):
    """b7 (a, a'): per packet cap, the per-datagram cost of real sockets: ((sink - virtual) C work ms/s) / the sink
    run's datagrams/s, in microseconds, as pkt_main_us, and the LiteNetLib thread's the same way as pkt_net_us; means
    over the repeats with each mean's run count. [{"packet_bytes", "pkt_main_us", "pkt_net_us", "n"}] for the caps with
    both arms."""
    by_cap = {}
    for row in _latest([r for r in rows if r.get("family") == "packets" and r.get("case") in ("cap", "cap-sink")
                        and r.get("accepted") and (r.get("point") or {}).get("P") == 100]):
        by_cap.setdefault(row["point"]["packet_bytes"], {}).setdefault(row["case"], []).append(row)
    out = []
    for size, arms in sorted(by_cap.items(), reverse=True):
        if not arms.get("cap") or not arms.get("cap-sink"):
            continue

        def mean(case, read):
            if isinstance(read, str):
                name = read
                read = lambda row: _metric(row, name)  # noqa: E731 -- a metric name reads as that metric
            values = [v for v in (read(row) for row in arms[case]) if v is not None]
            return math.fsum(values) / len(values) if values else None
        datagrams = mean("cap-sink", "datagrams_per_s")
        cost = {}
        for label, read in (("pkt_main_us", lambda row: _metric(row, "work_ms_per_s")), ("pkt_net_us", net_thread)):
            sink, virtual = mean("cap-sink", read), mean("cap", read)
            cost[label] = (sink - virtual) * 1000.0 / datagrams if None not in (sink, virtual) and datagrams else None
        out.append(dict(cost, packet_bytes=size, n=min(len(arms["cap"]), len(arms["cap-sink"]))))
    return out


def net_thread(row, cell="C"):
    """The cell's network-thread ms/s: thread_ms_per_s{litenetlib} plus {unnamed}. LiteNetLib names its threads
    before starting them, and where that name does not reach Windows ("names pre-start=absent", machine A) its CPU is
    filed as unnamed (ServerMeshThreadCpu.Classify): the 2026-10-08 smokes read litenetlib 0.0 with unnamed rising from
    ~8 ms/s (virtual) to 280 (57,000 datagrams/s). The unnamed rest (~8 ms/s) cancels in sink - virtual differences.
    None when neither figure is there."""
    values = [v for v in (_metric(row, "thread_ms_per_s{litenetlib}", cell=cell),
                          _metric(row, "thread_ms_per_s{unnamed}", cell=cell)) if v is not None]
    return math.fsum(values) if values else None


def main_saturated(row, cell="C"):
    """Whether a run saturated its main thread: C's work at or above 950 ms/s or 20 % of frames never waiting."""
    work, sat = _metric(row, "work_ms_per_s", cell=cell), _metric(row, "sat_pct", cell=cell)
    return bool((work is not None and p0base.at_least(work, SATURATED_WORK))
                or (sat is not None and p0base.at_least(sat, SATURATED_PCT)))


def probe_saturation(rows, cell="C"):
    """b7 (b): per probe P (ascending; the latest row of each), {"P", "thread_ms_per_s", "datagrams_per_s",
    "saturated" (p0cells0b.net_saturated across the runs: the network threads (net_thread) at 900 ms/s, or datagrams/s rising
    less than 10 % for a doubling of P), "near" (the verdict within 10 % of its threshold: a second repeat is owed),
    "main_saturated"}."""
    probes = sorted(_latest([r for r in rows if r.get("family") == "packets" and r.get("case") == "probe"]),
                    key=lambda r: r["point"]["P"])
    points = [(r["point"]["P"], net_thread(r, cell),
               _metric(r, "datagrams_per_s", cell=cell)) for r in probes]
    flags, by_p, out = p0cells0b.net_saturated(points), {p: d for p, _, d in points}, []
    for (p, thread, datagrams), flag, row in zip(points, flags, probes):
        half = by_p.get(p / 2)
        rise = datagrams / half - 1.0 if datagrams is not None and half else None
        near = ((thread is not None and abs(thread - p0cells0b.NET_SATURATED_MS) <= NEAR_SHARE * p0cells0b.NET_SATURATED_MS)
                or (rise is not None and abs(rise - p0cells0b.NET_RISE) <= NEAR_SHARE * p0cells0b.NET_RISE))
        out.append({"P": p, "thread_ms_per_s": thread, "datagrams_per_s": datagrams, "saturated": flag, "near": near,
                    "main_saturated": main_saturated(row, cell)})
    return out


def _fmt(value, unit=""):
    return "–" if value is None else f"{value:.1f}{unit}"


def gates_8b(runs):
    """The step 8b gate rows (decision 20) over the report's run rows: one Phantom validity (J) row per arm of b2
    (case J-lite) and b10 (J-full) with both loads accepted, and Crossings (B10) half A when b9 walk rows exist. No row
    for a family without rows (the report lists it as not evaluated)."""
    gates = []
    for family, case in (("calibrate", "J-lite"), ("jfull", "J-full")):
        for arm, p, bot, phantom in j_pairs(runs, family):
            j = gate_j(bot, phantom)
            inputs = {"bots": bot.get("plan_key"), "phantoms": phantom.get("plan_key"), "work": j["work"],
                      "bytes": j["bytes"], "v": j["v"], "uploads": j["uploads"]}
            key = (f"work {_fmt(j['work'][1])} vs {_fmt(j['work'][0])} ms/s; per client "
                   f"{_fmt(j['bytes'][1])} vs {_fmt(j['bytes'][0])} kB/s")
            notes = [f"arm {arm}, P {p}"] + j["notes"]
            gates.append(p0results.gate_row("Phantom validity (J)", j["verdict"], inputs, J_RULE, notes,
                                            p0results.header_of([bot, phantom], family), case, key))
    walks = [r for r in _latest(runs) if r.get("family") == "crossings" and r.get("case") == "walk" and r.get("accepted")]
    if walks:
        main = [r for r in walks if r["arm"] == "awake" and (r.get("point") or {}).get("bots") == CROSSING_BOTS]
        figures = crossing_figures(main)
        verdict = (p0base.NO_VERDICT if figures["p95"] is None
                   else p0base.pass_fail(p0base.at_most(figures["p95"], CROSSING_P95_MS)))
        notes = [f"{figures['ack']} ACK, {figures['rollback']} rollback, {figures['fail']} failed over "
                 f"{figures['n_runs']} run(s)"]
        for arm in ("awake", "asleep"):
            for n in sorted({(r.get("point") or {}).get("bots") for r in walks if r["arm"] == arm}, key=str):
                other = crossing_figures([r for r in walks if r["arm"] == arm and (r.get("point") or {}).get("bots") == n])
                notes.append(f"{arm} {n} bots: p50/p95/p99 {_fmt(other['p50'])}/{_fmt(other['p95'])}/"
                             f"{_fmt(other['p99'])} ms, wake {_fmt(other['wake_ms'], ' ms')}")
        cost = pace_cost(runs)
        if cost:
            interval = "" if cost["lo"] is None else f" [{cost['lo']:.3f}, {cost['hi']:.3f}]"
            notes.append(f"cost per crossing {cost['mean']:.3f}{interval} ms over {cost['n']} pace pair(s) (C + E; the "
                         f"login reconnect lands on the editor, not on C or E)")
        gates.append(p0results.gate_row("Crossings (B10) half A", verdict, {"p95_ms": figures["p95"], **figures},
                                        CROSSING_RULE, notes, p0results.header_of(walks, "crossings"), "walk",
                                        f"handoff_ready p95 {_fmt(figures['p95'], ' ms')}"))
    return gates
