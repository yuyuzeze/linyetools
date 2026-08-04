"""Public DocumentIR exporters."""

from pathlib import Path
from typing import Protocol

from ..models.document_ir import DocumentIR
from .html_exporter import HTMLExporter, HtmlExporter
from .json_exporter import JSONExporter, JsonExporter
from .jsonl import JSONLExporter, JsonlExporter, KnowledgeBaseJsonlExporter
from .markdown import MarkdownExporter


class DocumentExporter(Protocol):
    def export(self, document: DocumentIR, destination: Path) -> None: ...


__all__ = [
    "DocumentExporter",
    "HTMLExporter",
    "HtmlExporter",
    "JSONExporter",
    "JsonExporter",
    "JSONLExporter",
    "JsonlExporter",
    "KnowledgeBaseJsonlExporter",
    "MarkdownExporter",
]
