"""Unit tests for riglinks.py and regress_links.py's graders (python -m unittest discover -s Tools/ServerMeshing -p
"test_*.py"): Slice 7's linkcap layout, the on-demand link lines and the byte-identity check."""
import unittest

import regress
import regress_links
import riglinks


def links_line(out=8, band=8, record=0, recent=0, demand=0, closing=0, inbound=8, opened=8, closed=0, peak_out=8, peak_in=8):
    return (f"[Mesh] Mesh links: {out} out ({band} band, {record} record, {recent} recent, {demand} demand, {closing} closing), "
            f"{inbound} in; opened {opened}, closed {closed}; peak out {peak_out}, peak in {peak_in}")


def linked(*zones):
    return [f"[Mesh] Neighbour zone {z} linked (conn {i})" for i, z in enumerate(zones)]


class FakeRep:
    def __init__(self):
        self.checks, self.notes = [], []

    def check(self, ok, label):
        self.checks.append((bool(ok), label))
        return ok

    def failed(self):
        return [label for ok, label in self.checks if not ok]


class LayoutTests(unittest.TestCase):
    def test_grid_is_sixteen_cells_tiling_the_ground(self):
        grid = riglinks.grid()
        self.assertEqual(len(grid), 16)
        self.assertEqual(grid["L00"], (-300.0, -300.0, 150.0, 150.0))
        self.assertEqual(grid["L33"], (150.0, 150.0, 150.0, 150.0))
        self.assertEqual(sum(w * h for _, _, w, h in grid.values()), 600.0 * 600.0)

    def test_override_value_parses_as_the_runtime_reads_it(self):
        value = riglinks.override_value()
        self.assertTrue(value.startswith("ServerMeshTestMap=L00@-300,-300,150,150;"))
        self.assertEqual(value.count("@"), 16)
        self.assertNotIn(" ", value)

    def test_touching_counts_corners_and_the_seam(self):
        t = riglinks.touching()
        self.assertEqual(len(t["L11"]), 8, "an inner cell touches its ring")
        self.assertEqual(len(t["L00"]), 3, "a corner cell")
        self.assertEqual(t["C2"], {"L31", "L32"}, "C2 (world x 300-500, z -100..100) touches the east column's middle rows")
        self.assertIn("C2", t["L32"])
        self.assertNotIn("C2", t["L33"], "50 m apart in z")
        for a, near in t.items():
            for b in near:
                self.assertIn(a, t[b], "touching is symmetric")

    def test_cell_at(self):
        self.assertEqual(riglinks.cell_at(*regress_links.LINKCAP_BOT), "L02")
        self.assertEqual(riglinks.cell_at(*regress_links.LINKCAP_TESTER), "L12")
        self.assertEqual(riglinks.cell_at(160.0, 75.0), "L32")
        self.assertIsNone(riglinks.cell_at(400.0, 0.0))


class LinesTests(unittest.TestCase):
    def test_parse_links(self):
        self.assertIsNone(riglinks.parse_links("[Mesh] Mesh stats: players=0"))
        parsed = riglinks.parse_links(links_line(out=9, record=1, peak_in=10))
        self.assertEqual((parsed["out"], parsed["record"], parsed["peak_in"]), (9, 1, 10))
        self.assertEqual(riglinks.last_links([links_line(out=3), "x", links_line(out=5)])["out"], 5)

    def test_link_problems_failure_cases(self):
        neighbours = {"A", "B", "C"}
        problems = riglinks.link_problems("X", linked("A", "B"), neighbours, 10)
        self.assertIn("no hello from C", problems)
        self.assertIn("no Mesh links line", problems)
        lines = linked("A", "B", "C") + [links_line(out=6, peak_in=11), "[MeshLinkClient:A] Disconnected from neighbour (ConnectionRejected)"]
        problems = riglinks.link_problems("X", lines, neighbours, 10)
        self.assertTrue(any("6 links out for 3" in p for p in problems))
        self.assertTrue(any("peak in 11 over the cap 10" in p for p in problems))
        self.assertTrue(any("refused" in p for p in problems))

    def test_link_problems_clean(self):
        lines = linked("A", "B", "C") + [links_line(out=5, peak_in=4)]
        self.assertEqual(riglinks.link_problems("X", lines, {"A", "B", "C"}, 10), [])

    def test_byte_identity(self):
        clean = {"C": ["[Mesh] Mesh stats: players=1", "[Mesh] Neighbour zone E linked (conn 1)", "Sleeping cells: 3 cell(s) start on demand"]}
        self.assertEqual(riglinks.byte_identity_problems(clean), [])
        for line in (links_line(), "[Mesh] Mesh links on demand: band neighbours", "[Mesh] Mesh link to zone E opened on demand (record)",
                     "[Mesh] Mesh link to zone E closed on demand: unneeded and idle 25 s", "[Mesh] Neighbour zone E unlinked (idle close)",
                     "[Mesh] Party share for remote member m relayed through the central to map M", "[Mesh] Notice x to zone E waits for the link to it",
                     "[Mesh] Neighbour zone E reports itself registered over our link to it"):
            with self.subTest(line):
                self.assertEqual(riglinks.byte_identity_problems({"E": ["a", line]}), [("E", line)])

    def test_summary_and_closes(self):
        lines = {"C": ["[Mesh] Mesh link to zone W opened on demand (record)", links_line(peak_out=9, peak_in=7)],
                 "E": ["[Mesh] Mesh link to zone C closed on demand: unneeded and idle 25 s", links_line(peak_out=5, peak_in=12)]}
        self.assertEqual(riglinks.on_demand_summary(lines), (1, 1, 9, 12))
        self.assertTrue(riglinks.closed_to(lines["E"], "C"))
        self.assertFalse(riglinks.closed_to(lines["E"], "N"))
        self.assertTrue(riglinks.unlinked_from(["[Mesh] Neighbour zone E unlinked (idle close)"], "E"))
        self.assertFalse(riglinks.unlinked_from(["[Mesh] Neighbour zone E link lost (conn 3)"], "E"))


