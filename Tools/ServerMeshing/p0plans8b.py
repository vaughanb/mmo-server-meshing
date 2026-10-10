"""Phase 0 stage 0b two-machine run plans (step 8b, step8b_design.md decisions 7, 8, 11-15): accept-b (b1), calibrate
(b2), the sink arms of packets (b7), crossings (b9) and J-full (b10) as data. Pure: every input is an argument (the
bot calibration too: p0load passes p0peer.read_calibration(build)); nothing here runs the rig or touches machine B.
p0plans0b's _spec0b builds every spec; p0load runs the plans and p0dryrun prints them.

    p0plans8b.accept_b_plan("A,B,C,D,E,F")     # b1: 19 runs (A 2, B 1, C 4, D 1, E 1, F 10)
    p0plans8b.calibrate_plan(calibration)      # b2: 4 runs; also jfull_plan (b10, 4), crossings_plan (b9, 18)
    p0plans8b.packets_sink_plan()              # b7's sink arms: 9 sink cap, 9 virtual cap (8a's keys), 4 probe
    p0plans8b.jfull_refusal(calibration)       # why b10 is not planned, or None

A step 8b spec is a stage 0b RunSpec (p0plans0b) plus peer = {"sink": {"count": P, "kind": k, "sizes": bool} | None,
"bots": {"first": K, "n": N, "mode": m, "placement": p, "bot_net": s, "rig_ships": bool} | None} (decision 8; bots
start at meshbot2) and, where they apply: stop_after_outcomes (a b9 walk: the run ends once the origin cells logged 2N
handoff outcomes; a walk goes to the start x mirrored across the cut, +30, and back: p0peer's default), coverage_exempt (cells whose Work
coverage is not checked: an asleep neighbour starts only when woken), upload_hz (a phantom arm's
MESH_PHANTOM_UPLOAD_HZ from the bot calibration; None until calibrate-bots has run) and accept_part (b1). A bot run
warms up stagger x N + 45 s (b9 walks: 10 s); every peer run ends before a bot's pace stops (600 s after map entry).
Unit tests: python -m unittest discover -s Tools/ServerMeshing -p "test_*.py"
"""
import p0plans
import p0plans0b
import p0ships

FAMILIES = ("accept-b", "calibrate", "crossings", "jfull")
ACCEPT_B_PARTS = ("A", "B", "C", "D", "E", "F")
FIRST_BOT, BOT_STAGGER_S, BOT_SETTLE_S, BOT_NET_S = 2, 1, 45, 5   # meshbot2 up; remoteb's --stagger; decision 8
PACE_STOP_S = 600                 # ServerMeshAutoClient's pace stops 600 s after map entry (:749)
FANTASY_RATES, SPACE_RATES = p0plans0b.FANTASY_RATES, p0plans0b.SPACE_RATES
CE12_TIMING = (40, 30)           # step 6 gate 8: 300 frames of retries after the cell is ready; 30 s so the 5 s
#                                  Threads windows cover the measure (20 s left one short, 2026-10-08)
# b1 (decision 11)
A_PHANTOMS, A_VIEW, EYES, EYE_VIEW, C_PLAYERS, C_AREA, C_BOTS, C_PAIRS = 50, 100, 50, 200, 50, "0,0,25", 2, 2
# F: 200 eyes (step 5 used 400 on B; at 60/0.03 on A, 400 saturated cell C in all 10 runs, 2026-10-08) and 10
# pairs (5 left the interval [-3.7, 5.8] ms/s around a 1.05 mean, wider than the 1 % bound)
D_SHIPS, D_SCALE, D_SEEN, F_PAIRS, F_EYES, F_VIEW = "C:40:mirror,C:10:parked", 0.5, 45, 10, 200, 100
# b2 and b10 (decisions 12, 15): the 100 x 100 m square centred on (0, 0), sender tiers 20/20/20
CALIBRATE_P, JFULL_P, SQUARE, J_ARMS = 50, 100, "0,0,100,100", (
    ("kit", {}), ("char-sender", dict(p0plans0b.SENDER_ON, MESH_SENDER_TIERS=p0plans0b.PARITY)))
