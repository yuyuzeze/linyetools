"""Self-contained, semantic static HTML export."""

from __future__ import annotations

import html
import json
import re
from pathlib import Path

from ..models.document_ir import AssetIR, DocumentIR
from ._shared import all_assets, asset_uri, render_html_table


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
                region_anchor = f"{sheet_anchor}-region-{index}-{_anchor(region.region_id, str(index))}"
                nav.append(
                    f'<li><a href="#{region_anchor}">{html.escape(region.title or region.region_id)}</a></li>'
                )
            nav.append("</ol></li>")
        nav.append("</ol></nav>")

        content = [f"<h1>{html.escape(document.title)}</h1>", '<dl class="metadata">']
        metadata: list[tuple[str, object]] = [
            ("文档 ID", document.document_id),
            ("Schema 版本", document.schema_version),
        ]
        if document.template_id:
            metadata.append(
                ("模板", f"{document.template_id} v{document.template_version}" if document.template_version else document.template_id)
            )
        metadata.extend(document.metadata.items())
        for key, value in metadata:
            content.append(f"<dt>{html.escape(key)}</dt><dd>{html.escape(_value(value))}</dd>")
        content.append("</dl>")
        rendered_assets: set[str] = set()

        for sheet, sheet_anchor in zip(ordered_sheets, sheet_anchors, strict=True):
            content.append(f'<section id="{sheet_anchor}"><h2>{html.escape(sheet.name)}</h2>')
            assets = all_assets(document, sheet)
            for index, region in enumerate(sheet.regions):
                region_anchor = f"{sheet_anchor}-region-{index}-{_anchor(region.region_id, str(index))}"
                content.append(
                    f'<section id="{region_anchor}" data-region-type="{html.escape(region.region_type.value)}">'
                    f"<h3>{html.escape(region.title or region.region_id)}</h3>"
                )
                if region.values:
                    content.append('<dl class="metadata">')
                    for key, value in region.values.items():
                        content.append(
                            f"<dt>{html.escape(key)}</dt><dd>{html.escape(_value(value))}</dd>"
                        )
                    content.append("</dl>")
                for table in region.tables:
                    content.append('<div class="table-wrap">')
                    content.append(render_html_table(table, css_class="excelspec-table"))
                    content.append("</div>")
                for asset_id in region.asset_ids:
                    asset = assets.get(asset_id)
                    if asset is not None and asset_id not in rendered_assets:
                        content.append(_asset_html(asset, destination))
                        rendered_assets.add(asset_id)
                content.append("</section>")
            content.append("</section>")

        remaining_assets = {
            asset.asset_id: asset
            for asset in [
                *document.assets,
                *(asset for sheet in document.sheets for asset in sheet.assets),
            ]
            if asset.asset_id not in rendered_assets
        }
        if remaining_assets:
            content.append('<section id="resources"><h2>资源</h2>')
            content.extend(
                _asset_html(asset, destination)
                for asset in remaining_assets.values()
            )
            content.append("</section>")

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
