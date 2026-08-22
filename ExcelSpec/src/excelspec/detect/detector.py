"""Deterministic region detector operating on a SparseSheet (no densification)."""

from __future__ import annotations

import re
from dataclasses import dataclass

from ..ingest.sparse_model import SparseSheet, SparseWorkbookIR
from ..models.document_ir import (
    AssetIR,
    AssetType,
    DiagnosticIR,
    DiagnosticSeverity,
    SourceRef,
    StyleIR,
)
from .features import compute_features, is_value_cell
from .models import CandidateRegion, CandidateRegionType, CellBounds

_A1 = re.compile(r"^\$?([A-Z]{1,3})\$?(\d+)")


def _anchor_row(anchor: str | None) -> int | None:
    if not anchor:
        return None
    match = _A1.match(anchor.upper())
    return int(match.group(2)) if match else None


def _split_runs(values: list[int], gap_tolerance: int) -> list[list[int]]:
    """Split a sorted list where the blank gap between neighbours exceeds tol."""

    if not values:
        return []
    runs: list[list[int]] = []
    current = [values[0]]
    for value in values[1:]:
        if value - current[-1] - 1 > gap_tolerance:
            runs.append(current)
            current = [value]
        else:
            current.append(value)
    runs.append(current)
    return runs


@dataclass(slots=True)
class DetectionConfig:
    row_gap_tolerance: int = 1  # blank rows tolerated inside a block
    col_gap_tolerance: int = 1  # blank columns tolerated inside a block


def _value_positions(sheet: SparseSheet) -> list[tuple[int, int]]:
    return [
        (row, col)
        for (row, col), cell in sheet.cells.items()
        if is_value_cell(cell)
    ]


def _segment(
    sheet: SparseSheet,
    positions: list[tuple[int, int]],
    config: DetectionConfig,
) -> list[CellBounds]:
    """Segment value cells into blocks split by wide blank rows/columns."""

    if not positions:
        return []
    rows = sorted({row for row, _ in positions})
    position_set = set(positions)
    blocks: list[CellBounds] = []
    for band_rows in _split_runs(rows, config.row_gap_tolerance):
        band = set(band_rows)
        band_positions = [(r, c) for (r, c) in position_set if r in band]
        cols = sorted({c for _, c in band_positions})
        for col_group in _split_runs(cols, config.col_gap_tolerance):
            group = set(col_group)
            sub = [(r, c) for (r, c) in band_positions if c in group]
            if not sub:
                continue
            blocks.append(
                CellBounds(
                    min(r for r, _ in sub),
                    min(c for _, c in sub),
                    max(r for r, _ in sub),
                    max(c for _, c in sub),
                )
            )
    blocks.sort(key=lambda b: (b.min_row, b.min_col))
    return blocks


def _extract_title(
    sheet: SparseSheet, bounds: CellBounds, styles: dict[int, StyleIR]
) -> tuple[str | None, str | None, CellBounds]:
    """Peel a lone styled heading row off the top of a multi-row block.

    Returns ``(title_text, title_coordinate, content_bounds)``. The coordinate
    lets the caller keep the title cell counted as covered.
    """

    if bounds.row_count < 3:
        return None, None, bounds
    first_row_cells = [
        (col, cell)
        for (row, col), cell in sheet.cells.items()
        if row == bounds.min_row and bounds.min_col <= col <= bounds.max_col and is_value_cell(cell)
    ]
    if len(first_row_cells) != 1:
        return None, None, bounds
    col, cell = first_row_cells[0]
    style = styles.get(cell.style_id) if cell.style_id is not None else None
    styled = bool(style and (style.font.get("bold") or style.font.get("size")))
    # the next row must actually start a wider block for this to be a title
    second_row_cols = {
        c
        for (r, c), other in sheet.cells.items()
        if r == bounds.min_row + 1 and is_value_cell(other)
    }
    if styled and len(second_row_cols) >= 2:
        title = cell.display_value if cell.display_value is not None else str(cell.raw_value)
        return (
            title,
            cell.coordinate,
            CellBounds(bounds.min_row + 1, bounds.min_col, bounds.max_row, bounds.max_col),
        )
    return None, None, bounds


