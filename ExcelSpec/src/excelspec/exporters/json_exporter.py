"""Stable JSON export for replayable DocumentIR documents."""

from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path

from ..models.document_ir import DocumentIR


@dataclass(slots=True)
class JsonExporter:
    indent: int | None = 2

    def render(self, document: DocumentIR) -> str:
        return json.dumps(
            document.to_dict(),
            ensure_ascii=False,
            indent=self.indent,
            sort_keys=True,
            separators=(",", ":") if self.indent is None else None,
        ) + "\n"

    def export(self, document: DocumentIR, destination: Path) -> None:
        destination = Path(destination)
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_text(self.render(document), encoding="utf-8")


JSONExporter = JsonExporter
