"""Semantic JSONL chunks for retrieval and AI knowledge bases."""

from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from ..models.document_ir import AssetIR, DocumentIR, RegionIR, SheetIR, TableIR
from ._shared import all_assets, cell_text, logical_cell_map, table_bounds, table_cell_map, table_columns


def _display(value: object) -> str:
    if isinstance(value, (dict, list)):
        return json.dumps(value, ensure_ascii=False, sort_keys=True)
    return str(value)


def _screen_id(document: DocumentIR) -> str | None:
    candidate_keys = {"screen_id", "画面id", "画面_id"}
    for mapping in (
        document.metadata,
        *(region.values for sheet in document.sheets for region in sheet.regions),
    ):
        for key, value in mapping.items():
            if key.lower() in candidate_keys and value not in (None, ""):
                return str(value)
    return None


def _asset_reference(asset: AssetIR) -> dict[str, Any]:
    result: dict[str, Any] = {
        "asset_id": asset.asset_id,
        "asset_type": asset.asset_type.value,
        "uri": asset.uri,
        "extraction_status": asset.extraction_status,
    }
    for key in ("ocr", "vlm"):
        if key in asset.metadata:
            result[key] = asset.metadata[key]
    if asset.description:
        result["description"] = asset.description
    return result


def _source(source: object) -> dict[str, Any] | None:
    return source.to_dict() if source is not None else None  # type: ignore[attr-defined]


def _split_text(text: str, limit: int) -> list[str]:
    if len(text) <= limit:
        return [text]
    parts: list[str] = []
    remaining = text
    while remaining:
        split_at = remaining.rfind("\n", 0, limit + 1)
        if split_at <= 0:
            split_at = remaining.rfind(" ", 0, limit + 1)
        if split_at <= 0:
            split_at = limit
        parts.append(remaining[:split_at].rstrip())
        remaining = remaining[split_at:].lstrip()
    return [part for part in parts if part]


@dataclass(slots=True)
class KnowledgeBaseJsonlExporter:
    max_chunk_chars: int = 4000

    def __post_init__(self) -> None:
        if self.max_chunk_chars < 1:
            raise ValueError("max_chunk_chars must be positive")

    def _base_metadata(self, document: DocumentIR) -> dict[str, Any]:
        result: dict[str, Any] = {
            "document_id": document.document_id,
            "document_title": document.title,
            "schema_version": document.schema_version,
            "template_id": document.template_id,
            "template_version": document.template_version,
            "screen_id": _screen_id(document),
        }
        return {key: value for key, value in result.items() if value is not None}

    def _append(
        self,
        chunks: list[dict[str, Any]],
        *,
        chunk_id: str,
        chunk_type: str,
        text: str,
        metadata: dict[str, Any],
    ) -> None:
        for index, part in enumerate(_split_text(text, self.max_chunk_chars), start=1):
            suffix = f":part-{index}" if len(text) > self.max_chunk_chars else ""
            chunks.append(
                {
                    "chunk_id": f"{chunk_id}{suffix}",
                    "chunk_type": chunk_type,
                    "text": part,
                    "metadata": metadata,
                }
            )

    def _table_rows(
        self,
        document: DocumentIR,
        sheet: SheetIR,
        region: RegionIR,
        table: TableIR,
        chunks: list[dict[str, Any]],
        assets: dict[str, AssetIR],
    ) -> None:
        bounds = table_bounds(table)
        if bounds is None:
            return
        min_row, max_row, _, _ = bounds
        columns = table_columns(table)
        cells = table_cell_map(table)
        logical = logical_cell_map(table)
        header_end = min(max_row, min_row + table.header_rows - 1)
        labels: list[str] = []
        for column in columns:
            header_values: list[str] = []
            if table.header_rows:
                for row in range(min_row, header_end + 1):
                    value = cell_text(logical.get((row, column)))
                    if value and value not in header_values:
                        header_values.append(value)
            sample = cells.get((min_row, column)) or logical.get((min_row, column))
            column_key = "".join(char for char in sample.coordinate if char.isalpha()) if sample else str(column)
            labels.append(
                table.column_semantics.get(column_key)
                or " / ".join(header_values)
                or column_key
            )

        first_data_row = min_row + table.header_rows
        for row in range(first_data_row, max_row + 1):
            values = [cell_text(logical.get((row, column))) for column in columns]
            row_cells = [
                cell
                for (cell_row, _), cell in cells.items()
                if cell_row == row
            ]
            if not any(values) and not row_cells:
                continue
            text = "\n".join(
                f"{label}: {value}" for label, value in zip(labels, values, strict=True)
            )
            metadata = {
                **self._base_metadata(document),
                "sheet_id": sheet.sheet_id,
                "sheet_name": sheet.name,
                "region_id": region.region_id,
                "region_type": region.region_type.value,
                "table_id": table.table_id,
                "row": row,
                "source": _source(table.source or region.source),
                "source_cells": [
                    cell.coordinate
                    for cell in sorted(row_cells, key=lambda item: item.column)
                ],
                "assets": [
                    _asset_reference(assets[asset_id])
                    for asset_id in region.asset_ids
                    if asset_id in assets
                ],
            }
            self._append(
                chunks,
                chunk_id=f"{document.document_id}:{sheet.sheet_id}:{region.region_id}:{table.table_id}:row-{row}",
                chunk_type="table_row",
                text=text,
                metadata={key: value for key, value in metadata.items() if value is not None},
            )

    def chunks(self, document: DocumentIR) -> list[dict[str, Any]]:
        chunks: list[dict[str, Any]] = []
        document_text = document.title
        if document.metadata:
            document_text += "\n" + "\n".join(
                f"{key}: {_display(value)}" for key, value in document.metadata.items()
            )
        self._append(
            chunks,
            chunk_id=f"{document.document_id}:document",
            chunk_type="document",
            text=document_text,
            metadata={
                **self._base_metadata(document),
                "source_path": document.source_path,
                "assets": [
                    _asset_reference(asset) for asset in document.assets
                ],
            },
        )

        for sheet in sorted(document.sheets, key=lambda item: item.index):
            assets = all_assets(document, sheet)
            for region in sheet.regions:
                region_text = region.title or region.region_id
                if region.values:
                    region_text += "\n" + "\n".join(
                        f"{key}: {_display(value)}"
                        for key, value in region.values.items()
                    )
                self._append(
                    chunks,
                    chunk_id=f"{document.document_id}:{sheet.sheet_id}:{region.region_id}",
                    chunk_type="section",
                    text=region_text,
                    metadata={
                        **self._base_metadata(document),
                        "sheet_id": sheet.sheet_id,
                        "sheet_name": sheet.name,
                        "region_id": region.region_id,
                        "region_type": region.region_type.value,
                        "source": _source(region.source),
                        "source_cells": [],
                        "assets": [
                            _asset_reference(assets[asset_id])
                            for asset_id in region.asset_ids
                            if asset_id in assets
                        ],
                    },
                )
                for table in region.tables:
                    self._table_rows(
                        document, sheet, region, table, chunks, assets
                    )
        return chunks

    def render(self, document: DocumentIR) -> str:
        return "".join(
            json.dumps(chunk, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
            + "\n"
            for chunk in self.chunks(document)
        )

    def export(self, document: DocumentIR, destination: Path) -> None:
        destination = Path(destination)
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_text(self.render(document), encoding="utf-8")


JsonlExporter = KnowledgeBaseJsonlExporter
JSONLExporter = KnowledgeBaseJsonlExporter
