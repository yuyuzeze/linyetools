"""Human-reviewable HTML audit report (evaluation only; not a shipped exporter).

For each sheet it lays out the detected regions with their range, type, title,
confidence, detection method, features, header decision, semantic field
mappings, assets, diagnostics, plus the raw sparse cell content and the semantic
/ chunk output — so a human can eyeball whether detection is correct.
"""

from __future__ import annotations

import html
import json
from pathlib import Path

from ..chunking import chunk_document
from ..detect.detector import RegionDetector
from ..ingest import ingest_sparse_workbook
from ..profile import load_profile
from ..semantic import assemble_semantic


def _esc(value: object) -> str:
    return html.escape("" if value is None else str(value))


def _cell_grid(sheet, bounds_a1: str | None) -> str:
    if not bounds_a1:
        return ""
    from openpyxl.utils import range_boundaries

    min_col, min_row, max_col, max_row = range_boundaries(bounds_a1)
    rows = []
    for row in range(min_row, min(max_row, min_row + 30) + 1):
        cells = []
        for col in range(min_col, min(max_col, min_col + 15) + 1):
            cell = sheet.cells.get((row, col))
            text = "" if cell is None or cell.raw_value is None else str(cell.raw_value)
            cells.append(f"<td>{_esc(text)}</td>")
        rows.append("<tr>" + "".join(cells) + "</tr>")
    return f"<table class=grid>{''.join(rows)}</table>"


def build_audit_html(
    workbook: str | Path,
    output: str | Path,
    *,
    mode: str = "fast",
    profile: str | Path | None = None,
) -> Path:
    workbook_path = Path(workbook)
    asset_dir = Path(output).parent / f"{workbook_path.stem}_audit_assets"
    sparse = ingest_sparse_workbook(workbook_path, asset_dir=asset_dir)
    profile_obj = load_profile(profile) if profile else None

    from ..detect.assemble import assemble_document

    document, _ = assemble_document(sparse, mode=mode, profile=profile_obj)
    semantic = assemble_semantic(document)
    chunks = chunk_document(semantic)
    detector = RegionDetector()

    chunks_by_region: dict[str, list] = {}
    for chunk in chunks:
        chunks_by_region.setdefault(chunk.region_id, []).append(chunk)
    sem_by_id = {r.region_id: r for r in semantic.regions}

    parts: list[str] = [
        "<!doctype html><meta charset=utf-8>",
        "<style>",
        "body{font-family:sans-serif;margin:1rem;font-size:13px}",
        "h2{border-bottom:2px solid #333;margin-top:2rem}",
        "details{border:1px solid #ccc;margin:.4rem 0;padding:.4rem;border-radius:4px}",
        "summary{cursor:pointer;font-weight:bold}",
        ".grid td{border:1px solid #ddd;padding:2px 6px;font-size:12px}",
        ".meta{color:#555;font-size:12px}.diag{color:#b00}.kv{color:#060}",
        "code{background:#f4f4f4;padding:1px 3px}",
        "</style>",
        f"<h1>Audit: {_esc(workbook_path.name)}</h1>",
        f"<p class=meta>mode={_esc(mode)} profile={_esc(semantic.profile_id)} "
        f"sheets={len(document.sheets)} regions={len(semantic.regions)} chunks={len(chunks)}</p>",
    ]

    for sheet, sparse_sheet in zip(document.sheets, sparse.sheets):
        parts.append(f"<h2>{_esc(sheet.name)} <span class=meta>({_esc(sheet.metadata.get('sheet_role'))})</span></h2>")
        for region in sheet.regions:
            sem = sem_by_id.get(f"{sheet.sheet_id}:{region.region_id}")
            rtype = region.metadata.get("candidate_type", region.region_type.value)
            header = ""
            if region.tables:
                table = region.tables[0]
                decision = region.metadata.get("header_decision", {})
                semantics = table.column_semantics
                header = (
                    f"<div class=meta>header_rows={table.header_rows} "
                    f"evidence={_esc(decision.get('evidence'))} conf={_esc(decision.get('confidence'))}</div>"
                    f"<div class=kv>semantics={_esc(semantics)}</div>"
                )
            diagnostics = region.metadata.get("diagnostics", [])
            diag_html = "".join(
                f"<div class=diag>[{_esc(d.get('severity'))}] {_esc(d.get('code'))}: {_esc(d.get('message'))}</div>"
                for d in diagnostics
            )
            region_chunks = chunks_by_region.get(f"{sheet.sheet_id}:{region.region_id}", [])
            chunk_html = "".join(
                f"<details><summary>chunk {c.chunk_index} ({_esc(c.chunk_type)}, conf={c.confidence})</summary>"
                f"<pre>{_esc(c.text)}</pre>"
                f"<code>{_esc(json.dumps(c.structured_data, ensure_ascii=False)[:600])}</code></details>"
                for c in region_chunks
            )
            parts.append(
                "<details><summary>"
                f"{_esc(region.region_id)} — {_esc(rtype)} @ {_esc(region.source.range if region.source else '')} "
                f"conf={_esc(region.confidence)} via={_esc(region.metadata.get('detection_method'))}"
                "</summary>"
                f"<div class=meta>title={_esc(region.title)} assets={_esc(region.asset_ids)} "
                f"formula_refs={_esc(sem.formula_refs if sem else [])}</div>"
                f"{header}{diag_html}"
                f"<div class=meta>features={_esc(json.dumps(region.metadata.get('features', {}), ensure_ascii=False))}</div>"
                "<b>raw cells:</b>"
                f"{_cell_grid(sparse_sheet, region.source.range if region.source else None)}"
                f"<b>chunks ({len(region_chunks)}):</b>{chunk_html}"
                "</details>"
            )

    if semantic.references:
        parts.append("<h2>Formula references</h2>")
        for ref in semantic.references:
            parts.append(
                f"<div class=meta><code>{_esc(ref.source_sheet)}!{_esc(ref.source_cell)}</code> "
                f"{_esc(ref.formula)} → {_esc(ref.reference_type.value)} "
                f"targets={_esc([(t.sheet, t.range, t.name) for t in ref.targets])} resolved={ref.resolved}</div>"
            )

    output_path = Path(output)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text("\n".join(parts), encoding="utf-8")
    return output_path


__all__ = ["build_audit_html"]