# b7 (decision 13)
CAP_SINK_P, PROBE_P, PROBE_V = 100, (100, 200, 400, 800), 100
# b9 (decision 14): halves, 30/0.02; walks to x = +30 and back; pace-cut crosses the cut, pace-west never does
WALK_BOTS, PACE_BOTS, WALK_WARMUP_S, WALK_TAIL_S = (20, 50), 20, 10, 120
ASLEEP = {"MESH_SLEEP": "1", "MESH_SLEEP_RINGS": "0", "MESH_SLEEP_AFTER": "300"}   # regress's wake scenario


def bots(n, mode, placement, rig_ships=False, first=FIRST_BOT):
    """A peer's bots (decision 8): meshbot<first>..meshbot<first + n - 1> in auto mode stand, pace or walk at the
    placement's points, logging a Bot net window every 5 s. Refuses n < 1 or a first bot below 2 (meshbot is regress')."""
    if n < 1 or first < FIRST_BOT:
        raise ValueError(f"bots are meshbot{FIRST_BOT} and up, at least one; got first {first}, n {n}")
    return {"first": first, "n": n, "mode": mode, "placement": placement, "bot_net": BOT_NET_S, "rig_ships": rig_ships}


def sink(count, kind, sizes=False):
    """A peer's phantom sink (decision 8): count peers of kind on B, record sizes logged when sizes."""
    return {"count": count, "kind": kind, "sizes": sizes}


def bot_warmup(n, stagger=BOT_STAGGER_S):
    """A bot run's warm-up (decision 8): the bots' stagger x N + 45 s."""
    return stagger * n + BOT_SETTLE_S


def _spec8b(family, case, arm, point, repeat, game, kind, env, rates, peer=None, timing=None, **kw):
    """One step 8b spec: p0plans0b._spec0b (METER, seed, the 0b fields), then peer and the timing (default: a bot
    run's warm-up and 60 s, else 45 + 60 s). Refuses a peer run that would outlast a bot's 600 s of pacing."""
    spec = p0plans0b._spec0b(family, case, arm, point, repeat, game, kind, env, rates, **kw)
    crowd = (peer or {}).get("bots")
    warmup, measure = timing or (bot_warmup(crowd["n"]) if crowd else p0plans.LONG[0], p0plans.LONG[1])
    if peer and warmup + measure >= PACE_STOP_S:
        raise ValueError(f"{family} {case} {arm}: {warmup} + {measure} s outlasts a bot's {PACE_STOP_S} s of pacing")
    spec.update(warmup=warmup, measure=measure, peer=peer)
    return spec


def _peer(sink_part=None, bot_part=None):
    return {"sink": sink_part, "bots": bot_part}


def _part_a():
    def make(mode):
        env = {"MESH_PHANTOM": f"{mode}:{A_PHANTOMS}:character:C", "MESH_PHANTOM_VIEW": str(A_VIEW)}
        return _spec8b("accept-b", "A", mode, {"P": A_PHANTOMS, "view": A_VIEW, "rates": FANTASY_RATES}, 1, "fantasy",
                       "character", env, FANTASY_RATES, _peer(sink(A_PHANTOMS, "character")) if mode == "sink" else None)
    return [make("virtual"), make("sink")]


def _part_b():
    env = {"MESH_PHANTOM": f"virtual:{EYES}:eye:C", "MESH_PHANTOM_VIEW": str(EYE_VIEW)}
    return [_spec8b("accept-b", "B", "eyes-bot", {"eyes": EYES, "view": EYE_VIEW, "rates": FANTASY_RATES}, 1, "fantasy",
                    "character", env, FANTASY_RATES, _peer(bot_part=bots(1, "stand", "eye-centre")))]


