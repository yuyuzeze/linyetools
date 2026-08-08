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
        metadata={
            "header_labels": {"A": "项目 / ID", "B": "项目 / 名称", "C": "必填"}
        },
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
    # No column_semantics mapping: exercises the legacy full-grid/HTML-merge
    # fallback that unmapped tables still get.
    unmapped = TableIR(
        table_id="legend",
        header_rows=2,
        cells=[
            _cell("H1", 1, 8, "分类", col_span=2),
            _cell("I1", 1, 9, merged_master="H1"),
            _cell("J1", 1, 10, "编号", row_span=2),
            _cell("H2", 2, 8, "A"),
            _cell("I2", 2, 9, "B"),
            _cell("J2", 2, 10, merged_master="J1"),
            _cell("H3", 3, 8, "x"),
            _cell("I3", 3, 9, "y"),
            _cell("J3", 3, 10, "1"),
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
                        tables=[merged, plain, unmapped],
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

    def test_markdown_renders_compact_semantic_table_and_relative_asset(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            asset = root / "assets" / "screen.png"
            destination = root / "docs" / "screen.md"
            rendered = MarkdownExporter().render(_document(asset), destination)

        # column_semantics-mapped table: compact header + resolved merges,
        # not the full physical/merged-member grid.
        self.assertIn("| 项目 / ID | 项目 / 名称 | 必填 |", rendered)
        self.assertIn("| 001 | 用户名 | 是 |", rendered)
        self.assertIn("| 共享 | 共享 | 否 |", rendered)
        # Unmapped table without column_semantics: legacy HTML fallback for
        # complex merges is unchanged.
        self.assertIn('<table class="excelspec-table">', rendered)
        self.assertIn('colspan="2"', rendered)
        self.assertIn('rowspan="2"', rendered)
        # Simple, unmapped, unmerged table: still a plain GFM table.
        self.assertIn("| 键 | 值 |", rendered)
        self.assertIn("A\\|B", rendered)
        self.assertIn("![主画面](../assets/screen.png)", rendered)

    def test_html_is_semantic_responsive_and_preserves_spans(self) -> None:
        rendered = HtmlExporter().render(_document(Path("screen.png")))

        self.assertIn('<nav aria-label="目录">', rendered)
        self.assertIn('<section id="sheet-0-screen">', rendered)
        self.assertIn('data-region-type="table"', rendered)
        # Unmapped table without column_semantics: legacy HTML fallback still
        # preserves physical merges.
        self.assertIn('rowspan="2"', rendered)
        self.assertIn('colspan="2"', rendered)
        self.assertIn("@media (max-width:50rem)", rendered)
        self.assertIn('<img src="screen.png"', rendered)

    def test_html_renders_compact_semantic_table(self) -> None:
        rendered = HtmlExporter().render(_document(Path("screen.png")))

        # column_semantics-mapped table: compact header + resolved merges,
        # not the full physical/merged-member grid.
        self.assertIn(
            "<table><tr><th>项目 / ID</th><th>项目 / 名称</th><th>必填</th></tr>"
            "<tr><td>001</td><td>用户名</td><td>是</td></tr>"
            "<tr><td>共享</td><td>共享</td><td>否</td></tr></table>",
            rendered,
        )

    def test_readable_exports_hide_parser_noise_and_render_cover_text(self) -> None:
        document = _document(Path("screen.png"))
        document.metadata.update(
            {
                "ingestor": "openpyxl+ooxml",
                "template_match": {"unrecognized_ranges": {"表紙": ["A1:Z99"]}},
            }
        )
        sheet = document.sheets[0]
        sheet.regions.extend(
            [
                RegionIR(
                    region_id="cover-title",
                    region_type=RegionType.TABLE,
                    title="表紙タイトル",
                    tables=[
                        TableIR(
                            table_id="cover-title",
                            header_rows=0,
                            column_semantics={"E": "cover_text"},
                            cells=[
                                _cell("E4", 4, 5, "債務保証_平時"),
                                _cell("E5", 5, 5),
                                _cell("E6", 6, 5, "基本設計"),
                                _cell("E7", 7, 5, "SCR-A0030"),
                                _cell("E8", 8, 5, "保証審査_基本情報"),
                                _cell("E9", 9, 5, "画面設計書"),
                            ],
                        )
                    ],
                ),
                RegionIR(
                    region_id="partial-meta",
                    region_type=RegionType.KEY_VALUE,
                    title="文書情報",
                    values={"empty_field": None, "version": "0.91"},
                ),
                RegionIR(
                    region_id="empty-meta",
                    region_type=RegionType.KEY_VALUE,
                    title="空の基本情報",
                    values={"screen_id": None},
                ),
                RegionIR(
                    region_id="unrecognized-1",
                    region_type=RegionType.FREEFORM,
                    title="unrecognized-1",
                    tables=[
                        TableIR(
                            table_id="noise",
                            header_rows=0,
                            cells=[_cell("Z99", 99, 26, "方眼ノイズ")],
                        )
                    ],
                ),
            ]
        )

        markdown = MarkdownExporter().render(document)
        html_output = HtmlExporter().render(document)
        canonical = JsonExporter().render(document)

        for rendered in (markdown, html_output):
            self.assertNotIn("screen-001", rendered)
            self.assertNotIn("openpyxl+ooxml", rendered)
            self.assertNotIn("template_match", rendered)
            self.assertNotIn("unrecognized-1", rendered)
            self.assertNotIn("方眼ノイズ", rendered)
            self.assertNotIn("空の基本情報", rendered)
            self.assertNotIn("empty_field", rendered)
            self.assertIn("version", rendered)
            self.assertIn("0.91", rendered)
            self.assertIn("債務保証_平時", rendered)
            self.assertIn("基本設計", rendered)
            self.assertIn("画面設計書", rendered)
        self.assertIn("- 債務保証_平時", markdown)
        self.assertIn("<ul><li>債務保証_平時</li>", html_output)
        self.assertIn("unrecognized-1", canonical)
        self.assertIn("template_match", canonical)

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

    def test_group_row_renders_in_compact_table_and_residual_assets_hidden(self) -> None:
        source = SourceRef(sheet="入出力", range="A1:C4")
        table = TableIR(
            table_id="io",
            source=source,
            header_rows=1,
            column_semantics={"A": "seq_no", "B": "field_name", "C": "control_type"},
            metadata={
                "header_labels": {
                    "A": "No.",
                    "B": "画面項目名",
                    "C": "種別",
                }
            },
            cells=[
                _cell("A1", 1, 1, "No."),
                _cell("B1", 1, 2, "画面項目名"),
                _cell("C1", 1, 3, "種別"),
                _cell("A2", 2, 1, "グループ：基本情報入力", col_span=3),
                _cell("B2", 2, 2, merged_master="A2"),
                _cell("C2", 2, 3, merged_master="A2"),
                _cell("A3", 3, 1, "1"),
                _cell("B3", 3, 2, "保証種類"),
                _cell("C3", 3, 3, "プルダウンリスト"),
            ],
        )
        orphan = AssetIR(
            asset_id="orphan-image",
            asset_type=AssetType.IMAGE,
            uri="assets/orphan.png",
            description="残留图",
            source=SourceRef(sheet="入出力", cell="Z99"),
        )
        document = DocumentIR(
            document_id="io-group",
            title="グループ表示",
            sheets=[
                SheetIR(
                    sheet_id="io",
                    name="画面入出力項目一覧",
                    index=0,
                    assets=[orphan],
                    regions=[
                        RegionIR(
                            region_id="io-table",
                            region_type=RegionType.TABLE,
                            title="画面入出力項目一覧",
                            tables=[table],
                        )
                    ],
                )
            ],
        )
        markdown = MarkdownExporter().render(document)
        html_output = HtmlExporter().render(document)

        self.assertIn("グループ：基本情報入力", markdown)
        self.assertIn("**グループ：基本情報入力**", markdown)
        self.assertIn("保証種類", markdown)
        self.assertNotIn("## 资源", markdown)
        self.assertNotIn("残留图", markdown)
        self.assertNotIn("orphan-image", markdown)

        self.assertIn("グループ：基本情報入力", html_output)
        self.assertIn('colspan="3"', html_output)
        self.assertNotIn("资源", html_output)
        self.assertNotIn("残留图", html_output)

    def test_layout_interleaves_text_and_images_by_row(self) -> None:
        source = SourceRef(sheet="画面レイアウト", range="A6:H29")
        table = TableIR(
            table_id="layout",
            source=source,
            header_rows=0,
            cells=[
                _cell("A6", 6, 1, "■画面イメージ"),
                _cell("A7", 7, 1, "【初期表示】一覧"),
                _cell("A28", 28, 1, "※注記"),
                _cell("A29", 29, 1, "【検索後】更新"),
            ],
        )
        top = AssetIR(
            asset_id="img-top",
            asset_type=AssetType.IMAGE,
            uri="assets/top.png",
            description="初期画面",
            anchor="A8",
            source=SourceRef(sheet="画面レイアウト", cell="A8"),
        )
        bottom = AssetIR(
            asset_id="img-bottom",
            asset_type=AssetType.IMAGE,
            uri="assets/bottom.png",
            description="検索後",
            anchor="A30",
            source=SourceRef(sheet="画面レイアウト", cell="A30"),
        )
        document = DocumentIR(
            document_id="layout-order",
            title="レイアウト順序",
            sheets=[
                SheetIR(
                    sheet_id="layout",
                    name="画面レイアウト",
                    index=0,
                    assets=[top, bottom],
                    regions=[
                        RegionIR(
                            region_id="screen-layout",
                            region_type=RegionType.LAYOUT,
                            title="画面レイアウト",
                            metadata={"extractor_kind": "asset"},
                            tables=[table],
                            asset_ids=["img-top", "img-bottom"],
                        )
                    ],
                )
            ],
        )
        markdown = MarkdownExporter().render(document)
        html_output = HtmlExporter().render(document)

        self.assertLess(markdown.index("■画面イメージ"), markdown.index("初期画面"))
        self.assertLess(markdown.index("【初期表示】一覧"), markdown.index("初期画面"))
        self.assertLess(markdown.index("初期画面"), markdown.index("※注記"))
        self.assertLess(markdown.index("※注記"), markdown.index("【検索後】更新"))
        self.assertLess(markdown.index("【検索後】更新"), markdown.index("検索後"))
        self.assertLess(html_output.index("■画面イメージ"), html_output.index("初期画面"))
        self.assertLess(html_output.index("初期画面"), html_output.index("※注記"))
        self.assertLess(html_output.index("【検索後】更新"), html_output.index("検索後"))

    def test_layout_slots_unanchored_images_after_text_clusters(self) -> None:
        table = TableIR(
            table_id="layout",
            header_rows=0,
            cells=[
                _cell("A7", 7, 1, "キャプション1"),
                _cell("A8", 8, 1, "条件1"),
                _cell("A40", 40, 1, "キャプション2"),
                _cell("A41", 41, 1, "条件2"),
            ],
        )
        first = AssetIR(
            asset_id="img-1",
            asset_type=AssetType.IMAGE,
            uri="a.png",
            description="图1",
        )
        second = AssetIR(
            asset_id="img-2",
            asset_type=AssetType.IMAGE,
            uri="b.png",
            description="图2",
        )
        document = DocumentIR(
            document_id="gaps",
            title="gaps",
            sheets=[
                SheetIR(
                    sheet_id="layout",
                    name="画面レイアウト",
                    index=0,
                    assets=[first, second],
                    regions=[
                        RegionIR(
                            region_id="screen-layout",
                            region_type=RegionType.LAYOUT,
                            title="画面レイアウト",
                            metadata={"extractor_kind": "asset"},
                            tables=[table],
                            asset_ids=["img-1", "img-2"],
                        )
                    ],
                )
            ],
        )
        markdown = MarkdownExporter().render(document)
        self.assertLess(markdown.index("キャプション1"), markdown.index("图1"))
        self.assertLess(markdown.index("图1"), markdown.index("キャプション2"))
        self.assertLess(markdown.index("キャプション2"), markdown.index("图2"))

    def test_readable_values_prefer_japanese_value_labels(self) -> None:
        region = RegionIR(
            region_id="document-info",
            region_type=RegionType.KEY_VALUE,
            values={"document_no": "OKI-Q-102", "version": "0.91"},
            metadata={
                "value_labels": {
                    "document_no": "文書番号",
                    "version": "バージョン",
                }
            },
        )
        from excelspec.exporters._shared import readable_region_values

        self.assertEqual(
            [("文書番号", "OKI-Q-102"), ("バージョン", "0.91")],
            readable_region_values(region),
        )


if __name__ == "__main__":
    unittest.main()
