"""Shared rendering helpers for DocumentIR exporters."""

from __future__ import annotations

import html
import json
import os
from pathlib import Path
from urllib.parse import urlparse

from ..models.document_ir import AssetIR, CellIR, DocumentIR, SheetIR, TableIR


def cell_text(cell: CellIR | None) -> str:
    if cell is None:
        return ""
    if cell.display_value is not None:
        return cell.display_value
    if cell.raw_value is None:
        return ""
    if isinstance(cell.raw_value, (dict, list)):
        return json.dumps(cell.raw_value, ensure_ascii=False, sort_keys=True)
    return str(cell.raw_value)


def table_bounds(table: TableIR) -> tuple[int, int, int, int] | None:
    if not table.cells:
        return None
    return (
        min(cell.row for cell in table.cells),
        max(cell.row + cell.row_span - 1 for cell in table.cells),
        min(cell.column for cell in table.cells),
        max(cell.column + cell.col_span - 1 for cell in table.cells),
    )


def table_columns(table: TableIR) -> list[int]:
    """Return present columns only so trimmed blank padding columns stay removed."""
    return sorted({cell.column for cell in table.cells})


def table_rows(table: TableIR) -> list[int]:
    return sorted({cell.row for cell in table.cells})


def table_cell_map(table: TableIR) -> dict[tuple[int, int], CellIR]:
    return {(cell.row, cell.column): cell for cell in table.cells}


def logical_cell_map(table: TableIR) -> dict[tuple[int, int], CellIR]:
    """Map every represented position to its logical merge master."""
    cells = table_cell_map(table)
    by_coordinate = {cell.coordinate: cell for cell in table.cells}
    result: dict[tuple[int, int], CellIR] = {}
    for position, cell in cells.items():
        if cell.merged_master:
            result[position] = by_coordinate.get(cell.merged_master, cell)
        else:
            result[position] = cell
            for row in range(cell.row, cell.row + cell.row_span):
                for column in range(cell.column, cell.column + cell.col_span):
                    result.setdefault((row, column), cell)
    return result


def has_complex_merges(table: TableIR) -> bool:
    return any(
        cell.merged_master is not None or cell.row_span > 1 or cell.col_span > 1
        for cell in table.cells
    )


def render_html_table(table: TableIR, *, css_class: str = "") -> str:
    bounds = table_bounds(table)
    class_attribute = f' class="{html.escape(css_class, quote=True)}"' if css_class else ""
    if bounds is None:
        return f"<table{class_attribute}></table>"

    min_row, max_row, _, _ = bounds
    columns = table_columns(table)
    cells = table_cell_map(table)
    covered = {
        (row, column)
        for cell in table.cells
        if not cell.merged_master
        for row in range(cell.row, cell.row + cell.row_span)
        for column in range(cell.column, cell.column + cell.col_span)
        if (row, column) != (cell.row, cell.column)
    }
    lines = [f"<table{class_attribute}>"]
    for row in range(min_row, max_row + 1):
        if not any((row, column) in cells or (row, column) in covered for column in columns):
            continue
        lines.append("  <tr>")
        for column in columns:
            cell = cells.get((row, column))
            if (row, column) in covered or (cell is not None and cell.merged_master):
                continue
            tag = "th" if row < min_row + table.header_rows else "td"
            attributes: list[str] = []
            if cell is not None and cell.row_span > 1:
                attributes.append(f'rowspan="{cell.row_span}"')
            if cell is not None and cell.col_span > 1:
                attributes.append(f'colspan="{cell.col_span}"')
            if cell is not None:
                attributes.append(f'data-source-cell="{html.escape(cell.coordinate)}"')
            attribute_text = f" {' '.join(attributes)}" if attributes else ""
            value = html.escape(cell_text(cell)).replace("\n", "<br>")
            lines.append(f"    <{tag}{attribute_text}>{value}</{tag}>")
        lines.append("  </tr>")
    lines.append("</table>")
    return "\n".join(lines)


def all_assets(document: DocumentIR, sheet: SheetIR | None = None) -> dict[str, AssetIR]:
    assets = {asset.asset_id: asset for asset in document.assets}
    if sheet is not None:
        assets.update({asset.asset_id: asset for asset in sheet.assets})
    return assets


def asset_uri(asset: AssetIR, destination: Path) -> str:
    if "://" in asset.uri and not asset.uri.startswith("file://"):
        return asset.uri
    parsed = urlparse(asset.uri)
    raw_path = Path(parsed.path if parsed.scheme == "file" else asset.uri)
    if not raw_path.is_absolute():
        return raw_path.as_posix()
    try:
        return Path(os.path.relpath(raw_path, destination.parent)).as_posix()
    except ValueError:
        return raw_path.as_uri()
