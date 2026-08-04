from __future__ import annotations

import json
import tempfile
import unittest
from contextlib import redirect_stdout
from io import StringIO
from pathlib import Path

from openpyxl import Workbook

from excelspec.cli import main
from excelspec.models.document_ir import (
    CellIR,
    DocumentIR,
    RegionIR,
    RegionType,
    SheetIR,
    SourceRef,
    TableIR,
)
from excelspec.models.template import TemplateSpec
from excelspec.pipeline import export_document
from excelspec.validate import (
    validate_business_rules,
    validate_ir_schema,
    validate_template_structure,
)


def _template() -> TemplateSpec:
    return TemplateSpec.from_dict(
        {
            "schema_version": "1.0",
            "template_id": "validation-fixture",
            "version": "1",
            "name": "Validation fixture",
            "match": {"sheet_name_patterns": ["^Items$"], "minimum_score": 0.5},
            "sheets": [
                {
                    "sheet_id": "items",
                    "name_pattern": "^Items$",
                    "regions": [
                        {
                            "region_id": "items-table",
                            "region_type": "table",
                            "required": True,
                            "locator": {"mode": "fixed", "range": "A1:B4"},
                            "extractor": {
                                "kind": "table",
                                "header_rows": 1,
                                "column_semantics": {"ID": "item_id", "Type": "type"},
                            },
                            "validation_rules": [
                                {
                                    "rule_id": "unique-id",
                                    "kind": "unique",
                                    "field": "item_id",
                                },
                                {
                                    "rule_id": "known-type",
                                    "kind": "enum",
                                    "field": "type",
                                    "options": {"values": ["string", "number"]},
                                },
                            ],
                        }
                    ],
                }
            ],
            "validation_rules": [
                {
                    "rule_id": "required-id",
                    "kind": "required",
                    "field": "items.items-table.item_id",
                }
            ],
        }
    )


def _document() -> DocumentIR:
    source = SourceRef(sheet="Items", range="A1:B4")
    cells = [
        CellIR("A1", 1, 1, "ID", "ID"),
        CellIR("B1", 1, 2, "Type", "Type"),
        CellIR("A2", 2, 1, "A", "A"),
        CellIR("B2", 2, 2, "string", "string"),
        CellIR("A3", 3, 1, "A", "A"),
        CellIR("B3", 3, 2, "invalid", "invalid"),
    ]
    return DocumentIR(
        document_id="fixture",
        title="Fixture",
        template_id="validation-fixture",
        template_version="1",
        sheets=[
            SheetIR(
                sheet_id="items",
                name="Items",
                index=0,
                regions=[
                    RegionIR(
                        region_id="items-table",
                        region_type=RegionType.TABLE,
                        source=source,
                        tables=[
                            TableIR(
                                table_id="items-table",
                                cells=cells,
                                source=source,
                                header_rows=1,
                                column_semantics={"A": "item_id", "B": "type"},
                            )
                        ],
                    )
                ],
            )
        ],
    )


class ValidationTests(unittest.TestCase):
    def test_schema_template_and_business_diagnostics_are_machine_readable(self) -> None:
        document = _document()
        document.document_id = ""

        schema_diagnostics = validate_ir_schema(document)
        template_diagnostics = validate_template_structure(
            {"schema_version": "1.0"}, path="broken.yaml"
        )
        business_diagnostics = validate_business_rules(document, _template())

        self.assertEqual("schema.document_ir", schema_diagnostics[0].code)
        self.assertEqual("schema.template", template_diagnostics[0].code)
        self.assertTrue(
            {"business.unique", "business.enum"}.issubset(
                {item.code for item in business_diagnostics}
            )
        )
        self.assertTrue(
            all(
                item.source is not None and item.region_id == "items-table"
                for item in business_diagnostics
            )
        )

    def test_required_sheet_and_region_are_reported(self) -> None:
        document = DocumentIR(document_id="empty", title="Empty", sheets=[])
        diagnostics = validate_business_rules(document, _template())
        self.assertEqual(["business.required_sheet"], [item.code for item in diagnostics])


class CliTests(unittest.TestCase):
    def test_pipeline_dispatches_available_exporters(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            destinations = {
                "json": root / "document.json",
                "md": root / "document.md",
                "html": root / "document.html",
                "jsonl": root / "document.jsonl",
            }

            for format_name, destination in destinations.items():
                export_document(_document(), destination, format_name)

            self.assertEqual("fixture", json.loads(destinations["json"].read_text(encoding="utf-8"))["document_id"])
            self.assertIn("# Fixture", destinations["md"].read_text(encoding="utf-8"))
            self.assertIn("<!doctype html>", destinations["html"].read_text(encoding="utf-8"))
            self.assertEqual(
                "fixture",
                json.loads(destinations["jsonl"].read_text(encoding="utf-8").splitlines()[0])[
                    "metadata"
                ]["document_id"],
            )

    def test_invalid_ir_reports_schema_diagnostic(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            ir_path = Path(directory) / "invalid.json"
            ir_path.write_text(
                json.dumps(
                    {
                        "schema_version": "1.0",
                        "document_id": "",
                        "title": "Invalid",
                        "sheets": [],
                    }
                ),
                encoding="utf-8",
            )
            output = StringIO()

            with redirect_stdout(output):
                exit_code = main(["validate", str(ir_path), "--json"])

            payload = json.loads(output.getvalue())
            self.assertEqual(1, exit_code)
            self.assertEqual(
                "schema.document_ir", payload["results"][0]["diagnostics"][0]["code"]
            )

    def test_validate_ir_outputs_json_and_strict_exit_code(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            ir_path = root / "document.json"
            template_path = root / "template.json"
            _document().dump_json(ir_path)
            template_path.write_text(
                json.dumps(_template().to_dict(), ensure_ascii=False),
                encoding="utf-8",
            )
            output = StringIO()

            with redirect_stdout(output):
                exit_code = main(
                    [
                        "validate",
                        str(ir_path),
                        "--template",
                        str(template_path),
                        "--json",
                    ]
                )

            payload = json.loads(output.getvalue())
            self.assertEqual(1, exit_code)
            self.assertEqual("business.unique", payload["results"][0]["diagnostics"][0]["code"])

    def test_convert_xlsx_with_explicit_template_to_json(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            workbook_path = root / "items.xlsx"
            template_path = root / "template.json"
            output_path = root / "result.json"
            workbook = Workbook()
            sheet = workbook.active
            sheet.title = "Items"
            sheet.append(["ID", "Type"])
            sheet.append(["A", "string"])
            workbook.save(workbook_path)
            template_path.write_text(
                json.dumps(_template().to_dict(), ensure_ascii=False),
                encoding="utf-8",
            )

            exit_code = main(
                [
                    "convert",
                    str(workbook_path),
                    "--template",
                    str(template_path),
                    "--format",
                    "json",
                    "--output",
                    str(output_path),
                    "--json",
                ]
            )

            self.assertEqual(0, exit_code)
            exported = json.loads(output_path.read_text(encoding="utf-8"))
            self.assertEqual("validation-fixture", exported["template_id"])
            self.assertEqual("A", exported["sheets"][0]["regions"][0]["tables"][0]["cells"][2]["raw_value"])


if __name__ == "__main__":
    unittest.main()
