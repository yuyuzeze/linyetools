"""Explainable feature extraction over sparse cells (no densification)."""

from __future__ import annotations

from typing import Any

from ..ingest.sparse_model import SparseCell, SparseSheet
from ..models.document_ir import StyleIR
from .models import CellBounds


def is_value_cell(cell: SparseCell) -> bool:
    """A cell that carries real content (not a typed-empty / style placeholder)."""

    return cell.raw_value is not None


def value_cells_in(sheet: SparseSheet, bounds: CellBounds) -> list[SparseCell]:
    return [
        cell
        for (row, col), cell in sheet.cells.items()
        if is_value_cell(cell) and bounds.contains(row, col)
    ]


def _style_of(cell: SparseCell, styles: dict[int, StyleIR]) -> StyleIR | None:
    if cell.style_id is None:
        return None
    return styles.get(cell.style_id)


def _is_bold(style: StyleIR | None) -> bool:
    return bool(style and style.font.get("bold"))


def _has_fill(style: StyleIR | None) -> bool:
    if not style or not style.fill:
        return False
    fill_type = style.fill.get("type")
    return bool(fill_type) and fill_type != "none"


def _has_border(style: StyleIR | None) -> bool:
    return bool(style and style.border)


def _classify_value(cell: SparseCell) -> str:
    if cell.formula is not None:
        return "formula"
    value = cell.raw_value
    if isinstance(value, bool):
        return "text"
    if isinstance(value, (int, float)):
        return "numeric"
    return "text"


def compute_features(
    sheet: SparseSheet,
    bounds: CellBounds,
    styles: dict[int, StyleIR],
    *,
    asset_anchor_rows: set[int] | None = None,
) -> dict[str, Any]:
    """Compute the explainable feature vector for a rectangular candidate."""

    asset_anchor_rows = asset_anchor_rows or set()
    cells = value_cells_in(sheet, bounds)
    nonempty = len(cells)
    area = bounds.area or 1

    numeric = sum(_classify_value(c) == "numeric" for c in cells)
    formula = sum(_classify_value(c) == "formula" for c in cells)
    text = sum(_classify_value(c) == "text" for c in cells)

    # per-row occupancy (which columns are populated on each row)
    rows_occupancy: dict[int, set[int]] = {}
    for cell in cells:
        rows_occupancy.setdefault(cell.row, set()).add(cell.column)

    # merges intersecting the bounds
    merge_count = 0
    for (mr, mc) in sheet.merge_spans:
        if bounds.contains(mr, mc):
            merge_count += 1

    # blank row / column gaps inside the bounds
    populated_rows = set(rows_occupancy)
    populated_cols = {c.column for c in cells}
    blank_row_gaps = sum(
        1
        for row in range(bounds.min_row, bounds.max_row + 1)
        if row not in populated_rows
    )
    blank_column_gaps = sum(
        1
        for col in range(bounds.min_col, bounds.max_col + 1)
        if col not in populated_cols
    )

    # border density over value cells
    bordered = sum(_has_border(_style_of(c, styles)) for c in cells)
    border_density = bordered / nonempty if nonempty else 0.0

    # header score: first populated row is stronger-styled than the rest
    header_score = _header_score(rows_occupancy, cells, styles)

    # column-header score: first column stronger-styled than the rest (the
    # signature of a horizontal label/value block, as opposed to a table whose
    # styling is row-oriented).
    col_header_score = _col_header_score(cells, styles, bounds)

    # repeated-row score: how consistent column occupancy is across rows
    repeated_row_score = _repeated_row_score(rows_occupancy)

    # key/value score: dominated by two adjacent columns (label + value)
    key_value_score = _key_value_score(rows_occupancy, bounds)

    # style transitions between vertically adjacent populated rows
    style_transitions = _style_transitions(sheet, bounds, styles)

    nearby_assets = sum(
        1
        for row in asset_anchor_rows
        if bounds.min_row - 1 <= row <= bounds.max_row + 1
    )

    density = nonempty / area
    merge_density = merge_count / area
    # a visual/layout block: sparse text, many merges and/or nearby drawings
    visual_score = round(
        min(
            1.0,
            (merge_density * 2.0)
            + (0.5 if nearby_assets else 0.0)
            + (0.3 if density < 0.15 and bounds.area >= 12 else 0.0),
        ),
        4,
    )

    return {
        "nonempty_cell_count": nonempty,
        "row_count": bounds.row_count,
        "column_count": bounds.col_count,
        "density": round(density, 4),
        "border_density": round(border_density, 4),
        "merge_count": merge_count,
        "merge_density": round(merge_density, 4),
        "numeric_ratio": round(numeric / nonempty, 4) if nonempty else 0.0,
        "text_ratio": round(text / nonempty, 4) if nonempty else 0.0,
        "formula_ratio": round(formula / nonempty, 4) if nonempty else 0.0,
        "repeated_row_score": round(repeated_row_score, 4),
        "header_score": round(header_score, 4),
        "col_header_score": round(col_header_score, 4),
        "key_value_score": round(key_value_score, 4),
        "visual_score": visual_score,
        "blank_row_gaps": blank_row_gaps,
        "blank_column_gaps": blank_column_gaps,
        "nearby_assets": nearby_assets,
        "style_transitions": style_transitions,
        "populated_row_count": len(populated_rows),
        "populated_col_count": len(populated_cols),
    }


