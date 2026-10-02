"""Phase 0 base helpers (step 4): the leaf every other pure phase 0 module may import. Pure: no files, clock or
environment.

    p0base.at_most(0.8 - 0.75, 0.05)          # True: thresholds compare with a relative slack EPS
    p0base.ci_verdict(lo, hi, 0.5)            # GO / NO-GO / MARGINAL for a "value <= threshold" gate
    p0base.kill_verdict(lo, hi, 600.0)        # KILL / NOT KILLED / MARGINAL for the kill rule
    p0base.point_verdict_eps(ratios, 0.75, 2) # rigtools.point_verdict's rule, exact at the threshold
    p0base.t975(4)                            # 2.776, the two-sided 95 % t quantile
    p0base.threshold_text(600.0, (60, 30))    # "600 ms/s = 10.00 ms/frame at 60 fps, 20.00 ms/frame at 30 fps"

Unit tests: python -m unittest discover -s Tools/ServerMeshing -p "test_*.py"
"""
EPS = 1e-9   # relative slack, so a value that equals a threshold in the reals still compares equal in floats

T975 = {1: 12.706, 2: 4.303, 3: 3.182, 4: 2.776, 5: 2.571, 6: 2.447, 7: 2.365, 8: 2.306, 9: 2.262, 10: 2.228,
        11: 2.201, 12: 2.179, 13: 2.160, 14: 2.145, 15: 2.131, 16: 2.120, 17: 2.110, 18: 2.101, 19: 2.093,
        20: 2.086, 21: 2.080, 22: 2.074, 23: 2.069, 24: 2.064, 25: 2.060, 26: 2.056, 27: 2.052, 28: 2.048,
        29: 2.045, 30: 2.042, 40: 2.021, 60: 2.000, 120: 1.980}

BIN_US = 50   # MESH_WORK_BINS histogram bin width

GO, NO_GO, MARGINAL, INSUFFICIENT, NO_VERDICT = "GO", "NO-GO", "MARGINAL", "INSUFFICIENT", "NO VERDICT"
KILL, NOT_KILLED = "KILL", "NOT KILLED"
PASS, FAIL = "PASS", "FAIL"


class ModelError(ValueError):
    """ModelError is raised by the phase 0 statistics and geography code; its message names the thing at fault."""


def _slack(threshold):
    return EPS * max(1.0, abs(threshold))


def at_most(value, threshold):
    """Whether value <= threshold, allowing EPS relative slack (0.8 - 0.75 is at most 0.05)."""
    return value <= threshold + _slack(threshold)


def at_least(value, threshold):
    """Whether value >= threshold, allowing EPS relative slack."""
    return value >= threshold - _slack(threshold)


def above(value, threshold):
    """Whether value > threshold by more than the EPS slack (the complement of at_most)."""
    return value > threshold + _slack(threshold)


def ci_verdict(lo, hi, threshold):
    """GO when the whole interval is at most the threshold, NO-GO when all of it is above, else MARGINAL."""
    if at_most(hi, threshold):
        return GO
    if above(lo, threshold):
        return NO_GO
    return MARGINAL


def kill_verdict(lo, hi, threshold):
    """KILL when the whole interval is at least the threshold, NOT KILLED when all of it is below, else MARGINAL."""
    if at_least(lo, threshold):
        return KILL
    if not at_least(hi, threshold):
        return NOT_KILLED
    return MARGINAL


def ci_gate(ci, threshold, verdict=ci_verdict):
    """verdict(lo, hi, threshold) for a jackknife interval dict, or NO VERDICT when the interval is unbounded (a ratio
    whose denominator's interval includes 0) or missing."""
    if ci is None or ci.get("unbounded") or ci.get("lo") is None or ci.get("hi") is None:
        return NO_VERDICT
    return verdict(ci["lo"], ci["hi"], threshold)


