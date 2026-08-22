"""RC evaluation-harness regression tests and determinism checks.

These assert the STRUCTURAL accuracy of detection on synthetic annotated cases.
They do NOT claim real-world business accuracy (no real workbooks in this repo).
"""

from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

from excelspec.eval.runner import run_all_cases
from excelspec.exporters import ChunksJsonlExporter, JsonExporter, SemanticJsonExporter
from excelspec.pipeline import run_pipeline

FIXTURES = Path(__file__).resolve().parent / "fixtures"


class EvalHarnessTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.results, cls.aggregate = run_all_cases()

    def test_no_content_loss_or_duplication(self) -> None:
        self.assertEqual(0, self.aggregate["total_content_loss"])
        self.assertEqual(0, self.aggregate["total_duplicate_content"])

    def test_type_and_header_accuracy(self) -> None:
        self.assertEqual(1.0, self.aggregate["type_accuracy"])
        self.assertEqual(1.0, self.aggregate["header_accuracy"])
        self.assertEqual(1.0, self.aggregate["row_count_accuracy"])

    def test_region_recall_and_precision(self) -> None:
        self.assertGreaterEqual(self.aggregate["region_recall"], 0.9)
        self.assertGreaterEqual(self.aggregate["region_precision"], 0.9)

    def test_key_value_cases_not_misclassified_as_table(self) -> None:
        by_id = {r["case_id"]: r for r in self.results}
        for case_id in ("multi_kv_per_row", "horizontal_kv", "cross_sheet_formula"):
            self.assertEqual(1.0, by_id[case_id]["type_accuracy"], case_id)

    def test_three_row_header_detected(self) -> None:
        by_id = {r["case_id"]: r for r in self.results}
        self.assertEqual(1.0, by_id["three_row_header"]["header_accuracy"])

    def test_layout_box_detected(self) -> None:
        by_id = {r["case_id"]: r for r in self.results}
        self.assertGreaterEqual(by_id["graph_paper_layout"]["region_recall"], 0.5)


class HeaderEvidenceTests(unittest.TestCase):
    def test_header_decision_records_evidence(self) -> None:
        from excelspec.detect.router import detect_header_rows
        from excelspec.models.document_ir import CellIR, StyleIR

        bold = StyleIR(font={"bold": True})
        cells = [
            CellIR("A1", 1, 1, "ID", "ID", style=bold),
            CellIR("B1", 1, 2, "名称", "名称", style=bold),
            CellIR("A2", 2, 1, 1, "1"),
            CellIR("B2", 2, 2, "a", "a"),
        ]
        header_rows, decision = detect_header_rows(cells, (1, 1, 2, 2), {})
        self.assertEqual(1, header_rows)
        self.assertIn("styled-row", decision["evidence"])
        self.assertIn("confidence", decision)


class DeterminismTests(unittest.TestCase):
    WORKBOOK = FIXTURES / "workbooks" / "screen-design.xlsx"

    def test_three_runs_are_byte_identical(self) -> None:
        # Same input AND same asset_dir/config -> byte-identical outputs.
        chunk_outputs, json_outputs, sem_outputs = [], [], []
        with tempfile.TemporaryDirectory() as directory:
            assets = Path(directory) / "assets"
            for _ in range(3):
                result = run_pipeline(self.WORKBOOK, mode="fast", asset_dir=assets)
                chunk_outputs.append(ChunksJsonlExporter().render(result.document))
                json_outputs.append(JsonExporter().render(result.document))
                sem_outputs.append(SemanticJsonExporter().render(result.document))
        self.assertEqual(1, len(set(chunk_outputs)))
        self.assertEqual(1, len(set(json_outputs)))
        self.assertEqual(1, len(set(sem_outputs)))

    def test_chunk_ids_and_hashes_stable(self) -> None:
        from excelspec.chunking import chunk_document
        from excelspec.semantic import assemble_semantic

        runs = []
        for _ in range(3):
            with tempfile.TemporaryDirectory() as directory:
                result = run_pipeline(self.WORKBOOK, mode="fast", asset_dir=Path(directory))
            chunks = chunk_document(assemble_semantic(result.document))
            runs.append([(c.chunk_id, c.content_hash) for c in chunks])
        self.assertEqual(runs[0], runs[1])
        self.assertEqual(runs[1], runs[2])
        # no duplicate chunk ids
        ids = [cid for cid, _ in runs[0]]
        self.assertEqual(len(ids), len(set(ids)))


if __name__ == "__main__":
    unittest.main()
