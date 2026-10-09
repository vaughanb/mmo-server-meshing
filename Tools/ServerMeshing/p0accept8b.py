"""Phase 0 accept-b checks (step 8b, step8b_design.md decision 11): parts A-F's acceptance checks over the runs of
p0plans8b.accept_b_plan, graded together like accept-a's (p0accept). Pure: every input is an argument. Split from
p0checks8b so that it may reuse p0accept (which imports p0checks, which imports p0checks8b).

    p0accept8b.accept_b_checks(runs)   # [(ok, text)] over [(spec, row, run)] of an accept-b invocation

A run is p0checks' (cells with "p0" and "p0b" records) plus "peer" (p0cells0b.peer_record: B's Sink windows and each
bot's Bot net and Bot ships windows on A's clock); a row is p0results.run_row's. A part with no runs adds no check; a
part whose runs failed (no run) fails.
Unit tests: python -m unittest discover -s Tools/ServerMeshing -p "test_*.py"
"""
import math

import p0accept
import p0base
import p0cells0b
import p0plans8b

A_WIRE_SHARE = 0.03            # A: Send wire within ±3 % of Sink in
B_V_SHARE = 0.10               # B: V move within ±10 % of VIEW + the bot
C_RECORDS_SHARE = 0.02         # C: the sender's records/s within ±2 % of the kit's (step 6 gate 6)
F_SHARE = 0.01                 # F: the meter's cost under 1 % of the off arm's work


def _collector():
    checks = []
    return checks, lambda ok, text: checks.append((bool(ok), text))


def _part(runs, part):
    return [(spec, row, run) for spec, row, run in runs if spec.get("accept_part") == part]


def _windows(run, key, cell="C"):
    record = (((run or {}).get("cells") or {}).get(cell) or {}).get("p0b") or {}
    return record.get(key) or []


def _bots(run):
    return ((run or {}).get("peer") or {}).get("bots") or {}


def _per_s(windows, field):
    seconds = math.fsum(w["seconds"] for w in windows)
    return sum(w[field] for w in windows) / seconds if seconds > 0 else None


def _within(value, reference, share):
    return value is not None and reference and p0base.at_most(abs(value - reference), share * reference)


def _pct(value, reference):
    return "–" if value is None or not reference else f"{(value - reference) / reference:+.1%}"


def _part_a(add, runs):
    for spec, _, run in runs:
        if spec["arm"] == "virtual":
            add(run is not None and _per_s(_windows(run, "send"), "wire_bytes") is not None,
                f"A virtual: Send windows give the wire rate ({_fmt(_per_s(_windows(run, 'send'), 'wire_bytes'))} B/s)")
            continue
        wire = _per_s(_windows(run, "send"), "wire_bytes")
        sink = _per_s(((run or {}).get("peer") or {}).get("sink") or [], "in_bytes")
        add(_within(wire, sink, A_WIRE_SHARE), f"A sink: Send wire {_fmt(wire)} B/s within ±3 % of Sink in "
                                               f"{_fmt(sink)} B/s ({_pct(wire, sink)})")


def _fmt(value):
    return "–" if value is None else f"{value:.0f}"


def _part_b(add, runs):
    want = p0plans8b.EYE_VIEW + 1   # the filler movers and the bot
    for _, _, run in runs:
        phantoms = _windows(run, "phantom")
        seen = [w["eye_seen"] for w in phantoms]
        add(phantoms and not any(seen), f"B: eyeSeen 0 in every Phantom window ({seen})")
        mean, _ = p0cells0b.v_move({"phantom": phantoms})
        add(_within(mean, want, B_V_SHARE), f"B: V move mean {_fmt(mean)} within ±10 % of {want}")
        players = [w["players_seen"] for bot in _bots(run).values() for w in bot["net"]]
        add(players and max(players) <= want, f"B: the bot sees no eye (players seen {players} <= {want})")


def records_per_s(run):
    """A run's EntityState records per second over C's Send windows (records ro + u + other), or None."""
    sends = [w for w in _windows(run, "send") if w["records"] is not None]
    seconds = math.fsum(w["seconds"] for w in sends)
    return sum(w["records"]["ro"] + w["records"]["u"] + w["records"]["other"] for w in sends) / seconds \
        if seconds > 0 else None


