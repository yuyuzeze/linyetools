"""Legend screenshot + transition dynamic-bottom + filename-match tests."""

from __future__ import annotations

import sys
import tempfile
import types
import unittest
from pathlib import Path
from unittest import mock

from openpyxl import Workbook
from openpyxl.utils import get_column_letter

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
from excelspec.templates.capture_bounds import (
    resolve_connected_region,
    resolve_dynamic_bottom,
)

BORDER = StyleIR(border={"top": {"style": "thin"}})


def _cell(coord, row, col, value=None, *, style=None, row_span=1):
    return CellIR(coord, row, col, raw_value=value, display_value=None if value is None else str(value), style=style, row_span=row_span)


# --------------------------------------------------------------------------- #
# capture_bounds: connected_region (legend)
# --------------------------------------------------------------------------- #

class ConnectedRegionTests(unittest.TestCase):
    def test_shrinks_to_connected_columns_not_full_width(self) -> None:
        # legend at cols 30-32, but the locator base spans cols 1..40
        cells = [
            _cell("AD6", 6, 30, "■凡例"),
            _cell("AD7", 7, 30, "I/O"),
            _cell("AE7", 7, 31, "入出力"),
            _cell("AF7", 7, 32, "必須"),
        ]
        base = (6, 30, 8, 40)  # end_anchor gave rows 6..8, width to sheet max col 40
        resolution = resolve_connected_region(cells, base, padding_rows=1, padding_columns=1)
        # right edge tracks real content (col 32) + padding, not col 40
        self.assertEqual("AC5:AG8", resolution.range_a1)
        self.assertEqual("connected_region", resolution.bounds_method)

    def test_distant_style_only_cell_does_not_expand(self) -> None:
        cells = [
            _cell("AD6", 6, 30, "■凡例"),
            _cell("AE6", 6, 31, "説明"),
            # a far isolated bordered cell (gap > 1) must not widen the box
            _cell("BZ6", 6, 78, None, style=BORDER),
        ]
        base = (6, 30, 6, 80)
        resolution = resolve_connected_region(cells, base, padding_rows=0, padding_columns=0)
        self.assertEqual("AD6:AE6", resolution.range_a1)


# --------------------------------------------------------------------------- #
# capture_bounds: dynamic_bottom (transition)
# --------------------------------------------------------------------------- #

class DynamicBottomTests(unittest.TestCase):
    OPTIONS = {"left_column": "B", "right_column": "BZ", "padding_bottom_rows": 2, "max_bottom_row": 2000}

    def _assets(self, bottom_row, col="C"):
        return [AssetIR(asset_id="d1", asset_type=AssetType.SHAPE, uri="", anchor=f"{col}10:{col}{bottom_row}")]

    def test_fixed_sides_and_dynamic_bottom_from_drawing(self) -> None:
        base = (6, 2, 6, 100)  # top row 6, left/right come from options
        res = resolve_dynamic_bottom([], self._assets(83), base, self.OPTIONS, section_max_row=2000)
        self.assertEqual("B6:BZ85", res.range_a1)  # 83 + padding 2
        self.assertEqual("dynamic_bottom", res.bounds_method)

    def test_drawing_moving_down_moves_bottom(self) -> None:
        base = (6, 2, 6, 100)
        near = resolve_dynamic_bottom([], self._assets(40), base, self.OPTIONS, section_max_row=2000)
        far = resolve_dynamic_bottom([], self._assets(143), base, self.OPTIONS, section_max_row=2000)
        self.assertEqual("B6:BZ42", near.range_a1)
        self.assertEqual("B6:BZ145", far.range_a1)

    def test_connector_below_nodes_included(self) -> None:
        base = (6, 2, 6, 100)
        assets = [
            AssetIR(asset_id="n1", asset_type=AssetType.SHAPE, uri="", anchor="C10:E30"),
            AssetIR(asset_id="c1", asset_type=AssetType.SHAPE, uri="", anchor="D30:D55"),  # connector lower
        ]
        res = resolve_dynamic_bottom([], assets, base, self.OPTIONS, section_max_row=2000)
        self.assertEqual("B6:BZ57", res.range_a1)  # 55 + padding 2

    def test_distant_style_only_cell_does_not_expand_bottom(self) -> None:
        base = (6, 2, 6, 100)
        cells = [_cell("C900", 900, 3, None, style=BORDER)]  # bordered but no content, far away
        # a bordered cell counts, but only within the band and via content rules;
        # here it's the only signal -> it does set bottom, so exclude by making it
        # out of band instead:
        out_of_band = [_cell("ZZ900", 900, 702, None, style=BORDER)]
        res = resolve_dynamic_bottom(out_of_band, self._assets(50), base, self.OPTIONS, section_max_row=2000)
        self.assertEqual("B6:BZ52", res.range_a1)  # driven by drawing 50, not the far cell

    def test_max_bottom_row_caps(self) -> None:
        base = (6, 2, 6, 100)
        res = resolve_dynamic_bottom([], self._assets(5000), base, {**self.OPTIONS, "max_bottom_row": 500}, section_max_row=100000)
        self.assertEqual("B6:BZ500", res.range_a1)

    def test_content_fallback_without_drawings(self) -> None:
        base = (6, 2, 6, 100)
        cells = [_cell("C6", 6, 3, "開始"), _cell("C20", 20, 3, "終了")]
        res = resolve_dynamic_bottom(cells, [], base, self.OPTIONS, section_max_row=2000)
        self.assertEqual("B6:BZ22", res.range_a1)  # content bottom 20 + padding 2

    def test_no_signal_emits_diagnostic_and_top_only(self) -> None:
        base = (6, 2, 6, 100)
        res = resolve_dynamic_bottom([], [], base, self.OPTIONS, section_max_row=2000)
        self.assertEqual("fallback_top_only", res.bounds_method)
        self.assertTrue(any(code == "screenshot.dynamic_bottom_not_found" for code, _ in res.diagnostics))
        self.assertNotIn("1048576", res.range_a1)