def _part_c():
    specs = []
    for pair in range(1, C_PAIRS + 1):
        for arm in ("kit", "sender") if pair % 2 else ("sender", "kit"):
            env = dict(p0plans0b.SENDER_ON) if arm == "sender" else {}
            specs.append(_spec8b("accept-b", "C", arm, {"players": C_PLAYERS, "bots": C_BOTS, "rates": FANTASY_RATES},
                                 pair, "fantasy", "character", env, FANTASY_RATES,
                                 _peer(bot_part=bots(C_BOTS, "stand", "centre")), plan={"C": C_PLAYERS}, area=C_AREA))
    return specs


def _part_d():
    env = p0plans0b._merged(p0ships.scaled_env(D_SCALE), {"MESH_LOAD_SHIPS": D_SHIPS, "MESH_RIG_SHIPS": "1",
                                                          "MESH_SHIP_WIRE": "header56"})
    point = {"ships": D_SHIPS, "scale": D_SCALE, "rates": SPACE_RATES}
    return [_spec8b("accept-b", "D", "ship-bot", point, 1, "space", "ship", env, SPACE_RATES,
                    _peer(bot_part=bots(1, "stand", "centre", rig_ships=True)))]


def _part_e():
    env = dict(p0plans0b.SENDER_ON, MESH_CE12_CHECK="1")
    return [_spec8b("accept-b", "E", "ce12-sender", {"rates": FANTASY_RATES}, 1, "fantasy", "character", env,
                    FANTASY_RATES, timing=CE12_TIMING)]


def _part_f():
    specs = []
    for pair in range(1, F_PAIRS + 1):
        for arm in ("meter-off", "meter-on") if pair % 2 else ("meter-on", "meter-off"):
            env = {"MESH_PHANTOM": f"virtual:{F_EYES}:eye:C", "MESH_PHANTOM_VIEW": str(F_VIEW)}
            spec = _spec8b("accept-b", "F", arm, {"eyes": F_EYES, "view": F_VIEW, "rates": FANTASY_RATES}, pair,
                           "fantasy", "character", env, FANTASY_RATES)
            if arm == "meter-off":
                del spec["env"]["MESH_METER_SENDS"]   # the off arm runs without the send meter (decision 11 F)
            specs.append(spec)
    return specs


def accept_b_plan(parts=ACCEPT_B_PARTS):
    """b1 accept-b (decision 11) for the chosen parts, in part order: A virtual then sink character phantoms (50, the
    sink on B); B 50 eyes at VIEW 200 with 1 bot standing at the eye group's centre; C 50 synthetic players crowded on
    (0, 0) r 25 with 2 bots standing there, kit and sender in 2 pairs; D 40 mirror and 10 parked ships at scale 0.5,
    header56, MESH_RIG_SHIPS=1 and a ship bot at (0, 0); E MESH_CE12_CHECK=1 with the sender (no B); F 5 pairs of 400
    eyes at VIEW 100 without and with MESH_METER_SENDS=count (no B). Coarse C, no monsters; D at 30/0.02, the rest at
    60/0.03. 19 runs for every part. Refuses an unknown part."""
    chosen = [part.strip().upper() for part in (parts.split(",") if isinstance(parts, str) else parts)
              if part.strip()]
    unknown = sorted(set(chosen) - set(ACCEPT_B_PARTS))
    if unknown or not chosen:
        raise ValueError(f"accept-b has parts {', '.join(ACCEPT_B_PARTS)}; got {', '.join(unknown) or 'none'}")
    builders = {"A": _part_a, "B": _part_b, "C": _part_c, "D": _part_d, "E": _part_e, "F": _part_f}
    specs = [dict(spec, accept_part=part) for part in ACCEPT_B_PARTS if part in chosen for spec in builders[part]()]
    return p0plans._numbered(specs)


