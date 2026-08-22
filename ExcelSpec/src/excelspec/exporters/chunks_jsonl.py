"""Export KnowledgeChunkIR as JSONL (one structured chunk per line).

Consumes the SemanticDocumentIR directly (assembled from routed regions) — never
by re-parsing Markdown. UTF-8, one valid JSON object per line, Japanese/Chinese
text kept verbatim (no \\uXXXX escaping).
"""

from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path

from ..chunking import ChunkingOptions, KnowledgeChunker
from ..models.document_ir import DocumentIR
from .semantic_json import SemanticJsonExporter


@dataclass(slots=True)
class ChunksJsonlExporter:
    max_rows: int = 40
    max_chars: int = 4000

    def build_chunks(self, document: DocumentIR):
        semantic = SemanticJsonExporter().build(document)
        chunker = KnowledgeChunker(
            ChunkingOptions(max_rows=self.max_rows, max_chars=self.max_chars)
        )
        return chunker.chunk(semantic)

    def render(self, document: DocumentIR) -> str:
        lines = [
            json.dumps(chunk.to_dict(), ensure_ascii=False, sort_keys=True)
            for chunk in self.build_chunks(document)
        ]
        return "\n".join(lines) + ("\n" if lines else "")

    def export(self, document: DocumentIR, destination: Path) -> None:
        destination = Path(destination)
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_text(self.render(document), encoding="utf-8")


__all__ = ["ChunksJsonlExporter"]
