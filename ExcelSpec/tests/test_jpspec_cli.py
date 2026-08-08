from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path

from openpyxl import Workbook

from excelspec.inspection import write_inspection
from excelspec.jpspec_cli import main as jpspec_main
from excelspec.template_pack import compare_workbooks, init_template_pack
from excelspec.templates import load_template as load_template_file


ROOT = Path(__file__).resolve().parents[1]
DEMO_SCREEN = ROOT / "demo" / "workbooks"


def _first_demo_xlsx() -> Path:
    files = sorted(DEMO_SCREEN.glob("*.xlsx"))
    if not files:
        raise unittest.SkipTest("demo workbooks missing; run demo/build_demo_workbooks.py")
    return files[0]


def _mini_xlsx(path: Path) -> None:
    workbook = Workbook()
    sheet = workbook.active
    sheet.title = "画面入出力項目一覧"
    sheet["A1"] = "No."
    sheet["B1"] = "画面項目名"
    sheet["C1"] = "種別"
    sheet["A2"] = 1
    sheet["B2"] = "検索"
    sheet["C2"] = "ボタン"
    workbook.create_sheet("表紙")["A1"] = "画面設計書"
    workbook.save(path)
    workbook.close()


class InspectionTests(unittest.TestCase):
    def test_write_inspection_layout(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "sample.xlsx"
            output = Path(directory) / "inspection"
            _mini_xlsx(source)
            write_inspection(source, output)
            self.assertTrue((output / "workbook.json").is_file())
            self.assertTrue((output / "sheets" / "画面入出力項目一覧.json").is_file())
            self.assertTrue((output / "preview" / "画面入出力項目一覧.html").is_file())
            workbook = json.loads((output / "workbook.json").read_text(encoding="utf-8"))
            self.assertEqual(2, workbook["sheet_count"])


class TemplatePackTests(unittest.TestCase):
    def test_init_and_load_pack(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "sample.xlsx"
            pack = Path(directory) / "screen-design-v1"
            _mini_xlsx(source)
            init_template_pack(source, pack, document_type="screen-design")
            self.assertTrue((pack / "template.xlsx").is_file())
            self.assertTrue((pack / "template.yaml").is_file())
            self.assertTrue((pack / "schema.json").is_file())
            template = load_template_file(pack)
            self.assertTrue(template.template_id.startswith("screen-design"))

    def test_compare_same_structure(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            left = Path(directory) / "a.xlsx"
            right = Path(directory) / "b.xlsx"
            _mini_xlsx(left)
            _mini_xlsx(right)
            result = compare_workbooks(left, right)
            self.assertTrue(result["similar"])


class JpspecCliTests(unittest.TestCase):
    def test_inspect_parse_validate_commands(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "sample.xlsx"
            pack = root / "pack"
            inspection = root / "inspection"
            output = root / "output"
            _mini_xlsx(source)
            self.assertEqual(0, jpspec_main(["template", "init", str(source), "-o", str(pack), "--type", "screen-design"]))
            self.assertEqual(0, jpspec_main(["inspect", str(source), "-o", str(inspection)]))
            self.assertEqual(
                0,
                jpspec_main(
                    [
                        "parse",
                        str(source),
                        "--template",
                        str(pack),
                        "-o",
                        str(output),
                        "-f",
                        "json,md",
                    ]
                ),
            )
            canonical = output / "sample.json"
            markdown = output / "sample.md"
            assets = output / "asset.sample"
            self.assertTrue(canonical.is_file())
            self.assertTrue(markdown.is_file())
            self.assertTrue(assets.is_dir())
            self.assertEqual(0, jpspec_main(["validate", str(canonical), "--template", str(pack)]))

    def test_parse_batch_directory_uses_source_stems(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            inputs = root / "inputs"
            output = root / "output"
            pack = root / "pack"
            inputs.mkdir()
            first = inputs / "abc.xlsx"
            second = inputs / "def.xlsx"
            _mini_xlsx(first)
            _mini_xlsx(second)
            self.assertEqual(
                0,
                jpspec_main(
                    ["template", "init", str(first), "-o", str(pack), "--type", "screen-design"]
                ),
            )
            self.assertEqual(
                0,
                jpspec_main(
                    [
                        "parse",
                        str(inputs),
                        "--template",
                        str(pack),
                        "-o",
                        str(output),
                        "-f",
                        "json,md",
                    ]
                ),
            )
            self.assertTrue((output / "abc.json").is_file())
            self.assertTrue((output / "abc.md").is_file())
            self.assertTrue((output / "asset.abc").is_dir())
            self.assertTrue((output / "def.json").is_file())
            self.assertTrue((output / "def.md").is_file())
            self.assertTrue((output / "asset.def").is_dir())


if __name__ == "__main__":
    unittest.main()
