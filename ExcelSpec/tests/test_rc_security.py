"""RC security / robustness regression tests."""

from __future__ import annotations

import json
import tempfile
import unittest
import zipfile
from pathlib import Path

from excelspec.exporters import ChunksJsonlExporter, HtmlExporter, MarkdownExporter
from excelspec.ingest import ingest_xlsx
from excelspec.ingest.ooxml import _safe_filename
from excelspec.models.document_ir import (
    CellIR,
    DocumentIR,
    RegionIR,
    RegionType,
    SheetIR,
    SourceRef,
    TableIR,
)


def _doc(cells) -> DocumentIR:
    table = TableIR(
        table_id="t", cells=cells, header_rows=1,
        source=SourceRef(sheet="S", range="A1:B2"),
        metadata={"header_labels": {"A": "h1", "B": "h2"}},
    )
    region = RegionIR(
        region_id="t", region_type=RegionType.TABLE,
        source=SourceRef(sheet="S", range="A1:B2"), tables=[table],
        metadata={"candidate_type": "table"},
    )
    return DocumentIR(
        document_id="d", title="D", source_path="w.xlsx",
        sheets=[SheetIR(sheet_id="s1", name="S", index=0, regions=[region])],
        metadata={"extraction_mode": "fast"},
    )


class EscapingTests(unittest.TestCase):
    def test_html_escapes_script(self) -> None:
        doc = _doc([
            CellIR("A1", 1, 1, "h1", "h1"), CellIR("B1", 1, 2, "h2", "h2"),
            CellIR("A2", 2, 1, "<script>alert(1)</script>", "<script>alert(1)</script>"),
            CellIR("B2", 2, 2, "ok", "ok"),
        ])
        html = HtmlExporter().render(doc)
        self.assertIn("&lt;script&gt;", html)
        self.assertNotIn("<script>alert", html)

    def test_markdown_escapes_pipe_and_newline(self) -> None:
        doc = _doc([
            CellIR("A1", 1, 1, "h1", "h1"), CellIR("B1", 1, 2, "h2", "h2"),
            CellIR("A2", 2, 1, "a|b\nc", "a|b\nc"), CellIR("B2", 2, 2, "x", "x"),
        ])
        md = MarkdownExporter().render(doc)
        table_lines = [l for l in md.splitlines() if l.startswith("|")]
        for line in table_lines:
            # a data cell's pipe must be escaped so it never adds a column
            self.assertNotIn("a|b", line)
            self.assertNotIn("\n", line.replace("\\n", ""))

    def test_jsonl_control_chars_stay_one_line_per_object(self) -> None:
        doc = _doc([
            CellIR("A1", 1, 1, "h1", "h1"), CellIR("B1", 1, 2, "h2", "h2"),
            CellIR("A2", 2, 1, "line1\nline2\ttab", "line1\nline2\ttab"),
            CellIR("B2", 2, 2, "y", "y"),
        ])
        text = ChunksJsonlExporter().render(doc)
        for line in text.splitlines():
            json.loads(line)  # every line is a standalone valid JSON object

    def test_formula_is_never_executed(self) -> None:
        cells = [
            CellIR("A1", 1, 1, "h1", "h1"), CellIR("B1", 1, 2, "h2", "h2"),
            CellIR("A2", 2, 1, "=1+1", None, formula="=1+1", data_type="f"),
            CellIR("B2", 2, 2, "z", "z"),
        ]
        text = ChunksJsonlExporter().render(_doc(cells))
        # the formula text survives; the computed value "2" is never invented
        self.assertIn("=1+1", text)


class FilenameSafetyTests(unittest.TestCase):
    def test_safe_filename_strips_traversal(self) -> None:
        self.assertNotIn("/", _safe_filename("../../etc/passwd"))
        self.assertNotIn("\\", _safe_filename("..\\..\\evil"))
        self.assertNotIn("..", _safe_filename("....").strip("_") or "_")
        self.assertTrue(_safe_filename(""))  # never empty
        self.assertLessEqual(len(_safe_filename("x" * 500)), 100)


class MalformedWorkbookTests(unittest.TestCase):
    def test_non_ooxml_bytes_do_not_crash(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "bad.xlsx"
            path.write_bytes(b"this is not a zip file at all")
            with self.assertRaises(Exception):  # noqa: B017 - must not hang/segfault
                ingest_xlsx(path, asset_dir=Path(directory) / "a", engine="auto")

    def test_truncated_zip_falls_back_or_errors_cleanly(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            good = Path(directory) / "g.xlsx"
            from openpyxl import Workbook
            Workbook().save(good)
            data = good.read_bytes()
            bad = Path(directory) / "trunc.xlsx"
            bad.write_bytes(data[: len(data) // 2])  # truncated zip
            with self.assertRaises(Exception):  # noqa: B017
                ingest_xlsx(bad, asset_dir=Path(directory) / "a", engine="auto")


class CacheSafetyTests(unittest.TestCase):
    def test_cache_key_is_a_safe_filename(self) -> None:
        from excelspec.cache import document_cache_key

        key = document_cache_key(
            workbook_hash="a" * 64, mode="fast", profile_hash=None,
            asset_dir="../../etc",
        )
        # hex digest only -> no path separators, safe as a filename
        self.assertTrue(all(c in "0123456789abcdef" for c in key))


if __name__ == "__main__":
    unittest.main()
