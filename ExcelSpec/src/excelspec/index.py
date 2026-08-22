"""One-shot cell indexes reused by locating, region detection, and matching.

Historically every template locator, region check, and field match re-walked a
sheet's cells (and often re-``sorted`` them). :class:`SheetIndex` builds all the
lookup tables a single time so downstream passes become dictionary hits instead
of full-sheet scans.
"""

from __future__ import annotations

import json
from dataclasses import dataclass, field
from typing import Iterable, Iterator

from .models.document_ir import CellIR, SheetIR, StyleIR


def normalize_text(value: object) -> str:
    """Normalise cell text for tolerant lookups (trim + collapse whitespace)."""

    if value is None:
        return ""
    text = str(value)
    return " ".join(text.split()).casefold()


def _style_id(style: StyleIR | None) -> str:
    """Stable identifier for a style so equal styles share one id.

    Cells reference styles by this id rather than repeating a full
    :class:`StyleIR`, mirroring OOXML's ``s=`` style-index model.
    """

    if style is None:
        return "s0"
    payload = json.dumps(style.to_dict(), sort_keys=True, ensure_ascii=False)
    return f"s{hash(payload) & 0xFFFFFFFF:08x}"


def iter_sheet_cells(sheet: SheetIR) -> Iterator[CellIR]:
    """Yield every :class:`CellIR` under a sheet (region → table → cell)."""

    for region in sheet.regions:
        for table in region.tables:
            yield from table.cells


@dataclass(slots=True)
class SheetIndex:
    """Precomputed lookup tables for one sheet.

    Build once with :meth:`from_sheet` (or :meth:`from_cells`) and share the
    instance across locating / detection / matching passes.
    """

    sheet_name: str
    by_coordinate: dict[str, CellIR] = field(default_factory=dict)
    by_rowcol: dict[tuple[int, int], CellIR] = field(default_factory=dict)
    by_normalized_text: dict[str, list[CellIR]] = field(default_factory=dict)
    by_row: dict[int, list[CellIR]] = field(default_factory=dict)
    by_column: dict[int, list[CellIR]] = field(default_factory=dict)
    by_style_id: dict[str, list[CellIR]] = field(default_factory=dict)
    style_ids: dict[str, str] = field(default_factory=dict)

    @classmethod
    def from_sheet(cls, sheet: SheetIR) -> "SheetIndex":
        return cls.from_cells(sheet.name, iter_sheet_cells(sheet))

    @classmethod
    def from_cells(cls, sheet_name: str, cells: Iterable[CellIR]) -> "SheetIndex":
        index = cls(sheet_name=sheet_name)
        for cell in cells:
            index.by_coordinate[cell.coordinate] = cell
            index.by_rowcol[(cell.row, cell.column)] = cell
            index.by_row.setdefault(cell.row, []).append(cell)
            index.by_column.setdefault(cell.column, []).append(cell)
            text = normalize_text(
                cell.display_value if cell.display_value is not None else cell.raw_value
            )
            if text:
                index.by_normalized_text.setdefault(text, []).append(cell)
            style_id = _style_id(cell.style)
            index.style_ids[cell.coordinate] = style_id
            index.by_style_id.setdefault(style_id, []).append(cell)
        return index

    # -- convenience accessors -------------------------------------------------

    @property
    def cell_count(self) -> int:
        return len(self.by_coordinate)

    def cell(self, coordinate: str) -> CellIR | None:
        return self.by_coordinate.get(coordinate)

    def at(self, row: int, column: int) -> CellIR | None:
        return self.by_rowcol.get((row, column))

    def find_text(self, text: str) -> list[CellIR]:
        """Cells whose normalized text equals ``text`` (also normalized)."""

        return self.by_normalized_text.get(normalize_text(text), [])

    def bounds(self) -> tuple[int, int, int, int] | None:
        """(min_row, min_col, max_row, max_col) of indexed cells, or ``None``."""

        if not self.by_rowcol:
            return None
        rows = [row for row, _ in self.by_rowcol]
        cols = [col for _, col in self.by_rowcol]
        return min(rows), min(cols), max(rows), max(cols)


@dataclass(slots=True)
class WorkbookIndex:
    """Lazily-built, cached per-sheet indexes for a whole document."""

    _sheets: dict[str, SheetIndex] = field(default_factory=dict)

    @classmethod
    def from_document(cls, document) -> "WorkbookIndex":  # DocumentIR
        index = cls()
        for sheet in document.sheets:
            index._sheets[sheet.name] = SheetIndex.from_sheet(sheet)
        return index

    def sheet(self, name: str) -> SheetIndex | None:
        return self._sheets.get(name)

    def __iter__(self) -> Iterator[SheetIndex]:
        return iter(self._sheets.values())


__all__ = [
    "SheetIndex",
    "WorkbookIndex",
    "iter_sheet_cells",
    "normalize_text",
]
