"""Self-contained, semantic static HTML export."""

from __future__ import annotations

import html
import json
import re
from pathlib import Path

from ..models.document_ir import AssetIR, DocumentIR, TableIR
from ._shared import (
    all_assets,
    asset_uri,
    compact_list_rows,
    compact_rows,
    header_label_row,
    is_group_row,
    readable_document_metadata,
    readable_region_values,
    render_html_table,
    should_render_region,
)


_CSS = """
:root { color-scheme: light dark; --bg:#fff; --fg:#202124; --muted:#5f6368;
  --line:#dfe1e5; --panel:#f8f9fa; --accent:#1769aa; }
@media (prefers-color-scheme:dark) { :root { --bg:#171717; --fg:#eee;
  --muted:#aaa; --line:#444; --panel:#222; --accent:#7db7e8; } }
* { box-sizing:border-box; } html { scroll-behavior:smooth; }
body { margin:0; color:var(--fg); background:var(--bg);
  font:16px/1.6 system-ui,-apple-system,"Segoe UI",sans-serif; }
.layout { display:grid; grid-template-columns:minmax(14rem,20rem) minmax(0,1fr);
  gap:2rem; max-width:90rem; margin:auto; padding:2rem; }
nav { position:sticky; top:1rem; align-self:start; max-height:calc(100vh - 2rem);
  overflow:auto; padding:1rem; background:var(--panel); border-radius:.6rem; }
nav ol { padding-left:1.25rem; } nav a { color:var(--accent); }
main { min-width:0; } section { margin:0 0 2.5rem; scroll-margin-top:1rem; }
.metadata { display:grid; grid-template-columns:max-content minmax(0,1fr);
  gap:.35rem 1rem; } .metadata dt { font-weight:700; }
.table-wrap { width:100%; overflow:auto; margin:1rem 0; }
table { border-collapse:collapse; min-width:36rem; width:100%; }
th,td { border:1px solid var(--line); padding:.45rem .6rem; vertical-align:top; }
th { background:var(--panel); text-align:left; } img { max-width:100%; height:auto; }
figure { margin:1rem 0; } figcaption { color:var(--muted); }
@media (max-width:50rem) { .layout { display:block; padding:1rem; }
  nav { position:static; max-height:none; margin-bottom:2rem; } }
""".strip()


def _anchor(value: str, fallback: str) -> str:
    slug = re.sub(r"[^0-9A-Za-z_-]+", "-", value).strip("-").lower()
    return slug or fallback


def _value(value: object) -> str:
    if isinstance(value, (dict, list)):
        return json.dumps(value, ensure_ascii=False, sort_keys=True)
    return str(value)


def _asset_html(asset: AssetIR, destination: Path) -> str:
    uri = html.escape(asset_uri(asset, destination), quote=True)
    label = html.escape(asset.description or asset.asset_id)
    if asset.asset_type.value in {"image", "screenshot", "chart", "layout"}:
        return f'<figure><img src="{uri}" alt="{label}" loading="lazy"><figcaption>{label}</figcaption></figure>'
    return f'<p class="asset"><a href="{uri}">{label}</a></p>'


def _render_list_html(table: TableIR) -> str | None:
    """header_rows == 0 captures (e.g. a cover title block) read as a plain
    business list, not a table with a fabricated header row."""
    rows = compact_list_rows(table)
    if not rows:
        return None
    items = "".join(f"<li>{html.escape(' / '.join(row))}</li>" for row in rows)
    return f"<ul>{items}</ul>"


def _render_compact_table_html(table: TableIR) -> str | None:
    """Business-language table: only the columns the template mapped via
    column_semantics, using their real header text (merges resolved)."""
    rows = compact_rows(table)
    if not rows:
        return None
    headers = header_label_row(table)
    header = "".join(f"<th>{html.escape(value)}</th>" for value in headers)
    body_parts: list[str] = []
    for row in rows:
        if is_group_row(row):
            label = html.escape(next(value for value in row if value))
            body_parts.append(
                f'<tr><td colspan="{len(headers)}"><strong>{label}</strong></td></tr>'
            )
            continue
        body_parts.append(
            "<tr>" + "".join(f"<td>{html.escape(value)}</td>" for value in row) + "</tr>"
        )
    return f"<table><tr>{header}</tr>{''.join(body_parts)}</table>"


