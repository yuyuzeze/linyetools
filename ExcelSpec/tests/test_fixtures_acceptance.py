from __future__ import annotations

import json
import os
import shutil
import tempfile
import unittest
from contextlib import redirect_stdout
from io import StringIO
from pathlib import Path

from excelspec.cli import main
from excelspec.exporters import (
    HtmlExporter,
    JsonExporter,
    KnowledgeBaseJsonlExporter,
    MarkdownExporter,
)
from excelspec.models.document_ir import AssetType, DocumentIR, RegionType
from excelspec.pipeline import run_pipeline
from excelspec.templates import TemplateValidationError, load_template


ROOT = Path(__file__).resolve().parent
FIXTURES = ROOT / "fixtures"
GOLDEN = FIXTURES / "golden"
UPDATE_GOLDENS = os.environ.get("EXCELSPEC_UPDATE_GOLDENS") == "1"

CASES = {
    "screen-design": {
        "workbook": FIXTURES / "workbooks" / "screen-design.xlsx",
        "template": FIXTURES / "templates" / "screen-design.yaml",
        "manifest": FIXTURES / "screenshots.json",
    },
    "api-spec": {
        "workbook": FIXTURES / "workbooks" / "api-spec.xlsx",
        "template": FIXTURES / "templates" / "api-spec.json",
        "manifest": None,
    },
}


def _all_sources(document: DocumentIR):
    for asset in document.assets:
        if asset.source:
            yield asset.source
    for diagnostic in document.diagnostics:
        if diagnostic.source:
            yield diagnostic.source
    for sheet in document.sheets:
        for asset in sheet.assets:
            if asset.source:
                yield asset.source
        for diagnostic in sheet.diagnostics:
            if diagnostic.source:
                yield diagnostic.source
        for region in sheet.regions:
            if region.source:
                yield region.source
            for table in region.tables:
                if table.source:
                    yield table.source
                for cell in table.cells:
                    if cell.source:
                        yield cell.source


def _normalized_document(name: str, asset_dir: Path) -> DocumentIR:
    case = CASES[name]
    result = run_pipeline(
        case["workbook"],
        template=case["template"],
        screenshot_manifest=case["manifest"],
        asset_dir=asset_dir,
    )
    if result.validation.failed():
        raise AssertionError(result.validation.to_dict())
    document = DocumentIR.from_dict(result.document.to_dict())
    source_name = f"fixtures/workbooks/{case['workbook'].name}"
    document.source_path = source_name
    document.metadata["asset_directory"] = "fixtures/assets"
    # Phase 2: the sparse and legacy ingestors carry different ingestor metadata
    # (engine name, fallback flags, sparse stats). Business content is identical,
    # so normalize just this ingestor-identifying metadata for the shared golden.
    document.metadata["ingestor"] = "openpyxl+ooxml"
    for key in ("legacy_fallback", "fallback_reason", "sparse_stats"):
        document.metadata.pop(key, None)
    for source in _all_sources(document):
        if source.workbook_path:
            source.workbook_path = source_name
    for asset in [
        *document.assets,
        *(item for sheet in document.sheets for item in sheet.assets),
    ]:
        if asset.asset_type == AssetType.LAYOUT:
            asset.uri = "fixtures/screens/layout.png"
        elif asset.asset_type == AssetType.IMAGE:
            asset.uri = f"fixtures/assets/{Path(asset.uri).name}"
    return document


def _renderings(document: DocumentIR, name: str) -> dict[str, str]:
    return {
        "ir.json": JsonExporter().render(document),
        "md": MarkdownExporter().render(document, Path(f"{name}.md")),
        "html": HtmlExporter().render(document, Path(f"{name}.html")),
        "jsonl": KnowledgeBaseJsonlExporter().render(document),
    }


