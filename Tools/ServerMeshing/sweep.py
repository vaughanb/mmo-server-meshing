"""Run one scenario across every impairment profile and tabulate what degrades.

The question is not "does it pass" but "where does a crossing start to hurt", so each run is mined for the four
numbers that describe one: the client's gap without a character, the handoff ack, the puppet steering error the
neighbour measured, and whether anything threw.
"""
import os
import re
import subprocess
import sys
import time

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
sys.path.insert(0, os.path.join(ROOT, "Tools", "ServerMeshing"))
os.chdir(ROOT)
import regress as r  # noqa: E402 -- the harness folder goes on sys.path first

PROFILES = ["off", "lag5", "loss05", "loss2", "loss5", "reorder", "jitter", "nasty"]
SCENARIO = sys.argv[1] if len(sys.argv) > 1 else "pet"
OUT = os.path.join(ROOT, "Logs", f"sweep_{SCENARIO}.txt")


def metrics(day, since):
    """Pull the crossing numbers out of both machines' logs for the window just run."""
    acks, steer_mean, steer_max = [], [], []
    for zone in ("C", "E"):
        try:
            lines = r.zone_lines(zone, since, day)
        except RuntimeError:
            continue
        for line in lines:
            m = re.search(r"registered (\d+) ms after the notice", line)
            if m:
                acks.append(int(m.group(1)))
            m = re.search(r"ghostSteer=\d+ samples, mean ([\d.]+) m, max ([\d.]+) m", line)
            if m:
                steer_mean.append(float(m.group(1)))
                steer_max.append(float(m.group(2)))
    return acks, steer_mean, steer_max


def main():
    rows = []
    for profile in PROFILES:
        day = time.strftime("%Y-%m-%d")
        since = time.strftime("%H:%M:%S", time.localtime(time.time() - 5))
        print(f"\n########## {SCENARIO} @ {profile}", flush=True)
        cmd = [sys.executable, "-u", os.path.join(ROOT, "Tools", "ServerMeshing", "regress.py"),
               SCENARIO, "--remote-cells", "E"]
        if profile != "off":
            cmd += ["--impair", profile]
        proc = subprocess.run(cmd, capture_output=True, text=True, timeout=1800)
        out = (proc.stdout or "") + (proc.stderr or "")
        print("\n".join(line for line in out.splitlines() if "[FAIL]" in line or "PASS" in line or "FAIL" in line)[:600], flush=True)

        verdict = "PASS" if re.search(r"\b" + SCENARIO + r"\s+PASS", out) else "FAIL"
        checks = (re.search(r"\((\d+/\d+) checks\)", out) or [None, "?"])[1]
        gap = (re.search(r"maxGap=(\d+)ms", out) or [None, "?"])[1]
        jump = (re.search(r"maxJump=([\d.]+)m", out) or [None, "?"])[1]
        # Exceptions are graded per zone by the harness; surface any that it flagged.
        exc = len(re.findall(r"\[FAIL\].*exception lines", out))
        acks, smean, smax = metrics(day, since)
        rows.append({
            "profile": profile, "verdict": verdict, "checks": checks, "gap": gap, "jump": jump,
            "ack": f"{min(acks)}-{max(acks)}" if acks else "-",
            "steer": f"{min(smean):.2f}-{max(smean):.2f}" if smean else "-",
            "steermax": f"{max(smax):.2f}" if smax else "-",
            "exc": exc,
        })
        r.remote("impair", "--profile", "off")

    header = f"{'profile':<9} {'verdict':<7} {'checks':<7} {'gap ms':<8} {'jump m':<7} {'ack ms':<10} {'steer mean m':<13} {'steer max m':<12} {'exc zones'}"
    lines = [f"=== {SCENARIO} across impairment profiles ({time.strftime('%Y-%m-%d %H:%M')})", "", header, "-" * len(header)]
    for x in rows:
        lines.append(f"{x['profile']:<9} {x['verdict']:<7} {x['checks']:<7} {x['gap']:<8} {x['jump']:<7} "
                     f"{x['ack']:<10} {x['steer']:<13} {x['steermax']:<12} {x['exc']}")
    text = "\n".join(lines)
    print("\n" + text, flush=True)
    with open(OUT, "w", encoding="utf-8") as f:
        f.write(text + "\n")
    print("\nwritten to", OUT)
    return 0


if __name__ == "__main__":
    sys.exit(main())