def _classify(
    features: dict, *, has_asset: bool
) -> tuple[CandidateRegionType, str, float]:
    """Deterministic, explainable classification from the feature vector."""

    nonempty = features["nonempty_cell_count"]
    rows = features["populated_row_count"]
    cols = features["populated_col_count"]

    if nonempty == 0:
        return CandidateRegionType.LAYOUT if has_asset else CandidateRegionType.FREEFORM, "empty", 0.4
    if nonempty == 1:
        return CandidateRegionType.TEXT, "single_cell", 0.5
    if rows == 1:
        return CandidateRegionType.TEXT, "single_row_text", 0.55

    # Visual / graph-paper layout: sparse text, dense merges or nearby drawings.
    if features["visual_score"] >= 0.6 or (
        features["merge_density"] >= 0.25 and features["density"] < 0.2
    ):
        return (
            CandidateRegionType.LAYOUT,
            "visual_density",
            round(min(0.95, 0.5 + features["visual_score"] / 2), 4),
        )

    # Label/value pairs: a narrow block that is column-oriented (labels down the
    # first column) rather than row-oriented (a styled header row over data).
    # Checked before the table rule because the same shape would otherwise
    # satisfy it. Either signal qualifies: (a) no styled header row, or (b) the
    # column-header signal is at least as strong as the row-header signal.
    col_oriented = features.get("col_header_score", 0.0) >= features["header_score"]
    if (
        cols <= 3
        and features["key_value_score"] >= 0.5
        and (features["header_score"] < 0.3 or col_oriented)
    ):
        return (
            CandidateRegionType.KEY_VALUE,
            "label_value_pairs",
            round(min(0.9, 0.5 + features["key_value_score"] / 2), 4),
        )
    # Multiple label/value pairs across a row (e.g. ID | val | 版数 | val):
    # column-oriented styling with an even, narrow-ish width.
    if (
        4 <= cols <= 6
        and cols % 2 == 0
        and features.get("col_header_score", 0.0) >= 0.3
        and features["repeated_row_score"] >= 0.5
        and features.get("col_header_score", 0.0) >= features["header_score"]
    ):
        return (
            CandidateRegionType.KEY_VALUE,
            "row_paired_key_value",
            round(min(0.85, 0.5 + features["col_header_score"] / 2), 4),
        )

    # Structured table: repeated body-row column occupancy.
    if rows >= 2 and cols >= 2 and features["repeated_row_score"] >= 0.5:
        return (
            CandidateRegionType.TABLE,
            "repeated_rows",
            round(min(0.98, 0.55 + 0.4 * features["repeated_row_score"]), 4),
        )

    # Fallback label/value for narrow blocks that still look like pairs.
    if cols <= 3 and features["key_value_score"] >= 0.5:
        return (
            CandidateRegionType.KEY_VALUE,
            "label_value_pairs",
            round(min(0.9, 0.5 + features["key_value_score"] / 2), 4),
        )

    # A wider grid without strong repetition is still a (lower-confidence) table.
    if rows >= 3 and cols >= 2:
        return CandidateRegionType.TABLE, "grid_block", 0.6

    return CandidateRegionType.TEXT, "text_block", 0.5


def _asset_candidates(
    sheet: SparseSheet, blocks: list[CandidateRegion]
) -> list[CandidateRegion]:
    """Create image/shape candidates, attaching to a covering layout if any."""

    candidates: list[CandidateRegion] = []
    for index, asset in enumerate(sheet.assets, start=1):
        anchor_row = _anchor_row(asset.anchor)
        covering = None
        if anchor_row is not None:
            for block in blocks:
                if block.bounds.min_row <= anchor_row <= block.bounds.max_row:
                    covering = block
                    break
        if covering is not None:
            covering.asset_refs.append(asset.asset_id)
            # A drawing over a content block marks it as a visual layout.
            if covering.region_type in (
                CandidateRegionType.TEXT,
                CandidateRegionType.FREEFORM,
            ):
                covering.region_type = CandidateRegionType.LAYOUT
                covering.detection_method = "asset_over_block"
            continue
        kind = {
            AssetType.IMAGE: CandidateRegionType.IMAGE,
            AssetType.SHAPE: CandidateRegionType.SHAPE,
            AssetType.CHART: CandidateRegionType.IMAGE,
            AssetType.LAYOUT: CandidateRegionType.LAYOUT,
            AssetType.SCREENSHOT: CandidateRegionType.LAYOUT,
        }.get(asset.asset_type, CandidateRegionType.IMAGE)
        row = anchor_row or (sheet.content_bounds or (1, 1, 1, 1))[0]
        bounds = CellBounds(row, 1, row, 1)
        candidates.append(
            CandidateRegion(
                region_id=f"{kind.value}-asset-{index}",
                sheet_name=sheet.name,
                bounds=bounds,
                region_type=kind,
                confidence=0.9,
                detection_method="drawing_anchor",
                features={"asset_type": asset.asset_type.value},
                title=asset.description,
                asset_refs=[asset.asset_id],
            )
        )
    return candidates