def point_verdict_eps(ratios, threshold, min_runs):
    """rigtools.point_verdict's rule with at_most/above, so 2.1/2.8 = 0.7500000000000001 is still GO at 0.75:
    GO when every ratio is at most the threshold, NO-GO when every one is above it, MARGINAL when they straddle it,
    INSUFFICIENT with fewer than min_runs."""
    if len(ratios) < max(1, min_runs):
        return INSUFFICIENT
    if all(at_most(r, threshold) for r in ratios):
        return GO
    if all(above(r, threshold) for r in ratios):
        return NO_GO
    return MARGINAL


def t975(df):
    """The two-sided 95 % Student t quantile for df degrees of freedom: tabulated to 30, then the next LOWER
    tabulated df (40, 60, 120), and 1.980 above 120 (conservative: 1.960 is below the true t at every finite df).
    Refuses df < 1."""
    if df < 1:
        raise ModelError(f"a confidence interval needs at least 1 degree of freedom; got {df}")
    if df > 120:
        return T975[120]
    return T975[max(d for d in T975 if d <= df)]


def threshold_bin_edge_us(ms):
    """A threshold in ms as the upper edge of the last 0.05 ms bin at or under it, in integer µs (2.5 -> 2500,
    1000/60 -> 16650)."""
    return (round(ms * 1000) // BIN_US) * BIN_US


def bin_gate_passes(p95_us, threshold_ms):
    """Whether a histogram percentile (the upper edge of its bin, integer µs, None when above the histogram's range)
    passes a threshold in ms. Values in [T, T + 50 µs) fail: a conservative bias of less than one bin."""
    return p95_us is not None and p95_us <= threshold_bin_edge_us(threshold_ms)


def threshold_text(ms_per_s, fps):
    """A work threshold in ms/s and in ms/frame at each stated frame rate: "600 ms/s = 10.00 ms/frame at 60 fps".
    fps is one rate or a sequence of them; refuses a rate that is not positive."""
    rates = [fps] if isinstance(fps, (int, float)) else list(fps)
    if not rates or any(f <= 0 for f in rates):
        raise ModelError(f"threshold_text needs positive frame rates; got {rates}")
    frames = ", ".join(f"{ms_per_s / f:.2f} ms/frame at {f:g} fps" for f in rates)
    return f"{ms_per_s:g} ms/s = {frames}"


def pass_fail(ok):
    """PASS or FAIL."""
    return PASS if ok else FAIL


def n2_verdict(shares, threshold=0.80):
    """The N² gate over {removal arm: share of the excess removed}: PASS when the best arm removes at least the
    threshold share. Refuses an empty mapping."""
    if not shares:
        raise ModelError("the N² gate needs at least one removal arm")
    return pass_fail(at_least(max(shares.values()), threshold))


def geo_prediction_verdict(pairs, tolerance=0.05):
    """G-GEO (b) over (predicted, measured) ratio pairs: PASS when every |predicted - measured| is at most the
    tolerance. Refuses an empty list."""
    if not pairs:
        raise ModelError("G-GEO (b) needs at least one predicted and measured point")
    return pass_fail(all(at_most(abs(p - m), tolerance) for p, m in pairs))


def geo_slope_verdict(predicted, lo, hi):
    """G-GEO (c): PASS when the predicted band slope lies inside the measured slope's interval, ends included."""
    return pass_fail(at_least(predicted, lo) and at_most(predicted, hi))


def floor_verdict(ms_per_s, limit=600.0):
    """The floor gate: PASS when an idle cell's work is at most the limit (600 ms/s)."""
    return pass_fail(at_most(ms_per_s, limit))


def drifted(value, baseline, threshold=0.05):
    """Whether a canary drifted: |value - baseline|/baseline above the threshold. Refuses a baseline that is not
    positive."""
    if baseline <= 0:
        raise ModelError(f"a canary baseline must be positive; got {baseline:g} ms/s")
    return above(abs(value - baseline) / baseline, threshold)


def covered(covered_s, measured_s, share=0.8):
    """Whether covered_s seconds are at least `share` of the measured seconds. Refuses a measure that is not
    positive."""
    if measured_s <= 0:
        raise ModelError(f"the measured seconds must be positive; got {measured_s:g}")
    return at_least(covered_s / measured_s, share)
