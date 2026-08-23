"""Feature: template matching by file-name regex."""

from __future__ import annotations

import tempfile
import unittest
from pathlib import Path
from unittest import mock

from openpyxl import Workbook

from excelspec.models.document_ir import DocumentIR, SheetIR
from excelspec.models.template import TemplateSpec
from excelspec.templates import TemplateValidationError, load_template
from excelspec.templates.engine import match_template, score_template


def _doc(source_path: str) -> DocumentIR:
    return DocumentIR(
        document_id="d", title="D", source_path=source_path,
        sheets=[SheetIR(sheet_id="s1", name="S", index=0, regions=[])],
    )


def _template(tid, *, file_patterns=None, require=False, minimum=0.1,
              sheet_pattern="^S$", priority=False) -> TemplateSpec:
    data = {
        "schema_version": "1.0", "template_id": tid, "version": "1", "name": tid,
        "match": {"sheet_name_patterns": [sheet_pattern], "minimum_score": minimum},
        "sheets": [{"sheet_id": "s", "name_pattern": sheet_pattern, "regions": []}],
    }
    if file_patterns is not None:
        data["match"]["file_name_patterns"] = file_patterns
        data["match"]["require_file_name_match"] = require
        data["match"]["file_name_priority"] = priority
    return TemplateSpec.from_dict(data)


class FilenameMatchTests(unittest.TestCase):
    def test_no_patterns_scoring_unchanged_by_filename(self) -> None:
        template = _template("plain")  # no file_name_patterns
        a = score_template(_doc("/x/a.xlsx"), template)
        b = score_template(_doc("/y/completely-different.xlsm"), template)
        self.assertEqual(a.score, b.score)
        self.assertEqual(0.0, a.filename_score)
        self.assertIsNone(a.filename_matched_pattern)

    def test_filename_match_success(self) -> None:
        template = _template("m", file_patterns=[r"^.*画面設計書.*\.xlsx$"])
        candidate = score_template(_doc("/x/SCR_画面設計書_v1.xlsx"), template)
        self.assertEqual(1.0, candidate.filename_score)
        self.assertIsNotNone(candidate.filename_matched_pattern)

    def test_required_rejected_when_no_match(self) -> None:
        req = _template("req", file_patterns=[r"^.*画面遷移図.*"], require=True)
        opt = _template("opt", file_patterns=[r"^.*設計書.*"], require=False)
        result = match_template(_doc("/x/SCR_設計書.xlsx"), [req, opt])
        by_id = {c.template_id: c for c in result.candidates}
        self.assertFalse(by_id["req"].filename_accepted)
        self.assertFalse(by_id["req"].accepted)
        self.assertEqual("opt", result.template.template_id)

    def test_name_only_not_full_path(self) -> None:
        template = _template("m", file_patterns=[r"^deep"])  # matches a dir, not the name
        candidate = score_template(_doc("/deep/dir/sample.xlsx"), template)
        self.assertEqual(0.0, candidate.filename_score)  # name is "sample.xlsx"

    def test_nfkc_and_case_insensitive(self) -> None:
        template = _template("m", file_patterns=[r"^scr-a\d+.*\.xlsx$"])
        # full-width SCR + upper-case extension normalize/fold to match
        candidate = score_template(_doc("/x/ＳＣＲ-A0010_x.XLSX"), template)
        self.assertEqual(1.0, candidate.filename_score)

    def test_invalid_regex_fails_at_load(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "bad.yaml"
            path.write_text(
                'schema_version: "1.0"\ntemplate_id: bad\nversion: "1"\nname: bad\n'
                'match:\n  file_name_patterns:\n    - "([unclosed"\n'
                "sheets:\n  - sheet_id: s\n    name_pattern: \"^S$\"\n    regions: []\n",
                encoding="utf-8",
            )
            with self.assertRaises(TemplateValidationError):
                load_template(path)

    def test_explicit_legacy_template_force_runs_with_info_diagnostic(self) -> None:
        from excelspec.pipeline import run_pipeline

        with tempfile.TemporaryDirectory() as directory:
            workbook = Path(directory) / "unrelated_name.xlsx"
            book = Workbook()
            book.active.title = "S"
            book.active.append(["A", "B"])
            book.save(workbook)
            template_path = Path(directory) / "t.json"
            import json
            template_path.write_text(
                json.dumps(_template("m", file_patterns=[r"^.*画面遷移図.*"], require=True).to_dict(), ensure_ascii=False),
                encoding="utf-8",
            )
            result = run_pipeline(workbook, template=template_path, asset_dir=Path(directory) / "a")
        self.assertEqual("legacy-template", result.processing["processing_mode"])
        codes = {d.code for d in result.all_diagnostics()}
        self.assertIn("template.filename_not_matched", codes)

    def test_file_name_priority_wins_over_higher_scoring_generic(self) -> None:
        # generic (no file_name_patterns) scores 1.0 on sheet; the transition-like
        # template scores lower but has file_name_priority + a filename+sheet match.
        generic = _template("generic", sheet_pattern="^画面遷移図$")
        priority = _template("transition", file_patterns=[r"^.*画面遷移図.*"],
                             require=True, priority=True, sheet_pattern="^画面遷移図$", minimum=0.1)
        doc = _doc("/x/SCR_画面遷移図.xlsx")
        doc.sheets[0].name = "画面遷移図"
        result = match_template(doc, [generic, priority])
        self.assertEqual("transition", result.template.template_id)

    def test_file_name_priority_not_applied_without_filename_match(self) -> None:
        # same templates, but the file name does not match -> priority template is
        # rejected (require) and generic wins.
        generic = _template("generic", sheet_pattern="^画面遷移図$")
        priority = _template("transition", file_patterns=[r"^.*画面遷移図.*"],
                             require=True, priority=True, sheet_pattern="^画面遷移図$")
        doc = _doc("/x/unrelated.xlsx")
        doc.sheets[0].name = "画面遷移図"
        result = match_template(doc, [generic, priority])
        self.assertEqual("generic", result.template.template_id)

    def test_default_fast_does_not_run_template_matching(self) -> None:
        from excelspec import pipeline

        with tempfile.TemporaryDirectory() as directory:
            workbook = Path(directory) / "w.xlsx"
            book = Workbook()
            book.active.title = "S"
            book.active.append(["A", "B"])
            book.save(workbook)
            with mock.patch("excelspec.templates.engine.match_template") as m1, mock.patch(
                "excelspec.templates.engine.score_template"
            ) as m2:
                pipeline.run_pipeline(workbook, asset_dir=Path(directory) / "a")  # default fast
            m1.assert_not_called()
            m2.assert_not_called()


if __name__ == "__main__":
    unittest.main()
