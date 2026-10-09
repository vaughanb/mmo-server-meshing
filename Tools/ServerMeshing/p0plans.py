"""Phase 0 run plans (step 4): every stage 0a block as data, its run order and plan keys, and the third repeats a
2-repeat block may need. Pure: every input is an argument; nothing here runs the rig. p0dryrun prints the plans.

    p0plans.presence_plan()              # a block's RunSpecs in run order; also canary_plan, accept_plan,
                                         # rates_plan, density_plan and floor_plan
    p0plans.accept_commands("CD")        # accept-a parts C and D: the regress, unittest and ruff commands
    p0plans.abgeo_invocations(path)      # [(label, abload argv)]: a8 runs abload once per sub-block
    p0plans.third_repeat(specs, rows, "work_ms_per_s", THIRD_REPEAT_THRESHOLDS["floor"])   # the extra runs

A RunSpec is a dict: family, game, case, arm, point, repeat (1-based), seed, order (1-based position in its block),
layout (a rigrun.LAYOUTS name, or None for the ten-cell rig as regress starts it), cells_started, cells_read, plan
({cell: players}), env (the keys set; request_play clears every other regress.ENV_KEYS key), warmup, measure, rates
("60/0.03", or None when no MESH_RATES is set), uncapped (cells), band (metres or None), expect ({"puppets": {cell:
n}}, {"links": n}), key (plan_key); optionally checks_family (accept-a part E), replaces_repeat and third_reason
(third repeats). Every spec passes rigrun.check_env when it is built.
Unit tests: python -m unittest discover -s Tools/ServerMeshing -p "test_*.py"
"""
import copy
import hashlib
import json
import statistics

import p0base
import p0geo
import p0tools
import rigrun
import rigtools

INSTR = {"MESH_AOI_TIMING": "1", "MESH_WORK_BINS": "1", "MESH_THREAD_CPU": "1"}
LONG, SHORT = (45, 60), (25, 40)             # (warm-up, measured) seconds
RUN_SECONDS = {LONG: 126, SHORT: 90}         # a run's wall time, from the 2026-09-26 A/B log's 121-126 s spacing
RUN_OVERHEAD_S = 21                          # the same for any other timing, beside its warm-up and measurement
TEN_CELLS = ("SW", "S", "SE", "W", "C", "E", "NW", "N", "NE", "C2")   # regress.ZONES: the ten-cell rig
MAP_ONE = tuple(cid for cid, _, _ in rigrun.GRID_IDS)                 # the registry grid's nine cells
# Stage 0b families (p0plans0b) give each spec its own game; these are the families' defaults.
GAMES = {"canary": "neutral", "accept": "neutral", "rates": "neutral", "floor": "neutral", "presence": "fantasy",
         "density": "fantasy", "abgeo": "fantasy", "observed": "fantasy", "fanout": "fantasy", "targets": "fantasy",
         "syncrpc": "space", "packets": "fantasy", "prewarm": "fantasy", "accept-b": "fantasy", "calibrate": "fantasy",
         "crossings": "fantasy", "jfull": "fantasy"}
KINEMATIC = {"MESH_LOAD_MOTION": "kinematic"}
CANARY_ARMS, CANARY_RATES, CANARY_PLAYERS = ("baseline", "check"), "30/0.02", 200
RATE_POINTS = ("30/0.03", "30/0.02", "60/0.03", "60/0.02")   # block K's rate points, ascending
FLOOR_RATES = ("30/0.02", "60/0.03")
GRID_SIDES = (100, 200, 400, 600)
GROUND = 600
SCALE_POINTS = ((50, 70.71), (200, 141.42))    # 100 m² per character; 100 @ 100 is in the grid
ATTRIBUTION_POINT, SPARSE_POINT, BRIDGE_POINT = (200, 141.42), (200, 600), (500, 200)
BRIDGE_RATES = "60/0.03"                        # the bridge to 0.53 runs at 60 fps whatever the block's rates
REMOVALS = (("inert", {"MESH_LOAD_MOTION": "inert"}), ("no-fight", {"MESH_LOAD_FIGHT": "0"}), ("kinematic", KINEMATIC))
ANIMATION_OFF_POINT = 200
PRESENCE_BAND = 300.0
REGRESS = "Tools/ServerMeshing/regress.py"      # relative to the repository root, where p0load runs commands
SCENARIOS = ("pet", "party", "crash", "harvest", "build", "seam", "seamcold", "sleep", "pvp", "wake")
ABGEO = ((1, "b9-main", ("--puppets", "kinematic", "--players", "150,250", "--monsters", "off,all",
                         "--layouts", "coarse,halves,grid", "--repeats", "3")),
         (2, "b9-bands", ("--puppets", "kinematic", "--players", "250", "--monsters", "all",
                          "--layouts", "coarse,halves,strips4", "--bands", "20,40,80,150", "--repeats", "3")),
         (3, "b9-steered-b150", ("--puppets", "steered", "--players", "250", "--monsters", "all",
                                 "--layouts", "coarse,halves", "--bands", "150", "--repeats", "3")),
         (3, "b9-steered-b80", ("--puppets", "steered", "--players", "250", "--monsters", "all",
                                "--layouts", "halves,strips4", "--bands", "80", "--repeats", "3")))
