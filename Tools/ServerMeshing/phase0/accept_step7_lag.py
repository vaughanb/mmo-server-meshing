"""Step 7b decision 9 for accept_step7.py's bots group: the bot's lag p50 per window beside the Ships window's mirror age.

The lag is the age of a mirror ship's record when the server wrote it; the load mirror feeder and the logic tick both
run at 20 Hz on frames, so the age sits at k frames and steps when a hitch resets a phase. The steps of the bot's lag
must coincide with the steps of the mirror age's frame count K (a check: a mismatch is a timing bug and blocks step 8);
the lag's range, [0, 60] ms at 60 fps, is reported only.
"""
LAG_STEP_MS = 6                # a lag p50 change this large is a step (the levels sit 10-12 ms apart)
LAG_EXPECTED_MS = (0, 60)      # the bot's lag at 60 fps once its steps follow the feed phase


def lag_beside_age(bot_windows, ships_windows, to_utc):
    """Each timed Bot ships window's lag p50 beside the mirror age of the Ships window that ended nearest its time
    (within that window's length): [(lag ms, frames K, age ms)], K and age None where no Ships window is that near or
    it has no mirror age (a step 7 build, or n 0). to_utc converts a Ships window's end stamp to the bot's clock."""
    ended = [(to_utc(x["end"]), x) for x in ships_windows if x.get("end") is not None]
    rows = []
    for b in bot_windows:
        near = min(ended, key=lambda e: abs((e[0] - b["at"]).total_seconds()), default=None)
        if near is None or abs((near[0] - b["at"]).total_seconds()) > max(near[1]["seconds"], 1.0):
            rows.append((b["lag_p50_ms"], None, None))
        else:
            rows.append((b["lag_p50_ms"], near[1].get("mirror_age_frames"), near[1].get("mirror_age_ms")))
    return rows


def steps_coincide(rows):
    """True when every lag p50 step (a change of LAG_STEP_MS or more between neighbouring windows) has a mirror age K
    change at the same window or one beside it, and every K change a lag step; rows are (lag, K, age) with K known."""
    lag_steps = {i for i in range(1, len(rows)) if abs(rows[i][0] - rows[i - 1][0]) >= LAG_STEP_MS}
    k_steps = {i for i in range(1, len(rows)) if rows[i][1] != rows[i - 1][1]}

    def near(i, steps):
        return bool({i - 1, i, i + 1} & steps)
    return all(near(i, k_steps) for i in lag_steps) and all(near(i, lag_steps) for i in k_steps)


LAG_AGE_TOLERANCE_MS = 10      # a window's lag p50 may sit this far from the mirror age p50 beside it
LAG_AGE_SHARE = 0.9            # share of windows that must: a bot window and the nearest Ships window are not aligned,
                               # so a phase change inside one of them shows a level apart (A, 2026-10-04: 1 in 11)


def follows_age(rows, i):
    """True when row i's lag p50 is within LAG_AGE_TOLERANCE_MS of the mirror age of row i or a row beside it: the bot's
    and the server's 5 s windows are offset by up to one window, so a phase episode shorter than a window shows one
    window apart in the two series (A, 2026-10-04: every mismatch had its level in the neighbouring window)."""
    ages = [rows[k][2] for k in (i - 1, i, i + 1) if 0 <= k < len(rows) and rows[k][2] is not None]
    return any(abs(rows[i][0] - age) <= LAG_AGE_TOLERANCE_MS for age in ages)


def grade_lag(label, bot_windows, ships_windows, to_utc, check, note):
    """Checks that the bot's lag follows the mirror age level by level (within LAG_AGE_TOLERANCE_MS in LAG_AGE_SHARE of
    the windows that have a mirror age beside them, and those are LAG_AGE_SHARE of all); reports the rows, whether the
    steps coincide, and how many lie in LAG_EXPECTED_MS. check(ok, text) and note(text) are the runner's."""
    rows = lag_beside_age(bot_windows, ships_windows, to_utc)
    known = [row for row in rows if row[2] is not None]
    lo, hi = LAG_EXPECTED_MS
    inside = sum(lo <= row[0] <= hi for row in rows)
    note(f"    reported: {label} lag p50 ms / mirror age frames K / age ms per window {rows}; "
         f"{inside} of {len(rows)} windows in [{lo}, {hi}] ms (60 fps); steps coincide: "
         f"{steps_coincide([r for r in rows if r[1] is not None])}")
    check(bool(rows) and len(known) >= LAG_AGE_SHARE * len(rows),
          f"{label}: a mirror age beside the timed bot windows ({len(known)} of {len(rows)})")
    following = sum(follows_age(rows, i) for i in range(len(rows)) if rows[i][2] is not None)
    check(bool(known) and following >= LAG_AGE_SHARE * len(known),
          f"{label}: the lag follows the mirror age within {LAG_AGE_TOLERANCE_MS} ms in {following} of {len(known)} "
          f"windows (>= {LAG_AGE_SHARE:.0%}; otherwise a timing bug: blocks step 8)")
