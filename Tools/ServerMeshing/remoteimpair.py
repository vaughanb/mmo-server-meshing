"""clumsy on machine B (remoteb impair; Phase 0 step 9 decision 12): the link profiles, and starting, counting and
stopping clumsy over SSH. Split out of remoteb (the ~500-line rule); remoteb re-exports these names and keeps the
`impair` command, and p0peer's PeerSession and quiet_b use them for c4's packet loss.

    remoteimpair.start_impair(args, "loss1")   # the launch output once clumsy runs; NoAdmin / RuntimeError when not
    remoteimpair.clumsy_count(args)            # how many clumsy processes B runs
    remoteimpair.stop_impair(args)             # kills clumsy and its launch script; how many clumsy processes are left

clumsy is started outside the SSH session's job object (remotessh.launch_detached), so it keeps running after the
session ends, and after a crashed harness too: the link stays degraded until something stops it (`impair --profile
off`, or quiet_b, which every peer run and every canary of a peer block calls first and which refuses to go on while a
clumsy survives). clumsy holds the impairment only while it runs: stopping it restores the link, and a profile change
is stop-then-start. Nothing here runs at import.
"""
import time

import remotessh

# Link impairment, for the faults a datacentre actually produces: brief loss, jitter, reordering. Steady tens of
# milliseconds is deliberately NOT here -- cells are co-located, so that is a case this deployment never meets.
#
# The filter is UDP ONLY and scoped to A (udp_filter). SSH is TCP, so the control channel this is driven over stays
# clean even at high loss; scoping to A's address leaves B's own loopback and anything else it does alone.
PROFILES = {
    "off":       [],
    "lag5":      ["--lag", "on", "--lag-time", "5"],                       # a busy switch, both directions
    "loss05":    ["--drop", "on", "--drop-chance", "0.5"],
    "loss1":     ["--drop", "on", "--drop-chance", "1.0"],                 # step 9 c4 (decision 12)
    "loss2":     ["--drop", "on", "--drop-chance", "2.0"],
    "loss5":     ["--drop", "on", "--drop-chance", "5.0"],
    "jitter":    ["--lag", "on", "--lag-time", "20", "--drop", "on", "--drop-chance", "0.5"],
    "reorder":   ["--ood", "on", "--ood-chance", "5.0"],
    "nasty":     ["--lag", "on", "--lag-time", "20", "--drop", "on", "--drop-chance", "5.0",
                  "--ood", "on", "--ood-chance", "5.0"],
}
CLUMSY_DIR = r"C:\MeshTest\clumsy"     # remoteb's --clumsy default (MESH_B_CLUMSY overrides it there)
SCRIPT = "impair.cmd"                  # the launch script, in B's build folder (remotessh.launch_detached)
IS_CLUMSY = "($_.Name -eq 'clumsy.exe')"
STOP_CLUMSY = IS_CLUMSY + " -or $_.CommandLine -like '*" + SCRIPT + "*'"
START_WAIT_S = 3                       # clumsy loads WinDivert before it counts as up


class NoAdmin(RuntimeError):
    """The SSH session on B is not elevated, so clumsy cannot load the WinDivert driver (c4 is skipped: decision 12)."""


def clumsy_dir(args):
    """Where clumsy lives on B: args.clumsy (remoteb's --clumsy), else CLUMSY_DIR (remotessh.defaults() has none)."""
    return getattr(args, "clumsy", None) or CLUMSY_DIR


def udp_filter(a_ip):
    """clumsy's WinDivert filter: UDP to and from A, both directions."""
    return f"udp and (ip.DstAddr == {a_ip} or ip.SrcAddr == {a_ip})"


def impair_script(directory, a_ip, profile):
    """The launch script's lines for a profile (KeyError for an unknown one): clumsy run from its own folder."""
    return ["@echo off", 'cd /d "' + directory + '"',
            f'clumsy.exe --filter "{udp_filter(a_ip)}" ' + " ".join(PROFILES[profile])]


def facts_command(directory):
    """One query, no double quotes (remotessh.ssh wraps the command in them): whether the session is elevated and
    whether clumsy.exe is where it should be."""
    return ("'admin=' + ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent())"
            ".IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator); "
            "'clumsy=' + (Test-Path '" + directory + "\\clumsy.exe')")


def parse_facts(text):
    """facts_command's output as {"admin": bool, "clumsy": bool} (False for a line it did not print)."""
    found = dict(line.split("=", 1) for line in text.splitlines() if "=" in line)
    return {key: found.get(key, "").strip().lower() == "true" for key in ("admin", "clumsy")}


def clumsy_count(args):
    """How many clumsy processes B runs."""
    return remotessh.count(args, IS_CLUMSY)


def stop_impair(args):
    """Kills B's clumsy and its launch script (never this SSH session); returns how many clumsy processes are left."""
    return remotessh.stop(args, STOP_CLUMSY, IS_CLUMSY)


def start_impair(args, profile, wait_s=START_WAIT_S):
    """Starts clumsy on B with a profile (any running one is stopped first) and returns the launch output once it
    runs. ValueError for "off" or an unknown profile; NoAdmin when the SSH session is not elevated; RuntimeError when
    clumsy.exe is missing, the script could not be copied, or clumsy did not stay up."""
    if profile not in PROFILES or profile == "off":
        raise ValueError(f"impair profile {profile!r} is not one of {', '.join(p for p in PROFILES if p != 'off')}")
    directory = clumsy_dir(args)
    out, _, _ = remotessh.ssh(args, facts_command(directory))
    facts = parse_facts(out)
    if not facts["admin"]:
        raise NoAdmin(f"the SSH session on {args.host} is not elevated, so clumsy cannot load the WinDivert driver")
    if not facts["clumsy"]:
        raise RuntimeError(f"no clumsy.exe in {directory} on {args.host}")
    stop_impair(args)
    launched = remotessh.launch_detached(args, SCRIPT, impair_script(directory, args.a_ip, profile))
    if launched is None:
        raise RuntimeError(f"could not copy {SCRIPT} to B")
    time.sleep(wait_s)
    if clumsy_count(args) < 1:
        raise RuntimeError(f"clumsy did not stay up ({launched})")
    return launched
