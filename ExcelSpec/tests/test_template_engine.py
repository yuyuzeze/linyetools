from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

import jsonschema

from excelspec.exporters import MarkdownExporter
from excelspec.models.document_ir import (
    AssetIR,
    AssetType,
    CellIR,
    DocumentIR,
    RegionIR,
    RegionType,
    SheetIR,
    SourceRef,
    StyleIR,
    TableIR,
)
from excelspec.schemas import load_schema
from excelspec.templates import (
    TemplateValidationError,
    apply_best_template,
    load_template,
    load_templates,
    validate_template_data,
)


ROOT = Path(__file__).resolve().parents[1]


def cell(coordinate: str, row: int, column: int, value: object) -> CellIR:
    return CellIR(
        coordinate=coordinate,
        row=row,
        column=column,
        raw_value=value,
        display_value=str(value) if value is not None else None,
    )


def raw_sheet(name: str, index: int, cells: list[CellIR]) -> SheetIR:
    source = SourceRef(sheet=name)
    return SheetIR(
        sheet_id=f"raw-{index}",
        name=name,
        index=index,
        regions=[
            RegionIR(
                region_id="raw-grid",
                region_type=RegionType.FREEFORM,
                tables=[TableIR(table_id="raw-grid", cells=cells, source=source)],
                source=source,
            )
        ],
    )


class TemplateLoadingTests(unittest.TestCase):
    def test_loads_yaml_and_json_templates_with_schema_validation(self) -> None:
        templates = load_templates(ROOT / "templates")

        self.assertEqual(
            {
                "linye-screen-design",
                "linye-screen-design-6sheet",
                "linye-api-spec",
                "linye-screen-transition",
            },
            {template.template_id for template in templates},
        )
        screen = load_template(ROOT / "templates" / "linye-screen-design-v1.yaml")
        self.assertEqual("画面設計書", screen.name)
        self.assertEqual("screen_id", screen.sheets[0].regions[0].extractor.key_semantics["画面ID"])

    def test_reports_schema_error_with_field_location(self) -> None:
        with self.assertRaises(TemplateValidationError) as raised:
            validate_template_data(
                {
                    "schema_version": "1.0",
                    "template_id": "broken",
                    "version": "1",
                    "name": "broken",
                    "sheets": [
                        {
                            "sheet_id": "s",
                            "name_pattern": ".*",
                            "regions": [
                                {
                                    "region_id": "r",
                                    "region_type": "table",
                                    "locator": {"mode": "fixed"},
                                }
                            ],
                        }
                    ],
                }
            )
        self.assertIn("range", str(raised.exception))