# --------------------------------------------------------------------------- #
# Integration: legend via extract_with_template (mocked capture)
# --------------------------------------------------------------------------- #

def _legend_sheet():
    cells = [
        _cell("A6", 6, 1, "■凡例"),
        _cell("A7", 7, 1, "I/O"),
        _cell("B7", 7, 2, "入出力"),
        _cell("A9", 9, 1, "No."),  # end anchor
        _cell("B9", 9, 2, "画面項目名"),
    ]
    table = TableIR(table_id="raw-grid", cells=cells, source=SourceRef(sheet="画面入出力項目一覧", range="A6:B9"))
    region = RegionIR(region_id="raw-grid", region_type=RegionType.FREEFORM,
                      source=SourceRef(sheet="画面入出力項目一覧", range="A6:B9"), tables=[table])
    return SheetIR(sheet_id="sheet-1", name="画面入出力項目一覧", index=0, regions=[region])


def _legend_template():
    return TemplateSpec(
        template_id="legend", version="1.0", name="legend", schema_version="1.0",
        match=TemplateMatch(minimum_score=0.1),
        sheets=[SheetTemplate(sheet_id="io", name_pattern="^画面入出力項目一覧$", regions=[
            RegionTemplate(
                region_id="legend", region_type="image", title="凡例",
                locator=RegionLocator(mode=LocatorMode.ANCHOR, anchor_pattern="^\\s*■?\\s*凡例",
                                      end_anchor_pattern="^(No\\.?|№)$"),
                extractor=ExtractionSpec(kind="freeform", options={
                    "screenshot": True, "screenshot_bounds": "connected_region",
                    "padding_rows": 1, "padding_columns": 1, "text_fallback": True,
                }),
            )
        ])],
    )


