"""Readable Markdown export with safe HTML fallback for merged tables."""

from __future__ import annotations

import json
from pathlib import Path

from ..models.document_ir import AssetIR, DocumentIR, TableIR
from ._shared import (
    all_assets,
    asset_uri,
    cell_text,
    compact_list_rows,
    compact_rows,
    has_complex_merges,
    header_label_row,
    interleaved_region_blocks,
    is_group_row,
    readable_document_metadata,
    readable_region_values,
    render_html_table,
    should_interleave_region,
    should_render_region,
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


def _render_list(table: TableIR) -> list[str]:
    """header_rows == 0 captures (e.g. a cover title block) read as plain
    business text, not a table with a fabricated header row."""
    rows = compact_list_rows(table)
    return [f"- {_escape(' / '.join(row))}" for row in rows]


def _render_compact_table(table: TableIR) -> list[str]:
    """Business-language table: only the columns the template mapped via
    column_semantics, using their real header text (merges resolved)."""
    rows = compact_rows(table)
    if not rows:
        return []
    header = [_escape(value) for value in header_label_row(table)]
    body_lines: list[str] = []
    for row in rows:
        if is_group_row(row):
            label = _escape(next(value for value in row if value))
            padded = [f"**{label}**"] + [""] * (len(header) - 1)
            body_lines.append(f"| {' | '.join(padded)} |")
            continue
        body_lines.append(f"| {' | '.join(_escape(value) for value in row)} |")
    return [
        f"| {' | '.join(header)} |",
        f"| {' | '.join('---' for _ in header)} |",
        *body_lines,
    ]


def _render_grid_table(table: TableIR) -> list[str]:
    """Legacy full physical-grid rendering, kept for tables the template
    left unmapped (no column_semantics) and with a real header row."""
    bounds = table_bounds(table)
    if bounds is None:
        return []
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
        return []
    header = rows[0]
    body = rows[1:]
    return [
        f"| {' | '.join(header)} |",
        f"| {' | '.join('---' for _ in header)} |",
        *(f"| {' | '.join(row)} |" for row in body),
    ]


def _render_table(table: TableIR) -> list[str]:
    """Readable Markdown lines for a table, or [] when it has no business
    content -- callers should then skip the section entirely rather than
    print an empty placeholder."""
    if table.header_rows <= 0:
        return _render_list(table)
    if table.column_semantics:
        return _render_compact_table(table)
    if has_complex_merges(table):
        if not compact_rows(table):
            return []
        return [render_html_table(table, css_class="excelspec-table")]
    return _render_grid_table(table)


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
        metadata = readable_document_metadata(document)
        if metadata:
            lines.extend(f"- **{key}**: {_metadata_value(value)}" for key, value in metadata)
            lines.append("")
        rendered_assets: set[str] = set()

        for sheet in sorted(document.sheets, key=lambda item: item.index):
            lines.extend([f"## {sheet.name}", ""])
            assets = all_assets(document, sheet)
            for region in sheet.regions:
                if not should_render_region(region):
                    continue
                lines.extend([f"### {region.title or region.region_id}", ""])
                region_values = readable_region_values(region)
                if region_values:
                    lines.extend(
                        f"- **{key}**: {_metadata_value(value)}"
                        for key, value in region_values
                    )
                    lines.append("")

                if region.metadata.get("readable_mode") == "screenshot":
                    for asset_id in region.asset_ids:
                        asset = assets.get(asset_id)
                        if asset is None or asset_id in rendered_assets:
                            continue
                        if asset.metadata.get("source_kind") != "region_screenshot":
                            continue
                        lines.extend([_render_asset(asset, destination), ""])
                        rendered_assets.add(asset_id)
                    continue

                if should_interleave_region(region):
                    for block in interleaved_region_blocks(region, assets):
                        if block.kind == "text" and block.text:
                            lines.extend([f"- {_escape(block.text)}", ""])
                        elif block.kind == "asset" and block.asset_id:
                            asset = assets.get(block.asset_id)
                            if asset is not None and block.asset_id not in rendered_assets:
                                lines.extend([_render_asset(asset, destination), ""])
                                rendered_assets.add(block.asset_id)
                    continue

                for table in region.tables:
                    body = _render_table(table)
                    if not body:
                        continue
                    if len(region.tables) > 1:
                        lines.extend([f"#### {table.table_id}", ""])
                    lines.extend([*body, ""])
                for asset_id in region.asset_ids:
                    asset = assets.get(asset_id)
                    if asset is not None and asset_id not in rendered_assets:
                        lines.extend([_render_asset(asset, destination), ""])
                        rendered_assets.add(asset_id)

        # Unbound residual assets stay in canonical JSON only; readable MD
        # should not dump a trailing 「资源」 bucket.
        return "\n".join(lines).rstrip() + "\n"

    def export(self, document: DocumentIR, destination: Path) -> None:
        destination = Path(destination)
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_text(
            self.render(document, destination), encoding="utf-8"
        )