class FixtureIntegrationTests(unittest.TestCase):
    def test_screen_fixture_covers_semantics_merges_and_assets(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            result = run_pipeline(
                CASES["screen-design"]["workbook"],
                template=CASES["screen-design"]["template"],
                screenshot_manifest=CASES["screen-design"]["manifest"],
                asset_dir=Path(directory) / "assets",
            )

        self.assertEqual("fixture-screen-design", result.document.template_id)
        self.assertFalse(result.validation.failed())
        sheets = {sheet.name: sheet for sheet in result.document.sheets}
        self.assertEqual({"表紙", "改訂履歴", "画面項目"}, set(sheets))

        cover = next(
            region
            for region in sheets["表紙"].regions
            if region.region_id == "document-info"
        )
        self.assertEqual("SCR-DEMO-001", cover.values["screen_id"])
        history = next(
            region
            for region in sheets["改訂履歴"].regions
            if region.region_id == "revision-history"
        )
        self.assertEqual(2, history.tables[0].header_rows)

        items = next(
            region
            for region in sheets["画面項目"].regions
            if region.region_id == "screen-item-table"
        )
        table = items.tables[0]
        self.assertEqual("A2:H6", items.source.range)
        self.assertEqual(2, table.header_rows)
        self.assertEqual("data_type", table.column_semantics["C"])
        cells = {cell.coordinate: cell for cell in table.cells}
        self.assertEqual(2, cells["A2"].row_span)
        self.assertEqual(2, cells["C2"].col_span)
        self.assertEqual("A2", cells["A3"].merged_master)

        layout = next(
            region
            for region in sheets["表紙"].regions
            if region.region_id == "screen-layout"
        )
        self.assertEqual("A13:H24", layout.source.range)
        assets = {asset.asset_type: asset for asset in sheets["表紙"].assets}
        self.assertEqual(
            {AssetType.IMAGE, AssetType.SHAPE, AssetType.LAYOUT}, set(assets)
        )
        self.assertEqual("B13", assets[AssetType.IMAGE].anchor)
        self.assertEqual("検索ボタンで一覧を更新", assets[AssetType.SHAPE].description)
        self.assertEqual("pending", assets[AssetType.LAYOUT].metadata["ocr"]["status"])
        self.assertTrue(
            {asset.asset_id for asset in sheets["表紙"].assets}.issubset(
                set(layout.asset_ids)
            )
        )

    def test_api_fixture_and_freeform_preserve_complete_input(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            templated = run_pipeline(
                CASES["api-spec"]["workbook"],
                template=CASES["api-spec"]["template"],
                asset_dir=root / "assets",
            )
            empty_templates = root / "empty-templates"
            empty_templates.mkdir()
            freeform = run_pipeline(
                CASES["api-spec"]["workbook"],
                template_directory=empty_templates,
                asset_dir=root / "freeform-assets",
            )

        self.assertEqual("fixture-api-spec", templated.document.template_id)
        self.assertFalse(templated.validation.failed())
        request = next(
            region
            for sheet in templated.document.sheets
            for region in sheet.regions
            if region.region_id == "request-fields"
        )
        self.assertEqual("parameter_name", request.tables[0].column_semantics["A"])
        self.assertEqual("freeform", freeform.match.mode)
        self.assertIsNone(freeform.document.template_id)
        self.assertTrue(
            all(
                region.region_type == RegionType.FREEFORM
                for sheet in freeform.document.sheets
                for region in sheet.regions
            )
        )
        self.assertIn(
            "/v1/demo-users",
            {
                cell.display_value
                for sheet in freeform.document.sheets
                for region in sheet.regions
                for table in region.tables
                for cell in table.cells
            },
        )

    def test_invalid_template_returns_schema_diagnostic(self) -> None:
        invalid = FIXTURES / "templates" / "invalid-template.yaml"
        with self.assertRaises(TemplateValidationError):
            load_template(invalid)

        output = StringIO()
        with redirect_stdout(output):
            exit_code = main(
                [
                    "inspect",
                    str(CASES["screen-design"]["workbook"]),
                    "--template",
                    str(invalid),
                    "--json",
                ]
            )
        payload = json.loads(output.getvalue())
        self.assertEqual(1, exit_code)
        self.assertEqual(
            "schema.template", payload["results"][0]["diagnostics"][0]["code"]
        )

    def test_batch_cli_converts_both_fixture_types(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            inputs = root / "inputs"
            templates = root / "templates"
            outputs = root / "outputs"
            inputs.mkdir()
            templates.mkdir()
            for case in CASES.values():
                shutil.copy2(case["workbook"], inputs / case["workbook"].name)
                shutil.copy2(case["template"], templates / case["template"].name)
            stdout = StringIO()
            with redirect_stdout(stdout):
                exit_code = main(
                    [
                        "convert",
                        str(inputs),
                        "--template",
                        str(templates),
                        "--output",
                        str(outputs),
                        "--format",
                        "json",
                        "--json",
                    ]
                )
            payload = json.loads(stdout.getvalue())

            self.assertEqual(0, exit_code)
            self.assertEqual(2, len(payload["results"]))
            self.assertEqual(
                {"fixture-api-spec", "fixture-screen-design"},
                {
                    item["template"]["selected"]
                    for item in payload["results"]
                },
            )
            self.assertEqual(
                {"api-spec.json", "screen-design.json"},
                {path.name for path in outputs.glob("*.json")},
            )


class GoldenSnapshotTests(unittest.TestCase):
    def test_fixture_snapshots(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            asset_root = Path(directory) / "assets"
            for name in CASES:
                document = _normalized_document(name, asset_root / name)
                for suffix, actual in _renderings(document, name).items():
                    snapshot = GOLDEN / f"{name}.{suffix}"
                    if UPDATE_GOLDENS:
                        snapshot.parent.mkdir(parents=True, exist_ok=True)
                        snapshot.write_text(actual, encoding="utf-8")
                    self.assertTrue(
                        snapshot.is_file(),
                        f"missing snapshot: {snapshot}; set EXCELSPEC_UPDATE_GOLDENS=1",
                    )
                    self.assertEqual(
                        snapshot.read_text(encoding="utf-8"),
                        actual,
                        f"snapshot differs: {snapshot}",
                    )


if __name__ == "__main__":
    unittest.main()
