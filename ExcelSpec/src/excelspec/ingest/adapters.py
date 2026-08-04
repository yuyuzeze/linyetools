"""Optional enrichment and rendering extension points for workbook ingestion."""

from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Protocol


@dataclass(slots=True)
class EnrichmentRequest:
    """A provider-neutral request for OCR or visual understanding."""

    asset_id: str
    asset_path: Path
    media_type: str | None = None
    prompt: str | None = None
    context: dict[str, Any] = field(default_factory=dict)


@dataclass(slots=True)
class EnrichmentResult:
    status: str
    text: str | None = None
    confidence: float | None = None
    provider: str | None = None
    metadata: dict[str, Any] = field(default_factory=dict)


class OcrAdapter(Protocol):
    def recognize(self, request: EnrichmentRequest) -> EnrichmentResult: ...


class VlmAdapter(Protocol):
    def describe(self, request: EnrichmentRequest) -> EnrichmentResult: ...


@dataclass(slots=True)
class RenderRequest:
    workbook_path: Path
    output_dir: Path
    sheet_names: list[str] = field(default_factory=list)
    file_format: str = "pdf"
    options: dict[str, Any] = field(default_factory=dict)


@dataclass(slots=True)
class RenderResult:
    status: str
    output_paths: list[Path] = field(default_factory=list)
    diagnostics: list[str] = field(default_factory=list)
    metadata: dict[str, Any] = field(default_factory=dict)


class LibreOfficeRenderer(Protocol):
    """Reserved contract; ExcelSpec does not invoke LibreOffice in this stage."""

    def render(self, request: RenderRequest) -> RenderResult: ...


__all__ = [
    "EnrichmentRequest",
    "EnrichmentResult",
    "LibreOfficeRenderer",
    "OcrAdapter",
    "RenderRequest",
    "RenderResult",
    "VlmAdapter",
]