def _render_table_html(table: TableIR) -> str | None:
    """Readable HTML for a table, or None when it has no business content
    -- callers should then skip the section entirely."""
    if table.header_rows <= 0:
        return _render_list_html(table)
    if table.column_semantics:
        return _render_compact_table_html(table)
    if not compact_rows(table):
        return None
    return render_html_table(table, css_class="excelspec-table")


class HtmlExporter:
    def render(self, document: DocumentIR, destination: Path | None = None) -> str:
        destination = Path(destination or "document.html")
        sheet_anchors = [
            f"sheet-{index}-{_anchor(sheet.sheet_id, str(index))}"
            for index, sheet in enumerate(sorted(document.sheets, key=lambda item: item.index))
        ]
        ordered_sheets = sorted(document.sheets, key=lambda item: item.index)
        nav = ["<nav aria-label=\"目录\"><strong>目录</strong><ol>"]
        for sheet, sheet_anchor in zip(ordered_sheets, sheet_anchors, strict=True):
            nav.append(f'<li><a href="#{sheet_anchor}">{html.escape(sheet.name)}</a><ol>')
            for index, region in enumerate(sheet.regions):
                if not should_render_region(region):
                    continue
                region_anchor = f"{sheet_anchor}-region-{index}-{_anchor(region.region_id, str(index))}"
                nav.append(
                    f'<li><a href="#{region_anchor}">{html.escape(region.title or region.region_id)}</a></li>'
                )
            nav.append("</ol></li>")
        nav.append("</ol></nav>")

        content = [f"<h1>{html.escape(document.title)}</h1>"]
        document_metadata = readable_document_metadata(document)
        if document_metadata:
            content.append('<dl class="metadata">')
            for key, value in document_metadata:
                content.append(
                    f"<dt>{html.escape(key)}</dt><dd>{html.escape(_value(value))}</dd>"
                )
            content.append("</dl>")
        rendered_assets: set[str] = set()

        for sheet, sheet_anchor in zip(ordered_sheets, sheet_anchors, strict=True):
            content.append(f'<section id="{sheet_anchor}"><h2>{html.escape(sheet.name)}</h2>')
            assets = all_assets(document, sheet)
            for index, region in enumerate(sheet.regions):
                if not should_render_region(region):
                    continue
                region_anchor = f"{sheet_anchor}-region-{index}-{_anchor(region.region_id, str(index))}"
                content.append(
                    f'<section id="{region_anchor}" data-region-type="{html.escape(region.region_type.value)}">'
                    f"<h3>{html.escape(region.title or region.region_id)}</h3>"
                )
                region_values = readable_region_values(region)
                if region_values:
                    content.append('<dl class="metadata">')
                    for key, value in region_values:
                        content.append(
                            f"<dt>{html.escape(key)}</dt><dd>{html.escape(_value(value))}</dd>"
                        )
                    content.append("</dl>")
                for table in region.tables:
                    rendered_table = _render_table_html(table)
                    if rendered_table is None:
                        continue
                    content.append('<div class="table-wrap">')
                    content.append(rendered_table)
                    content.append("</div>")
                for asset_id in region.asset_ids:
                    asset = assets.get(asset_id)
                    if asset is not None and asset_id not in rendered_assets:
                        content.append(_asset_html(asset, destination))
                        rendered_assets.add(asset_id)
                content.append("</section>")
            content.append("</section>")

        # Unbound residual assets stay in canonical JSON only.
        return (
            "<!doctype html>\n<html lang=\"zh-CN\"><head><meta charset=\"utf-8\">"
            '<meta name="viewport" content="width=device-width,initial-scale=1">'
            f"<title>{html.escape(document.title)}</title><style>{_CSS}</style></head>"
            f'<body><div class="layout">{"".join(nav)}<main>{"".join(content)}</main></div></body></html>\n'
        )

    def export(self, document: DocumentIR, destination: Path) -> None:
        destination = Path(destination)
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_text(self.render(document, destination), encoding="utf-8")


HTMLExporter = HtmlExporter
