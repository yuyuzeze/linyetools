"""Run all evaluation cases and aggregate structural metrics."""

from __future__ import annotations

import statistics
import tempfile
from pathlib import Path

from ..detect.assemble import assemble_document
from ..ingest import ingest_sparse_workbook
from ..semantic import assemble_semantic
from .fixtures import build_cases
from .metrics import evaluate_case


def _value_cells(sparse) -> dict[str, set[tuple[int, int]]]:
    result: dict[str, set[tuple[int, int]]] = {}
    for sheet in sparse.sheets:
        result[sheet.name] = {
            (cell.row, cell.column)
            for cell in sheet.cells.values()
            if cell.raw_value is not None
        }
    return result


def run_case(case, directory: Path) -> dict:
    path = directory / f"{case.case_id}.xlsx"
    case.build(path)
    sparse = ingest_sparse_workbook(path, asset_dir=directory / f"{case.case_id}_assets")
    document, _ = assemble_document(sparse, mode="fast")
    semantic = assemble_semantic(document)
    metrics = evaluate_case(
        case,
        value_cells_by_sheet=_value_cells(sparse),
        semantic_regions=semantic.regions,
    )
    metrics["reference_count"] = len(semantic.references)
    metrics["expected_references"] = case.expected_references
    metrics["tags"] = case.tags
    metrics["description"] = case.description
    return metrics


def run_all_cases() -> tuple[list[dict], dict]:
    cases = build_cases()
    results: list[dict] = []
    with tempfile.TemporaryDirectory() as directory:
        root = Path(directory)
        for case in cases:
            results.append(run_case(case, root))

    def _mean(key: str) -> float:
        values = [r[key] for r in results if r.get(key) is not None]
        return round(statistics.mean(values), 4) if values else 0.0

    aggregate = {
        "case_count": len(results),
        "region_recall": _mean("region_recall"),
        "region_precision": _mean("region_precision"),
        "type_accuracy": _mean("type_accuracy"),
        "table_recall": _mean("table_recall"),
        "table_precision": _mean("table_precision"),
        "header_accuracy": _mean("header_accuracy"),
        "row_count_accuracy": _mean("row_count_accuracy"),
        "total_content_loss": sum(r["content_loss"] for r in results),
        "total_duplicate_content": sum(r["duplicate_content"] for r in results),
    }
    return results, aggregate


__all__ = ["run_all_cases", "run_case"]
