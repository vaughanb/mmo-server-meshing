"""Slice 7's graders (Docs/ServerMeshing/slices/slice7_design.md decisions 13-16), pure: the linkcap layout and who
touches whom in it, the cells' on-demand link lines, and the byte-identity check of a run without MESH_LINKS."""
import re

MAP = "ServerMeshTestMap"
MAP2 = "ServerMeshTestMap2"
CELL = 150.0          # linkcap's cells: a 4x4 of 150 m squares from (-300, -300), ids L<col><row>
ORIGIN = -300.0
C2_WORLD = ("C2", 300.0, -100.0, 200.0, 200.0)   # map 2 sits 400 m east; C2 is [-100, 100)^2 in its own frame
TOUCH_TOLERANCE = 0.01

LINKS_LINE = re.compile(
    r"Mesh links: (?P<out>\d+) out \((?P<band>\d+) band, (?P<record>\d+) record, (?P<recent>\d+) recent, "
    r"(?P<demand>\d+) demand, (?P<closing>\d+) closing\), (?P<inbound>\d+) in; opened (?P<opened>\d+), "
    r"closed (?P<closed>\d+); peak out (?P<peak_out>\d+), peak in (?P<peak_in>\d+)")
LINKED = re.compile(r"Neighbour zone (\S+) linked \(conn ")
REFUSED = re.compile(r"ConnectionRejected")
OPENED_FAR = re.compile(r"Mesh link to zone \S+ opened on demand \((record|demand|recent)")
CLOSED = re.compile(r"Mesh link to zone (\S+) closed on demand: unneeded and idle")
UNLINKED = re.compile(r"Neighbour zone (\S+) unlinked \(idle close\)")
# A cell that does not link on demand logs none of these (decision 16).
ON_DEMAND_ONLY = re.compile(r"Mesh links|opened on demand|closed on demand|relayed through the central|unlinked \(idle close\)|over our link to it|"
                            r"waits for the link to it")


def grid():
    """{cell id: (min x, min z, size x, size z)} of linkcap's 16 cells (scene frame = world frame on the test map)."""
    return {f"L{c}{r}": (ORIGIN + c * CELL, ORIGIN + r * CELL, CELL, CELL) for c in range(4) for r in range(4)}


def override_value(cells=None):
    """The MESH_LAYOUT_OVERRIDE value for linkcap's cut of the test map."""
    cells = grid() if cells is None else cells
    return f"{MAP}=" + ";".join(f"{cid}@{x:g},{z:g},{w:g},{h:g}" for cid, (x, z, w, h) in sorted(cells.items()))


def cell_at(x, z, cells=None):
    """The linkcap cell holding a point, or None."""
    for cid, (cx, cz, w, h) in (grid() if cells is None else cells).items():
        if cx <= x < cx + w and cz <= z < cz + h:
            return cid
    return None


def _touch(a, b, tol=TOUCH_TOLERANCE):
    ax, az, aw, ah = a
    bx, bz, bw, bh = b
    gap_x = max(bx - (ax + aw), ax - (bx + bw), 0.0)
    gap_z = max(bz - (az + ah), az - (bz + bh), 0.0)
    overlap = min(ax + aw, bx + bw) > max(ax, bx) and min(az + ah, bz + bh) > max(az, bz)
    return not overlap and gap_x <= tol and gap_z <= tol


def touching(cells=None, extra=(C2_WORLD,)):
    """{cell id: set of the ids it touches, edge or corner} over linkcap's cells and C2 (world frame)."""
    rects = dict(grid() if cells is None else cells)
    for cid, x, z, w, h in extra:
        rects[cid] = (x, z, w, h)
    return {a: {b for b in rects if b != a and _touch(rects[a], rects[b])} for a in rects}


def parse_links(line):
    """The fields of a `Mesh links:` line as ints, or None."""
    m = LINKS_LINE.search(line)
    return {k: int(v) for k, v in m.groupdict().items()} if m else None


def last_links(lines):
    """The last `Mesh links:` line's fields in a cell's lines, or None."""
    found = None
    for line in lines:
        parsed = parse_links(line)
        if parsed is not None:
            found = parsed
    return found


def linked_from(lines):
    """The zones whose hello reached this cell (`Neighbour zone X linked`)."""
    return {m.group(1) for m in (LINKED.search(line) for line in lines) if m}


def count(lines, rx):
    return sum(1 for line in lines if rx.search(line))


def link_problems(cell, lines, expected_neighbours, cap):
    """What is wrong with one cell's links in linkcap: a touching neighbour never linked, no `Mesh links` line, more out
    than its neighbours and two, a peak in over the cap, a refused connection. [] when nothing."""
    problems = []
    missing = sorted(expected_neighbours - linked_from(lines))
    if missing:
        problems.append(f"no hello from {', '.join(missing)}")
    links = last_links(lines)
    if links is None:
        problems.append("no Mesh links line")
    else:
        if links["out"] > len(expected_neighbours) + 2:
            problems.append(f"{links['out']} links out for {len(expected_neighbours)} neighbours")
        if links["peak_in"] > cap:
            problems.append(f"peak in {links['peak_in']} over the cap {cap}")
    refused = count(lines, REFUSED)
    if refused:
        problems.append(f"{refused} refused connection(s)")
    return problems


def on_demand_summary(lines_by_cell):
    """(links opened for a non-band reason, links closed, largest peak out, largest peak in) over the cells."""
    far = closed = peak_out = peak_in = 0
    for lines in lines_by_cell.values():
        far += count(lines, OPENED_FAR)
        closed += count(lines, CLOSED)
        links = last_links(lines)
        if links is not None:
            peak_out, peak_in = max(peak_out, links["peak_out"]), max(peak_in, links["peak_in"])
    return far, closed, peak_out, peak_in


def byte_identity_problems(lines_by_cell):
    """Decision 16: in a run without MESH_LINKS no cell logs an on-demand line. [(cell, first such line)]."""
    found = []
    for cell, lines in sorted(lines_by_cell.items()):
        hit = next((line for line in lines if ON_DEMAND_ONLY.search(line)), None)
        if hit is not None:
            found.append((cell, hit))
    return found


def closed_to(lines, zone):
    """True when a cell's lines hold its on-demand close of the link to `zone`."""
    return any(m and m.group(1) == zone for m in (CLOSED.search(line) for line in lines))


def unlinked_from(lines, zone):
    """True when a cell's lines hold the idle close of `zone`'s link to it."""
    return any(m and m.group(1) == zone for m in (UNLINKED.search(line) for line in lines))
