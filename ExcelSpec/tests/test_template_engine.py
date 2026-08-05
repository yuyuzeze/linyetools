from __future__ import annotations

import unittest
from pathlib import Path

import jsonschema

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
            {"linye-screen-design", "linye-screen-design-6sheet", "linye-api-spec"},
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


if __name__ == "__main__":
    unittest.main()