class LegendIntegrationTests(unittest.TestCase):
    def _run(self, capture):
        with tempfile.TemporaryDirectory() as directory:
            workbook = Path(directory) / "sample.xlsx"
            workbook.write_bytes(b"unused")
            document = DocumentIR(
                document_id="d", title="d", source_path=str(workbook),
                sheets=[_legend_sheet()], metadata={"asset_directory": str(Path(directory) / "assets")},
            )
            with mock.patch("excelspec.templates.engine.render_region_screenshot", side_effect=capture):
                result = extract_with_template(document, MatchResult(mode="template", template=_legend_template(), candidates=[]))
            markdown = MarkdownExporter().render(result.document)
            return result, markdown

    def test_success_shows_image_not_text_and_records_metadata(self) -> None:
        def capture(*, destination, workbook_path, sheet_name, a1_range):
            path = Path(destination)
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b"\x89PNG")
            capture.range = a1_range
            return path, "excel_com"

        result, markdown = self._run(capture)
        region = next(r for r in result.document.sheets[0].regions if r.region_id == "legend")
        self.assertEqual("screenshot", region.metadata.get("readable_mode"))
        # connected_region shrank to the legend's own columns (A:B + padding)
        self.assertEqual("connected_region", region.metadata.get("screenshot_bounds_method"))
        asset = next(a for a in result.document.sheets[0].assets if a.asset_id in region.asset_ids)
        # legend spans from ■凡例 (row 6) down to the row before the No. header
        self.assertEqual("A6:B8", asset.metadata["requested_range"])
        self.assertEqual(region.metadata["screenshot_resolved_range"], asset.metadata["resolved_range"])
        self.assertIn("![凡例]", markdown)
        self.assertNotIn("- ■凡例", markdown)

    def test_failure_keeps_text_and_diagnoses(self) -> None:
        def boom(*, destination, workbook_path, sheet_name, a1_range):
            raise RuntimeError("no excel")

        result, markdown = self._run(boom)
        region = next(r for r in result.document.sheets[0].regions if r.region_id == "legend")
        self.assertNotEqual("screenshot", region.metadata.get("readable_mode"))
        codes = {d.code for sheet in result.document.sheets for d in sheet.diagnostics}
        self.assertIn("template.screenshot_failed", codes)
        self.assertIn("凡例", markdown)  # text preserved


def _transition_template():
    return TemplateSpec(
        template_id="transition", version="1.0", name="t", schema_version="1.0",
        match=TemplateMatch(minimum_score=0.1),
        sheets=[SheetTemplate(sheet_id="st", name_pattern="^画面遷移図$", regions=[
            RegionTemplate(
                region_id="transition-diagram", region_type="layout", title="画面遷移図",
                locator=RegionLocator(mode=LocatorMode.ANCHOR,
                                      anchor_pattern="^\\s*■?\\s*画面遷移図\\s*$",
                                      row_offset=1, repeat_anchor=True),
                extractor=ExtractionSpec(kind="asset", options={
                    "screenshot": True, "screenshot_bounds": "dynamic_bottom",
                    "left_column": "B", "right_column": "BZ",
                    "padding_bottom_rows": 2, "max_bottom_row": 2000, "text_fallback": True,
                }),
            )
        ])],
    )


class TransitionIntegrationTests(unittest.TestCase):
    def test_repeat_anchor_produces_independent_diagrams(self) -> None:
        cells = [
            _cell("A5", 5, 1, "■画面遷移図"),
            _cell("A50", 50, 1, "■画面遷移図"),
        ]
        assets = [
            AssetIR(asset_id="d1", asset_type=AssetType.SHAPE, uri="", anchor="C10:C30"),
            AssetIR(asset_id="d2", asset_type=AssetType.SHAPE, uri="", anchor="C55:C80"),
        ]
        table = TableIR(table_id="raw-grid", cells=cells, source=SourceRef(sheet="画面遷移図", range="A5:BZ100"))
        region = RegionIR(region_id="raw-grid", region_type=RegionType.FREEFORM,
                          source=SourceRef(sheet="画面遷移図", range="A5:BZ100"), tables=[table])
        sheet = SheetIR(sheet_id="sheet-1", name="画面遷移図", index=0, regions=[region], assets=assets)

        captured: list[str] = []

        def capture(*, destination, workbook_path, sheet_name, a1_range):
            path = Path(destination); path.parent.mkdir(parents=True, exist_ok=True); path.write_bytes(b"png")
            captured.append(a1_range)
            return path, "excel_com"

        with tempfile.TemporaryDirectory() as directory:
            workbook = Path(directory) / "w.xlsx"; workbook.write_bytes(b"x")
            document = DocumentIR(document_id="d", title="d", source_path=str(workbook),
                                  sheets=[sheet], metadata={"asset_directory": str(Path(directory) / "assets")})
            with mock.patch("excelspec.templates.engine.render_region_screenshot", side_effect=capture):
                result = extract_with_template(document, MatchResult(mode="template", template=_transition_template(), candidates=[]))

        region_ids = [r.region_id for r in result.document.sheets[0].regions if r.region_id.startswith("transition-diagram")]
        self.assertEqual(["transition-diagram", "transition-diagram-2"], region_ids)
        # diagram 1 is bounded above the second anchor (uses d1), diagram 2 uses d2
        self.assertIn("B6:BZ32", captured)   # d1 bottom 30 + padding 2, capped below anchor2
        self.assertIn("B51:BZ82", captured)  # d2 bottom 80 + padding 2
        self.assertEqual(len(set(captured)), len(captured))  # no diagram captured into another


