"""Export a SemanticDocumentIR built from a routed DocumentIR."""

from __future__ import annotations

import hashlib
import json
from dataclasses import dataclass
from pathlib import Path

from ..models.document_ir import DocumentIR
from ..semantic import assemble_semantic


def source_hash(document: DocumentIR) -> str | None:
    path = document.source_path
    if not path:
        return None
    file_path = Path(path)
    if not file_path.is_file():
        return None
    digest = hashlib.sha256()
    with file_path.open("rb") as handle:
        for block in iter(lambda: handle.read(65536), b""):
            digest.update(block)
    return digest.hexdigest()


@dataclass(slots=True)
class SemanticJsonExporter:
    indent: int | None = 2

    def build(self, document: DocumentIR):
        return assemble_semantic(
            document,
            profile_id=document.metadata.get("profile_id"),
            processing_mode=document.metadata.get("extraction_mode"),
            source_hash=source_hash(document),
            document_type=document.metadata.get("document_type"),
        )

    def render(self, document: DocumentIR) -> str:
        semantic = self.build(document)
        return (
            json.dumps(
                semantic.to_dict(),
                ensure_ascii=False,
                indent=self.indent,
                sort_keys=True,
            )
            + "\n"
        )

    def export(self, document: DocumentIR, destination: Path) -> None:
        destination = Path(destination)
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_text(self.render(document), encoding="utf-8")


__all__ = ["SemanticJsonExporter", "source_hash"]