def upload_hz(calibration):
    """MESH_PHANTOM_UPLOAD_HZ for a phantom arm (decision 7): the bot calibration's measured upload rate rounded, or
    None without one. Refuses a rate the key refuses (1 to 60)."""
    rate = (calibration or {}).get("upload_hz")
    if rate is None:
        return None
    hz = int(round(rate))
    if not 1 <= hz <= 60:
        raise ValueError(f"the bots' measured upload rate {rate:g}/s rounds to {hz}, outside MESH_PHANTOM_UPLOAD_HZ's "
                         f"1 to 60")
    return hz


def _j_plan(family, case, p, calibration):
    """Gate J's runs at P (decisions 12, 15): arms kit and char-sender, each with P bots pacing at spread points then P
    sink character phantoms on B in the same square uploading at the bots' rate; coarse C at 60/0.03, 1 repeat."""
    hz = upload_hz(calibration)

    def arm(name, extra):
        def make(load, repeat):
            point = {"P": p, "load": load, "rates": FANTASY_RATES}
            if load == "bots":
                return _spec8b(family, case, name, point, repeat, "fantasy", "character", extra, FANTASY_RATES,
                               _peer(bot_part=bots(p, "pace", "spread")))
            env = dict(extra, MESH_PHANTOM=f"sink:{p}:character:C", MESH_PHANTOM_AREA=SQUARE,
                       **({"MESH_PHANTOM_UPLOAD_HZ": str(hz)} if hz else {}))
            spec = _spec8b(family, case, name, point, repeat, "fantasy", "character", env, FANTASY_RATES,
                           _peer(sink(p, "character")))
            spec["upload_hz"] = hz
            return spec
        return 1, ("bots", "phantoms"), make
    return p0plans._blocked([arm(name, extra) for name, extra in J_ARMS], 1)


def calibrate_plan(calibration=None):
    """b2 calibrate, J-lite (decision 12): _j_plan at P 50. 4 runs. Without a calibration the phantom arms carry
    upload_hz None and no MESH_PHANTOM_UPLOAD_HZ (p0load refuses to run them; a dry run prints them)."""
    return _j_plan("calibrate", "J-lite", CALIBRATE_P, calibration)


def jfull_refusal(calibration):
    """Why b10 is not planned (decision 7), or None: no calibration on this build, or one that does not say B hosts
    100 bots (p0peer.hosts' rule: it fits, and tried at least 100)."""
    if calibration is None:
        return "no bot calibration on this build: run p0load calibrate-bots first"
    if not (calibration.get("fits") and (calibration.get("tried") or 0) >= JFULL_P):
        return (f"not measured: B cannot host {JFULL_P} bots (the calibration tried {calibration.get('tried')}, "
                f"{calibration.get('in_map')} in the map, {calibration.get('hosted')} hosted)")
    return None


def jfull_plan(calibration, force=False):
    """b10 J-full (decision 15): _j_plan at P 100 (P 50 is b2 on the same build). 4 runs. Refuses (jfull_refusal)
    unless the calibration says B hosts 100 bots; force builds it anyway (a dry run's preview)."""
    refusal = jfull_refusal(calibration)
    if refusal and not force:
        raise ValueError(f"jfull: {refusal}")
    return _j_plan("jfull", "J-full", JFULL_P, calibration)


