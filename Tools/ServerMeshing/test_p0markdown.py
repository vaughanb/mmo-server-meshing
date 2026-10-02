"""Unit tests for p0markdown (python -m unittest discover -s Tools/ServerMeshing -p "test_*.py")."""
import unittest

import p0markdown
import p0results
import test_p0results

spec, a_run, CONTEXT = test_p0results.spec, test_p0results.a_run, test_p0results.CONTEXT


class MarkdownTests(unittest.TestCase):
    def test_markdown_summary(self):
        summary = p0results.summary_rows([p0results.run_row(spec(), a_run(), CONTEXT, [], None)])[0]
        gate = p0results.gate_row("G3-F (U)", "GO", {}, "k <= 0.5", [], header={"family": "presence"},
                                  key_metric="k_hold_kinematic 0.42 [0.35, 0.49]")
        elsewhere = p0results.gate_row("Floor", "PASS", {}, "600 ms/s", [], header={"family": "floor"})
        other_case = p0results.gate_row("Kill", "NOT KILLED", {}, "600 ms/s", [], header={"family": "presence"},
                                        case="solo")
        lines = p0markdown.markdown_summary([summary], [gate, elsewhere, other_case]).splitlines()
        self.assertEqual(lines[0], "| " + " | ".join(p0markdown.MARKDOWN_COLUMNS) + " |")
        self.assertEqual(lines[1], "|" + "---|" * 14)
        self.assertEqual(lines[2], "| fantasy | main [C] | puppets-kinematic (players 60) | character | 60/0.03 "
                                   "| 62 / 0 / 0 / – | 300.0 [300.0–300.0] | 2.50 | 0.65 | – "
                                   "| k_hold_kinematic 0.42 [0.35, 0.49] | G3-F (U) | GO | 1 |")
        self.assertEqual(len(lines), 3)

    def test_no_gate(self):
        summary = p0results.summary_rows([p0results.run_row(spec(point={}), a_run(), CONTEXT, [], None)])[0]
        cells = p0markdown.markdown_summary([summary], []).splitlines()[2].split(" | ")
        self.assertEqual((cells[2], cells[-4:]), ("puppets-kinematic", ["–", "–", "–", "1 |"]))

    def test_map_size_markdown(self):
        table = [{"side": 1000.0, "band": 80.0, "k": 1.0, "w_star": 336.8421, "base": 1000.0, "bases": 1,
                  "s_min": 250.0, "halvings": 2, "max_cells_per_base": 16, "first_ratio": 0.6, "total_cpu": 1.3,
                  "crossings_per_s": 1.0, "mode": "Dynamic if busy"}]
        lines = p0markdown.map_size_markdown(table).splitlines()
        self.assertEqual(lines[0], "| Side (m) | B (m) | k | W* (m) | Base (m) | s_min (m) | Max cells | Mode |")
        self.assertEqual(lines[2], "| 1000 | 80 | 1.000 | 336.8 | 1000 | 250 | 16 | Dynamic if busy |")


if __name__ == "__main__":
    unittest.main()
