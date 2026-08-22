"""Deterministic, region-aware chunker.

Chunks per semantic region (never a fixed character slice over the whole
document): text by paragraph, key/value by group, table by whole rows (never
splitting a row, repeating columns/header + sheet/title context into each
chunk), and one visual chunk per image/shape/layout. ``chunk_id`` and order are
deterministic — identical input yields identical ``chunk_id``s and sequence.
"""

from __future__ import annotations

import hashlib
import json
from dataclasses import dataclass

from ..models.chunk import ChunkSource, KnowledgeChunkIR
from ..models.semantic import (
    SemanticDocumentIR,
    SemanticRegion,
    SemanticRegionType,
    SemanticTable,
)


@dataclass(slots=True)
class ChunkingOptions:
    max_rows: int = 40
    max_chars: int = 4000


def _content_hash(payload: object) -> str:
    text = json.dumps(payload, ensure_ascii=False, sort_keys=True, default=str)
    return hashlib.sha256(text.encode("utf-8")).hexdigest()[:16]


class KnowledgeChunker:
    def __init__(self, options: ChunkingOptions | None = None) -> None:
        self.options = options or ChunkingOptions()

    def chunk(self, document: SemanticDocumentIR) -> list[KnowledgeChunkIR]:
        chunks: list[KnowledgeChunkIR] = []
        for region in document.regions:
            parts = self._chunk_region(document, region)
            chunks.extend(parts)
        # assign a stable global index in deterministic region order
        for index, chunk in enumerate(chunks):
            chunk.chunk_index = index
        return chunks

    # -- per-region -----------------------------------------------------------

    def _base(self, document, region, seq, chunk_type, text, structured):
        source = ChunkSource(
            workbook=document.source_path,
            sheet=region.sheet,
            range=region.source_range,
        )
        content_hash = _content_hash(
            {"text": text, "structured": structured, "range": region.source_range, "seq": seq}
        )
        return KnowledgeChunkIR(
            chunk_id=f"{document.document_id}:{region.region_id}:{seq}",
            document_id=document.document_id,
            chunk_index=0,
            chunk_type=chunk_type,
            text=text,
            source=source,
            content_hash=content_hash,
            document_type=document.document_type,
            sheet=region.sheet,
            sheet_role=region.sheet_role,
            section_path=list(region.section_path),
            region_id=region.region_id,
            title=region.title,
            structured_data=structured,
            asset_refs=list(region.asset_refs),
            formula_refs=list(region.formula_refs),
            confidence=round(region.confidence, 4),
        )

    def _chunk_region(
        self, document: SemanticDocumentIR, region: SemanticRegion
    ) -> list[KnowledgeChunkIR]:
        if region.region_type == SemanticRegionType.TABLE and region.table:
            return self._chunk_table(document, region, region.table)
        if region.region_type == SemanticRegionType.KEY_VALUE:
            return self._chunk_key_value(document, region)
        if region.region_type in (
            SemanticRegionType.IMAGE,
            SemanticRegionType.SHAPE,
            SemanticRegionType.LAYOUT,
        ):
            return self._chunk_visual(document, region)
        return self._chunk_text(document, region)

    def _context_prefix(self, region: SemanticRegion) -> str:
        bits = [region.sheet]
        if region.title and region.title != region.sheet:
            bits.append(region.title)
        return " / ".join(b for b in bits if b)

    def _chunk_table(self, document, region, table: SemanticTable):
        header = [
            (col.display_name or col.semantic_name or col.column_id) for col in table.columns
        ]
        columns_payload = [c.to_dict() for c in table.columns]
        prefix = self._context_prefix(region)
        chunks: list[KnowledgeChunkIR] = []
        rows = table.rows
        if not rows:
            # header-only / empty table still traceable
            structured = {"columns": columns_payload, "rows": []}
            text = f"{prefix}\n" + " | ".join(header)
            return [self._base(document, region, "t0", "table", text, structured)]

        # group whole rows by max_rows and max_chars
        groups: list[list] = []
        current: list = []
        current_chars = len(prefix) + len(" | ".join(header))
        for row in rows:
            row_text = _row_text(header, table.columns, row)
            if current and (
                len(current) >= self.options.max_rows
                or current_chars + len(row_text) > self.options.max_chars
            ):
                groups.append(current)
                current = []
                current_chars = len(prefix) + len(" | ".join(header))
            current.append(row)
            current_chars += len(row_text)
        if current:
            groups.append(current)

        for seq, group in enumerate(groups):
            lines = [prefix, " | ".join(header)]
            lines.extend(_row_text(header, table.columns, r) for r in group)
            structured = {
                "columns": columns_payload,
                "rows": [r.to_dict() for r in group],
            }
            chunk = self._base(
                document, region, f"t{seq}", "table", "\n".join(lines), structured
            )
            # row-level confidence average as an evidence-based value
            if group:
                chunk.confidence = round(sum(r.confidence for r in group) / len(group), 4)
            chunks.append(chunk)
        return chunks

    def _chunk_key_value(self, document, region):
        entries = region.key_values
        prefix = self._context_prefix(region)
        pairs = [(e.key, e.value) for e in entries]
        structured = {"key_values": [e.to_dict() for e in entries]}
        lines = [prefix] + [f"{k}: {'' if v is None else v}" for k, v in pairs]
        text = "\n".join(lines)
        if len(text) <= self.options.max_chars:
            return [self._base(document, region, "kv0", "key_value", text, structured)]
        # split by pair groups, repeating the context prefix
        chunks = []
        group: list = []
        seq = 0
        chars = len(prefix)
        for entry in entries:
            line = f"{entry.key}: {'' if entry.value is None else entry.value}"
            if group and chars + len(line) > self.options.max_chars:
                chunks.append(
                    self._base(
                        document, region, f"kv{seq}", "key_value",
                        "\n".join([prefix, *[f"{e.key}: {'' if e.value is None else e.value}" for e in group]]),
                        {"key_values": [e.to_dict() for e in group]},
                    )
                )
                seq += 1
                group = []
                chars = len(prefix)
            group.append(entry)
            chars += len(line)
        if group:
            chunks.append(
                self._base(
                    document, region, f"kv{seq}", "key_value",
                    "\n".join([prefix, *[f"{e.key}: {'' if e.value is None else e.value}" for e in group]]),
                    {"key_values": [e.to_dict() for e in group]},
                )
            )
        return chunks

    def _chunk_visual(self, document, region):
        # A visual chunk always carries asset_refs; text is only real context,
        # never a fabricated description.
        text = region.text or ""
        if not text and region.title:
            text = region.title
        structured = {"asset_refs": list(region.asset_refs)}
        chunk = self._base(
            document, region, "v0", region.region_type.value, text, structured
        )
        return [chunk]

    def _chunk_text(self, document, region):
        prefix = self._context_prefix(region)
        body = region.text or ""
        full = f"{prefix}\n{body}".strip() if prefix else body
        if len(full) <= self.options.max_chars:
            return [self._base(document, region, "x0", "text", full, {})]
        # split on paragraph (blank line) then line boundaries, repeating title
        paragraphs = [p for p in body.split("\n\n") if p.strip()]
        chunks = []
        current = ""
        seq = 0
        for paragraph in paragraphs:
            if current and len(current) + len(paragraph) > self.options.max_chars:
                chunks.append(
                    self._base(document, region, f"x{seq}", "text", f"{prefix}\n{current}".strip(), {})
                )
                seq += 1
                current = ""
            current = f"{current}\n\n{paragraph}".strip()
        if current:
            chunks.append(
                self._base(document, region, f"x{seq}", "text", f"{prefix}\n{current}".strip(), {})
            )
        return chunks


def _row_text(header: list[str], columns, row) -> str:
    parts = []
    for col in columns:
        key = col.semantic_name or col.column_id
        value = row.values.get(key)
        parts.append("" if value is None else str(value))
    return " | ".join(parts)


def chunk_document(
    document: SemanticDocumentIR, *, max_rows: int = 40, max_chars: int = 4000
) -> list[KnowledgeChunkIR]:
    return KnowledgeChunker(ChunkingOptions(max_rows=max_rows, max_chars=max_chars)).chunk(
        document
    )


__all__ = ["ChunkingOptions", "KnowledgeChunker", "chunk_document"]
