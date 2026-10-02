"""Phase 0 Markdown tables (step 4): §5's summary table over summary and gate rows, and the map-size table. Pure: the
rows come in as arguments. Split out of p0results (the ~500-line rule), whose summary_rows and gate_row make the rows
these print.

    p0markdown.markdown_summary(summaries, gates)  # §5's table, in its column order
    p0markdown.map_size_markdown(table)            # the map-size table (p0geo.map_size_table rows)

Unit tests: python -m unittest discover -s Tools/ServerMeshing -p "test_*.py"
"""
MARKDOWN_COLUMNS = ("Game", "Case", "Arm", "Kind", "Rates", "Owned / puppets / clients / V", "Work ms/s (median [range])",
                    "Non-AOI tick-frame p95 ms", "Tick send p95 ms", "Downlink p95 kB/s", "Key metric", "Gate",
                    "Verdict", "n")
MAP_SIZE_COLUMNS = ("Side (m)", "B (m)", "k", "W* (m)", "Base (m)", "s_min (m)", "Max cells", "Mode")
DASH = "–"


def _md_table(columns, rows):
    def line(cells):
        return "| " + " | ".join(str(cell).replace("|", "\\|") for cell in cells) + " |"
    return "\n".join([line(columns), "|" + "---|" * len(columns)] + [line(row) for row in rows]) + "\n"


def _number(value, spec):
    return DASH if value is None else format(value, spec)


def markdown_summary(summary_rows, gate_rows):
    """§5's Markdown summary: the MARKDOWN_COLUMNS header, then a line per summary row. Case is "{case} [{cell}]";
    Arm carries the point in brackets; owned / puppets are the median counts, clients 0, V "–"; Downlink "–" (0a);
    Key metric, Gate and Verdict list the gate rows of the summary's family whose case is None or the summary's
    ("; "-joined, "–" for none)."""
    lines = []
    for s in summary_rows:
        gates = [g for g in gate_rows if g.get("family") == s.get("family") and g.get("case") in (None, s.get("case"))]
        point = ", ".join(f"{key} {value}" for key, value in (s.get("point") or {}).items())
        lines.append([s.get("game"), f"{s.get('case')} [{s.get('cell')}]",
                      f"{s.get('arm')} ({point})" if point else s.get("arm"), s.get("kind") or DASH,
                      s.get("rates") or DASH,
                      f"{_number(s.get('owned'), '.0f')} / {_number(s.get('puppets'), '.0f')} / "
                      f"{s.get('clients', 0)} / {DASH}",
                      f"{s['work_ms_per_s_median']:.1f} [{s['work_ms_per_s_min']:.1f}–{s['work_ms_per_s_max']:.1f}]",
                      _number(s.get("nonaoi_tick_frame_p95_ms_median"), ".2f"),
                      _number(s.get("tick_send_p95_ms_median"), ".2f"), DASH,
                      "; ".join(g["key_metric"] for g in gates if g.get("key_metric")) or DASH,
                      "; ".join(str(g.get("name")) for g in gates) or DASH,
                      "; ".join(str(g.get("verdict")) for g in gates) or DASH, s.get("n")])
    return _md_table(MARKDOWN_COLUMNS, lines)


def map_size_markdown(table):
    """The map-size table (p0geo.map_size_table rows) as Markdown: side, B, k, W*, base, s_min, max cells, mode."""
    return _md_table(MAP_SIZE_COLUMNS, [[_number(r["side"], "g"), _number(r["band"], "g"), _number(r["k"], ".3f"),
                                         _number(r.get("w_star"), ".1f"), _number(r["base"], "g"),
                                         _number(r["s_min"], "g"), _number(r["max_cells_per_base"], "g"), r["mode"]]
                                        for r in table])