ABGEO_COMMON = ("--instrument", "--order", "repeat-major", "--rates", "60/0.03")
STAGE_LATER = {"workerab": "stage 0c, step 9", "dbburst": "stage 0c, step 9", "spaces": "stage 0c, step 9"}
OBSERVED_REFUSAL = "presence --observed has moved: use p0load observed --part O (or R, SA, M, S)"
THIRD_SHARE, BASELINE_SHARE = 0.10, 0.025
# Utilisation 0.95 is 950 ms/s at any frame rate (ms/frame over the budget is ms/s over 1000).
SATURATION = (("work_ms_per_s", 950.0), ("sat_pct", 20.0))
THIRD_REPEAT_THRESHOLDS = {"floor": (("work_ms_per_s", 600.0),) + SATURATION, "density": SATURATION,
                           "rates": SATURATION}   # rates: only a 2-repeat block K (rates --repeats 2) has {1, 2} points


def plan_key(spec):
    """The first 12 hex digits of sha1("family|case|arm|point as sorted JSON|repeat"): a run's identity across
    invocations (p0load --resume), the same for the same planned run."""
    text = f"{spec['family']}|{spec['case']}|{spec['arm']}|{json.dumps(spec['point'], sort_keys=True)}|{spec['repeat']}"
    return hashlib.sha1(text.encode("utf-8")).hexdigest()[:12]


def stepped(points, repeat):
    """The points (given in ascending order) as run in a repeat: ascending on odd repeats, descending on even ones.
    Refuses a repeat below 1."""
    if repeat < 1:
        raise ValueError(f"repeats count from 1; got {repeat}")
    return list(points) if repeat % 2 == 1 else list(reversed(points))


