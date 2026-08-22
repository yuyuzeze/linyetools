"""Sparse intermediate representation of an XLSX workbook.

Unlike the legacy path (which densifies a sheet's *effective rectangle* into one
``CellIR`` per grid position — inflating on distant styles, merges, and an
over-large ``<dimension>``), :class:`SparseWorkbookIR` stores only the cells that
actually carry meaning:

* value / formula cells (``cells``)
* style-only cells, tracked separately and **excluded from content bounds**
  (``style_only``) so a lone styled cell at ``XFD1048576`` cannot inflate the
  materialised area
* merge ranges (recorded, never expanded into member cells)
* a workbook-level style table referenced by ``style_id``

The number of stored cells is therefore proportional to the real XML content,
not to the bounding-box area. A :class:`SparseWorkbookIR` is turned into the
canonical :class:`~excelspec.models.document_ir.DocumentIR` by
``ingest.adapter.sparse_to_document`` which materialises **only** the content
bounds (values + merges) of each sheet.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any

from ..models.document_ir import AssetIR, DiagnosticIR, StyleIR


@dataclass(slots=True)
class SparseCell:
    """A single materially-present cell (has a value and/or a formula)."""

    row: int
    column: int
    coordinate: str
    raw_value: Any = None          # JSON-safe raw cell value (formula text for f-cells)
    display_value: str | None = None
    data_type: str | None = None   # openpyxl-compatible: 's' / 'n' / 'b' / 'e' / 'f' / 'd'
    formula: str | None = None
    cached_value: Any = None       # cached formula result (from the same <c> node)
    style_id: int | None = None


@dataclass(slots=True)
class SparseSheet:
    name: str
    sheet_id: str
    index: int
    state: str = "visible"
    cells: dict[tuple[int, int], SparseCell] = field(default_factory=dict)
    # (row, col) -> style_id for cells that carry only formatting, no value.
    style_only: dict[tuple[int, int], int] = field(default_factory=dict)
    merges: list[str] = field(default_factory=list)
    # master (row, col) -> (row_span, col_span)
    merge_spans: dict[tuple[int, int], tuple[int, int]] = field(default_factory=dict)
    # member (row, col) -> master (row, col)
    merge_members: dict[tuple[int, int], tuple[int, int]] = field(default_factory=dict)
    content_bounds: tuple[int, int, int, int] | None = None
    # Drawings/screenshots extracted for this sheet (populated by the sparse
    # ingestor's build_sparse_workbook so the detector can see image/shape
    # anchors without materialising the grid).
    assets: list[AssetIR] = field(default_factory=list)
    diagnostics: list[DiagnosticIR] = field(default_factory=list)
    metadata: dict[str, Any] = field(default_factory=dict)

    @property
    def xml_cell_count(self) -> int:
        return len(self.cells) + len(self.style_only)

    @property
    def value_cell_count(self) -> int:
        return len(self.cells)

    @property
    def style_only_count(self) -> int:
        return len(self.style_only)


@dataclass(slots=True)
class SparseWorkbookIR:
    path: str
    sheets: list[SparseSheet] = field(default_factory=list)
    styles: dict[int, StyleIR] = field(default_factory=dict)
    properties: dict[str, Any] = field(default_factory=dict)
    document_diagnostics: list[DiagnosticIR] = field(default_factory=list)
    metadata: dict[str, Any] = field(default_factory=dict)

    def sheet(self, name: str) -> SparseSheet | None:
        return next((sheet for sheet in self.sheets if sheet.name == name), None)


__all__ = [
    "SparseCell",
    "SparseSheet",
    "SparseWorkbookIR",
]
