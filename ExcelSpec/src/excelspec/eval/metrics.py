"""Structural accuracy metrics against hand annotations."""

from __future__ import annotations

from openpyxl.utils import range_boundaries

from .models import EvalCase, ExpectedRegion


def _rect(a1: str) -> tuple[int, int, int, int]:
    # range_boundaries -> (min_col, min_row, max_col, max_row)
    min_col, min_row, max_col, max_row = range_boundaries(a1)
    return (min_row, min_col, max_row, max_col)


def _area(rect: tuple[int, int, int, int]) -> int:
    return (rect[2] - rect[0] + 1) * (rect[3] - rect[1] + 1)


def _iou(a: str, b: str) -> float:
    ra, rb = _rect(a), _rect(b)
    ir0 = max(ra[0], rb[0])
    ic0 = max(ra[1], rb[1])
    ir1 = min(ra[2], rb[2])
    ic1 = min(ra[3], rb[3])
    if ir1 < ir0 or ic1 < ic0:
        return 0.0
    inter = (ir1 - ir0 + 1) * (ic1 - ic0 + 1)
    union = _area(ra) + _area(rb) - inter
    return inter / union if union else 0.0


def _cells_in(a1: str) -> set[tuple[int, int]]:
    r = _rect(a1)
    return {(row, col) for row in range(r[0], r[2] + 1) for col in range(r[1], r[3] + 1)}


def evaluate_case(
    case: EvalCase,
    *,
    value_cells_by_sheet: dict[str, set[tuple[int, int]]],
    semantic_regions: list,
    iou_threshold: float = 0.5,
) -> dict:
    """Compute structural metrics for one case. ``semantic_regions`` are
    SemanticRegion objects (sheet, source_range, region_type, table)."""

    detected = [r for r in semantic_regions if r.source_range]
    expected = case.expected_regions

    # greedy best-IoU matching within the same sheet
    matches: list[tuple[ExpectedRegion, object, float]] = []
    used_detected: set[int] = set()
    for exp in expected:
        best_idx, best_iou = -1, 0.0
        for idx, det in enumerate(detected):
            if idx in used_detected or det.sheet != exp.sheet:
                continue
            score = _iou(exp.range, det.source_range)
            if score > best_iou:
                best_idx, best_iou = idx, score
        if best_idx >= 0 and best_iou >= iou_threshold:
            used_detected.add(best_idx)
            matches.append((exp, detected[best_idx], best_iou))

    matched_expected = len(matches)
    region_recall = matched_expected / len(expected) if expected else 1.0
    region_precision = matched_expected / len(detected) if detected else 1.0

    type_correct = sum(1 for exp, det, _ in matches if det.region_type.value == exp.type)
    type_accuracy = type_correct / matched_expected if matched_expected else 1.0

    # table-specific
    table_expected = [e for e in expected if e.type == "table"]
    table_detected = [d for d in detected if d.region_type.value == "table"]
    table_matches = [(e, d) for e, d, _ in matches if e.type == "table" and d.region_type.value == "table"]
    table_recall = len(table_matches) / len(table_expected) if table_expected else 1.0
    table_precision = len(table_matches) / len(table_detected) if table_detected else 1.0

    header_total = 0
    header_correct = 0
    row_total = 0
    row_correct = 0
    for exp, det in table_matches:
        if exp.header_rows is not None and det.table is not None:
            header_total += 1
            if det.table.header_rows == exp.header_rows:
                header_correct += 1
        if exp.row_count is not None and det.table is not None:
            row_total += 1
            if len(det.table.rows) == exp.row_count:
                row_correct += 1

    # content loss & duplicates over value cells
    covered: dict[tuple[str, tuple[int, int]], int] = {}
    for det in detected:
        ranges = [det.source_range]
        title_range = det.metadata.get("title_range") if hasattr(det, "metadata") else None
        if title_range:
            ranges.append(title_range)
        for a1 in ranges:
            for cell in _cells_in(a1):
                key = (det.sheet, cell)
                covered[key] = covered.get(key, 0) + 1
    total_value = 0
    lost = 0
    duplicated = 0
    for sheet, cells in value_cells_by_sheet.items():
        for cell in cells:
            total_value += 1
            count = covered.get((sheet, cell), 0)
            if count == 0:
                lost += 1
            elif count > 1:
                duplicated += 1

    return {
        "case_id": case.case_id,
        "expected_regions": len(expected),
        "detected_regions": len(detected),
        "matched_regions": matched_expected,
        "region_recall": round(region_recall, 4),
        "region_precision": round(region_precision, 4),
        "type_accuracy": round(type_accuracy, 4),
        "table_recall": round(table_recall, 4),
        "table_precision": round(table_precision, 4),
        "header_accuracy": round(header_correct / header_total, 4) if header_total else None,
        "row_count_accuracy": round(row_correct / row_total, 4) if row_total else None,
        "content_loss": lost,
        "duplicate_content": duplicated,
        "total_value_cells": total_value,
    }


__all__ = ["evaluate_case", "_iou"]