def detect_sheet(
    sheet: SparseSheet,
    styles: dict[int, StyleIR],
    *,
    config: DetectionConfig | None = None,
) -> list[CandidateRegion]:
    """Detect candidate regions for one sparse sheet, fully explainably."""

    config = config or DetectionConfig()
    asset_rows = {r for a in sheet.assets if (r := _anchor_row(a.anchor)) is not None}
    positions = _value_positions(sheet)
    blocks = _segment(sheet, positions, config)

    candidates: list[CandidateRegion] = []
    for index, bounds in enumerate(blocks, start=1):
        title, title_coord, content_bounds = _extract_title(sheet, bounds, styles)
        features = compute_features(
            sheet, content_bounds, styles, asset_anchor_rows=asset_rows
        )
        has_asset = features["nearby_assets"] > 0
        region_type, method, confidence = _classify(features, has_asset=has_asset)
        source_cells = sorted(
            cell.coordinate
            for (row, col), cell in sheet.cells.items()
            if is_value_cell(cell) and content_bounds.contains(row, col)
        )
        if title_coord is not None:
            # keep the peeled heading counted as covered by this region
            source_cells.append(title_coord)
        candidate = CandidateRegion(
            region_id=f"{region_type.value}-{index}",
            sheet_name=sheet.name,
            bounds=content_bounds,
            region_type=region_type,
            confidence=confidence,
            detection_method=method,
            features=features,
            title=title,
            title_cell=title_coord,
            source_cells=source_cells,
        )
        if confidence < 0.55:
            candidate.diagnostics.append(
                DiagnosticIR(
                    code="detect.low_confidence_region",
                    severity=DiagnosticSeverity.INFO,
                    message=f"低置信度区域 {candidate.region_id} ({method}, conf={confidence})",
                    source=SourceRef(sheet=sheet.name, range=content_bounds.a1()),
                    region_id=candidate.region_id,
                )
            )
        candidates.append(candidate)

    candidates.extend(_detect_border_boxes(sheet, styles, candidates))
    candidates.extend(_asset_candidates(sheet, candidates))
    _resolve_and_cover(sheet, candidates)
    return candidates


def _bordered_style_only(sheet: SparseSheet, styles: dict[int, StyleIR]) -> set[tuple[int, int]]:
    result: set[tuple[int, int]] = set()
    for (row, col), style_id in sheet.style_only.items():
        style = styles.get(style_id)
        if style and style.border:
            result.add((row, col))
    return result


