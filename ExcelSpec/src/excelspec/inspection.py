"""Write structured workbook inspection artifacts for humans and agents."""

from __future__ import annotations

import html
import json
import re
from pathlib import Path
from typing import Any

from .ingest import ingest_xlsx
from .models.document_ir import CellIR, DocumentIR, SheetIR
from .serialization import to_json


def _safe_name(value: str) -> str:
    cleaned = re.sub(r'[\\/:*?"<>|]+', "_", value).strip().strip(".")
    return cleaned or "sheet"


def _display(cell: CellIR) -> str:
    if cell.display_value is not None:
        return str(cell.display_value)
    if cell.raw_value is None:
        return ""
    return str(cell.raw_value)


def _sheet_cells(sheet: SheetIR) -> list[CellIR]:
    cells: list[CellIR] = []
    seen: set[str] = set()
    for region in sheet.regions:
        for table in region.tables:
            for cell in table.cells:
                key = cell.coordinate.upper()
                if key in seen:
                    continue
                seen.add(key)
                cells.append(cell)
    return cells


def sheet_summary(sheet: SheetIR) -> dict[str, Any]:
    cells = _sheet_cells(sheet)
    nonblank = [cell for cell in cells if _display(cell).strip()]
    merges = [
        {
            "master": cell.merged_master or cell.coordinate,
            "coordinate": cell.coordinate,
            "row_span": cell.row_span,
            "col_span": cell.col_span,
        }
        for cell in cells
        if cell.merged_master or cell.row_span > 1 or cell.col_span > 1
    ]
    sample_values = [
        {
            "coordinate": cell.coordinate,
            "value": _display(cell),
            "row": cell.row,
            "column": cell.column,
        }
        for cell in sorted(nonblank, key=lambda item: (item.row, item.column))[:80]
    ]
    headers = [
        item["value"]
        for item in sample_values
        if item["row"] <= 20 and len(item["value"]) <= 40
    ]
    return {
        "sheet_id": sheet.sheet_id,
        "name": sheet.name,
        "index": sheet.index,
        "cell_count": len(cells),
        "nonblank_count": len(nonblank),
        "merge_hint_count": len(merges),
        "assets": [
            {
                "asset_id": asset.asset_id,
                "asset_type": asset.asset_type.value,
                "uri": asset.uri,
                "anchor": asset.anchor,
                "description": asset.description,
            }
            for asset in sheet.assets
        ],
        "sample_values": sample_values,
        "header_candidates": list(dict.fromkeys(headers))[:40],
        "merge_hints": merges[:200],
        "diagnostics": [item.to_dict() for item in sheet.diagnostics],
    }


def workbook_summary(document: DocumentIR) -> dict[str, Any]:
    return {
        "document_id": document.document_id,
        "title": document.title,
        "source_path": document.source_path,
        "schema_version": document.schema_version,
        "sheet_count": len(document.sheets),
        "sheets": [
            {
                "name": sheet.name,
                "index": sheet.index,
                "sheet_id": sheet.sheet_id,
                "nonblank_count": sheet_summary(sheet)["nonblank_count"],
                "asset_count": len(sheet.assets),
            }
            for sheet in document.sheets
        ],
        "metadata": document.metadata,
        "diagnostics": [item.to_dict() for item in document.diagnostics],
    }


def _preview_html(sheet: SheetIR) -> str:
    cells = _sheet_cells(sheet)
    if not cells:
        body = "<p>空シート</p>"
    else:
        min_row = min(cell.row for cell in cells)
        max_row = min(max(cell.row for cell in cells), min_row + 40)
        min_col = min(cell.column for cell in cells)
        max_col = min(max(cell.column for cell in cells), min_col + 20)
        lookup = {(cell.row, cell.column): cell for cell in cells}
        rows: list[str] = []
        for row in range(min_row, max_row + 1):
            cols: list[str] = []
            for column in range(min_col, max_col + 1):
                cell = lookup.get((row, column))
                text = html.escape(_display(cell)) if cell else ""
                attrs = ""
                if cell and (cell.row_span > 1 or cell.col_span > 1) and not cell.merged_master:
                    if cell.row_span > 1:
                        attrs += f' rowspan="{cell.row_span}"'
                    if cell.col_span > 1:
                        attrs += f' colspan="{cell.col_span}"'
                if cell and cell.merged_master:
                    continue
                cols.append(f"<td{attrs}>{text}</td>")
            rows.append("<tr>" + "".join(cols) + "</tr>")
        body = "<table>" + "".join(rows) + "</table>"
    title = html.escape(sheet.name)
    return (
        "<!DOCTYPE html><html><head><meta charset='utf-8'>"
        f"<title>{title}</title>"
        "<style>body{font:14px/1.4 sans-serif;padding:1rem}"
        "table{border-collapse:collapse}td{border:1px solid #ccc;padding:.25rem .4rem;"
        "min-width:2rem;vertical-align:top;white-space:pre-wrap}</style>"
        f"</head><body><h1>{title}</h1>{body}</body></html>\n"
    )


def write_inspection(
    source: str | Path,
    output_dir: str | Path,
    *,
    asset_dir: str | Path | None = None,
) -> Path:
    """Ingest an XLSX and write workbook/sheet/preview inspection files."""

    source_path = Path(source)
    root = Path(output_dir)
    sheets_dir = root / "sheets"
    preview_dir = root / "preview"
    assets = Path(asset_dir) if asset_dir is not None else root / "assets"
    sheets_dir.mkdir(parents=True, exist_ok=True)
    preview_dir.mkdir(parents=True, exist_ok=True)

    document = ingest_xlsx(source_path, asset_dir=assets)
    workbook = workbook_summary(document)
    (root / "workbook.json").write_text(
        to_json(workbook) + "\n", encoding="utf-8"
    )
    for sheet in document.sheets:
        name = _safe_name(sheet.name)
        summary = sheet_summary(sheet)
        (sheets_dir / f"{name}.json").write_text(
            to_json(summary) + "\n", encoding="utf-8"
        )
        (preview_dir / f"{name}.html").write_text(
            _preview_html(sheet), encoding="utf-8"
        )
    index = {
        "source": str(source_path.resolve()),
        "workbook": "workbook.json",
        "sheets": sorted(path.name for path in sheets_dir.glob("*.json")),
        "previews": sorted(path.name for path in preview_dir.glob("*.html")),
        "asset_dir": str(assets),
    }
    (root / "index.json").write_text(to_json(index) + "\n", encoding="utf-8")
    return root


__all__ = [
    "sheet_summary",
    "workbook_summary",
    "write_inspection",
]