def _header_score(
    rows_occupancy: dict[int, set[int]],
    cells: list[SparseCell],
    styles: dict[int, StyleIR],
) -> float:
    if not rows_occupancy:
        return 0.0
    first_row = min(rows_occupancy)
    first_cells = [c for c in cells if c.row == first_row]
    body_cells = [c for c in cells if c.row != first_row]
    if not first_cells:
        return 0.0

    def strength(group: list[SparseCell]) -> float:
        if not group:
            return 0.0
        marked = sum(
            _is_bold(_style_of(c, styles)) or _has_fill(_style_of(c, styles))
            for c in group
        )
        return marked / len(group)

    first_strength = strength(first_cells)
    body_strength = strength(body_cells)
    if not body_cells:
        return first_strength
    return max(0.0, first_strength - body_strength)


def _col_header_score(
    cells: list[SparseCell], styles: dict[int, StyleIR], bounds: CellBounds
) -> float:
    """First-column styling strength minus the rest (label-column signature)."""

    if not cells:
        return 0.0
    first_col = min(c.column for c in cells)
    first = [c for c in cells if c.column == first_col]
    rest = [c for c in cells if c.column != first_col]
    if not first:
        return 0.0

    def strength(group: list[SparseCell]) -> float:
        if not group:
            return 0.0
        marked = sum(
            _is_bold(_style_of(c, styles)) or _has_fill(_style_of(c, styles))
            for c in group
        )
        return marked / len(group)

    if not rest:
        return strength(first)
    return max(0.0, strength(first) - strength(rest))


def _repeated_row_score(rows_occupancy: dict[int, set[int]]) -> float:
    if len(rows_occupancy) < 2:
        return 0.0
    occ = list(rows_occupancy.values())
    body = occ[1:]  # exclude header row from the consistency measure
    if not body:
        return 0.0
    union = set().union(*body)
    if not union:
        return 0.0
    # average fraction of the union columns each body row fills
    return sum(len(row & union) / len(union) for row in body) / len(body)


def _key_value_score(rows_occupancy: dict[int, set[int]], bounds: CellBounds) -> float:
    if not rows_occupancy:
        return 0.0
    two_col_rows = sum(1 for cols in rows_occupancy.values() if len(cols) == 2)
    narrow = bounds.col_count <= 4
    base = two_col_rows / len(rows_occupancy)
    return base * (1.0 if narrow else 0.5)


def _style_transitions(
    sheet: SparseSheet, bounds: CellBounds, styles: dict[int, StyleIR]
) -> int:
    """Count vertical style changes in the first column of the bounds."""

    col = bounds.min_col
    previous: int | None = None
    transitions = 0
    seen = False
    for row in range(bounds.min_row, bounds.max_row + 1):
        cell = sheet.cells.get((row, col))
        style_id = cell.style_id if cell else None
        if seen and style_id != previous:
            transitions += 1
        previous = style_id
        seen = True
    return transitions


__all__ = ["compute_features", "is_value_cell", "value_cells_in"]
