"""Phase 0 dry runs (step 4): the load each planned run should put on its cells, and the lines p0load --dry-run and
abload --dry-run print for a plan. Pure: every input is an argument; nothing here runs the rig. Split out of p0plans
(the ~500-line rule), whose RunSpecs, RUN_SECONDS and run_seconds these read.

    p0dryrun.predict_load(spec)      # per started cell: ms/frame, util and [predicted saturated] (label E)
    p0dryrun.dry_run_lines(specs)    # what p0load --dry-run prints

Unit tests: python -m unittest discover -s Tools/ServerMeshing -p "test_*.py"
"""
import p0base
import p0geo
import p0plans
import rigrun

# predict_load's costs, ms per frame at 60 fps (label E): 0.71's steered owner and puppet and monster costs; step 3's
# saved C logs (one run each, streaming included) for kinematic and inert owners and kinematic puppets.
PREDICT_FPS, C0_MS = 60, 0.60
OWNER_MS = {"steered": 0.0432, "kinematic": 0.0103, "inert": 0.0164}
PUPPET_MS = {"steered": 0.0440, "kinematic": 0.0047}
MONSTER_MS, MONSTER_PUPPET_MS = 0.0037, 0.0087
MONSTERS = {"off": 0, "edge": 4, "all": 294}    # abload's monster settings (label E)
PREDICTED_SATURATED = 0.85                      # a chain3 window at util 0.88 already had sat 23.1 %


def _cells(spec):
    """[(cell, rect)] of the started cells with a rectangle (the ten-cell rig: the registry grid's; C2 has none)."""
    rects = dict(rigrun.LAYOUTS["grid" if spec["layout"] is None else spec["layout"]])
    return [(cid, rects[cid]) for cid in spec["cells_started"] if cid in rects]


def _motion(env, key, costs):
    motion = (env.get(key) or "steered").lower()
    if motion not in costs:
        raise ValueError(f"{key} {motion!r} has no predicted cost; known: {', '.join(costs)}")
    return motion


def predict_load(spec, monster_counts=None):
    """Per started cell {ms_per_frame, ms_per_s, util, saturated, uncapped, label "E"} at 60 fps (whatever the spec's
    rates): 0.60 + 0.0432 per steered, 0.0103 per kinematic, 0.0164 per inert owner + 0.0440 per steered, 0.0047 per
    kinematic puppet + 0.0037 per owned monster + 0.0087 per monster puppet ms/frame; util = ms/s / 1000; saturated
    ("[predicted saturated]") at util 0.85 or more on a capped cell. Owners come from the plan and puppets from
    expect["puppets"]; an abgeo spec (expect {}) takes its puppets from p0geo.layout_loads (spread load). Monsters use
    monster_counts {cell: {O_monster, U_monster}} when given, else abload's count for the spec's setting (MONSTERS)
    spread uniformly. Raises ValueError for a motion without a cost."""
    env, cells = spec["env"], _cells(spec)
    owner, puppet = _motion(env, "MESH_LOAD_MOTION", OWNER_MS), _motion(env, "MESH_PUPPET_MOTION", PUPPET_MS)
    band = spec["band"] or 80.0
    setting = "off" if env.get("MESH_NO_MONSTERS") == "1" else ("all" if env.get("MESH_MONSTER_AREAS") == "all" else
                                                                "edge")
    if monster_counts is None:
        monster_counts = {}
        if MONSTERS[setting] and cells:
            spread = p0geo.layout_loads(cells, band, MONSTERS[setting])
            monster_counts = {cid: {"O_monster": load["O"], "U_monster": load["U"]} for cid, load in spread.items()}
    puppets = spec["expect"].get("puppets") or {}
    if spec["family"] == "abgeo" and cells:
        spread = p0geo.layout_loads(cells, band, sum(spec["plan"].values()), 0, owner, puppet)
        puppets = {cid: load["U"] for cid, load in spread.items()}
    out = {}
    for cid in spec["cells_started"]:
        monsters = monster_counts.get(cid) or {}
        ms = (C0_MS + spec["plan"].get(cid, 0) * OWNER_MS[owner] + puppets.get(cid, 0) * PUPPET_MS[puppet]
              + monsters.get("O_monster", 0.0) * MONSTER_MS + monsters.get("U_monster", 0.0) * MONSTER_PUPPET_MS)
        util, uncapped = ms * PREDICT_FPS / 1000.0, cid in spec["uncapped"]
        out[cid] = {"ms_per_frame": ms, "ms_per_s": ms * PREDICT_FPS, "util": util, "uncapped": uncapped,
                    "saturated": not uncapped and p0base.at_least(util, PREDICTED_SATURATED), "label": "E"}
    return out


def _predicted(cid, load):
    text = f"{cid} {load['ms_per_frame']:.1f} ms/frame util {load['util']:.2f}"
    return text + (" (uncapped)" if load["uncapped"] else "") + (" [predicted saturated]" if load["saturated"] else "")


def dry_run_lines(specs, per_run_s=None, env_keys=None):
    """What p0load --dry-run prints: a header naming how many regress.ENV_KEYS keys no run sets (env_keys, default
    regress.ENV_KEYS through rigrun), then per run '[i/n] family case arm point rN seed S key K | layout L | start ...
    | read ... | W+M s', its env (the keys set, sorted) and predict_load's figures (label E), and a footer with the
    runs ("1 run", "12 runs") and minutes (p0plans.run_seconds)."""
    per_run_s = p0plans.RUN_SECONDS if per_run_s is None else per_run_s
    env_keys = rigrun.r.ENV_KEYS if env_keys is None else env_keys
    used = {key for spec in specs for key in spec["env"]}
    lines = [f"every other regress.ENV_KEYS key is cleared ({len(set(env_keys) - used)} keys)"]
    for index, spec in enumerate(specs, 1):
        point = " ".join(f"{key}={value}" for key, value in spec["point"].items()) or "-"
        extra = "".join(f" | {name} {spec[name]}" for name in ("checks_family", "third_reason") if spec.get(name))
        lines.append(f"[{index}/{len(specs)}] {spec['family']} {spec['case']} {spec['arm']} {point} r{spec['repeat']} "
                     f"seed {spec['seed']} key {spec['key']} | layout {spec['layout'] or 'ten-cell rig'} | start "
                     f"{','.join(spec['cells_started'])} | read {','.join(spec['cells_read'])} | "
                     f"{spec['warmup']}+{spec['measure']} s{extra}")
        lines.append("    env: " + (" ".join(f"{key}={value}" for key, value in sorted(spec["env"].items())) or "-"))
        lines.append("    predicted (E): " + ", ".join(_predicted(cid, load) for cid, load in predict_load(spec).items()))
    minutes = sum(p0plans.run_seconds(spec, per_run_s) for spec in specs) / 60.0
    rates = " and ".join(f"{seconds} s per {w}+{m} run" for (w, m), seconds in per_run_s.items())
    count = "1 run" if len(specs) == 1 else f"{len(specs)} runs"
    lines.append(f"{count}, about {minutes:.0f} min at {rates}")
    return lines