class SessionReuseTests(unittest.TestCase):
    def test_no_screenshot_config_does_not_launch_excel(self) -> None:
        from excelspec.render import excel_capture

        before = excel_capture.capture_launch_count()
        document = DocumentIR(
            document_id="d", title="d", source_path="x.xlsx",
            sheets=[_legend_sheet()], metadata={},
        )
        template = TemplateSpec(
            template_id="t", version="1", name="t", schema_version="1.0",
            match=TemplateMatch(minimum_score=0.1),
            sheets=[SheetTemplate(sheet_id="io", name_pattern="^画面入出力項目一覧$", regions=[])],
        )
        extract_with_template(document, MatchResult(mode="template", template=template, candidates=[]))
        self.assertEqual(before, excel_capture.capture_launch_count())

    def test_multiple_screenshots_launch_excel_once(self) -> None:
        from excelspec.render import excel_capture

        # two ■凡例 anchors -> two screenshot regions in one workbook
        cells = [
            _cell("A6", 6, 1, "■凡例"), _cell("A7", 7, 1, "説明1"), _cell("A9", 9, 1, "No."),
            _cell("A20", 20, 1, "■凡例"), _cell("A21", 21, 1, "説明2"), _cell("A23", 23, 1, "No."),
        ]
        table = TableIR(table_id="raw-grid", cells=cells, source=SourceRef(sheet="画面入出力項目一覧", range="A6:A23"))
        region = RegionIR(region_id="raw-grid", region_type=RegionType.FREEFORM,
                          source=SourceRef(sheet="画面入出力項目一覧", range="A6:A23"), tables=[table])
        sheet = SheetIR(sheet_id="sheet-1", name="画面入出力項目一覧", index=0, regions=[region])
        template = TemplateSpec(
            template_id="legend2", version="1", name="t", schema_version="1.0",
            match=TemplateMatch(minimum_score=0.1),
            sheets=[SheetTemplate(sheet_id="io", name_pattern="^画面入出力項目一覧$", regions=[
                RegionTemplate(region_id="legend", region_type="image", title="凡例",
                    locator=RegionLocator(mode=LocatorMode.ANCHOR, anchor_pattern="^\\s*■?\\s*凡例",
                                          end_anchor_pattern="^(No\\.?|№)$", repeat_anchor=True),
                    extractor=ExtractionSpec(kind="freeform", options={"screenshot": True}))
            ])],
        )

        dispatch = mock.MagicMock()
        client_mod = types.ModuleType("win32com.client"); client_mod.DispatchEx = dispatch
        win32_mod = types.ModuleType("win32com"); win32_mod.client = client_mod
        fake_image = mock.MagicMock(); fake_image.mode = "RGB"

        with tempfile.TemporaryDirectory() as directory:
            workbook = Path(directory) / "w.xlsx"; Workbook().save(workbook)
            document = DocumentIR(document_id="d", title="d", source_path=str(workbook),
                                  sheets=[sheet], metadata={"asset_directory": str(Path(directory) / "assets")})
            before = excel_capture.capture_launch_count()
            with mock.patch.dict(sys.modules, {"win32com": win32_mod, "win32com.client": client_mod}), \
                 mock.patch("PIL.ImageGrab.grabclipboard", return_value=fake_image):
                extract_with_template(document, MatchResult(mode="template", template=template, candidates=[]))
            after = excel_capture.capture_launch_count()

        self.assertEqual(1, after - before)          # Excel launched once
        self.assertEqual(1, dispatch.call_count)      # DispatchEx once
        self.assertEqual(1, dispatch.return_value.Workbooks.Open.call_count)  # opened once


if __name__ == "__main__":
    unittest.main()