def block_order(arms, repeats):
    """The arm order of each repeat, [[arms of repeat 1], ...]: repeat r rotates the arms left by (r - 1) x A // R
    (A arms, R repeats; offsets spread evenly and all below A, so no two repeats put an arm in the same position: 0,
    1, 2 for block K's 4 arms, 0, 3, 6 for 10 arms, 0, 3, 6 for 9); with A <= R it is rigtools.rotate(arms, r - 1),
    every position covered. Refuses fewer than 1 repeat."""
    if repeats < 1:
        raise ValueError(f"a block needs at least 1 repeat; got {repeats}")
    count = len(arms)
    offsets = [r - 1 if count <= repeats else (r - 1) * count // repeats for r in range(1, repeats + 1)]
    return [rigtools.rotate(list(arms), offset) for offset in offsets]


def _check_rates(rates):
    if rates is not None:
        _, _, _, error = p0tools.rates_setting(rates)
        if error:
            raise ValueError(f"rates {rates!r}: MESH_RATES {error}")


def _spec(family, case, arm, point, repeat, layout, started, read, plan=None, extra=None, rates="60/0.03", seed=None,
          band=None, uncapped=(), expect=None, timing=LONG, monsters="off", instrument=True, area=None):
    """One RunSpec, its env assembled: layout (override and started cells; none for the ten-cell rig), load plan and
    seed (seed = repeat unless given), crowd area, monsters (rigrun.MONSTER_ENV: off, edge = the scene default, all),
    MESH_RATES, MESH_BAND, MESH_UNCAPPED_CELLS, INSTR, then the arm's own keys. Empty values are left out (request_play
    sends an unset key empty). Raises ValueError from rigrun.check_env, for bad rates and for an unknown layout."""
    if layout is not None and layout not in rigrun.LAYOUTS:
        raise ValueError(f"layout {layout!r} is not one of {', '.join(rigrun.LAYOUTS)} (or None for the ten-cell rig)")
    _check_rates(rates)
    seed = repeat if seed is None else seed
    env = {}
    if layout is not None:
        env.update(MESH_LAYOUT_OVERRIDE=rigrun.layout_override(layout), MESH_SPAWN_CELLS=",".join(started))
    if plan:
        env.update(MESH_LOAD_PLAN=rigtools.plan_env(plan), MESH_LOAD_SEED=str(seed))
    env.update(rigrun.MONSTER_ENV[monsters])
    env.update(MESH_LOAD_AREA=area or "", MESH_RATES=rates or "", MESH_UNCAPPED_CELLS=",".join(uncapped),
               MESH_BAND="" if band is None else f"{band:g}")
    if instrument:
        env.update(INSTR)
    env.update(extra or {})
    env = {key: value for key, value in env.items() if value}
    rigrun.check_env(env)
    spec = {"family": family, "game": GAMES[family], "case": case, "arm": arm, "point": point, "repeat": repeat,
            "seed": seed, "order": 0, "layout": layout, "cells_started": list(started), "cells_read": list(read),
            "plan": dict(plan or {}), "env": env, "warmup": timing[0], "measure": timing[1], "rates": rates,
            "uncapped": list(uncapped), "band": band, "expect": copy.deepcopy(expect or {})}
    spec["key"] = plan_key(spec)
    return spec


def _numbered(specs):
    for order, spec in enumerate(specs, 1):
        spec["order"] = order
    return specs


def _blocked(arms, repeats):
    """Specs of a block: arms are (repeats, points, make(point, repeat)); arms rotate by block_order, each arm's points
    run back to back, stepped by repeat; an arm with fewer repeats sits out the later ones."""
    specs = []
    for repeat, order in enumerate(block_order(arms, repeats), 1):
        for arm_repeats, points, make in order:
            if repeat <= arm_repeats:
                specs += [make(point, repeat) for point in stepped(points, repeat)]
    return _numbered(specs)


def canary_plan(arm, runs=1):
    """The canary (family canary, game neutral): coarse layout, C alone with 200 kinematic owners at 30/0.02, seed 1,
    INSTR; `runs` specs of arm baseline (a2, once per build) or check (every 2 h, invocation start and end).
    Refuses another arm or fewer than 1 run."""
    if arm not in CANARY_ARMS or runs < 1:
        raise ValueError(f"a canary plan is arm baseline or check with at least 1 run; got {arm!r}, {runs}")
    point = {"players": CANARY_PLAYERS, "rates": CANARY_RATES}
    return _numbered([_spec("canary", "canary", arm, point, repeat, "coarse", ["C"], ["C"], {"C": CANARY_PLAYERS},
                            KINEMATIC, rates=CANARY_RATES, seed=1) for repeat in range(1, runs + 1)])


def baseline_extra(works, share=BASELINE_SHARE):
    """Whether the canary baseline needs a third run: its two runs' C work ms/s differ by more than 2.5 % of the lower.
    Refuses anything but two positive values."""
    if len(works) != 2 or min(works) <= 0:
        raise ValueError(f"a canary baseline compares two positive work figures; got {list(works)}")
    low, high = sorted(works)
    return p0base.above((high - low) / low, share)


def _parts(parts, known):
    chosen = [part.strip().upper() for part in parts if part.strip() not in ("", ",")]
    unknown = sorted(set(chosen) - set("ABCDE"))
    if unknown:
        raise ValueError(f"accept-a has parts A, B, C, D and E; got {', '.join(unknown)}")
    return [part for part in known if part in chosen]


def _accept_instrument():
    burn = {"MESH_PROBE_BURN_TICK_MS": "C:2"}

    def rig(arm, rates, instrument, extra=None, repeat=1):
        return _spec("accept", "instrument", arm, {"rates": rates} if rates else {}, repeat, None, TEN_CELLS, ["C"],
                     extra=extra, rates=rates, instrument=instrument, monsters="edge", timing=SHORT)

    specs = [rig("plain", None, False), rig("r60", "60/0.03", True), rig("r60burn", "60/0.03", True, burn),
             rig("r30", "30/0.02", True), rig("r30burn", "30/0.02", True, burn)]
    for pair in range(1, 6):
        for side in ("A", "B") if pair % 2 == 1 else ("B", "A"):
            specs.append(rig(f"overhead-{side}", "60/0.03", side == "B", repeat=pair))
    return specs


def _accept_puppets():
    arms = (("steered", {}), ("kin", {"MESH_PUPPET_MOTION": "kinematic"}),
            ("kin_kwalk", {"MESH_PUPPET_MOTION": "kinematic", "MESH_LOAD_MOTION": "kinematic"}),
            ("kin_inert", {"MESH_PUPPET_MOTION": "kinematic", "MESH_LOAD_MOTION": "inert"}))
    point = {"players": 60, "area": "80,0,30", "rates": "60/0.03"}
    return [_spec("accept", "puppets", arm, point, 1, None, TEN_CELLS, ["C", "E"], {"C": 60}, extra,
                  area="80,0,30") for arm, extra in arms]


def _accept_first_use():
    def coarse(arm, family, rates="60/0.03", plan=None, extra=None, uncapped=()):
        spec = _spec("accept", "first-use", arm, {"players": sum((plan or {}).values()), "rates": rates}, 1, "coarse",
                     ["C"], ["C"], plan, extra, rates=rates, uncapped=uncapped, timing=SHORT)
        spec["checks_family"] = family
        return spec

    band300 = _spec("accept", "first-use", "band300", {"players": 60, "rates": "60/0.03"}, 1, "halves", ["C", "E"],
                    ["C", "E"], {"C": 60}, dict(KINEMATIC, MESH_PUPPET_MOTION="kinematic"), band=PRESENCE_BAND,
                    expect={"puppets": {"E": 60}}, timing=SHORT)
    band300["checks_family"] = "presence"
    return [coarse("r30s03", "rates", "30/0.03"), coarse("r60s02", "rates", "60/0.02"),
            coarse("uncap", "density", uncapped=("C",)),
            coarse("nofight", "density", plan={"C": 60}, extra={"MESH_LOAD_FIGHT": "0"}),
            coarse("noanim", "presence", plan={"C": 60}, extra={"MESH_RIG_SERVER_ANIMATION": "0"}), band300]


def accept_plan(parts=("A", "B", "E")):
    """Accept-a's rig runs (family accept, game neutral) for the chosen parts, in part order. A instrument (ten-cell
    rig, 25 + 40 s, read C): plain {} (no INSTR, no MESH_RATES), r60, r60burn, r30, r30burn, then 5 overhead pairs
    AB BA AB BA AB (A = 60/0.03, B = A + INSTR). B puppets (ten-cell rig, read C and E): steered, kin, kin_kwalk,
    kin_inert with 60 players crowded at 80,0,30. E first-use (25 + 40 s, coarse C; band300 halves C, E), each graded
    with its target block's checks (checks_family). Parts C and D are commands: accept_commands. Refuses an unknown
    part."""
    chosen = _parts(parts, "ABE")
    builders = {"A": _accept_instrument, "B": _accept_puppets, "E": _accept_first_use}
    return _numbered([spec for part in chosen for spec in builders[part]()])


def accept_commands(parts=("C", "D")):
    """Accept-a parts C (regress scenarios; p0load reruns a failed scenario once) and D (tests) as
    [(part, label, argv, python)]: argv is relative to the repository root, and python says whether it runs under
    the Python interpreter (prefix sys.executable) or as its own program. Refuses an unknown part."""
    chosen = _parts(parts, "CD")
    commands = {"C": [("C", "scenarios", [REGRESS, *SCENARIOS], True),
                      ("C", "scenarios-dynamic", [REGRESS, *SCENARIOS, "--env", "MESH_CELL_MODE=all:Dynamic"], True),
                      ("C", "dynidle", [REGRESS, "dynidle"], True),
                      ("C", "kinematic", [REGRESS, *SCENARIOS, "dynidle", "--env", "MESH_PUPPET_MOTION=kinematic"],
                       True)],
                "D": [("D", "regress-unit", [REGRESS, "--unit"], True),
                      ("D", "unittest", ["-m", "unittest", "discover", "-s", "Tools/ServerMeshing", "-p", "test_*.py"],
                       True),
                      ("D", "ruff", ["-m", "ruff", "check", "Tools/ServerMeshing"], True)]}
    return [command for part in chosen for command in commands[part]]


def rates_plan(repeats=3, points=RATE_POINTS, players=200):
    """Block K (a3, family rates, game neutral): coarse, C alone, INSTR, C:200 (idle: none); arms idle, steered,
    kinematic, inert in block order, each arm's rate points stepped: 4 x 4 x 3 = 48 runs by default."""
    arms = (("idle", None, {}), ("steered", {"C": players}, {}), ("kinematic", {"C": players}, KINEMATIC),
            ("inert", {"C": players}, {"MESH_LOAD_MOTION": "inert"}))

    def arm_entry(arm, plan, extra):
        return (repeats, points, lambda rates, repeat: _spec(
            "rates", "K", arm, {"players": players if plan else 0, "rates": rates}, repeat, "coarse", ["C"], ["C"],
            plan, extra, rates=rates))

    return _blocked([arm_entry(*arm) for arm in arms], repeats)


def presence_plan(repeats=3, rates="60/0.03", points=(100, 200, 400), steered_points=(100, 200, 250),
                  additivity_kinematic=(200, 400), additivity_steered=(100, 200)):
    """B1-U (a4, family presence, game fantasy): halves, MESH_BAND=300, the rates given, INSTR, no monsters, spread load
    over the owning cell. Arms: baseline (no load); main puppets-steered, puppets-kinematic, owners-inert (owners on
    C, E holds the puppets); additivity (owners on C and E); solo owners-steered, owners-kinematic (C alone);
    animation-off (the two puppet arms at 200 with MESH_RIG_SERVER_ANIMATION=0). 66 runs by default."""
    kin_puppets = dict(KINEMATIC, MESH_PUPPET_MOTION="kinematic")
    inert = {"MESH_LOAD_MOTION": "inert", "MESH_PUPPET_MOTION": "kinematic"}
    no_anim = {"MESH_RIG_SERVER_ANIMATION": "0"}
    halves, solo, off = ("C", "E"), ("C",), (ANIMATION_OFF_POINT,)
    arms = (("baseline", "idle", halves, (0,), {}),
            ("main", "puppets-steered", halves, steered_points, KINEMATIC),
            ("main", "puppets-kinematic", halves, points, kin_puppets),
            ("main", "owners-inert", halves, points, inert),
            ("additivity", "puppets-steered", halves, additivity_steered, KINEMATIC),
            ("additivity", "puppets-kinematic", halves, additivity_kinematic, kin_puppets),
            ("solo", "owners-steered", solo, steered_points, {}),
            ("solo", "owners-kinematic", solo, points, KINEMATIC),
            ("animation-off", "puppets-steered", halves, off, dict(KINEMATIC, **no_anim)),
            ("animation-off", "puppets-kinematic", halves, off, dict(kin_puppets, **no_anim)))

    def arm_entry(case, arm, started, arm_points, extra):
        def make(n, repeat):
            both = case == "additivity"
            plan = {"C": n, "E": n} if both else ({"C": n} if n else {})
            point = dict({"players": n}, **({"players_E": n} if both else {}), rates=rates)
            holders = {"C": n, "E": n} if both else ({"E": n} if case in ("main", "animation-off") else None)
            return _spec("presence", case, arm, point, repeat, "halves", started, started, plan, extra, rates=rates,
                         band=PRESENCE_BAND, expect={"puppets": holders} if holders else {})
        return (repeats, sorted(arm_points), make)

    return _blocked([arm_entry(*arm) for arm in arms], repeats)


def density_plan(rates="60/0.03", players=(100, 200)):
    """B4 (a6, family density, game fantasy): coarse, C alone, the rates given, INSTR, steered owners fighting, no
    monsters unless stated, a crowd square MESH_LOAD_AREA=0,0,side/2 (none at 600 m). Arms, 3 repeats each: grid/base
    (N x side 100-600, points by N then denser), scale/base (50 @ 70.71 and 200 @ 141.42), attribution inert,
    no-fight, kinematic, monsters-on (200 @ 141.42), sparse inert, no-fight, kinematic (200 @ 600); bridge-0.53/uncapped
    2 repeats (the grid's nine cells, read C, which must link with 8, C:500 at 60/0.03 uncapped, scene monsters). 53
    runs by default."""
    def crowd(case, arm, extra=None, monsters="off"):
        def make(point, repeat):
            n, side = point
            return _spec("density", case, arm, {"players": n, "side": side, "rates": rates}, repeat, "coarse", ["C"],
                         ["C"], {"C": n}, extra, rates=rates, monsters=monsters,
                         area=None if side >= GROUND else f"0,0,{side / 2:g}")
        return make

    def bridge(point, repeat):
        n, side = point
        stated = {"players": n, "side": side, "rates": BRIDGE_RATES, "uncapped": "C"}
        return _spec("density", "bridge-0.53", "uncapped", stated, repeat, "grid", MAP_ONE, ["C"], {"C": n},
                     rates=BRIDGE_RATES, uncapped=("C",), monsters="edge", expect={"links": len(MAP_ONE) - 1})

    grid = sorted(((n, side) for n in players for side in GRID_SIDES), key=lambda p: (p[0], -p[1]))
    arms = [(3, grid, crowd("grid", "base")), (3, SCALE_POINTS, crowd("scale", "base"))]
    arms += [(3, (ATTRIBUTION_POINT,), crowd("attribution", arm, extra)) for arm, extra in REMOVALS]
    arms.append((3, (ATTRIBUTION_POINT,), crowd("attribution", "monsters-on", monsters="all")))
    arms += [(3, (SPARSE_POINT,), crowd("sparse", arm, extra)) for arm, extra in REMOVALS]
    arms.append((2, (BRIDGE_POINT,), bridge))
    return _blocked(arms, 3)


def floor_plan(repeats=2):
    """The process floor (a7, family floor, game neutral): INSTR, no monsters, at 30/0.02 and 60/0.03; cell/idle and
    cell/players-150 (coarse, C alone) and linked/idle (the grid's nine cells, read C, which must link with 8). 12
    runs by default."""
    arms = (("cell", "idle", "coarse", ("C",), None), ("cell", "players-150", "coarse", ("C",), {"C": 150}),
            ("linked", "idle", "grid", MAP_ONE, None))

    def arm_entry(case, arm, layout, started, plan):
        expect = {"links": len(started) - 1} if case == "linked" else {}
        return (repeats, FLOOR_RATES, lambda rates, repeat: _spec(
            "floor", case, arm, {"players": sum((plan or {}).values()), "rates": rates}, repeat, layout, started, ["C"],
            plan, rates=rates, expect=expect))

    return _blocked([arm_entry(*arm) for arm in arms], repeats)


def abgeo_invocations(jsonl_path, subs=(1, 2, 3)):
    """a8 (B9) as [(label, abload argv)], one abload run per invocation, each preceded by a canary: sub-block 1
    b9-main, 2 b9-bands, 3 the steered controls b9-steered-b150 and b9-steered-b80; every argv adds --instrument
    --order repeat-major --rates 60/0.03 --jsonl <path> --case <label>. 84 abload runs for all three. Refuses an
    unknown sub-block."""
    unknown = sorted(set(subs) - {sub for sub, _, _ in ABGEO})
    if unknown:
        raise ValueError(f"abgeo has sub-blocks 1, 2 and 3; got {unknown}")
    return [(label, ["ab", *args, *ABGEO_COMMON, "--jsonl", jsonl_path, "--case", label])
            for sub, label, args in ABGEO if sub in subs]


def ab_run_count(argv):
    """The runs an abload ab argv makes: players x monsters x bands (1 without --bands) x layouts x repeats. Refuses
    an argv without --players, --monsters, --layouts or --repeats (this reads only what the invocation states)."""
    values, flag = {}, None
    for token in argv:
        if token.startswith("--"):
            flag = token
        elif flag:
            values[flag], flag = token, None
    missing = [name for name in ("--players", "--monsters", "--layouts", "--repeats") if name not in values]
    if missing:
        raise ValueError(f"abload argv states no {', '.join(missing)}: {' '.join(argv)}")
    count = int(values["--repeats"])
    for name in ("--players", "--monsters", "--layouts", "--bands"):
        count *= len([item for item in values.get(name, "x").split(",") if item.strip()])
    return count


def _point_id(spec):
    return spec["family"], spec["case"], spec["arm"], json.dumps(spec["point"], sort_keys=True)


def _accepted(rows):
    """{plan_key: the last accepted run row with it}."""
    found = {}
    for row in rows:
        if row.get("row", "run") == "run" and row.get("accepted") and row.get("plan_key"):
            found[row["plan_key"]] = row
    return found


def _third_reason(runs, cell, key_metric, thresholds):
    values = [p0geo.metric(row, cell, key_metric) for row in runs]
    low, high = min(values), max(values)
    if low <= 0:
        raise ValueError(f"{cell} {key_metric} {low:g} is not positive, so its repeats cannot be compared")
    share = (high - low) / low
    if p0base.above(share, THIRD_SHARE):
        return f"repeats 1 and 2 differ by {share:.1%} on {cell} {key_metric} ({values[0]:.1f}, {values[1]:.1f})"
    for name, threshold in thresholds:
        pair = [p0geo.metric(row, cell, name) for row in runs]
        if p0base.above(pair[0], threshold) != p0base.above(pair[1], threshold):
            return f"repeats 1 and 2 straddle {name} {threshold:g} on {cell} ({pair[0]:.1f}, {pair[1]:.1f})"
    return None


def third_repeat(specs, rows, key_metric, thresholds=()):
    """The third-repeat specs of a block's 2-repeat (arm, point)s whose two accepted runs (rows by plan_key) differ by
    more than 10 % of the lower on the first read cell's key_metric (a mean metric), or lie on opposite sides of a
    threshold (metric name, value) of THIRD_REPEAT_THRESHOLDS. Each is repeat 3, seed 3, appended after the block's
    last order, with third_reason and replaces_repeat None: the repeat it replaces depends on its own result, so
    replaced_repeat names it once the run is in. A point missing an accepted run adds nothing."""
    found, groups = _accepted(rows), {}
    for spec in specs:
        groups.setdefault(_point_id(spec), {})[spec["repeat"]] = spec
    order, extra = max((spec["order"] for spec in specs), default=0), []
    for repeats in groups.values():
        if sorted(repeats) != [1, 2]:
            continue
        runs = [found.get(repeats[repeat]["key"]) for repeat in (1, 2)]
        if None in runs:
            continue
        reason = _third_reason(runs, repeats[1]["cells_read"][0], key_metric, thresholds)
        if reason:
            order += 1
            third = copy.deepcopy(repeats[2])
            third.update(repeat=3, seed=3, order=order, replaces_repeat=None, third_reason=reason)
            if "MESH_LOAD_SEED" in third["env"]:
                third["env"]["MESH_LOAD_SEED"] = "3"
            third["key"] = plan_key(third)
            extra.append(third)
    return extra


def replaced_repeat(spec, rows, key_metric):
    """The repeat (1 or 2) a third-repeat run replaces in its fit's jackknife groups: of the three runs at its (arm,
    point), the original further from their median on the first read cell's key_metric (a tie replaces repeat 1).
    Refuses when any of the three has no accepted row."""
    found, cell, values = _accepted(rows), spec["cells_read"][0], {}
    for repeat in (1, 2, 3):
        key = plan_key(dict(spec, repeat=repeat))
        if key not in found:
            raise ValueError(f"{spec['family']} {spec['case']} {spec['arm']} repeat {repeat} (key {key}) has no "
                             f"accepted run row")
        values[repeat] = p0geo.metric(found[key], cell, key_metric)
    median = statistics.median(values.values())
    return 1 if abs(values[1] - median) >= abs(values[2] - median) else 2


def run_seconds(spec, per_run_s=None):
    """A run's wall time in seconds: per_run_s[(warmup, measure)] (default RUN_SECONDS), else warm-up + measurement
    + RUN_OVERHEAD_S."""
    per_run_s = RUN_SECONDS if per_run_s is None else per_run_s
    timing = (spec["warmup"], spec["measure"])
    return per_run_s.get(timing, timing[0] + timing[1] + RUN_OVERHEAD_S)


def later_refusal(name):
    """The refusal p0load prints (exit 2) for a family a later stage builds, naming its stage and step. Refuses a name
    that is not one of STAGE_LATER."""
    if name not in STAGE_LATER:
        raise ValueError(f"{name!r} is not a later-stage family; known: {', '.join(STAGE_LATER)}")
    return f"p0load {name} needs {STAGE_LATER[name]}; no runs are built for it yet"
