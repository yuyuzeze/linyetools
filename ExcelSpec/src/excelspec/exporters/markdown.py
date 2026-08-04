"""Readable Markdown export with safe HTML fallback for merged tables."""

from __future__ import annotations

import json
from pathlib import Path

from ..models.document_ir import AssetIR, DocumentIR, TableIR
from ._shared import (
    all_assets,
    asset_uri,
    cell_text,
    has_complex_merges,
    render_html_table,
    table_bounds,
    table_cell_map,
    table_columns,
)


def _escape(value: str) -> str:
    return value.replace("\\", "\\\\").replace("|", "\\|").replace("\r\n", "<br>").replace("\n", "<br>")


def _metadata_value(value: object) -> str:
    if isinstance(value, (dict, list)):
        return json.dumps(value, ensure_ascii=False, sort_keys=True)
    return str(value)


def _render_gfm_table(table: TableIR) -> list[str]:
    bounds = table_bounds(table)
    if bounds is None:
        return ["_空表_"]
    min_row, max_row, _, _ = bounds
    columns = table_columns(table)
    cells = table_cell_map(table)
    rows = []
    for row in range(min_row, max_row + 1):
        values = [_escape(cell_text(cells.get((row, column)))) for column in columns]
        if row > min_row and not any(values):
            continue
        rows.append(values)
    if not rows:
        return ["_空表_"]
    header = rows[0]
    body = rows[1:]
    return [
        f"| {' | '.join(header)} |",
        f"| {' | '.join('---' for _ in header)} |",
        *(f"| {' | '.join(row)} |" for row in body),
    ]


def _render_asset(asset: AssetIR, destination: Path) -> str:
    uri = asset_uri(asset, destination)
    label = asset.description or asset.asset_id
    if asset.asset_type.value in {"image", "screenshot", "chart", "layout"}:
        return f"![{_escape(label)}]({uri})"
    return f"[{_escape(label)}]({uri})"


class MarkdownExporter:
    def render(self, document: DocumentIR, destination: Path | None = None) -> str:
        destination = Path(destination or "document.md")
        lines = [f"# {document.title}", ""]
        metadata: list[tuple[str, object]] = [
            ("文档 ID", document.document_id),
            ("Schema 版本", document.schema_version),
        ]
        if document.template_id:
            template = document.template_id
            if document.template_version:
                template += f" v{document.template_version}"
            metadata.append(("模板", template))
        if document.source_path:
            metadata.append(("来源", document.source_path))
        metadata.extend(document.metadata.items())
        lines.extend(f"- **{key}**: {_metadata_value(value)}" for key, value in metadata)
        lines.append("")
        rendered_assets: set[str] = set()

        for sheet in sorted(document.sheets, key=lambda item: item.index):
            lines.extend([f"## {sheet.name}", ""])
            assets = all_assets(document, sheet)
            for region in sheet.regions:
                lines.extend([f"### {region.title or region.region_id}", ""])
                if region.values:
                    lines.extend(
                        f"- **{key}**: {_metadata_value(value)}"
                        for key, value in region.values.items()
                    )
                    lines.append("")
                for table in region.tables:
                    if len(region.tables) > 1:
                        lines.extend([f"#### {table.table_id}", ""])
                    if has_complex_merges(table):
                        lines.extend([render_html_table(table, css_class="excelspec-table"), ""])
                    else:
                        lines.extend([*_render_gfm_table(table), ""])
                for asset_id in region.asset_ids:
                    asset = assets.get(asset_id)
                    if asset is not None and asset_id not in rendered_assets:
                        lines.extend([_render_asset(asset, destination), ""])
                        rendered_assets.add(asset_id)

        remaining_assets = {
            asset.asset_id: asset
            for asset in [
                *document.assets,
                *(asset for sheet in document.sheets for asset in sheet.assets),
            ]
            if asset.asset_id not in rendered_assets
        }
        if remaining_assets:
            lines.extend(["## 资源", ""])
            lines.extend(
                _render_asset(asset, destination)
                for asset in remaining_assets.values()
            )
            lines.append("")
        return "\n".join(lines).rstrip() + "\n"

    def export(self, document: DocumentIR, destination: Path) -> None:
        destination = Path(destination)
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_text(
            self.render(document, destination), encoding="utf-8"
        )