class GraderTests(unittest.TestCase):
    def passing_cells(self):
        t = riglinks.touching()
        cells = {cid: linked(*sorted(t[cid])) + [links_line(out=len(t[cid]), peak_in=len(t[cid]))] for cid in t}
        cells["L02"].append("[Mesh] Handoff ACK meshbotChar by zone L12: registered")
        cells["L12"] += ["[Mesh] Handoff ACK meshbotChar by zone L22: registered", "[Mesh] Handoff ACK meshbotChar by zone L32: registered"]
        cells["L32"].append("[Mesh] Handoff ACK meshbotChar by zone L22: registered")
        return cells

    def test_linkcap_passes(self):
        rep = FakeRep()
        regress_links.grade_linkcap(rep, "RESULT walk: migrations=4 allReady=True -> PASS", self.passing_cells())
        self.assertEqual(rep.failed(), [])
        self.assertEqual(len(rep.checks), 2 + 17)

    def test_linkcap_failure_cases(self):
        cells = self.passing_cells()
        cells["L11"] = cells["L11"][1:]   # one hello missing
        cells["L22"].append(links_line(out=15, peak_in=15))
        rep = FakeRep()
        regress_links.grade_linkcap(rep, "RESULT walk: migrations=3 -> FAIL", cells)
        failed = rep.failed()
        self.assertTrue(any("bot crossed" in f for f in failed))
        self.assertTrue(any(f.startswith("L11:") and "no hello from" in f for f in failed))
        self.assertTrue(any(f.startswith("L22:") and "over the cap" in f for f in failed))

    def test_partyfar_on_demand_and_not(self):
        lines = {"C": ["[Mesh] Party 1: shared exp with 1 here + 1 on other cells + 1 remote, gold with 1 + 1 + 1",
                       "[Mesh] Party share for remote member x relayed through the central to map M: exp 1"],
                 "E": [], "W": ["[Mesh] Party share applied to meshbot2Char: exp 3"]}
        rep = FakeRep()
        regress_links.grade_partyfar(rep, "x -> PASS", lines, True, "meshbot2Char")
        self.assertEqual(rep.failed(), [])
        rep = FakeRep()
        regress_links.grade_partyfar(rep, "x -> PASS", lines, False, "meshbot2Char")
        self.assertEqual(len(rep.failed()), 1, "relayed where a broadcast was expected")
        lines["E"].append("[Mesh] Party share for remote member x broadcast to 8 cell(s): exp 1")
        rep = FakeRep()
        regress_links.grade_partyfar(rep, "x -> PASS", lines, True, "meshbot2Char")
        self.assertEqual(len(rep.failed()), 1, "a broadcast in an on-demand run")

    def test_registration(self):
        for name in regress_links.SCENARIOS:
            self.assertIn(name, regress.ALL)
            self.assertNotIn(name, regress.DEFAULT)
            self.assertIn(name, regress.TWO_HOST_SKIP)
            self.assertIn(name, regress.PERSIST_SKIP)
        for key in ("MESH_LINKS", "MESH_LINK_CAP", "MESH_LINK_IDLE"):
            self.assertIn(key, regress.ENV_KEYS)
        self.assertEqual(regress_links.EXPECTED_ZONES["linkcap"], set(riglinks.grid()) | {"C2"})
        self.assertIn("MESH_LINK_CAP", regress_links.LINKCAP_ENV)


if __name__ == "__main__":
    unittest.main()
