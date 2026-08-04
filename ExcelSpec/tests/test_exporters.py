from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path

from excelspec.exporters import (
    HtmlExporter,
    JsonExporter,
    KnowledgeBaseJsonlExporter,
    MarkdownExporter,
)
from excelspec.models.document_ir import (
    AssetIR,
    AssetType,
    CellIR,
    DocumentIR,
    RegionIR,
    RegionType,
    SheetIR,
    SourceRef,
    TableIR,
)


def _cell(
    coordinate: str,
    row: int,
    column: int,
    value: str | None = None,
    *,
    row_span: int = 1,
    col_span: int = 1,
    merged_master: str | None = None,
) -> CellIR:
    return CellIR(
        coordinate=coordinate,
        row=row,
        column=column,
        raw_value=value,
        display_value=value,
        row_span=row_span,
        col_span=col_span,
        merged_master=merged_master,
        source=SourceRef(sheet="画面", cell=coordinate),
    )


def _document(asset_path: Path) -> DocumentIR:
    source = SourceRef(sheet="画面", range="A1:C4")
    merged = TableIR(
        table_id="items",
        source=source,
        header_rows=2,
        column_semantics={"A": "item_id", "B": "item_name", "C": "required"},
        cells=[
            _cell("A1", 1, 1, "项目", col_span=2),
            _cell("B1", 1, 2, merged_master="A1"),
            _cell("C1", 1, 3, "必填", row_span=2),
            _cell("A2", 2, 1, "ID"),
            _cell("B2", 2, 2, "名称"),
            _cell("C2", 2, 3, merged_master="C1"),
            _cell("A3", 3, 1, "001"),
            _cell("B3", 3, 2, "用户名"),
            _cell("C3", 3, 3, "是"),
            _cell("A4", 4, 1, "共享", col_span=2),
            _cell("B4", 4, 2, merged_master="A4"),
            _cell("C4", 4, 3, "否"),
        ],
    )
    plain = TableIR(
        table_id="notes",
        header_rows=1,
        cells=[
            _cell("E1", 1, 5, "键"),
            _cell("F1", 1, 6, "值"),
            _cell("E2", 2, 5, "说明"),
            _cell("F2", 2, 6, "A|B"),
        ],
    )
    asset = AssetIR(
        asset_id="screen-main",
        asset_type=AssetType.SCREENSHOT,
        uri=str(asset_path),
        description="主画面",
        source=SourceRef(sheet="画面", cell="H2"),
        metadata={"ocr": {"status": "completed", "text": "搜索画面"}},
    )
    return DocumentIR(
        document_id="screen-001",
        title="用户搜索",
        template_id="screen-design",
        template_version="1",
        source_path="spec.xlsx",
        metadata={"owner": "Linye"},
        sheets=[
            SheetIR(
                sheet_id="screen",
                name="画面",
                index=0,
                assets=[asset],
                regions=[
                    RegionIR(
                        region_id="items",
                        region_type=RegionType.TABLE,
                        title="画面项目",
                        source=source,
                        values={"screen_id": "SCR-001"},
                        tables=[merged, plain],
                        asset_ids=["screen-main"],
                    )
                ],
            )
        ],
    )


class ExporterTests(unittest.TestCase):
    def test_json_is_stable_and_replayable(self) -> None:
        document = _document(Path("screen.png"))
        rendered = JsonExporter().render(document)

        self.assertEqual(rendered, JsonExporter().render(document))
        self.assertIn('"document_id": "screen-001"', rendered)
        replayed = DocumentIR.from_json(rendered)
        self.assertEqual(document, replayed)
        self.assertLess(rendered.index('"document_id"'), rendered.index('"schema_version"'))

    def test_markdown_uses_html_fallback_and_relative_asset(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            asset = root / "assets" / "screen.png"
            destination = root / "docs" / "screen.md"
            rendered = MarkdownExporter().render(_document(asset), destination)

        self.assertIn('<table class="excelspec-table">', rendered)
        self.assertIn('colspan="2"', rendered)
        self.assertIn('rowspan="2"', rendered)
        self.assertIn("| 键 | 值 |", rendered)
        self.assertIn("A\\|B", rendered)
        self.assertIn("![主画面](../assets/screen.png)", rendered)

    def test_html_is_semantic_responsive_and_preserves_spans(self) -> None:
        rendered = HtmlExporter().render(_document(Path("screen.png")))

        self.assertIn('<nav aria-label="目录">', rendered)
        self.assertIn('<section id="sheet-0-screen">', rendered)
        self.assertIn('data-region-type="table"', rendered)
        self.assertIn('rowspan="2"', rendered)
        self.assertIn('colspan="2"', rendered)
        self.assertIn("@media (max-width:50rem)", rendered)
        self.assertIn('<img src="screen.png"', rendered)

    def test_jsonl_chunks_rows_with_logical_merges_and_provenance(self) -> None:
        document = _document(Path("screen.png"))
        exporter = KnowledgeBaseJsonlExporter()
        rendered = exporter.render(document)
        chunks = [json.loads(line) for line in rendered.splitlines()]

        self.assertEqual(rendered, exporter.render(document))
        self.assertEqual(
            {"document", "section", "table_row"},
            {chunk["chunk_type"] for chunk in chunks},
        )
        merged_row = next(
            chunk
            for chunk in chunks
            if chunk["chunk_type"] == "table_row"
            and chunk["metadata"]["row"] == 4
        )
        self.assertIn("item_id: 共享", merged_row["text"])
        self.assertIn("item_name: 共享", merged_row["text"])
        self.assertEqual(["A4", "B4", "C4"], merged_row["metadata"]["source_cells"])
        self.assertEqual("SCR-001", merged_row["metadata"]["screen_id"])
        self.assertEqual("A1:C4", merged_row["metadata"]["source"]["range"])
        self.assertEqual(
            "completed", merged_row["metadata"]["assets"][0]["ocr"]["status"]
        )
        for line in rendered.splitlines():
            self.assertEqual(line, json.dumps(json.loads(line), ensure_ascii=False, sort_keys=True, separators=(",", ":")))

    def test_jsonl_respects_max_chunk_size(self) -> None:
        document = _document(Path("screen.png"))
        document.metadata["description"] = "一" * 25
        chunks = KnowledgeBaseJsonlExporter(max_chunk_chars=10).chunks(document)

        self.assertTrue(any(":part-" in chunk["chunk_id"] for chunk in chunks))
        self.assertTrue(all(len(chunk["text"]) <= 10 for chunk in chunks))

    def test_exporters_create_parent_directories(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            document = _document(root / "screen.png")
            destinations = [
                (JsonExporter(), root / "json" / "document.json"),
                (MarkdownExporter(), root / "markdown" / "document.md"),
                (HtmlExporter(), root / "html" / "document.html"),
                (KnowledgeBaseJsonlExporter(), root / "jsonl" / "document.jsonl"),
            ]
            for exporter, destination in destinations:
                exporter.export(document, destination)
                self.assertTrue(destination.is_file())


if __name__ == "__main__":
    unittest.main()