def _detect_border_boxes(
    sheet: SparseSheet,
    styles: dict[int, StyleIR],
    existing: list[CandidateRegion],
) -> list[CandidateRegion]:
    """Detect border-only graph-paper layout boxes (no value cells).

    Conservative: a box must be a sizeable contiguous rectangle of *bordered
    style-only* cells (empty cells whose only content is a border), so ordinary
    bordered data tables — whose bordered cells carry values — are never picked
    up. Small value candidates inside the box are absorbed into the layout.
    """

    bordered = _bordered_style_only(sheet, styles)
    if len(bordered) < 12:
        return []
    rows = [r for r, _ in bordered]
    cols = [c for _, c in bordered]
    bounds = CellBounds(min(rows), min(cols), max(rows), max(cols))
    if bounds.row_count < 3 or bounds.col_count < 3 or bounds.area < 20:
        return []
    # coverage of the bbox by bordered cells must be high (a real grid box)
    if len(bordered) / bounds.area < 0.5:
        return []

    # absorb small text/freeform candidates fully inside the box
    absorbed: list[CandidateRegion] = []
    for candidate in list(existing):
        cb = candidate.bounds
        inside = (
            bounds.min_row <= cb.min_row and cb.max_row <= bounds.max_row
            and bounds.min_col <= cb.min_col and cb.max_col <= bounds.max_col
        )
        if inside and candidate.region_type in (
            CandidateRegionType.TEXT,
            CandidateRegionType.FREEFORM,
        ):
            absorbed.append(candidate)
    for candidate in absorbed:
        existing.remove(candidate)
    absorbed_cells = [c for cand in absorbed for c in cand.source_cells]

    title = None
    for candidate in existing:
        # a heading directly above the box becomes its title
        if (
            candidate.region_type == CandidateRegionType.TEXT
            and candidate.bounds.max_row == bounds.min_row - 1
            and candidate.title
        ):
            title = candidate.title
    return [
        CandidateRegion(
            region_id="layout-box-1",
            sheet_name=sheet.name,
            bounds=bounds,
            region_type=CandidateRegionType.LAYOUT,
            confidence=0.7,
            detection_method="border_box",
            features={
                "bordered_cell_count": len(bordered),
                "box_area": bounds.area,
            },
            title=title,
            source_cells=sorted(set(absorbed_cells)),
        )
    ]


def _resolve_and_cover(sheet: SparseSheet, candidates: list[CandidateRegion]) -> None:
    """Ensure every value cell is covered; emit overlap diagnostics."""

    owner: dict[str, str] = {}
    content = [c for c in candidates if c.region_type not in (
        CandidateRegionType.IMAGE,
        CandidateRegionType.SHAPE,
    )]
    for candidate in content:
        for coordinate in candidate.source_cells:
            existing = owner.get(coordinate)
            if existing is None:
                owner[coordinate] = candidate.region_id
            else:
                candidate.diagnostics.append(
                    DiagnosticIR(
                        code="detect.region_overlap",
                        severity=DiagnosticSeverity.WARNING,
                        message=f"单元格 {coordinate} 同时属于 {existing} 与 {candidate.region_id}",
                        source=SourceRef(sheet=sheet.name, cell=coordinate),
                        region_id=candidate.region_id,
                    )
                )
    covered = set(owner)
    all_value = {
        cell.coordinate
        for (row, col), cell in sheet.cells.items()
        if is_value_cell(cell)
    }
    uncovered = sorted(all_value - covered)
    if uncovered:
        candidates.append(
            CandidateRegion(
                region_id="freeform-residual",
                sheet_name=sheet.name,
                bounds=_bounds_of(sheet, uncovered),
                region_type=CandidateRegionType.FREEFORM,
                confidence=0.4,
                detection_method="coverage_residual",
                features={"nonempty_cell_count": len(uncovered)},
                source_cells=uncovered,
                diagnostics=[
                    DiagnosticIR(
                        code="detect.freeform_residual",
                        severity=DiagnosticSeverity.INFO,
                        message=f"{len(uncovered)} 个未归类单元格进入 freeform 兜底",
                        source=SourceRef(sheet=sheet.name),
                        region_id="freeform-residual",
                    )
                ],
            )
        )


def _bounds_of(sheet: SparseSheet, coordinates: list[str]) -> CellBounds:
    by_coord = {cell.coordinate: (cell.row, cell.column) for cell in sheet.cells.values()}
    rows = [by_coord[c][0] for c in coordinates if c in by_coord]
    cols = [by_coord[c][1] for c in coordinates if c in by_coord]
    if not rows:
        return CellBounds(1, 1, 1, 1)
    return CellBounds(min(rows), min(cols), max(rows), max(cols))


class RegionDetector:
    """Stateless detector over a whole sparse workbook."""

    def __init__(self, config: DetectionConfig | None = None) -> None:
        self.config = config or DetectionConfig()

    def detect_sheet_regions(
        self, sheet: SparseSheet, styles: dict[int, StyleIR]
    ) -> list[CandidateRegion]:
        return detect_sheet(sheet, styles, config=self.config)

    def detect_workbook(
        self, workbook: SparseWorkbookIR
    ) -> dict[str, list[CandidateRegion]]:
        return {
            sheet.name: detect_sheet(sheet, workbook.styles, config=self.config)
            for sheet in workbook.sheets
        }


__all__ = ["DetectionConfig", "RegionDetector", "detect_sheet"]