def _bot_health(add, label, run, want_players=None):
    crowd = _bots(run)
    add(crowd, f"{label}: bot logs fetched from B ({len(crowd)})")
    for user, bot in sorted(crowd.items()):
        bad = {mark: n for mark, n in bot["bad"].items() if n}
        add(bot["in_map"] and not bad, f"{label} {user}: in the map, free of faults ({bad or 'none'})")
        if want_players is not None:
            players = [w["players_seen"] for w in bot["net"]]
            add(players and min(players) >= want_players,
                f"{label} {user}: players seen >= {want_players} in every Bot net window ({players})")


def _part_c(add, runs):
    rates = {}
    for spec, _, run in runs:
        rates.setdefault(spec["arm"], []).append(records_per_s(run))
        _bot_health(add, f"C {spec['arm']} pair {spec['repeat']}", run, p0plans8b.C_PLAYERS)
    kit, sender = ([v for v in rates.get(arm, []) if v is not None] for arm in ("kit", "sender"))
    kit_mean = math.fsum(kit) / len(kit) if kit else None
    sender_mean = math.fsum(sender) / len(sender) if sender else None
    add(_within(sender_mean, kit_mean, C_RECORDS_SHARE),
        f"C: sender records/s {_fmt(sender_mean)} within ±2 % of the kit's {_fmt(kit_mean)} ({_pct(sender_mean, kit_mean)})")


def _part_d(add, runs):
    for _, _, run in runs:
        _bot_health(add, "D", run)
        seen = [w["seen"] for bot in _bots(run).values() for w in bot["ships"]]
        add(seen and min(seen) >= p0plans8b.D_SEEN,
            f"D: the ship bot sees >= {p0plans8b.D_SEEN} ships in every measured Bot ships window ({seen})")


def _part_e(add, runs):
    for _, _, run in runs:
        lines = _windows(run, "ce12")
        add(len(lines) == 1 and lines[0]["cell"] == "C" and lines[0]["shape"] == "pass",
            f"E: C logs exactly one CE-12 check line with shape=pass (the post-pass; the two consumed sets never "
            f"overlap) ({[(c['cell'], c['shape'], c['handler']) for c in lines]})")


def _part_f(add, runs):
    pairs = {}
    for spec, row, _ in runs:
        work = _c_work(row)
        if work is not None:
            pairs.setdefault(spec["repeat"], {})[spec["arm"]] = work
    both = [(p["meter-off"], p["meter-on"]) for p in pairs.values() if "meter-off" in p and "meter-on" in p]
    if len(both) < 2:
        add(False, f"F: the meter's overhead needs at least 2 pairs with both arms accepted ({len(both)})")
        return
    gate = F_SHARE * math.fsum(off for off, _ in both) / len(both)
    cost = p0accept.overhead(both, gate)
    add(cost["hi"] < gate,
        f"F: meter cost {cost['mean']:.2f} [{cost['lo']:.2f}, {cost['hi']:.2f}] ms/s over {cost['n']} pairs, the upper "
        f"end under 1 % of the off arm's work ({gate:.2f} ms/s)")


def _c_work(row):
    """An accepted row's C work ms/s, else None."""
    if row is None or not row.get("accepted"):
        return None
    return next((m["value"] for m in row.get("metrics") or [] if m["name"] == "work_ms_per_s" and m["stat"] == "mean"
                 and m.get("cell") == "C"), None)


def accept_b_checks(runs):
    """accept-b (decision 11) as [(ok, text)] over [(spec, row, run)] (run None for a failed run): A the sink run's
    Send wire within ±3 % of B's Sink in; B eyeSeen 0, V move within ±10 % of 201 and the bot's players seen at most
    201; C the sender's records/s within ±2 % of the kit's over the pairs, each bot in the map, fault-free, seeing >= 50
    players in every window; D the ship bot seeing >= 45 ships in every window; E one CE-12 line, shape=pass; F the
    meter's overhead (p0accept.overhead, t(n - 1)) below 1 % of the off arm's work."""
    checks, add = _collector()
    parts = {"A": _part_a, "B": _part_b, "C": _part_c, "D": _part_d, "E": _part_e, "F": _part_f}
    for part, check in parts.items():
        chosen = _part(runs, part)
        if chosen:
            check(add, chosen)
    return checks