def packets_sink_plan(repeats=3):
    """b7's sink arms (family packets; decision 13) at 30/0.02. cap-sink: ship-sender-28 tiered at scale 0.5 with 100
    sink ship phantoms on B and 400 AI ships (V 500) x MESH_SENDER_PACKET_BYTES {1020, 510, 255}; cap: the same
    points virtual on this build (p0plans0b.packets_plan's P 100 cap specs, the same plan keys); interleaved, x 3.
    Then probe: P {100, 200, 400, 800} sink eyes on B watching 100 load characters, the tiered character sender,
    MESH_THREAD_CPU=1, one unstepped run each (sink mode refuses steps), a saturation probe. 22 runs by default."""
    def cap(mode):
        def make(size, repeat):
            env = p0plans0b._merged(p0ships.scaled_env(p0plans0b.TARGET_SCALE),
                                    p0plans0b.SHIP_EXTRA["ship-sender-28-tiered"],
                                    {"MESH_PHANTOM": f"{mode}:{CAP_SINK_P}:ship:C",
                                     "MESH_LOAD_SHIPS": f"C:{p0plans0b.PACKET_V - CAP_SINK_P}:ai",
                                     "MESH_SENDER_PACKET_BYTES": str(size)})
            point = {"P": CAP_SINK_P, "V": p0plans0b.PACKET_V, "packet_bytes": size, "rates": SPACE_RATES,
                     "scale": p0plans0b.TARGET_SCALE}
            return _spec8b("packets", "cap-sink" if mode == "sink" else "cap", "ship-sender-28-tiered", point, repeat,
                           "space", "ship", env, SPACE_RATES,
                           _peer(sink(CAP_SINK_P, "ship")) if mode == "sink" else None)
        return repeats, p0plans0b.PACKET_BYTES, make

    def probe(p):
        env = dict(p0plans0b.SENDER_ON, MESH_SENDER_TIERS=p0plans0b.TIERED, MESH_PHANTOM=f"sink:{p}:eye:C",
                   MESH_PHANTOM_VIEW=str(PROBE_V), MESH_THREAD_CPU="1")
        return _spec8b("packets", "probe", "char-sender-tiered", {"P": p, "V": PROBE_V, "rates": SPACE_RATES}, 1,
                       "fantasy", "character", env, SPACE_RATES, _peer(sink(p, "eye")), saturation_probe=True)
    caps = p0plans._blocked([cap("sink"), cap("virtual")], repeats)
    return p0plans._numbered(caps + [probe(p) for p in PROBE_P])


def crossings_plan(repeats=3):
    """b9 crossings and wake (family crossings; decision 14), halves (cut x = 0) at 30/0.02, C and E read. walk: N {20,
    50} bots at west points walking to x = +30 and back, arms awake and asleep (MESH_SLEEP=1, MESH_SLEEP_RINGS=0,
    MESH_SLEEP_AFTER=300: regress's wake scenario; E exempt from the coverage checks), warm-up 10 s, measured until 2N
    handoff outcomes or stagger x N + 120 s. pace: 20 bots pacing across the cut (pace-cut) or west of it (pace-west),
    stagger x N + 45 s warm-up, 60 s measured. Arms rotate by repeat; pace-cut and pace-west stay adjacent. 18 runs by
    default."""
    def walk(arm, extra):
        def make(n, repeat):
            spec = _spec8b("crossings", "walk", arm, {"bots": n, "neighbour": arm, "rates": SPACE_RATES}, repeat,
                           "fantasy", "character", extra, SPACE_RATES, _peer(bot_part=bots(n, "walk", "west")),
                           timing=(WALK_WARMUP_S, BOT_STAGGER_S * n + WALK_TAIL_S), layout="halves",
                           started=p0plans0b.HALVES)
            spec.update(stop_after_outcomes=2 * n, coverage_exempt=["E"] if extra else [])
            return spec
        return repeats, WALK_BOTS, make

    def pace(arm):
        def make(n, repeat):
            return _spec8b("crossings", "pace", arm, {"bots": n, "rates": SPACE_RATES}, repeat, "fantasy", "character",
                           {}, SPACE_RATES, _peer(bot_part=bots(n, "pace", arm)), layout="halves",
                           started=p0plans0b.HALVES)
        return repeats, (PACE_BOTS,), make
    return p0plans._blocked([walk("awake", {}), walk("asleep", ASLEEP), pace("pace-cut"), pace("pace-west")], repeats)