class TemplateEngineTests(unittest.TestCase):
    def _screen_document(self) -> DocumentIR:
        overview = raw_sheet(
            "画面設計",
            0,
            [
                cell("A1", 1, 1, "画面設計書"),
                cell("A2", 2, 1, "画面ID"),
                cell("B2", 2, 2, "SCR-001"),
                cell("A3", 3, 1, "画面名"),
                cell("B3", 3, 2, "利用者検索"),
                cell("A14", 14, 1, "画面レイアウト"),
                cell("A15", 15, 1, "検索条件"),
            ],
        )
        items = raw_sheet(
            "画面項目",
            1,
            [
                cell("A1", 1, 1, "画面項目一覧"),
                cell("A2", 2, 1, "項目ID"),
                cell("B2", 2, 2, "項目名"),
                cell("C2", 2, 3, "属性"),
                cell("C3", 3, 3, "データ型"),
                cell("A4", 4, 1, "USR-NAME"),
                cell("B4", 4, 2, "利用者名"),
                cell("C4", 4, 3, "文字列"),
                cell("A8", 8, 1, "備考"),
                cell("A9", 9, 1, "この行は未認識"),
            ],
        )
        return DocumentIR(
            document_id="screen-fixture",
            title="利用者検索画面",
            sheets=[overview, items],
        )

    def test_auto_match_fixed_anchor_kv_and_multiline_table(self) -> None:
        result = apply_best_template(
            self._screen_document(), load_templates(ROOT / "templates")
        )

        self.assertEqual("linye-screen-design", result.document.template_id)
        self.assertEqual("template", result.match.mode)
        self.assertEqual(
            "linye-screen-design", result.match.candidates[0].template_id
        )
        overview = result.document.sheets[0]
        document_info = next(
            region for region in overview.regions if region.region_id == "document-info"
        )
        self.assertEqual("SCR-001", document_info.values["screen_id"])
        self.assertEqual("利用者検索", document_info.values["screen_name"])

        item_sheet = result.document.sheets[1]
        item_table = next(
            region for region in item_sheet.regions if region.region_id == "screen-item-table"
        ).tables[0]
        self.assertEqual(2, item_table.header_rows)
        self.assertEqual("item_id", item_table.column_semantics["A"])
        self.assertEqual("data_type", item_table.column_semantics["C"])
        self.assertIn("A8:A9", result.unrecognized_ranges["画面項目"])
        self.assertEqual(
            "screen-layout-screenshot",
            next(asset for asset in overview.assets if asset.metadata.get("template_binding")).asset_id,
        )
        jsonschema.validate(result.document.to_dict(), load_schema("document-ir"))

    def test_low_score_uses_freeform_without_claiming_semantics(self) -> None:
        sheet = raw_sheet(
            "自由記述",
            0,
            [
                cell("A1", 1, 1, "概要"),
                cell("B1", 1, 2, "任意の説明"),
                cell("B2", 2, 2, None),
                cell("A3", 3, 1, "注記"),
            ],
        )
        sheet.assets.append(
            AssetIR(
                asset_id="shape-1",
                asset_type=AssetType.SHAPE,
                uri="ooxml://shape/1",
            )
        )
        document = DocumentIR(document_id="freeform", title="自由", sheets=[sheet])

        result = apply_best_template(document, load_templates(ROOT / "templates"))

        self.assertEqual("freeform", result.match.mode)
        self.assertIsNone(result.document.template_id)
        self.assertTrue(
            all(region.region_type == RegionType.FREEFORM for region in result.document.sheets[0].regions)
        )
        preserved = {
            item.coordinate
            for region in result.document.sheets[0].regions
            for table in region.tables
            for item in table.cells
        }
        self.assertEqual({"A1", "B1", "B2", "A3"}, preserved)
        self.assertEqual("shape-1", result.document.sheets[0].assets[0].asset_id)
        self.assertTrue(
            all(
                region.confidence is not None and region.confidence <= 0.25
                for region in result.document.sheets[0].regions
            )
        )

    def test_freeform_splits_styled_heading_without_losing_cells(self) -> None:
        heading = cell("A3", 3, 1, "詳細")
        heading.style = StyleIR(font={"bold": True})
        document = DocumentIR(
            document_id="styled-freeform",
            title="自由",
            sheets=[
                raw_sheet(
                    "自由記述",
                    0,
                    [
                        cell("A1", 1, 1, "概要"),
                        cell("A2", 2, 1, "説明"),
                        heading,
                        cell("A4", 4, 1, "詳細説明"),
                    ],
                )
            ],
        )

        result = apply_best_template(document, [])

        ranges = [
            region.source.range
            for region in result.document.sheets[0].regions
            if region.metadata.get("segmentation") == "blank-and-style-boundaries"
        ]
        self.assertEqual(["A1:A2", "A3:A4"], ranges)

    def test_trim_empty_columns_and_shrink_to_content(self) -> None:
        from excelspec.models.template import (
            ExtractionSpec,
            LocatorMode,
            RegionLocator,
            RegionTemplate,
            SheetTemplate,
            TemplateMatch,
            TemplateSpec,
        )
        from excelspec.templates import MatchResult, extract_with_template

        sheet = raw_sheet(
            "一覧",
            0,
            [
                cell("A1", 1, 1, "No."),
                cell("B1", 1, 2, None),
                cell("C1", 1, 3, "項目名"),
                cell("D1", 1, 4, None),
                cell("E1", 1, 5, None),
                cell("A2", 2, 1, 1),
                cell("B2", 2, 2, None),
                cell("C2", 2, 3, "保証番号"),
                cell("D2", 2, 4, None),
                cell("E2", 2, 5, None),
                cell("A3", 3, 1, None),
                cell("B3", 3, 2, None),
                cell("C3", 3, 3, None),
                cell("A10", 10, 1, "远处噪声"),
            ],
        )
        template = TemplateSpec(
            template_id="trim-demo",
            version="1.0",
            name="trim",
            schema_version="1.0",
            match=TemplateMatch(minimum_score=0.1),
            sheets=[
                SheetTemplate(
                    sheet_id="list",
                    name_pattern="^一覧$",
                    regions=[
                        RegionTemplate(
                            region_id="main-table",
                            region_type="table",
                            locator=RegionLocator(
                                mode=LocatorMode.FIXED,
                                range="A1:E20",
                            ),
                            extractor=ExtractionSpec(
                                kind="table",
                                header_rows=1,
                                options={
                                    "stop_after_blank_rows": 1,
                                    "shrink_to_content": True,
                                    "trim_empty_columns": True,
                                },
                            ),
                        )
                    ],
                )
            ],
        )
        document = DocumentIR(document_id="trim", title="trim", sheets=[sheet])
        result = extract_with_template(
            document,
            MatchResult(mode="template", template=template, candidates=[]),
        )
        region = result.document.sheets[0].regions[0]
        coords = {cell.coordinate for table in region.tables for cell in table.cells}
        self.assertEqual({"A1", "C1", "A2", "C2"}, coords)
        self.assertEqual("A1:C2", region.source.range)

    def test_key_value_grid_reads_label_pairs_without_fixed_columns(self) -> None:
        from excelspec.models.template import (
            ExtractionSpec,
            LocatorMode,
            RegionLocator,
            RegionTemplate,
            SheetTemplate,
            TemplateMatch,
            TemplateSpec,
        )
        from excelspec.templates import MatchResult, extract_with_template

        sheet = raw_sheet(
            "画面レイアウト",
            0,
            [
                cell("A1", 1, 1, "基本設計"),
                cell("D1", 1, 4, "プロダクト"),
                cell("H1", 1, 8, "林業業務システム"),
                cell("P1", 1, 16, "作成日"),
                cell("T1", 1, 20, "2026/3/24"),
                cell("X1", 1, 24, "作成者"),
                cell("AA1", 1, 27, "OKI"),
                cell("D2", 2, 4, "画面名"),
                # Deliberately blank: the following label must not be stolen.
                cell("P2", 2, 16, "画面ID"),
                cell("T2", 2, 20, "SCR-A0010"),
            ],
        )
        template = TemplateSpec(
            template_id="grid-kv",
            version="1.0",
            name="grid-kv",
            schema_version="1.0",
            match=TemplateMatch(minimum_score=0.1),
            sheets=[
                SheetTemplate(
                    sheet_id="layout",
                    name_pattern="^画面レイアウト$",
                    regions=[
                        RegionTemplate(
                            region_id="sheet-header",
                            region_type="key_value",
                            locator=RegionLocator(
                                mode=LocatorMode.FIXED,
                                range="A1:AA2",
                            ),
                            extractor=ExtractionSpec(
                                kind="key_value",
                                key_semantics={
                                    "プロダクト": "product_name",
                                    "作成日": "created_at",
                                    "作成者": "author",
                                    "画面名": "screen_name",
                                    "画面ID": "screen_id",
                                },
                                options={
                                    "scan_labels": True,
                                    "value_mode": "next_nonblank",
                                },
                            ),
                        )
                    ],
                )
            ],
        )
        result = extract_with_template(
            DocumentIR(document_id="grid-kv", title="grid-kv", sheets=[sheet]),
            MatchResult(mode="template", template=template, candidates=[]),
        )
        values = result.document.sheets[0].regions[0].values
        self.assertEqual("林業業務システム", values["product_name"])
        self.assertEqual("2026/3/24", values["created_at"])
        self.assertEqual("OKI", values["author"])
        self.assertIsNone(values["screen_name"])
        self.assertEqual("SCR-A0010", values["screen_id"])

    def test_auto_header_rows_uses_merged_header_height(self) -> None:
        from excelspec.models.template import (
            ExtractionSpec,
            LocatorMode,
            RegionLocator,
            RegionTemplate,
            SheetTemplate,
            TemplateMatch,
            TemplateSpec,
        )
        from excelspec.templates import MatchResult, extract_with_template

        no = cell("A1", 1, 1, "No.")
        no.row_span = 2
        name = cell("B1", 1, 2, "画面項目名")
        name.row_span = 2
        sheet = raw_sheet(
            "画面入出力項目一覧",
            0,
            [no, name, cell("A3", 3, 1, 1), cell("B3", 3, 2, "検索")],
        )
        template = TemplateSpec(
            template_id="auto-header",
            version="1.0",
            name="auto-header",
            schema_version="1.0",
            match=TemplateMatch(minimum_score=0.1),
            sheets=[
                SheetTemplate(
                    sheet_id="items",
                    name_pattern="^画面入出力項目一覧$",
                    regions=[
                        RegionTemplate(
                            region_id="items",
                            region_type="table",
                            locator=RegionLocator(
                                mode=LocatorMode.FIXED,
                                range="A1:B3",
                            ),
                            extractor=ExtractionSpec(
                                kind="table",
                                header_rows=1,
                                column_semantics={
                                    "^No\\.$": "seq_no",
                                    "^画面項目名$": "field_name",
                                },
                                options={"auto_header_rows": True},
                            ),
                        )
                    ],
                )
            ],
        )
        result = extract_with_template(
            DocumentIR(document_id="auto-header", title="auto", sheets=[sheet]),
            MatchResult(mode="template", template=template, candidates=[]),
        )
        region = result.document.sheets[0].regions[0]
        self.assertEqual(2, region.tables[0].header_rows)
        self.assertEqual(2, region.metadata["resolved_header_rows"])

    def test_six_sheet_template_matches_current_demo_without_com_screenshot(self) -> None:
        from excelspec.pipeline import run_pipeline

        workbook = ROOT / "demo" / "workbooks" / "SCR-A0010_画面設計書_保証一覧.xlsx"
        template = ROOT / "templates" / "linye-screen-design-6sheet-v1.yaml"
        with tempfile.TemporaryDirectory() as directory:
            result = run_pipeline(
                workbook,
                template=template,
                asset_dir=Path(directory) / "assets",
            )

        self.assertEqual("legacy-template", result.processing["processing_mode"])
        self.assertEqual("linye-screen-design-6sheet", result.document.template_id)
        self.assertEqual("1.1", result.document.template_version)
        sheets = {sheet.name: sheet for sheet in result.document.sheets}
        cover = next(
            region for region in sheets["表紙"].regions if region.region_id == "document-info"
        )
        self.assertEqual("0.01", cover.values["version"])
        revisions = next(
            region
            for region in sheets["修正履歴"].regions
            if region.region_id == "revision-table"
        )
        self.assertEqual("change", revisions.tables[0].column_semantics["E"])
        header = next(
            region
            for region in sheets["画面レイアウト"].regions
            if region.region_id == "sheet-header"
        )
        self.assertEqual("SCR-A0010", header.values["screen_id"])
        layout = next(
            region
            for region in sheets["画面レイアウト"].regions
            if region.region_id == "screen-layout"
        )
        self.assertEqual("A7:I28", layout.source.range)
        self.assertTrue(layout.asset_ids)
        # The layout reuses its embedded image (no COM). Only the cell-drawn
        # 凡例 needs an Excel screenshot; Excel is unavailable in tests, so it
        # fails gracefully — screenshot_failed is reported only for the legend,
        # and the legend keeps its text (fallback), never for the layout.
        failed_regions = sorted(
            {
                diagnostic.region_id
                for diagnostic in result.all_diagnostics()
                if diagnostic.code == "template.screenshot_failed"
            }
        )
        self.assertEqual(["legend"], failed_regions)
        legend = next(
            region
            for region in sheets["画面入出力項目一覧"].regions
            if region.region_id == "legend"
        )
        self.assertNotEqual("screenshot", legend.metadata.get("readable_mode"))
        self.assertTrue(any(table.cells for table in legend.tables))  # text preserved
        markdown = MarkdownExporter().render(result.document)
        cover_markdown = markdown.split("## 修正履歴", 1)[0]
        self.assertEqual(
            1,
            cover_markdown.count("- 林業業務システム更改に係る設計・開発"),
        )
        self.assertEqual(1, markdown.count("| 8 | 保証割合 |"))

    def test_ignore_option_excludes_sheet_header_from_document_ir(self) -> None:
        from excelspec.models.template import (
            ExtractionSpec,
            LocatorMode,
            RegionLocator,
            RegionTemplate,
            SheetTemplate,
            TemplateMatch,
            TemplateSpec,
        )
        from excelspec.templates import MatchResult, extract_with_template

        sheet = raw_sheet(
            "画面入出力項目一覧",
            0,
            [
                cell("A1", 1, 1, "プロダクト"),
                cell("B1", 1, 2, "林業システム"),
                cell("A4", 4, 1, "画面ID"),
                cell("B4", 4, 2, "SCR-A0030"),
                cell("A5", 5, 1, "No."),
                cell("B5", 5, 2, "画面項目名"),
                cell("A6", 6, 1, 1),
                cell("B6", 6, 2, "保証種類"),
            ],
        )
        template = TemplateSpec(
            template_id="ignore-header",
            version="1.0",
            name="ignore-header",
            schema_version="1.0",
            match=TemplateMatch(minimum_score=0.1),
            sheets=[
                SheetTemplate(
                    sheet_id="items",
                    name_pattern="^画面入出力項目一覧$",
                    regions=[
                        RegionTemplate(
                            region_id="ignored-sheet-header",
                            region_type="freeform",
                            order=0,
                            locator=RegionLocator(
                                mode=LocatorMode.FIXED,
                                range="A1:XFD4",
                            ),
                            extractor=ExtractionSpec(
                                kind="freeform",
                                options={"ignore": True},
                            ),
                        ),
                        RegionTemplate(
                            region_id="io-table",
                            region_type="table",
                            order=10,
                            locator=RegionLocator(
                                mode=LocatorMode.FIXED,
                                range="A5:B6",
                            ),
                            extractor=ExtractionSpec(
                                kind="table",
                                header_rows=1,
                            ),
                        ),
                    ],
                )
            ],
        )
        result = extract_with_template(
            DocumentIR(document_id="ignore", title="ignore", sheets=[sheet]),
            MatchResult(mode="template", template=template, candidates=[]),
        )

        regions = result.document.sheets[0].regions
        self.assertEqual(["io-table"], [region.region_id for region in regions])
        values = {
            cell.display_value
            for region in regions
            for table in region.tables
            for cell in table.cells
        }
        self.assertNotIn("プロダクト", values)
        self.assertNotIn("SCR-A0030", values)
        self.assertEqual([], result.unrecognized_ranges["画面入出力項目一覧"])

    def test_merged_header_maps_each_semantic_only_once(self) -> None:
        from excelspec.models.template import (
            ExtractionSpec,
            LocatorMode,
            RegionLocator,
            RegionTemplate,
            SheetTemplate,
            TemplateMatch,
            TemplateSpec,
        )
        from excelspec.templates import MatchResult, extract_with_template

        header_no = cell("A1", 1, 1, "No.")
        header_no.col_span = 2
        header_no_member = cell("B1", 1, 2, None)
        header_no_member.merged_master = "A1"
        header_event = cell("C1", 1, 3, "イベントID")
        header_event.col_span = 2
        header_event_member = cell("D1", 1, 4, None)
        header_event_member.merged_master = "C1"
        sheet = raw_sheet(
            "画面アクション一覧",
            0,
            [
                header_no,
                header_no_member,
                header_event,
                header_event_member,
                cell("A2", 2, 1, 1),
                cell("C2", 2, 3, "EV01"),
            ],
        )
        template = TemplateSpec(
            template_id="merged-header",
            version="1.0",
            name="merged-header",
            schema_version="1.0",
            match=TemplateMatch(minimum_score=0.1),
            sheets=[
                SheetTemplate(
                    sheet_id="actions",
                    name_pattern="^画面アクション一覧$",
                    regions=[
                        RegionTemplate(
                            region_id="action-table",
                            region_type="table",
                            locator=RegionLocator(
                                mode=LocatorMode.FIXED,
                                range="A1:D2",
                            ),
                            extractor=ExtractionSpec(
                                kind="table",
                                header_rows=1,
                                column_semantics={
                                    "^No\\.?$": "seq_no",
                                    "^イベントID$": "event_id",
                                },
                            ),
                        )
                    ],
                )
            ],
        )
        result = extract_with_template(
            DocumentIR(document_id="merged", title="merged", sheets=[sheet]),
            MatchResult(mode="template", template=template, candidates=[]),
        )
        table = result.document.sheets[0].regions[0].tables[0]

        self.assertEqual({"A": "seq_no", "C": "event_id"}, table.column_semantics)

    def test_repeat_anchor_splits_multiple_tables(self) -> None:
        from excelspec.models.template import (
            ExtractionSpec,
            LocatorMode,
            RegionLocator,
            RegionTemplate,
            SheetTemplate,
            TemplateMatch,
            TemplateSpec,
        )
        from excelspec.templates import MatchResult, extract_with_template

        sheet = raw_sheet(
            "複合",
            0,
            [
                cell("A1", 1, 1, "No."),
                cell("B1", 1, 2, "名称"),
                cell("A2", 2, 1, 1),
                cell("B2", 2, 2, "表1行"),
                cell("A4", 4, 1, "No."),
                cell("B4", 4, 2, "名称"),
                cell("A5", 5, 1, 1),
                cell("B5", 5, 2, "表2行"),
                cell("A6", 6, 1, 2),
                cell("B6", 6, 2, "表2行B"),
            ],
        )
        template = TemplateSpec(
            template_id="repeat-demo",
            version="1.0",
            name="repeat",
            schema_version="1.0",
            match=TemplateMatch(minimum_score=0.1),
            sheets=[
                SheetTemplate(
                    sheet_id="multi",
                    name_pattern="^複合$",
                    regions=[
                        RegionTemplate(
                            region_id="block",
                            region_type="table",
                            title="データ表",
                            locator=RegionLocator(
                                mode=LocatorMode.ANCHOR,
                                anchor_text="No.",
                                width=2,
                                repeat_anchor=True,
                            ),
                            extractor=ExtractionSpec(
                                kind="table",
                                header_rows=1,
                                options={"stop_after_blank_rows": 1},
                            ),
                        )
                    ],
                )
            ],
        )
        document = DocumentIR(document_id="repeat", title="repeat", sheets=[sheet])
        result = extract_with_template(
            document,
            MatchResult(mode="template", template=template, candidates=[]),
        )
        regions = [
            region
            for region in result.document.sheets[0].regions
            if region.metadata.get("template_region_id") == "block"
        ]
        self.assertEqual(["block", "block-2"], [region.region_id for region in regions])
        self.assertEqual("A1:B2", regions[0].source.range)
        self.assertEqual("A4:B6", regions[1].source.range)
        self.assertEqual("データ表 (2)", regions[1].title)

    def test_layout_region_claims_unbound_visual_assets(self) -> None:
        from excelspec.templates import MatchResult, extract_with_template
        from excelspec.models.template import (
            ExtractionSpec,
            LocatorMode,
            RegionLocator,
            RegionTemplate,
            SheetTemplate,
            TemplateMatch,
            TemplateSpec,
        )

        sheet = raw_sheet(
            "画面レイアウト",
            0,
            [
                cell("A1", 1, 1, "■画面イメージ"),
                cell("A2", 2, 1, "モック"),
            ],
        )
        sheet.assets = [
            AssetIR(
                asset_id="near",
                asset_type=AssetType.IMAGE,
                uri="near.png",
                source=SourceRef(sheet="画面レイアウト", range="A3:D20"),
            ),
            AssetIR(
                asset_id="far-below",
                asset_type=AssetType.IMAGE,
                uri="far.png",
                source=SourceRef(sheet="画面レイアウト", range="A80:H120"),
            ),
            AssetIR(
                asset_id="far-shape",
                asset_type=AssetType.SHAPE,
                uri="shape.xml",
                source=SourceRef(sheet="画面レイアウト", range="A90:B91"),
            ),
        ]
        template = TemplateSpec(
            template_id="layout-claim",
            version="1.0",
            name="layout",
            schema_version="1.0",
            match=TemplateMatch(minimum_score=0.1),
            sheets=[
                SheetTemplate(
                    sheet_id="layout",
                    name_pattern="^画面レイアウト$",
                    regions=[
                        RegionTemplate(
                            region_id="screen-layout",
                            region_type="layout",
                            title="画面レイアウト",
                            locator=RegionLocator(
                                mode=LocatorMode.ANCHOR,
                                anchor_pattern="^■画面イメージ$",
                                row_offset=1,
                                height=10,
                            ),
                            extractor=ExtractionSpec(kind="asset"),
                        )
                    ],
                )
            ],
        )
        result = extract_with_template(
            DocumentIR(document_id="layout", title="layout", sheets=[sheet]),
            MatchResult(mode="template", template=template, candidates=[]),
        )
        layout = next(
            region
            for region in result.document.sheets[0].regions
            if region.region_id == "screen-layout"
        )
        self.assertIn("near", layout.asset_ids)
        self.assertIn("far-below", layout.asset_ids)
        self.assertNotIn("far-shape", layout.asset_ids)

    def test_region_screenshot_option_uses_excel_com(self) -> None:
        from unittest.mock import patch

        from excelspec.templates import MatchResult, extract_with_template
        from excelspec.models.template import (
            ExtractionSpec,
            LocatorMode,
            RegionLocator,
            RegionTemplate,
            SheetTemplate,
            TemplateMatch,
            TemplateSpec,
        )

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            asset_dir = root / "assets"
            workbook = root / "sample.xlsx"
            workbook.write_bytes(b"unused")
            png = asset_dir / "screenshots" / "sheet-placeholder.png"

            def fake_capture(*, destination, workbook_path, sheet_name, a1_range):
                path = Path(destination)
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b"\x89PNG\r\n\x1a\nfake")
                self.assertTrue(Path(workbook_path).exists())
                self.assertEqual("画面入出力項目一覧", sheet_name)
                self.assertTrue(a1_range)
                return path, "excel_com"

            sheet = raw_sheet(
                "画面入出力項目一覧",
                0,
                [
                    cell("AD6", 6, 30, "■凡例"),
                    cell("AD7", 7, 30, "I/O"),
                    cell("AE7", 7, 31, "入出力"),
                ],
            )
            template = TemplateSpec(
                template_id="legend-shot",
                version="1.0",
                name="legend",
                schema_version="1.0",
                match=TemplateMatch(minimum_score=0.1),
                sheets=[
                    SheetTemplate(
                        sheet_id="io",
                        name_pattern="^画面入出力項目一覧$",
                        regions=[
                            RegionTemplate(
                                region_id="legend",
                                region_type="image",
                                title="凡例",
                                locator=RegionLocator(
                                    mode=LocatorMode.ANCHOR,
                                    anchor_pattern="^\\s*■?\\s*凡例",
                                    height=4,
                                    width=5,
                                ),
                                extractor=ExtractionSpec(
                                    kind="freeform",
                                    options={"screenshot": True},
                                ),
                            )
                        ],
                    )
                ],
            )
            document = DocumentIR(
                document_id="legend",
                title="legend",
                source_path=str(workbook),
                sheets=[sheet],
                metadata={"asset_directory": str(asset_dir)},
            )
            with patch(
                "excelspec.templates.engine.render_region_screenshot",
                side_effect=fake_capture,
            ):
                result = extract_with_template(
                    document,
                    MatchResult(mode="template", template=template, candidates=[]),
                )
            region = next(
                item
                for item in result.document.sheets[0].regions
                if item.region_id == "legend"
            )
            self.assertEqual("screenshot", region.metadata.get("readable_mode"))
            self.assertTrue(region.asset_ids)
            asset = next(
                item
                for item in result.document.sheets[0].assets
                if item.asset_id == region.asset_ids[0]
            )
            self.assertEqual(AssetType.SCREENSHOT, asset.asset_type)
            self.assertEqual("excel_com", asset.metadata.get("capture_method"))
            self.assertTrue(Path(asset.uri).is_file())
            markdown = MarkdownExporter().render(result.document)
            self.assertIn("### 凡例", markdown)
            self.assertIn("![凡例]", markdown)
            self.assertNotIn("- ■凡例", markdown)


if __name__ == "__main__":
    unittest.main()
